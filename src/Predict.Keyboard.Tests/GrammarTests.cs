using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 11 (10D-1): the agreement rules (<see cref="AgreementContext"/>, pure, on hand-made analyses), the score
/// term's signs, and the homophone families.</summary>
public class AgreementContextTests
{
    private const uint N = MorphBits.Noun, A = MorphBits.Adjective, V = MorphBits.FiniteVerb, Q = MorphBits.PastParticiple,
                       Y = MorphBits.Infinitive, Prep = MorphBits.Preposition, Conj = MorphBits.Conjunction, Adv = MorphBits.Adverb,
                       Det = MorphBits.Determiner, m = MorphBits.Masc, f = MorphBits.Fem, e = MorphBits.GenderMask,
                       s = MorphBits.Sing, p = MorphBits.Plur, i = MorphBits.NumberMask, I = MorphBits.Indicative;

    // a few analyses as French.lex gives them (grammar.sqlite, 10D-0)
    private static readonly Dictionary<string, uint[]> Bits = new(StringComparer.Ordinal)
    {
        ["mange"] = new[] { V | MorphBits.P1s | MorphBits.P2s | MorphBits.P3s | I | MorphBits.Subjunctive | MorphBits.Imperative },
        ["mangent"] = new[] { V | MorphBits.P3p | I | MorphBits.Subjunctive },
        ["manger"] = new[] { N | m | s, Y },
        ["mangé"] = new[] { A | m | s, Q | m | s },
        ["mangez"] = new[] { V | MorphBits.P2p | I | MorphBits.Imperative },
        ["dit"] = new[] { A | m | s, V | MorphBits.P3s | I, Q | m | s, N | m | s },
        ["regarde"] = new[] { V | MorphBits.P1s | MorphBits.P3s | I | MorphBits.Imperative },
        ["t"] = new[] { N | m | i },
        ["est"] = new[] { N | m | s, V | MorphBits.P3s | I | MorphBits.Etre },
        ["sont"] = new[] { V | MorphBits.P3p | I | MorphBits.Etre },
        ["a"] = new[] { N | m | i, V | MorphBits.P3s | I | MorphBits.Avoir },
        ["à"] = new[] { Prep },
        ["au"] = new[] { Det | m | s, Prep },
        ["et"] = new[] { Conj },
        ["été"] = new[] { N | m | s, Q | e | i | MorphBits.Etre },
        ["petites"] = new[] { N | f | p, A | f | p },
        ["petit"] = new[] { N | m | s, A | m | s },
        ["maisons"] = new[] { N | f | p },
        ["maison"] = new[] { N | f | s },
        ["mais"] = new[] { N | m | p, Conj },
        ["enfants"] = new[] { N | e | p },
        ["chat"] = new[] { N | m | s },
        ["frère"] = new[] { N | m | s },
        ["porte"] = new[] { N | f | s, V | MorphBits.P1s | MorphBits.P3s | I },
        ["blanches"] = new[] { N | f | p, A | f | p },
        ["blanc"] = new[] { N | m | s, A | m | s },
        ["allée"] = new[] { Q | f | s | MorphBits.AuxEtre | MorphBits.Modal, N | f | s },
        ["allé"] = new[] { Q | m | s | MorphBits.AuxEtre | MorphBits.Modal },
        ["aller"] = new[] { N | m | s, Y | MorphBits.AuxEtre | MorphBits.Modal },
        ["vais"] = new[] { V | MorphBits.P1s | I | MorphBits.AuxEtre | MorphBits.Modal },
        ["faut"] = new[] { V | MorphBits.P3s | I | MorphBits.Modal },
        ["pour"] = new[] { Prep },
        ["avec"] = new[] { Prep },
        ["par"] = new[] { Prep },
        ["puis"] = new[] { V | MorphBits.P1s | I | MorphBits.Modal, Conj, Adv },
        ["voit"] = new[] { V | MorphBits.P3s | I },
        ["voient"] = new[] { V | MorphBits.P3p | I | MorphBits.Subjunctive },
        ["une"] = new[] { N | f | s, Det | f | s },
        ["maître"] = new[] { N | m | s },
        ["vélo"] = new[] { N | m | s },
        ["chien"] = new[] { N | m | s },
        ["neuf"] = new[] { N | m | s, A | m | s, Det | e | p },
        ["si"] = new[] { N | m | i, Conj, Adv },
        ["qui"] = new[] { MorphBits.OtherPronoun },
        ["cantine"] = new[] { N | f | s, V | MorphBits.P1s | MorphBits.P2s | MorphBits.P3s | I | MorphBits.Subjunctive | MorphBits.Imperative },
    };

