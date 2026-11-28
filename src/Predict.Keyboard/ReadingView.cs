using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Predict.Keyboard;

/// <summary>
/// The pictures of one reading document, requested once from the <see cref="PictureStore"/> (every distinct word, in text
/// order, on the picto-loader thread) and shared by the bar and the large window: a second request would cancel the first
/// one (the store keeps only the newest generation). UI thread.
/// </summary>
internal sealed class ReadingPictureSet
{
    private readonly Dictionary<string, ImageSource> _ready = new();
    private readonly int _gen;
    public event Action<string, ImageSource>? Arrived;

    /// <param name="names">plan 10C: the pictures of names (keys with <see cref="ReadingDocument.NameKeyPrefix"/>: a flag, the
    /// teacher's picture, or nothing — never ARASAAC) and the teacher's pictures of common words (before ARASAAC).</param>
    public ReadingPictureSet(ReadingDocument doc, PictureStore? store, Dispatcher ui, WordPictures? names = null)
    {
        _gen = SuggestionWindow.NextPictureGeneration();
        if (store is null && names is null) return;   // without ARASAAC the names' own pictures still show (review)
        var wanted = new List<string>();
        var seen = new HashSet<string>();
        foreach (var t in doc.Tiles)
        {
            if (t.PictureKey is not { } k || !seen.Add(k)) continue;
            bool name = k.StartsWith(ReadingDocument.NameKeyPrefix, StringComparison.Ordinal);
            var form = name ? k[ReadingDocument.NameKeyPrefix.Length..] : k;
            if (names?.For(form, name) is { } file)
            {
                if (names.Get(file) is ImageSource own) _ready[k] = own;
                continue;
            }
            if (name || store is null) continue;
            if (store.TryGetCached(k, out var img) && img is ImageSource src) _ready[k] = src;
            else if (store.Has(k)) wanted.Add(k);
        }
        if (wanted.Count > 0 && store != null)
            store.Request(_gen, wanted, (g, word, img) => ui.BeginInvoke(() =>
            {
                if (img is not ImageSource src) return;
                _ready[word] = src;
                Arrived?.Invoke(word, src);
            }));
    }

    public bool TryGet(string key, out ImageSource? img) => _ready.TryGetValue(key, out img);
}

/// <summary>
/// The reading view (plan 07, D21): a toolbar ▶/❚❚ · ⏮ · ⏭ · ⤢ (« grande fenêtre », bar only), the « Texte trop long »
/// line (D24), and the tiles — one per word with its picture above (empty slot when there is none, D4/D4b), punctuation
/// shown without a picture — wrapping over several lines in a vertical scroll area, in the bar's size, font and letter
/// spacing (D16). The current word only is highlighted (yellow tile, black text: high contrast in every font). Tiles are
/// built lazily: the first <see cref="Batch"/> at once, the rest in batches at Background priority (the UI never freezes on
/// 1000 words). Code-built so the bar and the large window share it. UI thread.
/// </summary>
internal sealed class ReadingView
{
    public const int Batch = 40;
    private static readonly Brush TileBg = Frozen(0x37, 0x41, 0x51), HighBg = Frozen(0xFD, 0xE0, 0x47), HighBorder = Frozen(0xEA, 0x58, 0x0C),
                                  ToolBg = Frozen(0x4B, 0x55, 0x63), Warn = Frozen(0xFD, 0xBA, 0x74), White = BarBuilder.White, Black = Frozen(0, 0, 0);
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol");

    private sealed class Tile
    {
        public Button Box = null!;
        public StackPanel Stack = null!;
        public Border? Slot;
        public Image? Img;
        public FrameworkElement Text = null!;
        public ReadingTile T;
    }

    private readonly DockPanel _root = new();
    private readonly WrapPanel _wrap = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _tooLong;
    private readonly Button _play, _prev, _next;
    private readonly Button? _large;
    private readonly List<Tile> _tiles = new();
    private readonly Action<string>? _log;
    private readonly string _name;
    private ReadingDocument? _doc;
    private ReadingPictureSet? _pics;
    private BarLook? _look;
    private bool _pictures;
    private int _highlight = -1, _loadGen;
    private long _loadStart;
    private double _maxBatchMs;

    public event Action? PlayPause, Prev, Next, Large;
    public event Action<int>? TileClicked;
    /// <summary>All tiles of the current document are built (the bar re-places itself).</summary>
    public event Action? AllBuilt;

    public FrameworkElement Root => _root;
    public bool Loaded => _doc != null;

