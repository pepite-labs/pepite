using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Predict.Core;

// Predictor benchmark tool. Two modes (auto-detected by the second argument):
//   cases   : curated cases (cases.json)
//   heldout : large-scale next-word / completion / keystroke-savings on held-out text  (.txt)
//
// usage: Predict.Eval <prediction.sqlite> <cases.json | sentences.txt> [--max N] [--prefix P]
//        [--no4gram] [--elision-boost F]   (F overrides PredictionEngine.ElisionBoost; 1 = off)
//        plan 10A, personal words:
//        [--user <mots.json>]   load a word-list fixture (the app's mots.json format) as the personal words
//        [--keep-case]          held-out: type the words with their capitals; a word after . ! ? (and, 0.8.3, : « " —) is a
//                               sentence start [--old-starts: . ! ? only]
//        [--oov-typed]          held-out: words unknown to the engine count as fully typed instead of being skipped,
//                               so KSS is over the same words with and without a list
//        [--w-user F] [--w-spelling F] [--user-unigram F] [--user-from N]   override the personal-word weights
//        --sweep-user [--cases cases-personal.json --user fixture.json] [--neutral a.txt,b.txt --neutral-max N]
//        Predict.Eval <db> <lesson-dir> --lesson-set [--sweep-user [--cases cases-personal.json]]
//                               lesson-domain set: per topic, <t>.lesson.txt -> LessonExtractor -> list,
//                               KSS (--oov-typed) on <t>.test.txt without vs with the list
//        plan 10B, spelling indicator:
//        Predict.Eval <db> spelling-cases.json --spelling [--user <mots.json>]
//                               each case's text is typed from the start of a document; the mark of its last word
//                               (SpellingIndicator + engine.Check) is compared with the expected one, per category
//        [--dump <file>]        held-out: the letters typed per word, one line each (to diff two builds)
//        plan 10C, names pack (any mode):
//        [--names <noms.json | folder>] [--name-cap F] [--names-from N]   load the « Noms propres » pack as the names layer
//        plan 11 (10D-0), grammar (any mode):
//        [--grammar <grammar.sqlite>]   load the morphology table (Data.Build/build_grammar.py) for the sets below
//        [--grammar-from-morph]         review 10D-0: build the entry index at load from morph instead of reading pred_index
//        [--no-grammar-rank]            keep engine.Grammar = Empty (the 0.8.3 baseline)
//        plan 11 (10D-1), the agreement term (with --grammar):
//        [--w-nominal F] [--w-verbal F] [--w-noun-subj F] [--inject-gap F] [--agree-bonus F] [--inject-sources N] [--inject-per-source N]
//        [--par-inf 0|1] [--wide 0|1] [--grammar-off]   override the weights; --grammar-off = GrammarOn false (all 0 = 0.8.3 byte for byte)
//        Predict.Eval <db> <grammar-cases.json | g-*.jsonl> --grammar-predict [--split dev|test|all] [--json <out>]
//                               cases: top-1 / hit@8 / MRR of `intended` per category × p (0…3 letters typed; p0 = next
//                               word) and `wrong` typed in full (swap), per domain, pooled; jsonl: each labelled error typed
//                               in full after the typed sentence before it — is `gold` top-1 / in the 8 words, per class
//        Predict.Eval <db> <heldout.txt> --siblings --grammar <g> [--max N] [--skip N] [--json <out>]
//                               P-auto: words with sound-alike siblings (same lemma, same fold, or a homophone family);
//                               prefix mode (their common prefix typed: sib-top1, top1, hit@8) and swap mode (the most
//                               frequent sibling typed in full), per class; plus a qy2p-phon row (same Lexique
//                               pronunciation, Q / Y / 2p: mangé / manger / mangez), measured apart, not in « all »
//        Predict.Eval <db> <any> --grammar <g> --grammar-load-compare [--rounds N]
//                               the shipped pred_index load against an index built at load from morph (time, memory, equality)
//        Predict.Eval <db> <grammar-cases.json> --sweep-grammar --grammar <g> --heldout <subs.txt,wiki.txt>
//                               the 10D-1 sweep on the dev slices: coordinate descent + a small joint grid, objective
//                               P-agree dev + P-auto dev sib-top1 + swap top-1 under a dev KSS loss <= 0.05 pt

Console.OutputEncoding = Encoding.UTF8;
var db = args.Length > 0 ? args[0] : "Data.Build/out/prediction.sqlite";
var target = args.Length > 1 ? args[1] : "Predict.Eval/cases.json";
int Arg(string name, int def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : def; }

