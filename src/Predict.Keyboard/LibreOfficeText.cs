using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Predict.Keyboard;

/// <summary>
/// LibreOffice Writer phase 1: the text around the caret through IAccessible2, over the <see cref="ILoBackend"/> seam
/// (pure orchestration, unit-tested with a fake). The pieces:
/// <list type="bullet">
/// <item>a <b>focus slot</b> written by the hook thread (<see cref="NoteFocus"/>: one struct write, a sequence number, a
/// prefetch request; no COM);</item>
/// <item>a <b>paragraph cache</b>: one entry per (soffice pid, foreground window, focus seq), with a lease count, so a
/// superseded entry is disposed (its RCWs released) by the last MTA thread that used it — never by the hook;</item>
/// <item>a <b>circuit breaker</b> per pid: a stuck call, 3 timeouts in a row, 5 structural failures in a row, or the IA2
/// proxy missing (globally) switch IA2 off, which is exactly the keys-only quick fix (<c>src=skip</c>). Phase 2 (S2b): a
/// stuck call or 3 timeouts switch it off for a cooldown only (10 s, 20 s, 40 s, then for good — counted again from 1
/// after 10 minutes on without an episode), after which one trial call decides — a modal dialog being created (« Enregistrer sous ») no longer costs IA2 for the whole session;</item>
/// <item>the reads: <see cref="Read"/>, <see cref="ReadBefore"/> (D22 read-back), <see cref="CaretPoint"/> (the bar),
/// <see cref="ProbePassword"/> (cached per focus seq: no COM call per spoken word in steady typing), and phase 2's
/// <see cref="ReadSelection"/> (the whole selection, walking flowsFrom / flowsTo).</item>
/// </list>
/// Order of a read: password (<c>STATE_SYSTEM_PROTECTED</c>) before any text call; an unfocused object gets one
/// re-resolve via the <c>accFocus</c> walk; only paragraphs / headings; a non-empty selection is <c>Selection</c> without
/// a text call; one <c>get_text</c> window; the text length must equal the offsets (UTF-16) or nothing is trusted.
/// Never logs any text. All methods but <see cref="NoteFocus"/> / <see cref="IsLive"/> run on MTA worker threads.
/// </summary>
internal sealed class LibreOfficeText : IDisposable
{
    public const string Src = "ia2";
    public const int DefaultStuckMs = 2000, TimeoutsToOff = 3, StructuralToOff = 5;
    /// <summary>The fallback caret point is used for at most this long (S4: only when the caret worker is late).</summary>
    public const int CaretCacheMs = 1000;
    /// <summary>A read slower than this logs its phases (never text).</summary>
    public const int PhaseLogMs = 40;

    /// <summary>Set once by the controller before the hooks start, cleared on dispose (S3); null = no IA2.</summary>
    public static LibreOfficeText? Shared;

    private readonly ILoBackend _b;
    private readonly bool _enabled;
    private readonly Action<string>? _log;
    private readonly Action<IntPtr>? _requestPrefetch;
    private readonly int _timeoutMs, _stuckMs;
    private readonly Action<Action> _onMta;
    private volatile bool _disposed;

    // ---- focus slot (hook thread writes, workers read) ----
    private readonly object _slotLock = new();
    private FocusSlot _slot;      // guarded by _slotLock
    private long _seq;            // guarded by _slotLock (read with Volatile elsewhere)

    // ---- cache, breaker, in-flight calls ----
    private readonly object _lock = new();
    private Entry? _entry;                                  // guarded by _lock
    private uint _pid;                                      // current soffice pid, 0 = none (guarded)
    private bool _off, _globalOff;                          // guarded
    private int _timeouts, _structural;                     // guarded
    private readonly Dictionary<long, InFlight> _inFlight = new();   // guarded
    private long _nextCall;                                 // guarded
    private volatile IntPtr _live;
    private ProbeCache _probe;                              // guarded
    private ProbeCache _probeLogged;                        // guarded: the cached answer last logged
    private Unfound _unfound;                               // guarded: probes that found no focused object, per focus seq
    /// <summary>Probes of one focus object that find nothing before IA2 stops answering for it (the UIA probe takes over).</summary>
    public const int ProbeGiveUp = 3;
    private CaretCache _caret;                              // guarded

    /// <param name="enabled">false = <c>--no-ia2</c>: the backend is never called.</param>
    /// <param name="requestPrefetch">asked by <see cref="NoteFocus"/> with the window (S3: the <c>lo-prefetch</c> queue,
    /// whose read calls <see cref="Prefetch"/>); must be hook-thread safe.</param>
    /// <param name="onMta">runs an RCW release on an MTA thread (<see cref="Dispose"/>); default: the thread pool. Tests
    /// pass a synchronous one.</param>
    public LibreOfficeText(ILoBackend backend, bool enabled, Action<string>? log, Action<IntPtr>? requestPrefetch = null,
                           int timeoutMs = KeyboardPolicy.ReadTimeoutMs, int stuckMs = DefaultStuckMs, Action<Action>? onMta = null)
    {
        _onMta = onMta ?? (a => ThreadPool.UnsafeQueueUserWorkItem(_ => { try { a(); } catch (Exception) { } }, null));
        _b = backend;
        _enabled = enabled;
        _log = log;
        _requestPrefetch = requestPrefetch;
        _timeoutMs = timeoutMs;
        _stuckMs = stuckMs;
    }

    public bool Enabled => _enabled;

    /// <summary>The window where IA2 last answered (Ok / Selection / Password / a probe answer); cleared by the breaker
    /// or a pid change. Read by the hook (settle reads) and the UI thread (D22, the bar): one volatile compare.</summary>
    public bool IsLive(IntPtr fg) => fg != IntPtr.Zero && _live == fg;

    /// <summary>Is IA2 switched off for this pid (or globally)? Cheap, no backend call except the pid lookup. Still true
    /// after a cooldown until the trial call has answered (S2b).</summary>
    public bool IsOff(IntPtr fg)
    {
        if (!_enabled) return true;
        lock (_lock)
        {
            if (_globalOff) return true;
            return _off && _b.PidOf(fg) == _pid;
        }
    }

    /// <summary>Hook thread, O(1): the out-of-context EVENT_OBJECT_FOCUS of a LibreOffice window (<paramref name="root"/>
    /// = its top-level window). Supersedes the cached paragraph (freed later on a worker) and, with
    /// <paramref name="prefetch"/> (Writer; not a dialog, whose slot serves the password probe only), asks a prefetch.</summary>
    public void NoteFocus(IntPtr root, IntPtr hwnd, int objectId, int childId, bool prefetch = true)
    {
        if (!_enabled) return;
        lock (_slotLock)
        {
            _seq++;
            _slot = new FocusSlot(root, hwnd, objectId, childId, _seq);
        }
        if (prefetch) _requestPrefetch?.Invoke(root);
    }

    /// <summary>The focus slot's sequence number (tests; the probe and caret caches key on it).</summary>
    public long FocusSeq { get { lock (_slotLock) return _seq; } }

    // =====================================================================================================
    // the reads
    // =====================================================================================================

    /// <summary>A caret read in a Writer window (steps 1–8 of the plan). Off / disabled → <c>Unavailable</c>, src <c>skip</c>.</summary>
    public ReadResult Read(IntPtr fg)
    {
        if (!Begin(fg, out var op)) return Fail(ReadStatus.Unavailable, "skip");
        try { return ReadCore(fg, op.Pid); }
        finally { End(op); }
    }

