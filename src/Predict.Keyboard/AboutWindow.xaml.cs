using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace Predict.Keyboard;

/// <summary>
/// « À propos » (D4, D5): logo, « Pépite — prédiction de mots », version, copyright « Les auteurs de Pépite » and the
/// project site (D18: no personal name or e-mail), ARASAAC attribution (from the pack's meta, else the verbatim fallback)
/// with its links, pack state, Lexique credit, the bundled OFL fonts (D16) with their copyright lines. A normal activating window, one instance at a time.
/// Links only open the user's browser (no network code in the app, D13). The credits are in Expanders inside a
/// ScrollViewer, the header and « Fermer » stay visible, and the window is never taller than the work area of its
/// monitor (owner, 2026-11-14: the two voices' credits made it taller than the screen).
/// </summary>
public partial class AboutWindow : Window
{
    private static AboutWindow? _open;

    /// <summary>Show the window, or bring the open one to the front.</summary>
    internal static void ShowSingle(PictureStore? pictures, bool disabledByOption, FontCatalog? fonts = null, NamesPack? names = null)
    {
        if (_open is { } w) { w.Activate(); return; }
        _open = new AboutWindow(pictures, disabledByOption, fonts, names);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    internal AboutWindow(PictureStore? pictures, bool disabledByOption, FontCatalog? fonts = null, NamesPack? names = null)
    {
        InitializeComponent();
        Title = Strings.AboutTitle;
        AppTitle.Text = Strings.AppName;
        AppTagline.Text = " — " + Strings.AppTagline;
        VersionText.Text = string.Format(Strings.AboutVersion, Version);
        Tagline.Text = Strings.AboutTagline;
        CopyrightText.Text = Strings.Copyright;
        SiteLabel.Text = Strings.AboutSite;
        SiteLink.NavigateUri = new Uri(Strings.SiteUrl);
        SiteLinkText.Text = Strings.SiteUrl;
        PictosHeader.Text = Strings.AboutPictosHeader;
        Attribution.Text = pictures?.Attribution ?? Strings.ArasaacAttribution;
        ArasaacLink.NavigateUri = new Uri(Strings.ArasaacUrl);
        ArasaacLinkText.Text = Strings.ArasaacLinkText;
        LicenceLink.NavigateUri = new Uri(Strings.LicenceUrl);
        LicenceLinkText.Text = Strings.AboutLicence;
        PackState.Text = pictures != null ? string.Format(Strings.AboutPictosResized, pictures.BuildDate ?? "?")
                       : disabledByOption ? Strings.AboutPictosOffOption : Strings.AboutPictosOffMissing;
        LexiqueHeader.Text = Strings.AboutLexiqueHeader;
        LexiqueText.Text = Strings.AboutLexique + " ";
        LexiqueLink.NavigateUri = new Uri(Strings.LexiqueUrl);
        LexiqueLinkText.Text = Strings.LexiqueLinkText;
        CorporaText.Text = Strings.AboutCorpora;
        // plan 10C: the « Noms propres » pack's own attribution text (study 10c § 3.4, generated from its lock file)
        NamesHeader.Text = Strings.AboutNamesHeader;
        NamesText.Text = names?.Attribution?.Trim() is { Length: > 0 } at ? at : names is null ? Strings.AboutNamesMissing : names.Title;
        NamesSource.Text = names is null ? "" : Strings.AboutNamesSource;
        FontsHeader.Text = Strings.AboutFontsHeader;
        foreach (var spec in FontSpecs.All)
        {
            if (spec.Folder is null) continue;   // Segoe UI ships with Windows
            var copyright = fonts?.Copyright(spec.Font);
            FontsList.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
                Text = copyright is null ? string.Format(Strings.AboutFontMissing, spec.FamilyName)
                                         : string.Format(Strings.AboutFontLine, spec.FamilyName, copyright),
            });
        }
        OflLink.NavigateUri = new Uri(Strings.OflUrl);
        OflLinkText.Text = Strings.AboutOflLink;
        // plans 08a / 08b: one block per installed Piper voice (its name, then the attribution lines of its voice.json),
        // the engines, the licences; the notices open locally, no network
        VoicesHeader.Text = Strings.AboutVoicesHeader;
        var packs = PiperVoicePack.Scan(System.IO.Path.Combine(AppContext.BaseDirectory, "voices"), null);
        if (packs.Count == 0) VoicesList.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Strings.AboutVoiceMissing });
        foreach (var p in packs)
        {
            VoicesList.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2),
                Text = Strings.PiperVoiceName(p.Id),
            });
            foreach (var line in p.Attribution)
                VoicesList.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2), Text = line });
        }
        EnginesText.Text = Strings.AboutEngines;
        LicencesHeader.Text = Strings.AboutLicencesHeader;
        // a screen reader names each section by its header (the Expander's own name would be empty)
        foreach (var (section, header) in new[] { (PictosSection, PictosHeader), (LexiqueSection, LexiqueHeader), (NamesSection, NamesHeader), (FontsSection, FontsHeader),
                                                  (VoicesSection, VoicesHeader), (LicencesSection, LicencesHeader) })
            System.Windows.Automation.AutomationProperties.SetName(section, header.Text);
        AppLicenceText.Text = Strings.AboutAppLicence;
        InteropText.Text = Strings.AboutIa2;
        NoticesLinkText.Text = Strings.AboutNoticesLink;
        CloseButton.Content = Strings.AboutClose;
        Icon = AppIcon(16);
        Logo.Source = AppIcon(48);
        // MaxHeight = the monitor's work area minus a margin: the credits scroll instead of leaving the screen; an Expander
        // opened makes the window grow downwards, it moves up
        Ui.FitToWorkArea(this, margin: 32);
    }

    /// <summary>Smallest frame of the embedded app icon at least <paramref name="min"/> px wide (null on failure: cosmetic).</summary>
    internal static BitmapFrame? AppIcon(int min)
    {
        try
        {
            using var s = typeof(AboutWindow).Assembly.GetManifestResourceStream("Predict.Keyboard.Assets.app.ico");
            if (s is null) return null;
            var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapFrame? best = null;
            foreach (var f in dec.Frames) if (f.PixelWidth >= min && (best is null || f.PixelWidth < best.PixelWidth)) best = f;
            return best ?? dec.Frames[0];
        }
        catch (Exception) { return null; }
    }

    /// <summary>AssemblyInformationalVersion (csproj &lt;Version&gt;), without a "+commit" suffix.</summary>
    public static string Version
    {
        get
        {
            var v = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            int plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception) { /* no browser: nothing to do */ }
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Open THIRD-PARTY-NOTICES.txt (next to the exe) in the user's text editor.</summary>
    private void OnNotices(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        try { if (System.IO.File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception) { /* no associated editor: nothing to do */ }
    }
}
