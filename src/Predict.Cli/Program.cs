using System.Diagnostics;
using Predict.Core;

// usage: Predict.Cli <prediction.sqlite> [--bench | --bench-user | --cold | typed ...]
//        plan 11 (10D-1): --probe [--grammar <grammar.sqlite>] [--grammar-off] "context|typed[|start]" ("context|" = next word);
//        --bench-grammar [<grammar.sqlite>]  speed with the agreement term on / off;
//        --mem-grammar [<grammar.sqlite>]    memory of the grammar lexicon load in this fresh process (run it 3 times)
//        0.10.2 (plan exact-typed-word): --exact off|present|first [--exact-margin F] for --probe and --bench;
//        --bench-exact [--grammar <g>] [--names <folder>]  speed of the rule (2 000 grammar-context queries typed as words)
//        0.10.2 (glued words): --splits / --no-splits [--w-split F] [--split-known 0|1] for --probe and --bench;
//        --bench-split [--grammar <g>] [--names <folder>]  speed of the split pass (off / on) and of RankCorrections
//        0.11.0 (plan dys-sound-tolerance): --sound-pairs none|chj|chj,fv,kg,td,pb|all5 [--w-tolerant F] [--tolerant-from N]
//        [--tolerant-check-from N] for --probe and --bench;
//        --bench-sound [--grammar <g>] [--names <folder>] [--dys <dys-sound folder>]  speed with the pairs none / chj / all five
//        0.11.0 (plan offensive-words): --exclusions <fr.txt>|none (any mode; default: the list next to the database;
//        --grammar expands it as the app does); --mem-exclusions [--grammar <g>]  load time and memory of the list;
//        the exhaustive probe is Predict.Eval --probe-exclusions
Console.OutputEncoding = System.Text.Encoding.UTF8;
var db = args.Length > 0 ? args[0] : "Data.Build/out/prediction.sqlite";
var sw = Stopwatch.StartNew();
using var engine = PredictionEngine.Load(db);
sw.Stop();
Console.WriteLine($"loaded {engine.Count} words in {sw.ElapsedMilliseconds} ms; context={engine.HasContext}");
// 0.11.0 (plan offensive-words A4): the « mots exclus » — the engine's own (next to the database), « --exclusions none »
// (A/B, benches) or another file; counts only, never a word
if (Array.IndexOf(args, "--exclusions") is var xi && xi >= 0 && xi + 1 < args.Length)
    engine.Excluded = args[xi + 1] == "none" ? ExcludedWords.Empty : ExcludedWords.FromFile(args[xi + 1], engine);
