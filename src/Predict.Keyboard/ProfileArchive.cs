using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>A <c>*.pepite</c> file that cannot be used; <see cref="Exception.Message"/> is the French sentence shown to
/// the teacher (from <see cref="Strings"/>).</summary>
internal sealed class ArchiveException : Exception
{
    public ArchiveException(string message) : base(message) { }
}

/// <summary>One profile read from a file. <paramref name="Problem"/>: null = it can be imported, else the French reason
/// (a bad or repeated first name, settings that are not an object, too many words). <paramref name="Words"/>: its word
/// lists (version 2, « Inclure les listes de mots »), null = the file has none for it.</summary>
internal sealed record ArchiveProfile(string? Id, string Name, Avatar Avatar, JsonObject Layer, string? Problem = null, WordBook? Words = null);

/// <summary>A validated <c>*.pepite</c> file: its « Classe » (null = none in the file: an empty layer), its pupils in file
/// order, and the teacher code's hashes only when the file was exported with « Inclure le code enseignant ».
/// <paramref name="PinProblem"/>: the file has a teacher code that cannot be used (unsafe hash parameters) — the French
/// reason shown instead of the « Utiliser le code enseignant du fichier » line; <see cref="Pin"/> is then null.</summary>
/// <param name="Images">plan 10C, a version 3 zip: the pictures of the names lists, by « &lt;profile id or classe&gt;/&lt;file&gt; »
/// (each checked: a stored thumbnail name that matches its content); null = a JSON file.</param>
internal sealed record ArchiveContent(int Version, string? App, string? Created, ArchiveProfile? Class,
                                      IReadOnlyList<ArchiveProfile> Pupils, PinHash? Pin, PinHash? Recovery, string? PinProblem = null,
                                      IReadOnlyDictionary<string, byte[]>? Images = null)
{
    public bool HasPin => Pin != null;
}

internal enum ImportAction { Create, Replace, KeepBoth, Skip }

/// <summary>What to do with a file profile whose first name already exists here (D29: Remplacer / Garder les deux / Ignorer).</summary>
internal enum ConflictChoice { Replace, KeepBoth, Skip }

/// <param name="Selected">the row's checkbox.</param>
internal readonly record struct ImportChoice(bool Selected, ConflictChoice Conflict);

/// <param name="ReplaceClass">« Remplacer la classe de ce poste » (unticked by default).</param>
/// <param name="ApplyPin">« Utiliser le code enseignant du fichier » (unticked by default, only when the file has one).</param>
/// <param name="IsVisible">whether a saved position (physical px rectangle; width / height 0 for the bar's point) lies on
/// a screen of this PC; null = keep every position.</param>
/// <param name="WordsOf">the words of a profile of this PC (plan 10A: the word rebase); null = words are not imported.</param>
/// <param name="Now">the day written on rebased words.</param>
internal sealed record ImportOptions(bool ReplaceClass = false, bool ApplyPin = false, Func<int, int, int, int, bool>? IsVisible = null,
                                     Func<string, WordBook>? WordsOf = null, DateTime Now = default);

/// <param name="Index">the profile's row in <see cref="ArchiveContent.Pupils"/>.</param>
/// <param name="ExistingId">the profile of this PC with the same first name (a conflict), if any.</param>
/// <param name="TargetId">the profile written: the existing one (Replace) or the new id (Create / KeepBoth).</param>
/// <param name="Layer">the settings difference over the target « Classe » (rebased, positions off-screen cleared).</param>
/// <param name="Words">its words over the target « Classe » (<see cref="WordLayering.Rebase"/>), null = left as they are.</param>
internal sealed record PlannedProfile(int Index, ArchiveProfile Source, ImportAction Action, string? ExistingId, string? TargetId,
                                      string Name, JsonObject Layer, WordBook? Words = null);

