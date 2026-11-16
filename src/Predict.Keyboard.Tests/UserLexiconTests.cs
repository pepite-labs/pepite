using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 10A step 1: the personal words in the engine (prefix / sound / edit hits, capitals kept, « Pierre » and
/// « pierre » both, « Mes fautes » exact and ≥ 3-letter prefix, an empty lexicon changes nothing), on a small test DB.</summary>
public class UserLexiconTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "userlex-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    private static readonly (string W, string Phon, double F)[] Words =
    {
        ("pierre", "pjER", 80), ("pied", "pje", 120), ("pire", "piR", 60), ("pis", "pi", 10), ("parce", "paRs", 300),
        ("par", "paR", 2000), ("pas", "pa", 5000), ("paris", "paRi", 50), ("le", "l°", 20000), ("la", "la", 18000),
        ("les", "le", 15000), ("lait", "lE", 40), ("léger", "leZe", 30), ("vélo", "velo", 20), ("vérité", "veRite", 70),
        ("volcan", "vOlk@", 15), ("magma", "magma", 2), ("maison", "mEz§", 400), ("quand", "k@", 3000), ("beaucoup", "boku", 2500),
        ("école", "ekOl", 200), ("trois", "tRwa", 900), ("tom", "tOm", 1), ("tomate", "tOmat", 12), ("ton", "t§", 1800),
    };

    public UserLexiconTests()
    {
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL)";
            cmd.ExecuteNonQuery();
            foreach (var (w, p, f) in Words)
            {
                cmd.CommandText = "INSERT INTO words VALUES ($o, $p, $f)";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$o", w);
                cmd.Parameters.AddWithValue("$p", p);
                cmd.Parameters.AddWithValue("$f", f);
                cmd.ExecuteNonQuery();
            }
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

    private static string[] W(IEnumerable<Suggestion> s) => s.Select(x => x.Word).ToArray();

    [Fact]
    public void Empty_Lexicon_Gives_Identical_Results()
    {
        var rnd = new Random(7);
        var queries = Enumerable.Range(0, 1000).Select(_ =>
        {
            var w = Words[rnd.Next(Words.Length)].W;
            var q = w[..rnd.Next(1, w.Length + 1)];
            return rnd.Next(4) == 0 ? q + "z" : q;   // some misspelt
        }).ToList();
        var before = queries.Select(q => _e.Predict(q, null, 8)).ToList();
        _e.User = UserLexicon.Build(new[] { new UserWord("Zorglub") });
        _e.User = UserLexicon.Empty;
        for (int i = 0; i < queries.Count; i++)
            Assert.Equal(before[i].Select(s => (s.Word, s.Score, s.Kind, s.Name)), _e.Predict(queries[i], null, 8).Select(s => (s.Word, s.Score, s.Kind, s.Name)));
        Assert.Same(UserLexicon.Empty, UserLexicon.Build(Array.Empty<UserWord>()));
        _e.User = null!;   // null means empty
        Assert.Same(UserLexicon.Empty, _e.User);
    }

    [Fact]
    public void New_Word_Is_Found_By_Prefix_Sound_And_Edit_With_Its_Capital()
    {
        _e.User = UserLexicon.Build(new[] { new UserWord("Vésuve") });
        var p = _e.Predict("Vé", null, 8);         // a name, typed with its capital
        Assert.Equal("Vésuve", p[0].Word);
        Assert.True(p[0].Name);
        Assert.Contains("Vésuve", W(_e.Predict("vé", null, 8)));   // in lowercase: still offered (a smaller lift)
        Assert.Contains("Vésuve", W(_e.Predict("vezu", null, 8)));          // sound prefix (z = s between vowels)
        Assert.Contains("Vésuve", W(_e.Predict("vésuev", null, 8)));       // one letter swapped
        Assert.True(_e.KnowsAny("vésuve"));
        Assert.False(_e.Knows("vésuve"));
    }

    [Fact]
    public void Pierre_And_pierre_Are_Both_Offered()
    {
        _e.User = UserLexicon.Build(new[] { new UserWord("Pierre") });
        var w = W(_e.Predict("pie", null, 8));
        Assert.Contains("Pierre", w);
        Assert.Contains("pierre", w);
        var name = _e.Predict("pie", null, 8).First(s => s.Word == "Pierre");
        Assert.True(name.Name);
        Assert.False(_e.Predict("pie", null, 8).First(s => s.Word == "pierre").Name);
        // shown through the display step, both stay (case-sensitive)
        Assert.Equal(2, Elision.Apply(_e.Predict("pie", null, 8)).Count(s => s.Word.Equals("pierre", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void A_Lowercase_Personal_Word_Lifts_The_Lexicon_Entry()
    {
        var before = W(_e.Predict("ma", null, 8));
        Assert.Equal("maison", before[0]);
        double before0 = _e.Predict("ma", null, 25).First(x => x.Word == "magma").Score;
        _e.User = UserLexicon.Build(new[] { new UserWord("magma") });
        Assert.True(_e.Predict("ma", null, 25).First(x => x.Word == "magma").Score > before0);
        _e.WUser = 0.5;   // this tiny test lexicon: a lift large enough to pass « maison »
        var after = _e.Predict("ma", null, 8);
        Assert.Equal("magma", after[0].Word);
        Assert.False(after[0].Name);
        Assert.Single(after, s => s.Word == "magma");
    }

    [Fact]
    public void Spelling_Pairs_Exact_And_Three_Letter_Prefix_Are_Top1()
    {
        _e.User = UserLexicon.Build(Array.Empty<UserWord>(), new[] { new SpellingFix("pace", "parce"), new SpellingFix("bocou", "beaucoup"), new SpellingFix("kan", "quand") });
        Assert.Equal("parce", _e.Predict("pace", null, 8)[0].Word);
        Assert.Equal(MatchKind.Phonetic, _e.Predict("pace", null, 8)[0].Kind);
        Assert.Equal("beaucoup", _e.Predict("boc", null, 8)[0].Word);       // first 3 letters
        Assert.Equal("quand", _e.Predict("kan", null, 8)[0].Word);          // a 3-letter pair, exact
        Assert.Equal("beaucoup", _e.Predict("BOCOU", null, 8)[0].Word);     // case and accents ignored
    }

    [Fact]
    public void Hyphenated_Name_Is_Indexed_Whole_And_By_Parts()
    {
        var lex = UserLexicon.Build(new[] { new UserWord("Jean-Baptiste"), new UserWord("Valbrune-les-Pins") });
        Assert.True(lex.Contains("jean-baptiste"));
        Assert.True(lex.Contains("Baptiste"));
        Assert.True(lex.Contains("Valbrune"));
        Assert.False(lex.Contains("les"));   // a lowercase part of a name is not a word of its own
        Assert.Equal("Baptiste", lex.NameForm("baptiste"));
        _e.User = lex;
        Assert.Contains("Baptiste", W(_e.Predict("Bap", null, 8)));
        Assert.Contains("Jean-Baptiste", W(_e.Predict("jea", null, 8)));
    }

    [Fact]
    public void Duplicates_Keep_The_Highest_Weight()
    {
        var lex = UserLexicon.Build(new[] { new UserWord("Léa", 0.5), new UserWord("Léa", 1.0), new UserWord("léa") });
        Assert.Equal(2, lex.Count);
        Assert.Equal("Léa", lex.NameForm("LÉA"));
    }

    [Fact]
    public void Many_New_Words_On_A_Short_Prefix_Keep_The_Best_Ones()
    {
        // 3 000 new words in « z… » (all alike for the model) + a known word: the shortest new words and the known one
        var words = Enumerable.Range(0, 3000).Select(i => new UserWord("z" + WordListModelTests.Name(i))).ToList();
        words.Add(new UserWord("zo"));
        words.Add(new UserWord("Zita"));
        _e.User = UserLexicon.Build(words);
        var w = W(_e.Predict("z", null, 8));
        Assert.Equal("zo", w[0]);
        Assert.All(w.Skip(1), x => Assert.True(x.Length <= 7, x));
        Assert.Equal("Zita", _e.Predict("Z", null, 8)[0].Word);   // a name typed with its capital
    }

    [Fact]
    public void A_Capital_At_A_Sentence_Start_Does_Not_Lift_Names()
    {
        _e.WUser = 0.01;   // this tiny test lexicon: a lift that matters next to « ton »
        _e.User = UserLexicon.Build(new[] { new UserWord("Tomi") });
        Assert.Equal("Tomi", _e.Predict("To", null, 8)[0].Word);                          // a capital inside a sentence
        Assert.NotEqual("Tomi", _e.Predict("To", null, 8, sentenceStart: true)[0].Word);  // « To… » starting a sentence
    }

    [Fact]
    public void A_Capitalised_Spelling_Target_Is_A_Name()
    {
        _e.User = UserLexicon.Build(Array.Empty<UserWord>(), new[] { new SpellingFix("piere", "Pierre") });
        var s = _e.Predict("piere", null, 8)[0];
        Assert.Equal("Pierre", s.Word);
        Assert.True(s.Name);
    }
}
