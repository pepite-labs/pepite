using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Predict.Keyboard;

/// <summary>
/// <c>Pepite.exe --speech-bench &lt;outDir&gt; [--profile normal|2core] [--audio device|null] [--voices &lt;dir&gt;]
/// [--sentences &lt;file&gt;] [--volume 0..100] [--voice siwis|pierre] [--switch] [--wav &lt;dir&gt; [--items L01,…]
/// [--variant ns,ls,nw]]</c> (plans 08a step 11, 08b step 5). No hooks, no UI: the real speech stack
/// (<see cref="SpeechWorker"/> + <see cref="EngineRouter"/> + <see cref="PiperSynth"/> + the WASAPI sink) on the plan 05
/// items. An independent sampler reads the device position every 2 ms (its own high-resolution waitable timer, so the
/// process timer resolution the app runs with is not changed) and builds position → time; "first audio" is when that
/// position passes the utterance's first sample, and a highlight event's lag is its arrival minus the time its word's
/// first sample played. Writes bench.json and bench.md; exit code 1 when a gate fails (device audio only).
/// Plan 08b: <c>--voice</c> picks the voice (default siwis); the synthesis call alone (<c>_model.Run</c>) is reported
/// apart; the liaison consonant of L01, L02, L06, L09 and S11 is measured on the alignment. <c>--switch</c>: siwis
/// configured, a « Écouter » of pierre, then the unload 30 s later (switch time, working set with both voices, working
/// set after the unload). <c>--wav</c>: each item spoken once and saved as a 16-bit WAV (with <c>--variant</c>, other
/// noise / length / noise_w scales), for the listening page. <c>--wav &lt;dir&gt; --word &lt;text&gt; [--word-reps N]
/// [--word-cache off]</c> (owner, 0.6.0): the word said N times (default 5) as a word echo; WAVs, whether the samples
/// are identical, their spread, and the phonemes of the word's case / punctuation variants.
/// <c>--wav &lt;dir&gt; --word-items &lt;file&gt; [--word-suffix .] [--carrier "Le mot : {0}."] [--phoneme-fix a=b;…]
/// [--word-reps N]</c> (owner listening of 0.6.0, isolated words): every item of the file (sentences.txt format; category
/// « word » = said as a word echo, else as a sentence) rendered N times (default 5, word cache off), the first take saved as
/// <c>&lt;id&gt;.wav</c>; per take the length, leading / trailing silence, onset / offset level and the phoneme
/// alignment, and the time from the request to the first audio queued (<c>first_ms</c>; p50 / p95 of all word takes in
/// <c>first_audio</c>), in <c>words.json</c>. <c>--carrier "Le mot : {0}."</c> replaces the card's word carrier (since
/// 0.7.2 the cut is the app's own path, <see cref="PiperCarrier"/>; <c>--carrier off</c> = the word alone);
/// <c>--phoneme-fix</c> replaces the card's phoneme corrections (<c>off</c> = none): replacements after espeak (<c>\uXXXX</c> escapes allowed).
/// </summary>
internal static class SpeechBench
{
    private static readonly string[] CommonWords =
    {
        "le", "et", "maison", (char)0xE9 + "cole" /* no accented literal outside Strings.cs */, "chat", "chien", "manger", "jouer", "rouge", "petit",
        "grand", "demain", "toujours", "voiture", "maman", "papa", "livre", "jardin", "soleil", "ami",
    };

    private sealed record Item(string Id, string Category, string Text);

    /// <summary>Liaisons checked on the alignment (plan 08b step 5): item, espeak word that ends with the liaison
    /// consonant (0-based), the consonant.</summary>
    private static readonly (string Item, int Word, string Consonant)[] Liaisons =
    {
        ("L01", 0, "z"), ("L02", 3, "n"), ("L06", 0, "t"), ("L09", 2, "z"), ("S11", 0, "z"),
    };

    /// <summary>Duration of the liaison consonant in ms (null: the word does not end with it, no liaison) from the
    /// phoneme alignment of an utterance (<c>(phoneme, samples)</c>, words separated by " ").</summary>
    internal static double? LiaisonMs(List<(string Phoneme, long Samples)> aligned, int word, string consonant, int rate)
    {
        int w = 0;
        (string P, long S)? last = null;
        foreach (var (p, n) in aligned)
        {
            if (p == " ") { if (w == word) break; w++; last = null; continue; }
            if (w != word || p is "^" or "$" or "_" or "-" or "ˈ" or "ˌ" or "ː" || p.Length == 1 && char.IsPunctuation(p[0])) continue;
            last = (p, n);
        }
        return w == word && last is { } l && l.P == consonant ? l.S * 1000.0 / rate : null;
    }

