using System;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>What a profile switch must stop in the running app (the controller, the speech, our windows).</summary>
internal interface IProfileHost
{
    /// <summary>Forget the tracked word, the echo, the re-read session, the reading view; hide the bar.</summary>
    void ResetForSwitch();
    void CancelSpeech();
    /// <summary>Close « Réglages » and the reading window: they show the previous profile.</summary>
    void CloseProfileWindows();
}

/// <summary>What « Réglages » edits: the active profile (through <see cref="SettingsService"/>) or, from the Espace
/// enseignant, any profile (<see cref="ProfileEditTarget"/>).</summary>
internal interface ISettingsTarget
{
    /// <summary>« Réglages — Léa » when teacher management is on; null = single user (« Réglages — Pépite »).</summary>
    string? ProfileName { get; }
    string ProfileId { get; }
    AppSettings Current { get; }
    bool IsLocked(string path);
    void Apply(AppSettings edited);
}

/// <summary>Réglages of the active profile: the running app's settings.</summary>
internal sealed class ActiveProfileTarget : ISettingsTarget
{
    private readonly SettingsService _settings;
    private readonly Func<string?> _name;
    public ActiveProfileTarget(SettingsService settings, Func<string?> name) { _settings = settings; _name = name; }
    public string? ProfileName => _name();
    public string ProfileId => _settings.Target;
    public AppSettings Current => _settings.Current;
    public bool IsLocked(string path) => _settings.IsLocked(path);
    public void Apply(AppSettings edited) => _settings.Apply(edited);
}

/// <summary>Réglages of another profile (the teacher edits Léa while Tom is active, V11): computed over its own class
/// layer, saved into its file only; the running app changes only when that profile is active or is « Classe ».</summary>
internal sealed class ProfileEditTarget : ISettingsTarget
{
    private readonly ProfileService _svc;
    public ProfileEditTarget(ProfileService svc, string id) { _svc = svc; ProfileId = id; }
    public string ProfileId { get; }
    public string? ProfileName => _svc.Store.Get(ProfileId)?.Profile.Name;
    private (AppSettings Effective, System.Collections.Generic.ISet<string> Locked) Eff =>
        _svc.Settings.EffectiveOf(_svc.Store.ClassLayerFor(ProfileId), _svc.Store.Get(ProfileId)?.Profile.Layer);
    public AppSettings Current => Eff.Effective;
    public bool IsLocked(string path) => _svc.Settings.IsLocked(path);

    public void Apply(AppSettings edited)
    {
        var e = _svc.Store.Get(ProfileId);
        if (e is null || e.Unreadable) return;
        var (cur, locked) = Eff;
        var layer = SettingsLayering.NewUserLayer(e.Profile.Layer, cur, edited, _svc.Settings.Machine, locked, _svc.Store.ClassLayerFor(ProfileId));
        _svc.SaveLayer(ProfileId, layer);
    }
}

/// <summary>What happens at the start of the app with teacher management on (plan 09 step 12, D29 Q2/Q10).</summary>
internal readonly record struct SessionStartDecision(bool NewLogon, bool ReturnToClass, bool ShowPicker);

internal static class SessionStart
{
    /// <summary>
    /// A new Windows logon (its time differs from the one saved by the previous run) → back to « Classe » (the return
    /// at logoff, Q10) and « Qui es-tu ? » when it is on and there are ≥ 2 readable pupil profiles (« Classe » is not
    /// counted: with one pupil there is nothing to choose); the same logon (a restart of the app, a crash) → the active
    /// profile is resumed, no picker. Management off, or the logon time unknown → resume.
    /// </summary>
    public static SessionStartDecision Decide(bool managementOn, int pupils, bool hasClass, bool pickerOn,
                                              DateTime? savedLogonUtc, DateTime? currentLogonUtc)
    {
        if (!managementOn || currentLogonUtc is not DateTime now) return new(false, false, false);
        bool newLogon = savedLogonUtc is not DateTime saved || Math.Abs((now - saved).TotalSeconds) > 2;
        if (!newLogon) return new(false, false, false);
        return new(true, hasClass, pickerOn && pupils >= 2);
    }
}

/// <summary>
/// The profiles of the running app (plan 09 step 9, UI thread): <see cref="Switch"/> stops what the previous profile
/// was doing (controller reset, speech cancelled, Réglages / reading window closed), points <see cref="SettingsService"/>
/// at the new layers — its <c>Changed</c> event reconfigures the bar, the controller and the voice (make-before-break,
/// plan 08b) —, writes <c>index.active</c> and ends the PIN grace. CRUD for the Espace enseignant: add, rename, picture,
/// duplicate, delete (never « Classe », never the active one), reorder. Logs carry ids, never first names.
/// </summary>
internal sealed class ProfileService
{
    private readonly IProfileHost _host;
    private readonly Action<string>? _log;

    public ProfileStore Store { get; }
    public SettingsService Settings { get; }
    public PinGate? Gate { get; set; }
    /// <summary>Plan 10A: the personal words follow the active profile (switch, import, delete, duplicate).</summary>
    public WordListService? Words
    {
        get => _words;
        set { _words = value; Store.BeforeBackup = value is null ? null : () => value.Store.Flush(); }
    }
    private WordListService? _words;
    /// <summary>The list, a name, a picture or the active profile changed (tray tooltip, Espace enseignant).</summary>
    public event Action? ProfilesChanged;

    public ProfileService(ProfileStore store, SettingsService settings, IProfileHost host, Action<string>? log)
    {
        Store = store;
        Settings = settings;
        _host = host;
        _log = log;
    }

