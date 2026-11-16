using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>Everything that decides how one suggestion looks (size, picture slot, F-label, font, spacing).</summary>
internal sealed record BarLook(BarMetrics Metrics, bool Pictures, bool ShowKeys, ResolvedFont Font, LetterSpacing Spacing)
{
    public static BarLook Default(FontCatalog fonts) =>
        new(BarMetrics.For(BarSize.M), true, true, fonts.Resolve(BarFont.SegoeUI), LetterSpacing.Normal);
}

/// <summary>
/// Builds one suggestion button of the bar — shared by the bar window, the settings preview and the render
/// sheets. Colours: blue = completion of what was typed, green = phonetic match, orange = letter inversion/typo.
/// The F-label stays Segoe UI (UI chrome); only the word uses the chosen font. Normal spacing keeps a plain
/// <see cref="TextBlock"/> (pixel-identical to iterations 1–2, kerning kept); wider spacings use <see cref="SpacedText"/>.
/// </summary>
internal static class BarBuilder
{
    public static readonly Brush Blue = Frozen(0x2D, 0x7D, 0xD2), Green = Frozen(0x2E, 0x9E, 0x5B),
                                  Orange = Frozen(0xD9, 0x7B, 0x16), White = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly FontFamily Chrome = new("Segoe UI");

    /// <param name="slot">the picture slot (border + image), null when the look has no pictures.</param>
    /// <param name="fallback">true when the word fell back to a TextBlock (a character missing from the font).</param>
    public static Button MakeButton(int i, Suggestion s, BarLook look, Style style, out (Border Slot, Image Img)? slot, out bool fallback)
    {
        var m = look.Metrics;
        var sp = new StackPanel();
        if (look.ShowKeys)
            sp.Children.Add(new TextBlock { Text = $"F{i + 1}", FontSize = m.LabelPt, FontFamily = Chrome, Opacity = 0.75,
                                            HorizontalAlignment = HorizontalAlignment.Center });
        slot = null;
        if (look.Pictures)
        {
            // same size with or without a picture: the bar height is constant and late pictures never move it
            var img = new Image { Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var border = new Border
            {
                Width = m.PictureDip, Height = m.PictureDip, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 2, 0, 4),
                Padding = new Thickness(2), Background = Brushes.Transparent, Child = img, HorizontalAlignment = HorizontalAlignment.Center,
            };
            sp.Children.Add(border);
            slot = (border, img);
        }
        fallback = false;
        var f = look.Font;
        double spacing = FontSpecs.SpacingEm(look.Spacing);
        if (spacing > 0 && f.Glyphs != null)
        {
            var st = new SpacedText(s.Word, f.Glyphs, f.Family, f.Weight, m.WordPt, spacing, White) { HorizontalAlignment = HorizontalAlignment.Center };
            fallback = st.Fallback;
            sp.Children.Add(st);
        }
        else
            sp.Children.Add(new TextBlock { Text = s.Word, FontSize = m.WordPt, FontFamily = f.Family, FontWeight = f.Weight,
                                            HorizontalAlignment = HorizontalAlignment.Center });
        var b = new Button
        {
            Style = style,
            Content = sp,
            Padding = new Thickness(m.PadH, m.PadTop, m.PadH, m.PadBottom),
            Background = s.Kind switch { MatchKind.Phonetic => Green, MatchKind.Edit => Orange, _ => Blue },
            ToolTip = s.Kind switch
            {
                MatchKind.Phonetic => Strings.TipPhonetic,
                MatchKind.Edit => Strings.TipEdit,
                _ => Strings.TipPrefix,
            } + Strings.TipActions,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, s.Word);
        ToolTipService.SetPlacement(b, System.Windows.Controls.Primitives.PlacementMode.Bottom);   // below the bar: never cover the words nor the line being typed
        ToolTipService.SetInitialShowDelay(b, 1200);
        return b;
    }

    /// <summary>Plan 10B (D12): the marker's red (« ? » unknown word); green and blue are the tiles' own.</summary>
    public static readonly Brush Red = Frozen(0xC6, 0x28, 0x28);
    private static readonly FontFamily Symbols = new("Segoe UI Symbol, Segoe UI");

    /// <summary>The marker's colour, symbol, accessible name and action hint (null: nothing to do on a click).</summary>
    public static (Brush Colour, string Symbol, string Name, string? Action) MarkerSpec(SpellingMark mark) => mark switch
    {
        SpellingMark.Known => (Green, "✓", Strings.SpellKnownName, null),
        SpellingMark.SoundsLike => (Blue, "≈", Strings.SpellSoundsName, Strings.SpellSoundsAction),
        _ => (Red, "?", Strings.SpellUnknownName, Strings.SpellUnknownAction),
    };

    /// <summary>
    /// Plan 10B (Q9): the round spelling marker left of the words — D12's colour and a white symbol (✓ ≈ ?, never ✗), so the
    /// colour is never the only cue, with an accessible name (« Orthographe : mot inconnu ») and a tooltip. Diameter = the
    /// F-label line + the word line when the bar has pictures (room enough), the word line alone without them, so the
    /// marker never makes the bar taller. Above it, like the tiles, the key that acts on it (« F9 », for ≈ and ? only; an
    /// empty line of the same height for ✓). Not focusable (the bar never takes the focus); UI Automation can invoke it.
    /// </summary>
    public static Button MakeMarker(SpellingMark mark, BarLook look, Style style)
    {
        var m = look.Metrics;
        var (colour, symbol, name, action) = MarkerSpec(mark);
        double d = m.MarkerDiameter(look.Pictures);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (look.ShowKeys)
            sp.Children.Add(new TextBlock { Text = action != null ? "F9" : " ", FontSize = m.LabelPt, FontFamily = Chrome, Opacity = 0.75,
                                            HorizontalAlignment = HorizontalAlignment.Center });
        var circle = new Border
        {
            Width = d, Height = d, CornerRadius = new CornerRadius(d / 2), Background = colour, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = symbol, FontFamily = Symbols, FontSize = Math.Round(d * 0.6), FontWeight = FontWeights.Bold, Foreground = White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        sp.Children.Add(circle);
        var b = new Button
        {
            Style = style, Content = sp, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(Math.Max(2, m.PadH / 3), m.PadTop, Math.Max(2, m.PadH / 3), m.PadBottom),
            ToolTip = name + (action != null ? Strings.SpellActionSep + action + (look.ShowKeys ? Strings.SpellKeyF9 : "") : "") + Strings.SpellListen,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, "SpellingMarker");
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        System.Windows.Automation.AutomationProperties.SetHelpText(b, action is null ? "" : action + (look.ShowKeys ? Strings.SpellKeyF9 : ""));
        ToolTipService.SetPlacement(b, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        ToolTipService.SetInitialShowDelay(b, 1200);
        return b;
    }

    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}
