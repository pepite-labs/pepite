using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.11.0 (plan hyphen-names, owner 2026-11-27: « saint-n » → Saint-Nicolas, Q1 = B): the engine side — the compounds
/// of the head before a typed hyphen (<see cref="PredictionEngine.PredictAfterHyphen"/>) and the compound name slot of a word
/// typed without a name's capital (<see cref="PredictionEngine.CompoundNameSlot"/>), on a small test DB; the shipped lexicon's
/// compounds and inversions on the real one.</summary>
public class HyphenCompoundTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "hyphen-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    private static readonly (string W, string Phon, double F)[] Words =
    {
        ("saint", "s5", 40), ("sainte", "s5t", 20), ("saints", "s5", 10), ("ne", "n°", 9000), ("non", "n§", 3000), ("nous", "nu", 8000),
        ("nom", "n§", 400), ("ni", "ni", 900), ("nid", "ni", 20), ("nicolas", "nikOla", 30), ("peut", "p2", 2000), ("être", "EtR", 3000),
        ("peut-être", "p2tEtR", 900), ("êtes", "Et", 400), ("grand", "gR@", 900), ("mère", "mER", 600), ("grand-mère", "gR@mER", 120),
        ("moi", "mwa", 3000), ("mais", "mE", 5000), ("dis", "di", 500), ("il", "il", 20000), ("ici", "isi", 900), ("je", "Z°", 20000),
        ("vais", "vE", 2000), ("à", "a", 20000), ("marin", "maR5", 30), ("marché", "maRSe", 60), ("jean", "Z@", 50), ("pierre", "pjER", 80),
        // enough words of the typed letters to fill a bar ahead of the lowercase names (the slot's case)
        ("saintes", "s5t", 8), ("sainteté", "s5t°te", 9), ("saintement", "s5t°m@", 3), ("saintier", "s5tje", 2), ("saintonge", "s5t§Z", 2),
        ("saintpaulia", "s5polja", 2), ("saintsimonien", "s5simOnj5", 2), ("jeans", "Zins", 40), ("jeannette", "ZanEt", 5),
        ("jeannot", "Zano", 5), ("jeanlin", "Z@l5", 2), ("jeanne", "Zan", 9), ("jeannine", "Zanin", 2), ("jeanneton", "Zant§", 2),
        ("jeantet", "Z@tE", 2), ("porte", "pORt", 300), ("porte-monnaie", "pORt°mOnE", 20), ("est-ce", "Es", 50), ("est-ce-que", "Esk°", 60),
        ("ce", "s°", 9000), ("est", "E", 9000),
    };

    private static NameRecord N(string nom, double rang = 0.6, int niveau = 1, bool restreint = false) =>
        new(nom, Array.Empty<string>(), "test", rang, niveau, null, null, null, null, restreint);

    private static readonly NameRecord[] Pack =
    {
        N("Saint-Nicolas", 0.8), N("Saint-Nazaire", 0.5), N("Saint-Marin", 0.9), N("Saint-Martin", 0.7), N("Jean-Pierre", 0.9),
        N("Nina", 0.6), N("Saint-Néron", restreint: true), N("Pierre", 0.5),
    };

    public HyphenCompoundTests()
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
        _e.Names = Lex(Pack);
    }

    public void Dispose()
    {
        _e.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private NamesLexicon Lex(IEnumerable<NameRecord> names) => NamesLexicon.Build(names, _e.Knows, new HashSet<string>(StringComparer.Ordinal));

    private static string[] W(IEnumerable<Suggestion> s) => s.Select(x => x.Word).ToArray();

    private string[] After(string head, string typed, string ctx, int k = 8, bool neutral = false) =>
        W(_e.PredictAfterHyphen(head, typed, ctx.Split(' ', StringSplitOptions.RemoveEmptyEntries), k, false, neutral));

    [Fact]
    public void A_lowercase_head_gets_the_whole_name_first_and_the_plain_words_after()
    {
        var s = _e.PredictAfterHyphen("saint", "n", new[] { "je", "vais", "à", "saint" }, 8);
        Assert.Equal("Saint-Nicolas", s[0].Word);   // the name's own form (owner Q1 = B)
        Assert.True(s[0].Name);
        Assert.Contains("Saint-Nazaire", W(s));
        Assert.Contains("ne", W(s));                  // today's words follow
        Assert.DoesNotContain("nicolas", W(s));       // the ending of a compound tile: the same text, dropped
        Assert.DoesNotContain("Saint-Néron", W(s));   // a restricted figure needs a capitalised head and 3 letters (D30)
        // the compounds come before every plain word
        int lastCompound = Array.FindLastIndex(W(s), w => w.StartsWith("Saint-", StringComparison.Ordinal));
        int firstPlain = Array.FindIndex(W(s), w => !w.StartsWith("Saint-", StringComparison.Ordinal));
        Assert.True(lastCompound < firstPlain);
    }

    [Fact]
    public void Off_or_no_head_is_todays_list_byte_for_byte()
    {
        var ctx = new[] { "je", "vais", "à", "saint" };
        var plain = _e.Predict("n", ctx, 8);
        _e.HyphenCompounds = false;
        Assert.Equal(plain, _e.PredictAfterHyphen("saint", "n", ctx, 8));
        Assert.Equal(_e.PredictNext(ctx, 8), _e.PredictAfterHyphen("saint", "", ctx, 8));
        _e.HyphenCompounds = true;
        Assert.Equal(plain, _e.PredictAfterHyphen("", "n", ctx, 8));
        Assert.Equal(plain, _e.PredictAfterHyphen(new string('a', PredictionEngine.HyphenHeadMax + 1), "n", ctx, 8));
        // a head with no compound: today's list unchanged
        Assert.Equal(plain, _e.PredictAfterHyphen("vais", "n", ctx, 8));
    }

    [Fact]
    public void A_lexicon_compound_keeps_the_head_as_typed_and_cases_its_ending_like_the_letters()
    {
        Assert.Equal("peut-être", After("peut", "", "il peut")[0]);   // nothing else to say after « peut- » here
        string C(string head, string typed) => After(head, typed, head).First(w => w.StartsWith(head + "-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("porte-monnaie", C("porte", "m"));
        Assert.Equal("Porte-monnaie", C("Porte", "m"));
        Assert.Equal("Porte-Monnaie", C("Porte", "M"));
        Assert.Equal("PORTE-MONNAIE", C("PORTE", "MO"));
        Assert.Equal("PORTE-MONNAIE", C("PORTE", "M"));   // a head in capitals: capitals (review)
        Assert.Equal("PEUT-ÊTRE", After("PEUT", "", "PEUT")[0]);
        // a compound whose ending is one of today's words writes the same text: today's word stays, in its place
        var s = After("peut", "ê", "il peut");
        Assert.Equal("être", s[0]);
        Assert.DoesNotContain("peut-être", s);
        Assert.Contains("moi", After("dis", "m", "dis"));   // no « dis-… » compound: the pronoun keeps its place
        Assert.Equal(After("dis", "m", "dis"), W(_e.Predict("m", new[] { "dis" }, 8)));
    }

    [Fact]
    public void A_lexicon_compound_never_goes_ahead_of_todays_first_word_and_a_misspelt_one_never_shows()
    {
        var today = W(_e.Predict("m", new[] { "le", "porte" }, 8));
        var s = After("porte", "m", "le porte");
        Assert.Equal(today[0], s[0]);                     // today's first word stays first
        Assert.Contains("porte-monnaie", s);              // the compound merged by its score
        Assert.Equal(today.Where(w => s.Contains(w)), s.Where(w => !w.Contains('-')));   // today's order kept
        _e.HyphenCompounds = false;
        try { Assert.Equal(today, After("porte", "m", "le porte")); } finally { _e.HyphenCompounds = true; }
        var q = After("est", "", "qu est");
        Assert.DoesNotContain("est-ce-que", q);           // « est-ce que » misspelt: denied in code
        Assert.DoesNotContain(q, w => w.EndsWith("-ce-que", StringComparison.Ordinal));
    }

    [Fact]
    public void A_capitalised_head_inside_a_sentence_asks_for_names_and_opens_the_restricted_figures_from_3_letters()
    {
        Assert.DoesNotContain("Saint-Néron", After("Saint", "Né", "je vais à Saint"));
        Assert.Contains("Saint-Néron", After("Saint", "Nér", "je vais à Saint"));
        // a sentence start (neutral head) or a lowercase head: never
        Assert.DoesNotContain("Saint-Néron", After("Saint", "Nér", "Saint", neutral: true));
        Assert.DoesNotContain("Saint-Néron", After("saint", "nér", "je vais à saint"));
        // the bare hyphen: the most popular names of the head
        var bare = After("Saint", "", "je vais à Saint");
        Assert.Equal("Saint-Marin", bare[0]);
        Assert.Contains("Saint-Martin", bare);
    }

    [Fact]
    public void HyphenMax_caps_the_compound_tiles()
    {
        Assert.Equal(5, _e.HyphenMax);   // the default (plan fallback, evaluation.md § Noms à trait d'union): 3 places left
        _e.HyphenMax = 1;
        try
        {
            var s = After("Saint", "", "je vais à Saint");
            Assert.Single(s, w => w.StartsWith("Saint-", StringComparison.Ordinal));
        }
        finally { _e.HyphenMax = 5; }
    }

    [Fact]
    public void An_excluded_compound_is_never_offered()
    {
        var keep = _e.Excluded;
        try
        {
            _e.Excluded = ExcludedWords.Empty.WithExtra(new[] { "saint-nicolas" }, _e);
            Assert.DoesNotContain("Saint-Nicolas", After("saint", "n", "je vais à saint"));
            Assert.DoesNotContain("Saint-Nicolas", W(_e.Predict("saint", null, 8, sentenceStart: true)));
        }
        finally { _e.Excluded = keep; }
    }

    [Fact]
    public void The_exact_typed_word_kept_in_the_bar_stays_in_it_after_the_compounds()
    {
        // nine « Saint-Ni… » names would push « ni » (✓, in today's bar) out of it
        var many = Enumerable.Range(0, 9).Select(i => N("Saint-Ni" + (char)('a' + i) + "ville", 0.9 - i * 0.01)).Concat(Pack);
        var keep = _e.Names;
        try
        {
            _e.Names = Lex(many);
            _e.HyphenMax = int.MaxValue;   // unbounded: the compounds alone would fill the bar
            var s = After("saint", "ni", "je vais à saint");
            Assert.Equal(8, s.Length);
            Assert.Equal("ni", s[7]);   // the Present rule's last place
            Assert.True(s.Take(7).All(w => w.StartsWith("Saint-Ni", StringComparison.Ordinal)));
            _e.HyphenMax = 5;              // the default: 5 compounds, then today's list (« ni » among it)
            s = After("saint", "ni", "je vais à saint");
            Assert.Equal(5, s.Count(w => w.StartsWith("Saint-", StringComparison.Ordinal)));
            Assert.Contains("ni", s);
        }
        finally { _e.Names = keep; _e.HyphenMax = 5; }
    }

    // ------------------------------------------------------------------ the compound name slot

    [Fact]
    public void A_word_typed_without_a_names_capital_gets_the_best_hyphenated_name_in_the_last_place()
    {
        var s = _e.Predict("saint", new[] { "je", "vais", "à" }, 8);
        Assert.Equal("saint", s[0].Word);
        var slot = s.Single(x => x.Word.StartsWith("Saint-", StringComparison.Ordinal));
        Assert.Equal("Saint-Marin", slot.Word);   // the most popular predicted-tier name
        Assert.True(slot.Name);
        Assert.Equal(Math.Min(7, s.Count - 1), Array.IndexOf(W(s), "Saint-Marin"));
        // at a sentence start too (the capital says nothing there)
        Assert.Contains("Saint-Marin", W(_e.Predict("Saint", null, 8, sentenceStart: true)));
        // KSS (k = 1) never changes; with a capital inside a sentence the names pass offers them itself
        Assert.Equal("saint", _e.Predict("saint", new[] { "je", "vais", "à" }, 1)[0].Word);
        // switch off: no slot
        _e.CompoundNameSlot = false;
        try { Assert.DoesNotContain(_e.Predict("saint", new[] { "je", "vais", "à" }, 8), x => x.Word.Contains('-')); }
        finally { _e.CompoundNameSlot = true; }
    }

    [Fact]
    public void The_slot_needs_4_letters_letters_only_and_no_such_name_already_in_the_bar()
    {
        Assert.DoesNotContain(_e.Predict("sai", new[] { "à" }, 8), x => x.Word.Contains('-'));
        Assert.DoesNotContain(_e.Predict("jean", new[] { "à" }, 7), x => x.Word.Contains('-'));   // not a full bar
        Assert.Contains("Jean-Pierre", W(_e.Predict("jean", new[] { "à" }, 8)));
        // a capitalised personal word starting with « typed- » comes first
        var keep = _e.User;
        try
        {
            _e.User = UserLexicon.Build(new[] { new UserWord("Saint-Ex") }, null);
            Assert.Contains("Saint-Ex", W(_e.Predict("saint", new[] { "je", "vais", "à" }, 8)));
        }
        finally { _e.User = keep; }
    }

    // ------------------------------------------------------------------ the shipped lexicon

    private static readonly object Gate = new();
    private static readonly Lazy<PredictionEngine> Real = new(() =>
    {
        var e = PredictionEngine.Load(Path.Combine(RealGrammarFactAttribute.Out, "prediction.sqlite"));
        e.Grammar = GrammarLexicon.Load(Path.Combine(RealGrammarFactAttribute.Out, "grammar.sqlite"), e);
        return e;
    });

    [RealGrammarFact]
    public void The_shipped_lexicon_gives_its_compounds_and_keeps_the_inversions()
    {
        lock (Gate)
        {
            var e = Real.Value;
            string[] A(string head, string typed, params string[] ctx) => W(e.PredictAfterHyphen(head, typed, ctx, 11));
            // the compound's ending is today's first word: that word stays (it writes the compound after the head)
            Assert.Equal("être", A("peut", "ê", "il", "est", "peut")[0]);
            Assert.Equal("bas", A("là", "b", "je", "vais", "là")[0]);
            Assert.Equal("cinq", A("vingt", "c", "il", "a", "vingt")[0]);
            Assert.Equal("là", A("celui", "l", "je", "veux", "celui")[0]);
            // a lexicon compound never goes ahead of today's first word (review: « va-tout », « est-ce-que »)
            Assert.Equal("t", Elision.Apply(e.PredictAfterHyphen("va", "t", new[] { "va" }, 11), afterHyphen: true)[0].Word);
            Assert.Equal("ce", A("est", "c", "qu", "est")[0]);
            Assert.DoesNotContain("est-ce-que", A("est", "", "qu", "est"));
            // the inversions: no compound pushes the pronoun down
            Assert.Equal("moi", A("dis", "m", "dis")[0]);
            Assert.Equal("il", A("a-t", "i", "a", "t")[0]);
            Assert.Contains("t", Elision.Apply(e.PredictAfterHyphen("va", "t", new[] { "va" }, 11), afterHyphen: true).Take(3).Select(x => x.Word));
        }
    }
}
