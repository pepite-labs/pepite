using System;
using System.Collections.Generic;
using System.Linq;

namespace Predict.Keyboard;

/// <summary>Where the caret landed after leaving an <see cref="EditSession"/>.</summary>
internal enum Landing { SameWord, SameSentence, Elsewhere, Unknown }

internal enum RereadKind { None, Word, Sentence }

/// <param name="Reason">why nothing is said (log: <c>reread skip(reason)</c>), or what is said.</param>
internal readonly record struct RereadDecision(RereadKind Kind, string Text, string Reason)
{
    public static RereadDecision Skip(string reason) => new(RereadKind.None, "", reason);
}

/// <summary>
/// D23 (plan 06), pure: where the caret landed (<see cref="Classify"/>) and what is re-read (<see cref="Decide"/>).
/// <list type="table">
/// <item>Same word → nothing. Another word of the same sentence → the modified word. Another sentence or window → the
/// whole modified sentence only (never the word too); a sentence cut by the read window → the modified word only.</item>
/// <item>Plain typing at the end of the text (the session did not start « in the middle ») → nothing: the echo (D7)
/// covers it. A word already said by the echo is not repeated.</item>
/// <item>A failed or late read, or a mirror that cannot be trusted → nothing (silence rather than a wrong reading).</item>
/// </list>
/// </summary>
internal static class EditClassifier
{
    /// <param name="read">the read at the new caret (null: failed / timed out / overtaken).</param>
    /// <param name="sameFg">the jump stayed in the session's foreground window.</param>
    public static Landing Classify(EditState s, TextWindow? read, bool sameFg)
    {
        if (!sameFg) return Landing.Elsewhere;
        if (read is null) return Landing.Unknown;
        var m = s.View;
        var r = new TextAnalysis(read.Text, read.Caret, read.TruncStart, read.TruncEnd);
        if (!m.HasSentence || !r.HasSentence) return Landing.Elsewhere;
        // equal keys, or the same words up to accents / case (Word's AutoCorrect « tres » -> « très », « ca » -> « ça »).
        // Never one changed letter here: « Il mange. » and « Il range. » are two sentences
        bool same = m.Key == r.Key || DocText.SameWords(m.SentenceTokens, r.SentenceTokens, loose: false);
        // Win32 Edit: absolute offsets tell two identical sentences apart (« Oui. Oui. »)
        if (same && s.Base >= 0 && read.Base >= 0) same = s.Base + m.Start == read.Base + r.Start;
        if (!same) return Landing.Elsewhere;
        int k = s.ModifiedWord is { } w ? m.IndexOf(w) : m.WordIndexAt(m.Caret);
        int j = r.WordIndexAt(r.Caret);
        return k >= 0 && k == j ? Landing.SameWord : Landing.SameSentence;
    }

    /// <summary>
    /// Say what the document holds rather than what was typed, when the landing read shows it (owner's Word run: the
    /// mirror said « tres » where AutoCorrect had written « très », mispronounced). Only from the landing read of the same
    /// window — a read the session trusts (it checked the field before reading any text). Same sentence: the read's
    /// word at the modified word's index, if it is that word up to AutoCorrect (accents, or one letter for 4+ letters:
    /// the sentence is established, only the spelling is taken). Another sentence: the one sentence of the read window
    /// with the same words up to accents / case (with Win32 offsets, at the same place, one letter is allowed too — the
    /// place proves it is that sentence); none or several → the mirror's text, as before.
    /// </summary>
    public static RereadDecision FromDocument(RereadDecision d, EditState s, Landing where, TextWindow? read, bool sameFg)
    {
        if (d.Kind == RereadKind.None || read is null || !sameFg) return d;
        var m = s.View;
        var r = new TextAnalysis(read.Text, read.Caret, read.TruncStart, read.TruncEnd);
        if (d.Kind == RereadKind.Word)
        {
            if (where != Landing.SameSentence || s.ModifiedWord is not { } w) return d;
            int k = m.IndexOf(w);
            var rt = r.SentenceTokens;
            if (k < 0 || k >= rt.Count || !DocText.Close(w.Norm, rt[k].Norm) || rt[k].Text == d.Text) return d;
            return d with { Text = rt[k].Text, Reason = d.Reason + " (document)" };
        }
        // a sentence: look for it in the read window
        var mine = m.SentenceTokens;
        string? found = null;
        int hits = 0;
        bool offsets = s.Base >= 0 && read.Base >= 0;
        var all = r.Tokens;
        int ti = 0;
        foreach (var (st, en) in Predict.Core.Tokenizer.Sentences(read.Text))
        {
            // the read's tokens are in order: walk them once for all sentences
            while (ti < all.Count && all[ti].End <= st) ti++;
            var toks = new System.Collections.Generic.List<Predict.Core.Token>();
            for (int k = ti; k < all.Count && all[k].Start < en; k++)
                if (all[k].Start >= st && all[k].End <= en && all[k].Kind != Predict.Core.TokenKind.Space) toks.Add(all[k]);
            if (offsets && s.Base + m.Start != read.Base + st) continue;
            if (!DocText.SameWords(mine, toks, loose: offsets)) continue;
            hits++;
            found = read.Text.Substring(st, en - st).Trim();
        }
        if (hits != 1 || found is null || found == d.Text) return d;
        return d with { Text = found, Reason = d.Reason + " (document)" };
    }

