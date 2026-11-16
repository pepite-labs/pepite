using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Predict.Core;

namespace PredictEval;

/// <summary>
/// Plan 11 (10D-0): the grammar measurements of <c>Predict.Eval</c> — P-agreement (<c>grammar-cases.json</c>), the jsonl
/// sets (G-invented, G-synthetic: each labelled error typed in full), P-auto (<c>--siblings</c>: sound-alike forms of the
/// same lemma in held-out text) and the dev grid of <c>--sweep-grammar</c>. With <c>--no-grammar-rank</c> (or every weight at
/// 0) a run is the 0.8.3 baseline; 10D-1 adds the agreement term and its weights. Only our own invented sentences are printed; held-out text
/// is never printed.
/// </summary>
internal static class GrammarEval
{
    private const int K = 8;
    private static readonly Regex Tok = new("[a-zà-ÿœ]+", RegexOptions.Compiled);

    /// <param name="fromMorph">review 10D-0: build the index at load from <c>morph</c> (no <c>pred_index</c> needed) —
    /// measured for the 10D-1 choice, one fresh process per run.</param>
    public static GrammarLexicon Load(string path, PredictionEngine eng, bool fromMorph = false)
    {
        long m0 = GC.GetTotalMemory(true);
        using var proc = Process.GetCurrentProcess();
        long ws0 = proc.WorkingSet64;
        var sw = Stopwatch.StartNew();
        var g = fromMorph ? GrammarLexicon.LoadFromMorph(path, eng) : GrammarLexicon.Load(path, eng);
        double ms = sw.Elapsed.TotalMilliseconds;
        long m1 = GC.GetTotalMemory(true);
        proc.Refresh();
        Console.WriteLine(FormattableString.Invariant($"grammar{(fromMorph ? " (index built from morph)" : "")}: {g.FormCount} forms, {g.LemmaCount} lemmas, {g.AnalysisCount} analyses, loaded in {ms:0} ms, managed memory +{(m1 - m0) / 1048576.0:0.0} MB, working set +{(proc.WorkingSet64 - ws0) / 1048576.0:0} MB (peak {proc.PeakWorkingSet64 / 1048576.0:0} MB)"));
        return g;
    }

    // ------------------------------------------------------------------ statistics
    internal sealed class Stat
    {
        public int N, Top1, Hit; public double Mrr;
        public void Add(int rank) { N++; if (rank == 1) Top1++; if (rank is > 0 and <= K) Hit++; if (rank > 0) Mrr += 1.0 / rank; }
        public double T1 => N == 0 ? 0 : Top1 / (double)N;
        public double H8 => N == 0 ? 0 : Hit / (double)N;
        public double MRR => N == 0 ? 0 : Mrr / N;
        public JsonObject Json() => new() { ["n"] = N, ["top1"] = Math.Round(T1, 4), ["hit8"] = Math.Round(H8, 4), ["mrr"] = Math.Round(MRR, 4) };
    }

