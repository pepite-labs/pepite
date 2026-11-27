using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Predict.Keyboard;

/// <summary><see cref="Reread"/>: D23 re-read of a modified word / sentence when the caret leaves it (plan 06).
/// <see cref="Reading"/>: one sentence of the selection reading mode (plan 07, D21), with word progress.</summary>
internal enum SpeechKind { Manual, Preview, Inserted, Word, Sentence, Reread, Reading }

/// <summary>An engine word-progress event: the character position / count in the utterance text, and the engine's own
/// audio position (diagnostics only). The highlight is driven by the event's <b>arrival</b>, never scheduled from
/// <see cref="AudioMs"/>: System.Speech reports audio positions in the voice's native format (at 22.05 kHz output every
/// time came out ×1.378, voice-evaluation.md § 2), so a schedule built on them would drift.</summary>
internal readonly record struct SpeechProgress(int CharPos, int CharLen, double AudioMs);

/// <param name="Settings">voice / rate / volume to use for this item only (« Écouter » with the window's working
/// copy); null = the configured ones.</param>
/// <param name="Progress">word progress of this utterance (any thread; <see cref="SpeechKind.Reading"/>).</param>
/// <param name="Done">called once when the item ended, was cancelled, or was dropped without being spoken (any thread).</param>
/// <param name="Covers">a grouped word echo (« les amis », plan 08b) also says this previous word: a pending Word item
/// with that text is dropped.</param>
/// <param name="Private">personal data (a pupil's first name in « Qui es-tu ? », plan 09): the log line gives the
/// length, never the text.</param>
/// <param name="After">2026-11-25 (listen before inserting): appended after the utterance being spoken and the pending
/// items, even for a kind that interrupts — the inserted group (« les amis ») of a word just heard waits for that word to
/// end instead of cutting it.</param>
internal sealed record SpeechItem(string Text, SpeechKind Kind, SpeechSettings? Settings = null,
                                  Action<SpeechProgress>? Progress = null, Action<SpeechOutcome>? Done = null, string? Covers = null,
                                  bool Private = false, bool After = false);

/// <param name="Id">the setting value (<see cref="VoiceId"/>): <c>sapi:&lt;name&gt;</c>, <c>piper:siwis</c>.</param>
/// <param name="Engine"><see cref="VoiceId.Sapi"/> or <see cref="VoiceId.Piper"/>.</param>
internal sealed record VoiceEntry(string Id, string Name, string Culture, string Gender, string Engine);

internal enum SpeechOutcome { Done, Cancelled, Error }

/// <summary>The synthesizer as the speech thread sees it (System.Speech in the app, a fake in the tests).
/// Every call is made on the speech thread; <c>done</c> may be called on any thread.</summary>
internal interface ISynth : IDisposable
{
    IReadOnlyList<VoiceEntry> Voices();
    /// <summary>Apply voice / rate / volume; returns a description of the voice actually used (for the log).</summary>
    string Configure(SpeechSettings s);
    /// <param name="progress">word progress (character position / count in <paramref name="text"/>), raised as the
    /// audio plays; may be called on any thread; null = not wanted.</param>
    void SpeakAsync(string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null);
    void CancelAll();
}

/// <summary>
/// Queue policy of the speech echo (pure):
/// <list type="table">
/// <item>Manual (right-click, a reading tile), Preview, Inserted, Reading — interrupt: the current utterance and everything
/// pending is cancelled.</item>
/// <item>Word — appended; when more than <see cref="MaxPendingWords"/> words wait, the oldest is dropped (fast typing).
/// A grouped word (<see cref="SpeechItem.Covers"/>, « les amis ») drops a still pending « les »: it says it again. A
/// pending group that ends with the covered word (« ils en », then « en ont ») is merged instead: « ils en ont ».</item>
/// <item>Sentence, Reread — drop the pending words, then appended.</item>
/// <item>Any kind with <see cref="SpeechItem.After"/> — appended, nothing interrupted nor dropped.</item>
/// </list>
/// </summary>
internal sealed class SpeechQueue
{
    public const int MaxPendingWords = 2;
    private readonly List<SpeechItem> _pending = new();

    public IReadOnlyList<SpeechItem> Pending => _pending;

    public static bool Interrupts(SpeechKind k) => k is SpeechKind.Manual or SpeechKind.Preview or SpeechKind.Inserted or SpeechKind.Reading;

