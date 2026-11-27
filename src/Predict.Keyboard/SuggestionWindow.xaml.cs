using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Predict.Core;
using WinForms = System.Windows.Forms;

namespace Predict.Keyboard;

/// <summary>
/// The suggestion bar: a topmost window that never takes focus (WS_EX_NOACTIVATE + MA_NOACTIVATE), so
/// the user keeps typing in their application and clicks/F-keys insert into that application.
/// Its look (size, pictures, F-labels, font, letter spacing) comes from the settings (<see cref="Look"/>);
/// it sits at the caret, docked at the top/bottom of the foreground window's monitor, or where the user
/// dragged it by its grip (<see cref="Position"/>, D9).
/// </summary>
public partial class SuggestionWindow : Window
{
    public event Action<int>? Picked;
    /// <summary>Owner 2026-11-25: a UI Automation Invoke on tile <c>index</c>'s word (screen reader, voice access, switch
    /// tools) — not a mouse click: it follows « Touches F1–F8 » (<see cref="ListenFirst.InvokeAction"/>), never reaches
    /// <see cref="Picked"/> nor its double-click detection. UI thread.</summary>
    public event Action<int>? Invoked;
    /// <summary>The spelling marker's word is to be spoken (its right-click). UI thread.</summary>
    public event Action<string>? SpeakRequested;
    /// <summary>Owner 2026-11-25: listen to tile <c>index</c> without inserting it — its right-click (or a touch
    /// press-and-hold) <c>"right-click"</c>, or option D's speaker band <c>"button"</c>. The controller speaks it (and arms
    /// the tile when a confirming press exists) — or, for a right-click in « Clic sur un mot : clic : écouter ; double-clic
    /// ou clic droit : insérer », inserts it. UI thread.</summary>
    public event Action<int, string>? ListenRequested;
    /// <summary>Plan 10B: the spelling marker was clicked (or invoked through UI Automation). UI thread.</summary>
    public event Action<SpellingMark>? MarkerClicked;
    /// <summary>The user dropped the bar at a new place (physical px, top-left). UI thread.</summary>
    public event Action<int, int>? FreeMoved;

    private IntPtr _hwnd;
    private Native.RECT _rect;

