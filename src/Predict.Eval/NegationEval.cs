using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Predict.Core;

namespace PredictEval;

/// <summary>
/// 0.11.0 (plan negation): the measurements of the missing « ne » detector (<see cref="PredictionEngine.MissingNe"/>). Every
/// text is « typed » word by word: after each word and the one or two separators that follow it, the clause before the caret
/// (<see cref="Negation.ClauseOf"/>, as the keyboard's tracker gives it) is checked, exactly as the keyboard would.
/// <list type="bullet">
/// <item><c>cases-negation.json</c>: invented sentences only; a positive must give exactly its <c>expect</c> text at its last
/// word and no alarm before; a negative no alarm anywhere.</item>
/// <item>a held-out <c>.txt</c> (or <c>g-clean.jsonl</c>): (1) the « ne » deletion test — each simple negation in scope (« ne /
/// n' », up to three clitics, one word, the negation word) loses its « ne » (« je n'ai » → « j'ai », « ce n'est » → « c'est »)
/// and the detector must give the original back; (2) the same with noise (pas → pa, jamais → jamé, veut / veux → veu); (3) the
/// clean text as it is: every alarm is counted and dumped (<c>--neg-dump</c>) for hand labelling. Held-out text is never
/// printed, only written to the dump file the caller names (outside the repository).</item>
/// </list>
/// </summary>
internal static class NegationEval
{
    private static readonly string[] NegWords = { "pas", "jamais", "rien", "personne", "guère", "plus" };

    /// <summary>Every check along <paramref name="text"/>: (index of the end of the word checked, the fix).</summary>
    internal static List<(int End, NegationFix Fix, string Tail)> Scan(PredictionEngine eng, string text)
    {
        var o = new List<(int, NegationFix, string)>();
        for (int i = 0; i < text.Length; i++)
        {
            if (!SpellingIndicator.IsWordChar(text[i]) || i + 1 < text.Length && SpellingIndicator.IsWordChar(text[i + 1])) continue;
            int end = i + 1;
            // the keyboard checks after each separator typed: one, then two (« . » + space, space + « ! »)
            for (int k = 1; k <= 2 && end + k <= text.Length; k++)
            {
                if (SpellingIndicator.IsWordChar(text[end + k - 1]) || char.IsDigit(text[end + k - 1])) break;
                var typed = text[..(end + k)];
                if (!Negation.ClauseOf(typed, 0, true, out var toks, out bool startKnown, out var tail, out bool plus)) continue;
                if (eng.MissingNe(toks, startKnown, plus) is { } fix) { o.Add((end, fix, tail)); break; }
            }
        }
        return o;
    }

    /// <summary>The text with the fix applied (the span before <paramref name="end"/> replaced).</summary>
    internal static string Apply(string text, int end, NegationFix fix) =>
        text[..(end - fix.Original.Length)] + fix.Replacement + text[end..];

    private static string Norm(string s) => s.Replace('’', '\'').ToLowerInvariant();

    // ------------------------------------------------------------------ curated cases
    public static void RunCases(PredictionEngine eng, string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        int pos = 0, posExact = 0, posFired = 0, neg = 0, negAlarms = 0;
        var perCat = new SortedDictionary<string, (int N, int Ok)>(StringComparer.Ordinal);
        foreach (var c in root["cases"]!.AsArray())
        {
            string text = (string)c!["text"]!;
            string? expect = c["expect"]?.GetValue<string>();
            string cat = (string?)c["cat"] ?? (expect is null ? "negative" : "positive");
            var hits = Scan(eng, text);
            bool ok;
            string got;
            if (expect is not null)
            {
                pos++;
                var last = hits.Count > 0 ? hits[^1] : default;
                bool atEnd = hits.Count == 1 && text[last.End..].All(ch => !SpellingIndicator.IsWordChar(ch));
                if (hits.Count > 0) posFired++;
                got = hits.Count == 0 ? "(no alarm)" : string.Join(" | ", hits.Select(h => Apply(text, h.End, h.Fix)));
                ok = atEnd && Apply(text, last.End, last.Fix) == expect;
                if (ok) posExact++;
            }
            else
            {
                neg++;
                ok = hits.Count == 0;
                if (!ok) negAlarms++;
                got = hits.Count == 0 ? "(no alarm)" : string.Join(" | ", hits.Select(h => $"{h.Fix.Original} → {h.Fix.Replacement} [{h.Fix.Rule}]"));
            }
            var pc = perCat.TryGetValue(cat, out var x) ? x : (N: 0, Ok: 0);
            perCat[cat] = (pc.N + 1, pc.Ok + (ok ? 1 : 0));
            Console.WriteLine($"{(ok ? "ok " : "XX ")} [{cat}] « {text.TrimEnd()} » → {got}");
        }
        Console.WriteLine();
        foreach (var (k, v) in perCat) Console.WriteLine($"{k,-24} {v.N,4} {v.Ok,4}");
        Console.WriteLine(FormattableString.Invariant($"negation curated: positives {pos} exact {posExact} ({(pos == 0 ? 0 : posExact / (double)pos):0.000}) fired {posFired}; negatives {neg} alarms {negAlarms}"));
        Console.WriteLine($"gates: positives exact >= 0.90 {(pos > 0 && posExact >= 0.9 * pos ? "PASS" : "FAIL")}, negatives 0 alarms {(negAlarms == 0 ? "PASS" : "FAIL")}");
    }

