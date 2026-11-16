using System;
using System.Collections.Generic;
using System.Linq;

namespace Predict.Keyboard;

/// <summary>
/// Which Piper voices stay in memory (plan 08b; pure, the caller locks). One model is ≈ 100 MB of working set, so only
/// the <see cref="Resident"/> voice (the configured one) stays loaded. A voice loaded for a « Écouter » preview only is
/// unloaded <see cref="UnloadIdleMs"/> after its last utterance, so previews in « Réglages » do not reload it each time.
/// A voice that stops being the configured one (Enregistrer with another voice) is <b>kept until the new configured
/// voice is loaded</b> (make-before-break: the router lets it speak meanwhile; kept for good when that load fails), then
/// unloaded as soon as nothing uses it; with a SAPI voice configured, at once. Both models are resident briefly.
/// A voice with an utterance pending or playing is never unloaded. A voice loaded for a preview runs without ORT's
/// memory arena (its memory goes back when it is unloaded); if it then becomes the configured voice, its session is
/// rebuilt with the arena once idle (<see cref="NeedsArena"/>).
/// </summary>
internal sealed class PiperResidency
{
    /// <summary>Grace of a preview-only voice after its last utterance (B3).</summary>
    public const long UnloadIdleMs = 30_000;
    public const string ReasonIdle = "idle";
    public const string ReasonChanged = "configured voice changed";
    public static string ReasonReplaced(string resident) => $"replaced by {resident}";

    private sealed class Entry
    {
        public long LastUse;
        public int Busy;
        public bool Demoted;   // was the configured voice, then another one was configured
        public bool Arena = true;
    }

    private readonly Dictionary<string, Entry> _loaded = new(StringComparer.Ordinal);
    private string? _resident;

    /// <summary>The configured voice (null: a SAPI voice is configured, no Piper voice needs to stay).</summary>
    public string? Resident
    {
        get => _resident;
        set
        {
            if (_resident == value) return;
            if (_resident != null && _loaded.TryGetValue(_resident, out var old)) old.Demoted = true;
            _resident = value;
            if (value != null && _loaded.TryGetValue(value, out var now)) now.Demoted = false;
        }
    }

    public IReadOnlyCollection<string> Loaded => _loaded.Keys;

    /// <param name="arena">the session uses ORT's CPU memory arena (false: loaded for a preview).</param>
    public void OnLoaded(string id, long nowMs, bool arena = true) => _loaded[id] = new Entry { LastUse = nowMs, Arena = arena };

    /// <summary>The configured voice, loaded without the arena (it was a preview first), idle: rebuild its session.</summary>
    public List<string> NeedsArena() =>
        _loaded.Where(kv => kv.Key == _resident && !kv.Value.Arena && kv.Value.Busy == 0).Select(kv => kv.Key).ToList();

    /// <summary>The session of <paramref name="id"/> now uses the arena.</summary>
    public void OnArena(string id)
    {
        if (_loaded.TryGetValue(id, out var e)) e.Arena = true;
    }

    public void OnUnloaded(string id) => _loaded.Remove(id);

    /// <summary>An utterance of <paramref name="id"/> was accepted (it now keeps the voice loaded).</summary>
    public void Begin(string id, long nowMs)
    {
        if (!_loaded.TryGetValue(id, out var e)) return;
        e.Busy++;
        e.LastUse = nowMs;
    }

    /// <summary>That utterance ended (done, cancelled, failed): the idle delay starts now.</summary>
    public void End(string id, long nowMs)
    {
        if (!_loaded.TryGetValue(id, out var e)) return;
        e.Busy = Math.Max(0, e.Busy - 1);
        e.LastUse = nowMs;
    }

    /// <summary>A loaded voice that is not the configured one: the clock must keep checking <see cref="Due"/>. A demoted
    /// voice waiting for the new one to load is not: the end of that load wakes the clock.</summary>
    public bool AnyCandidate => _loaded.Any(kv => kv.Key != _resident && !(kv.Value.Demoted && WaitsForResident));

    private bool WaitsForResident => _resident != null && !_loaded.ContainsKey(_resident);

    /// <summary>Voices to unload now, with the reason for the log.</summary>
    public List<(string Id, string Reason)> Due(long nowMs)
    {
        var o = new List<(string, string)>();
        foreach (var (id, e) in _loaded)
        {
            if (id == _resident || e.Busy > 0) continue;
            if (e.Demoted)
            {
                // the new configured voice is not loaded (yet, or its load failed): the old one still speaks for it
                if (_resident is null) o.Add((id, ReasonChanged));
                else if (!WaitsForResident) o.Add((id, ReasonReplaced(_resident)));
            }
            else if (nowMs - e.LastUse >= UnloadIdleMs) o.Add((id, ReasonIdle));
        }
        return o;
    }
}
