using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.10.2 (plan exact-typed-word, owner 2026-11-17): the word typed in full and spelled right (✓ while typing) is
/// always one of the bar's words. Present: today's ranking, the word in the bar's last slot at worst. First: the word first,
/// after the words that sound like it and rank higher. On the real built databases (skipped with a message when
/// <c>Data.Build/out</c> has no grammar.sqlite / prediction.sqlite).</summary>
public class ExactTypedTests
{
    private static readonly string Out = RealGrammarFactAttribute.Out;
    private static readonly object Gate = new();
    private static readonly Lazy<PredictionEngine> Data = new(() =>
    {
        var e = PredictionEngine.Load(Path.Combine(Out, "prediction.sqlite"));
        e.Grammar = GrammarLexicon.Load(Path.Combine(Out, "grammar.sqlite"), e);
        return e;
    });

    /// <summary>The engine in one mode (the engine is not thread-safe: one test at a time; every setting restored).</summary>
    private static T With<T>(ExactTypedMode mode, Func<PredictionEngine, T> f, int bar = 8, NamesLexicon? names = null,
                             UserLexicon? user = null)
    {
        lock (Gate)
        {
            var e = Data.Value;
            var keep = (e.ExactTyped, e.ExactBar, e.ExactMargin, e.Names, e.User);
            try
            {
                e.ExactTyped = mode; e.ExactBar = bar; e.ExactMargin = double.PositiveInfinity;
                if (names is not null) e.Names = names;
                if (user is not null) e.User = user;
                return f(e);
            }
            finally { (e.ExactTyped, e.ExactBar, e.ExactMargin, e.Names, e.User) = keep; }
        }
    }

    private static string[] Words(ExactTypedMode mode, string ctx, string typed, int k = 8, bool start = false, bool? mark = null, int bar = 8,
                                  NamesLexicon? names = null) =>
        With(mode, e => e.Predict(typed, Ctx(ctx), k, start, mark).Select(s => s.Word).ToArray(), bar, names);

    private static NameRecord R(string nom, double rang, int niveau = 1) => new(nom, Array.Empty<string>(), "test", rang, niveau, null, null, null, null);

    /// <summary>A small names pack (the shipped one is not in every test setup): names that are lexicon words too (Pierre,
    /// Rose, Mai, Belgique), a first name typed in full (Rodolphe), a recognised one (Martine).</summary>
    private static readonly Lazy<NamesLexicon> SmallPack = new(() => With(ExactTypedMode.Off, e => NamesLexicon.Build(new[]
    {
        R("Rodolphe", 0.3), R("Martine", 0.05, 2), R("Mai", 0.05), R("Pierre", 0.8), R("Rose", 0.6), R("Belgique", 0.6),
        R("Léa", 0.9), R("Maïa", 0.5), R("Paris", 0.9), R("Parisa", 0.2),
    }, e.Knows, e.MostFrequent300)));

    /// <summary>The probes with the pack: names typed in full, a name as a lexicon word, a sentence start.</summary>
    private static readonly (string Ctx, string Typed, bool Start)[] NameProbes =
    {
        ("avec", "Rodolphe", false), ("", "Rodolphe", true), ("avec", "Pierre", false), ("la", "pierre", false), ("en", "belgique", false),
        ("", "Mai", true), ("avec", "Mai", false), ("une", "rose", false), ("à", "Paris", false), ("avec", "Martine", false),
    };

    private static string[]? Ctx(string ctx) => ctx.Length == 0 ? null : new[] { ctx };

    private static readonly (string Ctx, string Typed, bool Start)[] Probes =
    {
        ("la", "mai", false), ("", "mai", true), ("je", "par", false), ("il", "et", false), ("ils", "mange", false),
        ("il", "est", false), ("la", "maison", false), ("c'est", "bon", false), ("dire", "bon", false), ("je vais", "par", false),
        ("le", "ver", false), ("la", "faim", false), ("le", "thème", false), ("elle", "mai", false), ("", "Mai", true),
        ("", "maison", true), ("un", "ver", false), ("il", "pas", false), ("ils", "son", false), ("le mois de", "mai", false),
    };

