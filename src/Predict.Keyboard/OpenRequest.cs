using System;
using System.IO;
using System.Text;

namespace Predict.Keyboard;

/// <summary>
/// A double-clicked <c>.pepite</c> file (plan 12 step 2d, Q3): Explorer starts <c>Pepite.exe --import "&lt;file&gt;"</c>.
/// When a copy already runs, the second one writes the full path to <c>%LOCALAPPDATA%\Pepite-Prediction\ouvrir.txt</c>
/// (temp file + rename, so the reader never sees half a path) and sets <c>Local\Pepite-Prediction.Open</c>; the running
/// copy takes (reads and deletes) the file. Why a file and not the event alone: an event carries no data, and a named pipe
/// or window message would be a second inter-process channel to secure. The running copy accepts only an existing
/// <c>*.pepite</c> of at most 50 MB (the archive limit) — anything in the request file is checked, never trusted.
/// </summary>
internal static class OpenRequest
{
    public const string FileName = "ouvrir.txt";
    public const string Extension = ".pepite";
    public const long MaxBytes = 50L * 1024 * 1024;

    /// <summary>The request folder: <c>%LOCALAPPDATA%\Pepite-Prediction</c> (per account, not roamed).</summary>
    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.FolderName);

    /// <summary>Write the request (full path) atomically; false when the folder cannot be written.</summary>
    public static bool Write(string dir, string file)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, FileName);
            var tmp = target + ".tmp";
            File.WriteAllText(tmp, Path.GetFullPath(file), new UTF8Encoding(false));
            File.Move(tmp, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Read and delete the request; null when there is none (or it cannot be read).</summary>
    public static string? Take(string dir)
    {
        var target = Path.Combine(dir, FileName);
        try
        {
            if (!File.Exists(target)) return null;
            var text = File.ReadAllText(target, Encoding.UTF8).Trim();
            File.Delete(target);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a requested path may be imported: an absolute path to an existing <c>*.pepite</c> of at most
    /// <see cref="MaxBytes"/>. <paramref name="length"/> = the file's size, or null when it does not exist.</summary>
    public static bool Acceptable(string? path, Func<string, long?> length)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        if (!Path.IsPathFullyQualified(path)) return false;
        if (!string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase)) return false;
        return length(path) is long n && n <= MaxBytes;
    }

    /// <summary>The size of an existing file, else null (for <see cref="Acceptable"/>).</summary>
    public static long? FileLength(string path)
    {
        try { var f = new FileInfo(path); return f.Exists ? f.Length : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}
