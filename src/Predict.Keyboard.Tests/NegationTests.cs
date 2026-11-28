using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.11.0 (plan negation): the missing « ne » detector (<see cref="Negation"/>, pure, on a hand-made lexicon), the
/// clause the keyboard gives it (<see cref="Negation.ClauseOf"/>, <see cref="WordTracker.TryClause"/>), the group replacement
/// (tracker, document check, keys, echo) and, on the real databases, the curated examples and the D35 exclusion.</summary>
public class NegationTests
{
    private const uint V = MorphBits.FiniteVerb, N = MorphBits.Noun, A = MorphBits.Adjective, I = MorphBits.Indicative,
                       E = MorphBits.Imperative, s = MorphBits.Sing, p = MorphBits.Plur, m = MorphBits.Masc, f = MorphBits.Fem;

    /// <summary>A few analyses and ≈ lists of our own; every listed form is Known, « veu » / « pa » sound like their words.</summary>
    private sealed class Lex : INegationLexicon
    {
        public readonly Dictionary<string, uint[]> B = new(StringComparer.Ordinal)
        {
            ["veut"] = new[] { V | MorphBits.P3s | I },
            ["veux"] = new[] { V | MorphBits.P1s | MorphBits.P2s | I | E },
            ["vœu"] = new[] { N | m | s },
            ["est"] = new[] { N | m | s, V | MorphBits.P3s | I | MorphBits.Etre },
            ["ai"] = new[] { V | MorphBits.P1s | I | MorphBits.Avoir },
            ["a"] = new[] { V | MorphBits.P3s | I | MorphBits.Avoir },
            ["aime"] = new[] { V | MorphBits.P1s | MorphBits.P3s | I | E },
            ["mange"] = new[] { V | MorphBits.P1s | MorphBits.P3s | I, V | MorphBits.P2s | E },
            ["mangent"] = new[] { V | MorphBits.P3p | I },
            ["inquiétez"] = new[] { V | MorphBits.P2p | I | E },
            ["savez"] = new[] { V | MorphBits.P2p | I },
            ["cousin"] = new[] { N | m | s },
            ["enfants"] = new[] { N | m | p },
            ["pas"] = new[] { N | m | MorphBits.NumberMask, MorphBits.Adverb },
            ["fait"] = new[] { V | MorphBits.P3s | I, N | m | s },
            ["un"] = new[] { MorphBits.Determiner | m | s },
            ["mon"] = new[] { MorphBits.Determiner | m | s },
            ["les"] = new[] { MorphBits.Determiner | MorphBits.Plur },
            ["petit"] = new[] { A | m | s, N | m | s },
            ["veuxje"] = Array.Empty<uint>(),
        };
        public readonly Dictionary<string, string[]> Sounds = new(StringComparer.Ordinal)
        {
            ["veu"] = new[] { "veux", "veut", "vœu", "veut-il", "veux pas" },
            ["pa"] = new[] { "pas", "pat" },
        };
        public string? Excluded1;
        public double NeFirstBonus;   // ChainScore: > 0 favours « ne vous V », < 0 « vous ne V »

        private static readonly HashSet<string> Closed = new(StringComparer.Ordinal)
        { "il", "je", "j", "c", "ce", "on", "nous", "vous", "le", "la", "l", "lui", "ne", "n", "jamais", "rien", "plus", "que", "qu", "et", "Léa" };

        public ReadOnlySpan<uint> Bits(string lower) => B.TryGetValue(lower, out var b) ? b : ReadOnlySpan<uint>.Empty;
        public SpellingCheck Check(string word)
        {
            var w = word.ToLowerInvariant();
            if (B.ContainsKey(w) || Closed.Contains(w)) return SpellingCheck.Known;
            return Sounds.TryGetValue(w, out var c) ? new SpellingCheck(SpellingStatus.SoundsLike, c) : SpellingCheck.Unknown;
        }
        public bool IsName(string word) => word == "Léa";
        public double ChainScore(string[] t) => t[0] == "ne" || t.Length > 1 && t[1] == "ne" && t[0] != "vous" && t[0] != "nous" ? NeFirstBonus : 0;
        public bool Excluded(string text) => Excluded1 is { } x && text.Split(' ', '\'').Contains(x);
    }

