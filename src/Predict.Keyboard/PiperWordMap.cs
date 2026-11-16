using System;
using System.Collections.Generic;
using System.Linq;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>A word of the utterance as the reading view sees it: [Start, Start+Length) in the utterance text, and how
/// many espeak words it is expected to become (« 2026 » → 3).</summary>
internal readonly record struct ReadingUnit(int Start, int Length, int ExpectedWords);

/// <summary>A word-progress mark: the sample (from the start of its sentence's audio) where the unit starts, and the unit.</summary>
internal readonly record struct WordMark(long Sample, int CharPos, int CharLen);

/// <summary>
/// Word spans from Piper's alignment output (plan 08a, step 7; pure). espeak's word boundaries are kept as they are: a
/// liaison consonant stays in the left word (<c>le-z amˈi</c>: « amis » starts after the /z/). When the expected word
/// counts add up to espeak's, units take espeak words in order and a multi-word unit (a number) gets one mark at its
/// first word; otherwise each sentence's units are spread over its voiced span in proportion to their character offsets
/// (<see cref="Plan.Fallback"/>, logged by the caller with counts only).
/// </summary>
internal static class PiperWordMap
{
    /// <summary>Punctuation the reading view shows as tiles but espeak does not say.</summary>
    private const string Silent = ",;:()«»\"-—–…'’“”[]{}/‹›";
    /// <summary>Phonemes that are not sound of a word: word separator, clause punctuation, BOS / EOS / PAD.</summary>
    private const string NotVoiced = " .,?!:;^$_";