    /// <returns>true when the utterance being spoken must be cancelled first.</returns>
    public bool Enqueue(SpeechItem item)
    {
        if (item.After) { _pending.Add(item); return false; }   // never interrupts (listen before inserting)
        switch (item.Kind)
        {
            case SpeechKind.Word:
                if (item.Covers is { } covered)
                {
                    int i = _pending.FindLastIndex(p => p.Kind == SpeechKind.Word &&
                                                        (p.Text == covered || p.Text.EndsWith(" " + covered, StringComparison.Ordinal)));
                    if (i >= 0)
                    {
                        var prev = _pending[i];
                        _pending.RemoveAt(i);
                        // a chain of liaisons: keep the whole group (« ils en » + « en ont » → « ils en ont »)
                        if (prev.Text != covered && item.Text.StartsWith(covered + " ", StringComparison.Ordinal))
                            item = item with { Text = prev.Text + item.Text.Substring(covered.Length), Covers = prev.Covers };
                    }
                }
                _pending.Add(item);
                while (_pending.Count(p => p.Kind == SpeechKind.Word) > MaxPendingWords)
                    _pending.RemoveAt(_pending.FindIndex(p => p.Kind == SpeechKind.Word));
                return false;
            case SpeechKind.Sentence:
            case SpeechKind.Reread:
                _pending.RemoveAll(p => p.Kind == SpeechKind.Word);
                _pending.Add(item);
                return false;
            default:
                _pending.Clear();
                _pending.Add(item);
                return true;
        }
    }

    public SpeechItem? Next()
    {
        if (_pending.Count == 0) return null;
        var i = _pending[0];
        _pending.RemoveAt(0);
        return i;
    }

    public void Clear() => _pending.Clear();
}

/// <summary>
/// Owns the synthesizer on a dedicated <c>speech</c> thread (MTA, below normal, background): creating it,
/// enumerating voices, opening the audio device and speaking can all stall, so callers (hook thread, UI
/// thread) only enqueue commands and never wait. Logs (with --log) <c>speak &lt;kind&gt; '…'</c>, then
/// <c>speech done / cancelled / error</c>.
/// </summary>
internal sealed class SpeechWorker : IDisposable
{
    private abstract record Cmd;
    private sealed record SayCmd(SpeechItem Item) : Cmd;
    private sealed record CancelCmd(long Tick) : Cmd;
    private sealed record ConfigureCmd(SpeechSettings S) : Cmd;
    private sealed record DoneCmd(long Id, SpeechOutcome Outcome, string? Error) : Cmd;
    private sealed record InvokeCmd(Action Action) : Cmd;

    private readonly BlockingCollection<Cmd> _cmds = new();
    private readonly Func<ISynth> _create;
    private readonly Action<string>? _log;
    private readonly Thread _thread;
    private readonly TaskCompletionSource<IReadOnlyList<VoiceEntry>> _voices = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SpeechQueue _queue = new();

    // speech thread only
    private ISynth? _synth;
    private SpeechSettings _settings;
    private SpeechSettings? _applied;
    private long _speakingId, _nextId;
    private SpeechItem? _speaking;
    private long _cancelTick;

    public SpeechWorker(Func<ISynth> create, SpeechSettings settings, Action<string>? log)
    {
        _create = create;
        _settings = settings;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "speech", Priority = ThreadPriority.BelowNormal };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Installed, enabled voices (completes once the synthesizer exists; empty when speech is unavailable).</summary>
    public Task<IReadOnlyList<VoiceEntry>> Voices => _voices.Task;

    /// <summary>Non-blocking (a lock-free enqueue): safe on the hook thread.</summary>
    public void Say(string text, SpeechKind kind) => Post(new SayCmd(new SpeechItem(text, kind)));
    public void Say(SpeechItem item) => Post(new SayCmd(item));
    public void Preview(string text, SpeechSettings s) => Post(new SayCmd(new SpeechItem(text, SpeechKind.Preview, s)));
    public void CancelAll() => Post(new CancelCmd(Environment.TickCount64));
    public void Configure(SpeechSettings s) => Post(new ConfigureCmd(s));

    /// <summary>Run <paramref name="a"/> on the speech thread (the engine router's load-end / fallback work).</summary>
    public void Invoke(Action a) => Post(new InvokeCmd(a));

    /// <summary>The synthesizer, for <see cref="Invoke"/>d code only (speech thread).</summary>
    internal ISynth? Synth => _synth;

    /// <summary>While true (tray Pause) a <see cref="SpeechKind.Reread"/> or <see cref="SpeechKind.Reading"/> item is
    /// dropped when it reaches the speech thread: one decided just before the pause cannot be spoken after it.</summary>
    public volatile bool RereadsPaused;

    /// <summary>While true (the selection reading mode is shown) the typing echo and the re-read (Word, Sentence, Reread)
    /// are dropped: the reading has the voice (plan 07).</summary>
    public volatile bool ReadingMode;

    private void Post(Cmd c)
    {
        try { _cmds.Add(c); } catch (InvalidOperationException) { }   // disposed or completed: dropped
    }