    private static NegationFix? Find(string typed, Lex? lex = null, int exactFrom = 0, bool start = true)
    {
        Assert.True(Negation.ClauseOf(typed, exactFrom, start, out var toks, out bool sk, out _, out bool plus), typed);
        return Negation.Find(toks, sk, plus, lex ?? new Lex());
    }

    private static string? Fixed(string typed, Lex? lex = null)
    {
        int tail = typed.Length;
        while (tail > 0 && !SpellingIndicator.IsWordChar(typed[tail - 1])) tail--;
        return Find(typed, lex) is { } f ? typed[..(tail - f.Original.Length)] + f.Replacement + typed[tail..] : null;
    }

    [Theory]
    [InlineData("mon cousin veu pa ", "mon cousin ne veut pas ")]   // R1 ≈ verb agreeing with 3s, R0 ≈ negation word
    [InlineData("c'est pas ", "ce n'est pas ")]                       // R5 elided subject expanded, « n' » before a vowel
    [InlineData("C'est pas ", "Ce n'est pas ")]
    [InlineData("j'aime plus.\n", "je n'aime plus.\n")]               // « plus » before punctuation
    [InlineData("je l'ai pas ", "je ne l'ai pas ")]                   // R2 an object clitic
    [InlineData("on a jamais ", "on n'a jamais ")]
    [InlineData("je veu pas ", "je ne veux pas ")]                    // the ≈ candidate agreeing with « je »
    [InlineData("les enfants mangent pas ", "les enfants ne mangent pas ")]   // a noun group, 3p
    [InlineData("le petit cousin veut pas ", "le petit cousin ne veut pas ")]
    [InlineData("Léa veut pas ", "Léa ne veut pas ")]                 // a name
    [InlineData("Mange pas ", "Ne mange pas ")]                       // an imperative at a sentence start
    public void Fixes(string typed, string expected) => Assert.Equal(expected, Fixed(typed));

    [Theory]
    [InlineData("il fait un pas ")]          // R1: « un » is no verb
    [InlineData("il ne veut pas ")]          // R4: « ne » is there
    [InlineData("il n'a pas ")]
    [InlineData("j'aime plus ")]             // « plus » without punctuation: comparative
    [InlineData("il veux pas ")]             // a known verb that does not agree
    [InlineData("veut-il pas ")]             // an inversion hyphen
    [InlineData("les enfants mange pas ")]   // 3s verb after a plural noun group
    [InlineData("cousin veut pas ")]         // a noun with no determiner
    public void Does_Not_Fix(string typed) => Assert.Null(Find(typed));

    [Fact]
    public void A_Lone_Dot_Waits_For_The_Next_Separator() =>
        Assert.False(Negation.ClauseOf("mange pas.", 0, true, out _, out _, out _, out _));   // « pas.fr »: an address may follow

    [Fact]
    public void Ever_Readings_And_Unknown_Starts_Are_Left_Alone()
    {
        // « le plus … qu'il a jamais »: a superlative before the subject makes « jamais » mean « ever »
        Assert.Null(Find("le plus petit cousin qu'il a jamais "));
        // an imperative needs a known clause start: after a jump the keyboard could not read, nothing
        Assert.Null(Find("mange pas ", start: false));
        // a word rebuilt by a read is not exact (R4): « il » before the exact part
        Assert.Null(Find("il veut pas ", exactFrom: 3));
        Assert.NotNull(Find("il veut pas ", exactFrom: 0));
    }

    [Fact]
    public void Nous_Vous_At_A_Clause_Start_Need_The_Language_Model_Margin()
    {
        var lex = new Lex { NeFirstBonus = 2.0 };   // « ne vous inquiétez » clearly better
        Assert.Equal("ne vous inquiétez pas ", Fixed("vous inquiétez pas ", lex));
        lex.NeFirstBonus = -2.0;
        Assert.Equal("vous n'inquiétez pas ", Fixed("vous inquiétez pas ", lex));
        lex.NeFirstBonus = 0.5;                     // under the margin (owner Q6): nothing
        Assert.Null(Find("vous inquiétez pas ", lex));
        // a verb that cannot be an imperative: the subject reading, no model needed
        Assert.Equal("vous ne savez pas ", Fixed("vous savez pas ", new Lex { NeFirstBonus = 0.5 }));
    }

