using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.10.2 (plan glued-words.md): a typed word that sounds like an elided word glued to the next one offers the joined
/// form as one « sounds like » tile (« je tème » → « t'aime »), ranked by the language model with the context; a split needs a
/// pair the corpus wrote. <c>Splits = false</c> gives the results without it.</summary>
public class GluedWordsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "glued-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    public GluedWordsTests()
    {
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            // « t » before aime / il / a (and « je t aime »), « l » before ai; « le thème »; no fourgrams table
            cmd.CommandText =
                "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL);" +
                "INSERT INTO words VALUES ('je','Z°',1000),('te','t°',300),('t','t',50),('t''','t',400),('thème','tEm',200)," +
                "('aime','Em',300),('il','il',900),('a','a',2000),('le','l°',3000),('l','l',70),('l''','l',1500),('ai','E',800)," +
                "('lai','lE',5),('tilleul','tij9l',5),('elle','El',700),('ta','ta',300),('est','E',1500),('élan','el@',50),('école','ekOl',100),('et','e',2000),('o','o',10),('au','o',500),('tême','tEm',1);" +
                "CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO bigrams VALUES ('je','t',200),('je','te',100),('je','l',50),('t','aime',150),('t','il',100),('t','a',50)," +
                "('le','thème',30),('a','t',40),('l','ai',30),('l','elle',2),('t','ai',5),('t','elle',5),('t','élan',5),('l','école',20),('il','est',100),('il','et',1),('j','o',100),('d','au',100),('qu','au',100);" +
                "CREATE TABLE trigrams (p2 TEXT, p1 TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO trigrams VALUES ('je','t','aime',150),('je','l','ai',30);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _e = PredictionEngine.Load(_db);
        _e.Splits = true;
        _e.WSplit = 0.5;
        _e.SplitMinCount = 1;   // the fixture's pairs are small counts (the minimum is tested on its own)
    }

    public void Dispose()
    {
        _e.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private string[] Words(string typed, params string[] ctx) =>
        _e.Predict(typed, ctx.Length == 0 ? null : ctx, 8, sentenceStart: ctx.Length == 0).Select(s => s.Word).ToArray();

    /// <summary>WSplit 0 is the shipped value (evaluation.md § Glued words); 0.5 was the plan's start value.</summary>
    [Theory]
    [InlineData(0.0)] [InlineData(0.5)]
    public void After_Je_Taime_Comes_Before_Theme(double wSplit)
    {
        _e.WSplit = wSplit;
        var w = Words("tème", "je");
        Assert.Contains("t'aime", w);
        Assert.Contains("thème", w);
        Assert.True(Array.IndexOf(w, "t'aime") < Array.IndexOf(w, "thème"), string.Join(", ", w));
        var s = _e.Predict("tème", new[] { "je" }, 8).Single(x => x.Word == "t'aime");
        Assert.Equal(MatchKind.Phonetic, s.Kind);   // the green « même son » tile (owner Q4)
    }

    [Theory]
    [InlineData(0.0)] [InlineData(0.5)]
    public void Alone_Theme_Comes_First_And_Taime_Is_In_The_Bar(double wSplit)
    {
        _e.WSplit = wSplit;
        var w = Words("tème");
        Assert.Equal("thème", w[0]);
        Assert.Contains("t'aime", w);
    }

    [Fact]
    public void After_A_Hyphen_There_Is_No_Til()
    {
        var s = _e.Predict("til", new[] { "a" }, 8);
        Assert.Contains(s, x => x.Word == "t'il");
        Assert.DoesNotContain(Elision.Apply(s, afterHyphen: true), x => x.Word == "t'il");
    }

    [Fact]
    public void Splits_Off_Is_The_Result_Without_Them()
    {
        var queries = new[] { ("tème", new[] { "je" }), ("tème", Array.Empty<string>()), ("til", new[] { "a" }), ("lai", new[] { "je" }) };
        _e.Splits = false;
        var before = queries.Select(q => _e.Predict(q.Item1, q.Item2, 8).ToArray()).ToList();
        foreach (var r in before) Assert.DoesNotContain(r, x => Elision.IsJoined(x.Word));
        _e.Splits = true;
        foreach (var q in queries) _e.Predict(q.Item1, q.Item2, 8);
        _e.Splits = false;
        for (int i = 0; i < queries.Length; i++) Assert.Equal(before[i], _e.Predict(queries[i].Item1, queries[i].Item2, 8).ToArray());
    }

    [Fact]
    public void A_Known_Typed_Word_Splits_Too_Unless_SplitKnown_Is_Off()
    {
        Assert.Contains("l'ai", Words("lai", "je"));
        _e.SplitKnown = false;
        try { Assert.DoesNotContain("l'ai", Words("lai", "je")); }
        finally { _e.SplitKnown = true; }
    }

    [Fact]
    public void An_Unattested_Pair_Gives_No_Split()
    {
        // « thème » would fit « t » + « em » by its sound, but « t thème » is no pair of the corpus (nor does it take an elision)
        Assert.DoesNotContain("t'thème", Words("tème", "je"));
        Assert.DoesNotContain(Words("tème", "je"), w => Elision.IsJoined(w) && w != "t'aime");
    }

    [Fact]
    public void A_Capital_Inside_A_Sentence_Or_A_Typed_Apostrophe_Gives_No_Split()
    {
        Assert.DoesNotContain(_e.Predict("Tème", new[] { "je" }, 8), x => Elision.IsJoined(x.Word));
        Assert.Contains(_e.Predict("Tème", null, 8, sentenceStart: true), x => x.Word == "t'aime");
        Assert.DoesNotContain(_e.Predict("t'ème", new[] { "je" }, 8), x => Elision.IsJoined(x.Word));
        Assert.DoesNotContain(_e.Predict("t", new[] { "je" }, 8), x => Elision.IsJoined(x.Word));   // under SplitFrom
    }

    [Fact]
    public void At_Most_SplitMax_Joined_Tiles()
    {
        _e.SplitMax = 1;
        try { Assert.Single(_e.Predict("tél", new[] { "je" }, 8), x => Elision.IsJoined(x.Word)); }
        finally { _e.SplitMax = 2; }
        var two = _e.Predict("tél", new[] { "je" }, 8);
        Assert.True(two.Count(x => Elision.IsJoined(x.Word)) == 2, string.Join(", ", two.Select(x => x.Word)));
    }

    /// <summary>The exact typed word rule (« First »): a joined form that sounds like the typed word counts as its homophone
    /// and keeps its place ahead of it.</summary>
    [Fact]
    public void First_Treats_A_Joined_Form_As_A_Homophone()
    {
        var mode = _e.ExactTyped;
        _e.ExactTyped = ExactTypedMode.First;
        try
        {
            var w = Words("lai", "je");
            Assert.True(Array.IndexOf(w, "l'ai") >= 0 && Array.IndexOf(w, "l'ai") < Array.IndexOf(w, "lai"), string.Join(", ", w));
        }
        finally { _e.ExactTyped = mode; }
    }

    // ---- step 4: the spelling check (context-free, unknown words only)
    [Fact]
    public void Check_Offers_The_Joined_Form_Of_An_Unknown_Word()
    {
        var c = _e.Check("lécole");
        Assert.Equal(SpellingStatus.SoundsLike, c.Status);   // only a split found: still ≈ (red before)
        Assert.Equal(new[] { "l'école" }, c.Candidates.ToArray());
        _e.Splits = false;
        try { Assert.Equal(SpellingStatus.Unknown, _e.Check("lécole").Status); }
        finally { _e.Splits = true; }
    }

    [Fact]
    public void Check_Merges_By_Estimated_Frequency()
    {
        // the list is not sorted: « tême » (same letters, log10(2) = 0.30) comes first, then the sound group, thème log10(201)
        // = 2.30; t'aime: f(t') 400 per million × 150 / 315 = 190 → 2.28 — after thème, not before the rare tême (review 2)
        Assert.Equal(new[] { "tême", "thème", "t'aime" }, _e.Check("tème").Candidates.ToArray());
        _e.SplitMinCount = 1;
        // a joined form more frequent than every remaining entry goes before them: « l école » estimated 1500 × 20 / 52
        Assert.Equal("l'école", _e.Check("lécole").Candidates[0]);
    }

    [Fact]
    public void Check_Leaves_Known_Words_And_Unattested_Pairs()
    {
        Assert.Equal(SpellingStatus.Known, _e.Check("lai").Status);
        Assert.Equal(SpellingStatus.Unknown, _e.Check("dacor").Status);   // no « d » successors: no split
        _e.CheckSplitMax = 0;
        try { Assert.Equal(SpellingStatus.Unknown, _e.Check("lécole").Status); }
        finally { _e.CheckSplitMax = 3; }
    }

    [Fact]
    public void Check_Keeps_The_Pupils_Own_Spelling_First()
    {
        _e.User = UserLexicon.Build(Array.Empty<UserWord>(), new[] { new SpellingFix("lécole", "lycée") });
        try { Assert.Equal(new[] { "lycée", "l'école" }, _e.Check("lécole").Candidates.ToArray()); }
        finally { _e.User = UserLexicon.Empty; }
    }

    // ---- step 5: the ≈ list ordered by the words before it (owner Q2)
    [Fact]
    public void RankCorrections_Follows_The_Context()
    {
        var c = new[] { "thème", "t'aime" };
        Assert.Equal(new[] { "t'aime", "thème" }, _e.RankCorrections(c, new[] { "je" }).ToArray());
        Assert.Equal(new[] { "thème", "t'aime" }, _e.RankCorrections(c, new[] { "le" }).ToArray());
        Assert.Same(c, _e.RankCorrections(c, null));                 // no context: the order by estimated frequency
        Assert.Same(c, _e.RankCorrections(c, Array.Empty<string>()));
    }

    [Fact]
    public void RankCorrections_Orders_Ordinary_Lists_Too()
    {
        Assert.Equal(new[] { "est", "et" }, _e.RankCorrections(new[] { "et", "est" }, new[] { "il" }).ToArray());
        _e.Splits = false;   // not tied to the split pass
        try { Assert.Equal(new[] { "t'aime", "thème" }, _e.RankCorrections(new[] { "thème", "t'aime" }, new[] { "je" }).ToArray()); }
        finally { _e.Splits = true; }
    }

    [Fact]
    public void RankCorrections_Keeps_Personal_Words_And_The_Pupils_Spelling_In_Place()
    {
        _e.User = UserLexicon.Build(new[] { new UserWord("Tehem") }, new[] { new SpellingFix("tème", "thème") });
        try
        {
            // « thème » is the pupil's own correction of « tème »: first whatever the context; « Tehem » keeps its slot
            Assert.Equal(new[] { "thème", "Tehem", "t'aime", "et" },
                         _e.RankCorrections(new[] { "thème", "Tehem", "et", "t'aime" }, new[] { "je" }, "tème").ToArray());
            Assert.Equal(new[] { "t'aime", "Tehem", "thème" },
                         _e.RankCorrections(new[] { "thème", "Tehem", "t'aime" }, new[] { "je" }).ToArray());
        }
        finally { _e.User = UserLexicon.Empty; }
    }

    // ---- review: corpus noise never makes a joined form
    [Fact]
    public void One_Letter_Words_And_Au_Need_Their_Bare_Form()
    {
        // « j o » and « d au » are attested (100) but no French: never j'o, d'au; « qu'au » is
        Assert.DoesNotContain(_e.Check("jo").Candidates, Elision.IsJoined);
        Assert.DoesNotContain(_e.Predict("jo", null, 8, sentenceStart: true), x => Elision.IsJoined(x.Word));
        Assert.DoesNotContain(_e.Check("dau").Candidates, Elision.IsJoined);
        Assert.DoesNotContain(_e.Predict("dau", new[] { "il" }, 8), x => Elision.IsJoined(x.Word));
        Assert.Contains("qu'au", _e.Check("quau").Candidates);
        Assert.Contains(_e.Predict("quau", new[] { "il" }, 8), x => x.Word == "qu'au");
    }

    [Fact]
    public void A_Pair_Needs_The_Minimum_Count()
    {
        Assert.Contains("l'école", _e.Check("lécole").Candidates);   // « l école » 20
        _e.SplitMinCount = 21;
        try
        {
            Assert.Equal(SpellingStatus.Unknown, _e.Check("lécole").Status);
            Assert.DoesNotContain(_e.Predict("lécole", new[] { "à" }, 8), x => Elision.IsJoined(x.Word));
        }
        finally { _e.SplitMinCount = 1; }
    }
}