    private static ReadOnlySpan<uint> Lookup(string w) => Bits.TryGetValue(w, out var b) ? b : ReadOnlySpan<uint>.Empty;

    private static Expect Of(string context, bool par = false, bool wide = false)
    {
        var t = context.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? At(int k) => t.Length - k >= 0 && t.Length - k < t.Length ? t[t.Length - k] : null;
        return AgreementContext.From(At(3), At(2), At(1), Lookup, par, wide);
    }

    [Theory]
    // S — subject chain
    [InlineData("ils", "Subject; verb 3p; disfavour")]
    [InlineData("ils ne", "Subject; verb 3p; disfavour")]
    [InlineData("il nous", "Subject; verb 3s; disfavour")]          // precision: « il nous voit »
    [InlineData("je les", "Subject; verb 1s; disfavour")]           // precision: « je les mange »; N does not apply
    [InlineData("tu les", "Subject; verb 2s; disfavour")]
    [InlineData("je ne le", "Subject; verb 1s; disfavour")]
    [InlineData("elle se", "Subject; verb 3s; disfavour")]
    [InlineData("vous", "Subject; verb 2p; disfavour")]
    [InlineData("nous nous", "Subject; verb 1p; disfavour")]
    [InlineData("qu il", "Subject; verb 3s; disfavour")]
    [InlineData("puis il", "Subject; verb 3s; disfavour")]          // « puis » is a conjunction too
    [InlineData("c", "Subject; verb 3s; disfavour")]
    [InlineData("regarde c", "Subject; verb 3s; disfavour")]        // « regarde, c'est »: an elided c' is never inverted
    [InlineData("ce", "Subject, Determiner; nominal ms; verb 3s|3p; disfavour")]
    [InlineData("mange t il", "none")]                              // precision: inversion « mange-t-il une »
    [InlineData("dit il", "none")]                                  // precision: « dit-il que »
    [InlineData("la cantine ils", "Subject; verb 3p; disfavour")]   // « cantine » is a verb too, but not of « ils »
    [InlineData("pour nous", "none")]                               // a stressed pronoun after a preposition
    [InlineData("puis nous", "Subject; verb 1p; disfavour")]        // « puis » is a conjunction too
    [InlineData("regarde nous", "none")]                            // « nous » / « vous » after a verb: its object (« va nous voir »)
    [InlineData("le maître nous", "none")]                          // review 10D-1: after a noun, « qui » or a name: an object too
    [InlineData("livre qui nous", "none")]
    [InlineData("léa vous", "none")]                                // an unknown word (a name)
    [InlineData("si nous", "Subject; verb 1p; disfavour")]          // « si » is a noun too, but a conjunction first
    [InlineData("le maître nous a", "none")]                        // no subject before « a »
    [InlineData("un vélo neuf", "none")]                            // a numeral after a noun is an adjective
    [InlineData("vélo neuf est", "none")]
    [InlineData("et le chat", "NounGroup; nominal ms (adjectives)")]   // « le chien et le chat » + son: coordinated, no person
    [InlineData("avec elle", "none")]
    // N — determiner group
    [InlineData("les", "Determiner; nominal p")]
    [InlineData("le", "Determiner; nominal ms")]
    [InlineData("regarde le", "Determiner; nominal ms")]            // precision: « le mais » keeps offering « maison »
    [InlineData("la", "Determiner; nominal fs")]
    [InlineData("l", "Determiner; nominal s")]
    [InlineData("une", "Determiner; nominal fs")]
    [InlineData("deux", "Determiner; nominal p")]
    [InlineData("les petites", "Determiner, NounGroup; nominal fp; verb 3p (noun)")]
    // NP — noun group
    [InlineData("les maisons", "NounGroup; nominal fp (adjectives); verb 3p (noun)")]
    [InlineData("les petites maisons", "NounGroup; nominal fp (adjectives); verb 3p (noun)")]
    [InlineData("le chat", "NounGroup; nominal ms (adjectives); verb 3s (noun)")]
    [InlineData("les enfants", "NounGroup; nominal p (adjectives); verb 3p (noun)")]
    [InlineData("mon frère", "NounGroup; nominal ms (adjectives); verb 3s (noun)")]
    // E — être
    [InlineData("elle est", "Etre; part fs")]
    [InlineData("ils sont", "Etre; part mp")]
    [InlineData("elle n est", "Etre; part fs")]
    [InlineData("la porte est", "Etre; part fs")]
    [InlineData("le petit est", "Etre; part ms")]                   // « est » is also a noun: E wins over NP
    [InlineData("ce sont", "Etre; part m")]
    [InlineData("il a été", "none")]                                // precision: « été » is a participle, not a finite être
    [InlineData("est", "none")]                                     // no subject
    // A — avoir
    [InlineData("il a", "Avoir; Q")]
    [InlineData("il les a", "Avoir; Q")]
    [InlineData("il y a", "Avoir; Q")]
    [InlineData("le chat a", "Avoir; Q")]
    // I — infinitive
    [InlineData("pour", "Infinitive; Y")]
    [InlineData("de", "Infinitive; Y")]
    [InlineData("d", "Infinitive; Y")]
    [InlineData("je vais", "Infinitive; Y")]
    [InlineData("il faut", "Infinitive; Y")]
    [InlineData("par", "none")]                                     // « par » only as a sweep option
    [InlineData("", "none")]
    public void Rules_Read_The_Last_Three_Tokens(string context, string expected) => Assert.Equal(expected, Of(context).ToString());

