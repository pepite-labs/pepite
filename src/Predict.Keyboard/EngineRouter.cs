using System;
using System.Collections.Generic;
using System.Linq;

namespace Predict.Keyboard;

internal enum PiperState { NotLoaded, Loading, Ready, Failed }

/// <summary>What <see cref="EngineRouter"/> needs from the Piper engine (a fake in the tests). One engine holds every
/// installed Piper voice (plan 08b); each voice has its own state.</summary>
internal interface IPiperEngine : ISynth
{
    PiperState StateOf(string voiceId);
    /// <summary>Raised on the engine's threads when a voice's state changes: a load starts (Loading), ends (Ready or
    /// Failed), or the voice is unloaded (NotLoaded). The router acts on the ends only.</summary>
    event Action<string, PiperState>? StateChanged;
    /// <summary>Post the load of <paramref name="voiceId"/> to the engine's thread (idempotent; never waits).</summary>
    void StartLoad(string voiceId);
    /// <summary>Speak with <paramref name="voiceId"/>. Returns false, without calling <paramref name="done"/>, when the
    /// voice is not Ready any more (unloaded between the router's check and this call): the router routes it again.</summary>
    /// <param name="word">an isolated word (word echo, inserted word, tile click): lower noise and the word cache.</param>
    bool Speak(string voiceId, string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null, bool word = false);
    /// <summary>The configured voice: it stays loaded; a voice loaded for a preview only is unloaded when idle
    /// (<see cref="PiperResidency"/>). null = a SAPI voice is configured.</summary>
    string? Resident { get; set; }
    /// <summary>An utterance the audio device could not take (two failed opens in a row): the router hands it to SAPI.</summary>
    Action<string, Action<SpeechOutcome, string?>, Action<SpeechProgress>?>? DeviceFallback { get; set; }
}

/// <summary>Optional: a synthesizer that wants to know the kind of the next utterance before it is configured for it
/// (the router holds a « Écouter » preview of a Piper voice until that voice is ready, and a preview's voice is not the
/// configured one).</summary>
internal interface ISpeechKindHint
{
    void NextKind(SpeechKind kind);
}

/// <summary>
/// The speech engines behind one <see cref="ISynth"/> (plans 08a step 9, 08b step 4): SAPI (System.Speech, always) and
/// Piper (when a voice pack is installed; one engine, several voices). Voice settings are <see cref="VoiceId"/>s; null =
/// the default (D24): <c>piper:siwis</c> when installed, else the first fr-FR SAPI voice — another Piper voice (pierre)
/// is never an implicit default. Each <c>piper:</c> voice is routed by its own state: while it loads (2.5–4 s) its
/// utterances go to SAPI (A5), logged once per load; with no SAPI voice, or for a preview of that voice, they wait for
/// its load to end (a cancel releases them). After a failed load, SAPI. The engine is chosen per utterance, so it
/// switches only between utterances. A Piper voice is loaded only when it is the effective voice and the app allowed it
/// (<see cref="AllowLoad"/>, after the hooks), or for a preview. The configured voice (settings applied for anything but
/// a preview) is the engine's <see cref="IPiperEngine.Resident"/>. <b>Make-before-break</b> (0.6.0 owner bug: Enregistrer
/// with Pierre gave the next echoes to Hortense): when the configured voice changes from a Ready Piper voice to one that
/// is not loaded yet, the old one (the <i>bridge</i>) keeps speaking the echoes until the new one is Ready, and stays
/// when the new load fails; the engine's residency keeps it loaded meanwhile. SAPI is the interim only when no Piper
/// voice is loaded. <see cref="Preload"/> starts a voice selected in « Réglages » before Enregistrer. Called on the
/// speech thread; Piper's load-end event and device fallback are brought back to it through <c>post</c>.
/// </summary>
internal sealed class EngineRouter : ISynth, ISpeechKindHint
{
    public const string DefaultPiperVoice = "piper:siwis";
    private readonly ISynth _sapi;
    private readonly IPiperEngine? _piper;
    private readonly Action<string>? _log;
    private readonly Action<Action> _post;
    private IReadOnlyList<VoiceEntry>? _sapiVoices;
    private bool _loadAllowed;
    private string? _piperId;         // the effective Piper voice, null = SAPI
    private string? _configured;      // the configured Piper voice (settings applied for anything but a preview)
    private string? _bridge;          // the previous configured voice, speaking until _configured is Ready
    private bool _bridgeLogged;
    private readonly HashSet<string> _interimLogged = new(StringComparer.Ordinal);
    private SpeechKind _nextKind = SpeechKind.Manual;
    private ISynth? _active;
    private long _cancelGen;          // CancelAll count; an utterance handed to Piper remembers the value it saw
    private long _piperGen;
    private (string Voice, string Text, Action<SpeechOutcome, string?> Done, Action<SpeechProgress>? Progress, SpeechKind Kind)? _held;

