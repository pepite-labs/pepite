using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>
/// A window of text read around the caret (plan 06): <see cref="Text"/> with the caret at <see cref="Caret"/> (UTF-16
/// index). <see cref="TruncStart"/> / <see cref="TruncEnd"/>: the document continues before / after the window.
/// <see cref="Base"/>: absolute offset of <c>Text[0]</c> in the control — known for a Win32 Edit (EM_GETSEL), −1 for UI
/// Automation, which has no offsets.
/// </summary>
internal sealed record TextWindow(string Text, int Caret, bool TruncStart, bool TruncEnd, int Base = -1)
{
    /// <summary>Builds a window whose control characters other than Tab and the line breaks of <see cref="Tokenizer"/>
    /// (Word's cell mark <c>\a</c>, U+FFFC embedded objects) become <c>\n</c>: same length, so offsets are kept, and
    /// they end a sentence as <see cref="ContextExtractor.IsSentenceEnd"/> already treats them.</summary>
    public static TextWindow Of(string text, int caret, bool truncStart, bool truncEnd, int @base = -1)
    {
        text ??= "";
        char[]? a = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if ((char.IsControl(c) && c != '\t' && !Tokenizer.IsBreak(c)) || c == '\uFFFC')
            {
                a ??= text.ToCharArray();
                a[i] = '\n';
            }
        }
        return new TextWindow(a is null ? text : new string(a), Math.Clamp(caret, 0, text.Length), truncStart, truncEnd, @base);
    }
}

/// <summary>
/// The re-read's view of a text and a caret (pure): the sentence holding the caret (<see cref="Tokenizer.Sentences"/>;
/// a caret in the spaces after a sentence still belongs to it, a caret at the start of the next one to the next),
/// whether that sentence is cut by the window (<see cref="Partial"/>), a comparison key, and the words in it.
/// </summary>
internal sealed class TextAnalysis
{
    public string Text { get; }
    public int Caret { get; }
    public IReadOnlyList<Token> Tokens { get; }
    /// <summary>The caret's sentence [Start, End); −1 when the caret is between sentences (blank line…).</summary>
    public int Start { get; } = -1;
    public int End { get; } = -1;
    /// <summary>The sentence may continue beyond the window (no sentence end / break seen on a cut side).</summary>
    public bool Partial { get; }
    private readonly bool _truncEnd;
    private readonly List<int> _sent = new();   // indexes in Tokens of the sentence's non-space tokens

    public TextAnalysis(string text, int caret, bool truncStart, bool truncEnd)
    {
        Text = text ?? "";
        Caret = Math.Clamp(caret, 0, Text.Length);
        _truncEnd = truncEnd;
        Tokens = Tokenizer.Tokenize(Text);
        var sents = Tokenizer.Sentences(Text);
        int idx = -1;
        for (int i = 0; i < sents.Count; i++)
        {
            var (s, e) = sents[i];
            if (s > Caret) break;
            int ext = e;
            while (ext < Text.Length && Tokenizer.IsSpace(Text[ext])) ext++;
            if (Caret <= ext) idx = i;   // a later sentence starting at the caret wins (« dort.|Il »)
        }
        if (idx < 0) return;
        (Start, End) = sents[idx];
        bool startOk = !truncStart || idx > 0 || HasBreak(0, Start);
        bool endOk = !truncEnd || idx < sents.Count - 1 || HasBreak(End, Text.Length) || EndsWithStop();
        Partial = !(startOk && endOk);
        for (int k = 0; k < Tokens.Count; k++)
        {
            var t = Tokens[k];
            if (t.Start >= Start && t.End <= End && t.Kind != TokenKind.Space) _sent.Add(k);
        }
    }

    public bool HasSentence => Start >= 0;
    public string Sentence => HasSentence ? Text.Substring(Start, End - Start) : "";

    /// <summary>The sentence's non-space tokens, normalised (<see cref="Token.Norm"/>): a read and our mirror of the
    /// same sentence compare equal even when the app changed only spaces (Word's no-break space before <c>; : ! ?</c>)
    /// or the apostrophe.</summary>
    public string Key => string.Join("\u0001", _sent.Select(k => Tokens[k].Norm));

