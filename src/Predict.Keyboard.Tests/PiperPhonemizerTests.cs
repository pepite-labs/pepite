using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>The C# port of Piper's phonemizer against piper-tts 1.8.0's own output (plan 08a golden test, pure part).</summary>
public class PiperPhonemizerTests
{
    private static readonly IReadOnlyDictionary<string, int[]> Map = PiperGolden.IdMap;

    [Theory]
    [InlineData("siwis", "fr_FR-siwis-medium", "df814aa3dfedd00510a07144442cc0a220ff21e43d03c3b078444fc0be68fc37")]
    [InlineData("pierre", "fr_FR-upmc-medium", "e8636ec15dfd5d72db37a02cb5320a20f2b8d339f2a0e4337da64c58a33a5868")]
    public void Fixture_Has_40_Items_And_The_Pinned_Versions(string voice, string model, string jsonSha)
    {
        var g = PiperGolden.For(voice);
        Assert.Equal(40, g.ItemsOf.Count);
        Assert.Equal("1.8.0", g.MetaOf["piper_tts"]!.GetValue<string>());
        Assert.Equal("1.52.0.1", g.MetaOf["espeak_ng"]!.GetValue<string>());
        Assert.Equal(model, g.MetaOf["voice"]!.GetValue<string>());
        Assert.Equal(jsonSha, g.MetaOf["voice_json_sha256"]!.GetValue<string>());
    }

    [Fact]
    public void Pierre_Fixture_Has_The_Speaker_And_Its_Scales()
    {
        var m = PiperGolden.For("pierre").MetaOf;
        Assert.Equal("pierre", m["speaker"]!.GetValue<string>());
        Assert.Equal(1, m["speaker_id"]!.GetValue<int>());
        Assert.Equal(2, m["num_speakers"]!.GetValue<int>());
        Assert.Equal(0.667, m["noise_scale"]!.GetValue<double>(), 3);
        Assert.Equal(0.8, m["noise_w"]!.GetValue<double>(), 3);
        Assert.Empty(PiperGolden.For("pierre").ClustersOf);
    }

    [Fact]
    public void Raw_Clauses_Are_The_Same_In_Both_Fixtures()
    {
        var a = PiperGolden.For("siwis").ItemsOf;
        var b = PiperGolden.For("pierre").ItemsOf;
        Assert.Equal(a.Select(i => i.Id), b.Select(i => i.Id));
        Assert.All(a.Zip(b), p => Assert.Equal(p.First.Clauses, p.Second.Clauses));
    }

    [Theory]
    [InlineData("siwis")]
    [InlineData("pierre")]
    public void Sentences_And_Ids_Equal_Piper_40_Of_40(string voice)
    {
        var g = PiperGolden.For(voice);
        var map = g.IdMapOf;
        var bad = new List<string>();
        foreach (var it in g.ItemsOf)
        {
            var sentences = PiperPhonemizer.Sentences(it.Clauses, g.ClustersOf);
            bool ok = sentences.Count == it.Sentences.Count
                      && sentences.Zip(it.Sentences).All(p => p.First.SequenceEqual(p.Second));
            var ids = sentences.Select(s => PiperPhonemizer.Ids(s, map, out _)).ToList();
            ok &= ids.Count == it.Ids.Count && ids.Zip(it.Ids).All(p => p.First.SequenceEqual(p.Second));
            if (!ok) bad.Add(it.Id);
        }
        Assert.True(bad.Count == 0, $"{voice}: {40 - bad.Count}/40; mismatches: {string.Join(", ", bad)}");
    }

    [Fact]
    public void Language_Flags_Are_Removed()
    {
        var s = PiperPhonemizer.Sentences(new[] { new EspeakClause("mɔ̃ (en)fˈʊtbɔːl(fr) ok", ".", true) }, null);
        Assert.Equal("mɔ̃ fˈʊtbɔːl ok.".Normalize(System.Text.NormalizationForm.FormD), string.Concat(s.Single()));
        // S08 in the fixture went through espeak's language switch
        Assert.Contains("(en)", PiperGolden.Get("S08").Clauses[0].Phonemes);
    }

    [Fact]
    public void Clause_Punctuation_Gets_A_Space_Sentence_Punctuation_Does_Not()
    {
        var s = PiperPhonemizer.Sentences(new[]
        {
            new EspeakClause("a", ",", false), new EspeakClause("b", ":", false), new EspeakClause("c", ";", false),
            new EspeakClause("d", "?", true), new EspeakClause("e", "!", true),
        }, null);
        Assert.Equal(new[] { "a, b: c; d?", "e!" }, s.Select(x => string.Concat(x)));
        Assert.Equal("?", PiperGolden.Get("S03").Sentences[0][^1]);
        Assert.Equal("!", PiperGolden.Get("S04").Sentences[0][^1]);
    }

    [Fact]
    public void Nfd_Splits_The_Nasal_Tilde_Into_Its_Own_Id()
    {
        var s = PiperPhonemizer.Sentences(new[] { new EspeakClause("ɑ̃", "", true) }, null).Single();
        Assert.Equal(new[] { "ɑ", "̃" }, s);
        Assert.Equal(new long[] { 1, 0, 51, 0, 141, 0, 2 }, PiperPhonemizer.Ids(s, Map, out _));
    }

    [Fact]
    public void Vowel_Clusters_Are_Merged()
    {
        var s = PiperPhonemizer.Sentences(new[] { new EspeakClause("taɪm", "", true) }, PiperGolden.Clusters).Single();
        Assert.Equal(new[] { "t", "aɪ", "m" }, s);
        Assert.Contains(161L, PiperPhonemizer.Ids(s, Map, out _));
    }

