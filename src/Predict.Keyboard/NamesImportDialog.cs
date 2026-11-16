using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Predict.Keyboard;

/// <summary>
/// « Importer une base de noms… » (plan 10C, Espace enseignant → Mots): what the database holds — its title, the number of
/// names and pictures, the licence(s), the lines left out — the rule for pictures (owner Q17: pictograms or drawings only,
/// never photos; the teacher is responsible for the rights) and, for a CSV, a licence for its pictures. « Importer » builds
/// the « noms » list (<see cref="NamesImport.Build"/>, thumbnails written to the profile) and adds it. Code-built, modal.
/// </summary>
internal sealed class NamesImportDialog : Window
{
    private readonly TextBox? _licence;
    public string? CsvLicence => _licence?.Text.Trim() is { Length: > 0 } l ? l : null;

    private NamesImportDialog(NamesSource src, string target, bool csv)
    {
        Ui.Setup(this, Strings.NamesImportTitle, "NamesImportDialog");
        var p = new StackPanel { Margin = new Thickness(16), MaxWidth = 640 };
        int images = src.Doc.Noms.Count(n => n.Image != null);
        p.Children.Add(Ui.Text(Strings.NamesImportSummary(src.Doc.Titre, src.Doc.Noms.Count, images), "NamesImportSummary", 15));
        var lic = src.Doc.Noms.Select(n => n.Licence).Where(l => !string.IsNullOrWhiteSpace(l)).Cast<string>().Distinct(StringComparer.Ordinal).Take(6).ToList();
        p.Children.Add(Ui.Text(string.Format(Strings.NamesImportLicences, lic.Count > 0 ? string.Join(" ; ", lic) : Strings.NamesImportNoLicence),
                               "NamesImportLicences", 13));
        p.Children.Add(Ui.Text(string.Format(Strings.NamesImportTo, target), "NamesImportTarget", 13));
        var rule = Ui.Text(Strings.NamesImportRule, "NamesImportRule", 13);
        rule.FontWeight = FontWeights.SemiBold;
        rule.Margin = new Thickness(0, 10, 0, 6);
        p.Children.Add(rule);
        if (csv)
        {
            _licence = new TextBox { Width = 420, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetAutomationId(_licence, "NamesImportCsvLicence");
            AutomationProperties.SetName(_licence, Strings.NamesImportCsvLicence);
            p.Children.Add(new Label { Content = Strings.NamesImportCsvLicence, Padding = new Thickness(0, 6, 0, 0), Target = _licence });
            p.Children.Add(_licence);
        }
        if (src.Doc.Refused.Count > 0)
        {
            p.Children.Add(Ui.Text(Strings.NamesImportRefused, "NamesImportRefusedHeading", 13));
            var list = new TextBox
            {
                Text = string.Join(Environment.NewLine, src.Doc.Refused.Take(200)), IsReadOnly = true, Height = 110, Width = 600,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap,
            };
            AutomationProperties.SetAutomationId(list, "NamesImportRefused");
            AutomationProperties.SetName(list, Strings.NamesImportRefused);
            p.Children.Add(list);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = Ui.Button(Strings.NamesImportOk, "NamesImportOk", (_, _) => DialogResult = true, 120);
        ok.IsDefault = true;
        ok.IsEnabled = src.Doc.Noms.Count > 0;
        var cancel = Ui.Button(Strings.NamesImportCancel, "NamesImportCancel", (_, _) => DialogResult = false);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        p.Children.Add(buttons);
        Content = p;
    }

    /// <summary>Ask, then import into <paramref name="profileId"/>. Returns the status line to show (French), or null when
    /// the teacher cancelled. The pictures are decoded and re-encoded on a worker (an STA thread: WPF imaging) while
    /// <paramref name="progress"/> shows « Import en cours… »; the window stays responsive (plan 10C review).</summary>
    public static async System.Threading.Tasks.Task<string?> Run(Window? owner, WordsContext c, string profileId, string targetName,
                                                                 Action<string>? progress = null)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Strings.NamesImportFilter, CheckFileExists = true, Title = Strings.NamesImportPick };
        if (dlg.ShowDialog(owner) != true) return null;
        var path = dlg.FileName;
        bool csv = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        NamesSource src;
        try { src = NamesImport.Open(path); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            c.Log?.Invoke($"names import: not read ({ex.GetType().Name})");
            return string.Format(Strings.NamesImportFailed, ex is InvalidDataException ? ex.Message : Strings.TxtUnreadable);
        }
        using (src)
        {
            if (src.Doc.Noms.Count == 0) return Strings.NamesImportEmpty;
            var ask = new NamesImportDialog(src, targetName, csv || src.Doc.Noms.Any(n => n.Image != null && string.IsNullOrWhiteSpace(n.Licence))) { Owner = owner };
            if (ask.ShowDialog() != true) return null;
            NamesSource use = src;
            if (ask.CsvLicence is { } lic && csv)
            {
                try { use = NamesImport.Open(path, lic); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { return string.Format(Strings.NamesImportFailed, ex.Message); }
            }
            else if (ask.CsvLicence is { } lic2)
                use = new NamesSource(src.Doc with { Noms = src.Doc.Noms.Select(n => n.Image != null && string.IsNullOrWhiteSpace(n.Licence) ? n with { Licence = lic2 } : n).ToList() },
                                      src.Origin, src.ReadImage, null);
            try
            {
                var dir = c.Words.ImagesDir(profileId);
                if (dir is null) return string.Format(Strings.NamesImportFailed, Strings.TxtNotWritten);
                var referenced = c.Words.BookOf(profileId).ImageFiles.ToList();
                var now = c.Words.Now;
                var log = c.Log;
                progress?.Invoke(Strings.NamesImportRunning);
                if (owner != null) owner.IsEnabled = false;
                NamesImportResult r;
                try { r = await OnSta(() => NamesImport.Build(use, dir, Thumbnail, referenced, now, log)); }
                finally { if (owner != null) owner.IsEnabled = true; }
                if (r.List is null) { c.Words.CleanImages(profileId); return Strings.NamesImportEmpty; }
                var why = c.Words.Update(profileId, b => { var nb = b.AddNamesList(r.List, profileId == Profile.ClassId, out _, out var p); return (nb, p); },
                                         "names database imported");
                c.Log?.Invoke($"names import: {r.Names} names, {r.Images} pictures, {r.Problems.Count} left out, into {profileId}{(why is null ? "" : " refused " + why)}");
                if (why is { } w) { c.Words.CleanImages(profileId); return Strings.WordProblemText(w); }   // the thumbnails just written go
                var done = Strings.NamesImportDone(r.Names, r.Images);
                return r.Problems.Count == 0 ? done : done + " " + Strings.NamesImportRefused + " " + string.Join(" ; ", r.Problems.Take(5)) + (r.Problems.Count > 5 ? " …" : "");
            }
            finally { if (!ReferenceEquals(use, src)) use.Dispose(); }
        }
    }

    /// <summary>Run <paramref name="f"/> on a new STA background thread (WPF imaging needs one) and await it.</summary>
    private static System.Threading.Tasks.Task<T> OnSta<T>(Func<T> f)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
        var t = new System.Threading.Thread(() =>
        {
            try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "names-import" };
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    /// <summary>WPF re-encoding of an imported picture (its size already checked from the header, ≤ 4096 px): decoded once,
    /// scaled to ≤ 256 px, flattened on white, saved as PNG. Null when it cannot be decoded.</summary>
    public static byte[]? Thumbnail(byte[] bytes)
    {
        try
        {
            var dec = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (dec.Frames.Count == 0) return null;
            BitmapSource f = dec.Frames[0];
            if (f.PixelWidth > NamesImport.MaxPixels || f.PixelHeight > NamesImport.MaxPixels) return null;
            double s = Math.Min(1.0, NamesImport.Thumb / (double)Math.Max(f.PixelWidth, f.PixelHeight));
            int w = Math.Max(1, (int)Math.Round(f.PixelWidth * s)), h = Math.Max(1, (int)Math.Round(f.PixelHeight * s));
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
                dc.DrawImage(f, new Rect(0, 0, w, h));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }
}
