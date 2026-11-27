using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>
/// 0.11.0 (plan offensive-words, Phase A): the « mots exclus » are never offered — by any source of the bar, the next word or
/// the ≈ list — while a pupil who types one still gets ✓. <b>Invented words only</b> (none is French, none is on any list):
/// each test first shows the source offers the word without the list, then that it does not with it.
/// </summary>
public class ExclusionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "excl-" + Guid.NewGuid().ToString("N"));
    private readonly string _db;
    private readonly PredictionEngine _e;
    private readonly ExcludedWords _x;

    /// <summary>The invented words the fixture's list excludes.</summary>
    private static readonly string[] Listed = { "zorblat", "zorblats", "kroutir", "jabule", "trambule", "ouflor", "glorbitude", "vrombal", "quagmor" };

    public ExclusionTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "prediction.sqlite");
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL);" +
                "INSERT INTO words VALUES ('zorblat','zORbla',300),('zorblats','zORbla',50),('zorbe','zORb',5),('zorbi','zORbi',4)," +
                "('zorbo','zORbo',4),('zorbu','zORby',3),('zorga','zORga',3),('zorgu','zORgy',3),('zorpi','zORpi',2),('zorvan','zORv@',2)," +
                "('zorquin','zORk5',2),('kroutir','kRutiR',200),('jabule','Zabyl',200),('trambule','tR@byl',200),('ouflor','uflOR',200)," +
                "('le','l°',3000),('la','la',3000),('l','l',50),('l''','l',3000),('un','1',2000),('il','il',2000),('est','E',2000)," +
                "('glorbin','glORb5',1),('nuvo','nyvo',50);" +
                "CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO bigrams VALUES ('le','zorblat',500),('le','zorbe',10),('l','ouflor',300),('la','quagmor',400),('la','nuvo',5)," +
                "('le','kroutir',100),('un','jabule',100);" +
                "CREATE TABLE trigrams (p2 TEXT, p1 TEXT, word TEXT, c INTEGER);" +
                "INSERT INTO trigrams VALUES ('il','est','zorblat',50);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        _e = PredictionEngine.Load(_db);                 // no exclusions folder next to it: none
        _x = WriteList(Path.Combine(_dir, "list.txt"), Listed, new[] { "zorblat" });
    }

    public void Dispose()
    {
        _e.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string B64(string w) => Convert.ToBase64String(Encoding.UTF8.GetBytes(w));

    private static string FileText(IEnumerable<string> forms, IEnumerable<string>? lemmas = null, IEnumerable<string>? names = null)
    {
        var sb = new StringBuilder(ExcludedWords.Header + "\n#categories R 1 H 0 S 0 D 0 X 2 V 0 I 0 G 0\n");
        foreach (var f in forms) sb.Append(B64(f)).Append('\n');
        if (lemmas != null) { sb.Append(ExcludedWords.LemmasMark).Append('\n'); foreach (var l in lemmas) sb.Append(B64(l)).Append('\n'); }
        if (names != null) { sb.Append(ExcludedWords.NamesMark).Append('\n'); foreach (var l in names) sb.Append(B64(l)).Append('\n'); }
        return sb.ToString();
    }

    private ExcludedWords WriteList(string path, IEnumerable<string> forms, IEnumerable<string>? lemmas = null,
                                    IEnumerable<string>? names = null)
    {
        File.WriteAllText(path, FileText(forms, lemmas, names), Encoding.ASCII);
        return ExcludedWords.FromFile(path, _e);
    }

    private string[] Bar(string typed, string? ctx = null, int k = 8, bool start = false) =>
        _e.Predict(typed, ctx is null ? null : ctx.Split(' '), k, start).Select(s => s.Word).ToArray();

    /// <summary>Without the list the source offers the word; with it, never — and the safety net had nothing to drop.</summary>
    private void Offered_Then_Not(string word, Func<string[]> query)
    {
        _e.Excluded = ExcludedWords.Empty;
        Assert.True(query().Contains(word, StringComparer.OrdinalIgnoreCase), "the fixture's source does not offer the word without the list");
        _e.Excluded = _x;
        long late = _e.ExcludedLate;
        var r = query();
        Assert.DoesNotContain(r, w => _x.IsExcluded(w));
        Assert.Equal(late, _e.ExcludedLate);
    }

    // ---- the file

    [Fact]
    public void File_Reads_Forms_Lemmas_Categories_And_Hash()
    {
        Assert.Equal(Listed.Length, _x.Count);
        Assert.Equal(Listed.Length, _x.ListedCount);
        Assert.Equal(1, _x.LemmaCount);
        Assert.Equal(2, _x.Categories['X']);
        Assert.Equal(64, _x.Sha256.Length);
        Assert.Equal(6, _x.EntryCount);                                  // the listed forms that are lexicon entries
        Assert.DoesNotContain(Listed, w => _x.Describe().Contains(w));  // the log line names no word
    }

    [Fact]
    public void Apostrophe_And_Case_Rule()
    {
        Assert.True(_x.IsExcluded("ZORBLAT"));
        Assert.True(_x.IsExcluded("l'ouflor"));
        Assert.True(_x.IsExcluded("qu’ouflor"));
        Assert.True(_x.IsExcluded("jusqu'à'zorblat"));
        Assert.False(_x.IsExcluded("zorblatx"));
        Assert.False(_x.IsExcluded("l'"));
        Assert.False(_x.IsExcluded("zorbe"));
        Assert.False(_x.IsExcluded(""));
        Assert.False(ExcludedWords.Empty.IsExcluded("zorblat"));
    }

    [Fact]
    public void Bad_Files_Are_Refused_And_Load_Gives_None()
    {
        var plain = Path.Combine(_dir, "plain.txt");
        File.WriteAllText(plain, "zorblat\n");
        Assert.Throws<InvalidDataException>(() => ExcludedWords.FromFile(plain, _e));
        File.WriteAllText(plain, ExcludedWords.Header + "\nnot*base64\n");
        Assert.Throws<InvalidDataException>(() => ExcludedWords.FromFile(plain, _e));
        File.WriteAllText(plain, "");
        Assert.Throws<InvalidDataException>(() => ExcludedWords.FromFile(plain, _e));
        // the engine: an unreadable list is none, with the reason, and the engine still works
        using var e2 = PredictionEngine.Load(_db, null, plain);
        Assert.True(e2.Excluded.IsEmpty);
        Assert.NotNull(e2.ExclusionsProblem);
        Assert.Contains("zorblat", e2.Predict("zorbl").Select(s => s.Word));
    }

    [Fact]
    public void The_List_Next_To_The_Database_Is_Loaded()
    {
        var dir = Path.Combine(_dir, "pack");
        Directory.CreateDirectory(Path.Combine(dir, ExcludedWords.FolderName));
        var db = Path.Combine(dir, "prediction.sqlite");
        File.Copy(_db, db);
        File.WriteAllText(Path.Combine(dir, ExcludedWords.FolderName, ExcludedWords.FileName), FileText(Listed), Encoding.ASCII);
        using (var e2 = PredictionEngine.Load(db))
        {
            Assert.Equal(Listed.Length, e2.Excluded.Count);
            Assert.Null(e2.ExclusionsProblem);
            Assert.DoesNotContain("zorblat", e2.Predict("zorbl").Select(s => s.Word));
        }
        SqliteConnection.ClearAllPools();
    }

    // ---- every source

    [Fact] public void Literal_Prefix() => Offered_Then_Not("zorblat", () => Bar("zorbl"));
    [Fact] public void Sound_Prefix() => Offered_Then_Not("kroutir", () => Bar("croutir"));
    [Fact] public void Tolerant_ChJ() => Offered_Then_Not("jabule", () => Bar("chabule"));
    [Fact] public void Edit_1() => Offered_Then_Not("trambule", () => Bar("tarmbule"));
    [Fact] public void Joined_Split() => Offered_Then_Not("l'ouflor", () => Bar("louflor", "le"));
    [Fact] public void Next_Word_Lexicon_Entry() => Offered_Then_Not("zorblat", () => _e.PredictNext(new[] { "le" }).Select(s => s.Word).ToArray());
    [Fact] public void Next_Word_Trigram() => Offered_Then_Not("zorblat", () => _e.PredictNext(new[] { "il", "est" }).Select(s => s.Word).ToArray());

    [Fact]
    public void Next_Word_Unknown_Corpus_Form()
    {
        // a corpus word that is no lexicon form (as the inflections known only to grammar.sqlite)
        Offered_Then_Not("quagmor", () => _e.PredictNext(new[] { "la" }).Select(s => s.Word).ToArray());
    }

    [Fact]
    public void Personal_Word_And_Mes_Fautes_Target()
    {
        _e.User = UserLexicon.Build(new[] { new UserWord("glorbitude"), new UserWord("glorbin") }, new[] { new SpellingFix("blipo", "zorblat") });
        Offered_Then_Not("glorbitude", () => Bar("glorb"));
        Offered_Then_Not("zorblat", () => Bar("blipo"));
        Offered_Then_Not("zorblat", () => _e.Check("blipo").Candidates.ToArray());
        _e.Excluded = _x;
        Assert.Contains("glorbin", Bar("glorb"));                   // the other personal word stays
    }

    [Fact]
    public void Pack_Name()
    {
        _e.Names = NamesLexicon.Build(new[] { new NameRecord("Vrombal", Array.Empty<string>(), "test", 0.9, 1, null, null, null, null) }, _e.Knows);
        Offered_Then_Not("Vrombal", () => Bar("Vromb", "le"));
        Offered_Then_Not("Vrombal", () => _e.Check("vrombal").Candidates.ToArray());   // the same letters in another case
    }

    [Fact]
    public void Check_Lists()
    {
        Offered_Then_Not("zorblat", () => _e.Check("zôrblat").Candidates.ToArray());        // the same letters, other accents
        Offered_Then_Not("jabule", () => _e.Check("chabule").Candidates.ToArray());          // tolerant tier
        Offered_Then_Not("l'ouflor", () => _e.Check("louflor").Candidates.ToArray());        // joined
    }

    // ---- a typed listed word

    [Fact]
    public void Typed_Listed_Word_Is_Known_But_Never_Placed()
    {
        _e.Excluded = _x;
        Assert.Equal(SpellingStatus.Known, _e.Check("zorblat").Status);
        Assert.True(_e.KnowsForMark("zorblat", false));
        foreach (var mode in new[] { ExactTypedMode.Present, ExactTypedMode.First })
        {
            _e.ExactTyped = mode;
            Assert.DoesNotContain("zorblat", Bar("zorblat"));
            Assert.DoesNotContain("zorblat", Bar("zorblat", "le"));
        }
        _e.ExactTyped = ExactTypedMode.Present;
        Assert.Equal(0, _e.ExcludedLate);
    }

    [Fact]
    public void The_Bar_Keeps_Its_Tiles()
    {
        _e.Excluded = _x;
        var bar = Bar("zor");
        Assert.Equal(8, bar.Length);
        Assert.DoesNotContain(bar, w => _x.IsExcluded(w));
    }

    [Fact]
    public void Empty_List_Gives_The_Results_Without_It()
    {
        var qs = new[] { ("zorbl", (string?)null), ("croutir", null), ("chabule", null), ("tarmbule", null), ("louflor", "le"), ("zor", null) };
        _e.Excluded = ExcludedWords.Empty;
        var a = qs.Select(q => _e.Predict(q.Item1, q.Item2?.Split(' '), 8).ToArray()).ToList();
        var b = qs.Select(q => _e.Predict(q.Item1, q.Item2?.Split(' '), 8).ToArray()).ToList();
        for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);
        // a list without any listed word in these queries changes nothing either
        _e.Excluded = WriteList(Path.Combine(_dir, "other.txt"), new[] { "snorfle" });
        var c = qs.Select(q => _e.Predict(q.Item1, q.Item2?.Split(' '), 8).ToArray()).ToList();
        for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], c[i]);
    }

    // ---- the load-time expansion and the swap

    [Fact]
    public void Grammar_Expansion_Keeps_The_Homograph_Guard()
    {
        var g = Path.Combine(_dir, "grammar.sqlite");
        using (var con = new SqliteConnection($"Data Source={g}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "CREATE TABLE lemma(id INTEGER PRIMARY KEY, lemma TEXT);" +
                "CREATE TABLE morph(form TEXT, lemma_id INTEGER, tag_id INTEGER, PRIMARY KEY(form, lemma_id, tag_id)) WITHOUT ROWID;" +
                "INSERT INTO lemma VALUES (1,'zorblat'),(2,'pimbal'),(3,'snorfle');" +
                // a grammar-only form of the excluded lemma; one shared with an innocent lemma; a lexicon form
                "INSERT INTO morph VALUES ('zorblate',1,1),('zorblon',1,1),('zorblon',2,1),('zorblats',1,1),('pimbal',2,1);";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var x = WriteList(Path.Combine(_dir, "g.txt"), new[] { "zorblat" }, new[] { "zorblat", "snorfle" });
        var y = x.WithGrammar(g, _e);
        SqliteConnection.ClearAllPools();
        Assert.True(y.IsExcluded("zorblate"));          // every grammar lemma excluded, no lexicon form
        Assert.False(y.IsExcluded("zorblon"));          // also a form of an innocent lemma: the guard keeps it
        Assert.True(y.IsExcluded("snorfle"));           // a lemma spelling in no table
        Assert.False(y.IsExcluded("zorblats"));         // a lexicon form: decided when the list was built
        Assert.Equal(2, y.GrammarCount);
        Assert.Same(x, x.WithGrammar(Path.Combine(_dir, "missing.sqlite"), _e));
        // and a word the corpus has but the lexicon not: excluded once the grammar is in
        Assert.True(y.WithExtra(new[] { "quagmor" }, _e).IsExcluded("quagmor"));
    }

    [Fact]
    public void Parts_Are_Kept_Apart_And_Composed_In_One_Place()
    {
        var x = WriteList(Path.Combine(_dir, "p.txt"), new[] { "zorblat" }, new[] { "zorblat" });
        // the teacher's words, then the grammar forms, then the teacher's words again: no part is lost
        var a = x.WithExtra(new[] { "kroutir" }, _e).WithGrammarForms(new[] { "zorblate" });
        Assert.True(a.IsExcluded("zorblat") && a.IsExcluded("kroutir") && a.IsExcluded("zorblate"));
        var b = a.WithExtra(new[] { "jabule" }, _e);
        Assert.True(b.IsExcluded("zorblate") && b.IsExcluded("jabule"));
        Assert.False(b.IsExcluded("kroutir"));                       // the teacher part is replaced, not added to
        Assert.Equal((1, 1, 1), (b.ListedCount, b.GrammarCount, b.ExtraCount));
        _e.Excluded = b;
        Assert.DoesNotContain("jabule", Bar("chabule"));             // the entry flags follow the teacher part
        // a forced interleaving: while the grammar update computes, a teacher update swaps in first; the grammar update must
        // see that its snapshot is stale, run again on the new one, and keep the teacher part
        _e.Excluded = x;
        int calls = 0;
        _e.UpdateExcluded(c =>
        {
            if (++calls == 1) _e.UpdateExcluded(d => d.WithExtra(new[] { "kroutir" }, _e));
            return c.WithGrammarForms(new[] { "zorblate" });
        });
        Assert.Equal(2, calls);
        Assert.True(_e.Excluded.IsExcluded("zorblate") && _e.Excluded.IsExcluded("kroutir") && _e.Excluded.IsExcluded("zorblat"));
        Assert.Equal((1, 1), (_e.Excluded.GrammarCount, _e.Excluded.ExtraCount));
    }

    [Fact]
    public void Exempt_Pack_Name_Keeps_Its_Capital_Only()
    {
        // owner 2026-11-27: a listed spelling that is also a pack name stays offered as that name, with its capital
        var x = WriteList(Path.Combine(_dir, "n.txt"), Listed, null, new[] { "zorblat" });
        Assert.Equal(1, x.NamesExemptCount);
        Assert.True(x.NameExempt("Zorblat"));
        Assert.False(x.NameExempt("zorblat"));
        _e.Names = NamesLexicon.Build(new[] { new NameRecord("Zorblat", Array.Empty<string>(), "test", 0.9, 1, null, null, null, null),
                                             new NameRecord("Vrombal", Array.Empty<string>(), "test", 0.9, 1, null, null, null, null) }, _e.Knows);
        _e.Excluded = x;
        long late = _e.ExcludedLate;
        var bar = Bar("Zorbl", "le");
        Assert.Contains("Zorblat", bar);                             // the pack name, with its capital
        Assert.DoesNotContain("zorblat", bar);                       // never the lowercase lexicon word
        Assert.DoesNotContain("zorblat", Bar("zorbl"));
        Assert.DoesNotContain("Vrombal", Bar("Vromb", "le"));        // a listed pack name without the exemption
        var c = _e.Check("zôrblat").Candidates;
        Assert.Contains("Zorblat", c);
        Assert.DoesNotContain("zorblat", c);
        // a personal word with the same letters is not exempt
        _e.Names = NamesLexicon.Empty;
        _e.User = UserLexicon.Build(new[] { new UserWord("Zorblat") });
        Assert.DoesNotContain(Bar("Zorbl", "le"), w => string.Equals(w, "zorblat", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(late, _e.ExcludedLate);
    }

    [Fact]
    public async Task Swapping_The_Snapshot_While_Querying_Is_Safe()
    {
        var other = WriteList(Path.Combine(_dir, "o.txt"), new[] { "zorblat" });
        _e.Excluded = _x;
        using var stop = new CancellationTokenSource();
        var swapper = Task.Run(() => { bool f = false; while (!stop.IsCancellationRequested) { _e.Excluded = (f = !f) ? other : _x; } });
        var qs = new[] { "zorbl", "zor", "croutir", "chabule" };
        for (int i = 0; i < 2000; i++)
            Assert.DoesNotContain("zorblat", Bar(qs[i % qs.Length]));   // excluded by both snapshots
        stop.Cancel();
        await swapper;
        Assert.Equal(0, _e.ExcludedLate);
    }
}

/// <summary>0.11.0: a test on the real data and the real list (<c>Data.Build/out</c>: prediction.sqlite, grammar.sqlite,
/// exclusions/fr.txt). Skipped locally when a file is missing; with <c>PEPITE_REQUIRE_DATA=1</c> (the CI test job, which
/// fetches the data pack and ships the list) it runs anyway, so a missing file fails instead of passing silently.</summary>
public sealed class RealExclusionFactAttribute : FactAttribute
{
    public RealExclusionFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PEPITE_REQUIRE_DATA") == "1") return;
        foreach (var f in new[] { "prediction.sqlite", "grammar.sqlite", Path.Combine("exclusions", "fr.txt") })
            if (!File.Exists(Path.Combine(RealGrammarFactAttribute.Out, f)))
            {
                Skip = $"{f} absent in {RealGrammarFactAttribute.Out}: build it (BUILD.md § 1, § 1b4) to run this test";
                break;
            }
    }
}

/// <summary>0.11.0: the real list with the real data (skipped without <c>Data.Build/out</c>): the « Ginet » case of the
/// dys-sound review, and the grammar's injected forms — by ids and counts only, no word in this file.</summary>
public class RealExclusionTests
{
    private static readonly string Out = RealGrammarFactAttribute.Out;
    private static readonly object Gate = new();
    private static readonly Lazy<(PredictionEngine E, GrammarLexicon G, ExcludedWords X)> Data = new(() =>
    {
        foreach (var f in new[] { "prediction.sqlite", "grammar.sqlite", Path.Combine("exclusions", "fr.txt") })
            Assert.True(File.Exists(Path.Combine(Out, f)), $"{f} missing in {Out} (PEPITE_REQUIRE_DATA=1)");
        var e = PredictionEngine.Load(Path.Combine(Out, "prediction.sqlite"));
        var g = GrammarLexicon.Load(Path.Combine(Out, "grammar.sqlite"), e);
        e.Grammar = g;
        e.Excluded = e.Excluded.WithGrammar(Path.Combine(Out, "grammar.sqlite"), e);
        return (e, g, e.Excluded);
    });

    /// <summary>A form of <c>forms.tsv</c> by its id (kept in memory only).</summary>
    private static string Form(string id) =>
        File.ReadLines(Path.Combine(Out, "..", "exclusions", "forms.tsv")).Skip(1).Select(l => l.Split('\t')).First(c => c[0] == id)[1];

    [RealExclusionFact]
    public void The_List_Is_Loaded_And_Expanded()
    {
        var x = Data.Value.X;
        Assert.True(x.ListedCount > 200);
        Assert.True(x.GrammarCount > 100);
        Assert.Equal(x.ListedCount, x.EntryCount);   // every listed form is a lexicon entry (forms.tsv: prediction forms only)
    }

    [RealExclusionFact]
    public void Ginet_No_Longer_Brings_F00055()
    {
        lock (Gate)
        {
            var (e, _, x) = Data.Value;
            var target = Form("F00055");
            e.Excluded = ExcludedWords.Empty;
            bool before = e.Predict("Ginet", null, 8, true).Any(s => string.Equals(s.Word, target, StringComparison.OrdinalIgnoreCase));
            e.Excluded = x;
            long late = e.ExcludedLate;
            bool after = e.Predict("Ginet", null, 8, true).Any(s => string.Equals(s.Word, target, StringComparison.OrdinalIgnoreCase));
            Assert.True(before, "F00055 was not in the bar without the list (the regression case moved)");
            Assert.False(after, "F00055 offered for the « Ginet » case");
            Assert.Equal(late, e.ExcludedLate);
        }
    }

    [RealExclusionFact]
    public void Inject_Sibling_And_Homophone_Family_Are_Filtered()
    {
        lock (Gate)
        {
            var (e, _, x) = Data.Value;
            // ordinary words excluded for this test only: the sibling « mangé » (lent by « mange » after « il a ») and the
            // family member « est » (after « il », typed « et »)
            static bool Has(IReadOnlyList<Suggestion> s, string w) => s.Any(t => t.Word == w);
            e.Excluded = x;
            Assert.True(Has(e.Predict("mange", new[] { "il", "a" }, 8), "mangé"));
            Assert.True(Has(e.Predict("et", new[] { "il" }, 8), "est"));
            try
            {
                e.Excluded = x.WithExtra(new[] { "mangé", "est" }, e);
                long late = e.ExcludedLate;
                Assert.False(Has(e.Predict("mange", new[] { "il", "a" }, 8), "mangé"));
                Assert.False(Has(e.Predict("et", new[] { "il" }, 8), "est"));
                Assert.Equal(late, e.ExcludedLate);
            }
            finally { e.Excluded = x; }
        }
    }

    [RealExclusionFact]
    public void Every_Excluded_Form_Keeps_Its_Mark_And_Is_Never_Offered()
    {
        lock (Gate)
        {
            var (e, _, x) = Data.Value;
            e.Excluded = x;
            long late = e.ExcludedLate;
            int changed = 0;
            foreach (var f in x.ProbeForms)
            {
                // the mark of a typed listed word is the mark it had without the list (✓ when the spelling reference has it)
                e.Excluded = ExcludedWords.Empty;
                bool k0 = e.Check(f).Status == SpellingStatus.Known, m0 = e.KnowsForMark(f, false);
                e.Excluded = x;
                if (k0 != (e.Check(f).Status == SpellingStatus.Known) || m0 != e.KnowsForMark(f, false)) changed++;
                for (int p = 2; p <= f.Length; p += 2)
                    Assert.False(e.Predict(f[..p], new[] { "le" }, 8).Any(s => x.IsExcluded(s.Word)), "a listed form offered");
            }
            Assert.Equal(late, e.ExcludedLate);
            Assert.Equal(0, changed);
        }
    }
}
