using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Predict.Keyboard;

/// <summary>The picture of a profile (D29 Q9): its ARASAAC pictogram on a white tile inside its colour, or — no
/// pictogram, no pack — its initial on the colour. No personal pictures.</summary>
internal static class AvatarView
{
    public static SolidColorBrush Brush(ProfileColor c)
    {
        uint v = Avatar.Argb(c);
        var b = new SolidColorBrush(Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v));
        b.Freeze();
        return b;
    }

    public static FrameworkElement Create(Profile p, PictureStore? pictures, double size) =>
        Create(p.Avatar, p.Initial, pictures, size);

    public static FrameworkElement Create(Avatar a, string initial, PictureStore? pictures, double size)
    {
        var border = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.18), Background = Brush(a.Color),
            SnapsToDevicePixels = true,
        };
        if (a.Picto is { } w && pictures != null && pictures.Has(w) && pictures.GetNow(w) is ImageSource img)
            border.Child = new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(size * 0.12), Margin = new Thickness(size * 0.09),
                Child = new Image { Source = img, Stretch = Stretch.Uniform, Margin = new Thickness(size * 0.05) },
            };
        else
            border.Child = new TextBlock
            {
                Text = initial, FontSize = size * 0.5, FontWeight = FontWeights.Bold,
                Foreground = a.Color == ProfileColor.Jaune ? Brushes.Black : Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        return border;
    }
}

/// <summary>Small builders for the code-built windows of plan 09 (every control gets an AutomationId; text from
/// <see cref="Strings"/>).</summary>
internal static class Ui
{
    public static Button Button(string text, string automationId, RoutedEventHandler click, double minWidth = 110)
    {
        var b = new Button { Content = text, MinWidth = minWidth, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetAutomationId(b, automationId);
        b.Click += click;
        return b;
    }

    public static TextBlock Text(string text, string? automationId = null, double size = 14, bool wrap = true)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (automationId != null) AutomationProperties.SetAutomationId(t, automationId);
        return t;
    }

    public static TextBlock Heading(string text, string? automationId = null, double size = 20)
    {
        var t = Text(text, automationId, size);
        t.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level1);
        return t;
    }

    public static CheckBox Check(string text, string automationId, bool on)
    {
        var c = new CheckBox { Content = text, IsChecked = on, Margin = new Thickness(0, 6, 0, 0) };
        AutomationProperties.SetAutomationId(c, automationId);
        return c;
    }

    /// <summary>Our windows: Segoe UI 14, the app icon, centred on the primary screen, shown in the taskbar.</summary>
    public static void Setup(Window w, string title, string automationId)
    {
        w.Title = title;
        w.FontFamily = new FontFamily("Segoe UI");
        w.FontSize = 14;
        w.ResizeMode = ResizeMode.NoResize;
        w.SizeToContent = SizeToContent.WidthAndHeight;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowInTaskbar = true;
        w.UseLayoutRounding = true;
        w.Icon = AboutWindow.AppIcon(16);
        AutomationProperties.SetAutomationId(w, automationId);
    }

    /// <summary>The work area of the monitor the window is on, in this window's DIPs (per-monitor DPI); the primary
    /// monitor's before the window has a handle.</summary>
    public static Rect WorkArea(Window w)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;
        var wa = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea;   // device pixels
        var m = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(m.Transform(new Point(wa.Left, wa.Top)), m.Transform(new Point(wa.Right, wa.Bottom)));
    }

    /// <summary>0.11.1: a tall window's content scrolls instead of leaving the screen. The window (SizeToContent) is never
    /// taller than the work area of its own monitor (minus <paramref name="margin"/>; at least <paramref name="min"/>):
    /// set when its handle exists, recomputed when it changes size, moves to another monitor, changes DPI or the display
    /// settings change. When it grows past the bottom of that work area (a tab switch, an Expander), it moves up. The
    /// content that must scroll is wrapped in <see cref="Scroll"/>.</summary>
    public static void FitToWorkArea(Window w, double margin = 0, double min = 240)
    {
        void Cap() => w.MaxHeight = Math.Max(min, WorkArea(w).Height - margin);
        void Fit()
        {
            Cap();
            if (double.IsNaN(w.Top)) return;
            var wa = WorkArea(w);
            if (w.Top + w.ActualHeight > wa.Bottom) w.Top = Math.Max(wa.Top, wa.Bottom - w.ActualHeight);
            if (w.Top < wa.Top) w.Top = wa.Top;
        }
        Cap();   // the primary monitor until the handle exists
        w.SourceInitialized += (_, _) => Cap();
        w.SizeChanged += (_, _) => Fit();
        w.DpiChanged += (_, _) => Fit();
        w.LocationChanged += (_, _) => Cap();   // dragged to another monitor: no move while the user drags
        EventHandler display = (_, _) => w.Dispatcher.BeginInvoke(Fit);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += display;
        w.Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= display;   // a static event: no leak
    }

    /// <summary>A tab's content that scrolls vertically when the window is capped by <see cref="FitToWorkArea"/>, so the
    /// window's own buttons stay visible. Not a tab stop itself: Tab goes to the controls inside, and a control that gets
    /// the keyboard focus is scrolled into view (WPF brings a keyboard-focused element into view). A right padding keeps
    /// the content off the scroll bar; the wheel over an inner list at its end scrolls the tab (<see cref="ForwardWheel"/>).</summary>
    public static ScrollViewer Scroll(UIElement content)
    {
        var sv = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Padding = new Thickness(0, 0, 8, 0),
        };
        ForwardWheel(sv);
        return sv;
    }

    /// <summary>The mouse wheel over a list (or any scroll viewer) inside <paramref name="outer"/>: WPF's inner viewer
    /// takes every notch, even at its end or when it cannot scroll, so the tab would never move under a list. When the
    /// inner viewer cannot scroll further that way, the notch goes to <paramref name="outer"/> instead.</summary>
    public static void ForwardWheel(ScrollViewer outer)
    {
        outer.PreviewMouseWheel += (_, e) =>
        {
            ScrollViewer? inner = null;
            for (var d = e.OriginalSource as DependencyObject; d != null && d != outer;
                 d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is ScrollViewer sv) { inner = sv; break; }
            if (inner is null) return;   // not over a nested viewer: the outer one scrolls as usual
            bool canScroll = e.Delta > 0 ? inner.VerticalOffset > 0.5 : inner.VerticalOffset < inner.ScrollableHeight - 0.5;
            if (inner.ScrollableHeight > 0 && canScroll) return;
            e.Handled = true;
            outer.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                             { RoutedEvent = UIElement.MouseWheelEvent, Source = outer });
        };
    }

    /// <summary>Above the other windows while it appears (a start not made by a click may not get the foreground),
    /// then a normal window again — as the welcome window.</summary>
    public static void ShowInFront(Window w)
    {
        w.Topmost = true;
        w.Show();
        w.Activate();
        w.Dispatcher.BeginInvoke(() => w.Topmost = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    public static void Announce(UIElement e)
    {
        (System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(e)
         ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(e))
            ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
}
