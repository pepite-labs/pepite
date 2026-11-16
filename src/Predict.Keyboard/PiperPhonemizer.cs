// Derived from piper1-gpl 1.8.0 (https://github.com/OHF-Voice/piper1-gpl), GPL-3.0-or-later:
// phonemize_espeak.py (clauses -> sentences, vowel clusters), phoneme_ids.py (ids), voice.py (alignment groups)
// and the CLAUSE_* constants of espeakbridge.c. See THIRD-PARTY-NOTICES.txt.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Predict.Keyboard;

/// <summary>One clause as espeak-ng returns it (<c>espeak_TextToPhonemesWithTerminator</c>): IPA phonemes, the
/// terminator mapped to <c>. ? ! , : ;</c> (or empty), and whether it ends a sentence.</summary>
internal readonly record struct EspeakClause(string Phonemes, string Terminator, bool EndOfSentence);

/// <summary>
/// The pure part of Piper's espeak phonemizer, ported one for one so the ids fed to the voice equal piper-tts 1.8.0's
/// (golden test, 40/40): clauses → sentences of phonemes (one phoneme = one NFD code point, known vowel clusters
/// merged), phonemes → ids (BOS PAD, each phoneme + PAD, EOS), and the per-phoneme id groups the alignment output is
/// summed over. Not ported: <c>[[ raw phonemes ]]</c> blocks (user text is never phoneme input).
/// </summary>
internal static class PiperPhonemizer
{
    public const string Pad = "_", Bos = "^", Eos = "$";

    // espeakbridge.c
    public const int ClauseIntonationFullStop = 0x00000000, ClauseIntonationComma = 0x00001000,
                     ClauseIntonationQuestion = 0x00002000, ClauseIntonationExclamation = 0x00003000;
    public const int ClauseTypeClause = 0x00040000, ClauseTypeSentence = 0x00080000;
    public const int ClausePeriod = 40 | ClauseIntonationFullStop | ClauseTypeSentence;
    public const int ClauseComma = 20 | ClauseIntonationComma | ClauseTypeClause;
    public const int ClauseQuestion = 40 | ClauseIntonationQuestion | ClauseTypeSentence;
    public const int ClauseExclamation = 45 | ClauseIntonationExclamation | ClauseTypeSentence;
    public const int ClauseColon = 30 | ClauseIntonationFullStop | ClauseTypeClause;
    public const int ClauseSemicolon = 30 | ClauseIntonationComma | ClauseTypeClause;

    private static readonly Regex LangFlag = new(@"\([^)]+\)", RegexOptions.CultureInvariant);

    /// <summary>The clause tuple of espeakbridge.c for a raw terminator value.</summary>
    public static EspeakClause Clause(string phonemes, int terminator)
    {
        terminator &= 0x000FFFFF;
        string t = terminator switch
        {
            ClausePeriod => ".",
            ClauseQuestion => "?",
            ClauseExclamation => "!",
            ClauseComma => ",",
            ClauseColon => ":",
            ClauseSemicolon => ";",
            _ => "",
        };
        return new EspeakClause(phonemes, t, (terminator & ClauseTypeSentence) == ClauseTypeSentence);
    }

    /// <summary>phonemize_espeak.py + voice.py: sentences of phonemes; an empty last sentence is dropped.</summary>
    public static List<List<string>> Sentences(IEnumerable<EspeakClause> clauses, IReadOnlyCollection<string[]>? vowelClusters)
    {
        var all = new List<List<string>>();
        var sentence = new List<string>();
        foreach (var c in clauses)
        {
            var ph = LangFlag.Replace(c.Phonemes, "");   // (en)…(fr) language switch flags
            ph += c.Terminator;                          // punctuation kept as a "phoneme"
            if (c.Terminator is "," or ":" or ";") ph += " ";
            sentence.AddRange(CodePoints(ph.Normalize(NormalizationForm.FormD)));
            if (c.EndOfSentence)
            {
                all.Add(Merge(sentence, vowelClusters));
                sentence = new List<string>();
            }
        }
        if (sentence.Count > 0) all.Add(Merge(sentence, vowelClusters));   // text without a final terminator
        if (all.Count > 0 && all[^1].Count == 0) all.RemoveAt(all.Count - 1);
        return all;
    }

    /// <summary>Above this many phonemes (≈ 35 words) a sentence is synthesised in pieces: one ORT run costs O(N²) in time and memory,
    /// and a long text without punctuation is one espeak sentence.</summary>
    public const int MaxRunPhonemes = 200;

