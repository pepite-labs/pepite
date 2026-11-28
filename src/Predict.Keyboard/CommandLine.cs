using System;
using System.Collections.Generic;
using System.Linq;

namespace Predict.Keyboard;

/// <summary>The app's command-line parsing (pure; moved out of <c>App</c> in plan 12 so that it is unit-tested): an option
/// with a value takes the next argument, which is then never a positional argument (the prediction DB path).</summary>
internal static class CommandLine
{
    /// <summary>Plan 12: <c>--import &lt;file.pepite&gt;</c> (the <c>.pepite</c> file association) and
    /// <c>--check-config &lt;file&gt;</c> (the installer's check of a school's machine file) take a value, so a double-clicked
    /// file is never taken as the database.</summary>
    public const string Import = "--import", CheckConfig = "--check-config";

    /// <summary>LibreOffice phase 1 (test switch): IAccessible2 is never called — Writer gets the 0.11.0 keys-only behaviour
    /// (the VM control, and the negation plan's « unreadable host »). A flag, no value.</summary>
    public const string NoIa2 = "--no-ia2";

    public static readonly string[] ValueOptions =
    {
        "--exit-after", "--pictos", "--render-bar", "--dpi", "--size", "--font", "--spacing", "--mark", "--listening",
        "--speech-bench", "--profile", "--audio", "--voices", "--sentences", "--volume", "--word-reps", "--sentence-reps", "--ort-spinning", "--long-text", "--max-run",
        "--voice", "--wav", "--items", "--keys-only-replace", "--variant", "--word", "--word-cache", "--word-items", "--word-suffix", "--carrier", "--phoneme-fix",
        "--names", "--grammar", Import, CheckConfig,
    };

    /// <summary>The value after <paramref name="name"/>, or null.</summary>
    public static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Arguments that are neither an option nor an option's value.</summary>
    public static List<string> Positional(string[] args)
    {
        var l = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (ValueOptions.Contains(args[i])) { i++; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) l.Add(args[i]);
        }
        return l;
    }
}