    public static RereadDecision Decide(EditState s, Landing where)
    {
        if (where == Landing.Unknown) return RereadDecision.Skip("unknown landing");
        if (s.Dirty) return RereadDecision.Skip("unseen change");
        if (!s.Middle) return RereadDecision.Skip("not inside the text");
        if (where == Landing.SameWord) return RereadDecision.Skip("same word");
        string? word = s.WordEdited && s.ModifiedWord is { } t ? t.Text : null;
        if (where == Landing.SameSentence)
        {
            if (word is null) return RereadDecision.Skip(s.WordEdited ? "no word left" : "word unchanged");
            return s.WordSpoken ? RereadDecision.Skip("word already spoken") : new RereadDecision(RereadKind.Word, word, "word");
        }
        if (!s.Edited) return RereadDecision.Skip("sentence unchanged");
        if (s.View.Partial)
            return word is null || s.WordSpoken ? RereadDecision.Skip("partial sentence")
                                                : new RereadDecision(RereadKind.Word, word, "word (partial sentence)");
        var text = s.View.Sentence.Trim();
        return text.Length == 0 ? RereadDecision.Skip("empty sentence") : new RereadDecision(RereadKind.Sentence, text, "sentence");
    }
}

/// <summary>What a caret jump does to the edit session: freeze it until the read classifies the landing, keep it
/// (Esc, Delete applied as an edit, the taskbar / our windows), or end it silently (the text changed unseen: Ctrl+V,
/// Ctrl+Z, Insert...).</summary>
internal enum SessionMove { Leave, Keep, End }

/// <summary>What the controller knows about one caret read, for <see cref="RereadFlow"/>.</summary>
/// <param name="Ok">the read reached the text (status Ok, a window) and was applied (nothing typed since the jump,
/// same window, in time).</param>
/// <param name="Latest">it is the latest read requested: an older one never decides a landing.</param>
/// <param name="IsDelete">the read follows a Delete applied to the session (a rebase, not a leave).</param>
/// <param name="Version">the tracker version the read was requested at: a begin / rebase is installed only if the
/// tracker still has it under the commit lock (no key slipped in between the read and the commit).</param>
internal readonly record struct ReadFacts(bool Ok, bool Password, bool Latest, bool IsDelete, IntPtr Fg, TextWindow? Window,
                                          long Version = 0);

/// <param name="Replace">false: leave the session untouched (nothing to decide from this read).</param>
/// <param name="Next">the session to install when <paramref name="Replace"/> (null = none).</param>
/// <param name="Drop">a password field was read: whatever the session is now, it is dropped.</param>
internal sealed record RereadPlan(RereadDecision Decision, Landing Where, bool Replace, EditSession? Next, bool Drop = false,
                                 long Version = 0);