Console.WriteLine((engine.Excluded.IsEmpty ? "excluded: none" : FormattableString.Invariant($"excluded: {engine.Excluded.Describe()}, {engine.ExclusionsLoadMs:0.0} ms")) + "\n");
string OptAfter(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : def; }
const string DefaultGrammar = "Data.Build/out/grammar.sqlite";
static ExactTypedMode ParseExact(string s) => s switch
{
    "off" => ExactTypedMode.Off, "present" => ExactTypedMode.Present, "first" => ExactTypedMode.First,
    _ => throw new ArgumentException("--exact off|present|first: " + s),
};
// 0.10.2: the exact typed word rule for --probe and --bench (default: the engine's)
engine.ExactTyped = ParseExact(OptAfter("--exact", engine.ExactTyped.ToString().ToLowerInvariant()));
engine.ExactMargin = double.Parse(OptAfter("--exact-margin", engine.ExactMargin.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                                  System.Globalization.CultureInfo.InvariantCulture);
if (args.Contains("--exact") || args.Contains("--exact-margin"))
    Console.WriteLine(FormattableString.Invariant($"exact typed word: {engine.ExactTyped}, margin {engine.ExactMargin}, bar {engine.ExactBar}\n"));
// 0.10.2 (glued words): --splits / --no-splits, --w-split F, --split-known 0|1 for --probe and --bench
if (args.Contains("--splits")) engine.Splits = true;
if (args.Contains("--no-splits")) engine.Splits = false;
engine.WSplit = double.Parse(OptAfter("--w-split", engine.WSplit.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture);
if (OptAfter("--split-known", "") is { Length: > 0 } sk) engine.SplitKnown = sk != "0";
engine.SplitMinCount = int.Parse(OptAfter("--split-min", engine.SplitMinCount.ToString()));
if (args.Contains("--splits") || args.Contains("--no-splits") || args.Contains("--w-split") || args.Contains("--split-known"))
    Console.WriteLine(FormattableString.Invariant($"glued words: splits {(engine.Splits ? "on" : "off")}, WSplit {engine.WSplit}, SplitMax {engine.SplitMax}, from {engine.SplitFrom}, known {engine.SplitKnown}\n"));

// 0.11.0 (plan dys-sound-tolerance): the pupil's confused sounds for --probe and --bench (default: the engine's)
if (OptAfter("--sound-pairs", "") is { Length: > 0 } sp) engine.Tolerance = SoundPairsInfo.Parse(sp);
engine.WTolerant = double.Parse(OptAfter("--w-tolerant", engine.WTolerant.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.CultureInfo.InvariantCulture);
engine.TolerantFrom = int.Parse(OptAfter("--tolerant-from", engine.TolerantFrom.ToString()));
engine.TolerantCheckFrom = int.Parse(OptAfter("--tolerant-check-from", engine.TolerantCheckFrom.ToString()));
if (args.Contains("--sound-pairs") || args.Contains("--w-tolerant") || args.Contains("--tolerant-from") || args.Contains("--tolerant-check-from"))
    Console.WriteLine(FormattableString.Invariant($"sound pairs: {SoundPairsInfo.Describe(engine.Tolerance)}, WTolerant {engine.WTolerant}, from {engine.TolerantFrom}, check from {engine.TolerantCheckFrom}, max swaps {engine.TolerantMaxSwaps}\n"));

if (args.Contains("--mem-exclusions"))
{
    // 0.11.0: the time and managed memory of the list (the file, then the grammar expansion), 5 rounds; counts only
    var path = PredictionEngine.ExclusionsNextTo(db) ?? throw new FileNotFoundException("no exclusions next to the database");
    var gpath = OptAfter("--grammar", DefaultGrammar);
    for (int r = 1; r <= 5; r++)
    {
        long m0 = GC.GetTotalMemory(true);
        var t0 = Stopwatch.GetTimestamp();
        var x = ExcludedWords.FromFile(path, engine);
        double ms1 = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        t0 = Stopwatch.GetTimestamp();
        var y = x.WithGrammar(gpath, engine);
        double ms2 = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        long m1 = GC.GetTotalMemory(true);
        Console.WriteLine(FormattableString.Invariant($"round {r}: file {ms1:0.0} ms ({x.Count} forms), grammar expansion {ms2:0.0} ms (+{y.GrammarCount}); managed memory +{(m1 - m0) / 1024.0:0} KB"));
        GC.KeepAlive(y);
    }
    return;
}

if (args.Contains("--bench-sound"))
{
    // 0.11.0: the speed of the tolerant pass. 2 000 queries: two thirds the dys set's misspellings (dys-sound-cases.json, the
    // « -full » cases with their context) typed letter by letter (2 letters to full), one third the --bench inputs in « il
    // fait »; 200 warm-up queries, 3 rounds of the pairs none / chj / all five at k = 11 (the keyboard's 8 + 3 spares), with
    // the grammar (and the names pack with --names). Then Check over the dys spelling words (200 rounds per setting).
    engine.Grammar = LoadGrammar(OptAfter("--grammar", DefaultGrammar), engine);
    if (OptAfter("--names", "") is { Length: > 0 } np) engine.Names = LoadNames(np, engine);
    var dir = OptAfter("--dys", "Predict.Eval/dys-sound");
    var full = new List<(string T, string[] C)>();
    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "dys-sound-cases.json"))))
        foreach (var c in doc.RootElement.EnumerateArray())
            if (c.GetProperty("category").GetString()!.EndsWith("-full", StringComparison.Ordinal) &&
                c.GetProperty("context").ValueKind == System.Text.Json.JsonValueKind.String)
                full.Add((c.GetProperty("typed").GetString()!, c.GetProperty("context").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    var spellWords = new List<string>();
    using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "dys-sound-spelling.json"))))
        foreach (var c in doc.RootElement.EnumerateArray())
            if (SpellingIndicator.LastWord(c.GetProperty("text").GetString()!)?.Word is { } lw) spellWords.Add(lw);
    var common = new[] { "maiz", "oriz", "bo", "sou", "enfan", "ordi", "ékri", "boku", "tele", "kestion" };
    var rnd = new Random(1);
    var qs = new List<(string Q, string[] C)>();
    for (int i = 0; i < 2000; i++)
    {
        if (i % 3 == 2) { qs.Add((common[rnd.Next(common.Length)], new[] { "il", "fait" })); continue; }
        var (w, c) = full[rnd.Next(full.Count)];
        qs.Add((w[..Math.Min(w.Length, rnd.Next(2, w.Length + 1))], c));
    }
    var keepTol = engine.Tolerance;
    var settings = new[] { SoundPairs.None, SoundPairs.ChJ, SoundPairsInfo.All };
    for (int round = 1; round <= 3; round++)
        foreach (var pairs in settings)
        {
            engine.Tolerance = pairs;
            for (int i = 0; i < 200; i++) engine.Predict(qs[i].Q, qs[i].C, 11);
            var times = new double[qs.Count];
            for (int i = 0; i < qs.Count; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                engine.Predict(qs[i].Q, qs[i].C, 11);
                times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            Array.Sort(times);
            Console.WriteLine(FormattableString.Invariant(
                $"round {round} pairs {SoundPairsInfo.Describe(pairs),-22}: 2000 queries: mean {times.Average():0.000} ms, p50 {times[1000]:0.000}, p95 {times[1899]:0.000}, p99 {times[1979]:0.000}, max {times[^1]:0.000} ms"));
        }
    foreach (var pairs in settings)
    {
        engine.Tolerance = pairs;
        foreach (var x in spellWords) engine.Check(x);
        var ct = new List<double>(spellWords.Count * 200);
        for (int r = 0; r < 200; r++)
            foreach (var x in spellWords)
            {
                var t0 = Stopwatch.GetTimestamp();
                engine.Check(x);
                ct.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
        ct.Sort();
        Console.WriteLine(FormattableString.Invariant(
            $"Check pairs {SoundPairsInfo.Describe(pairs),-22}: {spellWords.Count} dys spelling words × 200: mean {ct.Average() * 1000:0.0} µs, p99 {ct[(int)(ct.Count * 0.99)] * 1000:0.0} µs, max {ct[^1]:0.000} ms"));
    }
    engine.Tolerance = keepTol;
    return;
}

if (args.Contains("--bench-exact"))
{
    // 0.10.2: the speed of the exact typed word rule. 2 000 queries in grammar contexts whose typed letters are words (the
    // rule applies: « mai » < maison, « par » < partir, homophones et / est, mange / mangent; one in ten at a sentence start
    // with a capital), 200 warm-up queries, 3 rounds of off / present / first at k = 11 (the keyboard's 8 + 3 spares), with
    // the grammar (and the names pack with --names)
    engine.Grammar = LoadGrammar(OptAfter("--grammar", DefaultGrammar), engine);
    if (OptAfter("--names", "") is { Length: > 0 } np) engine.Names = LoadNames(np, engine);
    string[][] ctxs = { new[] { "les" }, new[] { "ils" }, new[] { "elle", "est" }, new[] { "il", "a" }, new[] { "pour" }, new[] { "je", "vais" },
                        new[] { "le", "chat" }, new[] { "la" }, new[] { "il" }, new[] { "je" }, new[] { "c'est" } };
    var words = new[] { "mai", "par", "bon", "ver", "pas", "et", "est", "son", "sont", "mange", "mangent", "la", "le", "ma", "mais", "en",
                        "faim", "fin", "ba", "pi", "port", "maison", "chat", "petit", "petite", "tout", "tous", "mer", "mère", "sa", "ça",
                        "ou", "où", "on", "ont", "ce", "se", "ces", "ses", "porte", "joue", "jouer", "fait", "fais", "va", "vas", "di", "dit" };
    foreach (var w in words) if (!engine.KnowsForMark(w, false)) Console.WriteLine($"note: « {w} » is not ✓: the rule does not apply to it");
    var rnd = new Random(1);
    var qs = new List<(string Q, string[] C, bool Start)>();
    for (int i = 0; i < 2000; i++)
    {
        var w = words[rnd.Next(words.Length)];
        bool start = rnd.Next(10) == 0;
        qs.Add((start ? char.ToUpperInvariant(w[0]) + w[1..] : w, start ? Array.Empty<string>() : ctxs[rnd.Next(ctxs.Length)], start));
    }
    for (int round = 1; round <= 3; round++)
        foreach (var m in new[] { ExactTypedMode.Off, ExactTypedMode.Present, ExactTypedMode.First })
        {
            engine.ExactTyped = m;
            for (int i = 0; i < 200; i++) engine.Predict(qs[i].Q, qs[i].C, 11, qs[i].Start);
            var times = new double[qs.Count];
            for (int i = 0; i < qs.Count; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                engine.Predict(qs[i].Q, qs[i].C, 11, qs[i].Start);
                times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            Array.Sort(times);
            Console.WriteLine(FormattableString.Invariant(
                $"round {round} exact {m,-7}: 2000 queries: mean {times.Average():0.000} ms, p50 {times[1000]:0.000}, p95 {times[1899]:0.000}, p99 {times[1979]:0.000}, max {times[^1]:0.000} ms"));
        }
    return;
}

if (args.Contains("--bench-split"))
{
    // 0.10.2 (glued words): the speed of the split pass. 2 000 queries: the glued words of the plan typed letter by letter
    // (2 letters to full) in their contexts, a third common inputs in « il fait »; 200 warm-up queries, 3 rounds of splits
    // off / on at k = 11 (the keyboard's 8 + 3 spares), with the grammar (and the names pack with --names). Then the ≈ list
    // ordering (RankCorrections) of the same glued words, warm.
    engine.Grammar = LoadGrammar(OptAfter("--grammar", DefaultGrammar), engine);
    if (OptAfter("--names", "") is { Length: > 0 } np) engine.Names = LoadNames(np, engine);
    (string W, string[] C)[] glued =
    {
        ("tème", new[] { "je" }), ("jai", Array.Empty<string>()), ("lécole", new[] { "à" }), ("cétait", new[] { "mais" }),
        ("cest", new[] { "et" }), ("quil", new[] { "je", "pense" }), ("daccord", new[] { "oui" }), ("jusqua", new[] { "je", "marche" }),
        ("ma", new[] { "il" }), ("sil", new[] { "ne", "sais", "pas" }), ("mappelle", new[] { "je" }), ("larbre", new[] { "dans" }),
        ("nest", new[] { "il" }), ("lami", new[] { "avec" }), ("lhistoire", new[] { "c'est" }), ("lecole", new[] { "la", "de" }),
    };
    var common = new[] { "maiz", "oriz", "bo", "sou", "enfan", "ordi", "ékri", "boku", "tele", "kestion" };
    var rnd = new Random(1);
    var qs = new List<(string Q, string[] C, bool Start)>();
    for (int i = 0; i < 2000; i++)
    {
        if (i % 3 == 2) { qs.Add((common[rnd.Next(common.Length)], new[] { "il", "fait" }, false)); continue; }
        var (w, c) = glued[rnd.Next(glued.Length)];
        qs.Add((w[..Math.Min(w.Length, rnd.Next(2, w.Length + 1))], c, c.Length == 0));
    }
    bool keep = engine.Splits;
    for (int round = 1; round <= 3; round++)
        foreach (var on in new[] { false, true })
        {
            engine.Splits = on;
            for (int i = 0; i < 200; i++) engine.Predict(qs[i].Q, qs[i].C, 11, qs[i].Start);
            var times = new double[qs.Count];
            for (int i = 0; i < qs.Count; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                engine.Predict(qs[i].Q, qs[i].C, 11, qs[i].Start);
                times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            Array.Sort(times);
            Console.WriteLine(FormattableString.Invariant(
                $"round {round} splits {(on ? "on " : "off")}: 2000 queries: mean {times.Average():0.000} ms, p50 {times[1000]:0.000}, p95 {times[1899]:0.000}, p99 {times[1979]:0.000}, max {times[^1]:0.000} ms"));
        }
    engine.Splits = true;
    var lists = glued.Select(g => (L: engine.Check(g.W).Candidates, g.C, g.W)).Where(x => x.L.Count > 1).ToList();
    foreach (var (l, c, w) in lists) engine.RankCorrections(l, c, w);   // warm
    var rt = new List<double>();
    for (int r = 0; r < 50; r++)
        foreach (var (l, c, w) in lists)
        {
            var t0 = Stopwatch.GetTimestamp();
            engine.RankCorrections(l, c, w);
            rt.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }
    rt.Sort();
    if (rt.Count > 0)
        Console.WriteLine(FormattableString.Invariant($"RankCorrections ({lists.Count} glued ≈ lists × 50, warm): mean {rt.Average():0.000} ms, p99 {rt[(int)(rt.Count * 0.99)]:0.000}, max {rt[^1]:0.000} ms"));
    engine.Splits = keep;
    return;
}

if (args.Contains("--mem-grammar"))
{
    // plan 11: the app's way alone in a fresh process — engine loaded and queried once, then GrammarLexicon.Load; managed /
    // GC committed / private / working set after a compacting GC, before and after
    var path = OptAfter("--mem-grammar", DefaultGrammar);
    (long Gc, long Com, long Priv, long Ws) Snap()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var p = Process.GetCurrentProcess();
        return (GC.GetTotalMemory(false), GC.GetGCMemoryInfo().TotalCommittedBytes, p.PrivateMemorySize64, p.WorkingSet64);
    }
    engine.Predict("m", new[] { "les" }, 8);   // the engine's own first-query state, not the grammar's
    var a = Snap();
    var lt = Stopwatch.StartNew();
    var g = LoadGrammar(path, engine);
    lt.Stop();
    var b = Snap();
    engine.Grammar = g;
    engine.Predict("m", new[] { "les" }, 8);
    var c = Snap();
    Console.WriteLine(FormattableString.Invariant(
        $"grammar load ({g.FormCount} forms, {g.LemmaCount} lemmas, {g.AnalysisCount} analyses) in {lt.ElapsedMilliseconds} ms: managed {(b.Gc - a.Gc) / 1048576.0:+0.0} MB, GC committed {(b.Com - a.Com) / 1048576.0:+0.0} MB, private {(b.Priv - a.Priv) / 1048576.0:+0.0} MB, working set {(b.Ws - a.Ws) / 1048576.0:+0.0} MB (total {b.Ws / 1048576.0:0} MB); after a grammar query: working set {(c.Ws - a.Ws) / 1048576.0:+0.0} MB; file {new FileInfo(path).Length / 1048576.0:0.0} MB"));
    GC.KeepAlive(g);
    return;
}

if (args.Contains("--bench-grammar"))
{
    // plan 11: the speed gate of the agreement term. Query sets run with the grammar off and on, alternating, 3 rounds, 200
    // warm-up queries each: the --bench inputs (context « il fait », no rule applies), 2 000 queries in grammar contexts
    // (« les », « ils », « elle est », « il a », « pour », « je vais », « le chat ») with prefixes of 1–6 letters of words
    // that agree or not, and 2 000 next-word queries in those contexts
    var path = OptAfter("--bench-grammar", DefaultGrammar);
    var g = LoadGrammar(path, engine);
    engine.InjectSources = int.Parse(OptAfter("--inject-sources", engine.InjectSources.ToString()));
    if (args.Contains("--no-lift")) engine.InjectPerSource = 0;
    string[][] ctxs = { new[] { "les" }, new[] { "ils" }, new[] { "elle", "est" }, new[] { "il", "a" }, new[] { "pour" }, new[] { "je", "vais" }, new[] { "le", "chat" } };
    var words = new[] { "maisons", "mangent", "allée", "mangé", "manger", "journaux", "petites", "enfants", "sont", "est", "chats",
                        "regardent", "fatiguée", "trouvé", "partir", "jouer", "blanches", "dorment", "contente", "arrivés", "et", "son", "a" };
    var bench = new[] { "maiz", "oriz", "bo", "sou", "enfan", "ordi", "ékri", "boku", "tele", "kestion" };
    var rnd = new Random(1);
    var setA = Enumerable.Range(0, 2000).Select(i => (Q: bench[i % bench.Length], C: new[] { "il", "fait" })).ToList();
    var setB = new List<(string Q, string[] C)>();
    for (int i = 0; i < 2000; i++) { var w = words[rnd.Next(words.Length)]; setB.Add((w[..Math.Min(w.Length, rnd.Next(1, 7))], ctxs[rnd.Next(ctxs.Length)])); }
    var setN = Enumerable.Range(0, 2000).Select(i => ctxs[i % ctxs.Length]).ToList();
    foreach (var (name, n) in new[] { ("bench (il fait)", 0), ("grammar contexts", 1), ("next word", 2) })
        for (int round = 1; round <= 3; round++)
            foreach (var on in new[] { false, true })
            {
                engine.Grammar = on ? g : GrammarLexicon.Empty;
                void Run(int i)
                {
                    if (n == 0) engine.Predict(setA[i].Q, setA[i].C, 8);
                    else if (n == 1) engine.Predict(setB[i].Q, setB[i].C, 8);
                    else engine.PredictNext(setN[i], 8);
                }
                for (int i = 0; i < 200; i++) Run(i);
                var times = new double[2000];
                for (int i = 0; i < 2000; i++)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    Run(i);
                    times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                }
                Array.Sort(times);
                Console.WriteLine(FormattableString.Invariant(
                    $"{name,-17} round {round} grammar {(on ? "on " : "off")}: 2000 queries: mean {times.Average():0.000} ms, p50 {times[1000]:0.000}, p95 {times[1899]:0.000}, max {times[^1]:0.000} ms"));
            }
    return;
}

if (args.Contains("--cold"))
{
    // first-call latency for very frequent context words (worst case: tens of thousands of successors)
    foreach (var w in new[] { "la", "de", "le", "et", "je", "il", "que", "un" })
    {
        var t = Stopwatch.StartNew();
        var r = engine.PredictNext(new[] { w }, 8);
        Console.WriteLine($"cold PredictNext([{w}]) {t.Elapsed.TotalMilliseconds,7:0.0} ms -> {string.Join(", ", r.Take(4).Select(x => x.Word))}");
    }
    return;
}

if (args.Contains("--bench"))
{
    string[] ctx = { "il", "fait" };
    var inputs = new[] { "maiz", "oriz", "bo", "sou", "enfan", "ordi", "ékri", "boku", "tele", "kestion" };
    // warm up
    foreach (var t in inputs) engine.Predict(t, ctx, 8);
    int N = 20000;
    var w = Stopwatch.StartNew();
    for (int i = 0; i < N; i++) engine.Predict(inputs[i % inputs.Length], ctx, 8);
    w.Stop();
    Console.WriteLine($"{N} predictions in {w.ElapsedMilliseconds} ms = {w.Elapsed.TotalMilliseconds / N:0.000} ms/prediction");
    Console.WriteLine($"working set: {Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)} MB; DB on disk: {new FileInfo(db).Length / (1024 * 1024)} MB");
    return;
}

if (args.Contains("--bench-user"))
{
    // plan 10A: the speed gate with personal words. ONE query set (2 000 prefixes of 1–7 letters: half from the 15 000-word
    // list, half common inputs) run with 0, 1 000 and 15 000 invented words (half with a capital, 1 in 20 hyphenated)
    // + 500 spelling pairs, so the three rows compare the same work; 200 warm-up queries first, 3 rounds, context « il fait »
    string[] ctx = { "il", "fait" };
    string[] syl = { "ba", "lo", "mi", "ré", "su", "ta", "vo", "ni", "cha", "pou", "lin", "dé", "gor", "fé", "zan", "qui", "bru", "tal", "on", "ex" };
    var common = new[] { "maiz", "oriz", "bo", "sou", "enfan", "ordi", "ékri", "boku", "tele", "kestion", "m", "pa", "vé" };
    var rnd = new Random(1);
    string Word() { var s = string.Concat(Enumerable.Range(0, rnd.Next(2, 5)).Select(_ => syl[rnd.Next(syl.Length)])); return rnd.Next(2) == 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s; }
    var all = new List<UserWord>();
    for (int i = 0; i < 15000; i++) all.Add(new UserWord(i % 20 == 0 ? Word() + "-" + Word() : Word()));
    var fixes = Enumerable.Range(0, 500).Select(i => new SpellingFix(Word().ToLowerInvariant() + "x", Word())).ToList();
    var queries = new List<string>();
    for (int i = 0; i < 2000; i++)
    {
        if (i % 2 == 0) { var w0 = all[rnd.Next(all.Count)].Form; queries.Add(w0[..Math.Min(w0.Length, rnd.Next(1, 8))]); }
        else queries.Add(common[rnd.Next(common.Length)]);
    }
    for (int round = 1; round <= 3; round++)
        foreach (var n in new[] { 0, 1000, 15000 })
        {
            var words = all.Take(n).ToList();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long mem0 = GC.GetTotalMemory(true);
            var bt = Stopwatch.StartNew();
            var lex = UserLexicon.Build(words, n == 0 ? null : fixes);
            bt.Stop();
            long mem1 = GC.GetTotalMemory(true);
            engine.User = lex;
            foreach (var q in queries.Take(200)) engine.Predict(q, ctx, 8);   // warm up
            var times = new double[queries.Count];
            for (int i = 0; i < queries.Count; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                engine.Predict(queries[i], ctx, 8);
                times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            Array.Sort(times);
            Console.WriteLine(FormattableString.Invariant(
                $"round {round} user words {n,6} ({lex.Count} forms, {lex.FixCount} pairs): build {bt.Elapsed.TotalMilliseconds:0.0} ms, snapshot +{(mem1 - mem0) / 1048576.0:0.0} MB; ") + FormattableString.Invariant(
                $"2000 queries: mean {times.Average():0.000} ms, p50 {times[1000]:0.000}, p95 {times[1899]:0.000}, max {times[^1]:0.000} ms"));
            engine.User = UserLexicon.Empty;
        }
    return;
}

if (args.Contains("--mem-names-stream"))
{
    // plan 10C review: the app's way alone in a fresh process — engine loaded, top-300 list computed, then the streaming
    // load of the pack; managed / GC committed / private / working set after a compacting GC, before and after
    int i0 = Array.IndexOf(args, "--mem-names-stream");
    var pack = i0 + 1 < args.Length ? args[i0 + 1] : "Data.Build/out/noms-pack/noms";
    (long Gc, long Com, long Priv, long Ws) Snap()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var p = Process.GetCurrentProcess();
        return (GC.GetTotalMemory(false), GC.GetGCMemoryInfo().TotalCommittedBytes, p.PrivateMemorySize64, p.WorkingSet64);
    }
    _ = engine.MostFrequent300;
    var a = Snap();
    var lex = LoadStreaming(pack, engine);
    var b = Snap();
    engine.Names = lex;
    Console.WriteLine(FormattableString.Invariant(
        $"streaming load ({lex.Count} forms): managed {(b.Gc - a.Gc) / 1048576.0:+0.0} MB, GC committed {(b.Com - a.Com) / 1048576.0:+0.0} MB, private {(b.Priv - a.Priv) / 1048576.0:+0.0} MB, working set {(b.Ws - a.Ws) / 1048576.0:+0.0} MB (total {b.Ws / 1048576.0:0} MB)"));
    GC.KeepAlive(lex);
    return;
}

if (args.Contains("--mem-names"))
{
    // plan 10C review: where the pack's memory goes, in a fresh process. Managed heap (after a full compacting GC) and the
    // working set at each step: the engine's own top-300 list, the parsed file (records alive), the layer built, the
    // records dropped (what the app keeps).
    int i0 = Array.IndexOf(args, "--mem-names");
    var pack = i0 + 1 < args.Length ? args[i0 + 1] : "Data.Build/out/noms-pack/noms";
    (long Gc, long Ws, long Priv, long Com) Snap()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var p = Process.GetCurrentProcess();
        return (GC.GetTotalMemory(false), p.WorkingSet64, p.PrivateMemorySize64, GC.GetGCMemoryInfo().TotalCommittedBytes);
    }
    string D((long Gc, long Ws, long Priv, long Com) a, (long Gc, long Ws, long Priv, long Com) b) => FormattableString.Invariant(
        $"managed {(b.Gc - a.Gc) / 1048576.0:+0.0;-0.0} MB, GC committed {(b.Com - a.Com) / 1048576.0:+0.0;-0.0} MB, private {(b.Priv - a.Priv) / 1048576.0:+0.0;-0.0} MB, working set {(b.Ws - a.Ws) / 1048576.0:+0.0;-0.0} MB");
    var s0 = Snap();
    _ = engine.MostFrequent300;
    var s1 = Snap();
    var file = Directory.Exists(pack) ? Path.Combine(pack, "noms.json") : pack;
    // the old way: every record in a list, then the layer (kept for comparison)
    var box = new NamesDocument?[1];
    int names = ParseInto(file, box);
    var s2 = Snap();
    var old = BuildFrom(box, engine);
    var s3 = Snap();
    box[0] = null; old = null!;
    var s4 = Snap();
    // the app's way since the review: a streaming parse straight into the layer (NamesLexicon.Load), no record list
    var lex = LoadStreaming(pack, engine);
    var s5 = Snap();
    engine.Names = lex;
    Console.WriteLine($"engine top-300 list: {D(s0, s1)}");
    Console.WriteLine($"parsed file ({names} records alive): {D(s1, s2)}");
    Console.WriteLine($"layer built ({lex.Count} forms): {D(s2, s3)}");
    Console.WriteLine($"records and old layer dropped: {D(s3, s4)}");
    Console.WriteLine($"old way, kept (layer only, vs before the pack): {D(s1, s4)}");
    Console.WriteLine($"streaming load (the app's way), kept: {D(s4, s5)}");
    Console.WriteLine($"both steps in this process vs before the pack: {D(s1, s5)}; total working set {s5.Ws / 1048576.0:0} MB");
    GC.KeepAlive(lex);
    return;
}

