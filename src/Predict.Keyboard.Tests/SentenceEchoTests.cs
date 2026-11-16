using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>D17 amendment (owner, 2026-11-14): a sentence end typed after a jump into the sentence says the whole
/// sentence from the document (owner's Word run: « tape. » after editing the last word said « tape » only).</summary>
public class SentenceEchoTests
{
    private static List<EchoEvent> Feed(SpeechEcho e, string text)
    {
        var l = new List<EchoEvent>();
        foreach (var c in text) if (e.Type(c) is { } ev) l.Add(ev);
        return l;
    }

    [Fact]
    public void A_Sentence_Typed_Whole_Is_Not_Marked()
    {
        var e = new SpeechEcho { SpeakSentences = true };
        var ev = Feed(e, "Le chat dort.").Single();
        Assert.Equal(new EchoEvent(EchoKind.Sentence, "Le chat dort"), ev);
    }

    [Fact]
    public void After_A_Jump_Into_A_Sentence_The_Sentence_End_Is_Marked_With_The_Typed_Words()
    {
        var e = new SpeechEcho { SpeakSentences = true };
        e.Seed("ta");                                    // caret after « ta » in « … une ta| »
        var ev = Feed(e, "pe.").Single();
        Assert.True(ev.AfterJump);
        Assert.Equal("tape", ev.Text);
        // the next sentence is typed whole
        Assert.False(Feed(e, " Il pleut.").Single().AfterJump);
    }

    [Fact]
    public void A_Jump_Then_Only_The_Sentence_End_Still_Asks_For_The_Document()
    {
        var e = new SpeechEcho { SpeakSentences = true };
        e.Reset();                                       // a jump whose read failed: the echo knows nothing
        var ev = Feed(e, ".").Single();
        Assert.True(ev.AfterJump);
        Assert.Equal("", ev.Text);
    }

    [Fact]
    public void A_Jump_To_A_Sentence_Start_Is_Not_An_Edit()
    {
        var e = new SpeechEcho { SpeakSentences = true };
        e.Seed("", atSentenceStart: true);
        Assert.False(Feed(e, "Il pleut.").Single().AfterJump);
        // without sentences on, the word echo is unchanged
        var w = new SpeechEcho { SpeakWords = true };
        w.Seed("ta");
        Assert.Equal(new EchoEvent(EchoKind.Word, "tape"), Feed(w, "pe.").Single());
    }

    [Fact]
    public void The_Document_Sentence_Is_Found_Before_The_Caret()
    {
        var w = TextWindow.Of("Il range sa tape. Puis il part.", 17, false, false);
        Assert.Equal("Il range sa tape.", SentenceDocRead.Find(w, "tape"));
        // the read came after a space and the next word were typed
        var w2 = TextWindow.Of("Il range sa tape. Puis", 22, false, false);
        Assert.Equal("Il range sa tape.", SentenceDocRead.Find(w2, "tape"));
        // AutoCorrect changed the typed word (« tres » -> « très »): the document's wording
        var w3 = TextWindow.Of("Il est très content.", 20, false, false);
        Assert.Equal("Il est très content.", SentenceDocRead.Find(w3, "tres content"));
        // Backspace went past the known text: the typed words begin inside a word (« pe » of « tape »)
        Assert.Equal("Il range sa tape.", SentenceDocRead.Find(w, "pe"));
        Assert.Null(SentenceDocRead.Find(w, "pa"));
        // nothing typed since the jump: the sentence that ends at the caret
        Assert.Equal("Il range sa tape.", SentenceDocRead.Find(w, ""));
    }

    [Fact]
    public void No_Document_Sentence_Means_The_Typed_Words()
    {
        // the sentence end is not there yet (the app had not handled the key)
        Assert.Null(SentenceDocRead.Find(TextWindow.Of("Il range sa tape", 16, false, false), "tape"));
        // other words at the end of the nearest sentence: not the one typed
        Assert.Null(SentenceDocRead.Find(TextWindow.Of("Il range sa table.", 18, false, false), "tape"));
        // the window cuts the sentence's start
        Assert.Null(SentenceDocRead.Find(TextWindow.Of("range sa tape.", 14, true, false), "tape"));
        // more typed than the sentence holds
        Assert.Null(SentenceDocRead.Find(TextWindow.Of("Oui.", 4, false, false), "non oui"));
    }

    [Fact]
    public void Same_Sentence_Ignores_Spaces_Case_And_Accents_Only()
    {
        Assert.True(SentenceDocRead.Same("Il range sa tape.", "Il  range sa tape ."));
        Assert.True(SentenceDocRead.Same("Il est tres content.", "Il est très content."));
        Assert.False(SentenceDocRead.Same("Il range sa tape.", "Il range sa table."));
    }

    [Fact]
    public void An_Unfinished_Caret_Sentence_Never_Gives_The_Previous_One()
    {
        // review: the period is not in the document yet; the sentence before must not be said instead
        var w = TextWindow.Of("Le chat dort. Le chien dort", 27, false, false);
        Assert.Null(SentenceDocRead.Find(w, "dort"));
        Assert.Null(SentenceDocRead.Find(w, "t"));          // a one-letter fragment (suffix of « dort »)
        Assert.Null(SentenceDocRead.Find(w, ""));           // nothing typed since the jump
        // the same document once the period is there: the caret's sentence
        var done = TextWindow.Of("Le chat dort. Le chien dort.", 28, false, false);
        Assert.Equal("Le chien dort.", SentenceDocRead.Find(done, "dort"));
        // text typed after the period (the next sentence's start): the finished one before it
        var next = TextWindow.Of("Le chat dort. Le chien dort. Il", 31, false, false);
        Assert.Equal("Le chien dort.", SentenceDocRead.Find(next, "dort"));
        // an edit in the middle of a text: the word typed after the period runs into the sentence that followed
        var mid = TextWindow.Of("Il range sa tape. Elle Puis il part.", 23, false, false);
        Assert.Equal("Il range sa tape.", SentenceDocRead.Find(mid, "pe"));
        // the period not there yet, text after the caret: still not the sentence before
        var early = TextWindow.Of("Le chat dort. Le chien dort Puis il part.", 27, false, false);
        Assert.Null(SentenceDocRead.Find(early, "dort"));
    }

