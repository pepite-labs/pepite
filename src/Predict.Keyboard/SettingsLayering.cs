using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>The read-only machine layer (D11): <c>%PROGRAMDATA%\Pepite-Prediction\settings.json</c> =
/// <c>{ "schema": 1, "defaults": {…partial…}, "locked": {…partial…} }</c>.</summary>
internal sealed record MachineLayer(JsonObject? Defaults, JsonObject? Locked)
{
    public static readonly MachineLayer None = new(null, null);

    /// <summary>Where the machine file is looked for, in order (D29): <c>%PROGRAMDATA%\Pepite-Prediction\settings.json</c>,
    /// then the file of an older install, <c>%PROGRAMDATA%\PredictKeyboard\settings.json</c>. The first <b>trusted</b> one
    /// wins (<see cref="Load(IReadOnlyList{string}, Func{string, bool, FileSecurityInfo?}, Action{string}?)"/>): a file
    /// a standard user planted under the new name (Users may create files in ProgramData) is rejected and the
    /// administrator's old file still applies.</summary>
    public static IReadOnlyList<string> DefaultPaths
    {
        get
        {
            var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return new[] { Path.Combine(pd, AppPaths.FolderName, "settings.json"), Path.Combine(pd, AppPaths.LegacyFolderName, "settings.json") };
        }
    }

    /// <summary>The first of <paramref name="paths"/> that exists, is trusted and parses; <see cref="None"/> when none.</summary>
    public static MachineLayer Load(IReadOnlyList<string> paths, Func<string, bool, FileSecurityInfo?> security, Action<string>? log)
    {
        foreach (var p in paths)
            if (TryLoad(p, security, log) is { } m) return m;
        return None;
    }

    /// <summary>Well-known SIDs allowed to own the machine file: BUILTIN\Administrators, SYSTEM, TrustedInstaller.</summary>
    public static readonly IReadOnlySet<string> TrustedOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-32-544", "S-1-5-18", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    };

    /// <summary>
    /// Load the machine file. Trust rule: by default a standard user may create files in <c>C:\ProgramData</c>,
    /// so a planted file could otherwise lock settings for everybody. The file is used only when
    /// <see cref="Untrusted"/> finds nothing wrong with the file and its folder (<paramref name="security"/> reads
    /// them; injected for tests). Missing → <see cref="None"/>; unreadable / bad JSON / untrusted → <see cref="None"/> + log.
    /// </summary>
    public static MachineLayer Load(string path, Func<string, bool, FileSecurityInfo?> security, Action<string>? log) =>
        TryLoad(path, security, log) ?? None;

