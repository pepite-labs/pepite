using System;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.11.0 (plan hyphen-names): the word before a typed hyphen — <see cref="HyphenHead"/> on a text, and the tracker's
/// <see cref="WordTracker.HyphenHead"/> / <see cref="WordTracker.HyphenHeadNeutral"/> on the text it kept.</summary>
public class HyphenHeadTests
{
    private static string? Head(string text)
    {
        int h = text.Length - 1;
        return HyphenHead.Find(text, h, 0, out int s) ? text[s..h] : null;
    }

    [Theory]
    [InlineData("saint-", "saint")]
    [InlineData("je vais à Saint-", "Saint")]
    [InlineData("Saint-Pierre-", "Saint-Pierre")]
    [InlineData("a-t-", "a-t")]
    [InlineData("l'arc-", "arc")]
    [InlineData("« Jean-", "Jean")]
    [InlineData("--", null)]
    [InlineData("x--", null)]
    [InlineData("- ", null)]
    [InlineData("3-", null)]
    [InlineData("-", null)]
    public void Find_takes_letters_joined_by_single_hyphens(string text, string? head) => Assert.Equal(head, Head(text));

    [Fact]
    public void Find_refuses_a_head_over_the_limit_or_cut_by_the_known_text()
    {
        var w = new string('a', PredictionEngine.HyphenHeadMax);
        Assert.Equal(w, Head(w + "-"));
        Assert.Null(Head(w + "a-"));
        // the start of the known text cuts the word: no head
        Assert.False(HyphenHead.Find("xsaint-", 6, 1, out _));
        Assert.True(HyphenHead.Find(" saint-", 6, 1, out int s));
        Assert.Equal(1, s);
    }

    [Theory]
    [InlineData("Saint-", true)]
    [InlineData("Il dit : Saint-", true)]
    [InlineData("Fin. Saint-", true)]
    [InlineData("je vais à Saint-", false)]
    public void NeutralBefore_is_a_sentence_start_an_opening_sign_or_nothing_known(string text, bool neutral)
    {
        Assert.True(HyphenHead.Find(text, text.Length - 1, 0, out int s));
        Assert.Equal(neutral, HyphenHead.NeutralBefore(text, s, 0));
    }

    private static WordTracker Typed(string text, bool start = false)
    {
        var t = new WordTracker();
        if (start) t.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        foreach (var c in text) t.Type(c);
        return t;
    }

    [Fact]
    public void The_tracker_gives_the_head_while_typing_after_the_hyphen()
    {
        var t = Typed("je vais à saint-");
        Assert.Equal("saint", t.HyphenHead);
        Assert.False(t.HyphenHeadNeutral);
        Assert.True(t.AfterSeparator);
        t.Type('n');
        Assert.Equal("n", t.Current);
        Assert.Equal("saint", t.HyphenHead);
        t.Type(' ');
        Assert.Null(t.HyphenHead);   // the hyphen is no longer right before the caret's word
        Assert.Equal("Saint-Pierre", Typed("Saint-Pierre-e").HyphenHead);
        Assert.Equal("a-t", Typed("il a-t-i").HyphenHead);
        Assert.Null(Typed("je vais à saint").HyphenHead);
        Assert.Null(Typed("saint--n").HyphenHead);
    }

    [Fact]
    public void A_head_at_a_sentence_start_or_after_nothing_known_is_neutral()
    {
        Assert.True(Typed("Saint-N", start: true).HyphenHeadNeutral);
        Assert.True(Typed("Saint-N").HyphenHeadNeutral);                      // nothing known before (a jump)
        Assert.True(Typed("Il a dit : Saint-", start: true).HyphenHeadNeutral);
        Assert.False(Typed("Il va à Saint-", start: true).HyphenHeadNeutral);
    }

    [Fact]
    public void A_head_rebuilt_from_a_read_is_none_but_the_word_a_read_gave_is_exact()
    {
        // a click after « saint- »: the read gives the words, not the hyphen (documented limit)
        var t = new WordTracker();
        t.Seed("", new[] { "vais", "à", "saint" }, 1, "", '\0', "uia");
        t.Type('n');
        Assert.Null(t.HyphenHead);
        // the caret right after « saint » (read), then « - » typed: the read's own word is exact
        t.Reset();
        t.Seed("saint", new[] { "vais", "à" }, 0, "", ' ', "uia");
        t.Type('-');
        Assert.Equal("saint", t.HyphenHead);
    }

    [Fact]
    public void Backspace_back_to_the_letters_after_the_hyphen_brings_the_head_back()
    {
        var t = Typed("je vais à Saint-Nicolas ");
        Assert.Null(t.HyphenHead);
        Assert.Equal(BackspaceEffect.Crossed, t.Backspace());
        Assert.Equal("Nicolas", t.Current);
        Assert.Equal("Saint", t.HyphenHead);
        Assert.Equal('-', t.LastSeparator);
    }
}
