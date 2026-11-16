using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 10C review: a file saved by a tool that read UTF-8 as Windows-1252 shows « P e-acute pite » / « copyright » (the
/// shipped Pepite.exe once carried that in its version info). No source, project or document of the repository may hold such
/// double-encoded text. The patterns are built from char codes so this file does not match itself.</summary>
public class MojibakeLintTests
{
    private static string FreewareDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "THIRD-PARTY-NOTICES.txt")) && Directory.Exists(Path.Combine(d.FullName, "src")))
                return d.FullName;
        throw new DirectoryNotFoundException("repository root not found above " + AppContext.BaseDirectory);
    }

    // « e-acute » (é), « e-grave » (è), « copyright » (©), « << » («), « dash » (— ’ …): UTF-8 bytes read as Windows-1252
    private static readonly string[] Bad =
    {
        "\u00C3\u00A9", "\u00C3\u00A8", "\u00C2\u00A9", "\u00C2\u00AB", "\u00C2\u00BB", "\u00E2\u20AC",
    };

    [Fact]
    public void No_Double_Encoded_Text_In_Freeware_Sources_And_Docs()
    {
        var root = FreewareDir();
        var exts = new[] { ".cs", ".md", ".csproj", ".xaml", ".json", ".txt", ".py" };
        var bad = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "out" + Path.DirectorySeparatorChar) &&
                        new FileInfo(f).Length < 4_000_000)
            .Where(f => { var t = File.ReadAllText(f); return Bad.Any(b => t.Contains(b, StringComparison.Ordinal)); })
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.True(bad.Count == 0, "double-encoded text (UTF-8 read as Windows-1252) in:\n" + string.Join("\n", bad));
    }
}
