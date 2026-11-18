using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 08b steps 3–4: several voice packs (order, speaker, inference override), the speaker id of a
/// multi-speaker model, which voices stay loaded (residency, on a fake clock), and the one shared audio sink.</summary>
public class PiperVoicesTests
{
    private const string UpmcConfig = @"{""audio"":{""sample_rate"":22050},""espeak"":{""voice"":""fr""},
        ""inference"":{""noise_scale"":0.667,""length_scale"":1,""noise_w"":0.8},""num_speakers"":2,
        ""speaker_id_map"":{""jessica"":0,""pierre"":1},""phoneme_id_map"":{""_"":[0],""^"":[1],""$"":[2]}}";
    private const string SiwisConfig = @"{""audio"":{""sample_rate"":22050},""espeak"":{""voice"":""fr""},
        ""inference"":{""noise_scale"":0.5,""length_scale"":1.0,""noise_w"":0.333},""num_speakers"":1,
        ""phoneme_id_map"":{""_"":[0],""^"":[1],""$"":[2]}}";

    private static string Card(string id, string extra = "") =>
        $@"{{""id"":""{id}"",""name"":""N"",""gender"":""male"",""culture"":""fr-FR"",""sampleRate"":22050,""model"":""v.onnx"",
            ""config"":""v.onnx.json"",""sizes"":{{""v.onnx"":3,""v.onnx.json"":2}},""sha256"":{{""v.onnx"":""{new string('0', 64)}"",""v.onnx.json"":""{new string('0', 64)}""}}{extra}}}";

    [Fact]
    public void Card_Parses_Speaker_Order_Licence_Public_And_Inference()
    {
        var p = PiperVoicePack.Parse("x", Card("pierre", @",""speaker"":""pierre"",""order"":2,""licence"":""CC BY-SA 4.0"",""public"":false,
            ""inference"":{""noise_scale"":0.5,""length_scale"":1.1,""noise_w"":0.333}"));
        Assert.Equal("pierre", p.Speaker);
        Assert.Equal(2, p.Order);
        Assert.Equal("CC BY-SA 4.0", p.Licence);
        Assert.False(p.Public);
        Assert.Equal(new PiperInference(0.5f, 1.1f, 0.333f), p.Inference);
        var cfg = p.Apply(PiperVoiceConfig.Parse(UpmcConfig));
        Assert.Equal((0.5f, 1.1f, 0.333f), (cfg.NoiseScale, cfg.LengthScale, cfg.NoiseW));
    }

    [Fact]
    public void Card_Without_The_New_Fields_Keeps_The_08a_Meaning()
    {
        var p = PiperVoicePack.Parse("x", Card("siwis", @",""speaker"":null"));
        Assert.Null(p.Speaker);
        Assert.Equal(PiperVoicePack.DefaultOrder, p.Order);
        Assert.True(p.Public);
        Assert.Null(p.Inference);
        var cfg = PiperVoiceConfig.Parse(SiwisConfig);
        Assert.Same(cfg, p.Apply(cfg));
        Assert.Null(p.SpeakerId(cfg));
    }

    [Fact]
    public void Bad_Inference_Override_Is_Rejected()
    {
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card("p", @",""inference"":{""noise_scale"":0.5,""length_scale"":0}")));
    }

    [Fact]
    public void Pierre_Is_Speaker_1_Of_Upmc_And_Wrong_Speakers_Fail_The_Load()
    {
        var upmc = PiperVoiceConfig.Parse(UpmcConfig);
        Assert.Equal(2, upmc.NumSpeakers);
        Assert.Equal(new Dictionary<string, long> { ["jessica"] = 0, ["pierre"] = 1 }, upmc.SpeakerIdMap);
        Assert.Equal(1L, PiperVoicePack.Parse("x", Card("pierre", @",""speaker"":""pierre""")).SpeakerId(upmc));
        // unknown speaker; multi-speaker model without a speaker; a speaker on a single-speaker model
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card("p", @",""speaker"":""marc""")).SpeakerId(upmc));
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card("p")).SpeakerId(upmc));
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card("p", @",""speaker"":""pierre""")).SpeakerId(PiperVoiceConfig.Parse(SiwisConfig)));
    }

    [Fact]
    public void Scan_Lists_Packs_By_Order_Then_Id()
    {
        var root = Path.Combine(Path.GetTempPath(), "kbvoices-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Pack(string id, string extra)
            {
                var dir = Path.Combine(root, id);
                Directory.CreateDirectory(dir);
                var model = new byte[] { 1, 2, 3 };
                File.WriteAllBytes(Path.Combine(dir, "v.onnx"), model);
                File.WriteAllText(Path.Combine(dir, "v.onnx.json"), "{}");
                string H(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
                File.WriteAllText(Path.Combine(dir, "voice.json"),
                    $@"{{""id"":""{id}"",""culture"":""fr-FR"",""gender"":""x"",""model"":""v.onnx"",""config"":""v.onnx.json"",
                        ""sizes"":{{""v.onnx"":3,""v.onnx.json"":2}},""sha256"":{{""v.onnx"":""{H(model)}"",""v.onnx.json"":""{H(Encoding.UTF8.GetBytes("{}"))}""}}{extra}}}");
            }
            Pack("pierre", @",""order"":2,""speaker"":""pierre""");   // alphabetically first
            Pack("siwis", @",""order"":1");
            Pack("zzz", "");                                          // no order: last
            Assert.Equal(new[] { "piper:siwis", "piper:pierre", "piper:zzz" }, PiperVoicePack.Scan(root, null).Select(p => p.VoiceId));
        }
        finally { Directory.Delete(root, true); }
    }

    // ---------------------------------------------------------------- residency (fake clock)

    [Fact]
    public void Configured_Voice_Stays_And_A_Preview_Voice_Is_Unloaded_30_s_After_Its_Last_Utterance()
    {
        var r = new PiperResidency { Resident = "piper:siwis" };
        r.OnLoaded("piper:siwis", 0);
        r.OnLoaded("piper:pierre", 1000);
        r.Begin("piper:pierre", 1000);            // the preview plays
        Assert.Empty(r.Due(100_000));               // busy: never unloaded
        r.End("piper:pierre", 3000);
        r.Begin("piper:siwis", 4000);               // the next echo, by siwis: no reload, pierre kept for now
        r.End("piper:siwis", 4500);
        Assert.True(r.AnyCandidate);
        Assert.Empty(r.Due(3000 + PiperResidency.UnloadIdleMs - 1));
        Assert.Equal(new[] { ("piper:pierre", PiperResidency.ReasonIdle) }, r.Due(3000 + PiperResidency.UnloadIdleMs));
        r.OnUnloaded("piper:pierre");
        Assert.False(r.AnyCandidate);
        Assert.Empty(r.Due(long.MaxValue / 2));     // the configured voice is never unloaded
    }

    [Fact]
    public void A_Voice_That_Stops_Being_Configured_Is_Unloaded_Once_Idle()
    {
        var r = new PiperResidency { Resident = "piper:siwis" };
        r.OnLoaded("piper:siwis", 0);
        r.OnLoaded("piper:pierre", 0, arena: false);   // preloaded on selection, Ready before Enregistrer
        r.Begin("piper:siwis", 10);
        r.Resident = "piper:pierre";                // Enregistrer with pierre while siwis speaks
        Assert.Empty(r.Due(20));                    // still speaking
        r.End("piper:siwis", 30);
        Assert.Equal(new[] { ("piper:siwis", "replaced by piper:pierre") }, r.Due(31));
        r.Resident = "piper:siwis";                 // changed back before the unload: siwis kept, pierre replaced
        Assert.Equal(new[] { ("piper:pierre", "replaced by piper:siwis") }, r.Due(32));
    }

    [Fact]
    public void Make_Before_Break_The_Old_Voice_Stays_Until_The_New_One_Is_Loaded()
    {
        var r = new PiperResidency { Resident = "piper:siwis" };
        r.OnLoaded("piper:siwis", 0);
        r.Resident = "piper:pierre";                // Enregistrer with pierre, not loaded yet
        Assert.Empty(r.Due(1_000_000));             // siwis speaks for pierre meanwhile
        Assert.False(r.AnyCandidate);               // nothing to poll: the end of pierre's load wakes the clock
        r.OnLoaded("piper:pierre", 1_000_000);      // pierre Ready
        Assert.True(r.AnyCandidate);
        Assert.Equal(new[] { ("piper:siwis", "replaced by piper:pierre") }, r.Due(1_000_001));
        Assert.DoesNotContain(r.Due(1_000_001), d => d.Id == "piper:pierre");
    }

    [Fact]
    public void Make_Before_Break_A_Failed_Load_Keeps_The_Old_Voice()
    {
        var r = new PiperResidency { Resident = "piper:siwis" };
        r.OnLoaded("piper:siwis", 0);
        r.Resident = "piper:pierre";                // pierre's load then fails: it is never OnLoaded
        Assert.Empty(r.Due(long.MaxValue / 2));
        Assert.Equal(new[] { "piper:siwis" }, r.Loaded);
    }

    [Fact]
    public void A_Voice_Preloaded_On_Selection_And_Not_Saved_Is_Unloaded_30_s_After_Its_Load()
    {
        var r = new PiperResidency { Resident = "piper:siwis" };
        r.OnLoaded("piper:siwis", 0);
        r.OnLoaded("piper:pierre", 5000, arena: false);   // selected in Réglages, then Annuler: never spoken
        Assert.True(r.AnyCandidate);
        Assert.Empty(r.Due(5000 + PiperResidency.UnloadIdleMs - 1));
        Assert.Equal(new[] { ("piper:pierre", PiperResidency.ReasonIdle) }, r.Due(5000 + PiperResidency.UnloadIdleMs));
    }

    [Fact]
    public void A_Sapi_Voice_Configured_Unloads_Every_Piper_Voice_Once_Idle()
    {
        var r = new PiperResidency { Resident = "piper:siwis" };
        r.OnLoaded("piper:siwis", 0);
        r.Resident = null;
        Assert.Equal(new[] { ("piper:siwis", PiperResidency.ReasonChanged) }, r.Due(1));
    }

    [Fact]
    public void A_Preview_Voice_That_Becomes_Configured_Gets_Its_Arena_Back_Once_Idle()
    {
        var r = new PiperResidency { Resident = "piper:siwis" };
        r.OnLoaded("piper:siwis", 0);
        r.OnLoaded("piper:pierre", 0, arena: false);   // loaded for « Écouter »
        Assert.Empty(r.NeedsArena());
        r.Begin("piper:pierre", 1);
        r.Resident = "piper:pierre";                    // Enregistrer while the preview speaks
        Assert.Empty(r.NeedsArena());                   // busy: not now
        r.End("piper:pierre", 2);
        Assert.Equal(new[] { "piper:pierre" }, r.NeedsArena());
        r.OnArena("piper:pierre");
        Assert.Empty(r.NeedsArena());
        Assert.DoesNotContain(r.Due(1_000_000), d => d.Id == "piper:pierre");   // configured: never unloaded
    }

    // ---------------------------------------------------------------- one sink for every voice

    private sealed class FakeSink : IAudioSink
    {
        public readonly int Rate;
        public bool Disposed;
        public FakeSink(int rate) => Rate = rate;
        public event Action<string>? Error { add { } remove { } }
        public void EnsureOpen() { }
        public void Close() { }
        public bool IsOpen => false;
        public long Enqueue(float[] samples, int count) => 0;
        public void Flush() { }
        public long PlayedSourceSamples => 0;
        public long QueuedEndSample => 0;
        public float Gain { get; set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void Sink_Is_Shared_And_Recreated_Only_For_Another_Sample_Rate()
    {
        var made = new List<FakeSink>();
        int setups = 0;
        var slot = new SinkSlot(r => { var s = new FakeSink(r); made.Add(s); return s; }, s => { setups++; s.Gain = 0.3f; });
        var a = slot.Get(22050);
        Assert.Same(a, slot.Get(22050));            // siwis then pierre: both 22.05 kHz, one sink
        Assert.Single(made);
        Assert.Equal(0.3f, a.Gain);
        var b = slot.Get(16000);
        Assert.NotSame(a, b);
        Assert.True(made[0].Disposed);
        Assert.Equal(2, setups);
        slot.Dispose();
        Assert.True(made[1].Disposed);
    }
    // ---------------------------------------------------------------- isolated words (owner, 0.6.0: « très » varied)

    [Fact]
    public void Card_Parses_The_Word_Noise_Scales()
    {
        var p = PiperVoicePack.Parse("x", Card("pierre", @",""speaker"":""pierre"",""inference_word"":{""noise_scale"":0.333,""noise_w"":0.2}"));
        Assert.Equal(new PiperWordInference(0.333f, 0.2f), p.InferenceWord);
        Assert.Null(PiperVoicePack.Parse("x", Card("siwis")).InferenceWord);
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card("p", @",""inference_word"":{""noise_scale"":0.3}")));
    }

    [Fact]
    public void Word_Cache_Key_Normalises_Space_And_Composition_But_Keeps_Case_Voice_And_Scales()
    {
        string? K(string t, string v = "piper:pierre", float ls = 1.1f) => PiperWordCache.Key(v, t, 0.333f, ls, 0.2f);
        Assert.Equal(K("très"), K("  très "));          // NFC, trimmed
        Assert.Equal(K("les amis"), K("les 	 amis"));
        Assert.NotEqual(K("très"), K("Très"));
        Assert.NotEqual(K("chat"), K("chat", "piper:siwis"));
        Assert.NotEqual(K("chat"), K("chat", ls: 1.2f));              // another rate
        Assert.Null(K("   "));
        Assert.Null(K(new string('a', PiperWordCache.MaxTextLength + 1)));
    }

    [Fact]
    public void Word_Cache_Returns_The_Same_Samples_And_Is_Bounded()
    {
        var c = new PiperWordCache(maxEntries: 3, maxBytes: 40);
        var a = new float[] { 1, 2, 3 };
        c.Add("a", a);
        Assert.True(c.TryGet("a", out var got));
        Assert.Same(a, got);
        c.Add("b", new float[2]); c.Add("c", new float[2]);
        Assert.True(c.TryGet("a", out _));                      // a is now the most recent
        c.Add("d", new float[2]);                                // 4 entries > 3: b (least recent) goes
        Assert.False(c.TryGet("b", out _));
        Assert.Equal(3, c.Count);
        c.Add("e", new float[8]);                                // 32 bytes: the byte bound drops the oldest ones
        Assert.True(c.Bytes <= 40);
        Assert.True(c.TryGet("e", out _));
        c.Add("huge", new float[11]);                            // larger than the whole cache: not kept
        Assert.False(c.TryGet("huge", out _));
    }
}
