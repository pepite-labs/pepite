using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Predict.Keyboard;

/// <summary>
/// Reads the text around the caret of the foreground app after a caret jump, on the dedicated MTA worker
/// of a <see cref="ReadQueue"/> (UI Automation clients must not run on a UI thread; the hook thread must
/// never wait). Win32 <c>Edit</c> controls (Notepad) are read with EM_GETSEL + WM_GETTEXT, each bounded by
/// SendMessageTimeout, which give the exact UTF-16 indices one Backspace/Delete removes; everything else
/// (Chromium, Word) with UI Automation TextPattern on the focused element.
/// Password fields are detected before any text is requested and their content is never read.
/// The window read is widened up to the caret's sentence bounds (at most <see cref="EditSession.BeforeMax"/> characters
/// before, <see cref="EditSession.AfterMax"/> after) and returned as a <see cref="TextWindow"/> for the D23 re-read.
/// </summary>
internal sealed class CaretTextReader : IDisposable
{
    public const int Win32MsgTimeoutMs = 50, StuckMs = 2000;

    private readonly ReadQueue _queue;
    private readonly Action<string>? _log;

    public CaretTextReader(Func<long, bool> isCurrent, Action<ReadRequest, ReadResult> completed, Action<string>? log)
    {
        _log = log;
        _queue = new ReadQueue(isCurrent, completed, fg => Read(fg, _log), WarmUp, log, StuckMs);
    }

    /// <summary>Id of the most recent request (a result for an older one does not change the password state).</summary>
    public long LatestId => _queue.LatestId;

    /// <summary>Hook-thread safe: a tiny lock and an event set, no I/O, never waits.</summary>
    public void Request(long version, IntPtr fg, int delayMs, string reason) => _queue.Request(version, fg, delayMs, reason);

