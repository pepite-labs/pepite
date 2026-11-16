using System;
using System.IO;
using System.Linq;

namespace Predict.Keyboard;

/// <summary>
/// Where the app looks for its data files and folders (plan 12 step 2a): next to the exe, and — for development builds
/// only — in <c>&lt;a parent&gt;\Data.Build\out\</c>. Why the walk-up is restricted: on a per-machine install (Program
/// Files, or a school's <c>/DIR=D:\…</c>) a standard user may create folders in a drive root or in another parent, so
/// <c>C:\Data.Build\out\pictos.sqlite</c> (or <c>fonts\</c>) planted there would be loaded by every account. A publish
/// folder or an install finds its data next to the exe or not at all.
/// </summary>
internal static class DataSearch
{
    /// <summary>The walk-up is allowed only when the exe runs from a build output: a <c>bin\Debug\</c> or
    /// <c>bin\Release\</c> segment whose parent folder holds a <c>*.csproj</c> (<c>src\Predict.Keyboard\bin\Release\…</c>,
    /// the test project's <c>bin\Release\net6.0\</c>). A folder merely named like that at install time does not qualify
    /// without a project file; an all-users install folder is protected by the installer anyway (plan 12).
    /// <paramref name="isProjectDir"/> is injected for tests (default: the folder holds a <c>*.csproj</c>).</summary>
    public static bool MayWalkUp(string baseDir, Func<string, bool>? isProjectDir = null)
    {
        isProjectDir ??= HasProjectFile;
        var parts = baseDir.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i + 1 < parts.Length; i++)
            if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase)
                && (parts[i + 1].Equals("Debug", StringComparison.OrdinalIgnoreCase) || parts[i + 1].Equals("Release", StringComparison.OrdinalIgnoreCase))
                && i >= 2   // a project folder below the drive root
                && isProjectDir(string.Join("\\", parts, 0, i)))
                return true;
        return false;
    }

    private static bool HasProjectFile(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.csproj").Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary><paramref name="relative"/> next to <paramref name="baseDir"/>, else (development builds only)
    /// <c>Data.Build\out\&lt;relative&gt;</c> in the nearest parent that has it; null when none. <paramref name="exists"/>
    /// is <see cref="File.Exists"/> or <see cref="Directory.Exists"/> (injected for tests).</summary>
    public static string? Find(string baseDir, string relative, Func<string, bool> exists, Func<string, bool>? isProjectDir = null)
    {
        var here = Path.Combine(baseDir, relative);
        if (exists(here)) return here;
        if (!MayWalkUp(baseDir, isProjectDir)) return null;
        for (var d = new DirectoryInfo(baseDir); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Data.Build", "out", relative);
            if (exists(p)) return p;
        }
        return null;
    }

    /// <summary>A file of the app's data (the exe's folder).</summary>
    public static string? FindFile(string name) => Find(AppContext.BaseDirectory, name, File.Exists);

    /// <summary>A folder of the app's data (the exe's folder).</summary>
    public static string? FindDir(string name) => Find(AppContext.BaseDirectory, name, Directory.Exists);

    /// <summary>Parents of <paramref name="baseDir"/> a development tool may search (none for a publish or an install).</summary>
    public static System.Collections.Generic.IEnumerable<DirectoryInfo> DevParents(string baseDir, Func<string, bool>? isProjectDir = null)
    {
        if (!MayWalkUp(baseDir, isProjectDir)) return Enumerable.Empty<DirectoryInfo>();
        return Walk(new DirectoryInfo(baseDir));

        static System.Collections.Generic.IEnumerable<DirectoryInfo> Walk(DirectoryInfo? d)
        {
            for (; d != null; d = d.Parent) yield return d;
        }
    }
}
