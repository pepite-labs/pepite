using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Predict.Keyboard.Tests;

/// <summary>The golden fixtures of plans 08a / 08b (tools/voice-lab/golden_ids.py, piper-tts 1.8.0 on the 40 plan 05
/// items), one per voice (<c>piper-golden-siwis.json</c>, <c>piper-golden-pierre.json</c>): per item the raw espeak
/// clauses, Piper's sentences of phonemes and ids, and for a few items one recorded alignment. The static members are
/// siwis' (the plan 08a tests); <see cref="For"/> gives any voice.</summary>
internal sealed class PiperGolden
{
    internal sealed record Item(string Id, string Text, List<EspeakClause> Clauses, List<List<string>> Sentences,
                                List<long[]> Ids, List<List<(string Phoneme, long Samples)>>? Align);

    public static readonly string[] VoiceIds = { "siwis", "pierre" };
    private static readonly Dictionary<string, Lazy<PiperGolden>> Cache =
        VoiceIds.ToDictionary(v => v, v => new Lazy<PiperGolden>(() => new PiperGolden(v)));

    /// <summary>The fixture of voice <paramref name="voice"/> (<c>siwis</c> or <c>pierre</c>).</summary>
    public static PiperGolden For(string voice) => Cache[voice].Value;

    public static JsonObject Meta => For("siwis").MetaOf;
    public static IReadOnlyList<Item> Items => For("siwis").ItemsOf;
    public static Item Get(string id) => For("siwis").GetItem(id);
    public static IReadOnlyDictionary<string, int[]> IdMap => For("siwis").IdMapOf;
    public static IReadOnlyCollection<string[]> Clusters => For("siwis").ClustersOf;

    public JsonObject MetaOf { get; }
    public IReadOnlyList<Item> ItemsOf { get; }
    public Item GetItem(string id) => ItemsOf.Single(i => i.Id == id);

    public IReadOnlyDictionary<string, int[]> IdMapOf =>
        ((JsonObject)MetaOf["id_map"]!).ToDictionary(kv => kv.Key, kv => kv.Value!.AsArray().Select(x => x!.GetValue<int>()).ToArray());

    public IReadOnlyCollection<string[]> ClustersOf =>
        MetaOf["vowel_clusters"]!.AsArray().Select(c => c!.AsArray().Select(x => x!.GetValue<string>()).ToArray()).ToList();

    private PiperGolden(string voice)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", $"piper-golden-{voice}.json");
        var arr = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        MetaOf = (JsonObject)arr[0]!["meta"]!;
        var items = new List<Item>();
        foreach (var n in arr.Skip(1))
        {
            var o = (JsonObject)n!;
            items.Add(new Item(
                o["id"]!.GetValue<string>(), o["text"]!.GetValue<string>(),
                o["clauses"]!.AsArray().Select(c => new EspeakClause(c![0]!.GetValue<string>(), c[1]!.GetValue<string>(), c[2]!.GetValue<bool>())).ToList(),
                o["sentences"]!.AsArray().Select(s => s!.AsArray().Select(p => p!.GetValue<string>()).ToList()).ToList(),
                o["ids"]!.AsArray().Select(s => s!.AsArray().Select(p => p!.GetValue<long>()).ToArray()).ToList(),
                o["align"] is JsonArray a
                    ? a.Select(s => s!.AsArray().Select(p => (p![0]!.GetValue<string>(), p[1]!.GetValue<long>())).ToList()).ToList()
                    : null));
        }
        ItemsOf = items;
    }
}