    [Fact]
    public void Par_Is_An_Option() => Assert.Equal("Infinitive; Y", Of("par", par: true).ToString());

    [Theory]
    [InlineData("ils", "mangent", 1)]
    [InlineData("ils", "mange", -1)]
    [InlineData("ils", "manger", 0)]          // a noun too: not every analysis is constrained
    [InlineData("il", "a", 1)]
    [InlineData("il", "à", -1)]               // a preposition after a subject
    [InlineData("il", "au", -1)]
    [InlineData("il", "et", -1)]
    [InlineData("il", "est", 1)]
    [InlineData("je les", "mange", 1)]        // precision: 1s, not the 3p of « les »
    [InlineData("je les", "mangent", -1)]
    [InlineData("il nous", "voit", 1)]
    [InlineData("il nous", "voient", -1)]
    [InlineData("mange t il", "une", 0)]
    [InlineData("les", "maisons", 1)]
    [InlineData("les", "maison", -1)]
    [InlineData("le", "maison", 1)]           // owner Q5: a noun is never demoted for gender
    [InlineData("le", "maisons", -1)]
    [InlineData("les petites", "maisons", 1)]
    [InlineData("les maisons", "blanches", 1)]
    [InlineData("les maisons", "blanc", 0)]   // its noun analysis is not judged after a noun
    [InlineData("les enfants", "mangent", 1)]
    [InlineData("les enfants", "mange", -1)]
    [InlineData("elle est", "allée", 1)]
    [InlineData("elle est", "allé", -1)]
    [InlineData("elle est", "aller", 0)]      // « un aller » is a noun
    [InlineData("il a", "mangé", 1)]
    [InlineData("il a", "mangez", -1)]
    [InlineData("pour", "manger", 1)]
    [InlineData("pour", "mangé", 0)]          // an adjective too
    [InlineData("pour", "allé", -1)]
    [InlineData("je vais", "manger", 1)]
    [InlineData("il a été", "mangé", 0)]
    public void Agreement_Signs(string context, string word, int sign) => Assert.Equal(sign, Of(context).Sign(Lookup(word)));