    private void Run()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _synth = _create();
            var v = _synth.Voices();
            _voices.TrySetResult(v);
            _log?.Invoke($"speech: synthesizer ready in {sw.ElapsedMilliseconds} ms, {v.Count} voices ({string.Join(", ", v.Select(x => $"{x.Id} [{x.Culture}]"))})");
            Apply(_settings);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"speech unavailable: {ex.GetType().Name}: {ex.Message}");
            _voices.TrySetResult(Array.Empty<VoiceEntry>());
            _synth = null;
        }
        try
        {
            foreach (var c in _cmds.GetConsumingEnumerable())
            {
                try { Handle(c); }
                catch (Exception ex) { _log?.Invoke($"speech error: {ex.GetType().Name}: {ex.Message}"); _speakingId = 0; }
            }
        }
        catch (ObjectDisposedException) { }
        try { _synth?.Dispose(); } catch (Exception) { }
    }

    private void Handle(Cmd c)
    {
        switch (c)
        {
            case ConfigureCmd k:
                _settings = k.S;
                if (_synth != null && _speakingId == 0) Apply(k.S);
                break;
            case CancelCmd k:
                DropPending(_queue.Pending.ToList());
                _queue.Clear();
                if (_synth != null && _speakingId != 0) { _cancelTick = k.Tick; _synth.CancelAll(); }
                break;
            case SayCmd s:
                if (_synth is null) { Notify(s.Item, SpeechOutcome.Error); return; }
                if (s.Item.Kind is SpeechKind.Reread or SpeechKind.Reading && RereadsPaused)
                { _log?.Invoke("speech dropped (paused)"); Notify(s.Item, SpeechOutcome.Cancelled); return; }
                if (s.Item.Kind is SpeechKind.Word or SpeechKind.Sentence or SpeechKind.Reread && ReadingMode)
                { _log?.Invoke($"speech dropped ({s.Item.Kind} while reading)"); Notify(s.Item, SpeechOutcome.Cancelled); return; }
                var before = _queue.Pending.ToList();
                bool interrupt = _queue.Enqueue(s.Item);
                DropPending(before.Where(p => !_queue.Pending.Contains(p)).ToList());
                if (interrupt && _speakingId != 0) { _cancelTick = Environment.TickCount64; _synth.CancelAll(); }
                break;
            case InvokeCmd a:
                a.Action();
                break;
            case DoneCmd d:
                if (d.Id != _speakingId) return;
                _speakingId = 0;
                var item = _speaking;
                _speaking = null;
                if (d.Outcome == SpeechOutcome.Cancelled)
                    _log?.Invoke(_cancelTick != 0 ? $"speech cancelled ({Environment.TickCount64 - _cancelTick} ms after the request)" : "speech cancelled");
                else _log?.Invoke(d.Outcome == SpeechOutcome.Done ? "speech done" : "speech error: " + d.Error);
                _cancelTick = 0;
                if (item != null) Notify(item, d.Outcome);
                break;
        }
        if (_synth != null && _speakingId == 0)
        {
            if (_queue.Next() is { } next) Speak(next);
            // idle after an item: the configured settings again (after a preview, or a Configure that arrived while it
            // spoke), so the engine knows the configured voice without waiting for the next utterance (plan 08b)
            else if (c is DoneCmd && _applied != _settings) Apply(_settings);
        }
    }

    private void Speak(SpeechItem item)
    {
        // the kind first: the router must know a preview's settings are not the configured ones (plan 08b residency)
        if (_synth is ISpeechKindHint hint) hint.NextKind(item.Kind);
        Apply(item.Settings ?? _settings);
        long id = ++_nextId;
        _speakingId = id;
        _speaking = item;
        _log?.Invoke(item.Private ? $"speak {item.Kind} (private, {item.Text.Length} chars)" : $"speak {item.Kind} '{item.Text}'");
        _synth!.SpeakAsync(item.Text, (o, e) => Post(new DoneCmd(id, o, e)), item.Progress);
    }

    /// <summary>Items removed from the queue without being spoken: their owner hears « cancelled » (speech thread).</summary>
    private void DropPending(IReadOnlyList<SpeechItem> dropped)
    {
        foreach (var p in dropped) Notify(p, SpeechOutcome.Cancelled);
    }

    private void Notify(SpeechItem item, SpeechOutcome o)
    {
        if (item.Done is null) return;
        try { item.Done(o); } catch (Exception ex) { _log?.Invoke($"speech callback error: {ex.GetType().Name}"); }
    }

    private void Apply(SpeechSettings s)
    {
        if (_applied is { } a && a.Voice == s.Voice && a.Rate == s.Rate && a.Volume == s.Volume) return;
        var used = _synth!.Configure(s);
        _applied = s;
        _log?.Invoke($"speech voice: {used}, rate {s.Rate}, volume {s.Volume}");
    }

    public void Dispose()
    {
        CancelAll();
        _cmds.CompleteAdding();
        _thread.Join(1000);
    }
}

internal enum ProbeStatus { NotPassword, Password, Unavailable }

