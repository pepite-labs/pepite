using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Predict.Keyboard;

/// <summary>
/// The teacher's switches (D29): PIN to change profile (off), PIN to change the current profile's settings (on), PIN to
/// quit (off), « Qui es-tu ? » at the start of each Windows session (on); the returns to « Classe » after an inactivity
/// (<see cref="IdleReturnMin"/>: 0 = never, or 10 / 20 / 30 / 60 min) and at Win+L (<see cref="ReturnOnLock"/>), plan 09B;
/// <see cref="OfferedHere"/>: « Profils proposés sur ce poste », the pupils « Qui es-tu ? » shows on each PC, keyed by
/// the computer name (a roaming or shared profiles folder must not impose one PC's list on the others); no entry for a
/// PC = every pupil.
/// </summary>
internal sealed record ProfilePolicy(bool PinToSwitch = false, bool PinToEdit = true, bool PinToQuit = false,
                                     bool SessionPicker = true, int IdleReturnMin = 0, bool ReturnOnLock = false,
                                     IReadOnlyDictionary<string, IReadOnlyList<string>>? OfferedHere = null)
{
    public static readonly IReadOnlyList<int> IdleChoices = new[] { 0, 10, 20, 30, 60 };

    /// <summary>The pupils offered on <paramref name="machine"/>, or null = all of them.</summary>
    public IReadOnlyList<string>? OfferedOn(string machine) =>
        OfferedHere is { } m && m.TryGetValue(machine.ToUpperInvariant(), out var ids) ? ids : null;

    /// <summary>This policy with <paramref name="ids"/> (null = all pupils) as the list of <paramref name="machine"/>.</summary>
    public ProfilePolicy WithOffered(string machine, IEnumerable<string>? ids)
    {
        var m = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (OfferedHere != null) foreach (var kv in OfferedHere) m[kv.Key] = kv.Value;
        if (ids is null) m.Remove(machine.ToUpperInvariant());
        else m[machine.ToUpperInvariant()] = ids.Distinct().ToList();
        return this with { OfferedHere = m.Count == 0 ? null : m };
    }

    public JsonObject ToJson()
    {
        JsonObject? offered = null;
        if (OfferedHere is { Count: > 0 } m)
        {
            offered = new JsonObject();
            foreach (var kv in m.OrderBy(k => k.Key, StringComparer.Ordinal))
                offered[kv.Key] = new JsonArray(kv.Value.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }
        return new()
        {
            ["pinToSwitch"] = PinToSwitch, ["pinToEdit"] = PinToEdit, ["pinToQuit"] = PinToQuit, ["sessionPicker"] = SessionPicker,
            ["idleReturnMin"] = IdleReturnMin, ["returnOnLock"] = ReturnOnLock, ["offeredHere"] = offered,
        };
    }

    public static ProfilePolicy FromJson(JsonNode? n)
    {
        var o = n as JsonObject;
        var d = new ProfilePolicy();
        static bool? B(JsonNode? x) => x is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        static int? I(JsonNode? x) => x is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        int idle = I(o?["idleReturnMin"]) ?? 0;
        Dictionary<string, IReadOnlyList<string>>? offered = null;
        if (o?["offeredHere"] is JsonObject om)
            foreach (var kv in om)
                if (kv.Value is JsonArray a && kv.Key.Length > 0)
                    (offered ??= new(StringComparer.Ordinal))[kv.Key.ToUpperInvariant()] =
                        a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                         .Where(s => Profile.IsValidId(s) && s != Profile.ClassId).Select(s => s!).Distinct().ToList();
        return new ProfilePolicy(B(o?["pinToSwitch"]) ?? d.PinToSwitch, B(o?["pinToEdit"]) ?? d.PinToEdit, B(o?["pinToQuit"]) ?? d.PinToQuit,
                                 B(o?["sessionPicker"]) ?? d.SessionPicker, IdleChoices.Contains(idle) ? idle : 0,
                                 B(o?["returnOnLock"]) ?? d.ReturnOnLock, offered);
    }
}

/// <summary>A profile as the store holds it: its file, and whether that file could be read.</summary>
internal sealed class ProfileEntry
{
    public Profile Profile { get; internal set; }
    /// <summary>The file exists but is not valid JSON / cannot be read: shown « (illisible) », never written, never
    /// activated; it can be deleted (after a backup).</summary>
    public bool Unreadable { get; }
    internal JsonFileStore File { get; }
    public string Id => Profile.Id;
    public bool IsClass => Profile.IsClass;

    internal ProfileEntry(Profile p, JsonFileStore file, bool unreadable)
    {
        Profile = p;
        File = file;
        Unreadable = unreadable;
    }
}

/// <summary>
/// <c>&lt;root&gt;\profiles\</c> (plan 09, step 5): <c>index.json</c> (order, default, active, policy, session) and one
/// <see cref="JsonFileStore"/> per profile (<c>&lt;id&gt;\profile.json</c>). A lost or corrupt index is rebuilt from the
/// folders; a profile that cannot be read is listed « (illisible) » while the others work. Zip backups of
/// <c>profiles\</c> go to <c>%LOCALAPPDATA%\Pepite-Prediction\sauvegardes\</c> before a delete (and later an import / a
/// reset) and once a day; the 5 newest of each reason are kept (an import never pushes out the last daily backup). UI thread (writes are queued to the background by the file stores).
/// </summary>
internal sealed class ProfileStore
{
    public const string DirName = "profiles", IndexFile = "index.json", ProfileFile = "profile.json", TeacherFile = "teacher.json";
    public const int KeepBackups = 5;
    public const int IndexSchema = 1;

    private readonly string _dir;
    private readonly string? _backupDir;
    private readonly Action<string>? _log;
    private readonly Func<DateTime> _utcNow;
    private readonly JsonFileStore _index;
    private readonly List<ProfileEntry> _list = new();

    /// <summary>This PC's name, the key of « Profils proposés sur ce poste » (<see cref="ProfilePolicy.OfferedHere"/>).</summary>
    public string MachineName { get; set; } = Environment.MachineName;

    public ProfileStore(string root, string? backupDir, Action<string>? log, Func<DateTime>? utcNow = null)
    {
        _dir = Path.Combine(root, DirName);
        _backupDir = backupDir;
        _log = log;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _index = new JsonFileStore(Path.Combine(_dir, IndexFile), log, "profiles", moveBadAside: true, schema: IndexSchema);
    }

    public string ProfilesDir => _dir;
    public string TeacherPath => Path.Combine(_dir, TeacherFile);
    public static string IndexPath(string root) => Path.Combine(root, DirName, IndexFile);

    public IReadOnlyList<ProfileEntry> Profiles => _list.ToList();
    public int Count => _list.Count;
    public string ActiveId { get; private set; } = "";
    public ProfileEntry Active => Get(ActiveId) ?? _list[0];
    public ProfileEntry? Class => Get(Profile.ClassId);
    public ProfilePolicy Policy { get; private set; } = new();
    public DateTime? SessionLogonUtc { get; private set; }
    public bool SessionEnded { get; private set; }

    public ProfileEntry? Get(string id) => _list.FirstOrDefault(e => e.Id == id);

    /// <summary>The layer a pupil inherits (« Classe »), or null for « Classe » itself / when there is none.</summary>
    public JsonObject? ClassLayerFor(string id) => id == Profile.ClassId ? null : Class is { Unreadable: false } c ? c.Profile.Layer : null;

    // ------------------------------------------------------------------ loading
    public void Load()
    {
        var t0 = Environment.TickCount64;
        Directory.CreateDirectory(_dir);
        var index = _index.Load();
        bool rebuild = !_index.Existed || _index.Unreadable;
        _list.Clear();
        var folders = Directory.GetDirectories(_dir).Select(Path.GetFileName).Where(n => Profile.IsValidId(n))
                               .Where(n => File.Exists(Path.Combine(_dir, n!, ProfileFile))).Select(n => n!).ToList();
        var loaded = new Dictionary<string, ProfileEntry>();
        foreach (var id in folders) loaded[id] = LoadOne(id);
        var listed = (index["order"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                     .Where(s => s != null).Select(s => s!).Distinct().ToList() ?? new List<string>();
        var order = listed.Where(loaded.ContainsKey).ToList();
        if (order.Count != listed.Count || order.Count != loaded.Count) rebuild = true;   // a folder lost or added behind our back
        // folders the index does not list (a lost index, a crash between the profile and the index): after the listed ones,
        // « Classe » first, then by creation date
        foreach (var e in loaded.Values.Where(e => !order.Contains(e.Id)).OrderBy(e => e.IsClass ? 0 : 1).ThenBy(e => e.Profile.CreatedUtc))
            order.Add(e.Id);
        foreach (var id in order) _list.Add(loaded[id]);
        if (Class is { } cls && _list.IndexOf(cls) != 0) { _list.Remove(cls); _list.Insert(0, cls); }

        Policy = ProfilePolicy.FromJson(index["policy"]);
        var session = index["session"] as JsonObject;
        SessionLogonUtc = session?["logonTimeUtc"] is JsonValue lv && lv.TryGetValue<string>(out var ls) &&
                          DateTime.TryParse(ls, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var lt)
                          ? lt : null;
        SessionEnded = session?["ended"] is JsonValue ev && ev.TryGetValue<bool>(out var ended) && ended;

        if (_list.Count == 0)
        {
            // nothing on disk (the migration normally made « Mon profil »): start single-user
            var p = new Profile(Profile.NewId(), Strings.DefaultProfileName, Avatar.Default, _utcNow(), new JsonObject());
            _list.Add(NewEntry(p));
            _log?.Invoke($"profiles: none found, created {p.Id}");
            rebuild = true;
        }
        var active = index["active"] is JsonValue av && av.TryGetValue<string>(out var a) ? a : null;
        ActiveId = active is not null && Get(active) is { Unreadable: false } ? active : FallbackActive();
        if (active != ActiveId) { _log?.Invoke($"profiles: active {active ?? "(none)"} not usable, using {ActiveId}"); rebuild = true; }
        if (rebuild)
        {
            if (_index.Existed) _log?.Invoke($"profiles: index rebuilt from {_list.Count} folders");
            SaveIndex();
        }
        _log?.Invoke($"profiles: loaded {_list.Count} in {Environment.TickCount64 - t0} ms (active {ActiveId}, " +
                     $"{_list.Count(e => e.Unreadable)} unreadable)");
    }

    /// <summary>teacher.json, loaded: a bad file is moved aside normally; in an in-memory session it is read-only (never
    /// moved, never written).</summary>
    public JsonFileStore OpenTeacherFile()
    {
        var f = new JsonFileStore(TeacherPath, _log, "profiles", moveBadAside: !InMemory, schema: 1);
        if (InMemory) f.MakeReadOnly();
        f.Load();
        return f;
    }

    /// <summary>True for a session on <see cref="LoadInMemory"/>: nothing is saved.</summary>
    public bool InMemory { get; private set; }

    /// <summary>The profiles folder cannot be used this session (<see cref="ProfileMigration.OpenStores"/>): one
    /// « Mon profil » with <paramref name="layer"/>, kept in memory only — every file store is read-only, no backup.</summary>
    public void LoadInMemory(JsonObject layer, string why)
    {
        _list.Clear();
        _index.MakeReadOnly();
        InMemory = true;
        var p = new Profile(Profile.NewId(), Strings.DefaultProfileName, Avatar.Default, _utcNow(), Json.Clone(layer));
        var file = new JsonFileStore(Path.Combine(_dir, p.Id, ProfileFile), _log, "profiles", moveBadAside: false, schema: Profile.Schema);
        file.MakeReadOnly();
        _list.Add(new ProfileEntry(p, file, false));
        ActiveId = p.Id;
        Policy = new ProfilePolicy();
        _log?.Invoke($"profiles: {why}; « Mon profil » {p.Id} in memory only, nothing saved this session");
    }

    private ProfileEntry LoadOne(string id)
    {
        var file = new JsonFileStore(Path.Combine(_dir, id, ProfileFile), _log, "profiles", moveBadAside: false, schema: Profile.Schema);
        var o = file.Load();
        if (file.Unreadable)
            return new ProfileEntry(new Profile(id, Strings.ProfileUnreadable, Avatar.Default, DateTime.UnixEpoch, new JsonObject()), file, true);
        var fallback = id == Profile.ClassId ? Strings.ClassProfileName : Strings.DefaultProfileName;
        return new ProfileEntry(Profile.FromJson(o, id, fallback), file, false);
    }

    private string FallbackActive() =>
        (Class is { Unreadable: false } c ? c : _list.FirstOrDefault(e => !e.Unreadable) ?? _list[0]).Id;

    private ProfileEntry NewEntry(Profile p)
    {
        var file = new JsonFileStore(Path.Combine(_dir, p.Id, ProfileFile), _log, "profiles", moveBadAside: false, schema: Profile.Schema);
        if (InMemory) file.MakeReadOnly();
        var e = new ProfileEntry(p, file, false);
        file.Update(root => ReplaceAll(root, p.ToJson()));
        return e;
    }

    private static void ReplaceAll(JsonObject root, JsonObject with)
    {
        foreach (var k in root.Select(kv => kv.Key).ToList()) root.Remove(k);
        Json.MergeInto(root, with);
    }

    private void SaveProfile(ProfileEntry e)
    {
        if (e.Unreadable) return;
        var json = e.Profile.ToJson();
        e.File.Update(root => ReplaceAll(root, json));
    }

    public void SaveIndex() => _index.Update(root =>
    {
        root["order"] = new JsonArray(_list.Select(e => (JsonNode?)JsonValue.Create(e.Id)).ToArray());
        root["default"] = Class != null ? Profile.ClassId : null;
        root["active"] = ActiveId;
        root["policy"] = Policy.ToJson();
        root["session"] = new JsonObject
        {
            ["logonTimeUtc"] = SessionLogonUtc?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["ended"] = SessionEnded,
        };
    });

    // ------------------------------------------------------------------ changes
    /// <summary>Null when <paramref name="name"/> may be given to a profile (other than <paramref name="exceptId"/>).</summary>
    public NameProblem? CheckName(string? name, string? exceptId = null) =>
        Profile.CheckName(name, _list.Where(e => e.Id != exceptId && !e.Unreadable).Select(e => e.Profile.Name));

    public bool CanAdd => _list.Count < Profile.MaxProfiles;

    /// <summary>Add a profile at the end. <paramref name="id"/> = <see cref="Profile.ClassId"/> for « Classe » (first), or
    /// a free pupil id (an import keeps the file's id when it can). A new pupil is offered on this PC when this PC has a
    /// list (« Profils proposés sur ce poste »): a profile the teacher just made must not be missing from « Qui es-tu ? ».</summary>
    public ProfileEntry Add(string name, Avatar avatar, JsonObject? layer = null, string? id = null)
    {
        if (!CanAdd) throw new InvalidOperationException($"at most {Profile.MaxProfiles} profiles");
        if (CheckName(name) is { } why) throw new ArgumentException($"name refused: {why}");
        if (id == Profile.ClassId && Class != null) throw new InvalidOperationException("« Classe » exists");
        if (id != null && (!Profile.IsValidId(id) || Get(id) != null || Directory.Exists(Path.Combine(_dir, id)) && !InMemory))
            throw new ArgumentException("id not usable: " + id);
        var p = new Profile(id ?? Profile.NewId(TakenIds()), Profile.CleanName(name), avatar, _utcNow(), Json.Clone(layer));
        var e = NewEntry(p);
        if (p.IsClass) _list.Insert(0, e); else _list.Add(e);
        if (!p.IsClass && Policy.OfferedOn(MachineName) is { } offered) Policy = Policy.WithOffered(MachineName, offered.Append(p.Id));
        SaveIndex();
        _log?.Invoke($"profiles: added {p.Id}");
        return e;
    }

    /// <summary>Ids in use: the list and the folders on disk (a folder left behind by a failed delete is not reused).</summary>
    public ICollection<string> TakenIds()
    {
        var taken = new HashSet<string>(_list.Select(e => e.Id), StringComparer.Ordinal);
        if (!InMemory && Directory.Exists(_dir))
            foreach (var d in Directory.GetDirectories(_dir)) taken.Add(Path.GetFileName(d));
        return taken;
    }

    /// <summary>
    /// An unreadable profile (« (illisible) », its file kept read-only) is overwritten with <paramref name="p"/> (same id,
    /// same place, a new writable file): « Remplacer la classe de ce poste » repairs a corrupt « Classe » this way. The
    /// caller made a backup first (the zip holds the bad file).
    /// </summary>
    public void Repair(Profile p)
    {
        var old = Get(p.Id) ?? throw new ArgumentException("unknown profile " + p.Id);
        if (!old.Unreadable) throw new InvalidOperationException("profile " + p.Id + " is readable");
        if (InMemory) throw new InvalidOperationException("in-memory session");
        int i = _list.IndexOf(old);
        _list[i] = NewEntry(p);
        if (ActiveId == p.Id) SaveIndex();
        _log?.Invoke($"profiles: unreadable {p.Id} overwritten");
    }

    /// <summary>Import « Remplacer » (plan 09B): the profile keeps its id (and its place, and stays active if it is), the
    /// file's name, picture and settings replace its own.</summary>
    public void Replace(string id, string name, Avatar avatar, JsonObject layer)
    {
        var e = Writable(id);
        if (CheckName(name, id) is { } why) throw new ArgumentException($"name refused: {why}");
        e.Profile = e.Profile with { Name = Profile.CleanName(name), Avatar = avatar, Layer = Json.Clone(layer) };
        SaveProfile(e);
        _log?.Invoke($"profiles: replaced {id}");
    }

    public void Rename(string id, string name)
    {
        var e = Writable(id);
        if (CheckName(name, id) is { } why) throw new ArgumentException($"name refused: {why}");
        e.Profile = e.Profile with { Name = Profile.CleanName(name) };
        SaveProfile(e);
        _log?.Invoke($"profiles: renamed {id}");
    }

    public void SetAvatar(string id, Avatar avatar)
    {
        var e = Writable(id);
        e.Profile = e.Profile with { Avatar = avatar };
        SaveProfile(e);
    }

    /// <summary>Replace a profile's settings layer (its differences from what it inherits).</summary>
    public void SetLayer(string id, JsonObject layer)
    {
        var e = Writable(id);
        e.Profile = e.Profile with { Layer = Json.Clone(layer) };
        SaveProfile(e);
    }

    /// <summary>« Dupliquer »: an independent copy (same picture, same settings layer), after the original.</summary>
    public ProfileEntry Duplicate(string id, string name)
    {
        var src = Writable(id);
        if (!CanAdd) throw new InvalidOperationException($"at most {Profile.MaxProfiles} profiles");
        if (CheckName(name) is { } why) throw new ArgumentException($"name refused: {why}");
        var p = new Profile(Profile.NewId(TakenIds()), Profile.CleanName(name), src.Profile.Avatar, _utcNow(),
                            Json.Clone(src.Profile.Layer));
        var e = NewEntry(p);
        _list.Insert(_list.IndexOf(src) + 1, e);
        if (Policy.OfferedOn(MachineName) is { } offered) Policy = Policy.WithOffered(MachineName, offered.Append(p.Id));
        SaveIndex();
        _log?.Invoke($"profiles: {id} duplicated as {p.Id}");
        return e;
    }

    /// <summary>Delete a profile (never « Classe », never the active one), after a backup of the folder.</summary>
    public void Delete(string id)
    {
        var e = Get(id) ?? throw new ArgumentException("unknown profile " + id);
        if (e.IsClass) throw new InvalidOperationException("« Classe » cannot be deleted");
        if (id == ActiveId) throw new InvalidOperationException("the active profile cannot be deleted");
        Backup("suppression");
        e.File.Flush();
        _list.Remove(e);
        SaveIndex();
        _index.Flush();
        if (InMemory) return;
        try { Directory.Delete(Path.Combine(_dir, id), recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"profiles: folder of {id} not deleted ({ex.GetType().Name}); left out of the index");
        }
        _log?.Invoke($"profiles: deleted {id}");
    }

    /// <summary>Move a pupil up (−1) or down (+1); « Classe » stays first.</summary>
    public bool Move(string id, int delta)
    {
        var e = Get(id);
        if (e is null || e.IsClass) return false;
        int i = _list.IndexOf(e), j = i + delta;
        int min = Class != null ? 1 : 0;
        if (j < min || j >= _list.Count) return false;
        _list.RemoveAt(i);
        _list.Insert(j, e);
        SaveIndex();
        return true;
    }

    public void SetActive(string id)
    {
        if (Get(id) is not { Unreadable: false }) throw new ArgumentException("not a usable profile: " + id);
        if (ActiveId == id) return;
        ActiveId = id;
        SaveIndex();
    }

    public void SetPolicy(ProfilePolicy p)
    {
        Policy = p;
        SaveIndex();
    }

    public void SetSession(DateTime? logonUtc, bool ended)
    {
        SessionLogonUtc = logonUtc;
        SessionEnded = ended;
        SaveIndex();
    }

    private ProfileEntry Writable(string id)
    {
        var e = Get(id) ?? throw new ArgumentException("unknown profile " + id);
        if (e.Unreadable) throw new InvalidOperationException("unreadable profile " + id);
        return e;
    }

    /// <summary>Wait for every pending write (exit, backups, tests).</summary>
    public void Flush()
    {
        _index.Flush();
        foreach (var e in _list) e.File.Flush();
    }

    // ------------------------------------------------------------------ backups
    /// <summary>Runs before every backup (plan 10A: the word files' pending writes are flushed into the zip).</summary>
    public Action? BeforeBackup { get; set; }

    /// <summary>A backup can be made (a backup folder is set and the session is not in memory).</summary>
    public bool CanBackup => _backupDir != null && !InMemory;

    /// <summary>Zip <c>profiles\</c> into the backup folder (<c>profils-&lt;stamp&gt;-&lt;reason&gt;.zip</c>), keep the 5 newest
    /// of that reason (daily, delete and import backups do not push each other out).
    /// Returns the zip path, or null (no backup folder, nothing to save, or a failure — logged, never thrown).</summary>
    public string? Backup(string reason)
    {
        if (_backupDir is null || InMemory || !Directory.Exists(_dir)) return null;
        try
        {
            Flush();
            BeforeBackup?.Invoke();
            Directory.CreateDirectory(_backupDir);
            var name = $"profils-{_utcNow().ToLocalTime():yyyyMMdd-HHmmss}-{reason}.zip";
            var zip = Path.Combine(_backupDir, name);
            if (File.Exists(zip)) File.Delete(zip);
            var tmp = zip + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
                foreach (var f in Directory.GetFiles(_dir, "*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;   // a write in progress
                    var rel = Path.GetRelativePath(_dir, f).Replace('\\', '/');
                    using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = z.CreateEntry(rel, CompressionLevel.Fastest).Open();
                    src.CopyTo(dst);
                }
            File.Move(tmp, zip, overwrite: true);
            foreach (var old in Directory.GetFiles(_backupDir, $"profils-*-{reason}.zip").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(KeepBackups))
                try { File.Delete(old); } catch (IOException) { }
            _log?.Invoke($"profiles: backup ({reason}) -> {name}");
            return zip;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"profiles: backup ({reason}) failed ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>The daily backup: when the newest one is older than a day (or there is none).</summary>
    public bool BackupIfDue()
    {
        if (_backupDir is null || InMemory) return false;
        var newest = Directory.Exists(_backupDir)
            ? Directory.GetFiles(_backupDir, "profils-*.zip").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max()
            : DateTime.MinValue;
        if (_utcNow() - newest < TimeSpan.FromDays(1)) return false;
        return Backup("jour") != null;
    }
}
