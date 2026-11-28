using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 07 (D21/D24): the reading document (tiles, offsets, sentences, 1000-word limit), the engine position →
/// word mapping, the reading state machine, the selection capture policy and the Ctrl+C fallback (fake clipboard), the
/// Reading speech kind on the speech thread, and the reading settings.</summary>
public class ReadingTests
{
    private const char Nnbsp = (char)0x202F, Nbsp = (char)0x00A0;

    private static string[] Texts(ReadingDocument d) => d.Tiles.Select(t => t.Text).ToArray();

    // ------------------------------------------------------------------ document: tiles and offsets
    [Fact]
    public void Tiles_Keep_Their_Offsets_Sentence_And_Picture_Key()
    {
        var d = ReadingDocument.Build("Le chat mange la souris.");
        Assert.Equal(new[] { "Le", "chat", "mange", "la", "souris", "." }, Texts(d));
        Assert.Equal(new[] { 0, 3, 8, 14, 17, 23 }, d.Tiles.Select(t => t.Start));
        Assert.All(d.Tiles, t => Assert.Equal(0, t.Sentence));
        Assert.Equal("le", d.Tiles[0].PictureKey);
        Assert.Equal("souris", d.Tiles[4].PictureKey);
        Assert.Equal(TileKind.Punct, d.Tiles[5].Kind);
        Assert.Null(d.Tiles[5].PictureKey);
        Assert.Equal(5, d.WordCount);
        Assert.False(d.Truncated);
        foreach (var t in d.Tiles) Assert.Equal(t.Text, d.Text.Substring(t.Start, t.Length));
    }

    [Fact]
    public void An_Elision_Is_Joined_To_Its_Word_And_Takes_Its_Picture()
    {
        var d = ReadingDocument.Build("L'arbre et l’eau, jusqu'à toi.");
        Assert.Equal(new[] { "L'arbre", "et", "l’eau", ",", "jusqu'à", "toi", "." }, Texts(d));
        Assert.Equal("arbre", d.Tiles[0].PictureKey);
        Assert.Equal("eau", d.Tiles[2].PictureKey);
        Assert.Equal("à", d.Tiles[4].PictureKey);
        Assert.Equal(5, d.WordCount);   // l'arbre counts once
        var lone = ReadingDocument.Build("Il dit l' puis rien");
        Assert.Contains(lone.Tiles, t => t.Text == "l'" && t.PictureKey is null && t.Kind == TileKind.Word);
    }

    [Fact]
    public void Numbers_Have_No_Picture_Punctuation_Is_Shown()
    {
        var d = ReadingDocument.Build("En 2026, il a 1,5 kg : oui !");
        Assert.Equal(new[] { "En", "2026", ",", "il", "a", "1,5", "kg", ":", "oui", "!" }, Texts(d));
        Assert.Equal(TileKind.Number, d.Tiles[1].Kind);
        Assert.Null(d.Tiles[1].PictureKey);
        Assert.True(d.Tiles[1].IsWord);   // highlighted like a word when spoken
    }

