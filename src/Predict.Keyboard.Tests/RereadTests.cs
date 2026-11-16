using System;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>D23 / plan 06: the edit session (mirror of the read window), the landing classifier and the re-read
/// decision, driven with read windows as the reader returns them ('|' marks the caret).</summary>
public class RereadTests
{
    private static readonly IntPtr Fg = (IntPtr)0x1234;

    private static TextWindow W(string withCaret, bool truncStart = false, bool truncEnd = false, int @base = -1)
    {
        int i = withCaret.IndexOf('|');
        return TextWindow.Of(withCaret.Remove(i, 1), i, truncStart, truncEnd, @base);
    }

    private static EditSession Begin(string withCaret, int @base = -1) => EditSession.Begin(W(withCaret, @base: @base), Fg);

    private static void Type(EditSession s, string text) { foreach (var c in text) s.Type(c); }

    /// <summary>The caret leaves (the hook's Jump), then the read at the new caret classifies the landing.</summary>
    private static (Landing Where, RereadDecision D) Land(EditSession s, TextWindow? read, bool sameFg = true)
    {
        s.Leave();
        var st = s.Snapshot();
        var where = EditClassifier.Classify(st, read, sameFg);
        return (where, EditClassifier.Decide(st, where));
    }

    private static string Text(EditSession s) => s.Snapshot().Text;

    [Fact]
    public void Letter_Inserted_Mid_Word_Then_Click_In_The_Same_Sentence_Says_The_Word()
    {
        var s = Begin("Le ch|t dort bien.");
        s.Type('a');
        Assert.Equal("Le chat dort bien.", Text(s));
        var (where, d) = Land(s, W("Le chat dort bi|en."));
        Assert.Equal(Landing.SameSentence, where);
        Assert.Equal(new RereadDecision(RereadKind.Word, "chat", "word"), d);
    }

    [Fact]
    public void Word_Inserted_Mid_Sentence_Then_Click_In_The_Next_Paragraph_Says_The_Sentence_Only()
    {
        var s = Begin("Le |chat dort.\nIl pleut.");
        Type(s, "gros ");
        var (where, d) = Land(s, W("Le gros chat dort.\nIl pl|eut."));
        Assert.Equal(Landing.Elsewhere, where);
        Assert.Equal(RereadKind.Sentence, d.Kind);
        Assert.Equal("Le gros chat dort.", d.Text);   // the sentence, never the word too
    }

    [Fact]
    public void Arrow_Inside_The_Word_Says_Nothing_Then_The_Word_When_It_Leaves()
    {
        var s = Begin("Le cha|t dort.");
        s.Type('a');                                       // « chaat »
        var (where, d) = Land(s, W("Le ch|aat dort."));   // ← ← inside the word
        Assert.Equal(Landing.SameWord, where);
        Assert.Equal(RereadKind.None, d.Kind);
        // the session continues from the read, keeping the word-level edit
        var next = EditSession.Continue(s.Snapshot(), W("Le ch|aat dort."), sameWord: true);
        (where, d) = Land(next, W("Le chaat do|rt."));
        Assert.Equal(Landing.SameSentence, where);
        Assert.Equal("chaat", d.Text);
    }

    [Fact]
    public void Same_Sentence_After_A_Reread_Then_Leaving_The_Sentence_Says_The_Sentence()
    {
        var s = Begin("Le ch|t dort.\nFin.");
        s.Type('a');
        var (_, d) = Land(s, W("Le chat do|rt.\nFin."));
        Assert.Equal("chat", d.Text);
        var next = EditSession.Continue(s.Snapshot(), W("Le chat do|rt.\nFin."), sameWord: false);
        (_, d) = Land(next, W("Le chat dort.\nF|in."));
        Assert.Equal(new RereadDecision(RereadKind.Sentence, "Le chat dort.", "sentence"), d);
    }

    [Fact]
    public void Tab_To_Another_Field_Or_A_Window_Switch_Says_The_Sentence()
    {
        var s = Begin("Il fait |beau.");
        Type(s, "très ");
        var (where, d) = Land(s, W("|"));                   // the other field is empty
        Assert.Equal(Landing.Elsewhere, where);
        Assert.Equal("Il fait très beau.", d.Text);
        var s2 = Begin("Il fait |beau.");
        Type(s2, "très ");
        (where, d) = Land(s2, null, sameFg: false);         // another window, whatever its read gave
        Assert.Equal(Landing.Elsewhere, where);
        Assert.Equal("Il fait très beau.", d.Text);
    }

