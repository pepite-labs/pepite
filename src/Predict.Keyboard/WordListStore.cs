using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>
/// The word files (plan 10A): one <see cref="JsonFileStore"/> per <c>profiles\&lt;id&gt;\mots.json</c>, loaded when first
/// asked and kept (UI thread). No file until the first change (an upgrade from 0.7 writes nothing); a bad file is moved
/// aside (<c>.bad-&lt;stamp&gt;</c>) and the profile starts with no words; a file that cannot be read is never overwritten.
/// Writes are atomic and in the background (<see cref="JsonFileStore"/>); edits are rare (no learning), so each one is
/// written at once and <see cref="Flush"/> waits for them (switch, session end, exit, backups). The profiles backups
/// (<c>sauvegardes\*.zip</c> of <c>profiles\</c>) include these files with no change. In an in-memory session nothing
/// is written. Thread-safe (the snapshot is built on a worker thread, plan 10A review).
/// </summary>
internal sealed class WordListStore
{
    public const string FileName = "mots.json";

    private readonly string _dir;
    private readonly Action<string>? _log;
    private readonly bool _inMemory;
    private readonly Dictionary<string, (JsonFileStore File, WordBook Book)> _open = new(StringComparer.Ordinal);

    /// <param name="profilesDir">the <c>profiles</c> folder (<see cref="ProfileStore.ProfilesDir"/>).</param>
    public WordListStore(string profilesDir, Action<string>? log, bool inMemory = false)
    {
        _dir = profilesDir;
        _log = log;
        _inMemory = inMemory;
    }

    public string PathOf(string id) => Path.Combine(_dir, id, FileName);

    /// <summary>The words of <paramref name="id"/> (empty when it has none).</summary>
    public WordBook Get(string id) => Entry(id).Book;

    /// <summary>Test hook: runs on the reading thread just before a file is read, outside the lock.</summary>
    internal Action<string>? BeforeRead { get; set; }

    /// <summary>The cached entry, or the file read <b>outside the lock</b> (a 5 000-word file read on the worker must not
    /// make the UI thread wait in <see cref="TryGetLoaded"/>) and inserted unless another thread was first.</summary>
    private (JsonFileStore File, WordBook Book) Entry(string id)
    {
        lock (_open)
            if (_open.TryGetValue(id, out var o)) return o;
        BeforeRead?.Invoke(id);
        var loaded = Read(id);
        lock (_open)
        {
            if (_open.TryGetValue(id, out var o)) return o;   // read meanwhile by another thread, or set: theirs wins
            _open[id] = loaded;
            return loaded;
        }
    }

    /// <summary>The words of <paramref name="id"/> if they are already in memory (no file read).</summary>
    public bool TryGetLoaded(string id, out WordBook book)
    {
        lock (_open)
        {
            if (_open.TryGetValue(id, out var o)) { book = o.Book; return true; }
            book = WordBook.Empty;
            return false;
        }
    }

    private (JsonFileStore File, WordBook Book) Read(string id)
    {
        var file = new JsonFileStore(PathOf(id), _log, "words", moveBadAside: !_inMemory, schema: WordBook.Schema);
        if (_inMemory) file.MakeReadOnly();
        var root = file.Load();
        bool over = false;
        var book = file.Existed && !file.Unreadable ? WordBook.FromJson(root, id == Profile.ClassId, out over) : WordBook.Empty;
        if (over) _log?.Invoke($"words: {id} holds more than the limits, the first words kept");
        file.Unload();   // the book is the model; the JSON tree would double the memory
        return (file, book);
    }

    /// <summary>Replace the words of <paramref name="id"/> and write them (background).</summary>
    public void Set(string id, WordBook book)
    {
        // « Mots à ne jamais proposer » belong to « Classe » only: never written into another profile's file
        if (id != Profile.ClassId && book.Exclus.Count > 0) book = book with { Exclus = Array.Empty<string>() };
        JsonFileStore file;
        Entry(id);   // loaded first (outside the lock), so the file store exists
        lock (_open)
        {
            file = _open[id].File;
            _open[id] = (file, book);
        }
        var json = book.ToJson();
        file.Update(root =>
        {
            foreach (var k in root.Select(kv => kv.Key).ToList()) root.Remove(k);
            Json.MergeInto(root, json);
        });
    }

    /// <summary>The profile was deleted (its folder with it): forget the cached words.</summary>
    public void Forget(string id)
    {
        JsonFileStore? f = null;
        lock (_open)
        {
            if (_open.TryGetValue(id, out var o)) f = o.File;
            _open.Remove(id);
        }
        f?.Flush();
    }

    /// <summary>Wait for the pending writes.</summary>
    public void Flush()
    {
        List<JsonFileStore> files;
        lock (_open) files = _open.Values.Select(o => o.File).ToList();
        foreach (var f in files) f.Flush();
    }

    /// <summary>Does <paramref name="id"/> have a word file on disk (tests, W1)?</summary>
    public bool HasFile(string id) => File.Exists(PathOf(id));
}
