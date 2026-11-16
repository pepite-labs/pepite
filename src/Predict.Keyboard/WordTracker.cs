using System.Collections.Generic;
using System.Text;

namespace Predict.Keyboard;

/// <summary>What <see cref="WordTracker.Backspace"/> did: stayed in the word, left it (state derived from the exact kept
/// text), or reached text it cannot trust (never seen, rebuilt from a read, a barrier: see
/// <see cref="WordTracker.IsBarrier"/>) and reset.</summary>
internal enum BackspaceEffect { InWord, Crossed, Lost }

/// <summary>
/// Follows what the user is typing: the word being typed (<see cref="Current"/>, the part before the caret)
/// and up to three previous words (<see cref="Context"/>) for the language model. Separators commit the
/// word; sentence ends clear the context; Backspace back over a separator brings the previous word back (below).
/// Anything that moves the caret (arrows, clicks, focus change) must call <see cref="Reset"/>; the
/// controller may then <see cref="Seed"/> it from a read of the text at the caret, which also gives the
/// rest of the word after the caret (<see cref="Suffix"/>) and the char after it (<see cref="NextChar"/>),
/// so an insertion can replace the whole word. Keystrokes stay the source while typing.
/// <see cref="Version"/> changes on every mutation, so a read requested before a keystroke is recognised
/// as stale. <see cref="AtSentenceStart"/> (D22): the word being typed opens a sentence (after <c>. ! ? …</c> or a
/// line break typed, or a read that found the start of the text / a sentence end) — suggestions are capitalised.
/// An inserted elision (<c>l'</c>) pushes its bare form and counts its apostrophe as the separator, exactly as if
/// it had been typed, so next-word prediction follows at once.
/// <para>Backspace across a word boundary (owner report, 0.6.0: « ma » ⌫ « o » predicted from « o », not « mo »): the
/// tracker also keeps the text it saw before the caret since the last jump (<c>_text</c>: typed, inserted or seeded,
/// at most <see cref="MaxText"/> chars). A Backspace that leaves the current word (or the separators after a word)
/// deletes the last char of that text and derives the whole state again with <see cref="ContextExtractor"/>, which
/// gives exactly the state typing that text would have reached (equivalence test): the previous word comes back as
/// the prefix, with its case and accents, its separator (« Ma, », « l'ami », several spaces) and its context, across
/// a sentence end too where the text was read. Nothing exactly known before the caret (start after a jump, a digit,
/// the part rebuilt from a read) or a barrier (<see cref="IsBarrier"/>) → <see cref="Reset"/> and the controller reads.</para>
/// </summary>
internal sealed class WordTracker
{
    private readonly StringBuilder _buf = new();
    private readonly List<string> _ctx = new();
    private int _sep;   // separator characters typed since the last committed word
    private bool _sentence;   // the sentence starts right before _ctx[0] (or the word): typed / read sentence end or
                              // start of text; unknown after a jump → false
    // the text before the caret since the last jump, behind a marker at [0]: '\n' = a known sentence start (read),
    // '0' = unknown (a digit: a barrier for ContextExtractor, not a sentence end). After a read it is rebuilt from the
    // read's words with one space between them (the read gives no more): _text[.._seeded] is approximate.
    private readonly StringBuilder _text = new("0");
    private int _seeded;      // _text[.._seeded] was rebuilt from a read (approximate): never derived from
    private bool _afterDigit; // plan 10B: the current word follows a digit with no separator (« 3e »)

    /// <summary>Text kept before the caret: a Backspace never goes further back than a few words in practice.</summary>
    public const int MaxText = 256;

    public string Current => _buf.ToString();
    public IReadOnlyList<string> Context => _ctx;
    /// <summary>True right after a word + separator: time for next-word prediction.</summary>
    public bool AfterSeparator => _buf.Length == 0 && _sep > 0;
    /// <summary>No word yet in this sentence, and the sentence start is known (typed or read).</summary>
    public bool AtSentenceStart => _sentence && _ctx.Count == 0;
    /// <summary>0.8.3 (10C review): a capital typed now says nothing about a name — a sentence start, a word right after
    /// <c>: « " — –</c> (<see cref="Predict.Core.CapitalContext"/>: « il a dit : Mer », « — Je »), or no word known before
    /// it (after a jump the reader could not read, keys-only hosts). The engine is then asked as at a sentence start.</summary>
    public bool CapitalNeutral
    {
        get
        {
            if (AtSentenceStart || _ctx.Count == 0) return true;
            for (int i = _text.Length - _buf.Length - 1; i >= 1 && !ContextExtractor.IsWordChar(_text[i]); i--)
                if (Predict.Core.CapitalContext.Opens(_text[i])) return true;
            return false;
        }
    }

