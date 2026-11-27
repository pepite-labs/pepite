using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>Everything that decides how one suggestion looks (size, picture slot, F-label, font, spacing).</summary>
/// <param name="Click">owner 2026-11-25, « Clic sur un mot » (listen before inserting): only the tooltip and the UIA help
/// text change (the tile itself looks the same).</param>
/// <param name="KeysListenFirst">« Touches F1–F8 : 1er appui : écouter, 2e : insérer »: the UIA help text.</param>
/// <param name="ListenButton">option D: the speaker band under the word (the tile gets taller, never wider).</param>
internal sealed record BarLook(BarMetrics Metrics, bool Pictures, bool ShowKeys, ResolvedFont Font, LetterSpacing Spacing,
                               ClickMode Click = ClickMode.Insert, bool KeysListenFirst = false, bool ListenButton = false)
{
    public static BarLook Default(FontCatalog fonts) =>
        new(BarMetrics.For(BarSize.M), true, true, fonts.Resolve(BarFont.SegoeUI), LetterSpacing.Normal);
}

/// <summary>The styles of a tile (App.xaml): the plain tile, and with option D its word part, its speaker band and the ring
/// around both.</summary>
internal sealed record BarStyles(Style Tile, Style Word, Style Band, Style Ring)
{
    public static BarStyles From(FrameworkElement e) =>
        new((Style)e.FindResource("Sugg"), (Style)e.FindResource("SuggWord"), (Style)e.FindResource("SuggBand"), (Style)e.FindResource("SuggRing"));
}

/// <summary>One tile of the bar: <c>Root</c> goes into the bar (one child per word); <c>Word</c> is the button that inserts
/// (the whole tile when option D is off; a <see cref="TileButton"/>: its UI Automation Invoke is not a mouse click);
/// <c>Band</c> the speaker band (option D), a sibling of the word button, never nested in it (a routed Click would bubble
/// from the band to the word).</summary>
internal sealed record BarTile(FrameworkElement Root, TileButton Word, Button? Band);