    [Fact]
    public void Text_Without_A_Terminator_Is_One_Sentence_And_An_Empty_Last_One_Is_Dropped()
    {
        Assert.Single(PiperGolden.Get("W01").Sentences);
        var s = PiperPhonemizer.Sentences(new[] { new EspeakClause("mɛzɔ̃", "", false) }, null);
        Assert.Single(s);
        var t = PiperPhonemizer.Sentences(new[] { new EspeakClause("a", ".", true), new EspeakClause("", "", true) }, null);
        Assert.Single(t);
    }

    [Fact]
    public void Unknown_Phoneme_Is_Skipped_And_Counted()
    {
        var ids = PiperPhonemizer.Ids(new[] { "a", "☃", "b" }, Map, out int unknown);
        Assert.Equal(1, unknown);
        Assert.Equal(new long[] { 1, 0, 14, 0, 15, 0, 2 }, ids);
        Assert.Null(PiperPhonemizer.Groups(new[] { "a", "☃", "b" }, Map, ids));
    }

    [Fact]
    public void Groups_Pair_Each_Phoneme_With_Its_Pad()
    {
        var it = PiperGolden.Get("W01");
        var g = PiperPhonemizer.Groups(it.Sentences[0], Map, it.Ids[0])!;
        Assert.Equal(it.Sentences[0].Count + 2, g.Count);
        Assert.Equal(("^", 2), g[0]);
        Assert.Equal(("$", 1), g[^1]);
        Assert.Equal(it.Ids[0].Length, g.Sum(x => x.Count));
        Assert.Equal(it.Align![0].Select(a => a.Phoneme), g.Select(x => x.Phoneme));
    }

    [Fact]
    public void Clause_Terminators_From_Raw_Values()
    {
        Assert.Equal(new EspeakClause("x", ".", true), PiperPhonemizer.Clause("x", PiperPhonemizer.ClausePeriod | 0x00100000));
        Assert.Equal(new EspeakClause("x", ",", false), PiperPhonemizer.Clause("x", PiperPhonemizer.ClauseComma));
        Assert.Equal(new EspeakClause("x", "?", true), PiperPhonemizer.Clause("x", PiperPhonemizer.ClauseQuestion));
        Assert.Equal(new EspeakClause("x", "", false), PiperPhonemizer.Clause("x", 0));
    }

    [Theory]
    [InlineData("Quelle belle journée !", "Quelle belle journée !")]
    [InlineData("Quelle belle journée !", "Quelle belle journée !")]
    [InlineData("J’ai oublié", "J'ai oublié")]
    [InlineData("un\r\ndeux", "un  deux")]
    public void Cleanup_Is_One_For_One(string raw, string clean)
    {
        Assert.Equal(clean, TextCleanup.ForEspeak(raw));
        Assert.Equal(raw.Length, TextCleanup.ForEspeak(raw).Length);
    }

    [Fact]
    public void Long_Sentences_Are_Split_At_Clauses_Then_Words_Keeping_Every_Word()
    {
        List<string> P(string x) => PiperPhonemizer.CodePoints(x).ToList();
        // short: untouched (same list object)
        var shortOnes = new List<List<string>> { P("le-z amˈi.") };
        Assert.Same(shortOnes, PiperPhonemizer.SplitLong(shortOnes, 400));
        // clause boundary before the limit
        var a = PiperPhonemizer.SplitLong(new List<List<string>> { P("aaa bbb, ccc ddd") }, 12);
        Assert.Equal(new[] { "aaa bbb,", "ccc ddd" }, a.Select(x => string.Concat(x)));
        // no clause: last word boundary
        var b = PiperPhonemizer.SplitLong(new List<List<string>> { P("aaa bbb ccc ddd") }, 9);
        Assert.Equal(new[] { "aaa bbb", "ccc ddd" }, b.Select(x => string.Concat(x)));
        // a 1000-word unpunctuated text: pieces <= 400, same espeak words in order
        var words = Enumerable.Range(0, 1000).Select(i => i % 2 == 0 ? "lə-" : "ʃˈa").ToList();
        var big = P(string.Join(" ", words));
        var pieces = PiperPhonemizer.SplitLong(new List<List<string>> { big }, PiperPhonemizer.MaxRunPhonemes);
        Assert.True(pieces.Count > 1);
        Assert.All(pieces, x => Assert.InRange(x.Count, 1, PiperPhonemizer.MaxRunPhonemes));
        Assert.Equal(1000, PiperWordMap.WordCount(pieces));
        Assert.Equal(words, pieces.SelectMany(x => string.Concat(x).Split(' ')));
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(10, 0.667)]
    [InlineData(-10, 1.5)]
    [InlineData(25, 0.667)]
    public void Rate_Maps_To_Length_Scale(int rate, double expected) =>
        Assert.Equal(expected, PiperVoiceConfig.LengthScaleFor(1f, rate), 3);

    [Theory]
    [InlineData(0, 1.1)]
    [InlineData(-8, 1.521)]
    [InlineData(-10, 1.65)]
    [InlineData(10, 0.733)]
    public void Rate_Follows_A_Slower_Voice_Base_Without_A_Flat_End(int rate, double expected) =>
        Assert.Equal(expected, PiperVoiceConfig.LengthScaleFor(1.1f, rate), 3);
}