    private static Stat Get(IDictionary<string, Stat> d, string k) => d.TryGetValue(k, out var s) ? s : d[k] = new Stat();
    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static int RankOf(IReadOnlyList<Suggestion> s, string w) { for (int i = 0; i < s.Count; i++) if (Eq(s[i].Word, w)) return i + 1; return 0; }
    private static bool AtStart(string? context) => context is null || context.TrimEnd().Length == 0 || ".!?…".IndexOf(context.TrimEnd()[^1]) >= 0;
    private static string F(double x) => x.ToString("0.00", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ P-agreement (grammar-cases.json)
    internal sealed record GCase(int Id, string Cat, string Domain, string? Context, string Intended, string? Wrong);

    internal static List<GCase> ReadCases(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException(path);
        var arr = (root is JsonObject o ? o["cases"] : root) as JsonArray ?? throw new InvalidDataException("no cases: " + path);
        var list = new List<GCase>(arr.Count);
        foreach (var c in arr)
            list.Add(new GCase((int)c!["id"]!, (string)c["cat"]!, (string)c["domain"]!, (string?)c["context"], (string)c["intended"]!, (string?)c["wrong"]));
        return list;
    }

    internal static bool InSplit(int id, string split) => split switch { "dev" => id % 2 == 0, "test" => id % 2 == 1, _ => true };

    /// <summary>Per case: for p = 0…3 the first p letters of <c>intended</c> (p = 0: next word), and <c>wrong</c> typed in
    /// full (« swap »); the rank of <c>intended</c> in the bar (8 words, elisions shown as the bar shows them).</summary>
    public static (double pooledTop1, int n) RunCases(PredictionEngine eng, string path, string split, string? jsonOut, bool print = true)
    {
        var cases = ReadCases(path).Where(c => InSplit(c.Id, split)).ToList();
        var rows = new SortedDictionary<string, Dictionary<string, Stat>>(StringComparer.Ordinal);
        void Bump(string row, string col, int rank)
        {
            if (!rows.TryGetValue(row, out var d)) rows[row] = d = new Dictionary<string, Stat>();
            Get(d, col).Add(rank);
            if (col != "swap") Get(d, "pooled").Add(rank);
        }
        foreach (var c in cases)
        {
            var ctx = c.Context is null ? null : new[] { c.Context };
            bool start = AtStart(c.Context);
            for (int p = 0; p <= 3; p++)
            {
                if (p > c.Intended.Length) continue;
                var s = Elision.Apply(p == 0 ? eng.PredictNext(ctx, K) : eng.Predict(c.Intended[..p], ctx, K, start));
                int r = RankOf(s, c.Intended);
                foreach (var row in new[] { c.Cat, "domain " + c.Domain, "all" }) Bump(row, "p" + p, r);
            }
            if (!string.IsNullOrEmpty(c.Wrong))
            {
                var s = Elision.Apply(eng.Predict(c.Wrong, ctx, K, start));
                int r = RankOf(s, c.Intended);
                foreach (var row in new[] { c.Cat, "domain " + c.Domain, "all" }) Bump(row, "swap", r);
            }
        }
        var all = rows.TryGetValue("all", out var a) ? Get(a, "pooled") : new Stat();
        if (print)
        {
            Console.WriteLine($"P-agreement: {cases.Count} cases of {Path.GetFileName(path)} (split {split}); top-1 / hit@8 per number of typed letters (p0 = next word), swap = the wrong sibling typed in full\n");
            Console.WriteLine($"{"category",-16} {"n",4} {"p0",11} {"p1",11} {"p2",11} {"p3",11} {"swap",11} {"pooled top1",12}");
            foreach (var (row, d) in rows.OrderBy(r => r.Key.StartsWith("domain") ? 1 : r.Key == "all" ? 2 : 0).ThenBy(r => r.Key, StringComparer.Ordinal))
            {
                string Cell(string col) => d.TryGetValue(col, out var s) ? $"{F(s.T1)} / {F(s.H8)}" : "-";
                int n = d.TryGetValue("p0", out var s0) ? s0.N : 0;
                Console.WriteLine($"{row,-16} {n,4} {Cell("p0"),11} {Cell("p1"),11} {Cell("p2"),11} {Cell("p3"),11} {Cell("swap"),11} {F(Get(d, "pooled").T1),12}");
            }
        }
        if (jsonOut != null)
        {
            var j = new JsonObject { ["file"] = Path.GetFileName(path), ["split"] = split, ["cases"] = cases.Count };
            var r = new JsonObject();
            foreach (var (row, d) in rows)
            {
                var o = new JsonObject();
                foreach (var (col, s) in d.OrderBy(x => x.Key, StringComparer.Ordinal)) o[col] = s.Json();
                r[row] = o;
            }
            j["rows"] = r;
            File.WriteAllText(jsonOut, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        return (all.T1, all.N);
    }

    // ------------------------------------------------------------------ jsonl sets (G-invented, G-synthetic)
    /// <summary>Each labelled error is typed in full with the <b>typed</b> sentence before it as the context (what the bar
    /// sees); is <c>gold</c> top-1 / in the 8 words, and how often is the typed form itself first (the bar keeps it)?</summary>
    public static void RunJsonl(PredictionEngine eng, string path, string? jsonOut)
    {
        var gold = new SortedDictionary<string, Stat>(StringComparer.Ordinal);
        var typedFirst = new Dictionary<string, int>(StringComparer.Ordinal);
        int sentences = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (JsonNode.Parse(line) is not JsonObject o || o["errors"] is not JsonArray errs || o["text"] is null) continue;
            sentences++;
            var text = (string)o["text"]!;
            foreach (var e in errs)
            {
                int start = (int)e!["start"]!;
                var typed = (string)e["typed"]!;
                var g = (string)e["gold"]!;
                var cls = (string)e["class"]!;
                var before = text[..start];
                var ctx = before.Trim().Length == 0 ? null : new[] { before };
                var s = Elision.Apply(eng.Predict(typed, ctx, K, AtStart(before)));
                int r = RankOf(s, g);
                foreach (var k in new[] { cls, "all" })
                {
                    Get(gold, k).Add(r);
                    if (s.Count > 0 && Eq(s[0].Word, typed)) typedFirst[k] = typedFirst.GetValueOrDefault(k) + 1;
                }
            }
        }
        Console.WriteLine($"{Path.GetFileName(path)}: {sentences} sentences; each error typed in full after the typed sentence before it\n");
        Console.WriteLine($"{"class",-10} {"errors",6} {"gold top1",10} {"gold hit@8",11} {"gold MRR",9} {"typed first",12}");
        foreach (var (k, s) in gold.OrderBy(x => x.Key == "all" ? 1 : 0).ThenBy(x => x.Key, StringComparer.Ordinal))
            Console.WriteLine($"{k,-10} {s.N,6} {F(s.T1),10} {F(s.H8),11} {F(s.MRR),9} {F(typedFirst.GetValueOrDefault(k) / (double)Math.Max(1, s.N)),12}");
        if (jsonOut != null)
        {
            var j = new JsonObject { ["file"] = Path.GetFileName(path), ["sentences"] = sentences };
            var r = new JsonObject();
            foreach (var (k, s) in gold) { var x = s.Json(); x["typed_first"] = typedFirst.GetValueOrDefault(k); r[k] = x; }
            j["classes"] = r;
            File.WriteAllText(jsonOut, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
    }

    // ------------------------------------------------------------------ P-auto (--siblings)
    internal sealed record SibInfo(string[] Sibs, string Cls, int Prefix, string Swap);

    /// <summary>The sound-alike siblings of a lexicon word: another lexicon form of one of its lemmas with the same
    /// <see cref="PhoneticKey.Fold"/> whose analysis differs in number, gender, person, Q / Y / 2p or participle
    /// agreement — or another member of its homophone family. Null when there is none. The class is the one of the most
    /// frequent sibling, which is also the one typed in swap mode.</summary>
    internal static SibInfo? SiblingsOf(PredictionEngine eng, GrammarLexicon g, string w)
    {
        var fam = HomophoneFamilies.Of(w);   // 10D-1: the table moved to Predict.Core
        var found = new List<(string Form, string Cls, double Freq)>();
        if (fam != null)
        {
            foreach (var m in fam)
                if (m != w && eng.IndexOf(m) is var mi and >= 0) found.Add((m, "family", eng.FreqAt(mi)));
        }
        else
        {
            int e = eng.IndexOf(w);
            if (e < 0) return null;
            var fold = PhoneticKey.Fold(w);
            var lem = g.LemmasOf(e);
            var bits = g.BitsOf(e);
            var done = new HashSet<int>();
            for (int k = 0; k < lem.Length; k++)
            {
                int l = lem[k];
                if (!done.Add(l)) continue;
                var mine = Of(bits, lem, l);
                foreach (var f in g.FormsOfLemma(l))
                {
                    if (f == e) continue;
                    var sform = eng.FormAt(f);
                    if (HomophoneFamilies.IsMember(sform) || PhoneticKey.Fold(sform) != fold) continue;
                    var cls = PairClass(mine, Of(g.BitsOf(f), g.LemmasOf(f), l));
                    if (cls != null && !found.Any(x => x.Form == sform)) found.Add((sform, cls, eng.FreqAt(f)));
                }
            }
        }
        if (found.Count == 0) return null;
        var top = found.OrderByDescending(x => x.Freq).ThenBy(x => x.Form, StringComparer.Ordinal).First();
        int lcp = w.Length;
        foreach (var (s, _, _) in found)
        {
            int i = 0;
            while (i < lcp && i < s.Length && w[i] == s[i]) i++;
            lcp = i;
        }
        return new SibInfo(found.Select(x => x.Form).ToArray(), top.Cls, Math.Min(lcp, w.Length - 1), top.Form);

        static List<uint> Of(ReadOnlySpan<uint> b, ReadOnlySpan<int> l, int lemma)
        {
            var o = new List<uint>();
            for (int i = 0; i < b.Length; i++) if (l[i] == lemma) o.Add(b[i]);
            return o;
        }
    }

    /// <summary>The class of a pair of analyses of one lemma (as build_grammar_sets.classify, on bits).</summary>
    internal static string? PairClass(List<uint> a, List<uint> b)
    {
        static bool Has(List<uint> x, uint pos) => x.Any(v => MorphBits.Pos(v) == pos);
        static uint Or(List<uint> x, uint pos) { uint o = 0; foreach (var v in x) if (MorphBits.Pos(v) == pos) o |= v; return o; }
        static string? Kind(List<uint> x)
        {
            if (Has(x, MorphBits.PastParticiple)) return "Q";
            if (Has(x, MorphBits.Infinitive)) return "Y";
            uint f = Or(x, MorphBits.FiniteVerb);
            return (f & MorphBits.P2p) != 0 && (f & (MorphBits.Indicative | MorphBits.Imperative)) != 0 ? "2p" : null;
        }
        static bool Both(uint gn, uint mask) => (gn & mask) == mask;
        if (Has(a, MorphBits.PastParticiple) && Has(b, MorphBits.PastParticiple))
        {
            uint qa = Or(a, MorphBits.PastParticiple), qb = Or(b, MorphBits.PastParticiple);
            bool open = Both(qa, MorphBits.GenderMask) || Both(qb, MorphBits.GenderMask) || Both(qa, MorphBits.NumberMask) || Both(qb, MorphBits.NumberMask);
            return !open && (qa & (MorphBits.GenderMask | MorphBits.NumberMask)) != (qb & (MorphBits.GenderMask | MorphBits.NumberMask)) ? "qgn" : null;
        }
        string? ka = Kind(a), kb = Kind(b);
        if (ka != null && kb != null && ka != kb) return "qy2p";
        if (Has(a, MorphBits.FiniteVerb) && Has(b, MorphBits.FiniteVerb) && !(ka != null && kb != null))
        {
            uint pa = Or(a, MorphBits.FiniteVerb) & MorphBits.PersonMask, pb = Or(b, MorphBits.FiniteVerb) & MorphBits.PersonMask;
            if ((pa & ~pb) != 0) return "person";
        }
        foreach (var pos in new[] { MorphBits.Noun, MorphBits.Adjective })
        {
            if (!Has(a, pos) || !Has(b, pos) || Has(a, MorphBits.PastParticiple) || Has(b, MorphBits.PastParticiple)) continue;
            uint x = Or(a, pos), y = Or(b, pos);
            uint ga = x & MorphBits.GenderMask, gb = y & MorphBits.GenderMask, na = x & MorphBits.NumberMask, nb = y & MorphBits.NumberMask;
            if (ga == gb && na != nb && na != MorphBits.NumberMask && nb != MorphBits.NumberMask) return "num";
            if (pos == MorphBits.Adjective && na == nb && ga != gb && ga != MorphBits.GenderMask && gb != MorphBits.GenderMask) return "gender";
        }
        return null;
    }

    private static void Measure(SibStats st, SibInfo info, string row, List<string> toks, int j, string w, PredictionEngine eng)
    {
        var ctx = toks.GetRange(0, j);
        var s = info.Prefix == 0 ? eng.PredictNext(ctx, K) : eng.Predict(w[..info.Prefix], ctx, K);
        int r = RankOf(s, w);
        bool first = r > 0 && info.Sibs.All(x => RankOf(s, x) is 0 || RankOf(s, x) > r);
        int r2 = RankOf(eng.Predict(info.Swap, ctx, K), w);
        var x = st.Row(row);
        x.Prefix.Add(r); x.Swap.Add(r2);
        st.By[row] = (x.Prefix, x.Swap, x.SibTop1 + (first ? 1 : 0), x.Letters + info.Prefix);
    }

    /// <summary>Review 10D-0: the Q / Y / 2p siblings of a word — lexicon forms of one of its lemmas with the <b>same
    /// Lexique pronunciation</b> (<c>words.phon</c>) whose pair class is qy2p (`mangé` / `manger` / `mangez`: the grapheme
    /// fold keeps them apart). Null when there is none.</summary>
    internal static SibInfo? QySiblingsOf(PredictionEngine eng, GrammarLexicon g, string w, IReadOnlyDictionary<string, string> phon)
    {
        int e = eng.IndexOf(w);
        if (e < 0 || !phon.TryGetValue(w, out var ph) || ph.Length == 0) return null;
        var lem = g.LemmasOf(e);
        var bits = g.BitsOf(e);
        var found = new List<(string Form, double Freq)>();
        var done = new HashSet<int>();
        for (int k = 0; k < lem.Length; k++)
        {
            int l = lem[k];
            if (!done.Add(l)) continue;
            var mine = new List<uint>();
            for (int i = 0; i < bits.Length; i++) if (lem[i] == l) mine.Add(bits[i]);
            foreach (var f in g.FormsOfLemma(l))
            {
                if (f == e) continue;
                var sform = eng.FormAt(f);
                if (!phon.TryGetValue(sform, out var sp) || sp != ph || found.Any(x => x.Form == sform)) continue;
                var theirs = new List<uint>();
                var fl = g.LemmasOf(f); var fb = g.BitsOf(f);
                for (int i = 0; i < fb.Length; i++) if (fl[i] == l) theirs.Add(fb[i]);
                if (PairClass(mine, theirs) == "qy2p") found.Add((sform, eng.FreqAt(f)));
            }
        }
        if (found.Count == 0) return null;
        var top = found.OrderByDescending(x => x.Freq).ThenBy(x => x.Form, StringComparer.Ordinal).First();
        int lcp = w.Length;
        foreach (var (sf, _) in found)
        {
            int i = 0;
            while (i < lcp && i < sf.Length && w[i] == sf[i]) i++;
            lcp = i;
        }
        return new SibInfo(found.Select(x => x.Form).ToArray(), "qy2p", Math.Min(lcp, w.Length - 1), top.Form);
    }

    /// <summary>Lexique pronunciations of the lexicon forms (lowercase → <c>words.phon</c>).</summary>
    internal static Dictionary<string, string> ReadPhon(string db)
    {
        var d = new Dictionary<string, string>(130_000, StringComparer.Ordinal);
        using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
        con.Open();
        using var c = con.CreateCommand();
        c.CommandText = "SELECT ortho, phon FROM words";
        using var r = c.ExecuteReader();
        while (r.Read()) d[r.GetString(0).ToLowerInvariant()] = r.IsDBNull(1) ? "" : r.GetString(1);
        return d;
    }

    /// <summary>Review 10D-0, for the 10D-1 choice: <see cref="GrammarLexicon.Load"/> (shipped <c>pred_index</c>) against
    /// <see cref="GrammarLexicon.LoadFromMorph"/> (index built at load from <c>morph</c>): time and managed memory of each,
    /// <paramref name="rounds"/> alternating rounds, and whether both give the same arrays.</summary>
    public static void CompareLoads(string path, PredictionEngine eng, int rounds)
    {
        var a = GrammarLexicon.Load(path, eng);
        var b = GrammarLexicon.LoadFromMorph(path, eng);
        Console.WriteLine($"same arrays: {a.SameAs(b)} ({a.FormCount} / {b.FormCount} forms, {a.AnalysisCount} / {b.AnalysisCount} analyses)");
        a = b = null!;
        var res = new Dictionary<string, List<(double Ms, double Kept, double Peak, double Alloc)>>();
        for (int r = 0; r < rounds; r++)
            foreach (var (name, morph) in new[] { ("pred_index", false), ("from morph", true) })
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long m0 = GC.GetTotalMemory(true), al0 = GC.GetTotalAllocatedBytes(true);
                using var proc = Process.GetCurrentProcess();
                long ws0 = proc.WorkingSet64;
                var sw = Stopwatch.StartNew();
                var g = morph ? GrammarLexicon.LoadFromMorph(path, eng) : GrammarLexicon.Load(path, eng);
                double ms = sw.Elapsed.TotalMilliseconds;
                long al = GC.GetTotalAllocatedBytes(true) - al0;
                long m1 = GC.GetTotalMemory(true);
                proc.Refresh();
                GC.KeepAlive(g);
                if (!res.TryGetValue(name, out var l)) res[name] = l = new();
                l.Add((ms, (m1 - m0) / 1048576.0, (proc.WorkingSet64 - ws0) / 1048576.0, al / 1048576.0));
            }
        Console.WriteLine($"{"loader",-12} {"rounds",6} {"ms (each)",28} {"kept managed MB",16} {"allocated MB",13} {"working set delta MB",21}");
        foreach (var (name, l) in res)
            Console.WriteLine(FormattableString.Invariant($"{name,-12} {l.Count,6} {string.Join(" ", l.Select(x => x.Ms.ToString("0", CultureInfo.InvariantCulture))),28} {l.Average(x => x.Kept),16:0.0} {l.Average(x => x.Alloc),13:0} {string.Join(" ", l.Select(x => x.Peak.ToString("0", CultureInfo.InvariantCulture))),21}"));
    }

    internal sealed class SibStats
    {
        public readonly SortedDictionary<string, (Stat Prefix, Stat Swap, int SibTop1, long Letters)> By = new(StringComparer.Ordinal);
        public int Occurrences;
        /// <summary>10D-1 diagnosis: per class, the occurrences whose context gives a rule (non-empty Expect): count, w first
        /// of its siblings, w top-1 in swap mode.</summary>
        public readonly SortedDictionary<string, (int N, int First, int Swap1)> Ruled = new(StringComparer.Ordinal);
        public (Stat Prefix, Stat Swap, int SibTop1, long Letters) Row(string k) =>
            By.TryGetValue(k, out var r) ? r : By[k] = (new Stat(), new Stat(), 0, 0);
        public double SibTop1(string k = "all") => By.TryGetValue(k, out var r) && r.Prefix.N > 0 ? r.SibTop1 / (double)r.Prefix.N : 0;
    }

    /// <summary>P-auto on held-out text: for every target word (≥ 2 letters, known) with siblings — prefix mode: the
    /// longest common prefix of the word and its siblings is typed (at most its length − 1): is the word first of its
    /// group in the bar (sib-top1), first of the bar (top1), in the bar (hit@8)? — swap mode: its most frequent sibling is
    /// typed in full: is the word top-1 / in the bar? <paramref name="skip"/> occurrences are passed first (dev slice).</summary>
    public static SibStats RunSiblings(PredictionEngine eng, GrammarLexicon g, string path, int max, int skip, bool print, string? jsonOut = null,
                                       IReadOnlyDictionary<string, string>? phon = null)
    {
        var st = new SibStats();
        var cache = new Dictionary<string, SibInfo?>(StringComparer.Ordinal);
        var qcache = new Dictionary<string, SibInfo?>(StringComparer.Ordinal);
        int seen = 0;
        var sw = Stopwatch.StartNew();
        foreach (var line in File.ReadLines(path))
        {
            var toks = new List<string>();
            foreach (Match m in Tok.Matches(line.ToLowerInvariant().Replace('\'', ' ').Replace('’', ' ').Replace('-', ' '))) toks.Add(TokenFold.Fold(m.Value));
            for (int j = 1; j < toks.Count; j++)
            {
                var w = toks[j];
                if (w.Length < 2 || !eng.Knows(w)) continue;
                if (!cache.TryGetValue(w, out var info)) cache[w] = info = SiblingsOf(eng, g, w);
                // review 10D-0: the Q / Y / 2p row — same Lexique pronunciation, measured apart (not in « all »), over the
                // same stretch of text as the fold siblings
                if (phon != null && seen >= skip)
                {
                    if (!qcache.TryGetValue(w, out var q)) qcache[w] = q = QySiblingsOf(eng, g, w, phon);
                    if (q != null) Measure(st, q, "qy2p-phon", toks, j, w, eng);
                }
                if (info is null) continue;
                if (seen++ < skip) continue;
                var ctx = toks.GetRange(0, j);
                var s = info.Prefix == 0 ? eng.PredictNext(ctx, K) : eng.Predict(w[..info.Prefix], ctx, K);
                int r = RankOf(s, w);
                bool first = r > 0 && info.Sibs.All(x => RankOf(s, x) is 0 || RankOf(s, x) > r);
                var sw2 = eng.Predict(info.Swap, ctx, K);
                int r2 = RankOf(sw2, w);
                bool ruled = !AgreementContext.From(j >= 3 ? toks[j - 3] : null, j >= 2 ? toks[j - 2] : null, toks[j - 1],
                                                    x => g.BitsOf(eng.IndexOf(x)), eng.ParInfinitive, eng.WideDisfavour).IsEmpty;
                foreach (var k in new[] { info.Cls, "all" })
                {
                    var row = st.Row(k);
                    row.Prefix.Add(r); row.Swap.Add(r2);
                    st.By[k] = (row.Prefix, row.Swap, row.SibTop1 + (first ? 1 : 0), row.Letters + info.Prefix);
                    if (ruled)
                    {
                        (int N, int First, int Swap1) u = st.Ruled.TryGetValue(k, out var v) ? v : (0, 0, 0);
                        st.Ruled[k] = (u.N + 1, u.First + (first ? 1 : 0), u.Swap1 + (r2 == 1 ? 1 : 0));
                    }
                }
                if (++st.Occurrences >= max) goto done;
            }
        }
    done:
        if (print)
        {
            Console.WriteLine($"P-auto ({Path.GetFileName(path)}): {st.Occurrences} occurrences with sound-alike siblings (after skipping {skip}), {sw.ElapsedMilliseconds} ms\n");
            Console.WriteLine($"{"class",-8} {"n",6} {"letters",8} {"sib-top1",9} {"top1",6} {"hit@8",6} {"swap top1",10} {"swap hit@8",11}");
            foreach (var (k, r) in st.By.OrderBy(x => x.Key == "qy2p-phon" ? 2 : x.Key == "all" ? 1 : 0).ThenBy(x => x.Key, StringComparer.Ordinal))
                Console.WriteLine($"{k,-8} {r.Prefix.N,6} {(r.Letters / (double)Math.Max(1, r.Prefix.N)).ToString("0.0", CultureInfo.InvariantCulture),8} {F(r.SibTop1 / (double)Math.Max(1, r.Prefix.N)),9} {F(r.Prefix.T1),6} {F(r.Prefix.H8),6} {F(r.Swap.T1),10} {F(r.Swap.H8),11}");
            // 10D-1 diagnosis: how many occurrences have a rule in their 3-word context, and how those fare
            Console.WriteLine();
            Console.WriteLine($"{"class",-8} {"with a rule",12} {"share",6} {"sib-top1",9} {"swap top1",10}   (occurrences whose last 3 context words give a rule)");
            foreach (var (k, u) in st.Ruled.OrderBy(x => x.Key == "all" ? 1 : 0).ThenBy(x => x.Key, StringComparer.Ordinal))
                Console.WriteLine($"{k,-8} {u.N,12} {F(u.N / (double)Math.Max(1, st.By.TryGetValue(k, out var rr) ? rr.Prefix.N : 1)),6} {F(u.First / (double)Math.Max(1, u.N)),9} {F(u.Swap1 / (double)Math.Max(1, u.N)),10}");
        }
        if (jsonOut != null)
        {
            var j = new JsonObject { ["file"] = Path.GetFileName(path), ["occurrences"] = st.Occurrences, ["skip"] = skip };
            var c = new JsonObject();
            foreach (var (k, r) in st.By)
                c[k] = new JsonObject { ["prefix"] = r.Prefix.Json(), ["swap"] = r.Swap.Json(), ["sib_top1"] = Math.Round(r.SibTop1 / (double)Math.Max(1, r.Prefix.N), 4) };
            j["classes"] = c;
            File.WriteAllText(jsonOut, j.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        return st;
    }

    // ------------------------------------------------------------------ --sweep-grammar (dev only)
    internal sealed record SweepPoint(double WNominal, double WVerbal, double WNounSubj, double InjectGap, bool Par, double Bonus, bool Wide)
    {
        public override string ToString() => FormattableString.Invariant($"N={WNominal} V={WVerbal} NS={WNounSubj} gap={InjectGap} par={(Par ? 1 : 0)} bonus={Bonus} wide={(Wide ? 1 : 0)}");
        public double Size => WNominal + WVerbal + WVerbal * WNounSubj;
    }

    internal sealed record SweepResult(double Agree, double SibTop1, double SwapTop1, double[] KssDelta, double WikiSib)
    {
        public double Objective => Agree + SibTop1 + SwapTop1;
    }

    internal static void Apply(PredictionEngine eng, SweepPoint p)
    {
        eng.GrammarOn = true;
        eng.WNominal = p.WNominal; eng.WVerbal = p.WVerbal; eng.WNounSubj = p.WNounSubj; eng.InjectGap = p.InjectGap; eng.ParInfinitive = p.Par; eng.AgreeBonus = p.Bonus; eng.WideDisfavour = p.Wide;
    }

    /// <summary>
    /// Plan 11 (10D-1) sweep on the dev slices only: P-agreement dev (even ids) pooled top-1, P-auto dev (occurrences
    /// 20 001–25 000 of the first held-out set) prefix sib-top1 and swap top-1, dev KSS (words 20 001–25 000 of each set)
    /// against grammar off. Objective (plan 11, revised): P-agree + sib-top1 + swap top-1, under a dev KSS loss ≤ 0.05 pt on
    /// every set. Coordinate descent (two rounds) from the engine's defaults, then a small joint grid around the best; the
    /// pick is the smallest weights within 0.5 pt of the best objective. The test splits are never read here.
    /// </summary>
    public static void RunSweep(PredictionEngine eng, GrammarLexicon g, string casesPath, string[] heldout)
    {
        var values = new Dictionary<string, double[]>
        {
            ["WNominal"] = new[] { 0, 0.5, 1, 1.5, 2, 3 }, ["WVerbal"] = new[] { 0, 0.5, 1, 1.5, 2, 3 },
            ["WNounSubj"] = new[] { 0, 0.5, 1 }, ["InjectGap"] = new[] { 0.5, 1, 2 }, ["Par"] = new[] { 0.0, 1 }, ["Bonus"] = new[] { 0, 0.5, 1 }, ["Wide"] = new[] { 0.0, 1 },
        };
        Console.WriteLine("grid: " + string.Join("; ", values.Select(x => x.Key + " ∈ {" + string.Join(", ", x.Value.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "}")));
        var sw = Stopwatch.StartNew();
        var start = new SweepPoint(eng.WNominal, eng.WVerbal, eng.WNounSubj, eng.InjectGap, eng.ParInfinitive, eng.AgreeBonus, eng.WideDisfavour);
        eng.GrammarOn = false;
        var (agree0, n) = RunCases(eng, casesPath, "dev", null, false);
        var sib0 = heldout.Length > 0 ? RunSiblings(eng, g, heldout[0], 5000, 20000, false) : new SibStats();
        double wiki0 = heldout.Length > 1 ? RunSiblings(eng, g, heldout[1], 5000, 20000, false).SibTop1() : 0;
        var kss0 = heldout.Select(h => Kss(eng, h, 5000, 20000)).ToArray();
        double obj0 = agree0 + sib0.SibTop1() + Swap(sib0);
        Console.WriteLine(FormattableString.Invariant($"grammar off (baseline): P-agree dev {agree0:0.0000} ({n} queries), P-auto dev sib-top1 {sib0.SibTop1():0.0000}, swap top-1 {Swap(sib0):0.0000}, wiki dev sib-top1 {wiki0:0.0000} (information), KSS dev {string.Join(" / ", kss0.Select(k => (100 * k).ToString("0.000", CultureInfo.InvariantCulture)))} %; objective {obj0:0.0000}; {sw.ElapsedMilliseconds / 1000} s"));
        Console.WriteLine();
        Console.WriteLine($"{"point",-40} {"P-agree",8} {"sib-top1",9} {"swap",7} {"objective",10} " + string.Join(" ", heldout.Select(h => $"{"dKSS " + Trunc(Path.GetFileName(h), 12),18}")) + $" {"wiki sib",9}");

        var done = new Dictionary<SweepPoint, SweepResult>();
        SweepResult Eval(SweepPoint p)
        {
            if (done.TryGetValue(p, out var r)) return r;
            Apply(eng, p);
            var (a, _) = RunCases(eng, casesPath, "dev", null, false);
            var st = heldout.Length > 0 ? RunSiblings(eng, g, heldout[0], 5000, 20000, false) : new SibStats();
            double wiki = heldout.Length > 1 ? RunSiblings(eng, g, heldout[1], 5000, 20000, false).SibTop1() : 0;
            var dk = heldout.Select((h, i) => 100 * (Kss(eng, h, 5000, 20000) - kss0[i])).ToArray();
            r = done[p] = new SweepResult(a, st.SibTop1(), Swap(st), dk, wiki);
            Console.WriteLine(FormattableString.Invariant($"{p,-40} {r.Agree,8:0.0000} {r.SibTop1,9:0.0000} {r.SwapTop1,7:0.0000} {r.Objective,10:0.0000} ") +
                              string.Join(" ", r.KssDelta.Select(d => d.ToString("+0.000;-0.000", CultureInfo.InvariantCulture).PadLeft(18))) +
                              FormattableString.Invariant($" {r.WikiSib,9:0.0000}{(Feasible(r) ? "" : "  (KSS loss)")}"));
            return r;
        }
        static bool Feasible(SweepResult r) => r.KssDelta.All(d => d >= -0.05);
        double Score(SweepPoint p) { var r = Eval(p); return Feasible(r) ? r.Objective : double.NegativeInfinity; }

        var cur = start;
        Score(cur);
        for (int round = 1; round <= 2; round++)
        {
            var before = cur;
            foreach (var name in values.Keys)
                foreach (var v in values[name])
                {
                    var p = name switch
                    {
                        "WNominal" => cur with { WNominal = v }, "WVerbal" => cur with { WVerbal = v }, "WNounSubj" => cur with { WNounSubj = v },
                        "InjectGap" => cur with { InjectGap = v }, "Bonus" => cur with { Bonus = v }, "Wide" => cur with { Wide = v != 0 }, _ => cur with { Par = v != 0 },
                    };
                    if (Score(p) > Score(cur)) cur = p;
                }
            Console.WriteLine($"-- after round {round}: {cur} ({sw.ElapsedMilliseconds / 1000} s)");
            if (cur == before) break;
        }
        // a small joint grid around the best: the neighbours of WNominal, WVerbal and InjectGap together
        static double[] Near(double[] vals, double x) { int i = Array.IndexOf(vals, x); return vals.Where((_, k) => i < 0 || Math.Abs(k - i) <= 1).ToArray(); }
        foreach (var wn in Near(values["WNominal"], cur.WNominal))
            foreach (var wv in Near(values["WVerbal"], cur.WVerbal))
                foreach (var gap in Near(values["InjectGap"], cur.InjectGap))
                    Score(cur with { WNominal = wn, WVerbal = wv, InjectGap = gap });
        var feasible = done.Where(x => Feasible(x.Value)).ToList();
        if (feasible.Count == 0) { Console.WriteLine("no point meets the KSS constraint"); return; }
        var top = feasible.OrderByDescending(x => x.Value.Objective).First();
        var pick = feasible.Where(x => x.Value.Objective >= top.Value.Objective - 0.005)
                           .OrderBy(x => x.Key.Size).ThenBy(x => x.Key.Par ? 1 : 0).ThenByDescending(x => x.Key.InjectGap).First();
        Console.WriteLine();
        Console.WriteLine(FormattableString.Invariant($"{done.Count} points in {sw.ElapsedMilliseconds / 1000} s; best {top.Key} objective {top.Value.Objective:0.0000}; pick (smallest weights within 0.5 pt): {pick.Key} objective {pick.Value.Objective:0.0000} (baseline {obj0:0.0000})"));
        Apply(eng, pick.Key);
    }

    private static double Swap(SibStats st) => st.By.TryGetValue("all", out var r) ? r.Swap.T1 : 0;

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n];

    /// <summary>Keystroke savings over <paramref name="max"/> words after <paramref name="skip"/> (as the held-out run).</summary>
    internal static double Kss(PredictionEngine eng, string path, int max, int skip)
    {
        long used = 0, len = 0, words = 0, seen = 0;
        foreach (var line in File.ReadLines(path))
        {
            var toks = new List<string>();
            foreach (Match m in Tok.Matches(line.ToLowerInvariant().Replace('\'', ' ').Replace('’', ' ').Replace('-', ' '))) toks.Add(TokenFold.Fold(m.Value));
            for (int j = 1; j < toks.Count; j++)
            {
                var tgt = toks[j];
                if (tgt.Length < 2 || !eng.Knows(tgt)) continue;
                if (seen++ < skip) continue;
                var ctx = toks.GetRange(0, j);
                int u = tgt.Length;
                for (int p = 0; p <= Math.Min(tgt.Length, 8); p++)
                {
                    var s = p == 0 ? eng.PredictNext(ctx, 1) : eng.Predict(tgt[..p], ctx, 1);
                    if (s.Count > 0 && Eq(s[0].Word, tgt)) { u = p; break; }
                }
                used += u; len += tgt.Length;
                if (++words >= max) return 1.0 - used / (double)len;
            }
        }
        return len == 0 ? 0 : 1.0 - used / (double)len;
    }
}