    [Fact]
    public void Glued_Candidates_With_A_Space_Or_A_Hyphen_Are_Skipped()
    {
        // « veu » ≈ veux, veut, vœu, « veut-il », « veux pas »: only single plain words can be the verb
        var lex = new Lex();
        lex.Sounds["veu"] = new[] { "veux pas", "veut-il", "veut" };
        Assert.Equal("il ne veut pas ", Fixed("il veu pas ", lex));
    }

    [Fact]
    public void D35_An_Excluded_Word_Is_Never_In_The_Group()
    {
        var lex = new Lex { Excluded1 = "veut" };
        Assert.Null(Find("mon cousin veu pa ", lex));
        Assert.Null(Find("il veut pas ", lex));
        lex.Excluded1 = "ne";
        Assert.Null(Find("il veut pas ", lex));
    }

    [Fact]
    public void The_Clause_Stops_At_A_Mark_And_Keeps_The_Tail()
    {
        Assert.True(Negation.ClauseOf("Léa, il veut pas ", 0, true, out var t, out bool start, out var tail, out bool plus));
        Assert.Equal(new[] { "il", "veut", "pas" }, t.Select(x => x.Word));
        Assert.True(start); Assert.Equal(" ", tail); Assert.False(plus);
        Assert.True(Negation.ClauseOf("j'en veux plus !", 0, true, out t, out _, out tail, out plus));
        Assert.Equal(new[] { "j", "en", "veux", "plus" }, t.Select(x => x.Word));
        Assert.Equal("'", t[0].SepAfter);
        Assert.Equal(" !", tail); Assert.True(plus);
        Assert.False(Negation.ClauseOf("il veut pas   ", 0, true, out _, out _, out _, out _));   // three separators
        // the text's first word with an unknown start is not exact (a fragment)
        Assert.True(Negation.ClauseOf("veut pas ", 0, false, out t, out start, out _, out _));
        Assert.False(t[0].Exact); Assert.True(t[1].Exact); Assert.False(start);
    }

    // ------------------------------------------------------------------ the keyboard side

    [Fact]
    public void The_Tracker_Gives_The_Clause_As_Typed_And_Replaces_The_Group()
    {
        var t = new WordTracker();
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        foreach (var c in "Léa, mon cousin veu pa ") t.Type(c);
        Assert.True(t.TryClause(out var toks, out bool start, out var tail, out _));
        Assert.Equal(new[] { "mon", "cousin", "veu", "pa" }, toks.Select(x => x.Word));
        Assert.True(start && toks.All(x => x.Exact));
        Assert.Equal(" ", tail);
        Assert.True(t.CanCorrectSpan("veu pa", " "));
        Assert.False(t.CanCorrectSpan("cousin veu", " "));
        t.CorrectSpan("veu pa", " ", "ne veut pas");
        Assert.Equal("pas", t.Finished!.Value.Word);
        Assert.Equal(new[] { "ne", "veut", "pas" }, t.Context);
        Assert.False(t.CanCorrectSpan("veu pa", " "));
        // a letter typed: no clause (the word is being typed)
        t.Type('x');
        Assert.False(t.TryClause(out _, out _, out _, out _));
        // a seeded (read) part is not exact
        t.Reset();
        t.Seed("", new[] { "il", "veut" }, 1, "", '\0', "uia");
        foreach (var c in "pas ") t.Type(c);
        Assert.True(t.TryClause(out toks, out _, out _, out _));
        Assert.Equal(new[] { false, false, true }, toks.Select(x => x.Exact));
        Assert.False(t.CanCorrectSpan("il veut pas", " "));
    }

    [Fact]
    public void Review_Plus_Before_A_Comma_And_Earlier_Negations_And_Noun_Homographs()
    {
        // owner 2026-11-27: « plus » only before . ! ? … or a line end
        Assert.False(Negation.ClauseOf("j'aime plus, ", 0, true, out _, out _, out _, out bool plus) && plus);
        Assert.Null(Find("j'aime plus, "));
        Assert.Null(Find("j'aime plus; "));
        Assert.NotNull(Find("j'aime plus !"));
        // an earlier negation: « rien » = anything
        Assert.Null(Find("il ne dit pas qu'il a rien "));
        Assert.Null(Find("il dit pas qu'il a rien "));
        // determiner + adjective + a noun / verb homograph: the group's noun (« la pauvre demeure pas »)
        var lex = new Lex();
        lex.B["demeure"] = new[] { N | f | s, V | MorphBits.P3s | I };
        lex.B["pauvre"] = new[] { A | f | s, N | f | s };
        lex.B["la"] = new[] { MorphBits.Determiner | f | s };
        Assert.Null(Find("la pauvre demeure pas ", lex));
        Assert.Equal("ne demeure pas", Find("la pauvre femme demeure pas ", WithFemme(lex))!.Replacement);
        static Lex WithFemme(Lex l) { l.B["femme"] = new[] { N | f | s }; return l; }
    }

