using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Predict.Keyboard;

/// <summary>
/// <c>settings.json</c> (per Windows account, D11) on a <see cref="JsonFileStore"/>: since 0.7.0 (plan 09) the file at the
/// root of <c>%APPDATA%\Pepite-Prediction</c> holds the app state (<c>state.trayPromoted</c>, formerly
/// <c>state.json</c>; <c>state.managementEnabled</c>), the settings themselves live in the profiles; up to 0.6.1 it
/// also held the user layer (<c>%APPDATA%\PredictKeyboard\settings.json</c>, read once by the migration). The whole
/// file is kept, so keys this version does not know are written back unchanged. Thread-safe.
/// </summary>
internal sealed class SettingsStore
{
    public const string FileName = "settings.json", LegacyStateFile = "state.json";

    private readonly JsonFileStore _file;
    private readonly string _legacy;
    private readonly Action<string>? _log;

    public SettingsStore(string dir, Action<string>? log)
    {
        _file = new JsonFileStore(Path.Combine(dir, FileName), log, "settings", moveBadAside: true, schema: AppSettings.Schema);
        _legacy = Path.Combine(dir, LegacyStateFile);
        _log = log;
    }

    /// <summary>The app's folder (plan 09: <c>%APPDATA%\Pepite-Prediction</c>).</summary>
    public static string DefaultDir => AppPaths.Root;

    public string FilePath => _file.FilePath;

    /// <summary>An in-memory session (plan 09, <see cref="ProfileMigration.OpenStores"/>): nothing is written, no bad
    /// file moved aside, <c>state.json</c> neither migrated nor deleted (<see cref="Load"/> skips that when read-only).</summary>
    public void MakeReadOnly() => _file.MakeReadOnly();
    public bool ReadOnly => _file.ReadOnly;

    /// <summary>Read the file (missing → empty; bad JSON → renamed <c>settings.json.bad-&lt;stamp&gt;</c>, empty),
    /// then migrate <c>state.json</c> once. Returns a copy of the user layer (<c>words/bar/speech/lock</c>).</summary>
    public JsonObject Load()
    {
        var t0 = Environment.TickCount64;
        _file.Load();
        // plan 08a: a bare SAPI voice name (0.4.0 and before) becomes sapi:<name>; written on the next save
        var migrated = _file.Read(root => VoiceId.MigrateJson(root));
        if (migrated != null) _log?.Invoke($"settings: speech.voice migrated to {migrated}");
        Migrate();
        _log?.Invoke($"settings: loaded {_file.FilePath} in {Environment.TickCount64 - t0} ms");
        return UserLayer();
    }

    /// <summary>Copy of the user layer (everything but <c>schema</c> and <c>state</c>).</summary>
    public JsonObject UserLayer()
    {
        var c = _file.Copy();
        c.Remove("schema");
        c.Remove("state");
        return c;
    }

    /// <summary>Copy of <c>state</c> (empty when absent).</summary>
    public JsonObject State() => _file.Read(r => Json.Clone(r["state"] as JsonObject));

    /// <summary>state.json → <c>state.trayPromoted</c>, written synchronously; state.json is deleted only once the
    /// new file is on disk (a failed write leaves it, and the migration is retried at the next start).</summary>
    private void Migrate()
    {
        try
        {
            if (!File.Exists(_legacy) || _file.ReadOnly) return;
            var old = JsonNode.Parse(File.ReadAllText(_legacy)) as JsonObject;
            _file.UpdateNow(root =>
            {
                if ((root["state"] as JsonObject)?["trayPromoted"] is null)
                {
                    var promoted = old?["trayPromoted"] as JsonObject ?? new JsonObject();
                    if (root["state"] is not JsonObject st) { st = new JsonObject(); root["state"] = st; }
                    st["trayPromoted"] = Json.Copy(promoted);
                }
            });
            File.Delete(_legacy);
            _log?.Invoke($"settings: migrated {LegacyStateFile} into {FileName} and deleted it");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"settings: migration of {LegacyStateFile} failed ({ex.GetType().Name}: {ex.Message}); kept, retried next start");
        }
    }

    /// <summary>Replace the whole user layer (keeps <c>schema</c>, <c>state</c> and nothing else of the old one),
    /// write in the background.</summary>
    public Task ReplaceUser(JsonObject user) => _file.Update(root =>
    {
        foreach (var k in root.Select(kv => kv.Key).Where(k => k is not "schema" and not "state").ToList()) root.Remove(k);
        Json.MergeInto(root, user);
    });

    /// <summary>Merge user values into the file and write it in the background.</summary>
    public Task SaveUser(JsonObject userValues) => _file.Update(root => Json.MergeInto(root, userValues));

    // ---------------------------------------------------------------- app state
    public bool IsPromoted(string exePath) =>
        _file.Read(root => (root["state"] as JsonObject)?["trayPromoted"] is JsonObject t &&
                           t.Any(kv => string.Equals(kv.Key, exePath, StringComparison.OrdinalIgnoreCase) &&
                                       kv.Value is JsonValue v && v.TryGetValue<bool>(out var b) && b));

    public Task MarkPromoted(string exePath) => _file.Update(root =>
    {
        if (root["state"] is not JsonObject st) { st = new JsonObject(); root["state"] = st; }
        if (st["trayPromoted"] is not JsonObject t) { t = new JsonObject(); st["trayPromoted"] = t; }
        t[exePath] = true;
    });

    /// <summary>Plan 09 (D29): teacher management turned on (<c>state.managementEnabled</c>; off by default).</summary>
    public bool ManagementEnabled =>
        _file.Read(root => (root["state"] as JsonObject)?["managementEnabled"] is JsonValue v && v.TryGetValue<bool>(out var b) && b);

    public Task SetManagementEnabled(bool on) => _file.Update(root =>
    {
        if (root["state"] is not JsonObject st) { st = new JsonObject(); root["state"] = st; }
        st["managementEnabled"] = on;
    });

    /// <summary>The welcome window was shown on this Windows account (<c>state.welcomeShown</c>, app-level: one flag for
    /// every profile, plan « aide intégrée » Q3); null = never recorded (a first start, or an install before 0.10.1).</summary>
    public bool? WelcomeShown =>
        _file.Read(root => (root["state"] as JsonObject)?["welcomeShown"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null);

    public Task SetWelcomeShown(bool shown) => _file.Update(root =>
    {
        if (root["state"] is not JsonObject st) { st = new JsonObject(); root["state"] = st; }
        st["welcomeShown"] = shown;
    });

    /// <summary>Write <c>state</c> values now (migration).</summary>
    public void MergeStateNow(JsonObject? state)
    {
        if (state is null || state.Count == 0) return;
        _file.UpdateNow(root =>
        {
            if (root["state"] is not JsonObject st) { st = new JsonObject(); root["state"] = st; }
            foreach (var (k, v) in state.ToList()) if (st[k] is null) st[k] = Json.Copy(v);
        });
    }

    /// <summary>Wait for the pending writes (exit, tests).</summary>
    public void Flush(int? timeoutMs = null) => _file.Flush(timeoutMs);
}

/// <summary><see cref="IPromotionState"/> on <c>state.trayPromoted</c> of settings.json (replaces state.json).</summary>
internal sealed class SettingsPromotionState : IPromotionState
{
    private readonly SettingsStore _store;
    public SettingsPromotionState(SettingsStore store) => _store = store;
    public bool IsPromoted(string exePath) => _store.IsPromoted(exePath);
    public void MarkPromoted(string exePath) { _store.MarkPromoted(exePath); _store.Flush(); }
}
