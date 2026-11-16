using System;
using System.Collections.Generic;

namespace Predict.Keyboard;

/// <summary>
/// Bookkeeping of the whole-sentence echo after an edit (D17 amendment, 2026-11-14), pure; the controller calls it under
/// its lock. While the sentence read is pending, the next echoes (a word typed right after the period, an inserted word,
/// another sentence) are <see cref="Defer"/>red and released right after the sentence, so none is heard before it nor
/// dropped by it (a sentence drops the pending words of the speech queue). A pending read older than
/// <see cref="ExpiryMs"/> (its read lost the queue's slot or the worker is stuck) is given up: its typed words are said,
/// then what was deferred. <see cref="Saying"/> / <see cref="Outcome"/> / <see cref="AlreadySaid"/>: the D23 re-read
/// skips that sentence only if it was spoken (or is being spoken), not merely asked for.
/// </summary>
internal sealed class SentenceEchoGate
{
    /// <summary>Two reads (one retry 80 ms after the first) within their budget, plus margin: then the read is given up.</summary>
    public const long ExpiryMs = 2 * KeyboardPolicy.RereadReadBudgetMs + 300;
    /// <summary>A sentence handed to the speech path with no outcome reported (the echo guard may drop it silently) counts
    /// as being spoken this long.</summary>
    public const long SayingMs = 10_000;

    /// <param name="Echo">an echo event (through the password guard), else <see cref="Text"/> said as <see cref="Kind"/>.</param>
    internal readonly record struct Deferred(SpeechKind Kind, string Text, EchoEvent? Echo, IntPtr Fg);

    internal sealed record Pending(long Seq, string Typed, IntPtr Fg, long Tick, bool Retried);

    private Pending? _pending;
    private readonly List<Deferred> _deferred = new();
    private long _seq;
    private (string Text, IntPtr Fg, long Tick, bool Done)? _said;

    public Pending? Current => _pending;

    /// <summary>A sentence read is asked. A previous one still pending is given up first (its typed words, then what it
    /// deferred, are returned to be said now).</summary>
    public (long Seq, List<Deferred> Now) Begin(string typed, IntPtr fg, long now)
    {
        var o = Flush();
        _pending = new Pending(++_seq, typed, fg, now, false);
        return (_seq, o);
    }

    /// <summary>An echo while a read is pending: kept for after the sentence (true). No read pending (false): say it now;
    /// an overdue read is given up first and its words are in <paramref name="before"/>.</summary>
    public bool Defer(Deferred d, long now, out List<Deferred> before)
    {
        before = Expire(now);
        if (_pending is null) return false;
        _deferred.Add(d);
        return true;
    }

    /// <summary>The read of <paramref name="seq"/> came back empty-handed once: ask again (true), once.</summary>
    public bool Retry(long seq)
    {
        if (_pending is not { } p || p.Seq != seq || p.Retried) return false;
        _pending = p with { Retried = true };
        return true;
    }

    /// <summary>The read of <paramref name="seq"/> is done: the pending sentence and what it deferred (null: not the
    /// current read, nothing to do).</summary>
    public (Pending Sentence, List<Deferred> Deferred)? Take(long seq)
    {
        if (_pending is not { } p || p.Seq != seq) return null;
        _pending = null;
        var d = new List<Deferred>(_deferred);
        _deferred.Clear();
        return (p, d);
    }

    /// <summary>The pending read is overdue: its typed words, then what it deferred.</summary>
    public List<Deferred> Expire(long now) =>
        _pending is { } p && now - p.Tick >= ExpiryMs ? Flush() : new List<Deferred>();

    /// <summary>A password field, a pause: nothing pending is said.</summary>
    public void Drop()
    {
        _pending = null;
        _deferred.Clear();
    }

    private List<Deferred> Flush()
    {
        var o = new List<Deferred>();
        if (_pending is { } p && p.Typed.Length > 0)
            o.Add(new Deferred(SpeechKind.Sentence, p.Typed, new EchoEvent(EchoKind.Sentence, p.Typed), p.Fg));
        _pending = null;
        o.AddRange(_deferred);
        _deferred.Clear();
        return o;
    }

    // ---- the D23 re-read must not say again a sentence the echo said from the document

    /// <summary>The document's sentence was handed to the speech path.</summary>
    public void Saying(string text, IntPtr fg, long now) => _said = (text, fg, now, false);

    /// <summary>Its outcome: Done keeps it as said; Cancelled / Error forget it (it was not heard).</summary>
    public void Outcome(string text, SpeechOutcome o)
    {
        if (_said is not { } s || s.Text != text) return;
        _said = o == SpeechOutcome.Done ? s with { Done = true } : null;
    }

    /// <summary>The D23 re-read of <paramref name="text"/> would repeat that sentence (said, or being said): true once.</summary>
    public bool AlreadySaid(string text, IntPtr fg, long now)
    {
        if (_said is not { } s || s.Fg != fg || !(s.Done || now - s.Tick < SayingMs) || !SentenceDocRead.Same(s.Text, text)) return false;
        _said = null;
        return true;
    }
}