    /// <summary>D22 read-back: the <paramref name="chars"/> raw characters before the caret (steps 1–3, then one text call).</summary>
    public ReadResult ReadBefore(IntPtr fg, int chars)
    {
        if (!Begin(fg, out var op)) return Fail(ReadStatus.Unavailable, "skip");
        try
        {
            var e = AcquireFocused(op.Pid, fg, out int state, out int hr);
            if (e is null) return Fail(ReadStatus.Unavailable, Src);
            try
            {
                if ((state & Ia2.STATE_SYSTEM_PROTECTED) != 0) { Succeeded(fg); return Fail(ReadStatus.Password, Src); }
                if (!IsTextRole(e.P.Role)) return Fail(ReadStatus.Unavailable, Src);
                hr = e.P.CaretOffset(out int c);
                if (hr < 0) return Fault(hr, op.Pid, e);
                if (c < 0) return Fail(ReadStatus.Unavailable, Src);
                int lo = Math.Max(0, c - Math.Max(0, chars));
                string text = "";
                if (c > lo)
                {
                    hr = e.P.Text(lo, c, out string? t);
                    if (hr < 0) return Fault(hr, op.Pid, e);
                    text = t ?? "";
                }
                if (text.Length != c - lo) { _log?.Invoke("ia2: offset mismatch (read-back)"); return Fail(ReadStatus.Unavailable, Src); }
                if (_b.Foreground() != fg) return Fail(ReadStatus.Stale, Src);
                Succeeded(fg);
                return new ReadResult(ReadStatus.Ok, Src, TextContext.Invalid, 0, text);
            }
            finally { Release(e); }
        }
        finally { End(op); }
    }

    /// <summary>The bar's anchor: (left, bottom) of the caret in screen pixels, as <c>Caret.Get</c> returns for a Win32
    /// caret; null when unknown or outside the foreground window. Stores the 1 s fallback (<see cref="CachedCaretPoint"/>).</summary>
    public (int X, int Y)? CaretPoint(IntPtr fg)
    {
        if (!Begin(fg, out var op)) return null;
        try
        {
            var e = AcquireFocused(op.Pid, fg, out int state, out int hr);
            if (e is null) return null;
            try
            {
                if ((state & Ia2.STATE_SYSTEM_PROTECTED) != 0 || !IsTextRole(e.P.Role)) return null;
                hr = e.P.CaretOffset(out int c);
                if (hr < 0) { Fault(hr, op.Pid, e); return null; }
                if (c < 0) return null;
                hr = e.P.CharacterCount(out int n);
                if (hr < 0) { Fault(hr, op.Pid, e); return null; }
                int x, y, w, h;
                (int X, int Y) pt;
                if (n <= 0)
                {
                    hr = e.P.Location(out x, out y, out w, out h);
                    if (hr < 0) { Fault(hr, op.Pid, e); return null; }
                    pt = (x, y + h);
                }
                else if (c < n)
                {
                    hr = e.P.CharacterExtents(c, out x, out y, out w, out h);
                    if (hr < 0) { Fault(hr, op.Pid, e); return null; }
                    pt = (x, y + h);
                }
                else
                {
                    hr = e.P.CharacterExtents(n - 1, out x, out y, out w, out h);
                    if (hr < 0) { Fault(hr, op.Pid, e); return null; }
                    pt = (x + w, y + h);
                }
                if (!_b.WindowRect(fg, out int l, out int t, out int r, out int bt)) return null;
                if (pt.X < l || pt.X > r || pt.Y < t || pt.Y > bt) return null;   // DPI / wrong-object guard
                lock (_lock) _caret = new CaretCache(fg, e.Seq, pt.X, pt.Y, _b.NowMs());
                return pt;
            }
            finally { Release(e); }
        }
        finally { End(op); }
    }

    /// <summary>The UI thread waits at most this long for the caret worker (S4).</summary>
    public const int CaretWaitMs = 20;

    /// <summary>Where a bar caret point came from: the worker in time, the 1 s fallback (worker late or busy), the throttle.</summary>
    public enum CaretFrom { Fresh, Late, Recent }

    private readonly object _caretGate = new();
    private AutoResetEvent? _caretSignal;   // created with the worker (guarded by _caretGate)
    private CaretJob? _caretJob;            // pending or running (guarded by _caretGate)

    private sealed class CaretJob
    {
        public CaretJob(IntPtr fg) => Fg = fg;
        public IntPtr Fg { get; }
        public readonly ManualResetEventSlim Done = new(false);
        public (int X, int Y)? Result;
        private int _holders = 2;   // the waiter and the worker: the last one disposes the event
        public void Release() { if (Interlocked.Decrement(ref _holders) == 0) Done.Dispose(); }
    }