    [Theory]
    [InlineData("il", "dit", 1, 1)]           // « dit » 3s agrees: + either way
    [InlineData("je", "manger", 0, -1)]       // an infinitive (and a noun) right after a bare subject
    // owner 2026-11-16: a clitic chain after the subject pronoun keeps the subject in force (« je les mange »)
    [InlineData("je vous", "manger", 0, -1)]
    [InlineData("je les", "manger", 0, -1)]
    [InlineData("je ne les", "manger", 0, 0)]      // the chain fills the window: the word before « je » is unknown
    [InlineData("tu nous les", "manger", 0, 0)]    // « veux tu nous les donner »
    [InlineData("et je les", "manger", 0, -1)]
    [InlineData("il nous", "manger", 0, -1)]
    [InlineData("nous les", "manger", 0, -1)]
    [InlineData("nous", "manger", 0, 0)]      // « nous » alone may be an object of a verb outside the window
    [InlineData("puis je vous", "manger", 0, 0)]   // « puis-je vous raconter »: after a finite verb the clitic is the infinitive's
    [InlineData("je vais les", "manger", 0, 0)]    // after a verb: no subject chain, the infinitive is right
    [InlineData("il faut les", "manger", 0, 0)]
    [InlineData("je vous", "mais", 0, -1)]    // but no noun
    [InlineData("je", "dit", 0, -1)]          // noun / adjective / participle too: neutral in the plan's lists, − with wide
    [InlineData("je", "regarde", 1, 1)]
    [InlineData("je", "mais", 0, -1)]         // N + Conj
    [InlineData("ce", "chat", 1, 1)]          // « ce chat »: a determiner too, nouns stay judged by number
    [InlineData("les", "mange", 0, -1)]       // a finite verb right after a determiner
    [InlineData("les", "porte", 0, -1)]       // N f s (wrong number) + V: the verb was neutral
    [InlineData("les enfants", "mange", -1, -1)]   // NP: the verb is judged by person, not disfavoured
    public void Wide_Disfavour(string context, string word, int plan, int wide)
    {
        Assert.Equal(plan, Of(context).Sign(Lookup(word)));
        Assert.Equal(wide, Of(context, wide: true).Sign(Lookup(word)));
    }

    [Fact]
    public void Term_Uses_The_Family_Weights()
    {
        var ils = Of("ils");
        Assert.Equal(2.0, ils.Term(Lookup("mangent"), 1.5, 2.0, 0.5));
        Assert.Equal(-2.0, ils.Term(Lookup("mange"), 1.5, 2.0, 0.5));
        Assert.Equal(1.5, Of("les").Term(Lookup("maisons"), 1.5, 2.0, 0.5));
        Assert.Equal(1.0, Of("les enfants").Term(Lookup("mangent"), 1.5, 2.0, 0.5));   // WNounSubj × WVerbal
        Assert.Equal(0.0, Of("les").Term(Lookup("maisons"), 0, 2.0, 0.5));
        Assert.Equal(0.0, Expect.None.Term(Lookup("maisons"), 1, 1, 1));
        Assert.Equal(0.0, Of("les").Term(ReadOnlySpan<uint>.Empty, 1, 1, 1));               // unknown word
        // AgreeBonus: the share of W an agreeing form gets (0 = only the forms that disagree lose)
        Assert.Equal(0.0, ils.Term(Lookup("mangent"), 1.5, 2.0, 0.5, bonus: 0));
        Assert.Equal(1.0, ils.Term(Lookup("mangent"), 1.5, 2.0, 0.5, bonus: 0.5));
        Assert.Equal(-2.0, ils.Term(Lookup("mange"), 1.5, 2.0, 0.5, bonus: 0));
        Assert.Equal(2.0, ils.AgreeWeight(Lookup("mangent"), 1.5, 2.0, 0.5));
        Assert.Equal(0.0, ils.AgreeWeight(Lookup("mange"), 1.5, 2.0, 0.5));
    }

    [Fact]
    public void Homophone_Families()
    {
        Assert.Equal(new[] { "est", "es", "et" }, HomophoneFamilies.Of("et"));
        Assert.Equal(new[] { "sont", "son" }, HomophoneFamilies.Of("son"));
        Assert.Null(HomophoneFamilies.Of("maison"));
        Assert.True(HomophoneFamilies.IsMember("à"));
        Assert.False(HomophoneFamilies.RuleMayChoose("ces"));
        Assert.False(HomophoneFamilies.RuleMayChoose("ses"));
        Assert.True(HomophoneFamilies.RuleMayChoose("sait"));
    }
}