    public static int Run(string[] args, string outDir, Func<string[], string, string?> option)
    {
        Directory.CreateDirectory(outDir);
        var logLines = new List<string>();
        var logSw = Stopwatch.StartNew();
        void Log(string m) { lock (logLines) logLines.Add($"{logSw.ElapsedMilliseconds,7} {m}"); }
        var profile = option(args, "--profile") ?? "normal";
        var audio = option(args, "--audio") ?? "device";
        int volume = int.TryParse(option(args, "--volume"), out var v) ? Math.Clamp(v, 0, 100) : 30;
        // repetitions (plan: words x10, sentences x3); fewer keep a run with sound short
        int wordReps = int.TryParse(option(args, "--word-reps"), out var wr) ? Math.Clamp(wr, 1, 50) : 10;
        int sentReps = int.TryParse(option(args, "--sentence-reps"), out var sr) ? Math.Clamp(sr, 1, 20) : 3;
        // A/B only: ORT threads allowed to spin between runs (the app never does, A8)
        PiperModel.AllowSpinning = option(args, "--ort-spinning") == "on";
        var longText = option(args, "--long-text");
        if (int.TryParse(option(args, "--max-run"), out var mr)) PiperSynth.RunPhonemes = Math.Max(50, mr);
        var wavDir = option(args, "--wav");
        if (option(args, "--variant") is { } variant)
        {
            var f = variant.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (f.Length != 3) throw new ArgumentException("--variant noise_scale,length_scale,noise_w");
            PiperSynth.VariantScales = new PiperInference(f[0], f[1], f[2]);
        }
        var proc = Process.GetCurrentProcess();
        if (profile == "2core") proc.ProcessorAffinity = (IntPtr)0x5;   // logical CPUs 0 and 2 (two physical cores), as plan 05
        var items = LoadItems(option(args, "--sentences"));
        var voicesDir = option(args, "--voices") ?? Path.Combine(AppContext.BaseDirectory, "voices");
        var packs = PiperVoicePack.Scan(voicesDir, Log);
        if (packs.Count == 0) { File.WriteAllText(Path.Combine(outDir, "bench.md"), $"no voice pack in {voicesDir}\n"); return 2; }

        var result = new Dictionary<string, object?> { ["profile"] = profile, ["audio"] = audio, ["volume"] = volume,
                                                       ["word_reps"] = wordReps, ["sentence_reps"] = sentReps,
                                                       ["date"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                                                       ["cpus"] = Environment.ProcessorCount };
        proc.Refresh();
        long wsBefore = proc.WorkingSet64, privBefore = proc.PrivateMemorySize64;
        var voiceId = "piper:" + (option(args, "--voice") ?? "siwis");
        var pack = packs.FirstOrDefault(p => p.VoiceId == voiceId);
        if (pack is null) { File.WriteAllText(Path.Combine(outDir, "bench.md"), $"no voice {voiceId} in {voicesDir}\n"); return 2; }
        result["voice"] = voiceId;
        var piper = new PiperSynth(packs, Path.Combine(AppContext.BaseDirectory, "espeak-ng-data"), Log,
                                   audio == "null" ? r => new NullSink(r) : null);
        piper.Resident = voiceId;   // the configured voice (as the router sets it before the load)
        var loadSw = Stopwatch.StartNew();
        piper.LoadNow(voiceId);
        result["load_ms"] = loadSw.ElapsedMilliseconds;
        if (piper.StateOf(voiceId) != PiperState.Ready) { File.WriteAllLines(Path.Combine(outDir, "bench.log"), logLines); return 2; }
        proc.Refresh();
        long wsWarm = proc.WorkingSet64, privWarm = proc.PrivateMemorySize64;

        SpeechWorker? worker = null;
        worker = new SpeechWorker(() => new EngineRouter(new Speaker.SapiSynth(null), piper, Log, a => worker!.Invoke(a)),
                                  new SpeechSettings(Voice: voiceId, Volume: volume), Log);
        worker.Voices.Wait(10000);
        var runAlone = new List<double>();
        piper.FirstRunMs = ms => { lock (runAlone) runAlone.Add(ms); };
        if (option(args, "--phoneme-fix") is { } fix)
        {
            PiperSynth.OverridePhonemeFix = true;
            PiperSynth.PhonemeFixOverride = fix == "off" ? null : PiperPhonemeFix.Parse(fix);
        }
        if (option(args, "--carrier") is { } carrierOpt) PiperSynth.CarrierOverride = carrierOpt == "off" ? "" : carrierOpt;
        if (wavDir != null && option(args, "--word-items") is { } wordItems)
            return RenderWordItems(args, option, outDir, wavDir, LoadItems(wordItems), worker, piper, pack, voiceId, result, logLines);
        if (wavDir != null && option(args, "--word") is { } wordText)
            return RenderWord(args, option, outDir, wavDir, wordText, worker, piper, pack, voiceId, result, logLines);
        if (wavDir != null) return RenderWavs(args, option, outDir, wavDir, items, worker, piper, pack, result, logLines);
        if (args.Contains("--switch")) return Switch(outDir, packs, piper, worker, voiceId, volume, proc, wsBefore, wsWarm, result, logLines);

        // ---- position sampler
        var sampler = new Sampler(() => piper.Sink?.PlayedSourceSamples ?? 0);
        long firstStart = -1;
        piper.FirstChunkQueued = s => { Volatile.Write(ref firstStart, s); };
        var synthOnly = new List<double>();

        (double FirstMs, double SynthMs, SpeechOutcome Outcome) Say(string text, SpeechKind kind, Action<SpeechProgress>? progress = null,
                                                                    int cancelAfterFirstMs = -1, int cancelAfterSayMs = -1,
                                                                    List<double>? cancelDone = null, List<double>? cancelStop = null)
        {
            Volatile.Write(ref firstStart, -1);
            long synthAt = 0;
            piper.FirstChunkQueued = s => { synthAt = Stopwatch.GetTimestamp(); Volatile.Write(ref firstStart, s); };
            using var ended = new ManualResetEventSlim();
            SpeechOutcome outcome = SpeechOutcome.Error;
            long doneAt = 0;
            long t0 = Stopwatch.GetTimestamp();
            worker!.Say(new SpeechItem(text, kind, null, progress, o => { outcome = o; doneAt = Stopwatch.GetTimestamp(); ended.Set(); }));
            double first = double.NaN;
            if (cancelAfterSayMs >= 0)
            {
                Thread.Sleep(cancelAfterSayMs);
                Cancel();
            }
            else
            {
                var deadline = Stopwatch.StartNew();
                while (Volatile.Read(ref firstStart) < 0 && !ended.IsSet && deadline.ElapsedMilliseconds < 10000) Thread.Sleep(1);
                long fs = Volatile.Read(ref firstStart);
                if (fs >= 0)
                {
                    long at = sampler.WaitFor(fs + 1, 10000);
                    if (at > 0) first = Ms(t0, at);
                }
                if (cancelAfterFirstMs >= 0) { Thread.Sleep(cancelAfterFirstMs); Cancel(); }
            }
            ended.Wait(30000);
            double synth = synthAt > 0 ? Ms(t0, synthAt) : double.NaN;
            return (first, synth, outcome);

            void Cancel()
            {
                long c0 = Stopwatch.GetTimestamp();
                worker!.CancelAll();
                ended.Wait(5000);
                cancelDone?.Add(Math.Max(0, Ms(c0, doneAt)));   // done may land before c0 is read back
                long q = piper.Sink?.QueuedEndSample ?? 0;
                long stop = sampler.WaitFor(q, 2000);
                cancelStop?.Add(stop > 0 ? Math.Max(0, Ms(c0, stop)) : double.NaN);
            }
        }

        result["ort_spinning"] = PiperModel.AllowSpinning;
        result["max_run_phonemes"] = PiperSynth.RunPhonemes;
        if (longText != null)
        {
            // one long utterance (e.g. 1000 words without punctuation): first audio, total, peak working set
            var text = File.ReadAllText(longText, Encoding.UTF8);
            proc.Refresh();
            long peak0 = proc.PeakWorkingSet64;
            var tl = Stopwatch.StartNew();
            var (lf, ls, lo) = Say(text, SpeechKind.Reading, _ => { });
            proc.Refresh();
            result["long_text"] = new { chars = text.Length, words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                                        first_audio_ms = lf, synth_first_ms = ls, outcome_after_30s = lo.ToString(), total_ms = tl.ElapsedMilliseconds,
                                        ws_peak_delta_mb = (proc.PeakWorkingSet64 - wsBefore) / 1048576.0,
                                        peak_before_mb = peak0 / 1048576.0 };
            sampler.Dispose();
            File.WriteAllText(Path.Combine(outDir, "bench.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
            File.WriteAllLines(Path.Combine(outDir, "bench.log"), logLines);
            worker.Dispose();
            return 0;
        }
        var benchSw = Stopwatch.StartNew();
        // ---- word echo
        var words = items.Where(i => i.Category == "word").Select(i => i.Text).Concat(CommonWords).ToList();
        var wordFirst = new List<double>();
        lock (runAlone) runAlone.Clear();
        for (int r = 0; r < wordReps; r++)
            foreach (var w in words)
            {
                var (f, s, _) = Say(w, SpeechKind.Word);
                wordFirst.Add(f); synthOnly.Add(s);
            }
        // ---- sentences
        List<double> runWords;
        lock (runAlone) { runWords = runAlone.ToList(); runAlone.Clear(); }
        var sentFirst = new List<double>();
        foreach (var it in items.Where(i => i.Category != "word"))
            for (int r = 0; r < sentReps; r++)
            {
                var (f, s, _) = Say(it.Text, SpeechKind.Sentence);
                sentFirst.Add(f); synthOnly.Add(s);
            }
        List<double> runSentences;
        lock (runAlone) runSentences = runAlone.ToList();
        // ---- cancel
        var s11 = items.FirstOrDefault(i => i.Id == "S11")?.Text ?? "Les enfants jouent dans la cour.";
        var cancelDone = new List<double>();
        var cancelStop = new List<double>();
        for (int r = 0; r < 20; r++)
        {
            Say(s11, SpeechKind.Sentence, null, cancelAfterFirstMs: 200, cancelDone: cancelDone, cancelStop: cancelStop);
            Say(s11, SpeechKind.Sentence, null, cancelAfterSayMs: 50, cancelDone: cancelDone, cancelStop: cancelStop);
        }
        // ---- highlight
        int units = 0, covered = 0, inWindow = 0;
        var lags = new List<double>();
        EspeakNative.Initialize(Path.Combine(AppContext.BaseDirectory, "espeak-ng-data"));
        int Isolated(string t) => PiperWordMap.WordCount(PiperPhonemizer.Sentences(EspeakNative.Clauses(TextCleanup.ForEspeak(t), "fr"), null));
        var liaison = new Dictionary<string, double?>();
        foreach (var it in items)
        {
            var events = new List<(SpeechProgress P, long At)>();
            List<(string, long)>? align = null;
            piper.Rendered = (_, a) => align = a;
            var (_, _, _) = Say(it.Text, SpeechKind.Reading, p => { lock (events) events.Add((p, Stopwatch.GetTimestamp())); });
            piper.Rendered = null;
            foreach (var l in Liaisons.Where(l => l.Item == it.Id))
                liaison[$"{l.Item} /{l.Consonant}/"] = align is null ? null : LiaisonMs(align, l.Word, l.Consonant, pack.SampleRate);
            long origin = Volatile.Read(ref firstStart);
            var us = PiperWordMap.Units(it.Text, Isolated);
            units += us.Count;
            foreach (var u in us)
            {
                var mine = events.Where(e => e.P.CharPos == u.Start).ToList();
                if (mine.Count != 1) continue;
                covered++;
                long sample = origin + (long)Math.Round(mine[0].P.AudioMs * pack.SampleRate / 1000.0);
                long played = sampler.TimeAt(sample);
                if (played <= 0) continue;
                double lag = Ms(played, mine[0].At);
                lags.Add(lag);
                if (lag >= -20 && lag <= 100) inWindow++;
            }
        }
        // ---- footprint after the last utterance (the bench's own 2 ms sampler stopped first)
        sampler.Dispose();
        proc.Refresh();
        long wsPeak = proc.PeakWorkingSet64;
        var cpu0 = proc.TotalProcessorTime;
        var cpuSw = Stopwatch.StartNew();
        Thread.Sleep(2000);
        proc.Refresh();
        double cpuOneCore = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / cpuSw.Elapsed.TotalMilliseconds * 100;
        double benchSeconds = benchSw.Elapsed.TotalSeconds;

        double P95(List<double> l) => Percentile(l.Where(x => !double.IsNaN(x)).ToList(), 0.95);
        double P50(List<double> l) => Percentile(l.Where(x => !double.IsNaN(x)).ToList(), 0.50);
        result["word_first_audio_ms"] = new { p50 = P50(wordFirst), p95 = P95(wordFirst), n = wordFirst.Count(x => !double.IsNaN(x)) };
        result["sentence_first_audio_ms"] = new { p50 = P50(sentFirst), p95 = P95(sentFirst), n = sentFirst.Count(x => !double.IsNaN(x)) };
        result["synth_first_chunk_ms"] = new { p50 = P50(synthOnly), p95 = P95(synthOnly),
                                               word_p95 = P95(synthOnly.Take(wordFirst.Count).ToList()),
                                               sentence_p95 = P95(synthOnly.Skip(wordFirst.Count).ToList()) };
        result["synth_run_alone_ms"] = new { word_p50 = P50(runWords), word_p95 = P95(runWords), sentence_p50 = P50(runSentences), sentence_p95 = P95(runSentences) };
        result["liaison_consonant_ms"] = liaison;
        result["cancel_done_ms"] = new { p50 = P50(cancelDone), p95 = P95(cancelDone), n = cancelDone.Count };
        result["cancel_audio_stop_ms"] = new { p50 = P50(cancelStop), p95 = P95(cancelStop), n = cancelStop.Count(x => !double.IsNaN(x)) };
        double coverage = units == 0 ? 0 : 100.0 * inWindow / units;
        result["highlight"] = new { units, one_event = covered, in_window = inWindow, pct = coverage,
                                    lag_p50 = P50(lags), lag_p95 = P95(lags), lag_min = lags.Count > 0 ? lags.Min() : double.NaN,
                                    lag_max = lags.Count > 0 ? lags.Max() : double.NaN };
        result["memory_mb"] = new { ws_before = wsBefore / 1048576.0, ws_after_warmup = wsWarm / 1048576.0, ws_peak = wsPeak / 1048576.0,
                                    ws_delta_warmup = (wsWarm - wsBefore) / 1048576.0, ws_delta_peak = (wsPeak - wsBefore) / 1048576.0,
                                    private_before = privBefore / 1048576.0, private_after_warmup = privWarm / 1048576.0 };
        result["cpu_after_last_pct_of_one_core"] = cpuOneCore;
        result["bench_seconds"] = benchSeconds;

        var gates = new List<(string Name, bool Pass, string Value)>
        {
            ("word first audio p95 <= 300 ms", P95(wordFirst) <= 300, $"{P95(wordFirst):0} ms"),
            ("sentence first audio p95 <= 500 ms", P95(sentFirst) <= 500, $"{P95(sentFirst):0} ms"),
            ("cancel -> done p95 <= 100 ms", P95(cancelDone) <= 100, $"{P95(cancelDone):0} ms"),
            ("cancel -> audio stops p95 <= 100 ms", P95(cancelStop) <= 100, $"{P95(cancelStop):0} ms"),
            ("highlight: units with one event, lag in [-20, +100] ms >= 95 %", coverage >= 95, $"{coverage:0.0} % ({inWindow}/{units})"),
            ("working set after warm-up <= +150 MB", (wsWarm - wsBefore) / 1048576.0 <= 150, $"+{(wsWarm - wsBefore) / 1048576.0:0} MB"),
            ("peak working set <= +250 MB", (wsPeak - wsBefore) / 1048576.0 <= 250, $"+{(wsPeak - wsBefore) / 1048576.0:0} MB"),
            ("CPU 2 s after the last utterance < 1 %", cpuOneCore < 1, $"{cpuOneCore:0.00} % of one core"),
        };
        result["gates"] = gates.Select(g => new { g.Name, g.Pass, g.Value }).ToList();
        bool gated = audio == "device";
        bool pass = gates.All(g => g.Pass);
        result["gates_apply"] = gated;
        result["pass"] = pass;

        File.WriteAllText(Path.Combine(outDir, "bench.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        var md = new StringBuilder();
        md.AppendLine($"# Speech bench — {profile}, audio {audio}");
        md.AppendLine();
        md.AppendLine($"{result["date"]}, {Environment.ProcessorCount} logical CPUs, voice {voiceId}, load {result["load_ms"]} ms, bench {benchSeconds:0} s, volume {volume}.");
        md.AppendLine();
        md.AppendLine("| Measure | p50 | p95 |");
        md.AppendLine("|---|---|---|");
        md.AppendLine(FormattableString.Invariant($"| Word echo first audio ({wordFirst.Count} runs) | {P50(wordFirst):0} ms | {P95(wordFirst):0} ms |"));
        md.AppendLine(FormattableString.Invariant($"| Sentence first audio ({sentFirst.Count} runs) | {P50(sentFirst):0} ms | {P95(sentFirst):0} ms |"));
        md.AppendLine(FormattableString.Invariant($"| Synthesis to first chunk queued, words | {P50(synthOnly.Take(wordFirst.Count).ToList()):0} ms | {P95(synthOnly.Take(wordFirst.Count).ToList()):0} ms |"));
        md.AppendLine(FormattableString.Invariant($"| Synthesis to first chunk queued, sentences | {P50(synthOnly.Skip(wordFirst.Count).ToList()):0} ms | {P95(synthOnly.Skip(wordFirst.Count).ToList()):0} ms |"));
        md.AppendLine(FormattableString.Invariant($"| Synthesis call alone (_model.Run), words | {P50(runWords):0} ms | {P95(runWords):0} ms |"));
        md.AppendLine(FormattableString.Invariant($"| Synthesis call alone (_model.Run), sentences | {P50(runSentences):0} ms | {P95(runSentences):0} ms |"));
        md.AppendLine(FormattableString.Invariant($"| Cancel → done ({cancelDone.Count}) | {P50(cancelDone):0} ms | {P95(cancelDone):0} ms |"));
        md.AppendLine(FormattableString.Invariant($"| Cancel → audio stops | {P50(cancelStop):0} ms | {P95(cancelStop):0} ms |"));
        md.AppendLine(FormattableString.Invariant($"| Highlight lag ({lags.Count} events) | {P50(lags):0} ms | {P95(lags):0} ms |"));
        md.AppendLine();
        md.AppendLine("Liaison consonant (alignment): " + string.Join(", ", liaison.Select(kv => kv.Value is { } ms ? FormattableString.Invariant($"{kv.Key} {ms:0} ms") : $"{kv.Key} none")));
        md.AppendLine();
        md.AppendLine(gated ? "Gates (device audio):" : "Gates (information only: null audio):");
        md.AppendLine();
        foreach (var g in gates) md.AppendLine($"- [{(g.Pass ? "x" : " ")}] {g.Name}: {g.Value}");
        File.WriteAllText(Path.Combine(outDir, "bench.md"), md.ToString());
        File.WriteAllLines(Path.Combine(outDir, "bench.log"), logLines);
        worker.Dispose();
        return gated && !pass ? 1 : 0;
    }

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    /// <summary><c>--wav &lt;dir&gt;</c>: each item (<c>--items</c>, default every sentence item) spoken once as a
    /// sentence; its audio saved as <c>&lt;dir&gt;\&lt;id&gt;.wav</c> (16-bit mono, the voice's rate) with the liaison
    /// consonant durations in <c>liaison.json</c> (plan 08b step 7).</summary>
    private static int RenderWavs(string[] args, Func<string[], string, string?> option, string outDir, string wavDir, List<Item> items,
                                  SpeechWorker worker, PiperSynth piper, PiperVoicePack pack, Dictionary<string, object?> result, List<string> logLines)
    {
        Directory.CreateDirectory(wavDir);
        var only = option(args, "--items")?.Split(',').ToHashSet(StringComparer.Ordinal);
        var chosen = items.Where(i => only?.Contains(i.Id) ?? i.Category != "word").ToList();
        var liaison = new Dictionary<string, double?>();
        foreach (var it in chosen)
        {
            float[]? audio = null;
            List<(string, long)>? align = null;
            piper.Rendered = (a, al) => { audio = a; align = al; };
            using var ended = new ManualResetEventSlim();
            worker.Say(new SpeechItem(it.Text, SpeechKind.Sentence, null, null, _ => ended.Set()));
            ended.Wait(60000);
            piper.Rendered = null;
            if (audio is null) { logLines.Add($"no audio for {it.Id}"); continue; }
            WriteWav(Path.Combine(wavDir, it.Id + ".wav"), audio, pack.SampleRate);
            foreach (var l in Liaisons.Where(l => l.Item == it.Id))
                liaison[$"{l.Item} /{l.Consonant}/"] = align is null ? null : LiaisonMs(align, l.Word, l.Consonant, pack.SampleRate);
        }
        result["variant"] = PiperSynth.VariantScales is { } v ? new[] { v.NoiseScale, v.LengthScale, v.NoiseW } : null;
        result["wav_items"] = chosen.Select(i => i.Id).ToList();
        result["liaison_consonant_ms"] = liaison;
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(wavDir, "liaison.json"), json);
        File.WriteAllText(Path.Combine(outDir, "bench.json"), json);
        File.WriteAllLines(Path.Combine(outDir, "bench.log"), logLines);
        worker.Dispose();
        return 0;
    }

    /// <summary><c>--word</c>: determinism of an isolated word (word cache on: identical samples; off: the spread of the
    /// word noise scales).</summary>
    private static int RenderWord(string[] args, Func<string[], string, string?> option, string outDir, string wavDir, string word,
                                  SpeechWorker worker, PiperSynth piper, PiperVoicePack pack, string voiceId,
                                  Dictionary<string, object?> result, List<string> logLines)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            File.WriteAllText(Path.Combine(outDir, "bench.md"), "--word needs a word\n");
            worker.Dispose();
            return 2;
        }
        Directory.CreateDirectory(wavDir);
        int reps = int.TryParse(option(args, "--word-reps"), out var n) ? Math.Clamp(n, 2, 50) : 5;
        PiperSynth.WordCacheEnabled = option(args, "--word-cache") != "off";
        var takes = new List<float[]>();
        for (int r = 0; r < reps; r++)
        {
            float[]? audio = null;
            piper.Rendered = (a, _) => audio = a;
            using var ended = new ManualResetEventSlim();
            worker.Say(new SpeechItem(word, SpeechKind.Word, null, null, _ => ended.Set()));
            ended.Wait(30000);
            piper.Rendered = null;
            if (audio is null) { logLines.Add($"no audio for take {r + 1}"); continue; }
            takes.Add(audio);
            WriteWav(Path.Combine(wavDir, $"take{r + 1}.wav"), audio, pack.SampleRate);
        }
        if (takes.Count == 0) { File.WriteAllLines(Path.Combine(outDir, "bench.log"), logLines); worker.Dispose(); return 2; }
        var first = takes[0];
        double Rms(float[] x) => Math.Sqrt(x.Select(v => (double)v * v).DefaultIfEmpty(0).Average());
        // spread: length in ms, and RMS of the difference with take 1 over the common length, relative to take 1's RMS
        var diffs = takes.Skip(1).Select(t =>
        {
            int len = Math.Min(t.Length, first.Length);
            double d = 0;
            for (int i = 0; i < len; i++) d += (t[i] - first[i]) * (double)(t[i] - first[i]);
            return len == 0 ? 0 : Math.Sqrt(d / len) / Math.Max(1e-9, Rms(first));
        }).ToList();
        var variants = new[] { word, word.ToUpperInvariant(), char.ToUpperInvariant(word[0]) + word.Substring(1), word + ",", word + ".", " " + word + " " };
        result["word"] = word;
        result["word_cache"] = PiperSynth.WordCacheEnabled;
        result["word_scales"] = pack.InferenceWord is { } iw ? new[] { iw.NoiseScale, iw.NoiseW } : null;
        result["takes"] = takes.Count;
        result["identical"] = takes.All(t => t.AsSpan().SequenceEqual(first));
        result["length_ms"] = takes.Select(t => Math.Round(t.Length * 1000.0 / pack.SampleRate, 1)).ToList();
        result["relative_rms_diff_vs_take1"] = diffs.Select(d => Math.Round(d, 4)).ToList();
        result["phonemes"] = variants.Distinct().ToDictionary(x => x, x => piper.PhonemesOf(voiceId, x));
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(Path.Combine(wavDir, "word.json"), json);
        File.WriteAllText(Path.Combine(outDir, "bench.json"), json);
        File.WriteAllLines(Path.Combine(outDir, "bench.log"), logLines);
        worker.Dispose();
        return 0;
    }

    /// <summary>Level measures of one rendering: leading / trailing silence (below 2 % of the peak), the sounding span,
    /// the level of its first and last 30 ms relative to the whole span (dB; a weak onset or a cut-off ending shows here)
    /// and the samples at ≥ 98 % of the peak (clipping-like flat tops).</summary>
    internal static Dictionary<string, double> Measure(float[] a, int rate)
    {
        float peak = a.Length == 0 ? 0 : a.Max(x => Math.Abs(x));
        var m = new Dictionary<string, double> { ["total_ms"] = Math.Round(a.Length * 1000.0 / rate, 1) };
        if (peak <= 0) return m;
        float thr = 0.02f * peak;
        int first = Array.FindIndex(a, x => Math.Abs(x) > thr), last = Array.FindLastIndex(a, x => Math.Abs(x) > thr);
        double Rms(int from, int n) { double s = 0; n = Math.Max(1, Math.Min(n, a.Length - from)); for (int i = from; i < from + n; i++) s += a[i] * (double)a[i]; return Math.Sqrt(s / n); }
        int span = last - first + 1, w = Math.Min(span, rate * 30 / 1000);
        double spanRms = Rms(first, span);
        m["lead_ms"] = Math.Round(first * 1000.0 / rate, 1);
        m["trail_ms"] = Math.Round((a.Length - 1 - last) * 1000.0 / rate, 1);
        m["span_ms"] = Math.Round(span * 1000.0 / rate, 1);
        m["onset_db"] = Math.Round(20 * Math.Log10(Rms(first, w) / spanRms), 1);
        m["offset_db"] = Math.Round(20 * Math.Log10(Rms(last - w + 1, w) / spanRms), 1);
        m["near_peak_samples"] = a.Count(x => Math.Abs(x) >= 0.98f * peak);
        return m;
    }

    /// <summary><c>--word-items</c>: the owner's isolated-word sheet (0.6.0 listening). See the class summary.</summary>
    private static int RenderWordItems(string[] args, Func<string[], string, string?> option, string outDir, string wavDir, List<Item> items,
                                       SpeechWorker worker, PiperSynth piper, PiperVoicePack pack, string voiceId,
                                       Dictionary<string, object?> result, List<string> logLines)
    {
        Directory.CreateDirectory(wavDir);
        int reps = int.TryParse(option(args, "--word-reps"), out var n) ? Math.Clamp(n, 1, 50) : 5;
        var suffix = option(args, "--word-suffix") ?? "";
        var carrier = PiperSynth.CarrierOverride is { } co ? (co.Length == 0 ? null : co) : pack.WordCarrier;
        PiperSynth.WordCacheEnabled = false;   // every take synthesised (spread); the app's cache only replays take 1
        int rate = pack.SampleRate;
        var outItems = new List<object>();
        var wordFirst = new List<double>();   // request -> first audio queued, every word take (cold: cache off)
        foreach (var it in items)
        {
            bool word = it.Category == "word";
            // the carrier is the engine's (PiperSynth.SpeakCarrier): the item is said as a word, as the app says it
            var text = word ? it.Text + suffix : it.Text;
            var takes = new List<Dictionary<string, object>>();
            for (int r = 0; r < reps; r++)
            {
                float[]? audio = null;
                List<(string Phoneme, long Samples)>? align = null;
                double runMs = double.NaN, firstMs = double.NaN;
                PiperCarrier.Cut? cutInfo = null;
                float[]? whole = null;
                List<(string Phoneme, long Samples)>? wholeAlign = null;
                piper.Rendered = (a, al) => { audio = a; align = al; };
                piper.CarrierRendered = (a, al, c) => { whole = a; wholeAlign = al; cutInfo = c; };
                piper.FirstRunMs = ms => runMs = ms;   // the synthesis call alone (wall_ms also counts the null-sink playback)
                using var ended = new ManualResetEventSlim();
                var sw = Stopwatch.StartNew();
                piper.FirstChunkQueued = _ => { if (double.IsNaN(firstMs)) firstMs = sw.Elapsed.TotalMilliseconds; };
                worker.Say(new SpeechItem(text, word ? SpeechKind.Word : SpeechKind.Sentence, null, null, _ => ended.Set()));
                ended.Wait(60000);
                piper.Rendered = null;
                piper.CarrierRendered = null;
                piper.FirstChunkQueued = null;
                if (audio is null || align is null) { logLines.Add($"no audio for {it.Id} take {r + 1}"); continue; }
                var take = new Dictionary<string, object> { ["wall_ms"] = sw.ElapsedMilliseconds, ["synth_ms"] = Math.Round(runMs, 1),
                                                            ["first_ms"] = Math.Round(firstMs, 1) };
                if (word) wordFirst.Add(firstMs);
                var alignMs = align.Select(x => FormattableString.Invariant($"{x.Phoneme} {x.Samples * 1000.0 / rate:0}")).ToList();
                if (word && carrier != null && pack.SaysAlone(text)) take["carrier_except"] = true;   // card word_carrier_except
                else if (word && carrier != null && cutInfo is null) take["carrier_fallback"] = true;
                if (word && cutInfo is { } ci && whole != null)
                {
                    take["aligned_start_ms"] = Math.Round(ci.AlignedStart * 1000.0 / rate, 1);
                    take["cut_start_ms"] = Math.Round(ci.CutStart * 1000.0 / rate, 1);
                    take["level_at_cut"] = Math.Round(ci.LevelAtCut, 4);   // relative to the carrier's peak (1.0): ~0 = cut in silence
                    take["carrier_ms"] = Math.Round(whole.Length * 1000.0 / rate, 1);
                    take["word_align"] = alignMs;
                    take["carrier_align"] = wholeAlign!.Select(x => FormattableString.Invariant($"{x.Phoneme} {x.Samples * 1000.0 / rate:0}")).ToList();
                }
                else take["align"] = alignMs;
                foreach (var (k, v) in Measure(audio, rate)) take[k] = v;
                takes.Add(take);
                if (r == 0) WriteWav(Path.Combine(wavDir, it.Id + ".wav"), audio, rate);
            }
            double Med(string k)
            {
                var l = takes.Where(t => t.ContainsKey(k)).Select(t => Convert.ToDouble(t[k], CultureInfo.InvariantCulture)).ToList();
                return Math.Round(Percentile(l, 0.5), 1);
            }
            outItems.Add(new
            {
                id = it.Id, text = it.Text, spoken = text, kind = word ? "word" : "sentence", phonemes = piper.PhonemesOf(voiceId, text),
                median = new { total_ms = Med("total_ms"), lead_ms = Med("lead_ms"), trail_ms = Med("trail_ms"), span_ms = Med("span_ms"),
                               onset_db = Med("onset_db"), offset_db = Med("offset_db"), wall_ms = Med("wall_ms"), synth_ms = Med("synth_ms"),
                               first_ms = Med("first_ms") },
                span_range_ms = takes.Where(t => t.ContainsKey("span_ms")).Select(t => (double)t["span_ms"]).DefaultIfEmpty(0)
                                     .Aggregate((Min: double.MaxValue, Max: double.MinValue), (a, x) => (Math.Min(a.Min, x), Math.Max(a.Max, x)),
                                                a => Math.Round(a.Max - a.Min, 1)),
                takes,
            });
        }
        result["variant"] = PiperSynth.VariantScales is { } vs ? new[] { vs.NoiseScale, vs.LengthScale, vs.NoiseW } : null;
        result["word_scales"] = pack.InferenceWord is { } iw ? new[] { iw.NoiseScale, iw.NoiseW } : null;
        result["suffix"] = suffix;
        result["carrier"] = carrier;
        result["phoneme_fix"] = PiperSynth.OverridePhonemeFix
            ? option(args, "--phoneme-fix")
            : pack.PhonemeFix is { } pf ? string.Join(";", pf.Rules.Select(x => x.From + "=" + x.To)) : null;
        var firstOk = wordFirst.Where(x => !double.IsNaN(x)).ToList();
        result["first_audio"] = new { n = firstOk.Count, p50_ms = Math.Round(Percentile(firstOk, 0.5), 1),
                                      p95_ms = Math.Round(Percentile(firstOk, 0.95), 1), max_ms = firstOk.Count > 0 ? Math.Round(firstOk.Max(), 1) : 0 };
        result["reps"] = reps;
        result["items"] = outItems;
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                                                                                 NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals });
        File.WriteAllText(Path.Combine(wavDir, "words.json"), json);
        File.WriteAllText(Path.Combine(outDir, "bench.json"), json);
        File.WriteAllLines(Path.Combine(outDir, "bench.log"), logLines);
        worker.Dispose();
        return 0;
    }