    /// <summary>0.8.3: can the finished word <paramref name="word"/> be replaced from the keys alone? True when nothing but
    /// that word and the one separator that finished it (<paramref name="sep"/>) was typed since — the kept text ends with
    /// exactly them, typed (not rebuilt from a read), with no letter glued before the word (its letters may come
    /// from a read's prefix, exact; never from the approximate part a read rebuilt).</summary>
    public bool CanCorrect(string word, out char sep)
    {
        sep = '\0';
        if (Finished is not { } f || _buf.Length > 0 || string.IsNullOrEmpty(word) || f.Word != word || f.Split) return false;
        int start = _text.Length - 1 - word.Length;   // index of the word's first letter
        if (start < 1 || start < _seeded) return false;
        if (string.CompareOrdinal(_text.ToString(start, word.Length), word) != 0) return false;
        if (ContextExtractor.IsWordChar(_text[start - 1])) return false;
        sep = _text[^1];
        return sep == f.After;
    }

    /// <summary>0.8.3: the finished word was replaced by <paramref name="correction"/> in the document (same separator
    /// after it): the kept text and the state follow, the finished word becomes the correction. Call only after
    /// <see cref="CanCorrect"/> said true for that word.</summary>
    public void CorrectFinished(string word, string correction)
    {
        if (Finished is not { } f) return;
        Version++;
        char sep = _text[^1];
        _text.Length -= word.Length + 1;
        AppendText(correction);
        AppendText(sep);
        if (!Derive(keepRight: false)) return;
        Finished = f with { Word = correction };
    }

    /// <summary>The separator typed right before the current word / caret ('\0' = none or unknown): a '-' means an
    /// inversion such as <c>a-t-il</c>, where <c>t</c> is not the elision <c>t'</c> (D22).</summary>
    public char LastSeparator { get; private set; }

    /// <summary>Word chars right after the caret (from the last read); deleted when a suggestion is inserted.</summary>
    public string Suffix { get; private set; } = "";
    /// <summary>Char after <see cref="Suffix"/> ('\0' = end of text / unknown); meaningful only if <see cref="HasNextChar"/>.</summary>
    public char NextChar { get; private set; }
    /// <summary>True only while the last read is still valid (no separator, jump or insertion since).</summary>
    public bool HasNextChar { get; private set; }
    /// <summary>Plan 10B: the word a separator just finished (its separators, the word before it, a sentence start, the
    /// first separator after a « . » / « : »), or the word an insertion just put in (<see cref="Accept"/>); kept until the
    /// next letter, a Backspace, a jump or another insertion; null otherwise. Only the spelling indicator reads it;
    /// nothing is stored.</summary>
    public Predict.Core.FinishedWord? Finished { get; private set; }

    /// <summary>Plan 10B: the word being typed with what surrounds it (null when no word is being typed).</summary>
    public Predict.Core.FinishedWord? Typing =>
        _buf.Length == 0 ? null
        : new Predict.Core.FinishedWord(_buf.ToString(), LastSeparator, '\0', _ctx.Count > 0 ? _ctx[^1] : null, AtSentenceStart, _afterDigit,
                                        Split: Suffix.Length > 0 || StartUnknown);

    /// <summary>Plan 10B (review): the current word began right where the tracker lost the text — after a jump the reader
    /// could not read (keys-only hosts), the start of the word is unknown (« Le chev| » + click + « al » is a fragment).
    /// A seeded word (a read gave its start) and a word after a digit (its own rule) are not concerned.</summary>
    private bool StartUnknown => !_afterDigit && _seeded == 0 && _text.Length - _buf.Length == 1 && _text[0] == '0';

    /// <summary>Incremented on every mutation.</summary>
    public long Version { get; private set; }
    /// <summary>Where the state came from: "keys", or the reader source ("win32", "uia").</summary>
    public string Origin { get; private set; } = "keys";