    public SuggestionWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _hwndShared = _hwnd;
            long ex = Native.GetWindowLongPtr(_hwnd, -20).ToInt64();
            ex |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/ | 0x00000008 /*TOPMOST*/;
            Native.SetWindowLongPtr(_hwnd, -20, new IntPtr(ex));
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        };
        Grip.MouseLeftButtonDown += OnGripDown;
        Grip.MouseMove += OnGripMove;
        Grip.MouseLeftButtonUp += OnGripUp;
        Grip.LostMouseCapture += OnGripLost;
        // the hook asks Contains() for every click: keep the rectangle right when the reading view grows (lazy tiles)
        SizeChanged += (_, _) => { if (_hwnd != IntPtr.Zero && _shown) { Native.GetWindowRect(_hwnd, out var r); lock (_rectLock) _rect = r; } };
    }

    private static int _pictureGen;
    /// <summary>Generations of picture requests, shared by the bar and the reading view (the store serves only the newest).</summary>
    internal static int NextPictureGeneration() => System.Threading.Interlocked.Increment(ref _pictureGen);

    /// <summary>The reading view of the bar (plan 07); created on first use.</summary>
    internal ReadingView? ReadingView { get; private set; }
    /// <summary>The bar shows the reading view (UI thread).</summary>
    internal bool IsReading => ReadingHost.Visibility == Visibility.Visible;

    /// <summary>Plan 07: switch the bar to the reading view of <paramref name="doc"/> (the suggestions are cleared). The
    /// wrap width is 60 % of the work area of the monitor the bar goes to, the tile area scrolls beyond 40 % of its height.</summary>
    /// <returns>ms spent building / laying out the first tiles, and in WPF's <c>Show()</c> of the hidden window (log phases).</returns>
    internal (double LoadMs, double ShowMs) ShowReading(ReadingDocument doc, ReadingPictureSet pics, bool pictures, PointI? caret)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        ReadingView ??= new ReadingView(true, Log, "bar");
        _gen = NextPictureGeneration();   // a late suggestion picture never lands now
        _last = Array.Empty<Suggestion>();
        _slots.Clear();
        Bar.Children.Clear();
        SetMarker(SpellingMark.None, null);
        var look = (Look ?? BarLook.Default(new FontCatalog(null, null))) with { Pictures = pictures && Pictures != null };
        var wa = WorkArea(caret);                                         // physical px of the bar's monitor
        double dip = 1.0 / Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);
        ReadingHost.Content = ReadingView.Root;
        ReadingHost.Visibility = Visibility.Visible;
        ReadingView.Load(doc, pics, look, look.Pictures, Math.Max(400, wa.Width * dip * 0.6), Math.Max(200, wa.Height * dip * 0.4));
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!IsVisible) Show();
        _shown = true;
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return ((t1 - t0) * f, (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * f);
    }

    /// <summary>Work area (physical px) of the monitor the bar is placed on, by the same rule as <see cref="Place"/>.</summary>
    private RectI WorkArea(PointI? caret)
    {
        var p = caret ?? default;
        return Position switch
        {
            BarPosition.Caret when caret is not null => Rect(WinForms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).WorkingArea),
            BarPosition.Free when FreePos is { } fp => Rect(WinForms.Screen.FromPoint(new System.Drawing.Point(fp.X, fp.Y)).WorkingArea),
            _ => Rect(WinForms.Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea),
        };
    }

    /// <summary>Back to the suggestion bar (hidden until the next suggestion).</summary>
    internal void HideReading()
    {
        if (!IsReading) return;
        ReadingView?.Clear();
        ReadingHost.Visibility = Visibility.Collapsed;
        ReadingHost.Content = null;
        HideBar();
    }

    private IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x0021) { handled = true; return (IntPtr)3; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE
        // WM_ACTIVATE, WA_ACTIVE / WA_CLICKACTIVE: see GiveBackForeground (BUG-1)
        if (msg == 0x0006 && (w.ToInt64() & 0xFFFF) != 0)
        {
            var from = l;
            // posted at Send priority: out of the activation sequence, yet before the UI Automation call that caused it
            // reaches the tile (WPF runs it at Send priority too, after this; the tile then posts its Invoke at Input)
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => GiveBackForeground(from)));
        }
        return IntPtr.Zero;
    }

    /// <summary>The bar's window handle (zero until shown once); read by the hook thread.</summary>
    internal IntPtr BarHwnd => _hwndShared;
    private volatile IntPtr _hwndShared;

    /// <summary>The window the controller tracks as the text window (hook thread's last foreground), the fallback when
    /// WM_ACTIVATE does not say which window lost the activation.</summary>
    internal Func<IntPtr>? TrackedForeground { get; set; }

    /// <summary>BUG-1 (VM 2026-11-26): the bar is no-activate for the mouse and the system, but a UI Automation client's
    /// Invoke or SetFocus (Narrateur, Accès vocal, a script) makes it the foreground window anyway, before the tile's
    /// Invoke runs. Then the keys would go to the bar, not to the text. So the bar gives the foreground straight back to
    /// the window it took it from (we are the foreground process, so <c>SetForegroundWindow</c> is allowed) and the
    /// controller does not count the change as a jump (<see cref="KeyboardPolicy.ClassifyForeground"/>). UI thread.</summary>
    /// <param name="from">the window that lost the activation (WM_ACTIVATE's lParam), or zero.</param>
    /// <returns>false when the bar is still the foreground window (no window left to give it to, or all refused): the
    /// caller must then not type anything (the keys would go to the bar).</returns>
    internal bool GiveBackForeground(IntPtr from = default)
    {
        if (_hwnd == IntPtr.Zero || Native.GetForegroundWindow() != _hwnd) return true;   // not ours (already given back)
        var targets = KeyboardPolicy.GiveBackTargets(from, TrackedForeground?.Invoke() ?? IntPtr.Zero, _hwnd);
        bool ok = false;
        foreach (var to in targets)   // a window closing as the activation moved: the tracked text window next
            if (Native.IsWindow(to) && Native.SetForegroundWindow(to)) { ok = true; break; }
        Log?.Invoke($"  bar activated from outside (UI Automation): foreground {(ok ? "given back" : targets.Length == 0 ? "not given back (no window to give it to)" : "NOT given back (refused)")}");
        return ok;
    }

    // thread-safe mirror of visibility/rect, read by the hook thread
    private volatile bool _shown;
    private readonly object _rectLock = new();

    /// <summary>The ARASAAC pack, or null (no slot at all: the bar of plan 01).</summary>
    internal PictureStore? Pictures { get; set; }
    /// <summary>Plan 10C: the pictures of names (pack flags) and the teacher's pictures, looked up before ARASAAC.</summary>
    internal WordPictures? WordPictures { get; set; }
    public Action<string>? Log { get; set; }

    internal BarLook? Look { get; private set; }
    internal BarPosition Position { get; private set; } = BarPosition.Caret;
    internal PointI? FreePos { get; private set; }

    private int _gen;                                                              // UI thread
    private long _shownAt;                                                         // UI thread: TickCount64 of the last show
    private readonly List<(string Key, Border Slot, Image Img)> _slots = new();   // UI thread: current bar
    private IReadOnlyList<Suggestion> _last = Array.Empty<Suggestion>();           // UI thread
    private SpellingMark _mark;                                                    // UI thread: the marker shown (plan 10B)
    private string? _markWord;                                                     // UI thread: its word (right-click: say it)
    private PointI? _lastCaret;                                                    // UI thread
    private readonly HashSet<string> _fallbackLogged = new();                      // UI thread
    private int? _listening;                                                       // UI thread: the ringed tile (listen before inserting)

    private bool SlotsOn => Pictures != null && (Look?.Pictures ?? true);

    /// <summary>Apply the settings' look and position. A visible bar is rebuilt in place.</summary>
    internal void Configure(BarLook look, BarPosition position, PointI? free)
    {
        bool lookChanged = Look != look;
        Look = look;
        Position = position;
        FreePos = free;
        Grip.Visibility = position == BarPosition.Free ? Visibility.Visible : Visibility.Collapsed;
        if (!IsVisible || _last.Count == 0 && _mark == SpellingMark.None) return;
        if (lookChanged) ShowSuggestions(_last, _mark, _markWord);
        Place(_lastCaret);
    }

    /// <param name="mark">plan 10B: the spelling marker left of the words (None = no marker); with no word the bar shows the
    /// marker alone (the caller decides when: Q10).</param>
    /// <param name="markWord">the word the marker is about (right-click says it); never logged.</param>
    public void ShowSuggestions(IReadOnlyList<Suggestion> s, SpellingMark mark = SpellingMark.None, string? markWord = null)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int gen = _gen = NextPictureGeneration();
        _last = s;
        _slots.Clear();
        Bar.Children.Clear();
        _listening = null;   // new tiles: the controller rings the pending one again if it is still there
        SetMarker(mark, markWord);
        List<string>? wanted = null;
        int cached = 0, named = 0;
        for (int i = 0; i < s.Count; i++)
        {
            Bar.Children.Add(MakeButton(i, s[i], out var slot));
            if (slot is not { } sl) continue;
            // plan 10C: the teacher's picture for this exact form, or the pack's flag of a name (same slot, Q18); 0.10.2 (glued
            // words, owner Q6): a joined tile (« l'école ») is looked up by its main word (« école »), as PictureStore.KeyFor
            var form = PictureStore.PictureForm(s[i]);   // a name keeps its full form (« N'Diaye »)
            if (WordPictures?.For(form, s[i].Name) is { } file)
            {
                if (WordPictures.Get(file) is { } own) { SetPicture(sl.Slot, sl.Img, own); named++; }
                continue;
            }
            if (PictureStore.KeyFor(s[i]) is not { } key) continue;   // a name: never the common noun's picture (plan 10A, Q13)
            _slots.Add((key, sl.Slot, sl.Img));
            if (Pictures!.TryGetCached(key, out var img)) { SetPicture(sl.Slot, sl.Img, img); cached++; }
            else if (Pictures.Has(key)) (wanted ??= new List<string>()).Add(key);
        }
        _shownAt = Environment.TickCount64;
        if (wanted != null)
            Pictures!.Request(gen, wanted, (g, word, img) => Dispatcher.BeginInvoke(() => OnPicture(g, word, img)));
        bool reshow = !IsVisible;
        if (reshow) Show();
        _shown = true;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Log?.Invoke(FormattableString.Invariant($"  bar gen={gen}: {s.Count} words{(mark != SpellingMark.None ? $" + marker {mark}" : "")}, {cached} pictures cached, {wanted?.Count ?? 0} requested{(named > 0 ? $", {named} name / teacher pictures" : "")}, build+show {ms:0.0} ms{(reshow ? " (window re-shown)" : "")}"));
    }

    /// <summary>Plan 10B: put the spelling marker left of the words (None = remove it). UI thread.</summary>
    private void SetMarker(SpellingMark mark, string? word)
    {
        _mark = mark; _markWord = word;
        if (mark == SpellingMark.None) { MarkerHost.Content = null; MarkerHost.Visibility = Visibility.Collapsed; return; }
        var look = (Look ?? BarLook.Default(new FontCatalog(null, null))) with { Pictures = SlotsOn };
        var b = BarBuilder.MakeMarker(mark, look, (Style)FindResource("Sugg"));
        b.Click += (_, _) => MarkerClicked?.Invoke(mark);
        b.MouseRightButtonUp += (_, e) => { if (_markWord is { Length: > 0 } w) SpeakRequested?.Invoke(w); e.Handled = true; };
        MarkerHost.Content = b;
        MarkerHost.Visibility = Visibility.Visible;
    }

    /// <summary>The marker shown with the bar (plan 10B; None when there is none). UI thread.</summary>
    internal SpellingMark Mark => _mark;

    /// <summary>A picture decoded on the loader thread arrived (UI thread). Ignored when the bar has moved
    /// on; the slot already has its final size, so the bar never moves.</summary>
    private void OnPicture(int gen, string word, object img)
    {
        if (gen != _gen) return;
        bool set = false;
        foreach (var (key, slot, image) in _slots)
            if (key == word) { SetPicture(slot, image, img); set = true; }
        if (set) Log?.Invoke($"  picto ready gen={gen} word={word} {Environment.TickCount64 - _shownAt} ms");
    }

    private static readonly Brush SlotBackground = BarBuilder.White;

    private static void SetPicture(Border slot, Image image, object? img)
    {
        if (img is not ImageSource src) return;
        image.Source = src;
        slot.Background = SlotBackground;   // ARASAAC's black outlines are unreadable on blue/green/orange
    }

    /// <summary>WPF decoder for <see cref="PictureStore"/> (runs on the picto-loader thread). Frozen, so the
    /// UI thread can use it.</summary>
    public static object DecodePng(byte[] png) => DecodePng(png, 0);

    /// <summary>Plan 10C (review): a picture wider than <paramref name="maxWidth"/> (a teacher's file, a v3 archive's) is
    /// decoded straight to that width (WPF <c>DecodePixelWidth</c>), so a large file never sits decoded in memory.</summary>
    public static object DecodePng(byte[] png, int maxWidth)
    {
        var b = new BitmapImage();
        b.BeginInit();
        if (maxWidth > 0 && NamesImport.TryImageSize(png, out var w, out _) && w > maxWidth) b.DecodePixelWidth = maxWidth;
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.StreamSource = new MemoryStream(png);
        b.EndInit();
        b.Freeze();
        return b;
    }

    private static readonly Suggestion[] WarmWords =
        Strings.PreviewWords.Select((w, i) => new Suggestion(w, 0, (MatchKind)(i % 3))).ToArray();

    /// <summary>Show the bar once off-screen at startup, with the current look. The first WPF window show is
    /// expensive (≈9 s with software rendering in Windows Sandbox, typically 0.1–0.5 s on real hardware), and so is
    /// the first use of a font (file mapping, glyph cache); paying it here keeps the first real prediction instant.
    /// With a pack, one real picture is decoded and drawn too (first use of the imaging pipeline).</summary>
    public long WarmUp()
    {
        var t = Environment.TickCount64;
        Left = -10000; Top = -10000;
        Pictures?.GetNow("maison");    // decoded into the cache → set synchronously by ShowSuggestions
        // plan 10B: the marker too (its symbol font's first use is paid here, not at the first finished word)
        ShowSuggestions(WarmWords, SpellingMark.Unknown);
        UpdateLayout();
        HideBar();
        _last = Array.Empty<Suggestion>();
        SetMarker(SpellingMark.None, null);
        // plan 07: the reading view's first use (toolbar buttons, tiles, highlight, and the scroll bar of a tile area that
        // overflows: the first real show took 470-560 ms in the sandbox while the small warm-up never scrolled) is paid here
        Pictures?.GetNow("chat");
        var wd = ReadingDocument.Build(string.Concat(System.Linq.Enumerable.Repeat("Le chat dort sur le lit. L'arbre est grand ! ", 12)));
        ShowReading(wd, new ReadingPictureSet(wd, Pictures, Dispatcher), Pictures != null, null);
        ReadingView!.SetHighlight(1);
        ReadingView.SetPlaying(true);
        UpdateLayout();
        HideReading();
        return Environment.TickCount64 - t;
    }

    /// <summary>After a font / spacing change: lay out and render the warm-up words with the new look off-screen
    /// (Background priority), so the first real bar does not pay the font load. Logs <c>font warm-up X ms</c>.</summary>
    internal void ReWarm()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (Look is not { } look) return;
            var t = System.Diagnostics.Stopwatch.StartNew();
            var root = new StackPanel { Orientation = Orientation.Horizontal };
            var style = (Style)FindResource("Sugg");
            root.Children.Add(BarBuilder.MakeMarker(SpellingMark.SoundsLike, look with { Pictures = SlotsOn }, style));   // plan 10B
            var styles = BarStyles.From(this);
            for (int i = 0; i < WarmWords.Length; i++) root.Children.Add(BarBuilder.MakeTile(i, WarmWords[i], look, styles, out _, out _).Root);
            var host = new Border { Background = Background, Child = root };
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            var rtb = new RenderTargetBitmap(Math.Max(1, (int)host.ActualWidth), Math.Max(1, (int)host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(host);
            // plan 07: the reading tiles use the same font at the same size; lay a few out too
            var rd = ReadingDocument.Build("Le chat dort. L'arbre est grand !");
            var rv = new ReadingView(false, null, "re-warm");
            rv.Load(rd, new ReadingPictureSet(rd, null, Dispatcher), look with { Pictures = SlotsOn }, SlotsOn, 600, 400);
            rv.Root.Measure(new Size(600, 400));
            rv.Root.Arrange(new Rect(rv.Root.DesiredSize));
            var rrtb = new RenderTargetBitmap(Math.Max(1, (int)rv.Root.ActualWidth), Math.Max(1, (int)rv.Root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            rrtb.Render(rv.Root);
            rv.Clear();
            Log?.Invoke(FormattableString.Invariant($"font warm-up {t.Elapsed.TotalMilliseconds:0} ms ({AppSettings.FontNames[look.Font.Font]}, {AppSettings.SpacingNames[look.Spacing]}, {look.Metrics.WordPt} pt)"));
        }));
    }

    /// <summary>Test render mode (no hooks, nothing injected): lay the bar out for <paramref name="words"/>
    /// with their pictures loaded synchronously and save it as a PNG at <paramref name="dpi"/>.</summary>
    /// <returns>pixel size, DIP size of the bar, and the words that fell back to a TextBlock.</returns>
    /// <param name="mark">plan 10B: the spelling marker drawn left of the words (render sheets of the three states).</param>
    /// <param name="listening">2026-11-25: the tile drawn with the « heard, press again » ring (0-based), or null.</param>
    internal static (int W, int H, Size Dip, List<string> Fallbacks) RenderBar(IReadOnlyList<string> words, PictureStore? store,
                                                                              BarLook look, double dpi, string outPng,
                                                                              SpellingMark mark = SpellingMark.None, int? listening = null)
    {
        var w = new SuggestionWindow { Pictures = store };
        w.Look = look;
        var kinds = new[] { MatchKind.Prefix, MatchKind.Phonetic, MatchKind.Edit };
        if (store != null) foreach (var word in words) store.GetNow(word);
        w.SetMarker(mark, null);
        var fallbacks = w.FillBar(words.Select((x, i) => new Suggestion(x, 0, kinds[i % 3])).ToList());
        w.SetListening(listening);
        var root = (FrameworkElement)w.Content;
        w.Content = null;
        var host = new Border { Background = w.Background, Child = root, UseLayoutRounding = true };
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        int pw = (int)Math.Ceiling(host.ActualWidth * dpi / 96), ph = (int)Math.Ceiling(host.ActualHeight * dpi / 96);
        var rtb = new RenderTargetBitmap(pw, ph, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create(outPng)) enc.Save(f);
        w.Close();
        return (pw, ph, new Size(host.ActualWidth, host.ActualHeight), fallbacks);
    }

    /// <summary>Fill the bar without showing the window (render mode); pictures must already be cached.</summary>
    private List<string> FillBar(IReadOnlyList<Suggestion> s)
    {
        var fb = new List<string>();
        Bar.Children.Clear();
        for (int i = 0; i < s.Count; i++)
        {
            Bar.Children.Add(MakeButton(i, s[i], out var slot, fb));
            if (slot is { } sl && Pictures!.TryGetCached(s[i].Word, out var img)) SetPicture(sl.Slot, sl.Img, img);
        }
        return fb;
    }

    public void HideBar() { _shown = false; if (IsVisible) Hide(); }

    /// <summary>Owner 2026-11-25: ring tile <paramref name="index"/> (its word was heard, a second press inserts it), or
    /// none. The ring changes no geometry (the template's border is always there, only its brush changes); the word button
    /// gets the UI Automation item status « Écouté — encore une fois pour insérer ». UI thread.</summary>
    internal void SetListening(int? index)
    {
        if (index is int n && (n < 0 || n >= Bar.Children.Count)) index = null;
        if (_listening == index) return;
        if (_listening is int old && old < Bar.Children.Count) Ring(Bar.Children[old], false);
        _listening = index;
        if (index is int i) Ring(Bar.Children[i], true);
    }

    private static void Ring(UIElement tile, bool on)
    {
        BarBuilder.SetListening(tile, on);
        if (BarBuilder.WordButtonOf(tile) is { } word)
            System.Windows.Automation.AutomationProperties.SetItemStatus(word, on ? Strings.ListenArmedStatus : "");
    }

    /// <summary>The bar (suggestions or reading view) is shown; any thread.</summary>
    internal bool Shown => _shown;

    /// <summary>Words actually on the bar after <see cref="Place"/> (fewer than suggested when trimmed to fit). UI thread.</summary>
    internal int VisibleCount => Bar.Children.Count;

    /// <summary>Place the bar for the current <see cref="Position"/>: under (or above, near the screen bottom) the
    /// caret point in physical pixels, docked on the foreground window's monitor, or at the dragged place.</summary>
    internal void Place(PointI? caret)
    {
        if (_hwnd == IntPtr.Zero) return;
        _lastCaret = caret;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        UpdateLayout();
        long tLayout = sw.ElapsedMilliseconds;
        Native.GetWindowRect(_hwnd, out var r);
        var size = new SizeI(r.Right - r.Left, r.Bottom - r.Top);
        var screens = WinForms.Screen.AllScreens.OrderByDescending(x => x.Primary).Select(x => Rect(x.WorkingArea)).ToList();
        var p = caret ?? default;
        var wa = Position switch
        {
            BarPosition.Caret => Rect(WinForms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).WorkingArea),
            BarPosition.Free => FreePos is { } fp ? BarPlacement.FreeArea(fp, size, screens) ?? screens[0] : screens[0],
            _ => Rect(WinForms.Screen.FromHandle(Native.GetForegroundWindow()).WorkingArea),
        };
        long tScreens = sw.ElapsedMilliseconds;
        // XL + wide spacing + 8 long words can be wider than the monitor: drop words from the end until the bar
        // fits (the bar always stays fully visible; F-keys are limited to the words shown, see VisibleCount)
        int before = Bar.Children.Count;
        if (size.W > wa.Width && before > 1 && ActualWidth > 0)
        {
            // first cut from the measured widths (the fixed part counts the grip and the spelling marker), then the loop
            // below settles the rounding
            double scale = size.W / ActualWidth;   // physical px per DIP
            var widths = Bar.Children.OfType<FrameworkElement>()
                            .Select(c => (int)Math.Ceiling((c.ActualWidth + c.Margin.Left + c.Margin.Right) * scale)).ToList();
            int keep = Math.Max(1, BarPlacement.WordsThatFit(size.W - widths.Sum(), widths, wa.Width));
            if (keep < Bar.Children.Count)
            {
                Bar.Children.RemoveRange(keep, Bar.Children.Count - keep);
                UpdateLayout();
                Native.GetWindowRect(_hwnd, out r);
                size = new SizeI(r.Right - r.Left, r.Bottom - r.Top);
            }
        }
        while (size.W > wa.Width && Bar.Children.Count > 1)
        {
            Bar.Children.RemoveAt(Bar.Children.Count - 1);
            UpdateLayout();
            Native.GetWindowRect(_hwnd, out r);
            size = new SizeI(r.Right - r.Left, r.Bottom - r.Top);
        }
        if (Bar.Children.Count < before) Log?.Invoke($"  bar trimmed to {Bar.Children.Count} of {before} words to fit the screen ({size.W} px <= {wa.Width})");
        var at = BarPlacement.Compute(Position, p, size, wa, FreePos, screens);
        Native.SetWindowPos(_hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, at.X, at.Y, 0, 0, 0x0001 /*NOSIZE*/ | 0x0010 /*NOACTIVATE*/);
        Native.GetWindowRect(_hwnd, out var nr);
        lock (_rectLock) _rect = nr;
        if (sw.ElapsedMilliseconds > 50)
            Log?.Invoke($"  slow place: layout {tLayout} ms, screens {tScreens - tLayout} ms, trim+move {sw.ElapsedMilliseconds - tScreens} ms");
    }

    private static RectI Rect(System.Drawing.Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>Is a physical screen point inside the visible bar? Safe to call from any thread.</summary>
    public bool Contains(int x, int y)
    {
        if (!_shown) return false;
        lock (_rectLock) return x >= _rect.Left && x < _rect.Right && y >= _rect.Top && y < _rect.Bottom;
    }

    // manual drag (UI thread): cursor and window top-left at the button-down, physical px
    private Native.POINT _dragFrom;
    private Native.RECT _dragRect;
    private IntPtr _dragFg;
    private bool _dragging;

    /// <summary>Free position: drag by the grip. A manual drag (mouse capture + <c>SetWindowPos(SWP_NOACTIVATE)</c>),
    /// not <c>Window.DragMove</c>: in the sandbox the system move loop of <c>DragMove</c> activated the bar (the foreground
    /// WinEvent saw our window, which hid the bar and reset the tracker). The hook ignores clicks inside the bar, so a
    /// drag is never a caret jump; the user's application keeps the focus and the typing continues.</summary>
    private void OnGripDown(object sender, MouseButtonEventArgs e)
    {
        if (Position != BarPosition.Free || _hwnd == IntPtr.Zero) return;
        e.Handled = true;
        Native.GetCursorPos(out _dragFrom);
        Native.GetWindowRect(_hwnd, out _dragRect);
        _dragFg = Native.GetForegroundWindow();
        _dragging = Grip.CaptureMouse();
    }

    private void OnGripMove(object sender, MouseEventArgs e)
    {
        if (!Grip.IsMouseCaptured) return;
        Native.GetCursorPos(out var p);
        Native.SetWindowPos(_hwnd, new IntPtr(-1), _dragRect.Left + p.X - _dragFrom.X, _dragRect.Top + p.Y - _dragFrom.Y, 0, 0,
                            0x0001 /*NOSIZE*/ | 0x0010 /*NOACTIVATE*/);
    }

    private void OnGripUp(object sender, MouseButtonEventArgs e)
    {
        if (!Grip.IsMouseCaptured) return;
        OnGripMove(sender, e);
        Grip.ReleaseMouseCapture();   // → OnGripLost saves the place
    }

    /// <summary>End of a drag: button released, or the capture taken away (Alt+Tab, a system dialog): the bar is
    /// where it was moved to, so that place is kept either way.</summary>
    private void OnGripLost(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Native.GetWindowRect(_hwnd, out var r);
        lock (_rectLock) _rect = r;
        FreePos = new PointI(r.Left, r.Top);
        Log?.Invoke($"bar dragged to ({r.Left},{r.Top}); foreground {(Native.GetForegroundWindow() == _dragFg ? "kept" : "CHANGED")}");
        FreeMoved?.Invoke(r.Left, r.Top);
    }

    private BarStyles? _styles;

    /// <summary>One tile (<see cref="BarBuilder.MakeTile"/>): a click on the word picks it; a right-click (a touch
    /// press-and-hold) on any part, or a click on option D's speaker band, asks to listen (<see cref="ListenRequested"/>).
    /// The band and the word are siblings, so a band click never reaches the word's Click.</summary>
    private FrameworkElement MakeButton(int i, Suggestion s, out (Border Slot, Image Img)? slot, List<string>? fallbacks = null)
    {
        var look = (Look ?? BarLook.Default(new FontCatalog(null, null))) with { Pictures = SlotsOn };
        var t = BarBuilder.MakeTile(i, s, look, _styles ??= BarStyles.From(this), out slot, out bool fb);
        if (fb)
        {
            fallbacks?.Add(s.Word);
            if (_fallbackLogged.Add(s.Word)) Log?.Invoke($"  font '{AppSettings.FontNames[look.Font.Font]}' lacks a character of '{s.Word}': plain text");
        }
        t.Word.Click += (_, _) => Picked?.Invoke(i);
        t.Word.AutomationInvoked += (_, _) => Invoked?.Invoke(i);   // UIA Invoke: not a click (the band's Invoke stays its Click)
        t.Root.MouseRightButtonUp += (_, e) => { ListenRequested?.Invoke(i, "right-click"); e.Handled = true; };
        if (t.Band is { } band) band.Click += (_, e) => { ListenRequested?.Invoke(i, "button"); e.Handled = true; };
        return t.Root;
    }
}
