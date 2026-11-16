using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>A names database opened for import (<see cref="NamesImport.Open"/>): the parsed document and a reader of its
/// pictures by relative path (null when absent, unsafe or too big). Dispose closes a zip.</summary>
internal sealed class NamesSource : IDisposable
{
    public NamesDocument Doc { get; }
    public string Origin { get; }
    private readonly Func<string, byte[]?> _read;
    private readonly IDisposable? _owner;
    public NamesSource(NamesDocument doc, string origin, Func<string, byte[]?> read, IDisposable? owner)
    { Doc = doc; Origin = origin; _read = read; _owner = owner; }
    public byte[]? ReadImage(string rel) => _read(rel);
    public void Dispose() => _owner?.Dispose();
}

/// <summary>The result of an import: the new « noms » list (null when nothing could be imported), the pictures stored and
/// the reasons of what was left out (French, shown to the teacher).</summary>
internal sealed record NamesImportResult(WordListDef? List, int Names, int Images, IReadOnlyList<string> Problems,
                                         IReadOnlyList<string> Licences);

/// <summary>
/// The teacher's import of a names database (plan 10C, owner Q17 / Q20; format: <see cref="NamesFile"/>): a folder or a
/// <c>.zip</c> holding <c>noms.json</c> (canonical) or <c>noms.csv</c> and its pictures, or the <c>noms.json</c> /
/// <c>noms.csv</c> file itself. Checks: ≤ 5 000 names, zip ≤ 50 MB, every picture path relative and inside the database
/// (no <c>..</c>, no absolute path, no drive, no reparse point), PNG or JPEG ≤ 5 MB and ≤ 4096 × 4096 px read from the
/// header before any decoding, then re-encoded as a ≤ 256 px PNG thumbnail named by its hash in
/// <c>profiles\&lt;id&gt;\images\</c> (≤ 500 per profile). A picture needs a licence (the entry's, its source's or the
/// database's; owner Q8: the teacher's responsibility, stored and shown); a picture without one is left out, the name is kept.
/// Pictograms or drawings only, never photos (owner Q17): stated in the dialog, Pépite cannot check it. Pure but for the
/// files it reads and the thumbnails it writes; the re-encoder is injected (WPF in the app).
/// </summary>
internal static class NamesImport
{
    public const long MaxZipBytes = 50L * 1024 * 1024;
    public const long MaxImageBytes = 5L * 1024 * 1024;
    public const long MaxUnzippedBytes = 200L * 1024 * 1024;
    public const int MaxPixels = 4096, Thumb = 256, MaxNames = 5000, MaxImagesPerProfile = 500;
    private static readonly Regex StoredName = new("^[0-9a-f]{16}\\.png$", RegexOptions.Compiled);

    /// <summary>A picture as stored in a profile: a bare <c>&lt;16 hex&gt;.png</c> file name (nothing else is ever opened).</summary>
    public static bool IsStoredImageName(string s) => StoredName.IsMatch(s ?? "");