/// <summary>What <see cref="ProfileArchive.Apply"/> will do; <see cref="Excess"/> &gt; 0 = more than 40 profiles: refused.</summary>
/// <param name="ClassWords">with « Remplacer la classe de ce poste »: the file's class words (null = « Classe » keeps its own).</param>
internal sealed record ImportPlan(IReadOnlyList<PlannedProfile> Items, bool ReplaceClass, JsonObject? ClassLayer, bool ApplyPin,
                                  PinHash? Pin, PinHash? Recovery, int Excess, WordBook? ClassWords = null,
                                  IReadOnlyDictionary<string, byte[]>? Images = null)
{
    public int Creates => Items.Count(i => i.Action == ImportAction.Create);
    public int Replaces => Items.Count(i => i.Action == ImportAction.Replace);
    public int KeepBoths => Items.Count(i => i.Action == ImportAction.KeepBoth);
    public int Skips => Items.Count(i => i.Action == ImportAction.Skip);
    public bool HasWork => Creates + Replaces + KeepBoths > 0 || ReplaceClass || ApplyPin;
    public bool CanApply => Excess == 0 && HasWork;
}

internal readonly record struct ImportResult(int Created, int Replaced, int KeptBoth, int Skipped, bool ClassReplaced, bool PinApplied,
                                             bool ActiveChanged, string? Backup);

/// <summary>
/// Export / import of profiles (plan 09B step 16, D29): one human-readable JSON file <c>*.pepite</c> =
/// <c>{ format: "pepite-profils", version: 2, app, created, remarque, classe {name, avatar, settings, mots?}, profils [{id, name,
/// avatar {picto, color}, settings, mots?}], enseignant? {pin, recovery} }</c>. Version 2 (plan 10A, Q14): <c>mots</c> =
/// the profile's word lists, « Mes fautes » and hidden words (the <c>mots.json</c> content), written when « Inclure les
/// listes de mots » is ticked (the default); version 1 files (no words) are still read, and 0.7 refuses a version 2 file
/// with its « Mettez Pépite à jour ». Version 3 (plan 10C, Q20): with « Inclure les images » and pictures in the names
/// lists, a zip (≤ 50 MB) of <c>pepite.json</c> + <c>images/&lt;id&gt;/&lt;file&gt;</c>; 0.8.1 refuses it as « not a Pépite file » (it reads JSON only). Words are rebased like the settings (<see cref="WordLayering.Rebase"/>). « Classe » is always written (pupils store only their
/// differences from it); the teacher code's <b>hashes</b> only when « Inclure le code enseignant » is ticked — never the
/// code itself. Reading is validated (≤ 20 MB, the format, a newer version refused with a French message, every
/// profile re-validated, unknown keys kept in the settings, a stray <c>lock</c> part dropped). <see cref="Plan"/> is pure:
/// per row create / replace / keep both (« Léa (2) », a new id) / skip, each pupil rebased from the file's « Classe »
/// onto the target one (<see cref="SettingsLayering.Rebase"/>: the effective settings stay the same), positions not on a
/// screen of this PC cleared, the 40-profile limit. <see cref="Apply"/> makes a backup first (no backup → nothing
/// imported), never in an in-memory session, and applies the PIN only through its own line. Logs carry counts and
/// ids, never a first name, a path (it may hold a name) or a hash.
/// </summary>
internal static class ProfileArchive
{
    public const string Format = "pepite-profils";
    public const int Version = 3;
    /// <summary>Plan 10C (owner Q20): a version 3 file is a zip (<c>pepite.json</c> + <c>images/&lt;id&gt;/…</c>), written only
    /// with « Inclure les images » when a names list has pictures; otherwise the file stays version 2 (or 1) JSON.</summary>
    public const int ZipVersion = 3, WordsVersion = 2;
    public const long MaxBytes = 20L * 1024 * 1024, MaxZipBytes = 50L * 1024 * 1024;
    public const string ZipIndex = "pepite.json", ClassImagesKey = "classe";
    public const string Extension = ".pepite";
    /// <summary>At most this many entries in <c>profils</c> (a PC holds 40 profiles; a bigger file is refused before
    /// any window is built).</summary>
    public const int MaxFileProfiles = 200;

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        // a file the teacher may open in Notepad: « Léa », not « Léa » (a data file, never put into HTML)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------ export
    /// <summary>The file's JSON: « Classe » + the readable pupils of <paramref name="pupilIds"/> (store order); the
    /// teacher code's hashes only when <paramref name="includePin"/>.</summary>
    public static JsonObject Export(ProfileStore store, IEnumerable<string> pupilIds, bool includePin, TeacherLock? teacherLock,
                                    string app, DateTime utcNow, Func<string, WordBook>? wordsOf = null)
    {
        JsonObject Words(string id) => Json.Without(wordsOf!(id).ToJson(), new[] { "schema" });
        var wanted = new HashSet<string>(pupilIds, StringComparer.Ordinal);
        var root = new JsonObject
        {
            ["format"] = Format,
            // without word lists the file is a version 1 file: 0.7 can still import it
            ["version"] = wordsOf != null ? WordsVersion : 1,
            ["app"] = app,
            ["created"] = utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["remarque"] = Strings.ArchiveRemark,
        };
        var cls = store.Class is { Unreadable: false } c ? c.Profile : null;
        root["classe"] = new JsonObject
        {
            ["name"] = cls?.Name ?? Strings.ClassProfileName,
            ["avatar"] = (cls?.Avatar ?? new Avatar(null, ProfileColor.Vert)).ToJson(),
            ["settings"] = CleanLayer(cls?.Layer),
        };
        if (wordsOf != null) ((JsonObject)root["classe"]!)["mots"] = Words(Profile.ClassId);
        var list = new JsonArray();
        foreach (var e in store.Profiles.Where(e => !e.IsClass && !e.Unreadable && wanted.Contains(e.Id)))
        {
            var po = new JsonObject
            {
                ["id"] = e.Id,
                ["name"] = e.Profile.Name,
                ["avatar"] = e.Profile.Avatar.ToJson(),
                ["settings"] = CleanLayer(e.Profile.Layer),
            };
            if (wordsOf != null) po["mots"] = Words(e.Id);
            list.Add(po);
        }
        root["profils"] = list;
        if (includePin && teacherLock?.Hashes() is { Pin: { } pin } h)
        {
            var t = new JsonObject { ["pin"] = TeacherLock.ToJson(pin) };
            if (h.Recovery is { } r) t["recovery"] = TeacherLock.ToJson(r);
            root["enseignant"] = t;
        }
        return root;
    }

