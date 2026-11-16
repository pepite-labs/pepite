using System;
using System.IO;
using System.Windows;

namespace Predict.Keyboard;

/// <summary>
/// « Pépite est prêt ! » once, at the very first manual start (owner Q3 of the embedded-help plan; Réglages can ask for it
/// once more) and « Pépite est déjà démarré » when the user starts a second copy (always). « En savoir plus » / « Aide »
/// open the embedded help (bienvenue.html / index.html, else the LISEZ-MOI). Both explain where the icon is, with a simplified
/// picture of the taskbar. One window at a time: a new request replaces the open one. Keyboard: Tab order follows the
/// layout, Enter / Esc = OK; every control has an AutomationId and the picture a screen-reader name. While it has the
/// focus the bar stays hidden (our own window, never read).
/// </summary>
public partial class WelcomeWindow : Window
{
    internal enum Mode { Welcome, AlreadyRunning }

    private static WelcomeWindow? _open;

    private readonly Action? _openSettings;
    private readonly string? _help;
    private readonly HelpTopic _topic;
    private readonly Action<string>? _log;

    /// <param name="onClosed">welcome mode: called when the window closes.</param>
    /// <param name="openSettings">« déjà démarré » mode: opens Réglages.</param>
    internal static void ShowSingle(Mode mode, Action? onClosed, Action? openSettings, Action<string>? log)
    {
        if (_open is { } w) w.Close();
        var win = _open = new WelcomeWindow(mode, openSettings, log);
        win.Closed += (_, _) =>
        {
            if (_open == win) _open = null;
            log?.Invoke($"welcome window closed ({mode})");
            onClosed?.Invoke();
        };
        // above the other windows even when Windows refuses us the foreground (a start not made by the user's own
        // click, e.g. from a script or a slow start): topmost while it appears, then a normal window again
        win.Topmost = true;
        win.Show();
        win.Activate();
        win.OkButton.Focus();
        win.Dispatcher.BeginInvoke(() => win.Topmost = false, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        log?.Invoke($"welcome window shown ({mode})");
    }

    internal WelcomeWindow(Mode mode, Action? openSettings, Action<string>? log = null)
    {
        InitializeComponent();
        _openSettings = openSettings;
        _log = log;
        bool welcome = mode == Mode.Welcome;
        _topic = welcome ? HelpTopic.Welcome : HelpTopic.Home;
        Title = welcome ? Strings.WelcomeWindowTitle : Strings.AlreadyWindowTitle;
        Heading.Text = welcome ? Strings.WelcomeHeading : Strings.AlreadyHeading;
        System.Windows.Automation.AutomationProperties.SetAutomationId(this, welcome ? "WelcomeWindow" : "AlreadyRunningWindow");
        Icon = AboutWindow.AppIcon(16);
        Logo.Source = AboutWindow.AppIcon(48);
        TrayLogo.Source = AboutWindow.AppIcon(24);
        SettingsButton.Visibility = welcome || openSettings is null ? Visibility.Collapsed : Visibility.Visible;
        MoreButton.Content = welcome ? Strings.WelcomeMore : Strings.AlreadyHelp;
        _help = HelpLinks.Resolve(AppContext.BaseDirectory, _topic, File.Exists);
        if (_help is null)
        {
            MoreButton.IsEnabled = false;
            MoreButton.ToolTip = Strings.HelpMissing;
            System.Windows.Automation.AutomationProperties.SetHelpText(MoreButton, Strings.HelpMissing);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        Close();
        _openSettings?.Invoke();
    }

    /// <summary>The help page « Bienvenue » (welcome) or the help's home (« déjà démarré »), else LISEZ-MOI.txt, in the
    /// user's browser / editor; local files only, no network (D13).</summary>
    private void OnHelp(object sender, RoutedEventArgs e)
    {
        if (_help is null) return;
        HelpUi.Open(_topic, this, _log);
    }
}
