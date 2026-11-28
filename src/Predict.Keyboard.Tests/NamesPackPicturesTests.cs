using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Pack 2026.3 (owner 2026-11-27, D30 amended): a name's picture may stay off its variants
/// (<c>"image_variantes": false</c>) — the Saint-Nicolas drawing is shown for « Saint-Nicolas », never for « Sint-Niklaas »,
/// while a flag still covers its variants (« België »). Pictures read by <see cref="NamesPack.ReadPictures"/>, the same
/// rule as the app's start-up load.</summary>
public sealed class NamesPackPicturesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "names-pics-" + Guid.NewGuid().ToString("N"));

    public NamesPackPicturesTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "images", "pepite"));
        Directory.CreateDirectory(Path.Combine(_dir, "images", "pays"));
        foreach (var f in new[] { "pepite/saint-nicolas.png", "pays/be.png", "pays/x.png" })
            File.WriteAllBytes(Path.Combine(_dir, "images", f), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(_dir, "noms.json"), """
            { "format": "pepite-noms", "version": 1, "titre": "T", "licence": "CC BY 4.0",
              "noms": [
                { "nom": "Saint-Nicolas", "variantes": ["Sint-Niklaas"], "image": "images/pepite/saint-nicolas.png",
                  "licence": "CC0 1.0", "image_variantes": false },
                { "nom": "Belgique", "variantes": ["België"], "image": "images/pays/be.png", "licence": "MIT" },
                { "nom": "Ypres", "variantes": ["Ieper"], "image": "images/pays/x.png", "image_variantes": true },
                { "nom": "Hitler", "restreint": true, "image": "images/pays/x.png" } ] }
            """);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Image_Variantes_Is_Read_And_Defaults_To_True()
    {
        var d = NamesFile.ParseJson(File.ReadAllBytes(Path.Combine(_dir, "noms.json")));
        Assert.False(d.Noms.Single(n => n.Nom == "Saint-Nicolas").ImageVariantes);
        Assert.Equal(new[] { "Sint-Niklaas" }, d.Noms.Single(n => n.Nom == "Saint-Nicolas").Variantes);   // still recognised
        Assert.True(d.Noms.Single(n => n.Nom == "Belgique").ImageVariantes);
        Assert.True(d.Noms.Single(n => n.Nom == "Ypres").ImageVariantes);
    }

    [Fact]
    public void The_Drawing_Is_The_Names_Only_And_Flags_Still_Cover_Their_Variants()
    {
        var pics = NamesPack.ReadPictures(_dir);
        var wp = new WordPictures(_ => new object());
        wp.SetPack(pics);
        Assert.EndsWith(Path.Combine("pepite", "saint-nicolas.png"), wp.For("Saint-Nicolas", name: true));
        Assert.Null(wp.For("Sint-Niklaas", name: true));
        Assert.Equal(wp.For("Belgique", true), wp.For("België", true));
        Assert.NotNull(wp.For("Ieper", true));
        Assert.Null(wp.For("Hitler", true));                    // a restricted figure: never a picture
        Assert.Null(wp.For("Saint-Nicolas", name: false));      // a pack picture is for a name tile only
        Assert.Equal(5, pics.Count);                            // Saint-Nicolas, Belgique, België, Ypres, Ieper; no Sint-Niklaas
    }

    /// <summary>The real built pack (<c>PEPITE_NAMES_PACK</c> = its <c>noms</c> folder, else <c>Data.Build/out/noms-pack/noms</c>):
    /// the shipped drawing is the name's only.</summary>
    [RealNamesPackFact]
    public void Real_Pack_Saint_Nicolas_Drawing_Not_On_Sint_Niklaas()
    {
        var dir = RealNamesPackFactAttribute.Dir;
        var wp = new WordPictures(_ => new object());
        wp.SetPack(NamesPack.ReadPictures(dir));
        var file = wp.For("Saint-Nicolas", true);
        Assert.NotNull(file);
        Assert.Equal("images/pepite/saint-nicolas.png", Path.GetRelativePath(dir, file!).Replace('\\', '/'));
        Assert.True(File.Exists(file));
        Assert.Null(wp.For("Sint-Niklaas", true));
        Assert.Equal(wp.For("Luxembourg", true), wp.For("Luxemburg", true));   // flags unchanged: on their variants too
        Assert.NotNull(wp.For("Luxemburg", true));
    }
}

/// <summary>Skipped when the real pack is absent or older than edition 2026.3 (the drawing); with
/// <c>PEPITE_REQUIRE_DATA=1</c> (the CI test job) it runs anyway, so a missing pack fails instead of passing silently.</summary>
public sealed class RealNamesPackFactAttribute : FactAttribute
{
    internal static readonly string Dir = Environment.GetEnvironmentVariable("PEPITE_NAMES_PACK") is { Length: > 0 } d
        ? Path.GetFullPath(d) : Path.Combine(RealGrammarFactAttribute.Out, "noms-pack", "noms");

    public RealNamesPackFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PEPITE_REQUIRE_DATA") == "1") return;
        var json = Path.Combine(Dir, "noms.json");
        if (!File.Exists(json)) { Skip = $"no names pack in {Dir} (BUILD.md § 1h)"; return; }
        using var doc = JsonDocument.Parse(File.ReadAllBytes(json));
        var ed = doc.RootElement.TryGetProperty("edition", out var e) ? e.GetString() : null;
        if (ed is null || Version.TryParse(ed, out var v) && v < new Version(2026, 3))
            Skip = $"names pack {Dir} is edition {ed}: the drawing comes with 2026.3";
    }
}