    /// <summary>Is the caret « in the middle »: strictly inside a word, or followed in its sentence by anything other
    /// than spaces and the closing <c>. ! ? … » )</c>, or before that sentence end when more text follows it (fixing the
    /// last word of a sentence that is not the last one) — typing there edits existing text. Only typing at the very
    /// end of the text (or right after the final sentence end) is not « middle ».</summary>
    public bool Middle
    {
        get
        {
            if (!HasSentence) return false;
            foreach (var t in Tokens)
                if (IsWord(t) && t.Start < Caret && Caret < t.End) return true;
            for (int i = Caret; i < End; i++)
            {
                char c = Text[i];
                if (!(Tokenizer.IsSpace(c) || Tokenizer.IsSentenceEnd(c) || Tokenizer.IsClosing(c) || Tokenizer.IsBreak(c))) return true;
            }
            if (Caret >= End) return false;   // after the sentence end: a new sentence is being typed
            if (_truncEnd) return true;       // the document goes on after the window
            for (int i = End; i < Text.Length; i++) if (!Tokenizer.IsSpace(Text[i])) return true;
            return false;
        }
    }

    public static bool IsWord(Token t) => t.Kind is TokenKind.Word or TokenKind.Elision or TokenKind.Number;

    /// <summary>Index among the sentence's non-space tokens of the word at <paramref name="pos"/>: containing it,
    /// ending at it, or (when <paramref name="allowRight"/>) starting at it; −1 if none.</summary>
    public int WordIndexAt(int pos, bool allowRight = true)
    {
        int right = -1;
        for (int j = 0; j < _sent.Count; j++)
        {
            var t = Tokens[_sent[j]];
            if (!IsWord(t)) continue;
            if (t.Start < pos && pos <= t.End) return j;
            if (allowRight && t.Start == pos) right = j;
        }
        return right;
    }

    /// <summary>The sentence's non-space tokens, in order.</summary>
    public IReadOnlyList<Token> SentenceTokens => _sentTokens ??= _sent.Select(k => Tokens[k]).ToList();
    private List<Token>? _sentTokens;

    /// <summary>The j-th non-space token of the sentence.</summary>
    public Token SentenceToken(int j) => Tokens[_sent[j]];

    /// <summary>Index among the sentence's non-space tokens of <paramref name="t"/>, −1 when outside the sentence.</summary>
    public int IndexOf(Token t)
    {
        for (int j = 0; j < _sent.Count; j++) if (Tokens[_sent[j]].Start == t.Start) return j;
        return -1;
    }

    private bool HasBreak(int from, int to)
    {
        for (int i = from; i < to; i++) if (Tokenizer.IsBreak(Text[i])) return true;
        return false;
    }

    private bool EndsWithStop()
    {
        int i = End - 1;
        while (i >= Start && (Tokenizer.IsClosing(Text[i]) || Tokenizer.IsSpace(Text[i]))) i--;
        return i >= Start && Tokenizer.IsSentenceEnd(Text[i]);
    }
}

/// <summary>
/// One edit session of D23 (plan 06), pure. Begun from a caret read after a jump; the keys typed, the suggestions
/// inserted, Backspace and Delete are applied to a <b>mirror</b> of the read window, so when the caret leaves, the
/// modified word / sentence is known without reading the text again. Fed on the hook thread (and the UI thread for
/// insertions) under the controller's lock — every edit is a StringBuilder insert/remove at the caret, nothing else.
/// <list type="bullet">
/// <item><see cref="Leave"/> (a caret jump): the mirror is frozen until the read at the new caret classifies the move;
/// keys arriving meanwhile cannot be placed (<see cref="EditState.TypedWhileLeft"/>).</item>
/// <item>The <b>anchor</b> is where the last word-level edit happened: after a letter typed, removed or a word
/// inserted; after a separator typed it stays at the end of the word just finished, so « gros␣ » typed in the middle
/// is the modified word, not the one after it.</item>
/// <item>A removal that crossed a separator (a whole word deleted) does not make the word starting at the deletion
/// point « modified »: deleting « gros␣ » in « le gros chat » leaves no modified word, only a modified sentence.</item>
/// </list>
/// </summary>
internal sealed class EditSession
{
    /// <summary>The read is widened up to the sentence bounds, at most this many characters before / after the caret.</summary>
    public const int BeforeMax = 600, AfterMax = 300;
    /// <summary>A mirror longer than this (a long dictation without any caret move) is no longer followed: the session
    /// is marked <see cref="Dirty"/> (never spoken) and stops growing.</summary>
    public const int MaxMirror = 4 * (BeforeMax + AfterMax);

    private readonly StringBuilder _t;
    private int _caret, _anchor, _edits, _spokenAt;
    private bool _removedSep;
    private readonly TextWindow _w;
    private readonly string _sentenceOrig, _wordBase;
    private readonly bool _middle;
    private readonly string _newline;