    [RealGrammarFact]
    public void Present_puts_the_typed_word_in_the_last_slot_when_the_ranking_leaves_it_out()
    {
        var off = Words(ExactTypedMode.Off, "la", "mai");
        Assert.DoesNotContain("mai", off);
        var on = Words(ExactTypedMode.Present, "la", "mai");
        Assert.Equal("mai", on[7]);
        Assert.Equal(off.Take(7), on.Take(7));   // the ranking is otherwise today's
        // already in the bar: nothing moves
        Assert.Equal(Words(ExactTypedMode.Off, "le", "ver"), Words(ExactTypedMode.Present, "le", "ver"));
        // fewer words than the bar (KSS: k = 1): untouched
        Assert.Equal(Words(ExactTypedMode.Off, "la", "mai", k: 1), Words(ExactTypedMode.Present, "la", "mai", k: 1));
    }

    [RealGrammarFact]
    public void First_puts_the_typed_word_first_after_its_homophones()
    {
        var mai = Words(ExactTypedMode.First, "la", "mai");
        Assert.Equal(new[] { "mais", "mai" }, mai.Take(2));            // « mais » sounds the same and ranks higher
        var mange = Words(ExactTypedMode.First, "ils", "mange");
        Assert.Equal(new[] { "mangent", "mange" }, mange.Take(2));     // the agreeing homophone first (owner Q3)
        var et = Words(ExactTypedMode.First, "il", "et");
        Assert.True(Array.IndexOf(et, "est") is >= 0 and var i && i < Array.IndexOf(et, "et"), string.Join(", ", et));
        Assert.Equal("maison", Words(ExactTypedMode.First, "la", "maison")[0]);
        Assert.Equal("bon", Words(ExactTypedMode.First, "dire", "bon")[0]);   // a completion is no homophone: it goes after (Q4)
        Assert.Equal("par", Words(ExactTypedMode.First, "je vais", "par")[0]);
        // at a sentence start the capital says nothing: « Mai » is the month, one tile
        var start = Words(ExactTypedMode.First, "", "Mai", start: true);
        Assert.Contains("mai", start);
        Assert.DoesNotContain("Mai", start);
    }

    [RealGrammarFact]
    public void The_rule_needs_a_tick_while_typing()
    {
        foreach (var m in new[] { ExactTypedMode.Present, ExactTypedMode.First })
        {
            // not ✓: no accent (« theme »), out of the spelling reference (« yeah »), a bare elision form, an acronym
            Assert.Equal(Words(ExactTypedMode.Off, "le", "theme"), Words(m, "le", "theme"));
            Assert.Equal(Words(ExactTypedMode.Off, "oh", "yeah"), Words(m, "oh", "yeah"));
            Assert.Equal(Words(ExactTypedMode.Off, "parce", "qu"), Words(m, "parce", "qu"));
            Assert.Equal(Words(ExactTypedMode.Off, "la", "SNCB"), Words(m, "la", "SNCB"));
            // letters the indicator does not judge (after a digit, a part of a compound…): the keyboard passes false
            Assert.Equal(Words(ExactTypedMode.Off, "la", "mai"), Words(m, "la", "mai", mark: false));
        }
    }

    [RealGrammarFact]
    public void A_pack_name_no_pass_kept_is_still_placed()
    {
        // 14 more popular names share the prefix: the names pass keeps the k + 4 best only, « Zorglub » is not among them
        var recs = new List<NameRecord> { new("Zorglub", Array.Empty<string>(), "prenom", 0.01, 1, null, null, null, null) };
        for (int i = 0; i < 14; i++)
            recs.Add(new NameRecord("Zorglub" + (char)('a' + i) + "x", Array.Empty<string>(), "prenom", 0.9, 1, null, null, null, null));
        var names = With(ExactTypedMode.Off, e => NamesLexicon.Build(recs, e.Knows, e.MostFrequent300));
        string[] Run(ExactTypedMode m) => With(m, e => e.Predict("Zorglub", new[] { "avec" }, 8).Select(s => s.Word).ToArray(), names: names);
        Assert.DoesNotContain("Zorglub", Run(ExactTypedMode.Off));
        Assert.Equal("Zorglub", Run(ExactTypedMode.Present)[7]);
        Assert.Equal("Zorglub", Run(ExactTypedMode.First)[0]);
        // a restricted figure (D30) only where the names pass offers it: never at a sentence start
        var fig = new[] { new NameRecord("Zorglux", Array.Empty<string>(), "personnage", 0.5, 2, null, null, null, null, Restreint: true) };
        var figs = With(ExactTypedMode.Off, e => NamesLexicon.Build(fig, e.Knows, e.MostFrequent300));
        foreach (var m in new[] { ExactTypedMode.Present, ExactTypedMode.First })
        {
            Assert.DoesNotContain("Zorglux", With(m, e => e.Predict("Zorglux", null, 8, sentenceStart: true).Select(s => s.Word).ToArray(), names: figs));
            Assert.Contains("Zorglux", With(m, e => e.Predict("Zorglux", new[] { "avec" }, 8).Select(s => s.Word).ToArray(), names: figs));
        }
        // a name typed in full with its capital is ✓; typed in lowercase inside a sentence it is not (the limit « avec martine »)
        With(ExactTypedMode.Off, e => { Assert.False(e.KnowsForMark("zorglub", false)); return 0; }, names: names);
    }

