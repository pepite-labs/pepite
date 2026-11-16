using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Threading;

namespace Predict.Keyboard;

/// <summary>
/// The reading mode of the bar (plan 07, D21/D24), UI thread: turns a selection read into a <see cref="ReadingDocument"/>,
/// shows it in the bar (and the « grande fenêtre » when open), drives the <see cref="ReadingController"/> and the speech
/// (<see cref="Speaker.Read"/>: one sentence per utterance, word progress marshalled here with the utterance id, so a late
/// event of a cancelled utterance is ignored). The highlight follows the engine's progress events as they arrive (never a
/// schedule from the engine's audio positions). <see cref="Active"/> is read by the hook thread (Space / F9 swallowed only
/// then, D24). With <c>--log</c> each progress event is logged with the tile highlighted and the delay from the event to the
/// highlight (the only lines with the selected text, as for every <c>--log</c> line with typed text).
/// </summary>
internal sealed class ReadingMode : IReadingOutput
{
    private readonly SuggestionWindow _win;
    private readonly Speaker? _speaker;
    private readonly Dispatcher _ui;
    private readonly Func<AppSettings> _settings;
    private readonly Action<int, int, int, int>? _saveWindow;
    private readonly ReadingController _ctl;
    private ReadingWindow? _large;
    private ReadingPictureSet? _pics;
    private PointI? _caret;
    private IntPtr _fg;                      // window of the selection shown (UI thread)
    private long _fgHook;                    // the same, read by the hook thread (Space / F9 only there, D24)
    private volatile string? _dismissedKey;  // Esc: this selection is not shown again in this window until it changes
    private IntPtr _dismissedFg;
    private volatile bool _active;
    private long _speakTick;                 // Stopwatch ticks when the current utterance was handed to the speech thread
    private Utterance? _utt;

    public Action<string>? Log { get; set; }
    internal PictureStore? Pictures { get; set; }
    /// <summary>Plan 10C: pictures of names and the teacher's pictures (null = none).</summary>
    internal WordPictures? WordPictures { get; set; }
    /// <summary>Plan 10C: is this capitalised form a known name (pack or lists)? Decides a sentence-start word's picture.</summary>
    internal Func<string, bool>? IsName { get; set; }

    public ReadingMode(SuggestionWindow win, Speaker? speaker, Func<AppSettings> settings, Action<int, int, int, int>? saveWindow)
    {
        _win = win;
        _speaker = speaker;
        _ui = win.Dispatcher;
        _settings = settings;
        _saveWindow = saveWindow;
        _ctl = new ReadingController(this);
    }

    /// <summary>Plan 10C (log only, never the words): tiles read as names (no common-noun picture), and those with a
    /// picture of their own (a flag, the teacher's).</summary>
    private static int NameTiles(ReadingDocument d) =>
        System.Linq.Enumerable.Count(d.Tiles, t => t.PictureKey?.StartsWith(ReadingDocument.NameKeyPrefix, StringComparison.Ordinal) == true);
    private int NamePictured(ReadingDocument d) =>
        WordPictures is not { } wp ? 0
        : System.Linq.Enumerable.Count(d.Tiles, t => t.PictureKey is { } k && k.StartsWith(ReadingDocument.NameKeyPrefix, StringComparison.Ordinal) &&
                             wp.For(k[ReadingDocument.NameKeyPrefix.Length..], true) != null);

    /// <summary>The reading view is shown (any thread: a volatile read).</summary>
    public bool Active => _active;
    /// <summary>The window whose selection is shown (any thread): Space / F9 are swallowed only while it is in front.</summary>
    public IntPtr ReadingWindowHandle => (IntPtr)System.Threading.Interlocked.Read(ref _fgHook);
    /// <summary>A selection was dismissed with Esc (any thread): the next caret read without a selection forgets it.</summary>
    public bool HasDismissed => _dismissedKey != null;
    internal ReadingState State => _ctl.State;
    internal ReadingDocument? Doc => _ctl.Doc;

