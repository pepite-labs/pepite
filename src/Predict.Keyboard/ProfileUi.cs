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