    /// <summary>
    /// The bar's anchor for the UI thread (S4), which never makes a COM call: <see cref="CaretPoint"/> runs on the
    /// <c>lo-caret</c> worker (one MTA thread, one slot) and the caller waits at most <paramref name="waitMs"/>. Late, or
    /// the worker still busy with an earlier lookup (no second call is queued behind a slow LibreOffice) → the 1 s cache
    /// (<see cref="CachedCaretPoint"/>, <c>Late</c> = true) or null (the caller uses the mouse).
    /// </summary>
    public (int X, int Y, CaretFrom From)? CaretPointBounded(IntPtr fg, int waitMs = CaretWaitMs)
    {
        if (!_enabled || _disposed || fg == IntPtr.Zero) return null;
        // the plan's CPU fallback (VM L18: 4.2–4.3 ms of Writer CPU per key at 40 keys/s, over the 4.0 target; one lookup
        // per bar update was the cost): a point of the same paragraph younger than CaretThrottleMs is reused, no COM call
        if (CachedCaretPoint(fg, CaretThrottleMs) is { } recent) return (recent.X, recent.Y, CaretFrom.Recent);
        CaretJob? job = null;
        lock (_caretGate)
        {
            if (_caretJob is null)
            {
                job = new CaretJob(fg);
                _caretJob = job;
                if (_caretSignal is null)
                {
                    _caretSignal = new AutoResetEvent(false);
                    var t = new Thread(CaretLoop) { IsBackground = true, Name = "lo-caret" };
                    if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.MTA);
                    t.Start();
                }
                _caretSignal.Set();
            }
        }
        if (job is not null)
        {
            // a kernel wait ends on the next timer tick (15.6 ms by default): Wait(20) measured up to 29 ms in the VM. A late
            // answer is rare (3 of 90 lookups there), so the last tick before the deadline is spent yielding instead
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), limit = waitMs * System.Diagnostics.Stopwatch.Frequency / 1000;
            // a kernel wait for all but the last timer tick (no spinning), then yield to the deadline
            bool done = job.Done.Wait(Math.Max(0, waitMs - 16));
            while (!done && System.Diagnostics.Stopwatch.GetTimestamp() - t0 < limit && !(done = job.Done.IsSet)) Thread.Yield();
            var res = job.Result;
            job.Release();
            if (done) return res is { } p ? (p.X, p.Y, CaretFrom.Fresh) : null;
        }
        return CachedCaretPoint(fg) is { } c ? (c.X, c.Y, CaretFrom.Late) : null;
    }

    /// <summary>A bar caret lookup reuses the point of the same paragraph when it is younger than this (fast typing: at most
    /// one IA2 lookup per 100 ms; at a child's pace every update still looks up).</summary>
    public const int CaretThrottleMs = 100;

    private void CaretLoop()
    {
        while (!_disposed)
        {
            AutoResetEvent sig;
            lock (_caretGate) sig = _caretSignal!;
            sig.WaitOne();
            CaretJob? j;
            lock (_caretGate) j = _caretJob;
            if (j is null) continue;
            (int X, int Y)? r = null;
            try { if (!_disposed) r = CaretPoint(j.Fg); }
            catch (Exception ex) { _log?.Invoke($"ia2 caret error: {ex.GetType().Name}"); }
            j.Result = r;
            lock (_caretGate) _caretJob = null;
            j.Done.Set();
            j.Release();
        }
    }

    /// <summary>The last caret point of this window and focus seq, if younger than <see cref="CaretCacheMs"/>.</summary>
    public (int X, int Y)? CachedCaretPoint(IntPtr fg) => CachedCaretPoint(fg, CaretCacheMs);

    /// <summary>The last caret point of this window and focus seq, if younger than <paramref name="maxAgeMs"/>.</summary>
    public (int X, int Y)? CachedCaretPoint(IntPtr fg, int maxAgeMs)
    {
        if (!_enabled) return null;
        long seq = FocusSeq;
        lock (_lock)
        {
            var c = _caret;
            if (c.Fg != fg || c.Fg == IntPtr.Zero || c.Seq != seq || _off || _globalOff) return null;
            if (_b.NowMs() - c.Tick > maxAgeMs) return null;
            return (c.X, c.Y);
        }
    }

    /// <summary>
    /// Password check for a Writer window, cached per (window, pid, focus seq): 0 COM calls on a hit. PROTECTED →
    /// Password; a focused paragraph / heading → NotPassword; anything else (dialogs, fields, toolbars) → Unavailable, so
    /// nothing is spoken there. null = no answer from IA2 (no focus slot for this window yet, off, disabled): the caller
    /// uses today's UIA probe.
    /// <paramref name="dialog"/>: a LibreOffice dialog (VM L11, « Saisir le mot de passe »: its first field gets no focus
    /// event of its own and the accFocus walk finds nothing, while UIA called it NotPassword) — no focused object found
    /// there is <b>Password</b> (nothing tracked, logged, shown or spoken until a focus event names the field), never cached
    /// and never given up.
    /// </summary>
    public ProbeStatus? ProbePassword(IntPtr fg, bool dialog = false)
    {
        if (!_enabled || fg == IntPtr.Zero) return null;
        FocusSlot slot;
        lock (_slotLock) slot = _slot;
        if (slot.Root != fg) return null;
        uint pid = _b.PidOf(fg);
        bool hit = false, logHit = false;
        ProbeStatus cached = default;
        lock (_lock)
        {
            var p = _probe;
            if (p.Fg == fg && p.Pid == pid && p.Seq == slot.Seq && pid == _pid && !_off && !_globalOff)
            {
                hit = true;
                cached = p.Status;
                // logged once per answer, not per spoken word (review S3–S7)
                if (_probeLogged != p) { _probeLogged = p; logHit = true; }
            }
            // this focus object was looked for ProbeGiveUp times without finding it: IA2 has no answer here (UIA probe)
            else if (_unfound.Fg == fg && _unfound.Seq == slot.Seq && _unfound.Count >= ProbeGiveUp) return null;
        }
        if (hit) { if (logHit) _log?.Invoke($"probe ia2 cached: {cached}"); return cached; }
        if (!Begin(fg, out var op)) return null;
        try
        {
            var e = AcquireFocused(op.Pid, fg, out int state, out int hr, out bool faulted);
            ProbeStatus s;
            if (e is null) s = dialog ? ProbeStatus.Password : ProbeStatus.Unavailable;
            else
            {
                try
                {
                    s = (state & Ia2.STATE_SYSTEM_PROTECTED) != 0 ? ProbeStatus.Password
                      : IsTextRole(e.P.Role) ? ProbeStatus.NotPassword
                      : ProbeStatus.Unavailable;
                }
                finally { Release(e); }
            }
            if (s != ProbeStatus.Unavailable && e is not null && !dialog) Succeeded(fg);   // a dialog never becomes « live »
            // cached only when an object answered: « nothing focused found » (a stale slot, a walk that failed) is asked
            // again next time, since the focus may settle (review S2)
            if (!faulted && e is not null)
                lock (_lock) if (op.Pid == _pid) _probe = new ProbeCache(fg, op.Pid, e.Seq, s);
            // role and state in hex (never a name or text): why a field was not classified
            _log?.Invoke(FormattableString.Invariant($"probe ia2 state {_b.NowMs() - op.Start:0.0} ms: {s}") +
                         (e is null ? $" (no focused object{(faulted ? $", 0x{hr:X8}" : "")})" : $" (role 0x{e.P.Role:X}, state 0x{state:X8})"));
            if (e is null && !dialog)
            {
                // review S3–S7: a Writer window whose focus object is never found would drop every echo for good (nothing
                // trips the breaker): after ProbeGiveUp misses on the same focus object, IA2 gives no answer (UIA probe)
                bool giveUp;
                lock (_lock)
                {
                    _unfound = _unfound.Fg == fg && _unfound.Seq == slot.Seq ? _unfound with { Count = _unfound.Count + 1 } : new Unfound(fg, slot.Seq, 1);
                    giveUp = _unfound.Count >= ProbeGiveUp;
                }
                if (giveUp) { _log?.Invoke($"probe ia2: no focused object after {ProbeGiveUp} tries, UIA probe for this focus"); return null; }
            }
            return s;
        }
        finally { End(op); }
    }

    /// <summary>The prefetch (lo-prefetch worker, after a focus event): resolve the object only — find, QS, QI, role; no
    /// text — so the first read after Enter or a click is warm.</summary>
    public void Prefetch(IntPtr fg)
    {
        if (!Begin(fg, out var op)) return;
        try
        {
            var e = Acquire(op.Pid, fg, forceWalk: false, out _, out int hr);
            if (e is null) { if (hr < 0) Fault(hr, op.Pid, null); return; }
            Release(e);
        }
        finally { End(op); }
    }

    private ReadResult ReadCore(IntPtr fg, uint pid)
    {
        double t0 = _b.NowMs();
        var e = AcquireFocused(pid, fg, out int state, out int hr, out _, acceptSelected: true);
        double tFind = _b.NowMs();
        if (e is null) return Fail(ReadStatus.Unavailable, Src);
        try
        {
            // 1. password first: no text call at all
            if ((state & Ia2.STATE_SYSTEM_PROTECTED) != 0) { Succeeded(fg); return Fail(ReadStatus.Password, Src); }
            // 2. paragraphs and headings only (sidebar, toolbars, Find bar, dialogs: not text we follow)
            if (!IsTextRole(e.P.Role)) return Fail(ReadStatus.Unavailable, Src);
            // 3. caret
            hr = e.P.CaretOffset(out int c);
            if (hr < 0) return Fault(hr, pid, e);
            // a selected paragraph that is not the focused one (Ctrl+A on a long document): only « a selection », never text
            if ((state & Ia2.STATE_SYSTEM_FOCUSED) == 0) c = -1;   // PROTECTED returned above
            // 4. a non-empty selection: no text call. Phase 2 probe (VM, 26.8): a table block selection gives caret -1, so the
            // selection is asked before the caret is judged; a selection that ends exactly at this paragraph's edge leaves
            // only an empty mark here (nSelections 1, [c, c] at offset 0 / n — never seen in plain typing): the neighbour
            // on that side tells (the edge rule)
            hr = e.P.SelectionCount(out int ns);
            if (hr < 0) return Fault(hr, pid, e);
            if (ns > 0)
            {
                hr = e.P.Selection(0, out int ss, out int se);
                if (hr < 0) return Fault(hr, pid, e);
                if (ss != se) { Succeeded(fg); return Fail(ReadStatus.Selection, Src); }
                if (c >= 0)
                {
                    hr = EdgeSelection(e.P, c, out bool edge);
                    if (hr < 0 && Classify(hr) is HrClass.Global or HrClass.ServerGone) return Fault(hr, pid, e);
                    if (edge) { Succeeded(fg); return Fail(ReadStatus.Selection, Src); }
                }
            }
            if (c < 0) return Fail(ReadStatus.Unavailable, Src);
            // 5. length, 6. one window [c - BeforeMax, c + AfterMax)
            hr = e.P.CharacterCount(out int n);
            if (hr < 0) return Fault(hr, pid, e);
            if (c > n) return Fail(ReadStatus.Unavailable, Src);
            double tState = _b.NowMs();
            int lo = Math.Max(0, c - EditSession.BeforeMax), hi = Math.Min(n, c + EditSession.AfterMax);
            string text = "";
            if (hi > lo)
            {
                hr = e.P.Text(lo, hi, out string? t);
                if (hr < 0) return Fault(hr, pid, e);
                text = t ?? "";
            }
            double tText = _b.NowMs();
            if (tText - t0 > PhaseLogMs)
                _log?.Invoke($"ia2 read phases: find {tFind - t0:0.0} state {tState - tFind:0.0} text {tText - tState:0.0} ms");
            if (text.Length != hi - lo) { _log?.Invoke("ia2: offset mismatch"); return Fail(ReadStatus.Unavailable, Src); }
            bool truncStart = lo > 0, truncEnd = hi < n;
            // 7. the tracker state and the D23 window (paragraph-relative offsets: base −1, as with UIA)
            var ctx = ContextExtractor.Extract(text, c - lo, truncStart, truncEnd);
            if (!ctx.Valid) return Fail(ReadStatus.Unavailable, Src);
            // 8. foreground re-check
            if (_b.Foreground() != fg) return Fail(ReadStatus.Stale, Src);
            Succeeded(fg);
            return new ReadResult(ReadStatus.Ok, Src, ctx, 0, null, TextWindow.Of(text, c - lo, truncStart, truncEnd));
        }
        finally { Release(e); }
    }

    // =====================================================================================================
    // phase 2: the selection, through IA2 (no Ctrl+C)
    // =====================================================================================================

    /// <summary>The selection reader's cap (<c>SelectionReader.MaxChars</c>, which the wiring passes).</summary>
    public const int SelectionMaxChars = 20_000;
    /// <summary>A selection walk follows at most this many flowsFrom / flowsTo hops (VM P0: Ctrl+A on 20 pages = 165).</summary>
    public const int SelectionMaxHops = 400;
    /// <summary>The read stops past this time, checked between calls of the walk, the text pass and the D26 context (VM P0:
    /// Ctrl+A on 20 pages ≈ 230 ms walk + 19 ms text). The breaker counts a timeout only past twice this (review S2: the
    /// last call may start just before the budget ends), still under the 2 s stuck limit.</summary>
    public const int SelectionWalkBudgetMs = 700;
    /// <summary>At most this many ranges are read per paragraph (Writer reports one per selection ring entry).</summary>
    private const int MaxRanges = 16;
    /// <summary><see cref="Portions"/>: a range outside [0, n] (E_INVALIDARG): the offsets cannot be trusted.</summary>
    private const int BadRange = unchecked((int)0x80070057);

    /// <summary>
    /// Phase 2: the whole selection of a Writer window through IA2. LibreOffice has no « whole selection » call: every
    /// paragraph reports its own part of the selection (focused or not), so the walk starts at the focused paragraph and
    /// follows <c>flowsFrom</c> / <c>flowsTo</c> while the selection touches the shared edge — first without text (the
    /// extent), then the text in document order (paragraphs joined by '\n'), with up to <paramref name="context"/>
    /// characters before / after it in its own paragraph for D26. Longer than <paramref name="maxChars"/>, or stopped by the
    /// time budget in the text pass: <c>Cut</c>, the text cut here at the last word boundary before the limit (never inside a
    /// surrogate pair), <c>After</c> = "" — so the selection reader's own cut (length ≥ its MaxChars) does not apply again.
    /// <c>Ok</c> with <c>Partial</c> = the start or end was not reached (hop / time budget, a selected non-text object, a COM
    /// error): the text is what was reached. A <c>PROTECTED</c> object anywhere → <c>Password</c> before any text call; offsets that
    /// do not match the text → <c>Unavailable</c>. Never logs text. MTA worker thread.
    /// </summary>
    public ReadResult ReadSelection(IntPtr fg, int maxChars = SelectionMaxChars, int context = SelectionExtend.Context)
    {
        if (!Begin(fg, out var op, 2 * SelectionWalkBudgetMs)) return Fail(ReadStatus.Unavailable, "skip");
        try { return ReadSelectionCore(fg, op.Pid, Math.Max(1, maxChars), Math.Max(0, context)); }
        finally { End(op); }
    }

    /// <summary>One paragraph of the walk: its length and its non-empty ranges (normalised, sorted); <see cref="Mark"/> = it
    /// reported a selection (nSelections &gt; 0), even an empty one.</summary>
    private sealed class Piece
    {
        public Piece(ILoParagraph p) => P = p;
        public ILoParagraph P { get; }
        public int N;
        public bool Mark;
        public readonly List<(int S, int E)> Ranges = new();
        public bool HasText => Ranges.Count > 0;
        /// <summary>Inside the selection: a non-empty range, or an empty paragraph with its mark (VM P0: an empty paragraph
        /// inside a selection reports [0,0], one outside reports no selection).</summary>
        public bool Inside => HasText || (N == 0 && Mark);
        public bool StartsAtZero => !HasText || Ranges[0].S == 0;
        public bool EndsAtN => !HasText || Ranges[^1].E == N;
    }

    /// <summary>Length, then the ranges: a backward selection is reported reversed ([54,3], VM P0) and is normalised; a range
    /// outside [0, n] is <see cref="BadRange"/> (nothing trusted).</summary>
    private static int Portions(Piece x)
    {
        int hr = x.P.CharacterCount(out x.N);
        if (hr < 0) return hr;
        hr = x.P.SelectionCount(out int ns);
        if (hr < 0) return hr;
        x.Mark = ns > 0;
        for (int i = 0; i < Math.Min(ns, MaxRanges); i++)
        {
            hr = x.P.Selection(i, out int a, out int b);
            if (hr < 0) return hr;
            int s = Math.Min(a, b), e = Math.Max(a, b);
            if (s < 0 || e > x.N) return BadRange;
            if (e > s) x.Ranges.Add((s, e));
        }
        x.Ranges.Sort((p, q) => p.S.CompareTo(q.S));
        return Ia2.S_OK;
    }

    /// <summary>The edge rule (VM P0): the focused paragraph holds an empty mark at offset 0 (n) — does the previous (next)
    /// paragraph's selection end (start) at the shared edge? An empty focused paragraph (n = 0, the cursor moved onto it by
    /// Shift+↓ or Shift+↑) asks the previous one, then the next one (review S2; to verify in the VM, LS3 / LS5). No text
    /// call.</summary>
    private static int EdgeSelection(ILoParagraph p, int c, out bool selection)
    {
        selection = false;
        int hr = p.CharacterCount(out int n);
        if (hr < 0) return hr;
        if (n < 0 || (c != 0 && c != n)) return Ia2.S_OK;
        if (n == 0)
        {
            hr = EdgeSide(p, next: false, out selection);
            if (selection || (hr < 0 && Classify(hr) is HrClass.Global or HrClass.ServerGone)) return hr;
            return EdgeSide(p, next: true, out selection);
        }
        return EdgeSide(p, next: c == n, out selection);
    }

    private static int EdgeSide(ILoParagraph p, bool next, out bool selection)
    {
        selection = false;
        int hr = p.Neighbour(next, out var nb);
        if (hr < 0 || nb is null) { nb?.Dispose(); return hr; }
        try
        {
            var x = new Piece(nb);
            hr = Portions(x);
            if (hr < 0) return hr;
            selection = x.HasText && (next ? x.Ranges[0].S == 0 : x.Ranges[^1].E == x.N);
            return Ia2.S_OK;
        }
        finally { nb.Dispose(); }
    }

    private ReadResult ReadSelectionCore(IntPtr fg, uint pid, int maxChars, int context)
    {
        double t0 = _b.NowMs();
        var e = AcquireFocused(pid, fg, out int state, out int hr, out _, acceptSelected: true);
        if (e is null) return Fail(ReadStatus.Unavailable, Src);
        var walked = new List<ILoParagraph>();
        try
        {
            // 1. password before any text call; paragraphs and headings only
            if ((state & Ia2.STATE_SYSTEM_PROTECTED) != 0) { Succeeded(fg); return Fail(ReadStatus.Password, Src); }
            if (!IsTextRole(e.P.Role)) return Fail(ReadStatus.Unavailable, Src);
            // 2. the focused paragraph's ranges (its caret only for the edge rule: -1 in a table block)
            hr = e.P.CaretOffset(out int c);
            if (hr < 0) return Fault(hr, pid, e);
            if ((state & Ia2.STATE_SYSTEM_FOCUSED) == 0) c = -1;   // a selected, unfocused start (LS8): no edge rule from it
            var focus = new Piece(e.P);
            hr = Portions(focus);
            if (hr < 0) return hr == BadRange ? Mismatch() : Fault(hr, pid, e);
            // an empty mark at the focused paragraph's edge: the selection lies on that side only, nothing of it here
            bool edgeBack = !focus.HasText && focus.Mark && focus.N > 0 && c == 0;
            bool edgeFwd = !focus.HasText && focus.Mark && focus.N > 0 && c == focus.N;
            if (!focus.Inside && !edgeBack && !edgeFwd)
            {
                if (_b.Foreground() != fg) return Fail(ReadStatus.Stale, Src);
                Succeeded(fg);
                return new ReadResult(ReadStatus.Ok, Src, TextContext.Invalid, 0, Selected: "");
            }

            // 3. extent pass (no text): backward while the earliest range starts at 0, forward while the last ends at n
            var pieces = new List<Piece>();
            if (focus.Inside) pieces.Add(focus);
            int hops = 0;
            string? partial = null;
            var cur = focus;
            bool more = focus.Inside ? focus.StartsAtZero : edgeBack;
            while (more && partial is null)
            {
                var step = Hop(cur, next: false, pid, t0, ref hops, walked, out var nb, out var stop);
                if (step is { } fail) return fail;
                partial = stop;
                if (nb is null) break;
                if (!(nb.Inside && nb.EndsAtN)) break;                   // the selection's start is found
                pieces.Insert(0, nb);
                cur = nb;
                more = nb.StartsAtZero;
            }
            // the start not reached (budget, a non-text object, a COM error): never show only the end of a selection
            // (plan LS8) — Unavailable, so the copy reads it when allowed, else nothing is shown
            if (partial is not null)
            {
                _log?.Invoke(FormattableString.Invariant($"selection ia2: {pieces.Count} paragraphs, {hops} hops, extent {_b.NowMs() - t0:0.0} ms, start not reached ({partial})"));
                return Fail(ReadStatus.Unavailable, Src);
            }
            // forward; once the cap is reached the end is cut anyway: no need to walk further
            int chars = Math.Max(0, pieces.Count - 1);   // the text pass's length: ranges + one line break between paragraphs
            foreach (var x in pieces) chars += Length(x);
            cur = focus;
            more = focus.Inside ? focus.EndsAtN : edgeFwd;
            while (more && partial is null && chars < maxChars)
            {
                var step = Hop(cur, next: true, pid, t0, ref hops, walked, out var nb, out var stop);
                if (step is { } fail) return fail;
                partial = stop;
                if (nb is null) break;
                if (!(nb.Inside && nb.StartsAtZero)) break;               // the selection's end is found
                pieces.Add(nb);
                chars += (pieces.Count > 1 ? 1 : 0) + Length(nb);
                cur = nb;
                more = nb.EndsAtN;
            }
            // the walk stopped at the cap while the selection goes on: the text is cut whatever the text pass finds
            bool stoppedAtCap = more && partial is null && chars >= maxChars;
            double tExtent = _b.NowMs();
            if (pieces.Count == 0 && partial is not null) return Fail(ReadStatus.Unavailable, Src);   // an edge mark, its side unreadable
            if (pieces.Count == 0)
            {
                // an edge mark whose neighbour holds nothing: no selection after all
                if (_b.Foreground() != fg) return Fail(ReadStatus.Stale, Src);
                Succeeded(fg);
                return new ReadResult(ReadStatus.Ok, Src, TextContext.Invalid, 0, Selected: "");
            }

            // 4. text pass, document order, clipped to the cap; every length must equal its offsets
            var sb = new StringBuilder();
            bool cut = false, midRange = false;   // midRange: the cap fell inside a range (maybe inside a word)
            bool OverBudget() => _b.NowMs() - t0 > SelectionWalkBudgetMs;
            for (int i = 0; i < pieces.Count && !cut; i++)
            {
                if (i > 0)
                {
                    if (sb.Length + 1 >= maxChars) { cut = true; break; }   // no room for text after a line break
                    sb.Append('\n');
                }
                for (int k = 0; k < pieces[i].Ranges.Count; k++)
                {
                    var (s, end) = pieces[i].Ranges[k];
                    if (k > 0)
                    {
                        // two ranges in one paragraph (a Ctrl+drag selection): never glue their words together
                        if (sb.Length + 1 >= maxChars) { cut = true; break; }   // no room for text after a line break
                        sb.Append('\n');
                    }
                    int left = maxChars - sb.Length;
                    if (left <= 0) { cut = true; break; }
                    if (OverBudget()) { cut = true; partial ??= "time"; break; }   // a slow LibreOffice: what was read so far
                    int want = Math.Min(end, s + left);
                    hr = pieces[i].P.Text(s, want, out string? t);
                    if (hr < 0) return Classify(hr) is HrClass.Global or HrClass.ServerGone ? Fault(hr, pid, e) : Fail(ReadStatus.Unavailable, Src);
                    if ((t ?? "").Length != want - s) return Mismatch();
                    // cut by the cap: never keep half of a surrogate pair (an emoji)
                    if (want < end && t!.Length > 0 && char.IsHighSurrogate(t[^1])) t = t[..^1];
                    sb.Append(t);
                    if (want < end) { cut = midRange = true; break; }
                }
            }
            cut |= stoppedAtCap;
            // 5. D26 context, in the first / last paragraph only
            string before = "", after = "";
            var first = pieces[0];
            if (first.HasText && first.Ranges[0].S > 0 && context > 0 && !OverBudget())
            {
                int s = first.Ranges[0].S, lo = Math.Max(0, s - context);
                hr = first.P.Text(lo, s, out string? t);
                if (hr < 0) return Classify(hr) is HrClass.Global or HrClass.ServerGone ? Fault(hr, pid, e) : Fail(ReadStatus.Unavailable, Src);
                if ((t ?? "").Length != s - lo) return Mismatch();
                before = t ?? "";
            }
            var last = pieces[^1];
            if (!cut && last.HasText && last.Ranges[^1].E < last.N && context > 0 && !OverBudget())
            {
                int s = last.Ranges[^1].E, hi = Math.Min(last.N, s + context);
                hr = last.P.Text(s, hi, out string? t);
                if (hr < 0) return Classify(hr) is HrClass.Global or HrClass.ServerGone ? Fault(hr, pid, e) : Fail(ReadStatus.Unavailable, Src);
                if ((t ?? "").Length != hi - s) return Mismatch();
                after = t ?? "";
            }
            double tText = _b.NowMs();
            _log?.Invoke(FormattableString.Invariant(
                $"selection ia2: {pieces.Count} paragraphs, {hops} hops, extent {tExtent - t0:0.0} ms, text {tText - tExtent:0.0} ms, ") +
                (partial is null ? (cut ? "complete (cut)" : "complete") : $"partial({partial})"));
            // 6. foreground re-check
            if (_b.Foreground() != fg) return Fail(ReadStatus.Stale, Src);
            Succeeded(fg);
            // cut inside a range: back to the last word boundary (D26 then has nothing after it to complete); cut at a range
            // or paragraph boundary: every word is whole already. Cut tells the reader either way
            string text = midRange ? SelectionPolicy.CutAtWord(sb.ToString()) : sb.ToString();
            return new ReadResult(ReadStatus.Ok, Src, TextContext.Invalid, 0, Before: before, Selected: text,
                                  After: cut ? "" : after, Cut: cut, Partial: partial is not null);
        }
        finally
        {
            // the walked neighbours' RCWs are released here, on this MTA thread; the focused entry keeps its lease logic
            foreach (var p in walked) p.Dispose();
            Release(e);
        }

        ReadResult Mismatch() { _log?.Invoke("ia2: offset mismatch (selection)"); return Fail(ReadStatus.Unavailable, Src); }
    }

    /// <summary>The characters a piece adds to the text: its ranges and a line break between two ranges.</summary>
    private static int Length(Piece x)
    {
        int n = Math.Max(0, x.Ranges.Count - 1);
        foreach (var (s, e) in x.Ranges) n += e - s;
        return n;
    }

    /// <summary>One hop of the walk: limits first (hops, time), then the neighbour, its state (PROTECTED → the whole read is
    /// <c>Password</c>), its role and its ranges. Returns a final result to give back at once, else null with
    /// <paramref name="nb"/> (null at the document's start / end, or when <paramref name="stop"/> says why the walk ends
    /// early: the read is then partial).</summary>
    private ReadResult? Hop(Piece from, bool next, uint pid, double t0, ref int hops, List<ILoParagraph> walked,
                            out Piece? nb, out string? stop)
    {
        nb = null;
        stop = null;
        if (hops >= SelectionMaxHops) { stop = "hops"; return null; }
        if (_b.NowMs() - t0 > SelectionWalkBudgetMs) { stop = "time"; return null; }
        hops++;
        int hr = from.P.Neighbour(next, out var p);
        if (p is not null) walked.Add(p);
        if (hr < 0)
        {
            // the server gone / the proxy missing keep their meaning; anything else ends the walk (copy fallback)
            if (Classify(hr) is HrClass.Global or HrClass.ServerGone) return Fault(hr, pid, null);
            stop = FormattableString.Invariant($"0x{hr:X8}");
            return null;
        }
        if (p is null) return null;   // first / last paragraph of this flow
        hr = p.State(out int st);
        if (hr < 0) { stop = FormattableString.Invariant($"0x{hr:X8}"); return null; }
        if ((st & Ia2.STATE_SYSTEM_PROTECTED) != 0) return Fail(ReadStatus.Password, Src);
        if (!IsTextRole(p.Role))
        {
            // review S2: a non-text object next to a selection that ends at the paragraph's edge is outside it unless
            // LibreOffice marks it selected (VM P0: selected paragraphs carry STATE_SYSTEM_SELECTED; to verify for frames /
            // tables in the VM, LS10). Not selected → the selection's edge is found (complete); selected → partial
            if ((st & Ia2.STATE_SYSTEM_SELECTED) == 0) return null;
            stop = FormattableString.Invariant($"role 0x{p.Role:X}");
            return null;
        }
        var x = new Piece(p);
        hr = Portions(x);
        if (hr == BadRange) return Fail(ReadStatus.Unavailable, Src);   // a range outside the paragraph: nothing trusted
        if (hr < 0) { stop = FormattableString.Invariant($"0x{hr:X8}"); return null; }
        nb = x;
        return null;
    }

    /// <summary>JIT the reader's methods off the critical path (a worker's warm-up, the hook path's): no COM call.</summary>
    public static void WarmUp()
    {
        const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                                                 System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        foreach (var m in typeof(LibreOfficeText).GetMethods(F))
            if (m.DeclaringType == typeof(LibreOfficeText) && !m.IsAbstract && !m.ContainsGenericParameters)
                try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle); } catch (Exception) { }
        _ = SettlePolicy.ShouldAsk(' ', false, false, false, false);
    }

    private static bool IsTextRole(int role) => role is Ia2.ROLE_PARAGRAPH or Ia2.ROLE_HEADING;

    private static ReadResult Fail(ReadStatus s, string src) => new(s, src, TextContext.Invalid, 0);

    // =====================================================================================================
    // resolve, cache, leases
    // =====================================================================================================

    private Entry? AcquireFocused(uint pid, IntPtr fg, out int state, out int hr) => AcquireFocused(pid, fg, out state, out hr, out _);

    private Entry? AcquireFocused(uint pid, IntPtr fg, out int state, out int hr, out bool faulted) =>
        AcquireFocused(pid, fg, out state, out hr, out faulted, acceptSelected: false);

    /// <summary>A cached or fresh entry whose object is focused (or PROTECTED); an unfocused one gets one re-resolve via the
    /// walk. null → Unavailable (<paramref name="faulted"/>: a COM failure, already classified). <paramref name="acceptSelected"/>
    /// (phase 2, VM LS8): a paragraph / heading that is not focused but <c>SELECTED</c> is accepted too — after Ctrl+A on a
    /// long document Writer moves the cursor to an off-screen paragraph without a focus event, and the walk ends on a
    /// paragraph that is still selected; the caller then answers only « a selection » from it, never caret text.</summary>
    private Entry? AcquireFocused(uint pid, IntPtr fg, out int state, out int hr, out bool faulted, bool acceptSelected)
    {
        state = 0;
        faulted = false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var e = Acquire(pid, fg, forceWalk: attempt == 1, out bool fresh, out hr);
            if (e is null)
            {
                if (hr < 0) { Fault(hr, pid, null); faulted = true; }
                return null;
            }
            hr = e.P.State(out state);
            if (hr < 0) { Fault(hr, pid, e); Release(e); faulted = true; return null; }
            if ((state & (Ia2.STATE_SYSTEM_PROTECTED | Ia2.STATE_SYSTEM_FOCUSED)) != 0) return e;
            // only an object the accFocus walk found (review S3–S5): never the event slot's stale, cached object
            if (acceptSelected && (attempt == 1 || e.Walked) && (state & Ia2.STATE_SYSTEM_SELECTED) != 0 && IsTextRole(e.P.Role)) return e;
            Drop(e);
            Release(e);
            if (fresh && e.Walked) break;   // the walk itself found nothing focused: no second walk
        }
        state = 0;
        hr = Ia2.S_FALSE;
        return null;
    }

    /// <summary>The cached entry for (pid, fg, current seq) with one more lease, else a fresh resolve (the focus slot of
    /// this window → AccessibleObjectFromEvent; no slot or <paramref name="forceWalk"/> → the accFocus walk).</summary>
    private Entry? Acquire(uint pid, IntPtr fg, bool forceWalk, out bool fresh, out int hr)
    {
        fresh = false;
        FocusSlot slot;
        lock (_slotLock) slot = _slot;
        long seq = slot.Seq;
        ILoParagraph? free = null;
        lock (_lock)
        {
            var c = _entry;
            if (c is not null && !forceWalk && c.Pid == pid && c.Fg == fg && c.Seq == seq)
            {
                c.Leases++;
                hr = Ia2.S_OK;
                return c;
            }
            if (c is not null && !(c.Pid == pid && c.Fg == fg && c.Seq == seq && !forceWalk)) free = DetachLocked();
        }
        free?.Dispose();

        bool walk = forceWalk || slot.Root != fg || slot.Hwnd == IntPtr.Zero;
        ILoParagraph? p;
        hr = walk ? _b.FromFocusWalk(fg, out p) : _b.FromEvent(slot.Hwnd, slot.ObjectId, slot.ChildId, out p);
        if (hr < 0 || p is null) { p?.Dispose(); if (hr >= 0) hr = Ia2.E_FAIL; return null; }
        fresh = true;

        var e = new Entry(pid, fg, seq, p, walk);
        lock (_lock)
        {
            if (pid == _pid) _structural = 0;   // a resolve that reached the object: the structural run is over (review S2)
            if (pid == _pid && !_off && !_globalOff && Volatile.Read(ref _seq) == seq)
            {
                free = DetachLocked();
                _entry = e;
                e.Leases = 2;   // the cache's own + the caller's
            }
            else
            {
                free = null;
                e.Leases = 1;   // superseded while resolving: the caller's only
            }
        }
        free?.Dispose();
        return e;
    }

    private void Release(Entry e)
    {
        bool dispose;
        lock (_lock) dispose = --e.Leases == 0;
        if (dispose) e.P.Dispose();
    }

    /// <summary>Removes the entry from the cache (stale or defunct); the caller still holds its lease.</summary>
    private void Drop(Entry e)
    {
        bool dispose = false;
        lock (_lock)
        {
            if (ReferenceEquals(_entry, e)) { _entry = null; dispose = --e.Leases == 0; }
        }
        if (dispose) e.P.Dispose();
    }

    /// <summary>Under <see cref="_lock"/>: detaches the cached entry; returns its paragraph if no one else holds it.</summary>
    private ILoParagraph? DetachLocked()
    {
        var c = _entry;
        if (c is null) return null;
        _entry = null;
        return --c.Leases == 0 ? c.P : null;
    }

    // =====================================================================================================
    // breaker, budgets
    // =====================================================================================================

    /// <param name="TimeoutMs">past this an operation counts as one timeout (a read's budget; a selection walk's is longer).</param>
    /// <param name="Trial">the half-open breaker's one trial call after a cooldown (S2b).</param>
    private readonly record struct Op(long Id, uint Pid, double Start, int TimeoutMs, bool Trial = false);

    private bool Begin(IntPtr fg, out Op op) => Begin(fg, out op, 0);

    /// <summary>Starts an IA2 operation: false when disabled, off, or no pid. A new pid starts afresh (and logs its
    /// version once); an older call still in flight past the stuck limit switches the pid off. <paramref name="budgetMs"/>
    /// &gt; 0: a long operation (the selection read) that counts as a timeout only past it; the stuck limit is unchanged. Off for a cooldown (S2b): false until it is over, then — once no call of the pid is in flight — exactly
    /// one trial call goes through (the others still get false while it runs).</summary>
    private bool Begin(IntPtr fg, out Op op, int budgetMs)
    {
        op = default;
        if (!_enabled || fg == IntPtr.Zero) return false;
        uint pid = _b.PidOf(fg);
        if (pid == 0) return false;
        double now = _b.NowMs();
        bool newPid = false, ok = false;
        ILoParagraph? free = null, stale = null;
        string? offLog = null;
        int timeoutMs = budgetMs > 0 ? Math.Max(_timeoutMs, budgetMs) : _timeoutMs;
        lock (_lock)
        {
            if (_globalOff) return false;
            if (pid != _pid)
            {
                _pid = pid;
                _off = false;
                _timeouts = _structural = _episodes = 0;
                _offUntil = 0;
                _onAgainAt = now;
                _trial = 0;
                _pidStart = now;
                _lastTimeoutEnd = double.MinValue;
                _live = IntPtr.Zero;
                _probe = default;
                _caret = default;
                free = DetachLocked();
                newPid = true;
            }
            bool trial = false;
            if (_off)
            {
                // a trial stuck past the limit is the next episode at once (its cooldown starts now)
                if (_trial != 0 && _inFlight.TryGetValue(_trial, out var t) && now - t.Start > _stuckMs)
                {
                    _trial = 0;
                    offLog = OffLocked("stuck");
                }
                // after the cooldown, no trial while any call of the pid is still in flight (never one behind a stuck call)
                bool busy = _trial != 0;
                foreach (var f in _inFlight.Values) busy |= f.Pid == pid;
                trial = now >= _offUntil && !busy;
            }
            else
            {
                foreach (var f in _inFlight.Values)
                    if (f.Pid == pid && now - f.Start > _stuckMs) { offLog = OffLocked("stuck"); break; }
                if (_off) stale = DetachLocked();
            }
            if (!_off || trial)
            {
                op = new Op(++_nextCall, pid, now, timeoutMs, trial);
                _inFlight[op.Id] = new InFlight(pid, now);
                if (trial) _trial = op.Id;
                ok = true;
            }
        }
        free?.Dispose();
        stale?.Dispose();
        if (offLog is not null) _log?.Invoke(offLog);
        if (newPid) _log?.Invoke($"ia2: LibreOffice {_b.ProductVersion(pid) ?? "?"} pid {pid}");
        return ok;
    }

    /// <summary>Ends an operation: past the stuck limit → off; past the read budget → one timeout (3 in a row → off);
    /// within budget → the timeout run is reset. VM run 2026-11-27: right after Pépite started, LibreOffice answered its
    /// first IA2 calls in 300–420 ms (the bridge waking up), three of them at once — counted as three timeouts, which
    /// switched IA2 off for the whole session. So a slow call in the pid's first <see cref="PidGraceMs"/> is not counted
    /// (a stuck call still is), and calls that overlapped one slow episode count once: a timeout counts only when its
    /// call started after the previous counted one ended. The trial call (S2b): in time → on again, counters reset;
    /// stuck or slow → the next episode.</summary>
    private void End(Op op)
    {
        if (op.Id == 0) return;
        double now = _b.NowMs(), ms = now - op.Start;
        string? offLog = null, slowLog = null, onLog = null;
        ILoParagraph? free = null;
        lock (_lock)
        {
            _inFlight.Remove(op.Id);
            if (op.Pid != _pid) return;
            if (op.Trial)
            {
                if (_trial != op.Id) return;   // already judged stuck by a Begin: that was the next episode
                _trial = 0;
                if (ms > _stuckMs) offLog = OffLocked("stuck");
                else if (ms > op.TimeoutMs) offLog = OffLocked(FormattableString.Invariant($"slow trial, {ms:0} ms"));
                else
                {
                    _off = false;
                    _timeouts = _structural = 0;
                    _lastTimeoutEnd = double.MinValue;
                    _onAgainAt = now;
                    onLog = FormattableString.Invariant($"ia2: on again for pid {_pid} after {(now - _offSince) / 1000:0} s");
                }
            }
            else if (_off) return;
            else if (ms > _stuckMs) offLog = OffLocked("stuck");
            else if (ms > op.TimeoutMs)
            {
                if (op.Start - _pidStart < PidGraceMs) slowLog = FormattableString.Invariant($"ia2: slow call at start, {ms:0} ms (not counted)");
                else if (op.Start >= _lastTimeoutEnd)
                {
                    _lastTimeoutEnd = now;
                    if (++_timeouts >= TimeoutsToOff) offLog = OffLocked($"{TimeoutsToOff} timeouts");
                }
            }
            // within the caret-read budget: the timeout run is over. A longer operation (the selection read, its own
            // allowance) that took more than a caret read may neither count as a timeout nor end the run (review S2)
            else if (ms <= _timeoutMs) _timeouts = 0;
            if (_off) free = DetachLocked();
        }
        free?.Dispose();
        if (slowLog is not null) _log?.Invoke(slowLog);
        if (offLog is not null) _log?.Invoke(offLog);
        if (onLog is not null) _log?.Invoke(onLog);
    }

    /// <summary>A slow IA2 call within this long of a pid's first call is not counted as a timeout (the bridge wakes up).</summary>
    public const int PidGraceMs = 5000;
    private double _pidStart, _lastTimeoutEnd = double.MinValue;   // guarded by _lock

    /// <summary>S2b, the half-open breaker: the first timed-off episode of a pid lasts this long, each next one twice as long;
    /// the <see cref="EpisodesToPermanent"/>th is permanent. VM P0 (F1): a call issued while « Enregistrer sous » is being
    /// created blocks 0.15–1.1 s, and LibreOffice answers in &lt; 1 ms again once the dialog is in front (≤ 2.1 s after the
    /// chord); the WR5 run had switched IA2 off for the whole session there.</summary>
    public const int CooldownMs = 10_000, EpisodesToPermanent = 4;
    /// <summary>After this long on without an episode, the episode count starts again — measured from the last time IA2
    /// came back on (the pid's start or a successful trial), never across failed trials. Intended (owner review S2): a
    /// stall now and then must never cost IA2 for the session; only <see cref="EpisodesToPermanent"/> episodes close
    /// together do.</summary>
    public const int EpisodeWindowMs = 10 * 60_000;
    private double _offUntil, _offSince;   // guarded by _lock: end of the cooldown (+∞ = permanent), start of the episode
    private double _onAgainAt;              // guarded: when IA2 last went on (pid start or a successful trial)
    private int _episodes;                  // guarded: timed-off episodes of the current pid
    private long _trial;                    // guarded: the trial call in flight (0 = none)

    /// <summary>Under <see cref="_lock"/>: switches the current pid off; returns the log line. A stuck call and 3 timeouts
    /// are timed (S2b: <see cref="CooldownMs"/>, doubling per episode, then permanent); <paramref name="permanent"/> (5
    /// structural failures: no usable IA2 there) is for the pid's life.</summary>
    private string OffLocked(string why, bool permanent = false)
    {
        bool wasOn = !_off;   // a failed trial (already off) is the next episode, never a calm spell
        _off = true;
        _live = IntPtr.Zero;
        _probe = default;
        _caret = default;
        double now = _b.NowMs();
        _offSince = now;
        if (!permanent && wasOn && _episodes > 0 && now - _onAgainAt >= EpisodeWindowMs) _episodes = 0;   // a calm spell: start again
        if (!permanent && ++_episodes < EpisodesToPermanent)
        {
            int ms = CooldownMs << (_episodes - 1);
            _offUntil = now + ms;
            return FormattableString.Invariant($"ia2: off for pid {_pid} ({why}), retry in {ms / 1000} s");
        }
        _offUntil = double.PositiveInfinity;
        return $"ia2: off for pid {_pid} ({why})";
    }

    private void Succeeded(IntPtr fg)
    {
        lock (_lock) { _structural = 0; if (!_off && !_globalOff) _live = fg; }
    }

    /// <summary>What a failed COM call means. Structural (no IAccessibleText / IAccessible2) counts toward the breaker;
    /// the proxy not registered switches IA2 off everywhere; the server gone forgets the pid; anything else (busy,
    /// defunct object, E_FAIL) only drops the cache. Always <c>Unavailable</c>.</summary>
    private ReadResult Fault(int hr, uint pid, Entry? e)
    {
        if (e is not null) Drop(e);
        string? log = null;
        ILoParagraph? free = null;
        lock (_lock)
        {
            switch (Classify(hr))
            {
                case HrClass.Global:
                    if (!_globalOff)
                    {
                        _globalOff = true;
                        _live = IntPtr.Zero;
                        _probe = default;
                        _caret = default;
                        log = $"ia2: off (proxy not registered, 0x{hr:X8})";
                    }
                    free = DetachLocked();
                    break;
                case HrClass.Structural:
                    if (pid == _pid && !_off && ++_structural >= StructuralToOff)
                    {
                        log = OffLocked($"{StructuralToOff} structural failures", permanent: true);
                        free = DetachLocked();
                    }
                    break;
                case HrClass.ServerGone:
                    if (pid == _pid)
                    {
                        log = $"ia2: pid {pid} forgotten (0x{hr:X8})";
                        _pid = 0;
                        _live = IntPtr.Zero;
                        _probe = default;
                        _caret = default;
                        free = DetachLocked();
                    }
                    break;
            }
        }
        free?.Dispose();
        if (log is not null) _log?.Invoke(log);
        return Fail(ReadStatus.Unavailable, Src);
    }

    internal enum HrClass { Ok, Normal, Structural, Global, ServerGone }

    internal static HrClass Classify(int hr) => hr switch
    {
        >= 0 => HrClass.Ok,
        Ia2.REGDB_E_IIDNOTREG => HrClass.Global,
        Ia2.E_NOINTERFACE => HrClass.Structural,
        Ia2.RPC_E_SERVER_DIED or Ia2.RPC_E_SERVER_DIED_DNE or Ia2.RPC_S_SERVER_UNAVAILABLE => HrClass.ServerGone,
        _ => HrClass.Normal,   // RPC_E_CALL_REJECTED, CO_E_OBJNOTCONNECTED, RPC_E_DISCONNECTED, E_FAIL, …
    };

    /// <summary>
    /// Shutdown (the controller's Dispose, on the UI thread: STA). The cached paragraph's RCWs were obtained on MTA workers
    /// and must not be released from an STA thread (review S2: the release would have to marshal into the MTA, or block
    /// on a busy LibreOffice), so the release is handed to a thread-pool thread (MTA) and not waited for; if the process
    /// exits first, COM's own rundown releases the proxies. Paragraphs still leased by a worker are released by it. The
    /// caret worker is stopped.
    /// </summary>
    public void Dispose()
    {
        ILoParagraph? free;
        lock (_lock) { free = DetachLocked(); _live = IntPtr.Zero; _disposed = true; }
        if (free is not null) _onMta(() => free.Dispose());
        lock (_caretGate) _caretSignal?.Set();
        if (ReferenceEquals(Shared, this)) Shared = null;
    }

    // =====================================================================================================

    private readonly record struct FocusSlot(IntPtr Root, IntPtr Hwnd, int ObjectId, int ChildId, long Seq);
    private readonly record struct InFlight(uint Pid, double Start);
    private readonly record struct ProbeCache(IntPtr Fg, uint Pid, long Seq, ProbeStatus Status);
    private readonly record struct Unfound(IntPtr Fg, long Seq, int Count);
    private readonly record struct CaretCache(IntPtr Fg, long Seq, int X, int Y, double Tick);

    private sealed class Entry
    {
        public Entry(uint pid, IntPtr fg, long seq, ILoParagraph p, bool walked)
        {
            Pid = pid; Fg = fg; Seq = seq; P = p; Walked = walked;
        }
        public uint Pid { get; }
        public IntPtr Fg { get; }
        public long Seq { get; }
        public ILoParagraph P { get; }
        public bool Walked { get; }
        public int Leases;   // guarded by LibreOfficeText._lock
    }
}

