using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Owner 2026-11-27 (D33, plan dys-sound-tolerance Phase 2): a word reached through a <b>ticked</b> pair is shown
/// green « même son » even when the edit pass (one letter changed) scores it higher — « la dable » → table with t/d ticked.
/// The score (so the rank) is the better of the two; with the pair not ticked the same word stays an edit (orange).</summary>
public class TickedPairGreenTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "tickedgreen-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    public TickedPairGreenTests()
    {
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL);" +
                "INSERT INTO words VALUES ('table','tabl',200),('grogne','gRoN',20),('bateau','bato',150),('voiture','vwatyR',150)," +
                "('la','la',3000),('le','l°',3000),('il','il',900),('une','yn',1500),('maison','mEz§',300),('tapis','tapi',60);" +
                "CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO bigrams VALUES ('la','table',40),('il','grogne',5),('le','bateau',30),('la','voiture',30);" +
                "CREATE TABLE trigrams (p2 TEXT, p1 TEXT, word TEXT, c INTEGER);";
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

    private Suggestion[] Bar(string typed, string prev) => _e.Predict(typed, new[] { prev }, 8, sentenceStart: false).ToArray();

    [Theory]
    [InlineData("dable", "la", "table", SoundPairs.TD)]
    [InlineData("crogne", "il", "grogne", SoundPairs.KG)]
    [InlineData("pateau", "le", "bateau", SoundPairs.PB)]
    [InlineData("foiture", "la", "voiture", SoundPairs.FV)]
    public void A_Ticked_Pair_Makes_The_Word_Green_At_The_Same_Rank(string typed, string prev, string word, SoundPairs pair)
    {
        _e.Tolerance = SoundPairs.ChJ;                                   // the pair not ticked: an edit, orange
        var off = Bar(typed, prev);
        var o = Assert.Single(off, s => s.Word == word);
        Assert.Equal(MatchKind.Edit, o.Kind);
        _e.Tolerance = SoundPairs.ChJ | pair;                            // ticked: green « même son »
        var on = Bar(typed, prev);
        var g = Assert.Single(on, s => s.Word == word);
        Assert.Equal(MatchKind.Phonetic, g.Kind);
        Assert.Equal(Array.IndexOf(off, o), Array.IndexOf(on, g));      // the better score kept: same place
        Assert.Equal(o.Score, g.Score, 9);
    }
}
