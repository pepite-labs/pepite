using System;
using System.Collections.Generic;
using System.Text;

namespace Predict.Keyboard;

/// <summary>
/// Synthesized audio of isolated words (owner, 0.6.0: the same word echoed twice must sound the same). VITS draws new
/// noise at every run, so without it « très » can come out differently each time; with it the second echo of a word
/// replays the first one's samples, and is faster. Keyed by voice, the word's text (<see cref="Key"/>: trimmed, NFC,
/// inner white space collapsed; case kept, an acronym is said otherwise) and the effective scales (rate). LRU bounded by
/// <see cref="MaxEntries"/> and <see cref="MaxBytes"/>. Pure, used on the <c>piper</c> thread only (no lock).
/// </summary>
internal sealed class PiperWordCache
{
    public const int MaxEntries = 200;
    public const long MaxBytes = 20L * 1024 * 1024;
    /// <summary>Longer texts are not cached (a word echo, a grouped liaison, a tile: a few words at most).</summary>
    public const int MaxTextLength = 48;
    /// <summary>Appended to the voice id of a word said in its carrier (0.7.2): its key never meets a plain rendering's.</summary>
    public const string CarrierMark = "\u0002carrier";

    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly LinkedList<(string Key, float[] Audio)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, float[] Audio)>> _index = new(StringComparer.Ordinal);

    public PiperWordCache(int maxEntries = MaxEntries, long maxBytes = MaxBytes)
    {
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    public long Bytes { get; private set; }
    public int Count => _index.Count;

    /// <summary>The cache key, or null when <paramref name="text"/> is not cacheable (empty or too long).</summary>
    public static string? Key(string voiceId, string text, float noiseScale, float lengthScale, float noiseW)
    {
        var t = text.Trim().Normalize(NormalizationForm.FormC);
        if (t.Length == 0 || t.Length > MaxTextLength) return null;
        var sb = new StringBuilder(t.Length + 48);
        bool space = false;
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        return FormattableString.Invariant($"{voiceId}\u0001{noiseScale:R}\u0001{lengthScale:R}\u0001{noiseW:R}\u0001{sb}");
    }

    public bool TryGet(string key, out float[] audio)
    {
        if (_index.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            audio = node.Value.Audio;
            return true;
        }
        audio = Array.Empty<float>();
        return false;
    }

    public void Add(string key, float[] audio)
    {
        long size = (long)audio.Length * sizeof(float);
        if (size > _maxBytes) return;
        if (_index.TryGetValue(key, out var old)) { _lru.Remove(old); _index.Remove(key); Bytes -= (long)old.Value.Audio.Length * sizeof(float); }
        _index[key] = _lru.AddFirst((key, audio));
        Bytes += size;
        while (_index.Count > _maxEntries || Bytes > _maxBytes)
        {
            var last = _lru.Last!;
            _lru.RemoveLast();
            _index.Remove(last.Value.Key);
            Bytes -= (long)last.Value.Audio.Length * sizeof(float);
        }
    }

    public void Clear()
    {
        _lru.Clear();
        _index.Clear();
        Bytes = 0;
    }
}
