using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.ML.OnnxRuntime;

namespace Predict.Keyboard;

/// <summary>
/// The Piper voices (plans 08a, 08b; D7, D20, D24, D28) behind <see cref="ISynth"/>: espeak-ng phonemes → ONNX Runtime →
/// WASAPI, with word progress from the model's alignment output played against the audio clock. <b>One engine for all
/// voice packs</b>: one <c>piper</c> thread (phonemize, synthesise, unload; a voice loads on its own BelowNormal
/// <c>piper-load</c> thread so a loaded voice keeps speaking meanwhile), one
/// <c>piper-clock</c> thread (every 10 ms while an utterance plays: device position → progress / done; closes the device
/// after 5 s idle; unloads voices no longer needed), one audio sink and one clock. espeak-ng has global state, so two
/// engines would contend on it. Each voice is loaded on demand; <see cref="PiperResidency"/> decides when one is
/// unloaded (the configured voice stays). Every <see cref="ISynth"/> call only posts or flags and returns (they come from
/// the speech thread). Logs carry no text: timings and counts only.
/// </summary>
internal sealed class PiperSynth : IPiperEngine
{
    public const int IdleCloseMs = 5000;
    private readonly List<Voice> _list;
    private readonly Dictionary<string, Voice> _voices;
    private readonly string _espeakData;
    private readonly Action<string>? _log;
    private readonly BlockingCollection<Action> _work = new();
    private readonly BlockingCollection<Action> _loads = new();   // the piper-load worker: one load at a time
    private readonly object _lock = new();
    private readonly PlaybackClock _clock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly LinkedList<(string Key, int Words)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, int Words)>> _lruIndex = new(StringComparer.Ordinal);
    private readonly PiperResidency _residency = new();   // under _lock
    private readonly HashSet<string> _unloadPosted = new(StringComparer.Ordinal);   // under _lock
    private readonly HashSet<string> _rebuildPosted = new(StringComparer.Ordinal);  // under _lock
    private readonly SinkSlot _sinks;                     // under _lock
    private readonly Func<long> _now;
    private Thread? _thread, _clockThread, _loadThread;
    private volatile bool _disposed;
    private Job? _current;           // under _lock
    private volatile string? _active;
    private int _rate, _volume = 100;
    private long _idleSince = -1;     // Environment.TickCount64 when the last utterance ended, -1 = playing / closed
    private volatile string? _deviceError;
    private int _openFailures;

    /// <summary>One installed voice pack and, while loaded, its model.</summary>
    private sealed class Voice
    {
        public readonly PiperVoicePack Pack;
        public volatile PiperState State = PiperState.NotLoaded;
        public PiperVoiceConfig? Cfg;   // set before State = Ready
        public IPiperModel? Model;
        public long? Sid;
        public bool UnknownLogged;
        public Voice(PiperVoicePack pack) => Pack = pack;
        public string Id => Pack.VoiceId;
    }

    /// <param name="packs">the installed packs, in list order (<see cref="PiperVoicePack.Scan"/>).</param>
    /// <param name="now">milliseconds clock for the residency (tests); default <see cref="Environment.TickCount64"/>.</param>
    public PiperSynth(IReadOnlyList<PiperVoicePack> packs, string espeakDataDir, Action<string>? log,
                      Func<int, IAudioSink>? sinkFactory = null, Func<long>? now = null)
    {
        if (packs.Count == 0) throw new ArgumentException("no voice pack", nameof(packs));
        _list = packs.Select(p => new Voice(p)).ToList();
        _voices = _list.ToDictionary(v => v.Id, StringComparer.Ordinal);
        _espeakData = espeakDataDir;
        _log = log;
        _now = now ?? (() => Environment.TickCount64);
        _sinks = new SinkSlot(sinkFactory ?? (rate => new WasapiSink(rate)), s =>
        {
            s.Gain = Math.Clamp(_volume, 0, 100) / 100f;
            s.Error += reason => { _deviceError = reason; _wake.Set(); };
        });
        _active = _list[0].Id;
    }

    public event Action<string, PiperState>? StateChanged;
    public Action<string, Action<SpeechOutcome, string?>, Action<SpeechProgress>?>? DeviceFallback { get; set; }

    public PiperState StateOf(string voiceId) => _voices.TryGetValue(voiceId, out var v) ? v.State : PiperState.Failed;

    /// <summary>The configured voice (set by the router): it stays loaded; the others are unloaded when not needed.</summary>
    public string? Resident
    {
        get { lock (_lock) return _residency.Resident; }
        set
        {
            lock (_lock) _residency.Resident = value;
            _wake.Set();
        }
    }

    /// <summary>Bench diagnostics (<c>--speech-bench</c>): the device sink, and a hook told the timeline index of each
    /// utterance's first chunk when it is queued (piper thread).</summary>
    internal IAudioSink? Sink { get { lock (_lock) return _sinks.Current; } }