    [Fact]
    public void The_Group_Takes_A_Sentence_Capital_Or_The_Documents()
    {
        Assert.Equal("Ce n'est pas", SoundAlikeCorrection.CasedGroup("ce n'est pas", sentenceStart: true));
        Assert.Equal("ce n'est pas", SoundAlikeCorrection.CasedGroup("ce n'est pas", sentenceStart: false));
        Assert.Equal("Ce n'est pas", SoundAlikeCorrection.CasedGroup("ce n'est pas", false, "C'est pas"));   // the app capitalised
        Assert.Equal("ne veut pas", SoundAlikeCorrection.CasedGroup("ne veut pas", false, "veu pa"));
        var t = new WordTracker();
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        foreach (var c in "c'est pas ") t.Type(c);
        Assert.True(t.SpanAtSentenceStart("c'est pas", " "));
        t.Reset();
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        foreach (var c in "Bon. c'est pas ") t.Type(c);
        Assert.True(t.SpanAtSentenceStart("c'est pas", " "));
        t.Reset();
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        foreach (var c in "mon cousin veu pa ") t.Type(c);
        Assert.False(t.SpanAtSentenceStart("veu pa", " "));
        t.Reset();
        foreach (var c in "c'est pas ") t.Type(c);   // after a jump the keyboard could not read: unknown
        Assert.False(t.SpanAtSentenceStart("c'est pas", " "));
    }

    [Fact]
    public void The_Marker_Is_Hidden_Where_The_Text_Cannot_Be_Read()
    {
        // owner 2026-11-27, re-review: « readable » from any Ok read, « unreadable » only from the group's own read; a window
        // never read follows its host; keyed by window and process
        var w = new ReadableWindows();
        IntPtr word = (IntPtr)1, lo = (IntPtr)2;
        const uint wordPid = 100, loPid = 200;
        Assert.True(w.MayReplace(word, wordPid, unreadableHost: false));
        // a Word-like window: one Unavailable focus read (caret after « 2024 », Word busy…) does not hide the marker
        w.Note(word, wordPid, ReadStatus.Unavailable, groupRead: false);
        Assert.True(w.MayReplace(word, wordPid, false));
        Assert.NotNull(Find("il veut pas "));                      // so typing « il veut pas␣ » there still gets it
        // only the group's own read coming back Unavailable hides it
        w.Note(word, wordPid, ReadStatus.Unavailable, groupRead: true);
        Assert.False(w.MayReplace(word, wordPid, false));
        w.Note(word, wordPid, ReadStatus.Timeout, groupRead: true);   // says nothing
        w.Note(word, wordPid, ReadStatus.Stale, groupRead: false);
        Assert.False(w.MayReplace(word, wordPid, false));
        w.Note(word, wordPid, ReadStatus.Ok, groupRead: false);       // any Ok read: shown again
        Assert.True(w.MayReplace(word, wordPid, false));
        // LibreOffice: hidden before any read, shown once a read of the window is Ok
        Assert.False(w.MayReplace(lo, loPid, unreadableHost: true));
        w.Note(lo, loPid, ReadStatus.Ok, groupRead: false);
        Assert.True(w.MayReplace(lo, loPid, true));
        // a recycled handle in another process: never read
        w.Note(word, wordPid, ReadStatus.Unavailable, groupRead: true);
        Assert.True(w.MayReplace(word, 999, unreadableHost: false));
        Assert.False(w.MayReplace(lo, 999, unreadableHost: true));
    }