    // ---------------------------------------------------------------- SentenceEchoGate (ordering, expiry, said)

    private static SentenceEchoGate.Deferred W(string t) => new(SpeechKind.Word, t, new EchoEvent(EchoKind.Word, t), (System.IntPtr)1);

    [Fact]
    public void Echoes_Wait_Behind_A_Pending_Sentence_Read_And_Come_After_It()
    {
        var g = new SentenceEchoGate();
        Assert.False(g.Defer(W("avant"), 0, out var none));      // nothing pending: said at once
        Assert.Empty(none);
        var (seq, now) = g.Begin("tape", (System.IntPtr)1, 100);
        Assert.Empty(now);
        Assert.True(g.Defer(W("Il"), 150, out _));                // a word typed right after the period
        Assert.True(g.Defer(new SentenceEchoGate.Deferred(SpeechKind.Word, "pleut", null, (System.IntPtr)1), 160, out _));   // an insertion
        var t = g.Take(seq);
        Assert.NotNull(t);
        Assert.Equal("tape", t!.Value.Sentence.Typed);
        Assert.Equal(new[] { "Il", "pleut" }, t.Value.Deferred.Select(d => d.Text));
        Assert.Null(g.Take(seq));                                  // once
        Assert.False(g.Defer(W("encore"), 200, out _));
    }

    [Fact]
    public void A_Retry_Is_Allowed_Once_And_A_Newer_Sentence_Gives_Up_The_Older()
    {
        var g = new SentenceEchoGate();
        var (s1, _) = g.Begin("tape", (System.IntPtr)1, 0);
        Assert.True(g.Retry(s1));
        Assert.False(g.Retry(s1));
        g.Defer(W("Il"), 10, out _);
        var (s2, now) = g.Begin("pleut", (System.IntPtr)1, 20);
        Assert.Equal(new[] { "tape", "Il" }, now.Select(d => d.Text));   // the older one's typed words, then its words
        Assert.Equal(SpeechKind.Sentence, now[0].Kind);
        Assert.Null(g.Take(s1));
        Assert.NotNull(g.Take(s2));
    }

    [Fact]
    public void An_Overdue_Read_Is_Given_Up_With_Its_Typed_Words_Then_What_Waited()
    {
        var g = new SentenceEchoGate();
        var (seq, _) = g.Begin("tape", (System.IntPtr)1, 1000);
        g.Defer(W("Il"), 1100, out _);
        Assert.Empty(g.Expire(1000 + SentenceEchoGate.ExpiryMs - 1));
        Assert.Equal(new[] { "tape", "Il" }, g.Expire(1000 + SentenceEchoGate.ExpiryMs).Select(d => d.Text));
        Assert.Null(g.Take(seq));                                   // its read comes back too late: nothing more
        // lazily, from the next echo
        var (s2, _) = g.Begin("", (System.IntPtr)1, 5000);
        Assert.False(g.Defer(W("x"), 5000 + SentenceEchoGate.ExpiryMs, out var before));
        Assert.Empty(before);                                       // nothing typed: no fallback text
        Assert.Null(g.Take(s2));
        // a password drop says nothing
        g.Begin("secret", (System.IntPtr)1, 9000);
        g.Defer(W("y"), 9001, out _);
        g.Drop();
        Assert.Empty(g.Expire(long.MaxValue / 2));
    }

    [Fact]
    public void The_Reread_Is_Skipped_Only_For_A_Sentence_Spoken_Or_Being_Spoken()
    {
        var fg = (System.IntPtr)1;
        var g = new SentenceEchoGate();
        Assert.False(g.AlreadySaid("Il range sa tape.", fg, 0));
        g.Saying("Il range sa tape.", fg, 0);
        Assert.True(g.AlreadySaid("Il range sa tape.", fg, 100));          // being spoken
        Assert.False(g.AlreadySaid("Il range sa tape.", fg, 100));         // once
        g.Saying("Il range sa tape.", fg, 0);
        g.Outcome("Il range sa tape.", SpeechOutcome.Cancelled);            // never heard
        Assert.False(g.AlreadySaid("Il range sa tape.", fg, 100));
        g.Saying("Il range sa tape.", fg, 0);
        g.Outcome("Il range sa tape.", SpeechOutcome.Done);
        Assert.False(g.AlreadySaid("Il range sa table.", fg, 100));        // changed since
        Assert.False(g.AlreadySaid("Il range sa tape.", (System.IntPtr)2, 100));   // another window
        Assert.True(g.AlreadySaid("Il range sa tape.", fg, SentenceEchoGate.SayingMs * 5));   // said: no time limit
        g.Saying("Oui.", fg, 0);                                             // no outcome ever (dropped silently)
        Assert.False(g.AlreadySaid("Oui.", fg, SentenceEchoGate.SayingMs));
    }
}
