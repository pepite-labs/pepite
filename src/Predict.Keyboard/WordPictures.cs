using System;
using System.Collections.Generic;
using System.IO;

namespace Predict.Keyboard;

/// <summary>
/// The pictures a word gets besides the ARASAAC pack (plan 10C, owner Q13 / Q17 / Q18): pictures imported by the teacher
/// with a names database (the pupil's lists first, then « Classe »'s) and the flags of the « Noms propres » pack. Lookup
/// order for a tile (<see cref="For"/>): the pupil's picture for that exact form → « Classe »'s → the pack's (a name only)
/// → none, and the caller falls back to ARASAAC only for a word that is not a name (a name never shows the picture of its
/// lowercase homograph: « Pierre » is not a stone). Pictures of names follow the profile setting « Images pour les noms
/// propres » (<c>words.namePictures</c>, on by default); a teacher's picture of a common word follows « Afficher les
/// images » only. Files are small PNG thumbnails (re-encoded at import, built by the pack): decoded on demand on the UI
/// thread by the injected decoder and kept in a small cache. UI thread.
/// </summary>
internal sealed class WordPictures
{
    public const int CacheCapacity = 200;
    private readonly Func<byte[], object> _decode;
    private readonly Action<string>? _log;
    private IReadOnlyDictionary<string, string> _own = new Dictionary<string, string>(), _class = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _pack = new Dictionary<string, string>();
    private readonly Dictionary<string, object> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>« Images pour les noms propres » of the active profile.</summary>
    public bool NamesOn { get; set; } = true;
    public int PackCount => _pack.Count;
    public int ListCount => _own.Count + _class.Count;

    public WordPictures(Func<byte[], object> decode, Action<string>? log = null) { _decode = decode; _log = log; }

    /// <summary>The pack's pictures: exact form (names and their variants) → absolute file path.</summary>
    public void SetPack(IReadOnlyDictionary<string, string> pack) => _pack = pack;

    /// <summary>The active profile's list pictures (its own, then « Classe »'s): exact form → absolute file path.</summary>
    public void SetLists(IReadOnlyDictionary<string, string> own, IReadOnlyDictionary<string, string> cls)
    {
        _own = own;
        _class = cls;
    }

    /// <summary>A capitalised form: a name for the picture rules (a pack or list name, or any word written with a capital).</summary>
    public static bool IsNameForm(string form) => form.Length > 0 && char.IsUpper(form[0]);

    /// <summary>The picture file of a tile, or null (then ARASAAC only when <paramref name="name"/> is false).</summary>
    /// <param name="name">the tile is a name (<see cref="Predict.Core.Suggestion.Name"/>).</param>
    public string? For(string form, bool name)
    {
        if (string.IsNullOrEmpty(form)) return null;
        bool nameRules = name || IsNameForm(form);
        if (nameRules && !NamesOn) return null;
        if (_own.TryGetValue(form, out var p) || _class.TryGetValue(form, out p)) return p;
        if (name && _pack.TryGetValue(form, out p)) return p;
        return null;
    }

    /// <summary>Is there a picture of ours for this tile (the slot must not take ARASAAC's)?</summary>
    public bool Has(string form, bool name) => For(form, name) != null;

    /// <summary>The decoded picture of <paramref name="path"/> (cached), or null when the file cannot be read.</summary>
    public object? Get(string path)
    {
        if (_cache.TryGetValue(path, out var img)) { Touch(path); return img; }
        if (_failed.Contains(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > NamesImport.MaxImageBytes) { _failed.Add(path); return null; }
            img = _decode(File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            _failed.Add(path);
            _log?.Invoke($"word picture failed: {Path.GetFileName(path)} ({ex.GetType().Name})");
            return null;
        }
        _cache[path] = img;
        _order.AddFirst(path);
        while (_order.Count > CacheCapacity) { _cache.Remove(_order.Last!.Value); _order.RemoveLast(); }
        return img;
    }

    private void Touch(string path)
    {
        var n = _order.Find(path);
        if (n is null) return;
        _order.Remove(n);
        _order.AddFirst(n);
    }
}
