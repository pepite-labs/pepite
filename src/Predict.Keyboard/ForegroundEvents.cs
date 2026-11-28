using System;

namespace Predict.Keyboard;

/// <summary>What to do with one <c>EVENT_SYSTEM_FOREGROUND</c>. <paramref name="Apply"/>: the window to hand to the
/// foreground path now (zero = nothing now); <paramref name="Recheck"/>: look at the real foreground again
/// <see cref="ForegroundEvents.RecheckMs"/> later; <paramref name="NewGeneration"/>: the event supersedes a pending re-check.</summary>
internal readonly record struct ForegroundEventDecision(IntPtr Apply, bool Recheck, bool NewGeneration);

/// <summary>What a re-check does: <paramref name="Apply"/> the real foreground (zero = nothing), or try once more
/// <paramref name="RearmMs"/> later (0 = no further try).</summary>
internal readonly record struct ForegroundRecheck(IntPtr Apply, int RearmMs);

/// <summary>
/// WR5 (0.11.0): the foreground event names a window, <c>GetForegroundWindow()</c> names the real foreground, and they can
/// differ — a window shown by a background process gets its event while its activation is refused (the old window keeps
/// the foreground), and its real activation a moment later raises a second event for the same window. Trusting the event
/// set the tracked window to the new one while the read went to the old one, and the real activation was then dropped as
/// « the same window »: the re-read of the edited sentence was lost. Pure: the real foreground is the truth (as for the
/// key-down backstop and the read); an event that names another window is looked at again a little later, up to
/// <see cref="MaxRechecks"/> times (an activation granted late may come without a second event).
/// </summary>
internal static class ForegroundEvents
{
    /// <summary>Delay of the first re-check after an event that names another window than the real foreground.</summary>
    public const int RecheckMs = 100;
    /// <summary>Delay of the further re-checks, while the real foreground is still the tracked window (or none).</summary>
    public const int RearmMs = 300;
    /// <summary>Re-checks per event, in all (100 ms, then +300 ms, then +300 ms).</summary>
    public const int MaxRechecks = 3;

    /// <param name="evt">the window the event names.</param>
    /// <param name="evtRoot">its root window (GA_ROOT): a frame's inner window (UWP) has the frame as its root.</param>
    /// <param name="real">GetForegroundWindow() when the event is handled (may be zero during a switch).</param>
    /// <param name="last">the window the controller tracks.</param>
    /// <param name="bar">our suggestion bar (BUG-1: an activation of the bar by UI Automation is never a jump).</param>
    public static ForegroundEventDecision Decide(IntPtr evt, IntPtr evtRoot, IntPtr real, IntPtr last, IntPtr bar)
    {
        // the bar: handled as before (the foreground path classifies it and ignores it); it neither supersedes nor needs a
        // re-check — the bar gives the activation back itself
        if (bar != IntPtr.Zero && evt == bar) return new(evt, false, false);
        // the usual case: the event names the foreground, or a window inside it (a frame's inner window)
        if (real != IntPtr.Zero && (evt == real || evtRoot == real)) return new(real, false, true);
        // the event names another window: the real foreground now if it is a change (a third window after a quick double
        // switch), and a re-check later in any case (the named window may be about to get the foreground)
        bool change = real != IntPtr.Zero && real != last && real != bar;
        return new(change ? real : IntPtr.Zero, true, true);
    }

    /// <summary>The re-check number <paramref name="attempt"/> (1-based): only the latest event's (<paramref name="scheduled"/>
    /// = <paramref name="current"/>). A real foreground other than the tracked window (and the bar) goes to the foreground
    /// path; none yet, or still the tracked window / the bar → once more later, up to <see cref="MaxRechecks"/>.</summary>
    public static ForegroundRecheck Recheck(long scheduled, long current, IntPtr real, IntPtr last, IntPtr bar, int attempt)
    {
        if (scheduled != current) return new(IntPtr.Zero, 0);
        if (real != IntPtr.Zero && real != last && real != bar) return new(real, 0);
        return new(IntPtr.Zero, attempt < MaxRechecks ? RearmMs : 0);
    }
}