/// <summary>Plan 11 (10D-1): the agreement term in the engine on the real built databases (skipped with a message when
/// <c>Data.Build/out</c> has no grammar.sqlite / prediction.sqlite).</summary>
public class GrammarPredictionTests
{
    private static readonly string Out = RealGrammarFactAttribute.Out;
    private static readonly object Gate = new();
    private static readonly Lazy<(PredictionEngine E, GrammarLexicon G)> Data = new(() =>
    {
        var e = PredictionEngine.Load(Path.Combine(Out, "prediction.sqlite"));
        return (e, GrammarLexicon.Load(Path.Combine(Out, "grammar.sqlite"), e));
    });

    /// <summary>The engine with the grammar and its default weights (the engine is not thread-safe: one test at a time).</summary>
    private static T With<T>(Func<PredictionEngine, T> f, bool grammar = true)
    {
        lock (Gate)
        {
            var (e, g) = Data.Value;
            var keep = (e.WNominal, e.WVerbal, e.WNounSubj, e.InjectGap, e.GrammarOn);
            e.Grammar = grammar ? g : GrammarLexicon.Empty;
            try { return f(e); }
            finally { (e.WNominal, e.WVerbal, e.WNounSubj, e.InjectGap, e.GrammarOn) = keep; e.Grammar = GrammarLexicon.Empty; }
        }
    }

    private static IReadOnlyList<Suggestion> P(PredictionEngine e, string context, string typed) =>
        e.Predict(typed, context.Split(' ', StringSplitOptions.RemoveEmptyEntries), 8);

    private static T WithOff<T>(PredictionEngine e, Func<T> f) { e.GrammarOn = false; try { return f(); } finally { e.GrammarOn = true; } }

    [RealGrammarFact]
    public void Next_Word_Never_Offers_An_Accent_Variant_Or_An_Unlisted_Form()
    {
        // review 10D-1 (grammar or not): « ést », « etait » are corpus spellings of « est », « était »
        With(e =>
        {
            foreach (var on in new[] { true, false })
            {
                e.GrammarOn = on;
                var n = e.PredictNext(new[] { "c" }, 8).Select(x => x.Word).ToArray();
                Assert.DoesNotContain("ést", n);
                Assert.DoesNotContain("etait", n);
                Assert.Contains("est", n);
            }
            return 0;
        });
    }

    private static int Rank(IReadOnlyList<Suggestion> s, string w) { for (int i = 0; i < s.Count; i++) if (s[i].Word == w) return i + 1; return 0; }

    [RealGrammarFact]
    public void The_Agreeing_Form_Comes_First()
    {
        With(e =>
        {
            var les = P(e, "les", "mais");
            Assert.True(Rank(les, "maisons") is > 0 && (Rank(les, "maison") == 0 || Rank(les, "maisons") < Rank(les, "maison")), string.Join(",", les.Select(x => x.Word)));
            Assert.Equal("mangent", P(e, "ils", "mang")[0].Word);
            Assert.Equal("allée", P(e, "elle est", "all")[0].Word);
            var ilA = P(e, "il a", "mang");
            Assert.True(Rank(ilA, "mangé") < Rank(ilA, "manger"));
            Assert.Equal("manger", P(e, "pour", "mang")[0].Word);
            Assert.Equal("mange", P(e, "je les", "mang")[0].Word);           // precision
            Assert.Equal("une", P(e, "mange t il", "une")[0].Word);           // precision: no rule
            Assert.True(Rank(P(e, "le", "mais"), "maison") > 0);               // owner Q5
            return 0;
        });
    }

