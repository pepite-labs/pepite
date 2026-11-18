using System;
using System.Collections.Generic;
using System.IO;

namespace Predict.Keyboard;

/// <summary>Which <c>--speech-bench</c> modes read the sentence file, and where it is. An installed Pépite has no
/// <c>development\voice-test\sentences.txt</c>: in the 0.10.1 VM, <c>--speech-bench</c> on the installed app exited 2
/// with its reason only in <c>bench-error.txt</c>, because the file was required before the mode was known, also by the
/// modes that never read it (<c>--word</c>, <c>--word-items</c>, <c>--switch</c>).</summary>
internal static class SpeechBenchSentences
{
    internal const string Missing =
        "sentences.txt not found: pass --sentences <file> (development\\voice-test\\sentences.txt of a source checkout; an installed copy does not ship it)";

    /// <summary>The bench.md line when no sentence file was found: the given <c>--sentences</c> path, else
    /// <see cref="Missing"/>.</summary>
    internal static string MissingMessage(string? given) =>
        given is null ? Missing : $"--sentences {given}: file not found";

    /// <summary>True when the mode chosen by <paramref name="args"/> reads the sentence items: the default bench and
    /// <c>--wav</c> alone. The order follows <c>SpeechBench.Run</c>: <c>--wav</c> with <c>--word-items</c> or
    /// <c>--word</c>, then <c>--wav</c>, then <c>--switch</c>, then the default bench.</summary>
    internal static bool Needed(string[] args, Func<string[], string, string?> option)
    {
        if (option(args, "--wav") != null) return option(args, "--word-items") == null && option(args, "--word") == null;
        return Array.IndexOf(args, "--switch") < 0;
    }

    /// <summary>The sentence file: <paramref name="given"/> (<c>--sentences</c>) when it exists, else the first
    /// <c>development\voice-test\sentences.txt</c> above <paramref name="baseDir"/> (a dev build); null = none.</summary>
    internal static string? Find(string? given, string baseDir)
    {
        if (given != null) return File.Exists(given) ? given : null;
        foreach (var d in DataSearch.DevParents(baseDir))
        {
            var p = Path.Combine(d.FullName, "development", "voice-test", "sentences.txt");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