if (args.Contains("--bench-names"))
{
    // plan 10C: the speed with and without the « Noms propres » pack. ONE query set (2 000 queries: 1–4 letters, a third
    // starting with a capital inside a sentence — the widest names pass —, the rest common inputs), 200 warm-up queries,
    // 3 rounds alternating pack off / on; the working set after loading the pack. usage: --bench-names <noms.json | folder>
    int i0 = Array.IndexOf(args, "--bench-names");
    var pack = i0 + 1 < args.Length ? args[i0 + 1] : "Data.Build/out/noms-pack/noms";
    string[] ctx = { "il", "voit" };
    var common = new[] { "maiz", "oriz", "bo", "sou", "enfan", "ordi", "ékri", "boku", "tele", "kestion", "ma", "pa", "vé", "be", "em", "lu" };
    var caps = new[] { "M", "Ma", "Mar", "Be", "Bel", "E", "Em", "Lu", "Pa", "Ca", "Ju", "Na", "Lo", "Br", "Yp" };
    var rnd = new Random(1);
    var queries = new List<(string Q, bool Start)>();
    for (int i = 0; i < 2000; i++)
        queries.Add(i % 3 == 0 ? (caps[rnd.Next(caps.Length)], false) : (common[rnd.Next(common.Length)], rnd.Next(4) == 0));
    _ = engine.MostFrequent300;   // the engine's own one-time list (the app computes it for lessons anyway)
    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    long ws0 = Process.GetCurrentProcess().WorkingSet64, gc0 = GC.GetTotalMemory(true);
    var lt = Stopwatch.StartNew();
    var names = LoadNames(pack, engine);   // a method of its own: the parsed file is unreachable once it returns
    lt.Stop();
    // as the app does once the pack is built (NamesPack): one compacting collection, so the parse buffers leave the working set
    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect(2, GCCollectionMode.Forced, true, true);
    long ws1 = Process.GetCurrentProcess().WorkingSet64, gc1 = GC.GetTotalMemory(true);
    Console.WriteLine(FormattableString.Invariant($"names pack: {names.NameCount} names, {names.Count} forms, built in {lt.ElapsedMilliseconds} ms; working set {ws0 / 1048576.0:0} -> {ws1 / 1048576.0:0} MB (+{(ws1 - ws0) / 1048576.0:0.0} MB), managed heap +{(gc1 - gc0) / 1048576.0:0.0} MB"));
    for (int round = 1; round <= 3; round++)
        foreach (var on in new[] { false, true })
        {
            engine.Names = on ? names : NamesLexicon.Empty;
            foreach (var (q, st) in queries.Take(200)) engine.Predict(q, ctx, 8, st);
            var times = new double[queries.Count];
            for (int i = 0; i < queries.Count; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                engine.Predict(queries[i].Q, ctx, 8, queries[i].Start);
                times[i] = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }
            Array.Sort(times);
            Console.WriteLine(FormattableString.Invariant(
                $"round {round} pack {(on ? "on " : "off")}: 2000 queries: mean {times.Average():0.000} ms, p50 {times[1000]:0.000}, p95 {times[1899]:0.000}, max {times[^1]:0.000} ms"));
        }
    return;
}