/// <summary>
/// The begin / rebase / classify / commit rules of the re-read (D23, plan 06), pure so the thread interleavings are
/// unit-tested. <see cref="Plan"/> runs on the reader thread on a snapshot, off the controller's lock;
/// <see cref="Commit"/> runs under the lock and decides what is installed and whether anything is said.
/// </summary>
internal static class RereadFlow
{
    /// <summary>What a caret jump does to the session (hook thread, under the lock). A leave while the first-character
    /// field probe is still pending drops the session: the characters typed since the last jump come from a field that
    /// was never proven to be a plain text field (and that probe's answer, now stale, can no longer drop it).</summary>
    /// <summary>Enter (hook thread, under the lock): it often moves the focus (two-step logins). Edits not yet covered
    /// by a NotPassword answer may be in a field that is about to be left unseen: the session is dropped.</summary>
    public static EditSession? OnEnter(EditSession? s) => s is { Proven: false } ? null : s;

    public static EditSession? OnJump(EditSession? s, SessionMove move, bool firstCharProbePending)
    {
        if (s is null || move == SessionMove.End) return null;
        if (move == SessionMove.Leave)
        {
            if (firstCharProbePending) return null;
            s.Leave();
        }
        return s;
    }

    public static RereadPlan Plan(EditState? st, ReadFacts f)
    {
        if (f.Password) return new(RereadDecision.Skip("password"), Landing.Unknown, true, null, Drop: true);   // never next to a password field
        if (st is null || !st.Left)
        {
            // no leave pending: start from this read (after Delete: rebase, keeping the word / sentence state)
            if (!f.Ok || f.Window is null) return new(RereadDecision.Skip("no read"), Landing.Unknown, false, null);
            var next = st != null && f.IsDelete && f.Fg == st.Fg && !st.Dirty
                ? EditSession.Continue(st, f.Window, sameWord: true) : EditSession.Begin(f.Window, f.Fg);
            return new(RereadDecision.Skip("begin"), Landing.Unknown, true, next, Version: f.Version);
        }
        if (!f.Latest) return new(RereadDecision.Skip("superseded"), Landing.Unknown, false, null);
        Landing where;
        RereadDecision d;
        if (st.TypedWhileLeft && !f.Ok) { where = Landing.Unknown; d = RereadDecision.Skip("typed meanwhile"); }
        else
        {
            where = f.Fg != st.Fg ? Landing.Elsewhere : f.Ok ? EditClassifier.Classify(st, f.Window, true) : Landing.Unknown;
            d = EditClassifier.Decide(st, where);
            if (f.Ok) d = EditClassifier.FromDocument(d, st, where, f.Window, f.Fg == st.Fg);
        }
        EditSession? follow = null;
        if (f.Ok && f.Window != null && !st.TypedWhileLeft)
            follow = where switch
            {
                Landing.SameWord => EditSession.Continue(st, f.Window, sameWord: true),
                Landing.SameSentence => EditSession.Continue(st, f.Window, sameWord: false),
                _ => EditSession.Begin(f.Window, f.Fg),
            };
        return new(d, where, true, follow, Version: f.Version);
    }

    /// <summary>Under the controller's lock. <paramref name="current"/> is the session now, <paramref name="old"/> the
    /// one <paramref name="st"/> was copied from. Returns the session to keep and whether the decision is spoken.</summary>
    /// <param name="trackerVersion">the tracker's version now (under the lock).</param>
    public static (EditSession? Session, bool Speak, string? Why) Commit(EditSession? current, EditSession? old, EditState? st,
                                                                         RereadPlan p, bool enabled, long trackerVersion)
    {
        if (p.Drop) return (null, false, "password");
        if (!p.Replace) return (current, false, null);
        if (current != old) return (current, false, "superseded");                // dropped or replaced meanwhile (password, pause…)
        if (old != null && st != null)
        {
            if (old.Leaves != st.Leaves) return (current, false, "left again");  // a newer leave: its own read decides
            if (!st.Left && (old.Left || old.Edits != st.Edits)) return (current, false, "edited meanwhile");
        }
        if (!enabled) return (null, false, "paused");
        // a begin / rebase describes the text at the read: a key typed since makes it wrong
        if ((st is null || !st.Left) && trackerVersion != p.Version) return (current, false, "typed since the read");
        // privacy: every edit in the mirror must have been seen in a field proven not to be a password field
        if (st != null && st.Left && p.Decision.Kind != RereadKind.None && !st.Proven) return (null, false, "field not proven");
        // keys after the read: the landing stands, but the next session would miss them
        var next = old != null && st != null && st.Left && old.TypedWhileLeft ? null : p.Next;
        return (next, p.Decision.Kind != RereadKind.None, null);
    }
}

