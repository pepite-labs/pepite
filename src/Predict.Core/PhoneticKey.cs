using System.Globalization;
using System.Text;

namespace Predict.Core;

/// <summary>
/// French grapheme-level phonetic fold (a Phonex/Sonnex-style key). Maps a written form to a
/// canonical "sound" string so that a word and its phonetic misspelling collapse together:
/// horizon/orizon -> "orizO", maison/maizon -> "mezO". Applied identically to the lexicon (at load)
/// and to what the user types (at query), so matching is symmetric. Prefix-compatible: the fold of a
/// typed prefix is a prefix of the fold of the full word, so it also works mid-word.
///
/// Phoneme alphabet: oral vowels a e i o u ; nasal vowels A(an) E(in) O(on) U(un) ;
/// consonants p b t d k g f v s z l r m n w ; C(ch /ʃ/), j(/ʒ/), N(gn /ɲ/). Built only from open
/// rules.
/// <para>0.8.3 (owner test « si je fè » → fais): /e/, /ɛ/, /ø/, /œ/ and /ə/ share the one symbol <c>e</c> (dyslexic
/// spellers mix é / è / ai / eu), but a final <c>e</c> is dropped only when it is a real mute e — a written unaccented
/// « e » on its own (« pomme », « parle »), Lexique's schwa <c>°</c> — never a sounded one: « fè », « fais » (/fɛ/),
/// « été », « feu » keep it (« fè » → <c>fe</c>; it was <c>f</c>, too short for the sound pass).</para>
/// </summary>
public static class PhoneticKey
{
    /// <summary>Internal mark of a sounded e-like vowel (é è ê ë, ai, ei, eu, oe; Lexique e E 2 9): written <c>e</c> in
    /// the key, but never dropped at the end of the word as a mute e is.</summary>
    private const char Sounded = '\u0001';
    /// <summary>Internal mark of Lexique's schwa <c>°</c>: <c>e</c> inside the key, dropped at its end.</summary>
    private const char Schwa = '\u0002';
    /// <summary>Internal mark of the letter « œ » until the c / g rule has seen it (« cœur » is /k/ like « coeur », not
    /// /s/ as before a plain e); then written as a sounded e, as the digraph « oe » is.</summary>
    private const char Oe = '\u0003';

    private static bool IsVowel(char c) => c == Sounded || c == Oe || "aeiouy".IndexOf(c) >= 0;

