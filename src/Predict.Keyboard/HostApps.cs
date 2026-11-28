using System;
using System.Collections.Generic;

namespace Predict.Keyboard;

/// <summary>
/// Host applications that need their own rule, by process image name (no extension, case-insensitive). One list per
/// family, shared by every policy that names it (keys-only replacement deny, the Ctrl+C selection default, the UIA skip,
/// the D22 punctuation rule), so that a new LibreOffice binary is added once.
/// </summary>
/// <summary>A LibreOffice window's module (<see cref="HostApps.ModuleOf(string?, string?)"/>); None = not LibreOffice.</summary>
internal enum LibreOfficeModule { None, Writer, Calc, Impress, Draw, Other }

internal static class HostApps
{
    /// <summary>LibreOffice (0.11.0 quick fix, owner 2026-11-26): its windows belong to <c>soffice.bin</c> (the launchers
    /// <c>soffice</c>, <c>swriter</c>, <c>scalc</c>… start it); the names of the launchers are kept for safety (a portable
    /// build, an older version). Apache OpenOffice and Collabora Office also run as <c>soffice.bin</c> and get the same rules,
    /// but they are outside the supported scope (owner: LibreOffice ≥ 25.2, Writer). It gives nothing to UI Automation (no text pattern, no caret: keyboard-design § 5b,
    /// « Unavailable »), rewrites typed words (AutoCorrect) and spaces <c>; : ! ?</c> itself in French text.</summary>
    public static readonly IReadOnlyCollection<string> LibreOffice = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "soffice", "soffice.bin", "swriter", "scalc", "simpress", "sdraw", "smath", "sbase",
    };

    public static bool IsLibreOffice(string? process) =>
        !string.IsNullOrEmpty(process) && ((HashSet<string>)LibreOffice).Contains(process);

    /// <summary>A host whose text and caret UI Automation never gives: the caret read, the selection read and the caret
    /// lookup of the bar are not even tried <b>through UIA</b> there (LibreOffice: a failed lookup cost ≈ 110 ms per bar
    /// update and 4–8× its CPU per key, phase 0 probe 2026-11-26) — classified <see cref="ReadStatus.Unavailable"/> at
    /// once. LibreOffice phase 1: a <b>Writer</b> window (<see cref="ModuleOf(IntPtr)"/>) is read through IAccessible2
    /// instead (<see cref="LibreOfficeText"/>: the caret read, the D22 read-back, the bar's caret, the password probe);
    /// Calc, Impress, Draw and LibreOffice's dialogs stay keys only.</summary>
    public static bool UiaTextUnavailable(string? process) => IsLibreOffice(process);

    // window → (pid, name): a recycled pid comes with another window; bounded (cleared when full)
    private static readonly Dictionary<IntPtr, (uint Pid, string? Name)> Names = new();
    private static readonly object Gate = new();

    /// <summary>Process image name (no .exe) of <paramref name="hwnd"/>'s process, or null. Any thread; cached per window.</summary>
    public static string? ProcessNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;
        lock (Gate)
            if (Names.TryGetValue(hwnd, out var c) && c.Pid == pid) return c.Name;
        // OpenProcess + QueryFullProcessImageName (a few us; LibreOffice phase 1 review: the hook thread asks this at a focus
        // event, where a Process object cost several ms on a cache miss); same name as Process.ProcessName
        string? name = PrivateHosts.ImageName(pid);
        lock (Gate)
        {
            if (Names.Count >= 64) Names.Clear();
            Names[hwnd] = (pid, name);
        }
        return name;
    }

    /// <summary>
    /// Which LibreOffice module a foreground window is (owner 2026-11-27: the default Ctrl+C copy for Writer only, never
    /// in Calc). Every module runs in the one <c>soffice.bin</c> process and every frame has the window class
    /// <c>SALFRAME</c>, so neither the process nor the class tells them apart; the frame's title does, and it is not
    /// translated: « &lt;document&gt; — LibreOffice Writer » (26.8, fr and en; U+2014 between spaces, « Sans nom 1 —
    /// LibreOffice Writer » / « Untitled 1 — LibreOffice Writer » for a new document; older versions used « - »). Only the
    /// suffix is compared, so the separator and the document name do not matter. An IA2 role would need a COM round trip
    /// into LibreOffice for every gesture: the title is free (no message is sent). A dialog in front (its own title) is
    /// <see cref="LibreOfficeModule.Other"/>: no default copy there. The launchers <c>swriter</c> / <c>scalc</c> name
    /// their module directly.
    /// </summary>
    public static LibreOfficeModule ModuleOf(string? process, string? title)
    {
        if (!IsLibreOffice(process)) return LibreOfficeModule.None;
        if (string.Equals(process, "swriter", StringComparison.OrdinalIgnoreCase)) return LibreOfficeModule.Writer;
        if (string.Equals(process, "scalc", StringComparison.OrdinalIgnoreCase)) return LibreOfficeModule.Calc;
        var t = (title ?? "").TrimEnd();
        bool Ends(string m) => t.EndsWith("LibreOffice " + m, StringComparison.OrdinalIgnoreCase);
        if (Ends("Writer")) return LibreOfficeModule.Writer;
        // any title ending « Calc » (LibreOfficeDev Calc, Collabora / OpenOffice Calc…), checked after Writer so that a
        // Writer document is never taken for a sheet: never a copy in a spreadsheet
        if (t.EndsWith("Calc", StringComparison.OrdinalIgnoreCase)) return LibreOfficeModule.Calc;
        if (Ends("Impress")) return LibreOfficeModule.Impress;
        if (Ends("Draw")) return LibreOfficeModule.Draw;
        return LibreOfficeModule.Other;
    }

    /// <summary>LibreOffice's top-level window classes (every module, every version): a document frame is <c>SALFRAME</c>, a
    /// dialog <c>SALSUBFRAME</c> (« Saisir le mot de passe », VM 26.8.3). The hook's focus path tests them first, one
    /// GetClassName, before any process lookup.</summary>
    public const string FrameClass = "SALFRAME", DialogClass = "SALSUBFRAME";

    /// <summary>Is <paramref name="hwnd"/> a LibreOffice frame or dialog (<see cref="FrameClass"/>, <see cref="DialogClass"/>)?
    /// One GetClassName; any thread.</summary>
    public static bool IsFrame(IntPtr hwnd) => FrameKind(hwnd) != 0;

    /// <summary>Is <paramref name="hwnd"/> a LibreOffice dialog (<see cref="DialogClass"/>)? One GetClassName; any thread.</summary>
    public static bool IsDialogFrame(IntPtr hwnd) => FrameKind(hwnd) == 2;

    /// <summary>One GetClassName: 1 = <see cref="FrameClass"/>, 2 = <see cref="DialogClass"/>, 0 = anything else (no string
    /// is built for other classes: only their length is compared).</summary>
    private static int FrameKind(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        var sb = new System.Text.StringBuilder(16);
        int n = Native.GetClassName(hwnd, sb, sb.Capacity);
        if (n != FrameClass.Length && n != DialogClass.Length) return 0;
        var cls = sb.ToString(0, n);
        return string.CompareOrdinal(cls, FrameClass) == 0 ? 1 : string.CompareOrdinal(cls, DialogClass) == 0 ? 2 : 0;
    }

    public static bool IsFrameClass(string? cls) =>
        string.Equals(cls, FrameClass, StringComparison.Ordinal) || string.Equals(cls, DialogClass, StringComparison.Ordinal);

    /// <summary>
    /// LibreOffice phase 1: the windows whose focus objects are recorded for IAccessible2 — Writer (reads, caret, password
    /// probe) and LibreOffice's dialogs (<see cref="LibreOfficeModule.Other"/>: the password probe only, so a password
    /// field there is seen as one: VM L11, its UIA probe answered NotPassword and the typed password reached the
    /// <c>--log</c>). Calc, Impress, Draw: unchanged (keys only, UIA probe).
    /// </summary>
    public static bool Ia2Focus(LibreOfficeModule m) => m is LibreOfficeModule.Writer or LibreOfficeModule.Other;

    /// <summary>The window's title (no message sent), or "". Any thread.</summary>
    public static string TitleOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(512);
        int n = Native.GetWindowText(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : "";
    }

    /// <summary><see cref="ModuleOf(string?, string?)"/> of a window. Any thread.</summary>
    public static LibreOfficeModule ModuleOf(IntPtr hwnd)
    {
        var p = ProcessNameOf(hwnd);
        return IsLibreOffice(p) ? ModuleOf(p, TitleOf(hwnd)) : LibreOfficeModule.None;
    }

    /// <summary><see cref="UiaTextUnavailable(string?)"/> of the window's process. Any thread.</summary>
    public static bool UiaTextUnavailable(IntPtr hwnd) => UiaTextUnavailable(ProcessNameOf(hwnd));
}
