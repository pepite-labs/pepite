#if HAVE_ESPEAK
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>The espeak-ng built by Data.Build/build_espeak.ps1, through our P/Invoke, against the raw clauses piper-tts
/// 1.8.0's espeakbridge returned for each voice (plans 08a / 08b golden test, native part; compiled only when the build is staged).</summary>
public class EspeakGoldenTests
{
    private static readonly string Data = Path.Combine(AppContext.BaseDirectory, "espeak-ng-data");

    [Fact]
    public void Version_Is_The_Piper_Pin()
    {
        Assert.Equal("1.52.0.1", EspeakNative.Initialize(Data));
    }

    [Theory]
    [InlineData("siwis")]
    [InlineData("pierre")]
    public void Raw_Clauses_Equal_Piper_40_Of_40(string voice)
    {
        EspeakNative.Initialize(Data);
        var bad = new List<string>();
        foreach (var it in PiperGolden.For(voice).ItemsOf)
        {
            var got = EspeakNative.Clauses(TextCleanup.ForEspeak(it.Text), "fr");
            if (!got.SequenceEqual(it.Clauses)) bad.Add($"{it.Id}: {string.Join(" | ", got)}");
        }
        Assert.True(bad.Count == 0, $"{40 - bad.Count}/40; mismatches:\n{string.Join("\n", bad)}");
    }

    [Theory]
    [InlineData("siwis")]
    [InlineData("pierre")]
    public void End_To_End_Ids_Equal_Piper_40_Of_40(string voice)
    {
        EspeakNative.Initialize(Data);
        var g = PiperGolden.For(voice);
        int ok = 0;
        foreach (var it in g.ItemsOf)
        {
            var s = PiperPhonemizer.Sentences(EspeakNative.Clauses(TextCleanup.ForEspeak(it.Text), "fr"), g.ClustersOf);
            var ids = s.Select(x => PiperPhonemizer.Ids(x, g.IdMapOf, out _)).ToList();
            if (ids.Count == it.Ids.Count && ids.Zip(it.Ids).All(p => p.First.SequenceEqual(p.Second))) ok++;
        }
        Assert.Equal(40, ok);
    }

    [Fact]
    public void Narrow_No_Break_Space_Gives_The_Same_Ids()
    {
        EspeakNative.Initialize(Data);
        long[] Ids(string t) => PiperPhonemizer.Ids(PiperPhonemizer.Sentences(EspeakNative.Clauses(TextCleanup.ForEspeak(t), "fr"), null).Single(), PiperGolden.IdMap, out _);
        Assert.Equal(Ids("Quelle belle journée !"), Ids("Quelle belle journée !"));
        Assert.Equal(Ids("J'ai oublié."), Ids("J’ai oublié."));
    }

    [Fact]
    public void Missing_Data_Is_An_Error_Not_An_Exit()
    {
        var empty = Path.Combine(Path.GetTempPath(), "predict-no-espeak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try { Assert.Throws<FileNotFoundException>(() => EspeakNative.Initialize(empty)); }
        finally { Directory.Delete(empty); }
    }
}
#endif
