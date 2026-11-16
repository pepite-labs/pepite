using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Predict.Keyboard;

/// <summary>
/// « Qui es-tu ? » (plan 09 step 12, D29 Q2): one large tile per pupil (96 DIP picture, the name in the class font, a
/// coloured border); a click or the arrows select a tile and <b>say the name</b> (never logged); « C'est moi ✓ », Enter or
/// a double click confirm; Esc keeps the current profile. UIA name of a tile « Profil de Léa ». Opened from the tray
/// (« Changer de profil… »), at the start of a Windows session, and by « Fin de session ». One at a time; our own
/// window, so the bar stays hidden while it is in front.
/// </summary>
internal sealed class ProfilePicker : Window
{
    private static ProfilePicker? _open;

    private readonly ProfileService _svc;
    private readonly Speaker? _speaker;
    private readonly string _reason;
    private readonly Action<string>? _log;
    private readonly ListBox _list;
    private bool _speakOnSelect;

    public static void ShowSingle(ProfileService svc, PictureStore? pictures, FontCatalog fonts, Speaker? speaker, string reason, Action<string>? log)
    {
        if (_open is { } w) { w.Activate(); return; }
        var pupils = SessionPolicy.Offered(svc.Store);   // « Profils proposés sur ce poste » (9B)
        if (pupils.Count == 0) { log?.Invoke("picker: no pupil profile"); return; }
        var win = _open = new ProfilePicker(svc, pictures, fonts, speaker, reason, log);
        win.Closed += (_, _) => { if (_open == win) _open = null; };
        Ui.ShowInFront(win);
        log?.Invoke($"picker shown ({reason}, {pupils.Count} pupils)");
    }

    public static void CloseOpen() => _open?.Close();

    private ProfilePicker(ProfileService svc, PictureStore? pictures, FontCatalog fonts, Speaker? speaker, string reason, Action<string>? log)
    {
        _svc = svc;
        _speaker = speaker;
        _reason = reason;
        _log = log;
        Ui.Setup(this, Strings.PickerTitle, "ProfilePicker");
        var classFont = svc.Settings.EffectiveOf(null, svc.Store.Class?.Profile.Layer).Effective.Font;
        var rf = fonts.Resolve(classFont);

        var body = new DockPanel { Margin = new Thickness(24, 18, 24, 18) };
        var head = Ui.Heading(Strings.PickerHeading, "PickerHeading", 30);
        head.FontFamily = rf.Family;
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        var hint = Ui.Text(Strings.PickerHint, "PickerHint", 14);
        hint.Margin = new Thickness(0, 6, 0, 10);
        hint.Opacity = 0.8;
        DockPanel.SetDock(hint, Dock.Top);
        body.Children.Add(hint);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = Ui.Button(Strings.PickerConfirm, "PickerConfirm", (_, _) => Confirm(), 160);
        ok.IsDefault = true;
        ok.FontSize = 18;
        ok.Margin = new Thickness(0);
        row.Children.Add(ok);
        DockPanel.SetDock(row, Dock.Bottom);
        body.Children.Add(row);

        _list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        AutomationProperties.SetAutomationId(_list, "PickerList");
        AutomationProperties.SetName(_list, Strings.PickerHeading);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(WrapPanel));
        _list.ItemsPanel = new ItemsPanelTemplate(panel);
        var wa = SystemParameters.WorkArea;
        _list.MaxWidth = Math.Min(wa.Width * 0.85, 6 * 168);
        _list.MaxHeight = wa.Height * 0.65;
        int sel = -1;
        foreach (var e in SessionPolicy.Offered(svc.Store))
        {
            var p = e.Profile;
            var tile = new StackPanel { Width = 140, Margin = new Thickness(6) };
            tile.Children.Add(AvatarView.Create(p, pictures, 96));
            tile.Children.Add(new TextBlock
            {
                Text = p.Name, FontFamily = rf.Family, FontWeight = rf.Weight, FontSize = 20, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            });
            var item = new ListBoxItem
            {
                Content = tile, Tag = p.Id, Padding = new Thickness(4), Margin = new Thickness(4), HorizontalContentAlignment = HorizontalAlignment.Center,
                BorderThickness = new Thickness(4), BorderBrush = AvatarView.Brush(p.Avatar.Color),
            };
            AutomationProperties.SetName(item, string.Format(Strings.PickerTileName, p.Name));
            AutomationProperties.SetHelpText(item, string.Format(Strings.PickerTileHelp, p.Name, Strings.ColorName(p.Avatar.Color)));
            AutomationProperties.SetAutomationId(item, "Pick-" + p.Id);
            item.MouseDoubleClick += (_, _) => Confirm();
            if (p.Id == svc.Store.ActiveId) sel = _list.Items.Count;
            _list.Items.Add(item);
        }
        _list.SelectionChanged += (_, _) =>
        {
            if (_speakOnSelect && _list.SelectedItem is ListBoxItem { Tag: string id } && svc.Store.Get(id) is { } pe) _speaker?.SayName(pe.Profile.Name);
        };
        body.Children.Add(_list);
        Content = body;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _log?.Invoke($"picker: kept {svc.Store.ActiveId} (Esc)"); Close(); e.Handled = true; }
        };
        Loaded += (_, _) =>
        {
            if (_list.Items.Count > 0)
            {
                _list.SelectedIndex = sel >= 0 ? sel : 0;
                (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) as ListBoxItem)?.Focus();
            }
            _speakOnSelect = true;   // the first selection is ours: nothing said until the pupil moves or clicks
        };
    }

    private void Confirm()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: string id }) return;
        Close();
        _svc.Switch(id, _reason);
    }
}