    [RealGrammarFact]
    public void Off_and_unjudged_letters_give_the_same_results()
    {
        foreach (var (c, t, s) in Probes)
        {
            var off = With(ExactTypedMode.Off, e => e.Predict(t, Ctx(c), 11, s).ToArray());
            foreach (var m in new[] { ExactTypedMode.Present, ExactTypedMode.First })
                Assert.Equal(off, With(m, e => e.Predict(t, Ctx(c), 11, s, exactMark: false).ToArray()));
        }
    }

    [RealGrammarFact]
    public void Fewer_words_are_always_the_first_ones_of_more_words()
    {
        foreach (var pack in new[] { false, true })
            foreach (var m in new[] { ExactTypedMode.Off, ExactTypedMode.Present, ExactTypedMode.First })
                foreach (var (c, t, s) in pack ? Probes.Concat(NameProbes) : Probes)
                {
                    var names = pack ? SmallPack.Value : null;
                    var all = Words(m, c, t, k: 11, start: s, names: names);
                    for (int k = 1; k <= 11; k++)
                        Assert.True(all.Take(k).SequenceEqual(Words(m, c, t, k: k, start: s, names: names)),
                                    $"{m} pack={pack} [{c}] {t} k={k}: {string.Join(", ", all)}");
                }
    }

    [RealGrammarFact]
    public void A_bar_of_five_words_gets_the_word_in_its_fifth_slot()
    {
        var present = Words(ExactTypedMode.Present, "la", "mai", k: 8, bar: 5);
        Assert.Equal("mai", present[4]);
        var first = Words(ExactTypedMode.First, "le", "ver", k: 8, bar: 5);
        Assert.InRange(Array.IndexOf(first, "ver"), 0, 4);
        Assert.InRange(Array.IndexOf(Words(ExactTypedMode.Present, "la", "mai", k: 5, bar: 5), "mai"), 0, 4);
    }

    [RealGrammarFact]
    public void The_rule_follows_the_tick_the_keyboard_shows()
    {
        // a very rare recognised name: ✓ inside a sentence, not at a start (IsWeak). After « : » the tracker's ✓ is computed
        // as inside a sentence while the engine is told « sentence start » (CapitalContext: a neutral capital)
        var weak = new[] { new NameRecord("Zorgla", Array.Empty<string>(), "prenom", 0.005, 2, null, null, null, null) };
        var names = With(ExactTypedMode.Off, e => NamesLexicon.Build(weak, e.Knows, e.MostFrequent300));
        With(ExactTypedMode.Off, e =>
        {
            Assert.True(e.KnowsForMark("Zorgla", false));
            Assert.False(e.KnowsForMark("Zorgla", true));
            return 0;
        }, names: names);
        string[] Run(bool? mark) =>
            With(ExactTypedMode.Present, e => e.Predict("Zorgla", new[] { "Il", "dit", ":" }, 8, sentenceStart: true, mark).Select(s => s.Word).ToArray(), names: names);
        Assert.Contains("Zorgla", Run(true));        // the keyboard's ✓: in the bar
        Assert.DoesNotContain("Zorgla", Run(null));  // the engine's own judgement at a sentence start: no ✓, no rule
        Assert.DoesNotContain("Zorgla", Run(false));
    }

    [RealGrammarFact]
    public void A_one_word_bar_has_no_rule()
    {
        // the last slot of a one-word bar is its first: the « first » variant the owner rejected
        foreach (var (c, t, s) in Probes)
            foreach (var k in new[] { 1, 4 })
                Assert.Equal(Words(ExactTypedMode.Off, c, t, k: k, start: s), Words(ExactTypedMode.Present, c, t, k: k, start: s, bar: 1));
    }