/// <summary>0.10.2 review: on the real database the corpus noise (« s e » 47, « j o » 23, « d au » 510…) never gives a nonsense
/// joined form in the bar nor in the ≈ list (skipped without <c>Data.Build/out</c>).</summary>
public class GluedRealDataTests
{
    private static readonly Lazy<PredictionEngine> Data = new(() =>
        PredictionEngine.Load(Path.Combine(RealGrammarFactAttribute.Out, "prediction.sqlite")));
    private static readonly object Gate = new();

    /// <summary>A joined form no French text writes: a one-letter main word but a / à / y, or « au » / « aux » after a bare
    /// form that is not qu' jusqu' lorsqu' puisqu'.</summary>
    private static bool Nonsense(string w) =>
        Elision.IsJoined(w, out var bare, out var main) &&
        (main.Length == 1 && main.ToLowerInvariant() is not ("a" or "à" or "y") ||
         main.ToLowerInvariant() is "au" or "aux" && !bare.ToLowerInvariant().EndsWith("qu", StringComparison.Ordinal));

    [RealGrammarFact]
    public void The_Review_Words_Show_No_Nonsense_Joined_Form()
    {
        lock (Gate)
        {
            var e = Data.Value;
            Assert.True(e.Splits);
            foreach (var w in new[] { "sai", "Sai", "jo", "mo", "to", "ju", "ty", "lau", "dau" })
            {
                bool start = char.IsUpper(w[0]);
                var c = e.Check(w).Candidates;
                Assert.False(c.Any(Nonsense), w + ": " + string.Join(", ", c));
                var p = e.Predict(w, start ? null : new[] { "il" }, 11, start).Select(x => x.Word).ToArray();
                Assert.False(p.Any(Nonsense), w + ": " + string.Join(", ", p));
                var r = e.RankCorrections(c, null, w);   // « Sai␣ » + F9 at a start: no context, the Check order
                Assert.False(r.Any(Nonsense), w);
                // review 2: a joined form never goes ahead of a more frequent same-letters word (« sai » → sais first)
                int sais = c.ToList().IndexOf("sais"), joined = c.ToList().FindIndex(x => Elision.IsJoined(x));
                if (w == "sai" && sais >= 0 && joined >= 0) Assert.True(sais < joined, string.Join(", ", c));
            }
        }
    }
}
