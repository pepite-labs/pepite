using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>
/// Glue between the system-wide keyboard/mouse hooks, the word tracker, the prediction engine and the
/// suggestion bar.
///
/// Threading: the low-level hooks live on a <b>dedicated thread with its own message loop</b>. Windows
/// silently skips a low-level hook that does not answer within its timeout, so the hooks must never wait
/// for prediction or WPF work — they only update the tracker (under a lock) and post work to the UI thread.
/// Keys: F1–F8 insert suggestion 1–8 (only while the bar is visible and the setting is on), Esc hides the bar.
/// Speech echo (D7): the <see cref="SpeechEcho"/> is fed with the same keys under the same lock as the tracker;
/// what it emits is handed to the <see cref="Speaker"/> after the lock (an enqueue, never a wait).
///
/// Caret jumps (click, navigation keys, Ctrl+key, foreground change) reset the tracker and ask the
/// <see cref="CaretTextReader"/> (its own thread) to read the text at the caret; its result seeds the
/// tracker only if nothing was typed meanwhile, the same window is in front and it took ≤ 150 ms.
/// While a password field is focused nothing is tracked or logged (<c>_suppress</c>). At the first character after
/// every jump (and after the start) a <see cref="FieldProbe"/> asks whether the focused field is a password field —
/// a page can focus one after the jump's read (autofocus). Until it answers, typed characters are not logged; the
/// bar shows only when a read or the probe has seen a non-password field. Password → same suppression as a read.
///
/// Punctuation (D22): right after an insertion that added a space (<see cref="AutoSpaceState"/>: tracker version + foreground,
/// so any other key, jump or Backspace disarms it), <c>, . … ) ' -</c> swallow the key and inject Backspace + the
/// character, <c>; : ! ?</c> Backspace + U+202F + the character (setting), and a second space is absorbed. The
/// injection is one SendInput made <b>on the hook thread</b> before the key is swallowed, so a fast next key can
/// never overtake it.
///
/// Re-read of edits (D23, plan 06): every applied caret read begins an <see cref="EditSession"/> (mirror of the read
/// window); keys, insertions, Backspace and Delete edit the mirror under the same lock. A leave (click, arrows,
/// Home/End, PgUp/PgDn, Tab, window switch) only freezes the session; the read at the new caret classifies the landing
/// off the lock (<see cref="EditClassifier"/>) and the modified word / sentence is queued as <see cref="SpeechKind.Reread"/>.
/// Delete with a session is an edit, not a leave; Ctrl+key (paste, undo…) and Insert end the session silently.
///
/// Selection reading (plan 07, D21/D24): a caret read that reports a selection (after a click or drag, a double click,
/// Shift + navigation, Ctrl+A, a window switch) asks the <see cref="SelectionReader"/> (its own thread) for the selected
/// text; the bar then switches to the reading view (<see cref="ReadingMode"/>, UI thread). Typing, Backspace, Enter, Delete
/// and Esc leave it at once; a later caret read without a selection (click elsewhere, arrows, another window, a password
/// field) leaves it too. While it is shown, Space and F9 (no modifier) are swallowed — key-down and key-up — and toggle
/// Play / Pause; F1–F8 go to the application. The hook only reads a volatile flag and posts to the UI thread.
///
/// Spelling indicator (plan 10B, D12): <see cref="Update"/> (UI thread) asks <see cref="SpellingIndicator"/> about the word
/// being typed (✓ only) or the word a separator just finished (✓ ≈ ?, kept until the next letter) and puts the marker left
/// of the words — alone for ≈ / ? when there is no word to propose (Q10). A click on it, or F9 while a ≈ / ? marker is
/// shown (the hook reads a volatile flag, as for F1–F8), shows the sound-alike words (≈) or offers to add the word (?).
/// Nothing is recorded; the log has the state, never the word.
///
/// Listen before inserting (owner 2026-11-25, <see cref="ListenFirst"/>), two independent settings. « Touches F1–F8 »
/// (default: 1st press listens): the first plain F<i>n</i> speaks the tile's word (<see cref="SpeechKind.Manual"/>) and rings
/// the tile; a second press on it inserts it without saying it again (unless the group differs: « les amis », « l'école »).
/// « Clic sur un mot »: insert at once, 1st click listens / 2nd inserts (with a ring), or — the default — a click speaks
/// (no ring) and a double click or a right-click inserts without cutting the word being heard. With option D's speaker band
/// a click on the word inserts at once. Maj+F<i>n</i> (option A), a right-click (when it does not insert) and the band speak
/// and arm when a confirming press exists. The hook only reads volatile flags and posts to the UI thread; any other key, a
/// jump, a hidden or rebuilt bar with another word there cancels the ring.
/// </summary>
internal sealed class KeyboardController : IDisposable
{
    /// <summary>dwExtraInfo stamped on our own injected input ("LXKB"), so the hook ignores it.</summary>
    public static readonly UIntPtr Marker = (UIntPtr)0x4C584B42u;

    private readonly PredictionEngine _engine;
    private readonly SuggestionWindow _win;
    private readonly Dispatcher _ui;
    private readonly WordTracker _tracker = new();
    private readonly SpeechEcho _echo = new();
    private readonly object _sync = new();               // guards _tracker and _echo
    private readonly Speaker? _speaker;
    private readonly Native.HookProc _kbProc, _msProc;   // keep delegates alive (GC)
    private readonly HashSet<uint> _consumed = new();    // hook thread only
    /// <summary>F1…F8 held (hook thread only): the action of each key's first down — only that action repeats, a passed
    /// down's key-up is not swallowed (<see cref="FKeyHolds"/>).</summary>
    private FKeyHolds _fKeys = new();
    /// <summary>+1 on a session lock / unlock (UI thread, <see cref="ForgetHeldKeys"/>): the hook drops its F-key records
    /// at the next F-key down (a key-up lost to the secure desktop).</summary>
    private long _fKeysGeneration;
    /// <summary>+1 (hook thread) for every key that reaches the listen-cancel check; <see cref="Heard"/> compares it with the
    /// value read when its gesture began, so a key typed while the flags were still false cancels too.</summary>
    private long _keySeq;
    private IntPtr _kbHook, _msHook;                     // hook thread only
    private IntPtr _lastFg;                              // hook thread writes; UI thread reads (Volatile) for the bar's give-back
    private readonly Native.WinEventProc _winEvtProc;    // keep delegate alive (GC)
    private IntPtr _winEvtHook;                          // hook thread only
    private bool _mouseDownOutside;                      // hook thread only
    private bool _mouseDownShell;                        // hook thread only: the click went to the taskbar / tray / our windows
    private IntPtr _focusHook;                           // hook thread only: EVENT_OBJECT_FOCUS (plan 06 review)
    private readonly FocusFilter _focusFilter = new();   // hook thread only
    private long _clickVersion;                          // hook thread only: tracker version at button-down
    private volatile bool _suppress;                     // a password field is focused
    private volatile bool _privateHost;                  // 0.8.3: a terminal or remote / VM window is in front: nothing tracked, read, shown or said
    private const int FieldUnknown = 0, FieldClear = 2;
    private volatile int _field;                         // a read or the probe saw a non-password field (bar allowed)
    private const int ProbeNotAsked = 0, ProbePending = 1, ProbeDone = 2;
    private volatile int _probe;                         // field probe since the last jump (typed text loggable when Done)
    private long _fieldProbeId, _fieldProbeEpoch;        // latest FieldProbe request and the epoch it was asked in (_sync)
    private long _epoch;                                 // (_sync) +1 on every jump, Enter and password suppression
    private long _readEpoch;                             // (_sync) epoch of the latest read request
    private FieldProbe? _fieldProbe;
    private FieldProbe? _editProbe;                      // D23: the field the session's edits are typed into (plan 06 review)
    private CaretTextReader? _reader;
    // LibreOffice phase 1: Writer's text through IAccessible2 (null until Start); its prefetch worker (a focus event
    // resolves the paragraph, no text, so the jump read that follows is warm)
    private LibreOfficeText? _lo;
    private ReadQueue? _loPrefetch;
    /// <summary>false = <c>--no-ia2</c> (test switch): IAccessible2 is never called. Set before <see cref="Start"/>.</summary>
    public bool Ia2Enabled { get; set; } = true;
    private SelectionReader? _selection;                 // plan 07
    private readonly ReadingMode? _reading;              // plan 07 (UI thread; Active is read by the hook)
    private volatile bool _readingOn = true;
    private volatile int _clipboardFallback = -1;        // reading.clipboardFallback: -1 not set (the host's default), 0 off, 1 on
    private long _selGesture = -1;                       // tracker version of the last selection gesture (drag, double click, Shift+nav, Ctrl+A)
    private Native.POINT _downPt;                        // hook thread only: button-down point / time, last button-up (double click)
    private long _downTick, _lastUpTick;
    private Native.POINT _lastUpPt;
    private volatile uint _dblClickMs = 500;           // written on the UI thread (each tile click), read by the hook
    /// <summary>A window of the UI thread that owns the clipboard after the Ctrl+C fallback restored it (plan 07).</summary>
    public IntPtr ClipboardWindow { get; set; }
    private Thread? _hookThread;
    private uint _hookThreadId;

    private IReadOnlyList<Suggestion> _sugg = Array.Empty<Suggestion>();   // UI thread
    private volatile int _suggCount;                                       // mirror for the hook thread
    private int _updatePending;