/// <summary>
/// D17 amendment (owner, 2026-11-14), pure: the sentence end typed after a caret jump into a sentence (an edit) says the
/// whole sentence as the document holds it, read at the caret right after the key — not only the words typed since the
/// jump (« tape. » said « tape »). <see cref="Find"/> picks, in the read window, the last sentence starting before the
/// caret that ends with a sentence end and with the typed words (up to AutoCorrect, <see cref="DocText.Close"/>), only
/// spaces / the words typed since between it and the caret; null (→ a second read, then the typed words) when none,
/// when the window cuts it, or when the caret's own sentence is unfinished and ends with the typed words (the period is
/// not in the document yet) or nothing was typed. <see cref="Same"/>: the D23 re-read of that sentence on leaving is skipped when it did not change.
/// </summary>
internal static class SentenceDocRead
{
    public static string? Find(TextWindow w, string typed)
    {
        var mine = Predict.Core.Tokenizer.Tokenize(typed ?? "").Where(t => t.Kind != Predict.Core.TokenKind.Space).ToList();
        var all = Predict.Core.Tokenizer.Tokenize(w.Text);
        var sents = Predict.Core.Tokenizer.Sentences(w.Text);
        for (int i = sents.Count - 1; i >= 0; i--)
        {
            var (st, en) = sents[i];
            if (st >= w.Caret) continue;
            // the caret inside a sentence (words typed after the period run into the text that followed): only its part
            // before the caret counts, and it is unfinished
            en = Math.Min(en, w.Caret);
            var text = w.Text.Substring(st, en - st).Trim();
            int last = text.Length - 1;
            while (last >= 0 && Predict.Core.Tokenizer.IsClosing(text[last])) last--;
            var toks = all.Where(t => t.Start >= st && t.End <= en && t.Kind != Predict.Core.TokenKind.Space).ToList();
            if (last < 0 || !Predict.Core.Tokenizer.IsSentenceEnd(text[last]))
            {
                // the caret's sentence is not finished. Either the period is not in the document yet (the read came
                // before the app handled the key: « Le chat dort. Le chien dort| »): never the previous sentence; or the
                // user already typed the start of the next one after the period: look at the sentence before it
                if (mine.Count == 0 || EndsWith(toks, mine)) return null;
                continue;
            }
            // cut by the window: the read did not reach its start
            if (i == 0 && w.TruncStart && !HasBreak(w.Text, 0, st)) return null;
            if (EndsWith(toks, mine) && Between(w.Text, en, w.Caret, i + 1 < sents.Count ? sents[i + 1].Start : -1)) return text;
            return null;   // the nearest finished sentence before the caret is not the one typed: say the typed words
        }
        return null;
    }

    /// <summary>The same sentence up to accents / case / spaces (the D23 re-read of it would say nothing new).</summary>
    public static bool Same(string a, string b)
    {
        var x = Predict.Core.Tokenizer.Tokenize(a).Where(t => t.Kind != Predict.Core.TokenKind.Space).ToList();
        var y = Predict.Core.Tokenizer.Tokenize(b).Where(t => t.Kind != Predict.Core.TokenKind.Space).ToList();
        return DocText.SameWords(x, y, loose: false);
    }

    /// <summary>The sentence's last words are the typed ones (the sentence end itself included in both).</summary>
    private static bool EndsWith(List<Predict.Core.Token> sentence, List<Predict.Core.Token> typed)
    {
        // the typed text is words only (the echo drops punctuation): compare the sentence's words
        var words = sentence.Where(TextAnalysis.IsWord).ToList();
        var mine = typed.Where(TextAnalysis.IsWord).ToList();
        if (mine.Count > words.Count) return false;
        for (int k = 1; k <= mine.Count; k++)
        {
            if (DocText.Close(mine[^k].Norm, words[^k].Norm)) continue;
            // the first word typed may be the end of a word: the echo forgot its start at the jump (Backspace past the
            // known text, then « pe. » for « tape. »: the owner's log said « ape »)
            if (k == mine.Count && DocText.Fold(words[^k].Norm).EndsWith(DocText.Fold(mine[^k].Norm), StringComparison.Ordinal)) continue;
            return false;
        }
        return true;
    }