    public ReadingView(bool largeButton, Action<string>? log, string name)
    {
        _log = log;
        _name = name;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _play = Tool("\uE768", Strings.ReadPlay, "ReadPlay", () => PlayPause?.Invoke());
        _prev = Tool("\uE892", Strings.ReadPrev, "ReadPrev", () => Prev?.Invoke());
        _next = Tool("\uE893", Strings.ReadNext, "ReadNext", () => Next?.Invoke());
        bar.Children.Add(_play); bar.Children.Add(_prev); bar.Children.Add(_next);
        if (largeButton) { _large = Tool("\uE740", Strings.ReadLarge, "ReadLarge", () => Large?.Invoke()); bar.Children.Add(_large); }
        DockPanel.SetDock(bar, Dock.Top);
        _root.Children.Add(bar);
        _tooLong = new TextBlock { Text = Strings.ReadTooLong, Foreground = Warn, FontFamily = new FontFamily("Segoe UI"), FontSize = 15,
                                   Margin = new Thickness(2, 0, 0, 4), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_tooLong, "ReadTooLong");
        DockPanel.SetDock(_tooLong, Dock.Top);
        _root.Children.Add(_tooLong);
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _wrap, Focusable = false,
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_scroll, "ReadTiles");
        _root.Children.Add(_scroll);
        System.Windows.Automation.AutomationProperties.SetName(_root, Strings.ReadingName);
    }

    private static Button Tool(string glyph, string name, string id, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 22, Foreground = White },
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 6, 0), Background = ToolBg, BorderThickness = new Thickness(0),
            Focusable = false, ToolTip = name, Cursor = Cursors.Hand,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        System.Windows.Automation.AutomationProperties.SetAutomationId(b, id);
        ToolTipService.SetPlacement(b, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        ToolTipService.SetInitialShowDelay(b, 800);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Show a document: the first batch of tiles now, the rest lazily.</summary>
    /// <param name="maxWidth">wrap width (DIP); NaN = the available width (large window).</param>
    /// <param name="maxHeight">height of the tile area before it scrolls (DIP); NaN = the available height.</param>
    public void Load(ReadingDocument doc, ReadingPictureSet pics, BarLook look, bool pictures, double maxWidth, double maxHeight)
    {
        Clear();
        int gen = ++_loadGen;
        _doc = doc;
        _pics = pics;
        _look = look;
        _pictures = pictures;
        _wrap.MaxWidth = double.IsNaN(maxWidth) ? double.PositiveInfinity : maxWidth;
        _scroll.MaxHeight = double.IsNaN(maxHeight) ? double.PositiveInfinity : maxHeight;
        _tooLong.Text = doc.Partial ? Strings.ReadPartial : Strings.ReadTooLong;   // phase 2: a partial read has its own words
        _tooLong.Visibility = doc.Truncated || doc.Partial ? Visibility.Visible : Visibility.Collapsed;
        pics.Arrived += OnPicture;
        _loadStart = Stopwatch.GetTimestamp();
        _maxBatchMs = 0;
        BuildBatch(gen);
    }

    private void BuildBatch(int gen)
    {
        if (gen != _loadGen || _doc is not { } doc) return;
        long t0 = Stopwatch.GetTimestamp();
        int from = _tiles.Count, to = Math.Min(doc.Tiles.Count, from + Batch);
        for (int i = from; i < to; i++) _wrap.Children.Add(MakeTile(i, doc.Tiles[i]));
        if (_highlight >= from && _highlight < to) Style(_highlight, true);
        _root.UpdateLayout();   // the layout of the batch is part of its cost (measured, ≤ 50 ms budget)
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        if (ms > _maxBatchMs) _maxBatchMs = ms;
        if (to < doc.Tiles.Count)
        {
            if (from == 0) _log?.Invoke(FormattableString.Invariant($"reading view {_name}: first {to} of {doc.Tiles.Count} tiles in {ms:0.0} ms"));
            _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => BuildBatch(gen)));
            return;
        }
        double all = (Stopwatch.GetTimestamp() - _loadStart) * 1000.0 / Stopwatch.Frequency;
        _log?.Invoke(FormattableString.Invariant($"reading view {_name}: {doc.Tiles.Count} tiles ({doc.WordCount} words) built in {all:0} ms, {Math.Max(1, (doc.Tiles.Count + Batch - 1) / Batch)} batch(es), max batch {_maxBatchMs:0.0} ms (build + layout)"));
        AllBuilt?.Invoke();
    }

    /// <summary>A tile is a <see cref="Button"/> drawn as a plain rounded border (no chrome): clickable, and exposed to
    /// UI Automation (Invoke, Name = the word) — a Border has no automation peer.</summary>
    private static readonly ControlTemplate TileTemplate = MakeTileTemplate();

    private static ControlTemplate MakeTileTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
        t.Seal();
        return t;
    }

    private Button MakeTile(int i, ReadingTile t)
    {
        var look = _look!;
        var m = look.Metrics;
        var tile = new Tile { T = t };
        var sp = new StackPanel();
        if (_pictures)
        {
            // every tile has the slot's height (the text lines up); punctuation and numbers: no picture, slot not drawn
            var border = new Border
            {
                Width = t.Kind == TileKind.Word ? m.PictureDip : 1, Height = m.PictureDip, CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 2, 0, 4), Padding = new Thickness(2), Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (t.Kind == TileKind.Word)
            {
                var img = new Image { Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                border.Child = img;
                tile.Img = img;
                if (t.PictureKey is { } k && _pics!.TryGet(k, out var src)) { img.Source = src; border.Background = White; }
            }
            sp.Children.Add(border);
            tile.Slot = border;
        }
        tile.Text = MakeText(t.Text, look, White);
        sp.Children.Add(tile.Text);
        tile.Stack = sp;
        var box = new Button
        {
            Template = TileTemplate, Content = sp, Background = TileBg, BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent,
            Padding = new Thickness(m.PadH / 2, m.PadTop, m.PadH / 2, m.PadBottom), Margin = new Thickness(2), Cursor = Cursors.Hand,
            ToolTip = Strings.ReadTileHint, Focusable = false,
        };
        ToolTipService.SetInitialShowDelay(box, 1200);
        ToolTipService.SetPlacement(box, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        System.Windows.Automation.AutomationProperties.SetName(box, t.Text);
        System.Windows.Automation.AutomationProperties.SetAutomationId(box, "Tile" + i);
        box.Click += (_, _) => TileClicked?.Invoke(i);
        tile.Box = box;
        _tiles.Add(tile);
        return box;
    }

    /// <summary>The word in the bar's font and letter spacing (as <see cref="BarBuilder"/>).</summary>
    private static FrameworkElement MakeText(string text, BarLook look, Brush fg)
    {
        var f = look.Font;
        double spacing = FontSpecs.SpacingEm(look.Spacing);
        if (spacing > 0 && f.Glyphs != null)
            return new SpacedText(text, f.Glyphs, f.Family, f.Weight, look.Metrics.WordPt, spacing, fg) { HorizontalAlignment = HorizontalAlignment.Center };
        return new TextBlock { Text = text, FontSize = look.Metrics.WordPt, FontFamily = f.Family, FontWeight = f.Weight, Foreground = fg,
                               HorizontalAlignment = HorizontalAlignment.Center };
    }

    private void OnPicture(string key, ImageSource img)
    {
        foreach (var t in _tiles)
            if (t.Img != null && t.T.PictureKey == key) { t.Img.Source = img; t.Slot!.Background = White; }
    }

    /// <summary>Highlight one tile (-1 = none) and scroll it into view.</summary>
    public void SetHighlight(int tile)
    {
        if (tile == _highlight) return;
        if (_highlight >= 0 && _highlight < _tiles.Count) Style(_highlight, false);
        _highlight = tile;
        if (tile >= 0 && tile < _tiles.Count)
        {
            Style(tile, true);
            _tiles[tile].Box.BringIntoView();
        }
    }

    private void Style(int i, bool on)
    {
        var t = _tiles[i];
        t.Box.Background = on ? HighBg : TileBg;
        t.Box.BorderBrush = on ? HighBorder : Brushes.Transparent;
        // SpacedText draws with a fixed brush: the text element is rebuilt in the other colour (one element, < 1 ms)
        int at = t.Stack.Children.IndexOf(t.Text);
        t.Text = MakeText(t.T.Text, _look!, on ? Black : White);
        t.Stack.Children.RemoveAt(at);
        t.Stack.Children.Insert(at, t.Text);
    }

    public void SetPlaying(bool playing)
    {
        ((TextBlock)_play.Content).Text = playing ? "\uE769" : "\uE768";
        var name = playing ? Strings.ReadPause : Strings.ReadPlay;
        _play.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(_play, name);
    }

    public void Clear()
    {
        _loadGen++;
        if (_pics != null) _pics.Arrived -= OnPicture;
        _pics = null;
        _doc = null;
        _tiles.Clear();
        _wrap.Children.Clear();
        _highlight = -1;
        _scroll.ScrollToTop();
        SetPlaying(false);
    }

    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
}
