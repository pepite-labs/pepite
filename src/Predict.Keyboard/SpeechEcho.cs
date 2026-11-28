using System.Collections.Generic;
using System.Text;

namespace Predict.Keyboard;

internal enum EchoKind { Word, Sentence }

/// <param name="Covers">plan 08b: a grouped word echo (« les amis ») also says the previous word (« les »): a still
/// pending echo of that word is dropped (<see cref="SpeechQueue.Enqueue"/>).</param>
/// <param name="AfterJump">a sentence end typed in a sentence the caret jumped into (an edit): <see cref="Text"/> holds
/// only the words typed since the jump (may be empty); the controller reads the whole sentence from the document and
/// says that instead (D17 amendment, 2026-11-14), <see cref="Text"/> being the fallback.</param>
internal sealed record EchoEvent(EchoKind Kind, string Text, string? Covers = null, bool AfterJump = false);

/// <summary>
/// What the speech echo (D7) says while the user types — from the keystrokes only, like the tracker
/// (pure; fed on the hook thread under the controller's lock).
/// <list type="bullet">
/// <item>Chunks: letters, digits, <c>'</c> <c>’</c> and <c>-</c> join one chunk (« l'arbre », « peut-être » spoken
/// whole). A chunk is flushed as a <see cref="EchoKind.Word"/> on whitespace, Enter, punctuation or a sentence end.</item>
/// <item>Sentence: the chunks since the last <c>. ! ? …</c> (at most 60). At a sentence end with sentences on,
/// the sentence is spoken and its last word is not spoken separately. Enter flushes the word only.</item>
/// <item>Backspace mirrors the tracker: removes letters, and deleting the separator after a chunk brings it back (also
/// after a chunk typed there was erased: « Ma c » ⌫ ⌫ ⌫ « o » says « Mo »). Across a sentence end it resets (the
/// sentence was said; the next sentence end is then an edit, D17 amendment).</item>
/// <item><see cref="Reset"/> (caret jump) forgets everything; the sentence end that follows a jump into a sentence is
/// marked <see cref="EchoEvent.AfterJump"/> with the words typed since (owner decision 5, D17), and the controller says
/// the whole sentence read from the document instead (D17 amendment, owner 2026-11-14: « tape. » after editing the
/// last word said « tape » only). A jump to a sentence start (<see cref="Seed"/> with <c>atSentenceStart</c>) is not
/// one: the sentence is typed whole.</item>
/// <item><see cref="Inserted"/>: an inserted word is already spoken by the "inserted" echo; it is marked so a
/// following space does not repeat it, and letters typed onto it clear the mark (« maison » + s → « maisons »).</item>
/// <item>Liaison grouping (plan 08b, D28): a word typed after only spaces since the previous word is said together
/// with it when <see cref="Liaison.Triggers"/> (« les » … « les amis »), so the voice can make the liaison; the event's
/// <see cref="EchoEvent.Covers"/> names the previous word. Not for the sentence echo (it is whole already).</item>
/// </list>
/// </summary>
internal sealed class SpeechEcho
{
    public const int MaxSentenceWords = 60;

    private readonly StringBuilder _chunk = new();
    private bool _chunkSpoken;                                     // the current chunk was already spoken
    private bool _chunkJoins;                                      // only spaces between the previous chunk and this one
    private readonly List<(string Text, bool Spoken, bool JoinsPrev, int SepBefore, bool SepOtherBefore)> _sentence = new();
    private int _sep;                                              // separators typed since the last chunk
    private bool _sepOther;                                        // one of them was not a space (, ; : Enter …)
    private int _chunkSep;                                         // the separators before the current chunk (_sep, _sepOther
    private bool _chunkSepOther;                                   // when it started): Backspace emptying it restores them
    private bool _jumped;                                          // the caret jumped into the current sentence

    public volatile bool SpeakWords, SpeakSentences;

    /// <summary>Nothing typed or restored: no chunk, no sentence, no separator (after a reset).</summary>
    public bool IsBlank => _chunk.Length == 0 && _sentence.Count == 0 && _sep == 0;

    public static bool IsChunkChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’' or '-';
    public static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or '…';

