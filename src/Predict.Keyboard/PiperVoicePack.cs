using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>
/// An installed Piper voice: <c>voices\&lt;id&gt;\voice.json</c> (the app's card, written by Data.Build/fetch_voices.py)
/// with the patched model, its <c>.onnx.json</c> and the licence files. <see cref="Scan"/> is cheap (sizes only, at
/// startup); <see cref="Verify"/> hashes every file (60–77 MB, on the <c>piper</c> thread at load). Plan 08b: a pack may
/// name a <see cref="Speaker"/> of a multi-speaker model (pierre in <c>fr_FR-upmc-medium</c>), an <see cref="Order"/> in
/// the voice list, and an <see cref="Inference"/> override of the model's scales (the <c>.onnx.json</c> is never
/// edited, so its pinned hash holds). 0.7.2: a <see cref="WordCarrier"/> for isolated words and a
/// <see cref="PhonemeFix"/> (pierre); 0.10.0: siwis' carrier too, with words said alone (<see cref="WordCarrierExcept"/>).
/// </summary>
internal sealed class PiperVoicePack
{
    public string Dir { get; }
    public string Id { get; }
    public string Culture { get; }
    public string Gender { get; }
    public int SampleRate { get; }
    public string Model { get; }
    public string Config { get; }
    public IReadOnlyDictionary<string, long> Sizes { get; }
    public IReadOnlyDictionary<string, string> Sha256 { get; }
    /// <summary>Credit lines for « À propos » (CC BY / BY-SA attribution and change notice).</summary>
    public IReadOnlyList<string> Attribution { get; }
    /// <summary>Speaker name in the model's <c>speaker_id_map</c> (multi-speaker models only), else null.</summary>
    public string? Speaker { get; init; }
    /// <summary>Position in the voice list (smaller first; <see cref="DefaultOrder"/> when the card has none).</summary>
    public int Order { get; init; } = DefaultOrder;
    /// <summary>The card's licence (e.g. « CC BY-SA 4.0 »), for the size report.</summary>
    public string? Licence { get; init; }
    /// <summary>false: private test releases only (D28; plan 10 leaves such a pack out of a public release).</summary>
    public bool Public { get; init; } = true;
    /// <summary>Optional override of the <c>.onnx.json</c> scales (B4: pierre's tuned inference, plan 08b step 7).</summary>
    public PiperInference? Inference { get; init; }
    /// <summary>Optional lower noise for isolated-word utterances (<c>inference_word</c>: <c>noise_scale</c>,
    /// <c>noise_w</c>; the length scale stays the voice's). VITS samples noise at each run: on a one-word input it is
    /// heard (owner, 0.6.0: « très » with pierre sounded different from one echo to the next); null = the voice's scales.</summary>
    public PiperWordInference? InferenceWord { get; init; }
    /// <summary>Optional carrier sentence for isolated words (<c>word_carrier</c>, e.g. « Le mot : {0}. »; owner's blind
    /// sheet of 0.6.0, W5): the word is synthesised inside it with the voice's scales and cut out on the alignment
    /// (<see cref="PiperCarrier"/>); when the cut fails the word is synthesised alone with <see cref="InferenceWord"/>.
    /// Null = isolated words are synthesised alone.</summary>
    public string? WordCarrier { get; init; }
    /// <summary>Words always said alone even with a <see cref="WordCarrier"/> (<c>word_carrier_except</c>, stored as
    /// <see cref="PiperCarrier.ExceptKey"/>). Siwis (owner's blind sheet, 2026-11-16): « eu » 5 / 5 alone but 2 / 2 in
    /// either carrier, « les » 5 alone but 3 in « Le mot : ».</summary>
    public IReadOnlySet<string> WordCarrierExcept { get; init; } = new HashSet<string>();
    /// <summary>Optional phoneme post-corrections (<c>phoneme_fix</c>: <c>[["iʲ", "i"]]</c>), applied wherever the voice
    /// speaks (<see cref="PiperPhonemeFix"/>).</summary>
    public PiperPhonemeFix? PhonemeFix { get; init; }

    public const int DefaultOrder = 100;

    private PiperVoicePack(string dir, string id, string culture, string gender, int rate, string model, string config,
                           Dictionary<string, long> sizes, Dictionary<string, string> sha, List<string> attribution)
    {
        Dir = dir; Id = id; Culture = culture; Gender = gender; SampleRate = rate; Model = model; Config = config;
        Sizes = sizes; Sha256 = sha; Attribution = attribution;
    }

    public string VoiceId => "piper:" + Id;