    /// <summary>The first UIA call loads the client and talks to the core; JIT and touch it once off the
    /// critical path so the first real read fits in the budget. A replacement worker (generation &gt; 0)
    /// exists because a provider hung: it does not touch UIA here (that could hang it unseen too).</summary>
    private void WarmUp(int generation)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var name in new[] { nameof(Read), nameof(ReadWin32), nameof(ReadUia) })
            {
                var m = typeof(CaretTextReader).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (m != null) RuntimeHelpers.PrepareMethod(m.MethodHandle);
            }
            ContextExtractor.Extract("la mai", 6, false, false);
            _ = EditSession.Begin(TextWindow.Of("Le chat dort. Il pleut", 4, false, false), IntPtr.Zero).Snapshot().ModifiedWord;
            _ = EditClassifier.Classify(EditSession.Begin(TextWindow.Of("Le chat.", 2, false, false), IntPtr.Zero).Snapshot(),
                                        TextWindow.Of("Le chat.", 3, false, false), true);
        }
        catch (Exception ex) { _log?.Invoke($"reader warm-up error: {ex.GetType().Name}"); }
        if (generation == 0) WarmUpUiaRead(_log);
        _log?.Invoke($"reader warm-up (generation {generation}): {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// The first UIA read pays for the client's COM interop (text-range wrappers built on first use) and, in Word, for
    /// the provider's first answers: 383 ms in the owner's Word run, over the 150 ms budget, so the first edit after a
    /// start was never re-read. Exercise the same calls a read makes, once, on whatever is focused — every step on its
    /// own (a failing FocusedElement must not skip the rest, as it did), the password check first, and GetText(0):
    /// no text is ever read here. Worker thread, counted as busy by the queue (a hung provider is replaced).
    /// </summary>
    private static void WarmUpUiaRead(Action<string>? log)
    {
        _ = TextPattern.Pattern;
        AutomationElement? el = null;
        for (int i = 0; i < 2 && el is null; i++)
        {
            try { el = AutomationElement.FocusedElement; }
            catch (Exception ex) { log?.Invoke($"reader warm-up: focused element {ex.GetType().Name}"); System.Threading.Thread.Sleep(100); }
        }
        try
        {
            if (el is null || el.Current.IsPassword || !el.TryGetCurrentPattern(TextPattern.Pattern, out var o)) return;
            var tp = (TextPattern)o;
            var sel = tp.GetSelection();
            if (sel.Length != 1) return;
            var r = sel[0].Clone();
            var doc = tp.DocumentRange;
            _ = r.CompareEndpoints(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.Start);
            _ = r.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, 0);
            r.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start);
            _ = r.GetText(0);   // zero characters: the call path only
        }
        catch (Exception ex) { log?.Invoke($"reader warm-up: text pattern {ex.GetType().Name}"); }
    }

    /// <summary>The same read as a caret jump's (password check first, window widened to the sentence), for the sentence
    /// echo after an edit (D17 amendment); worker thread of the caller's queue.</summary>
    internal static ReadResult ReadAround(IntPtr fg, Action<string>? log) => Read(fg, log);

    private static ReadResult Read(IntPtr fg, Action<string>? log)
    {
        uint tid = Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return Fail(ReadStatus.Unavailable, "own");   // tray menu / À propos
        var gi = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        IntPtr focus = Native.GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;

        ReadResult r;
        if (focus != IntPtr.Zero && string.Equals(ClassOf(focus), "Edit", StringComparison.OrdinalIgnoreCase))
            r = ReadWin32(focus);
        else
            r = ReadUia(pid, log);

        if (Native.GetForegroundWindow() != fg && r.Status != ReadStatus.Password)
            r = r with { Status = ReadStatus.Stale };
        return r;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        int n = Native.GetClassName(h, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : "";
    }

    private static ReadResult Fail(ReadStatus s, string src) => new(s, src, TextContext.Invalid, 0);

    /// <summary>Classic Edit control. Only <c>Edit</c>: RichEdit's text messages need in-process buffers.</summary>
    private static ReadResult ReadWin32(IntPtr h)
    {
        const string src = "win32";
        const uint flags = Native.SMTO_ABORTIFHUNG | Native.SMTO_ERRORONEXIT;
        long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64();
        if ((style & Native.ES_PASSWORD) != 0) return Fail(ReadStatus.Password, src);   // no text message sent

        if (Native.SendMessageTimeout(h, Native.EM_GETSEL, IntPtr.Zero, IntPtr.Zero, flags, Win32MsgTimeoutMs, out var sel) == IntPtr.Zero)
            return Fail(ReadStatus.Unavailable, src);
        long v = sel.ToInt64();
        if ((int)v == -1) return Fail(ReadStatus.Unavailable, src);   // > 64 K chars: positions don't fit
        int start = (int)(v & 0xFFFF), end = (int)((v >> 16) & 0xFFFF);
        if (start != end) return Fail(ReadStatus.Selection, src);

        // WM_GETTEXT copies from the start of the text: the widest window costs nothing more
        int n = start + EditSession.AfterMax + 2;
        var sb = new StringBuilder(n);
        if (Native.SendMessageTimeout(h, Native.WM_GETTEXT, (IntPtr)n, sb, flags, Win32MsgTimeoutMs, out var got) == IntPtr.Zero)
            return Fail(ReadStatus.Unavailable, src);
        int copied = (int)got.ToInt64();
        if (copied < start || copied > n - 1) return Fail(ReadStatus.Unavailable, src);
        string all = sb.ToString();
        if (all.Length < copied) return Fail(ReadStatus.Unavailable, src);

        int lo = Math.Max(0, start - EditSession.BeforeMax);
        string text = all.Substring(lo, copied - lo);
        bool truncEnd = copied == n - 1;
        var ctx = ContextExtractor.Extract(text, start - lo, lo > 0, truncEnd);
        return ctx.Valid ? new ReadResult(ReadStatus.Ok, src, ctx, 0, null, TextWindow.Of(text, start - lo, lo > 0, truncEnd, lo))
                         : Fail(ReadStatus.Unavailable, src);
    }

    /// <summary>UI Automation on the focused element only (never its ancestors: Chromium's document also
    /// has a TextPattern, with the wrong caret). Characters + our tokenizer, not TextUnit.Word (Word and
    /// Chromium disagree on word boundaries).</summary>
    private static ReadResult ReadUia(uint fgPid, Action<string>? log)
    {
        const string src = "uia";
        long t0 = Stopwatch.GetTimestamp(), t1 = 0, t2 = 0, t3 = 0;   // phases, logged when the read is slow (owner run 2)
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el is null) return Fail(ReadStatus.Unavailable, src);
            // Chromium's in-page elements report ProcessId 0 (unknown): then the foreground re-check in
            // Read() is the guard; a known pid must be the foreground window's.
            int elPid = el.Current.ProcessId;
            if (elPid != 0 && elPid != (int)fgPid) return Fail(ReadStatus.Unavailable, src);
            if (el.Current.IsPassword) return Fail(ReadStatus.Password, src);   // before any pattern call
            // 0.8.3: a web terminal (xterm.js) is never read nor tracked, like a password field
            if (KeyboardPolicy.IsWebTerminal(el.Current.ClassName, el.Current.AutomationId)) return Fail(ReadStatus.Password, src);
            if (!el.TryGetCurrentPattern(TextPattern.Pattern, out var o)) return Fail(ReadStatus.Unavailable, src);
            var tp = (TextPattern)o;
            t1 = Stopwatch.GetTimestamp();
            var sel = tp.GetSelection();
            if (sel.Length != 1) return Fail(ReadStatus.Unavailable, src);
            var s0 = sel[0];
            if (s0.CompareEndpoints(TextPatternRangeEndpoint.Start, s0, TextPatternRangeEndpoint.End) != 0)
                return Fail(ReadStatus.Selection, src);

            t2 = Stopwatch.GetTimestamp();
            const int W = ContextExtractor.WindowChars, A = ContextExtractor.AfterChars;
            // Chromium lets a field's range move past the field into the rest of the page (other inputs,
            // body text): clamp both ends to the focused element's own document range.
            var doc = tp.DocumentRange;
            var b = s0.Clone();
            bool truncStart = b.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -W) == -W;
            if (b.CompareEndpoints(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.Start) < 0)
            { b.MoveEndpointByRange(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.Start); truncStart = false; }
            var a = s0.Clone();
            bool truncEnd = a.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, A + 1) == A + 1;
            if (a.CompareEndpoints(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.End) > 0)
            { a.MoveEndpointByRange(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.End); truncEnd = false; }
            // Whole ranges (-1): they are already bounded by the moves above. A provider "Character" may be
            // several UTF-16 units (emoji, ZWJ sequences, graphemes), so a UTF-16 cap would cut the text
            // next to the caret and shift every index.
            string before = b.GetText(-1);
            string after = a.GetText(-1);
            t3 = Stopwatch.GetTimestamp();
            bool widened = false;

            // D23: widen to the sentence bounds (a second, longer move only when the first window cut the sentence)
            if (truncStart && !HasStop(before))
            {
                const int more = EditSession.BeforeMax - W;
                widened = true;
                truncStart = b.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -more) == -more;
                if (b.CompareEndpoints(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.Start) < 0)
                { b.MoveEndpointByRange(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.Start); truncStart = false; }
                before = b.GetText(-1);
            }
            if (truncEnd && !HasStop(after))
            {
                const int more = EditSession.AfterMax - A;
                widened = true;
                truncEnd = a.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, more) == more;
                if (a.CompareEndpoints(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.End) > 0)
                { a.MoveEndpointByRange(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.End); truncEnd = false; }
                after = a.GetText(-1);
            }

            long t4 = Stopwatch.GetTimestamp();
            double f = 1000.0 / Stopwatch.Frequency;
            if ((t4 - t0) * f > 40)
                log?.Invoke(FormattableString.Invariant(
                    $"uia read phases: focus+pattern {(t1 - t0) * f:0} ms, selection {(t2 - t1) * f:0} ms, window {(t3 - t2) * f:0} ms, widen {(t4 - t3) * f:0} ms{(widened ? " (widened)" : "")}"));
            var ctx = ContextExtractor.ExtractAround(before, after, truncStart, truncEnd);
            return ctx.Valid ? new ReadResult(ReadStatus.Ok, src, ctx, 0, null, TextWindow.Of(before + after, before.Length, truncStart, truncEnd))
                             : Fail(ReadStatus.Unavailable, src);
        }
        catch (Exception ex)
        {
            log?.Invoke($"uia read error: {ex.GetType().Name}: {ex.Message}");   // never text content
            return Fail(ReadStatus.Unavailable, src);
        }
    }

    /// <summary>The text holds a sentence end or a line break (a sentence bound of <see cref="Predict.Core.Tokenizer"/>),
    /// or a control character that <see cref="TextWindow.Of"/> turns into one.</summary>
    private static bool HasStop(string s)
    {
        foreach (var c in s)
            if (Predict.Core.Tokenizer.IsSentenceEnd(c) || Predict.Core.Tokenizer.IsBreak(c) || (char.IsControl(c) && c != '\t') || c == '\uFFFC') return true;
        return false;
    }

    /// <summary>Warm-up of a second UIA worker (the read-back queue): load the client, read nothing.</summary>
    public static void WarmUpUia(int generation)
    {
        if (generation != 0) return;
        try
        {
            RuntimeHelpers.PrepareMethod(typeof(CaretTextReader).GetMethod(nameof(ReadBeforeCaret))!.MethodHandle);
            _ = AutomationElement.FocusedElement;
            _ = TextPattern.Pattern;
        }
        catch (Exception) { }
    }

    /// <summary>D22 read-back (Word): the last <paramref name="chars"/> characters before a collapsed caret of the
    /// focused element, raw (UIA). Same guards as a read: foreground pid, password field never read.</summary>
    public static ReadResult ReadBeforeCaret(IntPtr fg, int chars, Action<string>? log)
    {
        const string src = "uia";
        try
        {
            Native.GetWindowThreadProcessId(fg, out uint pid);
            var el = AutomationElement.FocusedElement;
            if (el is null) return Fail(ReadStatus.Unavailable, src);
            int elPid = el.Current.ProcessId;
            if (elPid != 0 && elPid != (int)pid) return Fail(ReadStatus.Unavailable, src);
            if (el.Current.IsPassword) return Fail(ReadStatus.Password, src);
            if (!el.TryGetCurrentPattern(TextPattern.Pattern, out var o)) return Fail(ReadStatus.Unavailable, src);
            var tp = (TextPattern)o;
            var sel = tp.GetSelection();
            if (sel.Length != 1) return Fail(ReadStatus.Unavailable, src);
            var s0 = sel[0];
            if (s0.CompareEndpoints(TextPatternRangeEndpoint.Start, s0, TextPatternRangeEndpoint.End) != 0)
                return Fail(ReadStatus.Selection, src);
            var b = s0.Clone();
            b.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -chars);
            string before = b.GetText(-1);
            if (Native.GetForegroundWindow() != fg) return Fail(ReadStatus.Stale, src);
            return new ReadResult(ReadStatus.Ok, src, TextContext.Invalid, 0, before);
        }
        catch (Exception ex)
        {
            log?.Invoke($"uia read-back error: {ex.GetType().Name}: {ex.Message}");
            return Fail(ReadStatus.Unavailable, src);
        }
    }

    public void Dispose() => _queue.Dispose();
}
