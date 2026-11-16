using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Predict.Keyboard;

/// <summary>
/// « Grande fenêtre » of the reading mode (plan 07, D21): the same tiles as the bar in a resizable window, for long texts;
/// it scrolls to the word being read. Its place and size are remembered per user (<c>reading.window</c>, physical px).
/// A normal, activating window (resizing a non-activating one goes through the system size loop, which activates it
/// anyway): while it is in front the source application keeps its selection, the controller treats it as one of our
/// windows (the reading mode stays). Space / F9 = Play / Pause, Esc closes it. UI thread.
/// </summary>
public partial class ReadingWindow : Window
{
    internal ReadingView View { get; }
    private readonly Action<int, int, int, int>? _saveRect;
    private readonly (int X, int Y, int W, int H)? _rect;

    internal ReadingWindow(ReadingSettings settings, Action<int, int, int, int>? saveRect, Action<string>? log)
    {
        InitializeComponent();
        View = new ReadingView(false, log, "window");
        Host.Child = View.Root;
        _saveRect = saveRect;
        Icon = AboutWindow.AppIcon(16);
        if (settings.HasWindow) _rect = (settings.WinX!.Value, settings.WinY!.Value, settings.WinW!.Value, settings.WinH!.Value);
        else
        {
            var wa = SystemParameters.WorkArea;   // DIP, primary monitor
            Width = wa.Width * 0.6; Height = wa.Height * 0.6;
            Left = wa.Left + wa.Width * 0.2; Top = wa.Top + wa.Height * 0.2;
        }
        SourceInitialized += (_, _) =>
        {
            if (_rect is not { } r) return;
            // the saved place in physical px, kept inside a monitor (a monitor may have gone)
            var screens = System.Windows.Forms.Screen.AllScreens;
            var pt = new System.Drawing.Point(r.X + 40, r.Y + 20);
            bool on = false;
            foreach (var s in screens) if (s.WorkingArea.Contains(pt)) on = true;
            if (!on) return;
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, r.X, r.Y, r.W, r.H, 0x0004 /*NOZORDER*/ | 0x0010 /*NOACTIVATE*/);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key is Key.Space or Key.F9 && System.Windows.Input.Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                if (!e.IsRepeat) PlayPauseKey?.Invoke();   // a held key toggles once
            }
        };
        Closing += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero && WindowState == WindowState.Normal && Native.GetWindowRect(h, out var r))
                _saveRect?.Invoke(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        };
    }

    /// <summary>
    /// Bring the window to the front after the user clicked ⤢ on the bar. The bar never activates, so the application keeps
    /// the foreground and Windows' foreground lock may refuse a plain activation (owner's Word run 1: the window opened behind
    /// Word). The click is the user's request: if <c>Activate()</c> was not enough, the input of the foreground thread is
    /// attached for the call (the documented way around the lock) and released at once. UI thread.
    /// </summary>
    /// <returns>whether the window is in front afterwards.</returns>
    internal bool BringToFront()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return false;
        Activate();
        var fg = Native.GetForegroundWindow();
        if (fg == h) return true;
        uint fgThread = Native.GetWindowThreadProcessId(fg, out _), me = Native.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        try
        {
            Native.BringWindowToTop(h);
            Native.SetForegroundWindow(h);
        }
        finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
        Activate();
        return Native.GetForegroundWindow() == h;
    }

    /// <summary>Space / F9 pressed while this window has the keyboard.</summary>
    internal event Action? PlayPauseKey;
}
