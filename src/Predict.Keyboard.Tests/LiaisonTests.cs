using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 08b step 8: the word echo says « prev + word » together when a liaison follows, so the voice can make
/// it (TODO « The voice does not make French liaisons »).</summary>
public class LiaisonTests
{
    [Theory]
    // the six TODO pairs
    [InlineData("les", "amis")]
    [InlineData("un", "enfant")]
    [InlineData("ils", "ont")]
    [InlineData("deux", "heures")]
    [InlineData("petit", "ami")]
    [InlineData("c'est", "un")]
    // more
    [InlineData("bon", "exemple")]
    [InlineData("mon", "ami")]
    [InlineData("grand", "homme")]
    [InlineData("Les", "Amis")]           // case-insensitive
    [InlineData("C’est", "un")]           // typographic apostrophe
    [InlineData("trois", "enfants")]
    public void Grouped(string prev, string next) => Assert.True(Liaison.Triggers(prev, next));

    [Theory]
    [InlineData("les", "héros")]          // h aspiré
    [InlineData("et", "un")]              // never after « et »
    [InlineData("les", "mains")]          // consonant
    [InlineData("ils", "sont")]
    [InlineData("les", "huit")]
    [InlineData("un", "hibou")]
    [InlineData("chat", "ami")]           // not a liaison word
    [InlineData("", "ami")]
    [InlineData("les", "")]
    public void Not_Grouped(string prev, string next) => Assert.False(Liaison.Triggers(prev, next));

    private static List<EchoEvent> Feed(SpeechEcho e, string text)
    {
        var l = new List<EchoEvent>();
        foreach (var c in text) if (e.Type(c) is { } ev) l.Add(ev);
        return l;
    }

    [Fact]
    public void Echo_Says_Les_Then_Les_Amis_Covering_Les()
    {
        var e = new SpeechEcho { SpeakWords = true };
        var ev = Feed(e, "les amis arrivent ");
        Assert.Equal(new[] { "les", "les amis", "arrivent" }, ev.Select(x => x.Text));
        Assert.Equal(new string?[] { null, "les", null }, ev.Select(x => x.Covers));
    }

    [Fact]
    public void Each_Todo_Pair_Is_One_Utterance_And_The_Others_Are_Not()
    {
        foreach (var (text, want) in new[]
                 {
                     ("un enfant ", "un enfant"), ("ils ont ", "ils ont"), ("deux heures ", "deux heures"),
                     ("petit ami ", "petit ami"), ("c'est un ", "c'est un"), ("les héros ", "héros"), ("et un ", "un"),
                     ("les mains ", "mains"),
                 })
            Assert.Equal(want, Feed(new SpeechEcho { SpeakWords = true }, text).Last().Text);
    }

    [Fact]
    public void Only_Spaces_Join_A_Comma_Or_Enter_Does_Not()
    {
        Assert.Equal(new[] { "les", "amis" }, Feed(new SpeechEcho { SpeakWords = true }, "les, amis ").Select(x => x.Text));
        Assert.Equal(new[] { "les", "amis" }, Feed(new SpeechEcho { SpeakWords = true }, "les\namis ").Select(x => x.Text));
        Assert.Equal(new[] { "les", "les amis" }, Feed(new SpeechEcho { SpeakWords = true }, "les  amis ").Select(x => x.Text));
    }

    [Fact]
    public void Sentence_Echo_Is_Not_Grouped_And_Words_Before_The_End_Are()
    {
        var e = new SpeechEcho { SpeakWords = true, SpeakSentences = true };
        var ev = Feed(e, "les amis.");
        Assert.Equal(new[] { "les", "les amis" }, ev.Select(x => x.Text));
        Assert.Equal(EchoKind.Sentence, ev.Last().Kind);
        Assert.Equal("les amis", ev.Last().Text);
        Assert.Null(ev.Last().Covers);
        // sentences off: the last word is grouped too
        Assert.Equal("les amis", Feed(new SpeechEcho { SpeakWords = true }, "les amis.").Last().Text);
    }

    [Fact]
    public void Backspace_Into_Amis_Keeps_The_Grouping()
    {
        var e = new SpeechEcho { SpeakWords = true };
        Feed(e, "les ami ");
        e.Backspace();                                    // the space after « ami »: the chunk comes back
        Assert.Equal("les amis", Feed(e, "s ").Single().Text);
    }

    [Fact]
    public void Backspace_Emptying_A_Word_Keeps_Its_Place_After_Les()
    {
        var e = new SpeechEcho { SpeakWords = true };
        Assert.Equal(new[] { "les" }, Feed(e, "les x").Select(x => x.Text));
        e.Backspace();                                    // « x » erased: the next word still follows « les » after a space
        Assert.Equal("les amis", Feed(e, "amis ").Single().Text);
        // erased chunk, then another space: still spaces only
        var f = new SpeechEcho { SpeakWords = true };
        Feed(f, "les x");
        f.Backspace();
        Assert.Equal("les amis", Feed(f, " amis ").Single().Text);
        // a comma before the erased chunk stays a comma
        var g = new SpeechEcho { SpeakWords = true };
        Feed(g, "les, x");
        g.Backspace();
        Assert.Equal("amis", Feed(g, "amis ").Single().Text);
        var h = new SpeechEcho { SpeakWords = true };
        Feed(h, "les, x");
        h.Backspace();
        Assert.Equal("amis", Feed(h, " amis ").Single().Text);
    }

