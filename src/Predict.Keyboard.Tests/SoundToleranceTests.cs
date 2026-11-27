using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.11.0 (plan dys-sound-tolerance): a pupil who swaps a voiced and a voiceless consonant still gets the word they
/// meant — « le chard » → jardin in the bar (green, after the exact sound-alikes), « chardin » ≈ jardin in a second tier of
/// the ≈ list. <c>Tolerance = None</c> gives the results without it.</summary>
public class SoundToleranceTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "soundtol-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    public SoundToleranceTests()
    {
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL);" +
                "INSERT INTO words VALUES ('jardin','ZaRd5',100),('chardon','SaRd§',100),('chien','Sj5',300),('manger','m@Ze',200)," +
                "('manche','m@S',50),('bouche','buS',80),('bouge','buZ',80),('chat','Sa',300),('visage','vizaZ',100)," +
                "('voiture','vwatyR',150),('grogne','gRoN',10),('jambe','Z@b',60),('le','l°',3000),('la','la',3000),('mon','m§',900)," +
                "('il','il',900),('faut','fo',400),('je','Z°',1000),('chez','Se',300),('maison','mEz§',300),('et','e',2000)," +
                "('jeune','Z9n',200),('chêne','SEn',50),('avec','avEk',800);" +
                "CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO bigrams VALUES ('le','jardin',50),('mon','chien',50),('la','bouche',200),('il','faut',100),('le','chat',30);" +
                "CREATE TABLE trigrams (p2 TEXT, p1 TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO trigrams VALUES ('il','faut','manger',30);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _e = PredictionEngine.Load(_db);
        _e.Tolerance = SoundPairs.ChJ;
    }

    public void Dispose()
    {
        _e.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private Suggestion[] Bar(string typed, params string[] ctx) =>
        _e.Predict(typed, ctx.Length == 0 ? null : ctx, 8, sentenceStart: ctx.Length == 0).ToArray();

    // ---- PhoneticKey.Variants

    [Fact]
    public void Variants_Swap_The_Enabled_Pair()
    {
        Assert.Equal(new[] { "jardE" }, PhoneticKey.Variants("CardE", SoundPairs.ChJ));
        Assert.Equal(new[] { "CiA" }, PhoneticKey.Variants("jiA", SoundPairs.ChJ));
        Assert.Empty(PhoneticKey.Variants("CardE", SoundPairs.None));
        Assert.Empty(PhoneticKey.Variants("maizO", SoundPairs.ChJ));   // nothing swappable
        Assert.Contains("groN", PhoneticKey.Variants("kroN", SoundPairs.KG));
    }

    [Fact]
    public void Variants_Singles_By_Position_Then_Pairs()
    {
        Assert.Equal(new[] { "jaj", "CaC", "jaC" }, PhoneticKey.Variants("Caj", SoundPairs.ChJ));
    }

    [Fact]
    public void Variants_Cap_Positions_And_Never_Return_The_Key()
    {
        var v = PhoneticKey.Variants("CjCjCj", SoundPairs.ChJ);
        Assert.Equal(4 + 6, v.Count);                                  // the first 4 positions: 4 singles + 6 pairs
        Assert.All(v, x => Assert.Equal("Cj", x.Substring(4)));        // positions 5 and 6 untouched
        Assert.DoesNotContain("CjCjCj", v);
        Assert.Equal(v.Count, v.Distinct().Count());
        Assert.Equal(4, PhoneticKey.Variants("CjCjCj", SoundPairs.ChJ, maxSwaps: 1).Count);
    }

    [Fact]
    public void Variants_Leave_Disabled_Symbols_Untouched()
    {
        // f/v is off: only the ch/j symbol moves
        Assert.Equal(new[] { "vij" }, PhoneticKey.Variants("viC", SoundPairs.ChJ));
        Assert.Equal(new[] { "fiC", "vij", "fij" }, PhoneticKey.Variants("viC", SoundPairs.ChJ | SoundPairs.FV));
    }

    [Fact]
    public void SoundPairs_Parse_And_Describe()
    {
        Assert.Equal(SoundPairs.None, SoundPairsInfo.Parse("none"));
        Assert.Equal(SoundPairs.ChJ | SoundPairs.FV, SoundPairsInfo.Parse("chj,fv"));
        Assert.Equal(SoundPairsInfo.All, SoundPairsInfo.Parse("all5"));
        Assert.Equal("chj, fv", SoundPairsInfo.Describe(SoundPairs.ChJ | SoundPairs.FV));
        Assert.Throws<ArgumentException>(() => SoundPairsInfo.Parse("sz"));
    }

    // ---- Predict

    [Fact]
    public void Chardin_And_Le_Chard_Give_Jardin_Green()
    {
        var full = Bar("chardin");
        Assert.Contains(full, s => s.Word == "jardin" && s.Kind == MatchKind.Phonetic);
        var pre = Bar("chard", "le");
        Assert.Contains(pre, s => s.Word == "jardin" && s.Kind == MatchKind.Phonetic);
    }

    [Fact]
    public void Jien_Gives_Chien_And_Mancher_Gives_Manger()
    {
        Assert.Contains(Bar("jien", "mon"), s => s.Word == "chien" && s.Kind == MatchKind.Phonetic);
        Assert.Contains(Bar("gien"), s => s.Word == "chien");
        Assert.Contains(Bar("mancher", "il", "faut"), s => s.Word == "manger" && s.Kind == MatchKind.Phonetic);
    }

    [Fact]
    public void Two_Symbols_Get_No_Tolerant_Candidate()
    {
        // « ja » (key « ja ») must not bring « chat » (key « Ca »): je / chez
        Assert.DoesNotContain(Bar("ja"), s => s.Word == "chat");
        Assert.DoesNotContain(Bar("je"), s => s.Word == "chez");
    }

    [Fact]
    public void None_Gives_The_Results_Without_Tolerance()
    {
        var inputs = new (string T, string[] C)[]
        {
            ("chardin", Array.Empty<string>()), ("chard", new[] { "le" }), ("jien", new[] { "mon" }), ("gien", Array.Empty<string>()),
            ("mancher", new[] { "il", "faut" }), ("bouje", new[] { "la" }), ("bouche", Array.Empty<string>()), ("jat", new[] { "le" }),
            ("visache", Array.Empty<string>()), ("chanbe", Array.Empty<string>()), ("ja", Array.Empty<string>()), ("je", Array.Empty<string>()),
            ("m", new[] { "il", "faut" }), ("man", Array.Empty<string>()), ("maizon", Array.Empty<string>()), ("chi", new[] { "mon" }),
            ("vo", Array.Empty<string>()), ("grog", Array.Empty<string>()), ("crogne", Array.Empty<string>()), ("jardin", new[] { "le" }),
        };
        _e.Tolerance = SoundPairs.None;
        var before = inputs.Select(q => _e.Predict(q.T, q.C, 8).ToArray()).ToList();
        _e.Tolerance = SoundPairs.ChJ;
        foreach (var q in inputs) _e.Predict(q.T, q.C, 8);
        Assert.Contains(_e.Predict("chardin", null, 8), s => s.Word == "jardin");
        _e.Tolerance = SoundPairs.None;
        Assert.DoesNotContain(_e.Predict("chardin", null, 8), s => s.Word == "jardin");
        for (int i = 0; i < inputs.Length; i++) Assert.Equal(before[i], _e.Predict(inputs[i].T, inputs[i].C, 8).ToArray());
    }

    [Fact]
    public void At_Equal_Frequency_The_Exact_Sound_Alike_Comes_First()
    {
        // « bouje » sounds like bouge (exact) and, confused, like bouche: same frequency, no context
        var w = Bar("bouje").Select(s => s.Word).ToList();
        Assert.Contains("bouge", w);
        Assert.Contains("bouche", w);
        Assert.True(w.IndexOf("bouge") < w.IndexOf("bouche"), string.Join(", ", w));
    }

    [Fact]
    public void A_Literal_Prefix_Word_Keeps_Its_Lead()
    {
        // « chard »: chardon (the letters typed) and jardin (ch/j confused) have the same frequency
        var w = Bar("chard").Select(s => s.Word).ToList();
        Assert.Equal("chardon", w[0]);
        Assert.Contains("jardin", w);
    }

    [Fact]
    public void A_Capitalised_Exact_Sound_Alike_Keeps_Its_Kind_And_Score()
    {
        // review 2026-11-26: « avec Jène » — jeune is an exact sound-alike (held back by the capital inside a sentence) and an
        // edit; chêne comes in through ch/j. The edit pass must not overwrite jeune's sound score (it would turn orange).
        _e.Tolerance = SoundPairs.None;
        var off = _e.Predict("Jène", new[] { "avec" }, 8).Single(s => s.Word == "jeune");
        _e.Tolerance = SoundPairs.ChJ;
        var bar = _e.Predict("Jène", new[] { "avec" }, 8);
        Assert.Contains(bar, s => s.Word == "chêne");
        var on = bar.Single(s => s.Word == "jeune");
        Assert.Equal(MatchKind.Phonetic, off.Kind);
        Assert.Equal(off.Kind, on.Kind);
        Assert.Equal(off.Score, on.Score);
    }

    // ---- Check and the ≈ list in two tiers

    [Fact]
    public void Check_Known_Word_Stays_Known()
    {
        Assert.Equal(SpellingStatus.Known, _e.Check("bouche").Status);
        Assert.Equal(SpellingStatus.Known, _e.Check("bouge").Status);
    }

    [Fact]
    public void Check_Chardin_Sounds_Like_Jardin_In_The_Tolerant_Tier()
    {
        var c = _e.Check("chardin");
        Assert.Equal(SpellingStatus.SoundsLike, c.Status);
        Assert.Equal(new[] { "jardin" }, c.Candidates);
        Assert.Equal(1, c.Tolerant);
    }

    [Fact]
    public void Check_Bouje_Exact_First_Then_The_Confusion()
    {
        var c = _e.Check("bouje");
        Assert.Equal(SpellingStatus.SoundsLike, c.Status);
        Assert.Equal("bouge", c.Candidates[0]);
        Assert.Equal("bouche", c.Candidates[^1]);
        Assert.Equal(1, c.Tolerant);
    }

    [Fact]
    public void Check_Without_Tolerance_Chardin_Is_Unknown()
    {
        _e.Tolerance = SoundPairs.None;
        var c = _e.Check("chardin");
        Assert.Equal(SpellingStatus.Unknown, c.Status);
        Assert.Equal(0, c.Tolerant);
        Assert.Equal(0, _e.Check("bouje").Tolerant);
    }

    [Fact]
    public void RankCorrections_Keeps_A_Confusion_After_The_Exact_Ones()
    {
        // « la bouje »: the context favours bouche (la bouche 200), but it is the confusion: it stays after bouge
        var c = _e.Check("bouje");
        Assert.Equal(new[] { "bouge", "bouche" }, _e.RankCorrections(c.Candidates, new[] { "la" }, "bouje", c.Tolerant));
        // one tier (0.10.2): the context alone orders the list
        Assert.Equal(new[] { "bouche", "bouge" }, _e.RankCorrections(c.Candidates, new[] { "la" }, "bouje", 0));
        Assert.Equal(_e.RankCorrections(c.Candidates, new[] { "la" }, "bouje"), _e.RankCorrections(c.Candidates, new[] { "la" }, "bouje", 0));
    }

    // ---- the dys set (generated by src/Predict.Eval/dys-sound/gen_dys_sound.py)

    [Fact]
    public void Every_Full_Dys_Case_Is_One_Variant_Away()
    {
        var path = Path.Combine(RepoSrc(), "Predict.Eval", "dys-sound", "dys-sound-cases.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int n = 0;
        foreach (var c in doc.RootElement.EnumerateArray())
        {
            var cat = c.GetProperty("category").GetString()!;
            if (!cat.EndsWith("-full", StringComparison.Ordinal)) continue;
            var pair = SoundPairsInfo.Parse(cat[..cat.IndexOf('-')]);
            var typed = c.GetProperty("typed").GetString()!;
            var intended = c.GetProperty("intended").GetString()!;
            Assert.True(PhoneticKey.Variants(PhoneticKey.Fold(typed), pair).Contains(PhoneticKey.Fold(intended)),
                        $"{typed} → {intended}: {PhoneticKey.Fold(typed)} / {PhoneticKey.Fold(intended)}");
            n++;
        }
        Assert.True(n >= 40, "full cases: " + n);
    }

    private static string RepoSrc()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "Predict.Eval", "dys-sound"))) return d.FullName;
        throw new DirectoryNotFoundException("src\\Predict.Eval\\dys-sound not found above " + AppContext.BaseDirectory);
    }
}
