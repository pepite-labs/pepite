using System;
using System.Collections.Generic;
using System.Linq;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.11.0 (plan hyphen-names, owner Q1 = B): the keyboard side of a compound tile after « head- » — how it goes in
/// (<see cref="KeyboardPolicy.CompoundInsert"/>: the ending at the caret, or the whole name over the head, read first:
/// <see cref="SoundAlikeCorrection.DecideCompound"/>), what the bar shows (<see cref="KeyboardPolicy.Shown"/>, the owner's
/// Q2, decided 2026-11-27: <see cref="KeyboardPolicy.CompoundTiles"/>), and the tracker / echo after the replacement.</summary>
public class CompoundReplaceTests
{
    private static Suggestion S(string w, bool name = false) => new(w, 0, MatchKind.Prefix, name);

    [Fact]
    public void CompoundInsert_tells_the_ending_and_whether_the_head_is_rewritten()
    {
        Assert.Equal((true, "Nicolas"), KeyboardPolicy.CompoundInsert("Saint-Nicolas", "saint"));
        Assert.Equal((false, "Nicolas"), KeyboardPolicy.CompoundInsert("Saint-Nicolas", "Saint"));
        Assert.Equal((false, "être"), KeyboardPolicy.CompoundInsert("peut-être", "peut"));
        Assert.Equal((false, "et-Miquelon"), KeyboardPolicy.CompoundInsert("Saint-Pierre-et-Miquelon", "Saint-Pierre"));
        Assert.Equal((true, "Pierre-et-Miquelon"), KeyboardPolicy.CompoundInsert("Saint-Pierre-et-Miquelon", "SAINT"));
        Assert.Null(KeyboardPolicy.CompoundInsert("nicolas", "saint"));
        Assert.Null(KeyboardPolicy.CompoundInsert("Saint", "saint"));
        Assert.Null(KeyboardPolicy.CompoundInsert("Saint-", "saint"));
        Assert.Null(KeyboardPolicy.CompoundInsert("Saintes-Maries", "saint"));
    }

    [Fact]
    public void Shown_never_cases_a_compound_tile_again()
    {
        var ranked = new[] { S("Saint-Nicolas", true), S("saint-nectaire"), S("ne"), S("nom") };
        var shown = KeyboardPolicy.Shown(ranked, true, '\'', "N", false, 8, "saint");
        Assert.Equal(new[] { "Saint-Nicolas", "saint-nectaire", "Ne", "Nom" }, shown.Select(x => x.Word));
        Assert.True(shown[0].Name);
        // without a head the old casing applies to every tile
        Assert.Equal("Saint-nectaire", KeyboardPolicy.Shown(ranked, true, '\'', "N", false, 8)[1].Word);
    }

    [Fact]
    public void Q2_the_tile_where_the_head_cannot_be_replaced_follows_one_switch()
    {
        var shown = new List<Suggestion> { S("Saint-Nicolas", true), S("Saint-Nazaire", true), S("Nicolas"), S("ne") };
        Assert.Same(shown, KeyboardPolicy.CompoundTiles(shown, "saint", mayReplace: true));
        Assert.Same(shown, KeyboardPolicy.CompoundTiles(shown, "saint", mayReplace: false, CompoundUnreadable.WholeTile));
        Assert.Equal(CompoundUnreadable.WholeTile, KeyboardPolicy.UnreadableCompoundTile);   // the owner's choice (Q2, 2026-11-27)
        var ending = KeyboardPolicy.CompoundTiles(shown, "saint", mayReplace: false, CompoundUnreadable.EndingTile);
        Assert.Equal(new[] { "Nicolas", "Nazaire", "ne" }, ending.Select(x => x.Word));
        // a head typed as the name writes it: nothing to replace, the whole tile stays
        var same = new List<Suggestion> { S("Saint-Nicolas", true) };
        Assert.Equal("Saint-Nicolas", KeyboardPolicy.CompoundTiles(same, "Saint", false, CompoundUnreadable.EndingTile)[0].Word);
    }

    [Fact]
    public void DecideCompound_never_deletes_the_head_unseen()
    {
        Assert.Equal(SoundAlikeCorrection.Action.Replace, SoundAlikeCorrection.DecideCompound(ReadStatus.Ok, true, false));
        Assert.Equal(SoundAlikeCorrection.Action.Insert, SoundAlikeCorrection.DecideCompound(ReadStatus.Ok, false, false));
        Assert.Equal(SoundAlikeCorrection.Action.Insert, SoundAlikeCorrection.DecideCompound(ReadStatus.Timeout, false, false));
        Assert.Equal(SoundAlikeCorrection.Action.Insert, SoundAlikeCorrection.DecideCompound(ReadStatus.Selection, false, false));
        Assert.Equal(SoundAlikeCorrection.Action.Insert, SoundAlikeCorrection.DecideCompound(ReadStatus.Unavailable, false, false));
        Assert.Equal(SoundAlikeCorrection.Action.ReplaceKeysOnly, SoundAlikeCorrection.DecideCompound(ReadStatus.Unavailable, false, true));
        Assert.Equal(SoundAlikeCorrection.Action.Nothing, SoundAlikeCorrection.DecideCompound(ReadStatus.Stale, true, true));
        Assert.Equal(SoundAlikeCorrection.Action.Nothing, SoundAlikeCorrection.DecideCompound(ReadStatus.Password, true, true));
    }

