using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 11 (10D-0): <see cref="GrammarLexicon"/> on a small hand-made <c>grammar.sqlite</c> (schema 1, as
/// Data.Build/build_grammar.py writes it), and the engine's <see cref="PredictionEngine.Grammar"/> slot, which changes no
/// result in 10D-0.</summary>
public class GrammarLexiconTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "grammar-" + Guid.NewGuid().ToString("N"));
    private readonly string _db, _g;
    private readonly PredictionEngine _e;

    private const uint N = MorphBits.Noun, V = MorphBits.FiniteVerb, Q = MorphBits.PastParticiple, Y = MorphBits.Infinitive;
    private static readonly string[] Lemmas = { "aller", "allée", "est", "manger", "être" };
    // (ortho, analyses: lemma id, bits)
    private static readonly (string Ortho, (int L, uint B)[] A)[] Index =
    {
        ("allée", new[] { (0, Q | MorphBits.Fem | MorphBits.Sing | MorphBits.AuxEtre | MorphBits.Modal), (1, N | MorphBits.Fem | MorphBits.Sing) }),
        ("aller", new[] { (0, Y | MorphBits.AuxEtre | MorphBits.Modal) }),
        ("est", new[] { (2, N | MorphBits.Masc | MorphBits.Sing), (4, V | MorphBits.P3s | MorphBits.Indicative | MorphBits.Etre) }),
        ("mange", new[] { (3, V | MorphBits.P1s | MorphBits.P2s | MorphBits.P3s | MorphBits.Indicative | MorphBits.Subjunctive | MorphBits.Imperative) }),
        ("mangent", new[] { (3, V | MorphBits.P3p | MorphBits.Indicative | MorphBits.Subjunctive) }),
        ("inconnu", new[] { (3, V) }),            // not a form of the prediction lexicon: ignored
    };

    public GrammarLexiconTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "p.sqlite");
        _g = Path.Combine(_dir, "g.sqlite");
        Exec(_db, "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL)",
             new[] { "il", "elle", "est", "allée", "aller", "mange", "mangent", "manger", "les", "maison" }
                 .Select(w => $"INSERT INTO words VALUES ('{w}', '', 10)").ToArray());
        var rows = new List<string>
        {
            "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)", "INSERT INTO meta VALUES ('schema', '1')",
            "INSERT INTO meta VALUES ('source', 'test')", "CREATE TABLE lemma(id INTEGER PRIMARY KEY, lemma TEXT)",
            "CREATE TABLE pred_index(ortho TEXT PRIMARY KEY, a BLOB) WITHOUT ROWID",
        };
        for (int i = 0; i < Lemmas.Length; i++) rows.Add($"INSERT INTO lemma VALUES ({i}, '{Lemmas[i]}')");
        foreach (var (o, a) in Index)
            rows.Add($"INSERT INTO pred_index VALUES ('{o}', X'{string.Concat(a.Select(x => Hex((uint)x.L) + Hex(x.B)))}')");
        Exec(_g, rows[0], rows.Skip(1).ToArray());
        SqliteConnection.ClearAllPools();
        _e = PredictionEngine.Load(_db);
    }

    private static string Hex(uint v) => string.Concat(BitConverter.GetBytes(v).Select(b => b.ToString("X2")));   // little-endian

    private static void Exec(string path, string first, params string[] more)
    {
        using var con = new SqliteConnection($"Data Source={path};Pooling=False");
        con.Open();
        foreach (var sql in new[] { first }.Concat(more))
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        _e.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Loads_The_Analyses_By_Entry()
    {
        var g = GrammarLexicon.Load(_g, _e);
        Assert.Equal(5, g.FormCount);
        Assert.Equal(5, g.LemmaCount);
        var est = g.BitsOf(_e.IndexOf("est")).ToArray();
        Assert.Contains(est, b => MorphBits.Pos(b) == V && (b & MorphBits.Etre) != 0 && (b & MorphBits.P3s) != 0);
        Assert.Contains(est, b => MorphBits.Pos(b) == N && (b & MorphBits.Masc) != 0);
        Assert.Equal("V 3p I S", MorphBits.Describe(g.BitsOf(_e.IndexOf("mangent"))[0]));
        Assert.Equal("Q f s aux-être modal", MorphBits.Describe(g.BitsOf(_e.IndexOf("allée"))[0]));
        Assert.Equal(0, g.BitsOf(_e.IndexOf("maison")).Length);       // no analysis
        Assert.Equal(0, g.BitsOf(-1).Length);
        // lemma → its lexicon forms: manger (id 3) has « mange », « mangent » (« inconnu » is not in the lexicon)
        var forms = g.FormsOfLemma(3).ToArray().Select(i => _e.FormAt(i)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "mange", "mangent" }, forms);
        Assert.Equal(new[] { "aller", "allée" }, g.FormsOfLemma(0).ToArray().Select(i => _e.FormAt(i)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void A_Second_Row_For_The_Same_Entry_Is_Skipped()
    {
        // review 10D-0: two pred_index spellings with one lowercase form must not add the analyses twice (first row wins)
        var g2 = Path.Combine(_dir, "g2.sqlite");
        Exec(g2, "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)", "INSERT INTO meta VALUES ('schema', '1')",
             "CREATE TABLE lemma(id INTEGER PRIMARY KEY, lemma TEXT)", "INSERT INTO lemma VALUES (0, 'manger')",
             "CREATE TABLE pred_index(ortho TEXT PRIMARY KEY, a BLOB) WITHOUT ROWID",
             $"INSERT INTO pred_index VALUES ('MANGE', X'{Hex(0) + Hex(V | MorphBits.P1s)}')",
             $"INSERT INTO pred_index VALUES ('mange', X'{Hex(0) + Hex(V | MorphBits.P3s)}')");
        var g = GrammarLexicon.Load(g2, _e);
        Assert.Equal(1, g.FormCount);
        Assert.Equal(new[] { V | MorphBits.P1s }, g.BitsOf(_e.IndexOf("mange")).ToArray());
        Assert.Equal(new[] { _e.IndexOf("mange") }, g.FormsOfLemma(0).ToArray());
    }

    [Fact]
    public void Another_Schema_Is_Refused()
    {
        Exec(_g, "UPDATE meta SET value = '2' WHERE key = 'schema'");
        Assert.Throws<InvalidDataException>(() => GrammarLexicon.Load(_g, _e));
    }

    [Fact]
    public void Empty_And_Loaded_With_Weights_Zero_Give_The_Same_Predictions()
    {
        // 10D-1: with every weight at 0 (or GrammarOn false) a loaded grammar changes nothing; with the weights it does
        string Run()
        {
            var o = new List<string>();
            foreach (var (t, c) in new[] { ("mang", "ils"), ("all", "elle est"), ("e", "il"), ("m", (string?)null) })
                o.Add(string.Join(",", _e.Predict(t, c is null ? null : new[] { c }, 8).Select(s => s.Word + ":" + s.Score.ToString("R"))));
            o.Add(string.Join(",", _e.PredictNext(new[] { "il" }, 8).Select(s => s.Word)));
            return string.Join("\n", o);
        }
        Assert.True(_e.Grammar.IsEmpty);
        var before = Run();
        _e.Grammar = GrammarLexicon.Load(_g, _e);
        Assert.False(_e.Grammar.IsEmpty);
        Assert.NotEqual(before, Run());                       // « ils » + « mang »: « mangent » first
        var keep = (_e.WNominal, _e.WVerbal, _e.WNounSubj, _e.InjectGap);
        _e.WNominal = _e.WVerbal = _e.WNounSubj = _e.InjectGap = 0;
        Assert.Equal(before, Run());
        (_e.WNominal, _e.WVerbal, _e.WNounSubj, _e.InjectGap) = keep;
        _e.GrammarOn = false;
        Assert.Equal(before, Run());
        _e.GrammarOn = true;
        _e.Grammar = null!;
        Assert.Same(GrammarLexicon.Empty, _e.Grammar);
    }
}

/// <summary>A test on the real built databases: skipped, with the reason, when Data.Build/out has no grammar.sqlite or
/// prediction.sqlite (they are git-ignored build outputs, BUILD.md § 1 and § 1b3).</summary>
public sealed class RealGrammarFactAttribute : FactAttribute
{
    internal static readonly string Out = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data.Build", "out"));

    public RealGrammarFactAttribute()
    {
        foreach (var f in new[] { "grammar.sqlite", "prediction.sqlite" })
            if (!File.Exists(Path.Combine(Out, f)))
            {
                Skip = $"{f} absent in {Out}: build it with Data.Build (BUILD.md § 1 / § 1b3) to run this test";
                break;
            }
    }
}

/// <summary>Plan 11 (10D-0): the real <c>Data.Build/out/grammar.sqlite</c> with the real prediction lexicon (skipped with a
/// message when they are not built).</summary>
public class GrammarRealDataTests
{
    private static readonly string Out = RealGrammarFactAttribute.Out;
    private static readonly Lazy<(PredictionEngine E, GrammarLexicon G)> Data = new(() =>
    {
        var e = PredictionEngine.Load(Path.Combine(Out, "prediction.sqlite"));
        return (e, GrammarLexicon.Load(Path.Combine(Out, "grammar.sqlite"), e));
    });

    private static string[] Describe(string w)
    {
        var (e, g) = Data.Value;
        int i = e.IndexOf(w);
        var bits = g.BitsOf(i).ToArray();
        var lem = g.LemmasOf(i).ToArray();
        return bits.Select((b, k) => MorphBits.Describe(b) + " #" + lem[k]).ToArray();
    }

    [RealGrammarFact]
    public void Known_Forms_Have_Their_Analyses()
    {
        var (e, g) = Data.Value;
        Assert.True(g.FormCount > 115_000, $"{g.FormCount} forms");
        Assert.Contains(Describe("mangent"), d => d.StartsWith("V 3p I S", StringComparison.Ordinal));
        var est = Describe("est");
        Assert.Contains(est, d => d.StartsWith("V 3s I être", StringComparison.Ordinal));
        Assert.Contains(est, d => d.StartsWith("N m s", StringComparison.Ordinal));
        var allee = g.BitsOf(e.IndexOf("allée")).ToArray();
        int aller = g.LemmasOf(e.IndexOf("aller")).ToArray().Distinct().Single(l => g.FormsOfLemma(l).ToArray().Contains(e.IndexOf("vais")));
        Assert.Contains(Enumerable.Range(0, allee.Length), k => MorphBits.Describe(allee[k]) == "Q f s aux-être modal" && g.LemmasOf(e.IndexOf("allée"))[k] == aller);
        // Lexique writes « coeur »: the analyses are those of French.lex « cœur »
        Assert.Contains(Describe("coeur"), d => d.StartsWith("N m s", StringComparison.Ordinal));
    }

    [RealGrammarFact]
    public void An_Index_Built_From_Morph_Equals_Pred_Index()
    {
        // review 10D-0: the alternative for 10D-1 (no pred_index shipped) gives exactly the same arrays
        var (e, g) = Data.Value;
        Assert.True(g.SameAs(GrammarLexicon.LoadFromMorph(Path.Combine(Out, "grammar.sqlite"), e)));
    }
}