    public IntPtr Fg { get; }
    /// <summary>A caret jump happened: waiting for the read at the new caret.</summary>
    public bool Left { get; private set; }
    /// <summary>The text changed in a way the mirror cannot follow (deletion beyond the window…): never spoken.</summary>
    public bool Dirty { get; private set; }
    /// <summary>A key or an insertion arrived after <see cref="Leave"/>.</summary>
    public bool TypedWhileLeft { get; private set; }
    public int Edits => _edits;

    private EditSession(TextWindow w, IntPtr fg, string sentenceOrig, string wordBase, bool middle, int anchor, bool wordSpoken)
    {
        _w = w;
        _t = new StringBuilder(w.Text, w.Text.Length + 64);
        _caret = w.Caret;
        Fg = fg;
        _sentenceOrig = sentenceOrig;
        _wordBase = wordBase;
        _middle = middle;
        _anchor = anchor;
        _spokenAt = wordSpoken ? 0 : -1;
        _newline = w.Base >= 0 ? "\r\n" : "\n";   // a Win32 Edit (Notepad) stores Enter as CR LF: keeps the offsets exact
    }

    /// <summary>A new session at the caret of <paramref name="w"/> (nothing edited yet).</summary>
    public static EditSession Begin(TextWindow w, IntPtr fg)
    {
        var a = new TextAnalysis(w.Text, w.Caret, w.TruncStart, w.TruncEnd);
        return new EditSession(w, fg, a.Sentence, a.Sentence, a.Middle, -1, false);
    }

    /// <summary>The caret moved inside the same sentence: the read <paramref name="w"/> becomes the mirror; the
    /// sentence's original text and the « middle » flag carry over, and so does the word-level state when the caret
    /// stayed in the modified word (<paramref name="sameWord"/>).</summary>
    public static EditSession Continue(EditState prev, TextWindow w, bool sameWord)
    {
        var a = new TextAnalysis(w.Text, w.Caret, w.TruncStart, w.TruncEnd);
        int anchor = -1;
        string wordBase = a.Sentence;
        bool spoken = false;
        if (sameWord && prev.WordEdited)
        {
            int j = a.WordIndexAt(a.Caret);
            if (j >= 0)
            {
                anchor = a.SentenceToken(j).End;
                wordBase = prev.WordBase;
                spoken = prev.WordSpoken;
            }
        }
        return new EditSession(w, prev.Fg, prev.SentenceOrig, wordBase, prev.Middle || a.Middle, anchor, spoken);
    }

    /// <summary>Incremented by every <see cref="Leave"/>: a read that classified an older leave must not install its
    /// result over a newer one.</summary>
    public int Leaves { get; private set; }

    public void Leave() { Left = true; Leaves++; }

    /// <summary>The app changed the text unseen (paste, undo, overwrite mode…).</summary>
    public void MarkDirty() => Dirty = true;

    /// <summary>The word echo or the « inserted » echo has just said the modified word: not repeated at the next move.</summary>
    public void MarkWordSpoken() => _spokenAt = _edits;

    private bool Frozen()
    {
        if (Left) TypedWhileLeft = true;   // first: a key after a leave is never lost, even on an untrusted mirror
        if (Dirty) return true;            // untrusted: nothing more is mirrored
        if (_t.Length > MaxMirror) { Dirty = true; return true; }
        return Left;
    }

    // ---- field proof (plan 06 review): every edit must be covered by a « not a password field » answer
    private int _provenEdits;        // edits covered by the read the session began from, then by edit-time probes
    private int _probeAtEdits;
    private long _probeId;

    /// <summary>A probe of the focused field is in flight (asked after an edit, while the caret was still there).</summary>
    public bool ProbeInFlight { get; private set; }

    /// <summary>Every edit so far is covered by a NotPassword answer (or by the read the session began from).</summary>
    public bool Proven => _provenEdits == _edits;

    /// <summary>Edits not yet covered by a probe and none in flight, caret still in the field: ask one now.</summary>
    public bool NeedsProbe => !ProbeInFlight && !Left && !Dirty && _provenEdits != _edits;

    /// <summary>The controller asked probe <paramref name="id"/> for the edits made so far.</summary>
    public void StartProbe(long id)
    {
        ProbeInFlight = true;
        _probeAtEdits = _edits;
        _probeId = id;
    }