if (args.Contains("--probe"))
{
    // 0.8.3: usage --probe [--names <folder>] "context|typed[|start]" ...  → the bar's words and the spelling check of typed
    int ni = Array.IndexOf(args, "--names");
    if (ni >= 0 && ni + 1 < args.Length) engine.Names = LoadNames(args[ni + 1], engine);
    int gi = Array.IndexOf(args, "--grammar");
    if (gi >= 0 && gi + 1 < args.Length) engine.Grammar = LoadGrammar(args[gi + 1], engine);
    if (args.Contains("--grammar-off")) engine.GrammarOn = false;
    bool scores = args.Contains("--scores");
    // 0.10.2: the context-free estimate of each elided form (per million); « kept » = Lexique has an apostrophe row
    var et = Stopwatch.StartNew();
    var estimates = engine.GetElisionEstimates();
    et.Stop();
    Console.WriteLine(FormattableString.Invariant($"elided forms (ElisionUnigram={engine.ElisionUnigram}; all {estimates.Count} estimated again in {et.Elapsed.TotalMilliseconds:0.0} ms): ") +
        string.Join(", ", estimates.Select(x => FormattableString.Invariant($"{x.Bare}' {x.Estimate:0} ({(x.Calibrated ? "Lexique bare row" : "Lexique, kept")} {x.Lexique:0.##})"))));
    // 0.10.2 (glued words): the bare forms' successors kept at load (words taking an elision / all successors' count)
    Console.WriteLine("bare successors (glued words): " + string.Join(", ", engine.GetBareSuccessors().Select(x =>
        FormattableString.Invariant($"{x.Bare}' {x.Successors}/{x.Total} ({x.PerMillion:0}/M)"))));
    foreach (var a in args.Skip(1).Where((a, i) => a.Contains('|')))
    {
        var parts = a.Split('|');
        var ctx = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool start = parts.Length > 2 && parts[2] == "start";
        var expect = engine.Grammar.IsEmpty ? "" : $"   expect={engine.ExpectOf(ctx)}";
        if (parts[1].Length == 0)
        {
            // plan 11: an empty typed word = the next-word bar
            Console.WriteLine($"[{parts[0]}] (next)     -> {string.Join(", ", engine.PredictNext(ctx, 8).Select(x => x.Word))}{expect}");
            continue;
        }
        var s = engine.Predict(parts[1], ctx, 8, start);
        var c = engine.Check(parts[1]);
        // ~ = sounds like (Phonetic), * = correction (Edit)
        Console.WriteLine($"[{parts[0]}] {parts[1],-10}{(start ? " (start)" : "")} -> {string.Join(", ", s.Select(x => x.Word + (x.Kind == MatchKind.Prefix ? "" : x.Kind == MatchKind.Phonetic ? "~" : "*") + (scores ? "(" + x.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ")" : "")))}   check={c.Status} {string.Join("/", c.Candidates)}{(c.Tolerant > 0 ? $" (tolerant tier {c.Tolerant})" : "")}{expect}");
    }
    return;
}