    public void Reset()
    {
        _buf.Clear(); _ctx.Clear(); _sep = 0; _sentence = false; LastSeparator = '\0'; ClearRight(); Origin = "keys"; ResetText('0');
        _afterDigit = false; Finished = null; Version++;
    }

    public void Type(char c)
    {
        Version++;
        if (ContextExtractor.IsWordChar(c)) { if (_buf.Length == 0) { _sep = 0; Finished = null; } _buf.Append(c); AppendText(c); return; }
        if (char.IsDigit(c)) { Reset(); _afterDigit = true; return; }
        bool unknownStart = _buf.Length > 0 && StartUnknown;   // before the separator joins the kept text
        AppendText(c);
        // plan 10B: the word this separator finishes, as typed (the caret inside a word, or an unknown start: not the whole
        // word); after a « . » / « : » the first separator that follows is kept (only a space judges « www. » in full)
        if (_buf.Length > 0)
            Finished = new Predict.Core.FinishedWord(_buf.ToString(), LastSeparator, c, _ctx.Count > 0 ? _ctx[^1] : null, AtSentenceStart,
                                                     _afterDigit, Split: Suffix.Length > 0 || unknownStart);
        else if (Finished is { Then: '\0' } f && f.After is '.' or ':')
            Finished = f with { Then = c };
        _afterDigit = false;
        ClearRight();
        Commit();
        if (c is '.' or '!' or '?' or '\n' or '\r' or '…') { _ctx.Clear(); _sep = 0; _sentence = true; LastSeparator = c; return; }
        LastSeparator = c;
        _sep++;   // space, apostrophe, hyphen, comma…
    }

    /// <summary>Inside the word: one letter less (the read suffix stays). Otherwise the caret leaves the separators /
    /// the word: the state is derived again from the kept text (see the class summary). O(<see cref="MaxText"/>).</summary>
    public BackspaceEffect Backspace()
    {
        Version++;
        Finished = null;   // plan 10B: the word is being changed
        if (_buf.Length > 1) { _buf.Length--; DropText(); return BackspaceEffect.InWord; }
        if (_text.Length <= 1) { Reset(); return BackspaceEffect.Lost; }   // nothing known before the caret
        if (_buf.Length == 1)   // the last letter: the caret is now after the separators (the suffix is still there)
        {
            DropText();
            return Derive(keepRight: true) ? BackspaceEffect.InWord : BackspaceEffect.Lost;
        }
        // the char this Backspace deletes is a separator; the state behind it is trusted only if that char is exactly
        // what the document holds
        int at = _text.Length - 1;
        if (at < _seeded || IsBarrier(_text[at], Origin == "keys")) { Reset(); return BackspaceEffect.Lost; }
        DropText();
        return Derive(keepRight: false) ? BackspaceEffect.Crossed : BackspaceEffect.Lost;
    }

    /// <summary>A separator that a Backspace does not cross with the kept text: a typed line break (Enter may have moved
    /// the focus: two-step logins), and, where only keys are known (no read: Word), the characters such hosts rewrite
    /// behind our back — <c>. ! ? … ; : " « »</c> and no-break spaces (AutoCorrect « ... » → « … », a no-break space
    /// before <c>! ? ; :</c> and inside « », <c>"</c> → « »), which would make the kept text longer or shorter than the
    /// document. Spaces, commas, apostrophes, hyphens, tabs are crossed (they come out as typed, at most re-shaped
    /// one for one: ' → ’).</summary>
    internal static bool IsBarrier(char c, bool keysOnly) =>
        c is '\n' or '\r' ||
        keysOnly && c is '.' or '!' or '?' or '…' or ';' or ':' or '"' or '«' or '»' or '\u00A0' or '\u202F';

    /// <summary>A suggestion was inserted (the word, plus one space if <paramref name="spaced"/>). An elision
    /// (<c>l'</c>, never spaced) enters the context bare and its apostrophe is the separator, as when typed.</summary>
    public void Accept(string word, bool spaced)
    {
        Version++;
        // plan 10B: an inserted word is finished as a typed one would be (by its space, by its apostrophe for « l' », or by
        // nothing known when the text already goes on: '\0')
        bool elided = Predict.Core.Elision.IsElided(word);
        Finished = new Predict.Core.FinishedWord(elided ? word[..^1] : word, LastSeparator, elided ? word[^1] : spaced ? ' ' : '\0',
                                                 _ctx.Count > 0 ? _ctx[^1] : null, AtSentenceStart, _afterDigit);
        _afterDigit = false;
        _text.Length = System.Math.Max(1, _text.Length - _buf.Length);   // the prefix the word replaced
        if (_seeded > _text.Length) _seeded = _text.Length;
        AppendText(word);
        if (spaced) AppendText(' ');
        _buf.Clear();
        if (Predict.Core.Elision.IsElided(word)) { Push(word[..^1]); _sep = 1; LastSeparator = word[^1]; }
        else { Push(word); _sep = spaced ? 1 : 0; LastSeparator = spaced ? ' ' : '\0'; }
        ClearRight();
    }

