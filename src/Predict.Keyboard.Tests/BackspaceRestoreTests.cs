using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Backspace back across a word boundary restores the previous word as the prefix (owner report, 0.6.0:
/// « Ma » ⌫ « o » predicted from « o », not « Mo »), in the tracker and in the speech echo.</summary>
public class BackspaceRestoreTests
{
    private static WordTracker Typed(string s)
    {
        var t = new WordTracker();
        foreach (var c in s) t.Type(c);
        return t;
    }

    private static BackspaceEffect Back(WordTracker t, int n)
    {
        var e = BackspaceEffect.InWord;
        for (int i = 0; i < n; i++) e = t.Backspace();
        return e;
    }

    [Fact]
    public void OwnerRun_MaCherie_BackToMa_ThenO_GivesMo()
    {
        var t = Typed("Ma chérie");
        Back(t, 5);                                                 // « hérie »
        Assert.Equal("c", t.Current);
        Assert.Equal(BackspaceEffect.InWord, t.Backspace());        // « c »: the caret is after « Ma  »
        Assert.Equal("", t.Current); Assert.Equal(new[] { "Ma" }, t.Context); Assert.True(t.AfterSeparator);
        Assert.Equal(BackspaceEffect.Crossed, t.Backspace());       // the space: « Ma » is the prefix again
        Assert.Equal("Ma", t.Current); Assert.Empty(t.Context); Assert.False(t.AfterSeparator);
        t.Backspace();
        Assert.Equal("M", t.Current);
        t.Type('o');
        Assert.Equal("Mo", t.Current); Assert.Empty(t.Context);
    }

    [Fact]
    public void SeveralWordsBack()
    {
        var t = Typed("le petit chat ");
        Back(t, 6);                                                 // space, « chat », space
        Assert.Equal("petit", t.Current); Assert.Equal(new[] { "le" }, t.Context);
        Back(t, 6);                                                 // « petit », space
        Assert.Equal("le", t.Current); Assert.Empty(t.Context);
        t.Type('s');
        Assert.Equal("les", t.Current);
    }

    [Fact]
    public void PunctuationSeparator()
    {
        var t = Typed("Ma, c");
        t.Backspace();
        Assert.True(t.AfterSeparator); Assert.Equal(' ', t.LastSeparator); Assert.Equal(new[] { "Ma" }, t.Context);
        t.Backspace();                                              // the space: still after « Ma, »
        Assert.True(t.AfterSeparator); Assert.Equal(',', t.LastSeparator); Assert.Equal(new[] { "Ma" }, t.Context);
        Assert.Equal(BackspaceEffect.Crossed, t.Backspace());       // the comma
        Assert.Equal("Ma", t.Current); Assert.False(t.AfterSeparator);
    }

    [Fact]
    public void Elision_TypedOrInserted()
    {
        var t = Typed("l'ami");
        Back(t, 3);
        Assert.True(t.AfterSeparator); Assert.Equal('\'', t.LastSeparator); Assert.Equal(new[] { "l" }, t.Context);
        t.Backspace();
        Assert.Equal("l", t.Current); Assert.Empty(t.Context);

        var u = Typed("je vois l");
        u.Accept("l'", false);                                      // F1 on the elision: no space
        foreach (var c in "ami") u.Type(c);
        Back(u, 3);
        Assert.Equal(new[] { "je", "vois", "l" }, u.Context); Assert.Equal('\'', u.LastSeparator); Assert.True(u.AfterSeparator);
        u.Backspace();
        Assert.Equal("l", u.Current); Assert.Equal(new[] { "je", "vois" }, u.Context);
    }

    [Fact]
    public void InsertedWordWithAutoSpace()
    {
        var t = Typed("la mai");
        t.Accept("maison", true);                                   // F1: « la maison␣ »
        Assert.Equal(BackspaceEffect.Crossed, t.Backspace());       // our space
        Assert.Equal("maison", t.Current); Assert.Equal(new[] { "la" }, t.Context);
        t.Backspace();
        Assert.Equal("maiso", t.Current);
        t.Type('n'); t.Type('s'); t.Type(' ');
        Assert.Equal(new[] { "la", "maisons" }, t.Context);
    }

    [Fact]
    public void SentenceStart_KeepsCase_AndCapitalisation()
    {
        var t = Typed("Fin. Ma c");
        t.Backspace();
        Assert.Equal("", t.Current); Assert.Equal(new[] { "Ma" }, t.Context); Assert.False(t.AtSentenceStart);
        t.Backspace();
        Assert.Equal("Ma", t.Current); Assert.Empty(t.Context); Assert.True(t.AtSentenceStart);
        Back(t, 3);                                                 // « Ma », the space: right after « Fin. »
        Assert.Equal("", t.Current); Assert.Equal('.', t.LastSeparator); Assert.True(t.AtSentenceStart);
        Assert.Equal(BackspaceEffect.Lost, t.Backspace());          // keys only: Word may have rewritten « . » (« ... » → « … »)
        Assert.Equal("", t.Current); Assert.Empty(t.Context);

        var r = new WordTracker();                                  // where reads work, the typed full stop is crossed
        r.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        foreach (var c in "Fin. Ma") r.Type(c);
        Assert.Equal(BackspaceEffect.Crossed, Back(r, 4));
        Assert.Equal("Fin", r.Current); Assert.True(r.AtSentenceStart);
    }

    [Fact]
    public void MultipleSpaces()
    {
        var t = Typed("Ma   c");
        t.Backspace();
        for (int i = 0; i < 2; i++)
        {
            t.Backspace();
            Assert.Equal("", t.Current); Assert.True(t.AfterSeparator); Assert.Equal(new[] { "Ma" }, t.Context);
        }
        t.Backspace();
        Assert.Equal("Ma", t.Current);
    }

    [Fact]
    public void UnknownHistory_AfterJump_IsLost()
    {
        var t = Typed("le chat");
        t.Reset();                                                  // a click: what is before the caret is unknown
        t.Type('o');
        t.Backspace();
        Assert.Equal("", t.Current); Assert.False(t.AfterSeparator);
        Assert.Equal(BackspaceEffect.Lost, t.Backspace());
        Assert.Equal("", t.Current); Assert.Empty(t.Context); Assert.Equal("keys", t.Origin);
        var d = Typed("12 ab");                                     // a digit resets: nothing known before « ab »
        Back(d, 3);
        Assert.Equal(BackspaceEffect.Lost, d.Backspace());
    }

    [Fact]
    public void AfterRead_TheRebuiltPartIsNeverDerived()
    {
        var t = new WordTracker();
        t.Seed("", new[] { "le", "chat" }, 1, "", '\0', "uia");   // the separator was rebuilt (« chat, » or « chat  »?)
        Assert.Equal(BackspaceEffect.Lost, t.Backspace());
        Assert.Equal("", t.Current); Assert.Empty(t.Context);

        var u = new WordTracker();
        u.Seed("mai", new[] { "la" }, 0, "", '\0', "win32", atSentenceStart: true);
        foreach (var c in " est") u.Type(c);                         // typed after the read: exact
        Assert.Equal(BackspaceEffect.InWord, u.Backspace());
        Back(u, 2);
        Assert.Equal(BackspaceEffect.Crossed, u.Backspace());
        Assert.Equal("mai", u.Current); Assert.Equal(new[] { "la" }, u.Context);
        Assert.Equal(BackspaceEffect.InWord, Back(u, 3));            // the read's own prefix: still exact
        Assert.True(u.AfterSeparator);
        Assert.Equal(BackspaceEffect.Lost, u.Backspace());           // the separator the read did not give
    }

    /// <summary>Review case: « Bonjour, ma| » read (prefix « ma », separators rebuilt as one space); holding Backspace
    /// makes every read stale. Restoring « Bonjour » from the rebuilt text would give a prefix of the wrong length (F1
    /// then deletes one char too many: « mmaison »); the tracker resets instead.</summary>
    [Fact]
    public void SeededDrift_HoldingBackspace_NeverRestoresFromTheRebuiltText()
    {
        var t = new WordTracker();
        t.Seed("ma", new[] { "Bonjour" }, 0, "", '\0', "uia");
        Back(t, 2);
        Assert.Equal("", t.Current); Assert.Equal(new[] { "Bonjour" }, t.Context);
        for (int i = 0; i < 4; i++) { Assert.Equal(BackspaceEffect.Lost, t.Backspace()); Assert.Equal("", t.Current); }
    }

    [Theory]
    [InlineData('.')] [InlineData('!')] [InlineData('?')] [InlineData('…')] [InlineData(';')] [InlineData(':')]
    [InlineData('"')] [InlineData('«')] [InlineData('»')] [InlineData('\u00A0')] [InlineData('\u202F')]
    public void KeysOnly_RewriteProneSeparator_IsABarrier(char c)
    {
        var t = Typed("oui" + c);
        Assert.Equal(BackspaceEffect.Lost, t.Backspace());
        Assert.Equal("", t.Current); Assert.Equal("keys", t.Origin);
        var r = new WordTracker();
        r.Seed("", Array.Empty<string>(), 0, "", '\0', "win32");
        foreach (var ch in "oui" + c) r.Type(ch);
        Assert.Equal(BackspaceEffect.Crossed, r.Backspace());         // a window that can be read: crossed (and checked)
        Assert.Equal("oui", r.Current);
    }

    [Theory]
    [InlineData(' ')] [InlineData(',')] [InlineData('\'')] [InlineData('’')] [InlineData('-')] [InlineData('\t')]
    public void KeysOnly_PlainSeparator_IsCrossed(char c)
    {
        var t = Typed("oui" + c);
        Assert.Equal(BackspaceEffect.Crossed, t.Backspace());
        Assert.Equal("oui", t.Current);
    }

    [Theory]
    [InlineData("keys")] [InlineData("uia")]
    public void Enter_IsABarrier(string origin)
    {
        var t = new WordTracker();
        if (origin != "keys") t.Seed("", Array.Empty<string>(), 0, "", '\0', origin);
        foreach (var c in "nom\nx") t.Type(c);
        t.Backspace();
        Assert.Equal(BackspaceEffect.Lost, t.Backspace());           // the focus may have moved (two-step logins)
        Assert.Equal("", t.Current); Assert.Empty(t.Context);
        var r = new WordTracker();
        foreach (var c in "nom\rx") r.Type(c);
        Back(r, 1);
        Assert.Equal(BackspaceEffect.Lost, r.Backspace());
    }

    [Fact]
    public void LastLetterOfAWordBeforeASuffix_NoNextWordPrediction()
    {
        var t = new WordTracker();
        t.Seed("m", new[] { "la" }, 0, "aison", ' ', "uia");      // « la m|aison »
        t.Backspace();
        Assert.Equal("", t.Current); Assert.Equal("aison", t.Suffix); Assert.False(t.AfterSeparator);
    }

    /// <summary>Typing a text (with F1 insertions, sometimes after a long text) then Backspaces = typing the shorter
    /// text, until a Backspace is Lost, which must be a barrier (5 000 random cases, no digits: a digit resets).</summary>
    [Fact]
    public void Equivalence_BackspaceVsTypingTheShorterText()
    {
        const string alphabet = "abcéèàçœ AÉŒ'’-,\t\u202F.!?…;:\"«»\n\r";
        string[] words = { "maison", "l'", "Est", "qu’", "œuvre" };
        var rnd = new Random(7);
        int lost = 0, compared = 0;
        for (int n = 0; n < 5000; n++)
        {
            var t = new WordTracker();
            var doc = new System.Text.StringBuilder();
            void Put(char c) { t.Type(c); doc.Append(c); }
            if (rnd.Next(10) == 0) foreach (var c in string.Concat(Enumerable.Repeat("un mot ", 50))) Put(c);   // > MaxText
            int start = doc.Length, steps = rnd.Next(1, 41);
            for (int i = 0; i < steps; i++)
            {
                if (rnd.Next(7) > 0) { Put(alphabet[rnd.Next(alphabet.Length)]); continue; }
                var w = words[rnd.Next(words.Length)];                // F1: replaces the prefix, our space unless an elision
                bool spaced = !Predict.Core.Elision.IsElided(w);
                doc.Length -= t.Current.Length;
                t.Accept(w, spaced);
                doc.Append(w); if (spaced) doc.Append(' ');
            }
            if (doc.Length <= start) continue;
            int k = rnd.Next(1, doc.Length - start + 1);
            for (int i = 0; i < k; i++)
            {
                char gone = doc[^1];
                bool wasInWord = t.Current.Length > 0;
                var e = t.Backspace();
                doc.Length--;
                string at = $"[{Show(doc.ToString(start, doc.Length - start))}|{Show(gone.ToString())}] step {i}";
                if (e == BackspaceEffect.Lost)
                {
                    Assert.True(!wasInWord && WordTracker.IsBarrier(gone, true), $"{at}: lost on a plain char");
                    lost++;
                    break;
                }
                var f = new WordTracker();
                foreach (var c in doc.ToString()) f.Type(c);
                Assert.True(t.Current == f.Current, $"{at} current '{t.Current}' vs '{f.Current}'");
                Assert.True(t.Context.SequenceEqual(f.Context), $"{at} context [{string.Join(" ", t.Context)}] vs [{string.Join(" ", f.Context)}]");
                Assert.True(t.AfterSeparator == f.AfterSeparator, $"{at} after-separator");
                Assert.True(t.AtSentenceStart == f.AtSentenceStart, $"{at} sentence start");
                Assert.True(t.LastSeparator == f.LastSeparator, $"{at} last separator");
                compared++;
            }
        }
        Assert.True(lost > 100 && compared > 10000, $"lost {lost}, compared {compared}");
    }

    private static string Show(string s) => s.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    [Fact]
    public void LongText_IsTrimmed_AndStillRestores()
    {
        var t = Typed(string.Concat(Enumerable.Repeat("un mot ", 200)) + "fin");
        Back(t, 4);
        Assert.Equal("mot", t.Current); Assert.Equal(new[] { "mot", "un" }, t.Context.TakeLast(2));
        Back(t, WordTracker.MaxText * 2);                            // far beyond what is kept: lost, no exception
        Assert.Equal("", t.Current); Assert.Empty(t.Context);
    }

    // ---- speech echo -------------------------------------------------------------------------------------------

    private static List<EchoEvent> Feed(SpeechEcho e, string text)
    {
        var l = new List<EchoEvent>();
        foreach (var c in text) if (e.Type(c) is { } ev) l.Add(ev);
        return l;
    }

    private static void Back(SpeechEcho e, int n) { for (int i = 0; i < n; i++) e.Backspace(); }

    [Fact]
    public void Echo_OwnerRun_SaysMo()
    {
        var e = new SpeechEcho { SpeakWords = true };
        Feed(e, "Ma c");
        Back(e, 3);                                                 // « c », the space, « a »
        Assert.Equal(new[] { "Mo" }, Feed(e, "o ").Select(x => x.Text));
    }

    [Fact]
    public void Echo_SeveralWordsBack()
    {
        var e = new SpeechEcho { SpeakWords = true };
        Feed(e, "le petit chat ");
        Back(e, 12);                                                // back to « le| »
        Assert.Equal(new[] { "les" }, Feed(e, "s ").Select(x => x.Text));
        Assert.False(e.IsBlank);
    }

    [Fact]
    public void Echo_LiaisonGrouping_AfterRestore()
    {
        var e = new SpeechEcho { SpeakWords = true };
        Feed(e, "les x");
        Back(e, 3);                                                 // « x », the space, « s »: « le| »
        var l = Feed(e, "s amis ");
        Assert.Equal(new[] { "les", "les amis" }, l.Select(x => x.Text));
        Assert.Equal("les", l[1].Covers);
        var f = new SpeechEcho { SpeakWords = true };
        Feed(f, "les, x");
        Back(f, 3);                                                 // « x », the space, the comma: « les| »
        Assert.Equal(new[] { "les", "les amis" }, Feed(f, " amis ").Select(x => x.Text));
    }

    [Fact]
    public void Echo_InsertedWord_NotRepeatedAfterRestore()
    {
        var e = new SpeechEcho { SpeakWords = true };
        Feed(e, "la mai");
        e.Inserted("maison", spaced: true, spoken: true);
        Feed(e, "x");
        Back(e, 2);                                                 // « x », our space: « maison » again, already said
        Assert.Empty(Feed(e, " "));
        Assert.True(new SpeechEcho().IsBlank);
    }
}
