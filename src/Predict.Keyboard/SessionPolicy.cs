using System;
using System.Collections.Generic;
using System.Linq;

namespace Predict.Keyboard;

/// <summary>What the app must do after a session event: <paramref name="Secure"/> = close the Espace enseignant and Réglages
/// and end the PIN grace (the adult has left: the next person must not find them open); then switch back to « Classe »,
/// then (or only) open « Qui es-tu ? ».</summary>
internal readonly record struct SessionAction(bool ReturnToClass, bool ShowPicker, string Reason, bool Secure = false)
{
    public static readonly SessionAction None = new(false, false, "");
    public bool Any => ReturnToClass || ShowPicker || Secure;
}

/// <summary>The app's state the policy looks at, read on the UI thread at each event.</summary>
/// <param name="OfferedPupils">readable pupils offered on this PC (« Profils proposés sur ce poste »).</param>
/// <param name="Busy">a modal window of ours is open (a PIN, the import, a confirmation): a switch now would close its
/// owner under it, so the return waits.</param>
internal readonly record struct SessionContext(bool ManagementOn, ProfilePolicy Policy, string ActiveId, bool HasClass, int OfferedPupils,
                                               bool Busy);

/// <summary>
/// Session returns (plan 09B step 18, D29 Q2/Q10), pure — the app feeds it events and executes the actions:
/// <list type="bullet">
/// <item>start: <see cref="SessionStart.Decide"/> with the pupils offered on this PC (a new logon → « Classe » + the
/// picker; the same logon → resume);</item>
/// <item>inactivity (<see cref="OnIdle"/>, from <c>GetLastInputInfo</c> polled every 30 s): after
/// <see cref="ProfilePolicy.IdleReturnMin"/> minutes without keyboard or mouse input (off by default), once per idle
/// period → « Classe » + the picker;</item>
/// <item>Win+L (<see cref="OnLock"/> / <see cref="OnUnlock"/>, <c>WTS_SESSION_LOCK / UNLOCK</c>): with
/// <see cref="ProfilePolicy.ReturnOnLock"/> (off by default) → « Classe » at the lock, the picker at the unlock;</item>
/// <item>logoff (<see cref="OnLogoff"/>, <c>WTS_SESSION_LOGOFF</c> or the session-ending message): always → « Classe »
/// (Q10: on), so the next logon starts there even if its logon time cannot be read.</item>
/// </list>
/// Nothing happens with teacher management off or without « Classe ». When the idle or the lock return fires, the
/// Espace enseignant and Réglages are closed and the PIN grace ended (<see cref="SessionAction.Secure"/>) even when
/// « Classe » is already active — only the switch and, after an idle, the picker (no picker for an empty room) are then
/// left out. The picker needs ≥ 2 offered pupils and « Qui es-tu ? » on.
/// </summary>
internal sealed class SessionPolicy
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private bool _idleDone;       // a return was made for this idle period: wait for input before the next one
    private bool _pickerOnUnlock; // the lock returned to « Classe » (or found it): the next pupil chooses at the unlock
    private bool _returnOnUnlock; // the lock came while a modal window was open: return at the unlock instead

    /// <summary>The pupils « Qui es-tu ? » offers on this PC: the readable pupils of this PC's list, or all of them when
    /// the PC has no list — and also when the list matches none of them (a picker must never be empty: deleted or
    /// unreadable profiles would otherwise lock the class out).</summary>
    public static IReadOnlyList<ProfileEntry> Offered(ProfileStore store)
    {
        var pupils = store.Profiles.Where(e => !e.IsClass && !e.Unreadable).ToList();
        if (store.Policy.OfferedOn(store.MachineName) is not { } ids) return pupils;
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        var here = pupils.Where(e => set.Contains(e.Id)).ToList();
        return here.Count > 0 ? here : pupils;
    }

    /// <summary>The decision at the app's start (see <see cref="SessionStart.Decide"/>), counting the offered pupils.</summary>
    public static SessionStartDecision AtStart(bool managementOn, ProfileStore store, DateTime? currentLogonUtc) =>
        SessionStart.Decide(managementOn, Offered(store).Count, store.Class is { Unreadable: false }, store.Policy.SessionPicker,
                            store.SessionLogonUtc, currentLogonUtc);

    private static bool Applies(SessionContext c) => c.ManagementOn && c.HasClass;
    private static bool Picker(SessionContext c) => c.Policy.SessionPicker && c.OfferedPupils >= 2;

    /// <summary>A poll: <paramref name="idle"/> = time since the last keyboard / mouse input of the session.</summary>
    public SessionAction OnIdle(TimeSpan idle, SessionContext c)
    {
        int min = c.Policy.IdleReturnMin;
        if (!Applies(c) || min <= 0 || idle < TimeSpan.FromMinutes(min))
        {
            _idleDone = false;
            return SessionAction.None;
        }
        if (_idleDone || c.Busy) return SessionAction.None;
        _idleDone = true;
        // « Classe » active: nobody to send back and no picker for an empty room, but an open Espace enseignant is closed
        if (c.ActiveId == Profile.ClassId) return new SessionAction(false, false, "idle", Secure: true);
        return new SessionAction(true, Picker(c), "idle", Secure: true);
    }

    /// <summary>Win+L (or a switch of user, which locks first).</summary>
    public SessionAction OnLock(SessionContext c)
    {
        if (!Applies(c) || !c.Policy.ReturnOnLock) return SessionAction.None;
        _pickerOnUnlock = true;
        // a modal window of ours is open: closing its owner under it is not safe — everything waits for the unlock
        if (c.Busy) { _returnOnUnlock = true; return SessionAction.None; }
        return new SessionAction(c.ActiveId != Profile.ClassId, false, "lock", Secure: true);
    }

    public SessionAction OnUnlock(SessionContext c)
    {
        bool picker = _pickerOnUnlock, back = _returnOnUnlock;
        _pickerOnUnlock = _returnOnUnlock = false;
        if (!Applies(c) || !picker) return SessionAction.None;
        bool ret = back && c.ActiveId != Profile.ClassId && !c.Busy;
        return new SessionAction(ret, Picker(c) && !c.Busy, "unlock", Secure: back && !c.Busy);
    }

    /// <summary>The Windows session ends (logoff, shutdown): « Classe » is written as the active profile (D29 Q10, on).</summary>
    public SessionAction OnLogoff(SessionContext c) =>
        Applies(c) && c.ActiveId != Profile.ClassId ? new SessionAction(true, false, "logoff") : SessionAction.None;
}