    [Fact]
    public void A_Multi_Word_Tile_Inserted_Before_Is_Read_Word_By_Word()
    {
        // glued words follow-up: « il y a » inserted as one string is kept verbatim; the clause splits it into words
        var t = new WordTracker();
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        t.Accept("il y a", spaced: true);
        foreach (var c in "personne ") t.Type(c);
        Assert.True(t.TryClause(out var toks, out bool start, out var tail, out bool plus));
        Assert.Equal(new[] { "il", "y", "a", "personne" }, toks.Select(x => x.Word));
        var lex = new Lex();
        lex.B["personne"] = new[] { N | f | s };
        Assert.Equal("il n'y a personne", Negation.Find(toks, start, plus, lex)!.Replacement);
    }

    [Fact]
    public void The_Tracker_Clause_Matches_The_Pure_One()
    {
        foreach (var text in new[] { "c'est pas ", "il fait un pas ", "je l'ai pas ", "Bon. vous savez rien !", "j'aime plus.\n" })
        {
            var t = new WordTracker();
            t.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
            foreach (var c in text) t.Type(c);
            bool a = t.TryClause(out var ta, out bool sa, out var la, out bool pa);
            bool b = Negation.ClauseOf(text, 0, true, out var tb, out bool sb, out var lb, out bool pb);
            Assert.Equal(b, a);
            Assert.Equal(tb, ta);
            Assert.Equal((sb, lb, pb), (sa, la, pa));
        }
    }

    [Fact]
    public void The_Document_Must_End_With_The_Group_And_Its_Tail()
    {
        Assert.True(SoundAlikeCorrection.DocumentEndsWith("Léa, c'est pas ", "c'est pas", " ", out var d) && d == "c'est pas");
        Assert.True(SoundAlikeCorrection.DocumentEndsWith("C’est pas ", "C'est pas", " ", out d) && d == "C’est pas");   // curled by the app
        Assert.Equal('’', SoundAlikeCorrection.ApostropheOf(d, '\''));
        Assert.True(SoundAlikeCorrection.DocumentEndsWith("j'aime plus.\r\n", "j'aime plus", ".\n", out _));
        Assert.True(SoundAlikeCorrection.DocumentEndsWith("il en a plus !", "il en a plus", " !", out _));
        Assert.False(SoundAlikeCorrection.DocumentEndsWith("il en a plus !", "il en a plus", " !", out _));   // the app changed the space
        Assert.False(SoundAlikeCorrection.DocumentEndsWith("xc'est pas ", "c'est pas", " ", out _));            // a letter glued before
        Assert.False(SoundAlikeCorrection.DocumentEndsWith("c'est  pas ", "c'est pas", " ", out _));
    }

    [Theory]
    [InlineData("Ok", true, "Replace")]
    [InlineData("Ok", false, "Nothing")]          // AutoCorrect changed a word: never insert a group at the caret
    [InlineData("Unavailable", false, "Nothing")] // LibreOffice, a canvas: never from the keys alone
    [InlineData("Stale", true, "Nothing")]
    [InlineData("Password", false, "Nothing")]
    [InlineData("Timeout", false, "Nothing")]
    [InlineData("Selection", false, "Nothing")]
    public void The_Group_Read_Decides(string st, bool agrees, string expected) =>
        Assert.Equal(Enum.Parse<SoundAlikeCorrection.Action>(expected), SoundAlikeCorrection.DecideGroup(Enum.Parse<ReadStatus>(st), agrees));

    [Fact]
    public void The_Group_Keys()
    {
        var mk = (UIntPtr)7;
        var k = SoundAlikeCorrection.BuildGroup("veu pa", " ", "ne veut pas", mk);
        Assert.Equal((7 + 12) * 2, k.Length);                    // 7 Backspaces (group + space), « ne veut pas␣ »
        Assert.Equal(Inserter.VK_BACK, k[0].u.ki.wVk);
        var line = SoundAlikeCorrection.BuildGroup("j'aime plus", ".\n", "je n'aime plus", mk);
        Assert.Equal(Inserter.VK_LEFT, line[0].u.ki.wVk);        // the line break is never deleted nor retyped
        Assert.Equal(Inserter.VK_RIGHT, line[^2].u.ki.wVk);
        Assert.Equal(2 + 12 * 2 + 15 * 2 + 2, line.Length);      // Left, 12 Backspaces (group + « . »), « je n'aime plus. », Right
    }

