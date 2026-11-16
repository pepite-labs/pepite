using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.7.2 (owner's blind sheet of 0.6.0, 2026-11-14): pierre's isolated words said in a carrier sentence and
/// cut out (W5), the fallback when the cut is not possible, the cache key of a carrier word, and the <c>iʲ → i</c>
/// phoneme correction (P2) — which must leave the espeak word count, hence the word map, unchanged.</summary>
public class PiperCarrierTests
{
    private const int Rate = 22050;

    // ---------------------------------------------------------------- format

    [Fact]
    public void Format_Needs_One_Slot_After_A_Clause_Boundary()
    {
        Assert.True(PiperCarrier.IsValidFormat("Le mot : {0}."));
        Assert.True(PiperCarrier.IsValidFormat("Bon, le mot : {0}."));
        Assert.False(PiperCarrier.IsValidFormat("Le mot {0}."));        // no boundary: the word cannot be found
        Assert.False(PiperCarrier.IsValidFormat("{0}, le mot."));       // boundary after the word only
        Assert.False(PiperCarrier.IsValidFormat("Le mot : {0} {0}."));
        Assert.False(PiperCarrier.IsValidFormat("Le mot : {1}."));
        Assert.False(PiperCarrier.IsValidFormat("Le {mot} : {0}."));
        Assert.False(PiperCarrier.IsValidFormat(""));
        Assert.False(PiperCarrier.IsValidFormat(null));
        Assert.Equal(1, PiperCarrier.SeparatorsBefore("Le mot : {0}."));
        Assert.Equal(2, PiperCarrier.SeparatorsBefore("Bon, le mot : {0}, voilà."));
        Assert.Equal("Le mot : très.", PiperCarrier.Text("Le mot : {0}.", "  très "));
        Assert.Equal("Le mot : {x}.", PiperCarrier.Text("Le mot : {0}.", "{x}"));   // the word is an argument, never a format
    }

    [Fact]
    public void Carrier_Applies_To_Short_Texts_Without_A_Sentence_End()
    {
        Assert.True(PiperCarrier.Applies("très"));
        Assert.True(PiperCarrier.Applies("les amis"));
        Assert.True(PiperCarrier.Applies("il y a"));
        Assert.False(PiperCarrier.Applies("oui."));                    // two sentences in the carrier: the word alone
        Assert.False(PiperCarrier.Applies("quoi ?"));
        Assert.False(PiperCarrier.Applies("   "));
        Assert.False(PiperCarrier.Applies(new string('a', PiperWordCache.MaxTextLength + 1)));
    }

    [Fact]
    public void Card_Parses_The_Carrier_And_The_Phoneme_Fix()
    {
        string Card(string extra) =>
            $@"{{""id"":""pierre"",""name"":""N"",""gender"":""male"",""culture"":""fr-FR"",""model"":""v.onnx"",""config"":""v.onnx.json"",
                ""sizes"":{{""v.onnx"":3,""v.onnx.json"":2}},""sha256"":{{""v.onnx"":""{new string('0', 64)}"",""v.onnx.json"":""{new string('0', 64)}""}}{extra}}}";
        var p = PiperVoicePack.Parse("x", Card(@",""word_carrier"":""Le mot : {0}."",""phoneme_fix"":[[""iʲ"",""i""]]"));
        Assert.Equal("Le mot : {0}.", p.WordCarrier);
        Assert.Equal(new[] { ("iʲ", "i") }, p.PhonemeFix!.Rules);
        var none = PiperVoicePack.Parse("x", Card(""));
        Assert.Null(none.WordCarrier);
        Assert.Null(none.PhonemeFix);
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card(@",""word_carrier"":""Le mot {0}.""")));
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card(@",""phoneme_fix"":[[""i""]]")));
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card(@",""phoneme_fix"":[[""a b"",""a""]]")));
    }

    private static string Card(string extra) =>
        $@"{{""id"":""siwis"",""name"":""N"",""gender"":""female"",""culture"":""fr-FR"",""model"":""v.onnx"",""config"":""v.onnx.json"",
            ""sizes"":{{""v.onnx"":3,""v.onnx.json"":2}},""sha256"":{{""v.onnx"":""{new string('0', 64)}"",""v.onnx.json"":""{new string('0', 64)}""}}{extra}}}";

    [Fact]
    public void Carrier_Exceptions_Match_After_Trim_Case_And_Unicode_Normalisation()
    {
        var p = PiperVoicePack.Parse("x", Card(@",""word_carrier"":""Le mot : {0}."",""word_carrier_except"":[""eu"","" LES "",""À""]"));
        Assert.True(p.SaysAlone("eu"));
        Assert.True(p.SaysAlone(" Eu "));
        Assert.True(p.SaysAlone("EU"));
        Assert.True(p.SaysAlone("les"));
        Assert.True(p.SaysAlone("à"));
        Assert.True(p.SaysAlone("à"));                 // decomposed « à »
        Assert.False(p.SaysAlone("a"));
        Assert.False(p.SaysAlone("le"));
        Assert.False(p.SaysAlone("eux"));                    // whole words only
        Assert.False(p.SaysAlone("les amis"));               // a liaison group is not the listed word
        Assert.Equal("eu", PiperCarrier.ExceptKey("  EU "));

        var none = PiperVoicePack.Parse("x", Card(@",""word_carrier"":""Le mot : {0}."""));
        Assert.Empty(none.WordCarrierExcept);
        Assert.False(none.SaysAlone("eu"));
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card(@",""word_carrier_except"":""eu""")));
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card(@",""word_carrier_except"":[""eu"",""  ""]")));
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", Card(@",""word_carrier_except"":[null]")));
        Assert.Throws<InvalidOperationException>(() => PiperVoicePack.Parse("x", Card(@",""word_carrier_except"":[3]")));
    }

    /// <summary>The siwis entry of the pinned <c>Data.Build/voices.json</c>, made into a card as fetch_voices.py does
    /// (the keys the app reads), parses with the carrier of the 2026-11-16 blind sheet and its two exceptions.</summary>
    [Fact]
    public void The_Pinned_Siwis_Card_Has_The_Carrier_And_Says_Eu_And_Les_Alone()
    {
        var pins = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data.Build", "voices.json"));
        var siwis = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(pins))!["voices"]!["siwis"]!.AsObject();
        string Keys(params string[] k) => string.Concat(k.Where(x => siwis[x] != null).Select(x => $@",""{x}"":{siwis[x]!.ToJsonString()}"));
        var p = PiperVoicePack.Parse("x", Card(Keys("inference", "inference_word", "word_carrier", "word_carrier_except", "phoneme_fix")));
        Assert.Equal("Le mot : {0}.", p.WordCarrier);
        Assert.True(p.SaysAlone("eu"));
        Assert.True(p.SaysAlone("Les"));
        Assert.False(p.SaysAlone("papa"));
        Assert.False(p.SaysAlone("mais"));
        Assert.Null(p.InferenceWord);                        // the fallback is the word alone with the voice's scales (A)
        Assert.Null(p.PhonemeFix);
    }

    // ---------------------------------------------------------------- the cut

    /// <summary>« ^ l : ␠ p a . $ » with 100 / 200 / 300 / 400 / 500 / 500 / 300 / 200 samples: the word starts at 1000.
    /// The audio is 0.5 everywhere except a silent pause at [600, 900).</summary>
    private static (float[] Audio, List<(string, long)> Align) Synthetic()
    {
        var align = new List<(string, long)> { ("^", 100), ("l", 200), (":", 300), (" ", 400), ("p", 500), ("a", 500), (".", 300), ("$", 200) };
        var audio = Enumerable.Repeat(0.5f, 2500).ToArray();
        for (int i = 600; i < 900; i++) audio[i] = 0;
        return (audio, align);
    }

    [Fact]
    public void Cut_Is_At_The_Quietest_5_ms_Before_The_Aligned_Start_With_A_Fade_In()
    {
        var (audio, align) = Synthetic();
        var cut = PiperCarrier.Extract(audio, align, 1, Rate, out var why);
        Assert.NotNull(cut);
        Assert.Null(why);
        Assert.Equal(1000, cut!.AlignedStart);
        // 5 ms windows (110 samples) in half steps from 0: the first all-silent one is [605, 715), cut at its middle
        Assert.Equal(660, cut.CutStart);
        Assert.Equal(2500 - 660, cut.Audio.Length);
        Assert.Equal(0f, cut.Audio[0]);                               // fade-in from 0
        Assert.Equal(1.0, (double)cut.Audio.Max(x => Math.Abs(x)), 3);        // peak-normalised again
        Assert.Equal("p", cut.WordAlign[0].Phoneme);
        Assert.Equal("$", cut.WordAlign[^1].Phoneme);
        Assert.Equal(1.0f * 0.5f, cut.LevelAtCut);                   // ±5 ms reaches the sound before the pause
        Assert.Equal(new[] { 0.5f }, audio.Skip(1000).Take(1).ToArray());   // the input is not modified
    }

    [Fact]
    public void Second_Boundary_Is_Used_When_The_Format_Has_Two()
    {
        var align = new List<(string, long)> { ("^", 100), ("b", 100), (",", 100), (" ", 100), ("l", 100), (":", 100), (" ", 100), ("p", 300), ("$", 100) };
        var cut = PiperCarrier.Extract(new float[1200], align, 2, Rate, out _);
        // silence everywhere: the first window of the search (from 0) wins; the aligned start is after the second « : ␠ »
        Assert.Equal(700, cut!.AlignedStart);
        Assert.Equal("p", cut.WordAlign[0].Phoneme);
    }

    [Fact]
    public void No_Cut_Means_Fallback_With_A_Reason()
    {
        var (audio, align) = Synthetic();
        Assert.Null(PiperCarrier.Extract(audio, null, 1, Rate, out var why));                       // ids did not line up
        Assert.Equal("no alignment", why);
        Assert.Null(PiperCarrier.Extract(audio, align, 2, Rate, out why));                          // one boundary only
        Assert.Equal("boundary not found", why);
        var noWord = new List<(string, long)> { ("^", 100), ("l", 200), (":", 300), (" ", 400), (".", 300), ("$", 200) };
        Assert.Null(PiperCarrier.Extract(audio, noWord, 1, Rate, out why));
        Assert.Equal("no word after the boundary", why);
        Assert.Null(PiperCarrier.Extract(new float[900], align, 1, Rate, out why));                 // alignment past the audio
        Assert.Equal("start outside the audio", why);
    }

    // ---------------------------------------------------------------- cache

    [Fact]
    public void Carrier_Words_Have_Their_Own_Cache_Key()
    {
        var plain = PiperWordCache.Key("piper:pierre", "très", 0.5f, 1.1f, 0.333f);
        var carrier = PiperWordCache.Key("piper:pierre" + PiperWordCache.CarrierMark, "très", 0.5f, 1.1f, 0.333f);
        Assert.NotNull(carrier);
        Assert.NotEqual(plain, carrier);
        Assert.Equal(carrier, PiperWordCache.Key("piper:pierre" + PiperWordCache.CarrierMark, " très ", 0.5f, 1.1f, 0.333f));
    }

    // ---------------------------------------------------------------- P2: iʲ → i

    private static readonly PiperPhonemeFix P2 = PiperPhonemeFix.Create(new[] { ("iʲ", "i") });

    [Fact]
    public void Fix_Rules_May_Not_Add_Or_Remove_Words()
    {
        Assert.Throws<FormatException>(() => PiperPhonemeFix.Create(new[] { ("iʲ", "") }));
        Assert.Throws<FormatException>(() => PiperPhonemeFix.Create(new[] { ("iʲ a", "ja") }));   // the P4 variant: merges words
        Assert.Throws<FormatException>(() => PiperPhonemeFix.Create(new[] { ("-", "a") }));
        Assert.Throws<FormatException>(() => PiperPhonemeFix.Create(Array.Empty<(string, string)>()));
        Assert.Equal(new[] { ("iʲ", "i") }, PiperPhonemeFix.Parse(@"iʲ=i").Rules);
    }

    [Fact]
    public void P2_On_The_Pierre_Golden_Items_Changes_Only_The_Palatal_Mark_And_Keeps_The_Word_Map()
    {
        var g = PiperGolden.For("pierre");
        var changed = new List<string>();
        foreach (var it in g.ItemsOf)
        {
            var fixedS = P2.Apply(it.Sentences, g.ClustersOf);
            Assert.Equal(it.Sentences.Count, fixedS.Count);
            bool had = it.Sentences.Any(s => s.Contains("ʲ"));
            Assert.DoesNotContain(fixedS, s => s.Contains("ʲ"));
            if (!had) { for (int k = 0; k < fixedS.Count; k++) Assert.Same(it.Sentences[k], fixedS[k]); continue; }
            changed.Add(it.Id);
            for (int k = 0; k < fixedS.Count; k++)
            {
                int marks = it.Sentences[k].Count(p => p == "ʲ");
                Assert.Equal(it.Sentences[k].Count - marks, fixedS[k].Count);          // ʲ removed, nothing else
                Assert.Equal(it.Sentences[k].Where(p => p != "ʲ"), fixedS[k]);
                Assert.Equal(PiperWordMap.Words(it.Sentences[k]).Count, PiperWordMap.Words(fixedS[k]).Count);
                var ids = PiperPhonemizer.Ids(fixedS[k], g.IdMapOf, out int unknown);
                Assert.Equal(0, unknown);
                Assert.Equal(it.Ids[k].Length - 2 * marks, ids.Length);                 // one id + its PAD less per mark
                Assert.NotNull(PiperPhonemizer.Groups(fixedS[k], g.IdMapOf, ids));      // the alignment still lines up
            }
            // the highlight: the same units land on the same espeak words
            var units = PiperWordMap.Units(it.Text, _ => 1);
            var before = PiperWordMap.Assign(units, it.Sentences);
            var after = PiperWordMap.Assign(units, fixedS);
            Assert.Equal(before.Fallback, after.Fallback);
            Assert.Equal(before.PerSentence.Select(s => s.Select(x => (x.Unit, x.Word)).ToList()),
                         after.PerSentence.Select(s => s.Select(x => (x.Unit, x.Word)).ToList()));
            for (int k = 0; k < fixedS.Count; k++)
            {
                var al = new[] { ("^", 100L) }.Concat(fixedS[k].Select(p => (p, 100L))).Append(("$", 100L)).ToList();
                var marks = PiperWordMap.Marks(after, k, al);
                Assert.Equal(after.PerSentence[k].Count(x => x.Word >= 0), marks.Count);
            }
        }
        // espeak writes ʲ for « y » and for an /i/ before a vowel (amis‿arrivent, lit‿une): 7 of the 40 items
        Assert.Equal(new[] { "L01", "L02", "L08", "S05", "S07", "S13", "H05" }, changed);
        var l02 = P2.Apply(PiperGolden.For("pierre").GetItem("L02").Sentences, g.ClustersOf);
        Assert.StartsWith("il i a œ̃n", string.Concat(l02[0]).Normalize(System.Text.NormalizationForm.FormC));
    }
}