    /// <summary>A selection read of <paramref name="fg"/> (UI thread; the caller checked it is still current).</summary>
    /// <param name="tailAt">D26: where the sentence end added after the selection starts in <paramref name="text"/> (-1 = none).</param>
    /// <param name="cut">the reader stopped at its character limit: the text is cut (message shown).</param>
    public void OnSelection(IntPtr fg, string text, long triggerTick, string source, long readMs, int tailAt = -1, bool cut = false)
    {
        var key = SelectionPolicy.Key(text);
        var dismissed = fg == _dismissedFg ? _dismissedKey : null;
        var action = SelectionPolicy.Decide(_active, _ctl.Doc?.Key, dismissed, text);
        if (key.Length == 0 || key != dismissed) _dismissedKey = null;   // the dismissed selection is gone
        switch (action)
        {
            case SelectionAction.Show:
                Show(fg, text, triggerTick, source, readMs, tailAt, cut);
                break;
            case SelectionAction.Keep:
                SetFg(fg);
                Log?.Invoke($"reading: same selection ({text.Length} chars), kept ({_ctl.State})");
                break;
            case SelectionAction.Exit:
                Exit("selection ended");
                break;
            default:
                if (dismissed != null && SelectionPolicy.Key(text) == dismissed) Log?.Invoke("reading: dismissed selection, not shown again");
                break;
        }
    }

    private void SetFg(IntPtr fg)
    {
        _fg = fg;
        System.Threading.Interlocked.Exchange(ref _fgHook, (long)fg);
    }

    private void Show(IntPtr fg, string text, long triggerTick, string source, long readMs, int tailAt, bool cut)
    {
        long t0 = Stopwatch.GetTimestamp();
        var s = _settings();
        var doc = ReadingDocument.Build(text, ReadingDocument.MaxWords, tailAt, cut, IsName);
        if (doc.Tiles.Count == 0) { Exit("selection without words"); return; }
        if (_active) _ctl.Exit();   // a new selection replaces the shown one (speech stopped)
        SetFg(fg);
        _pics = new ReadingPictureSet(doc, Pictures, _ui, WordPictures);
        _caret = null;
        if (_win.Position == BarPosition.Caret) { var (x, y, _) = Caret.Get(); _caret = new PointI(x, y); }
        long t1 = Stopwatch.GetTimestamp();
        var (loadMs, showMs) = _win.ShowReading(doc, _pics, s.ReadingPictures, _caret);
        long t2 = Stopwatch.GetTimestamp();
        _win.ReadingView!.AllBuilt -= OnAllBuilt;
        _win.ReadingView.AllBuilt += OnAllBuilt;
        Wire(_win.ReadingView);
        long t3 = Stopwatch.GetTimestamp();
        _win.Place(_caret);
        long t4 = Stopwatch.GetTimestamp();
        _active = true;
        if (_speaker != null) _speaker.ReadingMode = true;
        _ctl.Show(doc);
        if (_large != null) LoadLarge();
        double f = 1000.0 / Stopwatch.Frequency, ms = (Stopwatch.GetTimestamp() - t0) * f;
        Log?.Invoke(FormattableString.Invariant(
            $"reading shown: {doc.WordCount} words{(doc.Truncated ? " (truncated at 1000)" : "")}, {doc.Tiles.Count} tiles{(NameTiles(doc) is var nt && nt > 0 ? $" ({nt} name tiles, {NamePictured(doc)} with a picture of theirs)" : "")}, {doc.Sentences.Count} sentences, src={source} read {readMs} ms, show {ms:0.0} ms (model+pictures+caret {(t1 - t0) * f:0.0}, tiles {loadMs:0.0}, WPF Show() {showMs:0.0}, other {(t2 - t1) * f - loadMs - showMs:0.0}, place {(t4 - t3) * f:0.0}), {Environment.TickCount64 - triggerTick} ms after the selection gesture"));
    }

    private bool _wiredBar;
    private void Wire(ReadingView v)
    {
        if (v == _win.ReadingView) { if (_wiredBar) return; _wiredBar = true; }
        v.PlayPause += () => TogglePlay("button");
        v.Prev += () => { _ctl.Prev(); Log?.Invoke($"reading: previous sentence -> {_ctl.Sentence} ({_ctl.State})"); };
        v.Next += () => { _ctl.Next(); Log?.Invoke($"reading: next sentence -> {_ctl.Sentence} ({_ctl.State})"); };
        v.Large += OpenLarge;
        v.TileClicked += i => { _ctl.Click(i); Log?.Invoke($"reading: tile {i} clicked -> sentence {_ctl.Sentence}"); };
    }

    private void OnAllBuilt()
    {
        if (_active) _win.Place(_caret);   // the bar grew while its tiles were built: keep it on the screen
    }

    /// <summary>Space / F9 (hook, posted here), the ▶/❚❚ button, or Space / F9 in the large window.</summary>
    public void TogglePlay(string how)
    {
        if (!_active) return;
        _ctl.TogglePlay();
        Log?.Invoke($"reading: play/pause ({how}) -> {_ctl.State}, sentence {_ctl.Sentence}");
    }

