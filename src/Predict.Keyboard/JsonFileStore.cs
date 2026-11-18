using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Predict.Keyboard;

/// <summary>
/// One small JSON file kept whole as a <see cref="JsonObject"/> (keys this version does not know are written back
/// unchanged), factored out of <see cref="SettingsStore"/> for the profile files (plan 09, step 2). Load once (missing →
/// empty; bad JSON → renamed <c>&lt;file&gt;.bad-&lt;stamp&gt;</c>, or — <c>moveBadAside: false</c>, the profiles — kept and
/// marked <see cref="Unreadable"/>; a file that exists but cannot be read is never overwritten: <see cref="ReadOnly"/>);
/// writes are serialised on a background task chain (roaming / redirected <c>%APPDATA%</c> can be slow) and atomic
/// (<c>.tmp</c> flushed to disk, then renamed). Thread-safe.
/// </summary>
internal sealed class JsonFileStore
{
    private readonly string _path, _label;
    private readonly Action<string>? _log;
    private readonly bool _moveBadAside;
    private readonly int? _schema;
    private readonly object _lock = new();
    private JsonObject _root = new();           // guarded by _lock
    private Task _writes = Task.CompletedTask;  // guarded by _lock: the chain of pending writes
    private volatile bool _readOnly;            // a file that could not be read / moved aside: never overwrite it

    /// <param name="label">log prefix (« settings », « profiles »…).</param>
    /// <param name="schema">written as <c>schema</c> on every save; null = left as it is.</param>
    public JsonFileStore(string path, Action<string>? log, string label = "settings", bool moveBadAside = true, int? schema = null)
    {
        _path = path;
        _log = log;
        _label = label;
        _moveBadAside = moveBadAside;
        _schema = schema;
    }

    public string FilePath => _path;
    /// <summary>Nothing is written this session (an unreadable file is kept as it is).</summary>
    public bool ReadOnly => _readOnly;
    /// <summary>The file existed but its content could not be used (bad JSON, read error).</summary>
    public bool Unreadable { get; private set; }
    public bool Existed { get; private set; }

    /// <summary>Read the file (see the class summary). Returns a copy of the whole document.</summary>
    public JsonObject Load()
    {
        JsonObject root = new();
        Unreadable = false;
        try
        {
            Existed = File.Exists(_path);
            if (Existed)
            {
                var text = File.ReadAllText(_path);
                try { root = JsonNode.Parse(text) as JsonObject ?? throw new JsonException("not an object"); }
                catch (JsonException ex)
                {
                    Unreadable = true;
                    var why = ex.Message.Split('\n')[0];
                    if (!_moveBadAside || _readOnly)   // read-only (an in-memory session): never moved aside either
                    {
                        _readOnly = true;
                        _log?.Invoke($"{_label}: {_path} is not valid JSON ({why}); kept as it is, not saved this session");
                    }
                    else
                    {
                        var bad = _path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        try
                        {
                            File.Move(_path, bad, overwrite: true);
                            _log?.Invoke($"{_label}: {_path} is not valid JSON ({why}), moved to {Path.GetFileName(bad)}; using defaults");
                        }
                        catch (Exception mx) when (mx is IOException or UnauthorizedAccessException)
                        {
                            // keep the user's file intact: defaults for this session, nothing written over it
                            _readOnly = true;
                            _log?.Invoke($"{_label}: {_path} is not valid JSON ({why}) and could not be moved aside ({mx.GetType().Name}); " +
                                         $"using defaults, {_label} will not be saved this session");
                        }
                    }
                    root = new JsonObject();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the file exists but cannot be read (locked, ACL): never overwrite what we could not read
            _readOnly = _readOnly || File.Exists(_path);
            Unreadable = _readOnly;
            _log?.Invoke($"{_label}: cannot read {_path} ({ex.GetType().Name}); using defaults" +
                         (_readOnly ? $", {_label} will not be saved this session" : ""));
        }
        lock (_lock) _root = root;
        return Copy();
    }

    /// <summary>Nothing is written from now on (an in-memory session after a start-up failure); called before
    /// <see cref="Load"/>, a bad file is not moved aside either.</summary>
    public void MakeReadOnly() => _readOnly = true;

    /// <summary>Drop the parsed document from memory once the caller keeps its own model of it (plan 10A: a word file of
    /// 10 000 words is several MB of JSON nodes). Later <see cref="Update"/> calls start from an empty document, so the
    /// caller must write the whole content (as <see cref="WordListStore.Set"/> does).</summary>
    public void Unload() { lock (_lock) _root = new JsonObject(); }

    /// <summary>A deep copy of the document.</summary>
    public JsonObject Copy() { lock (_lock) return Json.Clone(_root); }

    /// <summary>Read under the lock (no copy: <paramref name="f"/> must not keep the nodes).</summary>
    public T Read<T>(Func<JsonObject, T> f) { lock (_lock) return f(_root); }

    /// <summary>Change the document and write it in the background.</summary>
    public Task Update(Action<JsonObject> f)
    {
        lock (_lock)
        {
            f(_root);
            return QueueWrite();
        }
    }

    /// <summary>Change the document and write it now, on this thread (migration: each step on disk before the next).
    /// Throws when the write fails or the file is read-only.</summary>
    public void UpdateNow(Action<JsonObject> f)
    {
        if (_readOnly) throw new IOException($"{_path} is kept read-only this session");
        string text;
        lock (_lock)
        {
            f(_root);
            text = Snapshot();
        }
        Flush();
        WriteNow(text);
    }

    /// <summary>How long <see cref="Flush"/> waits when no timeout is given: 3 s, so that the exit never hangs on a stuck
    /// disk. The tests raise it (a test hook): on a busy CI runner a queued write can take longer, and a test that reopens
    /// the file right after <see cref="Flush"/> must find it written.</summary>
    internal static volatile int DefaultFlushTimeoutMs = 3000;

    /// <summary>Wait for the pending writes (exit, tests).</summary>
    public void Flush(int? timeoutMs = null)
    {
        Task t;
        lock (_lock) t = _writes;
        try { t.Wait(timeoutMs ?? DefaultFlushTimeoutMs); } catch (AggregateException) { }
    }

    private string Snapshot()   // under _lock
    {
        if (_schema is int s) _root["schema"] = s;
        return _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private Task QueueWrite()   // under _lock
    {
        if (_readOnly) { _log?.Invoke($"{_label}: not saved (the unreadable {Path.GetFileName(_path)} is kept as is)"); return Task.CompletedTask; }
        var text = Snapshot();
        _writes = _writes.ContinueWith(_ =>
        {
            try { WriteNow(text); }
            catch (Exception ex) { _log?.Invoke($"{_label}: write failed ({ex.GetType().Name}: {ex.Message})"); }
        }, TaskScheduler.Default);
        return _writes;
    }

    /// <summary>Atomic write: <c>.tmp</c> flushed to disk, then renamed over the file (a crash leaves the old or the new
    /// file, never a torn one).</summary>
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        try
        {
            using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
                f.Write(bytes, 0, bytes.Length);
                f.Flush(flushToDisk: true);
            }
            try { File.Move(tmp, path, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a reader (antivirus, indexer, a backup zipping the folder) may hold the target for a moment: once more
                System.Threading.Thread.Sleep(50);
                File.Move(tmp, path, overwrite: true);
            }
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
    }

    private void WriteNow(string text) => WriteAtomic(_path, text);
}