using var engine = PredictionEngine.Load(db);
Console.WriteLine(engine.UnlistedCount > 0 ? $"spelling reference: {engine.UnlistedCount} unlisted" : "spelling reference: absent");
if (args.Contains("--no4gram")) engine.UseFourgrams = false;
{ int bi = Array.IndexOf(args, "--elision-boost"); if (bi >= 0 && bi + 1 < args.Length) engine.ElisionBoost = double.Parse(args[bi + 1], System.Globalization.CultureInfo.InvariantCulture); }
double ArgD(string name, double def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture) : def; }
string? ArgS(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
engine.WUser = ArgD("--w-user", engine.WUser);
engine.WSpelling = ArgD("--w-spelling", engine.WSpelling);
engine.UserUnigram = ArgD("--user-unigram", engine.UserUnigram);
engine.UserBonusFrom = Arg("--user-from", engine.UserBonusFrom);
engine.NameLowercaseShare = ArgD("--name-lower", engine.NameLowercaseShare);
engine.NameCapitalLift = ArgD("--name-capital", engine.NameCapitalLift);
engine.NameCapitalBonus = ArgD("--name-capital-bonus", engine.NameCapitalBonus);
bool oovTyped = args.Contains("--oov-typed");
bool oldStarts = args.Contains("--old-starts");   // held-out --keep-case: sentence starts after . ! ? … only (0.8.2)
bool keepCase = args.Contains("--keep-case");   // held-out: type each word as written (capitals), sentence starts known
double lastStart = 0;   // sentence-start words (capital typed) kept top-1, from the last PersonalScores
if (ArgS("--user") is { } userPath)
{
    var sw0 = Stopwatch.StartNew();
    engine.User = LoadUser(userPath);
    Console.WriteLine(FormattableString.Invariant($"personal words: {engine.User.Count} forms + {engine.User.FixCount} spelling pairs from {Path.GetFileName(userPath)} (built in {sw0.Elapsed.TotalMilliseconds:0.0} ms); WUser={engine.WUser} WSpelling={engine.WSpelling} UserUnigram={engine.UserUnigram}"));
}
if (ArgS("--names") is { } namesPath)
{
    // plan 10C: the « Noms propres » pack (noms.json or its folder) as the engine's names layer
    long m0 = GC.GetTotalMemory(true);
    var sw0 = Stopwatch.StartNew();
    _ = engine.MostFrequent300;   // computed by the engine anyway: not the pack's memory
    m0 = GC.GetTotalMemory(true);
    // in a method of its own: the parsed document dies with its frame (the app keeps only the lexicon and the picture map)
    var (nl, title, edition) = LoadNames(namesPath, engine, ArgD("--name-cap", 5.0));
    engine.Names = nl;
    double ms = sw0.Elapsed.TotalMilliseconds;
    long m1 = GC.GetTotalMemory(true);
    if (args.Contains("--names-twice"))
    {
        // a second, warm load: the first one also pays for one-time statics (JSON, regex, code); this delta is the layer
        long w0 = GC.GetTotalMemory(true);
        var again = LoadNames(namesPath, engine, ArgD("--name-cap", 5.0));
        long w1 = GC.GetTotalMemory(true);
        Console.WriteLine(FormattableString.Invariant($"names layer alone (second load): +{(w1 - w0) / 1048576.0:0.0} MB ({again.Item1.Count} forms)"));
        GC.KeepAlive(again.Item1);
    }
    engine.NamesFrom = Arg("--names-from", engine.NamesFrom);
    Console.WriteLine(FormattableString.Invariant($"names pack: « {title} » edition {edition}: {nl.NameCount} names -> {nl.Count} forms, loaded in {ms:0} ms, managed memory +{(m1 - m0) / 1048576.0:0.0} MB"));
}
GrammarLexicon? grammar = null;
if (ArgS("--grammar") is { } grammarPath)
{
    // plan 11: the morphology table; engine.Grammar stays Empty with --no-grammar-rank (in 10D-0 nothing scores with it)
    grammar = PredictEval.GrammarEval.Load(grammarPath, engine, args.Contains("--grammar-from-morph"));
    if (!args.Contains("--no-grammar-rank")) engine.Grammar = grammar;
    // plan 11 (10D-1): the agreement weights (defaults = the engine's, chosen by --sweep-grammar)
    engine.WNominal = ArgD("--w-nominal", engine.WNominal);
    engine.WVerbal = ArgD("--w-verbal", engine.WVerbal);
    engine.WNounSubj = ArgD("--w-noun-subj", engine.WNounSubj);
    engine.InjectGap = ArgD("--inject-gap", engine.InjectGap);
    engine.AgreeBonus = ArgD("--agree-bonus", engine.AgreeBonus);
    engine.InjectSources = Arg("--inject-sources", engine.InjectSources);
    engine.InjectPerSource = Arg("--inject-per-source", engine.InjectPerSource);
    if (ArgS("--par-inf") is { } par) engine.ParInfinitive = par != "0";
    if (ArgS("--wide") is { } wide) engine.WideDisfavour = wide != "0";
    if (args.Contains("--grammar-off")) engine.GrammarOn = false;
    Console.WriteLine(args.Contains("--no-grammar-rank") ? "grammar rank: off (--no-grammar-rank, the 0.8.3 baseline)"
        : FormattableString.Invariant($"grammar rank: {(engine.GrammarOn ? "on" : "off (--grammar-off)")}; WNominal={engine.WNominal} WVerbal={engine.WVerbal} WNounSubj={engine.WNounSubj} InjectGap={engine.InjectGap} AgreeBonus={engine.AgreeBonus} wide={(engine.WideDisfavour ? 1 : 0)} sources={engine.InjectSources}x{engine.InjectPerSource} par={(engine.ParInfinitive ? 1 : 0)}"));
}
Console.WriteLine($"loaded {engine.Count} words; context={engine.HasContext}; 4-grams={engine.UseFourgrams}; elision boost={engine.ElisionBoost}\n");

GrammarLexicon NeedGrammar() => grammar ?? throw new ArgumentException("this mode needs --grammar <grammar.sqlite>");
if (args.Contains("--grammar-predict"))
{
    if (target.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) PredictEval.GrammarEval.RunJsonl(engine, target, ArgS("--json"));
    else PredictEval.GrammarEval.RunCases(engine, target, ArgS("--split") ?? "all", ArgS("--json"));
}
else if (args.Contains("--siblings"))
    PredictEval.GrammarEval.RunSiblings(engine, NeedGrammar(), target, Arg("--max", 20000), Arg("--skip", 0), true, ArgS("--json"),
                                        PredictEval.GrammarEval.ReadPhon(db));
else if (args.Contains("--grammar-load-compare")) PredictEval.GrammarEval.CompareLoads(ArgS("--grammar")!, engine, Arg("--rounds", 3));
else if (args.Contains("--sweep-grammar"))
    PredictEval.GrammarEval.RunSweep(engine, NeedGrammar(), target, (ArgS("--heldout") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
else if (args.Contains("--spelling")) RunSpelling(engine, target);
else if (args.Contains("--lesson-set"))
{
    if (args.Contains("--sweep-user")) RunSweepUser(engine, target, ArgS("--cases"));
    else RunLessonSet(engine, target, true);
}
else if (target.EndsWith(".txt"))
{
    if (args.Contains("--sweep")) RunSweep(engine, target, Arg("--max", 15000), Arg("--n", 6000));
    else RunHeldout(engine, target, Arg("--max", 20000));
}
else RunCases(engine, target);

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static (NamesLexicon, string, string) LoadNames(string path, PredictionEngine eng, double cap)
{
    var (nl, nd) = NamesLexicon.Load(path, eng, cap);
    return (nl, nd.Titre, nd.Edition ?? "?");
}

// ------------------------------------------------------------------ plan 10A: personal words
// a word-list fixture in the app's mots.json format: { listes: [ { active, mots: [ { w } ] } ], fautes: [ { ecrit, mot } ] }
static UserLexicon LoadUser(string path)
{
    var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("not a JSON object: " + path);
    var words = new List<UserWord>();
    foreach (var l in root["listes"] as JsonArray ?? new JsonArray())
    {
        if (l is not JsonObject lo || (lo["active"] is JsonValue av && av.TryGetValue<bool>(out var on) && !on)) continue;
        foreach (var m in lo["mots"] as JsonArray ?? new JsonArray())
            if (m?["w"] is JsonValue wv && wv.TryGetValue<string>(out var w)) words.Add(new UserWord(w));
    }
    var fixes = new List<SpellingFix>();
    foreach (var f in root["fautes"] as JsonArray ?? new JsonArray())
        if (f?["ecrit"] is JsonValue ev && ev.TryGetValue<string>(out var e) && f["mot"] is JsonValue mv && mv.TryGetValue<string>(out var m))
            fixes.Add(new SpellingFix(e, m));
    return UserLexicon.Build(words, fixes);
}

// the lesson-domain set (built by Data.Build/build_lesson_set.py into <work>): per topic, the lesson half gives the list
// (LessonExtractor, ticked words only), KSS --oov-typed on the test half without vs with that list
(long baseUsed, long withUsed, long len) RunLessonSet(PredictionEngine eng, string dir, bool print, Func<int, bool>? topicFilter = null)
{
    var topics = Directory.GetFiles(dir, "*.lesson.txt").OrderBy(f => f, StringComparer.Ordinal).ToList();
    long bU = 0, wU = 0, L = 0; int listWords = 0, used = 0;
    var keep = eng.User;
    if (print) Console.WriteLine($"{"topic",-22} {"list",5} {"words",6} {"KSS without",12} {"KSS with",9} {"gain",7}");
    for (int t = 0; t < topics.Count; t++)
    {
        if (topicFilter != null && !topicFilter(t)) continue;
        var name = Path.GetFileName(topics[t])[..^".lesson.txt".Length];
        var test = Path.Combine(dir, name + ".test.txt");
        if (!File.Exists(test)) continue;
        eng.User = UserLexicon.Empty;
        var list = LessonExtractor.Extract(File.ReadAllText(topics[t]), w => eng.Knows(w), eng.MostFrequent300)
                                  .Where(x => x.Ticked).Select(x => new UserWord(x.Form)).ToList();
        var (u0, l0, n0) = KssCount(eng, test, int.MaxValue, true);
        eng.User = UserLexicon.Build(list);
        var (u1, _, _) = KssCount(eng, test, int.MaxValue, true);
        bU += u0; wU += u1; L += l0; listWords += list.Count; used++;
        if (print) Console.WriteLine(FormattableString.Invariant($"{name,-22} {list.Count,5} {n0,6} {100.0 * (1 - u0 / (double)l0),11:0.0}% {100.0 * (1 - u1 / (double)l0),8:0.0}% {100.0 * (u0 - u1) / l0,7:+0.0;-0.0}"));
    }
    eng.User = keep;
    if (print && L > 0)
        Console.WriteLine(FormattableString.Invariant($"\nlesson-domain set ({used} topics, {listWords} list words, --oov-typed): KSS {100.0 * (1 - bU / (double)L):0.00}% without -> {100.0 * (1 - wU / (double)L):0.00}% with the lesson lists (gain {100.0 * (bU - wU) / L:+0.00;-0.00} pts; typed {bU} -> {wU} of {L} letters)"));
    return (bU, wU, L);
}

// --sweep-user: WUser x UserBonusFrom x UserUnigram: lesson-set gain on the even topics (dev) and the odd ones (test),
// the curated personal cases (top-1 within 2 letters for names / lesson words, top-1 for the spelling pairs) with the
// fixture, and the neutrality loss (KSS with the fixture vs without) on the first --neutral-max words of each --neutral set
void RunSweepUser(PredictionEngine eng, string dir, string? casesPath)
{
    double[] WU = (ArgS("--grid") ?? "1e-4,3e-4,1e-3,3e-3").Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray(), UU = { 1e-6 };
    int[] FROM = (ArgS("--grid-from") ?? "1").Split(',').Select(int.Parse).ToArray();
    var cases = casesPath is null ? null : JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(casesPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    var fixture = ArgS("--user") is { } up ? LoadUser(up) : null;
    var neutral = (ArgS("--neutral") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
    int nmax = Arg("--neutral-max", 3000);
    var keep = eng.User;
    var baseNeutral = new List<(long u, long l)>();
    eng.User = UserLexicon.Empty;
    foreach (var t in neutral) { var (u, l, _) = KssCount(eng, t, nmax, false); baseNeutral.Add((u, l)); }
    Console.WriteLine($"{"WUser",6} {"from",5} {"UserUni",8} {"dev gain",9} {"test gain",10} {"top1<=2",8} {"fautes",7} {"start",6} " + string.Join(" ", neutral.Select(n => $"{"loss " + Path.GetFileName(n)[..Math.Min(12, Path.GetFileName(n).Length)],18}")));
    foreach (var wu in WU) foreach (var fr in FROM) foreach (var uu in UU)
    {
        eng.WUser = wu; eng.UserUnigram = uu; eng.UserBonusFrom = fr;
        eng.User = UserLexicon.Empty;
        var d = RunLessonSet(eng, dir, false, t => t % 2 == 0);
        var te = RunLessonSet(eng, dir, false, t => t % 2 == 1);
        string c = "", nl = "";
        if (fixture != null)
        {
            eng.User = fixture;
            if (cases != null) { var (w2, fa) = PersonalScores(eng, cases); c = FormattableString.Invariant($"{w2,8:0.000} {fa,7:0.000} {lastStart,6:0.000}"); }
            for (int i = 0; i < neutral.Length; i++)
            {
                var (u, l, _) = KssCount(eng, neutral[i], nmax, false);
                nl += FormattableString.Invariant($" {100.0 * (u - baseNeutral[i].u) / baseNeutral[i].l,17:+0.00;-0.00}");
            }
        }
        Console.WriteLine(FormattableString.Invariant($"{wu,6} {fr,5} {uu,8:0e0} {100.0 * (d.baseUsed - d.withUsed) / Math.Max(1, d.len),9:+0.00;-0.00} {100.0 * (te.baseUsed - te.withUsed) / Math.Max(1, te.len),10:+0.00;-0.00} {c}{nl}"));
    }
    eng.User = keep;
}

// personal cases: (top-1 within 2 letters over the names / lesson words, top-1 over the spelling pairs)
(double within2, double fautes) PersonalScores(PredictionEngine eng, List<Case> cases)
{
    var hit = new Dictionary<string, bool>(StringComparer.Ordinal);
    int fa = 0, fn = 0, st = 0, sn = 0;
    foreach (var k in cases)
    {
        var ctxArr = k.Context is null ? null : new[] { k.Context };
        var sugg = Elision.Apply(string.IsNullOrEmpty(k.Typed) ? eng.PredictNext(ctxArr, 8) : eng.Predict(k.Typed, ctxArr, 8, AtStart(k.Context)));
        bool top1 = RankOfCase(sugg, k.Intended) == 1;
        if (k.Category.StartsWith("fautes", StringComparison.Ordinal)) { fn++; if (top1) fa++; }
        else if (k.Category.StartsWith("start", StringComparison.Ordinal)) { sn++; if (top1) st++; }
        else if (k.Typed.Length <= 2) hit[k.Intended] = (hit.TryGetValue(k.Intended, out var h) && h) || top1;
    }
    lastStart = sn == 0 ? 0 : st / (double)sn;
    return (hit.Count == 0 ? 0 : hit.Values.Count(v => v) / (double)hit.Count, fn == 0 ? 0 : fa / (double)fn);
}

// ------------------------------------------------------------------ weight sweep (optimise held-out KSS)
void RunSweep(PredictionEngine eng, string path, int testWords, int devWords)
{
    (double wl, double wp, double we, double al) def = (eng.WLm, eng.WPrefix, eng.WEdit, eng.Alpha);
    void Set(double wp, double we, double al) { eng.WLm = 1.0; eng.WPrefix = wp; eng.WEdit = we; eng.Alpha = al; }

    Set(def.wp, def.we, def.al);
    double baseKss = Kss(eng, path, testWords, 0);
    Console.WriteLine($"baseline weights (WPrefix={def.wp} WEdit={def.we} Alpha={def.al}): test KSS = {baseKss * 100:0.00}% ({testWords} words)\n");

    double[] WP = { 1, 1.5, 2, 3, 4 }, WE = { 1, 1.5, 2.5 }, AL = { 0.2, 0.4, 0.6 };
    double bestProxy = -1; (double wp, double we, double al) best = (def.wp, def.we, def.al);
    Console.WriteLine("sweeping on a DISJOINT dev split (proxy = top-1 at 1+2 letters)…");
    foreach (var wp in WP) foreach (var we in WE) foreach (var al in AL)
    {
        Set(wp, we, al);
        double proxy = Proxy(eng, path, devWords, testWords);   // dev = words AFTER the test slice
        if (proxy > bestProxy) { bestProxy = proxy; best = (wp, we, al); Console.WriteLine($"  WPrefix={wp,-4} WEdit={we,-4} Alpha={al,-4} -> dev proxy {proxy * 100:0.00}%  *"); }
    }

    Set(best.wp, best.we, best.al);
    double tunedKss = Kss(eng, path, testWords, 0);
    Console.WriteLine($"\nbest weights: WPrefix={best.wp} WEdit={best.we} Alpha={best.al}");
    Console.WriteLine($"test KSS: baseline {baseKss * 100:0.00}%  ->  tuned {tunedKss * 100:0.00}%   (delta {(tunedKss - baseKss) * 100:+0.00;-0.00}%)");
}

// keystroke savings with --oov-typed (every word of 2+ letters counts; one the engine cannot propose costs all its
// letters): (typed letters, total letters, words)
(long used, long len, long words) KssCount(PredictionEngine eng, string path, int max, bool oov)
{
    var tok = new Regex("[a-zà-ÿœ]+", RegexOptions.Compiled);
    long used = 0, len = 0, words = 0;
    foreach (var line in File.ReadLines(path))
    {
        var toks = Tokens(tok, line);
        for (int j = 1; j < toks.Count; j++)
        {
            var tgt = toks[j];
            if (tgt.Length < 2) continue;
            if (!eng.KnowsAny(tgt))
            {
                if (!oov) continue;
                used += tgt.Length; len += tgt.Length;
                if (++words >= max) return (used, len, words);
                continue;
            }
            var ctx = toks.GetRange(0, j);
            int u = tgt.Length;
            for (int p = 0; p <= Math.Min(tgt.Length, 8); p++)
            {
                var s = p == 0 ? eng.PredictNext(ctx, 1) : eng.Predict(tgt.Substring(0, p), ctx, 1);
                if (s.Count > 0 && Eq(s[0].Word, tgt)) { u = p; break; }
            }
            used += u; len += tgt.Length;
            if (++words >= max) return (used, len, words);
        }
    }
    return (used, len, words);
}

// keystroke savings over `max` words, skipping the first `skip` eligible words
double Kss(PredictionEngine eng, string path, int max, int skip)
{
    var tok = new Regex("[a-zà-ÿœ]+", RegexOptions.Compiled);
    long used = 0, len = 0, words = 0, seen = 0;
    foreach (var line in File.ReadLines(path))
    {
        var toks = Tokens(tok, line);
        for (int j = 1; j < toks.Count; j++)
        {
            var tgt = toks[j];
            if (tgt.Length < 2 || !eng.Knows(tgt)) continue;
            if (seen++ < skip) continue;
            var ctx = toks.GetRange(0, j);
            int u = tgt.Length;
            for (int p = 0; p <= Math.Min(tgt.Length, 8); p++)
            {
                var s = p == 0 ? eng.PredictNext(ctx, 1) : eng.Predict(tgt.Substring(0, p), ctx, 1);
                if (s.Count > 0 && Eq(s[0].Word, tgt)) { u = p; break; }
            }
            used += u; len += tgt.Length;
            if (++words >= max) return 1.0 - used / (double)len;
        }
    }
    return len == 0 ? 0 : 1.0 - used / (double)len;
}

// fast tuning proxy: top-1 accuracy at 1 and 2 typed letters, over `n` words after `skip`
double Proxy(PredictionEngine eng, string path, int n, int skip)
{
    var tok = new Regex("[a-zà-ÿœ]+", RegexOptions.Compiled);
    long hit = 0, tot = 0, seen = 0, words = 0;
    foreach (var line in File.ReadLines(path))
    {
        var toks = Tokens(tok, line);
        for (int j = 1; j < toks.Count; j++)
        {
            var tgt = toks[j];
            if (tgt.Length < 2 || !eng.Knows(tgt)) continue;
            if (seen++ < skip) continue;
            var ctx = toks.GetRange(0, j);
            foreach (var p in new[] { 1, 2 })
            {
                if (tgt.Length < p) continue;
                var s = eng.Predict(tgt.Substring(0, p), ctx, 1);
                tot++; if (s.Count > 0 && Eq(s[0].Word, tgt)) hit++;
            }
            if (++words >= n) return hit / (double)tot;
        }
    }
    return tot == 0 ? 0 : hit / (double)tot;
}

static List<string> Tokens(Regex tok, string line)
{
    var o = new List<string>();
    foreach (Match m in tok.Matches(line.ToLowerInvariant().Replace('\'', ' ').Replace('’', ' ').Replace('-', ' '))) o.Add(TokenFold.Fold(m.Value));   // « œ » fold per token (corpus-light 0.1)
    return o;
}

// ------------------------------------------------------------------ curated cases
void RunCases(PredictionEngine eng, string casesPath)
{
    const int K = 8, DEEP = 25;
    var cases = JsonSerializer.Deserialize<List<Case>>(File.ReadAllText(casesPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    Console.WriteLine($"{cases.Count} curated cases\n");
    var agg = new Dictionary<string, Stat>();
    foreach (var c in cases)
    {
        // empty "typed" = next-word prediction; the list is shown as the bar shows it (elisions with their apostrophe)
        var ctxArr = c.Context is null ? null : new[] { c.Context };
        var sugg = Elision.Apply(string.IsNullOrEmpty(c.Typed) ? eng.PredictNext(ctxArr, DEEP) : eng.Predict(c.Typed, ctxArr, DEEP, AtStart(c.Context)));
        int rank = RankOfCase(sugg, c.Intended);
        Bump(agg, c.Category, rank, K); Bump(agg, "overall", rank, K);
        var mark = rank == 1 ? "✓1" : rank is > 0 and <= K ? $"✓{rank}" : "✗ ";
        var shown = string.IsNullOrEmpty(c.Typed) ? $"[{c.Context}] ∅" : c.Typed;
        Console.WriteLine($"  {mark} {shown,-11} → {c.Intended,-12} | {Trunc(string.Join(", ", sugg.Take(K).Select(x => x.Word)), 64)}");
    }
    Console.WriteLine($"\n=== Quality by category (hit@{K}) ===\n{"category",-14} {"cases",5} {"top1",7} {"hit@" + K,7} {"MRR",7}");
    foreach (var k in agg.Keys.Where(k => k != "overall").OrderBy(k => k).Append("overall"))
    {
        var s = agg[k];
        Console.WriteLine($"{k,-14} {s.N,5} {s.Top1 / (double)s.N,7:0.00} {s.Hit / (double)s.N,7:0.00} {s.Mrr / s.N,7:0.00}"
            + (k == "overall" ? "   => " + Grade(agg["overall"]) : ""));
    }
    if (cases.Any(c => c.Category.StartsWith("fautes", StringComparison.Ordinal)))
    {
        var (w2, fa) = PersonalScores(eng, cases);
        Console.WriteLine(FormattableString.Invariant($"\npersonal words: names / lesson words top-1 within 2 letters {w2:0.000}; spelling pairs (« Mes fautes ») top-1 {fa:0.000}; sentence-start words top-1 {lastStart:0.000}"));
    }
}

// ------------------------------------------------------------------ large held-out benchmark
void RunHeldout(PredictionEngine eng, string path, int maxWords)
{
    var tok = new Regex("[a-zà-ÿœ]+", RegexOptions.Compiled);
    int[] P = { 0, 1, 2, 3 };               // letters typed before predicting (0 = pure next word)
    var n = new long[4]; var t1 = new long[4]; var t3 = new long[4]; var h8 = new long[4]; var mrr = new double[4];
    long tot = 0, cov = 0, words = 0, ksUsed = 0, ksLen = 0;
    long elWords = 0, elUsed = 0, elLen = 0, elTop1 = 0;   // the subset right after an elision (l' d' qu'…), D22
    var sw = Stopwatch.StartNew();
    // --dump <file>: one line per evaluated word (index, last 3 context words, the word, letters typed): diff two builds
    using var dump = ArgS("--dump") is { } dp ? new StreamWriter(dp, false, new UTF8Encoding(false)) : null;
    foreach (var line in File.ReadLines(path))
    {
        var toks = new List<string>();
        var typedToks = new List<string>();   // --keep-case: the word as written (its capital), else lowercase
        var starts = new List<bool>();        // --keep-case: the word follows . ! ? … in the line
        var lowered = line.ToLowerInvariant().Replace('\'', ' ').Replace('’', ' ').Replace('-', ' ');
        bool cased = keepCase && lowered.Length == line.Length;
        int prevEnd = 0;
        foreach (Match m in tok.Matches(lowered))
        {
            // « œ » / « æ » folded per token after the match (corpus-light 0.1), never on the line (it keeps its length for
            // --keep-case); the word typed with its capital is folded too (« Œuvre » → « Oeuvre »), as the lexicon spells it
            toks.Add(TokenFold.Fold(m.Value));
            typedToks.Add(TokenFold.Fold(cased ? line.Substring(m.Index, m.Length) : m.Value));
            // 0.8.3: as the keyboard's tracker, a capital after « : « " — » says nothing about a name either (--old-starts: . ! ? … only)
            var gap = line.Substring(prevEnd, m.Index - prevEnd);
            starts.Add(keepCase && (toks.Count == 1 || gap.IndexOfAny(new[] { '.', '!', '?', '…' }) >= 0 || !oldStarts && CapitalContext.OpensIn(gap)));
            prevEnd = m.Index + m.Length;
        }
        for (int j = 1; j < toks.Count; j++)
        {
            var tgt = toks[j];
            var typedT = typedToks[j];
            bool atStart = starts[j];
            if (tgt.Length < 2) continue;
            tot++;
            // plan 10C: with --keep-case a capitalised word the names pack knows (« Paris », « Emma ») is predictable too
            if (oovTyped && !eng.KnowsAny(tgt) && !(cased && eng.KnowsAny(typedT)))
            {
                // --oov-typed: a word the engine cannot propose costs all its letters (same words with and without a list)
                foreach (var p in P) if (p <= tgt.Length) n[p]++;
                ksUsed += tgt.Length; ksLen += tgt.Length;
                if (++words >= maxWords) goto done;
                continue;
            }
            if (!oovTyped && !eng.Knows(tgt)) continue;      // OOV words are unpredictable; measure over the lexicon
            cov++;
            var ctx = toks.GetRange(0, j);
            foreach (var p in P)
            {
                if (p > tgt.Length) continue;
                var sugg = p == 0 ? eng.PredictNext(ctx, 8) : eng.Predict(typedT.Substring(0, p), ctx, 8, atStart);
                int r = RankOf(sugg, tgt);
                n[p]++; if (r == 1) t1[p]++; if (r is > 0 and <= 3) t3[p]++; if (r is > 0 and <= 8) h8[p]++; if (r > 0) mrr[p] += 1.0 / r;
            }
            int used = tgt.Length;              // keystroke savings: first prefix length that makes it top-1
            for (int p = 0; p <= Math.Min(tgt.Length, 8); p++)
            {
                var sugg = p == 0 ? eng.PredictNext(ctx, 1) : eng.Predict(typedT.Substring(0, p), ctx, 1, atStart);
                if (sugg.Count > 0 && Eq(sugg[0].Word, tgt)) { used = p; break; }
            }
            ksUsed += used; ksLen += tgt.Length;
            dump?.WriteLine($"{words}	{string.Join(" ", ctx.Skip(Math.Max(0, ctx.Count - 3)))}	{typedT}	{used}");
            if (Elision.IsBare(ctx[^1])) { elWords++; elUsed += used; elLen += tgt.Length; if (used <= 1) elTop1++; }
            if (++words >= maxWords) goto done;
        }
    }
done:
    sw.Stop();
    Console.WriteLine($"held-out words evaluated: {words}  (coverage: in-lexicon {cov}/{tot} = {100.0 * cov / tot:0.0}%)   {sw.ElapsedMilliseconds} ms\n");
    Console.WriteLine($"{"prefix",-8} {"top1",7} {"top3",7} {"hit@8",7} {"MRR",7}   (given the previous words + the first <prefix> letters)");
    foreach (var p in P)
        if (n[p] > 0)
            Console.WriteLine($"{(p == 0 ? "next" : p + " letter"),-8} {t1[p] / (double)n[p],7:0.000} {t3[p] / (double)n[p],7:0.000} {h8[p] / (double)n[p],7:0.000} {mrr[p] / n[p],7:0.000}");
    Console.WriteLine($"\nkeystroke savings (KSS): {100.0 * (1.0 - ksUsed / (double)ksLen):0.0}%   " +
                      $"(typed {ksUsed} of {ksLen} letters to reach top-1)");
    if (elLen > 0)
        Console.WriteLine($"  after an elision: {elWords} words, KSS {100.0 * (1.0 - elUsed / (double)elLen):0.0}% " +
                          $"(typed {elUsed} of {elLen}), top-1 within 1 letter {elTop1 / (double)elWords:0.000}");
}

// ------------------------------------------------------------------ plan 10B: spelling indicator
// spelling-cases.json: [ { cat, text, expect: known|sounds|unknown|none|notred|notknown, target? } ]. Gates (plan 10B): no correct
// lexicon word red; SoundsLike recall on the phonetic misspellings >= 0.8; numbers, acronyms, names, elisions, hyphenated
// words and punctuation never red wrongly. The typed word is never printed by the app; here the cases are ours.
void RunSpelling(PredictionEngine eng, string path)
{
    var cases = JsonSerializer.Deserialize<List<SpellCase>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    Console.WriteLine($"{cases.Count} spelling cases\n");
    var byCat = new SortedDictionary<string, int[]>(StringComparer.Ordinal);   // per category: n, known, sounds, unknown, none, as expected
    int phon = 0, phonKnown = 0, phonSounds = 0, phonFirst = 0, phonAmong = 0, badRed = 0, inner = 0, innerRed = 0;
    var misses = new List<string>();
    var innerMarks = new int[4];
    int names = 0, namesKnown = 0, namesSounds = 0, starts = 0, startSounds = 0, startRed = 0;
    foreach (var c in cases)
    {
        // every finished word along the text is judged as the keyboard shows it while typing (« www. » before « ecole »…);
        // the words before the last are correct by construction: any red there is a wrong red
        var (states, w) = SpellingIndicator.Scan(c.Text);
        int nInner = w is { After: not '\0' } ? states.Count - 1 : states.Count;
        for (int s = 0; s < nInner; s++)
        {
            var im = SpellingIndicator.Finished(states[s], x => eng.Check(x));
            inner++; innerMarks[(int)im]++;
            if (im == SpellingMark.Unknown) { innerRed++; misses.Add($"  (inner) red on « {states[s].Word} » after '{states[s].After}' in « {c.Text.TrimEnd()} »"); }
        }
        SpellingMark mark = SpellingMark.None;
        SpellingCheck chk = SpellingCheck.Unknown;
        if (w is { } fw)
        {
            if (fw.After == '\0') mark = SpellingIndicator.Typing(fw, x => eng.KnowsForMark(x, fw.SentenceStart));   // 0.8.3: as the keyboard
            else mark = SpellingIndicator.Finished(fw, x => chk = eng.Check(x));
        }
        bool ok = c.Expect switch
        {
            "known" => mark == SpellingMark.Known,
            "sounds" => mark == SpellingMark.SoundsLike,
            "unknown" => mark == SpellingMark.Unknown,
            "none" => mark == SpellingMark.None,
            "notknown" => mark != SpellingMark.Known,   // 0.8.3: a lexicon form the spelling reference does not have (« yeah »)
            _ => mark != SpellingMark.Unknown,   // notred
        };
        if (!byCat.TryGetValue(c.Cat, out var a)) byCat[c.Cat] = a = new int[6];
        int col = mark switch { SpellingMark.Known => 1, SpellingMark.SoundsLike => 2, SpellingMark.Unknown => 3, _ => 4 };
        a[0]++; a[col]++; if (ok) a[5]++;
        // a wrong red: a case that must never be red (a correct word, number, acronym, name, elision, hyphen, punctuation);
        // a misspelling shown red instead of ≈ is a recall miss, not a wrong red
        if (c.Expect is "known" or "none" or "notred" && mark == SpellingMark.Unknown) badRed++;
        // plan 10C: names (with the pack: ✓, never ≈ nor ?) and misspellings at a sentence start (« Maizon » ≈ maison)
        if (c.Cat is "name" or "name-pack") { names++; if (mark == SpellingMark.Known) namesKnown++; if (mark == SpellingMark.SoundsLike) namesSounds++; }
        if (c.Cat == "start-fault") { starts++; if (mark == SpellingMark.SoundsLike) startSounds++; if (mark == SpellingMark.Unknown) startRed++; }
        if (c.Cat == "phonetic")
        {
            phon++;
            if (mark == SpellingMark.Known) phonKnown++;
            if (mark == SpellingMark.SoundsLike)
            {
                phonSounds++;
                if (c.Target != null && string.Equals(chk.Candidate, c.Target, StringComparison.Ordinal)) phonFirst++;
                if (c.Target != null && chk.Candidates.Contains(c.Target, StringComparer.Ordinal)) phonAmong++;
            }
        }
        // a name shown ≈ (only at a sentence start) is not wrong, but worth seeing
        if (ok && c.Cat == "name" && mark != SpellingMark.None)
            misses.Add($"  (info) {c.Cat,-6} {mark,-10} « {c.Text.TrimEnd()} »" + (chk.Candidates.Count > 0 ? $" ≈ {string.Join(", ", chk.Candidates)}" : ""));
        if (!ok) misses.Add($"  {c.Cat,-10} expect {c.Expect,-7} got {mark,-10} « {c.Text.TrimEnd()} »" +
                            (chk.Candidates.Count > 0 ? $" ≈ {string.Join(", ", chk.Candidates)}" : ""));
    }
    Console.WriteLine($"{"category",-11} {"cases",5} {"✓",5} {"≈",5} {"?",5} {"none",5} {"as expected",12}");
    foreach (var (cat, a) in byCat)
        Console.WriteLine($"{cat,-11} {a[0],5} {a[1],5} {a[2],5} {a[3],5} {a[4],5} {a[5],7}/{a[0]}");
    if (misses.Count > 0) Console.WriteLine("\nnot as expected (and the names that got a mark):\n" + string.Join("\n", misses));
    // speed: Check over every case word, 200 rounds (the app does one per finished word, on the UI thread)
    var words = cases.Select(c => SpellingIndicator.LastWord(c.Text)?.Word).Where(x => x != null).Cast<string>().ToList();
    foreach (var x in words) eng.Check(x);
    var times = new List<double>(words.Count * 200);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    long alloc0 = GC.GetAllocatedBytesForCurrentThread();
    for (int r = 0; r < 200; r++)
        foreach (var x in words)
        {
            long t0 = Stopwatch.GetTimestamp();
            eng.Check(x);
            times.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }
    long alloc = GC.GetAllocatedBytesForCurrentThread() - alloc0;
    int dg0 = GC.CollectionCount(0) - g0, dg1 = GC.CollectionCount(1) - g1, dg2 = GC.CollectionCount(2) - g2;
    int over1 = times.Count(t => t > 1.0);
    times.Sort();
    double mean = times.Count == 0 ? 0 : times.Average(), p99 = times.Count == 0 ? 0 : times[(int)(times.Count * 0.99)], worst = times.Count == 0 ? 0 : times[^1];
    Console.WriteLine(FormattableString.Invariant($"\ngates: wrong reds {badRed} (target 0); SoundsLike recall on phonetic misspellings {phonSounds}/{phon} = {phonSounds / (double)Math.Max(1, phon):0.000} (target >= 0.8; {phonSounds}/{phon - phonKnown} = {phonSounds / (double)Math.Max(1, phon - phonKnown):0.000} without the {phonKnown} that are lexicon forms), intended word first {phonFirst}/{phon}, among the candidates {phonAmong}/{phon}"));
    Console.WriteLine(FormattableString.Invariant($"names (plan 10C): {names} cases, ✓ {namesKnown}, ≈ {namesSounds} (target 0 with the pack); sentence-start misspellings: {startSounds}/{starts} ≈ = recall {startSounds / (double)Math.Max(1, starts):0.000}, red {startRed} (a capitalised word is never red)"));
    Console.WriteLine(FormattableString.Invariant($"every finished word before the last: {inner} judged (✓ {innerMarks[1]}, ≈ {innerMarks[2]}, ? {innerMarks[3]}, none {innerMarks[0]}); wrong reds there {innerRed} (target 0)"));
    Console.WriteLine(FormattableString.Invariant($"Check: mean {mean * 1000:0.0} µs, p99 {p99 * 1000:0.0} µs, max {worst:0.000} ms over {times.Count} calls; {over1} calls > 1 ms; GC during the loop gen0 {dg0} gen1 {dg1} gen2 {dg2}; {alloc / (double)Math.Max(1, times.Count):0} bytes allocated per call (timing list included)"));
}

// ------------------------------------------------------------------ helpers
static int RankOf(IReadOnlyList<Suggestion> s, string word)
{ for (int i = 0; i < s.Count; i++) if (Eq(s[i].Word, word)) return i + 1; return 0; }
// a curated case typed at a sentence start: no context, or a context ending with . ! ? …
static bool AtStart(string? context) => context is null || context.TrimEnd().Length == 0 || ".!?…".IndexOf(context.TrimEnd()[^1]) >= 0;
static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
// curated cases: an intended word with a capital (a personal name, plan 10 Q13) must come with it
static int RankOfCase(IReadOnlyList<Suggestion> s, string word)
{
    if (!word.Any(char.IsUpper)) return RankOf(s, word);
    for (int i = 0; i < s.Count; i++) if (string.Equals(s[i].Word, word, StringComparison.Ordinal)) return i + 1;
    return 0;
}
static void Bump(Dictionary<string, Stat> a, string key, int rank, int k)
{ var s = a.TryGetValue(key, out var v) ? v : new Stat(); s.Add(rank, k); a[key] = s; }
static string Grade(Stat s)
{ double hit = s.Hit / (double)s.N, top1 = s.Top1 / (double)s.N; return hit >= 0.85 && top1 >= 0.55 ? "GOOD" : hit >= 0.65 ? "FAIR" : "POOR"; }
static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

record Case(string Category, string? Context, string Typed, string Intended);
record SpellCase(string Cat, string Text, string Expect, string? Target);
struct Stat
{
    public int N, Top1, Hit; public double Mrr;
    public void Add(int rank, int k) { N++; if (rank == 1) Top1++; if (rank is > 0 && rank <= k) Hit++; if (rank > 0) Mrr += 1.0 / rank; }
}
