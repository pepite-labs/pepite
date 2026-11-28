using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Predict.Keyboard;

/// <summary>
/// Reads the selected text of the foreground window for the reading mode (plan 07, D21), on its own MTA worker (a
/// <see cref="ReadQueue"/>: latest request wins, a request overtaken by typing / another jump is Stale, a hung provider is
/// abandoned). Asked by the controller when a caret read reported a selection (click / drag / double click, Shift +
/// navigation, Ctrl+A, window switch). Password fields are checked before any text is requested (Win32 <c>ES_PASSWORD</c>,
/// UIA <c>IsPassword</c>). Win32 <c>Edit</c>: EM_GETSEL + WM_GETTEXT; others: UIA <c>TextPattern.GetSelection</c> on the
/// focused element (or, when it has no text pattern, its nearest ancestor that has one: a selection in a web page's
/// static text). LibreOffice phase 2: a Writer document window (never one of its dialogs) is read through IAccessible2
/// (<see cref="LibreOfficeText.ReadSelection"/>); its other windows are not read. At most <see cref="MaxChars"/> characters
/// (≈ 1000 words and more). The simulated Ctrl+C fallback (<see cref="ClipboardCopy"/>) runs only when the request allows
/// it and the direct read found no text pattern, or IA2 read only part of the selection.
/// </summary>
internal sealed class SelectionReader : IDisposable
{
    public const int MaxChars = 20000, TimeoutMs = 1500;

    private readonly ReadQueue _queue;
    private readonly Action<string>? _log;
    private readonly IntPtr _clipboardWindow;

    /// <param name="completed">worker thread: the request, the result (<see cref="ReadResult.Selected"/>) and the trigger tick.</param>
    /// <param name="clipboardWindow">a window of our UI thread (it pumps messages): owner of the restored clipboard.</param>
    public SelectionReader(Func<long, bool> isCurrent, Action<ReadRequest, ReadResult, long> completed, IntPtr clipboardWindow, Action<string>? log)
    {
        _log = log;
        _clipboardWindow = clipboardWindow;
        _queue = new ReadQueue(isCurrent, (q, r) => completed(q, r, q.Trigger), Read, WarmUp, log,
                               CaretTextReader.StuckMs, TimeoutMs, "selection-reader");
    }

    public long LatestId => _queue.LatestId;

    /// <summary>Any thread; never waits (a short lock + an event set).</summary>
    public void Request(long version, IntPtr fg, int delayMs, bool clipboard, long triggerTick, string reason)
    {
        _queue.Request(version, fg, delayMs, reason, clipboard, triggerTick);   // the flags travel with the request
    }

    private void WarmUp(int generation)
    {
        foreach (var name in new[] { nameof(Read), nameof(ReadWin32), nameof(ReadUia), nameof(ReadLibreOffice) })
            if (typeof(SelectionReader).GetMethod(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static |
                                                         System.Reflection.BindingFlags.Instance) is { } m)
                RuntimeHelpers.PrepareMethod(m.MethodHandle);
        _ = ReadingDocument.Build("Le chat dort. L'arbre est grand.").UtteranceOf(0);
        if (LibreOfficeText.Shared is not null) LibreOfficeText.WarmUp();   // phase 2: the walk is JIT-compiled off the gesture
    }

