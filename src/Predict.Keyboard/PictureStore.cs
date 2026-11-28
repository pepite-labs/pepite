using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Predict.Keyboard;

/// <summary>
/// Read-only access to the offline ARASAAC pack <c>pictos.sqlite</c> (built by Data.Build/build_pictos.py:
/// <c>picto(id, png)</c>, <c>word_picto(form, picto_id)</c>, <c>meta(key, value)</c>, schema 1).
///
/// Threading: the word → picture-id map is loaded on a background task (<see cref="Has"/> is false until
/// then); PNGs are read and decoded on one dedicated <c>picto-loader</c> thread with its own read-only
/// connection, so the UI thread never touches SQLite or a decoder. The UI asks with a generation number:
/// requests older than the latest generation are skipped (the bar has moved on). Decoded images go into
/// a small LRU keyed by picture id (several forms share a picture). The decoder is injected
/// (<c>byte[] → frozen ImageSource</c> in the app) so the store itself stays free of WPF and testable.
/// </summary>
internal sealed class PictureStore : IDisposable
{
    public const int SchemaVersion = 1;
    public const int CacheCapacity = 320;   // holds the startup prefetch (≤ 300 pictures) + the current bar

    private readonly string _path;
    private readonly Func<byte[], object> _decode;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, string> _meta;
    private volatile Dictionary<string, int>? _words;                 // null until loaded
    private readonly BlockingCollection<Job> _jobs = new();
    private readonly Thread _loader;
    private readonly Lru _cache;
    private int _latestGen = int.MinValue;
    private SqliteConnection? _con;                                    // loader thread only
    private readonly CancellationTokenSource _stop = new();            // Dispose: abandon queued work at once
    private readonly HashSet<int> _failed = new();                     // loader thread only: ids that failed once

    private sealed record Job(int Generation, IReadOnlyList<string> Words, Action<int, string, object>? Ready);

    public string Attribution { get; }
    public string? BuildDate => _meta.TryGetValue("build_date", out var v) ? v : null;
    public int PictureCount { get; }
    public int WordCount => _words?.Count ?? 0;
    public Task Loaded { get; }

    private PictureStore(string path, Dictionary<string, string> meta, int pictures, Func<byte[], object> decode,
                         Action<string>? log, int cacheCapacity)
    {
        _path = path;
        _meta = meta;
        _decode = decode;
        _log = log;
        _cache = new Lru(cacheCapacity);
        PictureCount = pictures;
        Attribution = meta.TryGetValue("attribution_fr", out var a) && !string.IsNullOrWhiteSpace(a) ? a : Strings.ArasaacAttribution;
        Loaded = Task.Run(LoadWords);
        _loader = new Thread(Loop) { IsBackground = true, Name = "picto-loader", Priority = ThreadPriority.BelowNormal };
        if (OperatingSystem.IsWindows()) _loader.SetApartmentState(ApartmentState.STA);   // WPF imaging prefers STA
        _loader.Start();
    }