    /// <summary><see cref="Load(string, Func{string, bool, FileSecurityInfo?}, Action{string}?)"/>, null instead of
    /// <see cref="None"/> when the file is missing, untrusted or unreadable.</summary>
    private static MachineLayer? TryLoad(string path, Func<string, bool, FileSecurityInfo?> security, Action<string>? log)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string? why;
            FileSecurityInfo? file = null;
            try
            {
                file = security(path, false);
                var dir = security(Path.GetDirectoryName(path)!, true);
                why = file is null || dir is null ? "security unreadable" : Untrusted(file, dir);
            }
            catch (Exception ex) { why = $"security unreadable ({ex.GetType().Name})"; }
            if (why != null)
            {
                log?.Invoke($"machine settings ignored ({why}): {path}");
                return null;
            }
            if (Parse(File.ReadAllText(path), out var error) is not { } m)
            {
                log?.Invoke($"machine settings ignored ({error}): {path}");
                return null;
            }
            foreach (var part in new[] { m.Defaults, m.Locked })
                if (VoiceId.MigrateJson(part) is { } migrated) log?.Invoke($"machine settings: speech.voice read as {migrated}");
            log?.Invoke($"machine settings: {path} (owner {file!.OwnerSid}), defaults [{string.Join(", ", Json.LeafPaths(m.Defaults))}], " +
                        $"locked [{string.Join(", ", Json.LeafPaths(m.Locked))}]");
            return m;
        }
        catch (Exception ex)
        {
            log?.Invoke($"machine settings ignored ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>The schema this build reads (<c>"schema": 1</c>; a file without the key is read as schema 1).</summary>
    public const int Schema = 1;

    /// <summary>
    /// Parse the machine file's text, without the trust check (plan 12: shared by the app and <c>--check-config</c>, which
    /// the installer runs on a school's file before it copies it). Null + <paramref name="error"/> when the text is not
    /// JSON, not an object, has another <c>schema</c>, or a <c>defaults</c> / <c>locked</c> that is not an object.
    /// </summary>
    public static MachineLayer? Parse(string text, out string? error)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
            Materialize(root);
        }
        catch (System.Text.Json.JsonException ex) { error = "invalid JSON: " + ex.Message; return null; }
        // a duplicate key: JsonObject fails when it is first read (ArgumentException), here rather than in the app
        catch (ArgumentException) { error = "invalid JSON: duplicate key"; return null; }
        if (root is not JsonObject o) { error = "not a JSON object"; return null; }
        if (o["schema"] is { } s && !(s is JsonValue v && v.TryGetValue<double>(out var n) && n == Schema))
        {
            error = "unknown schema " + s.ToJsonString();
            return null;
        }
        foreach (var key in new[] { "defaults", "locked" })
            if (o[key] is { } part && part is not JsonObject) { error = $"\"{key}\" is not an object"; return null; }
        error = null;
        return new MachineLayer(o["defaults"] as JsonObject, o["locked"] as JsonObject);
    }

    /// <summary>Reads every object and array once, so that a duplicate key anywhere fails in <see cref="Parse"/>.</summary>
    private static void Materialize(JsonNode? node)
    {
        if (node is JsonObject obj) foreach (var kv in obj) Materialize(kv.Value);
        else if (node is JsonArray arr) foreach (var item in arr) Materialize(item);
    }

    /// <summary>Exit codes of <c>Pepite.exe --check-config &lt;file&gt;</c>.</summary>
    public const int CheckValid = 0, CheckInvalid = 2, CheckUnreadable = 3;

    /// <summary><c>--check-config</c> (plan 12 step 2b): 0 = a valid machine file, 2 = invalid JSON or unknown schema,
    /// 3 = missing or unreadable. No trust check (the installer validates a file before it protects it).</summary>
    public static int Check(string? path, Func<string, string> read, out string message)
    {
        if (string.IsNullOrEmpty(path)) { message = "no file given"; return CheckUnreadable; }
        string text;
        try { text = read(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            message = $"unreadable ({ex.GetType().Name}): {path}";
            return CheckUnreadable;
        }
        if (Parse(text, out var error) is { } m)
        {
            message = $"valid: defaults [{string.Join(", ", Json.LeafPaths(m.Defaults))}], locked [{string.Join(", ", Json.LeafPaths(m.Locked))}]";
            return CheckValid;
        }
        message = "invalid: " + error;
        return CheckInvalid;
    }

    // Win32 access rights that let someone change the file's content or its protection
    private const int WriteData = 0x2, AppendData = 0x4, DeleteChild = 0x40, Delete = 0x10000, WriteDac = 0x40000,
                      WriteOwner = 0x80000, GenericAll = 0x10000000, GenericWrite = 0x40000000;
    public const int FileWriteRights = WriteData | AppendData | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;
    /// <summary>On the folder: creating new files is fine (the default ProgramData ACL lets Users do it), but nobody
    /// else may delete / replace the file in it or change the folder's protection.</summary>
    public const int FolderWriteRights = DeleteChild | Delete | WriteDac | WriteOwner | GenericAll;

    /// <summary>Why the machine file must not be trusted, or null: the file and its folder must not be reparse
    /// points (a link to a user-controlled place), both must be owned by a trusted SID, and no "allow" entry may give
    /// a non-trusted SID the right to change the file (or delete / replace it through the folder).</summary>
    public static string? Untrusted(FileSecurityInfo file, FileSecurityInfo folder)
    {
        if (file.IsReparsePoint) return "the file is a link";
        if (folder.IsReparsePoint) return "its folder is a link";
        if (file.OwnerSid is null || !TrustedOwners.Contains(file.OwnerSid)) return $"owner {file.OwnerSid ?? "unknown"}";
        if (folder.OwnerSid is null || !TrustedOwners.Contains(folder.OwnerSid)) return $"folder owner {folder.OwnerSid ?? "unknown"}";
        foreach (var ace in file.Aces)
            if (ace.Allow && (ace.Rights & FileWriteRights) != 0 && !TrustedOwners.Contains(ace.Sid))
                return $"{ace.Sid} may write the file";
        foreach (var ace in folder.Aces)
            if (ace.Allow && (ace.Rights & FolderWriteRights) != 0 && !TrustedOwners.Contains(ace.Sid))
                return $"{ace.Sid} may replace files in its folder";
        return null;
    }
}

/// <summary>What the trust rule needs to know about a file or folder (read by the app with the Windows ACL API).</summary>
internal sealed record FileSecurityInfo(string? OwnerSid, bool IsReparsePoint, IReadOnlyList<AceInfo> Aces);
internal readonly record struct AceInfo(string Sid, int Rights, bool Allow);