    private ReadResult Read(ReadRequest q)
    {
        var fg = q.Fg;
        uint tid = Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return Fail(ReadStatus.Unavailable, "own");
        var gi = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        IntPtr focus = Native.GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;
        // LibreOffice gives UIA no selection: a Writer document window is read through IA2 (phase 2); anything else of
        // LibreOffice (Calc, Impress, a dialog) → Unavailable at once (the Ctrl+C fallback when allowed, never in Calc; its
        // password check still runs right before the copy)
        var r = HostApps.UiaTextUnavailable(fg) ? ReadLibreOffice(fg)
            : focus != IntPtr.Zero && string.Equals(PasswordProbe.ClassOf(focus), "Edit", StringComparison.OrdinalIgnoreCase)
            ? ReadWin32(focus) : ReadUia(pid, _log);
        if (r.Status == ReadStatus.Ok && r.Selected is { Length: > 0 } sel)
        {
            // D26: complete cut words, add the sentence end after the last word (from the context read in the same field)
            var x = SelectionExtend.Apply(r.Before ?? "", sel, r.After ?? "");
            if (x.AddedBefore != 0 || x.AddedAfter != 0)
                _log?.Invoke($"selection: completed (D26) +{x.AddedBefore} before, +{x.AddedAfter} after{(x.TailAt >= 0 ? ", sentence end added" : "")}");
            r = r with { Selected = x.Text, TailAt = x.TailAt, Before = null, After = null };
        }
        if (r.Status == ReadStatus.Ok && r.Selected is { } all && (r.Cut || all.Length >= MaxChars))
        {
            // longer than the reader's limit (fewer than 1000 words is possible with long words): cut at a word boundary,
            // the message is shown; an IA2 read already cut cleanly is only capped (phase 2)
            var (limited, cut) = SelectionPolicy.Limit(all, r.Cut, MaxChars);
            if (limited.Length != all.Length) r = r with { TailAt = -1 };
            r = r with { Selected = limited, Cut = cut };
        }
        // phase 2: an IA2 read that reached the selection's start but not its end (Partial) is shown with its own message
        // unless the copy (when allowed) gives the whole of it; one that missed the start comes back Unavailable
        if (q.Clipboard && SelectionPolicy.WantsFallback(r.Status, r.Partial) && Native.GetForegroundWindow() == fg)
        {
            // never in a console / terminal: Ctrl+C there is an interrupt (SIGINT)
            string fgClass = PasswordProbe.ClassOf(fg), focusClass = focus != IntPtr.Zero ? PasswordProbe.ClassOf(focus) : "";
            string? image = null;
            try { using var pr = Process.GetProcessById((int)pid); image = pr.ProcessName; } catch (Exception) { }
            if (ClipboardCopy.IsTerminalClass(fgClass) || ClipboardCopy.IsTerminalClass(focusClass) || ClipboardCopy.IsTerminalProcess(image))
            {
                _log?.Invoke($"selection: Ctrl+C fallback skipped (terminal: {image} / {fgClass})");
                return r;
            }
            // 0.11.0 (owner 2026-11-27): never in Calc, whatever the setting (Enter would paste the clipboard into the sheet)
            if (HostApps.ModuleOf(fg) == LibreOfficeModule.Calc)
            {
                _log?.Invoke("selection: clipboard fallback refused: Calc");
                return r;
            }
            // one Run at a time in the process (a replaced, slow worker must never overlap a new one on the clipboard)
            if (!Monitor.TryEnter(RunLock))
            {
                _log?.Invoke("selection: Ctrl+C fallback skipped (another one still running)");
                return r;
            }
            (string? text, string outcome) res;
            // a plain read is « stuck » after 2 s; a fallback Run may take up to WorstMs: the limit is raised only meanwhile
            _queue.StuckMsOverride = ClipboardCopy.WorstMs + 1000;
            try { res = ClipboardCopy.Run(new Win32Clipboard(_clipboardWindow, fg), () => PasswordProbe.Check(fg), ClipboardCopy.WaitFor(image)); }
            finally { _queue.StuckMsOverride = 0; Monitor.Exit(RunLock); }
            var (text, outcome) = res;
            _log?.Invoke($"selection: Ctrl+C fallback: {outcome}");   // never the text
            // no context around a copied selection: it is read exactly as selected (D26 does not apply)
            r = text is null ? r : new ReadResult(ReadStatus.Ok, "clipboard", TextContext.Invalid, 0, Selected: Cap(text));
        }
        if (Native.GetForegroundWindow() != fg && r.Status != ReadStatus.Password) r = r with { Status = ReadStatus.Stale };
        return r;
    }

    private static readonly object RunLock = new();

    /// <summary>LibreOffice phase 2: a Writer document frame (<c>SALFRAME</c>, never a <c>SALSUBFRAME</c> dialog: nothing
    /// is ever read there) with IA2 on → the IA2 walk, cut at <see cref="MaxChars"/> (its <c>Cut</c> kept); else
    /// <c>Unavailable</c>, source <c>skip</c>.</summary>
    private static ReadResult ReadLibreOffice(IntPtr fg)
    {
        if (LibreOfficeText.Shared is not { } lo || !string.Equals(PasswordProbe.ClassOf(fg), HostApps.FrameClass, StringComparison.Ordinal)
            || HostApps.ModuleOf(fg) != LibreOfficeModule.Writer)
            return Fail(ReadStatus.Unavailable, "skip");
        return lo.ReadSelection(fg, MaxChars, SelectionExtend.Context);
    }

    private static string Cap(string s) => s.Length > MaxChars ? s.Substring(0, MaxChars) : s;