    /// <summary>Packs under <paramref name="voicesDir"/> whose card parses and whose files all have the listed size.
    /// <paramref name="log"/> gets one line per folder left out (no text, names only).</summary>
    public static List<PiperVoicePack> Scan(string voicesDir, Action<string>? log)
    {
        var o = new List<PiperVoicePack>();
        if (!Directory.Exists(voicesDir)) return o;
        foreach (var d in Directory.GetDirectories(voicesDir))
        {
            var card = Path.Combine(d, "voice.json");
            if (!File.Exists(card)) continue;
            try
            {
                var p = Parse(d, File.ReadAllText(card));
                var bad = p.Sizes.FirstOrDefault(kv => !File.Exists(Path.Combine(d, kv.Key)) || new FileInfo(Path.Combine(d, kv.Key)).Length != kv.Value);
                if (bad.Key != null) { log?.Invoke($"speech: voice pack {Path.GetFileName(d)} left out (size of {bad.Key})"); continue; }
                o.Add(p);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
            {
                log?.Invoke($"speech: voice pack {Path.GetFileName(d)} left out ({ex.GetType().Name}: {ex.Message})");
            }
        }
        // the voice list order (plan 08b): the card's order, then the id
        return o.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static PiperVoicePack Parse(string dir, string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("voice.json is not an object");
        string S(string k) => o[k]?.GetValue<string>() ?? throw new FormatException($"voice.json: {k} missing");
        var id = S("id");
        if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_'))) throw new FormatException("voice.json: bad id");
        var sizes = (o["sizes"] as JsonObject ?? throw new FormatException("voice.json: sizes missing"))
                    .ToDictionary(kv => SafeName(kv.Key), kv => kv.Value!.GetValue<long>(), StringComparer.Ordinal);
        var sha = (o["sha256"] as JsonObject ?? throw new FormatException("voice.json: sha256 missing"))
                  .ToDictionary(kv => SafeName(kv.Key), kv => kv.Value!.GetValue<string>().ToLowerInvariant(), StringComparer.Ordinal);
        var model = SafeName(S("model"));
        var config = SafeName(o["config"]?.GetValue<string>() ?? model + ".json");
        if (!sizes.ContainsKey(model) || !sha.ContainsKey(model) || !sizes.ContainsKey(config) || !sha.ContainsKey(config))
            throw new FormatException("voice.json: model / config not listed");
        var attribution = (o["attribution"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? new List<string>();
        var speaker = o["speaker"]?.GetValue<string>();
        if (speaker is { Length: 0 }) throw new FormatException("voice.json: empty speaker");
        PiperInference? inference = null;
        if (o["inference"] is JsonObject inf)
        {
            float F(string k) => inf[k]?.GetValue<float>() is float f && f > 0 && f < 10 ? f : throw new FormatException($"voice.json: inference.{k} missing or out of range");
            inference = new PiperInference(F("noise_scale"), F("length_scale"), F("noise_w"));
        }
        PiperWordInference? word = null;
        if (o["inference_word"] is JsonObject iw)
        {
            float F(string k) => iw[k]?.GetValue<float>() is float f && f >= 0 && f < 10 ? f : throw new FormatException($"voice.json: inference_word.{k} missing or out of range");
            word = new PiperWordInference(F("noise_scale"), F("noise_w"));
        }
        var carrier = o["word_carrier"]?.GetValue<string>();
        if (carrier != null && !PiperCarrier.IsValidFormat(carrier))
            throw new FormatException("voice.json: word_carrier needs one {0} after a clause boundary (, : ;)");
        var except = new HashSet<string>(StringComparer.Ordinal);
        if (o["word_carrier_except"] is { } we)
            foreach (var x in we as JsonArray ?? throw new FormatException("voice.json: word_carrier_except is a list of words"))
                except.Add(x?.GetValue<string>() is { } s && !string.IsNullOrWhiteSpace(s)
                    ? PiperCarrier.ExceptKey(s) : throw new FormatException("voice.json: empty word in word_carrier_except"));
        PiperPhonemeFix? fix = null;
        if (o["phoneme_fix"] is JsonArray pf)
            fix = PiperPhonemeFix.Create(pf.Select(r => r is JsonArray { Count: 2 } a
                ? (a[0]?.GetValue<string>() ?? "", a[1]?.GetValue<string>() ?? "")
                : throw new FormatException("voice.json: phoneme_fix entries are [from, to]")));
        return new PiperVoicePack(dir, id, S("culture"), S("gender"), o["sampleRate"]?.GetValue<int>() ?? 22050, model, config, sizes, sha, attribution)
        {
            Speaker = speaker,
            Order = o["order"]?.GetValue<int>() ?? DefaultOrder,
            Licence = o["licence"]?.GetValue<string>(),
            Public = o["public"]?.GetValue<bool>() ?? true,
            Inference = inference,
            InferenceWord = word,
            WordCarrier = carrier,
            WordCarrierExcept = except,
            PhonemeFix = fix,
        };
    }

    /// <summary>The text is on the card's <see cref="WordCarrierExcept"/> list: said alone, never in the carrier.</summary>
    public bool SaysAlone(string text) => WordCarrierExcept.Contains(PiperCarrier.ExceptKey(text));

    /// <summary>The model's speaker id for this pack: null for a single-speaker model without a speaker, else the
    /// <c>speaker_id_map</c> entry. A speaker the model does not have, a speaker on a single-speaker model, or a
    /// multi-speaker model without a speaker throws <see cref="FormatException"/> (the load fails: SAPI + log).</summary>
    public long? SpeakerId(PiperVoiceConfig cfg)
    {
        if (cfg.NumSpeakers <= 1)
            return Speaker is null ? null : throw new FormatException($"speaker {Speaker} on a single-speaker model");
        if (Speaker is null) throw new FormatException("multi-speaker model without a speaker in voice.json");
        return cfg.SpeakerIdMap.TryGetValue(Speaker, out var sid) ? sid : throw new FormatException($"unknown speaker {Speaker}");
    }

    /// <summary>The model config with the card's inference override applied (if any).</summary>
    public PiperVoiceConfig Apply(PiperVoiceConfig cfg) =>
        Inference is { } i ? cfg with { NoiseScale = i.NoiseScale, LengthScale = i.LengthScale, NoiseW = i.NoiseW } : cfg;

    /// <summary>A plain file name inside the pack folder (no path, no "..").</summary>
    private static string SafeName(string n) =>
        n.Length == 0 || n.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || n == "." || n == ".." ? throw new FormatException($"voice.json: bad file name {n}") : n;

    /// <summary>Every listed file has its SHA-256; throws <see cref="InvalidDataException"/> (« sha256 mismatch ») otherwise.</summary>
    public void Verify()
    {
        foreach (var (name, want) in Sha256)
        {
            using var f = File.OpenRead(Path.Combine(Dir, name));
            using var h = SHA256.Create();
            var got = Convert.ToHexString(h.ComputeHash(f)).ToLowerInvariant();
            if (got != want) throw new InvalidDataException($"sha256 mismatch: {name}");
        }
    }
}

/// <summary>What the synthesis needs from a voice's <c>.onnx.json</c> (Piper's config).</summary>
internal sealed record PiperVoiceConfig(string EspeakVoice, int SampleRate, int HopLength, float NoiseScale, float LengthScale,
                                        float NoiseW, IReadOnlyDictionary<string, int[]> IdMap, IReadOnlyCollection<string[]> VowelClusters,
                                        int NumSpeakers)
{
    /// <summary>Speaker name to id of a multi-speaker model (upmc: jessica 0, pierre 1); empty otherwise.</summary>
    public IReadOnlyDictionary<string, long> SpeakerIdMap { get; init; } = new Dictionary<string, long>();
    /// <summary>Piper's <c>default_speaker_id</c> (0 when absent). The app never relies on it: a pack names its speaker.</summary>
    public long DefaultSpeakerId { get; init; }

    public static PiperVoiceConfig Parse(string json)
    {
        var o = JsonNode.Parse(json) as JsonObject ?? throw new FormatException("voice config is not an object");
        var inf = o["inference"] as JsonObject;
        var map = (o["phoneme_id_map"] as JsonObject ?? throw new FormatException("phoneme_id_map missing"))
                  .ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().Select(x => x!.GetValue<int>()).ToArray(), StringComparer.Ordinal);
        var clusters = (o["vowel_clusters"] as JsonArray)?.Select(c => c!.AsArray().Select(x => x!.GetValue<string>()).ToArray()).ToList()
                       ?? new List<string[]>();
        return new PiperVoiceConfig(
            (o["espeak"] as JsonObject)?["voice"]?.GetValue<string>() ?? throw new FormatException("espeak.voice missing"),
            (o["audio"] as JsonObject)?["sample_rate"]?.GetValue<int>() ?? 22050,
            o["hop_length"]?.GetValue<int>() ?? 256,
            inf?["noise_scale"]?.GetValue<float>() ?? 0.667f,
            inf?["length_scale"]?.GetValue<float>() ?? 1f,
            inf?["noise_w"]?.GetValue<float>() ?? 0.8f,
            map, clusters, o["num_speakers"]?.GetValue<int>() ?? 1)
        {
            SpeakerIdMap = (o["speaker_id_map"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<long>(), StringComparer.Ordinal)
                           ?? new Dictionary<string, long>(),
            DefaultSpeakerId = o["default_speaker_id"]?.GetValue<long>() ?? 0,
        };
    }

    /// <summary>A7: the rate setting (−10..10) as Piper's length_scale = base × 1.5^(−rate/10): rate 0 is the voice's own
    /// pace (its card's <c>length_scale</c>: 1.1 for siwis and pierre since 2026-11-14), −10 is 1.5 × slower, +10 1.5 ×
    /// faster. The clamp follows the base (it used to be the absolute [0.667, 1.5], which with a base of 1.1 made −8, −9
    /// and −10 all the same speed).</summary>
    public static float LengthScaleFor(float baseScale, int rate) =>
        (float)(baseScale * Math.Pow(1.5, -Math.Clamp(rate, -10, 10) / 10.0));
}

/// <summary>Piper's three inference scales (<c>noise_scale</c>, <c>length_scale</c>, <c>noise_w</c>).</summary>
internal readonly record struct PiperInference(float NoiseScale, float LengthScale, float NoiseW);

/// <summary>The noise scales of isolated-word utterances (<c>inference_word</c> in a voice card).</summary>
internal readonly record struct PiperWordInference(float NoiseScale, float NoiseW);