    /// <summary>16-bit PCM mono WAV of float samples in [−1, 1].</summary>
    internal static void WriteWav(string path, float[] samples, int rate)
    {
        using var f = new BinaryWriter(File.Create(path));
        int data = samples.Length * 2;
        f.Write(Encoding.ASCII.GetBytes("RIFF")); f.Write(36 + data); f.Write(Encoding.ASCII.GetBytes("WAVE"));
        f.Write(Encoding.ASCII.GetBytes("fmt ")); f.Write(16); f.Write((short)1); f.Write((short)1); f.Write(rate); f.Write(rate * 2);
        f.Write((short)2); f.Write((short)16);
        f.Write(Encoding.ASCII.GetBytes("data")); f.Write(data);
        foreach (var x in samples) f.Write((short)Math.Round(Math.Clamp(x, -1f, 1f) * 32767));
    }

    /// <summary><c>--switch</c> (plan 08b step 5): the configured voice (siwis) speaks, then rests (device closed after
    /// <see cref="PiperSynth.IdleCloseMs"/>, then 35 s): its working set is the baseline. Then a « Écouter » of pierre
    /// (held while it loads, then spoken), an echo by siwis again, the wait for pierre's unload
    /// (<see cref="PiperResidency.UnloadIdleMs"/> after its own last utterance) and 35 s more, measured as the baseline
    /// was. Gates: both voices resident ≤ +350 MB; 35 s after the unload ≤ the siwis-alone baseline + 10 MB (owner's
    /// like-for-like re-baseline of the plan's « ≤ +170 MB », 2026-11-13).</summary>
    private static int Switch(string outDir, IReadOnlyList<PiperVoicePack> packs, PiperSynth piper, SpeechWorker worker, string voiceId,
                              int volume, Process proc, long wsBefore, long wsWarm, Dictionary<string, object?> result, List<string> logLines)
    {
        var other = packs.FirstOrDefault(p => p.VoiceId != voiceId)?.VoiceId;
        if (other is null) { File.WriteAllText(Path.Combine(outDir, "bench.md"), "--switch needs two voices\n"); return 2; }
        const int RestMs = 35_000;
        void SayWait(SpeechItem item)
        {
            using var ended = new ManualResetEventSlim();
            worker.Say(item with { Done = _ => ended.Set() });
            ended.Wait(60000);
        }
        long RestedWs()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(RestMs);
            proc.Refresh();
            return proc.WorkingSet64;
        }
        // ---- baseline: the configured voice alone, after it spoke, device closed, rested
        SayWait(new SpeechItem(Strings.ListenText, SpeechKind.Sentence));
        SayWait(new SpeechItem("maison", SpeechKind.Word));
        Thread.Sleep(PiperSynth.IdleCloseMs + 1000);
        long wsBaseline = RestedWs();
        // ---- preview of the other voice
        var sw = Stopwatch.StartNew();
        long previewFirst = -1;
        piper.FirstChunkQueued = _ => { if (previewFirst < 0) previewFirst = sw.ElapsedMilliseconds; };
        SayWait(new SpeechItem(Strings.ListenText, SpeechKind.Preview, new SpeechSettings(Voice: other, Volume: volume)));
        var sincePreview = Stopwatch.StartNew();   // the preview voice's last utterance ended
        long switchMs = previewFirst;
        piper.FirstChunkQueued = null;
        SayWait(new SpeechItem("maison", SpeechKind.Word));   // the configured voice again: no reload
        proc.Refresh();
        long wsBoth = proc.WorkingSet64, peakBoth = proc.PeakWorkingSet64;
        bool Unloaded() { lock (logLines) return logLines.Any(l => l.Contains($"{other} unloaded")); }
        while (!Unloaded() && sincePreview.ElapsedMilliseconds < PiperResidency.UnloadIdleMs + 20_000) Thread.Sleep(100);
        long unloadAfterMs = sincePreview.ElapsedMilliseconds;
        long wsAfter = RestedWs();
        double Mb(long b) => b / 1048576.0;
        result["switch"] = new
        {
            configured = voiceId, preview = other, preview_first_audio_ms = switchMs, unloaded = Unloaded(),
            unload_after_preview_end_ms = unloadAfterMs,
            ws_after_warmup_delta_mb = Mb(wsWarm - wsBefore), ws_baseline_one_voice_rested_delta_mb = Mb(wsBaseline - wsBefore),
            ws_both_delta_mb = Mb(wsBoth - wsBefore), ws_peak_delta_mb = Mb(peakBoth - wsBefore),
            ws_35s_after_unload_delta_mb = Mb(wsAfter - wsBefore), ws_after_minus_baseline_mb = Mb(wsAfter - wsBaseline),
        };
        var gates = new List<(string Name, bool Pass, string Value)>
        {
            ("both voices resident <= +350 MB", Mb(wsBoth - wsBefore) <= 350, $"+{Mb(wsBoth - wsBefore):0} MB"),
            ("35 s after the preview voice is unloaded <= one-voice rested baseline + 10 MB",
             Unloaded() && Mb(wsAfter - wsBaseline) <= 10,
             FormattableString.Invariant($"+{Mb(wsAfter - wsBefore):0} MB vs baseline +{Mb(wsBaseline - wsBefore):0} MB ({Mb(wsAfter - wsBaseline):+0.0;-0.0} MB; unloaded: {Unloaded()})")),
        };
        result["gates"] = gates.Select(g => new { g.Name, g.Pass, g.Value }).ToList();
        result["pass"] = gates.All(g => g.Pass);
        File.WriteAllText(Path.Combine(outDir, "bench.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        var md = new StringBuilder();
        md.AppendLine($"# Speech bench — voice switch ({voiceId} configured, preview of {other})");
        md.AppendLine();
        md.AppendLine(FormattableString.Invariant($"Load {result["load_ms"]} ms; preview of {other} (load + first chunk) {switchMs} ms; {other} unloaded {unloadAfterMs / 1000.0:0.0} s after its own last utterance."));
        md.AppendLine(FormattableString.Invariant($"Working set: +{Mb(wsWarm - wsBefore):0} MB one voice after warm-up; one voice rested baseline (spoke, device closed, {RestMs / 1000} s) +{Mb(wsBaseline - wsBefore):0} MB; both +{Mb(wsBoth - wsBefore):0} MB (peak +{Mb(peakBoth - wsBefore):0}); {RestMs / 1000} s after the unload +{Mb(wsAfter - wsBefore):0} MB."));
        md.AppendLine();
        foreach (var g in gates) md.AppendLine($"- [{(g.Pass ? "x" : " ")}] {g.Name}: {g.Value}");
        File.WriteAllText(Path.Combine(outDir, "bench.md"), md.ToString());
        File.WriteAllLines(Path.Combine(outDir, "bench.log"), logLines);
        worker.Dispose();
        return gates.All(g => g.Pass) ? 0 : 1;
    }

    private static double Percentile(List<double> l, double p)
    {
        if (l.Count == 0) return double.NaN;
        l.Sort();
        double rank = p * (l.Count - 1);
        int lo = (int)Math.Floor(rank), hi = (int)Math.Ceiling(rank);
        return l[lo] + (l[hi] - l[lo]) * (rank - lo);
    }

    private static List<Item> LoadItems(string? path)
    {
        if (path is null)
            foreach (var d in DataSearch.DevParents(AppContext.BaseDirectory))
            {
                var p = Path.Combine(d.FullName, "development", "voice-test", "sentences.txt");
                if (File.Exists(p)) { path = p; break; }
            }
        if (path is null || !File.Exists(path)) throw new FileNotFoundException("sentences.txt not found: pass --sentences <file>");
        return File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Length > 0 && !l.StartsWith("#"))
                   .Select(l => l.Split('\t')).Where(f => f.Length == 3).Select(f => new Item(f[0], f[1], f[2])).ToList();
    }

