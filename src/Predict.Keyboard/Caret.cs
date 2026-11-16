using System;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Predict.Keyboard;

/// <summary>
/// Finds the text caret of the foreground application, in physical screen pixels. Order:
/// 1) GetGUIThreadInfo (Win32 caret — Notepad, classic edit controls),
/// 2) UI Automation TextPattern (modern apps: browsers, Word, WinUI),
/// 3) the mouse position (last resort).
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
                        return ((int)b.Right, (int)b.Bottom, "uia");
                    }
                }
            }
        }
        catch { /* provider without TextPattern, or cross-process failure: fall through */ }

        Native.GetCursorPos(out var c);
        return (c.X, c.Y + 20, "mouse");
    }
}
