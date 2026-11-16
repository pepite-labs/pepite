namespace Predict.Core;

/// <summary>What <see cref="PredictionEngine.Check"/> says about one word (plan 10B, D12).</summary>
public enum SpellingStatus { Known, SoundsLike, Unknown }

/// <param name="Candidates">for <see cref="SpellingStatus.SoundsLike"/>: the known words it may stand for, best first (a
/// « Mes fautes » target, then the same letters with their accents, then the same sound; the most frequent first); empty
/// otherwise.</param>
/// <param name="WeakName">plan 10C (owner 2026-11-15): SoundsLike given for a word that is also a very rare first name of the
/// pack (« Pome », « Kan »): ≈ at a sentence start, ✓ inside a sentence.</param>
public readonly record struct SpellingCheck(SpellingStatus Status, IReadOnlyList<string> Candidates, bool WeakName = false)
{
    public static readonly SpellingCheck Known = new(SpellingStatus.Known, Array.Empty<string>());
    public static readonly SpellingCheck Unknown = new(SpellingStatus.Unknown, Array.Empty<string>());
    public string? Candidate => Candidates.Count > 0 ? Candidates[0] : null;
}

/// <summary>The round marker left of the bar's words (plan 10B, Q9): none, ✓ green, ≈ blue, ? red — never ✗.</summary>
public enum SpellingMark { None, Known, SoundsLike, Unknown }

/// <summary>
/// A word as the keyboard saw it typed (plan 10B): the word just finished by a separator, or the word being typed
/// (<see cref="After"/> = '\0'). Nothing here is ever stored: the app keeps the last one only until the next letter.
/// </summary>
/// <param name="Before">the separator typed right before the word ('\0' = none or unknown: start, a jump, a read).</param>
/// <param name="After">the separator that finished it ('\0' while the word is being typed).</param>
/// <param name="Previous">the word before it in the sentence (null at a sentence start or when unknown): « l » in « l'école ».</param>
/// <param name="SentenceStart">the word opens a sentence (typed or read sentence end, start of the text).</param>
/// <param name="AfterDigit">the word follows a digit with no separator (« 3e », « 2ème », « 10h »).</param>
/// <param name="Split">the letters typed are not known to be the whole word: the caret was inside a word, or the word
/// began right where the keyboard lost track of the text (a jump where the text could not be read: « Le chev| » + click +
/// « al␣ » is a fragment, not the word « al »).</param>
/// <param name="Then">the first separator typed after <see cref="After"/> ('\0' = none yet): a word finished by « . » or
/// « : » is judged in full only once a space or a line break follows (« www. », « https: » are not words).</param>
public readonly record struct FinishedWord(string Word, char Before, char After, string? Previous, bool SentenceStart,
                                           bool AfterDigit = false, bool Split = false, char Then = '\0');

/// <summary>
/// The spelling indicator's rules (plan 10B, D12, owner answers Q8–Q10), pure. On a <b>finished</b> word the final state
/// (✓ known, ≈ sounds like a known word, ? unknown), kept by the app until the next letter; while <b>typing</b>, ✓ only
/// when the letters already form a known word, nothing otherwise. No mark (never a wrong red) where the letters alone
/// cannot be judged:
/// <list type="bullet">
/// <item>one letter (« a », « à », « y »), a word after a digit (« 3e », « 10h »), a word cut by the caret;</item>
/// <item>all capitals (acronyms: « SNCB », « PC »);</item>
/// <item>next to a hyphen (« peut-être », « Jean-Pierre », « a-t-il »: a part of a compound) and before an apostrophe
/// (« l' », « aujourd' », « presqu' »); after an apostrophe only when the word before is an elision (« l'école » is
/// checked, the « hui » of « aujourd'hui » is not);</item>
/// <item>next to <c>@ / \ _</c> or right after a dot (addresses, paths, « ecole.be »); a word finished by « . » or « : »
/// gets only ✓ until a space or a line break follows (« www. », « https:// »);</item>
/// <item>a word whose start is unknown (typed right after a jump the keyboard could not read: a fragment);</item>
/// <item>a capitalised word that is not known (a name neither the lists nor the « Noms propres » pack hold): no mark inside
/// a sentence; at a sentence start (plan 10C) it is judged by its letters as a lowercase word would be, but never red (it
/// may still be a rare name): ≈ when it sounds like a known word (« Maizon » ≈ maison), else no mark. A known capitalised
/// word (« Maison » at a sentence start, a personal « Léa », a pack name « Emma », « Bruxelles ») is ✓.</item>
/// </list>
/// </summary>
public static class SpellingIndicator
{
    /// <summary>The mark of a word just finished by a separator.</summary>
    public static SpellingMark Finished(in FinishedWord w, Func<string, SpellingCheck> check)
    {
        if (!Judged(w, out bool capital)) return SpellingMark.None;
        var c = check(w.Word);
        if (Pending(w)) return c.Status == SpellingStatus.Known ? SpellingMark.Known : SpellingMark.None;
        return c.Status switch
        {
            SpellingStatus.Known => SpellingMark.Known,
            // plan 10C (owner 2026-11-14): at a sentence start the capital says nothing, so an unknown capitalised word is
            // judged by its letters — « Maizon » ≈ maison; inside a sentence it may be a name the lists do not hold
            SpellingStatus.SoundsLike when c.WeakName => w.SentenceStart ? SpellingMark.SoundsLike : SpellingMark.Known,
            SpellingStatus.SoundsLike => !capital || w.SentenceStart ? SpellingMark.SoundsLike : SpellingMark.None,
            _ => capital ? SpellingMark.None : SpellingMark.Unknown,
        };
    }