    /// <summary>Is <paramref name="rel"/> a safe relative path inside a database (no root, drive, <c>..</c> or empty part)?</summary>
    public static bool SafeRelative(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Length > 260) return false;
        var r = rel.Replace('\\', '/');
        if (r.StartsWith("/", StringComparison.Ordinal) || r.Contains(':', StringComparison.Ordinal)) return false;
        foreach (var part in r.Split('/'))
            if (part.Length == 0 || part == "." || part == ".." || part.Trim() != part) return false;
        return true;
    }

    /// <summary>Open a folder, a zip, or a <c>noms.json</c> / <c>noms.csv</c> file. Throws <see cref="InvalidDataException"/>
    /// with a French message when it cannot be read as a names database.</summary>
    /// <param name="csvLicence">for a CSV: the licence the teacher gives the whole file (rows may override it).</param>
    public static NamesSource Open(string path, string? csvLicence = null)
    {
        if (Directory.Exists(path)) return OpenFolder(path, csvLicence);
        if (!File.Exists(path)) throw new InvalidDataException(Strings.NamesOpenNotFound);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".zip") return OpenZip(path, csvLicence);
        if (ext is ".json" or ".csv") return OpenFolder(Path.GetDirectoryName(Path.GetFullPath(path))!, csvLicence, Path.GetFileName(path));
        throw new InvalidDataException(Strings.NamesOpenWhat);
    }

    private static NamesDocument Parse(string name, string text, string titre, string? csvLicence) =>
        name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? NamesFile.ParseCsv(text, titre, MaxNames, false, csvLicence)
            : NamesFile.ParseJson(text, MaxNames, false);

    private static string ReadText(byte[] b)
    {
        try { return new UTF8Encoding(false, true).GetString(b).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(b); }   // a CSV saved by an old spreadsheet
    }

    private static NamesSource OpenFolder(string dir, string? csvLicence, string? file = null)
    {
        var root = Path.GetFullPath(dir);
        string? index = file != null ? Path.Combine(root, file)
                      : File.Exists(Path.Combine(root, "noms.json")) ? Path.Combine(root, "noms.json")
                      : File.Exists(Path.Combine(root, "noms.csv")) ? Path.Combine(root, "noms.csv") : null;
        if (index is null) throw new InvalidDataException(Strings.NamesOpenNoIndex);
        var fi = new FileInfo(index);
        if (fi.Length > MaxZipBytes) throw new InvalidDataException(Strings.NamesOpenTooBig);
        var doc = Parse(index, ReadText(File.ReadAllBytes(index)), new DirectoryInfo(root).Name, csvLicence);
        byte[]? Read(string rel)
        {
            if (!SafeRelative(rel)) return null;
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var f = new FileInfo(full);
            if (!f.Exists || f.Length > MaxImageBytes) return null;
            // no reparse point on the file or on any folder between it and the database root
            for (FileSystemInfo? x = f; x != null && x.FullName.Length > root.Length; x = (x as FileInfo)?.Directory ?? (x as DirectoryInfo)?.Parent)
                if ((x.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return File.ReadAllBytes(full);
        }
        return new NamesSource(doc, root, Read, null);
    }

    private static NamesSource OpenZip(string path, string? csvLicence)
    {
        if (new FileInfo(path).Length > MaxZipBytes) throw new InvalidDataException(Strings.NamesOpenTooBig);
        var zip = ZipFile.OpenRead(path);
        try
        {
            if (zip.Entries.Sum(e => e.Length) > MaxUnzippedBytes) throw new InvalidDataException(Strings.NamesOpenTooBig);
            // noms.json (or noms.csv) at the root, or inside a single top folder
            var index = zip.Entries.Where(e => SafeRelative(e.FullName) && (e.Name.Equals("noms.json", StringComparison.OrdinalIgnoreCase) || e.Name.Equals("noms.csv", StringComparison.OrdinalIgnoreCase)))
                           .OrderBy(e => e.FullName.Count(c => c == '/')).ThenBy(e => e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (index is null) throw new InvalidDataException(Strings.NamesOpenNoIndex);
            if (index.Length > MaxZipBytes) throw new InvalidDataException(Strings.NamesOpenTooBig);
            var prefix = index.FullName[..^index.Name.Length];
            byte[] text;
            using (var s = index.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); text = ms.ToArray(); }
            var doc = Parse(index.Name, ReadText(text), Path.GetFileNameWithoutExtension(path), csvLicence);
            var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries) if (SafeRelative(e.FullName)) byName[e.FullName] = e;
            byte[]? Read(string rel)
            {
                if (!SafeRelative(rel) || !byName.TryGetValue(prefix + rel.Replace('\\', '/'), out var e) || e.Length > MaxImageBytes) return null;
                using var s = e.Open();
                using var ms = new MemoryStream();
                var buf = new byte[81920];
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (ms.Length + n > MaxImageBytes) return null;   // the header lied about the size
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            }
            return new NamesSource(doc, path, Read, zip);
        }
        catch { zip.Dispose(); throw; }
    }

    /// <summary>Width and height from a PNG (IHDR) or JPEG (SOF) header, without decoding; false for anything else.</summary>
    public static bool TryImageSize(byte[] b, out int w, out int h)
    {
        w = h = 0;
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G' && b[12] == 'I' && b[13] == 'H' && b[14] == 'D' && b[15] == 'R')
        {
            w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
            h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
            return w > 0 && h > 0;
        }
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte m = b[i + 1];
                if (m == 0xFF) { i++; continue; }
                if (m is 0xD8 or 0x01 || m >= 0xD0 && m <= 0xD7) { i += 2; continue; }
                int len = (b[i + 2] << 8) | b[i + 3];
                if (m is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    h = (b[i + 5] << 8) | b[i + 6];
                    w = (b[i + 7] << 8) | b[i + 8];
                    return w > 0 && h > 0;
                }
                if (len < 2) return false;
                i += 2 + len;
            }
        }
        return false;
    }

    /// <summary>Why a picture is refused before decoding (null = it may be decoded).</summary>
    public static string? CheckImage(byte[]? b)
    {
        if (b is null) return Strings.NamesImageMissing;
        if (b.Length > MaxImageBytes) return Strings.NamesImageTooBig;
        if (!TryImageSize(b, out var w, out var h)) return Strings.NamesImageFormat;
        if (w > MaxPixels || h > MaxPixels) return string.Format(Strings.NamesImagePixels, w, h);
        return null;
    }

    /// <summary>
    /// Build the « noms » list from <paramref name="src"/>: names checked by <see cref="NamesFile"/>, pictures checked
    /// (<see cref="CheckImage"/>), re-encoded by <paramref name="thumbnail"/> (bytes → PNG bytes ≤ 256 px, null when it cannot
    /// decode) and written to <paramref name="imagesDir"/>. <paramref name="referenced"/>: the pictures the profile's lists
    /// already use (the 500 limit counts distinct pictures in use; a picture already in use, or a file already there, is
    /// not a new one). Nothing is written to the profile's word list here: the caller adds the list.
    /// </summary>
    public static NamesImportResult Build(NamesSource src, string imagesDir, Func<byte[], byte[]?> thumbnail, IReadOnlyCollection<string> referenced,
                                          DateTime now, Action<string>? log = null)
    {
        var problems = new List<string>(src.Doc.Refused);
        var entries = new List<WordEntry>();
        var licences = new SortedSet<string>(StringComparer.Ordinal);
        int images = 0;
        var used = new HashSet<string>(referenced, StringComparer.OrdinalIgnoreCase);
        var day = WordBook.Today(now);
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // same source picture → one file
        foreach (var n in src.Doc.Noms)
        {
            string? file = null;
            if (n.Image != null)
            {
                if (string.IsNullOrWhiteSpace(n.Licence)) problems.Add(string.Format(Strings.NamesImageNoLicence, n.Nom));
                else if (stored.TryGetValue(n.Image, out var again)) file = again;
                else
                {
                    var bytes = src.ReadImage(n.Image);
                    if (CheckImage(bytes) is { } why) problems.Add(string.Format(Strings.NamesImageProblem, n.Nom, why));
                    else
                    {
                        byte[]? png = null;
                        try { png = thumbnail(bytes!); } catch (Exception ex) { log?.Invoke($"names import: picture failed ({ex.GetType().Name})"); }
                        if (png is null) problems.Add(string.Format(Strings.NamesImageUnreadable, n.Nom));
                        else
                        {
                            var name = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant()[..16] + ".png";
                            if (!used.Contains(name) && used.Count >= MaxImagesPerProfile)
                                problems.Add(string.Format(Strings.NamesImageTooMany, n.Nom, MaxImagesPerProfile));
                            else
                            {
                                file = name;
                                Directory.CreateDirectory(imagesDir);
                                var dest = Path.Combine(imagesDir, file);
                                if (!File.Exists(dest)) File.WriteAllBytes(dest, png);
                                stored[n.Image] = file;
                                if (used.Add(file)) images++;
                            }
                        }
                    }
                }
            }
            if (file != null && n.Licence != null) licences.Add(n.Licence);
            var meta = new NameMeta(file, file != null ? n.Licence : null, file != null ? n.Source : null, file != null ? n.Auteur : null,
                                    n.Categorie == "nom" ? null : n.Categorie, n.Variantes.Count > 0 ? n.Variantes : null);
            entries.Add(new WordEntry(n.Nom, WordEntry.Import, day,
                                      meta.Image is null && meta.Categorie is null && meta.Variantes is null ? null : meta));
        }
        if (src.Doc.Licence is { Length: > 0 } dl) licences.Add(dl);
        var title = src.Doc.Titre.Length > 60 ? src.Doc.Titre[..60] : src.Doc.Titre;
        var list = entries.Count == 0 ? null
                 : new WordListDef("", title, true, WordListDef.Names, entries, null, src.Doc.Licence ?? (licences.Count == 1 ? licences.Min : null));
        return new NamesImportResult(list, entries.Count, images, problems, licences.ToList());
    }

    /// <summary>Delete the stored pictures of <paramref name="imagesDir"/> that <paramref name="referenced"/> does not hold (a
    /// list deleted or erased, an import the profile's limits refused). Returns the number deleted.</summary>
    public static int DeleteUnreferenced(string imagesDir, IEnumerable<string> referenced)
    {
        if (!Directory.Exists(imagesDir)) return 0;
        var keep = new HashSet<string>(referenced, StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(imagesDir, "*.png").ToList())
        {
            var name = Path.GetFileName(f);
            if (!IsStoredImageName(name) || keep.Contains(name)) continue;
            try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return n;
    }
}
