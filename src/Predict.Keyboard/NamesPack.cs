using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>
/// The shipped « Noms propres » pack (plan 10C, owner Q19b / Q6): a separate, CC BY 4.0 open-data package in the
/// <c>noms\</c> folder next to Pepite.exe (<c>noms.json</c> + <c>images\</c> + its licence and sources), built by
/// <c>Data.Build/pepite-noms/build_names.py</c>, never in the prediction database. Loaded once on a worker thread at start
/// (≈ 0.5 s): the names layer goes into <see cref="PredictionEngine.Names"/> (one reference swap) and the pictures of the
/// names (flags) into a map handed to <see cref="WordPictures"/> on the UI thread. Without the folder the app runs as
/// 0.8.1 (no name proposed by the pack, logged once).
/// </summary>
internal sealed class NamesPack
{
    public string Dir { get; }
    public string Title { get; }
    public string? Edition { get; }
    public string? Attribution { get; }
    public int Names { get; }
    public int Forms { get; }
    public IReadOnlyDictionary<string, string> Pictures { get; }

    private NamesPack(string dir, NamesDocument doc, NamesLexicon lex, IReadOnlyDictionary<string, string> pictures)
    {
        Dir = dir; Title = doc.Titre; Edition = doc.Edition; Attribution = doc.Attribution;
        Names = lex.NameCount; Forms = lex.Count; Pictures = pictures;
    }

    /// <summary>The pack folder: <paramref name="option"/> (<c>--names &lt;dir&gt;</c>) or <c>noms\</c> next to the exe;
    /// null when it has no <c>noms.json</c>.</summary>
    public static string? Find(string? option)
    {
        var dir = option ?? Path.Combine(AppContext.BaseDirectory, "noms");
        return File.Exists(Path.Combine(dir, "noms.json")) ? Path.GetFullPath(dir) : null;
    }

    /// <summary>Read and build the pack on a worker thread (the engine gets its names layer there); null when there is no
    /// usable pack (logged).</summary>
    public static Task<NamesPack?> LoadAsync(string? dir, PredictionEngine engine, Action<string>? log) => Task.Run(() =>
    {
        var p = Load(dir, engine, log);
        if (p != null)
        {
            // the parsed file (≈ 31 000 records) is garbage now: one compacting collection at start, so that it leaves the
            // working set at once (plan 10C gate: the pack adds ≤ 15 MB) instead of at some later full collection
            var sw = Stopwatch.StartNew();
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            log?.Invoke($"names: memory compacted in {sw.ElapsedMilliseconds} ms, managed heap {GC.GetTotalMemory(false) / 1048576} MB");
        }
        return p;
    });

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static NamesPack? Load(string? dir, PredictionEngine engine, Action<string>? log)
    {
        if (dir is null) { log?.Invoke("names: off (pack not found)"); return null; }
        try
        {
            var sw = Stopwatch.StartNew();
            var pics = new Dictionary<string, string>(StringComparer.Ordinal);
            var root = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            // streaming: the pictures are collected as the names are read (no list of records is kept)
            var (lex, doc) = NamesLexicon.Load(dir, engine, also: n =>
            {
                // never a picture on a restricted figure (owner 2026-11-15), whatever the file says
                if (n.Restreint || n.Image is null || !NamesImport.SafeRelative(n.Image)) return;
                var full = Path.GetFullPath(Path.Combine(dir, n.Image.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return;
                pics.TryAdd(n.Nom, full);
                foreach (var v in n.Variantes) pics.TryAdd(v, full);
            });
            engine.Names = lex;
            var p = new NamesPack(dir, doc, lex, pics);
            log?.Invoke($"names: pack « {p.Title} » edition {p.Edition}, {p.Names} names, {p.Forms} forms, {pics.Count} pictured forms, loaded in {sw.ElapsedMilliseconds} ms");
            return p;
        }
        catch (Exception ex)
        {
            log?.Invoke($"names: off (bad pack: {ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }
}