    /// <summary>Owner 2026-11-16 (written-corpus database): object clitics between the subject and the verb keep the subject
    /// in force; after a verb (« je vais les », « il faut les ») the infinitive stays first.</summary>
    [RealGrammarFact]
    public void A_Clitic_Chain_Keeps_The_Subject()
    {
        With(e =>
        {
            Assert.Equal("mange", P(e, "je les", "mang")[0].Word);
            Assert.Equal("mange", P(e, "je ne les", "mang")[0].Word);
            Assert.Equal("prends", P(e, "tu le", "prend")[0].Word);
            // « il nous regard »: a 3s form first (the corpus prefers « regardait » to « regarde »), never the infinitive
            var nous = P(e, "il nous", "regard");
            Assert.True(Rank(nous, "regarde") is > 0 and <= 2 && Rank(nous, "regarde") < Rank(nous, "regarder"), string.Join(",", nous.Select(x => x.Word)));
            // review: a clitic chain that fills the window may follow an inverted verb (« veux tu nous les donner »): neutral
            var veux = P(e, "veux tu nous les", "donn");
            Assert.True(Rank(veux, "donner") is > 0 and <= 3, string.Join(",", veux.Select(x => x.Word)));
            Assert.Equal("manger", P(e, "je vais les", "mang")[0].Word);
            Assert.Equal("manger", P(e, "il faut les", "mang")[0].Word);
            return 0;
        });
    }

    [RealGrammarFact]
    public void The_Agreeing_Form_Is_Added_When_The_Letters_Cannot_Reach_It()
    {
        With(e =>
        {
            var ilA = P(e, "il a", "manger");
            int r = Rank(ilA, "mangé");
            Assert.True(r is > 0 and <= 3, string.Join(",", ilA.Select(x => x.Word)));
            Assert.Equal(MatchKind.Phonetic, ilA[r - 1].Kind);                 // Q3: pronounced as « manger »
            var les = P(e, "les", "journal");
            int j = Rank(les, "journaux");
            Assert.True(j > 0, string.Join(",", les.Select(x => x.Word)));
            Assert.Equal(MatchKind.Edit, les[j - 1].Kind);                     // Q3: another pronunciation
            Assert.True(Rank(P(e, "il", "et"), "est") > 0);
            Assert.True(Rank(P(e, "ils", "son"), "sont") > 0);
            Assert.True(Rank(P(e, "il", "à"), "a") > 0);
            Assert.Equal(0, Rank(P(e, "il", "ses"), "ces"));                   // never chosen by a rule
            // review 10D-1: a sibling must agree as a form of the lending lemma and part of speech (no « étais » / « êtres »)
            foreach (var (c, t) in new[] { ("mon vélo neuf", "est"), ("deux", "est"), ("les", "est"), ("mes", "été") })
            {
                var rr = P(e, c, t);
                Assert.True(Rank(rr, "étais") is 0 || Rank(rr, "étais") > Rank(rr, t), c + " " + t + ": " + string.Join(",", rr.Select(x => x.Word)));
                Assert.True(Rank(rr, "êtres") is 0 || Rank(rr, "êtres") > Rank(rr, t), c + " " + t + ": " + string.Join(",", rr.Select(x => x.Word)));
            }
            Assert.Equal(e.PredictNext(new[] { "maître", "nous" }, 8).Select(x => x.Word), WithOff(e, () => e.PredictNext(new[] { "maître", "nous" }, 8)).Select(x => x.Word));
            var tu = P(e, "tu", "fait");                                       // a sibling found by its sound, lifted
            Assert.True(Rank(tu, "fais") is > 0 && (Rank(tu, "fait") == 0 || Rank(tu, "fais") < Rank(tu, "fait")), string.Join(",", tu.Select(x => x.Word)));
            return 0;
        });
    }

    [RealGrammarFact]
    public void Nothing_Changes_Without_A_Rule()
    {
        // « il fait » + … : no rule applies to « fait » after « il », so the bar is that of grammar off
        string Run(PredictionEngine e) => string.Join("|", new[] { "manger", "m", "journal", "et" }.Select(t => string.Join(",", P(e, "il fait", t).Select(x => $"{x.Word}:{x.Score:R}:{x.Kind}"))));
        var on = With(Run);
        var off = With(Run, grammar: false);
        Assert.Equal(off, on);
    }

