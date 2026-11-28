using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.11.0 (plan glued-words-followup.md): « est » after c' s' n' qu' (A), subject + clitic + word (C) and the fixed
/// expressions (B), each with its switch; with the three off the results are those of 0.10.2. A small fixture of our own
/// counts; the minimum counts are lowered to 1 (tested on their own).</summary>
public class GluedFollowupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glued2-" + Guid.NewGuid().ToString("N"));
    private readonly string _db;
    private readonly PredictionEngine _e;

    public GluedFollowupTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "prediction.sqlite");
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            // « aimoux » is invented (the exclusion test lists it)
            cmd.CommandText =
                "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL);" +
                "INSERT INTO words VALUES ('je','Z°',1000),('j','Zi',50),('t','te',50),('t''','t',400),('te','t°',300),('aime','Em',300)," +
                "('aimoux','Emu',100),('il','il',900),('ils','il',300),('elle','El',700),('a','a',2000),('est','e',1500),('et','e',2000)," +
                "('c','se',60),('ce','s2',3000),('ces','se',500),('ses','se',400),('se','s°',2000),('s','Es',40),('s''','s',400)," +
                "('n','En',50),('n''','n',800),('ne','n°',1500),('ai','E',800),('y','i',500),('parce','paRs',50),('que','k°',3000)," +
                "('qu','ky',20),('parsec','paRsEk',1),('contre','k§tR',200),('par','paR',1500),('m','Em',50),('m''','m',300)," +
                "('appelle','apEl',100),('cet','sEt',200),('jeté','Z°te',45),('tu','ty',800),('ça','sa',900),('va','va',800)," +
                "('cava','kava',1),('cette','sEt',1000),('cela','s°la',1500);" +
                "CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO bigrams VALUES ('c','est',200),('c','et',100),('s','est',100),('n','est',100),('n','ai',50),('il','s',80)," +
                "('il','est',300),('je','t',200),('t','aime',150),('t','aimoux',50),('m','appelle',60),('je','m',100),('il','y',400)," +
                "('y','a',300),('parce','que',100),('par','contre',80),('est','ce',100),('ce','que',300),('ça','va',100),('qu','est',50)," +
                "('est','parce',100),('il','a',500),('je','ne',300);" +
                "CREATE TABLE trigrams (p2 TEXT, p1 TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO trigrams VALUES ('je','t','aime',150),('je','t','aimoux',50),('je','m','appelle',60),('il','y','a',300)," +
                "('est','ce','que',100),('c','est','parce',100);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _e = PredictionEngine.Load(_db);
        _e.SplitMinCount = 1;
        _e.Split3MinCount = 1;
        _e.ExprMinCount = 1;
        _e.EstSplits = _e.Split3 = _e.Expressions = true;
    }

    public void Dispose()
    {
        _e.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string[] Words(string typed, params string[] ctx) =>
        _e.Predict(typed, ctx.Length == 0 ? null : ctx, 8, sentenceStart: ctx.Length == 0).Select(s => s.Word).ToArray();

    private ExcludedWords List(params string[] forms)
    {
        var p = Path.Combine(_dir, "list.txt");
        var sb = new StringBuilder(ExcludedWords.Header + "\n");
        foreach (var f in forms) sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(f))).Append('\n');
        File.WriteAllText(p, sb.ToString(), Encoding.ASCII);
        return ExcludedWords.FromFile(p, _e);
    }

    // ---- A: « est » after c' s' n' qu'
    [Fact]
    public void Ce_And_Ne_Sounds_Give_Cest_Sest_Nest()
    {
        var w = Words("cé", "il");
        Assert.Contains("c'est", w);
        Assert.Contains("s'est", w);
        Assert.Contains("n'est", Words("nè", "il"));
        var s = _e.Predict("nè", new[] { "il" }, 8).Single(x => x.Word == "n'est");
        Assert.Equal(MatchKind.Phonetic, s.Kind);   // green « même son »
        Assert.Contains("c'est", _e.Check("cé").Candidates);
        Assert.Contains("n'est", _e.Check("nè").Candidates);
    }

    [Fact]
    public void Ce_Typed_In_Full_Gives_Cest_Never_Sest()
    {
        var w = Words("ce");
        Assert.Contains("c'est", w);
        Assert.DoesNotContain("s'est", w);
        Assert.Contains("ce", w);                       // the typed word stays
        Assert.Contains("s'est", Words("se"));
        Assert.DoesNotContain("c'est", Words("se"));
        Assert.Equal(SpellingStatus.Known, _e.Check("ce").Status);   // bar only: « ce » is ✓
    }

    [Fact]
    public void Est_Never_Brings_Et()
    {
        // « c et » is in the counts (corpus noise), but only « est » joins under the sound `e`
        Assert.DoesNotContain("c'et", Words("cé", "il"));
        Assert.DoesNotContain("c'et", _e.Check("cé").Candidates);
        _e.EstSplits = false;
        var off = _e.Predict("cet", new[] { "il" }, 8).ToArray();
        _e.EstSplits = true;
        Assert.Equal(off, _e.Predict("cet", new[] { "il" }, 8).ToArray());   // « cet »: nothing new
    }

    [Fact]
    public void EstFromKnown_Off_Leaves_Known_Words_But_Ce()
    {
        _e.EstFromKnown = false;
        try
        {
            Assert.DoesNotContain(Words("ces", "il"), x => x.EndsWith("'est", StringComparison.Ordinal));
            Assert.Contains("c'est", Words("ce"));      // « ce » keeps c'est (0.10.2 owner Q3)
            Assert.Contains("c'est", Words("cé", "il"));
        }
        finally { _e.EstFromKnown = true; }
    }

    [Fact]
    public void The_Three_Switches_Off_Are_The_Results_Without_Them()
    {
        var queries = new[] { ("cé", new[] { "il" }), ("ce", Array.Empty<string>()), ("nè", new[] { "il" }), ("jtaime", Array.Empty<string>()),
                              ("ilya", Array.Empty<string>()), ("parceque", new[] { "c'est" }), ("ces", new[] { "il" }) };
        var words = new[] { "cé", "nè", "jtaime", "ilya", "parceque", "eske", "parcontre" };
        _e.EstSplits = _e.Split3 = _e.Expressions = false;
        var bars = queries.Select(q => _e.Predict(q.Item1, q.Item2, 8).ToArray()).ToList();
        var checks = words.Select(w => _e.Check(w).Candidates.ToArray()).ToList();
        foreach (var b in bars) Assert.DoesNotContain(b, x => x.Word.Contains(' ') || x.Word.EndsWith("'est", StringComparison.Ordinal));
        _e.EstSplits = _e.Split3 = _e.Expressions = true;
        foreach (var q in queries) _e.Predict(q.Item1, q.Item2, 8);
        foreach (var w in words) _e.Check(w);
        _e.EstSplits = _e.Split3 = _e.Expressions = false;
        for (int i = 0; i < queries.Length; i++) Assert.Equal(bars[i], _e.Predict(queries[i].Item1, queries[i].Item2, 8).ToArray());
        for (int i = 0; i < words.Length; i++) Assert.Equal(checks[i], _e.Check(words[i]).Candidates.ToArray());
        _e.EstSplits = _e.Split3 = _e.Expressions = true;
        _e.Splits = false;   // Splits off turns every glued kind off
        try
        {
            Assert.DoesNotContain(_e.Check("jtaime").Candidates, x => x.Contains(' '));
            Assert.DoesNotContain(Words("jtaime"), x => x.Contains(' '));
        }
        finally { _e.Splits = true; }
    }

    // ---- C: subject + clitic + word
    [Fact]
    public void Jtaime_Gives_Je_Taime_First()
    {
        Assert.Equal("je t'aime", Words("jtaime")[0]);
        Assert.Equal("je t'aime", Words("jetaime")[0]);
        Assert.Equal("je t'aime", _e.Check("jtaime").Candidates[0]);
        Assert.Contains("je m'appelle", Words("jemapelle", "je"));
        var s = _e.Predict("jtaime", null, 8, true).Single(x => x.Word == "je t'aime");
        Assert.Equal(MatchKind.Phonetic, s.Kind);
    }

    [Fact]
    public void A_Known_Word_Gets_No_Three_Word_Tile()
    {
        // owner Q1 (2026-11-27): « jeté » is a word; it never offers « je t'ai » (Split3Known = false)
        Assert.DoesNotContain(Words("jeté"), x => x.Contains(' '));
        Assert.Equal(SpellingStatus.Known, _e.Check("jeté").Status);
        Assert.DoesNotContain(Words("jta"), x => x.Contains(' '));   // under Split3From
    }

    [Fact]
    public void Three_Words_Need_The_Minimum_Count()
    {
        _e.Split3MinCount = 151;   // « je t aime » 150
        try
        {
            Assert.DoesNotContain("je t'aime", Words("jtaime"));
            Assert.DoesNotContain("je t'aime", _e.Check("jtaime").Candidates);
        }
        finally { _e.Split3MinCount = 1; }
    }

    [Fact]
    public void An_Excluded_Word_Is_Never_Inside_A_Joined_Or_Multi_Word_Tile()
    {
        Assert.Contains("je t'aimoux", Words("jtaimoux"));   // the source offers it without the list
        Assert.Contains("je t'aimoux", _e.Check("jtaimoux").Candidates);
        Assert.Contains("t'aimoux", Words("taimoux", "je"));
        Assert.Contains("par contre", Words("parcontre"));
        _e.Excluded = List("aimoux", "contre");
        long late = _e.ExcludedLate;
        try
        {
            Assert.DoesNotContain(Words("jtaimoux"), x => _e.Excluded.IsExcluded(x));
            Assert.DoesNotContain(_e.Check("jtaimoux").Candidates, x => _e.Excluded.IsExcluded(x));
            Assert.DoesNotContain(Words("taimoux", "je"), x => _e.Excluded.IsExcluded(x));
            Assert.DoesNotContain("par contre", Words("parcontre"));
            Assert.DoesNotContain("par contre", _e.Check("parcontre").Candidates);
            Assert.Equal(late, _e.ExcludedLate);         // every source filtered: the safety net dropped nothing
        }
        finally { _e.Excluded = ExcludedWords.Empty; }
    }

    [Fact]
    public void IsExcluded_Tests_Each_Space_Separated_Part()
    {
        var x = List("aimoux");
        Assert.True(x.IsExcluded("je t'aimoux"));
        Assert.True(x.IsExcluded("je t’aimoux"));
        Assert.True(x.IsExcluded("aimoux de"));
        Assert.True(x.IsExcluded("il y aimoux"));
        Assert.False(x.IsExcluded("je t'aime"));
        Assert.False(x.IsExcluded("aimouxx y"));
        Assert.False(x.IsExcluded("aimouxx"));            // a single word: unchanged
    }

    // ---- B: the fixed expressions
    [Fact]
    public void Ilya_Gives_Il_Y_A()
    {
        Assert.Contains("il y a", Words("ilya"));
        Assert.Equal("il y a", _e.Check("ilya").Candidates[0]);
        Assert.DoesNotContain(Words("il"), x => x.Contains(' '));     // « il » never shows « il y a »
        Assert.DoesNotContain(Words("ily"), x => x.Contains(' '));    // under ExprFrom
    }

    [Fact]
    public void Parceque_Lists_Parce_Que_Before_Parsec_And_Eske_Est_Ce_Que()
    {
        var c = _e.Check("parceque").Candidates.ToList();
        Assert.True(c.IndexOf("parce que") >= 0 && c.IndexOf("parce que") < c.IndexOf("parsec"), string.Join(", ", c));
        Assert.Contains("est-ce que", _e.Check("eske").Candidates);
        Assert.Equal("parce que", _e.RankCorrections(new[] { "parsec", "parce que" }, new[] { "c'est" })[0]);
        Assert.Equal(SpellingStatus.Known, _e.Check("parce").Status);   // a word: no expression (owner Q1)
    }

    [Fact]
    public void A_Row_Below_The_Minimum_Or_Glued_Into_A_Word_Is_Left_Out()
    {
        _e.ExprMinCount = 301;   // « il y a » 300
        try
        {
            Assert.DoesNotContain("il y a", Words("ilya"));
            Assert.DoesNotContain("il y a", _e.Check("ilya").Candidates);
        }
        finally { _e.ExprMinCount = 1; }
        // « ça va » glued is « cava », a word: the row is left out at load (owner Q2)
        Assert.NotNull(_e.GetExpressions().Single(r => r.Written == "ça va").Dropped);
        Assert.Null(_e.GetExpressions().Single(r => r.Written == "il y a").Dropped);
    }
}