    private volatile bool _enabled = true;
    /// <summary>Tray Pause / Reprendre. Also tells the speech thread to drop a re-read that was queued while pausing.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value; _speaker?.PauseRereads(!value); if (!value) ExitReadingSoon("paused");
            Interlocked.Increment(ref _fKeysGeneration);   // a key-up during Pause is not seen: no stale F-key record
            lock (_sync) _tracker.Reset();   // keys typed while paused were not followed: pause and resume start afresh
        }
    }
    public Action<string>? Log { get; set; }

    // settings (written on the UI thread, read on the hook thread)
    private volatile int _wordCount = 8;
    /// <summary>0.10.2: the words the bar could show last time it was full or trimmed to fit the screen (XL on a small
    /// monitor): the exact typed word goes in the last of those slots, never in one trimmed away.</summary>
    private int _barFits = 8;
    private volatile bool _keysOn = true, _speakInserted = true;
    // 2026-11-25 owner, listen before inserting: « Touches F1–F8 » 1st press listens (hook reads), A Maj+F1…F8, D the
    // speaker band; « Clic sur un mot » (UI thread)
    private volatile bool _keysListenFirst = true, _listenShift, _listenButton;
    private ClickMode _clickMode = ClickMode.ListenDouble;   // UI thread
    private volatile bool _listenArmed;                     // a tile is ringed (the hook posts a cancel for any other key)
    private volatile bool _wordHeard;                       // a word was heard (ListenFirst.LastHeard): any other key forgets it too
    private readonly ListenFirst _listen = new();           // UI thread
    private volatile int _punct = (int)PunctuationSpacing.French;
    private volatile bool _spellingOn = true;              // plan 10B: « Indicateur d'orthographe »
    private volatile bool _markAction;                     // plan 10B: a ≈ / ? marker is shown: F9 acts on it (hook reads)
    private string? _markWord;                             // UI thread: the word the marker is about (never logged)
    private SpellingCheck _markCheck = SpellingCheck.Unknown;   // UI thread: its check (≈: the sound-alike words)
    // 0.8.3: the ≈ list shown by the marker and the finished word it may replace (UI thread; null once the bar changed)
    private SoundAlikeCorrection.Target? _correction;
    // 0.11.0 (plan negation): the « ne » fix of the finished word (UI thread: Update), the group tile shown from it, and the
    // group replacement waiting for its read (_sync)
    private (NegationFix Fix, string Tail, long Version)? _negation;
    private SoundAlikeCorrection.GroupTarget? _group;
    private (SoundAlikeCorrection.GroupTarget? Fix, long Id, string? Heard) _pendingGroup;
    private readonly ReadableWindows _readable = new();   // owner 2026-11-27: no « ne » marker where choosing cannot replace
    private ReadQueue? _correctRead;                        // 0.8.3: the read that checks the document before a replacement
    private (SoundAlikeCorrection.Target? Fix, string Word, long Id, bool KeysOnly, string? Heard) _pendingFix;   // (_sync) the replacement waiting for its read (Heard: listen before inserting)
    // 0.11.0 (plan hyphen-names): the head the bar's compound tiles complete (UI thread: Update), and the compound replacement
    // waiting for its read (_sync)
    private SoundAlikeCorrection.CompoundTarget? _compound;
    private (SoundAlikeCorrection.CompoundTarget? Fix, string Whole, string Ending, long Id, bool KeysOnly, string? Heard,
             PunctuationSpacing Mode, bool Adaptive, bool AdaptiveLo) _pendingCompound;
    private long _fixIds;                                   // (_sync)
    private EchoEvent? _fixEcho;                            // (_sync) a correction's word echo when « Lire le mot inséré » is off
    private const string ReasonCorrect = "correct";
    /// <summary>0.8.3 review: keyboard events inside our hook callback right now (any thread reads it). A physical key the
    /// hook is still handling may reach the app before keys we inject: an edit sent from another thread is sent only when
    /// this is 0 (checked under the lock, which the hook takes for every tracked key).</summary>
    private long _keysInHook;

    // plan 10B, line end (owner option B, re-review): Enter arms a one-shot timer; any key, jump, focus or foreground event
    // bumps the generation (hook thread) and so cancels it
    private long _lineEndGen;
    private DispatcherTimer? _lineEndTimer;                // UI thread
    // LibreOffice phase 1 (review): a bar placed at a reused IA2 caret point (ia2-recent, < 100 ms old) is placed again
    // once typing pauses, so it never stays at a stale point after a line wrap or Enter (UI thread; restarted per update)
    private DispatcherTimer? _caretRefresh;
    private long _lineEndProbeId = -1;                     // (_sync) the field probe asked for a line-end marker
    private long _lineEndAccepted = -1;                    // (_sync) tracker version whose line-end word LineEndMark accepted
    public const int LineEndDelayMs = 130;                 // after Enter: the app has handled it (a dialog closed, a field changed)
    private const string ReasonEsc = "esc";

    /// <summary>Plan 10B: the « ? » marker was clicked (or F9): offer to add this word to the active profile's words (the app
    /// applies the PIN rule of Q3). UI thread.</summary>
    public Action<string>? AddWordRequested { get; set; }

    private readonly AutoSpaceState _autoSpace = new();   // (_sync) our space after an insertion (D22)
    private EditSession? _session;                         // (_sync) D23 edit session (plan 06)
    private volatile bool _reread = true;                  // setting « Relire le mot / la phrase modifié(e) »
    private const string ReasonDelete = "del";
    // LibreOffice phase 1 (D23 after the host's own changes): one read SettlePolicy.DelayMs after a separator / Enter in a
    // Writer window where IA2 answers, skipped without any COM call if anything was typed since (version check); it
    // re-seeds the tracker and rebases the session only when the document disagrees with the keys (AutoCorrect, word
    // completion). Its own queue: jump reads and selection decisions are not affected
    private ReadQueue? _settleRead;
    private const string ReasonSettle = "settle";
    private const string ReasonBackspace = "bksp";      // a Backspace left the word: only the tracker (and a lost echo) use it
    // D22 adaptive Word rule (all under _sync): the window armed in "app may space ; : ! ?" mode, the user's setting
    // then, what each such window was seen to do (true = adds its own space), the read-back waiting for an answer
    private IntPtr _adaptiveFg;
    private PunctuationSpacing _adaptiveSetting;
    private readonly Dictionary<IntPtr, bool> _appAddsSpace = new();
    private readonly PendingReadBack _pendingCheck = new();   // (_sync) the D22 read-back waiting for its answer
    private bool _adaptiveLo;   // the window armed in adaptive mode is LibreOffice Writer (read back through IA2)
    private ReadQueue? _punctCheck;
    // D17 amendment (2026-11-14): a sentence end typed after a jump into the sentence says the whole sentence, read from
    // the document by its own queue (typing does not make that read stale, a jump does: _jumps)
    private ReadQueue? _sentenceRead;
    private const string ReasonSentence = "sentence";
    private const int SentenceReadDelayMs = 60;            // the app handles the key first (the sentence end must be there)
    private long _jumps;                                   // (_sync) +1 on every jump
    private const int SentenceRetryMs = 80;                // a read that found no finished sentence: once more, later
    private readonly SentenceEchoGate _gate = new();       // (_sync) the pending read, the echoes deferred behind it, what was said
    private Timer? _sentenceTimer;                         // gives up an overdue sentence read (its words deferred behind it)

    // hook timing (hook thread only), summarised in the log every 200 calls
    private long _hookTicks, _hookMaxTicks;
    private int _hookN;

    public KeyboardController(PredictionEngine engine, SuggestionWindow win, Speaker? speaker = null, ReadingMode? reading = null)
    {
        _engine = engine;
        _win = win;
        _speaker = speaker;
        _reading = reading;
        if (speaker != null) speaker.PasswordSeen += fg => SuppressPassword(fg, "echo guard");
        _ui = win.Dispatcher;
        _kbProc = KeyboardProc;
        _msProc = MouseProc;
        _winEvtProc = OnWinEvent;
        _win.Picked += PickedByMouse;
        _win.Invoked += InvokedByAutomation;
        _win.TrackedForeground = () => Volatile.Read(ref _lastFg);   // BUG-1: where the bar gives an activation back
        _win.MarkerClicked += _ => OnMarker("click");
        _win.ListenRequested += OnListenRequested;
    }

    private bool _soundPairsLogged;

    /// <summary>Settings that the controller uses (UI thread).</summary>
    public void Configure(AppSettings s)
    {
        _wordCount = s.WordCount;
        _barFits = s.WordCount;
        _engine.ExactBar = s.WordCount;   // 0.10.2: the exact typed word goes in the bar's last slot at worst, not in a spare
        // D33: « Sons que l'élève confond » of the active profile (Configure runs again on a profile switch / « Classe » change)
        var pairs = s.Sounds.ToEngine();
        if (pairs != _engine.Tolerance || !_soundPairsLogged)
        {
            _engine.Tolerance = pairs;
            _soundPairsLogged = true;
            Log?.Invoke("sound pairs: " + SoundPairsInfo.Describe(pairs));
        }
        _keysOn = s.Keys == KeyMode.F1F8;
        _punct = (int)s.Punctuation;
        _speakInserted = s.Speech.SpeakInserted;
        _echo.SpeakWords = s.Speech.SpeakWords;
        _echo.SpeakSentences = s.Speech.SpeakSentences;
        _reread = s.Speech.RereadEdits;
        if (!_reread) lock (_sync) _session = null;
        _readingOn = s.Reading.Enabled;
        _clipboardFallback = s.Reading.ClipboardFallback is bool cf ? (cf ? 1 : 0) : -1;
        _spellingOn = s.SpellingIndicator;
        if (!_readingOn) _reading?.Exit("setting off");
        _keysListenFirst = s.KeysListenFirst;
        _clickMode = s.Click;
        _listenShift = s.ListenShiftKeys;
        _listenButton = s.ListenButton;
        // a ring set by an Invoke has its confirming Invoke as long as the keys stay « 1er appui », even with the F-keys off
        if (!Rings && !(_ringByInvoke && _keysListenFirst)) CancelListen("setting");
    }

    /// <summary>After the bar was rebuilt or moved outside <see cref="Update"/> (settings change, drag to another
    /// monitor): F-keys follow the words now on screen. UI thread.</summary>
    public void SyncVisibleCount()
    {
        if (_sugg.Count > 0) _suggCount = Math.Min(_sugg.Count, _win.VisibleCount);
        SyncExactBar();
        KeepListen();   // a rebuilt bar: the ring follows its word, or is cancelled
    }

    /// <summary>0.10.2: <see cref="PredictionEngine.ExactBar"/> = the slots the bar shows — the word count, or fewer when the
    /// last bar was trimmed to fit the screen (learnt again from each full or trimmed bar; a bar of fewer words than the
    /// count, not trimmed, tells nothing). One slot: the engine applies no rule. UI thread.</summary>
    private void SyncExactBar()
    {
        int shown = _win.VisibleCount;
        if (_sugg.Count > 0 && shown < _sugg.Count) _barFits = Math.Max(1, shown);   // trimmed to fit the screen
        else if (shown >= _wordCount) _barFits = _wordCount;                       // the full bar fits
        _engine.ExactBar = Math.Min(_wordCount, _barFits);
    }

    /// <summary>Tray « Pause »: nothing more is said (the hooks already skip everything while paused).</summary>
    public void OnPaused()
    {
        lock (_sync) { _echo.Reset(); _session = null; _gate.Drop(); }
        _reading?.Exit("paused");
        _speaker?.CancelAll();
        CancelListen("pause");
    }

    /// <summary>Plan 09: another profile becomes active — what the previous pupil typed is forgotten (tracked word, echo,
    /// re-read session, pending sentence read, our auto-space), the reading view closes and the bar hides. UI thread; the
    /// caller cancels the speech.</summary>
    public void ResetForSwitch()
    {
        lock (_sync) { _tracker.Reset(); _echo.Reset(); _session = null; _gate.Drop(); _autoSpace.Disarm(); }
        _reading?.Exit("profile switch");
        _sugg = Array.Empty<Suggestion>(); _suggCount = 0; _markAction = false; _markWord = null;
        CancelListen("switch");
        _win.HideBar();
    }

    /// <summary>JIT the code the hook runs (plus the echo and speech enqueue) before the hooks exist: the first keys
    /// after a start otherwise paid several ms of compilation inside the low-level hook callback (sandbox: 5 ms).</summary>
    private void WarmUpHookPath()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                                                 System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        foreach (var name in new[] { nameof(KeyboardProc), nameof(KeyboardProcCore), nameof(MouseProc), nameof(OnKeyDown),
                                     nameof(Translate), nameof(Jump), nameof(ScheduleUpdate), nameof(HideSoon), nameof(FixAutoSpace),
                                     nameof(OnWinEvent), nameof(OnFocusEvent), nameof(FocusJump), nameof(ProbeEdits),
                                     nameof(ReadingKey), nameof(ExitReadingSoon), nameof(SelectionGesture),
                                     nameof(SettleWanted), nameof(ReadBackPending), nameof(CorrectionArmed) })
            if (typeof(KeyboardController).GetMethod(name, F) is { } m) System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle);
        var echo = new SpeechEcho { SpeakWords = true, SpeakSentences = true };
        foreach (var c in "l'a b, c.\n") echo.Type(c);
        echo.Backspace(); echo.Inserted("a", true, false); echo.Reset();
        var tr = new WordTracker();
        foreach (var c in "a b.") tr.Type(c);
        tr.Backspace(); tr.Accept("l'", false); tr.Reset();
        _ = new FocusFilter().ShouldJump(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 0, false, false);
        // LibreOffice phase 1: NoteFocus / IsLive / the settle decision and the module test of the focus path (no COM)
        LibreOfficeText.WarmUp();
        _ = HostApps.ModuleOf("soffice.bin", "Sans nom 1 — LibreOffice Writer");
        _ = HostApps.IsFrame(Native.GetForegroundWindow());
        _ = HostApps.ModuleOf(Native.GetForegroundWindow());   // ProcessNameOf's OpenProcess path (cached per window)
        // the first real calls of the mouse / focus paths: P/Invoke stubs and marshalling are built here, not in a hook
        _ = Native.IsShellOrOwn(Native.GetAncestor(Native.WindowFromPoint(new Native.POINT { X = 1, Y = 1 }), Native.GA_ROOT));
        _ = Native.IsShellOrOwn(Native.GetForegroundWindow());
        var mbuf = Marshal.AllocHGlobal(Marshal.SizeOf<Native.MSLLHOOKSTRUCT>());
        try { Marshal.WriteInt64(mbuf, 0); _ = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(mbuf); } finally { Marshal.FreeHGlobal(mbuf); }
        _ = RereadFlow.OnJump(null, SessionMove.Leave, false);
        var es = EditSession.Begin(TextWindow.Of("le chat", 3, false, false), IntPtr.Zero);
        es.Type('a'); es.Type(' '); es.Backspace(); es.Delete(); es.Insert(0, 1, "la "); es.MarkWordSpoken(); _ = es.NeedsProbe; es.StartProbe(1); _ = es.ProbeAnswered(1, ProbeStatus.NotPassword); es.Leave(); es.Type('b');
        _ = KeyboardPolicy.AfterAutoSpace('!', PunctuationSpacing.French, false);
        _ = KeyboardPolicy.FKeyAction(0, false, false, false, true, false, true, 1, new FKeyHolds().Held(0, Native.IsDown(0x70), 1));
        _ = KeyboardPolicy.SwallowFKeyUp(FKeyAct.Pass);
        _ = Inserter.Build(0, 1, "!", Marker);
        Translate(0x41, 0x1E, Native.GetForegroundWindow());
        _ = Marshal.SizeOf<Native.KBDLLHOOKSTRUCT>();
        _speaker?.WarmUp();
        Log?.Invoke(FormattableString.Invariant($"hook path warm-up {sw.Elapsed.TotalMilliseconds:0.0} ms"));
    }

    public void Start()
    {
        WarmUpHookPath();
        _fgRecheckTimer = new Timer(PostForegroundRecheck);   // WR5: made here, before the hooks (never inside a WinEvent)
        // LibreOffice phase 1: before the hooks (OnFocusEvent records Writer's focus objects from the first event)
        _lo = new LibreOfficeText(new ComLoBackend(), Ia2Enabled, Log, root => _loPrefetch?.Request(0, root, 0, "prefetch"));
        LibreOfficeText.Shared = _lo;
        if (Ia2Enabled)
            _settleRead = new ReadQueue(v => { lock (_sync) return v == _tracker.Version; }, OnSettleRead, q => CaretTextReader.ReadAround(q.Fg, Log),
                                        null, Log, CaretTextReader.StuckMs, KeyboardPolicy.ReadTimeoutMs, "settle-reader");
        if (Ia2Enabled)
            _loPrefetch = new ReadQueue(_ => true, (_, _) => { }, q => { _lo.Prefetch(q.Fg); return new ReadResult(ReadStatus.Unavailable, LibreOfficeText.Src, TextContext.Invalid, 0); },
                                        null, Log, CaretTextReader.StuckMs, KeyboardPolicy.ReadTimeoutMs, "lo-prefetch");
        else Log?.Invoke("ia2: off (--no-ia2)");
        _fieldProbe = new FieldProbe(PasswordProbe.Check, OnFieldProbe, Log);
        _editProbe = new FieldProbe(EditProbeCheck, OnEditProbe, Log);
        // one harmless request each (no window: Unavailable, no session, stale id): JIT the request / answer paths off the hook
        _editProbe.Request(IntPtr.Zero);
        _fieldProbe.Request(IntPtr.Zero);
        _reader = new CaretTextReader(v => { lock (_sync) return v == _tracker.Version; }, OnRead, Log);
        _punctCheck = new ReadQueue(v => { lock (_sync) return v == _tracker.Version; }, OnPunctCheck,
                                    fg => CaretTextReader.ReadBeforeCaret(fg, 4, Log), CaretTextReader.WarmUpUia, Log, CaretTextReader.StuckMs);
        _sentenceRead = new ReadQueue(v => { lock (_sync) return v == _jumps; }, OnSentenceRead, q => CaretTextReader.ReadAround(q.Fg, Log),
                                      CaretTextReader.WarmUpUia, Log, CaretTextReader.StuckMs, KeyboardPolicy.RereadReadBudgetMs, "sentence-reader");
        _correctRead = new ReadQueue(v => { lock (_sync) return v == _tracker.Version; }, OnCorrectRead, q => CaretTextReader.ReadAround(q.Fg, Log),
                                     CaretTextReader.WarmUpUia, Log, CaretTextReader.StuckMs, KeyboardPolicy.RereadReadBudgetMs, "correct-reader");
        _sentenceTimer = new Timer(_ => { try { ExpireSentence(); } catch (Exception ex) { Log?.Invoke($"sentence echo error: {ex.GetType().Name}"); } });
        if (_reading != null)
            _selection = new SelectionReader(v => { lock (_sync) return v == _tracker.Version; }, OnSelectionRead, ClipboardWindow, Log);
        _dblClickMs = Native.GetDoubleClickTime();
        var ready = new ManualResetEventSlim();
        Exception? error = null;
        _hookThread = new Thread(() =>
        {
            _hookThreadId = Native.GetCurrentThreadId();
            var mod = Native.GetModuleHandle(null);
            _fKeys = new FKeyHolds();   // the hook install starts with no key held
            _kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbProc, mod, 0);
            if (_kbHook == IntPtr.Zero) error = new Win32Exception(Marshal.GetLastWin32Error(), "keyboard hook");
            _msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _msProc, mod, 0);
            _lastFg = Native.GetForegroundWindow();   // the WinEvent only reports later changes
            _privateHost = PrivateHosts.Check(_lastFg);
            _winEvtHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
                _winEvtProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);   // own process included: tray menu / À propos hide the bar
            // focus moves inside a window (in-page dialog, two-step login): out of context, so delivered to this loop
            _focusHook = Native.SetWinEventHook(Native.EVENT_OBJECT_FOCUS, Native.EVENT_OBJECT_FOCUS, IntPtr.Zero,
                _winEvtProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            ready.Set();
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WmForegroundRecheck) { OnForegroundRecheck((long)msg.wParam); continue; }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            if (_kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbHook);
            if (_msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_msHook);
            if (_winEvtHook != IntPtr.Zero) Native.UnhookWinEvent(_winEvtHook);
            if (_focusHook != IntPtr.Zero) Native.UnhookWinEvent(_focusHook);
        })
        { IsBackground = true, Name = "keyboard-hook", Priority = ThreadPriority.AboveNormal };
        _hookThread.Start();
        ready.Wait();
        if (error != null) throw error;
        Log?.Invoke($"hooks installed on a dedicated thread (foreground WinEvent {(_winEvtHook != IntPtr.Zero ? "on" : "FAILED")}, focus WinEvent {(_focusHook != IntPtr.Zero ? "on" : "FAILED")})");
    }

    // ---------------------------------------------------------------- hooks (hook thread)
    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _keysInHook);
        IntPtr r;
        try { r = KeyboardProcCore(nCode, wParam, lParam); }
        finally { Interlocked.Decrement(ref _keysInHook); }
        long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        _hookTicks += dt;
        if (dt > _hookMaxTicks) _hookMaxTicks = dt;
        if (++_hookN >= 200 && Log is { } log)
        {
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            log(FormattableString.Invariant($"hook: n={_hookN} mean={_hookTicks * f / _hookN:0.000} ms max={_hookMaxTicks * f:0.000} ms"));
            _hookN = 0; _hookTicks = 0; _hookMaxTicks = 0;
        }
        return r;
    }

    private IntPtr KeyboardProcCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (k.dwExtraInfo != Marker && Enabled)
            {
                int msg = wParam.ToInt32();
                bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                try
                {
                    if (down) { if (OnKeyDown(k.vkCode, k.scanCode)) return (IntPtr)1; }
                    else if (k.vkCode >= 0x70 && k.vkCode <= 0x77) { if (FKeyUp(k.vkCode)) return (IntPtr)1; }
                    else if (_consumed.Remove(k.vkCode)) return (IntPtr)1;   // swallow the matching key-up
                }
                catch (Exception ex) { Log?.Invoke("hook error: " + ex.Message); }
            }
        }
        return Native.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int m = wParam.ToInt32();
            try
            {
                if (m == Native.WM_LBUTTONDOWN || m == Native.WM_RBUTTONDOWN || m == Native.WM_MBUTTONDOWN)
                {
                    var ms = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                    if (!_win.Contains(ms.pt.X, ms.pt.Y))
                    {
                        _downPt = ms.pt; _downTick = Environment.TickCount64;   // plan 07: drag / double click (selection gesture)
                        // the taskbar, the tray icon or our own windows: the text is not left (D23), nothing to read there
                        _mouseDownShell = Native.IsShellOrOwn(Native.GetAncestor(Native.WindowFromPoint(ms.pt), Native.GA_ROOT));
                        _clickVersion = Jump("click", -1, move: _mouseDownShell ? SessionMove.Keep : SessionMove.Leave);   // caret probably moved
                        _mouseDownOutside = true;
                    }
                }
                else if ((m == Native.WM_LBUTTONUP || m == Native.WM_RBUTTONUP || m == Native.WM_MBUTTONUP) && _mouseDownOutside)
                {
                    _mouseDownOutside = false;   // read once the app has placed the caret (the hook sees the click first)
                    if (m == Native.WM_LBUTTONUP) SelectionGesture(Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam).pt);
                    // version of the button-down reset: anything typed during the click makes the read stale
                    if (Enabled && !_mouseDownShell) RequestRead(_clickVersion, Native.GetForegroundWindow(), 50, "click");
                }
            }
            catch (Exception ex) { Log?.Invoke("hook error: " + ex.Message); }
        }
        return Native.CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    /// <summary>Plan 07: a left button-up after a drag or a double click is a selection gesture (the Ctrl+C fallback may be
    /// tried when the window's own read gives no selection). Hook thread, O(1).</summary>
    private void SelectionGesture(Native.POINT up)
    {
        long now = Environment.TickCount64;
        bool drag = Math.Abs(up.X - _downPt.X) > 4 || Math.Abs(up.Y - _downPt.Y) > 4;
        bool dbl = _downTick - _lastUpTick <= _dblClickMs && Math.Abs(up.X - _lastUpPt.X) <= 4 && Math.Abs(up.Y - _lastUpPt.Y) <= 4;
        if (drag || dbl) Interlocked.Exchange(ref _selGesture, _clickVersion);
        _lastUpTick = now; _lastUpPt = up;
    }

    /// <summary>Plan 07 keys while the reading view is shown (hook thread): Space / F9 without modifier = Play / Pause,
    /// swallowed (down and up: the up is swallowed through <c>_consumed</c>; an auto-repeated down is swallowed without
    /// toggling again). Esc leaves (the selection is « dismissed »).</summary>
    /// <returns>true to swallow the key.</returns>
    private bool ReadingKey(uint vk, IntPtr fg, bool ctrl, bool alt, bool shift)
    {
        // only in the window whose selection is shown: after Alt+Tab elsewhere, Space / F9 belong to that window (D24)
        if ((vk == Native.VK_SPACE || vk == Native.VK_F9) && !ctrl && !alt && !shift && _win.Shown && fg == _reading!.ReadingWindowHandle &&
            !Native.IsDown(0x5B) && !Native.IsDown(0x5C))
        {
            if (_consumed.Add(vk)) _ui.BeginInvoke(() => _reading!.TogglePlay(vk == Native.VK_F9 ? "F9" : "Space"));
            return true;
        }
        if (vk == 0x1B) _ui.BeginInvoke(() => _reading!.Exit("Esc", dismissed: true));
        return false;
    }

    /// <summary>Leave the reading mode (any thread: posted to the UI thread).</summary>
    private void ExitReadingSoon(string why)
    {
        if (_reading is { Active: true } r) _ui.BeginInvoke(() => r.Exit(why));
    }

    /// <summary>Key-up of F1…F8 (hook thread): the held record ends; swallowed when its first down was handled (never a lone
    /// key-up to the application), passed when that down was passed.</summary>
    /// <returns>true to swallow the key-up.</returns>
    private bool FKeyUp(uint vk) => _fKeys.Up((int)(vk - 0x70));

    /// <summary>A session lock or unlock (UI thread, from the session watcher): a key held at Win+L has its key-up delivered
    /// to the secure desktop, so the hook drops its F-key records at the next F-key down.</summary>
    public void ForgetHeldKeys() => Interlocked.Increment(ref _fKeysGeneration);

    /// <returns>true to swallow the key.</returns>
    private bool OnKeyDown(uint vk, uint scan)
    {
        Interlocked.Increment(ref _lineEndGen);   // plan 10B: any key cancels a pending line-end marker (Enter re-arms it)
        var fg = Native.GetForegroundWindow();
        if (fg != _lastFg && fg != _win.BarHwnd)   // backstop for the WinEvent (never for the bar: BUG-1, OnForeground)
        {
            Log?.Invoke($"  foreground changed: reset"); OnForeground(fg);
        }
        bool fkey = vk >= 0x70 && vk <= 0x77;
        // passed to the app untouched: its key-up must pass too (no record left from an earlier handled down)
        if (Native.IsOwnWindow(fg)) { if (fkey) _fKeys.Forget((int)(vk - 0x70)); return false; }   // our tray menu / À propos: not tracked, no bar
        if (_privateHost) { if (fkey) _fKeys.Forget((int)(vk - 0x70)); return false; }             // 0.8.3: a terminal / remote window: never tracked (owner)

        bool ctrl = Native.IsDown(Native.VK_CONTROL), alt = Native.IsDown(Native.VK_MENU), shift = Native.IsDown(Native.VK_SHIFT);
        // plan 07: the user's own copy / cut (physical keys): the Ctrl+C fallback never restores over it
        if (ClipboardCopy.IsCopyChord(vk, ctrl, shift, alt)) Interlocked.Exchange(ref ClipboardCopy.LastUserCopyTick, Environment.TickCount64);
        if (_reading is { Active: true } && ReadingKey(vk, fg, ctrl, alt, shift)) return true;

        if (vk == 0x78 && _markAction && _keysOn && !ctrl && !alt && !shift && _win.Shown)   // F9: the ≈ / ? marker (plan 10B)
        {
            // an auto-repeated F9 (held down) acts once: the first down is consumed until its key-up, as in ReadingKey
            if (_consumed.Add(vk)) _ui.BeginInvoke(() => OnMarker("F9"));
            return true;
        }
        if (vk >= 0x70 && vk <= 0x77)   // F1..F8
        {
            int i = (int)(vk - 0x70);
            // 2026-11-25: insert, or first / second press (« Touches F1–F8 »), Maj+F listen (A); a key still held repeats only
            // its first down's action: passed stays passed, an « insérer tout de suite » insert inserts again, the rest is
            // swallowed whatever the bar shows now (review of 238c58b)
            // a swallowed down: its record decides (its key state is probably never set); a passed one: the key state, read
            // before Windows updates it (down = already down = an auto-repeat)
            bool wasDown = Native.IsDown((int)vk);
            var first = _fKeys.Held(i, wasDown, Volatile.Read(ref _fKeysGeneration));
            var act = KeyboardPolicy.FKeyAction(i, ctrl, alt, shift, _keysOn, _listenShift, _keysListenFirst, _suggCount, first);
            _fKeys.Record(i, first ?? act);
            // VM check of the key-state premise (no word: the decision only)
            Log?.Invoke($"  F{i + 1} down: isDown={(wasDown ? 1 : 0)} held={(first is { } f ? f.ToString() : "-")} -> {act}");
            switch (act)
            {
                case FKeyAct.Insert: _ui.BeginInvoke(() => InsertTile(i, "F" + (i + 1))); return true;   // heard just before: not said again
                // armed already here (not only once the UI thread ran the press): a key typed right after it cancels too
                case FKeyAct.Press: _listenArmed = true; _ui.BeginInvoke(() => OnTilePress(i, "F" + (i + 1))); return true;
                case FKeyAct.Listen: _listenArmed = true; _ui.BeginInvoke(() => ListenTo(i, "Shift+F" + (i + 1), arm: Rings)); return true;
                case FKeyAct.Swallow: return true;
            }
            Interlocked.Increment(ref _keySeq);   // full fence before the flags are read (pairs with Heard)
            if (_listenArmed || _wordHeard) _ui.BeginInvoke(() => CancelListen("key"));   // Ctrl+Fn, Fn with no word: to the app
            return false;
        }
        // any other key but a modifier, a lock or Win cancels a ringed tile and forgets a word heard without a ring (Esc,
        // typing, Backspace, navigation, shortcuts): a much later insertion of that tile is never taken for a confirmation
        if (!KeyboardPolicy.IsModifierOrLock(vk))
        {
            Interlocked.Increment(ref _keySeq);   // full fence before the flags are read (pairs with Heard)
            if (_listenArmed || _wordHeard) _ui.BeginInvoke(() => CancelListen("key"));
        }

        switch (vk)
        {
            case 0x10: case 0x11: case 0x12: case 0x14: case 0x5B: case 0x5C: case 0x90:
            case 0xA0: case 0xA1: case 0xA2: case 0xA3: case 0xA4: case 0xA5:
                return false;                                                       // modifiers, locks, Win
            // Esc: the caret does not move; a read re-seeds the tracker where the host is readable (plan 10B re-review: else
            // the first word typed after Esc has an unknown start). The read only seeds: no D23 begin, no selection decision
            // (a selection dismissed with Esc stays dismissed)
            case 0x1B: Jump(ReasonEsc, 50, move: SessionMove.Keep); return false;
            case 0x08:                                                              // Backspace
                ExitReadingSoon("Backspace");
                if (ctrl) { Jump("ctrl", 120, move: SessionMove.End); return false; }   // Ctrl+Backspace: a word gone unseen
                if (_suppress) return false;                                        // password field
                BackspaceEffect bs; long bv; bool keysOnly;
                lock (_sync)
                {
                    bs = _tracker.Backspace(); _echo.Backspace(); _session?.Backspace(); ProbeEdits(fg);
                    bv = _tracker.Version; keysOnly = _tracker.Origin == "keys";
                    if (_session is { Left: true }) bs = BackspaceEffect.InWord;   // a leave's own read decides (D23): not superseded
                }
                // back across a word boundary: the tracker restored the previous word from the text it saw typed, or lost
                // it (barrier, text rebuilt from a read, never seen). Lost → read the caret; restored where reads work → a
                // read checks it (it replaces the exact typed text only if the document says otherwise, see OnRead)
                if (bs == BackspaceEffect.Lost || (bs == BackspaceEffect.Crossed && !keysOnly))
                    if (Enabled && !Native.IsOwnWindow(fg)) RequestRead(bv, fg, 50, ReasonBackspace);
                ScheduleUpdate(); return false;
            case 0x0D:                                                              // Enter
                ExitReadingSoon("Enter");
                EchoEvent? e = null;
                FinishedWord? lineEnd = null;                                       // plan 10B, owner option B
                long lineVersion = 0;
                bool fieldWasClear = _field == FieldClear;                          // the line was typed in a non-password field
                bool settleEnter = false;
                lock (_sync)
                {
                    if (!_suppress)
                    {
                        _tracker.Type('\n'); e = _echo.Type('\n');
                        lineEnd = _tracker.Finished; lineVersion = _tracker.Version;
                        _session = RereadFlow.OnEnter(_session);   // unproven edits: the focus may move now (D23 privacy)
                        _session?.Type('\n'); ProbeEdits(fg);
                        settleEnter = SettleWanted('\n', fg);      // LibreOffice: its word completion is accepted by Enter
                    }
                    // Enter often moves the focus (two-step logins: name, Enter, password field): ask again
                    Rearm();
                }
                if (e != null) _speaker?.Echo(e, fg);
                if (settleEnter) _settleRead!.Request(lineVersion, fg, SettlePolicy.DelayMs, ReasonSettle);
                HideSoon();
                // the last word of the line gets its mark (owner, 2026-11-14), but only once the app has handled Enter: a
                // one-shot timer re-checks then that nothing moved (same window, same focused control, no key / jump)
                if (lineEnd is { } le && fieldWasClear && _spellingOn)
                {
                    long gen = Interlocked.Read(ref _lineEndGen);
                    var (focus, caret, top) = FocusOf(fg);   // before the app handles Enter (the hook runs first)
                    _ui.BeginInvoke(() => ArmLineEnd(le, lineVersion, fg, focus, caret, top, gen));
                }
                return false;
            case 0x09: case 0x21: case 0x22: case 0x23: case 0x24:                  // Tab, PgUp/Dn, End, Home
            case 0x25: case 0x26: case 0x27: case 0x28:                             // arrows (Ctrl+arrows too)
                // Shift + navigation extends a selection (plan 07): the selection read waits until the keys stop
                if (shift && vk != 0x09) { Interlocked.Exchange(ref _selGesture, Jump(ReasonShift, 50, move: SessionMove.Leave)); return false; }
                Jump("nav", 50, move: SessionMove.Leave); return false;
            case 0x2D:                                                              // Ins: overwrite mode, the mirror can't follow
                Jump("nav", 50, move: SessionMove.End); return false;
            case 0x2E:                                                              // Del
                ExitReadingSoon("Delete");
                bool deleted = false;
                if (!ctrl && !shift && !_suppress)                                  // Ctrl+Del (a word), Shift+Del (cut): unseen
                    lock (_sync)
                        if (_session is { } s)
                        {
                            s.Delete();                                             // left already: only marks it typed-while-left
                            deleted = !s.Left;
                            ProbeEdits(fg);
                        }
                // the tracker still re-reads (its suffix changed); the session stays: a forward delete is an edit (D23)
                Jump(deleted ? ReasonDelete : "nav", 50, move: deleted ? SessionMove.Keep : ctrl || shift ? SessionMove.End : SessionMove.Leave);
                return false;
        }

        if (ctrl && !alt)                                        // shortcut (Ctrl+Alt = AltGr): paste, undo…
        {
            long v = Jump("ctrl", 120, move: SessionMove.End);
            if (vk == 0x41) Interlocked.Exchange(ref _selGesture, v);   // Ctrl+A: select all (plan 07)
            return false;
        }
        if (_suppress) return false;                             // password field: not tracked, not logged

        // VK_PACKET: a Unicode character injected by another tool (on-screen keyboard, dictation, IME)
        var text = vk == 0xE7 ? ((char)scan).ToString() : Translate(vk, scan, fg);
        bool known = _probe == ProbeDone;
        if (_probe == ProbeNotAsked && !string.IsNullOrEmpty(text))
            lock (_sync)
                if (_probe == ProbeNotAsked)
                {
                    // first character since the jump / Enter: is the focused field (now) a password field?
                    // own thread, no wait (Request is a short lock + event set)
                    long id = _fieldProbe?.Request(fg) ?? 0;
                    _fieldProbeId = id;
                    _fieldProbeEpoch = _epoch;
                    _probe = id == 0 ? ProbeNotAsked : ProbePending;
                }
        Log?.Invoke(known ? $"  key vk=0x{vk:X2} -> '{text}'" : $"  key vk=0x{vk:X2} -> (pending)");   // debug, only with --log
        if (string.IsNullOrEmpty(text)) return false;      // dead key or non-character
        ExitReadingSoon("typing");                         // plan 07: typing replaces the selection, back to predictions
        if (text.Length == 1 && FixAutoSpace(vk, text[0], fg, ctrl || alt)) return true;
        EchoEvent? ev = null;
        bool settle;
        long sv;
        lock (_sync)
        {
            foreach (var c in text) { _tracker.Type(c); ev = _echo.Type(c) ?? ev; _session?.Type(c); }
            // the echo says it (the word, or the whole edited sentence with it): not re-read again
            if (ev is { Kind: EchoKind.Word } or { AfterJump: true }) _session?.MarkWordSpoken();
            ProbeEdits(fg);
            // LibreOffice phase 1: AutoCorrect runs on separators; read again once it has (the version taken here)
            settle = text.Length == 1 && SettleWanted(text[0], fg);
            sv = _tracker.Version;
        }
        if (ev != null) Echo(ev, fg);   // an enqueue: the password guard runs on its own thread
        if (settle) _settleRead!.Request(sv, fg, SettlePolicy.DelayMs, ReasonSettle);
        ScheduleUpdate();
        return false;
    }

    /// <summary>LibreOffice phase 1 (under _sync; hook thread, O(1): a volatile compare and a few fields): ask a settle
    /// read after <paramref name="c"/>? Only in a Writer window where IA2 answers, for a separator, and never while our
    /// auto-space is armed, a D22 read-back is pending or a ≈ / « ne » replacement is armed (<see cref="SettlePolicy"/>).</summary>
    private bool SettleWanted(char c, IntPtr fg) =>
        _settleRead != null && _lo is { } lo &&
        SettlePolicy.ShouldAsk(c, lo.IsLive(fg), _autoSpace.IsArmed(_tracker.Version, fg), ReadBackPending(fg), CorrectionArmed());

    /// <summary>A D22 read-back waits for its answer at the current version (under _sync).</summary>
    private bool ReadBackPending(IntPtr fg) => _pendingCheck.IsPending(fg, _tracker.Version);

    /// <summary>A ≈ / « ne » replacement is armed for the current version, or waits for its read (under _sync; the targets
    /// are written on the UI thread: reference reads).</summary>
    private bool CorrectionArmed()
    {
        long v = _tracker.Version;
        return Volatile.Read(ref _correction) is { } c && c.Version == v || Volatile.Read(ref _group) is { } g && g.Version == v ||
               _pendingFix.Fix != null || _pendingGroup.Fix != null || _pendingCompound.Fix != null;
    }

    /// <summary>
    /// LibreOffice phase 1, the settle read (its own thread): the document after the host's own changes (AutoCorrect, word
    /// completion). Agrees with the keys → kept. Disagrees, nothing typed since, same window, in budget, and no auto-space /
    /// read-back / replacement armed → the tracker is seeded (the echo only if blank) and the D23 session rebased — only
    /// when a session exists, is not left, is in the same window and is not dirty (never a landing, never a selection
    /// decision). The log has the decision, never the text.
    /// </summary>
    private void OnSettleRead(ReadRequest q, ReadResult r)
    {
        _readable.Note(q.Fg, PidOf(q.Fg), r.Status, groupRead: false);
        if (r.Status == ReadStatus.Password) { SuppressPassword(q.Fg, "settle read"); return; }
        if (r.Status != ReadStatus.Ok) { Log?.Invoke($"settle: skipped({r.Status} {r.Ms} ms)"); return; }
        string? why = null;
        long seeded = -1;
        bool rebase = false;
        lock (_sync)
        {
            if (!KeyboardPolicy.ShouldApply(q.Version, _tracker.Version, q.Fg, Native.GetForegroundWindow(), r.Ms)) why = "typed meanwhile";
            else if (_autoSpace.IsArmed(_tracker.Version, q.Fg) || ReadBackPending(q.Fg) || CorrectionArmed()) why = "armed";
            else if (!SettlePolicy.Agrees(r.Ctx.Prefix, r.Ctx.Suffix, r.Ctx.Context, _tracker.Current, _tracker.Context))
            {
                var c = r.Ctx;
                _tracker.Seed(c.Prefix, c.Context, c.Separators, c.Suffix, c.NextChar, r.Source, c.AtSentenceStart, c.Apostrophe);
                if (_echo.IsBlank) _echo.Seed(c.Prefix, c.AtSentenceStart && c.Context.Count == 0);
                seeded = _tracker.Version;
                rebase = _session is { Left: false } s && s.Fg == q.Fg && !s.Dirty;
            }
        }
        Log?.Invoke(why != null ? $"settle: skipped({why}) {r.Ms} ms" : seeded < 0 ? $"settle: agrees {r.Ms} ms"
                                : $"settle: seeded {r.Ms} ms{(rebase ? ", session rebased" : "")}");
        if (seeded < 0) return;
        ScheduleUpdate();
        if (!rebase) return;
        try { Reread(q, r, true, seeded); }
        catch (Exception ex) { Log?.Invoke($"reread error: {ex.GetType().Name}: {ex.Message}"); lock (_sync) _session = null; }
    }

    /// <summary>The typing echo (hook thread; the UI thread for an insertion). A sentence end typed after a jump into the
    /// sentence (D17 amendment, owner 2026-11-14) asks the sentence reader for the whole sentence; the words typed since
    /// the jump are the fallback (read failed, over budget, overtaken by a jump, or no finished sentence after a second
    /// read). While that read is pending every other echo is deferred behind it (<see cref="SentenceEchoGate"/>).</summary>
    private void Echo(EchoEvent ev, IntPtr fg)
    {
        if (ev is { Kind: EchoKind.Sentence, AfterJump: true } && _sentenceRead is { } q)
        {
            long seq, v;
            List<SentenceEchoGate.Deferred> now;
            lock (_sync)
            {
                (seq, now) = _gate.Begin(ev.Text, fg, Environment.TickCount64);   // a previous read still pending: given up
                v = _jumps;
            }
            SayDeferred(now);
            q.Request(v, fg, SentenceReadDelayMs, ReasonSentence, trigger: seq);
            _sentenceTimer?.Change(SentenceEchoGate.ExpiryMs + 50, Timeout.Infinite);
            return;
        }
        if (ev.Text.Length == 0) return;
        if (Deferred(new SentenceEchoGate.Deferred(ev.Kind == EchoKind.Sentence ? SpeechKind.Sentence : SpeechKind.Word, ev.Text, ev, fg))) return;
        _speaker?.Echo(ev, fg);
    }

    /// <summary>The « inserted » echo: deferred behind a pending sentence read (then said as a word, after the sentence),
    /// else said at once (interrupts). <paramref name="after"/> (2026-11-25, a confirmation whose group differs from the word
    /// just heard: « les amis », « l'école »): queued after the word still being spoken, never cutting it.</summary>
    private void SayInserted(string said, IntPtr fg, bool after = false)
    {
        // deferred as a word echo: through the password guard, in order behind the sentence (never ahead of it)
        if (Deferred(new SentenceEchoGate.Deferred(SpeechKind.Word, said, new EchoEvent(EchoKind.Word, said), fg))) return;
        _speaker?.SayChecked(said, SpeechKind.Inserted, fg, after);   // 0.8.3: only in a field proven non-password (owner)
    }

    /// <returns>true when <paramref name="d"/> waits for the pending sentence read.</returns>
    private bool Deferred(SentenceEchoGate.Deferred d)
    {
        bool deferred;
        List<SentenceEchoGate.Deferred> before;
        lock (_sync) deferred = _gate.Defer(d, Environment.TickCount64, out before);
        if (before.Count > 0) { Log?.Invoke("sentence echo: read overdue, typed words"); SayDeferred(before); }
        return deferred;
    }

    private void SayDeferred(List<SentenceEchoGate.Deferred> items)
    {
        foreach (var d in items)
        {
            if (d.Echo is { } e) _speaker?.Echo(e, d.Fg);
            else _speaker?.SayChecked(d.Text, d.Kind, d.Fg);
        }
    }

    /// <summary>Timer: a sentence read that never came back (lost its slot, stuck worker) is given up.</summary>
    private void ExpireSentence()
    {
        List<SentenceEchoGate.Deferred> due;
        lock (_sync) due = _gate.Expire(Environment.TickCount64);
        if (due.Count == 0) return;
        Log?.Invoke("sentence echo: read overdue, typed words");
        if (_enabled && !_suppress) SayDeferred(due);
    }

    /// <summary>The sentence read (its own thread): say the document's sentence, or the typed words, then the echoes that
    /// waited for it. The read checked the field for a password before reading any text (Password: tracking suspended,
    /// nothing said); the echo guard probes it again before speaking. A read that found no finished sentence (the app
    /// had not handled the period yet) is asked once more, 80 ms later. The log has the decision and the length, never
    /// the text.</summary>
    private void OnSentenceRead(ReadRequest q, ReadResult r)
    {
        long seq = q.Trigger;
        if (r.Status == ReadStatus.Password)
        {
            // whatever request it answered, the focused field is a password field (SuppressPassword drops the gate)
            SuppressPassword(q.Fg, "sentence read");
            return;
        }
        string? typed;
        lock (_sync) typed = _gate.Current is { } c && c.Seq == seq ? c.Typed : null;
        if (typed is null) return;   // given up, or replaced by a newer one
        var doc = r.Status == ReadStatus.Ok && r.Window != null ? SentenceDocRead.Find(r.Window, typed) : null;
        if (doc is null && r.Status == ReadStatus.Ok)
        {
            bool again;
            lock (_sync) again = _gate.Retry(seq);
            if (again) { _sentenceRead?.Request(q.Version, q.Fg, SentenceRetryMs, ReasonSentence, trigger: seq); return; }
        }
        (SentenceEchoGate.Pending Sentence, List<SentenceEchoGate.Deferred> Deferred)? taken;
        lock (_sync) taken = _gate.Take(seq);
        if (taken is not { } t) return;
        if (!_enabled || _suppress) return;
        Log?.Invoke(doc != null ? $"sentence echo from the document: {r.Ms} ms ({doc.Length} chars, {typed.Length} typed{(t.Sentence.Retried ? ", 2nd read" : "")})"
                                : $"sentence echo: typed words (read {r.Status} {r.Ms} ms{(t.Sentence.Retried ? ", 2nd read" : "")})");
        var text = doc ?? typed;
        if (text.Length > 0)
        {
            Action<SpeechOutcome>? done = null;
            if (doc != null)
            {
                lock (_sync) _gate.Saying(doc, t.Sentence.Fg, Environment.TickCount64);
                done = o => { lock (_sync) _gate.Outcome(doc, o); };
            }
            _speaker?.Echo(new EchoEvent(EchoKind.Sentence, text), t.Sentence.Fg, done);
        }
        SayDeferred(t.Deferred);
    }

    /// <summary>D22: <paramref name="c"/> typed right after our automatic space (same tracker version, same window).
    /// Absorb a second space, or replace our space by the punctuation (Backspace + text in <b>one</b> SendInput, sent
    /// from the hook thread before the key is swallowed so nothing typed later can overtake it). Feeds the tracker and
    /// the echo what the app now has. Hook thread.</summary>
    /// <returns>true to swallow the key.</returns>
    private bool FixAutoSpace(uint vk, char c, IntPtr fg, bool ctrlOrAlt)
    {
        (AutoSpaceFix Fix, string Text) d;
        // Ctrl / Alt / AltGr held: never inject a Backspace under a modifier (Ctrl+Backspace deletes a word)
        bool adaptive = false, ours = false, lo = false;
        lock (_sync)
        {
            d = _autoSpace.OnChar(c, _tracker.Version, fg, (PunctuationSpacing)_punct, ctrlOrAlt);
            if (d.Fix == AutoSpaceFix.Replace && KeyboardPolicy.HighPunct(c) && fg == _adaptiveFg)
            {
                adaptive = true;
                lo = _adaptiveLo;
                // this Word window was seen NOT to add its own space: put ours at once (the read-back still checks); never
                // in LibreOffice Writer, whose own U+00A0 always goes first (owner Q4)
                if (!lo && _appAddsSpace.TryGetValue(fg, out var adds) && !adds) { d.Text = AppRules.SpaceFor(_adaptiveSetting) + d.Text; ours = true; }
            }
        }
        if (d.Fix == AutoSpaceFix.Keep) return false;
        if (d.Fix == AutoSpaceFix.Absorb)
        {
            _consumed.Add(vk);   // still armed: "maison" F1, space, "," → "maison,"
            Log?.Invoke("  auto-space: extra space absorbed");
            return true;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var (sent, expected, err) = Inserter.Replace(0, 1, d.Text, Marker);
        double sendMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (sent == 0)
        {
            // blocked (UIPI…): nothing was injected, let the key through as typed (OnChar disarmed already)
            Log?.Invoke($"  auto-space: SendInput blocked (error {err}), key passed through");
            return false;
        }
        _consumed.Add(vk);
        EchoEvent? ev = null;
        lock (_sync)
        {
            _tracker.Backspace(); _echo.Backspace(); _session?.Backspace();
            foreach (var ch in d.Text) { _tracker.Type(ch); ev = _echo.Type(ch) ?? ev; _session?.Type(ch); }
            if (ev is { Kind: EchoKind.Word } or { AfterJump: true }) _session?.MarkWordSpoken();
            ProbeEdits(fg);
        }
        Log?.Invoke($"  auto-space: '{c}' -> BS + {Codes(d.Text)} (SendInput {sent}/{expected}, {sendMs.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} ms)");
        bool settle = false;
        long sv = 0;
        if (adaptive)
        {
            // read back what the app produced once it has handled the key (its own thread; the hook never waits)
            long v;
            lock (_sync) { v = _tracker.Version; _pendingCheck.Arm(c, ours, fg, v, lo); }
            _punctCheck?.Request(v, fg, 60, "punct");
        }
        else
            lock (_sync) { settle = SettleWanted(c, fg); sv = _tracker.Version; }   // LibreOffice: its AutoCorrect ran on the sign
        if (settle) _settleRead!.Request(sv, fg, SettlePolicy.DelayMs, ReasonSettle);
        if (ev != null) Echo(ev, fg);
        ScheduleUpdate();
        return true;
    }

    /// <summary>D22 read-back in Word (read-back thread): one space-like char before the punctuation → done; none →
    /// add ours (unless « Aucune »); two → remove one. Never when anything was typed since (version) or the window
    /// changed: we never edit text behind the user. Remembers per window whether Word adds its own space.
    /// LibreOffice Writer (phase 1, owner Q1 = yes; read through IA2): the same, except that two spaces are only logged —
    /// LibreOffice's own U+00A0 is never removed; then a settle read follows (its AutoCorrect ran on the sign).</summary>
    private void OnPunctCheck(ReadRequest q, ReadResult r)
    {
        (char Punct, bool Ours, IntPtr Fg, long Version, bool Lo) pc;
        PunctuationSpacing setting;
        lock (_sync) { pc = _pendingCheck.Current; setting = _adaptiveSetting; }
        if (pc.Version != q.Version || pc.Fg != q.Fg) return;   // superseded by a newer check
        string host = pc.Lo ? " (writer)" : "";
        if (r.Status != ReadStatus.Ok)
        {
            lock (_sync) _pendingCheck.Done(q.Fg, q.Version);
            Log?.Invoke($"  punct check: {r.Status} {r.Ms} ms, nothing done{host}");
            return;
        }
        var check = AppRules.Check(r.Before, pc.Punct);
        var action = AppRules.ReadBack(check, setting, pc.Lo);
        string what, sent = "";
        Native.INPUT[]? fix = null;
        // The edit is sent INSIDE the lock, right after the re-check, and only when no keyboard event is inside our hook
        // (_keysInHook): a key the hook is still handling (it may be waiting for this lock) could otherwise reach the app
        // before our keys; a key arriving after the check waits for the lock, then for its turn behind our queued events.
        // No deadlock: SendInput only queues the events (it never waits for the hook), and our own marked events return
        // from the hook before taking _sync.
        // The worker runs at Normal priority while the hook thread is AboveNormal: raise it for the locked section so a
        // preempted worker cannot hold _sync (and stall a physical key) for a whole scheduling interval under load.
        var prio = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        try
        {
        lock (_sync)
        {
            if (_tracker.Version != q.Version || Native.GetForegroundWindow() != q.Fg || Interlocked.Read(ref _keysInHook) != 0)
            { what = "typed meanwhile, nothing done"; check = SpacingCheck.Unknown; }
            else
                switch (check)
                {
                    case SpacingCheck.Ok:
                        RememberAdds(q.Fg, !pc.Ours);
                        what = pc.Ours ? "our space only (the app adds none)" : "the app added its own space";
                        break;
                    case SpacingCheck.Missing:
                        RememberAdds(q.Fg, false);
                        char sp = AppRules.SpaceFor(setting);
                        if (action != ReadBackFix.InsertOurs) what = "no space (setting none)";
                        else { fix = Inserter.BuildBeforeLast(false, sp.ToString(), Marker); what = $"the app added none: U+{(int)sp:X4} inserted"; }
                        break;
                    case SpacingCheck.Double:
                        RememberAdds(q.Fg, true);
                        if (action == ReadBackFix.RemoveOne)
                        {
                            fix = Inserter.BuildBeforeLast(true, "", Marker);
                            what = "two spaces (the app added one too): one removed";
                        }
                        else what = "two spaces: the app's own kept, nothing removed";
                        break;
                    default:
                        what = "text does not end with the punctuation, nothing done";
                        break;
                }
            if (fix != null) { var (s, e, err) = Inserter.Send(fix); sent = $" (SendInput {s}/{e}, error {err})"; }
        }
        }
        finally { Thread.CurrentThread.Priority = prio; }
        Log?.Invoke($"  punct check '{pc.Punct}' {r.Ms} ms: before=[{Codes(r.Before ?? "")}] {check}: {what}{sent}{host}");
        // answered: no longer pending (review S3–S7: else SettleWanted saw it pending forever and this settle never ran)
        lock (_sync) _pendingCheck.Done(q.Fg, q.Version);
        if (!pc.Lo) return;
        bool settle;
        long sv;
        lock (_sync) { settle = q.Version == _tracker.Version && SettleWanted(pc.Punct, q.Fg); sv = _tracker.Version; }
        if (settle) _settleRead!.Request(sv, q.Fg, SettlePolicy.DelayMs, ReasonSettle);
    }

    /// <summary>Per-window memory of the adaptive Word rule, bounded (a long session opens many windows; a forgotten
    /// window is simply read back again). Under _sync.</summary>
    private void RememberAdds(IntPtr fg, bool adds)
    {
        if (_appAddsSpace.Count >= 32 && !_appAddsSpace.ContainsKey(fg)) _appAddsSpace.Clear();
        _appAddsSpace[fg] = adds;
    }

    /// <summary>The injected characters for the log, spaces as code points (<c>U+202F '!'</c>).</summary>
    private static string Codes(string t) =>
        string.Join(" ", t.Select(ch => ch is ' ' or KeyboardPolicy.NarrowNbsp or KeyboardPolicy.Nbsp ? $"U+{(int)ch:X4}" : $"'{ch}'"));

    /// <summary>The character(s) this key produces in the foreground window's keyboard layout (AZERTY…),
    /// without disturbing its dead-key state.</summary>
    private static string? Translate(uint vk, uint scan, IntPtr fg)
    {
        uint tid = Native.GetWindowThreadProcessId(fg, out _);
        var hkl = Native.GetKeyboardLayout(tid);
        var state = new byte[256];
        if ((Native.GetKeyState(Native.VK_CAPITAL) & 1) != 0) state[Native.VK_CAPITAL] = 0x01;
        foreach (var m in new[] { Native.VK_SHIFT, Native.VK_LSHIFT, Native.VK_RSHIFT, Native.VK_CONTROL,
                                  Native.VK_LCONTROL, Native.VK_RCONTROL, Native.VK_MENU, Native.VK_LMENU, Native.VK_RMENU })
            if (Native.IsDown(m)) state[m] = 0x80;
        var sb = new StringBuilder(8);
        int r = Native.ToUnicodeEx(vk, scan, state, sb, sb.Capacity, 4, hkl);
        return r > 0 ? sb.ToString(0, r) : null;
    }

    /// <summary>The caret moved to an unknown place: forget the tracked word and, unless
    /// <paramref name="delayMs"/> &lt; 0, read the text there once the app has handled the event.
    /// Password suppression survives jumps inside the same window (arrows or Ctrl+V in a password field
    /// must not start tracking); it ends on a foreground change or when the latest read finds a
    /// non-password field. Hook thread.</summary>
    /// <returns>the tracker version after the reset (what a later read must still match).</returns>
    private long Jump(string reason, int delayMs, bool fgChanged = false, SessionMove move = SessionMove.Leave)
    {
        Interlocked.Increment(ref _lineEndGen);   // plan 10B: a jump (click, focus, foreground, navigation) cancels a line-end marker
        long v;
        lock (_sync)
        {
            bool probePending = _probe == ProbePending;
            _tracker.Reset(); _echo.Reset(); v = _tracker.Version; Rearm();
            _jumps++;   // a sentence read asked before this jump is stale (its typed words are said instead)
            _session = RereadFlow.OnJump(_session, move, probePending);   // O(1): the read decides what the move meant (D23)
        }
        if (fgChanged) _suppress = false;
        HideSoon();
        var fg = Native.GetForegroundWindow();
        if (delayMs >= 0 && Enabled && !Native.IsOwnWindow(fg)) RequestRead(v, fg, delayMs, reason);   // never read our own windows
        return v;
    }

    /// <summary>Ask for a caret read, remembering the epoch: a read asked before an Enter / newer jump must not mark
    /// the field that has the focus afterwards as clear. Hook thread.</summary>
    private void RequestRead(long version, IntPtr fg, int delayMs, string reason)
    {
        if (_privateHost) return;   // 0.8.3: a terminal / remote window is never read
        lock (_sync) _readEpoch = _epoch;
        _reader?.Request(version, fg, delayMs, reason);
    }

    private long _fgJumpTick;   // hook thread only: when the last foreground jump happened

    /// <summary>EVENT_SYSTEM_FOREGROUND and EVENT_OBJECT_FOCUS (hook thread, via its message loop; out of context, so
    /// never inside a low-level hook callback). Cheap filters first: most focus events come from other windows.</summary>
    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (evt == Native.EVENT_OBJECT_FOCUS) { OnFocusEvent(hwnd, idObject, idChild); return; }
            OnForegroundEvent(hwnd);
        }
        catch (Exception ex) { Log?.Invoke("hook error: " + ex.Message); }
    }

    private long _fgEventGen;       // hook thread: bumped by each foreground event that supersedes a pending re-check
    private long _fgRecheckFor;     // the generation the pending re-check belongs to (hook thread writes, timer reads)
    private int _fgRecheckAttempt;  // hook thread: the number of the pending re-check (1..MaxRechecks)
    private Timer? _fgRecheckTimer; // one-shot, made in Start (never inside a WinEvent); posts WmForegroundRecheck
    private const uint WmForegroundRecheck = 0x8000 + 0x51;   // WM_APP + n, a thread message of the hook loop

    /// <summary>Timer thread: hand the re-check to the hook thread (its loop runs <see cref="OnForegroundRecheck"/>).</summary>
    private void PostForegroundRecheck(object? _)
    {
        uint t = _hookThreadId;
        if (t != 0) Native.PostThreadMessage(t, WmForegroundRecheck, (IntPtr)Interlocked.Read(ref _fgRecheckFor), IntPtr.Zero);
    }

    private void ArmForegroundRecheck(int ms)
    {
        try { _fgRecheckTimer?.Change(ms, Timeout.Infinite); }
        catch (ObjectDisposedException) { }   // stopping: no re-check any more
    }

    /// <summary>WR5: EVENT_SYSTEM_FOREGROUND (hook thread). The real foreground decides (<see cref="ForegroundEvents"/>):
    /// an event that names another window (an activation refused for now) never becomes the tracked window; the real
    /// foreground is looked at again <see cref="ForegroundEvents.RecheckMs"/> later (then up to twice more).</summary>
    private void OnForegroundEvent(IntPtr hwnd)
    {
        var real = Native.GetForegroundWindow();
        // a frame's inner window (UWP) names the frame as its root: the same window, no re-check
        var root = hwnd == real || hwnd == IntPtr.Zero ? hwnd : Native.GetAncestor(hwnd, Native.GA_ROOT);
        var d = ForegroundEvents.Decide(hwnd, root, real, _lastFg, _win.BarHwnd);
        if (d.NewGeneration) _fgEventGen++;
        if (d.Recheck)
        {
            // only for an event that named another window (no text, no handle)
            Log?.Invoke($"  foreground event for a window not in front: {(d.Apply != IntPtr.Zero ? "real foreground used, " : "")}re-check in {ForegroundEvents.RecheckMs} ms");
            Interlocked.Exchange(ref _fgRecheckFor, _fgEventGen);
            _fgRecheckAttempt = 1;
            ArmForegroundRecheck(ForegroundEvents.RecheckMs);
        }
        if (d.Apply != IntPtr.Zero) OnForeground(d.Apply);
    }

    /// <summary>The re-check after a foreground event that named another window (hook thread, from its message loop).</summary>
    private void OnForegroundRecheck(long gen)
    {
        try
        {
            var r = ForegroundEvents.Recheck(gen, _fgEventGen, Native.GetForegroundWindow(), _lastFg, _win.BarHwnd, _fgRecheckAttempt);
            if (r.Apply != IntPtr.Zero && OnForeground(r.Apply)) Log?.Invoke($"  foreground re-check {_fgRecheckAttempt}: changed");
            if (r.RearmMs > 0) { _fgRecheckAttempt++; ArmForegroundRecheck(r.RearmMs); }
        }
        catch (Exception ex) { Log?.Invoke("hook error: " + ex.Message); }
    }

    /// <summary>A foreground change seen by the WinEvent or by a key-down's backstop (hook thread). BUG-1: the bar made
    /// the foreground by a UI Automation call (Invoke, SetFocus) is not a jump — the bar gives the foreground back itself
    /// (<see cref="SuggestionWindow"/>'s WM_ACTIVATE) and <see cref="_lastFg"/> stays the text window, so its return is
    /// « the same window » and nothing is reset. The return's focus events are those of a window's first focus.</summary>
    /// <returns>false when the change was ignored (same window, or the bar).</returns>
    private bool OnForeground(IntPtr fg)
    {
        if (fg == _lastFg) return false;   // cheap first: no Win32 call for the same window
        var move = KeyboardPolicy.ClassifyForeground(fg, _lastFg, _win.BarHwnd, Native.IsShellOrOwn(fg));
        if (move == ForegroundMove.Bar) { _fgJumpTick = Environment.TickCount64; return false; }
        _lastFg = fg;
        FocusJump(fg, move == ForegroundMove.Own);
        return true;
    }

    /// <summary>The foreground window changed. Our windows (tray menu, À propos, Réglages) and the taskbar / tray are
    /// not « leaving » the text (D23: Keep, nothing read); any other window is a leave. Hook thread.</summary>
    private void FocusJump(IntPtr fg, bool shellOrOwn)
    {
        _fgJumpTick = Environment.TickCount64;
        // a key-up may have gone to the other (elevated, secure) window: no F-key record survives a foreground change
        // (WinEvent or seen at a key-down; the bar and its reading view never keep the foreground: a UI Automation call
        // that activates the bar is given back at once and never reaches here, BUG-1)
        _fKeys.Clear();
        bool priv = PrivateHosts.Check(fg, out bool unknown);   // a few Win32 calls, cached per window
        if (priv != _privateHost) Log?.Invoke(priv ? (unknown ? "  process not identified: treated as private" : "  terminal or remote window in front: nothing tracked nor said")
                                                   : "  terminal or remote window left");
        _privateHost = priv;
        if (shellOrOwn)
        {
            if (Native.IsOwnWindow(fg)) Log?.Invoke("  foreground is ours (tray menu / À propos): bar hidden, nothing read");
            Jump("own", -1, fgChanged: true, move: SessionMove.Keep);
            return;
        }
        Jump("focus", 120, fgChanged: true, move: SessionMove.Leave);
    }

    /// <summary>The keyboard focus moved inside the foreground window without a key, click or window switch (an
    /// in-page dialog with an autofocused password field, a two-step login): a leave, the probe re-armed, a read asked —
    /// keys typed in the new field are never placed in the session's mirror. Same object again, other windows, a held
    /// mouse button (its button-up read follows) and the focus event that follows a window switch are ignored.</summary>
    private void OnFocusEvent(IntPtr hwnd, int idObject, int idChild)
    {
        if (!_enabled || hwnd == IntPtr.Zero) return;
        var fg = Native.GetForegroundWindow();
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root != fg) return;
        // LibreOffice phase 1: Writer's focus object (one per paragraph) is recorded before any filter — also before the
        // window-switch check, since a new window's (a dialog's) first focus event may come before its foreground event —
        // so the first focus of a window counts too; O(1) (a struct write and a prefetch request; the module is the title,
        // no message). The frame class first: one GetClassName for every other app's focus events, no process lookup.
        // A LibreOffice dialog with no IA2 answer anyway is a password field for PasswordProbe.
        if (_lo is { Enabled: true } lo && HostApps.IsFrame(root) && HostApps.ModuleOf(root) is var mod && HostApps.Ia2Focus(mod))
            lo.NoteFocus(root, hwnd, idObject, idChild, prefetch: mod == LibreOfficeModule.Writer);
        if (fg != _lastFg) return;                                // a window switch: the foreground path handles it
        if (!_focusFilter.ShouldJump(root, fg, hwnd, idObject, idChild, _mouseDownOutside, Native.IsShellOrOwn(root))) return;
        if (Environment.TickCount64 - _fgJumpTick < 200) return;   // the new window's own first focus
        // 0.8.3 re-review: the focused control may be a terminal (an IDE's console pane) or leave one
        bool priv = PrivateHosts.Check(fg, out bool unknown);
        if (priv != _privateHost) Log?.Invoke(priv ? (unknown ? "  process not identified: treated as private" : "  terminal or remote window in front: nothing tracked nor said")
                                                   : "  terminal or remote window left");
        _privateHost = priv;
        Log?.Invoke("  focus moved inside the window");
        Jump("focus", 50, move: SessionMove.Leave);
    }

    /// <summary>Result of a caret read (reader thread).</summary>
    private void OnRead(ReadRequest q, ReadResult r)
    {
        _readable.Note(q.Fg, PidOf(q.Fg), r.Status, groupRead: false);   // 0.11.0: an Ok read shows the « ne » marker again
        if (r.Status == ReadStatus.Password)
        {
            bool had;
            lock (_sync) { had = _session != null; _session = null; }   // never re-read into / next to a password field
            if (had) Log?.Invoke("reread skip(password)");
            if (q.Id == _reader!.LatestId) SuppressPassword(q.Fg, null);
            Log?.Invoke($"read {q.Reason} src={r.Source} status=Password {r.Ms} ms (content not read)");
            return;
        }
        // A real (non-Stale) read of the latest request proved the focused field is not a password field.
        // A Stale result read nothing, so it must not lift suppression (e.g. Left then Esc inside a password box).
        if (q.Id == _reader!.LatestId && r.Status != ReadStatus.Stale) _suppress = false;
        // Ok / Selection: the read reached the text, so the field is not a password field (Unavailable or Timeout
        // prove nothing: the first character will ask the field probe)
        if (q.Id == _reader.LatestId && r.Status is ReadStatus.Ok or ReadStatus.Selection && Native.GetForegroundWindow() == q.Fg)
            lock (_sync) if (_readEpoch == _epoch) _field = FieldClear;
        bool applied = false;
        long seeded = -1;
        bool agrees = false;
        if (r.Status == ReadStatus.Ok)
            lock (_sync)
                if (KeyboardPolicy.ShouldApply(q.Version, _tracker.Version, q.Fg, Native.GetForegroundWindow(), r.Ms)
                    && !(agrees = q.Reason == ReasonBackspace && Agrees(r.Ctx)))
                {
                    var c = r.Ctx;
                    _tracker.Seed(c.Prefix, c.Context, c.Separators, c.Suffix, c.NextChar, r.Source, c.AtSentenceStart, c.Apostrophe);
                    // a caret at a sentence start (no word before it in the sentence) is not an edit of a sentence; after a
                    // Backspace the echo keeps what it restored itself (its sentence, liaison grouping), unless it lost all
                    if (q.Reason != ReasonBackspace || _echo.IsBlank) _echo.Seed(c.Prefix, c.AtSentenceStart && c.Context.Count == 0);
                    applied = true;
                    seeded = _tracker.Version;   // what a begin from this read must still find at its commit
                }
        bool late = false;   // D23: a read over the tracker budget may still serve the re-read session
        if (r.Status == ReadStatus.Timeout && r.Window != null)
            lock (_sync)
                if (KeyboardPolicy.UsableLateForReread(true, true, r.Ms, q.Version, _tracker.Version, q.Fg, Native.GetForegroundWindow()))
                {
                    late = true;
                    seeded = _tracker.Version;
                }
        var status = r.Status == ReadStatus.Ok && !applied && !agrees ? ReadStatus.Stale : r.Status;
        Log?.Invoke($"read {q.Reason} src={r.Source} status={status} {r.Ms} ms" + (agrees ? " agrees with the keys (kept)" : "") +
            (applied ? $" prefix='{r.Ctx.Prefix}' suffix='{r.Ctx.Suffix}' next={Show(r.Ctx.NextChar)} ctx=[{string.Join(" ", r.Ctx.Context)}] sep={r.Ctx.Separators}" +
                       (r.Ctx.AtSentenceStart ? " start" : "") : " -> keys" + (late ? " (used by the re-read)" : "")));
        // Esc only seeds (plan 10B final re-review): the bar Esc hid stays hidden until the next key
        if (applied && q.Reason != ReasonEsc) ScheduleUpdate();
        // not a caret move (Backspace, Esc): no D23 begin / landing, no selection decision
        if (q.Reason is ReasonBackspace or ReasonEsc) return;
        try { Reread(q, r, applied || late, seeded); }
        catch (Exception ex) { Log?.Invoke($"reread error: {ex.GetType().Name}: {ex.Message}"); lock (_sync) _session = null; }
        try { AfterCaretRead(q, r); }
        catch (Exception ex) { Log?.Invoke($"selection error: {ex.GetType().Name}: {ex.Message}"); }
    }

    private const string ReasonShift = "shift";

    /// <summary>A <c>bksp</c> read that finds what the tracker restored from the typed text (same prefix and context)
    /// leaves it: the typed text is exact, the read's rebuilt one is not (separators). Under _sync.</summary>
    private bool Agrees(TextContext c) =>
        c.Prefix == _tracker.Current && c.Suffix.Length == 0 && c.Context.SequenceEqual(_tracker.Context);

    /// <summary>
    /// Plan 07, reader thread: what the latest caret read means for the reading mode. A selection → read its text (after
    /// Shift + navigation, 250 ms after the last key: a held Shift+→ is not read at every step) — LibreOffice phase 2: in
    /// Writer too, through IA2, the copy only as its safety net (<see cref="SelectionPolicy.Route"/>). No text pattern but a
    /// selection gesture (drag, double click, Shift+nav, Ctrl+A) and the Ctrl+C fallback on (by default: in LibreOffice
    /// Writer, <see cref="SelectionPolicy.Fallback"/>; never in Calc) → the fallback. A collapsed
    /// caret, a password field, a failed read → the selection is gone: back to predictions. Stale reads decide nothing (a
    /// newer jump has its own read; typing has left already).
    /// </summary>
    private void AfterCaretRead(ReadRequest q, ReadResult r)
    {
        if (_reading is null || _selection is null || q.Id != _reader!.LatestId || r.Status == ReadStatus.Stale) return;
        bool active = _reading.Active;
        if (!Enabled || !_readingOn || _suppress)
        {
            if (active || _reading.HasDismissed) { var why = _suppress ? "password field" : "off"; _ui.BeginInvoke(() => _reading.SelectionGone(why)); }
            return;
        }
        bool gesture = Interlocked.Read(ref _selGesture) == q.Version;
        int cfs = _clipboardFallback;
        bool? cfSetting = cfs < 0 ? null : cfs == 1;
        // LibreOffice phase 2: a selection is read directly from any source (Writer through IA2); the Ctrl+C copy is only
        // the safety net when the direct read fails (setting « Dans LibreOffice Writer seulement » by default; never in Calc;
        // the window is looked up only when a copy could follow)
        var module = SelectionPolicy.NeedsModule(r.Status, r.Source, gesture, cfSetting) ? HostApps.ModuleOf(q.Fg) : LibreOfficeModule.None;
        if (module == LibreOfficeModule.Calc && r.Status == ReadStatus.Unavailable)
            Log?.Invoke("selection: clipboard fallback refused: Calc");   // a spreadsheet is never modified (Enter would paste)
        var route = SelectionPolicy.Route(r.Status, r.Source, gesture, q.Reason == ReasonShift, cfSetting, module, _suppress);
        if (route.Ask)
        {
            _selection.Request(q.Version, q.Fg, route.DelayMs, route.Clipboard, q.RequestedTick, q.Reason);
            return;
        }
        // the selection is gone: leave the reading mode, and forget a selection dismissed with Esc
        if (active || _reading.HasDismissed) { var why = $"no selection ({q.Reason} read {r.Status})"; _ui.BeginInvoke(() => _reading.SelectionGone(why)); }
    }

    /// <summary>Plan 07, selection-reader thread: the selected text (or none). Applied on the UI thread only if nothing was
    /// typed and no other jump happened since the caret read that asked for it (tracker version), in the same window.</summary>
    private void OnSelectionRead(ReadRequest q, ReadResult r, long trigger)
    {
        if (_reading is null || q.Id != _selection!.LatestId) return;
        Log?.Invoke($"selection read {q.Reason} src={r.Source} status={r.Status} {r.Ms} ms" + (r.Selected is { } t ? $" ({t.Length} chars)" : ""));
        if (r.Status == ReadStatus.Stale) return;
        if (r.Status == ReadStatus.Password) { SuppressPassword(q.Fg, "selection reader"); return; }
        if (r.Status != ReadStatus.Ok)
        {
            if (_reading.Active) _ui.BeginInvoke(() => _reading.SelectionGone($"selection read {r.Status}"));
            return;
        }
        var text = r.Selected ?? "";
        _ui.BeginInvoke(() =>
        {
            bool current;
            lock (_sync) current = q.Version == _tracker.Version;
            if (!current || !Enabled || !_readingOn || _suppress || Native.GetForegroundWindow() != q.Fg)
            { Log?.Invoke("selection read: overtaken, not shown"); return; }
            _suggCount = 0;
            CancelListen("reading");
            _reading.OnSelection(q.Fg, text, trigger, r.Source, r.Ms, r.TailAt, r.Cut, r.Partial);
        });
    }

    /// <summary>
    /// D23 (plan 06), reader thread. A read with no pending leave begins a session (or, after Delete, rebases it on the
    /// app's real text). A read that ends a leave classifies the landing and decides the re-read: the session is copied
    /// under the lock, analysed and tokenised outside it, then committed only if nothing replaced it meanwhile. Only the
    /// latest read decides; keys typed between the leave and its read make the read stale → nothing said. Spoken only
    /// while enabled, not paused, not in a password field, setting on. The log names the decision, never the text (the
    /// <c>speak Reread</c> line of the speech thread has it, with <c>--log</c> only).
    /// </summary>
    private void Reread(ReadRequest q, ReadResult r, bool applied, long seededVersion)
    {
        if (!_reread) return;
        // a leave right after the last key: give the edit probe of that key up to 200 ms (debounce slot + probe; reader
        // thread; the re-read is never spoken for edits it has not covered: « field not proven », silent by design)
        for (int i = 0; i < 40; i++)
        {
            bool waiting;
            lock (_sync) waiting = _session is { Left: true, ProbeInFlight: true };
            if (!waiting) break;
            Thread.Sleep(5);
        }
        EditSession? old;
        EditState? st;
        lock (_sync) { old = _session; st = old?.Snapshot(); }
        bool settle = q.Reason == ReasonSettle;
        // a settle read only rebases a session that is still there, not left, same window, not dirty (never a begin or a
        // landing; its queue's ids are not the caret reader's either)
        if (settle && (st is null || st.Left || st.Fg != q.Fg || st.Dirty)) return;
        var facts = new ReadFacts(applied && (r.Status is ReadStatus.Ok or ReadStatus.Timeout) && r.Window != null, r.Status == ReadStatus.Password,
                                  !settle && q.Id == _reader!.LatestId, q.Reason is ReasonDelete or ReasonSettle, q.Fg, r.Window, seededVersion);
        var plan = RereadFlow.Plan(st, facts);                    // tokenising and classifying: off the lock
        if (!plan.Replace) return;
        bool speak;
        string? why;
        lock (_sync)
        {
            (_session, speak, why) = RereadFlow.Commit(_session, old, st, plan, _enabled && _reread, _tracker.Version);
            speak = speak && !_suppress;
        }
        if (st is null || !st.Left) return;                       // a begin / rebase: nothing to say, nothing to log
        var d = plan.Decision;
        string? skip = d.Kind == RereadKind.None ? d.Reason : why ?? (_suppress ? "password" : speak ? null : "not spoken");
        // the sentence echo already said this sentence, read from the document after the edit (D17 amendment): unchanged
        // since -> not twice
        if (skip is null && d.Kind == RereadKind.Sentence)
            lock (_sync)
                if (_gate.AlreadySaid(d.Text, st.Fg, Environment.TickCount64)) skip = "sentence already spoken";
        Log?.Invoke(skip != null
            ? $"reread skip({skip}) landing={plan.Where} {q.Reason} {r.Ms} ms"
            : $"reread {(d.Kind == RereadKind.Word ? "word" : "sentence")} landing={plan.Where} {q.Reason} {r.Ms} ms ({d.Text.Length} chars)");
        if (skip is null && speak) _speaker?.Reread(d.Text);
    }

    /// <summary>A password field is focused in <paramref name="fg"/> (reader, field probe or echo guard): stop
    /// tracking, forget what was typed, stop speech, hide the bar. Any thread.</summary>
    private void SuppressPassword(IntPtr fg, string? who)
    {
        if (Native.GetForegroundWindow() != fg) return;
        lock (_sync)
        {
            _suppress = true;
            _tracker.Reset();
            _echo.Reset();
            _session = null;
            _gate.Drop();   // a pending sentence and the echoes behind it are never said
            Rearm();
        }
        _speaker?.CancelAll();
        ExitReadingSoon("password field");
        HideSoon();
        if (who != null) Log?.Invoke($"{who}: password field, tracking suspended");
    }

    /// <summary>The focused field is unknown again: the next character asks the field probe. Under _sync.</summary>
    private void Rearm()
    {
        _epoch++;
        _field = FieldUnknown;
        _probe = ProbeNotAsked;
    }

    /// <summary>Answer of the field probe (field-probe thread). Checked and applied under _sync against the epoch
    /// the request was made in: an answer that crosses a jump / Enter can never mark the new field as clear.</summary>
    /// <summary>After an edit of the session (under _sync): ask the edit probe whether the focused field is a password
    /// field, unless one is in flight (its answer asks again if edits were made meanwhile). Hook / UI thread: a short
    /// lock and an event set, never a wait.</summary>
    private void ProbeEdits(IntPtr fg)
    {
        if (_session is not { NeedsProbe: true } s || _editProbe is null) return;
        // debounce: at most one probe per EditProbeGapMs while typing. The request is made now (so the latest keys are
        // always covered, even without a leave) but runs no earlier than EditProbeGapMs after the previous one.
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref _editProbeDue, Math.Max(now, _editProbeLast + EditProbeGapMs));
        _editProbeLast = Interlocked.Read(ref _editProbeDue);
        long id = _editProbe.Request(fg);
        if (id != 0) s.StartProbe(id);   // refused (worker stuck): the edits stay unproven, nothing will be said
    }

    /// <summary>Minimum gap between two edit probes (UIA providers: one cross-process call per probe).</summary>
    private const int EditProbeGapMs = 150;
    private long _editProbeDue, _editProbeLast;   // (_sync for writes) when the next edit probe may run / the last one ran
    private long _editProbeWaited;                // edit-probe thread: debounce wait of the probe being answered

    /// <summary>The edit probe's check (its own thread): waits for its debounce slot, then probes the field.</summary>
    private ProbeStatus EditProbeCheck(IntPtr fg)
    {
        long wait = Interlocked.Read(ref _editProbeDue) - Environment.TickCount64;
        if (wait > 0) Thread.Sleep((int)Math.Min(wait, EditProbeGapMs));
        Interlocked.Exchange(ref _editProbeWaited, Math.Max(0, wait));
        return PasswordProbe.Check(fg);
    }

    /// <summary>Answer of the edit probe (its own thread). NotPassword covers the edits made up to the request;
    /// Password or Unavailable drops the session: what was typed may be in a password field (never spoken).</summary>
    private void OnEditProbe(long id, IntPtr fg, ProbeStatus st, long ms)
    {
        bool dropped = false;
        long waited = Interlocked.Read(ref _editProbeWaited);
        lock (_sync)
        {
            if (_session is { } s)
            {
                bool more = s.ProbeAnswered(id, st);
                // any Password / Unavailable answer about the session's window drops it, whatever request it answers
                if (st != ProbeStatus.NotPassword && (fg == s.Fg || s.Dirty)) { _session = null; dropped = true; }
                else if (more && Native.GetForegroundWindow() == s.Fg) ProbeEdits(s.Fg);
            }
        }
        // one line per answer (--log only): the owner's Word run measures the probe load from it (not the start-up
        // warm-up request, which has no window)
        if (fg != IntPtr.Zero) Log?.Invoke($"reread: edit probe {st} {Math.Max(0, ms - waited)} ms (debounce {waited} ms){(dropped ? ", session dropped" : "")}");
    }

    private void OnFieldProbe(long id, IntPtr fg, ProbeStatus st, long ms)
    {
        bool fgSame = Native.GetForegroundWindow() == fg;
        bool lineEnd;
        lock (_sync)
        {
            if (id != _fieldProbeId || _fieldProbeEpoch != _epoch || _probe != ProbePending) return;   // stale answer
            if (!fgSame) { _probe = ProbeNotAsked; return; }
            if (st != ProbeStatus.Password) { _field = FieldClear; _probe = ProbeDone; }
            // D23: the field typed into is not known to be a plain text field (Unavailable fails open for the bar,
            // never for speech): the keys may be in a password field, so the session never speaks them
            if (st != ProbeStatus.NotPassword) _session = null;
            lineEnd = id == _lineEndProbeId;
            _lineEndProbeId = -1;
        }
        Log?.Invoke($"field probe: {st} {ms} ms");
        if (st == ProbeStatus.Password) { SuppressPassword(fg, "field probe"); return; }
        // plan 10B: a line-end marker is shown only on a field proven non-password (Unavailable proves nothing)
        if (lineEnd && st != ProbeStatus.NotPassword)
        {
            bool typed;
            lock (_sync) typed = _tracker.Current.Length > 0 || _tracker.AfterSeparator;
            Log?.Invoke("spelling: line end not shown (field not proven)");
            if (!typed) return;   // a key typed meanwhile still gets its bar (Unavailable fails open for the words, as before)
        }
        ScheduleUpdate();
    }

    /// <summary>Printable form of NextChar for the log.</summary>
    private static string Show(char c) => c switch
    {
        '\0' => "EOT",
        ' ' => "␠",
        '\r' => "\\r",
        '\n' => "\\n",
        '\t' => "\\t",
        // a lone surrogate (caret inside an emoji's text) would make the strict UTF-8 log writer throw
        _ when char.IsControl(c) || char.IsSurrogate(c) => $"\\u{(int)c:X4}",
        _ => c.ToString(),
    };

    // ---------------------------------------------------------------- prediction + UI (UI thread)
    private void ScheduleUpdate()
    {
        if (Interlocked.Exchange(ref _updatePending, 1) == 1) return;
        _ui.BeginInvoke(DispatcherPriority.Background, new Action(Update));
    }

    /// <summary>Hide the suggestion bar (posted). The reading view is not hidden here: the next read decides (plan 07).</summary>
    private void HideSoon() => _ui.BeginInvoke(() =>
    {
        _sugg = Array.Empty<Suggestion>(); _suggCount = 0; _markAction = false; _correction = null; _group = null; _negation = null;
        _compound = null;
        CancelListen("hidden");   // a jump (click, focus, window switch, navigation) or a password field
        if (_reading is { Active: true }) return;
        _win.HideBar();
    });

    private void Update()
    {
        Interlocked.Exchange(ref _updatePending, 0);
        _markAction = false;
        _correction = null;   // 0.8.3: the bar is rebuilt — a ≈ list shown before it no longer corrects
        _group = null; _negation = null;   // 0.11.0: the same for the « ne » group tile
        _compound = null;                  // 0.11.0: and for the compound tiles after a hyphen
        if (_reading is { Active: true }) { _suggCount = 0; CancelListen("reading"); return; }   // plan 07: predictions stay hidden while reading
        if (!Enabled || _suppress || _privateHost) { CancelListen("hidden"); _win.HideBar(); _suggCount = 0; return; }
        if (_field != FieldClear)
        {
            // nothing has seen the focused field yet: it may be a password field, so nothing is shown nor logged
            bool any;
            lock (_sync) any = _tracker.Current.Length > 0 || _tracker.AfterSeparator;
            if (any) { CancelListen("hidden"); _win.HideBar(); _suggCount = 0; Log?.Invoke("typed=(pending field probe)"); }
            return;
        }
        bool loggable = _probe != ProbePending;   // typed since the jump and not answered yet: no text in the log
        string cur, origin, suffix; List<string> ctx; bool after, start, hyphen, neutral, headNeutral; char apostrophe;
        string? head; long version;
        FinishedWord? typing, finished;
        lock (_sync)
        {
            cur = _tracker.Current; ctx = _tracker.Context.ToList(); after = _tracker.AfterSeparator;
            origin = _tracker.Origin; suffix = _tracker.Suffix; start = _tracker.AtSentenceStart;
            hyphen = _tracker.LastSeparator == '-';
            // 0.11.0 (plan hyphen-names): the word before a typed hyphen, for its compounds (« saint-n » → Saint-Nicolas)
            head = hyphen ? _tracker.HyphenHead : null;
            headNeutral = head is not null && _tracker.HyphenHeadNeutral;
            version = _tracker.Version;
            apostrophe = _tracker.ApostropheStyle;   // 0.10.2: j' or j’, as the document writes it
            neutral = _tracker.CapitalNeutral;   // 0.8.3: « : Mer », « « Je », an unknown context: no name bonus for the capital
            typing = _tracker.Typing;
            // a word finished by a line break is shown only by the line-end check (never by a later update: a chat that sent
            // the line on Enter, then a space)
            finished = KeyboardPolicy.FinishedForMark(_tracker.Finished, _tracker.Version, _lineEndAccepted);
        }

        IReadOnlyList<Suggestion> s = Array.Empty<Suggestion>();
        var t0 = Environment.TickCount64;
        try
        {
            // one spare: the display step may drop a pseudo-word
            // 0.10.2: the exact typed word rule follows the ✓ while typing exactly — the tracker's own sentence start, not
            // « start || neutral » — and also when the marker setting is off
            bool tick = typing is { } jw && SpellingIndicator.Typing(jw, w => _engine.KnowsForMark(w, jw.SentenceStart)) == SpellingMark.Known;
            // 0.11.0: after « head- » the compounds of the head first, then the same list as below
            if (head is not null && (cur.Length > 0 || after && ctx.Count > 0))
                s = _engine.PredictAfterHyphen(head, cur, ctx, cur.Length > 0 ? _wordCount + 3 : _wordCount + 1, start || neutral, headNeutral, tick);
            else if (cur.Length > 0) s = _engine.Predict(cur, ctx, _wordCount + 3, start || neutral, tick);   // spares: the display step may drop a pseudo-word or merge « Pierre » + « pierre »
            else if (after && ctx.Count > 0) s = _engine.PredictNext(ctx, _wordCount + 1);
        }
        catch (Exception ex) { Log?.Invoke("predict error: " + ex.Message); }

        // D22: elisions with their apostrophe (l'), capital at the start of a sentence
        // plan 10A: a capitalised personal word and the cased lexicon word with the same text make one tile
        var shown = KeyboardPolicy.Shown(s, hyphen, apostrophe, cur, start, _wordCount, head);
        if (head is not null)
        {
            // owner Q2 (2026-11-27: the whole tile): where the head cannot be replaced, what a « Saint-Nicolas » tile shows (one switch)
            var fg = Native.GetForegroundWindow();
            bool may = _readable.MayReplace(fg, PidOf(fg), HostApps.UiaTextUnavailable(fg));
            shown = KeyboardPolicy.CompoundTiles(shown, head, may);
            _compound = new SoundAlikeCorrection.CompoundTarget(head, head + "-" + cur, version, fg, shown);
        }
        _sugg = shown;
        _suggCount = _sugg.Count;

        // plan 10B: the spelling marker — ✓ while typing a known word; on the word just finished ✓ ≈ ? (Q8)
        var (mark, markWord, check, checkMs) = SpellingMarkOf(typing, finished);
        // 0.11.0 (plan negation): a negation typed without « ne » — the « ne » marker wins over ✓ and ≈ on that word
        string negLog = "";
        // (not right after « head- »: the bar is about the compound, the head is no finished word yet)
        if (typing is null && finished is { } nf && head is null && NegationNow() is { } ng)
        {
            _negation = ng;
            mark = SpellingMark.Grammar; markWord ??= nf.Word;
            negLog = $" negation={ng.Fix.Rule}";
        }
        _markWord = markWord; _markCheck = check;
        string marked = mark == SpellingMark.None ? "" : FormattableString.Invariant($" mark={mark}{negLog} ({checkMs:0.00} ms)");
        if (loggable) Log?.Invoke($"typed='{cur}' ctx=[{string.Join(" ", ctx)}]{(start ? " start" : "")} src={origin} suffix='{suffix}' -> {string.Join(", ", _sugg.Select(x => x.Word))}  ({Environment.TickCount64 - t0} ms){marked}");
        else Log?.Invoke($"typed=(pending field probe) -> {_sugg.Count} words{marked}");
        // Q10: with no word to propose the bar shows the marker alone for ≈ and ?, never for ✓ alone. While typing, a ✓ word
        // is itself a tile with the exact typed word rule (0.10.2), so this hides the ✓ alone of a finished word (owner Q5)
        if (_sugg.Count == 0 && !MarkActs(mark)) { CancelListen("hidden"); _win.HideBar(); return; }
        ShowBar(mark);
        KeepListen();   // same word still at the ringed index: ringed again, else cancelled
    }

    /// <summary>A marker with an action (click / F9): ≈, ?, and 0.11.0 the « ne » marker.</summary>
    private static bool MarkActs(SpellingMark mark) => mark is SpellingMark.SoundsLike or SpellingMark.Unknown or SpellingMark.Grammar;

    /// <summary>
    /// 0.11.0 (plan negation): the « ne » fix for the clause the finished word ends (<see cref="PredictionEngine.MissingNe"/>
    /// on <see cref="WordTracker.TryClause"/>), with the tail typed after it and the tracker version; null when the spelling
    /// indicator is off (owner Q5: on together with it), nothing is finished, no rule fires, or (owner 2026-11-27) the
    /// window's text cannot be read (<see cref="ReadableWindows"/>: choosing the group could do nothing there). UI thread.
    /// </summary>
    private (NegationFix Fix, string Tail, long Version)? NegationNow()
    {
        if (!_spellingOn) return null;
        // owner 2026-11-27: hidden where choosing the group could do nothing (the window's text cannot be read)
        var fg = Native.GetForegroundWindow();
        if (!_readable.MayReplace(fg, PidOf(fg), HostApps.UiaTextUnavailable(fg))) return null;
        List<NegToken> clause; bool startKnown, plus; string tail; long v;
        lock (_sync)
        {
            if (!_tracker.TryClause(out clause, out startKnown, out tail, out plus)) return null;
            v = _tracker.Version;
        }
        try { return _engine.MissingNe(clause, startKnown, plus) is { } fix ? (fix, tail, v) : null; }
        catch (Exception ex) { Log?.Invoke("negation error: " + ex.GetType().Name); return null; }
    }

    /// <summary>Plan 10B: the marker for the word being typed or the word just finished (UI thread, a few µs: the
    /// engine's <see cref="PredictionEngine.Check"/> is a few lookups). Off by the setting → none.</summary>
    private (SpellingMark Mark, string? Word, SpellingCheck Check, double Ms) SpellingMarkOf(FinishedWord? typing, FinishedWord? finished)
    {
        if (!_spellingOn || typing is null && finished is null) return (SpellingMark.None, null, SpellingCheck.Unknown, 0);
        long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var check = SpellingCheck.Unknown;
        SpellingMark mark;
        string word;
        try
        {
            if (typing is { } tw) { word = tw.Word; mark = SpellingIndicator.Typing(tw, w => _engine.KnowsForMark(w, tw.SentenceStart)); }
            else { var fw = finished!.Value; word = fw.Word; mark = SpellingIndicator.Finished(fw, w => check = _engine.Check(w)); }
        }
        catch (Exception ex) { Log?.Invoke("spelling error: " + ex.GetType().Name); return (SpellingMark.None, null, SpellingCheck.Unknown, 0); }
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - c0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return (mark, mark == SpellingMark.None ? null : word, check, ms);
    }

    /// <summary>Show <see cref="_sugg"/> with the marker and place the bar (UI thread).</summary>
    private void ShowBar(SpellingMark mark)
    {
        var t1 = Environment.TickCount64;
        _win.ShowSuggestions(_sugg, mark, _markWord, groupFirst: _group is { } g && ReferenceEquals(g.List, _sugg));
        var t2 = Environment.TickCount64;
        string src = AppSettings.PositionNames[_win.Position];
        PointI? caret = null;
        long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_win.Position == BarPosition.Caret) { var (x, y, s0) = Caret.Get(); caret = new PointI(x, y); src = s0; }   // docked / free: no caret lookup
        var t3 = Environment.TickCount64;
        // LibreOffice phase 1: every IA2 lookup is timed (the gate: p95 ≤ 5 ms, the UI never waits > 20 ms)
        if (src.StartsWith(LibreOfficeText.Src, StringComparison.Ordinal))
            Log?.Invoke(FormattableString.Invariant($"  caret({src}) {(System.Diagnostics.Stopwatch.GetTimestamp() - c0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00} ms"));
        _win.Place(caret);
        if (src == "ia2-recent") ArmCaretRefresh();
        _suggCount = Math.Min(_sugg.Count, _win.VisibleCount);   // a bar trimmed to fit the screen shows fewer words
        SyncExactBar();
        _markAction = MarkActs(mark);
        if (Environment.TickCount64 - t1 > 100)
            Log?.Invoke($"  slow UI: show {t2 - t1} ms, caret({src}) {t3 - t2} ms, place {Environment.TickCount64 - t3} ms");
    }

    /// <summary>LibreOffice phase 1: one re-placement of the bar <see cref="LibreOfficeText.CaretThrottleMs"/> + 20 ms after
    /// the last update that reused a caret point (fast typing keeps pushing it back, so it costs no lookup then). UI thread.</summary>
    private void ArmCaretRefresh()
    {
        _caretRefresh ??= new DispatcherTimer(DispatcherPriority.Background, _ui) { Interval = TimeSpan.FromMilliseconds(LibreOfficeText.CaretThrottleMs + 20) };
        _caretRefresh.Stop();
        _caretRefresh.Tick -= OnCaretRefresh;
        _caretRefresh.Tick += OnCaretRefresh;
        _caretRefresh.Start();
    }

    private void OnCaretRefresh(object? sender, EventArgs e)
    {
        _caretRefresh!.Stop();
        if (!_win.Shown || _win.Position != BarPosition.Caret || _reading is { Active: true }) return;
        var (x, y, src) = Caret.Get();
        if (!src.StartsWith(LibreOfficeText.Src, StringComparison.Ordinal)) return;   // the window changed: the next update places it
        _win.Place(new PointI(x, y));
        Log?.Invoke($"  caret({src}) refresh");
    }

    /// <summary>The focused control of <paramref name="fg"/>'s thread and its system caret (window, top in client coordinates;
    /// zero when none): GetGUIThreadInfo, cheap enough for the hook. Any thread.</summary>
    private static (IntPtr Focus, IntPtr Caret, int Top) FocusOf(IntPtr fg)
    {
        uint tid = Native.GetWindowThreadProcessId(fg, out _);
        var gi = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        return tid != 0 && Native.GetGUIThreadInfo(tid, ref gi) ? (gi.hwndFocus, gi.hwndCaret, gi.rcCaret.Top) : (IntPtr.Zero, IntPtr.Zero, 0);
    }

    /// <summary>Plan 10B: Enter finished a word — check it <see cref="LineEndDelayMs"/> later (UI thread).</summary>
    private void ArmLineEnd(FinishedWord w, long version, IntPtr fg, IntPtr focus, IntPtr caret, int top, long gen)
    {
        _lineEndTimer ??= new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(LineEndDelayMs) };
        _lineEndTimer.Stop();
        _lineEndTimer.Tag = (w, version, fg, focus, caret, top, gen);
        _lineEndTimer.Tick -= OnLineEndTick;
        _lineEndTimer.Tick += OnLineEndTick;
        _lineEndTimer.Start();
    }

    private void OnLineEndTick(object? sender, EventArgs e)
    {
        _lineEndTimer!.Stop();
        if (_lineEndTimer.Tag is not ValueTuple<FinishedWord, long, IntPtr, IntPtr, IntPtr, int, long> t) return;
        _lineEndTimer.Tag = null;
        var (w, version, fg, focus, caret, top, gen) = t;
        LineEndMark(w, version, fg, focus, caret, top, gen);
    }

    /// <summary>
    /// Plan 10B, owner decision 2026-11-14 (option B), re-review: Enter finished <paramref name="w"/>; <see cref="LineEndDelayMs"/>
    /// later (the app has handled the key: a Save As box closed, a chat sent its line), if nothing moved — no key, jump, focus or
    /// foreground event since (generation), the same tracker version, the same window in front and <b>the same focused
    /// control</b> — and <b>the system caret went down to a new line</b> in that control
    /// (<see cref="KeyboardPolicy.NewLineAfterEnter"/>: a single-line field, still open while it saves, never does), and the
    /// word is ≈ or ?, the field probe is asked again (Enter re-armed it: a two-step login may have put a
    /// password field there); only a NotPassword answer schedules the normal <see cref="Update"/>, which shows that marker
    /// alone at the caret read then, on the new line (never ✓ alone). Click / F9 act on that word; the next letter or any
    /// jump clears it, as after a space. Logs the decision, never the word.
    /// </summary>
    private void LineEndMark(FinishedWord w, long version, IntPtr fg, IntPtr focus, IntPtr caret, int top, long gen)
    {
        long v;
        lock (_sync) v = _tracker.Version;
        var now = FocusOf(fg);
        string? why = Interlocked.Read(ref _lineEndGen) != gen ? "key or jump" : v != version ? "typed" : Native.GetForegroundWindow() != fg ? "window"
                    : now.Focus != focus ? "focus moved" : !Enabled || _suppress || !_spellingOn || _reading is { Active: true } ? "off"
                    : !KeyboardPolicy.NewLineAfterEnter(caret, top, now.Caret, now.Top) ? (caret == IntPtr.Zero ? "no caret" : "caret not on a new line")
                    : null;
        if (why != null) { Log?.Invoke($"spelling: line end cancelled ({why})"); return; }
        var (mark, _, _, ms) = SpellingMarkOf(null, w);
        if (!MarkActs(mark) && NegationNow() is null) return;   // 0.11.0: « j'aime plus⏎ » gets its « ne » marker too
        if (!MarkActs(mark)) mark = SpellingMark.Grammar;
        long id = 0;
        lock (_sync)
            if (_probe == ProbeNotAsked && v == _tracker.Version)
            {
                id = _fieldProbe?.Request(fg) ?? 0;
                _fieldProbeId = id; _fieldProbeEpoch = _epoch; _lineEndProbeId = id; _lineEndAccepted = v;
                _probe = id == 0 ? ProbeNotAsked : ProbePending;
            }
        Log?.Invoke(FormattableString.Invariant($"spelling: line end mark={mark} ({ms:0.00} ms), {(id != 0 ? "field probe asked" : "no probe")}"));
    }

    /// <summary>
    /// Plan 10B: the marker was clicked, or F9 (UI thread). ≈ → the bar shows the words that sound like the finished word
    /// (0.8.3: choosing one replaces the finished word when nothing moved, see <see cref="Correct"/>; else it goes at the
    /// caret); ? → the app offers to add the word (« Ajouter « … » à mes mots ? », PIN per Q3). Ignored when a letter
    /// was typed meanwhile (the marker is gone with the next Update).
    /// </summary>
    private void OnMarker(string how)
    {
        var mark = _win.Mark;
        if (!_win.Shown || _markWord is not { } word || !MarkActs(mark)) return;
        CancelListen("marker");
        string cur; bool start; List<string> ctx; char apostrophe;
        lock (_sync) { cur = _tracker.Current; start = _tracker.AtSentenceStart; ctx = _tracker.Context.ToList(); apostrophe = _tracker.ApostropheStyle; }
        if (cur.Length > 0) return;
        if (mark == SpellingMark.Unknown)
        {
            Log?.Invoke($"spelling: add offer ({how})");
            AddWordRequested?.Invoke(word);
            return;
        }
        if (mark == SpellingMark.Grammar) { OnNegationMarker(word, how, ctx, start, apostrophe); return; }
        // 0.8.3: a word chosen from this list replaces the finished word, if nothing moves before the choice; the list then
        // takes the case of that word's place (after Enter the caret opens a line, but « la maizon⏎ » is not a sentence start)
        char sep; bool can; long v; bool wordStart;
        lock (_sync)
        {
            can = _tracker.CanCorrect(word, out sep); v = _tracker.Version;
            wordStart = _tracker.Finished?.SentenceStart ?? start;
        }
        // 0.10.2 (glued words plan, owner Q2): the list ordered by the words before the finished word, when it opens; then
        // every apostrophe in the document's style (« j’ai »), then the case of the word's place
        _sugg = SoundAlikeList(word, ctx, apostrophe, can ? wordStart : start, _wordCount, out double rankMs);
        _suggCount = _sugg.Count;
        _correction = can ? new SoundAlikeCorrection.Target(word, sep, v, Native.GetForegroundWindow(), _sugg) : null;
        Log?.Invoke(FormattableString.Invariant($"spelling: sound-alike words shown ({_sugg.Count}, {how}, ranked in {rankMs:0.0} ms){(can ? ", may replace" : ", insert only")}"));
        if (_sugg.Count > 0) ShowBar(mark);
    }

    /// <summary>The ≈ list of the finished word: ordered by the words before it (0.10.2), every apostrophe in the document's
    /// style, the case of the word's place (UI thread).</summary>
    private IReadOnlyList<Suggestion> SoundAlikeList(string word, List<string> ctx, char apostrophe, bool sentenceStart, int max, out double rankMs)
    {
        IReadOnlyList<string> ordered = _markCheck.Candidates;
        long r0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try { ordered = _engine.RankCorrections(_markCheck.Candidates, KeyboardPolicy.WordsBefore(ctx, word), word, _markCheck.Tolerant); }
        catch (Exception ex) { Log?.Invoke("spelling: ranking error " + ex.GetType().Name); }
        rankMs = (System.Diagnostics.Stopwatch.GetTimestamp() - r0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var cands = ordered.Select(c => new Suggestion(Elision.Styled(c, apostrophe), 0, MatchKind.Phonetic));
        return KeyboardPolicy.CasedDistinct(cands, "", sentenceStart, max);
    }

    /// <summary>
    /// 0.11.0 (plan negation, owner Q3): the « ne » marker was clicked, or F9 — the bar shows the corrected group first (a
    /// green correction tile, no picture: « ne veut pas », « Ce n'est pas »), then, when the negation word itself was ≈
    /// (« pa »), its sound-alike words. Choosing the group replaces the typed words (<see cref="CorrectGroup"/>); a ≈ word
    /// replaces that word only, as from the ≈ marker. Listening before inserting applies to the group tile as to any tile
    /// (owner 2026-11-25: F-keys / click « 1er appui : écouter »), and the group heard is not said again when inserted.
    /// </summary>
    private void OnNegationMarker(string word, string how, List<string> ctx, bool start, char apostrophe)
    {
        if (_negation is not { } ng) return;
        char sep; bool can; long v; bool wordStart, groupStart;
        lock (_sync)
        {
            v = _tracker.Version;
            can = _tracker.CanCorrect(word, out sep);
            wordStart = _tracker.Finished?.SentenceStart ?? start;
            groupStart = _tracker.SpanAtSentenceStart(ng.Fix.Original, ng.Tail);
        }
        if (v != ng.Version) { Log?.Invoke("negation: the text moved, nothing shown"); return; }
        // the group's capital at a sentence start (« c'est pas » → « Ce n'est pas »)
        var groupText = SoundAlikeCorrection.CasedGroup(ng.Fix.Replacement, groupStart);
        var list = new List<Suggestion> { new(Elision.Styled(groupText, apostrophe), 0, MatchKind.Phonetic) };
        double rankMs = 0;
        if (_markCheck.Status == SpellingStatus.SoundsLike && _wordCount > 1)
            foreach (var x in SoundAlikeList(word, ctx, apostrophe, can ? wordStart : start, _wordCount - 1, out rankMs))
                if (!string.Equals(x.Word, list[0].Word, StringComparison.Ordinal)) list.Add(x);
        _sugg = list;
        _suggCount = _sugg.Count;
        var fg = Native.GetForegroundWindow();
        _group = new SoundAlikeCorrection.GroupTarget(ng.Fix.Original, ng.Tail, groupText, v, fg, _sugg, ng.Fix.Rule.ToString());
        _correction = can && _sugg.Count > 1 ? new SoundAlikeCorrection.Target(word, sep, v, fg, _sugg) : null;
        Log?.Invoke(FormattableString.Invariant($"negation: group shown ({ng.Fix.Rule}, {how}{(_sugg.Count > 1 ? $", + {_sugg.Count - 1} sound-alike words, ranked in {rankMs:0.0} ms" : "")})"));
        ShowBar(SpellingMark.Grammar);
    }

    private TileClick? _lastTileClick;     // UI thread: the last tile click (message time, tile, screen point)
    /// <summary>0.8.3 (VM recheck): the second click of a double click on a tile reached the bar the first click had just
    /// rebuilt (after a correction or an insertion) and inserted its first word. That second click is ignored — the same
    /// tile, or within the double-click rectangle of the first, within the double-click time, measured on the input
    /// messages' own time and point (<c>GetMessageTime</c> / <c>GetMessagePos</c>, not when the busy UI thread handles
    /// them). A deliberate quick pick of another tile still works (re-review). F-keys are not concerned.
    /// <para>2026-11-25 (« Clic sur un mot », <see cref="ListenFirst.ClickAction"/>): in <see cref="ClickMode.ListenDouble"/>
    /// (the default) the first click only speaks, so that second click is the insertion — the word just heard, not said
    /// again; in the other modes it is still ignored.</para></summary>
    private void PickedByMouse(int i)
    {
        uint pos = Native.GetMessagePos();
        var click = new TileClick(Native.GetMessageTime(), i, (short)(pos & 0xFFFF), (short)(pos >> 16));
        // read now, like the rectangle: a change of the Windows setting (the help points to it) applies at once
        _dblClickMs = Native.GetDoubleClickTime();
        bool second = _lastTileClick is { } prev && KeyboardPolicy.IsSecondClickOfDouble(prev, click, _dblClickMs,
                          Native.GetSystemMetrics(Native.SM_CXDOUBLECLK), Native.GetSystemMetrics(Native.SM_CYDOUBLECLK));
        _lastTileClick = click;
        // mode 3: the second click inserts only the word just heard on that tile (never a blind insert)
        bool heardHere = second && i >= 0 && i < _sugg.Count &&
                         _listen.WasHeard(i, _sugg[i].Word, TrackerVersion(), Native.GetForegroundWindow());
        switch (ListenFirst.ClickAction(_clickMode, _listenButton, second, heardHere))
        {
            case ClickAct.Ignore: Log?.Invoke("  tile click ignored (second click of a double click)"); return;
            case ClickAct.Press: OnTilePress(i, "click"); return;
            case ClickAct.Listen:   // ListenDouble: speaks, no ring
                if (second) Log?.Invoke("  second click on a word not heard there: listens");
                ListenTo(i, "click", arm: false);
                return;
            default:
                // a double click ends here: a third quick click is a first click again (it listens to the new bar's word)
                if (second) _lastTileClick = null;
                InsertTile(i, second ? "double click" : "click");
                return;
        }
    }

    /// <summary>Owner 2026-11-25: a UI Automation Invoke on tile <paramref name="i"/>'s word (Narrateur, Accès vocal, switch
    /// tools): follows « Touches F1–F8 » (<see cref="ListenFirst.InvokeAction"/>) — the 1st Invoke speaks and rings the tile
    /// (item status « Écouté — encore une fois pour insérer »), a 2nd one on it inserts — even with the F-keys off. Never the
    /// mouse path: the last tile click is forgotten so that a real click after an Invoke is never taken for the second half
    /// of a double click. Log: <c>listen uia …</c> / <c>listen confirm uia</c>; <c>tile invoked (uia)</c> first.</summary>
    private void InvokedByAutomation(int i)
    {
        _lastTileClick = null;
        // a bar hidden without clearing _sugg (password field, private host, suppression): never speak nor type its old word
        if (!_win.Shown || i < 0 || i >= _suggCount) { Log?.Invoke($"  tile {i + 1} invoked: ignored, bar hidden"); return; }
        // BUG-1: the call that brought this Invoke may have made the bar the foreground; the bar gives it back on its
        // WM_ACTIVATE (before this runs) — again here if it is still ours, so that the heard word and the insertion
        // are tied to the text window, never to the bar; if the bar is still in front (the text window gone or refusing),
        // nothing is spoken nor typed: SendInput would type into the bar and the word would be lost
        if (!_win.GiveBackForeground() && Native.GetForegroundWindow() == _win.BarHwnd)
        {
            Log?.Invoke($"  tile {i + 1} invoked: ignored, bar in front");
            return;
        }
        Log?.Invoke($"  tile {i + 1} invoked (uia{(_keysListenFirst ? ", listen first" : ", insert")})");
        if (ListenFirst.InvokeAction(_keysListenFirst) == ClickAct.Press)
        {
            OnTilePress(i, "uia");
            _ringByInvoke = _listen.Armed;
        }
        else InsertTile(i, "uia");
    }

    /// <summary>The ring was set by an Invoke (UI thread): a settings change that leaves no other confirming press (F-keys
    /// off) keeps it while « Touches F1–F8 » stays « 1er appui » — the next Invoke confirms it.</summary>
    private bool _ringByInvoke;

    /// <summary>The bar's listen request on tile <paramref name="i"/>: a right-click (touch press-and-hold) — which inserts
    /// in <see cref="ClickMode.ListenDouble"/> (band off) — or option D's speaker band.</summary>
    private void OnListenRequested(int i, string how)
    {
        if (how == "right-click" && ListenFirst.RightClickInserts(_clickMode, _listenButton)) { InsertTile(i, how); return; }
        ListenTo(i, how, arm: Rings);
    }

    /// <summary>Does a listen gesture arm a ring now (a confirming press exists)? UI thread.</summary>
    private bool Rings => ListenFirst.Rings(_clickMode, _listenButton, _keysOn && _keysListenFirst);

    /// <summary>An insertion at once (a click, a double click, a right-click, F<i>n</i> in « insérer tout de suite ») that
    /// may confirm the word just heard on that tile (ringed, or the last listen gesture: <see cref="ListenFirst.WasHeard"/>):
    /// then it is not said again, and a different group (« les amis », « l'école ») is queued after the word still being
    /// spoken, never cutting it.</summary>
    private void InsertTile(int i, string how)
    {
        string? heard = null;
        if (i >= 0 && i < _sugg.Count && _listen.WasHeard(i, _sugg[i].Word, TrackerVersion(), Native.GetForegroundWindow()))
        {
            heard = _sugg[i].Word;
            Log?.Invoke($"listen confirm {how}");
        }
        Insert(i, heard);
    }

    /// <param name="heard">the tile's word when this insertion confirms a word just heard (listen before inserting): it is
    /// not said again, unless the insertion says something else (<see cref="ListenFirst.SayAtInsert"/>).</param>
    private void Insert(int i, string? heard = null)
    {
        CancelListen(null);   // consumed by this insertion (or moot: another word goes in)
        if (i < 0 || i >= _sugg.Count || i >= _win.VisibleCount) return;
        var word = _sugg[i].Word;
        if (i == 0 && _group is { } g && ReferenceEquals(g.List, _sugg)) { _group = null; _correction = null; CorrectGroup(g, heard); return; }
        if (_correction is { } fix && ReferenceEquals(fix.List, _sugg)) { _correction = null; _group = null; Correct(fix, word, heard); return; }
        // 0.11.0 (plan hyphen-names): a compound tile after « head- » — its ending at the caret, or, when it rewrites the head
        // (« saint- » → « Saint-Nicolas »), the whole name over the head, read first
        if (_compound is { } ct && ReferenceEquals(ct.List, _sugg) && KeyboardPolicy.CompoundInsert(word, ct.Head) is { } ci)
        {
            _compound = null;
            if (ci.ReplaceHead) ReplaceCompound(ct, word, ci.Ending, heard);
            else InsertWord(ci.Ending, heard);
            return;
        }
        InsertWord(word, heard);
    }

    /// <summary>
    /// 0.11.0 (plan hyphen-names, owner Q1 = B): the tile <paramref name="whole"/> (« Saint-Nicolas ») writes the head otherwise
    /// than it was typed (« saint-n »): it replaces the head, its hyphen and the letters after it when nothing moved since the
    /// bar (version, window, <see cref="WordTracker.CanReplaceCompound"/>) and a read of the document agrees
    /// (<see cref="SoundAlikeCorrection.DecideCompound"/>, D23: never delete what is not seen; <see cref="OnCompoundRead"/>).
    /// Where the window's text cannot be read (<see cref="ReadableWindows"/>), or anything moved, the ending goes in at the
    /// caret at once (« saint-Nicolas »). The bar is hidden while the read runs. UI thread; the log has the decisions, never
    /// the words.
    /// </summary>
    private void ReplaceCompound(SoundAlikeCorrection.CompoundTarget ct, string whole, string ending, string? heard)
    {
        var fg = Native.GetForegroundWindow();
        if (!_readable.MayReplace(fg, PidOf(fg), HostApps.UiaTextUnavailable(fg)))
        {
            Log?.Invoke("compound: the text cannot be read here, ending inserted at the caret");
            InsertWord(ending, heard);
            return;
        }
        string? proc = ProcessNameOf(fg);
        bool keysOnly = SoundAlikeCorrection.KeysOnlyAllowed(proc);   // empty list in the product
        var mode = AppRules.Effective((PunctuationSpacing)_punct, proc);
        bool adaptive = AppRules.ReadsBackSpacing(proc, _lo?.IsLive(fg) == true) && (PunctuationSpacing)_punct != PunctuationSpacing.None;
        bool same;
        long id = 0;
        lock (_sync)
        {
            same = _tracker.Version == ct.Version && fg == ct.Fg && _tracker.CanReplaceCompound(ct.Span);
            if (same) { id = ++_fixIds; _pendingCompound = (ct, whole, ending, id, keysOnly, heard, mode, adaptive, adaptive && HostApps.IsLibreOffice(proc)); }
        }
        if (!same) { Log?.Invoke("compound: the text moved since the bar, ending inserted at the caret"); InsertWord(ending, heard); return; }
        _sugg = Array.Empty<Suggestion>(); _suggCount = 0; _markAction = false; _win.HideBar();
        _correctRead?.Request(ct.Version, fg, 0, ReasonCorrect, trigger: id);
    }

    /// <summary>0.11.0: the compound's read (the correction reader's thread). The document before the caret ends with the span
    /// → the keys (Backspace over the span, the whole name, a space unless the text goes on) are sent under the lock, only if
    /// still nothing was typed (version, window) and no key is inside the hook. The document differs, no text, a timeout → the
    /// ending at the caret (UI thread, if nothing moved). A key or a jump first, a password field → nothing.</summary>
    private void OnCompoundRead(ReadRequest q, ReadResult r)
    {
        (SoundAlikeCorrection.CompoundTarget? Fix, string Whole, string Ending, long Id, bool KeysOnly, string? Heard,
         PunctuationSpacing Mode, bool Adaptive, bool AdaptiveLo) p;
        lock (_sync) p = _pendingCompound;
        if (p.Fix is not { } ct || p.Id != q.Trigger) return;
        bool agrees = r.Status == ReadStatus.Ok && r.Window is { } w &&
                      SoundAlikeCorrection.DocumentEndsWithSpan(w.Text[..w.Caret], ct.Span, out _);
        var action = SoundAlikeCorrection.DecideCompound(r.Status, agrees, p.KeysOnly);
        if (action == SoundAlikeCorrection.Action.Nothing)
        {
            lock (_sync) if (_pendingCompound.Id == p.Id) _pendingCompound = default;
            if (r.Status == ReadStatus.Password) SuppressPassword(q.Fg, "compound read");
            else Log?.Invoke("compound: typed meanwhile, nothing done");
            return;
        }
        if (action == SoundAlikeCorrection.Action.Insert)
        {
            Log?.Invoke($"compound: read {r.Status} {r.Ms} ms, {(r.Status == ReadStatus.Ok ? "the document differs" : "not replaced")}, ending inserted at the caret");
            _ui.BeginInvoke(() =>
            {
                bool still;
                lock (_sync) { still = _tracker.Version == ct.Version && _pendingCompound.Id == p.Id; if (still) _pendingCompound = default; }
                if (still) InsertWord(p.Ending, p.Heard);
            });
            return;
        }
        bool done = false, space = false;
        var prio = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        try
        {
            lock (_sync)
            {
                if (_pendingCompound.Id == p.Id && _tracker.Version == ct.Version && Native.GetForegroundWindow() == ct.Fg &&
                    Interlocked.Read(ref _keysInHook) == 0)
                {
                    // the char after the caret: the read's when it has one, else the tracker's (keys only)
                    bool known = r.Window is not null || _tracker.HasNextChar;
                    char next = r.Window is { } rw ? (rw.Caret < rw.Text.Length ? rw.Text[rw.Caret] : '\0') : _tracker.NextChar;
                    space = KeyboardPolicy.NeedsSpace(known, next, p.Whole);
                    var keys = Inserter.Build(0, ct.Span.Length, p.Whole + (space ? " " : ""), Marker);
                    var (sent, expected, err) = Inserter.Send(keys);
                    string how = action == SoundAlikeCorrection.Action.Replace ? $"read {r.Ms} ms, the document agrees" : "no text to read, keys only";
                    Log?.Invoke($"compound: {how}, head replaced (space={space}; SendInput {sent}/{expected}, error {err})");
                    if (sent != expected) { _tracker.Reset(); _echo.Reset(); _session = null; }
                    else { ApplyCompound(ct, p.Whole, space, p.Heard, p.Mode, p.Adaptive, p.AdaptiveLo); done = true; }
                }
                else Log?.Invoke("compound: typed meanwhile, nothing done");
                _pendingCompound = default;
            }
        }
        finally { Thread.CurrentThread.Priority = prio; }
        if (done) _ui.BeginInvoke(() => AfterCorrection(p.Whole, ct.Fg, p.Heard));
    }

    /// <summary>0.11.0: the tracker, the echo and the D23 mirror follow the compound just written over the span (under _sync):
    /// the echo goes back over the span as Backspace would and takes the whole name as inserted (said by the « inserted »
    /// echo: « Saint-Nicolas »); our space is armed as after any insertion (D22).</summary>
    private void ApplyCompound(SoundAlikeCorrection.CompoundTarget ct, string whole, bool space, string? heard, PunctuationSpacing mode,
                               bool adaptive, bool adaptiveLo)
    {
        bool say = _speakInserted || heard != null;
        for (int k = 0; k < ct.Span.Length; k++) _echo.Backspace();
        var ev = _echo.Inserted(whole, space, say);
        _tracker.ReplaceCompound(ct.Span, whole, space);
        _session?.Insert(0, ct.Span.Length, whole + (space ? " " : ""));
        if (say || ev is { Kind: EchoKind.Word }) _session?.MarkWordSpoken();
        if (space) _autoSpace.Arm(_tracker.Version, ct.Fg, mode); else _autoSpace.Disarm();
        _adaptiveFg = space && adaptive ? ct.Fg : IntPtr.Zero;
        _adaptiveLo = space && adaptiveLo;
        _adaptiveSetting = (PunctuationSpacing)_punct;
        _fixEcho = say ? null : ev;
    }

    private long TrackerVersion() { lock (_sync) return _tracker.Version; }

    /// <summary>
    /// 2026-11-25, listen before inserting (UI thread): a plain press on tile <paramref name="i"/> (F<i>n</i>, a click, a
    /// tap). It confirms the ringed tile (same index, word, tracker version, window) → the word is inserted, not said again;
    /// otherwise it is a first press: the word is spoken now and the tile ringed. Called only in a listen-first mode (the
    /// F-keys' « 1er appui : écouter », the click's <see cref="ClickMode.ListenFirst"/>).
    /// </summary>
    private void OnTilePress(int i, string how)
    {
        _ringByInvoke = false;
        if (i < 0 || i >= _sugg.Count || i >= _win.VisibleCount) return;
        var word = _sugg[i].Word;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), seq = Volatile.Read(ref _keySeq);
        if (_listen.Press(i, word, TrackerVersion(), Native.GetForegroundWindow()) == ListenFirst.Outcome.Insert)
        {
            Log?.Invoke($"listen confirm {how}");
            Insert(i, heard: word);
            return;
        }
        Heard(i, word, how, t0, seq);
    }

    /// <summary>
    /// 2026-11-25: a listen gesture on tile <paramref name="i"/> — a right-click (touch press-and-hold), Maj+F<i>n</i> (option
    /// A), option D's speaker band, and a click in <see cref="ClickMode.ListenDouble"/>. Speaks the word; with
    /// <paramref name="arm"/> (<see cref="Rings"/>) the tile is armed (re-armed when it is the ringed one: it stays ringed), so
    /// the next plain press on it inserts. Without it (the ListenDouble click) nothing is armed and a ring on another tile is
    /// dropped (the pupil moved on); the word is remembered as heard for the double click / right-click that inserts it.
    /// UI thread.
    /// </summary>
    private void ListenTo(int i, string how, bool arm)
    {
        _ringByInvoke = false;
        if (i < 0 || i >= _sugg.Count || i >= _win.VisibleCount || !_win.Shown) return;
        var word = _sugg[i].Word;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), seq = Volatile.Read(ref _keySeq);
        if (arm) _listen.Arm(i, word, TrackerVersion(), Native.GetForegroundWindow());
        else if (_listen.Current is { } p && p.Index != i) CancelListen("listen elsewhere");
        Heard(i, word, how, t0, seq);
    }

    /// <summary>Say the tile's word (Manual: interrupts, the word voice; the bar never shows in a password or unproven
    /// field) and ring the tile when it is armed. Logs the gesture, the word only when typed text is loggable.
    /// <paramref name="seq"/>: <c>_keySeq</c> when the gesture began. The flags are published, then (after a full fence) the
    /// counter is read again: a key the hook counted meanwhile saw the flags still false, so the cancel is done here
    /// (second review of 238c58b); a key counted later sees the flags and posts its own cancel.</summary>
    private void Heard(int i, string word, string how, long t0, long seq)
    {
        _speaker?.Say(word, SpeechKind.Manual);
        _listen.NoteHeard(i, word, TrackerVersion(), Native.GetForegroundWindow());
        bool armed = _listen.Current is { } p && p.Index == i;
        _wordHeard = true;
        _listenArmed = armed;
        Interlocked.MemoryBarrier();   // the flags' writes before the counter's read (pairs with the hook's Increment)
        if (Volatile.Read(ref _keySeq) != seq) { CancelListen("key"); return; }
        _win.SetListening(armed ? i : null);
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        string w = _probe != ProbePending ? $" '{word}'" : "";
        Log?.Invoke(FormattableString.Invariant($"listen {how}{w}{(armed ? " armed" : "")} ({ms:0.0} ms)"));
    }

    /// <summary>Drop the ringed tile (UI thread); <paramref name="why"/> null = consumed by an insertion (not logged).</summary>
    private void CancelListen(string? why)
    {
        _listenArmed = false; _wordHeard = false; _ringByInvoke = false;
        bool had = _listen.Cancel();
        _win.SetListening(null);
        if (had && why != null) Log?.Invoke($"listen cancelled ({why})");
    }

    /// <summary>After the bar was rebuilt without a cancelling event (a field-probe answer, a settings change, a caret read
    /// that re-seeded the tracker): the ring follows its word when it is still at that index, else it is dropped; the last
    /// heard word follows the same rule on its own (<see cref="ListenFirst.KeepAfterRebuild"/>) — a rebuild with no ring
    /// never forgets it (review of 238c58b: the mode-3 double click after a picture arrived would have said the word again).
    /// A hidden bar forgets both.</summary>
    private void KeepListen()
    {
        if (!_listen.Armed && _listen.LastHeard is null) return;
        if (!_win.Shown) { CancelListen("bar"); return; }
        bool ringed = _listen.Armed;
        int? keep = _listen.KeepAfterRebuild(_sugg.Select(x => x.Word).ToList(), Math.Min(_suggCount, _win.VisibleCount),
                                             TrackerVersion(), Native.GetForegroundWindow());
        _wordHeard = _listen.LastHeard != null || keep != null;
        if (ringed && keep is null)
        {
            _listenArmed = false; _ringByInvoke = false; _win.SetListening(null);
            Log?.Invoke("listen cancelled (bar)");
            return;
        }
        if (keep != null) _win.SetListening(keep);
    }

    /// <summary>
    /// 0.8.3: <paramref name="chosen"/> (from the ≈ list) replaces the finished word of <paramref name="fix"/>, with that
    /// word's case (UI thread). Nothing moved since the list was shown (version, window, the tracker's kept text) → the
    /// document is ALWAYS read first, whatever the tracker's history (review: « keys only » after Pause / Reprendre or a
    /// profile switch says nothing about the host); <see cref="OnCorrectRead"/> decides with
    /// <see cref="SoundAlikeCorrection.Decide"/>. The list is hidden at once (a second click must not insert again).
    /// Anything moved: inserted at the caret as before (<see cref="InsertWord"/>). The log has the decision, never the words.
    /// </summary>
    private void Correct(SoundAlikeCorrection.Target fix, string chosen, string? heard = null)
    {
        var corr = SoundAlikeCorrection.CasedLike(chosen, fix.Word);
        var fg = Native.GetForegroundWindow();
        bool keysOnly = SoundAlikeCorrection.KeysOnlyAllowed(ProcessNameOf(fg));   // UI thread only (cached per window); empty list in the product
        bool same;
        long id = 0;
        lock (_sync)
        {
            same = _tracker.Version == fix.Version && fg == fix.Fg && _tracker.CanCorrect(fix.Word, out var sep) && sep == fix.Sep;
            if (same) { id = ++_fixIds; _pendingFix = (fix, corr, id, keysOnly, heard); }
        }
        if (!same) { Log?.Invoke("correction: the text moved since the list, inserted at the caret"); InsertWord(chosen, heard); return; }
        _sugg = Array.Empty<Suggestion>(); _suggCount = 0; _markAction = false; _win.HideBar();
        _correctRead?.Request(fix.Version, fg, 0, ReasonCorrect, trigger: id);
    }

    /// <summary>0.8.3, the correction read (its own thread): <see cref="SoundAlikeCorrection.Decide"/> on its status and on
    /// whether the document before the caret ends with the word and its separator. Replace: the keys are sent under the
    /// lock, only if still nothing was typed (version, window) and no key is inside the hook; insert: at the caret on the UI
    /// thread, if still nothing moved; nothing: a key or a jump came first (stale), a password field (tracking suspended).</summary>
    private void OnCorrectRead(ReadRequest q, ReadResult r)
    {
        bool group, compound;
        lock (_sync)
        {
            group = _pendingGroup.Fix != null && _pendingGroup.Id == q.Trigger;
            compound = !group && _pendingCompound.Fix != null && _pendingCompound.Id == q.Trigger;
        }
        // only a replacement read's Unavailable (the group's, 0.11.0 the compound's) marks the window unreadable
        _readable.Note(q.Fg, PidOf(q.Fg), r.Status, groupRead: group || compound);
        if (group) { OnGroupRead(q, r); return; }   // 0.11.0: the « ne » group's read
        if (compound) { OnCompoundRead(q, r); return; }   // 0.11.0: a compound name over its head
        (SoundAlikeCorrection.Target? Fix, string Word, long Id, bool KeysOnly, string? Heard) p;
        lock (_sync) p = _pendingFix;
        if (p.Fix is not { } fix || p.Id != q.Trigger) return;
        string docWord = "";
        bool agrees = r.Status == ReadStatus.Ok && r.Window is { } w &&
                      SoundAlikeCorrection.DocumentEndsWith(w.Text[..w.Caret], fix.Word, fix.Sep, out docWord);
        var action = SoundAlikeCorrection.Decide(r.Status, agrees, fix.Sep, p.KeysOnly);
        if (action == SoundAlikeCorrection.Action.Nothing)
        {
            lock (_sync) if (_pendingFix.Id == p.Id) _pendingFix = default;
            if (r.Status == ReadStatus.Password) SuppressPassword(q.Fg, "correction read");
            else Log?.Invoke("correction: typed meanwhile, nothing done");
            return;
        }
        if (action == SoundAlikeCorrection.Action.Insert)
        {
            Log?.Invoke($"correction: read {r.Status} {r.Ms} ms, not replaced, inserted at the caret");
            _ui.BeginInvoke(() =>
            {
                bool still;
                lock (_sync) { still = _tracker.Version == fix.Version && _pendingFix.Id == p.Id; if (still) _pendingFix = default; }
                if (still) InsertWord(p.Word, p.Heard);
            });
            return;
        }
        // the document's case when it was read (an app may have added a capital), else the tracker's
        var corr = action == SoundAlikeCorrection.Action.Replace ? SoundAlikeCorrection.CasedLike(p.Word, docWord) : p.Word;
        var keys = SoundAlikeCorrection.Build(fix.Word.Length, corr, fix.Sep, Marker);
        string how = action == SoundAlikeCorrection.Action.Replace ? $"read {r.Ms} ms, the document agrees" : "no text to read, keys only";
        bool done = false;
        var prio = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        try
        {
            lock (_sync)
            {
                if (_pendingFix.Id == p.Id && _tracker.Version == fix.Version && Native.GetForegroundWindow() == fix.Fg &&
                    Interlocked.Read(ref _keysInHook) == 0)
                {
                    var (sent, expected, err) = Inserter.Send(keys);
                    Log?.Invoke($"correction: {how}, replaced (SendInput {sent}/{expected}, error {err})");
                    if (sent != expected) { _tracker.Reset(); _echo.Reset(); _session = null; }
                    else { ApplyCorrection(fix, corr, p.Heard); done = true; }
                }
                else Log?.Invoke("correction: typed meanwhile, nothing done");
                _pendingFix = default;
            }
        }
        finally { Thread.CurrentThread.Priority = prio; }
        if (done) _ui.BeginInvoke(() => AfterCorrection(corr, fix.Fg, p.Heard));
    }

    /// <summary>0.8.3: the tracker, the echo and the D23 mirror follow the replacement just sent (under _sync).
    /// <paramref name="heard"/> (2026-11-25): the word was just heard on the ≈ list's tile, so it counts as said for the
    /// echo and the D23 session (not repeated by them), as in <see cref="InsertWord"/>.</summary>
    private void ApplyCorrection(SoundAlikeCorrection.Target fix, string corr, string? heard = null)
    {
        bool say = _speakInserted || heard != null;
        _echo.Backspace();                       // back over the separator: the word is the echo's chunk again
        _echo.Inserted(corr, false, say);
        var ev = _echo.Type(fix.Sep);            // the separator commits it (said as inserted, not by the word echo)
        _tracker.CorrectFinished(fix.Word, corr);
        if (SoundAlikeCorrection.IsLineBreak(fix.Sep)) _session = null;   // Left / Right: the mirror cannot follow
        else
        {
            _session?.Insert(0, fix.Word.Length + 1, corr + fix.Sep);
            if (say || ev is { Kind: EchoKind.Word }) _session?.MarkWordSpoken();
        }
        _autoSpace.Disarm();
        _fixEcho = say ? null : ev;
    }

    /// <summary>0.8.3: the corrected word is said (as an inserted word) and the bar follows the new text. UI thread.</summary>
    private void AfterCorrection(string corr, IntPtr fg, string? heard = null)
    {
        EchoEvent? ev;
        lock (_sync) { ev = _fixEcho; _fixEcho = null; }
        if (_speakInserted)
        {
            if (ListenFirst.SayAtInsert(corr, heard)) SayInserted(corr, fg, after: heard != null);
            else Log?.Invoke("  correction: heard just before, not said again");
        }
        else if (ev != null) Echo(ev, fg);
        ScheduleUpdate();
    }

    /// <summary>
    /// 0.11.0 (plan negation): the group tile was chosen — the corrected group replaces the typed words when nothing moved
    /// since the list (version, window, <see cref="WordTracker.CanCorrectSpan"/>) and a read of the document agrees
    /// (<see cref="SoundAlikeCorrection.DecideGroup"/>, D23: never delete what is not seen; the keys-only path of the ≈ list
    /// does not exist here). Anything else does nothing: a group is never inserted at the caret. The log has the decisions,
    /// never the words.
    /// </summary>
    private void CorrectGroup(SoundAlikeCorrection.GroupTarget g, string? heard)
    {
        CancelListen(null);
        var fg = Native.GetForegroundWindow();
        bool same;
        long id = 0;
        lock (_sync)
        {
            same = _tracker.Version == g.Version && fg == g.Fg && _tracker.CanCorrectSpan(g.Original, g.Tail);
            if (same) { id = ++_fixIds; _pendingGroup = (g, id, heard); }
        }
        _sugg = Array.Empty<Suggestion>(); _suggCount = 0; _markAction = false; _win.HideBar();
        if (!same) { Log?.Invoke("negation: the text moved since the list, nothing done"); ScheduleUpdate(); return; }
        _correctRead?.Request(g.Version, fg, 0, ReasonCorrect, trigger: id);
    }

    /// <summary>0.11.0: the group's read (the correction reader's thread). The document before the caret must end with the
    /// group and its tail; then the keys are sent under the lock, only if still nothing was typed (version, window) and no
    /// key is inside the hook. Written with the document's apostrophe.</summary>
    private void OnGroupRead(ReadRequest q, ReadResult r)
    {
        (SoundAlikeCorrection.GroupTarget? Fix, long Id, string? Heard) p;
        lock (_sync) p = _pendingGroup;
        if (p.Fix is not { } g || p.Id != q.Trigger) return;
        string docSpan = "";
        bool agrees = r.Status == ReadStatus.Ok && r.Window is { } w &&
                      SoundAlikeCorrection.DocumentEndsWith(w.Text[..w.Caret], g.Original, g.Tail, out docSpan);
        if (SoundAlikeCorrection.DecideGroup(r.Status, agrees) == SoundAlikeCorrection.Action.Nothing)
        {
            lock (_sync) if (_pendingGroup.Id == p.Id) _pendingGroup = default;
            if (r.Status == ReadStatus.Password) SuppressPassword(q.Fg, "negation read");
            else Log?.Invoke($"negation: read {r.Status} {r.Ms} ms, {(r.Status == ReadStatus.Ok ? "the document differs" : "no text to read")}, nothing done");
            if (r.Status != ReadStatus.Password) _ui.BeginInvoke(ScheduleUpdate);
            return;
        }
        char style;
        lock (_sync) style = _tracker.ApostropheStyle;
        // the document's capital (an app may have capitalised « C'est pas ») and apostrophe
        var rep = Elision.Styled(SoundAlikeCorrection.CasedGroup(g.Replacement, false, docSpan), SoundAlikeCorrection.ApostropheOf(docSpan, style));
        var keys = SoundAlikeCorrection.BuildGroup(g.Original, g.Tail, rep, Marker);
        bool done = false;
        var prio = Thread.CurrentThread.Priority;
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        try
        {
            lock (_sync)
            {
                if (_pendingGroup.Id == p.Id && _tracker.Version == g.Version && Native.GetForegroundWindow() == g.Fg &&
                    Interlocked.Read(ref _keysInHook) == 0)
                {
                    var (sent, expected, err) = Inserter.Send(keys);
                    Log?.Invoke($"negation: read {r.Ms} ms, the document agrees, replaced ({g.Rule}; SendInput {sent}/{expected}, error {err})");
                    if (sent != expected) { _tracker.Reset(); _echo.Reset(); _session = null; }
                    else { ApplyGroupCorrection(g, rep, p.Heard); done = true; }
                }
                else Log?.Invoke("negation: typed meanwhile, nothing done");
                _pendingGroup = default;
            }
        }
        finally { Thread.CurrentThread.Priority = prio; }
        // the group heard on its tile counts as said whatever its apostrophe (the tile: the tracker's; the document's here)
        string? heard = p.Heard is { } h && string.Equals(Elision.Styled(h, '\''), Elision.Styled(rep, '\''), StringComparison.OrdinalIgnoreCase) ? rep : p.Heard;
        if (done) _ui.BeginInvoke(() => AfterCorrection(rep, g.Fg, heard));
    }

    /// <summary>The window's process id (0 when gone): <see cref="ReadableWindows"/> keys.</summary>
    private static uint PidOf(IntPtr hwnd) { Native.GetWindowThreadProcessId(hwnd, out uint pid); return pid; }

    private static readonly char[] LineBreaks = { '\n', '\r' };

    /// <summary>0.11.0: the tracker, the echo and the D23 mirror follow the group replacement just sent (under _sync). The
    /// echo goes back over the group and its tail as Backspace would, then takes the corrected group: its words as typed,
    /// its last word as inserted (said by the « inserted » echo, owner Q7), then the tail — a sentence end there makes the
    /// sentence echo say the corrected sentence.</summary>
    private void ApplyGroupCorrection(SoundAlikeCorrection.GroupTarget g, string rep, string? heard)
    {
        bool say = _speakInserted || heard != null;
        int back = g.Original.Length + g.Tail.Length;
        var ev = _echo.ReplacedGroup(back, rep, g.Tail, say);
        _tracker.CorrectSpan(g.Original, g.Tail, rep);
        if (g.Tail.IndexOfAny(LineBreaks) >= 0) _session = null;   // Left / Right: the mirror cannot follow
        else
        {
            _session?.Insert(0, back, rep + g.Tail);
            if (say || ev is { Kind: EchoKind.Word }) _session?.MarkWordSpoken();
        }
        _autoSpace.Disarm();
        _fixEcho = say ? null : ev;
    }

    /// <param name="heard">listen before inserting: the word was just heard (the tile's text), so the insertion does not say it
    /// again — nor does the word echo or the D23 re-read — unless what it would say differs (« les amis »).</param>
    private void InsertWord(string word, string? heard = null)
    {
        // D22 per-app rule (UI thread, never in the hook): looked up BEFORE the tracker snapshot, so the lookup does
        // not widen the window in which a typed key makes the snapshot stale
        var fg = Native.GetForegroundWindow();
        string? proc = ProcessNameOf(fg);
        var mode = AppRules.Effective((PunctuationSpacing)_punct, proc);
        bool ia2Live = _lo?.IsLive(fg) == true;   // LibreOffice phase 1: Writer answers through IA2 (D22 read-back, owner Q1)
        string cur; int suf; bool known; char next; long v0;
        lock (_sync)
        {
            cur = _tracker.Current; suf = _tracker.Suffix.Length; known = _tracker.HasNextChar; next = _tracker.NextChar;
            v0 = _tracker.Version;   // a key typed while we inject makes our picture of the text unreliable
        }
        bool space = KeyboardPolicy.NeedsSpace(known, next, word);
        // whole word: Delete the part after the caret, Backspace the part before, then the word
        var (sent, expected, err) = Inserter.Replace(suf, cur.Length, word + (space ? " " : ""), Marker);
        Log?.Invoke($"insert '{word}' (delete {suf} right, {cur.Length} left, space={space}): SendInput {sent}/{expected}, error {err}");
        if (sent != expected) { lock (_sync) { _tracker.Reset(); _session = null; } HideSoon(); return; }   // blocked: don't pretend
        EchoEvent? ev;
        // an elision (l') is said with the next word (« l'horizon »), never alone
        bool say = _speakInserted && !Elision.IsElided(word);
        // 2026-11-25: a word just heard counts as said for the echo and the D23 session (not repeated by them)
        bool spoken = say || heard != null && !Elision.IsElided(word);
        string said;
        bool raced;
        lock (_sync)
        {
            raced = _tracker.Version != v0;
            if (raced)
            {
                // the hook saw a key between our read of the tracker and now: forget the word, never arm
                _tracker.Reset(); _echo.Reset(); _autoSpace.Disarm();
                _session = null;   // our injection and that key: order unknown, the mirror can't follow
                ev = null; said = word;
            }
            else
            {
                _tracker.Accept(word, space);
                ev = _echo.Inserted(word, space, spoken);
                said = _echo.LastInsertedSpoken;   // plan 08b: « les amis » after « les » (liaison grouping)
                // D23: the same Delete / Backspace / text as sent; a word said now (inserted or word echo) is not re-read
                _session?.Insert(suf, cur.Length, word + (space ? " " : ""));
                if (spoken || ev is { Kind: EchoKind.Word }) _session?.MarkWordSpoken();
                ProbeEdits(fg);
                // D22: our space may be taken back by the next key (punctuation, a second space)
                if (space) _autoSpace.Arm(_tracker.Version, fg, mode); else _autoSpace.Disarm();
                // Word may space ; : ! ? itself (AutoCorrect, French text): read back and adapt, unless « Aucune »
                // (LibreOffice also spaces them: read back through IA2 in a live Writer window, phase 1 / owner Q1; else
                // its U+00A0 is accepted as is and nothing is read back, 0.11.0)
                bool adaptive = space && AppRules.ReadsBackSpacing(proc, ia2Live) && (PunctuationSpacing)_punct != PunctuationSpacing.None;
                _adaptiveFg = adaptive ? fg : IntPtr.Zero;
                _adaptiveLo = adaptive && HostApps.IsLibreOffice(proc);
                _adaptiveSetting = (PunctuationSpacing)_punct;
            }
        }
        if (raced) Log?.Invoke("  insert: a key was typed meanwhile, tracker reset, auto-space not armed");
        else if (space && mode != (PunctuationSpacing)_punct) Log?.Invoke($"  auto-space armed for {proc}: {AppSettings.PunctuationNames[mode]} (the app spaces ; : ! ? itself)");
        // a confirmation whose group differs (« les amis », « l'école ») waits for the heard word to end (never cuts it)
        if (say && ListenFirst.SayAtInsert(said, heard)) SayInserted(said, fg, after: heard != null);
        else if (say) Log?.Invoke("  insert: heard just before, not said again");
        else if (ev != null) Echo(ev, fg);
        ScheduleUpdate();
    }

    /// <summary>Process name (no .exe) of the window's process, or null (one cached helper for every thread).</summary>
    private static string? ProcessNameOf(IntPtr hwnd) => HostApps.ProcessNameOf(hwnd);

    public void Dispose()
    {
        _lineEndTimer?.Stop();   // plan 10B: no line-end tick after the probes are gone
        _caretRefresh?.Stop();
        // WR5: the re-check timer first (no post after the loop is gone); a WinEvent still running finds it disposed
        if (_fgRecheckTimer is { } fgt) { try { fgt.Change(Timeout.Infinite, Timeout.Infinite); } catch (ObjectDisposedException) { } fgt.Dispose(); }
        if (_hookThreadId != 0) Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread?.Join(1000);
        _hookThreadId = 0;
        _reader?.Dispose();
        _selection?.Dispose();
        _punctCheck?.Dispose();
        _sentenceRead?.Dispose();
        _correctRead?.Dispose();
        _sentenceTimer?.Dispose();
        _fieldProbe?.Dispose();
        _editProbe?.Dispose();
        _loPrefetch?.Dispose();
        _settleRead?.Dispose();
        _lo?.Dispose();   // its RCWs are released on an MTA thread, never here (UI thread)
    }
}