    private static ReadResult Fail(ReadStatus s, string src) => new(s, src, TextContext.Invalid, 0);
    private static ReadResult Ok(string src, string text, string? before = null, string? after = null) =>
        new(ReadStatus.Ok, src, TextContext.Invalid, 0, before, null, text, after);

    /// <summary>Classic Edit control: password style first, then EM_GETSEL and WM_GETTEXT (it copies from the start of the
    /// text: up to the selection's end, at most <see cref="MaxChars"/> past its start).</summary>
    private static ReadResult ReadWin32(IntPtr h)
    {
        const string src = "win32";
        const uint flags = Native.SMTO_ABORTIFHUNG | Native.SMTO_ERRORONEXIT;
        if ((Native.GetWindowLongPtr(h, Native.GWL_STYLE).ToInt64() & Native.ES_PASSWORD) != 0) return Fail(ReadStatus.Password, src);
        if (Native.SendMessageTimeout(h, Native.EM_GETSEL, IntPtr.Zero, IntPtr.Zero, flags, CaretTextReader.Win32MsgTimeoutMs, out var sel) == IntPtr.Zero)
            return Fail(ReadStatus.Unavailable, src);
        long v = sel.ToInt64();
        if ((int)v == -1) return Fail(ReadStatus.Unavailable, src);
        int a = (int)(v & 0xFFFF), b = (int)((v >> 16) & 0xFFFF);
        int lo = Math.Min(a, b), hi = Math.Max(a, b);
        if (lo == hi) return Ok(src, "");
        int want = Math.Min(hi, lo + MaxChars);
        bool cut = want < hi;   // longer than MaxChars: the end is cut anyway (1000 words), no context after it
        int n = want + (cut ? 0 : SelectionExtend.Context) + 1;   // D26: a little text after the selection, same field
        var sb = new StringBuilder(n);
        if (Native.SendMessageTimeout(h, Native.WM_GETTEXT, (IntPtr)n, sb, flags, 200, out var got) == IntPtr.Zero)
            return Fail(ReadStatus.Unavailable, src);
        string all = sb.ToString();
        int end = Math.Min(all.Length, want);
        if ((int)got.ToInt64() < lo || end <= lo) return Fail(ReadStatus.Unavailable, src);
        int b0 = Math.Max(0, lo - SelectionExtend.Context);
        return Ok(src, all.Substring(lo, end - lo), all.Substring(b0, lo - b0), cut ? "" : all.Substring(end));
    }