/// <summary>
/// Builds one suggestion button of the bar — shared by the bar window, the settings preview and the render
/// sheets. Colours: blue = completion of what was typed, green = phonetic match, orange = letter inversion/typo.
/// The F-label stays Segoe UI (UI chrome); only the word uses the chosen font. Normal spacing keeps a plain
/// <see cref="TextBlock"/> (pixel-identical to iterations 1–2, kerning kept); wider spacings use <see cref="SpacedText"/>.
/// </summary>
internal static class BarBuilder
{
    /// <summary>Owner 2026-11-25: the tile whose word was heard and waits for its confirming press (« Écouter avant
    /// d'insérer »). Set on the tile's root and inherited by its parts: the template draws a white ring (a shape, not only a
    /// colour: WCAG 2.2 SC 1.4.1 / 1.4.11) and the speaker band shows a third wave.</summary>
    public static readonly DependencyProperty ListeningProperty = DependencyProperty.RegisterAttached(
        "Listening", typeof(bool), typeof(BarBuilder), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetListening(DependencyObject d) => (bool)d.GetValue(ListeningProperty);
    public static void SetListening(DependencyObject d, bool value) => d.SetValue(ListeningProperty, value);

    public static readonly Brush Blue = Frozen(0x2D, 0x7D, 0xD2), Green = Frozen(0x2E, 0x9E, 0x5B),
                                  Orange = Frozen(0xD9, 0x7B, 0x16), White = Frozen(0xFF, 0xFF, 0xFF);
    private static readonly FontFamily Chrome = new("Segoe UI");

    /// <param name="slot">the picture slot (border + image), null when the look has no pictures.</param>
    /// <param name="fallback">true when the word fell back to a TextBlock (a character missing from the font).</param>
    public static TileButton MakeButton(int i, Suggestion s, BarLook look, Style style, out (Border Slot, Image Img)? slot, out bool fallback)
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
        var b = new TileButton   // a UI Automation Invoke follows « Touches F1–F8 », not the mouse Click (owner 2026-11-25)
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
            } + Strings.TipActionsFor(look.Click, look.ListenButton),
        };
        System.Windows.Automation.AutomationProperties.SetName(b, s.Word);
        System.Windows.Automation.AutomationProperties.SetHelpText(b,
            Strings.TileHelp(look.ShowKeys ? i + 1 : null, look.KeysListenFirst, look.Click, look.ListenButton));
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

    /// <summary>
    /// A whole tile. Option D off: today's button (<see cref="MakeButton"/> with the <c>Sugg</c> style), pixel-identical.
    /// Option D on (owner answer 8): a ring border holding a vertical grid of two sibling buttons — the word (top corners
    /// rounded) and, under it, the full-width speaker band (bottom corners rounded, the tile's colour darkened by 25 %),
    /// <see cref="BarMetrics.ListenBandDip"/> high, with a vector speaker icon (no font). The band has its own UIA id
    /// (<c>Listen{n}</c>), name (« Écouter {mot} ») and tooltip; nothing in the bar is focusable.
    /// </summary>
    public static BarTile MakeTile(int i, Suggestion s, BarLook look, BarStyles styles, out (Border Slot, Image Img)? slot, out bool fallback)
    {
        if (!look.ListenButton)
        {
            var plain = MakeButton(i, s, look, styles.Tile, out slot, out fallback);
            return new BarTile(plain, plain, null);
        }
        var word = MakeButton(i, s, look, styles.Word, out slot, out fallback);
        var band = new Button
        {
            Style = styles.Band, Background = word.Background, Height = look.Metrics.ListenBandDip,
            Content = SpeakerIcon(Math.Round(look.Metrics.ListenBandDip * 0.72)), ToolTip = Strings.ListenButtonTip,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(band, "Listen" + (i + 1));
        System.Windows.Automation.AutomationProperties.SetName(band, string.Format(Strings.ListenButtonName, s.Word));
        ToolTipService.SetPlacement(band, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        ToolTipService.SetInitialShowDelay(band, 1200);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(band, 1);
        grid.Children.Add(word);
        grid.Children.Add(band);
        var ring = new Border { Style = styles.Ring, Child = grid };
        return new BarTile(ring, word, band);
    }

    /// <summary>The word button of a tile root (the root itself when option D is off).</summary>
    public static Button? WordButtonOf(UIElement root) =>
        root as Button ?? (root is Border { Child: Grid g } && g.Children.Count > 0 ? g.Children[0] as Button : null);

    /// <summary>Speaker body and two waves (white strokes in a 24-unit box), plus a third wave shown only while the tile is
    /// armed (<see cref="ListeningProperty"/>, inherited).</summary>
    private static Viewbox SpeakerIcon(double size)
    {
        var c = new Canvas { Width = 24, Height = 24 };
        c.Children.Add(new Path { Data = Geometry.Parse("M2,9 L7,9 L12,4 L12,20 L7,15 L2,15 Z"), Fill = White });
        static Path Wave(string d) => new()
        {
            Data = Geometry.Parse(d), Stroke = White, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        c.Children.Add(Wave("M15,9 A4,4 0 0 1 15,15"));
        c.Children.Add(Wave("M17.5,6.5 A7.5,7.5 0 0 1 17.5,17.5"));
        var third = Wave("M20,4 A11,11 0 0 1 20,20");
        third.Style = ThirdWave;
        c.Children.Add(third);
        return new Viewbox
        {
            Width = size, Height = size, Child = c, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static readonly Style ThirdWave = MakeThirdWave();

    private static Style MakeThirdWave()
    {
        var st = new Style(typeof(Path));
        st.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Hidden));
        var t = new Trigger { Property = ListeningProperty, Value = true };
        t.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible));
        st.Triggers.Add(t);
        st.Seal();
        return st;
    }

    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}
