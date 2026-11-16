using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Predict.Keyboard;

/// <summary>
/// A word said alone inside a carrier sentence (owner's blind sheet of 0.6.0, 2026-11-14, variant W5: 4.83 / 5 against
/// 2–2.75 for every way of saying the word on its own). Pierre, given one short word, ends it mid-sound and squeezes its
/// vowel; inside « Le mot : très. » the same word comes out whole. The carrier is synthesised with the voice's sentence
/// scales and the word is cut out on the model's alignment: the cut is at the quietest 5 ms of the 150 ms before the
/// word's aligned start (the alignment's 11.6 ms hop is too coarse for a voiced onset such as m, v, ʃ), with a 10 ms
/// fade-in, then peak-normalised again. The word keeps its natural end (the « . » and EOS frames). Pure; used by
/// <see cref="PiperSynth"/> and the bench alike, so the app plays what the owner rated.
/// </summary>
internal static class PiperCarrier
{
    /// <summary>The phonemes espeak gives a clause boundary inside a sentence (followed by a <c>" "</c> phoneme).</summary>
    private static readonly string[] Separators = { ",", ":", ";" };

    /// <summary>A carrier text format: one <c>{0}</c> (the word), no other brace, and at least one clause boundary
    /// (<c>, : ;</c>) before it: the cut is found after that boundary in the phonemes, not by counting words (a liaison
    /// group or « il y a » is several espeak words).</summary>
    public static bool IsValidFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return false;
        int at = format.IndexOf("{0}", StringComparison.Ordinal);
        if (at < 0 || format.IndexOf("{0}", at + 3, StringComparison.Ordinal) >= 0) return false;
        var rest = format.Remove(at, 3);
        if (rest.IndexOf('{') >= 0 || rest.IndexOf('}') >= 0) return false;
        return SeparatorsBefore(format) > 0;
    }

    /// <summary>The clause boundaries (<c>, : ;</c>) of the format before <c>{0}</c>: the word starts after that many
    /// boundaries in the carrier's phonemes.</summary>
    public static int SeparatorsBefore(string format)
    {
        int at = format.IndexOf("{0}", StringComparison.Ordinal);
        return at < 0 ? 0 : format.Take(at).Count(c => c is ',' or ':' or ';');
    }

    /// <summary>The text given to espeak: the format with the word (trimmed).</summary>
    public static string Text(string format, string word) =>
        string.Format(CultureInfo.InvariantCulture, format, word.Trim());

    /// <summary>The word as a card's <c>word_carrier_except</c> list compares it: trimmed as <see cref="Text"/> does,
    /// NFC, lower case (invariant), so « Eu », « eu » and « eu » typed with a decomposed accent all match.</summary>
    public static string ExceptKey(string word) => word.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();

    /// <summary>The carrier is for isolated words: a short text (the word cache's bound, <see
    /// cref="PiperWordCache.MaxTextLength"/>) without a sentence end of its own (that would split the carrier into
    /// two sentences: the plain word synthesis is used).</summary>
    public static bool Applies(string text)
    {
        var t = text.Trim();
        return t.Length > 0 && t.Length <= PiperWordCache.MaxTextLength && t.IndexOfAny(new[] { '.', '?', '!', '…' }) < 0;
    }

    /// <summary>The word cut out of the carrier, and what the bench reports about the cut.</summary>
    /// <param name="AlignedStart">sample where the alignment says the word starts (after the boundary's space).</param>
    /// <param name="CutStart">sample where the cut was made (≤ <paramref name="AlignedStart"/>).</param>
    /// <param name="LevelAtCut">peak level within ±5 ms of the cut, relative to the carrier's peak (≈ 0: cut in silence).</param>
    /// <param name="WordAlign">the alignment from the word's first phoneme to EOS.</param>
    internal sealed record Cut(float[] Audio, long AlignedStart, int CutStart, float LevelAtCut,
                               List<(string Phoneme, long Samples)> WordAlign);

    /// <summary>Cut the word out of the carrier's audio. Null (with <paramref name="why"/>, for the log: no text) when
    /// the alignment is missing (an unknown phoneme was skipped: the ids do not line up), the boundary is not found, no
    /// voiced phoneme follows it, or the start is past the audio: the caller then synthesises the word on its own.</summary>
    /// <param name="aligned">samples per phoneme, BOS first (<see cref="PiperPhonemizer.Groups"/> summed).</param>
    /// <param name="separators"><see cref="SeparatorsBefore"/> of the format.</param>
    public static Cut? Extract(float[] audio, IReadOnlyList<(string Phoneme, long Samples)>? aligned, int separators, int rate, out string? why)
    {
        why = null;
        if (aligned is null || aligned.Count == 0) { why = "no alignment"; return null; }
        if (separators <= 0) { why = "no boundary in the format"; return null; }
        int boundary = -1, seen = 0;
        for (int i = 0; i + 1 < aligned.Count; i++)
            if (Array.IndexOf(Separators, aligned[i].Phoneme) >= 0 && aligned[i + 1].Phoneme == " " && ++seen == separators)
            {
                boundary = i + 1;
                break;
            }
        if (boundary < 0) { why = "boundary not found"; return null; }
        if (!aligned.Skip(boundary + 1).Any(x => IsVoiced(x.Phoneme))) { why = "no word after the boundary"; return null; }
        long start = 0;
        for (int i = 0; i <= boundary; i++) start += aligned[i].Samples;
        if (start <= 0 || start >= audio.Length) { why = "start outside the audio"; return null; }

        // the quietest 5 ms (half-window steps) within the 150 ms before the aligned start; none (start < 5 ms): 10 ms before
        int win = Math.Max(1, rate / 200), from = (int)Math.Max(0, start - rate * 10 / 1000);
        double best = double.MaxValue;
        for (int s = (int)Math.Max(0, start - rate * 150 / 1000); s + win <= Math.Min(audio.Length, start); s += Math.Max(1, win / 2))
        {
            double e = 0;
            for (int i = s; i < s + win; i++) e += audio[i] * (double)audio[i];
            if (e < best) { best = e; from = s + win / 2; }
        }
        float atCut = 0;
        for (int i = Math.Max(0, from - rate / 200); i < Math.Min(audio.Length, from + rate / 200); i++) atCut = Math.Max(atCut, Math.Abs(audio[i]));
        var cut = new float[audio.Length - from];
        Array.Copy(audio, from, cut, 0, cut.Length);
        int fade = Math.Min(cut.Length, rate / 100);
        for (int i = 0; i < fade; i++) cut[i] *= (float)i / fade;
        Normalize(cut);
        return new Cut(cut, start, from, atCut, aligned.Skip(boundary + 1).ToList());
    }

    private static bool IsVoiced(string p) => p.Length > 0 && !(p.Length == 1 && " .,?!:;^$_-".IndexOf(p[0]) >= 0);

    /// <summary>Piper's normalize_audio (as <c>PiperModel.Normalize</c>): divide by the peak, clip to [−1, 1].</summary>
    internal static void Normalize(float[] a)
    {
        float max = 0;
        foreach (var x in a) max = Math.Max(max, Math.Abs(x));
        if (max < 1e-8f) { Array.Clear(a); return; }
        for (int i = 0; i < a.Length; i++) a[i] = Math.Clamp(a[i] / max, -1f, 1f);
    }
}