#pragma warning disable CS0649   // the bench hooks are set by SpeechBench, which the test project does not compile
    internal Action<long>? FirstChunkQueued;
    /// <summary>Bench: the synthesis call alone (<c>_model.Run</c>) of each utterance's first sentence, ms (piper thread).</summary>
    internal Action<double>? FirstRunMs;
    /// <summary>Bench <c>--variant</c> (plan 08b step 7): noise scales used instead of the voice's.</summary>
    internal static PiperInference? VariantScales;
    /// <summary>Bench <c>--wav</c>: each utterance's audio, with the phoneme alignment of every sentence (piper thread).</summary>
    internal Action<float[], List<(string Phoneme, long Samples)>>? Rendered;
    /// <summary>Bench <c>--word-cache off</c>: isolated words are synthesized each time (to measure the spread).</summary>
    internal static bool WordCacheEnabled = true;
    /// <summary>Bench <c>--phoneme-fix</c>: replaces the cards' <see cref="PiperVoicePack.PhonemeFix"/> (null with
    /// <see cref="OverridePhonemeFix"/> = no correction), to hear a post-correction before writing it in a card.</summary>
    internal static PiperPhonemeFix? PhonemeFixOverride;
    internal static bool OverridePhonemeFix;
    /// <summary>Bench <c>--carrier</c>: replaces the cards' <see cref="PiperVoicePack.WordCarrier"/> (a format, or ""
    /// = no carrier); null = the card's.</summary>
    internal static string? CarrierOverride;
    /// <summary>Bench <c>--wav</c> with a carrier: the whole carrier's audio and alignment, and the cut (piper thread).</summary>
    internal Action<float[], List<(string Phoneme, long Samples)>, PiperCarrier.Cut>? CarrierRendered;
