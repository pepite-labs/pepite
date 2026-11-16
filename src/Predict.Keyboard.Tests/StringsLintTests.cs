using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>D14: every user-visible string lives in Strings.cs (an English resource file later replaces its values).</summary>
public class StringsLintTests
{
    private static string SourceDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Predict.Keyboard");
            if (File.Exists(Path.Combine(p, "Strings.cs"))) return p;
        }
        throw new DirectoryNotFoundException("Predict.Keyboard sources not found above " + AppContext.BaseDirectory);
    }

    private static readonly Regex XamlLiteral = new(@"\b(Text|Content|Title|Header|ToolTip)=""(?!\{)([^""]*\p{L}[^""]*)""");

    [Fact]
    public void Xaml_Has_No_Literal_Text()
    {
        var bad = Directory.GetFiles(SourceDir(), "*.xaml")
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
            .Where(x => XamlLiteral.IsMatch(x.l))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.l.Trim()}")
            .ToList();
        Assert.True(bad.Count == 0, "XAML literals (use {x:Static local:Strings.X}):\n" + string.Join("\n", bad));
    }

    private static readonly Regex StringLiteral = new(@"\$?@?""(?:[^""\\]|\\.)*""");
    private static readonly Regex French = new(@"[à-ÿœÀ-ŸŒ]");
    private static readonly Regex LogCall = new(@"\b_?[Ll]og\??\.Invoke\(|\blog\(|\bLog\?\.Invoke\(");

    [Fact]
    public void No_French_String_Literal_Outside_Strings_Cs()
    {
        var bad = Directory.GetFiles(SourceDir(), "*.cs")
            .Where(f => !f.EndsWith("Strings.cs", StringComparison.Ordinal) && !f.EndsWith(".g.cs", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
            .Where(x =>
            {
                var code = x.l;
                int c = code.IndexOf("//", StringComparison.Ordinal);
                var trimmed = code.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*")) return false;   // comments
                if (LogCall.IsMatch(code)) return false;                                 // log lines: developer text
                return StringLiteral.Matches(code).Any(m => (c < 0 || m.Index < c || IsInsideLiteral(code, c)) && French.IsMatch(m.Value));
            })
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.l.Trim()}")
            .ToList();
        Assert.True(bad.Count == 0, "French literals outside Strings.cs:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Settings_Window_Access_Keys_Are_Unique()
    {
        var xaml = File.ReadAllText(Path.Combine(SourceDir(), "SettingsWindow.xaml"));
        var keys = Regex.Matches(xaml, @"Strings\.(\w+)\}").Select(m => m.Groups[1].Value).Distinct()
            .Select(n => (n, v: typeof(Strings).GetField(n)?.GetValue(null) as string))
            .Where(x => x.v != null && x.v.IndexOf('_') is int i && i >= 0 && i + 1 < x.v.Length)
            .Select(x => (x.n, key: char.ToLowerInvariant(x.v![x.v.IndexOf('_') + 1])))
            .ToList();
        Assert.True(keys.Count >= 15, "access keys found: " + keys.Count);
        var dup = keys.GroupBy(k => k.key).Where(g => g.Count() > 1).Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.n))}").ToList();
        Assert.True(dup.Count == 0, "duplicate access keys: " + string.Join("; ", dup));
    }

    /// <summary>Is the "//" at <paramref name="pos"/> inside a string literal (a URL)?</summary>
    private static bool IsInsideLiteral(string line, int pos) =>
        StringLiteral.Matches(line).Any(m => m.Index < pos && pos < m.Index + m.Length);
}