/// <summary>Layer order, lowest first (plan 09, D29): built-in defaults &lt; machine defaults &lt; the « Classe » profile
/// (inherited by every pupil; null when the active profile is « Classe » itself or management was never on) &lt; the
/// profile's own layer &lt; the exam overlay (9C, null for now) &lt; machine locked.</summary>
internal static class SettingsLayering
{
    public static (AppSettings Effective, ISet<string> LockedPaths) Merge(JsonObject? machineDefaults, JsonObject? classLayer,
                                                                          JsonObject? user, JsonObject? examOverlay,
                                                                          JsonObject? machineLocked)
    {
        var merged = new AppSettings().ToJson();
        Json.MergeInto(merged, machineDefaults);
        Json.MergeInto(merged, classLayer);
        Json.MergeInto(merged, user);
        Json.MergeInto(merged, examOverlay);
        Json.MergeInto(merged, machineLocked);
        var locked = new HashSet<string>(Json.LeafPaths(machineLocked), StringComparer.Ordinal);
        return (AppSettings.FromJson(merged), locked);
    }

    public static (AppSettings Effective, ISet<string> LockedPaths) Merge(JsonObject? machineDefaults, JsonObject? user,
                                                                          JsonObject? machineLocked) =>
        Merge(machineDefaults, null, user, null, machineLocked);

    public static (AppSettings Effective, ISet<string> LockedPaths) Merge(MachineLayer machine, JsonObject? user) =>
        Merge(machine.Defaults, null, user, null, machine.Locked);

    public static (AppSettings Effective, ISet<string> LockedPaths) Merge(MachineLayer machine, JsonObject? classLayer, JsonObject? user) =>
        Merge(machine.Defaults, classLayer, user, null, machine.Locked);

    /// <summary>
    /// The user layer after an edit: the previous user layer, plus the leaves the user actually changed
    /// (<paramref name="edited"/> vs <paramref name="current"/>), never locked ones. A changed leaf that equals the
    /// baseline (built-in + machine defaults + the « Classe » layer of a pupil + locked) is <b>removed</b> instead of
    /// written, and unchanged leaves are left as they were — so a later change of a machine default or of the class
    /// still reaches everyone who did not choose that value themselves (saving or dragging must not freeze the defaults
    /// of the day into the profile).
    /// </summary>
    public static JsonObject NewUserLayer(JsonObject? oldUser, AppSettings current, AppSettings edited,
                                          MachineLayer machine, ISet<string> locked, JsonObject? classLayer = null)
    {
        var user = Json.Clone(oldUser);
        var cur = current.ToJson();
        var ed = edited.Validated().ToJson();
        var baseline = Merge(machine.Defaults, classLayer, null, null, machine.Locked).Effective.ToJson();
        var paths = new SortedSet<string>(Json.LeafPaths(cur).Concat(Json.LeafPaths(ed)), StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (Json.IsLocked(locked, path)) continue;
            var (hasE, e) = Json.Get(ed, path);
            var (hasC, c) = Json.Get(cur, path);
            if (hasE == hasC && Json.Same(e, c)) continue;              // not changed now: keep what the file says
            var (hasB, b) = Json.Get(baseline, path);
            if (!hasE || (hasB && Json.Same(e, b))) user = Json.Without(user, new[] { path });
            else Json.Set(user, path, e);
        }
        return user;
    }

    /// <summary>
    /// A pupil's layer moved from one « Classe » to another (import, 9B), pure: the result gives, over
    /// <paramref name="targetClass"/>, the same values as <paramref name="pupil"/> over <paramref name="sourceClass"/>
    /// (built-in defaults below both; machine layers are the target PC's). Leaves the target class already gives are
    /// dropped (the pupil keeps inheriting them); unknown keys of the pupil are kept as they are. A value only the target
    /// class sets, which the source effective settings do not have at all, cannot be undone by a layer and stays.
    /// </summary>
    public static JsonObject Rebase(JsonObject? sourceClass, JsonObject? pupil, JsonObject? targetClass)
    {
        var builtin = new AppSettings().ToJson();
        var src = Json.MergeInto(Json.MergeInto(Json.Clone(builtin), sourceClass), pupil);
        var tgt = Json.MergeInto(Json.Clone(builtin), targetClass);
        var result = Json.Clone(pupil);
        var paths = new SortedSet<string>(Json.LeafPaths(pupil).Concat(Json.LeafPaths(sourceClass)).Concat(Json.LeafPaths(targetClass)),
                                          StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var (hasS, s) = Json.Get(src, path);
            if (!hasS) continue;
            var (hasT, t) = Json.Get(tgt, path);
            if (hasT && Json.Same(s, t)) result = Json.Without(result, new[] { path });
            else Json.Set(result, path, s);
        }
        return result;
    }
}
