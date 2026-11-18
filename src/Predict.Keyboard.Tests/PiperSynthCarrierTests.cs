#if HAVE_ESPEAK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.7.2: the engine's word path end to end (real espeak-ng, the pierre id map of the golden fixture, a fake
/// model, null audio): a word of pierre is said in its carrier with the voice's scales and cut out, the second echo is
/// a cache hit, a failed cut falls back to the word alone with the word scales (and is cached too), the <c>iʲ → i</c>
/// correction reaches every utterance of pierre, and a voice without it (siwis) is unchanged. 2026-11-16: siwis has the
/// carrier too, with « eu » and « les » said alone (card <c>word_carrier_except</c>).</summary>
public sealed class PiperSynthCarrierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "predict-carrier-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();
    private readonly FakeModel _model = new();
    private readonly PiperSynth _synth;
    private readonly int _palatalId;

    private sealed class FakeModel : IPiperModel
    {
        public readonly List<(long[] Ids, float Ns, float Nw)> Runs = new();
        public volatile bool BreakAlignment;
        public (float[] Audio, long[] SamplesPerId) Run(long[] ids, float noiseScale, float lengthScale, float noiseW, RunOptions run)
        {
            lock (Runs) Runs.Add((ids, noiseScale, noiseW));
            var per = Enumerable.Repeat(256L, BreakAlignment ? ids.Length - 1 : ids.Length).ToArray();
            var audio = new float[ids.Length * 256];
            for (int i = 0; i < audio.Length; i++) audio[i] = (float)(0.3 * Math.Sin(i * 0.05));
            return (audio, per);
        }
        public void Dispose() { }
        public int Count { get { lock (Runs) return Runs.Count; } }
        public (long[] Ids, float Ns, float Nw) Last { get { lock (Runs) return Runs[^1]; } }
    }

    public PiperSynthCarrierTests()
    {
        var meta = PiperGolden.For("pierre").MetaOf;
        _palatalId = ((JsonArray)meta["id_map"]!["ʲ"]!)[0]!.GetValue<int>();
        var cfg = new JsonObject
        {
            ["audio"] = new JsonObject { ["sample_rate"] = 22050 },
            ["espeak"] = new JsonObject { ["voice"] = "fr" },
            ["inference"] = new JsonObject { ["noise_scale"] = 0.667, ["length_scale"] = 1, ["noise_w"] = 0.8 },
            ["num_speakers"] = 1,
            ["phoneme_id_map"] = JsonNode.Parse(meta["id_map"]!.ToJsonString()),
            ["vowel_clusters"] = JsonNode.Parse(meta["vowel_clusters"]!.ToJsonString()),
        }.ToJsonString();
        var pierre = Pack("pierre", cfg, @",""inference"":{""noise_scale"":0.5,""length_scale"":1.1,""noise_w"":0.333},
            ""inference_word"":{""noise_scale"":0.333,""noise_w"":0.2},""word_carrier"":""Le mot : {0}."",""phoneme_fix"":[[""iʲ"",""i""]]");
        var siwis = Pack("siwis", cfg, @",""inference"":{""noise_scale"":0.5,""length_scale"":1.1,""noise_w"":0.333},
            ""word_carrier"":""Le mot : {0}."",""word_carrier_except"":[""eu"",""les""]");
        _synth = new PiperSynth(new[] { pierre, siwis }, Path.Combine(AppContext.BaseDirectory, "espeak-ng-data"),
                                s => { lock (_log) _log.Add(s); }, r => new NullSink(r))
        {
            ModelFactory = (_, _, _, _) => _model,
        };
        _synth.Resident = "piper:pierre";
        _synth.LoadNow("piper:pierre");
        _synth.LoadNow("piper:siwis");
        Assert.Equal(PiperState.Ready, _synth.StateOf("piper:pierre"));
        Assert.Equal(PiperState.Ready, _synth.StateOf("piper:siwis"));
    }

    private PiperVoicePack Pack(string id, string cfg, string extra)
    {
        var d = Path.Combine(_dir, id);
        Directory.CreateDirectory(d);
        File.WriteAllBytes(Path.Combine(d, "m.onnx"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(d, "m.onnx.json"), cfg);
        string Sha(string f) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(d, f)))).ToLowerInvariant();
        long Size(string f) => new FileInfo(Path.Combine(d, f)).Length;
        var card = $@"{{""id"":""{id}"",""name"":""N"",""gender"":""male"",""culture"":""fr-FR"",""model"":""m.onnx"",""config"":""m.onnx.json"",
            ""sizes"":{{""m.onnx"":{Size("m.onnx")},""m.onnx.json"":{Size("m.onnx.json")}}},
            ""sha256"":{{""m.onnx"":""{Sha("m.onnx")}"",""m.onnx.json"":""{Sha("m.onnx.json")}""}}{extra}}}";
        return PiperVoicePack.Parse(d, card);
    }

    private (float[] Audio, int Runs) Say(string voice, string text, bool word = true)
    {
        int before = _model.Count;
        float[]? audio = null;
        using var done = new ManualResetEventSlim();
        _synth.Rendered = (a, _) => audio = a;
        Assert.True(_synth.Speak(voice, text, (_, _) => done.Set(), null, word));
        Assert.True(done.Wait(60000), "utterance did not end");
        _synth.Rendered = null;
        return (audio!, _model.Count - before);
    }

    private string[] Log { get { lock (_log) return _log.ToArray(); } }

    [Fact]
    public void A_Pierre_Word_Is_Said_In_The_Carrier_Cut_Out_And_Then_Replayed_From_The_Cache()
    {
        var (audio, runs) = Say("piper:pierre", "très");
        Assert.Equal(1, runs);
        var run = _model.Last;
        Assert.Equal((0.5f, 0.333f), (run.Ns, run.Nw));             // the voice's scales, not the word scales
        Assert.True(run.Ids.Length > 20, "the carrier sentence was synthesised");   // « lə- mˈo: tʁˈɛ. » vs « tʁˈɛ »
        Assert.True(audio.Length < run.Ids.Length * 256, "the word is cut out of the carrier");
        Assert.Contains(Log, l => l.Contains("word carrier:") && l.Contains("cut at"));
        Assert.DoesNotContain(Log, l => l.Contains("très"));         // no text in the log

        var (again, runs2) = Say("piper:pierre", "très");
        Assert.Equal(0, runs2);
        Assert.Same(audio, again);
        Assert.Contains(Log, l => l.Contains("word cache hit, carrier"));
    }

    [Fact]
    public void A_Failed_Cut_Falls_Back_To_The_Word_Alone_And_Is_Cached_Under_The_Carrier_Key()
    {
        _model.BreakAlignment = true;
        var (_, runs) = Say("piper:pierre", "chat");
        Assert.Equal(2, runs);
        var alone = _model.Last;
        Assert.Equal((0.333f, 0.2f), (alone.Ns, alone.Nw));         // fallback = the 0.6.0 word synthesis
        Assert.True(alone.Ids.Length < 15, "the word alone");
        Assert.Contains(Log, l => l.Contains("word carrier fallback (no alignment)"));
        var (_, runs2) = Say("piper:pierre", "chat");
        Assert.Equal(0, runs2);                                      // not synthesised twice again
    }

    [Fact]
    public void Text_With_A_Sentence_End_Is_Said_Alone()
    {
        var (_, runs) = Say("piper:pierre", "oui.");
        Assert.Equal(1, runs);
        Assert.Equal((0.333f, 0.2f), (_model.Last.Ns, _model.Last.Nw));
    }

    [Fact]
    public void Il_Y_A_Loses_The_Palatal_Mark_With_Pierre_Everywhere_And_Keeps_It_With_Siwis()
    {
        Say("piper:pierre", "il y a");
        Assert.DoesNotContain(_palatalId, _model.Last.Ids.Select(x => (int)x));
        Say("piper:pierre", "Il y a un enfant dans la cour.", word: false);
        Assert.DoesNotContain(_palatalId, _model.Last.Ids.Select(x => (int)x));
        Say("piper:siwis", "Il y a un enfant dans la cour.", word: false);
        Assert.Contains(_palatalId, _model.Last.Ids.Select(x => (int)x));
    }

    [Fact]
    public void A_Siwis_Word_Is_Said_In_The_Carrier_With_The_Voice_Scales()
    {
        var (audio, runs) = Say("piper:siwis", "papa");
        Assert.Equal(1, runs);
        var run = _model.Last;
        Assert.Equal((0.5f, 0.333f), (run.Ns, run.Nw));
        Assert.True(run.Ids.Length > 20, "the carrier sentence was synthesised");
        Assert.True(audio.Length < run.Ids.Length * 256, "the word is cut out of the carrier");
        Assert.Contains(Log, l => l.Contains("word carrier:") && l.Contains("cut at"));
    }

    [Theory]
    [InlineData("eu")]
    [InlineData("Les")]
    public void A_Siwis_Word_On_The_Except_List_Is_Said_Alone_With_The_Voice_Scales(string word)
    {
        var (_, runs) = Say("piper:siwis", word);
        Assert.Equal(1, runs);
        Assert.True(_model.Last.Ids.Length < 15, "no carrier");
        Assert.Equal((0.5f, 0.333f), (_model.Last.Ns, _model.Last.Nw));   // siwis has no inference_word
        Assert.DoesNotContain(Log, l => l.Contains("carrier"));
        var (_, again) = Say("piper:siwis", word);
        Assert.Equal(0, again);                                            // cached under the plain key
    }

    [Fact]
    public void Pierre_Has_No_Except_List()
    {
        var (_, runs) = Say("piper:pierre", "eu");
        Assert.Equal(1, runs);
        Assert.True(_model.Last.Ids.Length > 20, "pierre says « eu » in the carrier");
    }

    [Fact]
    public void A_Word_With_Progress_Is_Not_Carried()
    {
        int before = _model.Count;
        using var done = new ManualResetEventSlim();
        Assert.True(_synth.Speak("piper:pierre", "très", (_, _) => done.Set(), _ => { }, word: true));
        Assert.True(done.Wait(60000));
        Assert.Equal(1, _model.Count - before);
        Assert.True(_model.Last.Ids.Length < 15);
    }

    public void Dispose()
    {
        _synth.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
#endif