    /// <summary>UI Automation: the focused element (pid checked, IsPassword before any pattern call), or its nearest
    /// ancestor with a text pattern in the same process; every selected range, joined by line breaks.</summary>
    private static ReadResult ReadUia(uint fgPid, Action<string>? log)
    {
        const string src = "uia";
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el is null) return Fail(ReadStatus.Unavailable, src);
            int elPid = el.Current.ProcessId;
            if (elPid != 0 && elPid != (int)fgPid) return Fail(ReadStatus.Unavailable, src);
            if (el.Current.IsPassword) return Fail(ReadStatus.Password, src);
            TextPattern? tp = null;
            var cur = el;
            for (int up = 0; up < 8 && cur != null; up++)
            {
                if (cur.TryGetCurrentPattern(TextPattern.Pattern, out var o)) { tp = (TextPattern)o; break; }
                cur = TreeWalker.ControlViewWalker.GetParent(cur);
                if (cur is null || cur == AutomationElement.RootElement) break;
                int p = cur.Current.ProcessId;
                if (p != 0 && p != (int)fgPid) break;
                if (cur.Current.IsPassword) return Fail(ReadStatus.Password, src);
            }
            if (tp is null) return Fail(ReadStatus.Unavailable, src);
            var sb = new StringBuilder();
            TextPatternRange? first = null, lastR = null;
            foreach (var r in tp.GetSelection())
            {
                if (r.CompareEndpoints(TextPatternRangeEndpoint.Start, r, TextPatternRangeEndpoint.End) == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                int left = MaxChars - sb.Length;
                if (left <= 0) break;
                string t = r.GetText(left);
                sb.Append(t);
                first ??= r;
                lastR = t.Length < left ? r : null;   // a selection cut at MaxChars has no « after » context
            }
            if (first is null) return Ok(src, "");
            // D26: a little text around the selection, from ranges clamped to the same pattern's document (never past it)
            var doc = tp.DocumentRange;
            string before = Around(first, doc, cur!, true, log), after = lastR is null ? "" : Around(lastR, doc, cur!, false, log);
            return Ok(src, sb.ToString(), before, after);
        }
        catch (Exception ex)
        {
            log?.Invoke($"selection: uia read error: {ex.GetType().Name}: {ex.Message}");   // never text content
            return Fail(ReadStatus.Unavailable, src);
        }
    }

    /// <summary>D26 context: up to <see cref="SelectionExtend.Context"/> characters before / after <paramref name="r"/>, clamped
    /// to the document range; "" when that text lies in (or contains) a password field — the reading never extends into one.</summary>
    private static string Around(TextPatternRange r, TextPatternRange doc, AutomationElement owner, bool before, Action<string>? log)
    {
        try
        {
            var c = r.Clone();
            if (before)
            {
                c.MoveEndpointByRange(TextPatternRangeEndpoint.End, c, TextPatternRangeEndpoint.Start);
                c.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -SelectionExtend.Context);
                if (c.CompareEndpoints(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.Start) < 0)
                    c.MoveEndpointByRange(TextPatternRangeEndpoint.Start, doc, TextPatternRangeEndpoint.Start);
            }
            else
            {
                c.MoveEndpointByRange(TextPatternRangeEndpoint.Start, c, TextPatternRangeEndpoint.End);
                c.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, SelectionExtend.Context);
                if (c.CompareEndpoints(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.End) > 0)
                    c.MoveEndpointByRange(TextPatternRangeEndpoint.End, doc, TextPatternRangeEndpoint.End);
            }
            var enc = c.GetEnclosingElement();
            if (enc is null || enc.Current.IsPassword) return "";
            foreach (var child in c.GetChildren()) if (child.Current.IsPassword) return "";
            // a password field anywhere under the element that holds this context (bounded: the context is ≤ 48
            // characters, so its enclosing element is a paragraph-sized subtree — unless it is the whole field / document)
            if (!Automation.Compare(enc, owner) &&
                enc.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsPasswordProperty, true)) != null) return "";
            return c.GetText(-1);
        }
        catch (Exception ex)
        {
            log?.Invoke($"selection: context read error: {ex.GetType().Name}");
            return "";
        }
    }

    public void Dispose() => _queue.Dispose();
}

/// <summary>
/// <see cref="IClipboardOps"/> on the Win32 clipboard. The restored data is owned by <c>owner</c>, a window of our UI thread
/// (a non-pumping owner would hang the next application that empties the clipboard). Only HGLOBAL formats are copied
/// (<see cref="ClipboardCopy.CanRestore"/> refuses the others beforehand). Worker thread.
/// </summary>
internal sealed class Win32Clipboard : IClipboardOps
{
    private const long MaxBytes = 32L * 1024 * 1024;
    private readonly IntPtr _owner, _fg;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    /// <param name="fg">the window the selection was read in (re-checked right before Ctrl+C).</param>
    public Win32Clipboard(IntPtr owner, IntPtr fg) { _owner = owner; _fg = fg; }

    public bool StillTarget() => Native.GetForegroundWindow() == _fg;

    private long _sentTick = long.MaxValue;

