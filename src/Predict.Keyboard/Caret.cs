using System;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Predict.Keyboard;

/// <summary>
/// Finds the text caret of the foreground application, in physical screen pixels. Order:
/// 1) GetGUIThreadInfo (Win32 caret — Notepad, classic edit controls),
/// 2) LibreOffice phase 1: IAccessible2 in a Writer window where IA2 answers (<see cref="LibreOfficeText.IsLive"/>) —
///    the lookup runs on the <c>lo-caret</c> worker and this (UI) thread waits at most
///    <see cref="LibreOfficeText.CaretWaitMs"/> (source <c>ia2</c>), else the last point of the same paragraph younger than
///    1 s (<c>ia2-late</c>), else the mouse; this thread never makes a COM call,
/// 3) UI Automation TextPattern (modern apps: browsers, Word, WinUI) — not asked in a host that never gives one
///    (<see cref="HostApps.UiaTextUnavailable(IntPtr)"/>: LibreOffice),
/// 4) the mouse position (last resort).
/// </summary>
internal static class Caret
{
    public static (int X, int Y, string Source) Get()
    {
        var fg = Native.GetForegroundWindow();
        uint tid = Native.GetWindowThreadProcessId(fg, out _);
        var gi = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (Native.GetGUIThreadInfo(tid, ref gi) && gi.hwndCaret != IntPtr.Zero)
        {
            var p = new Native.POINT { X = gi.rcCaret.Left, Y = gi.rcCaret.Bottom };
            if (Native.ClientToScreen(gi.hwndCaret, ref p)) return (p.X, p.Y, "win32");
        }

        if (LibreOfficeText.Shared is { } lo && lo.IsLive(fg) && lo.CaretPointBounded(fg) is { } l)
            return (l.X, l.Y, l.From switch { LibreOfficeText.CaretFrom.Late => "ia2-late", LibreOfficeText.CaretFrom.Recent => "ia2-recent", _ => "ia2" });

        // 0.11.0: LibreOffice has no system caret and gives UIA none: the lookup only cost ≈ 110 ms (phase 0 probe)
        if (!HostApps.UiaTextUnavailable(fg) && FromUia() is { } u) return (u.X, u.Y, "uia");

        Native.GetCursorPos(out var c);
        return (c.X, c.Y + 20, "mouse");
    }

    private static (int X, int Y)? FromUia()
    {
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el != null && el.TryGetCurrentPattern(TextPattern.Pattern, out var o))
            {
                var tp = (TextPattern)o;
                var sel = tp.GetSelection();
                if (sel.Length > 0)
                {
                    var r = sel[0].Clone();
                    r.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
                    var rects = r.GetBoundingRectangles();
                    if (rects.Length > 0)
                    {
                        var b = rects[^1];
                        return ((int)b.Right, (int)b.Bottom);
                    }
                }
            }
        }
        catch { /* provider without TextPattern, or cross-process failure: fall through */ }
        return null;
    }
}