    public static string ToText(JsonObject root) => root.ToJsonString(Pretty) + Environment.NewLine;

    /// <summary>Plan 10C: write the pictures of an imported profile's names lists into its folder (already there: kept).</summary>
    private static void CopyImages(IReadOnlyDictionary<string, byte[]>? images, string? fileKey, string targetId, WordBook words, WordListService? svc)
    {
        if (images is null || images.Count == 0 || fileKey is null || svc?.ImagesDir(targetId) is not { } dir) return;
        foreach (var f in words.ImageFiles)
        {
            if (!images.TryGetValue(fileKey + "/" + f, out var b)) continue;
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, f);
            if (!File.Exists(dest)) File.WriteAllBytes(dest, b);
        }
    }

    /// <summary>Export into <paramref name="path"/> (atomic write). Returns the number of pupils written.</summary>
    /// <param name="wordsOf">« Inclure les listes de mots »: the words of a profile; null = no words in the file.</param>
    /// <param name="imagesOf">plan 10C, « Inclure les images »: the pictures folder of a profile (null = no pictures in the
    /// file). With at least one picture of a names list the file is a version 3 zip (≤ 50 MB, else refused).</param>
    public static int Write(string path, ProfileStore store, IEnumerable<string> pupilIds, bool includePin, TeacherLock? teacherLock,
                            string app, Action<string>? log, Func<string, WordBook>? wordsOf = null, Func<string, string?>? imagesOf = null)
    {
        var sw = Stopwatch.StartNew();
        store.Flush();
        var root = Export(store, pupilIds, includePin, teacherLock, app, DateTime.UtcNow, wordsOf);
        int n = ((JsonArray)root["profils"]!).Count;
        var images = wordsOf != null && imagesOf != null ? ImagesToExport(root, wordsOf, imagesOf) : new List<(string Key, string File)>();
        long size;
        if (images.Count == 0)
        {
            var text = ToText(root);
            JsonFileStore.WriteAtomic(path, text);
            size = text.Length;
        }
        else
        {
            root["version"] = ZipVersion;
            size = WriteZip(path, ToText(root), images);
        }
        log?.Invoke(FormattableString.Invariant(
            $"profiles: export of {n} pupils + classe, teacher code {(root.ContainsKey("enseignant") ? "included" : "not included")}, words {(wordsOf != null ? "included" : "not included")}, {images.Count} pictures, {size / 1024.0:0.#} KB in {sw.Elapsed.TotalMilliseconds:0} ms"));
        return n;
    }

    /// <summary>The pictures the exported names lists refer to that exist: (key « id/file » or « classe/file », path).</summary>
    private static List<(string Key, string File)> ImagesToExport(JsonObject root, Func<string, WordBook> wordsOf, Func<string, string?> imagesOf)
    {
        var o = new List<(string, string)>();
        var ids = new List<(string Key, string Id)> { (ClassImagesKey, Profile.ClassId) };
        foreach (var p in (JsonArray)root["profils"]!) if (p?["id"] is JsonValue v && v.TryGetValue<string>(out var id)) ids.Add((id, id));
        foreach (var (key, id) in ids)
        {
            if (imagesOf(id) is not { } dir) continue;
            foreach (var f in wordsOf(id).ImageFiles)
            {
                var path = Path.Combine(dir, f);
                if (NamesImport.IsStoredImageName(f) && File.Exists(path)) o.Add((key + "/" + f, path));
            }
        }
        return o;
    }

    private static long WriteZip(string path, string json, List<(string Key, string File)> images)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry(ZipIndex).Open(), new System.Text.UTF8Encoding(false))) w.Write(json);
                long total = json.Length;
                foreach (var (key, file) in images)
                {
                    var bytes = File.ReadAllBytes(file);
                    total += bytes.Length;
                    if (total > MaxZipBytes) throw new ArchiveException(Strings.ExportImagesTooBig);
                    using var s = z.CreateEntry("images/" + key, System.IO.Compression.CompressionLevel.NoCompression).Open();
                    s.Write(bytes, 0, bytes.Length);
                }
            }
            File.Move(tmp, path, true);
            return new FileInfo(path).Length;
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
    }

    /// <summary>A layer as it goes into or out of a file: a copy without the old settings-lock part (a PIN hash lives in
    /// teacher.json and travels only as <c>enseignant</c>).</summary>
    private static JsonObject CleanLayer(JsonObject? layer) => Json.Without(Json.Clone(layer), new[] { "lock" });

    // ------------------------------------------------------------------ read
    public static ArchiveContent Read(string path)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) throw new ArchiveException(Strings.ImportNotFound); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new ArchiveException(Strings.ImportUnreadable); }
        if (fi.Length > MaxZipBytes) throw new ArchiveException(Strings.ImportTooBig);
        bool zip;
        try { using var f = File.OpenRead(path); zip = f.ReadByte() == 'P' && f.ReadByte() == 'K'; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ArchiveException(Strings.ImportUnreadable); }
        if (zip)   // plan 10C: a version 3 file
        {
            try { return ReadZip(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw new ArchiveException(Strings.ImportUnreadable); }
        }
        if (fi.Length > MaxBytes) throw new ArchiveException(Strings.ImportTooBig);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ArchiveException(Strings.ImportUnreadable); }
        return Parse(text);
    }

    /// <summary>A version 3 file: <c>pepite.json</c> (≤ 20 MB) and the pictures, each a bare stored thumbnail name under
    /// « images/&lt;profile id or classe&gt;/ », ≤ 5 MB, whose name matches its content's hash (anything else is ignored).</summary>
    private static ArchiveContent ReadZip(string path)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(path);
        var index = z.GetEntry(ZipIndex) ?? throw new ArchiveException(Strings.ImportNotPepite);
        if (index.Length > MaxBytes) throw new ArchiveException(Strings.ImportTooBig);
        string text;
        using (var r = new StreamReader(index.Open(), System.Text.Encoding.UTF8)) text = r.ReadToEnd();
        var content = Parse(text);
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var e in z.Entries)
        {
            var parts = e.FullName.Split('/');
            if (parts.Length != 3 || parts[0] != "images" || !(parts[1] == ClassImagesKey || Profile.IsValidId(parts[1]))) continue;
            if (!NamesImport.IsStoredImageName(parts[2]) || e.Length > NamesImport.MaxImageBytes) continue;
            if ((total += e.Length) > MaxZipBytes) break;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var b = ms.ToArray();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant()[..16] + ".png";
            if (hash == parts[2] && NamesImport.CheckImage(b) is null) images[parts[1] + "/" + parts[2]] = b;
        }
        return content with { Images = images };
    }

    public static ArchiveContent Parse(string text)
    {
        if (text.Length > MaxBytes) throw new ArchiveException(Strings.ImportTooBig);
        JsonObject root;
        try { root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 64 }) as JsonObject ?? throw new ArchiveException(Strings.ImportNotPepite); }
        catch (JsonException) { throw new ArchiveException(Strings.ImportNotPepite); }
        if (Str(root["format"]) != Format) throw new ArchiveException(Strings.ImportNotPepite);
        if (root["version"] is not JsonValue vv || !vv.TryGetValue<int>(out var version) || version < 1)
            throw new ArchiveException(Strings.ImportNotPepite);
        if (version > Version) throw new ArchiveException(Strings.ImportNewer);
        if (root["profils"] is not JsonArray arr) throw new ArchiveException(Strings.ImportNotPepite);
        if (arr.Count > MaxFileProfiles) throw new ArchiveException(string.Format(Strings.ImportTooManyInFile, MaxFileProfiles));

        ArchiveProfile? cls = null;
        if (root["classe"] is JsonObject co)
        {
            var (layer, bad) = LayerOf(co);
            var (words, wbad) = WordsOf(co, true);
            cls = new ArchiveProfile(Profile.ClassId, Strings.ClassProfileName, Avatar.FromJson(co["avatar"]), layer, bad, words);
            if (bad != null) throw new ArchiveException(Strings.ImportBadClass);
            if (wbad != null) throw new ArchiveException(Strings.ImportClassWordsBad);
        }
        var pupils = new List<ArchiveProfile>();
        var names = new List<string>();
        foreach (var node in arr)
        {
            if (node is not JsonObject o) { pupils.Add(new ArchiveProfile(null, Strings.ProfileUnreadable, Avatar.Default, new JsonObject(), Strings.ImportRowUnreadable)); continue; }
            var raw = Str(o["name"]);
            var name = Profile.CleanName(raw);
            string? problem = Profile.CheckName(raw, names) is { } np ? Strings.NameProblemText(np) : null;
            if (name.Length == 0) name = Strings.ProfileUnreadable;
            var (layer, bad) = LayerOf(o);
            problem ??= bad;
            var (words, wbad) = WordsOf(o, false);
            problem ??= wbad;
            var id = Str(o["id"]) is { } sid && Profile.IsValidId(sid) && sid != Profile.ClassId ? sid : null;
            pupils.Add(new ArchiveProfile(id, name, Avatar.FromJson(o["avatar"]), layer, problem, words));
            if (problem is null) names.Add(name);
        }
        // the teacher code: both hashes with sane parameters (TeacherLock.IsImportable), else the PIN part is refused
        PinHash? pin = null, recovery = null;
        string? pinProblem = null;
        if (root["enseignant"] is JsonObject t)
        {
            var p = TeacherLock.ParseHash(t["pin"]);
            var r = t["recovery"] is null ? null : TeacherLock.ParseHash(t["recovery"]);
            bool recoveryBad = t["recovery"] is not null && (r is null || !TeacherLock.IsImportable(r));
            if (p is null || !TeacherLock.IsImportable(p) || recoveryBad) pinProblem = Strings.ImportPinRefused;
            else { pin = p; recovery = r; }
        }
        return new ArchiveContent(version, Str(root["app"]), Str(root["created"]), cls, pupils, pin, recovery, pinProblem);
    }

    private static (JsonObject Layer, string? Problem) LayerOf(JsonObject o)
    {
        var s = o["settings"];
        if (s is null) return (new JsonObject(), null);
        if (s is not JsonObject so) return (new JsonObject(), Strings.ImportRowBadSettings);
        var layer = CleanLayer(so);
        VoiceId.MigrateJson(layer);
        return (layer, null);
    }

    /// <summary>The <c>mots</c> part (version 2): null when absent; more words than the limits allow → refused.</summary>
    private static (WordBook? Words, string? Problem) WordsOf(JsonObject o, bool isClass)
    {
        var m = o["mots"];
        if (m is null) return (null, null);
        if (m is not JsonObject mo) return (null, Strings.ImportRowBadWords);
        var b = WordBook.FromJson(mo, isClass, out var over);
        return over ? (null, Strings.ImportRowBadWords) : (b, null);
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ------------------------------------------------------------------ plan (pure)
    /// <summary>The default row choices: profiles that can be imported are ticked; a conflict keeps both (nothing lost).</summary>
    public static ImportChoice DefaultChoice(ArchiveProfile p) => new(p.Problem is null, ConflictChoice.KeepBoth);

    /// <summary>The profile of <paramref name="store"/> with the same first name (ignoring case), if any.</summary>
    public static ProfileEntry? ConflictOf(ProfileStore store, ArchiveProfile p) =>
        store.Profiles.FirstOrDefault(e => !e.Unreadable && Profile.CheckName(p.Name, new[] { e.Profile.Name }) == NameProblem.Duplicate);

    /// <summary>
    /// What an import would do, without changing anything. <paramref name="choices"/>: per row of
    /// <see cref="ArchiveContent.Pupils"/> (missing = <see cref="DefaultChoice"/>). A row with a problem is skipped; a
    /// conflict with « Classe » itself cannot replace it (kept both instead). « Garder les deux » names the copy
    /// « Léa (2) » (then (3)…), shortened to 30 characters; new ids keep the file's id when it is free here.
    /// </summary>
    public static ImportPlan Plan(ArchiveContent content, ProfileStore store, IReadOnlyDictionary<int, ImportChoice>? choices,
                                  ImportOptions options)
    {
        var targetClass = options.ReplaceClass ? content.Class?.Layer ?? new JsonObject()
                                               : store.Class is { Unreadable: false } c ? c.Profile.Layer : null;
        var sourceClass = content.Class?.Layer;
        // plan 10A: the words, rebased from the file's « Classe » onto the target one
        var fileClassWords = content.Class?.Words;
        WordBook? targetClassWords = options.WordsOf is null ? null
            : options.ReplaceClass && fileClassWords != null ? fileClassWords
            : store.Class is { Unreadable: false } ? options.WordsOf(Profile.ClassId) : null;
        var now = options.Now == default ? DateTime.Now : options.Now;
        var names = store.Profiles.Where(e => !e.Unreadable).Select(e => e.Profile.Name).ToList();
        var taken = store.TakenIds();
        var items = new List<PlannedProfile>();
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < content.Pupils.Count; i++)
        {
            var p = content.Pupils[i];
            var ch = choices != null && choices.TryGetValue(i, out var cc) ? cc : DefaultChoice(p);
            var existing = ConflictOf(store, p);
            if (p.Problem != null || !ch.Selected)
            {
                items.Add(new PlannedProfile(i, p, ImportAction.Skip, existing?.Id, null, p.Name, new JsonObject()));
                continue;
            }
            var layer = FixPositions(SettingsLayering.Rebase(sourceClass, p.Layer, targetClass), options.IsVisible);
            var action = existing is null ? ImportAction.Create : ch.Conflict switch
            {
                ConflictChoice.Replace when !existing.IsClass && !replaced.Contains(existing.Id) => ImportAction.Replace,
                ConflictChoice.Skip => ImportAction.Skip,
                _ => ImportAction.KeepBoth,
            };
            string name = p.Name, target;
            switch (action)
            {
                case ImportAction.Skip:
                    items.Add(new PlannedProfile(i, p, action, existing?.Id, null, p.Name, new JsonObject()));
                    continue;
                case ImportAction.Replace:
                    target = existing!.Id;
                    replaced.Add(target);
                    break;
                default:
                    if (action == ImportAction.KeepBoth || Profile.CheckName(name, names) != null) name = FreeName(p.Name, names);
                    target = p.Id is { } sid && !taken.Contains(sid) ? sid : Profile.NewId(taken);
                    taken.Add(target);
                    names.Add(name);
                    break;
            }
            WordBook? words = options.WordsOf is null || (p.Words is null && fileClassWords is null) ? null
                : WordLayering.Rebase(fileClassWords, p.Words ?? WordBook.Empty, targetClassWords, now);
            items.Add(new PlannedProfile(i, p, action, existing?.Id, target, name, layer, words));
        }
        int after = store.Count + items.Count(x => x.Action is ImportAction.Create or ImportAction.KeepBoth) +
                    (options.ReplaceClass && store.Class is null ? 1 : 0);
        bool pin = options.ApplyPin && content.Pin != null;
        return new ImportPlan(items, options.ReplaceClass, options.ReplaceClass ? Json.Clone(content.Class?.Layer) : null, pin,
                              pin ? content.Pin : null, pin ? content.Recovery : null, Math.Max(0, after - Profile.MaxProfiles),
                              options.ReplaceClass && options.WordsOf != null ? fileClassWords : null,
                              options.WordsOf != null ? content.Images : null);
    }

    /// <summary>« Léa (2) », « Léa (3) »… not among <paramref name="names"/> (ignoring case), at most 30 characters.</summary>
    public static string FreeName(string name, IReadOnlyCollection<string> names)
    {
        for (int n = 2; ; n++)
        {
            var suffix = $" ({n})";
            var si = new StringInfo(name);
            var head = si.LengthInTextElements + suffix.Length > Profile.MaxName
                ? si.SubstringByTextElements(0, Math.Max(1, Profile.MaxName - suffix.Length)).TrimEnd() : name;
            var candidate = head + suffix;
            if (Profile.CheckName(candidate, names) is null) return candidate;
        }
    }

    /// <summary>Clear the free bar position and the reading window when they are not on a screen of this PC.</summary>
    public static JsonObject FixPositions(JsonObject layer, Func<int, int, int, int, bool>? isVisible)
    {
        if (isVisible is null) return layer;
        var r = layer;
        if (r["bar"] is JsonObject bar && bar["free"] is JsonObject f && !(Int(f["x"]) is int x && Int(f["y"]) is int y && isVisible(x, y, 0, 0)))
            r = Json.Without(r, new[] { "bar.free" });
        if (r["reading"] is JsonObject rd && rd["window"] is JsonObject w &&
            !(Int(w["x"]) is int wx && Int(w["y"]) is int wy && Int(w["w"]) is int ww && Int(w["h"]) is int wh && isVisible(wx, wy, ww, wh)))
            r = Json.Without(r, new[] { "reading.window" });
        return r;
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    // ------------------------------------------------------------------ apply
    /// <summary>
    /// Apply <paramref name="plan"/> (UI thread): a backup of <c>profiles\</c> first — none possible → nothing imported
    /// (<see cref="ArchiveException"/>) —; « Classe » replaced when chosen; each row created / replaced; the PIN hashes only
    /// when <see cref="ImportPlan.ApplyPin"/>. Replacing the active profile, or « Classe » under the active pupil, reloads
    /// the running settings (<see cref="ProfileService.ImportApplied"/> → <c>Changed</c>).
    /// </summary>
    public static ImportResult Apply(ImportPlan plan, ProfileService svc, TeacherLock? teacherLock, Action<string>? log)
    {
        var store = svc.Store;
        if (store.InMemory) throw new ArchiveException(Strings.CannotSaveHint);
        if (!plan.CanApply) throw new ArchiveException(plan.Excess > 0 ? string.Format(Strings.ImportTooMany, Profile.MaxProfiles, plan.Excess)
                                                                      : Strings.ImportNothing);
        var sw = Stopwatch.StartNew();
        var zip = store.Backup("import");
        if (zip is null && store.CanBackup) throw new ArchiveException(Strings.ImportBackupFailed);
        bool active = false;
        if (plan.ReplaceClass)
        {
            var layer = plan.ClassLayer ?? new JsonObject();
            if (store.Class is { Unreadable: false }) store.SetLayer(Profile.ClassId, layer);
            // an unreadable « Classe » is overwritten (repaired) with the file's; the backup above holds the bad file
            else if (store.Class is { Unreadable: true })
                store.Repair(new Profile(Profile.ClassId, Strings.ClassProfileName, new Avatar(null, ProfileColor.Vert), DateTime.UtcNow, Json.Clone(layer)));
            else if (store.Class is null)
            {
                var name = store.CheckName(Strings.ClassProfileName) == NameProblem.Duplicate ? FreeName(Strings.ClassProfileName, store.Profiles.Select(e => e.Profile.Name).ToList()) : Strings.ClassProfileName;
                store.Add(name, new Avatar(null, ProfileColor.Vert), layer, Profile.ClassId);
            }
            active = true;   // « Classe » itself, or the layer every pupil inherits
            if (plan.ClassWords != null) svc.Words?.Store.Set(Profile.ClassId, plan.ClassWords);
        }
        foreach (var it in plan.Items)
        {
            switch (it.Action)
            {
                case ImportAction.Replace:
                    store.Replace(it.TargetId!, it.Name, it.Source.Avatar, it.Layer);
                    if (it.TargetId == store.ActiveId) active = true;
                    break;
                case ImportAction.Create:
                case ImportAction.KeepBoth:
                    store.Add(it.Name, it.Source.Avatar, it.Layer, it.TargetId);
                    break;
            }
            // « Remplacer » replaces the words, « Garder les deux » / a new profile copies them (plan 10A)
            if (it.Words != null && it.TargetId != null && it.Action != ImportAction.Skip)
            {
                svc.Words?.Store.Set(it.TargetId, it.Words);
                CopyImages(plan.Images, it.Source.Id, it.TargetId, it.Words, svc.Words);   // plan 10C
            }
        }
        if (plan.ReplaceClass && plan.ClassWords != null) CopyImages(plan.Images, ClassImagesKey, Profile.ClassId, plan.ClassWords, svc.Words);
        bool pin = false;
        if (plan.ApplyPin && plan.Pin is { } ph && teacherLock != null)
        {
            teacherLock.SetHashes(ph, plan.Recovery);
            pin = true;
        }
        store.Flush();
        svc.Words?.Store.Flush();
        svc.ImportApplied(active);
        var r = new ImportResult(plan.Creates, plan.Replaces, plan.KeepBoths, plan.Skips, plan.ReplaceClass, pin, active,
                                 zip is null ? null : Path.GetFileName(zip));
        log?.Invoke(FormattableString.Invariant(
            $"profiles: import applied in {sw.Elapsed.TotalMilliseconds:0} ms: created {r.Created}, replaced {r.Replaced}, kept both {r.KeptBoth}, skipped {r.Skipped}, ") +
            $"class {(r.ClassReplaced ? "replaced" : "kept")}, words of {plan.Items.Count(i => i.Words != null && i.TargetId != null && i.Action != ImportAction.Skip) + (plan.ClassWords != null ? 1 : 0)} profiles, teacher code {(pin ? "imported" : "unchanged")}, active {(active ? "reloaded" : "unchanged")} " +
            $"[{string.Join(", ", plan.Items.Where(i => i.TargetId != null).Select(i => $"{i.Action.ToString().ToLowerInvariant()} {i.TargetId}"))}]");
        return r;
    }
}