    /// <summary>A word finished by « . » or « : » with no space or line break after it yet: maybe a part of an address.</summary>
    public static bool Pending(in FinishedWord w) => w.After is '.' or ':' && !IsSpace(w.Then);

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\u00A0' or '\u202F';

    /// <summary>The mark of the word being typed: ✓ when its letters already form a known word, else none (Q8).</summary>
    public static SpellingMark Typing(in FinishedWord w, Func<string, bool> known) =>
        Judged(w, out _) && known(w.Word) ? SpellingMark.Known : SpellingMark.None;

    /// <summary>Can the letters of <paramref name="w"/> be judged at all (see the class summary)?</summary>
    public static bool Judged(in FinishedWord w, out bool capital)
    {
        capital = false;
        var s = w.Word;
        if (string.IsNullOrEmpty(s) || s.Length < 2 || w.AfterDigit || w.Split) return false;
        int letters = 0, upper = 0;
        foreach (var ch in s)
        {
            if (!IsWordChar(ch)) return false;
            letters++;
            if (char.IsUpper(ch)) upper++;
        }
        if (upper == letters) return false;                                // acronym
        if (w.Before is '-' || w.After is '-') return false;               // a part of a compound
        if (Elision.IsApostrophe(w.After)) return false;                    // « l' », « aujourd' », « presqu' »
        if (Elision.IsApostrophe(w.Before) && !Elision.IsBare(w.Previous)) return false;   // « aujourd'hui »: not « l'école »
        if (w.Before is '@' or '/' or '\\' or '_' or '.' || w.After is '@' or '/' or '\\' or '_') return false;
        capital = char.IsUpper(s[0]);
        return true;
    }

    /// <summary>The letters a word is made of — the one definition used by the keyboard's word tracker and context reader
    /// too: ASCII letters, Latin-1 accented letters (not the division sign ÷, U+00F7), œ.</summary>
    public static bool IsWordChar(char c)
    {
        char l = char.ToLowerInvariant(c);
        return (l >= 'a' && l <= 'z') || (l >= 'à' && l <= 'ÿ' && l != '÷') || l == 'œ';
    }

    private static bool SentenceEnd(char c) => c is '.' or '!' or '?' or '\n' or '\r' or '…';

    /// <summary>
    /// The word the keyboard would judge after <paramref name="text"/> was typed: the word being typed when the text ends
    /// with a letter, else the last word a separator finished (null if none). <paramref name="startKnown"/>: typed from the
    /// start of a document (a read found it), else after a jump the keyboard could not read (the first word's start is
    /// unknown). The same rules as the keyboard's word tracker for typed keys (an xUnit test checks both agree).
    /// </summary>
    public static FinishedWord? LastWord(string text, bool startKnown = true) => Scan(text, startKnown).Last;

    /// <summary>Every state of the finished word along <paramref name="text"/> (a new finished word, or the separator after
    /// a « . » / « : »), in typing order, and <see cref="LastWord"/>. Used by <c>Predict.Eval --spelling</c>, which judges
    /// every finished word of a case, not only the last.</summary>
    public static (List<FinishedWord> Finished, FinishedWord? Last) Scan(string text, bool startKnown = true)
    {
        var states = new List<FinishedWord>();
        var buf = new System.Text.StringBuilder();
        string? prev = null;
        bool sentence = startKnown, afterDigit = false, inSentence = false;   // inSentence: a word was committed since the sentence start
        bool unknownStart = !startKnown;                                       // no separator seen since the untracked start
        char last = '\0';
        FinishedWord? finished = null;
        foreach (var c in text ?? "")
        {
            if (IsWordChar(c)) { if (buf.Length == 0) finished = null; buf.Append(c); continue; }
            if (char.IsDigit(c))
            {
                buf.Clear(); prev = null; sentence = false; inSentence = false; last = '\0'; finished = null; afterDigit = true;
                unknownStart = false;   // the digit says it all (AfterDigit)
                continue;
            }
            if (buf.Length > 0)
            {
                finished = new FinishedWord(buf.ToString(), last, c, prev, sentence && !inSentence, afterDigit, Split: unknownStart && !afterDigit);
                states.Add(finished.Value);
                prev = buf.ToString(); inSentence = true; buf.Clear();
            }
            else if (finished is { Then: '\0' } f && f.After is '.' or ':')
            {
                finished = f with { Then = c };
                states.Add(finished.Value);
            }
            unknownStart = false;
            afterDigit = false;
            last = c;
            if (SentenceEnd(c)) { prev = null; sentence = true; inSentence = false; }
        }
        if (buf.Length > 0)
            return (states, new FinishedWord(buf.ToString(), last, '\0', prev, sentence && !inSentence, afterDigit, Split: unknownStart && !afterDigit));
        return (states, finished);
    }
}