/// <summary>
/// Phoneme post-corrections of one voice (a card's <c>phoneme_fix</c>), applied to espeak's phonemes before the ids,
/// wherever that voice speaks (words, sentences, reading). Pierre: <c>iʲ → i</c> (owner's blind sheet, 2026-11-14:
/// « il y a » 5 / 5 alone and 4 / 3 in a sentence against 1 and 4 / 3 unfixed: espeak's palatal mark ʲ is a phoneme of
/// its own and pierre stretches it). A rule never contains a space and both sides have a sounding phoneme, so the espeak
/// word count (the word map of the highlight, the liaison groups) is the same before and after; the alignment groups
/// are computed on the corrected phonemes. Pure.
/// </summary>
internal sealed class PiperPhonemeFix
{
    private readonly List<(string From, string To)> _rules;

    private PiperPhonemeFix(List<(string From, string To)> rules) => _rules = rules;

    public IReadOnlyList<(string From, string To)> Rules => _rules;

    /// <summary>Rules as NFD strings (phonemes are NFD code points). Throws <see cref="FormatException"/> for an empty
    /// rule, a space, or a side without a sounding phoneme (it could add or remove an espeak word).</summary>
    public static PiperPhonemeFix Create(IEnumerable<(string From, string To)> rules)
    {
        var list = new List<(string, string)>();
        foreach (var (f, t) in rules)
        {
            var from = (f ?? "").Normalize(NormalizationForm.FormD);
            var to = (t ?? "").Normalize(NormalizationForm.FormD);
            if (from.Length == 0) throw new FormatException("phoneme_fix: empty rule");
            if (from.Any(char.IsWhiteSpace) || to.Any(char.IsWhiteSpace)) throw new FormatException("phoneme_fix: a rule may not contain a space");
            if (!Sounding(from) || !Sounding(to)) throw new FormatException("phoneme_fix: both sides need a sounding phoneme");
            list.Add((from, to));
        }
        if (list.Count == 0) throw new FormatException("phoneme_fix: no rule");
        return new PiperPhonemeFix(list);
    }

    /// <summary>The bench's <c>--phoneme-fix a=b;c=d</c> (<c>\uXXXX</c> escapes allowed).</summary>
    public static PiperPhonemeFix Parse(string spec) =>
        Create(spec.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                   .Select(p => (System.Text.RegularExpressions.Regex.Unescape(p[0]),
                                 System.Text.RegularExpressions.Regex.Unescape(p.Length > 1 ? p[1] : "")))
                   .ToList());

    private static bool Sounding(string s) => s.Any(c => " .,?!:;^$_-".IndexOf(c) < 0);

    /// <summary>Each sentence's phonemes joined, the rules replaced in order, then split into code points and the
    /// voice's vowel clusters merged again (as the phonemizer does). A sentence no rule matches is returned as it is.</summary>
    public List<List<string>> Apply(List<List<string>> sentences, IReadOnlyCollection<string[]>? vowelClusters)
    {
        var o = new List<List<string>>(sentences.Count);
        foreach (var s in sentences)
        {
            var joined = string.Concat(s);
            var t = joined;
            foreach (var (from, to) in _rules) t = t.Replace(from, to, StringComparison.Ordinal);
            o.Add(ReferenceEquals(t, joined) || t == joined ? s : PiperPhonemizer.Merge(PiperPhonemizer.CodePoints(t).ToList(), vowelClusters));
        }
        return o;
    }
}
