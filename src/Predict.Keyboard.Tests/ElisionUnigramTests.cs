using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.10.2 (plan « elided forms from the first letter »): the context-free prior of a bare elision form Lexique has no
/// apostrophe row for is estimated at load from its full form and the corpus counts, so « j » at a sentence start offers
/// « j' » next to « je ». Forms with an apostrophe row (« l' ») keep Lexique's value; <c>ElisionUnigram = 0</c> is the
/// 0.10.1 behaviour exactly.</summary>
public class ElisionUnigramTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "eluni-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    public ElisionUnigramTests()
    {
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            // je 1000 per million; « j » before a vowel / mute-h word 90 + 10 times, before « c » (no elision) 50 times;
            // « je » 400 times in all: est(j') = 1000 × 100 / 400 = 250, between jour (300) and jamais (200)
            cmd.CommandText =
                "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL);" +
                "INSERT INTO words VALUES ('je','Z°',1000),('j','Z',40),('jour','ZuR',300),('jamais','Zamε',200),('juste','Zyst',100)," +
                "('ai','e',2000),('habite','abit',50),('c','s',40),('suis','s8i',800),('ne','n°',900)," +
                "('le','l°',3000),('l','l',70),('l''','l',1500),('arbre','aRbR',100),('livre','livR',150);" +
                "CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO bigrams VALUES ('j','ai',90),('j','habite',10),('j','c',50),('je','suis',300),('je','ne',100)," +
                "('l','arbre',80),('le','livre',100),('et','j',30),('et','jour',20),('et','je',10);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _e = PredictionEngine.Load(_db);
    }

    public void Dispose()
    {
        _e.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private string[] Words(string typed) => _e.Predict(typed, null, 8, sentenceStart: true).Select(s => s.Word).ToArray();

    [Fact]
    public void The_Estimate_Uses_The_Full_Form_And_The_Vowel_Successors()
    {
        var j = _e.GetElisionEstimates().Single(x => x.Bare == "j");
        Assert.Equal("je", j.Full);
        Assert.True(j.Calibrated);
        Assert.Equal(40, j.Lexique, 6);   // freq 40: a full prefix gate, as the real « j » (64)
        Assert.Equal(250, j.Estimate, 6);   // 1000 × (90 + 10) / 400: « j c » is no elision
    }

    [Fact]
    public void J_Comes_Second_At_A_Sentence_Start()
    {
        Assert.Equal(new[] { "je", "jour", "j", "jamais", "juste" }, Words("j"));
        Assert.Equal(new[] { "je", "jour", "j'", "jamais", "juste" },
                     Elision.Apply(_e.Predict("j", null, 8, sentenceStart: true)).Select(s => s.Word).ToArray());
    }

    [Fact]
    public void Off_Is_The_Old_Order_And_Back_On_Is_The_Same()
    {
        var on = _e.Predict("j", null, 8, true).ToArray();
        _e.ElisionUnigram = 0;
        Assert.Equal(new[] { "je", "jour", "jamais", "juste", "j" }, Words("j"));
        var off = _e.Predict("j", null, 8, true).ToArray();
        _e.ElisionUnigram = 2;
        Assert.Equal("j", Words("j")[1]);   // 500 > jour
        _e.ElisionUnigram = 0;
        Assert.Equal(off, _e.Predict("j", null, 8, true).ToArray());   // restored exactly
        _e.ElisionUnigram = 1;
        Assert.Equal(on, _e.Predict("j", null, 8, true).ToArray());
    }

    [Fact]
    public void A_Form_With_An_Apostrophe_Row_Keeps_Lexique()
    {
        var l = _e.GetElisionEstimates().Single(x => x.Bare == "l");
        Assert.False(l.Calibrated);
        Assert.Equal(1500, l.Lexique, 6);          // the « l' » row
        Assert.Equal(3000 * 80 / 100.0, l.Estimate, 6);
        var on = _e.Predict("l", null, 8, true).ToArray();
        _e.ElisionUnigram = 0;
        Assert.Equal(on, _e.Predict("l", null, 8, true).ToArray());
        _e.ElisionUnigram = 1;
    }

    [Fact]
    public void Next_Words_Do_Not_Move()
    {
        var on = _e.PredictNext(new[] { "je" }, 8).ToArray();
        var afterEt = _e.PredictNext(new[] { "et" }, 8).ToArray();   // « j » itself a successor: its n-gram count, not its unigram
        Assert.Contains(afterEt, x => x.Word == "j");
        _e.ElisionUnigram = 0;
        Assert.Equal(on, _e.PredictNext(new[] { "je" }, 8).ToArray());
        Assert.Equal(afterEt, _e.PredictNext(new[] { "et" }, 8).ToArray());
        _e.ElisionUnigram = 1;
    }

    /// <summary>0.10.2 (glued words): the successors of every bare form are kept from the load — only the lexicon words that
    /// take an elision (« j c » is no glued word), with the count of all successors as the total.</summary>
    [Fact]
    public void Bare_Successors_Keep_The_Elision_Words_And_The_Whole_Total()
    {
        var s = _e.GetBareSuccessors().ToDictionary(x => x.Bare);
        Assert.Equal(Elision.SplitForms.Count, s.Count);
        Assert.Equal((2, 150L), (s["j"].Successors, s["j"].Total));      // ai, habite; « c » counted in the total only
        Assert.Equal((1, 80L), (s["l"].Successors, s["l"].Total));       // a form with an apostrophe row too
        Assert.Equal(1500, s["l"].PerMillion, 6);                        // the « l' » row, higher than the bare « l »
        Assert.Equal((0, 0L), (s["qu"].Successors, s["qu"].Total));      // no row: empty, no error
    }

    [Fact]
    public void A_Nonsense_Weight_Is_Off()
    {
        _e.ElisionUnigram = double.NaN;
        Assert.Equal(0, _e.ElisionUnigram);
        _e.ElisionUnigram = -1;
        Assert.Equal(0, _e.ElisionUnigram);
        Assert.Equal("j", Words("j")[^1]);
        _e.ElisionUnigram = 1;
    }

    /// <summary>The counts' second connection fails: the same counts are read on the main one (same calibration).</summary>
    [Fact]
    public void A_Failed_Second_Connection_Falls_Back_To_The_Main_One()
    {
        SqliteConnection.ClearAllPools();
        using var e = PredictionEngine.Load(_db, null, _ => throw new SqliteException("second connection refused (test)", 14));
        Assert.Equal(Words("j"), e.Predict("j", null, 8, sentenceStart: true).Select(s => s.Word).ToArray());
        Assert.Equal("j", e.Predict("j", null, 8, sentenceStart: true)[2].Word);
    }

    /// <summary>A database without a bigrams table (words only): no estimate, the lexicon order, no error.</summary>
    [Fact]
    public void No_Bigrams_Table_Means_No_Calibration()
    {
        var db = Path.Combine(Path.GetTempPath(), "eluni-nobi-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var con = new SqliteConnection($"Data Source={db}"))
            {
                con.Open();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL);" +
                                  "INSERT INTO words VALUES ('je','Z°',1000),('j','Z',40),('jour','ZuR',300);";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            using (var e = PredictionEngine.Load(db))
            {
                Assert.Empty(e.GetElisionEstimates());
                Assert.Equal(new[] { "je", "jour", "j" }, e.Predict("j", null, 8, sentenceStart: true).Select(s => s.Word).ToArray());
                e.ElisionUnigram = 2;
                Assert.Equal(new[] { "je", "jour", "j" }, e.Predict("j", null, 8, sentenceStart: true).Select(s => s.Word).ToArray());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch (IOException) { }
        }
    }
}