    [Fact]
    public void Typing_At_The_End_Of_The_Text_Is_Not_Reread()
    {
        var s = Begin("Le chat|");
        Type(s, " dort bien");
        var (_, d) = Land(s, W("|Le chat dort bien"));
        Assert.Equal(RereadDecision.Skip("not inside the text"), d);
        // before the final period too: the end of the sentence, the echo covers it
        Assert.False(Begin("Le chat dort|.").Snapshot().Middle);
        Assert.True(Begin("Le chat |dort.").Snapshot().Middle);
        Assert.True(Begin("Le chat do|rt").Snapshot().Middle);   // inside a word
    }

    [Fact]
    public void Fixing_The_Last_Word_Of_A_Sentence_That_Is_Not_The_Last_Is_Middle()
    {
        Assert.True(Begin("Le chat dort|. Il pleut.").Snapshot().Middle);
        Assert.True(EditSession.Begin(W("Le chat dort|.", truncEnd: true), Fg).Snapshot().Middle);   // the document goes on
        Assert.False(Begin("Le chat dort.| Il pleut.").Snapshot().Middle);                         // after the period: a new sentence
        Assert.False(Begin("Le chat dort|.  ").Snapshot().Middle);                                 // only spaces follow
        var s = Begin("Le chat dor|. Il pleut.");
        s.Type('t');
        Assert.Equal("Le chat dort.", Land(s, W("Le chat dort. Il pl|eut.")).D.Text);
    }

    [Fact]
    public void Partial_Sentence_Says_Only_The_Word()
    {
        // the window cut the sentence on both sides (no sentence end seen)
        var s = EditSession.Begin(W("ment le ch|t dort sur le", truncStart: true, truncEnd: true), Fg);
        Assert.True(s.Snapshot().View.Partial);
        s.Type('a');
        var (where, d) = Land(s, null, sameFg: false);
        Assert.Equal(Landing.Elsewhere, where);
        Assert.Equal(new RereadDecision(RereadKind.Word, "chat", "word (partial sentence)"), d);
        // a sentence end inside the window makes the sentence whole again
        Assert.False(EditSession.Begin(W("fin. Le ch|t dort.", truncStart: true, truncEnd: true), Fg).Snapshot().View.Partial);
    }

    [Fact]
    public void Read_Failed_Says_Nothing()
    {
        var s = Begin("Le ch|t dort.");
        s.Type('a');
        var (where, d) = Land(s, null);
        Assert.Equal(Landing.Unknown, where);
        Assert.Equal(RereadKind.None, d.Kind);
    }

    [Fact]
    public void Inserted_Suggestion_Is_The_Modified_Word_And_Not_Repeated_When_Already_Said()
    {
        var s = Begin("Le |chat dort.\nFin.");
        Type(s, "gr");
        s.Insert(0, 2, "gros ");                            // Backspace × 2, then « gros␣ » (Inserter.Replace)
        Assert.Equal("Le gros chat dort.\nFin.", Text(s));
        Assert.Equal("gros", Peek(s, W("Le gros chat d|ort.\nFin.")).D.Text);
        // the « inserted » echo said it: the move inside the sentence says nothing, leaving it says the sentence
        s.MarkWordSpoken();
        var (_, d) = Peek(s, W("Le gros chat d|ort.\nFin."));
        Assert.Equal(RereadDecision.Skip("word already spoken"), d);
        (_, d) = Land(s, W("Le gros chat dort.\nF|in."));
        Assert.Equal("Le gros chat dort.", d.Text);
    }

    [Fact]
    public void Insert_Replaces_The_Whole_Word_Around_The_Caret()
    {
        var s = Begin("Le ma|son est belle.");
        s.Insert(3, 2, "maison ");                          // Delete × 3 (« son »), Backspace × 2 (« ma »)
        Assert.Equal("Le maison  est belle.", Text(s));
        Assert.Equal("maison", Land(s, W("Le maison  est be|lle.")).D.Text);
    }