    /// <summary>Device position every 2 ms on a high-resolution waitable timer: (QPC time, position) records.</summary>
    private sealed class Sampler : IDisposable
    {
        private readonly Func<long> _position;
        private readonly List<(long At, long Pos)> _rec = new(1 << 18);
        private readonly Thread _thread;
        private volatile bool _stop;

        public Sampler(Func<long> position)
        {
            _position = position;
            _thread = new Thread(Loop) { IsBackground = true, Name = "bench-sampler", Priority = ThreadPriority.Highest };
            _thread.Start();
        }

        private void Loop()
        {
            var timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /*HIGH_RESOLUTION*/, 0x1F0003);
            if (timer == IntPtr.Zero) timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x1F0003);
            try
            {
                while (!_stop)
                {
                    long pos = _position();
                    long at = Stopwatch.GetTimestamp();
                    lock (_rec) _rec.Add((at, pos));
                    long due = -20000;   // 2 ms, relative, 100 ns units
                    if (timer != IntPtr.Zero && SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(timer, 100);
                    else Thread.Sleep(1);
                }
            }
            finally { if (timer != IntPtr.Zero) CloseHandle(timer); }
        }

        /// <summary>QPC time of the first record whose position reached <paramref name="sample"/> (0 = never).</summary>
        public long TimeAt(long sample)
        {
            lock (_rec)
            {
                int lo = 0, hi = _rec.Count - 1, found = -1;
                // positions are non-decreasing between device reopenings; search the last run backwards
                for (int i = hi; i >= lo; i--)
                {
                    if (_rec[i].Pos >= sample) found = i;
                    else if (found >= 0) break;
                }
                return found >= 0 ? _rec[found].At : 0;
            }
        }

        public long WaitFor(long sample, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                lock (_rec)
                    if (_rec.Count > 0 && _rec[^1].Pos >= sample)
                    {
                        int i = _rec.Count - 1;
                        while (i > 0 && _rec[i - 1].Pos >= sample) i--;
                        return _rec[i].At;
                    }
                Thread.Sleep(1);
            }
            return 0;
        }

        public void Dispose() { _stop = true; _thread.Join(1000); }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attrs, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr h);
    }
}
