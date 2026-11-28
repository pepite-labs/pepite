using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>
/// The one migration of 0.7.0 (plan 09 step 6, D29 Q8): <c>%APPDATA%\PredictKeyboard\settings.json</c> (0.6.1 and before)
/// becomes the profile « Mon profil » under <c>%APPDATA%\Pepite-Prediction\profiles\</c>. Order, each step on disk before
/// the next: the <c>profiles</c> folder (nothing is written when it cannot be created); app <c>state</c> → new settings.json; <c>lock.pin</c> (if any) → teacher.json; the profile; <b>index.json
/// last</b>; then the old file is renamed <c>settings.json.avant-profils</c> (kept, never deleted). A crash anywhere
/// leaves either no index (the migration runs again, reading the old file or its renamed copy) or a complete one. Already
/// migrated (an index, or profile folders the store rebuilds the index from) → nothing. The old file's content is read
/// as it is: bad JSON → an empty « Mon profil », the file is still kept; a file that cannot be read (locked, ACL) → an
/// exception before any write, retried at the next start. <see cref="OpenStores"/> is the start-up sequence: it never
/// throws, an IO / access failure gives a session on an in-memory « Mon profil » (0.6.1 fell back to defaults too).
/// </summary>
internal static class ProfileMigration
{
    public const string KeptSuffix = ".avant-profils";

    public enum Outcome { AlreadyDone, Migrated, Fresh }

    public static Outcome Run(string root, string legacyDir, Action<string>? log, Func<DateTime>? utcNow = null)
    {
        var now = utcNow ?? (() => DateTime.UtcNow);
        var profilesDir = Path.Combine(root, ProfileStore.DirName);
        if (File.Exists(ProfileStore.IndexPath(root))) return Outcome.AlreadyDone;
        if (Directory.Exists(profilesDir) &&
            Directory.GetDirectories(profilesDir).Any(d => Profile.IsValidId(Path.GetFileName(d)) && File.Exists(Path.Combine(d, ProfileStore.ProfileFile))))
        {
            log?.Invoke("migration: profile folders without an index; the store rebuilds it");
            return Outcome.AlreadyDone;
        }

        // 0. the profiles folder first: when it cannot be created (a file in its place, an ACL), nothing is written
        Directory.CreateDirectory(profilesDir);

        var legacy = Path.Combine(legacyDir, SettingsStore.FileName);
        var kept = legacy + KeptSuffix;
        var source = File.Exists(legacy) ? legacy : File.Exists(kept) ? kept : null;
        JsonObject old = new();
        if (source != null)
        {
            try
            {
                old = JsonNode.Parse(File.ReadAllText(source)) as JsonObject ?? throw new JsonException("not an object");
            }
            catch (JsonException ex)
            {
                log?.Invoke($"migration: {source} not usable ({ex.GetType().Name}); « Mon profil » starts from the defaults, the file is kept");
                old = new JsonObject();
            }
            // an IO / access error is not "bad content": it propagates before anything is written (no index), so the
            // next start tries again; the caller (OpenStores) runs this session on an in-memory « Mon profil »
        }

        // 1. app state (trayPromoted, …) into the new settings.json
        var store = new SettingsStore(root, log);
        store.Load();
        store.MergeStateNow(old["state"] as JsonObject);

        // 2. a PIN set by a hand-edited 0.6 file (never used by 0.6: the lock was not shipped) is kept for the teacher
        if (old["lock"] is JsonObject lk && TeacherLock.ParseHash(lk["pin"]) is { } pin)
        {
            var tf = new JsonFileStore(Path.Combine(profilesDir, ProfileStore.TeacherFile), log, "profiles", schema: 1);
            tf.Load();
            if (tf.Read(r => r["pin"] is null))
                tf.UpdateNow(r => r["pin"] = new JsonObject { ["alg"] = pin.Alg, ["iterations"] = pin.Iterations, ["salt"] = pin.Salt, ["hash"] = pin.Hash });
            log?.Invoke("migration: lock.pin moved to teacher.json");
        }

        // 3. the profile: everything but schema / state / lock is the user layer (voice ids normalised)
        var layer = Json.Clone(old);
        layer.Remove("schema");
        layer.Remove("state");
        layer.Remove("lock");
        if (VoiceId.MigrateJson(layer) is { } v) log?.Invoke($"migration: speech.voice migrated to {v}");
        var p = new Profile(Profile.NewId(), Strings.DefaultProfileName, Avatar.Default, now(), layer);
        var pf = new JsonFileStore(Path.Combine(profilesDir, p.Id, ProfileStore.ProfileFile), log, "profiles", moveBadAside: false, schema: Profile.Schema);
        pf.UpdateNow(r => Json.MergeInto(r, p.ToJson()));

        // 4. the index, last
        var idx = new JsonFileStore(ProfileStore.IndexPath(root), log, "profiles", schema: ProfileStore.IndexSchema);
        idx.UpdateNow(r =>
        {
            r["order"] = new JsonArray(JsonValue.Create(p.Id));
            r["default"] = null;
            r["active"] = p.Id;
            r["policy"] = new ProfilePolicy().ToJson();
        });

        // 5. the old file is kept under another name (a 0.6.1 started again later sees no settings, by design)
        if (source == legacy)
        {
            try
            {
                if (!File.Exists(kept)) File.Move(legacy, kept);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"migration: {legacy} not renamed ({ex.GetType().Name}); left as it is");
            }
        }
        var outcome = source is null ? Outcome.Fresh : Outcome.Migrated;
        log?.Invoke(outcome == Outcome.Fresh
            ? $"migration: first start, profile {p.Id} created"
            : $"migration: {source} -> profile {p.Id} ({Json.LeafPaths(layer).Count()} settings){(source == legacy ? $", kept as {SettingsStore.FileName}{KeptSuffix}" : "")}");
        return outcome;
    }

    /// <summary>The user layer of the old settings file (or its kept copy), for an in-memory session; empty when it
    /// cannot be read. Never throws.</summary>
    public static JsonObject ReadLegacyLayer(string legacyDir, Action<string>? log)
    {
        try
        {
            var legacy = Path.Combine(legacyDir, SettingsStore.FileName);
            var source = File.Exists(legacy) ? legacy : File.Exists(legacy + KeptSuffix) ? legacy + KeptSuffix : null;
            if (source is null || JsonNode.Parse(File.ReadAllText(source)) is not JsonObject old) return new JsonObject();
            old.Remove("schema");
            old.Remove("state");
            old.Remove("lock");
            VoiceId.MigrateJson(old);
            return old;
        }
        catch (Exception ex)
        {
            log?.Invoke($"migration: old settings not readable either ({ex.GetType().Name}); defaults this session");
            return new JsonObject();
        }
    }

    /// <summary>
    /// The start-up sequence (plan 09): the migration, then the app state and the profiles. Never throws: when the
    /// folder cannot be created or written (ACL, a file in its place, a full or offline redirected drive), the error is
    /// logged and the session runs on an in-memory « Mon profil » built from the old settings (nothing is written, so
    /// the next start retries the migration). <paramref name="appVersion"/> (the running version): at the first start of
    /// a new version the profiles are zipped first (<see cref="VersionBackup"/>), before anything of theirs is loaded.
    /// </summary>
    public static (SettingsStore App, ProfileStore Profiles) OpenStores(string root, string legacyDir, string? backupDir,
                                                                       Action<string>? log, Func<DateTime>? utcNow = null,
                                                                       string? appVersion = null)
    {
        var app = new SettingsStore(root, log);
        var profiles = new ProfileStore(root, backupDir, log, utcNow);
        bool appLoaded = false;
        try
        {
            var outcome = Run(root, legacyDir, log, utcNow);
            app.Load();
            appLoaded = true;
            VersionBackup.Run(app, profiles, appVersion, outcome == Outcome.AlreadyDone, log);
            profiles.Load();
        }
        catch (Exception ex)
        {
            log?.Invoke($"profiles: start-up failed ({ex.GetType().Name}: {ex.Message})");
            // read-only first: the second read neither moves a bad settings.json aside nor touches state.json
            app.MakeReadOnly();
            if (!appLoaded) try { app.Load(); } catch (Exception) { }
            profiles.LoadInMemory(ReadLegacyLayer(legacyDir, log), ex.GetType().Name);
        }
        return (app, profiles);
    }
}