    [Fact]
    public void Word_Echo_Already_Said_The_Word_Typed_In_The_Middle()
    {
        var s = Begin("Le |chat dort.");
        Type(s, "gros ");
        s.MarkWordSpoken();                                 // the word echo said « gros » at the space
        var (_, d) = Land(s, W("Le gros chat d|ort."));
        Assert.Equal(RereadKind.None, d.Kind);
    }

    [Fact]
    public void Backspace_And_Delete_Inside_A_Word()
    {
        var s = Begin("Le chaa|t dort.");
        s.Backspace();
        Assert.Equal("Le chat dort.", Text(s));
        Assert.Equal("chat", Land(s, W("Le chat do|rt.")).D.Text);

        var d = Begin("Le |xchat dort.");
        d.Delete();
        Assert.Equal("Le chat dort.", Text(d));
        Assert.Equal("chat", Land(d, W("Le chat do|rt.")).D.Text);
    }

    [Fact]
    public void A_Whole_Word_Deleted_Has_No_Word_But_Changes_The_Sentence()
    {
        var s = Begin("Le gros |chat dort.\nFin.");
        for (int i = 0; i < 5; i++) s.Backspace();
        Assert.Equal("Le chat dort.\nFin.", Text(s));
        Assert.Equal(RereadDecision.Skip("no word left"), Peek(s, W("Le chat do|rt.\nFin.")).D);
        Assert.Equal("Le chat dort.", Land(s, W("Le chat dort.\nF|in.")).D.Text);

        var f = Begin("Le |gros chat dort.");
        for (int i = 0; i < 5; i++) f.Delete();
        Assert.Equal("Le chat dort.", Text(f));
        Assert.Equal(RereadKind.None, Land(f, W("Le chat do|rt.")).D.Kind);
    }

    [Fact]
    public void Unchanged_Text_Says_Nothing()
    {
        var s = Begin("Le ch|at dort.\nFin.");
        s.Type('x'); s.Backspace();                         // typed then removed
        Assert.Equal(RereadDecision.Skip("word unchanged"), Peek(s, W("Le chat do|rt.\nFin.")).D);
        Assert.Equal(RereadDecision.Skip("sentence unchanged"), Land(s, W("Le chat dort.\nF|in.")).D);
    }

    [Fact]
    public void Keys_After_The_Leave_Are_Not_Placed_In_The_Mirror()
    {
        var s = Begin("Le ch|t dort.");
        s.Leave();
        s.Type('a');
        var st = s.Snapshot();
        Assert.True(st.TypedWhileLeft);
        Assert.Equal("Le cht dort.", st.Text);
    }

    [Fact]
    public void Deleting_Beyond_The_Read_Window_Makes_The_Session_Untrusted()
    {
        var s = EditSession.Begin(W("le chat|", truncEnd: true), Fg);
        s.Delete();
        var (_, d) = Land(s, W("le c|hat"));
        Assert.Equal(RereadDecision.Skip("unseen change"), d);
    }

    [Fact]
    public void Win32_Offsets_Tell_Identical_Sentences_Apart()
    {
        var st = Begin("Ou|i. Oui.", @base: 10).Snapshot();
        Assert.Equal(Landing.Elsewhere, EditClassifier.Classify(st, W("Oui. Ou|i.", @base: 10), true));
        Assert.NotEqual(Landing.Elsewhere, EditClassifier.Classify(st, W("O|ui. Oui.", @base: 10), true));
        // UI Automation has no offsets: an identical sentence is taken for the same one (accepted limit)
        var u = Begin("Ou|i. Oui.").Snapshot();
        Assert.NotEqual(Landing.Elsewhere, EditClassifier.Classify(u, W("Oui. Ou|i."), true));
    }

    [Fact]
    public void Enter_In_A_Win32_Edit_Is_Cr_Lf()
    {
        var s = Begin("Le chat| dort.", @base: 0);
        s.Type('\n');
        Assert.Equal("Le chat\r\n dort.", Text(s));
        s.Backspace();                                      // one Backspace removes both, as in Notepad
        Assert.Equal("Le chat dort.", Text(s));
        var u = Begin("Le chat| dort.");
        u.Type('\n');
        Assert.Equal("Le chat\n dort.", Text(u));
    }

    [Fact]
    public void Spaces_Changed_By_The_App_Still_Match_The_Same_Sentence()
    {
        // Word may put its own no-break space before « ! »: the key ignores spaces
        var s = Begin("Il fait |beau!");
        Type(s, "très ");
        var (where, d) = Land(s, W("Il fait très beau !|"));
        Assert.Equal(Landing.SameSentence, where);
        Assert.Equal("très", d.Text);
    }