    public EchoEvent? Type(char c)
    {
        if (IsChunkChar(c))
        {
            if (_chunk.Length == 0) StartChunk();
            _chunk.Append(c);
            _chunkSpoken = false;   // typed onto an inserted word: it changed
            return null;
        }
        if (IsSentenceEnd(c))
        {
            var (word, spoken, said, covers) = Commit();
            EchoEvent? ev = null;
            // after a jump into this sentence, even with no word typed since (« tape| » then « . »): the document has it
            if (SpeakSentences && (_sentence.Count > 0 || _jumped)) ev = new EchoEvent(EchoKind.Sentence, Join(), AfterJump: _jumped);
            else if (SpeakWords && word != null && !spoken) ev = new EchoEvent(EchoKind.Word, said!, covers);
            _jumped = false;   // the next sentence is typed whole
            _sentence.Clear();
            _sep = 0;
            _sepOther = false;
            return ev;
        }
        // whitespace, Enter, , ; : and any other punctuation: flush the word
        var (w, sp, s, cov) = Commit();
        _sep++;
        if (c != ' ') _sepOther = true;
        return SpeakWords && w != null && !sp ? new EchoEvent(EchoKind.Word, s!, cov) : null;
    }

    public void Backspace()
    {
        if (_chunk.Length > 0)
        {
            _chunk.Length--;
            // the chunk is gone: the caret is back after the separators it followed (« Ma c » ⌫ ⌫ → after « Ma » again;
            // « les, x » ⌫ then space and « amis »: the comma still stands between « les » and « amis »)
            if (_chunk.Length == 0) { _sep = _chunkSep; _sepOther = _chunkSepOther; _chunkSep = 0; _chunkSepOther = false; }
            return;
        }
        if (_sep > 0)
        {
            _sep--;
            if (_sep == 0 && _sentence.Count > 0)
            {
                var (t, spoken, joins, sepBefore, otherBefore) = _sentence[^1];
                _sentence.RemoveAt(_sentence.Count - 1);
                _chunk.Append(t);
                _chunkSpoken = spoken;
                _chunkJoins = joins;
                _chunkSep = sepBefore;           // several words back: erasing this one too finds its separators
                _chunkSepOther = otherBefore;
            }
            return;
        }
        Reset();
    }

    public void Reset()
    {
        _chunk.Clear();
        _chunkSpoken = false;
        _chunkJoins = false;
        _chunkSep = 0;
        _chunkSepOther = false;
        _sentence.Clear();
        _sep = 0;
        _sepOther = false;
        _jumped = true;
    }

    /// <summary>A new chunk begins: it joins the previous word when only spaces separate them. The separators are
    /// kept aside for a Backspace that erases the chunk.</summary>
    private void StartChunk()
    {
        _chunkJoins = _sentence.Count > 0 && _sep > 0 && !_sepOther;
        _chunkSep = _sep;
        _chunkSepOther = _sepOther;
        _sep = 0;
        _sepOther = false;
    }

    /// <summary>The reader found the caret inside / after a word: that prefix becomes the current chunk.</summary>
    /// <param name="atSentenceStart">the caret is at the start of a sentence (before <paramref name="prefix"/>): what
    /// follows is typed whole, it is not an edit of a sentence.</param>
    public void Seed(string prefix, bool atSentenceStart = false)
    {
        Reset();
        _jumped = !atSentenceStart;
        _chunk.Append(prefix);
    }

    /// <summary>The whole chunk after the last <see cref="Inserted"/> (« l'horizon » when <c>horizon</c> followed an
    /// inserted <c>l'</c>): what the "inserted" echo says.</summary>
    public string LastInserted { get; private set; } = "";

    /// <summary>What the "inserted" echo says (plan 08b): <see cref="LastInserted"/>, grouped with the previous word when
    /// a liaison follows (F1 after « les » says « les amis »).</summary>
    public string LastInsertedSpoken { get; private set; } = "";