    /// <summary>A caret read found no selection (collapsed caret, another window, a password field): the selection is gone.</summary>
    public void SelectionGone(string why)
    {
        _dismissedKey = null;
        Exit(why);
    }

    /// <summary>Back to predictions. <paramref name="dismissed"/> (Esc): the same selection is not shown again in this window.</summary>
    public void Exit(string why, bool dismissed = false)
    {
        if (!_active) return;
        if (dismissed) { _dismissedKey = _ctl.Doc?.Key; _dismissedFg = _fg; }
        _ctl.Exit();
        _active = false;
        SetFg(IntPtr.Zero);
        if (_speaker != null) _speaker.ReadingMode = false;
        _win.HideReading();
        _pics = null;
        _utt = null;
        if (_large != null) { var l = _large; _large = null; l.Close(); }
        Log?.Invoke($"reading: exit ({why})");
    }

    private void OpenLarge()
    {
        if (!_active) return;
        if (_large != null) { _large.Activate(); return; }
        var w = _large = new ReadingWindow(_settings().Reading, _saveWindow, Log);
        Wire(w.View);
        w.PlayPauseKey += () => TogglePlay("large window key");
        w.Closed += (_, _) => { if (_large == w) _large = null; Log?.Invoke("reading: large window closed"); };
        LoadLarge();
        w.Show();
        bool front = w.BringToFront();   // the user clicked ⤢: the window comes to the front
        Log?.Invoke($"reading: large window opened (in front: {front})");
    }

    private void LoadLarge()
    {
        if (_large is not { } w || _ctl.Doc is not { } doc || _pics is null) return;
        var look = (_win.Look ?? BarLook.Default(new FontCatalog(null, null))) with { Pictures = _settings().ReadingPictures && Pictures != null };
        w.View.Load(doc, _pics, look, look.Pictures, double.NaN, double.NaN);
        w.View.SetHighlight(_ctl.Current);
        w.View.SetPlaying(_ctl.State == ReadingState.Playing);
    }

    // ------------------------------------------------------------------ IReadingOutput (UI thread)
    void IReadingOutput.Speak(long id, Utterance u)
    {
        _utt = u;
        _speakTick = Stopwatch.GetTimestamp();
        if (_speaker is null) { _ui.BeginInvoke(() => _ctl.OnDone(id, SpeechOutcome.Error)); return; }
        _speaker.Read(u.Text,
            p => { long at = Stopwatch.GetTimestamp(); _ui.BeginInvoke(DispatcherPriority.Send, new Action(() => OnProgress(id, p, at))); },
            o => _ui.BeginInvoke(() => _ctl.OnDone(id, o)));
        Log?.Invoke($"reading: sentence {u.Sentence} id={id} ({u.Text.Length} chars from offset {u.Start})");
    }

    private void OnProgress(long id, SpeechProgress p, long eventTick)
    {
        int tile = _ctl.OnProgress(id, p.CharPos, p.CharLen);
        if (Log is not { } log) return;
        double lag = (Stopwatch.GetTimestamp() - eventTick) * 1000.0 / Stopwatch.Frequency;
        double wall = (eventTick - _speakTick) * 1000.0 / Stopwatch.Frequency;
        string engine = _utt is { } u && p.CharPos >= 0 && p.CharPos < u.Text.Length
            ? u.Text.Substring(p.CharPos, Math.Min(Math.Max(1, p.CharLen), u.Text.Length - p.CharPos)) : "";
        string shown = tile >= 0 && _ctl.Doc is { } d ? d.Tiles[tile].Text : "";
        log(string.Format(CultureInfo.InvariantCulture,
            "reading progress id={0} pos={1} len={2} audio={3:0} ms wall={4:0} ms engine='{5}' tile={6} '{7}' highlight +{8:0.00} ms{9}",
            id, p.CharPos, p.CharLen, p.AudioMs, wall, engine, tile, shown, lag, id != _ctl.UtteranceId ? " (stale, ignored)" : ""));
    }

    void IReadingOutput.SayWord(string word) => _speaker?.Say(word, SpeechKind.Manual);

    void IReadingOutput.Cancel() => _speaker?.CancelAll();

    void IReadingOutput.Highlight(int tile)
    {
        _win.ReadingView?.SetHighlight(tile);
        _large?.View.SetHighlight(tile);
    }

    void IReadingOutput.StateChanged(ReadingState s)
    {
        Log?.Invoke($"reading: state {s}");
        bool playing = s == ReadingState.Playing;
        if (s != ReadingState.Idle) _win.ReadingView?.SetPlaying(playing);
        _large?.View.SetPlaying(playing);
    }
}
