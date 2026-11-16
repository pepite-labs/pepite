using System;
using System.Collections.Generic;

namespace Predict.Keyboard;

/// <summary>A word-progress mark on the audio timeline: absolute source sample (as <see cref="IAudioSink.Enqueue"/>
/// numbers them) and the unit in the utterance text.</summary>
internal readonly record struct ClockMark(long Sample, int CharPos, int CharLen, double AudioMs);

/// <summary>
/// Drives one utterance's progress and end from the audio clock (plan 08a, step 6; pure: the played position is passed
/// to <see cref="Tick"/>). Each mark fires <c>progress</c> once, in order, never before its sample has played;
/// <c>done(Done)</c> fires once the end is known and played; after <see cref="Cancel"/> or <see cref="Fail"/> nothing
/// more is raised. Thread-safe: marks are added on the <c>piper</c> thread, ticks come from the <c>piper-clock</c>
/// thread, cancel from the speech thread; callbacks run under the lock, so none can follow a cancel (they only post).
/// </summary>
internal sealed class PlaybackClock
{
    private readonly object _lock = new();
    private readonly List<ClockMark> _marks = new();
    private Action<SpeechProgress>? _progress;
    private Action<SpeechOutcome, string?>? _done;
    private int _next;
    private long _end = -1;
    private bool _active;

    /// <summary>Start a new utterance (any previous one must have ended).</summary>
    public void Begin(Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress)
    {
        lock (_lock)
        {
            _marks.Clear();
            _next = 0;
            _end = -1;
            _done = done;
            _progress = progress;
            _active = true;
        }
    }

    public bool Active { get { lock (_lock) return _active; } }

    public void AddMarks(IEnumerable<ClockMark> marks)
    {
        lock (_lock) if (_active) _marks.AddRange(marks);
    }

    /// <summary>All the audio is queued: the utterance ends when <paramref name="endSample"/> has played.</summary>
    public void SetEnd(long endSample)
    {
        lock (_lock) if (_active) _end = endSample;
    }

    /// <summary>Raise what the played position has reached. Returns false once the utterance is over.</summary>
    public bool Tick(long played)
    {
        lock (_lock)
        {
            if (!_active) return false;
            while (_next < _marks.Count && _marks[_next].Sample <= played)
            {
                var m = _marks[_next++];
                try { _progress?.Invoke(new SpeechProgress(m.CharPos, m.CharLen, m.AudioMs)); } catch (Exception) { }
            }
            if (_end >= 0 && played >= _end) Finish(SpeechOutcome.Done, null);
            return _active;
        }
    }

    /// <summary>Cancelled: <c>done(Cancelled)</c> once, nothing after. False when the utterance had already ended.</summary>
    public bool Cancel() { lock (_lock) return Finish(SpeechOutcome.Cancelled, null); }

    /// <summary>Device / synthesis error: <c>done(Error)</c> once.</summary>
    public bool Fail(string error) { lock (_lock) return Finish(SpeechOutcome.Error, error); }

    private bool Finish(SpeechOutcome o, string? error)   // under _lock
    {
        if (!_active) return false;
        _active = false;
        var d = _done;
        _done = null;
        _progress = null;
        try { d?.Invoke(o, error); } catch (Exception) { }
        return true;
    }
}
