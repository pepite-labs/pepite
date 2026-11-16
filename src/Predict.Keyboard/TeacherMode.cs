using System;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>
/// Teacher management on / off (plan 09 step 6, D29): <see cref="Activate"/> = create the PIN (+ recovery code) and the
/// « Classe » profile (empty layer: nothing changes for the pupil) and set <c>state.managementEnabled</c>;
/// <see cref="Deactivate"/> (after the PIN) = clear teacher.json and the flag, keep every profile and the active one;
/// activating again restores the list (a new PIN is asked).
/// </summary>
internal sealed class TeacherMode
{
    private readonly SettingsStore _app;
    private readonly ProfileStore _profiles;
    private readonly TeacherLock _lock;
    private readonly Action<string>? _log;

    public TeacherMode(SettingsStore app, ProfileStore profiles, TeacherLock teacherLock, Action<string>? log)
    {
        _app = app;
        _profiles = profiles;
        _lock = teacherLock;
        _log = log;
    }

    public bool Enabled => _app.ManagementEnabled && _lock.HasPin;
    public TeacherLock Lock => _lock;

    /// <summary>Turn management on with <paramref name="pin"/>; returns the recovery code to show once.</summary>
    public string Activate(string pin)
    {
        var code = _lock.SetPin(pin);
        EnableAfterPin();
        return code;
    }

    /// <summary>The second half of <see cref="Activate"/>, on the UI thread once the PIN is stored (the PIN dialog hashes
    /// it on a worker thread): « Classe » and the flag.</summary>
    public void EnableAfterPin()
    {
        if (!_lock.HasPin) throw new InvalidOperationException("no PIN");
        if (_profiles.Class is null)
        {
            var name = Strings.ClassProfileName;
            if (_profiles.CheckName(name) == NameProblem.Duplicate) name = Strings.ClassProfileName + " (2)";
            _profiles.Add(name, new Avatar(null, ProfileColor.Vert), new JsonObject(), Profile.ClassId);
        }
        _app.SetManagementEnabled(true);
        _log?.Invoke($"teacher mode: on ({_profiles.Count} profiles)");
    }

    /// <summary>Turn management off (the caller checked the PIN). Profiles stay on disk, the active one stays active.</summary>
    public void Deactivate()
    {
        _lock.Clear();
        _app.SetManagementEnabled(false);
        _log?.Invoke($"teacher mode: off ({_profiles.Count} profiles kept, active {_profiles.ActiveId})");
    }
}