    /// <param name="post">runs an action on the speech thread (<see cref="SpeechWorker.Invoke"/>).</param>
    public EngineRouter(ISynth sapi, IPiperEngine? piper, Action<string>? log, Action<Action> post)
    {
        _sapi = sapi;
        _piper = piper;
        _log = log;
        _post = post;
        if (_piper != null)
        {
            _piper.StateChanged += (id, _) => _post(() => LoadEnded(id));
            _piper.DeviceFallback = (text, done, progress) => _post(() =>
            {
                // a cancel that reached the router after Piper gave the utterance up must not be lost: Piper already
                // dropped the job, so the cancel did not call done there
                if (_cancelGen != _piperGen) { done(SpeechOutcome.Cancelled, null); return; }
                _log?.Invoke("speech: audio device unavailable for Piper, utterance given to SAPI");
                _active = _sapi;
                _sapi.SpeakAsync(text, done, progress);
            });
        }
    }

    private IReadOnlyList<VoiceEntry> SapiVoices => _sapiVoices ??= _sapi.Voices();
    private IReadOnlyList<VoiceEntry> PiperVoices => _piper?.Voices() ?? Array.Empty<VoiceEntry>();

    /// <summary>The voice « no saved voice » means (D24, literally): siwis when installed, else the first fr-FR SAPI
    /// voice, else the first SAPI voice.</summary>
    public string? DefaultVoice =>
        PiperVoices.FirstOrDefault(v => v.Id == DefaultPiperVoice)?.Id ??
        SapiVoices.FirstOrDefault(v => v.Culture.StartsWith("fr", StringComparison.OrdinalIgnoreCase))?.Id ?? SapiVoices.FirstOrDefault()?.Id;

    public IReadOnlyList<VoiceEntry> Voices() => PiperVoices.Concat(SapiVoices).ToList();

    public string Configure(SpeechSettings s)
    {
        // the worker says the kind before applying an item's settings: a preview's voice is not the configured one
        bool preview = _nextKind == SpeechKind.Preview;
        var id = VoiceId.Parse(s.Voice);
        // a saved voice that is not installed (any more) means the default (D24, plan step 9), with the existing log
        if (id is { } want && Voices().All(v => v.Id != want.ToString()))
        {
            _log?.Invoke($"speech: voice '{want}' not installed, using the default voice");
            id = null;
        }
        // no voice: siwis when installed; otherwise SapiSynth picks its own default (the first fr-FR voice)
        if (id is null && PiperVoices.Any(v => v.Id == DefaultPiperVoice)) id = VoiceId.Parse(DefaultPiperVoice);
        if (id is { IsPiper: true } p && PiperVoices.Any(v => v.Id == p.ToString()))
        {
            _piperId = p.ToString();
            if (!preview) SetConfigured(_piperId);
            _sapi.Configure(s with { Voice = null });   // the interim / fallback voice: the first fr-FR SAPI voice
            if (_loadAllowed) StartLoad(_piperId);
            return _piper!.Configure(s with { Voice = _piperId });
        }
        _piperId = null;
        if (!preview && _piper != null) SetConfigured(null);
        // explicit SAPI voice (name without the prefix) or the SAPI default: SapiSynth logs « voice '…' not installed »
        // for a name it does not know
        return _sapi.Configure(s with { Voice = id is null ? null : id.Value.IsSapi ? id.Value.Name : id.Value.ToString() });
    }

    /// <summary>The configured Piper voice changes (Enregistrer): the old one bridges while the new one loads.</summary>
    private void SetConfigured(string? id)
    {
        var old = _configured;
        if (old != id)
        {
            _configured = id;
            if (id is null || id == _bridge || _piper!.StateOf(id) == PiperState.Ready) _bridge = null;
            else if (old != null && _piper.StateOf(old) == PiperState.Ready) { _bridge = old; _bridgeLogged = false; }
            // else (old not loaded either, e.g. a third voice saved during a load): the existing bridge, if any, stays
        }
        _piper!.Resident = id;
    }

    /// <summary>« Réglages »: a Piper voice was selected in the list. Start its load now (as a preview-only voice: the
    /// residency unloads it 30 s after the load if it is not saved), so it is usually Ready when Enregistrer is
    /// clicked. Ignored before <see cref="AllowLoad"/>, for a voice that is not installed or already loading / loaded.</summary>
    public void Preload(string voiceId)
    {
        if (_piper is null || !_loadAllowed || PiperVoices.All(v => v.Id != voiceId)) return;
        if (_piper.StateOf(voiceId) != PiperState.NotLoaded) return;
        _log?.Invoke($"speech: preloading {voiceId} (selected in Réglages)");
        StartLoad(voiceId);
    }

    /// <summary>The app finished starting (hooks installed): the Piper voice in use may load now.</summary>
    public void AllowLoad()
    {
        _loadAllowed = true;
        if (_piperId != null) StartLoad(_piperId);
    }