/// <summary>0.11.0 (plan glued-words-followup.md step 5): a tile of several words (« il y a », « je t'aime ») goes through the
/// bar's display steps, the picture rule, the ≈ replacement and the tracker as one inserted string.</summary>
public class GluedFollowupTileTests
{
    [Fact]
    public void A_Multi_Word_Tile_Shows_The_Picture_Of_Its_Joined_Last_Part_Only()
    {
        Assert.Equal("aime", PictureStore.PictureForm(new Suggestion("je t'aime", 0, MatchKind.Phonetic)));
        Assert.Equal("aime", PictureStore.KeyFor(new Suggestion("Je t’aime", 0, MatchKind.Phonetic)));
        // « il y a »: its own form, which no picture of the store has (the pictures are single words)
        Assert.Equal("il y a", PictureStore.PictureForm(new Suggestion("il y a", 0, MatchKind.Phonetic)));
        Assert.Equal("Jean Moulin", PictureStore.PictureForm(new Suggestion("Jean Moulin", 0, MatchKind.Prefix, Name: true)));
    }

    [Fact]
    public void The_Bar_Writes_The_Capital_And_The_Documents_Apostrophe()
    {
        var s = new[] { new Suggestion("il y a", 0, MatchKind.Phonetic) };
        Assert.Equal("Il y a", KeyboardPolicy.Shown(s, false, '\'', "Ilya", true, 8)[0].Word);   // « Ilya » at a start
        Assert.Equal("il y a", KeyboardPolicy.Shown(s, false, '\'', "ilya", false, 8)[0].Word);
        var t = new[] { new Suggestion("je t'aime", 0, MatchKind.Phonetic), new Suggestion("qu'est-ce que", -1, MatchKind.Phonetic) };
        Assert.Equal(new[] { "je t’aime", "qu’est-ce que" }, KeyboardPolicy.Shown(t, false, '’', "jtaime", false, 8).Select(x => x.Word).ToArray());
        Assert.Equal("Qu’est-ce que", KeyboardPolicy.CasedDistinct(new[] { t[1] with { Word = Elision.Styled(t[1].Word, '’') } }, "", true, 8)[0].Word);
    }