    /// <summary>Answer of probe <paramref name="id"/>. NotPassword covers the edits made up to its request; anything
    /// else (Password, Unavailable) makes the session untrusted: what was typed may be in a password field.</summary>
    /// <returns>true when another probe is needed (edits made while this one ran, caret still there).</returns>
    public bool ProbeAnswered(long id, ProbeStatus status)
    {
        if (!ProbeInFlight || id != _probeId) return false;
        ProbeInFlight = false;
        if (status == ProbeStatus.NotPassword) _provenEdits = Math.Max(_provenEdits, _probeAtEdits);
        else Dirty = true;
        return NeedsProbe;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || Tokenizer.IsMark(c);

    /// <summary>A typed character (Enter = <c>'\n'</c>).</summary>
    public void Type(char c)
    {
        if (Frozen()) return;
        string s = c is '\n' or '\r' ? _newline : c.ToString();
        int p = _caret;
        _t.Insert(p, s);
        _caret += s.Length;
        if (_anchor > p) _anchor += s.Length;
        if (IsWordChar(c)) { _anchor = _caret; _removedSep = false; }
        else if (p > 0 && IsWordChar(_t[p - 1])) _anchor = p;   // a separator after a word: that word stays the modified one
        _edits++;
    }

    public void Backspace()
    {
        if (Frozen()) return;
        if (_caret == 0) { if (_w.TruncStart) Dirty = true; return; }   // deletes text we never read
        int n = _caret >= 2 && ((_t[_caret - 2] == '\r' && _t[_caret - 1] == '\n') ||
                                (char.IsHighSurrogate(_t[_caret - 2]) && char.IsLowSurrogate(_t[_caret - 1]))) ? 2 : 1;
        char c = _t[_caret - 1];
        _t.Remove(_caret - n, n);
        _caret -= n;
        Removed(c);
    }

    /// <summary>Forward delete (the Delete key).</summary>
    public void Delete()
    {
        if (Frozen()) return;
        if (_caret >= _t.Length) { if (_w.TruncEnd) Dirty = true; return; }
        int n = _caret + 1 < _t.Length && ((_t[_caret] == '\r' && _t[_caret + 1] == '\n') ||
                                           (char.IsHighSurrogate(_t[_caret]) && char.IsLowSurrogate(_t[_caret + 1]))) ? 2 : 1;
        char c = _t[_caret];
        _t.Remove(_caret, n);
        Removed(c);
    }

    private void Removed(char c)
    {
        if (!IsWordChar(c)) _removedSep = true;
        _anchor = _caret;   // the deletion point (n chars gone)
        _edits++;
    }

    /// <summary>A suggestion was inserted the way <see cref="Inserter.Replace"/> sends it: Delete × <paramref name="right"/>,
    /// Backspace × <paramref name="left"/>, then <paramref name="text"/> (the word and maybe a space).</summary>
    public void Insert(int right, int left, string text)
    {
        if (Frozen()) return;
        for (int i = 0; i < right; i++) Delete();
        for (int i = 0; i < left; i++) Backspace();
        foreach (var c in text) Type(c);
    }

    public EditState Snapshot() =>
        new(_t.ToString(), _caret, _anchor, _removedSep, _w.TruncStart, _w.TruncEnd, _w.Base, _sentenceOrig, _wordBase, _middle,
            _spokenAt >= 0 && _spokenAt == _edits, Fg, Left, Dirty, TypedWhileLeft, _edits, Leaves,
            _provenEdits == _edits, ProbeInFlight);
}

/// <summary>An immutable copy of an <see cref="EditSession"/>, analysed off the hook thread (tokenising up to ~1000
/// characters never runs under the controller's lock).</summary>
internal sealed record EditState(string Text, int Caret, int Anchor, bool RemovedSep, bool TruncStart, bool TruncEnd, int Base,
                                 string SentenceOrig, string WordBase, bool Middle, bool WordSpoken, IntPtr Fg,
                                 bool Left, bool Dirty, bool TypedWhileLeft, int Edits, int Leaves = 0,
                                 bool Proven = true, bool ProbePending = false)
{
    private TextAnalysis? _view;
    public TextAnalysis View => _view ??= new TextAnalysis(Text, Caret, TruncStart, TruncEnd);

    /// <summary>The caret's sentence differs from what it was when the caret entered it.</summary>
    public bool Edited => View.Sentence != SentenceOrig;

    /// <summary>A word-level edit since the caret entered this word (or since the last word was re-read).</summary>
    public bool WordEdited => Anchor >= 0 && View.Sentence != WordBase;

    /// <summary>The word at the anchor (see <see cref="EditSession"/>), or null.</summary>
    public Token? ModifiedWord
    {
        get
        {
            if (Anchor < 0) return null;
            Token? right = null;
            foreach (var t in View.Tokens)
            {
                if (!TextAnalysis.IsWord(t)) continue;
                if (t.Start < Anchor && Anchor <= t.End) return t;
                if (t.Start == Anchor && !RemovedSep) right = t;
            }
            return right;
        }
    }
}