    private static string StripDiacritics(string s)
    {
        // plain ASCII has no diacritic: the same string, without the two normalisations (plan 10A: snapshot build time)
        bool ascii = true;
        foreach (var c in s) if (c >= 0x80) { ascii = false; break; }
        if (ascii) return s;
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Map Lexique 3.83's SAMPA pronunciation (the <c>phon</c> column) into the same fold alphabet as
    /// <see cref="Fold"/>, so a word's real pronunciation can be matched against the grapheme fold of what
    /// the user typed. Catches irregular spellings the grapheme rules can't (e.g. <c>femme</c> = /fam/).
    /// </summary>
    public static string FromLexiquePhon(string sampa)
    {
        if (string.IsNullOrEmpty(sampa)) return "";
        var o = new StringBuilder(sampa.Length);
        foreach (var c in sampa)
        {
            char m = c switch
            {
                'a' or 'A' => 'a',
                'e' or 'E' or '2' or '9' => Sounded,   // /e/ /ɛ/ /ø/ /œ/: one symbol, kept at the end
                '°' => Schwa,                           // /ə/: dropped at the end (a mute e)
                '@' => 'A',                             // ɑ̃ (nasal)
                'o' or 'O' => 'o',
                'u' or 'y' or '8' => 'u',
                'i' => 'i',
                '§' => 'O',                 // ɔ̃ (on)
                '5' => 'E',                 // ɛ̃ (in)
                '1' => 'U',                 // œ̃ (un)
                'S' => 'C',                 // ʃ
                'Z' => 'j',                 // ʒ
                'N' or 'G' => 'N',          // ɲ / ŋ
                'R' => 'r',
                'j' => 'i',                 // yod
                'w' => 'w',
                'x' => 'k',
                'p' or 'b' or 't' or 'd' or 'k' or 'g' or 'f' or 'v'
                    or 's' or 'z' or 'm' or 'n' or 'l' => c,
                _ => '\0'
            };
            if (m != '\0') o.Append(m);
        }
        return Finish(o);
    }

    /// <summary>The e-like symbols of a key as one letter: <see cref="Sounded"/>, <see cref="Schwa"/> and <c>e</c>.</summary>
    private static char Plain(char c) => c is Sounded or Schwa ? 'e' : c;

    /// <summary>Collapse doubles (a sounded e merged with a plain one stays sounded: « réel », « fées »), drop the trailing
    /// plural <c>s</c> and mute e (a plain written e, a schwa — never a sounded e), then write every e-like symbol as
    /// <c>e</c>.</summary>
    private static string Finish(StringBuilder o)
    {
        var r = new StringBuilder(o.Length);
        for (int i = 0; i < o.Length; i++)
        {
            char ch = o[i];
            if (r.Length > 0 && Plain(r[^1]) == Plain(ch))
            {
                if (ch == Sounded) r[^1] = Sounded;
                else if (ch == 'e' && r[^1] == Schwa) r[^1] = 'e';
                continue;
            }
            r.Append(ch);
        }
        while (r.Length > 1 && r[^1] is 'e' or 's' or Schwa) r.Length--;
        for (int i = 0; i < r.Length; i++) if (r[i] is Sounded or Schwa) r[i] = 'e';
        return r.ToString();
    }

    /// <summary>Lower case with the French accents folded (é è ê ë → <see cref="Sounded"/>, œ → <see cref="Oe"/>, æ → e, ç → s, the others to
    /// their letter), any other diacritic stripped. Plain ASCII is returned as is (plan 10A: snapshot build time).</summary>
    private static string Prepare(string word)
    {
        var w = word.ToLowerInvariant();
        bool ascii = true;
        foreach (var c in w) if (c >= 0x80) { ascii = false; break; }
        if (ascii) return w;
        var sb = new StringBuilder(w.Length);
        foreach (var c in w)
        {
            if (c < 0x80) { sb.Append(c); continue; }
            switch (c)
            {
                case 'é': case 'è': case 'ê': case 'ë': sb.Append(Sounded); break;
                case 'à': case 'â': case 'ä': sb.Append('a'); break;
                case 'î': case 'ï': sb.Append('i'); break;
                case 'ô': case 'ö': sb.Append('o'); break;
                case 'ù': case 'û': case 'ü': sb.Append('u'); break;
                case 'ÿ': sb.Append('y'); break;
                case 'ç': sb.Append('s'); break;
                case 'œ': sb.Append(Oe); break;
                case 'æ': sb.Append('e'); break;
                default: sb.Append(StripDiacritics(c.ToString())); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Lower-cased, accent-stripped form (for edit-distance matching).</summary>
    public static string StripLower(string word) => StripDiacritics((word ?? "").ToLowerInvariant());

    public static string Fold(string word)
    {
        if (string.IsNullOrEmpty(word)) return "";
        var w = Prepare(word);

        var o = new StringBuilder(w.Length);
        int n = w.Length;
        // nasalising context: the n/m is followed by a consonant (not n/m) or ends the word
        bool Nasal(int j) => j >= n || (!IsVowel(w[j]) && w[j] != 'n' && w[j] != 'm');

        for (int i = 0; i < n;)
        {
            // the next two / three letters as chars ('\0' past the end): no substring per position (plan 10A: build time)
            char a0 = w[i], a1 = i + 1 < n ? w[i + 1] : '\0', a2 = i + 2 < n ? w[i + 2] : '\0';

            if (a2 != '\0' && a0 == 'e' && a1 == 'a' && a2 == 'u') { o.Append('o'); i += 3; continue; }
            if (a2 != '\0' && (a0 == 'a' || a0 == 'e') && a1 == 'i' && (a2 == 'n' || a2 == 'm')) { o.Append('E'); i += 3; continue; }

            if (a1 != '\0')
                switch ((a0, a1))
                {
                    case ('a', 'u'): o.Append('o'); i += 2; continue;
                    case ('o', 'u'): o.Append('u'); i += 2; continue;
                    case ('e', 'u'): case ('o', 'e'): o.Append(Sounded); i += 2; continue;   // feu, coeur: /ø/ /œ/
                    case ('a', 'i'): case ('e', 'i'): o.Append(Sounded); i += 2; continue;   // fais, neige: /ɛ/
                    case ('o', 'i'): o.Append("wa"); i += 2; continue;
                    case ('p', 'h'): o.Append('f'); i += 2; continue;
                    case ('c', 'h'): o.Append('C'); i += 2; continue;
                    case ('g', 'n'): o.Append('N'); i += 2; continue;
                    case ('q', 'u'): o.Append('k'); i += 2; continue;
                    case ('a', 'n'): case ('a', 'm'): case ('e', 'n'): case ('e', 'm'):
                        if (Nasal(i + 2)) { o.Append('A'); i += 2; continue; } break;
                    case ('o', 'n'): case ('o', 'm'):
                        if (Nasal(i + 2)) { o.Append('O'); i += 2; continue; } break;
                    case ('u', 'n'): case ('u', 'm'):
                        if (Nasal(i + 2)) { o.Append('U'); i += 2; continue; } break;
                    case ('i', 'n'): case ('i', 'm'): case ('y', 'n'): case ('y', 'm'):
                        if (Nasal(i + 2)) { o.Append('E'); i += 2; continue; } break;
                }

            char c = w[i];
            char next = i + 1 < n ? w[i + 1] : '\0';
            switch (c)
            {
                case 'c': o.Append(next is 'e' or 'i' or 'y' or Sounded ? 's' : 'k'); break;
                case 'g': o.Append(next is 'e' or 'i' or 'y' or Sounded ? 'j' : 'g'); break;
                case 'q': o.Append('k'); break;
                case 's':
                    bool betweenVowels = o.Length > 0 && IsVowel(o[^1]) && IsVowel(next);
                    o.Append(betweenVowels ? 'z' : 's'); break;
                case 'h': break;              // silent
                case 'y': o.Append('i'); break;
                case 'w': o.Append('v'); break;
                case 'x': o.Append("ks"); break;
                case Oe: o.Append(Sounded); break;   // « œ » = the digraph « oe » (cœur / coeur, sœur / soeur: one key)
                default: o.Append(c); break;  // vowels + remaining consonants map to themselves
            }
            i++;
        }

        return Finish(o);
    }
}
