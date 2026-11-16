using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading.Tasks;

namespace Predict.Keyboard;

/// <summary>
/// Speech (D7) with the Windows built-in SAPI 5 voices — free, offline, no third-party engine. The synthesizer
/// lives on the dedicated <c>speech</c> thread of a <see cref="SpeechWorker"/>; every method here only enqueues
/// and returns at once (safe on the hook and UI threads). Word / sentence echo and, since 0.8.3, the inserted and corrected
/// words go through the <see cref="EchoGuard"/> first (spoken only in a field proven non-password, never in a terminal
/// or a remote window); right-click reading (asked by the pupil) and the re-read of an edit (D23: its session is spoken
/// only once the edit probe proved the field) do not.
/// </summary>
internal sealed class Speaker : IDisposable
{
    private readonly SpeechWorker _worker;
    private readonly EchoGuard _guard;

    /// <param name="piper">the Piper engine (plans 08a/08b: one engine, every installed voice), or null (no voice pack,
    /// or the tests).</param>
    public Speaker(SpeechSettings settings, Action<string>? log, Func<IPiperEngine?>? piper = null)
    {
        SpeechWorker? worker = null;
        // the router's posts reach the speech thread only after the worker exists (they follow a Piper load)
        _worker = worker = new SpeechWorker(() => new EngineRouter(new SapiSynth(log), Engine(piper), log, a => worker!.Invoke(a)),
                                            settings, log);
        _guard = new EchoGuard(PasswordProbe.Check, Native.GetForegroundWindow, i => _worker.Say(i), log, fg => PasswordSeen?.Invoke(fg),
                               PrivateHosts.Check);
    }

    public Task<IReadOnlyList<VoiceEntry>> Voices => _worker.Voices;

    private volatile IPiperEngine? _engine;

    /// <summary>Speech thread: the Piper engine, its state changes passed on to <see cref="VoiceStateChanged"/>.</summary>
    private IPiperEngine? Engine(Func<IPiperEngine?>? piper)
    {
        var e = piper?.Invoke();
        if (e != null) e.StateChanged += (id, st) => { try { VoiceStateChanged?.Invoke(id, st); } catch (Exception) { } };
        _engine = e;
        return e;
    }

    /// <summary>A Piper voice's load started / ended, or it was unloaded (any thread): the Réglages status line and the
    /// tray tooltip (owner, 2026-11-14: discreet feedback, no popup).</summary>
    public event Action<string, PiperState>? VoiceStateChanged;

    /// <summary>A Piper voice's state now (NotLoaded when there is no engine yet or the voice is not installed).</summary>
    public PiperState VoiceState(string voiceId) =>
        _engine is { } e && e.Voices().Any(v => v.Id == voiceId) ? e.StateOf(voiceId) : PiperState.NotLoaded;

    /// <summary>The configured Piper voice (null: a SAPI voice, or no engine).</summary>
    public string? ConfiguredPiperVoice => _engine?.Resident;

    /// <summary>The echo guard found a password field in this foreground window (echo-guard thread).</summary>
    public event Action<IntPtr>? PasswordSeen;

    /// <summary>Right-click on a suggestion / inserted word: spoken at once (interrupts).</summary>
    public void Say(string text, SpeechKind kind) => _worker.Say(text, kind);

    /// <summary>0.8.3 (owner, « ne rien lire si on ne sait pas »): typed text said outside the echo — the inserted word, the
    /// corrected word — goes through the same <see cref="EchoGuard"/>: spoken only in a field proven non-password, never in
    /// a terminal or a remote window.</summary>
    public void SayChecked(string text, SpeechKind kind, IntPtr fg) => _guard.Submit(new SpeechItem(text, kind), fg);

    /// <summary>A pupil's first name (« Qui es-tu ? »): spoken at once, never written to the log (plan 09).</summary>
    public void SayName(string name) => _worker.Say(new SpeechItem(name, SpeechKind.Manual, Private: true));

    /// <summary>Word / sentence echo: checked against password fields of <paramref name="fg"/> first.</summary>
    /// <param name="done">the outcome of this item (any thread): Done once spoken; Cancelled when dropped, by the guard
    /// or the queue.</param>
    public void Echo(EchoEvent e, IntPtr fg, Action<SpeechOutcome>? done = null) =>
        _guard.Submit(new SpeechItem(e.Text, e.Kind == EchoKind.Sentence ? SpeechKind.Sentence : SpeechKind.Word, Done: done, Covers: e.Covers), fg);

    /// <summary>D23 re-read of a modified word / sentence (plan 06). Not through the <see cref="EchoGuard"/>: the text
    /// comes from an edit session, which only begins from a caret read that checked the field (ES_PASSWORD / UIA
    /// IsPassword) before reading any text, and is dropped when a password field is seen; the guard would also drop a
    /// sentence left by switching windows (foreground changed).</summary>
    public void Reread(string text) => _worker.Say(new SpeechItem(text, SpeechKind.Reread));

    /// <summary>Tray Pause: a re-read or a reading sentence already on its way to the speech thread is dropped there
    /// (pause race).</summary>
    public void PauseRereads(bool paused) => _worker.RereadsPaused = paused;

    /// <summary>Plan 07: one sentence of the selection reading mode (interrupts). <paramref name="progress"/> and
    /// <paramref name="done"/> may be called on any thread.</summary>
    public void Read(string text, Action<SpeechProgress> progress, Action<SpeechOutcome> done) =>
        _worker.Say(new SpeechItem(text, SpeechKind.Reading, null, progress, done));