    /// <summary>The reading units of <paramref name="text"/> (<see cref="Tokenizer"/>): an elision glued to its word is one
    /// unit (« l'école », « Qu'est-ce », « qu'il »); a hyphenated word is one; a number or a spoken symbol (€ % + &amp;)
    /// expects <paramref name="isolatedWords"/>(token) espeak words, 0 = no unit; any other word expects one.</summary>
    public static List<ReadingUnit> Units(string text, Func<string, int> isolatedWords)
    {
        var toks = Tokenizer.Tokenize(text);
        var o = new List<ReadingUnit>();
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            switch (t.Kind)
            {
                case TokenKind.Elision when i + 1 < toks.Count && toks[i + 1].Kind == TokenKind.Word && toks[i + 1].Start == t.End:
                    o.Add(new ReadingUnit(t.Start, toks[i + 1].End - t.Start, 1));
                    i++;
                    break;
                case TokenKind.Elision:
                case TokenKind.Word:
                    o.Add(new ReadingUnit(t.Start, t.Length, 1));
                    break;
                case TokenKind.Number:
                case TokenKind.Punct when t.Text.Length > 0 && Silent.IndexOf(t.Text[0]) < 0 && !char.IsWhiteSpace(t.Text[0]):
                    int n = isolatedWords(t.Text);
                    if (n > 0) o.Add(new ReadingUnit(t.Start, t.Length, n));
                    break;
            }
        }
        return o;
    }

    /// <summary>espeak words of a phonemized text: phonemes split at <c>" "</c>, keeping only groups with a voiced phoneme.</summary>
    public static int WordCount(IEnumerable<IReadOnlyList<string>> sentences) =>
        sentences.Sum(s => Words(s).Count);

    /// <summary>Per espeak word of one sentence: the index (in the sentence's phoneme list) of its first voiced phoneme.</summary>
    public static List<int> Words(IReadOnlyList<string> phonemes)
    {
        var o = new List<int>();
        bool inWord = false;
        for (int i = 0; i < phonemes.Count; i++)
        {
            if (phonemes[i] == " ") { inWord = false; continue; }
            if (!inWord && IsVoiced(phonemes[i])) { o.Add(i); inWord = true; }
        }
        return o;
    }

    private static bool IsVoiced(string p) => p.Length > 0 && !(p.Length == 1 && NotVoiced.IndexOf(p[0]) >= 0) && p != "-";

    /// <summary>How the units of an utterance map to its sentences' espeak words (decided before synthesis).</summary>
    internal sealed class Plan
    {
        /// <summary>The counts did not add up: proportional placement.</summary>
        public bool Fallback { get; init; }
        public int EspeakWords { get; init; }
        public int UnitCount { get; init; }
        /// <summary>Exact: per sentence, (unit, espeak word index in the sentence). Fallback: per sentence, its units.</summary>
        public List<List<(ReadingUnit Unit, int Word)>> PerSentence { get; init; } = new();
    }

    public static Plan Assign(IReadOnlyList<ReadingUnit> units, IReadOnlyList<IReadOnlyList<string>> sentences)
    {
        var words = sentences.Select(s => Words(s).Count).ToList();
        int total = words.Sum();
        int expected = units.Sum(u => u.ExpectedWords);
        var per = sentences.Select(_ => new List<(ReadingUnit, int)>()).ToList();
        if (sentences.Count == 0) return new Plan { Fallback = units.Count > 0, EspeakWords = 0, UnitCount = units.Count, PerSentence = per };
        if (expected == total)
        {
            int k = 0, w = 0;
            foreach (var u in units)
            {
                while (k < words.Count && w >= words[k]) { k++; w = 0; }
                if (k >= words.Count) break;
                per[k].Add((u, w));
                // a multi-word unit may run over a sentence end: skip its words
                int left = u.ExpectedWords;
                while (left > 0 && k < words.Count)
                {
                    int take = Math.Min(left, words[k] - w);
                    w += take; left -= take;
                    if (w >= words[k] && left > 0) { k++; w = 0; }
                }
            }
            return new Plan { Fallback = false, EspeakWords = total, UnitCount = units.Count, PerSentence = per };
        }
        // fallback: units to sentences in proportion to the expected words before them
        double cum = 0;
        var cumW = new List<double>();
        foreach (var c in words) { cum += c; cumW.Add(cum); }
        double before = 0;
        foreach (var u in units)
        {
            double p = expected == 0 ? 0 : before / expected * Math.Max(1, total);
            int k = 0;
            while (k < cumW.Count - 1 && p >= cumW[k]) k++;
            per[k].Add((u, -1));
            before += u.ExpectedWords;
        }
        return new Plan { Fallback = true, EspeakWords = total, UnitCount = units.Count, PerSentence = per };
    }

    /// <summary>The marks of sentence <paramref name="k"/> from its alignment (<c>[phoneme, samples]</c> for BOS, each
    /// phoneme, EOS, as <see cref="PiperPhonemizer.Groups"/> sums them): samples from the start of that sentence's audio,
    /// strictly increasing, within the audio.</summary>
    public static List<WordMark> Marks(Plan plan, int k, IReadOnlyList<(string Phoneme, long Samples)> aligned)
    {
        var o = new List<WordMark>();
        if (k < 0 || k >= plan.PerSentence.Count || plan.PerSentence[k].Count == 0) return o;
        // offsets of the phonemes (aligned[0] is BOS, aligned[^1] EOS)
        var start = new long[aligned.Count + 1];
        for (int i = 0; i < aligned.Count; i++) start[i + 1] = start[i] + aligned[i].Samples;
        long total = start[aligned.Count];
        var phonemes = aligned.Skip(1).Take(Math.Max(0, aligned.Count - 2)).Select(a => a.Phoneme).ToList();
        var units = plan.PerSentence[k];
        if (!plan.Fallback)
        {
            var words = Words(phonemes);
            foreach (var (u, w) in units)
                if (w >= 0 && w < words.Count) o.Add(new WordMark(start[words[w] + 1], u.Start, u.Length));
        }
        else
        {
            long vs = -1, ve = -1;
            for (int i = 0; i < phonemes.Count; i++)
                if (IsVoiced(phonemes[i])) { if (vs < 0) vs = start[i + 1]; ve = start[i + 2]; }
            if (vs < 0) return o;
            int first = units[0].Unit.Start, last = units[^1].Unit.Start + units[^1].Unit.Length;
            double span = Math.Max(1, last - first);
            foreach (var (u, _) in units)
                o.Add(new WordMark(vs + (long)((u.Start - first) / span * (ve - vs)), u.Start, u.Length));
        }
        // strictly increasing and inside the audio
        long prev = -1;
        for (int i = 0; i < o.Count; i++)
        {
            long s = Math.Min(Math.Max(o[i].Sample, prev + 1), Math.Max(0, total - 1));
            o[i] = o[i] with { Sample = s };
            prev = s;
        }
        return o;
    }
}