/// <summary>
/// Plan installer-upgrade R5: at the first start of each new version (<c>state.lastVersion</c> differs), one
/// <c>profils-&lt;stamp&gt;-mise-a-jour.zip</c> of <c>profiles\</c> plus the root <c>settings.json</c> (as
/// <c>application/settings.json</c>) in the backups folder, the 5 newest kept. Runs before the profiles are loaded, so the
/// zip holds what the previous version left. A first start (no profiles before this start) records the version and
/// zips nothing; <c>lastVersion</c> is recorded only once a zip is written, so a failure is retried at the next start.
/// Skipped for an unknown version (a dev build) or a read-only <c>settings.json</c> (newer schema, unreadable). Never throws.
/// </summary>
internal static class VersionBackup
{
    public const string Reason = "mise-a-jour";

    public static void Run(SettingsStore app, ProfileStore profiles, string? appVersion, bool hadProfiles, Action<string>? log)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(appVersion) || appVersion == "?" || app.ReadOnly) return;
            var old = app.LastVersion;
            if (old == appVersion) return;
            var t0 = Environment.TickCount64;
            if (!hadProfiles)
            {
                app.SetLastVersion(appVersion);
                log?.Invoke($"version: {old ?? "none"} -> {appVersion}, first start, no backup");
                return;
            }
            if (!profiles.CanBackup)
            {
                log?.Invoke($"version: {old ?? "none"} -> {appVersion}, no backup folder, not recorded");
                return;
            }
            app.Flush();
            var zip = profiles.Backup(Reason, new[] { app.FilePath });
            if (zip is null)
            {
                log?.Invoke($"version: {old ?? "none"} -> {appVersion}, backup failed, retried next start");
                return;
            }
            app.SetLastVersion(appVersion);
            log?.Invoke($"version: {old ?? "none"} -> {appVersion}, backup {Path.GetFileName(zip)} in {Environment.TickCount64 - t0} ms");
        }
        catch (Exception ex)
        {
            log?.Invoke($"version: backup failed ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
