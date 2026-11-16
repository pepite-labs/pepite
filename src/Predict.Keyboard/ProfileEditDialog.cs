using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Predict.Keyboard;

/// <summary>
/// Add / edit a profile (plan 09 step 11): the first name, a pictogram chosen by its word (searched in the ARASAAC pack,
/// <see cref="PictureStore.Has"/> / <see cref="PictureStore.GetNow"/>) and one of the 8 named colours — no personal
/// pictures (D29 Q9). A live preview of the tile; the name is checked as the store checks it (1–30 characters, unique).
/// </summary>
internal sealed class ProfileEditDialog : Window
{
    private readonly PictureStore? _pictures;
    private readonly Func<string, NameProblem?> _check;
    private readonly TextBox _name, _picto;
    private readonly TextBlock _pictoNote, _error;
    private readonly ContentControl _preview = new();
    private ProfileColor _color;

    public string ResultName { get; private set; } = "";
    public Avatar ResultAvatar { get; private set; } = Avatar.Default;

    private ProfileEditDialog(string name, Avatar avatar, PictureStore? pictures, Func<string, NameProblem?> check, bool nameEditable)
    {
        _pictures = pictures;
        _check = check;
        _color = avatar.Color;
        Ui.Setup(this, Strings.ProfileEditTitle, "ProfileEditDialog");
        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18), MinWidth = 420 };

        body.Children.Add(new Label { Content = Strings.ProfileNameLabel, Padding = new Thickness(0, 0, 0, 4) });
        _name = new TextBox { Text = name, MaxLength = 60, FontSize = 16, IsEnabled = nameEditable };
        AutomationProperties.SetAutomationId(_name, "ProfileName");
        AutomationProperties.SetName(_name, Strings.ProfileNameLabel);
        body.Children.Add(_name);

        var pl = new Label { Content = Strings.ProfilePictoLabel, Padding = new Thickness(0, 12, 0, 4) };
        body.Children.Add(pl);
        _picto = new TextBox { Text = avatar.Picto ?? "", MaxLength = Avatar.MaxPicto, FontSize = 16 };
        AutomationProperties.SetAutomationId(_picto, "ProfilePicto");
        AutomationProperties.SetName(_picto, Strings.ProfilePictoLabel);
        body.Children.Add(_picto);
        _pictoNote = Ui.Text("", "ProfilePictoNote", 13);
        _pictoNote.Opacity = 0.8;
        _pictoNote.Margin = new Thickness(0, 4, 0, 0);
        AutomationProperties.SetLiveSetting(_pictoNote, AutomationLiveSetting.Polite);
        body.Children.Add(_pictoNote);

        body.Children.Add(new Label { Content = Strings.ProfileColorLabel, Padding = new Thickness(0, 12, 0, 4) });
        var colors = new WrapPanel { MaxWidth = 440 };
        foreach (ProfileColor c in Enum.GetValues(typeof(ProfileColor)))
        {
            var swatch = new StackPanel { Orientation = Orientation.Horizontal };
            swatch.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = AvatarView.Brush(c), Margin = new Thickness(0, 0, 6, 0) });
            swatch.Children.Add(new TextBlock { Text = Strings.ColorName(c), VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton { Content = swatch, GroupName = "color", IsChecked = c == _color, Margin = new Thickness(0, 4, 14, 4) };
            AutomationProperties.SetAutomationId(rb, "Color-" + Avatar.ColorNames[c]);
            AutomationProperties.SetName(rb, Strings.ColorName(c));
            var cc = c;
            rb.Checked += (_, _) => { _color = cc; Refresh(); };
            colors.Children.Add(rb);
        }
        body.Children.Add(colors);

        var pv = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        pv.Children.Add(new TextBlock { Text = Strings.ProfilePreview, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        AutomationProperties.SetAutomationId(_preview, "ProfilePreview");
        pv.Children.Add(_preview);
        body.Children.Add(pv);

        _error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x34, 0x12)), Margin = new Thickness(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_error, "ProfileError");
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        body.Children.Add(_error);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = Ui.Button(Strings.Ok, "ProfileOk", (_, _) => OnOk(), 96);
        ok.IsDefault = true;
        var cancel = Ui.Button(Strings.Cancel, "ProfileCancel", (_, _) => Close(), 96);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0);
        row.Children.Add(ok);
        row.Children.Add(cancel);
        body.Children.Add(row);
        Content = body;

        _name.TextChanged += (_, _) => Refresh();
        _picto.TextChanged += (_, _) => Refresh();
        Loaded += (_, _) => { (nameEditable ? _name : _picto).Focus(); _name.SelectAll(); };
        Refresh();
    }

    /// <summary>Edit name + picture; null = cancelled. <paramref name="check"/> returns why a name is refused.</summary>
    public static (string Name, Avatar Avatar)? Edit(Window? owner, string name, Avatar avatar, PictureStore? pictures,
                                                    Func<string, NameProblem?> check, bool nameEditable = true)
    {
        var d = new ProfileEditDialog(name, avatar, pictures, check, nameEditable);
        if (owner is { IsVisible: true }) { d.Owner = owner; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        return d.ShowDialog() == true ? (d.ResultName, d.ResultAvatar) : null;
    }

    private string? PictoWord => _picto.Text.Trim().Length == 0 ? null : _picto.Text.Trim().ToLowerInvariant();

    private void Refresh()
    {
        var word = PictoWord;
        _pictoNote.Text = word is null ? "" : _pictures is null ? Strings.ProfilePictoMissing : _pictures.Has(word) ? "" : Strings.ProfileNoPicto;
        _pictoNote.Visibility = _pictoNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var clean = Profile.CleanName(_name.Text);
        var initial = clean.Length == 0 ? "?" : new System.Globalization.StringInfo(clean).SubstringByTextElements(0, 1).ToUpperInvariant();
        _preview.Content = AvatarView.Create(new Avatar(word, _color), initial, _pictures, 64);
    }

    private void OnOk()
    {
        var name = Profile.CleanName(_name.Text);
        if (_name.IsEnabled && _check(name) is { } why)
        {
            _error.Text = Strings.NameProblemText(why);
            _error.Visibility = Visibility.Visible;
            Ui.Announce(_error);
            _name.Focus();
            return;
        }
        var word = PictoWord;
        // a word without a picture is kept only when the pack is absent (another PC may have it): else the initial
        if (word != null && _pictures != null && !_pictures.Has(word)) word = null;
        ResultName = name;
        ResultAvatar = new Avatar(word, _color);
        DialogResult = true;
    }
}
