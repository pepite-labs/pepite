using System;

namespace Predict.Keyboard;

/// <summary>What the teacher PIN protects (D29): changing the current profile's settings (« Réglages… »), changing
/// profile, the « Espace enseignant » (always), quitting. <see cref="SessionStart"/>: the « Qui es-tu ? » picker at the
/// start of a Windows session, never behind the PIN (the class must be able to start).</summary>
/// <summary><see cref="AddWord"/> (plan 10A, Q3): a pupil may add a word (tray « Ajouter un mot… ») without the PIN even
/// when « Code requis pour modifier le profil » is on; editing, deleting, lists and hiding stay in Réglages (Edit).</summary>
internal enum PinReason { Edit, Switch, Teacher, Quit, SessionStart, AddWord }

/// <summary>Every way into a protected action goes through a gate (formerly <c>OpenGate</c>, the adult lock of D8).</summary>
internal interface ISettingsGate
{
    void Open(PinReason reason, Action open);
}

/// <summary>
/// The teacher-PIN gate (plan 09 step 8), pure but for the prompt it is given: management off or no PIN → never asks;
/// « Espace enseignant » → always, grace or not (D29: always behind the PIN); Edit / Switch / Quit → the teacher's three
/// switches (defaults on / off / off); the session-start pick → never. After a correct PIN a 5-minute grace spares the
/// teacher the PIN for Edit / Switch / Quit (e.g. Réglages right after the Espace enseignant); a profile switch ends
/// the grace (the next pupil starts locked).
/// </summary>
internal sealed class PinGate : ISettingsGate
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private readonly Func<bool> _managementOn;
    private readonly Func<ProfilePolicy> _policy;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<PinReason, Action> _prompt;
    private readonly Action<string>? _log;
    private DateTime? _graceUntil;

    /// <param name="managementOn">teacher management on and a PIN set.</param>
    /// <param name="prompt">shows the PIN dialog for the reason; calls its action once the PIN was right.</param>
    public PinGate(Func<bool> managementOn, Func<ProfilePolicy> policy, Action<PinReason, Action> prompt, Func<DateTime>? utcNow, Action<string>? log)
    {
        _managementOn = managementOn;
        _policy = policy;
        _prompt = prompt;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log;
    }

    /// <summary>The rule alone (no grace).</summary>
    public static bool Protects(PinReason reason, bool managementOn, ProfilePolicy p) =>
        managementOn && reason switch
        {
            PinReason.Teacher => true,
            PinReason.Edit => p.PinToEdit,
            PinReason.Switch => p.PinToSwitch,
            PinReason.Quit => p.PinToQuit,
            _ => false,
        };

    /// <summary>Plan 10A Q3: the tray's « Ajouter un mot… » never needs the PIN for a pupil — but with teacher management
    /// on, a word added while « Classe » is active goes to every pupil: that is an edit of « Classe » (Edit, which follows
    /// « Code requis pour modifier le profil »).</summary>
    public static PinReason AddWordReason(bool managementOn, string activeId) =>
        managementOn && activeId == Profile.ClassId ? PinReason.Edit : PinReason.AddWord;

    public bool InGrace => _graceUntil is DateTime g && _utcNow() < g;

    public bool Needs(PinReason reason) =>
        Protects(reason, _managementOn(), _policy()) && (reason == PinReason.Teacher || !InGrace);

    public void Open(PinReason reason, Action open)
    {
        if (!Needs(reason)) { open(); return; }
        _log?.Invoke($"pin gate: {reason} asks for the PIN");
        _prompt(reason, () =>
        {
            Granted();
            open();
        });
    }

    /// <summary>A correct PIN was typed (also by the Espace enseignant's own dialogs): grace starts.</summary>
    public void Granted() => _graceUntil = _utcNow() + Grace;

    /// <summary>A profile switch (or deactivation): the grace ends.</summary>
    public void ClearGrace()
    {
        if (_graceUntil != null) _log?.Invoke("pin gate: grace cleared");
        _graceUntil = null;
    }
}