/// <summary>
/// When to ask a settle read in a live Writer window (D23 after LibreOffice's own AutoCorrect / word completion, which run
/// on these keys): a typed separator or Enter, <see cref="DelayMs"/> later — never while our auto-space is armed (a seed
/// would disarm the D22 fix), a D22 read-back is pending, or a ≈ / negation target is armed for the current version.
/// Pure (S6 wires it into the hook path).
/// </summary>
internal static class SettlePolicy
{
    public const int DelayMs = 120;

    /// <summary>White space (Enter included), or punctuation except the apostrophes and the hyphen (inside words).</summary>
    public static bool IsSeparator(char c) =>
        char.IsWhiteSpace(c) || (char.IsPunctuation(c) && c is not ('\'' or '’' or '-'));

    public static bool ShouldAsk(bool live, bool autoSpaceArmed, bool readBackPending, bool correctionArmed) =>
        live && !autoSpaceArmed && !readBackPending && !correctionArmed;

    /// <summary>After a typed character.</summary>
    public static bool ShouldAsk(char typed, bool live, bool autoSpaceArmed, bool readBackPending, bool correctionArmed) =>
        IsSeparator(typed) && ShouldAsk(live, autoSpaceArmed, readBackPending, correctionArmed);

    /// <summary>
    /// Does the document (a settle read) say what the keys say? The same word before the caret, nothing after it, the same
    /// context words — ignoring the case and the apostrophe's form: LibreOffice's AutoCorrect capitalises a sentence's
    /// first word (« jtaime␣ » → « Jtaime␣ ») and turns ' into ’, and a seed for that alone would forget the finished word
    /// (its ≈ / « ne » marker gone, the negation tokens « rebuilt »), while the correction read already takes the
    /// document's case and apostrophe. Any other difference (a letter, a length) re-seeds.
    /// </summary>
    public static bool Agrees(string docPrefix, string docSuffix, IReadOnlyList<string> docContext, string current, IReadOnlyList<string> context)
    {
        if (docSuffix.Length != 0 || !Same(docPrefix, current) || docContext.Count != context.Count) return false;
        for (int i = 0; i < context.Count; i++) if (!Same(docContext[i], context[i])) return false;
        return true;
    }

    private static bool Same(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            char x = a[i], y = b[i];
            if (x == y) continue;
            if (x is '\'' or '’' && y is '\'' or '’') continue;
            if (char.ToLowerInvariant(x) != char.ToLowerInvariant(y)) return false;
        }
        return true;
    }
}
