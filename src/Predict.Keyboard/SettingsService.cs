using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>
/// The effective settings of the running app (UI thread): built-in defaults &lt; machine defaults &lt; the « Classe »
/// layer (a pupil's profile, plan 09) &lt; the active profile's layer &lt; machine-locked values (D11, D29).
/// <see cref="Apply"/> takes an edited copy (settings window, a drag of the bar), writes only the user-changeable
/// differences into the active profile, and raises <see cref="Changed"/>(old, new); <see cref="Retarget"/> points it at
/// another profile (a switch) or reloads the class layer, raising <see cref="Changed"/> when the effective settings differ.
/// </summary>
internal sealed class SettingsService
{
    private readonly MachineLayer _machine;
    private readonly Action<string>? _log;
    private JsonObject _user;
    private JsonObject? _class;
    private Action<JsonObject> _save;

    public AppSettings Current { get; private set; }
    public ISet<string> Locked { get; private set; }
    public MachineLayer Machine => _machine;
    /// <summary>The profile id the settings are saved to (logs carry ids, never first names).</summary>
    public string Target { get; private set; }
    public event Action<AppSettings, AppSettings>? Changed;

    /// <param name="saveUser">writes the active profile's new layer (background).</param>
    public SettingsService(MachineLayer machine, JsonObject user, JsonObject? classLayer, Action<JsonObject> saveUser, string target,
                           Action<string>? log)
    {
        _machine = machine;
        _log = log;
        _user = Json.Clone(user);
        _class = classLayer is null ? null : Json.Clone(classLayer);
        _save = saveUser;
        Target = target;
        (Current, Locked) = SettingsLayering.Merge(machine, _class, _user);
        log?.Invoke($"settings ({target}): {Describe(Current)}" + (Locked.Count > 0 ? $"; locked [{string.Join(", ", Locked)}]" : ""));
    }

    public bool IsLocked(string path) => Json.IsLocked(Locked, path);

    /// <summary>The effective settings of a profile layer over a class layer on this PC.</summary>
    public (AppSettings Effective, ISet<string> Locked) EffectiveOf(JsonObject? classLayer, JsonObject? user) =>
        SettingsLayering.Merge(_machine, classLayer, user);

    /// <summary>Point at another profile (or reload the current one's layers).</summary>
    public void Retarget(JsonObject user, JsonObject? classLayer, Action<JsonObject> saveUser, string target)
    {
        _user = Json.Clone(user);
        _class = classLayer is null ? null : Json.Clone(classLayer);
        _save = saveUser;
        var old = Current;
        bool moved = target != Target;
        Target = target;
        (Current, Locked) = SettingsLayering.Merge(_machine, _class, _user);
        if (moved) _log?.Invoke($"settings ({target}): {Describe(Current)}");
        if (old == Current) return;
        if (!moved) _log?.Invoke($"settings reloaded ({target}): {Describe(Current)}");
        Changed?.Invoke(old, Current);
    }

    public void Apply(AppSettings edited)
    {
        _user = SettingsLayering.NewUserLayer(_user, Current, edited, _machine, Locked, _class);
        _save(Json.Clone(_user));
        var old = Current;
        (Current, Locked) = SettingsLayering.Merge(_machine, _class, _user);
        if (old == Current) return;
        _log?.Invoke($"settings applied ({Target}): {Describe(Current)}");
        Changed?.Invoke(old, Current);
    }

    /// <summary>The bar was dragged (free position): remember its top-left in physical px.</summary>
    public void SetFreePosition(int x, int y) => Apply(Current with { FreeX = x, FreeY = y });

    /// <summary>The « grande fenêtre » of the reading mode was moved / resized (physical px), plan 07.</summary>
    public void SetReadingWindow(int x, int y, int w, int h) =>
        Apply(Current with { Reading = Current.Reading with { WinX = x, WinY = y, WinW = w, WinH = h } });

    public static string Describe(AppSettings s) =>
        $"words {s.WordCount} size {s.Size} pictures {(s.Pictures ? "on" : "off")} keys {AppSettings.KeyNames[s.Keys]} " +
        $"font {AppSettings.FontNames[s.Font]} spacing {AppSettings.SpacingNames[s.Spacing]} position {AppSettings.PositionNames[s.Position]}" +
        (s.FreeX is int x ? $" ({x},{s.FreeY})" : "") +
        $" punctuation {AppSettings.PunctuationNames[s.Punctuation]}" +
        $" speech inserted={s.Speech.SpeakInserted} words={s.Speech.SpeakWords} sentences={s.Speech.SpeakSentences} reread={s.Speech.RereadEdits} " +
        $"voice={s.Speech.Voice ?? "(default)"} rate={s.Speech.Rate} volume={s.Speech.Volume}" +
        $" reading={s.Reading.Enabled} clipboard={s.Reading.ClipboardFallback} reading-pictures={(s.Reading.Pictures is bool p ? p.ToString() : "follow")} welcome={s.Welcome}";
}