    [Fact]
    public void Window_Control_Characters_End_A_Sentence()
    {
        var w = TextWindow.Of("cellule\aautre", 2, false, false);
        Assert.Equal("cellule\nautre", w.Text);
        var a = new TextAnalysis(w.Text, w.Caret, false, false);
        Assert.Equal("cellule", a.Sentence);
    }

    [Fact]
    public void Caret_In_The_Spaces_After_A_Sentence_Belongs_To_It()
    {
        Assert.Equal("Il pleut.", new TextAnalysis("Il pleut.  Oui", 10, false, false).Sentence);
        Assert.Equal("Oui", new TextAnalysis("Il pleut. Oui", 10, false, false).Sentence);
        Assert.False(new TextAnalysis("\n\n", 1, false, false).HasSentence);
    }

    [Fact]
    public void Reread_Drops_Pending_Words_Like_A_Sentence()
    {
        var q = new SpeechQueue();
        q.Enqueue(new SpeechItem("le", SpeechKind.Word));
        q.Enqueue(new SpeechItem("chat", SpeechKind.Word));
        Assert.False(q.Enqueue(new SpeechItem("Le gros chat dort.", SpeechKind.Reread)));   // never interrupts
        Assert.Equal(new[] { SpeechKind.Reread }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(q.Pending, p => p.Kind)));
    }

    // ---------------------------------------------------------------- review fixes: flow, interleavings, focus

    private static ReadFacts Read(TextWindow? w, bool latest = true, bool password = false, bool del = false,
                                  long version = 0, bool fgOther = false) =>
        new(w != null && !password, password, latest, del, fgOther ? (IntPtr)0x9999 : Fg, w, version);

    /// <summary>The edit probe answered NotPassword for everything typed so far (what the controller waits for).</summary>
    private static void Prove(EditSession s)
    {
        if (!s.NeedsProbe) return;
        s.StartProbe(99);
        s.ProbeAnswered(99, ProbeStatus.NotPassword);
    }

    [Fact]
    public void Flow_Edits_Not_Covered_By_A_Not_Password_Probe_Are_Never_Spoken()
    {
        var s = Begin("Le ch|t dort bien.");
        s.Type('a');                                          // no probe answer: the field was never proven
        s.Leave();
        var st = s.Snapshot();
        Assert.False(st.Proven);
        var p = RereadFlow.Plan(st, Read(W("Le chat dort bi|en.")));
        Assert.Equal(RereadKind.Word, p.Decision.Kind);
        var (session, speak, why) = RereadFlow.Commit(s, s, st, p, true, 0);
        Assert.False(speak);
        Assert.Null(session);
        Assert.Equal("field not proven", why);

        var t = Begin("Le ch|t dort bien.");
        t.Type('a');
        t.StartProbe(1);
        t.Type('b');                                          // typed while the probe ran: not covered by it
        Assert.True(t.ProbeAnswered(1, ProbeStatus.NotPassword));   // covers 'a' only, asks for another probe
        Assert.False(t.Snapshot().Proven);
        Assert.True(t.NeedsProbe);                            // the controller asks again for 'b'
        t.StartProbe(2);
        t.ProbeAnswered(2, ProbeStatus.NotPassword);
        Assert.True(t.Snapshot().Proven);
    }

    [Fact]
    public void Flow_An_Edit_Probe_That_Finds_A_Password_Or_Nothing_Makes_The_Session_Untrusted()
    {
        foreach (var st in new[] { ProbeStatus.Password, ProbeStatus.Unavailable })
        {
            var s = Begin("Le |chat dort.");
            foreach (var c in "zqxwvk") s.Type(c);            // focus moved silently into a password field: typed there
            s.StartProbe(7);
            s.ProbeAnswered(7, st);
            Assert.True(s.Snapshot().Dirty);
            s.Leave();
            var snap = s.Snapshot();
            var p = RereadFlow.Plan(snap, Read(W("Il pl|eut."), fgOther: true));
            Assert.Equal(RereadDecision.Skip("unseen change"), p.Decision);
            Assert.False(RereadFlow.Commit(s, s, snap, p, true, 0).Speak);
        }
    }

    // ---------------------------------------------------------------- owner Word run 2: late reads, AutoCorrect

    [Fact]
    public void A_Late_Read_Still_Serves_The_Session_When_Nothing_Moved()
    {
        IntPtr fg = (IntPtr)1, other = (IntPtr)2;
        Assert.True(KeyboardPolicy.UsableLateForReread(true, true, 158, 7, 7, fg, fg));     // the long Word paragraph
        Assert.False(KeyboardPolicy.UsableLateForReread(true, true, 401, 7, 7, fg, fg));    // over the re-read budget
        Assert.False(KeyboardPolicy.UsableLateForReread(true, true, 158, 7, 8, fg, fg));    // a key / jump since the request
        Assert.False(KeyboardPolicy.UsableLateForReread(true, true, 158, 7, 7, fg, other)); // another window in front
        Assert.False(KeyboardPolicy.UsableLateForReread(true, false, 158, 7, 7, fg, fg));   // no text read (password, selection…)
        Assert.False(KeyboardPolicy.UsableLateForReread(false, true, 158, 7, 7, fg, fg));   // not a timeout
        Assert.False(KeyboardPolicy.ShouldApply(7, 7, fg, fg, 158));                        // the tracker keeps its 150 ms rule
    }

    [Theory]
    [InlineData("tres", "très", true)]
    [InlineData("ca", "ça", true)]
    [InlineData("maison", "maisons", true)]     // one letter, 4+ letters
    [InlineData("chat", "chien", false)]
    [InlineData("le", "la", false)]             // short words: accents only
    [InlineData(".", "!", false)]
    [InlineData("dort", "dort", true)]
    public void Words_Compare_Up_To_AutoCorrect(string a, string b, bool close) => Assert.Equal(close, DocText.Close(a, b));

    [Fact]
    public void AutoCorrect_Keeps_The_Same_Sentence_And_The_Document_Word_Is_Said()
    {
        // the owner's WR6: « tres » typed, Word wrote « très » and a no-break space before « ! »
        var s = Begin("Il fait |beau!\nFin.");
        Type(s, "tres ");
        Prove(s);
        s.Leave();
        var st = s.Snapshot();
        var read = W("Il fait très beau\u00A0!|\nFin.");
        var where = EditClassifier.Classify(st, read, true);
        Assert.Equal(Landing.SameSentence, where);
        var d = EditClassifier.FromDocument(EditClassifier.Decide(st, where), st, where, read, true);
        Assert.Equal("très", d.Text);
    }

    [Fact]
    public void Leaving_The_Sentence_Says_The_Document_Sentence_When_The_Read_Shows_It()
    {
        var s = Begin("Il dit que |tout va bien.\nFin.");
        Type(s, "ca ");
        Prove(s);
        s.Leave();
        var st = s.Snapshot();
        var read = W("Il dit que ça tout va bien.\nF|in.");     // the read window still holds the edited sentence
        var where = EditClassifier.Classify(st, read, true);
        Assert.Equal(Landing.Elsewhere, where);
        var d = EditClassifier.FromDocument(EditClassifier.Decide(st, where), st, where, read, true);
        Assert.Equal("Il dit que ça tout va bien.", d.Text);
        // not shown by the read, or another window: the mirror's text, as before
        Assert.Equal("Il dit que ca tout va bien.", EditClassifier.FromDocument(EditClassifier.Decide(st, where), st, where, W("F|in."), true).Text);
        Assert.Equal("Il dit que ca tout va bien.", EditClassifier.FromDocument(EditClassifier.Decide(st, Landing.Elsewhere), st, Landing.Elsewhere, read, false).Text);
    }

    [Fact]
    public void A_Sentence_One_Letter_Away_Is_Another_Sentence()
    {
        // review: « Il mnge. Il range. » — « mange » fixed, then a click into « Il range. »: the edited sentence is said
        var s = Begin("Il m|nge. Il range.");
        s.Type('a');
        Prove(s);
        s.Leave();
        var st = s.Snapshot();
        var read = W("Il mange. Il ra|nge.");
        var where = EditClassifier.Classify(st, read, true);
        Assert.Equal(Landing.Elsewhere, where);
        var d = EditClassifier.FromDocument(EditClassifier.Decide(st, where), st, where, read, true);
        Assert.Equal(new RereadDecision(RereadKind.Sentence, "Il mange.", "sentence"), d);
    }

    [Fact]
    public void Accents_And_Case_Still_Match_A_Sentence()
    {
        Assert.True(DocText.SameWords(Tokens("il fait tres beau"), Tokens("Il fait très beau"), loose: false));
        Assert.True(DocText.SameWords(Tokens("il dit que ca va"), Tokens("il dit que ça va"), loose: false));
        Assert.False(DocText.SameWords(Tokens("il mange"), Tokens("il range"), loose: false));
        Assert.True(DocText.SameWords(Tokens("il mange"), Tokens("il range"), loose: true));   // only once the place is known
    }

    [Fact]
    public void A_Near_Copy_Sentence_In_The_Read_Is_Not_Taken_Without_Offsets()
    {
        // the edited sentence is not in the read window; a sentence one letter away is: keep the mirror's text
        var s = Begin("Le chat |dort.\nFin.");
        Type(s, "gros ");
        Prove(s);
        s.Leave();
        var st = s.Snapshot();
        var read = W("Le chat gras dort.\nF|in.");   // another sentence (UIA: no offsets)
        var d = EditClassifier.FromDocument(EditClassifier.Decide(st, Landing.Elsewhere), st, Landing.Elsewhere, read, true);
        Assert.Equal("Le chat gros dort.", d.Text);
    }

    private static System.Collections.Generic.IReadOnlyList<Predict.Core.Token> Tokens(string text) =>
        System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(Predict.Core.Tokenizer.Tokenize(text), t => t.Kind != Predict.Core.TokenKind.Space));

    [Fact]
    public void Two_Matching_Sentences_In_The_Read_Keep_The_Mirror_Text()
    {
        var s = Begin("Il dit que |tout va bien.\nFin.");
        Type(s, "ca ");
        Prove(s);
        s.Leave();
        var st = s.Snapshot();
        var read = W("Il dit que ça tout va bien.\nIl dit que ça tout va bien.\nF|in.");
        var d = EditClassifier.FromDocument(EditClassifier.Decide(st, Landing.Elsewhere), st, Landing.Elsewhere, read, true);
        Assert.Equal("Il dit que ca tout va bien.", d.Text);
    }

    [Fact]
    public void Flow_Enter_With_Unproven_Edits_Drops_The_Session()
    {
        var s = Begin("identifiant |eleve");
        s.Type('x');                                          // no NotPassword answer yet
        Assert.Null(RereadFlow.OnEnter(s));                   // Enter may move the focus (two-step login): dropped
        var t = Begin("Le ch|t dort.");
        t.Type('a');
        Prove(t);
        Assert.Same(t, RereadFlow.OnEnter(t));                // every edit covered: Enter is an edit, the session stays
        var u = Begin("Le |chat dort.");
        Assert.Same(u, RereadFlow.OnEnter(u));                // nothing typed yet: the read proved the field
        Assert.Null(RereadFlow.OnEnter(null));
    }

    [Fact]
    public void Flow_A_Leave_While_The_First_Character_Probe_Is_Pending_Drops_The_Session()
    {
        var s = Begin("Le ch|t dort.");
        s.Type('a');
        Assert.Null(RereadFlow.OnJump(s, SessionMove.Leave, firstCharProbePending: true));
        var t = Begin("Le ch|t dort.");
        Assert.Same(t, RereadFlow.OnJump(t, SessionMove.Leave, firstCharProbePending: false));
        Assert.True(t.Left);
        Assert.Same(t, RereadFlow.OnJump(t, SessionMove.Keep, firstCharProbePending: true));   // Esc / taskbar: kept
        Assert.Null(RereadFlow.OnJump(t, SessionMove.End, false));
    }

    [Fact]
    public void Flow_A_Begin_Is_Installed_Only_If_Nothing_Was_Typed_Since_The_Read()
    {
        var p = RereadFlow.Plan(null, Read(W("Le ch|t dort."), version: 41));
        Assert.Null(RereadFlow.Commit(null, null, null, p, true, 42).Session);   // a key moved the tracker on
        Assert.NotNull(RereadFlow.Commit(null, null, null, p, true, 41).Session);
    }

    [Fact]
    public void Keys_After_A_Leave_Are_Marked_Even_On_An_Untrusted_Mirror()
    {
        var s = Begin("le chat|");
        s.Delete();                                           // nothing to delete, window not cut: fine
        var big = EditSession.Begin(W("le chat|", truncEnd: true), Fg);
        big.Delete();                                         // past the window: untrusted
        Assert.True(big.Snapshot().Dirty);
        big.Leave();
        big.Type('x');
        Assert.True(big.Snapshot().TypedWhileLeft);
    }


    [Fact]
    public void Flow_Begins_A_Session_From_The_First_Read_And_Says_Nothing()
    {
        var p = RereadFlow.Plan(null, Read(W("Le ch|t dort.")));
        Assert.True(p.Replace);
        Assert.NotNull(p.Next);
        var (session, speak, _) = RereadFlow.Commit(null, null, null, p, enabled: true, 0);
        Assert.Same(p.Next, session);
        Assert.False(speak);
    }

    [Fact]
    public void Flow_A_Second_Leave_During_The_Read_Keeps_The_Session_For_The_Newer_Read()
    {
        var s = Begin("Le ch|t dort bien.");
        s.Type('a');
        Prove(s); s.Leave();
        var st = s.Snapshot();
        var p = RereadFlow.Plan(st, Read(W("Le chat dort bi|en.")));
        Assert.Equal("chat", p.Decision.Text);
        Prove(s); s.Leave();                                           // another arrow while the read was being classified
        var (session, speak, why) = RereadFlow.Commit(s, s, st, p, enabled: true, 0);
        Assert.Same(s, session);                             // the newer read decides: nothing installed, nothing said
        Assert.False(speak);
        Assert.Equal("left again", why);
        var st2 = s.Snapshot();
        var p2 = RereadFlow.Plan(st2, Read(W("Le chat dort bien|.")));
        Assert.True(RereadFlow.Commit(s, s, st2, p2, enabled: true, 0).Speak);   // said once, by the newer read
    }

    [Fact]
    public void Flow_Keys_Typed_After_The_Leave_Make_The_Read_Stale_Or_End_The_Session()
    {
        var s = Begin("Le ch|t dort bien.");
        s.Type('a');
        Prove(s); s.Leave();
        s.Type('x');                                          // typed before the read: the read is not applied
        var st = s.Snapshot();
        var p = RereadFlow.Plan(st, new ReadFacts(false, false, true, false, Fg, W("Le chat dort bi|xen.")));
        Assert.Equal(RereadDecision.Skip("typed meanwhile"), p.Decision);
        Assert.Null(RereadFlow.Commit(s, s, st, p, true, 0).Session);

        var t = Begin("Le ch|t dort bien.");
        t.Type('a');
        Prove(t); t.Leave();
        var st2 = t.Snapshot();
        var p2 = RereadFlow.Plan(st2, Read(W("Le chat dort bi|en.")));
        t.Type('y');                                          // typed after the read: the landing stands, no next session
        var (session, speak, _) = RereadFlow.Commit(t, t, st2, p2, true, 0);
        Assert.Null(session);
        Assert.True(speak);
    }

    [Fact]
    public void Flow_A_Password_Read_Drops_Any_Session_And_Says_Nothing()
    {
        var s = Begin("Le ch|t dort.");
        s.Type('a');
        Prove(s); s.Leave();
        var st = s.Snapshot();
        var p = RereadFlow.Plan(st, Read(null, password: true));
        var other = Begin("autre |texte");                   // even a session installed meanwhile
        var (session, speak, why) = RereadFlow.Commit(other, s, st, p, true, 0);
        Assert.Null(session);
        Assert.False(speak);
        Assert.Equal("password", why);
    }

    [Fact]
    public void Flow_Focus_Moved_To_A_Password_Field_Never_Speaks_What_Was_Typed_There()
    {
        // the focus event is a leave: the secret typed afterwards is never placed in the mirror
        var s = Begin("Le |chat dort.");
        s.Type('g'); s.Type('r'); s.Type('o'); s.Type('s'); s.Type(' ');
        Prove(s); s.Leave();                                            // EVENT_OBJECT_FOCUS -> Jump("focus", Leave)
        foreach (var c in "zqxwvk") s.Type(c);
        var st = s.Snapshot();
        Assert.DoesNotContain("zqxwvk", st.Text);
        var p = RereadFlow.Plan(st, Read(null, password: true));
        Assert.Null(RereadFlow.Commit(s, s, st, p, true, 0).Session);
        Assert.Equal(RereadKind.None, p.Decision.Kind);
    }

    [Fact]
    public void Flow_Paused_Installs_Nothing_And_Says_Nothing()
    {
        var s = Begin("Le ch|t dort bien.");
        s.Type('a');
        Prove(s); s.Leave();
        var st = s.Snapshot();
        var p = RereadFlow.Plan(st, Read(W("Le chat dort bi|en.")));
        Assert.Equal(RereadKind.Word, p.Decision.Kind);
        var (session, speak, why) = RereadFlow.Commit(s, s, st, p, enabled: false, 0);
        Assert.Null(session);
        Assert.False(speak);
        Assert.Equal("paused", why);
        Assert.Null(RereadFlow.Commit(null, s, st, p, true, 0).Session);   // a session dropped meanwhile is never brought back
    }

    [Fact]
    public void Flow_An_Older_Read_Never_Decides()
    {
        var s = Begin("Le ch|t dort.");
        s.Type('a');
        Prove(s); s.Leave();
        Assert.False(RereadFlow.Plan(s.Snapshot(), Read(W("Le chat do|rt."), latest: false)).Replace);
    }

    [Fact]
    public void Flow_Delete_Read_Rebases_Unless_Edited_Meanwhile()
    {
        var s = Begin("Le |xchat dort.");
        s.Delete();
        var st = s.Snapshot();
        var p = RereadFlow.Plan(st, Read(W("Le |chat dort."), del: true));
        var session = RereadFlow.Commit(s, s, st, p, true, 0).Session;
        Assert.NotSame(s, session);
        Assert.Equal("chat", Land(session!, W("Le chat do|rt.")).D.Text);   // the word-level edit survived the rebase
        var t = Begin("Le |xchat dort.");
        t.Delete();
        var st2 = t.Snapshot();
        var p2 = RereadFlow.Plan(st2, Read(W("Le |chat dort."), del: true));
        t.Type('z');                                          // typed after the snapshot: keep the mirror that has it
        Assert.Same(t, RereadFlow.Commit(t, t, st2, p2, true, 0).Session);
    }

    [Fact]
    public void Delete_While_Left_Is_Typed_While_Left()
    {
        var s = Begin("Le ch|at dort.");
        Prove(s); s.Leave();
        s.Delete();
        Assert.True(s.Snapshot().TypedWhileLeft);
        Assert.Equal("Le chat dort.", s.Snapshot().Text);
    }

    [Fact]
    public void A_Mirror_Grown_Too_Long_Is_No_Longer_Followed()
    {
        var s = Begin("|");
        for (int i = 0; i <= EditSession.MaxMirror + 1; i++) s.Type('a');
        var st = s.Snapshot();
        Assert.True(st.Dirty);
        Assert.True(st.Text.Length <= EditSession.MaxMirror + 1);
    }

    [Fact]
    public void Focus_Filter_Keeps_New_Objects_Of_The_Foreground_Window_Only()
    {
        IntPtr fg = (IntPtr)100, other = (IntPtr)200, rw = (IntPtr)101;
        var f = new FocusFilter();
        Assert.True(f.ShouldJump(fg, fg, rw, -4, 10, false, false));       // a new element (password field) in the page
        Assert.False(f.ShouldJump(fg, fg, rw, -4, 10, false, false));      // the same one again
        Assert.True(f.ShouldJump(fg, fg, rw, -4, 11, false, false));       // back to the text field
        Assert.False(f.ShouldJump(other, fg, (IntPtr)201, -4, 1, false, false));   // another window
        Assert.False(f.ShouldJump(fg, fg, rw, -4, 12, true, false));       // mouse held: the click's read follows
        Assert.False(f.ShouldJump(fg, fg, rw, -4, 12, false, false));      // ...and it was remembered
        Assert.False(f.ShouldJump(fg, fg, rw, -4, 13, false, true));       // taskbar / our windows
    }

    /// <summary>Where a leave from the session's current state would land, without leaving it (two landings from one edit).</summary>
    private static (Landing Where, RereadDecision D) Peek(EditSession s, TextWindow? read, bool sameFg = true)
    {
        var st = s.Snapshot();
        var where = EditClassifier.Classify(st, read, sameFg);
        return (where, EditClassifier.Decide(st, where));
    }
}