    public ProfileEntry Active => Store.Active;

    /// <summary>The <see cref="SettingsService"/> constructor arguments for the active profile.</summary>
    public static SettingsService CreateSettings(ProfileStore store, MachineLayer machine, Action<string>? log)
    {
        var a = store.Active;
        return new SettingsService(machine, a.Profile.Layer, store.ClassLayerFor(a.Id), SaveTo(store, a.Id), a.Id, log);
    }

    private static Action<JsonObject> SaveTo(ProfileStore store, string id) => layer => store.SetLayer(id, layer);

    private Action<JsonObject> SaveFor(string id) => layer =>
    {
        Store.SetLayer(id, layer);
        ProfilesChanged?.Invoke();
    };

    /// <summary>Make <paramref name="id"/> the active profile. False when it is already active or cannot be used.</summary>
    public bool Switch(string id, string reason)
    {
        var e = Store.Get(id);
        if (e is null || e.Unreadable) { _log?.Invoke($"profile: switch to {id} refused (unknown or unreadable)"); return false; }
        if (id == Store.ActiveId) { _log?.Invoke($"profile: {id} kept ({reason})"); Gate?.ClearGrace(); return false; }
        var sw = Stopwatch.StartNew();
        var from = Store.ActiveId;
        _host.ResetForSwitch();
        _host.CancelSpeech();
        _host.CloseProfileWindows();
        Settings.Retarget(e.Profile.Layer, Store.ClassLayerFor(id), SaveFor(id), id);
        Store.SetActive(id);
        Words?.Rebuild("switch");
        Gate?.ClearGrace();
        _log?.Invoke(FormattableString.Invariant($"profile: switch {from} -> {id} ({reason}) applied in {sw.Elapsed.TotalMilliseconds:0.0} ms"));
        ProfilesChanged?.Invoke();
        return true;
    }

    /// <summary>A profile's layer saved from the Espace enseignant; the running app follows when it is the active profile
    /// or « Classe » under the active pupil.</summary>
    public void SaveLayer(string id, JsonObject layer)
    {
        Store.SetLayer(id, layer);
        _log?.Invoke($"profile: settings of {id} saved");
        if (id == Store.ActiveId || (id == Profile.ClassId && Store.ActiveId != Profile.ClassId)) ReloadActive();
        ProfilesChanged?.Invoke();
    }

    /// <summary>Reload the active profile's layers (its class changed, or the store was edited).</summary>
    public void ReloadActive()
    {
        var a = Store.Active;
        Settings.Retarget(a.Profile.Layer, Store.ClassLayerFor(a.Id), SaveFor(a.Id), a.Id);
    }

    public ProfileEntry Add(string name, Avatar avatar)
    {
        var e = Store.Add(name, avatar);
        ProfilesChanged?.Invoke();
        return e;
    }

    public void Rename(string id, string name) { Store.Rename(id, name); ProfilesChanged?.Invoke(); }
    public void SetAvatar(string id, Avatar a) { Store.SetAvatar(id, a); ProfilesChanged?.Invoke(); }

    public ProfileEntry Duplicate(string id, string name)
    {
        var e = Store.Duplicate(id, name);
        Words?.OnDuplicated(id, e.Id);
        ProfilesChanged?.Invoke();
        return e;
    }

    /// <summary>The words are flushed and forgotten first: a write still pending must not recreate the folder.</summary>
    public void Delete(string id) { Words?.OnDeleted(id); Store.Delete(id); ProfilesChanged?.Invoke(); }
    public bool Move(string id, int delta) { bool m = Store.Move(id, delta); if (m) ProfilesChanged?.Invoke(); return m; }

    public void SetPolicy(ProfilePolicy p)
    {
        Store.SetPolicy(p);
        _log?.Invoke($"profiles: policy pinToSwitch={p.PinToSwitch} pinToEdit={p.PinToEdit} pinToQuit={p.PinToQuit} sessionPicker={p.SessionPicker} " +
                     $"idleReturnMin={p.IdleReturnMin} returnOnLock={p.ReturnOnLock} offeredHere={(p.OfferedOn(Store.MachineName) is { } o ? o.Count.ToString() : "all")}");
    }

    /// <summary>
    /// The Windows session ends (logoff, shutdown; plan 09B): when <paramref name="a"/> says so, a real
    /// <see cref="Switch"/> to « Classe » — the running app and the store stay in step even when the logoff is then
    /// cancelled by another program —, then the session marked ended and everything written now.
    /// </summary>
    public void EndSession(SessionAction a, DateTime? logonUtc, string why)
    {
        if (Store.InMemory) return;
        if (a.ReturnToClass && Store.Class is { Unreadable: false })
        {
            _log?.Invoke($"session: {why} -> classe (was {Store.ActiveId})");
            Switch(Profile.ClassId, why);
        }
        Store.SetSession(logonUtc ?? Store.SessionLogonUtc, true);
        Store.Flush();
        Words?.Store.Flush();
    }

    /// <summary>An import was applied (plan 09B): the running settings follow when the active profile or « Classe » was
    /// replaced; the list changed.</summary>
    public void ImportApplied(bool reloadActive)
    {
        if (reloadActive) ReloadActive();
        Words?.Reloaded("import");
        ProfilesChanged?.Invoke();
    }

    /// <summary>Teacher management turned on / off: the list may have changed (« Classe » added), the class layer too.</summary>
    public void ManagementChanged()
    {
        ReloadActive();
        Words?.Rebuild("management");
        ProfilesChanged?.Invoke();
    }
}
