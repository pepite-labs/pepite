using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan corpus-light 0.1: the n-gram tables are built with « œ » → « oe » (Lexique spells « coeur »), so the
/// engine folds the context words the same way: « mon cœur » and « mon coeur » give the same next words.</summary>
public class ContextFoldTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "fold-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    public ContextFoldTests()
    {
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL);" +
                "INSERT INTO words VALUES ('mon','m§',900),('coeur','k9R',200),('soeur','s9R',100),('bat','ba',30),('est','E',9000),('le','l°',20000);" +
                "CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO bigrams VALUES ('coeur','bat',50),('soeur','est',40),('mon','coeur',30);" +
                "CREATE TABLE trigrams (p2 TEXT, p1 TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO trigrams VALUES ('mon','coeur','bat',20);";
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

    [Theory]
    [InlineData("cœur", "coeur")] [InlineData("sœur", "soeur")] [InlineData("nævus", "naevus")] [InlineData("coeur", "coeur")]
    [InlineData("été", "été")] [InlineData("", "")] [InlineData("Œuvre", "Oeuvre")] [InlineData("Æsope", "Aesope")]
    public void Fold_Writes_Oe_And_Ae(string token, string folded) => Assert.Equal(folded, TokenFold.Fold(token));

    [Theory]
    [InlineData("mon cœur", "mon coeur")] [InlineData("ma sœur", "ma soeur")] [InlineData("Mon Cœur", "mon coeur")]
    public void Folded_And_Unfolded_Contexts_Give_The_Same_Next_Words(string withLigature, string plain)
    {
        var a = _e.PredictNext(new[] { withLigature }, 8).Select(s => s.Word).ToList();
        var b = _e.PredictNext(new[] { plain }, 8).Select(s => s.Word).ToList();
        Assert.NotEmpty(a);
        Assert.Equal(b, a);
    }

    [Fact]
    public void Ligature_Context_Reaches_The_Ngram()
    {
        Assert.Equal("bat", _e.PredictNext(new[] { "mon cœur" }, 8)[0].Word);
        Assert.Equal("bat", _e.Predict("b", new[] { "mon cœur" }, 8)[0].Word);
    }
}
