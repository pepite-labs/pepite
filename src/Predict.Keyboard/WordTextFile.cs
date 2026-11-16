using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Predict.Keyboard;

/// <summary>
/// A word list as plain text (plan 10A): one word per line, so teachers can share a lesson list without profiles.
/// Read: UTF-8 with or without its BOM; a file that is not valid UTF-8 is read as Windows-1252 (an older Notepad
/// file); at most 1 MB; empty lines skipped, words checked by <see cref="WordBook.Check"/>, duplicates dropped.
/// Written: UTF-8 without BOM, Windows line ends. Pure but for the file access.
/// </summary>
internal static class WordTextFile
{
    public const long MaxBytes = 1024 * 1024;
    public const string Extension = ".txt";

    /// <summary>The valid words of <paramref name="bytes"/> in file order, and the number of lines refused.</summary>
    public static (IReadOnlyList<string> Words, int Refused) Parse(byte[] bytes)
    {
        string text;
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try { text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start); }
        catch (DecoderFallbackException) { text = Windows1252().GetString(bytes); }
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int refused = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            if (WordBook.Check(line) != null) { refused++; continue; }
            var w = WordBook.Clean(line);
            if (seen.Add(w)) words.Add(w);
        }
        return (words, refused);
    }

    /// <summary>Read <paramref name="path"/>; throws <see cref="IOException"/> for a file over 1 MB.</summary>
    public static (IReadOnlyList<string> Words, int Refused) Read(string path)
    {
        var fi = new FileInfo(path);
        if (fi.Length > MaxBytes) throw new IOException("file too big");
        return Parse(File.ReadAllBytes(path));
    }

    public static string Format(IEnumerable<string> words) => string.Concat(words.Select(w => w + "\r\n"));

    public static void Write(string path, IEnumerable<string> words) => JsonFileStore.WriteAtomic(path, Format(words));

    private static Encoding Windows1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }
}