    private void StartLoad(string id)
    {
        if (_piper is null || _piper.StateOf(id) != PiperState.NotLoaded) return;
        _interimLogged.Remove(id);
        _piper.StartLoad(id);
    }

    public void NextKind(SpeechKind kind) => _nextKind = kind;

    public void SpeakAsync(string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null)
    {
        var kind = _nextKind;
        _nextKind = SpeechKind.Manual;
        if (_piperId is not { } id || _piper is null) { Speak(_sapi, null, text, done, progress); return; }
        Route(id, kind, text, done, progress);
    }

    /// <summary>A Piper voice's utterance by that voice's state; a voice unloaded between the check and the call (idle
    /// unload race) is loaded again and the utterance waits or goes to SAPI meanwhile, as at start-up.</summary>
    private void Route(string id, SpeechKind kind, string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress)
    {
        var state = _piper!.StateOf(id);
        if (state == PiperState.Ready)
        {
            if (Speak(_piper, id, text, done, progress, kind)) return;
            _log?.Invoke($"speech: {id} was unloaded meanwhile, loading it again");
            state = _piper.StateOf(id);
            if (state == PiperState.NotLoaded) StartLoad(id);
        }
        // make-before-break: the configured voice is not Ready (loading, or its load failed): the previous Piper voice
        // speaks meanwhile; never for a preview (the user wants to hear the new voice)
        if (kind != SpeechKind.Preview && id == _configured && _bridge is { } b && b != id && _piper.StateOf(b) == PiperState.Ready)
        {
            if (!_bridgeLogged) { _bridgeLogged = true; _log?.Invoke($"speech: using {b} until {id} is ready"); }
            if (Speak(_piper, b, text, done, progress, kind)) return;
            state = _piper.StateOf(id);
        }
        if (state == PiperState.Failed) { Speak(_sapi, null, text, done, progress); return; }
        // not loaded yet / loading
        if (kind != SpeechKind.Preview && SapiVoices.Count > 0)
        {
            if (_interimLogged.Add(id)) _log?.Invoke($"speech: using SAPI until {id} is ready");
            Speak(_sapi, null, text, done, progress);
            return;
        }
        _held?.Done(SpeechOutcome.Cancelled, null);   // the worker sends one item at a time: defensive
        _held = (id, text, done, progress, kind);
        if (_piper.StateOf(id) == PiperState.NotLoaded) StartLoad(id);
        _log?.Invoke("speech: waiting for the Piper voice to load");
    }

    /// <returns>false when Piper refused it (voice not Ready any more): nothing was spoken, done not called.</returns>
    private bool Speak(ISynth s, string? piperVoice, string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress,
                       SpeechKind kind = SpeechKind.Manual)
    {
        _active = s;
        if (ReferenceEquals(s, _piper) && piperVoice != null)
        {
            _piperGen = _cancelGen;
            return _piper.Speak(piperVoice, text, done, progress, IsWord(kind));
        }
        s.SpeakAsync(text, done, progress);
        return true;
    }

    /// <summary>One word (or a liaison group) said on its own: the word echo, an inserted word, a right-click / tile
    /// (every <see cref="SpeechKind.Manual"/> item is a single word).</summary>
    internal static bool IsWord(SpeechKind kind) => kind is SpeechKind.Word or SpeechKind.Inserted or SpeechKind.Manual;

    /// <summary>Speech thread: a load ended. The configured voice Ready: the bridge is not needed any more (the residency
    /// unloads it: <c>unloaded (replaced by …)</c>); failed: the bridge stays.</summary>
    private void LoadEnded(string voiceId)
    {
        if (_bridge is { } b && voiceId == _configured)
        {
            var state = _piper!.StateOf(voiceId);
            if (state == PiperState.Ready) _bridge = null;
            else if (state == PiperState.Failed) _log?.Invoke($"speech: {voiceId} could not be loaded, keeping {b}");
        }
        ReleaseHeld(voiceId);
    }

    /// <summary>Speech thread: the load of <paramref name="voiceId"/> ended; an utterance held for that voice goes to it,
    /// or to SAPI after a failure.</summary>
    private void ReleaseHeld(string voiceId)
    {
        if (_held is not { } h || h.Voice != voiceId) return;
        var state = _piper!.StateOf(voiceId);
        if (state is PiperState.NotLoaded or PiperState.Loading) return;
        _held = null;
        if (state == PiperState.Ready) Route(voiceId, h.Kind, h.Text, h.Done, h.Progress);
        else if (SapiVoices.Count > 0) Speak(_sapi, null, h.Text, h.Done, h.Progress);
        else h.Done(SpeechOutcome.Error, "no voice");
    }

    public void CancelAll()
    {
        _cancelGen++;
        if (_held is { } h) { _held = null; h.Done(SpeechOutcome.Cancelled, null); }
        _active?.CancelAll();
    }

    public void Dispose()
    {
        try { _piper?.Dispose(); } catch (Exception) { }
        _sapi.Dispose();
    }
}