    /// <summary>500 queries over the grammar contexts: the same words, scores and kinds with grammar off, with every
    /// weight at 0, and with GrammarOn false.</summary>
    [RealGrammarFact]
    public void Weights_Zero_Or_Off_Give_The_083_Results()
    {
        var ctxs = new[] { "les", "ils", "elle est", "il a", "pour", "je vais", "le chat", "il", "je les", "il fait", "les petites" };
        var words = new[] { "maisons", "mangent", "allée", "mangé", "manger", "journal", "et", "son", "a", "à", "petites", "on", "blanc" };
        var q = new List<(string C, string T)>();
        for (int k = 0; q.Count < 500; k++) { var w = words[k % words.Length]; q.Add((ctxs[k % ctxs.Length], w[..Math.Min(w.Length, 1 + k % 7)])); }
        string Run(PredictionEngine e) => string.Join("\n", q.Select(x => string.Join(",", P(e, x.C, x.T).Select(s => $"{s.Word}:{s.Score:R}:{s.Kind}"))))
                                          + "\n" + string.Join("\n", ctxs.Select(c => string.Join(",", e.PredictNext(c.Split(' '), 8).Select(s => $"{s.Word}:{s.Score:R}"))));
        var off = With(Run, grammar: false);
        Assert.Equal(off, With(e => { e.WNominal = 0; e.WVerbal = 0; e.WNounSubj = 0; e.InjectGap = 0; return Run(e); }));
        Assert.Equal(off, With(e => { e.GrammarOn = false; return Run(e); }));
        Assert.NotEqual(off, With(Run));
    }

    [RealGrammarFact]
    public void The_Next_Word_After_Les_Is_Plural()
    {
        With(e =>
        {
            var g = Data.Value.G;
            var next = e.PredictNext(new[] { "les" }, 8);
            foreach (var s in next)
            {
                var b = g.BitsOf(e.IndexOf(s.Word)).ToArray();
                Assert.False(b.Length > 0 && b.All(x => MorphBits.Pos(x) == MorphBits.Noun && (x & MorphBits.NumberMask) == MorphBits.Sing), s.Word);
            }
            return 0;
        });
    }

    [RealGrammarFact]
    public void A_Grammar_Swap_During_Queries_Gives_One_Of_The_Two_Results()
    {
        lock (Gate)
        {
            var (e, g) = Data.Value;
            string Q() => string.Join(",", e.Predict("mang", new[] { "ils" }, 8).Select(x => x.Word));
            e.Grammar = GrammarLexicon.Empty;
            var off = Q();
            e.Grammar = g;
            var on = Q();
            Assert.NotEqual(off, on);
            using var stop = new ManualResetEventSlim();
            var swapper = new Thread(() => { bool b = false; while (!stop.IsSet) { e.Grammar = (b = !b) ? g : GrammarLexicon.Empty; Thread.Yield(); } });
            swapper.Start();
            try
            {
                for (int k = 0; k < 300; k++) { var r = Q(); Assert.True(r == on || r == off, r); }
            }
            finally { stop.Set(); swapper.Join(); e.Grammar = GrammarLexicon.Empty; }
        }
    }

    [RealGrammarFact]
    public void Ten_Thousand_Queries_Allocate_At_Most_Ten_Percent_More()
    {
        var ctxs = new[] { new[] { "les" }, new[] { "ils" }, new[] { "elle", "est" }, new[] { "il", "a" }, new[] { "pour" }, new[] { "il", "fait" } };
        var typed = new[] { "ma", "man", "mang", "all", "jo", "pe", "bla", "et", "son" };
        long Alloc(PredictionEngine e)
        {
            for (int k = 0; k < 200; k++) e.Predict(typed[k % typed.Length], ctxs[k % ctxs.Length], 8);   // warm (n-gram caches)
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int k = 0; k < 10_000; k++) e.Predict(typed[k % typed.Length], ctxs[k % ctxs.Length], 8);
            return GC.GetAllocatedBytesForCurrentThread() - a0;
        }
        long off = With(Alloc, grammar: false), on = With(Alloc);
        Assert.True(on <= off * 1.10, $"grammar on {on / 1048576.0:0.0} MB vs off {off / 1048576.0:0.0} MB");
    }
}