var tests = args.Length > 1 ? args[1..] : new[] { "orizon", "maizon", "famme", "oto", "boku" };
foreach (var t in tests)
{
    var s = engine.Predict(t, null, 8);
    Console.WriteLine($"{t,-12} -> {string.Join("  ", s.Select(x => $"{x.Word}[{(x.Kind == MatchKind.Prefix ? "p" : "ph")}]"))}");
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static GrammarLexicon LoadGrammar(string path, PredictionEngine eng)
{
    var g = GrammarLexicon.Load(path, eng);
    var x0 = eng.Excluded;
    eng.Excluded = x0.WithGrammar(path, eng);   // 0.11.0: as the app once its grammar is in (plan offensive-words A1)
    if (!ReferenceEquals(x0, eng.Excluded)) Console.WriteLine($"excluded: +{eng.Excluded.GrammarCount} grammar forms ({eng.Excluded.Count} in all)");
    return g;
}

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static NamesLexicon LoadNames(string path, PredictionEngine eng) => NamesLexicon.Load(path, eng).Lexicon;

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static int ParseInto(string file, NamesDocument?[] box) { box[0] = NamesFile.ParseJson(File.ReadAllBytes(file)); return box[0]!.Noms.Count; }

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static NamesLexicon BuildFrom(NamesDocument?[] box, PredictionEngine eng) =>
    NamesLexicon.Build(box[0]!.Noms, eng.Knows, eng.MostFrequent300, 5.0, box[0]!.Edition);

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static NamesLexicon LoadStreaming(string path, PredictionEngine eng) => NamesLexicon.Load(path, eng).Lexicon;