    [RealGrammarFact]
    public void A_tick_while_typing_means_the_word_is_a_tile()
    {
        var texts = new[] { "la mai", "mai", "je par", "il et", "ils mange", "il est", "la maison", "dire bon", "je vais par", "le ver",
                            "la faim", "elle mai", "Mai", "Maison", "il fait bo", "le theme", "avec Rodolphe", "un ver", "il pas", "ils son",
                            "Le chat. Il et", "en mai", "on va", "les enfants", "il mange", "je mange", "la mer" };
        var nameTexts = new[] { "avec Rodolphe", "Rodolphe", "avec Pierre", "la pierre", "en belgique", "Mai", "avec Mai", "une rose",
                                "à Paris", "avec Martine", "Il dit : Martine", "Martine" };
        foreach (var pack in new[] { false, true })
        foreach (var m in new[] { ExactTypedMode.Present, ExactTypedMode.First })
            foreach (var text in pack ? texts.Concat(nameTexts) : texts)
                With(m, e =>
                {
                    var fw = SpellingIndicator.LastWord(text)!.Value;
                    var mark = SpellingIndicator.Typing(fw, w => e.KnowsForMark(w, fw.SentenceStart));
                    if (mark != SpellingMark.Known) return 0;
                    var ctx = text[..^fw.Word.Length].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    // as the keyboard: the ✓ just computed is passed on
                    var shown = KeyboardPolicy.Shown(e.Predict(fw.Word, ctx, 8 + 3, fw.SentenceStart, true), false, '\'', fw.Word, fw.SentenceStart, 8);
                    Assert.True(shown.Any(s => string.Equals(s.Word, fw.Word, StringComparison.OrdinalIgnoreCase)),
                                $"{m} pack={pack} « {text} » ✓ but not shown: {string.Join(", ", shown.Select(s => s.Word))}");
                    return 0;
                }, names: pack ? SmallPack.Value : null);
    }

    [RealGrammarFact]
    public void A_personal_word_no_pass_kept_is_still_placed()
    {
        // 40 heavier list words share the prefix: the personal pass scores only the ShortlistSize (32) heaviest unknown ones
        var words = new List<UserWord> { new("zorbu", 0.1) };
        for (int i = 0; i < 40; i++) words.Add(new UserWord("zorbu" + (char)('a' + i % 26) + (char)('a' + i / 26), 5.0));
        var user = UserLexicon.Build(words);
        string[] Run(ExactTypedMode m) => With(m, e => e.Predict("zorbu", new[] { "le" }, 8).Select(s => s.Word).ToArray(), user: user);
        With(ExactTypedMode.Off, e => { Assert.True(e.KnowsForMark("zorbu", false)); return 0; }, user: user);
        Assert.DoesNotContain("zorbu", Run(ExactTypedMode.Off));
        var present = Run(ExactTypedMode.Present);
        Assert.Equal("zorbu", present[7]);                                    // the window's lowest score - 0.001: last slot
        Assert.Equal(Run(ExactTypedMode.Off).Take(7), present.Take(7));
        Assert.Equal("zorbu", Run(ExactTypedMode.First)[0]);
    }

    [RealGrammarFact]
    public void A_capitalised_form_of_the_typed_letters_counts_as_present()
    {
        // « belgique » typed in lowercase: the pack's « Belgique » takes the place of the rare lexicon form, and is the word
        var off = Words(ExactTypedMode.Off, "en", "belgique", names: SmallPack.Value);
        var present = Words(ExactTypedMode.Present, "en", "belgique", names: SmallPack.Value);
        Assert.Contains("Belgique", off);
        Assert.Equal(off, present);   // already in the bar: nothing added, no lowercase twin
        Assert.DoesNotContain("belgique", present);
    }

    [RealGrammarFact]
    public void A_finished_word_is_unchanged_so_a_tick_alone_still_hides_the_bar()
    {
        // Q10 (owner Q5): after a separator the bar holds the next words, never the finished one: the rule does not touch them
        var w = SpellingIndicator.LastWord("la maison. ")!.Value;
        Assert.Equal(SpellingMark.Known, With(ExactTypedMode.First, e => SpellingIndicator.Finished(w, x => e.Check(x))));
        foreach (var ctx in new[] { new[] { "la", "maison" }, new[] { "zzzqx" } })
        {
            var off = With(ExactTypedMode.Off, e => e.PredictNext(ctx, 9).ToArray());
            Assert.Equal(off, With(ExactTypedMode.First, e => e.PredictNext(ctx, 9).ToArray()));
            Assert.Equal(off, With(ExactTypedMode.Present, e => e.PredictNext(ctx, 9).ToArray()));
        }
    }
}