#pragma warning restore CS0649
    /// <summary>The voice model of a pack (tests: a fake); arena = ORT's memory arena (the configured voice).</summary>
    internal Func<string, PiperVoiceConfig, long?, bool, IPiperModel> ModelFactory =
        (path, cfg, sid, arena) => new PiperModel(path, cfg, sid, arena);
    private readonly PiperWordCache _wordCache = new();   // piper thread only
    /// <summary>Longest piece of a sentence given to one ORT run (bench A/B: <c>--max-run</c>).</summary>
    internal static int RunPhonemes = PiperPhonemizer.MaxRunPhonemes;

    /// <summary>The voice packs under <c>&lt;exe&gt;\voices</c> as one engine (null: none installed).</summary>
    /// <param name="nullAudio">test only (<c>--audio null</c>, a guest without a sound device): the timeline runs on a
    /// stopwatch instead of a device, so routing, logs and highlight logic can be checked.</param>
    public static PiperSynth? Scan(string exeDir, Action<string>? log, bool nullAudio = false)
    {
        var packs = PiperVoicePack.Scan(Path.Combine(exeDir, "voices"), log);
        if (packs.Count == 0) { log?.Invoke("speech: no Piper voice installed"); return null; }
        if (nullAudio) log?.Invoke("speech: Piper audio to a null sink (--audio null, test only)");
        return new PiperSynth(packs, Path.Combine(exeDir, "espeak-ng-data"), log, nullAudio ? r => new NullSink(r) : null);
    }

    public IReadOnlyList<VoiceEntry> Voices() =>
        _list.Select(v => new VoiceEntry(v.Id, v.Pack.Id, v.Pack.Culture, v.Pack.Gender, VoiceId.Piper)).ToList();

    /// <summary>Selects the voice of the next utterances (<c>piper:&lt;id&gt;</c>; the router passes installed ids only),
    /// rate and volume.</summary>
    public string Configure(SpeechSettings s)
    {
        if (s.Voice is { } id && _voices.ContainsKey(id)) _active = id;
        _rate = s.Rate;
        _volume = s.Volume;
        lock (_lock) if (_sinks.Current is { } sink) sink.Gain = Math.Clamp(_volume, 0, 100) / 100f;
        var p = _voices[_active!].Pack;
        var speaker = p.Speaker is null ? "" : $", speaker {p.Speaker}";
        return $"{p.VoiceId} ({Path.GetFileNameWithoutExtension(p.Model)}{speaker}, {p.SampleRate} Hz) [{p.Culture}]";
    }

    // ------------------------------------------------------------------ threads
    private void EnsureThreads()
    {
        lock (_lock)
        {
            if (_thread != null) return;
            // synthesis at Normal priority (the app and the bench alike); loads run BelowNormal on their own worker
            _thread = new Thread(WorkLoop) { IsBackground = true, Name = "piper", Priority = ThreadPriority.Normal };
            _thread.Start();
            _loadThread = new Thread(LoadLoop) { IsBackground = true, Name = "piper-load", Priority = ThreadPriority.BelowNormal };
            _loadThread.Start();
            _clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "piper-clock", Priority = ThreadPriority.AboveNormal };
            _clockThread.Start();
        }
    }

    private void WorkLoop()
    {
        try
        {
            foreach (var a in _work.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { _log?.Invoke($"piper error: {ex.GetType().Name}"); }
            }
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>The <c>piper-load</c> worker: not the piper thread, so while a second voice loads (session 5–10 s) the
    /// voice already loaded keeps speaking (make-before-break; the VM run showed an echo queued 6 s behind the load);
    /// one load at a time. espeak calls are serialised by EspeakNative's own lock, as for Rebuild.</summary>
    private void LoadLoop()
    {
        try
        {
            foreach (var a in _loads.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { _log?.Invoke($"piper error: {ex.GetType().Name}"); }
            }
        }
        catch (ObjectDisposedException) { }
    }

    private void PostLoad(Action a)
    {
        try { _loads.Add(a); } catch (InvalidOperationException) { }
    }

    public void StartLoad(string voiceId)
    {
        if (!_voices.TryGetValue(voiceId, out var v)) return;
        lock (_lock)
        {
            if (v.State != PiperState.NotLoaded) return;
            v.State = PiperState.Loading;
        }
        // listeners see the load start too (Réglages status line, tray tooltip); the router ignores a Loading event
        try { StateChanged?.Invoke(v.Id, PiperState.Loading); } catch (Exception) { }
        EnsureThreads();
        PostLoad(() => Load(v));
    }

    private void Post(Action a)
    {
        try { _work.Add(a); } catch (InvalidOperationException) { }
    }

    /// <summary>Synchronous load for the bench and the tests (same steps, same log).</summary>
    public void LoadNow(string voiceId)
    {
        var v = _voices[voiceId];
        lock (_lock)
        {
            if (v.State != PiperState.NotLoaded) return;
            v.State = PiperState.Loading;
        }
        EnsureThreads();
        using var done = new ManualResetEventSlim();
        PostLoad(() => { try { Load(v); } finally { done.Set(); } });
        done.Wait();
    }

    /// <summary>The <c>piper-load</c> worker (BelowNormal).</summary>
    private void Load(Voice v)
    {
        var sw = Stopwatch.StartNew();
        long verify = 0, espeak = 0, session = 0, warm = 0;
        IPiperModel? model = null;
        try
        {
            v.Pack.Verify();
            verify = sw.ElapsedMilliseconds;
            var cfg = v.Pack.Apply(PiperVoiceConfig.Parse(File.ReadAllText(Path.Combine(v.Pack.Dir, v.Pack.Config))));
            var sid = v.Pack.SpeakerId(cfg);   // unknown speaker, or a speaker on a single-speaker model: FormatException
            EspeakNative.Initialize(_espeakData);   // once per process (idempotent)
            EspeakNative.SetVoice(cfg.EspeakVoice);
            espeak = sw.ElapsedMilliseconds - verify;
            // a voice that is not the configured one (a preview) runs without ORT's memory arena: its memory is given
            // back when it is unloaded 30 s later (plan 08b switch gate)
            bool resident;
            lock (_lock) resident = _residency.Resident == v.Id;
            model = ModelFactory(Path.Combine(v.Pack.Dir, v.Pack.Model), cfg, sid, resident);
            session = sw.ElapsedMilliseconds - verify - espeak;
            using (var ro = new RunOptions())
                foreach (var s in Phonemize("Bonjour.", cfg)) model.Run(Ids(v, cfg, s), cfg.NoiseScale, cfg.LengthScale, cfg.NoiseW, ro);
            warm = sw.ElapsedMilliseconds - verify - espeak - session;
            lock (_lock)
            {
                if (_disposed) { model.Dispose(); return; }   // the app quit during the load (own thread)
                v.Cfg = cfg;
                v.Model = model;
                v.Sid = sid;
                v.State = PiperState.Ready;
                _residency.OnLoaded(v.Id, _now(), arena: resident);
            }
            var speaker = v.Pack.Speaker is null ? "" : $", speaker {v.Pack.Speaker}={sid}";
            _log?.Invoke($"speech: {v.Id} ready in {sw.ElapsedMilliseconds} ms (verify {verify}, espeak {espeak}, session {session}, warm-up {warm} ms, 2 threads{speaker})");
        }
        catch (Exception ex)
        {
            model?.Dispose();
            lock (_lock) v.State = PiperState.Failed;
            var msg = ex is InvalidDataException ? ex.Message : ex.Message.Split('\n')[0];
            _log?.Invoke($"speech: {v.Id} unavailable ({ex.GetType().Name}: {msg}), using SAPI");
        }
        try { StateChanged?.Invoke(v.Id, v.State); } catch (Exception) { }
        _wake.Set();   // the clock starts the residency checks
    }

    /// <summary>Piper thread: dispose a voice the residency gave up (re-checked: an utterance may have taken it since).</summary>
    private void Unload(Voice v, string reason)
    {
        IPiperModel? model;
        lock (_lock)
        {
            _unloadPosted.Remove(v.Id);
            if (v.State != PiperState.Ready || !_residency.Due(_now()).Any(d => d.Id == v.Id)) return;
            model = v.Model;
            v.Model = null;
            v.State = PiperState.NotLoaded;
            _residency.OnUnloaded(v.Id);
        }
        model?.Dispose();
        _log?.Invoke($"speech: {v.Id} unloaded ({reason})");
        try { StateChanged?.Invoke(v.Id, PiperState.NotLoaded); } catch (Exception) { }
    }

    /// <summary>A voice loaded for a preview became the configured one: build a session with the memory arena in the
    /// background (the old one keeps speaking meanwhile), then swap it in on the piper thread.</summary>
    private void Rebuild(Voice v)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            IPiperModel? fresh = null;
            var sw = Stopwatch.StartNew();
            try
            {
                var cfg = v.Cfg!;
                fresh = ModelFactory(Path.Combine(v.Pack.Dir, v.Pack.Model), cfg, v.Sid, true);
                // warm-up as at load (EspeakNative serialises its calls with its own lock)
                using var ro = new RunOptions();
                foreach (var s in Phonemize("Bonjour.", cfg))
                    fresh.Run(PiperPhonemizer.Ids(s, cfg.IdMap, out _), cfg.NoiseScale, cfg.LengthScale, cfg.NoiseW, ro);
            }
            catch (Exception ex)
            {
                lock (_lock) _rebuildPosted.Remove(v.Id);
                _log?.Invoke($"speech: {v.Id} session rebuild failed ({ex.GetType().Name}), kept without the arena");
                return;
            }
            Post(() =>
            {
                IPiperModel? old = null;
                lock (_lock)
                {
                    _rebuildPosted.Remove(v.Id);
                    if (v.State == PiperState.Ready && _residency.NeedsArena().Contains(v.Id))
                    {
                        old = v.Model;
                        v.Model = fresh;
                        fresh = null;
                        _residency.OnArena(v.Id);
                    }
                }
                fresh?.Dispose();
                if (old != null)
                {
                    old.Dispose();
                    _log?.Invoke($"speech: {v.Id} session rebuilt with the memory arena in {sw.ElapsedMilliseconds} ms (now the configured voice)");
                }
            });
        });
    }

    // ------------------------------------------------------------------ phonemes
    private static List<List<string>> Phonemize(string text, PiperVoiceConfig cfg) =>
        PiperPhonemizer.Sentences(EspeakNative.Clauses(TextCleanup.ForEspeak(text), cfg.EspeakVoice), cfg.VowelClusters);

    /// <summary>The phonemes a voice speaks: espeak's, then the voice's post-corrections (card <c>phoneme_fix</c>, or the
    /// bench's override). Every utterance of the voice goes through here (words, carrier, sentences, reading).</summary>
    private static List<List<string>> PhonemizeFor(Voice v, PiperVoiceConfig cfg, string text)
    {
        var ph = Phonemize(text, cfg);
        var fix = OverridePhonemeFix ? PhonemeFixOverride : v.Pack.PhonemeFix;
        return fix is null ? ph : fix.Apply(ph, cfg.VowelClusters);
    }

    /// <summary>The voice's word carrier format (card or bench override), null = none.</summary>
    private static string? CarrierFor(Voice v)
    {
        var f = CarrierOverride ?? v.Pack.WordCarrier;
        return PiperCarrier.IsValidFormat(f) ? f : null;
    }

    private long[] Ids(Voice v, PiperVoiceConfig cfg, IReadOnlyList<string> phonemes)
    {
        var ids = PiperPhonemizer.Ids(phonemes, cfg.IdMap, out int unknown);
        if (unknown > 0 && !v.UnknownLogged) { v.UnknownLogged = true; _log?.Invoke($"piper: {v.Id}: {unknown} unknown phoneme(s) skipped"); }
        return ids;
    }

    /// <summary>Bench only: the phonemes espeak gives for <paramref name="text"/> with a loaded voice's config
    /// (sentences joined by « | »).</summary>
    internal string PhonemesOf(string voiceId, string text)
    {
        var v = _voices[voiceId];
        var cfg = v.Cfg ?? throw new InvalidOperationException("voice not loaded");
        var ph = PhonemizeFor(v, cfg, text);
        return string.Join(" | ", ph.Select(s => string.Concat(s)));
    }

    /// <summary>espeak words of a token phonemized alone (numbers, symbols), LRU of 256 per espeak voice. Without the
    /// voice's phoneme corrections: they never change the espeak word count (<see cref="PiperPhonemeFix"/>).</summary>
    private int IsolatedWords(string token, PiperVoiceConfig cfg)
    {
        var key = cfg.EspeakVoice + "\u0001" + token;
        if (_lruIndex.TryGetValue(key, out var node)) { _lru.Remove(node); _lru.AddFirst(node); return node.Value.Words; }
        int n = PiperWordMap.WordCount(Phonemize(token, cfg));
        _lruIndex[key] = _lru.AddFirst((key, n));
        if (_lru.Count > 256) { _lruIndex.Remove(_lru.Last!.Value.Key); _lru.RemoveLast(); }
        return n;
    }

    // ------------------------------------------------------------------ utterances
    private sealed class Job
    {
        public readonly Voice Voice;   // captured at SpeakAsync: a later Configure does not change it
        public readonly string Text;
        public readonly Action<SpeechOutcome, string?> Done;
        public readonly Action<SpeechProgress>? Progress;
        public readonly bool Word;     // an isolated word (word echo, inserted word, tile): word scales and cache
        public readonly Stopwatch Since = Stopwatch.StartNew();
        public readonly RunOptions Run = new();
        public bool Cancelled;     // under PiperSynth._lock
        public bool Begun;         // the clock owns done
        public long Origin;        // timeline index of the first chunk
        private int _finished;
        private readonly Action<Job> _ended;

        public Job(Voice voice, string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress, bool word, Action<Job> ended)
        {
            Voice = voice; Text = text; Done = done; Progress = progress; Word = word; _ended = ended;
        }

        private bool _runReleased;   // under PiperSynth._lock

        /// <summary>Terminate a running inference (cancel); a no-op once the run options are released: touching a
        /// disposed native handle would crash the process.</summary>
        public void Terminate() { if (!_runReleased) Run.Terminate = true; }

        public void ReleaseRun() { if (!_runReleased) { _runReleased = true; Run.Dispose(); } }

        public void Finish(SpeechOutcome o, string? e)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            _ended(this);
            Done(o, e);
        }
    }

    /// <summary>Speak with the voice <see cref="Configure"/> selected.</summary>
    public void SpeakAsync(string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null)
    {
        if (!Speak(_active!, text, done, progress)) done(SpeechOutcome.Error, "piper not ready");
    }

    /// <summary>Speak with <paramref name="voiceId"/>; false (done not called) when it is not Ready (unloaded meanwhile).</summary>
    /// <param name="word">an isolated word (word echo, inserted word, tile): the card's <c>inference_word</c> scales and
    /// the <see cref="PiperWordCache"/>.</param>
    public bool Speak(string voiceId, string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null, bool word = false)
    {
        if (!_voices.TryGetValue(voiceId, out var v)) return false;
        Job? job = null;
        lock (_lock)
        {
            if (v.State == PiperState.Ready)
            {
                job = new Job(v, text, done, progress, word, JobEnded);
                _residency.Begin(v.Id, _now());   // keeps the voice loaded until the job ends (Finish)
                // current from the moment it is accepted: a cancel that arrives before the piper thread picks it up still
                // reaches it (the worker sends one utterance at a time)
                _current = job;
            }
        }
        if (job is null) return false;
        Post(() => Process(job));
        return true;
    }

    private void JobEnded(Job job)
    {
        lock (_lock) _residency.End(job.Voice.Id, _now());
        _wake.Set();
    }

    private void Process(Job job)
    {
        lock (_lock) if (job.Cancelled) { job.ReleaseRun(); return; }
        var v = job.Voice;
        var cfg = v.Cfg!;
        var model = v.Model!;
        IAudioSink sink;
        // one sink for all voices: recreated only for another sample rate (the previous utterance has ended: the worker
        // sends one at a time)
        lock (_lock) sink = _sinks.Get(cfg.SampleRate);
        try
        {
            var sentenceScales = VariantScales ?? new PiperInference(cfg.NoiseScale, cfg.LengthScale, cfg.NoiseW);
            var scales = sentenceScales;
            if (job.Word && VariantScales is null && v.Pack.InferenceWord is { } w) scales = scales with { NoiseScale = w.NoiseScale, NoiseW = w.NoiseW };
            float length = PiperVoiceConfig.LengthScaleFor(scales.LengthScale, _rate);
            // an isolated word without progress, with a carrier (pierre, 0.7.2): said inside the carrier sentence with
            // the voice's own scales, then cut out; the word scales are only the fallback's. A word on the card's
            // word_carrier_except list (siwis: « eu », « les ») is said alone
            var carrier = job.Word && job.Progress is null && PiperCarrier.Applies(job.Text) && !v.Pack.SaysAlone(job.Text)
                ? CarrierFor(v) : null;
            // an isolated word without progress: the same word always sounds the same (cache), and comes faster. With a
            // carrier the key is the carrier's (its scales and a marker); a fallback rendering is kept under it too, so a
            // word whose cut failed once is not synthesised twice again
            var cacheKey = job.Word && job.Progress is null && WordCacheEnabled
                ? carrier != null
                    ? PiperWordCache.Key(v.Id + PiperWordCache.CarrierMark, job.Text, sentenceScales.NoiseScale,
                                         PiperVoiceConfig.LengthScaleFor(sentenceScales.LengthScale, _rate), sentenceScales.NoiseW)
                    : PiperWordCache.Key(v.Id, job.Text, scales.NoiseScale, length, scales.NoiseW)
                : null;
            if (cacheKey != null && _wordCache.TryGet(cacheKey, out var cached))
            {
                if (!Open(sink, job)) return;
                lock (_lock)
                {
                    if (job.Cancelled) return;
                    _clock.Begin(job.Finish, job.Progress); job.Begun = true; _idleSince = -1;
                    long played = sink.PlayedSourceSamples;
                    long start = sink.Enqueue(cached, cached.Length);
                    job.Origin = start;
                    FirstChunkQueued?.Invoke(start);
                    _clock.SetEnd(start + cached.Length);
                    _log?.Invoke($"speech: piper first audio {job.Since.ElapsedMilliseconds + Math.Max(0, start - played) * 1000 / cfg.SampleRate} ms (word cache hit{(carrier != null ? ", carrier" : "")})");
                }
                Rendered?.Invoke(cached, new List<(string, long)>());
                _wake.Set();
                return;
            }
            if (carrier != null && SpeakCarrier(job, v, cfg, model, sink, sentenceScales, carrier, cacheKey) != CarrierResult.Fallback)
                return;
            // (fallback: the word alone with the word scales, cached under the carrier key, see above)
            var phonemes = PhonemizeFor(v, cfg, job.Text);
            var sentences = PiperPhonemizer.SplitLong(phonemes, RunPhonemes);
            if (sentences.Count == 0) { job.Finish(SpeechOutcome.Done, null); return; }
            PiperWordMap.Plan? plan = null;
            if (job.Progress != null)
            {
                plan = PiperWordMap.Assign(PiperWordMap.Units(job.Text, t => IsolatedWords(t, cfg)), sentences);
                if (plan.Fallback) _log?.Invoke($"piper: word map fallback ({plan.EspeakWords} espeak words, {plan.UnitCount} units)");
            }
            if (!Open(sink, job)) return;
            // a word: its phoneme count in the log (not the text), so two echoes of one word can be compared
            var wordInfo = job.Word ? FormattableString.Invariant($", word: {sentences.Sum(x => x.Count)} phonemes, ns {scales.NoiseScale:0.###} nw {scales.NoiseW:0.###}") : "";
            long synthMs = 0, end = 0;
            var rendered = Rendered != null || cacheKey != null ? new List<float>() : null;
            var renderedAlign = Rendered != null ? new List<(string, long)>() : null;
            for (int k = 0; k < sentences.Count; k++)
            {
                lock (_lock) if (job.Cancelled) return;
                var ids = Ids(v, cfg, sentences[k]);
                var t = Stopwatch.StartNew();
                float[] audio; long[] perId;
                try { (audio, perId) = model.Run(ids, scales.NoiseScale, length, scales.NoiseW, job.Run); }
                catch (OnnxRuntimeException) when (Volatile.Read(ref job.Cancelled)) { return; }
                synthMs += t.ElapsedMilliseconds;
                if (k == 0) FirstRunMs?.Invoke(t.Elapsed.TotalMilliseconds);
                List<ClockMark>? marks = null;
                var groups = PiperPhonemizer.Groups(sentences[k], cfg.IdMap, ids);
                List<(string, long)>? aligned = null;
                if (groups != null && perId.Length == ids.Length)
                {
                    aligned = new List<(string, long)>(groups.Count);
                    int i = 0;
                    foreach (var (p, n) in groups) { long sum = 0; for (int j = 0; j < n; j++) sum += perId[i + j]; i += n; aligned.Add((p, sum)); }
                }
                if (plan != null && aligned != null)
                    marks = PiperWordMap.Marks(plan, k, aligned).Select(m => new ClockMark(m.Sample, m.CharPos, m.CharLen, 0)).ToList();
                if (rendered != null) { rendered.AddRange(audio); if (aligned != null) renderedAlign?.AddRange(aligned); }
                lock (_lock)
                {
                    if (job.Cancelled) return;
                    if (!job.Begun) { _clock.Begin(job.Finish, job.Progress); job.Begun = true; _idleSince = -1; }
                    long played = sink.PlayedSourceSamples;
                    long start = sink.Enqueue(audio, audio.Length);
                    if (k == 0) job.Origin = start;
                    // AudioMs: from the utterance's first sample (diagnostics; the highlight follows the event's arrival)
                    if (marks != null)
                        _clock.AddMarks(marks.Select(m => m with { Sample = start + m.Sample, AudioMs = (start + m.Sample - job.Origin) * 1000.0 / cfg.SampleRate }));
                    end = start + audio.Length;
                    if (k == 0) FirstChunkQueued?.Invoke(start);
                    if (k == 0)
                        _log?.Invoke($"speech: piper first audio {job.Since.ElapsedMilliseconds + Math.Max(0, start - played) * 1000 / cfg.SampleRate} ms (synth {synthMs} ms, {sentences.Count} sentences{wordInfo})");
                }
                _wake.Set();
            }
            if (rendered != null)
            {
                var all = rendered.ToArray();
                // cached only when spoken to the end of synthesis (a cancel returned above)
                if (cacheKey != null) _wordCache.Add(cacheKey, all);
                Rendered?.Invoke(all, renderedAlign ?? new List<(string, long)>());
            }
            lock (_lock) if (!job.Cancelled) _clock.SetEnd(end);
            _wake.Set();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"piper: utterance failed ({ex.GetType().Name})");
            lock (_lock) { if (job.Begun) _clock.Fail(ex.GetType().Name); else job.Finish(SpeechOutcome.Error, ex.GetType().Name); }
        }
        finally
        {
            lock (_lock)
            {
                if (_current == job && !job.Begun) _current = null;
                job.ReleaseRun();
            }
        }
    }

    private enum CarrierResult { Spoken, Stopped, Fallback }

    /// <summary>Piper thread: an isolated word said inside the voice's carrier sentence and cut out
    /// (<see cref="PiperCarrier"/>), enqueued in one piece and cached. <see cref="CarrierResult.Fallback"/> (logged
    /// with the reason, no text) when the carrier is not one sentence or the cut fails: the caller synthesises the word
    /// alone. <see cref="CarrierResult.Stopped"/>: cancelled, or the device fell back to SAPI.</summary>
    private CarrierResult SpeakCarrier(Job job, Voice v, PiperVoiceConfig cfg, IPiperModel model, IAudioSink sink,
                                       PiperInference scales, string format, string? cacheKey)
    {
        var sentences = PhonemizeFor(v, cfg, PiperCarrier.Text(format, job.Text));
        if (sentences.Count != 1 || sentences[0].Count > RunPhonemes)
        {
            _log?.Invoke($"piper: word carrier fallback ({sentences.Count} sentences)");
            return CarrierResult.Fallback;
        }
        if (!Open(sink, job)) return CarrierResult.Stopped;
        float length = PiperVoiceConfig.LengthScaleFor(scales.LengthScale, _rate);
        lock (_lock) if (job.Cancelled) return CarrierResult.Stopped;
        var ids = Ids(v, cfg, sentences[0]);
        var t = Stopwatch.StartNew();
        float[] audio; long[] perId;
        try { (audio, perId) = model.Run(ids, scales.NoiseScale, length, scales.NoiseW, job.Run); }
        catch (OnnxRuntimeException) when (Volatile.Read(ref job.Cancelled)) { return CarrierResult.Stopped; }
        long synthMs = t.ElapsedMilliseconds;
        FirstRunMs?.Invoke(t.Elapsed.TotalMilliseconds);
        var groups = PiperPhonemizer.Groups(sentences[0], cfg.IdMap, ids);
        List<(string Phoneme, long Samples)>? aligned = null;
        if (groups != null && perId.Length == ids.Length)
        {
            aligned = new List<(string, long)>(groups.Count);
            int i = 0;
            foreach (var (p, n) in groups) { long sum = 0; for (int j = 0; j < n; j++) sum += perId[i + j]; i += n; aligned.Add((p, sum)); }
        }
        var cut = PiperCarrier.Extract(audio, aligned, PiperCarrier.SeparatorsBefore(format), cfg.SampleRate, out var why);
        if (cut is null)
        {
            _log?.Invoke($"piper: word carrier fallback ({why})");
            return CarrierResult.Fallback;
        }
        lock (_lock)
        {
            if (job.Cancelled) return CarrierResult.Stopped;
            _clock.Begin(job.Finish, job.Progress); job.Begun = true; _idleSince = -1;
            long played = sink.PlayedSourceSamples;
            long start = sink.Enqueue(cut.Audio, cut.Audio.Length);
            job.Origin = start;
            FirstChunkQueued?.Invoke(start);
            _clock.SetEnd(start + cut.Audio.Length);
            _log?.Invoke(FormattableString.Invariant(
                $"speech: piper first audio {job.Since.ElapsedMilliseconds + Math.Max(0, start - played) * 1000 / cfg.SampleRate} ms (synth {synthMs} ms, word carrier: {ids.Length} ids, cut at {cut.CutStart * 1000L / cfg.SampleRate} of {audio.Length * 1000L / cfg.SampleRate} ms, ns {scales.NoiseScale:0.###} nw {scales.NoiseW:0.###})"));
        }
        if (cacheKey != null) _wordCache.Add(cacheKey, cut.Audio);
        CarrierRendered?.Invoke(audio, aligned!, cut);
        Rendered?.Invoke(cut.Audio, cut.WordAlign);
        _wake.Set();
        return CarrierResult.Spoken;
    }

    /// <summary>Open the device for <paramref name="job"/>; after two failed opens in a row the utterance goes to the
    /// <see cref="DeviceFallback"/> (SAPI) and false is returned.</summary>
    private bool Open(IAudioSink sink, Job job)
    {
        bool wasOpen = sink.IsOpen;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                sink.EnsureOpen();
                if (!wasOpen && _openFailures > 0) _log?.Invoke("speech: audio device reopened (after a failure)");
                _openFailures = 0;
                return true;
            }
            catch (Exception ex)
            {
                _openFailures++;
                _log?.Invoke($"speech: audio device open failed ({ex.GetType().Name})");
            }
        }
        lock (_lock) { if (job.Cancelled) return false; job.Cancelled = true; }
        if (DeviceFallback is { } fb) fb(job.Text, job.Finish, job.Progress);
        else job.Finish(SpeechOutcome.Error, "audio device unavailable");
        return false;
    }

    public void CancelAll()
    {
        Job? job;
        lock (_lock)
        {
            job = _current;
            if (job is null || job.Cancelled) return;
            job.Cancelled = true;
            try { job.Terminate(); } catch (Exception) { }
            _sinks.Current?.Flush();
            if (job.Begun) _clock.Cancel();
        }
        job.Finish(SpeechOutcome.Cancelled, null);   // no-op when the clock already said it
        _wake.Set();
    }

    // ------------------------------------------------------------------ clock
    private void ClockLoop()
    {
        while (!_disposed)
        {
            if (_deviceError is { } why)
            {
                _deviceError = null;
                lock (_lock)
                {
                    if (_clock.Active) _clock.Fail("audio device: " + why);
                    _sinks.Current?.Close();
                    if (_current is { Begun: true }) _current = null;   // a job still synthesising stays cancellable
                }
                _log?.Invoke($"speech: audio device reopened ({why})");
                continue;
            }
            bool active, candidates;
            lock (_lock)
            {
                active = _clock.Active && _clock.Tick(_sinks.Current?.PlayedSourceSamples ?? 0);
                if (!active && _current is { Begun: true }) { _current = null; _idleSince = Environment.TickCount64; }
                if (!active && _idleSince >= 0 && Environment.TickCount64 - _idleSince >= IdleCloseMs && _current is null)
                {
                    _sinks.Current?.Close();
                    _idleSince = -1;
                }
                // residency: unload on the piper thread (it owns the models); posted once per voice
                foreach (var (id, reason) in _residency.Due(_now()))
                    if (_unloadPosted.Add(id)) { var v = _voices[id]; Post(() => Unload(v, reason)); }
                foreach (var id in _residency.NeedsArena())
                    if (_rebuildPosted.Add(id)) Rebuild(_voices[id]);
                candidates = _residency.AnyCandidate;
            }
            if (active) _wake.WaitOne(10);
            else _wake.WaitOne(_idleSince >= 0 || candidates ? 1000 : Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        CancelAll();
        _disposed = true;
        _work.CompleteAdding();
        _loads.CompleteAdding();
        _wake.Set();
        bool stopped = _thread?.Join(2000) ?? true;
        bool clockStopped = _clockThread?.Join(1000) ?? true;
        if (!stopped || !clockStopped) return;   // a thread still runs (a long inference): leave its objects to the process exit
        lock (_lock) _sinks.Dispose();
        foreach (var v in _list) v.Model?.Dispose();
    }
}