    /// <summary>A suggestion replaced the current chunk's last word (plus one space if <paramref name="spaced"/>).
    /// An elision (<c>l'</c>) is inserted unspaced and not spoken: it is said with the next word.
    /// <paramref name="spoken"/>: the "inserted" echo said it; otherwise the word echo may say it at its separator.</summary>
    public EchoEvent? Inserted(string word, bool spaced, bool spoken)
    {
        // the tracker's word is letters only: what the chunk holds up to its last ' ’ or - (an elision « l' » typed
        // or inserted, a compound head « peut- ») stays, so the chunk is « l'horizon » (D22)
        if (_chunk.Length == 0)   // inserted right after the separator (F1 after « les »): a new chunk starts here
        {
            int sep = _sep;
            StartChunk();
            _sep = sep;   // Commit below resets it; an unspaced insertion keeps the separators it follows
        }
        int keep = _chunk.Length;
        while (keep > 0 && _chunk[keep - 1] is not ('\'' or '’' or '-')) keep--;
        _chunk.Length = keep;
        _chunk.Append(word);
        LastInserted = _chunk.ToString();
        LastInsertedSpoken = Grouped(_chunkJoins, LastInserted.Trim('\'', '\u2019', '-')).Said;
        _chunkSpoken = spoken;
        if (!spaced) return null;
        var (w, sp, s, cov) = Commit();
        _sep = 1;
        _sepOther = false;
        return SpeakWords && w != null && !sp ? new EchoEvent(EchoKind.Word, s!, cov) : null;
    }

    /// <summary>
    /// 0.11.0 (plan negation): a group replaced the last <paramref name="back"/> characters typed (the group and the
    /// <paramref name="tail"/> after it): the echo goes back over them as Backspace would, then takes the
    /// <paramref name="replacement"/> — its words as typed (their word echoes are dropped: the group is said once), its last
    /// word as inserted (<paramref name="spoken"/>: the « inserted » echo says the group, owner Q7) — then the tail. Returns
    /// the tail's event: a sentence end there makes the sentence echo (after a Backspace over a sentence end: the document's
    /// sentence, <see cref="EchoEvent.AfterJump"/>).
    /// </summary>
    public EchoEvent? ReplacedGroup(int back, string replacement, string tail, bool spoken)
    {
        for (int k = 0; k < back; k++) Backspace();
        int last = replacement.Length;
        while (last > 0 && Predict.Core.SpellingIndicator.IsWordChar(replacement[last - 1])) last--;
        foreach (var c in replacement[..last]) Type(c);
        Inserted(replacement[last..], false, spoken);
        EchoEvent? ev = null;
        foreach (var c in tail) ev = Type(c) ?? ev;
        return ev;
    }

    /// <summary>The word as the echo says it: with the previous word when only spaces separate them and a liaison
    /// follows (<see cref="Liaison.Triggers"/>).</summary>
    private (string Said, string? Covers) Grouped(bool joinsPrev, string word)
    {
        if (joinsPrev && _sentence.Count > 0 && Liaison.Triggers(_sentence[^1].Text, word))
            return (_sentence[^1].Text + " " + word, _sentence[^1].Text);
        return (word, null);
    }

    /// <summary>Current chunk → sentence buffer. Returns it (null when empty after trimming), its spoken mark, what
    /// the word echo says (grouped or not) and the previous word it covers.</summary>
    private (string? Word, bool Spoken, string? Said, string? Covers) Commit()
    {
        if (_chunk.Length == 0) return (null, false, null, null);
        var w = _chunk.ToString().Trim('\'', '’', '-');
        bool spoken = _chunkSpoken, joins = _chunkJoins;
        int sepBefore = _chunkSep;
        bool otherBefore = _chunkSepOther;
        _chunk.Clear();
        _chunkSpoken = false;
        _chunkJoins = false;
        _chunkSep = 0;
        _chunkSepOther = false;
        if (w.Length == 0) return (null, false, null, null);
        var (said, covers) = Grouped(joins, w);
        _sentence.Add((w, spoken, joins, sepBefore, otherBefore));
        if (_sentence.Count > MaxSentenceWords) _sentence.RemoveAt(0);
        _sep = 0;
        _sepOther = false;
        return (w, spoken, said, covers);
    }

    private string Join()
    {
        var sb = new StringBuilder();
        foreach (var (t, _, _, _, _) in _sentence) { if (sb.Length > 0) sb.Append(' '); sb.Append(t); }
        return sb.ToString();
    }
}