    /// <summary>While the reading mode is shown the typing echo and the re-read are muted (plan 07).</summary>
    public bool ReadingMode { set => _worker.ReadingMode = value; }

    /// <summary>« Écouter » in the settings window, with its working copy's voice / rate / volume.</summary>
    public void Preview(string text, SpeechSettings s) => _worker.Preview(text, s);

    public void CancelAll()
    {
        _guard.Clear();
        _worker.CancelAll();
    }

    public void Configure(SpeechSettings s) => _worker.Configure(s);

    /// <summary>Plan 08a: the app has started (hooks installed): the Piper voice may load now, on its own thread, if it is
    /// the voice in use. Never waits.</summary>
    public void StartVoiceLoad() => _worker.Invoke(() => (_worker.Synth as EngineRouter)?.AllowLoad());

    /// <summary>« Réglages »: a Piper voice was selected in the list; start its load in the background (preview-only until
    /// saved) so Enregistrer usually finds it Ready. Never waits.</summary>
    public void Preload(string voiceId) => _worker.Invoke(() => (_worker.Synth as EngineRouter)?.Preload(voiceId));

    /// <summary>JIT the enqueue paths called from the hook thread (no utterance).</summary>
    public void WarmUp()
    {
        foreach (var m in new[] { typeof(Speaker).GetMethod(nameof(Echo)), typeof(Speaker).GetMethod(nameof(CancelAll)),
                                  typeof(EchoGuard).GetMethod(nameof(EchoGuard.Submit)), typeof(SpeechWorker).GetMethod(nameof(SpeechWorker.CancelAll)) })
            if (m != null) System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
        _guard.Clear();
        _worker.CancelAll();   // nothing is speaking yet: a no-op on the speech thread, but the queue path is compiled
    }

    public void Dispose()
    {
        _guard.Dispose();
        _worker.Dispose();
    }

    /// <summary>System.Speech behind <see cref="ISynth"/>; created and used on the speech thread only.</summary>
    internal sealed class SapiSynth : ISynth
    {
        private readonly SpeechSynthesizer _s = new();
        private readonly Dictionary<Prompt, Action<SpeechOutcome, string?>> _done = new();
        private readonly Dictionary<Prompt, Action<SpeechProgress>> _progress = new();
        private readonly object _lock = new();

        public SapiSynth(Action<string>? log)
        {
            // when the engine actually starts the utterance (plan 06 timing: read -> speech started)
            if (log != null) _s.SpeakStarted += (_, _) => log("speech started");
            // plan 07: word progress, raised by SAPI as the audio reaches each word (CharacterPosition / CharacterCount in
            // the prompt's text). AudioPosition is passed on for the log only (native-format time, see SpeechProgress).
            _s.SpeakProgress += (_, e) =>
            {
                Action<SpeechProgress>? p;
                lock (_lock) if (!_progress.TryGetValue(e.Prompt, out p)) p = null;
                p?.Invoke(new SpeechProgress(e.CharacterPosition, e.CharacterCount, e.AudioPosition.TotalMilliseconds));
            };
            _s.SpeakCompleted += (_, e) =>
            {
                Action<SpeechOutcome, string?>? d;
                lock (_lock) { if (_done.Remove(e.Prompt, out d) == false) d = null; _progress.Remove(e.Prompt); }
                d?.Invoke(e.Cancelled ? SpeechOutcome.Cancelled : e.Error is null ? SpeechOutcome.Done : SpeechOutcome.Error, e.Error?.Message);
            };
            _s.SetOutputToDefaultAudioDevice();
        }

        public IReadOnlyList<VoiceEntry> Voices() =>
            _s.GetInstalledVoices().Where(v => v.Enabled)
              .Select(v => new VoiceEntry(VoiceId.Sapi + ":" + v.VoiceInfo.Name, v.VoiceInfo.Name, v.VoiceInfo.Culture?.Name ?? "",
                                          v.VoiceInfo.Gender.ToString(), VoiceId.Sapi))
              .ToList();

        public string Configure(SpeechSettings st)
        {
            string? used = null;
            // the router passes a bare SAPI name; a prefixed one (sapi:…) is stripped here too
            if (VoiceId.Parse(st.Voice) is { IsSapi: true } id && st.Voice!.StartsWith(VoiceId.Sapi + ":", StringComparison.OrdinalIgnoreCase))
                st = st with { Voice = id.Name };
            if (st.Voice is { } name)
            {
                try { _s.SelectVoice(name); used = name; }
                catch (ArgumentException) { }
            }
            if (used is null)
            {
                try { _s.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, new CultureInfo("fr-FR")); } catch (Exception) { }
                used = _s.Voice.Name;
                if (st.Voice is { } missing) used = $"{used} (voice '{missing}' not installed, using {used})";
            }
            _s.Rate = Math.Clamp(st.Rate, -10, 10);
            _s.Volume = Math.Clamp(st.Volume, 0, 100);
            return $"{used} [{_s.Voice.Culture}]";
        }

        public void SpeakAsync(string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null)
        {
            var p = new Prompt(text);
            lock (_lock)
            {
                _done[p] = done;
                if (progress != null) _progress[p] = progress;
            }
            _s.SpeakAsync(p);
        }

        public void CancelAll() => _s.SpeakAsyncCancelAll();

        public void Dispose() => _s.Dispose();
    }
}
