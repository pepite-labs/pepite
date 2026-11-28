using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>The « Plusieurs élèves » section of Réglages (plan 09): is teacher management on, turn it on, open the
/// Espace enseignant.</summary>
/// <param name="CanSave">false in an in-memory session (the profiles folder is unusable): activation is disabled.</param>
internal sealed record PupilsSection(Func<bool> ManagementOn, Func<bool> CanSave, Action Activate, Action OpenTeacher);

/// <summary>« Afficher le message de bienvenue au prochain démarrage » (plan « aide intégrée »): an app-level flag, the
/// same for every profile — <paramref name="Due"/> reads it, <paramref name="SetDue"/> writes it on « Enregistrer ».</summary>
internal sealed record WelcomeFlag(Func<bool> Due, Action<bool> SetDue);

/// <summary>
/// « Réglages » (D8): edits a working copy of the settings with a live preview of the real bar buttons;
/// « Enregistrer » applies and saves, « Annuler » / Esc / closing discards (owner decision 8). Values locked by the
/// machine file are disabled with « Réglé par l'administrateur ». A normal activating window, one instance;
/// while it is in front the bar is hidden and nothing is tracked (our own window). Every control has an
/// AutomationId (scripted tests drive it through UI Automation).
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private readonly ISettingsTarget _service;
    private readonly PupilsSection? _pupils;
    private readonly Speaker? _speaker;
    private readonly PictureStore? _pictures;
    private readonly FontCatalog _fonts;
    private readonly Action<string>? _log;
    private AppSettings _w;           // working copy
    private bool _loading = true;
    private bool _welcomeTouched;     // ShowWelcome changed in this window (else Enregistrer writes nothing for it)
    private readonly WelcomeFlag? _welcome;
    private string? _defaultVoice;    // D24: piper:siwis when installed, else the first fr-FR SAPI voice: what "no saved voice" means
    // plan 10B: « Indicateur d'orthographe » on the « Mots » tab — a setting of the profile (saved by Enregistrer, layered,
    // lockable), above the word editor (whose changes are saved at once)
    private readonly CheckBox SpellingBox = new() { Content = Strings.SpellingIndicatorLabel, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock LockSpelling = new();
    private readonly CheckBox NamePicturesBox = new() { Content = Strings.NamePicturesLabel, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock LockNamePictures = new();
    // D33 (plan dys-sound-tolerance Phase 2): « Sons que l'élève confond » — five pairs, a setting of the profile like the two above
    private readonly CheckBox SoundChJBox = new() { Content = Strings.SoundPairChJLabel, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox SoundFVBox = new() { Content = Strings.SoundPairFVLabel, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox SoundKGBox = new() { Content = Strings.SoundPairKGLabel, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox SoundTDBox = new() { Content = Strings.SoundPairTDLabel, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox SoundPBBox = new() { Content = Strings.SoundPairPBLabel, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock LockSoundChJ = new(), LockSoundFV = new(), LockSoundKG = new(), LockSoundTD = new(), LockSoundPB = new();

    /// <summary>Open Réglages for <paramref name="target"/> (one window: another profile's request replaces it).</summary>
    internal static void ShowSingle(ISettingsTarget target, PupilsSection? pupils, Speaker? speaker, PictureStore? pictures, FontCatalog fonts,
                                    Action<string>? log, WordsContext? words = null, WelcomeFlag? welcome = null)
    {
        if (_open is { } w)
        {
            if (w._service.ProfileId == target.ProfileId) { w.Activate(); return; }
            w.Close();
        }
        var win = _open = new SettingsWindow(target, pupils, speaker, pictures, fonts, log, words, welcome);
        win.Closed += (_, _) => { if (_open == win) _open = null; };
        win.Show();
        win.Activate();
        log?.Invoke($"settings window opened ({target.ProfileId})");
    }

    /// <summary>A profile switch: the open window shows the previous profile (plan 09).</summary>
    internal static void CloseOpen() => _open?.Close();

    /// <summary>Teacher management was turned on / off: the « Plusieurs élèves » section follows.</summary>
    internal static void RefreshPupils() => _open?.ShowPupils();

    internal SettingsWindow(ISettingsTarget service, PupilsSection? pupils, Speaker? speaker, PictureStore? pictures, FontCatalog fonts,
                            Action<string>? log, WordsContext? words = null, WelcomeFlag? welcome = null)
    {
        _welcome = welcome;
        InitializeComponent();
        // plan 10A: the « Mots » tab edits the words of the profile shown (saved at once, not by Enregistrer); plan 10B: the
        // spelling indicator setting above it
        System.Windows.Automation.AutomationProperties.SetAutomationId(SpellingBox, "SpellingIndicator");
        LockSpelling.Style = (Style)FindResource("Locked");
        var spellRow = new StackPanel { Orientation = Orientation.Horizontal };
        spellRow.Children.Add(SpellingBox);
        spellRow.Children.Add(LockSpelling);
        spellRow.Children.Add(HelpButtonFor("orthographe", "HelpSpelling", Strings.SpellingIndicatorLabel.Replace("_", ""), new Thickness(12, 0, 0, 0)));
        var spellHint = new TextBlock { Text = Strings.SpellingIndicatorHint, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, Opacity = 0.75,
                                        Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetAutomationId(spellHint, "SpellingIndicatorHint");
        System.Windows.Automation.AutomationProperties.SetHelpText(SpellingBox, Strings.SpellingIndicatorHint);
        var spellPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 10), Tag = "aide:orthographe" };
        spellPanel.Children.Add(spellRow);
        spellPanel.Children.Add(spellHint);
        // plan 10C: « Images pour les noms propres » (flags of the « Noms propres » pack, the teacher's pictures of names)
        System.Windows.Automation.AutomationProperties.SetAutomationId(NamePicturesBox, "NamePictures");
        System.Windows.Automation.AutomationProperties.SetHelpText(NamePicturesBox, Strings.NamePicturesHint);
        LockNamePictures.Style = (Style)FindResource("Locked");
        var namesRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        namesRow.Children.Add(NamePicturesBox);
        namesRow.Children.Add(LockNamePictures);
        spellPanel.Children.Add(namesRow);
        spellPanel.Children.Add(new TextBlock { Text = Strings.NamePicturesHint, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, Opacity = 0.75,
                                                Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left });
        spellPanel.Children.Add(SoundPairsPanel());
        var wordsDock = new DockPanel();
        DockPanel.SetDock(spellPanel, Dock.Top);
        wordsDock.Children.Add(spellPanel);
        if (words != null)
            wordsDock.Children.Add(new WordEditor(words, service.ProfileId, teacher: false,
                                                  HelpButtonFor("mes-mots", "HelpMyWords", Strings.MyWordsList, new Thickness(10, 0, 0, 0)))
                                   { Tag = "aide:mes-mots" });
        // 0.11.0: « Classe »'s Mots tab is taller than a 768-pixel screen: the window never outgrows the work area and the
        // tab scrolls (reviewer 2026-11-27)
        WordsTab.Content = new ScrollViewer { Content = wordsDock, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                              HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        MaxHeight = SystemParameters.WorkArea.Height;
        SizeChanged += (_, _) => KeepOnScreen();
        // Enter in the word search box must not « Enregistrer » and close the window
        Tabs.SelectionChanged += (_, e) => { if (e.OriginalSource == Tabs) SaveButton.IsDefault = Tabs.SelectedItem != WordsTab; };
        _service = service;
        _pupils = pupils;
        Title = service.ProfileName is { } pn ? string.Format(Strings.SettingsTitleFor, pn) : Strings.SettingsTitle;
        _speaker = speaker;
        _pictures = pictures;
        _fonts = fonts;
        _log = log;
        _w = service.Current;
        Icon = AboutWindow.AppIcon(16);

        for (int i = AppSettings.MinWords; i <= AppSettings.MaxWords; i++) WordCount.Items.Add(i);
        foreach (var spec in FontSpecs.All)
        {
            bool ok = fonts.IsAvailable(spec.Font);
            var name = FontLabel(spec.Font);
            var item = new ComboBoxItem
            {
                Content = ok ? name : string.Format(Strings.FontMissing, name), Tag = spec.Font, IsEnabled = ok,
                FontFamily = fonts.Resolve(spec.Font).Family, FontWeight = fonts.Resolve(spec.Font).Weight,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "Font-" + AppSettings.FontNames[spec.Font]);
            FontBox.Items.Add(item);
        }
        // 2026-11-25 owner: « Clic sur un mot » and « Touches F1–F8 » (listen before inserting)
        foreach (var (mode, text) in new[] { (ClickMode.Insert, Strings.ClickInsert), (ClickMode.ListenFirst, Strings.ClickListenFirst),
                                             (ClickMode.ListenDouble, Strings.ClickListenDouble) })
        {
            var item = new ComboBoxItem { Content = text, Tag = mode };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "ClickMode-" + AppSettings.ClickModeNames[mode]);
            ClickModeBox.Items.Add(item);
        }
        foreach (var (listenFirst, text, id) in new[] { (false, Strings.KeysInsert, "insert"), (true, Strings.KeysListenFirst, "listenFirst") })
        {
            var item = new ComboBoxItem { Content = text, Tag = listenFirst };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "KeysMode-" + id);
            KeysModeBox.Items.Add(item);
        }
        // 0.11.0 (owner 2026-11-27): « Copier la sélection par Ctrl+C… » — Writer only (not set) / Partout / Jamais
        foreach (var (value, text, id) in new (bool?, string, string)[] { (null, Strings.ReadingClipboardWriter, "writer"),
                                                                           (true, Strings.ReadingClipboardEverywhere, "everywhere"),
                                                                           (false, Strings.ReadingClipboardNever, "never") })
        {
            var item = new ComboBoxItem { Content = text, Tag = new ClipboardChoice(value) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "ReadingClipboard-" + id);
            ReadingClipboard.Items.Add(item);
        }
        Voice.Items.Add(new ComboBoxItem { Content = Strings.VoiceLoading, IsEnabled = false });
        Voice.SelectedIndex = 0;

        Load(_w);
        WireEvents();
        Lock();
        _loading = false;
        RefreshPreview();
        Loaded += (_, _) => CentreOnPrimary();
        WireHelp();
        _ = FillVoicesAsync();
        PupilsButton.Click += (_, _) =>
        {
            if (_pupils is null) return;
            if (_pupils.ManagementOn()) _pupils.OpenTeacher(); else _pupils.Activate();
        };
        ShowPupils();
    }

    /// <summary>« Plusieurs élèves »: off → « Activer la gestion enseignant… » + one line; on → « Ouvrir l'Espace
    /// enseignant… ».</summary>
    private void ShowPupils()
    {
        bool on = _pupils?.ManagementOn() == true;
        bool canSave = _pupils?.CanSave() != false;
        PupilsButton.Content = on ? Strings.OpenTeacherSpace : Strings.ActivateManagement;
        PupilsHint.Text = on ? Strings.ManagementOnHint : canSave ? Strings.ActivateManagementHint : Strings.CannotSaveHint;
        PupilsButton.IsEnabled = _pupils != null && (on || canSave);
        Title = _service.ProfileName is { } pn && on ? string.Format(Strings.SettingsTitleFor, pn) : Strings.SettingsTitle;
    }

    /// <summary>Plan « aide intégrée »: the « ? » buttons of the « Barre et voix » sections (declared in XAML with a
    /// <c>aide:&lt;section&gt;</c> tag) get their screen-reader name and open their section; F1 (ApplicationCommands.Help,
    /// this window only) opens the section holding the focus. The hook never sees F1 here: our own window is in front
    /// (KeyboardController.OnKeyDown returns before the F-keys, and the bar is hidden).</summary>
    private void WireHelp()
    {
        foreach (var b in Descendants<Button>(Tabs))
        {
            if (b.Tag is not string tag || !tag.StartsWith(HelpTag, StringComparison.Ordinal)) continue;
            if (b.Parent is Panel head && head.Children.OfType<TextBlock>().FirstOrDefault() is { } title)
                System.Windows.Automation.AutomationProperties.SetName(b, Strings.HelpButtonName(title.Text));
            var name = tag.Substring(HelpTag.Length);
            b.Click += (_, _) => OpenHelp(HelpLinks.ForSettings(name, wordsTab: false), "button");
        }
        CommandBindings.Add(new System.Windows.Input.CommandBinding(System.Windows.Input.ApplicationCommands.Help, (_, e) =>
        {
            e.Handled = true;
            OpenHelp(HelpLinks.ForSettings(SectionAt(System.Windows.Input.Keyboard.FocusedElement as DependencyObject),
                                           Tabs.SelectedItem == WordsTab), "F1");
        }));
    }

    private const string HelpTag = "aide:";

    /// <summary>A « ? » button built in code (the « Mots » tab).</summary>
    private Button HelpButtonFor(string section, string automationId, string sectionTitle, Thickness margin)
    {
        var b = new Button { Style = (Style)FindResource("HelpButton"), Margin = margin, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, automationId);
        System.Windows.Automation.AutomationProperties.SetName(b, Strings.HelpButtonName(sectionTitle));
        b.Click += (_, _) => OpenHelp(HelpLinks.ForSettings(section, wordsTab: true), "button");
        return b;
    }

    /// <summary>« Sons que l'élève confond » (D33): the header with its « ? » (→ <c>reglages.html#sons</c>), the five boxes on one
    /// wrapping row (each with its lock note) to keep the Mots tab short on a 768-pixel screen, and the hint.</summary>
    private StackPanel SoundPairsPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0), Tag = HelpTag + "sons" };
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = Strings.SoundPairsHeader, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetAutomationId(title, "SoundPairsHeader");
        head.Children.Add(title);
        head.Children.Add(HelpButtonFor("sons", "HelpSoundPairs", Strings.SoundPairsHeader, new Thickness(12, 0, 0, 0)));
        panel.Children.Add(head);
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 620,
                                  HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (box, note, id) in new[] { (SoundChJBox, LockSoundChJ, "SoundPairChJ"), (SoundFVBox, LockSoundFV, "SoundPairFV"),
                                                (SoundKGBox, LockSoundKG, "SoundPairKG"), (SoundTDBox, LockSoundTD, "SoundPairTD"),
                                                (SoundPBBox, LockSoundPB, "SoundPairPB") })
        {
            System.Windows.Automation.AutomationProperties.SetAutomationId(box, id);
            System.Windows.Automation.AutomationProperties.SetHelpText(box, Strings.SoundPairsHint);
            note.Style = (Style)FindResource("Locked");
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 18, 2) };
            item.Children.Add(box);
            item.Children.Add(note);
            row.Children.Add(item);
        }
        panel.Children.Add(row);
        var hint = new TextBlock { Text = Strings.SoundPairsHint, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, Opacity = 0.75,
                                   Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetAutomationId(hint, "SoundPairsHint");
        panel.Children.Add(hint);
        return panel;
    }

    /// <summary>The section (<c>aide:&lt;name&gt;</c> tag) holding an element: walk up the visual tree, else the logical
    /// one (a popup's items); null = none (the window's buttons, an open drop-down).</summary>
    private static string? SectionAt(DependencyObject? d)
    {
        for (int guard = 0; d != null && guard < 200; guard++)
        {
            if (d is FrameworkElement { Tag: string tag } && tag.StartsWith(HelpTag, StringComparison.Ordinal)) return tag.Substring(HelpTag.Length);
            d = (d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : null)
                ?? LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OpenHelp(HelpTopic topic, string how)
    {
        _log?.Invoke($"settings: help ({how}) {topic}");
        HelpUi.Open(topic, this, _log);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var x in Descendants<T>(child)) yield return x;
        }
    }

    private static string FontLabel(BarFont f) => f switch
    {
        BarFont.OpenDyslexic => Strings.FontOpenDyslexic,
        BarFont.Lexend => Strings.FontLexend,
        BarFont.Andika => Strings.FontAndika,
        _ => Strings.FontSegoe,
    };

    /// <summary>A tab switch resizes the window (SizeToContent): move it up when its bottom would leave the work area.</summary>
    private void KeepOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        if (Top + ActualHeight > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - ActualHeight);
    }

    private void CentreOnPrimary()
    {
        var wa = SystemParameters.WorkArea;   // primary monitor, DIP
        Left = wa.Left + Math.Max(0, (wa.Width - ActualWidth) / 2);
        Top = wa.Top + Math.Max(0, (wa.Height - ActualHeight) / 2);
    }

    private void Load(AppSettings s)
    {
        WordCount.SelectedItem = s.WordCount;
        (s.Size switch { BarSize.S => SizeS, BarSize.L => SizeL, BarSize.XL => SizeXL, _ => SizeM }).IsChecked = true;
        FontBox.SelectedItem = FontBox.Items.Cast<ComboBoxItem>().First(i => (BarFont)i.Tag == s.Font);
        (s.Spacing switch { LetterSpacing.Wider => SpacingWider, LetterSpacing.Widest => SpacingWidest, _ => SpacingNormal }).IsChecked = true;
        Pictures.IsChecked = s.Pictures;
        if (_pictures is null) { Pictures.IsEnabled = false; PicturesMissing.Visibility = Visibility.Visible; }
        Keys.IsChecked = s.Keys == KeyMode.F1F8;
        ClickModeBox.SelectedItem = ClickModeBox.Items.Cast<ComboBoxItem>().First(i => (ClickMode)i.Tag == s.Click);
        KeysModeBox.SelectedItem = KeysModeBox.Items.Cast<ComboBoxItem>().First(i => (bool)i.Tag == s.KeysListenFirst);
        ClickBandNote.Visibility = s.ListenButton ? Visibility.Visible : Visibility.Collapsed;
        ListenShiftBox.IsChecked = s.ListenShiftKeys;
        ListenButtonBox.IsChecked = s.ListenButton;
        // the hidden "french-nbsp" (JSON only) is French typography too
        (s.Punctuation == PunctuationSpacing.None ? PunctNone : PunctFrench).IsChecked = true;
        (s.Position switch { BarPosition.Top => PosTop, BarPosition.Bottom => PosBottom, BarPosition.Free => PosFree, _ => PosCaret }).IsChecked = true;
        SpeakInserted.IsChecked = s.Speech.SpeakInserted;
        SpeakWords.IsChecked = s.Speech.SpeakWords;
        SpeakSentences.IsChecked = s.Speech.SpeakSentences;
        RereadEdits.IsChecked = s.Speech.RereadEdits;
        ReadingEnabled.IsChecked = s.Reading.Enabled;
        ReadingPictures.IsChecked = s.ReadingPictures;
        if (_pictures is null) ReadingPictures.IsEnabled = false;
        ReadingClipboard.SelectedItem = ReadingClipboard.Items.Cast<ComboBoxItem>().First(i => ((ClipboardChoice)i.Tag).Value == s.Reading.ClipboardFallback);
        // locked by the machine file: its value; else the app-level « shown » flag (the profile's value never decides now)
        ShowWelcome.IsChecked = _welcome is null || _service.IsLocked("start.welcome") ? s.Welcome : _welcome.Due();
        SpellingBox.IsChecked = s.SpellingIndicator;
        NamePicturesBox.IsChecked = s.NamePictures;
        SoundChJBox.IsChecked = s.Sounds.ChJ;
        SoundFVBox.IsChecked = s.Sounds.FV;
        SoundKGBox.IsChecked = s.Sounds.KG;
        SoundTDBox.IsChecked = s.Sounds.TD;
        SoundPBBox.IsChecked = s.Sounds.PB;
        Rate.Value = s.Speech.Rate;
        Volume.Value = s.Speech.Volume;
        FreeHint.Visibility = s.Position == BarPosition.Free ? Visibility.Visible : Visibility.Collapsed;
        ShowTuning();
    }

    /// <summary>A position of the Ctrl+C choice (a boxed bool? cannot tell null from « no tag »).</summary>
    private sealed record ClipboardChoice(bool? Value);

    /// <summary>The chosen speed and volume as words next to the sliders, and in their UI Automation name, so a screen
    /// reader says « Vitesse : Un peu lente (−2) » rather than a bare number (owner, 2026-11-14).</summary>
    private void ShowTuning()
    {
        var rate = Strings.RateValue((int)Math.Round(Rate.Value));
        var volume = Strings.VolumeValue((int)Math.Round(Volume.Value));
        RateValue.Text = rate;
        VolumeValue.Text = volume;
        System.Windows.Automation.AutomationProperties.SetName(Rate, Strings.SliderName(Strings.RateLabel, rate));
        System.Windows.Automation.AutomationProperties.SetName(Volume, Strings.SliderName(Strings.VolumeLabel, volume));
        System.Windows.Automation.AutomationProperties.SetHelpText(Rate, rate);
        System.Windows.Automation.AutomationProperties.SetHelpText(Volume, volume);
    }

    private void WireEvents()
    {
        WordCount.SelectionChanged += (_, _) => Edit(w => w with { WordCount = WordCount.SelectedItem is int n ? n : w.WordCount });
        foreach (var (rb, v) in new[] { (SizeS, BarSize.S), (SizeM, BarSize.M), (SizeL, BarSize.L), (SizeXL, BarSize.XL) })
            rb.Checked += (_, _) => Edit(w => w with { Size = v });
        FontBox.SelectionChanged += (_, _) => Edit(w => w with { Font = FontBox.SelectedItem is ComboBoxItem { Tag: BarFont f } ? f : w.Font });
        foreach (var (rb, v) in new[] { (SpacingNormal, LetterSpacing.Normal), (SpacingWider, LetterSpacing.Wider), (SpacingWidest, LetterSpacing.Widest) })
            rb.Checked += (_, _) => Edit(w => w with { Spacing = v });
        // Checked/Unchecked rather than Click: also raised by UI Automation's Toggle and by Space
        void OnToggle(System.Windows.Controls.Primitives.ToggleButton b, Func<AppSettings, bool, AppSettings> f)
        {
            b.Checked += (_, _) => Edit(w => f(w, true));
            b.Unchecked += (_, _) => Edit(w => f(w, false));
        }
        OnToggle(Pictures, (w, on) => w with { Pictures = on });
        OnToggle(Keys, (w, on) => w with { Keys = on ? KeyMode.F1F8 : KeyMode.Off });
        // 2026-11-25: « Touches F1–F8 » and Maj + F1…F8 need the F-keys (disabled, not changed, while they are off)
        Keys.Checked += (_, _) => SyncListenControls();
        Keys.Unchecked += (_, _) => SyncListenControls();
        ClickModeBox.SelectionChanged += (_, _) => Edit(w => w with { Click = ClickModeBox.SelectedItem is ComboBoxItem { Tag: ClickMode m } ? m : w.Click });
        KeysModeBox.SelectionChanged += (_, _) => Edit(w => w with { KeysListenFirst = KeysModeBox.SelectedItem is ComboBoxItem { Tag: bool b } ? b : w.KeysListenFirst });
        OnToggle(ListenShiftBox, (w, on) => w with { ListenShiftKeys = on });
        OnToggle(ListenButtonBox, (w, on) => w with { ListenButton = on });
        // the speaker band (D) on: a click on the word inserts at once whatever the click mode (owner answer 4) — greyed
        ListenButtonBox.Checked += (_, _) => SyncListenControls();
        ListenButtonBox.Unchecked += (_, _) => SyncListenControls();
        PunctFrench.Checked += (_, _) => Edit(w => w with
        { Punctuation = w.Punctuation == PunctuationSpacing.None ? PunctuationSpacing.French : w.Punctuation });
        PunctNone.Checked += (_, _) => Edit(w => w with { Punctuation = PunctuationSpacing.None });
        foreach (var (rb, v) in new[] { (PosCaret, BarPosition.Caret), (PosTop, BarPosition.Top), (PosBottom, BarPosition.Bottom), (PosFree, BarPosition.Free) })
            rb.Checked += (_, _) =>
            {
                FreeHint.Visibility = v == BarPosition.Free ? Visibility.Visible : Visibility.Collapsed;
                Edit(w => w with { Position = v });
            };
        OnToggle(SpeakInserted, (w, on) => w with { Speech = w.Speech with { SpeakInserted = on } });
        OnToggle(SpeakWords, (w, on) => w with { Speech = w.Speech with { SpeakWords = on } });
        OnToggle(SpeakSentences, (w, on) => w with { Speech = w.Speech with { SpeakSentences = on } });
        OnToggle(RereadEdits, (w, on) => w with { Speech = w.Speech with { RereadEdits = on } });
        OnToggle(ReadingEnabled, (w, on) => w with { Reading = w.Reading with { Enabled = on } });
        OnToggle(ReadingPictures, (w, on) => w with { Reading = w.Reading with { Pictures = on } });
        ReadingClipboard.SelectionChanged += (_, _) => Edit(w => ReadingClipboard.SelectedItem is ComboBoxItem { Tag: ClipboardChoice c }
            ? w with { Reading = w.Reading with { ClipboardFallback = c.Value } } : w);
        OnToggle(SpellingBox, (w, on) => w with { SpellingIndicator = on });
        OnToggle(NamePicturesBox, (w, on) => w with { NamePictures = on });
        OnToggle(SoundChJBox, (w, on) => w with { Sounds = w.Sounds with { ChJ = on } });
        OnToggle(SoundFVBox, (w, on) => w with { Sounds = w.Sounds with { FV = on } });
        OnToggle(SoundKGBox, (w, on) => w with { Sounds = w.Sounds with { KG = on } });
        OnToggle(SoundTDBox, (w, on) => w with { Sounds = w.Sounds with { TD = on } });
        OnToggle(SoundPBBox, (w, on) => w with { Sounds = w.Sounds with { PB = on } });
        ShowWelcome.Checked += (_, _) => { if (!_loading) _welcomeTouched = true; };
        ShowWelcome.Unchecked += (_, _) => { if (!_loading) _welcomeTouched = true; };
        Voice.SelectionChanged += (_, _) =>
        {
            if (Voice.SelectedItem is ComboBoxItem { Tag: string name })
            {
                Edit(w => w with { Speech = w.Speech with { Voice = name == _defaultVoice && w.Speech.Voice is null ? null : name } });
                // a Piper voice takes 2.5–5 s to load: start it on selection (not only on « Écouter »), so Enregistrer
                // usually finds it Ready; unloaded 30 s later when not saved (Annuler leaves the configured voice)
                if (!_loading && VoiceId.Parse(name) is { IsPiper: true }) _speaker?.Preload(name);
                ShowVoiceStatus(name, _speaker?.VoiceState(name) ?? PiperState.NotLoaded, announce: false);
            }
        };
        Rate.ValueChanged += (_, _) => { ShowTuning(); Edit(w => w with { Speech = w.Speech with { Rate = (int)Math.Round(Rate.Value) } }); };
        Volume.ValueChanged += (_, _) => { ShowTuning(); Edit(w => w with { Speech = w.Speech with { Volume = (int)Math.Round(Volume.Value) } }); };
        Listen.Click += (_, _) => { _speaker?.Preview(Strings.ListenText, _w.Speech); _log?.Invoke("settings: listen (preview voice)"); };
        SaveButton.Click += (_, _) =>
        {
            _log?.Invoke("settings: Enregistrer");
            // start.welcome of the profile is never written here any more (ForSave keeps the current value); the box
            // drives the app-level flag
            _service.Apply(StartupPolicy.ForSave(_w, _service.Current));
            if (_welcomeTouched && _welcome != null && !_service.IsLocked("start.welcome")) _welcome.SetDue(ShowWelcome.IsChecked == true);
            Close();
        };
        CancelButton.Click += (_, _) => { _log?.Invoke("settings: Annuler"); Close(); };
        // the load state of the selected voice, live (status line; a screen reader announces it: LiveSetting polite)
        if (_speaker is { } sp)
        {
            void OnVoiceState(string id, PiperState _) => Dispatcher.BeginInvoke(() =>
            {
                // the state now, not the event's (events of one voice may arrive in another order than they happened)
                if (Voice.SelectedItem is ComboBoxItem { Tag: string sel } && sel == id) ShowVoiceStatus(id, sp.VoiceState(id), announce: true);
            });
            sp.VoiceStateChanged += OnVoiceState;
            Closed += (_, _) => sp.VoiceStateChanged -= OnVoiceState;
        }
    }

    /// <summary>« Chargement de la voix Pierre… » / « ✓ Voix Pierre prête » / unavailable; nothing for a SAPI voice.</summary>
    private void ShowVoiceStatus(string voiceId, PiperState state, bool announce)
    {
        var text = Strings.VoiceStatus(voiceId, state);
        // a voice not loaded yet whose preload was just asked: the Loading event follows at once
        VoiceStatus.Text = text ?? "";
        VoiceStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (announce && text != null)
            (System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(VoiceStatus)
             ?? System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(VoiceStatus))
                ?.RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }

    private void Edit(Func<AppSettings, AppSettings> f)
    {
        if (_loading) return;
        _w = f(_w).Validated();
        RefreshPreview();
    }

    /// <summary>Machine-locked values (D11): control disabled, note shown.</summary>
    private void Lock()
    {
        void L(string path, TextBlock note, params Control[] controls)
        {
            if (!_service.IsLocked(path)) return;
            foreach (var c in controls) c.IsEnabled = false;
            note.Visibility = Visibility.Visible;
        }
        L("words.count", LockWordCount, WordCount);
        L("words.size", LockSize, SizeS, SizeM, SizeL, SizeXL);
        L("words.font", LockFont, FontBox);
        L("words.spacing", LockSpacing, SpacingNormal, SpacingWider, SpacingWidest);
        L("words.pictures", LockPictures, Pictures);
        L("words.keys", LockKeys, Keys);
        L("words.clickMode", LockClickMode, ClickModeBox);
        L("words.keysListenFirst", LockKeysMode, KeysModeBox);
        L("words.listenShiftKeys", LockListenShift, ListenShiftBox);
        L("words.listenButton", LockListenButton, ListenButtonBox);
        SyncListenControls();
        L("text.punctuationSpace", LockPunct, PunctFrench, PunctNone);
        L("bar.position", LockPosition, PosCaret, PosTop, PosBottom, PosFree);
        L("speech.speakInserted", LockSpeakInserted, SpeakInserted);
        L("speech.speakWords", LockSpeakWords, SpeakWords);
        L("speech.speakSentences", LockSpeakSentences, SpeakSentences);
        L("speech.rereadEdits", LockRereadEdits, RereadEdits);
        L("speech.voice", LockVoice, Voice);
        L("reading.enabled", LockReadingEnabled, ReadingEnabled);
        L("reading.pictures", LockReadingPictures, ReadingPictures);
        L("reading.clipboardFallback", LockReadingClipboard, ReadingClipboard);
        L("start.welcome", LockShowWelcome, ShowWelcome);
        L("words.spellingIndicator", LockSpelling, SpellingBox);
        L("words.namePictures", LockNamePictures, NamePicturesBox);
        L("words.soundPairs.chj", LockSoundChJ, SoundChJBox);
        L("words.soundPairs.fv", LockSoundFV, SoundFVBox);
        L("words.soundPairs.kg", LockSoundKG, SoundKGBox);
        L("words.soundPairs.td", LockSoundTD, SoundTDBox);
        L("words.soundPairs.pb", LockSoundPB, SoundPBBox);
        L("speech.rate", LockSpeechTuning, Rate);
        L("speech.volume", LockSpeechTuning, Volume);
    }

    /// <summary>2026-11-25 (listen before inserting): « Touches F1–F8 » and Maj + F1…F8 are greyed while the F-keys are
    /// off; « Clic sur un mot » is greyed, with its note, while the speaker band is on (a click on the word then inserts at
    /// once). A machine lock keeps its control disabled.</summary>
    private void SyncListenControls()
    {
        bool keys = Keys.IsChecked == true, band = ListenButtonBox.IsChecked == true;
        KeysModeBox.IsEnabled = keys && !_service.IsLocked("words.keysListenFirst");
        ListenShiftBox.IsEnabled = keys && !_service.IsLocked("words.listenShiftKeys");
        ClickModeBox.IsEnabled = !band && !_service.IsLocked("words.clickMode");
        ClickBandNote.Visibility = band ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPreview()
    {
        Preview.Children.Clear();
        var look = new BarLook(BarMetrics.For(_w.Size), _pictures != null && _w.Pictures, _w.Keys == KeyMode.F1F8,
                               _fonts.Resolve(_w.Font), _w.Spacing, _w.Click, _w.KeysListenFirst, _w.ListenButton);
        var styles = BarStyles.From(this);
        var kinds = new[] { MatchKind.Prefix, MatchKind.Phonetic, MatchKind.Edit };
        for (int i = 0; i < Strings.PreviewWords.Length; i++)
        {
            var word = Strings.PreviewWords[i];
            var b = BarBuilder.MakeTile(i, new Suggestion(word, 0, kinds[i % 3]), look, styles, out var slot, out _).Root;   // with D: the band under the word
            b.IsHitTestVisible = false;
            if (slot is { } sl && _pictures?.GetNow(word) is System.Windows.Media.ImageSource img)
            {
                sl.Img.Source = img;
                sl.Slot.Background = BarBuilder.White;
            }
            Preview.Children.Add(b);
        }
    }

    private async System.Threading.Tasks.Task FillVoicesAsync()
    {
        IReadOnlyList<VoiceEntry> voices = Array.Empty<VoiceEntry>();
        if (_speaker != null)
        {
            try { voices = await _speaker.Voices; }   // the speech thread enumerates them; the UI never waits
            catch (Exception) { }
        }
        bool wasLoading = _loading;
        _loading = true;
        Voice.Items.Clear();
        // the router's rule (EngineRouter.DefaultVoice): siwis, never another Piper voice implicitly (plan 08b)
        _defaultVoice = voices.FirstOrDefault(v => v.Id == EngineRouter.DefaultPiperVoice)?.Id
                        ?? voices.FirstOrDefault(v => v.Engine == VoiceId.Sapi && v.Culture.StartsWith("fr", StringComparison.OrdinalIgnoreCase))?.Id
                        ?? voices.FirstOrDefault(v => v.Engine == VoiceId.Sapi)?.Id ?? voices.FirstOrDefault()?.Id;
        foreach (var v in voices)
            Voice.Items.Add(new ComboBoxItem { Content = Label(v), Tag = v.Id });
        var saved = VoiceId.Normalize(_w.Speech.Voice);
        if (saved != null && voices.All(v => v.Id != saved) && VoiceId.Parse(saved) is { } missing)
            Voice.Items.Add(new ComboBoxItem
            {
                Content = string.Format(Strings.VoiceMissing, missing.IsPiper ? Strings.PiperVoiceName(missing.Name) : missing.Name), Tag = saved,
            });
        if (Voice.Items.Count == 0) Voice.Items.Add(new ComboBoxItem { Content = Strings.VoiceNone, IsEnabled = false });
        var want = saved ?? _defaultVoice;
        Voice.SelectedItem = Voice.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == want) ?? Voice.Items[0];
        _loading = wasLoading;
        _log?.Invoke($"settings window: {voices.Count} voices listed");
    }

    /// <summary>« Siwis — voix naturelle (femme) » for a Piper voice, « &lt;name&gt; (fr-FR) » for a Windows voice.</summary>
    private static string Label(VoiceEntry v) => v.Engine == VoiceId.Piper ? Strings.PiperVoiceName(v.Name) : $"{v.Name} ({v.Culture})";
}