    /// <summary>Split sentences longer than <paramref name="max"/> phonemes: at the last clause boundary (<c>, : ;</c> +
    /// space) before the limit, else at the last word boundary (space), else hard at the limit. Pieces keep every phoneme
    /// in order and a piece never starts with the separating space (word counts are unchanged). Shorter sentences are
    /// returned as they are (the golden ids are not affected).</summary>
    public static List<List<string>> SplitLong(List<List<string>> sentences, int max)
    {
        if (sentences.All(x => x.Count <= max)) return sentences;
        var o = new List<List<string>>();
        foreach (var sentence in sentences)
        {
            int start = 0;
            while (sentence.Count - start > max)
            {
                int cut = -1;
                for (int i = start + max - 1; i > start; i--)
                    if (sentence[i] == " " && sentence[i - 1] is "," or ":" or ";") { cut = i; break; }
                if (cut < 0)
                    for (int i = start + max - 1; i > start; i--)
                        if (sentence[i] == " ") { cut = i; break; }
                if (cut < 0) cut = start + max;
                o.Add(sentence.GetRange(start, cut - start));
                start = cut;
                while (start < sentence.Count && sentence[start] == " ") start++;   // the space ends the piece before
            }
            if (start < sentence.Count) o.Add(sentence.GetRange(start, sentence.Count - start));
        }
        return o;
    }

    /// <summary>Python's <c>list(str)</c>: one string per Unicode code point.</summary>
    public static IEnumerable<string> CodePoints(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { yield return s.Substring(i, 2); i++; }
            else yield return s[i].ToString();
        }
    }

    /// <summary>_merge_known_vowel_clusters: longest known cluster first, left to right.</summary>
    public static List<string> Merge(List<string> phones, IReadOnlyCollection<string[]>? clusters)
    {
        if (clusters is null || clusters.Count == 0) return phones;
        int maxLen = clusters.Max(k => k.Length);
        var o = new List<string>(phones.Count);
        int i = 0;
        while (i < phones.Count)
        {
            string[]? match = null;
            for (int n = Math.Min(maxLen, phones.Count - i); n > 1; n--)
            {
                foreach (var k in clusters)
                    if (k.Length == n && Enumerable.Range(0, n).All(j => phones[i + j] == k[j])) { match = k; break; }
                if (match != null) break;
            }
            if (match != null) { o.Add(string.Concat(match)); i += match.Length; }
            else { o.Add(phones[i]); i++; }
        }
        return o;
    }

    /// <summary>phoneme_ids.py: BOS + PAD, each known phoneme + PAD, EOS. Unknown phonemes are skipped and counted in
    /// <paramref name="unknown"/>.</summary>
    public static long[] Ids(IReadOnlyList<string> phonemes, IReadOnlyDictionary<string, int[]> map, out int unknown)
    {
        var ids = new List<long>(phonemes.Count * 2 + 3);
        unknown = 0;
        var pad = map[Pad];
        ids.AddRange(map[Bos].Select(x => (long)x));
        ids.AddRange(pad.Select(x => (long)x));
        foreach (var p in phonemes)
        {
            if (!map.TryGetValue(p, out var v)) { unknown++; continue; }
            ids.AddRange(v.Select(x => (long)x));
            ids.AddRange(pad.Select(x => (long)x));
        }
        ids.AddRange(map[Eos].Select(x => (long)x));
        return ids.ToArray();
    }

    /// <summary>voice.py alignment: for BOS, each phoneme and EOS, how many ids it produced (its own ids + the PAD after
    /// it; EOS without PAD). Summing the per-id sample counts over these groups gives samples per phoneme. Null when the
    /// ids do not line up (an unknown phoneme was skipped): Piper then gives no alignment either.</summary>
    public static List<(string Phoneme, int Count)>? Groups(IReadOnlyList<string> phonemes, IReadOnlyDictionary<string, int[]> map, IReadOnlyList<long> ids)
    {
        var pad = map.TryGetValue(Pad, out var pv) ? pv : Array.Empty<int>();
        var o = new List<(string, int)>(phonemes.Count + 2);
        int k = 0;
        foreach (var p in new[] { Bos }.Concat(phonemes).Append(Eos))
        {
            var expected = map.TryGetValue(p, out var e) ? e : Array.Empty<int>();
            var check = p == Eos ? expected : expected.Concat(pad).ToArray();
            int start = k;
            foreach (var id in check)
            {
                if (k >= ids.Count || ids[k] != id) return null;
                k++;
            }
            o.Add((p, k - start));
        }
        return o;
    }
}

/// <summary>
/// One-for-one replacements before espeak, so a character offset in the text given to espeak is the same in the
/// utterance text (the same rule as <see cref="ReadingDocument.SpeakText"/>): no-break, narrow and thin spaces → space
/// (the D22 narrow no-break space before <c>! ?</c> must not change the ids), <c>’</c> → <c>'</c>, line breaks and tabs
/// → space.
/// </summary>
internal static class TextCleanup
{
    public static string ForEspeak(string text) => ReadingDocument.SpeakText(text, 0, text.Length);
}