    [Fact]
    public void A_Multi_Word_Insert_Is_Kept_As_Typed_And_Backspace_Follows_It()
    {
        var tr = new WordTracker();
        foreach (var c in "ily") tr.Type(c);
        tr.Accept("il y a", true);
        Assert.Equal("il y a", tr.Finished!.Value.Word);
        Assert.Equal("il y a", tr.Context[^1]);
        Assert.True(tr.AfterSeparator);
        tr.Backspace();                      // the space: « a » is the word again
        Assert.Equal("a", tr.Current);
        tr.Backspace();                      // « a » gone: « il y » before the caret
        Assert.Equal("", tr.Current);
        Assert.Equal(new[] { "il", "y" }, tr.Context.TakeLast(2).ToArray());
    }

    [Fact]
    public void The_Sound_Alike_Replacement_Of_A_Glued_Word_Keeps_The_Words()
    {
        var tr = new WordTracker();
        tr.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        foreach (var c in "ilya ") tr.Type(c);
        Assert.True(tr.CanCorrect("ilya", out var sep));
        Assert.Equal(' ', sep);
        tr.CorrectFinished("ilya", "il y a");
        Assert.Equal("il y a", tr.Finished!.Value.Word);
        // the kept text is read again: its words, as the document now has them (the next-word bar follows « il y a »)
        Assert.Equal(new[] { "il", "y", "a" }, tr.Context.TakeLast(3).ToArray());
        Assert.False(SpellingIndicator.Judged(tr.Finished!.Value, out _));   // never judged: no mark
    }
}