    public bool OwnedByTarget()
    {
        var o = Native.GetClipboardOwner();
        if (o == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(o, out uint op);
        Native.GetWindowThreadProcessId(_fg, out uint tp);
        return op != 0 && op == tp;
    }

    public bool HasOwner() => Native.GetClipboardOwner() != IntPtr.Zero;

    public bool UserCopied() => Interlocked.Read(ref ClipboardCopy.LastUserCopyTick) >= _sentTick;

    // clipboard-history formats (Windows 10 1809+): the restored copy of the user's content is not added again to the
    // history nor synced to the cloud clipboard. The application's own copy, made by its Ctrl+C handler, cannot be hidden.
    private static readonly uint FmtExclude = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"),
                                 FmtHistory = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory"),
                                 FmtCloud = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");

    public uint Sequence => Native.GetClipboardSequenceNumber();
    public long NowMs => _sw.ElapsedMilliseconds;
    public void Sleep(int ms) => Thread.Sleep(ms);

    private bool Open()
    {
        for (int i = 0; i < 10; i++)
        {
            if (Native.OpenClipboard(_owner)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    public IReadOnlyList<uint> Formats()
    {
        var l = new List<uint>();
        if (!Open()) return new uint[] { uint.MaxValue };   // unknown: CanRestore → false (never guess)
        try { for (uint f = Native.EnumClipboardFormats(0); f != 0; f = Native.EnumClipboardFormats(f)) l.Add(f); }
        finally { Native.CloseClipboard(); }
        return l;
    }

    public bool OwnerResponsive()
    {
        var o = Native.GetClipboardOwner();
        if (o == IntPtr.Zero) return true;
        return Native.SendMessageTimeout(o, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 100, out _) != IntPtr.Zero;
    }

    public ClipboardSnapshot? Save()
    {
        _sentTick = Environment.TickCount64;   // a user copy from now on (snapshot, Ctrl+C, watch) is never restored over
        if (!Open()) return null;
        try
        {
            var items = new List<(uint, byte[])>();
            long total = 0;
            for (uint f = Native.EnumClipboardFormats(0); f != 0; f = Native.EnumClipboardFormats(f))
            {
                if (ClipboardCopy.IsHandleFormat(f)) continue;   // re-synthesised from the DIB (CanRestore checked it)
                var h = Native.GetClipboardData(f);
                if (h == IntPtr.Zero) continue;
                long size = (long)Native.GlobalSize(h);
                total += size;
                if (total > MaxBytes) return null;               // too big to keep a copy: the fallback is not used
                var p = Native.GlobalLock(h);
                if (p == IntPtr.Zero) continue;
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(p, data, 0, (int)size);
                    items.Add((f, data));
                }
                finally { Native.GlobalUnlock(h); }
            }
            return new ClipboardSnapshot(items);
        }
        finally { Native.CloseClipboard(); }
    }

    public bool ModifiersDown() =>
        Native.IsDown(Native.VK_SHIFT) || Native.IsDown(Native.VK_CONTROL) || Native.IsDown(Native.VK_MENU) || Native.IsDown(0x5B) || Native.IsDown(0x5C);

    public bool SendCopy()
    {
        var m = KeyboardController.Marker;
        Native.INPUT K(ushort vk, uint fl) => new()
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = fl, dwExtraInfo = m } },
        };
        var a = new[] { K(0x11, 0), K(Native.VK_C, 0), K(Native.VK_C, Native.KEYEVENTF_KEYUP), K(0x11, Native.KEYEVENTF_KEYUP) };
        return Inserter.Send(a).Sent == a.Length;
    }

    public string? ReadText()
    {
        if (!Open()) return null;
        try
        {
            var h = Native.GetClipboardData(Native.CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            long size = (long)Native.GlobalSize(h);
            var p = Native.GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                var s = Marshal.PtrToStringUni(p, (int)Math.Min(size / 2, SelectionReader.MaxChars + 1)) ?? "";
                int nul = s.IndexOf('\0');
                return nul >= 0 ? s.Substring(0, nul) : s;
            }
            finally { Native.GlobalUnlock(h); }
        }
        finally { Native.CloseClipboard(); }
    }

    public bool Restore(ClipboardSnapshot snap)
    {
        if (!Open()) return false;
        try
        {
            if (!Native.EmptyClipboard()) return false;
            bool ok = true;
            foreach (var (f, data) in snap.Items)
            {
                var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)Math.Max(1, data.Length));
                if (h == IntPtr.Zero) { ok = false; continue; }
                var p = Native.GlobalLock(h);
                if (p == IntPtr.Zero) { Native.GlobalFree(h); ok = false; continue; }
                Marshal.Copy(data, 0, p, data.Length);
                Native.GlobalUnlock(h);
                if (Native.SetClipboardData(f, h) == IntPtr.Zero) { Native.GlobalFree(h); ok = false; }   // the system owns it on success
            }
            foreach (var (f, data) in new[] { (FmtExclude, new byte[] { 0 }), (FmtHistory, new byte[4]), (FmtCloud, new byte[4]) })
            {
                if (f == 0 || snap.Items.Any(i => i.Format == f)) continue;
                var h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)data.Length);
                if (h == IntPtr.Zero) continue;
                var p = Native.GlobalLock(h);
                if (p == IntPtr.Zero) { Native.GlobalFree(h); continue; }
                Marshal.Copy(data, 0, p, data.Length);   // DWORD 0 = no
                Native.GlobalUnlock(h);
                if (Native.SetClipboardData(f, h) == IntPtr.Zero) Native.GlobalFree(h);
            }
            return ok;
        }
        finally { Native.CloseClipboard(); }
    }
}