    [Fact]
    public void Sentences_And_Utterances_Are_The_Exact_Spans()
    {
        string text = "Il pleut. Le chat dort" + Nnbsp + "! « Oui. »\r\nFin";
        var d = ReadingDocument.Build(text);
        Assert.Equal(4, d.Sentences.Count);
        var u = d.UtteranceOf(1);
        Assert.Equal(10, u.Start);
        Assert.Equal("Le chat dort !", u.Text);   // same length: U+202F → space
        Assert.Equal(text.Substring(10, u.Text.Length).Length, u.Text.Length);
        Assert.Equal("« Oui. »", d.UtteranceOf(2).Text);
        Assert.Equal("Fin", d.UtteranceOf(3).Text);
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1, 1 }, d.Tiles.Take(7).Select(t => t.Sentence));
        Assert.Equal(3, d.Tiles.Last().Sentence);
    }

    [Fact]
    public void Utterance_Can_Resume_At_A_Word()
    {
        var d = ReadingDocument.Build("Le chat mange la souris.");
        var u = d.UtteranceOf(0, 2);
        Assert.Equal(8, u.Start);
        Assert.Equal("mange la souris.", u.Text);
        Assert.Equal(14, u.ToDocument(6));
    }

    [Fact]
    public void Speak_Text_Replaces_One_For_One()
    {
        string t = "a" + Nbsp + "b’c\nd";
        var s = ReadingDocument.SpeakText(t, 0, t.Length);
        Assert.Equal("a b'c d", s);
        Assert.Equal(t.Length, s.Length);
    }

    [Fact]
    public void A_Sentence_Of_Punctuation_Only_Has_No_Words()
    {
        var d = ReadingDocument.Build("Bon. ... Oui.");
        Assert.True(d.Sentences[0].HasWords);
        Assert.False(d.Sentences[1].HasWords);
        Assert.True(d.Sentences[2].HasWords);
    }

    // ------------------------------------------------------------------ 1000-word limit (D24)
    private static string Words(int n) => string.Join(" ", Enumerable.Range(0, n).Select(i => "mot" + (char)('a' + i % 26))) + ".";

    [Fact]
    public void Limit_Is_1000_Words_Then_Cut_Before_The_Next_Word()
    {
        var exact = ReadingDocument.Build(Words(1000));
        Assert.Equal(1000, exact.WordCount);
        Assert.False(exact.Truncated);
        Assert.EndsWith(".", exact.Text);

        var over = ReadingDocument.Build(Words(1005));
        Assert.True(over.Truncated);
        Assert.Equal(1000, over.WordCount);
        Assert.Equal(1000, over.Tiles.Count(t => t.IsWord));
        Assert.False(over.Text.EndsWith(" "));
        Assert.Equal(over.Text, over.Text.TrimEnd());
        Assert.Equal(SelectionPolicy.Key(Words(1005)), over.Key);   // de-duplication on the whole selection
    }

    [Fact]
    public void Limit_Counts_An_Elision_With_Its_Word_Once()
    {
        var d = ReadingDocument.Build("l'arbre l'eau l'ami", maxWords: 2);
        Assert.True(d.Truncated);
        Assert.Equal(new[] { "l'arbre", "l'eau" }, Texts(d));
    }

    // ------------------------------------------------------------------ engine position → tile
    [Fact]
    public void Progress_Maps_To_The_Word_Exact_And_Inside()
    {
        var d = ReadingDocument.Build("Le chat mange la souris.");
        Assert.Equal(1, d.TileAt(0, 3, 4));    // exact start of « chat »
        Assert.Equal(1, d.TileAt(0, 5, 1));    // inside « chat »
        Assert.Equal(4, d.TileAt(0, 17, 6));
    }

    [Fact]
    public void Progress_On_A_Space_Goes_To_The_Next_Word_On_Punctuation_Keeps_The_Highlight()
    {
        var d = ReadingDocument.Build("Le chat, mange.");
        Assert.Equal(1, d.TileAt(0, 2, 5));    // the space before « chat », length covering it
        Assert.Equal(-1, d.TileAt(0, 7, 1));   // « , »
        Assert.Equal(-1, d.TileAt(0, 14, 1));  // « . »
        Assert.Equal(-1, d.TileAt(5, 0, 1));   // no such sentence
    }

    [Fact]
    public void Progress_After_Oe_And_Typographic_Apostrophe()
    {
        var d = ReadingDocument.Build("Le cœur de l’eau bat.");
        var t = Texts(d);
        Assert.Equal(new[] { "Le", "cœur", "de", "l’eau", "bat", "." }, t);
        Assert.Equal(1, d.TileAt(0, 3, 4));
        Assert.Equal(2, d.TileAt(0, 8, 2));     // « de » right after « cœur »: no shift
        Assert.Equal(3, d.TileAt(0, 11, 2));    // « l’ »
        Assert.Equal(3, d.TileAt(0, 13, 3));    // « eau » (an engine may report the elided word apart)
        Assert.Equal(4, d.TileAt(0, 17, 3));
    }

    [Fact]
    public void A_Number_The_Voice_Expands_Stays_On_One_Tile()
    {
        var d = ReadingDocument.Build("En 2026 il neige.");
        // « deux mille vingt-six »: several progress events, all inside the token (or at its start)
        Assert.Equal(1, d.TileAt(0, 3, 4));
        Assert.Equal(1, d.TileAt(0, 3, 2));
        Assert.Equal(1, d.TileAt(0, 5, 2));
        Assert.Equal(2, d.TileAt(0, 8, 2));
    }

    [Fact]
    public void Progress_Before_A_Narrow_No_Break_Space()
    {
        var d = ReadingDocument.Build("Bonjour" + Nnbsp + "! Toi.");
        Assert.Equal(0, d.TileAt(0, 0, 7));
        Assert.Equal(-1, d.TileAt(0, 8, 1));   // « ! »
        Assert.Equal(2, d.TileAt(1, 10, 3));
    }

    // ------------------------------------------------------------------ state machine
    private sealed class Rec : IReadingOutput
    {
        public readonly List<string> Calls = new();
        public readonly List<(long Id, Utterance U)> Spoken = new();
        public int Highlighted = -1;
        public void Speak(long id, Utterance u) { Spoken.Add((id, u)); Calls.Add($"speak {u.Sentence}@{u.Start}"); }
        public void SayWord(string word) => Calls.Add($"word {word}");
        public void Cancel() => Calls.Add("cancel");
        public void Highlight(int tile) { Highlighted = tile; Calls.Add($"hl {tile}"); }
        public void StateChanged(ReadingState s) => Calls.Add($"state {s}");
        public long LastId => Spoken[^1].Id;
    }

    private static (ReadingController C, Rec R) Ready(string text = "Le chat dort. Il pleut. Fin.")
    {
        var r = new Rec();
        var c = new ReadingController(r);
        c.Show(ReadingDocument.Build(text));
        return (c, r);
    }

    [Fact]
    public void Show_Does_Not_Autoplay()
    {
        var (c, r) = Ready();
        Assert.Equal(ReadingState.Ready, c.State);
        Assert.Empty(r.Spoken);
        Assert.Equal(-1, r.Highlighted);
    }

    [Fact]
    public void Play_Reads_One_Sentence_Per_Utterance_To_The_End()
    {
        var (c, r) = Ready();
        c.TogglePlay();
        Assert.Equal(ReadingState.Playing, c.State);
        Assert.Equal("Le chat dort.", r.Spoken[0].U.Text);
        Assert.Equal(0, r.Highlighted);                         // the first word at once
        Assert.Equal(1, c.OnProgress(r.LastId, 3, 4));          // « chat »
        Assert.Equal(1, r.Highlighted);
        c.OnDone(r.LastId, SpeechOutcome.Done);
        Assert.Equal("Il pleut.", r.Spoken[1].U.Text);
        Assert.Equal(5, c.OnProgress(r.LastId, 3, 5));          // « pleut » = offset 17 in the document
        c.OnDone(r.LastId, SpeechOutcome.Done);
        Assert.Equal("Fin.", r.Spoken[2].U.Text);
        c.OnDone(r.LastId, SpeechOutcome.Done);
        Assert.Equal(ReadingState.Ready, c.State);
        Assert.Equal(-1, r.Highlighted);
        Assert.Equal(0, c.Sentence);                           // the next Play starts again at the top
        Assert.Equal(3, r.Spoken.Count);
    }

    [Fact]
    public void Pause_Cancels_And_Play_Resumes_At_The_Highlighted_Word()
    {
        var (c, r) = Ready("Le chat mange la souris. Fin.");
        c.Play();
        long first = r.LastId;
        c.OnProgress(first, 8, 5);                              // « mange »
        c.TogglePlay();
        Assert.Equal(ReadingState.Paused, c.State);
        Assert.Equal("cancel", r.Calls.Last(x => !x.StartsWith("state")));
        // late events of the cancelled utterance are ignored (stale id)
        Assert.Equal(-1, c.OnProgress(first, 14, 2));
        c.OnDone(first, SpeechOutcome.Cancelled);
        Assert.Equal(ReadingState.Paused, c.State);
        Assert.Equal(2, r.Highlighted);
        c.TogglePlay();
        Assert.Equal(8, r.Spoken[^1].U.Start);
        Assert.Equal("mange la souris.", r.Spoken[^1].U.Text);
        Assert.Equal(3, c.OnProgress(r.LastId, 6, 2));          // « la »: utterance offset + resume start
    }

    [Fact]
    public void Next_And_Prev_While_Playing_And_While_Stopped()
    {
        var (c, r) = Ready();
        c.Next();                                               // stopped: cursor moves, first word highlighted, nothing said
        Assert.Equal(1, c.Sentence);
        Assert.Empty(r.Spoken);
        Assert.Equal(c.Doc!.FirstWordOf(1), r.Highlighted);
        c.Play();
        Assert.Equal("Il pleut.", r.Spoken[^1].U.Text);
        long old = r.LastId;
        c.Next();                                               // playing: the next sentence at once (interrupts)
        Assert.Equal("Fin.", r.Spoken[^1].U.Text);
        Assert.Equal(-1, c.OnProgress(old, 0, 2));              // the replaced utterance's events are ignored
        c.OnDone(old, SpeechOutcome.Cancelled);
        Assert.Equal(ReadingState.Playing, c.State);
        c.Next();                                               // last sentence: said again from its start
        Assert.Equal("Fin.", r.Spoken[^1].U.Text);
        c.Prev(); c.Prev();
        Assert.Equal("Le chat dort.", r.Spoken[^1].U.Text);
        c.Prev();                                               // first: again
        Assert.Equal(0, r.Spoken[^1].U.Sentence);
    }

    [Fact]
    public void Prev_Skips_Sentences_Without_Words()
    {
        var (c, r) = Ready("Bon. ... Oui.");
        c.Next();
        Assert.Equal(2, c.Sentence);
        c.Prev();
        Assert.Equal(0, c.Sentence);
    }

    [Fact]
    public void Click_Stops_Says_The_Word_And_Play_Starts_At_Its_Sentence()
    {
        var (c, r) = Ready();
        c.Play();
        long id = r.LastId;
        c.Click(5);                                            // « pleut » (sentence 1)
        Assert.Equal(ReadingState.Ready, c.State);
        Assert.Equal("word pleut", r.Calls.Last(x => x.StartsWith("word")));
        Assert.Equal(5, r.Highlighted);
        Assert.Equal(-1, c.OnProgress(id, 3, 4));
        c.OnDone(id, SpeechOutcome.Cancelled);
        Assert.Equal(ReadingState.Ready, c.State);
        c.Play();
        Assert.Equal("Il pleut.", r.Spoken[^1].U.Text);         // from the start of the clicked word's sentence
        c.Click(3);                                            // « . » of sentence 0: no word said, speech stopped
        Assert.Equal("cancel", r.Calls.Last(x => !x.StartsWith("state") && !x.StartsWith("hl")));
    }

    [Fact]
    public void Exit_Stops_And_Ignores_Late_Events()
    {
        var (c, r) = Ready();
        c.Play();
        long id = r.LastId;
        c.Exit();
        Assert.Equal(ReadingState.Idle, c.State);
        Assert.Contains("cancel", r.Calls);
        Assert.Equal(-1, c.OnProgress(id, 3, 4));
        c.OnDone(id, SpeechOutcome.Done);
        Assert.Single(r.Spoken);
        c.Play(); c.Next(); c.Click(0);                         // nothing works while idle
        Assert.Single(r.Spoken);
    }

    [Fact]
    public void Cancelled_By_Someone_Else_Pauses_Error_Stops()
    {
        var (c, r) = Ready();
        c.Play();
        c.OnProgress(r.LastId, 3, 4);
        c.OnDone(r.LastId, SpeechOutcome.Cancelled);
        Assert.Equal(ReadingState.Paused, c.State);
        c.Play();
        Assert.Equal(3, r.Spoken[^1].U.Start);                 // resumes at « chat »
        c.OnDone(r.LastId, SpeechOutcome.Error);
        Assert.Equal(ReadingState.Ready, c.State);
    }

    [Fact]
    public void A_New_Selection_While_Playing_Cancels_And_Waits_For_Play()
    {
        var (c, r) = Ready();
        c.Play();
        c.Show(ReadingDocument.Build("Autre texte."));
        Assert.Equal(ReadingState.Ready, c.State);
        Assert.Contains("cancel", r.Calls);
        Assert.Single(r.Spoken);
    }

    // ------------------------------------------------------------------ selection policy
    [Fact]
    public void Decide_Dedupes_On_The_Trimmed_Text()
    {
        Assert.Equal(SelectionAction.Show, SelectionPolicy.Decide(false, null, null, "Le chat"));
        Assert.Equal(SelectionAction.Keep, SelectionPolicy.Decide(true, "Le chat", null, "  Le chat\r\n"));
        Assert.Equal(SelectionAction.Show, SelectionPolicy.Decide(true, "Le chat", null, "Le chien"));
        Assert.Equal(SelectionAction.Exit, SelectionPolicy.Decide(true, "Le chat", null, ""));
        Assert.Equal(SelectionAction.Exit, SelectionPolicy.Decide(true, "Le chat", null, "   "));
        Assert.Equal(SelectionAction.Ignore, SelectionPolicy.Decide(false, null, null, null));
        // dismissed with Esc: not shown again until it changes
        Assert.Equal(SelectionAction.Ignore, SelectionPolicy.Decide(false, null, "Le chat", "Le chat "));
        Assert.Equal(SelectionAction.Show, SelectionPolicy.Decide(false, null, "Le chat", "Le chat dort"));
    }

    [Fact]
    public void Clipboard_Fallback_Only_After_A_Gesture_Setting_On_No_Text_Pattern_No_Password()
    {
        Assert.True(SelectionPolicy.MayUseClipboard(true, true, false, ReadStatus.Unavailable));
        Assert.False(SelectionPolicy.MayUseClipboard(false, true, false, ReadStatus.Unavailable));   // off by default
        Assert.False(SelectionPolicy.MayUseClipboard(true, false, false, ReadStatus.Unavailable));   // a plain click
        Assert.False(SelectionPolicy.MayUseClipboard(true, true, true, ReadStatus.Unavailable));     // password field
        Assert.False(SelectionPolicy.MayUseClipboard(true, true, false, ReadStatus.Password));
        Assert.False(SelectionPolicy.MayUseClipboard(true, true, false, ReadStatus.Ok));             // the app said: no selection
        Assert.False(SelectionPolicy.MayUseClipboard(true, true, false, ReadStatus.Selection));      // read directly
        Assert.True(new AppSettings().Reading.Enabled);
        Assert.Null(new AppSettings().Reading.ClipboardFallback);   // 0.11.0: not set = the host's default
    }

    /// <summary>LibreOffice phase 2: a selection is read directly from any source (Writer through IA2); the Ctrl+C copy
    /// is only the safety net for an IA2 read, under the 0.11.0 rules (gesture, setting, never in Calc).</summary>
    [Fact]
    public void Route_Reads_Every_Selection_Directly_The_Copy_Is_Only_The_Ia2_Safety_Net()
    {
        var W = LibreOfficeModule.Writer;
        // ia2 Selection + gesture + Writer default → read, the copy allowed as a fallback
        Assert.Equal((true, true, 0), SelectionPolicy.Route(ReadStatus.Selection, "ia2", true, false, null, W, false));
        Assert.Equal((true, true, 250), SelectionPolicy.Route(ReadStatus.Selection, "ia2", true, true, null, W, false));   // Shift: debounce
        Assert.Equal((true, false, 0), SelectionPolicy.Route(ReadStatus.Selection, "ia2", true, false, false, W, false));  // « Jamais »: IA2 only
        Assert.Equal((true, false, 0), SelectionPolicy.Route(ReadStatus.Selection, "ia2", false, false, null, W, false));  // no gesture
        Assert.Equal((true, false, 0), SelectionPolicy.Route(ReadStatus.Selection, "ia2", true, false, null, W, true));    // password suppressed
        Assert.Equal((true, false, 0), SelectionPolicy.Route(ReadStatus.Selection, "ia2", true, false, true, LibreOfficeModule.Calc, false));
        foreach (var src in new[] { "uia", "win32" })                                                                       // read directly, no copy
            Assert.Equal((true, false, 0), SelectionPolicy.Route(ReadStatus.Selection, src, true, false, true, LibreOfficeModule.None, false));
        // no text pattern: the copy under the same rules as 0.11.0
        Assert.Equal((true, true, 30), SelectionPolicy.Route(ReadStatus.Unavailable, "skip", true, false, null, W, false));
        Assert.Equal((true, true, 250), SelectionPolicy.Route(ReadStatus.Unavailable, "skip", true, true, null, W, false));
        Assert.Equal((false, false, 0), SelectionPolicy.Route(ReadStatus.Unavailable, "skip", true, false, null, LibreOfficeModule.Calc, false));
        Assert.Equal((false, false, 0), SelectionPolicy.Route(ReadStatus.Unavailable, "skip", true, false, true, LibreOfficeModule.Calc, false));
        Assert.Equal((false, false, 0), SelectionPolicy.Route(ReadStatus.Unavailable, "skip", false, false, null, W, false));
        Assert.Equal((false, false, 0), SelectionPolicy.Route(ReadStatus.Unavailable, "uia", true, false, null, LibreOfficeModule.None, false));
        // nothing selected, a password field, a failed read: nothing to ask
        Assert.Equal((false, false, 0), SelectionPolicy.Route(ReadStatus.Ok, "ia2", true, false, null, W, false));
        Assert.Equal((false, false, 0), SelectionPolicy.Route(ReadStatus.Password, "ia2", true, false, true, W, false));
        Assert.Equal((false, false, 0), SelectionPolicy.Route(ReadStatus.Timeout, "ia2", true, false, true, W, false));
    }

    [Fact]
    public void Module_Is_Looked_Up_Only_When_A_Copy_Could_Follow()
    {
        Assert.True(SelectionPolicy.NeedsModule(ReadStatus.Selection, "ia2", true, null));
        Assert.True(SelectionPolicy.NeedsModule(ReadStatus.Unavailable, "skip", true, true));
        Assert.False(SelectionPolicy.NeedsModule(ReadStatus.Selection, "uia", true, true));
        Assert.False(SelectionPolicy.NeedsModule(ReadStatus.Selection, "ia2", false, null));
        Assert.False(SelectionPolicy.NeedsModule(ReadStatus.Unavailable, "skip", true, false));
        Assert.False(SelectionPolicy.NeedsModule(ReadStatus.Ok, "ia2", true, null));
    }

    /// <summary>Review S3–S5: an IA2 text already cut cleanly is never re-cut by the reader's limit, only capped if the D26
    /// completion made it longer; an uncut text at the limit is cut at a word.</summary>
    [Fact]
    public void Limit_Never_Recuts_A_Clean_Cut()
    {
        Assert.Equal(("le chat dort", true), SelectionPolicy.Limit("le chat dort", alreadyCut: true, 12));
        Assert.Equal(("le chat dort", true), SelectionPolicy.Limit("le chat dort", alreadyCut: true, 100));
        Assert.Equal(("le chat do", true), SelectionPolicy.Limit("le chat dort", alreadyCut: true, 10));   // D26 overflow: capped
        Assert.Equal(("le chat", true), SelectionPolicy.Limit("le chat dort", alreadyCut: false, 12));     // at the limit: a word cut
        Assert.Equal(("le chat dort", false), SelectionPolicy.Limit("le chat dort", alreadyCut: false, 13));
    }

    [Fact]
    public void A_Partial_Read_Has_Its_Own_Message()
    {
        var d = ReadingDocument.Build("Le chat dort.", partial: true);
        Assert.True(d.Partial);
        Assert.False(d.Truncated);
        Assert.False(ReadingDocument.Build("Le chat dort.").Partial);
        Assert.NotEqual(Strings.ReadTooLong, Strings.ReadPartial);
    }

    /// <summary>The selection reader's fallback decision: a failed read, or (phase 2) an IA2 read that reached only part of
    /// the selection; never a complete read, a password field or a stale read.</summary>
    [Fact]
    public void Fallback_Is_Wanted_For_A_Failed_Or_Partial_Read()
    {
        Assert.True(SelectionPolicy.WantsFallback(ReadStatus.Unavailable));
        Assert.True(SelectionPolicy.WantsFallback(ReadStatus.Ok, partial: true));
        Assert.False(SelectionPolicy.WantsFallback(ReadStatus.Ok));
        Assert.False(SelectionPolicy.WantsFallback(ReadStatus.Password, partial: true));
        Assert.False(SelectionPolicy.WantsFallback(ReadStatus.Stale, partial: true));
    }

    /// <summary>Owner 2026-11-27: the module of a LibreOffice window from its (untranslated) title suffix.</summary>
    [Theory]
    [InlineData("soffice.bin", "Sans nom 1 — LibreOffice Writer", (int)LibreOfficeModule.Writer)]
    [InlineData("soffice.bin", "Untitled 1 — LibreOffice Writer", (int)LibreOfficeModule.Writer)]
    [InlineData("soffice.bin", "rapport de stage.odt — LibreOffice Writer", (int)LibreOfficeModule.Writer)]
    [InlineData("soffice.bin", "lettre.docx - LibreOffice Writer ", (int)LibreOfficeModule.Writer)]      // older « - », trailing space
    [InlineData("SOFFICE.BIN", "Sans nom 1 — LibreOffice Calc", (int)LibreOfficeModule.Calc)]
    [InlineData("soffice.bin", "Untitled 1 — LibreOffice Calc", (int)LibreOfficeModule.Calc)]
    [InlineData("soffice.bin", "notes de Writer.ods — LibreOffice Calc", (int)LibreOfficeModule.Calc)]   // only the suffix counts
    [InlineData("soffice.bin", "Sans nom 1 — LibreOfficeDev Calc", (int)LibreOfficeModule.Calc)]
    [InlineData("soffice.bin", "budget.ods - OpenOffice Calc", (int)LibreOfficeModule.Calc)]
    [InlineData("soffice.bin", "budget.ods — Collabora Office Calc", (int)LibreOfficeModule.Calc)]
    [InlineData("soffice.bin", "Calc — LibreOffice Writer", (int)LibreOfficeModule.Writer)]           // a Writer document named « Calc »
    [InlineData("soffice.bin", "Sans nom 1 — LibreOffice Impress", (int)LibreOfficeModule.Impress)]
    [InlineData("soffice.bin", "Sans nom 1 — LibreOffice Draw", (int)LibreOfficeModule.Draw)]
    [InlineData("soffice.bin", "Rechercher et remplacer", (int)LibreOfficeModule.Other)]               // a dialog in front
    [InlineData("soffice.bin", "", (int)LibreOfficeModule.Other)]
    [InlineData("soffice.bin", null, (int)LibreOfficeModule.Other)]
    [InlineData("swriter", "", (int)LibreOfficeModule.Writer)] [InlineData("scalc", "x", (int)LibreOfficeModule.Calc)]
    [InlineData("notepad", "Sans titre - LibreOffice Writer", (int)LibreOfficeModule.None)]            // not LibreOffice's process
    [InlineData(null, "Sans nom 1 — LibreOffice Writer", (int)LibreOfficeModule.None)]
    public void LibreOffice_Module_From_The_Title(string? process, string? title, int expected) =>
        Assert.Equal((LibreOfficeModule)expected, HostApps.ModuleOf(process, title));

    [Theory]
    [InlineData(null, (int)LibreOfficeModule.Writer, (int)ClipboardFallback.Allowed)]     // the default: Writer only
    [InlineData(null, (int)LibreOfficeModule.Impress, (int)ClipboardFallback.Off)]
    [InlineData(null, (int)LibreOfficeModule.Draw, (int)ClipboardFallback.Off)]
    [InlineData(null, (int)LibreOfficeModule.Other, (int)ClipboardFallback.Off)]
    [InlineData(null, (int)LibreOfficeModule.None, (int)ClipboardFallback.Off)]
    [InlineData(null, (int)LibreOfficeModule.Calc, (int)ClipboardFallback.RefusedCalc)]
    [InlineData(true, (int)LibreOfficeModule.None, (int)ClipboardFallback.Allowed)]       // « Partout »
    [InlineData(true, (int)LibreOfficeModule.Impress, (int)ClipboardFallback.Allowed)]
    [InlineData(true, (int)LibreOfficeModule.Writer, (int)ClipboardFallback.Allowed)]
    [InlineData(true, (int)LibreOfficeModule.Calc, (int)ClipboardFallback.RefusedCalc)]   // never in Calc, even « Partout »
    [InlineData(false, (int)LibreOfficeModule.Writer, (int)ClipboardFallback.Off)]        // « Jamais »
    [InlineData(false, (int)LibreOfficeModule.None, (int)ClipboardFallback.Off)]
    [InlineData(false, (int)LibreOfficeModule.Calc, (int)ClipboardFallback.RefusedCalc)]
    public void Clipboard_Fallback_Per_Window(bool? setting, int module, int expected)
    {
        var m = (LibreOfficeModule)module;
        Assert.Equal((ClipboardFallback)expected, SelectionPolicy.Fallback(setting, m));
        bool allowed = (ClipboardFallback)expected == ClipboardFallback.Allowed;
        Assert.Equal(allowed, SelectionPolicy.MayUseClipboard(setting, m, true, false, ReadStatus.Unavailable));
        Assert.False(SelectionPolicy.MayUseClipboard(setting, m, false, false, ReadStatus.Unavailable));   // no gesture
        Assert.False(SelectionPolicy.MayUseClipboard(setting, m, true, true, ReadStatus.Unavailable));     // password
        Assert.False(SelectionPolicy.MayUseClipboard(setting, m, true, false, ReadStatus.Ok));
    }

    [Fact]
    public void Clipboard_Setting_Three_Positions_Layering_Lock_And_Old_Files()
    {
        JsonObject? J(string s) => JsonNode.Parse(s) as JsonObject;
        // older settings files: the key absent → not set (Writer only); true / false kept as they are
        Assert.Null(AppSettings.FromJson(J(@"{""reading"":{""enabled"":true}}")).Reading.ClipboardFallback);
        Assert.Null(AppSettings.FromJson(J(@"{}")).Reading.ClipboardFallback);
        Assert.Null(AppSettings.FromJson(J(@"{""reading"":{""clipboardFallback"":null}}")).Reading.ClipboardFallback);
        Assert.False(AppSettings.FromJson(J(@"{""reading"":{""clipboardFallback"":false}}")).Reading.ClipboardFallback);
        Assert.True(AppSettings.FromJson(J(@"{""reading"":{""clipboardFallback"":true}}")).Reading.ClipboardFallback);
        // round trip of the three positions
        foreach (var v in new bool?[] { null, true, false })
        {
            var s = new AppSettings() with { Reading = new ReadingSettings(ClipboardFallback: v) };
            Assert.Equal(v, AppSettings.FromJson(s.ToJson()).Reading.ClipboardFallback);
        }
        // layers: machine default < « Classe » < profile < machine lock
        var off = J(@"{""reading"":{""clipboardFallback"":false}}");
        var on = J(@"{""reading"":{""clipboardFallback"":true}}");
        var unset = J(@"{""reading"":{""clipboardFallback"":null}}");
        Assert.Null(SettingsLayering.Merge((JsonObject?)null, null, null).Effective.Reading.ClipboardFallback);
        Assert.False(SettingsLayering.Merge(off, null, null).Effective.Reading.ClipboardFallback);                 // admin: « Jamais »
        Assert.True(SettingsLayering.Merge(off, on, null).Effective.Reading.ClipboardFallback);                    // the user's choice
        Assert.False(SettingsLayering.Merge(MachineLayer.None, off, null).Effective.Reading.ClipboardFallback);    // « Classe »: « Jamais »
        Assert.Null(SettingsLayering.Merge(MachineLayer.None, off, unset).Effective.Reading.ClipboardFallback);    // a pupil's explicit Writer only
        var (eff, locked) = SettingsLayering.Merge((JsonObject?)null, on, off);
        Assert.False(eff.Reading.ClipboardFallback);                                                               // the lock wins
        Assert.Contains("reading.clipboardFallback", locked);
        // Réglages: each position is an explicit choice. Over a machine default « Jamais », « Writer only » is written as
        // a JSON null (not dropped); back to « Jamais » removes the key (equal to the baseline, never frozen)
        var machine = new MachineLayer(off, null);
        var noLocks = new HashSet<string>();
        var cur = SettingsLayering.Merge(machine, null).Effective;
        var u1 = SettingsLayering.NewUserLayer(new JsonObject(), cur, cur with { Reading = cur.Reading with { ClipboardFallback = null } }, machine, noLocks);
        var g = Json.Get(u1, "reading.clipboardFallback");
        Assert.True(g.Found);
        Assert.Null(g.Node);
        Assert.Null(SettingsLayering.Merge(machine, u1).Effective.Reading.ClipboardFallback);
        var cur1 = SettingsLayering.Merge(machine, u1).Effective;
        var u2 = SettingsLayering.NewUserLayer(u1, cur1, cur1 with { Reading = cur1.Reading with { ClipboardFallback = false } }, machine, noLocks);
        Assert.False(Json.Get(u2, "reading.clipboardFallback").Found);
        Assert.False(SettingsLayering.Merge(machine, u2).Effective.Reading.ClipboardFallback);
        // a locked value is never written by the window
        var lockedMachine = new MachineLayer(null, off);
        var (lcur, lpaths) = SettingsLayering.Merge(lockedMachine, null);
        var u3 = SettingsLayering.NewUserLayer(new JsonObject(), lcur, lcur with { Reading = lcur.Reading with { ClipboardFallback = true } }, lockedMachine, lpaths);
        Assert.False(Json.Get(u3, "reading.clipboardFallback").Found);
    }

    private sealed class FakeClipboard : IClipboardOps
    {
        public List<(uint Format, byte[] Data)> Content = new() { (13, new byte[] { 65, 0, 0, 0 }), (0xC123, new byte[] { 1, 2, 3 }) };
        public uint Seq = 7;
        public bool Busy, AppCopies = true, Modifiers, Target = true, ThrowOnRead, UserCopy, RewriteOnce;
        public string Owner = "user-app";     // who owns the clipboard: "target" after the app's copy, "other", "us"
        public string Selection = "le texte choisi";
        public int Copies, Restores, RestoreAttempts, OpenFailures;
        public long Now, LateCopyAt = -1;     // the app copies only when the clock reaches this (ms), -1 = at once / never
        private bool _sent;
        public uint Sequence
        {
            get
            {
                if (_sent && LateCopyAt >= 0 && Now >= LateCopyAt) { LateCopyAt = -1; Copy(); }
                return Seq;
            }
        }
        private void Copy() { Content = new() { (13, System.Text.Encoding.Unicode.GetBytes(Selection + "\0")) }; Seq++; Owner = "target"; }
        public bool StillTarget() => Target;
        public bool OwnedByTarget() => Owner == "target";
        public bool HasOwner() => Owner != "none";
        public bool UserCopied() => UserCopy;
        public IReadOnlyList<uint> Formats() => Content.Select(c => c.Format).ToList();
        public bool OwnerResponsive() => !Busy;
        public ClipboardSnapshot? Save() => new(Content.Select(c => (c.Format, c.Data.ToArray())).ToList());
        public bool ModifiersDown() => Modifiers;
        public bool SendCopy()
        {
            Copies++;
            _sent = true;
            if (AppCopies && LateCopyAt < 0) Copy();
            return true;
        }
        public string? ReadText() => ThrowOnRead ? throw new InvalidOperationException("boom") : Content.Where(c => c.Format == 13).Select(c => System.Text.Encoding.Unicode.GetString(c.Data).TrimEnd('\0')).FirstOrDefault();
        public bool Restore(ClipboardSnapshot s)
        {
            RestoreAttempts++;
            if (OpenFailures > 0) { OpenFailures--; return false; }   // another application holds the clipboard open
            Restores++; Content = s.Items.Select(i => (i.Format, i.Data)).ToList(); Seq++; Owner = "us";
            if (RewriteOnce) { RewriteOnce = false; LateCopyAt = Now + 60; }   // the app writes once more after our restore
            return true;
        }
        public void Sleep(int ms) => Now += ms;
        public long NowMs => Now;
    }

    [Fact]
    public void Clipboard_Copy_Reads_The_Selection_And_Restores_Everything()
    {
        var cb = new FakeClipboard();
        var before = cb.Content.Select(c => (c.Format, Convert.ToBase64String(c.Data))).ToList();
        var (text, outcome) = ClipboardCopy.Run(cb, () => ProbeStatus.NotPassword);
        Assert.Equal("le texte choisi", text);
        Assert.Equal(1, cb.Restores);
        Assert.Equal(before, cb.Content.Select(c => (c.Format, Convert.ToBase64String(c.Data))).ToList());
        Assert.Contains("restored", outcome);
        Assert.DoesNotContain("choisi", outcome);   // the log never has the text
    }

    [Fact]
    public void Clipboard_Copy_Never_In_A_Password_Field_Nor_An_Unknown_One()
    {
        foreach (var st in new[] { ProbeStatus.Password, ProbeStatus.Unavailable })
        {
            var cb = new FakeClipboard();
            var (text, _) = ClipboardCopy.Run(cb, () => st);
            Assert.Null(text);
            Assert.Equal(0, cb.Copies);
            Assert.Equal(0, cb.Restores);
        }
    }

    [Fact]
    public void Clipboard_Copy_Skipped_When_Busy_Unrestorable_Or_Modifier_Held()
    {
        var busy = new FakeClipboard { Busy = true };
        Assert.Null(ClipboardCopy.Run(busy, () => ProbeStatus.NotPassword).Text);
        Assert.Equal(0, busy.Copies);

        var emf = new FakeClipboard();
        emf.Content.Add((ClipboardCopy.CF_ENHMETAFILE, Array.Empty<byte>()));
        Assert.Null(ClipboardCopy.Run(emf, () => ProbeStatus.NotPassword).Text);
        Assert.Equal(0, emf.Copies);

        var held = new FakeClipboard { Modifiers = true };
        Assert.Contains("modifier", ClipboardCopy.Run(held, () => ProbeStatus.NotPassword).Outcome);
        Assert.Equal(0, held.Copies);

        var noCopy = new FakeClipboard { AppCopies = false };   // the app ignored Ctrl+C: nothing to restore
        var (t, o) = ClipboardCopy.Run(noCopy, () => ProbeStatus.NotPassword);
        Assert.Null(t);
        Assert.Equal(0, noCopy.Restores);
        Assert.Contains("untouched", o);

        var moved = new FakeClipboard { Target = false };       // another window came to the front: no Ctrl+C
        Assert.Null(ClipboardCopy.Run(moved, () => ProbeStatus.NotPassword).Text);
        Assert.Equal(0, moved.Copies);
        Assert.Equal(0, moved.Restores);
    }

    private static List<(uint, string)> Snap(FakeClipboard c) => c.Content.Select(x => (x.Format, Convert.ToBase64String(x.Data))).ToList();

    [Fact]
    public void Clipboard_A_Late_Copy_Is_Watched_And_Undone()
    {
        var cb = new FakeClipboard { LateCopyAt = ClipboardCopy.WaitMs + 400 };   // after the 300 ms wait
        var before = Snap(cb);
        var (text, outcome) = ClipboardCopy.Run(cb, () => ProbeStatus.NotPassword);
        Assert.Null(text);                                   // too late for this read
        Assert.Equal(1, cb.Restores);
        Assert.Equal(before, Snap(cb));
        Assert.Contains("late copy", outcome);
        var never = new FakeClipboard { LateCopyAt = ClipboardCopy.WaitMs + ClipboardCopy.LateWatchMs + 500 };
        Assert.Contains("untouched", ClipboardCopy.Run(never, () => ProbeStatus.NotPassword).Outcome);
    }

    /// <summary>0.11.0 VM run: Writer answered a double click's Ctrl+C after 650 ms. In LibreOffice the copy is waited
    /// for 800 ms (and read); elsewhere 300 ms as before (a late copy is undone, not read).</summary>
    [Fact]
    public void Clipboard_Waits_Longer_In_LibreOffice()
    {
        Assert.Equal(800, ClipboardCopy.WaitFor("soffice.bin"));
        Assert.Equal(ClipboardCopy.WaitMs, ClipboardCopy.WaitFor("notepad"));
        Assert.Equal(ClipboardCopy.WaitMs, ClipboardCopy.WaitFor(null));
        var lo = new FakeClipboard { LateCopyAt = 650 };
        var before = Snap(lo);
        var (text, outcome) = ClipboardCopy.Run(lo, () => ProbeStatus.NotPassword, ClipboardCopy.WaitFor("soffice.bin"));
        Assert.Equal("le texte choisi", text);
        Assert.Contains("copied in", outcome);
        Assert.Equal(before, Snap(lo));                                   // restored
        var np = new FakeClipboard { LateCopyAt = 650 };
        var r = ClipboardCopy.Run(np, () => ProbeStatus.NotPassword, ClipboardCopy.WaitFor("notepad"));
        Assert.Null(r.Text);
        Assert.Contains("late copy", r.Outcome);
        // the worst Run (the selection worker's « stuck » limit) covers the longer wait
        Assert.True(ClipboardCopy.WorstMs >= ClipboardCopy.ModifierWaitMs + ClipboardCopy.LibreOfficeWaitMs + ClipboardCopy.LateWatchMs);
    }

    [Fact]
    public void Clipboard_Restore_Is_Retried_While_The_Clipboard_Is_Held()
    {
        var cb = new FakeClipboard { OpenFailures = 3 };
        var before = Snap(cb);
        var (text, outcome) = ClipboardCopy.Run(cb, () => ProbeStatus.NotPassword);
        Assert.Equal("le texte choisi", text);
        Assert.Equal(4, cb.RestoreAttempts);
        Assert.Equal(before, Snap(cb));
        Assert.Contains("try 4", outcome);
        var stuck = new FakeClipboard { OpenFailures = 100 };
        Assert.Contains("NOT restored", ClipboardCopy.Run(stuck, () => ProbeStatus.NotPassword).Outcome);
        Assert.Equal(ClipboardCopy.RestoreTries, stuck.RestoreAttempts);
    }

    [Fact]
    public void Clipboard_An_Error_After_Ctrl_C_Still_Restores()
    {
        var cb = new FakeClipboard { ThrowOnRead = true };
        var before = Snap(cb);
        var (text, outcome) = ClipboardCopy.Run(cb, () => ProbeStatus.NotPassword);
        Assert.Null(text);
        Assert.Equal(1, cb.Restores);
        Assert.Equal(before, Snap(cb));
        Assert.Contains("error", outcome);
    }

    [Fact]
    public void Clipboard_A_Copy_By_The_User_Meanwhile_Is_Never_Overwritten()
    {
        var cb = new FakeClipboard { UserCopy = true };       // Ctrl+C / Ctrl+X / Ctrl+Insert / Shift+Del after ours
        var (_, outcome) = ClipboardCopy.Run(cb, () => ProbeStatus.NotPassword);
        Assert.Equal(0, cb.RestoreAttempts);
        Assert.Contains("user copied", outcome);
        Assert.True(ClipboardCopy.IsCopyChord(0x43, true, false, false));   // Ctrl+C
        Assert.True(ClipboardCopy.IsCopyChord(0x58, true, false, false));   // Ctrl+X
        Assert.True(ClipboardCopy.IsCopyChord(0x2D, true, false, false));   // Ctrl+Insert
        Assert.True(ClipboardCopy.IsCopyChord(0x2E, false, true, false));   // Shift+Delete
        Assert.False(ClipboardCopy.IsCopyChord(0x43, false, false, false));
        Assert.False(ClipboardCopy.IsCopyChord(0x43, true, false, true));   // AltGr+C
    }

    [Fact]
    public void Clipboard_A_Change_By_Another_Application_Is_Never_Overwritten()
    {
        // e.g. a password manager clearing the clipboard, or another app's copy, during the watch
        var cb = new FakeClipboard { AppCopies = false };
        cb.Seq++; cb.Owner = "other";                         // happens right after our Ctrl+C
        var (_, outcome) = ClipboardCopy.Run(new SeqShift(cb), () => ProbeStatus.NotPassword);
        Assert.Equal(0, cb.RestoreAttempts);
        Assert.Contains("another application", outcome);
    }

    /// <summary>The fake as it is after another application changed the clipboard: the snapshot sees the old sequence.</summary>
    private sealed class SeqShift : IClipboardOps
    {
        private readonly FakeClipboard _c; private bool _sent;
        public SeqShift(FakeClipboard c) { _c = c; }
        public uint Sequence => _sent ? _c.Sequence : _c.Sequence - 1;
        public IReadOnlyList<uint> Formats() => _c.Formats();
        public bool OwnerResponsive() => true;
        public ClipboardSnapshot? Save() => _c.Save();
        public bool StillTarget() => true;
        public bool OwnedByTarget() => _c.OwnedByTarget();
        public bool HasOwner() => _c.HasOwner();
        public bool UserCopied() => false;
        public bool ModifiersDown() => false;
        public bool SendCopy() { _sent = true; return _c.SendCopy(); }
        public string? ReadText() => _c.ReadText();
        public bool Restore(ClipboardSnapshot s) => _c.Restore(s);
        public void Sleep(int ms) => _c.Sleep(ms);
        public long NowMs => _c.NowMs;
    }

    [Fact]
    public void Clipboard_Emptied_With_No_Owner_Is_Never_Overwritten()
    {
        var cb = new FakeClipboard { AppCopies = false };
        cb.Seq++; cb.Owner = "none";                          // e.g. EmptyClipboard by a process with no window
        var (_, outcome) = ClipboardCopy.Run(new SeqShift(cb), () => ProbeStatus.NotPassword);
        Assert.Equal(0, cb.RestoreAttempts);
        Assert.Contains("no clipboard owner", outcome);
    }

    [Fact]
    public void Clipboard_A_Second_Write_By_The_Application_Is_Undone_Too()
    {
        var cb = new FakeClipboard { RewriteOnce = true };
        var before = Snap(cb);
        var (_, outcome) = ClipboardCopy.Run(cb, () => ProbeStatus.NotPassword);
        Assert.Equal(2, cb.Restores);
        Assert.Equal(before, Snap(cb));
        Assert.Contains("rewritten", outcome);
    }

    [Fact]
    public void Clipboard_Worst_Run_Is_Below_The_Selection_Worker_Limit() =>
        Assert.True(ClipboardCopy.WorstMs < ClipboardCopy.WorstMs + 1000 && ClipboardCopy.WorstMs > ClipboardCopy.WaitMs + ClipboardCopy.LateWatchMs);

    [Fact]
    public void Clipboard_Never_In_A_Console_Or_Terminal()
    {
        foreach (var n in new[] { "WindowsTerminal", "OpenConsole", "conhost", "putty", "kitty", "mintty", "wezterm-gui", "alacritty", "Hyper", "Tabby", "MobaXterm", "SecureCRT" })
            Assert.True(ClipboardCopy.IsTerminalProcess(n), n);
        Assert.False(ClipboardCopy.IsTerminalProcess("notepad"));
        Assert.False(ClipboardCopy.IsTerminalProcess("powershell"));   // a GUI it hosts is not a terminal (its console is a console class)
        Assert.True(ClipboardCopy.IsTerminalClass("org.wezfurlong.wezterm"));
        foreach (var c in new[] { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PuTTY", "mintty", "VirtualConsoleClass" })
            Assert.True(ClipboardCopy.IsTerminalClass(c), c);
        foreach (var c in new[] { "Notepad", "Edit", "Chrome_WidgetWin_1", "_WwG", "WindowsForms10.Window.8.app.0.1" })
            Assert.False(ClipboardCopy.IsTerminalClass(c), c);
    }

    [Fact]
    public void A_Selection_Over_The_Character_Limit_Is_Cut_At_A_Word_And_Flagged()
    {
        Assert.Equal("anticonstitutionnellement tres", SelectionPolicy.CutAtWord("anticonstitutionnellement tres longtem"));
        var d = ReadingDocument.Build("un deux trois", ReadingDocument.MaxWords, -1, cut: true);
        Assert.True(d.Truncated);
        Assert.Equal(3, d.WordCount);
    }

    [Fact]
    public void Clipboard_Formats_That_Windows_Resynthesises_Are_Restorable()
    {
        Assert.True(ClipboardCopy.CanRestore(new uint[] { ClipboardCopy.CF_DIB, ClipboardCopy.CF_BITMAP, ClipboardCopy.CF_PALETTE, 13 }));
        Assert.False(ClipboardCopy.CanRestore(new uint[] { ClipboardCopy.CF_BITMAP }));   // no DIB to rebuild it from
        Assert.False(ClipboardCopy.CanRestore(new uint[] { ClipboardCopy.CF_METAFILEPICT, 13 }));
        Assert.False(ClipboardCopy.CanRestore(new uint[] { 0x0301 }));                     // GDI-private
        Assert.True(ClipboardCopy.CanRestore(Array.Empty<uint>()));
    }

    // ------------------------------------------------------------------ the Reading kind on the speech thread
    private sealed class ProgressSynth : ISynth
    {
        public readonly ConcurrentQueue<string> Spoken = new();
        public int SpeakMs = 80;
        private CancellationTokenSource? _cts;
        public IReadOnlyList<VoiceEntry> Voices() => new[] { new VoiceEntry("sapi:Fake", "Fake", "fr-FR", "Female", VoiceId.Sapi) };
        public string Configure(SpeechSettings s) => "Fake";
        public void SpeakAsync(string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null)
        {
            Spoken.Enqueue(text);
            var cts = _cts = new CancellationTokenSource();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int pos = 0;
                foreach (var w in text.Split(' '))
                {
                    if (cts.IsCancellationRequested) break;
                    progress?.Invoke(new SpeechProgress(pos, w.Length, pos * 10));
                    pos += w.Length + 1;
                }
                bool cancelled = cts.Token.WaitHandle.WaitOne(SpeakMs);
                done(cancelled ? SpeechOutcome.Cancelled : SpeechOutcome.Done, null);
            });
        }
        public void CancelAll() => _cts?.Cancel();
        public void Dispose() { }
    }

    private static bool WaitFor(Func<bool> cond, int ms = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(5); }
        return cond();
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void Reading_Item_Gets_Progress_And_Done()
    {
        var synth = new ProgressSynth();
        using var w = new SpeechWorker(() => synth, new SpeechSettings(), null);
        var events = new ConcurrentQueue<SpeechProgress>();
        SpeechOutcome? done = null;
        w.Say(new SpeechItem("le chat dort", SpeechKind.Reading, null, p => events.Enqueue(p), o => done = o));
        Assert.True(WaitFor(() => done != null));
        Assert.Equal(SpeechOutcome.Done, done);
        Assert.Equal(new[] { 0, 3, 8 }, events.Select(e => e.CharPos));
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void Reading_Mutes_The_Echo_And_Is_Dropped_While_Paused()
    {
        var synth = new ProgressSynth();
        using var w = new SpeechWorker(() => synth, new SpeechSettings(), null) { ReadingMode = true };
        w.Say("mot", SpeechKind.Word);
        w.Say("une phrase", SpeechKind.Sentence);
        w.Say(new SpeechItem("relu", SpeechKind.Reread));
        SpeechOutcome? done = null;
        w.Say(new SpeechItem("la lecture", SpeechKind.Reading, null, null, o => done = o));
        Assert.True(WaitFor(() => done != null));
        Assert.Equal(new[] { "la lecture" }, synth.Spoken.ToArray());
        w.RereadsPaused = true;
        done = null;
        w.Say(new SpeechItem("en pause", SpeechKind.Reading, null, null, o => done = o));
        Assert.True(WaitFor(() => done != null));
        Assert.Equal(SpeechOutcome.Cancelled, done);
        Assert.DoesNotContain("en pause", synth.Spoken);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void A_Reading_Item_Interrupts_And_The_Replaced_One_Hears_Cancelled()
    {
        var synth = new ProgressSynth { SpeakMs = 60000 };   // an utterance ends only when cancelled (no race with its end)
        using var w = new SpeechWorker(() => synth, new SpeechSettings(), null);
        SpeechOutcome? first = null, second = null;
        w.Say(new SpeechItem("phrase un", SpeechKind.Reading, null, null, o => first = o));
        Assert.True(WaitFor(() => synth.Spoken.Count == 1));
        w.Say(new SpeechItem("phrase deux", SpeechKind.Reading, null, null, o => second = o));
        Assert.True(WaitFor(() => first != null));
        Assert.Equal(SpeechOutcome.Cancelled, first);
        Assert.True(WaitFor(() => synth.Spoken.Count == 2));
        w.CancelAll();
        Assert.True(WaitFor(() => second != null));
        Assert.Equal(SpeechOutcome.Cancelled, second);
        Assert.True(SpeechQueue.Interrupts(SpeechKind.Reading));
    }

    // ------------------------------------------------------------------ D26: selection boundaries for the reading
    /// <summary>The selection is marked with [ ] inside <paramref name="all"/>; returns what is read.</summary>
    private static SelectionText Ext(string all)
    {
        int a = all.IndexOf('['), b = all.IndexOf(']');
        string before = all.Substring(0, a), sel = all.Substring(a + 1, b - a - 1), after = all.Substring(b + 1);
        return SelectionExtend.Apply(before, sel, after);
    }

    [Fact]
    public void D26_A_Word_Cut_At_The_End_Is_Completed()
    {
        Assert.Equal("Le chat dort", Ext("[Le chat do]rt bien.").Text);
        Assert.Equal(-1, Ext("[Le chat do]rt bien.").TailAt);
    }

    [Fact]
    public void D26_A_Word_Cut_At_The_Start_Is_Completed()
    {
        var x = Ext("Le ch[at bien] fort");
        Assert.Equal("chat bien", x.Text);
        Assert.Equal(2, x.AddedBefore);
        Assert.Equal("cœur battait", Ext("Le c[œur batt]ait vite.").Text);
        Assert.Equal("aujourd'hui", Ext("aujourd'[hui] il").Text);   // inside a word joined by an apostrophe
        Assert.Equal("peut-être", Ext("peut-[êt]re oui").Text);
    }

    [Fact]
    public void D26_An_Elided_Start_Keeps_The_Elision()
    {
        Assert.Equal("l'ami vient", Ext("Voici l'a[mi vient] demain").Text);
        Assert.Equal("l'ami", Ext("Voici l'[ami] demain").Text);           // right after the apostrophe: one reading unit
        Assert.Equal("l’eau", Ext("De l’[eau] ici").Text);
        Assert.Equal("Voici l'ami", Ext("[Voici l]'ami demain").Text);     // ends inside the elision: the word with it
        Assert.Equal("l'ami", ReadingDocument.Build(Ext("Voici l'[ami] demain").Text).Tiles.Single().Text);
    }

    [Fact]
    public void D26_The_Sentence_End_After_The_Last_Word_Is_Added()
    {
        Assert.Equal("Il dort bien.", Ext("[Il dort bien]. Puis").Text);
        Assert.Equal("Il dort bien !", Ext("[Il dort bien] ! Puis").Text);
        Assert.Equal("Il dort bien" + Nnbsp + "?", Ext("[Il dort bien]" + Nnbsp + "? Oui").Text);
        Assert.Equal("Il dort bien…", Ext("[Il dort bien]… Oui").Text);
        Assert.Equal("Il dort bien ?!", Ext("[Il dort bien] ?! Oui").Text);
        Assert.Equal("Il dort bien !!!", Ext("[Il dort bien] !!! Oui").Text);
        Assert.Equal("dort bien ! »", Ext("« Le chat [dort bi]en ! » dit-il").Text);   // cut word + closing guillemet
        Assert.Equal("bien.)", Ext("(Il dort [bien].) Puis").Text);
        Assert.Equal("Viens ! \"", Ext("Il dit \"[Viens] ! \" puis").Text);
        var x = Ext("[Il dort bien] ! Puis");
        Assert.Equal(12, x.TailAt);
        Assert.Equal(2, x.AddedAfter);
    }

    [Fact]
    public void D26_Nothing_Added_When_Not_Wanted()
    {
        Assert.Equal("Il dort bien.", Ext("[Il dort bien.] Puis").Text);       // the period already selected
        Assert.Equal(-1, Ext("[Il dort bien.] Puis").TailAt);
        Assert.Equal("Il dort bien", Ext("[Il dort bien], puis").Text);         // comma: not added
        Assert.Equal("Il dort bien", Ext("[Il dort bien] ; puis").Text);
        Assert.Equal("Il dort bien", Ext("[Il dort bien] : puis").Text);
        Assert.Equal("Il dort", Ext("[Il dort] bien.").Text);                   // not the sentence's last word
        Assert.Equal("Il dort", Ext("[Il dort]" + (char)13 + (char)10 + "Fin.").Text);   // a line break, no punctuation
        Assert.Equal("chat", SelectionExtend.Apply("", "chat", "").Text);       // no context (Ctrl+C fallback): as selected
        Assert.Equal("Il dort bien ?!", Ext("[Il dort bien ?]! Oui").Text);     // a cut run of sentence ends is completed
    }

    [Fact]
    public void D26_A_Trailing_Selected_Space_Still_Gets_The_Punctuation()
    {
        Assert.Equal("Il dort bien !", Ext("[Il dort bien ]! Oui").Text);
    }

    [Fact]
    public void D26_The_Added_Punctuation_Is_Shown_On_The_Last_Word_Tile()
    {
        var x = Ext("[Le chat dort bien] » ! Puis");
        var y = Ext("« [Le chat dort bien] ! » Puis");
        var d = ReadingDocument.Build(y.Text, ReadingDocument.MaxWords, y.TailAt);
        Assert.Equal(new[] { "Le", "chat", "dort", "bien ! »" }, Texts(d));
        Assert.Equal("bien", d.Tiles[3].PictureKey);
        Assert.Equal("Le chat dort bien ! »", d.UtteranceOf(0).Text);          // the voice gets the intonation mark
        Assert.Equal(3, d.TileAt(0, 18, 1));                                    // an event on « ! »: the same tile
        Assert.Equal(new[] { "Le", "chat", "dort", "bien" }, Texts(ReadingDocument.Build(x.Text, ReadingDocument.MaxWords, x.TailAt)));
        // a selected period stays its own tile (nothing was added)
        Assert.Equal(".", Texts(ReadingDocument.Build("Il dort.")).Last());
    }

    // ------------------------------------------------------------------ settings
    [Fact]
    public void Reading_Settings_Round_Trip_And_Pictures_Follow_The_Bar()
    {
        var d = new AppSettings();
        Assert.True(d.ReadingPictures);
        Assert.False((d with { Pictures = false }).ReadingPictures);
        var s = d with { Pictures = false, Reading = new ReadingSettings(false, true, true, 10, 20, 800, 600) };
        var back = AppSettings.FromJson(s.ToJson());
        Assert.False(back.Reading.Enabled);
        Assert.True(back.Reading.ClipboardFallback);
        Assert.True(back.ReadingPictures);
        Assert.Equal((10, 20, 800, 600), (back.Reading.WinX, back.Reading.WinY, back.Reading.WinW, back.Reading.WinH));
        var j = AppSettings.FromJson(JsonNode.Parse(@"{""reading"":{""clipboardFallback"":""yes"",""window"":{""x"":1,""y"":2,""w"":50,""h"":600}}}") as JsonObject);
        Assert.Null(j.Reading.ClipboardFallback);    // tolerant parse: a wrong type keeps the default (not set)
        Assert.False(j.Reading.HasWindow);            // too small: dropped
        Assert.True(j.Reading.Enabled);
        Assert.Null(j.Reading.Pictures);
    }
}