    [Fact]
    public void The_Echo_Says_The_Corrected_Group_And_Sentence()
    {
        var e = new SpeechEcho { SpeakWords = true, SpeakSentences = true };
        foreach (var c in "mon cousin veu pa ") e.Type(c);
        var ev = e.ReplacedGroup("veu pa ".Length, "ne veut pas", " ", spoken: true);
        Assert.Null(ev);                                          // the group was said as inserted: no word echo for « pas »
        Assert.Equal("pas", e.LastInserted);
        var end = e.Type('.');
        Assert.Equal(EchoKind.Sentence, end!.Kind);
        Assert.Equal("mon cousin ne veut pas", end.Text);
    }

    [Fact]
    public void With_The_Inserted_Word_Not_Read_The_Word_Echo_Says_The_Last_Word()
    {
        // documented behaviour: « Lire le mot inséré » off → the group is not said as a whole; the word echo (on) says its last
        // word at the tail, as after a typed word, and the sentence echo later says the corrected sentence
        var e = new SpeechEcho { SpeakWords = true, SpeakSentences = true };
        foreach (var c in "mon cousin veu pa ") e.Type(c);
        var ev = e.ReplacedGroup("veu pa ".Length, "ne veut pas", " ", spoken: false);
        Assert.Equal(EchoKind.Word, ev!.Kind);
        Assert.Equal("pas", ev.Text);
        Assert.Equal("mon cousin ne veut pas", e.Type('.')!.Text);
        // the words echo off: nothing at the tail
        var q = new SpeechEcho { SpeakWords = false, SpeakSentences = true };
        foreach (var c in "il veut pas ") q.Type(c);
        Assert.Null(q.ReplacedGroup("il veut pas ".Length, "il ne veut pas", " ", spoken: false));
    }

    // ------------------------------------------------------------------ the real databases

    private static readonly object Gate = new();
    private static readonly Lazy<PredictionEngine> Real = new(() =>
    {
        var e = PredictionEngine.Load(Path.Combine(RealGrammarFactAttribute.Out, "prediction.sqlite"));
        e.Grammar = GrammarLexicon.Load(Path.Combine(RealGrammarFactAttribute.Out, "grammar.sqlite"), e);
        return e;
    });

    private static string? RealFixed(PredictionEngine e, string typed)
    {
        if (!Negation.ClauseOf(typed, 0, true, out var toks, out bool sk, out _, out bool plus)) return null;
        int tail = typed.Length;
        while (tail > 0 && !SpellingIndicator.IsWordChar(typed[tail - 1])) tail--;
        return e.MissingNe(toks, sk, plus) is { } f ? typed[..(tail - f.Original.Length)] + f.Replacement + typed[tail..] : null;
    }

    [RealGrammarFact]
    public void Real_Curated_Examples_And_Switch()
    {
        lock (Gate)
        {
            var e = Real.Value;
            Assert.Equal("mon cousin ne veut pas ", RealFixed(e, "mon cousin veu pa "));
            Assert.Equal("ce n'est pas ", RealFixed(e, "c'est pas "));
            Assert.Equal("je n'aime plus.\n", RealFixed(e, "j'aime plus.\n"));
            Assert.Equal("je ne l'ai pas ", RealFixed(e, "je l'ai pas "));
            Assert.Equal("on n'a jamais ", RealFixed(e, "on a jamais "));
            foreach (var neg in new[] { "il fait un pas ", "pas à pas ", "il est plus grand ", "moi non plus.\n", "si jamais ", "de rien ",
                                        "une personne ", "il ne veut pas ", "ne pas manger ", "est-ce pas ", "as-tu pas ", "pourquoi pas ", "non pas " })
                Assert.Null(RealFixed(e, neg));
            e.NegationOn = false;
            try { Assert.Null(RealFixed(e, "c'est pas ")); }
            finally { e.NegationOn = true; }
        }
    }

    [RealGrammarFact]
    public void Real_D35_The_Group_Never_Holds_An_Excluded_Word()
    {
        lock (Gate)
        {
            var e = Real.Value;
            var keep = e.Excluded;
            try
            {
                e.Excluded = ExcludedWords.Empty.WithExtra(new[] { "veut" }, e);
                Assert.Null(RealFixed(e, "mon cousin veu pa "));      // the ≈ verb would be excluded
                Assert.Null(RealFixed(e, "il veut pas "));
                Assert.Equal("ce n'est pas ", RealFixed(e, "c'est pas "));
                Assert.Equal(0, e.ExcludedLate);
            }
            finally { e.Excluded = keep; }
        }
    }
}