    /// <summary>State from a read of the text at the caret (see <see cref="ContextExtractor"/>).</summary>
    public void Seed(string current, IReadOnlyList<string> context, int separators, string suffix, char nextChar, string origin,
                     bool atSentenceStart = false)
    {
        _sentence = atSentenceStart && context.Count <= 3;
        LastSeparator = '\0';   // not given by a read
        Finished = null; _afterDigit = false;
        _buf.Clear(); _buf.Append(current);
        _ctx.Clear();
        for (int i = System.Math.Max(0, context.Count - 3); i < context.Count; i++) _ctx.Add(context[i]);
        _sep = current.Length > 0 ? 0 : separators;
        Suffix = current.Length > 0 ? suffix : "";
        NextChar = nextChar;
        HasNextChar = true;
        Origin = origin;
        // the kept text: the words as read, one space between them (the exact separators are not known, so a Backspace
        // into this part is Lost: the controller reads again)
        ResetText(_sentence ? '\n' : '0');
        for (int i = 0; i < _ctx.Count; i++) { if (i > 0) _text.Append(' '); _text.Append(_ctx[i]); }
        if (_ctx.Count > 0) _text.Append(' ', current.Length > 0 ? 1 : System.Math.Max(1, separators));
        else if (current.Length == 0) _text.Append(' ', separators);
        _seeded = _text.Length;
        _text.Append(current);
        Version++;
    }

    /// <summary>The state typing <c>_text</c> would have reached (equivalence with <see cref="ContextExtractor"/>).</summary>
    private bool Derive(bool keepRight)
    {
        var c = ContextExtractor.Extract(_text.ToString(), _text.Length, false, false);
        if (!c.Valid) { Reset(); return false; }
        _buf.Clear(); _buf.Append(c.Prefix);
        _ctx.Clear(); foreach (var w in c.Context) _ctx.Add(w);
        _sep = c.Prefix.Length > 0 ? 0 : c.Separators;
        _sentence = c.AtSentenceStart;
        int at = _text.Length - c.Prefix.Length - 1;   // the char right before the word / caret ([0] = the marker)
        LastSeparator = at >= 1 ? _text[at] : '\0';
        if (!keepRight) ClearRight();
        else if (Suffix.Length > 0) _sep = 0;   // a word still follows the caret: no next-word prediction (as before)
        return true;
    }

    private void ResetText(char marker) { _text.Clear(); _text.Append(marker); _seeded = 0; }

    private void DropText() { if (_text.Length > 1) _text.Length--; if (_seeded > _text.Length) _seeded = _text.Length; }

    private void AppendText(char c) { _text.Append(c); if (_text.Length > MaxText) TrimText(); }

    private void AppendText(string s) { _text.Append(s); if (_text.Length > MaxText) TrimText(); }

    /// <summary>Forget the oldest half (a word cut there is dropped whole): what came before is unknown.</summary>
    private void TrimText()
    {
        int cut = _text.Length - MaxText / 2;
        while (cut < _text.Length && ContextExtractor.IsWordChar(_text[cut])) cut++;
        _text.Remove(0, cut);
        _text.Insert(0, '0');
        _seeded = System.Math.Max(0, _seeded - cut + 1);
    }

    private void ClearRight() { Suffix = ""; NextChar = '\0'; HasNextChar = false; }

    private void Commit()
    {
        if (_buf.Length == 0) return;
        Push(_buf.ToString());
        _buf.Clear();
        _sep = 0;
    }

    // a word dropped off the context: the sentence start is no longer right before the first context word
    private void Push(string w) { _ctx.Add(w); if (_ctx.Count > 3) { _ctx.RemoveAt(0); _sentence = false; } }
}