    [Fact]
    public void Queue_Merges_A_Chain_Of_Liaisons()
    {
        // « ils en ont » typed fast: « ils », « ils en » (covers ils), « en ont » (covers en) all pending
        var q = new SpeechQueue();
        q.Enqueue(new SpeechItem("chat", SpeechKind.Word));
        q.Enqueue(new SpeechItem("ils", SpeechKind.Word));
        q.Enqueue(new SpeechItem("ils en", SpeechKind.Word, Covers: "ils"));
        Assert.Equal(new[] { "chat", "ils en" }, q.Pending.Select(x => x.Text));
        q.Enqueue(new SpeechItem("en ont", SpeechKind.Word, Covers: "en"));
        Assert.Equal(new[] { "chat", "ils en ont" }, q.Pending.Select(x => x.Text));
        Assert.Equal("ils", q.Pending[^1].Covers);
        // the echo itself produces that chain
        var e = new SpeechEcho { SpeakWords = true };
        Assert.Equal(new[] { "ils", "ils en", "en ont" }, Feed(e, "ils en ont ").Select(x => x.Text));
    }

    [Fact]
    public void Reset_And_Seed_Forget_The_Previous_Word()
    {
        var e = new SpeechEcho { SpeakWords = true };
        Feed(e, "les ");
        e.Reset();
        Assert.Equal("amis", Feed(e, "amis ").Single().Text);
        Feed(e, "les ");
        e.Seed("am");
        Assert.Equal("amis", Feed(e, "is ").Single().Text);
    }

    [Fact]
    public void F1_After_Les_Says_Les_Amis()
    {
        var e = new SpeechEcho { SpeakWords = true };
        Feed(e, "les ");
        Assert.Null(e.Inserted("amis", spaced: true, spoken: true));
        Assert.Equal("amis", e.LastInserted);
        Assert.Equal("les amis", e.LastInsertedSpoken);
        // typed prefix then F1
        var f = new SpeechEcho { SpeakWords = true };
        Feed(f, "un enf");
        f.Inserted("enfant", spaced: true, spoken: true);
        Assert.Equal("un enfant", f.LastInsertedSpoken);
        // no liaison: the word alone
        var g = new SpeechEcho { SpeakWords = true };
        Feed(g, "les ");
        g.Inserted("mains", spaced: true, spoken: true);
        Assert.Equal("mains", g.LastInsertedSpoken);
    }

    [Fact]
    public void Elision_Chunk_Has_No_Previous_Word_Of_Its_Own()
    {
        Assert.Equal("l'ami", Feed(new SpeechEcho { SpeakWords = true }, "les l'ami ").Last().Text);
    }

    [Fact]
    public void Queue_Drops_A_Pending_Covered_Word_Only()
    {
        var q = new SpeechQueue();
        q.Enqueue(new SpeechItem("chat", SpeechKind.Word));
        q.Enqueue(new SpeechItem("les", SpeechKind.Word));
        q.Enqueue(new SpeechItem("les amis", SpeechKind.Word, Covers: "les"));
        Assert.Equal(new[] { "chat", "les amis" }, q.Pending.Select(x => x.Text));
        // « les » already playing (not pending): nothing to drop, « les amis » follows
        var r = new SpeechQueue();
        r.Enqueue(new SpeechItem("les", SpeechKind.Word));
        Assert.Equal("les", r.Next()!.Text);
        r.Enqueue(new SpeechItem("les amis", SpeechKind.Word, Covers: "les"));
        Assert.Equal(new[] { "les amis" }, r.Pending.Select(x => x.Text));
    }

#if HAVE_ESPEAK
    [Theory]
    [InlineData("les amis", "z")]
    [InlineData("un enfant", "n")]
    [InlineData("ils ont", "z")]
    [InlineData("deux heures", "z")]
    [InlineData("petit ami", "t")]
    [InlineData("c'est un", "t")]
    public void Grouped_Phrase_Has_The_Liaison_Consonant_In_Espeak(string phrase, string consonant)
    {
        EspeakNative.Initialize(System.IO.Path.Combine(AppContext.BaseDirectory, "espeak-ng-data"));
        var words = string.Concat(PiperPhonemizer.Sentences(EspeakNative.Clauses(TextCleanup.ForEspeak(phrase), "fr"), null).Single())
                          .Split(' ');
        // espeak keeps the liaison consonant at the end of the left word (« le-z amˈi »)
        var left = words[0].TrimEnd('.', ',', '-');
        Assert.EndsWith(consonant, left);
        // alone, the left word has no such consonant (except « c'est », whose /t/ comes from the liaison)
        var alone = string.Concat(PiperPhonemizer.Sentences(EspeakNative.Clauses(TextCleanup.ForEspeak(phrase.Split(' ')[0]), "fr"), null).Single())
                          .TrimEnd('.', ',', '-');
        Assert.False(alone.EndsWith(consonant), $"{phrase}: alone {alone}");
    }
#endif
}