    [Fact]
    public void DocumentEndsWithSpan_matches_the_span_at_the_caret_in_any_case()
    {
        Assert.True(SoundAlikeCorrection.DocumentEndsWithSpan("je vais à saint-n", "saint-n", out var doc));
        Assert.Equal("saint-n", doc);
        Assert.True(SoundAlikeCorrection.DocumentEndsWithSpan("Saint-N", "saint-n", out doc));
        Assert.Equal("Saint-N", doc);
        Assert.False(SoundAlikeCorrection.DocumentEndsWithSpan("je vais à saint-n ", "saint-n", out _));   // the caret moved on
        Assert.False(SoundAlikeCorrection.DocumentEndsWithSpan("toussaint-n", "saint-n", out _));         // glued to a letter
        Assert.False(SoundAlikeCorrection.DocumentEndsWithSpan("x-saint-n", "saint-n", out _));           // a longer head
        Assert.False(SoundAlikeCorrection.DocumentEndsWithSpan("", "saint-n", out _));
    }

    private static WordTracker Typed(string text)
    {
        var t = new WordTracker();
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        foreach (var c in text) t.Type(c);
        return t;
    }

    [Fact]
    public void The_tracker_follows_the_replacement_as_if_the_name_had_been_typed()
    {
        var t = Typed("Je vais à saint-n");
        Assert.True(t.CanReplaceCompound("saint-n"));
        Assert.False(t.CanReplaceCompound("aint-n"));   // a letter glued before
        Assert.False(t.CanReplaceCompound("saint-x"));
        long v = t.Version;
        t.ReplaceCompound("saint-n", "Saint-Nicolas", spaced: true);
        Assert.NotEqual(v, t.Version);
        var typed = Typed("Je vais à Saint-Nicolas ");
        Assert.Equal(typed.Current, t.Current);
        Assert.Equal(typed.Context, t.Context);
        Assert.Equal(typed.AfterSeparator, t.AfterSeparator);
        Assert.Equal(typed.LastSeparator, t.LastSeparator);
        Assert.Equal(new FinishedWord("Nicolas", '-', ' ', "Saint", false), t.Finished);
        // Backspace: the ending again, with its head
        Assert.Equal(BackspaceEffect.Crossed, t.Backspace());
        Assert.Equal("Nicolas", t.Current);
        Assert.Equal("Saint", t.HyphenHead);
        Assert.Equal(BackspaceEffect.Crossed, typed.Backspace());
        Assert.Equal(typed.Current, t.Current);
        Assert.Equal(typed.Context, t.Context);
    }

    [Fact]
    public void Without_a_space_the_name_is_finished_as_an_insertion_is()
    {
        var t = Typed("à saint-n");
        t.ReplaceCompound("saint-n", "Saint-Nicolas", spaced: false);
        Assert.Equal("", t.Current);
        Assert.False(t.AfterSeparator);
        Assert.Equal("Nicolas", t.Context[^1]);
        Assert.Equal('\0', t.Finished!.Value.After);
    }

    [Fact]
    public void A_read_rebuilt_head_or_a_letter_after_the_caret_is_never_replaced()
    {
        var t = new WordTracker();
        t.Seed("", new[] { "à", "saint" }, 1, "", '\0', "uia");
        foreach (var c in "-n") t.Type(c);
        Assert.False(t.CanReplaceCompound("saint-n"));   // « saint » comes from the read's rebuilt part
        t.Reset();
        t.Seed("n", new[] { "à" }, 0, "icolas", ' ', "uia");
        Assert.False(t.CanReplaceCompound("n"));          // a suffix after the caret
    }

    [Fact]
    public void The_negation_clause_reads_the_name_as_typed_words_after_a_replacement()
    {
        var t = Typed("Je vais à saint-n");
        t.ReplaceCompound("saint-n", "Saint-Nicolas", spaced: true);
        foreach (var c in "veut pas ") t.Type(c);
        Assert.True(t.TryClause(out var toks, out _, out var tail, out _));
        Assert.Equal("pas", toks[^1].Word);
        Assert.Contains(toks, x => x.Word == "Nicolas" && x.Exact);
        Assert.Equal(" ", tail);
    }

    [Fact]
    public void The_echo_says_the_whole_name_on_both_paths()
    {
        // the replacement: back over the span, the name inserted
        var e = new SpeechEcho { SpeakWords = true };
        foreach (var c in "je vais à saint-n") e.Type(c);
        for (int i = 0; i < "saint-n".Length; i++) e.Backspace();
        e.Inserted("Saint-Nicolas", true, spoken: true);
        Assert.Equal("Saint-Nicolas", e.LastInserted);
        // the ending at the caret: the typed head stays in the chunk
        var f = new SpeechEcho { SpeakWords = true };
        foreach (var c in "je vais à saint-n") f.Type(c);
        f.Inserted("Nicolas", true, spoken: true);
        Assert.Equal("saint-Nicolas", f.LastInserted);
        // a word just heard as « Saint-Nicolas » is not said again for « saint-Nicolas » (case is not heard)
        Assert.False(ListenFirst.SayAtInsert(f.LastInsertedSpoken, "Saint-Nicolas"));
    }

    [Fact]
    public void The_replacement_keys_backspace_the_span_then_type_the_name()
    {
        var keys = Inserter.Build(0, "saint-n".Length, "Saint-Nicolas ", UIntPtr.Zero);
        Assert.Equal(("saint-n".Length + "Saint-Nicolas ".Length) * 2, keys.Length);
        Assert.Equal(Inserter.VK_BACK, keys[0].u.ki.wVk);
        Assert.Equal('S', (char)keys["saint-n".Length * 2].u.ki.wScan);
    }
}
