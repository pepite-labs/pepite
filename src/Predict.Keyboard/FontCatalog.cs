using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Predict.Keyboard;

/// <summary>A bar font ready for WPF: family, the word weight, the glyph typeface (for <see cref="SpacedText"/>).</summary>
internal sealed record ResolvedFont(BarFont Font, FontFamily Family, FontWeight Weight, GlyphTypeface? Glyphs, bool Available, string? FontFile);

/// <summary>
/// The bundled OFL fonts (D16), loaded <b>privately</b> from <c>fonts\&lt;Family&gt;\</c> next to the exe
/// (<c>new FontFamily(baseUri, "./#Family")</c>): nothing is installed into Windows, other apps never see them.
/// A family is available when its files exist and WPF can build a glyph typeface at the word weight; otherwise
/// Segoe UI is used (log <c>font '&lt;x&gt;' not available, using Segoe UI</c>). Results are cached. UI thread.
/// </summary>
internal sealed class FontCatalog
{
    private readonly Dictionary<BarFont, ResolvedFont> _cache = new();
    private readonly Action<string>? _log;
    private readonly HashSet<BarFont> _logged = new();

    public string? FontsDir { get; }

    public FontCatalog(string? fontsDir, Action<string>? log)
    {
        FontsDir = fontsDir;
        _log = log;
    }

    /// <summary><c>fonts\</c> next to the exe, else walk up to <c>Data.Build\out\fonts</c> (development builds only, plan 12:
    /// <see cref="DataSearch.MayWalkUp"/>).</summary>
    public static string? FindFontsDir() => DataSearch.FindDir("fonts");

    /// <summary>Can this font itself be used (not its Segoe UI fallback)?</summary>
    public bool IsAvailable(BarFont f)
    {
        if (!_cache.TryGetValue(f, out var r)) { r = Load(FontSpecs.For(f)); _cache[f] = r; }
        return r.Available;
    }

    /// <summary>The font, or Segoe UI when it is not available (logged once).</summary>
    public ResolvedFont Resolve(BarFont f)
    {
        IsAvailable(f);   // loads and caches
        var r = _cache[f];
        if (!r.Available && _logged.Add(f))
            _log?.Invoke($"font '{AppSettings.FontNames[f]}' not available, using Segoe UI");
        return r.Available ? r : Fallback(f);
    }

    private ResolvedFont Fallback(BarFont requested)
    {
        if (!_cache.TryGetValue(BarFont.SegoeUI, out var s)) { s = Load(FontSpecs.For(BarFont.SegoeUI)); _cache[BarFont.SegoeUI] = s; }
        return s with { Font = BarFont.SegoeUI, Available = requested == BarFont.SegoeUI || s.Available };
    }

    private ResolvedFont Load(FontSpec spec)
    {
        var weight = FontWeight.FromOpenTypeWeight(spec.WordWeight);
        if (spec.Folder is null)
        {
            var fam = new FontFamily(spec.FamilyName);
            new Typeface(fam, FontStyles.Normal, weight, FontStretches.Normal).TryGetGlyphTypeface(out var g);
            return new ResolvedFont(spec.Font, fam, weight, g, true, g?.FontUri?.LocalPath);
        }
        try
        {
            if (FontsDir is null) return Missing(spec, "no fonts folder");
            var dir = Path.Combine(FontsDir, spec.Folder);
            var missing = spec.FileNames.Where(n => !File.Exists(Path.Combine(dir, n))).ToList();
            if (missing.Count > 0) return Missing(spec, $"missing {string.Join(", ", missing)} in {dir}");
            var baseUri = new Uri(dir.TrimEnd('\\') + "\\");
            // WPF groups static fonts by their typographic family (name ID 16) when present; some files carry only
            // the legacy family "Andika SemiBold": try both, and keep the one whose glyph file has the wanted weight.
            foreach (var (family, w) in new[] { (spec.FamilyName, weight), (spec.FamilyName + " " + WeightName(spec.WordWeight), FontWeights.Normal) })
            {
                var fam = new FontFamily(baseUri, "./#" + family);
                var tf = new Typeface(fam, FontStyles.Normal, w, FontStretches.Normal);
                if (!tf.TryGetGlyphTypeface(out var g)) continue;
                var file = g.FontUri?.LocalPath ?? "";
                if (!file.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;   // a system font answered
                if (w == weight && g.Weight != weight) continue;   // WPF picked another face of the family
                if (g.Style != FontStyles.Normal) continue;         // never an italic face
                _log?.Invoke($"font '{AppSettings.FontNames[spec.Font]}': family '{family}' weight {g.Weight} from {Path.GetFileName(file)}");
                return new ResolvedFont(spec.Font, fam, w, g, true, file);
            }
            return Missing(spec, "no glyph typeface");
        }
        catch (Exception ex) { return Missing(spec, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    private static string WeightName(int w) => w switch { 600 => "SemiBold", 700 => "Bold", 500 => "Medium", _ => "Regular" };

    private ResolvedFont Missing(FontSpec spec, string why)
    {
        _log?.Invoke($"font '{AppSettings.FontNames[spec.Font]}' unavailable: {why}");
        return new ResolvedFont(spec.Font, new FontFamily("Segoe UI"), FontWeights.SemiBold, null, false, null);
    }

    /// <summary>Characters of <paramref name="text"/> that the font's glyph typeface does not have (render sheets, F0).</summary>
    public static string MissingGlyphs(GlyphTypeface? g, string text)
    {
        if (g is null) return text;
        var map = g.CharacterToGlyphMap;
        return new string(text.Where(c => !map.ContainsKey(c)).Distinct().ToArray());
    }

    /// <summary>Copyright line of the family's OFL.txt (À propos), or null when the folder is missing.</summary>
    public string? Copyright(BarFont f)
    {
        var spec = FontSpecs.For(f);
        if (spec.Folder is null || FontsDir is null) return null;
        var ofl = Path.Combine(FontsDir, spec.Folder, "OFL.txt");
        if (!File.Exists(ofl)) return null;
        try
        {
            var lines = File.ReadLines(ofl).Take(4).Select(l => l.Trim()).TakeWhile(l => l.Length > 0 && !l.StartsWith("This Font Software"));
            return string.Join(" ", lines);
        }
        catch (IOException) { return null; }
    }
}
