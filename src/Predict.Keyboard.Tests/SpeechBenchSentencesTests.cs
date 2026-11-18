using System;
using System.IO;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.10.2: <c>--speech-bench</c> on an installed Pépite exited 2 in the 0.10.1 VM — the sentence file was
/// required before the mode was known, and an install has none. Only the default bench and <c>--wav</c> alone read it.</summary>
public class SpeechBenchSentencesTests
{
    /// <summary>The app's option reader (App.Option).</summary>
    private static string? Option(string[] args, string name) => CommandLine.Option(args, name);

    [Theory]
    [InlineData(new[] { "--speech-bench", "out", "--audio", "null" }, true)]                         // the default bench
    [InlineData(new[] { "--speech-bench", "out", "--wav", "w" }, true)]                                // every sentence as a WAV
    [InlineData(new[] { "--speech-bench", "out", "--wav", "w", "--items", "L01" }, true)]
    [InlineData(new[] { "--speech-bench", "out", "--wav", "w", "--word", "papa" }, false)]             // one word
    [InlineData(new[] { "--speech-bench", "out", "--wav", "w", "--word-items", "words.txt" }, false)]  // its own file
    [InlineData(new[] { "--speech-bench", "out", "--switch" }, false)]                                 // voice switch
    [InlineData(new[] { "--speech-bench", "out", "--wav", "w", "--switch" }, true)]                    // --wav comes first
    [InlineData(new[] { "--speech-bench", "out", "--word", "papa" }, true)]                            // --word needs --wav
    public void Needed_Only_By_The_Modes_That_Read_Sentences(string[] args, bool needed) =>
        Assert.Equal(needed, SpeechBenchSentences.Needed(args, Option));

    [Fact]
    public void Find_Takes_The_Given_File_And_Never_Walks_Up_From_An_Install()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bench-sentences-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(dir, "Programs", "Pepite-Prediction");
        Directory.CreateDirectory(Path.Combine(dir, "development", "voice-test"));
        Directory.CreateDirectory(install);
        try
        {
            var planted = Path.Combine(dir, "development", "voice-test", "sentences.txt");
            File.WriteAllText(planted, "S01\tsentence\tLe chat dort.\n");
            Assert.Null(SpeechBenchSentences.Find(null, install));                      // an install: no walk-up
            Assert.Equal(planted, SpeechBenchSentences.Find(planted, install));          // --sentences <file>
            Assert.Null(SpeechBenchSentences.Find(Path.Combine(dir, "none.txt"), install)); // a missing --sentences file
            Assert.Contains("--sentences", SpeechBenchSentences.Missing);
            Assert.Equal(SpeechBenchSentences.Missing, SpeechBenchSentences.MissingMessage(null));
            Assert.Contains(Path.Combine(dir, "none.txt"), SpeechBenchSentences.MissingMessage(Path.Combine(dir, "none.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