    /// <summary>Opens the pack; null (logged) when the file is missing, unreadable or not schema 1.</summary>
    public static PictureStore? Open(string? path, Func<byte[], object> decode, Action<string>? log,
                                     int cacheCapacity = CacheCapacity)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) { log?.Invoke("pictures: off (pack not found)"); return null; }
        try
        {
            using var con = OpenReadOnly(path);
            var meta = new Dictionary<string, string>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT key, value FROM meta";
                using var r = cmd.ExecuteReader();
                while (r.Read()) meta[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
            if (!meta.TryGetValue("schema", out var s) || s != SchemaVersion.ToString())
            {
                log?.Invoke($"pictures: off (pack schema '{s}' not {SchemaVersion})");
                return null;
            }
            int pictures;
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM picto";
                pictures = Convert.ToInt32(cmd.ExecuteScalar());
                cmd.CommandText = "SELECT form, picto_id FROM word_picto LIMIT 1";   // table and columns exist
                cmd.ExecuteScalar();
            }
            return new PictureStore(path, meta, pictures, decode, log, cacheCapacity);
        }
        catch (Exception ex)
        {
            log?.Invoke($"pictures: off (bad pack: {ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var con = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private }.ToString());
        con.Open();
        return con;
    }

    private void LoadWords()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var con = OpenReadOnly(_path);
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT form, picto_id FROM word_picto";
            var d = new Dictionary<string, int>(40000, StringComparer.Ordinal);
            using var r = cmd.ExecuteReader();
            while (r.Read()) d[r.GetString(0)] = r.GetInt32(1);
            _words = d;
            _log?.Invoke($"pictos: {d.Count} words, {PictureCount} pictures, loaded in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { _log?.Invoke($"pictos: word list failed: {ex.Message}"); }
    }

    private static string Key(string word) => word.ToLowerInvariant();

    /// <summary>The picture key of a bar tile: its word in lowercase, or null for a personal name stored with a capital
    /// (plan 10A, Q13: « Pierre » the pupil never shows the stone; 10C gives names their own pictures). 0.10.2 (glued words,
    /// owner Q6): a joined tile shows the picture of its main word (« l'école » → « école »).</summary>
    public static string? KeyFor(Predict.Core.Suggestion s) =>
        s.Name ? null : Key(PictureForm(s));

    /// <summary>0.10.2 (glued words, owner Q6): the form a tile's picture is looked up by — the main word of a joined tile
    /// (« l'école » → « école »), but a name keeps its full form (review: « N'Diaye », « M'Bappé », « D'Artagnan » look like
    /// joined words). 0.11.0 (glued words follow-up): a tile of several words shows the main word of its last part when that
    /// part is a joined form (« je t'aime » → « aime », a green tile only); any other keeps its full form (« il y a »), which no shipped picture
    /// has (the pictures are single words) — only a teacher's picture of that exact form (a personal word with spaces).</summary>
    public static string PictureForm(Predict.Core.Suggestion s)
    {
        if (s.Name) return s.Word;
        int sp = s.Word.LastIndexOf(' ');
        if (sp >= 0)   // only the engine's glued tiles (green); a personal « chemin d'accès » keeps its full form
            return s.Kind == Predict.Core.MatchKind.Phonetic && Predict.Core.Elision.IsJoined(s.Word[(sp + 1)..], out _, out var last) ? last : s.Word;
        return Predict.Core.Elision.IsJoined(s.Word, out _, out var main) ? main : s.Word;
    }

    /// <summary>Is there a picture for this word? Any thread; false until the word list is loaded.</summary>
    public bool Has(string word) => _words is { } w && w.ContainsKey(Key(word));

    /// <summary>The decoded picture if it is already in memory. UI thread, O(1), never touches the DB.</summary>
    public bool TryGetCached(string word, out object? image)
    {
        image = null;
        return _words is { } w && w.TryGetValue(Key(word), out int id) && _cache.TryGet(id, out image);
    }

    /// <summary>Queue the pictures of <paramref name="words"/>; <paramref name="ready"/> is called on the
    /// loader thread for each one found (the caller marshals to its UI thread). A newer generation
    /// cancels what is left of older requests.</summary>
    public void Request(int generation, IReadOnlyList<string> words, Action<int, string, object> ready)
    {
        InterlockedMax(ref _latestGen, generation);
        Enqueue(new Job(generation, words, ready));
    }

    /// <summary>Queue a job unless the store is being disposed (never throws).</summary>
    private bool Enqueue(Job job)
    {
        if (_stop.IsCancellationRequested) return false;
        try { return _jobs.TryAdd(job); }
        catch (InvalidOperationException) { return false; }   // adding completed (Dispose raced us)
    }

    /// <summary>Warm the cache in the background (e.g. the most frequent forms), lowest priority.</summary>
    public void Prefetch(IReadOnlyList<string> words)
    {
        Enqueue(new Job(int.MinValue, words, null));
    }

    /// <summary>Blocking read + decode (render mode, warm-up). Any thread; null when there is no picture.</summary>
    public object? GetNow(string word)
    {
        Loaded.Wait();
        if (_words is not { } w || !w.TryGetValue(Key(word), out int id)) return null;
        if (_cache.TryGet(id, out var img)) return img;
        using var con = OpenReadOnly(_path);
        img = Fetch(con, id);
        if (img != null) _cache.Put(id, img);
        return img;
    }

    private object? Fetch(SqliteConnection con, int id)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT png FROM picto WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is byte[] png ? _decode(png) : null;
    }

    private void Loop()
    {
        var stop = _stop.Token;
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable(stop))
            {
                try
                {
                    Loaded.Wait(stop);
                    if (_words is not { } words) continue;
                    _con ??= OpenReadOnly(_path);
                    RunJob(job, words, stop);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _log?.Invoke($"picto-loader error: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }   // disposed while waiting for work
        finally { _con?.Dispose(); }
    }

    private void RunJob(Job job, Dictionary<string, int> words, CancellationToken stop)
    {
        bool prefetch = job.Ready is null;
        if (prefetch) RunPrefetch(job, words, stop);
        else foreach (var word in job.Words) if (!Step(job, word, words, stop)) return;
    }

    private void RunPrefetch(Job job, Dictionary<string, int> words, CancellationToken stop)
    {
        foreach (var word in job.Words)
        {
            if (_jobs.Count > 0) { Enqueue(job with { Words = Rest(job.Words, word) }); return; }   // yield to a bar
            if (!Step(job, word, words, stop)) return;
        }
        _log?.Invoke($"pictos: prefetch done, {_cache.Count} pictures in memory");
    }

    /// <returns>false to stop the job (disposed, or a newer bar exists).</returns>
    private bool Step(Job job, string word, Dictionary<string, int> words, CancellationToken stop)
    {
        if (stop.IsCancellationRequested) return false;
        // a newer bar exists: drop the rest of this request
        if (job.Ready is not null && job.Generation < Volatile.Read(ref _latestGen)) return false;
        if (!words.TryGetValue(Key(word), out int id) || _failed.Contains(id)) return true;
        try
        {
            if (!_cache.TryGet(id, out var img))
            {
                img = Fetch(_con!, id);
                if (img is null) { _failed.Add(id); return true; }
                _cache.Put(id, img);
            }
            if (stop.IsCancellationRequested) return false;   // never post to a closing dispatcher
            job.Ready?.Invoke(job.Generation, word, img!);
        }
        catch (Exception ex)
        {
            // one bad picture (corrupt blob, decoder error) must not cost the rest of the bar,
            // nor be retried on every bar
            _failed.Add(id);
            _log?.Invoke($"picto-loader: picture {id} ({word}) failed: {ex.GetType().Name}: {ex.Message}");
        }
        return true;
    }

    private static IReadOnlyList<string> Rest(IReadOnlyList<string> words, string from)
    {
        var l = new List<string>();
        bool on = false;
        foreach (var w in words) { if (ReferenceEquals(w, from)) on = true; if (on) l.Add(w); }
        return l;
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int cur;
        while ((cur = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
    }

    /// <summary>Number of decoded pictures held (tests, logs).</summary>
    public int CachedCount => _cache.Count;

    /// <summary>Stops the loader at once: queued requests and the prefetch are abandoned (checked per word),
    /// so quitting during the startup prefetch neither blocks OnExit nor posts to a closing dispatcher.</summary>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _jobs.CompleteAdding();
        _loader.Join(1000);
    }

    /// <summary>Thread-safe LRU of decoded pictures by picture id.</summary>
    private sealed class Lru
    {
        private readonly int _cap;
        private readonly Dictionary<int, LinkedListNode<(int Id, object Img)>> _map = new();
        private readonly LinkedList<(int Id, object Img)> _order = new();
        private readonly object _lock = new();

        public Lru(int cap) => _cap = Math.Max(1, cap);
        public int Count { get { lock (_lock) return _map.Count; } }

        public bool TryGet(int id, out object? img)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(id, out var n)) { _order.Remove(n); _order.AddFirst(n); img = n.Value.Img; return true; }
            }
            img = null;
            return false;
        }

        public void Put(int id, object img)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(id, out var n)) { _order.Remove(n); _map.Remove(id); }
                _map[id] = _order.AddFirst((id, img));
                while (_map.Count > _cap) { var last = _order.Last!; _order.RemoveLast(); _map.Remove(last.Value.Id); }
            }
        }
    }
}