    /// <summary>Between the sentence's end and the caret: spaces, or the next sentence being typed (the read came a little
    /// after the key).</summary>
    private static bool Between(string text, int end, int caret, int nextStart)
    {
        if (caret < end) return true;
        for (int i = end; i < caret; i++)
            if (!Predict.Core.Tokenizer.IsSpace(text[i]) && !Predict.Core.Tokenizer.IsClosing(text[i]) && !Predict.Core.Tokenizer.IsBreak(text[i]))
                return nextStart >= 0 && nextStart <= i && caret - nextStart <= 40;
        return true;
    }

    private static bool HasBreak(string text, int from, int to)
    {
        for (int i = from; i < to; i++) if (Predict.Core.Tokenizer.IsBreak(text[i])) return true;
        return false;
    }
}

/// <summary>
/// EVENT_OBJECT_FOCUS filter (plan 06 review): the focus moved inside the foreground window without a key, click or
/// window switch (an in-page dialog with an autofocused password field, a two-step login). Pure; the controller keeps
/// one on the hook thread. Kept: a focus change in the foreground window to another object than the last one seen,
/// while no mouse button is held (a click's own read follows its button-up). Ignored: other windows (the foreground
/// path handles them), the taskbar / tray and our own windows, the same object again.
/// </summary>
internal sealed class FocusFilter
{
    private IntPtr _hwnd;
    private int _obj, _child;

    public bool ShouldJump(IntPtr root, IntPtr fg, IntPtr hwnd, int idObject, int idChild, bool buttonDown, bool shellOrOwn)
    {
        if (root != fg || fg == IntPtr.Zero || shellOrOwn) return false;
        bool same = hwnd == _hwnd && idObject == _obj && idChild == _child;
        _hwnd = hwnd; _obj = idObject; _child = idChild;
        return !same && !buttonDown;
    }
}

/// <summary>Word comparison « up to AutoCorrect » for the re-read (pure): same normalised word, or the same letters
/// without accents (« tres » / « très », « ca » / « ça »), or — for words of 4+ letters — one letter changed, added or
/// removed (typos the app fixed). Punctuation tokens must be equal.</summary>
internal static class DocText
{
    public static string Fold(string s)
    {
        var dcmp = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(dcmp.Length);
        foreach (var c in dcmp)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    public static bool Close(string a, string b)
    {
        if (a == b) return true;
        if (a.Length == 0 || b.Length == 0) return false;
        bool wa = char.IsLetter(a[0]), wb = char.IsLetter(b[0]);
        if (!wa || !wb) return false;
        if (Fold(a) == Fold(b)) return true;
        return a.Length >= 4 && b.Length >= 4 && WithinOneEdit(Fold(a), Fold(b));
    }

    /// <summary>Equal, or equal without accents (lower case already: <see cref="Predict.Core.Token.Norm"/>).</summary>
    public static bool FoldEqual(string a, string b) => a == b || (a.Length > 0 && b.Length > 0 && Fold(a) == Fold(b));

    /// <summary>Same number of words / punctuation, each one <see cref="FoldEqual"/> — or, when <paramref name="loose"/>
    /// (the place is already known), <see cref="Close"/>; at least one token.</summary>
    public static bool SameWords(System.Collections.Generic.IReadOnlyList<Predict.Core.Token> a, System.Collections.Generic.IReadOnlyList<Predict.Core.Token> b,
                                 bool loose)
    {
        if (a.Count == 0 || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!(loose ? Close(a[i].Norm, b[i].Norm) : FoldEqual(a[i].Norm, b[i].Norm))) return false;
        return true;
    }

    private static bool WithinOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length > b.Length) i++;
            else if (b.Length > a.Length) j++;
            else { i++; j++; }
        }
        return edits + (a.Length - i) + (b.Length - j) <= 1;
    }
}