    // ------------------------------------------------------------------ held-out
    private static readonly Regex NeSite = new(
        @"(?<![\p{L}])(?<ne>[Nn]e\s+|[Nn]['’])(?<cl>(?:(?:me|te|se|le|la|les|lui|leur|y|en|nous|vous)\s+|[mtsl]['’]){0,3})(?<v>\p{L}+)(?<sp>\s+)(?<neg>pas|jamais|rien|personne|guère|plus)(?![\p{L}'’-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal sealed class Counts
    {
        public int Sites, Fired, Exact, Wrong;
        public string Line() => FormattableString.Invariant(
            $"sites {Sites,5}  fired {Fired,5} ({R(Fired, Sites)})  exact {Exact,5} ({R(Exact, Sites)})  wrong {Wrong,4} ({R(Wrong, Fired)} of alarms)");
        private static string R(int a, int b) => b == 0 ? "-" : (a / (double)b).ToString("0.000", CultureInfo.InvariantCulture);
    }

    /// <summary>The sentences of a held-out set: a .txt (one per line) or a .jsonl with a « text » field (G-clean).</summary>
    private static IEnumerable<string> Lines(string path, int max)
    {
        int n = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (n >= max) yield break;
            var t = line;
            if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                t = (string?)JsonNode.Parse(line)?["text"] ?? "";
            }
            t = t.Trim();
            if (t.Length == 0) continue;
            n++;
            yield return t;
        }
    }

    public static void RunHeldout(PredictionEngine eng, string path, int max, string? dumpPath, string? offeredPath)
    {
        string set = Path.GetFileNameWithoutExtension(path);
        var del = new SortedDictionary<string, Counts>(StringComparer.Ordinal);
        var noisy = new SortedDictionary<string, Counts>(StringComparer.Ordinal);
        int sentences = 0, cleanAlarms = 0;
        var perRule = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var perWord = new SortedDictionary<string, int>(StringComparer.Ordinal);
        using var dump = dumpPath is null ? null : new StreamWriter(dumpPath, append: true, new UTF8Encoding(false));
        using var offered = offeredPath is null ? null : new StreamWriter(offeredPath, append: true, new UTF8Encoding(false));
        var times = new List<double>();
        foreach (var raw in Lines(path, max))
        {
            sentences++;
            var text = raw + "\n";
            // (3) clean text: every alarm
            long t0 = Stopwatch.GetTimestamp();
            var hits = Scan(eng, text);
            times.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
            foreach (var h in hits)
            {
                cleanAlarms++;
                perRule[h.Fix.Rule.ToString()] = perRule.GetValueOrDefault(h.Fix.Rule.ToString()) + 1;
                perWord[h.Fix.Negation] = perWord.GetValueOrDefault(h.Fix.Negation) + 1;
                dump?.WriteLine($"clean\t{set}\t{h.Fix.Rule}\t{h.Fix.Original}\t{h.Fix.Replacement}\t{raw}");
                offered?.WriteLine(h.Fix.Replacement);
            }
            // (1) and (2): each simple negation in scope loses its « ne »
            foreach (Match m in NeSite.Matches(text))
            {
                if (m.Groups["neg"].Value == "plus" && !PlusEnds(text, m.Index + m.Length)) continue;
                foreach (bool noise in new[] { false, true })
                {
                    var (mod, end, expect) = Delete(text, m, noise);
                    if (mod is null) continue;
                    var d = noise ? noisy : del;
                    string w = m.Groups["neg"].Value;
                    var c = d.TryGetValue(w, out var cc) ? cc : d[w] = new Counts();
                    c.Sites++;
                    // the check right after the negation word, as typed (its separators)
                    NegationFix? fix = null;
                    foreach (var h in Scan(eng, mod[..Math.Min(mod.Length, end + 2)]))
                        if (h.End == end) fix = h.Fix;
                    if (fix is null)
                    {
                        if (!noise) dump?.WriteLine($"del-miss	{set}	-	{mod[..end]}	-	{expect}");
                        continue;
                    }
                    c.Fired++;
                    var got = Apply(mod, end, fix);
                    bool exact = Norm(got[..(end - fix.Original.Length + fix.Replacement.Length)]) == Norm(expect);
                    if (exact) c.Exact++;
                    else
                    {
                        c.Wrong++;
                        dump?.WriteLine($"{(noise ? "noisy-wrong" : "del-wrong")}\t{set}\t{fix.Rule}\t{fix.Original}\t{fix.Replacement}\t{expect}");
                    }
                    offered?.WriteLine(fix.Replacement);
                }
            }
        }
        Console.WriteLine($"set {set}: {sentences} sentences");
        foreach (var (name, d) in new[] { ("deletion", del), ("noisy", noisy) })
        {
            var pooled = new Counts();
            var core = new Counts();
            foreach (var (w, c) in d)
            {
                Console.WriteLine($"  {name,-9} {w,-9} {c.Line()}");
                Add(pooled, c);
                if (w is "pas" or "jamais" or "rien") Add(core, c);
            }
            Console.WriteLine($"  {name,-9} {"all",-9} {pooled.Line()}");
            Console.WriteLine($"  {name,-9} {"pas+jamais+rien",-9} {core.Line()}");
        }
        Console.WriteLine(FormattableString.Invariant($"  clean alarms {cleanAlarms} = {1000.0 * cleanAlarms / Math.Max(1, sentences):0.0} per 1000 sentences  (by rule: {string.Join(", ", perRule.Select(kv => kv.Key + " " + kv.Value))}; by word: {string.Join(", ", perWord.Select(kv => kv.Key + " " + kv.Value))})"));
        if (times.Count > 0)
        {
            times.Sort();
            Console.WriteLine(FormattableString.Invariant($"  scan per sentence (every word checked): p50 {times[times.Count / 2]:0} µs, p99 {times[(int)(times.Count * 0.99)]:0} µs"));
        }
    }

    private static void Add(Counts a, Counts b) { a.Sites += b.Sites; a.Fired += b.Fired; a.Exact += b.Exact; a.Wrong += b.Wrong; }

    private static bool PlusEnds(string text, int at) =>
        at < text.Length && text[at] is '.' or '!' or '?' or '…' or '\n' or '\r';   // owner 2026-11-27: not , ; :

    /// <summary>The sentence without the « ne » of <paramref name="m"/> (« je n'ai » → « j'ai », « ce n'est » → « c'est »), the
    /// index right after its negation word in it, and the original text up to that word (what the fix must give back);
    /// <paramref name="noise"/>: also pas → pa, jamais → jamé, veut / veux → veu (null when nothing to make noisy).</summary>
    private static (string? Text, int End, string Expect) Delete(string text, Match m, bool noise)
    {
        int negEnd = m.Index + m.Length;
        string expect = text[..negEnd];
        string before = text[..m.Index];
        string ne = m.Groups["ne"].Value;
        string rest = text[(m.Index + ne.Length)..];
        // « je ai » / « ce est »: the elision comes back (« j'ai », « c'est »)
        var bm = Regex.Match(before, @"(?<![\p{L}])(?<w>[Jj]e|[Cc]e)\s+$");
        if (ne.Length == 2 && bm.Success)
            before = before[..bm.Index] + bm.Groups["w"].Value[0] + ne[1];
        var mod = before + rest;
        int end = negEnd - (text.Length - mod.Length);
        if (noise)
        {
            string neg = m.Groups["neg"].Value, verb = m.Groups["v"].Value;
            int vAt = m.Groups["v"].Index - (text.Length - mod.Length), negAt = m.Groups["neg"].Index - (text.Length - mod.Length);
            string nneg = neg switch { "pas" => "pa", "jamais" => "jamé", _ => neg };
            string nverb = verb is "veut" or "veux" ? "veu" : verb;
            if (nneg == neg && nverb == verb) return (null, 0, "");
            mod = mod[..vAt] + nverb + mod[(vAt + verb.Length)..negAt] + nneg + mod[(negAt + neg.Length)..];
            end += nverb.Length - verb.Length + nneg.Length - neg.Length;
        }
        return (mod, end, expect);
    }

    // ------------------------------------------------------------------ speed (Predict.Cli --bench-negation uses its own loop)
}
