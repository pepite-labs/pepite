using System;
using System.IO;
using System.Linq;

namespace Predict.Keyboard;

/// <summary>How the app was started: by the user (double-click, Start menu), or by Windows at sign-in. The future
/// installer's Run-key entry passes <c>--autostart</c> (value name <c>Pepite-Prediction</c>, D13); nothing else does.</summary>
internal enum StartKind { Manual, Autostart }

/// <summary>What the first instance shows once it is ready.</summary>
internal enum StartFeedback
{
    /// <summary>Nothing (the welcome was shown already, or a scripted run passed <c>--no-welcome</c>).</summary>
    None,
    /// <summary>The welcome window « Pépite est prêt ! ».</summary>
    WelcomeWindow,
    /// <summary>Only a discreet notification « Pépite est prêt » (start with Windows: no window pops up).</summary>
    ReadyNotification,
}

/// <summary>What a second copy does when an instance is already running.</summary>
internal enum SecondStartAction
{
    /// <summary>Ask the running instance to show « Pépite est déjà démarré », then exit.</summary>
    SignalRunning,
    /// <summary>Exit without a word: Windows started us at sign-in while the user had already started Pépite.</summary>
    ExitSilently,
}

/// <summary>
/// Start-up feedback rules (TODO « Start-up feedback and single instance », owner spec 2026-11-14), pure:
/// a manual start shows the welcome window once — at the very first start (owner Q3 of the embedded-help plan,
/// 2026-11-16; it used to come back until « Ne plus afficher ce message » was ticked) — and Réglages can ask for it once
/// more; a start with Windows shows only a notification; a second manual start always shows « déjà démarré »
/// in the running instance (the checkbox does not apply: it answers a user action).
/// </summary>
internal static class StartupPolicy
{
    public const string AutostartFlag = "--autostart";
    /// <summary>Scripted tests: no welcome window (it would cover the test application).</summary>
    public const string NoWelcomeFlag = "--no-welcome";

    public static StartKind KindOf(string[] args) =>
        args.Any(a => string.Equals(a, AutostartFlag, StringComparison.OrdinalIgnoreCase)) ? StartKind.Autostart : StartKind.Manual;

    public static bool NoWelcome(string[] args) =>
        args.Any(a => string.Equals(a, NoWelcomeFlag, StringComparison.OrdinalIgnoreCase));

    /// <param name="welcomeSetting"><c>start.welcome</c> of the settings (false once the box was ticked).</param>
    public static StartFeedback AtStart(StartKind kind, bool welcomeSetting, bool noWelcomeFlag) =>
        kind == StartKind.Autostart ? StartFeedback.ReadyNotification
        : welcomeSetting && !noWelcomeFlag ? StartFeedback.WelcomeWindow
        : StartFeedback.None;

    public static SecondStartAction OnSecondStart(StartKind kind) =>
        kind == StartKind.Autostart ? SecondStartAction.ExitSilently : SecondStartAction.SignalRunning;

    /// <summary>Outcome of a second start (<see cref="ResolveSecondStart"/>).</summary>
    public enum SecondStartOutcome { Acquired, Signalled, Failed }

    /// <summary>
    /// The second copy's loop: signal the running copy; when that fails (its event not created yet — it is still in its
    /// first milliseconds — or it is exiting and has closed it), try the mutex again: the other copy may have gone, and
    /// then this one starts normally (<see cref="SecondStartOutcome.Acquired"/>). Gives up after
    /// <paramref name="timeoutMs"/>: the caller then shows « déjà démarré » itself (e.g. another user's copy).
    /// </summary>
    public static SecondStartOutcome ResolveSecondStart(Func<bool> tryAcquire, Func<bool> signal, Action<int> sleep,
                                                        int timeoutMs = 4000, int stepMs = 250)
    {
        for (int waited = 0; ; waited += stepMs)
        {
            if (signal()) return SecondStartOutcome.Signalled;
            if (tryAcquire()) return SecondStartOutcome.Acquired;
            if (waited >= timeoutMs) return SecondStartOutcome.Failed;
            sleep(stepMs);
        }
    }

    /// <summary>What « Enregistrer » in Réglages saves: the edited copy, except <c>start.welcome</c>, which keeps the
    /// current value — Réglages drives the app-level « welcome shown » flag instead (plan « aide intégrée »), and a
    /// stale copy must never write the profile's old value back.</summary>
    public static AppSettings ForSave(AppSettings edited, AppSettings current) => edited with { Welcome = current.Welcome };

    /// <summary>One-time migration at start, before any profile switch: no app-level flag yet and the active profile's
    /// <c>start.welcome</c> is off (the former « Ne plus afficher ce message » box) without a machine lock → record the
    /// welcome as shown, so that no later profile decides it again.</summary>
    public static bool MigrateWelcomeShown(bool? stored, bool setting, bool locked) => stored is null && !locked && !setting;

    /// <summary>Whether the welcome was shown already on this Windows account: the app-level flag
    /// (<c>state.welcomeShown</c>, the same for every profile, so turning teacher management on or switching to
    /// « Classe » or a pupil never brings it back); never recorded = not shown.</summary>
    public static bool WelcomeShownBefore(bool? stored) => stored ?? false;

    /// <summary>The welcome is due at a manual start: shown once, at the very first start (plan « aide intégrée », owner
    /// Q3, 2026-11-16; Réglages « … au prochain démarrage » clears the flag); a machine lock of <c>start.welcome</c> wins:
    /// locked on = at every manual start, locked off = never.</summary>
    public static bool WelcomeDue(bool shownBefore, bool setting, bool locked) => locked ? setting : !shownBefore;

    /// <summary>The flag to record once the welcome window is shown (null = leave it: the machine file decides).</summary>
    public static bool? WelcomeShownAfter(bool locked) => locked ? null : true;
}
