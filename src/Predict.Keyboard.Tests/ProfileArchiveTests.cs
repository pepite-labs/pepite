using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 09B step 16: export / import of profiles (*.pepite): round trip, subset, rebase, conflicts, the 40 limit,
/// bad files, the PIN only when ticked, backup first, the in-memory session.</summary>
public class ProfileArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kbarch-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();
    private readonly List<string> _calls = new();

    public ProfileArchiveTests() => TeacherLock.Iterations = 1000;

    public void Dispose()
    {
        TestDirs.Delete(_dir);
    }

    private sealed class Host : IProfileHost
    {
        private readonly List<string> _c;
        public Host(List<string> c) => _c = c;
        public void ResetForSwitch() => _c.Add("reset");
        public void CancelSpeech() => _c.Add("cancel");
        public void CloseProfileWindows() => _c.Add("close");
    }

    private static JsonObject J(string s) => JsonNode.Parse(s)!.AsObject();

    private sealed record Pc(ProfileStore Store, ProfileService Svc, TeacherLock Lock, string Root, string Backups);

    /// <summary>A PC with « Classe » (<paramref name="classLayer"/>) and the given pupils (name, picto, colour, layer).</summary>
    private Pc NewPc(string name, string classLayer, params (string Name, string? Picto, ProfileColor Color, string Layer)[] pupils)
    {
        var root = Path.Combine(_dir, name);
        var backups = Path.Combine(root, "sauvegardes");
        var store = new ProfileStore(root, backups, _log.Add);
        store.Load();
        // the store starts with « Mon profil »: make it the class's first pupil list like a real activation
        store.Add(Strings.ClassProfileName, new Avatar(null, ProfileColor.Vert), J(classLayer), Profile.ClassId);
        store.SetActive(Profile.ClassId);
        store.Delete(store.Profiles.First(e => e.Profile.Name == Strings.DefaultProfileName).Id);
        foreach (var p in pupils) store.Add(p.Name, new Avatar(p.Picto, p.Color), J(p.Layer));
        var settings = ProfileService.CreateSettings(store, MachineLayer.None, _log.Add);
        var svc = new ProfileService(store, settings, new Host(_calls), _log.Add);
        var tf = new JsonFileStore(store.TeacherPath, _log.Add, "profiles", schema: 1);
        tf.Load();
        return new Pc(store, svc, new TeacherLock(tf, null, _log.Add), root, backups);
    }

    /// <summary>A PIN hashed with import-grade parameters (100 000 iterations, the minimum a file may carry), set without
    /// touching the shared <see cref="TeacherLock.Iterations"/> that other test classes lower.</summary>
    private static void StrongPin(TeacherLock l, string pin) =>
        l.SetHashes(TeacherLock.Hash(pin, TeacherLock.MinImportIterations), TeacherLock.Hash("ABCDEFGHJKLM", TeacherLock.MinImportIterations));

    private static string IdOf(ProfileStore s, string name) => s.Profiles.First(e => e.Profile.Name == name).Id;

    private static AppSettings Effective(ProfileStore s, string id) =>
        SettingsLayering.Merge(MachineLayer.None, s.ClassLayerFor(id), s.Get(id)!.Profile.Layer).Effective;

    private string ExportFile(Pc pc, IEnumerable<string> ids, bool pin = false)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ProfileArchive.Extension);
        ProfileArchive.Write(path, pc.Store, ids, pin, pc.Lock, "Pépite test", _log.Add);
        return path;
    }

    private Pc Source() => NewPc("a", @"{""words"":{""font"":""andika""}}",
        ("Léa", "chat", ProfileColor.Orange, @"{""speech"":{""voice"":""piper:siwis""}}"),
        ("Tom", "fusée", ProfileColor.Vert, @"{""words"":{""size"":""XL""},""bar"":{""position"":""free"",""free"":{""x"":5000,""y"":4000}}}"),
        ("Zoé", null, ProfileColor.Bleu, @"{}"));

    [Fact]
    public void Export_Is_Readable_Json_With_Class_Pupils_And_No_Teacher_Code_By_Default()
    {
        var a = Source();
        StrongPin(a.Lock, "480917");
        var path = ExportFile(a, a.Store.Profiles.Select(e => e.Id));
        var text = File.ReadAllText(path);
        Assert.Contains("\"format\": \"pepite-profils\"", text);
        Assert.Contains("\"Léa\"", text);                 // not escaped: readable in Notepad
        Assert.Contains("\"picto\": \"fusée\"", text);
        Assert.DoesNotContain("enseignant", text);
        Assert.DoesNotContain("\"pin\"", text);
        Assert.DoesNotContain("\"hash\"", text);
        var c = ProfileArchive.Read(path);
        Assert.Equal(new[] { "Léa", "Tom", "Zoé" }, c.Pupils.Select(p => p.Name));
        Assert.Equal("andika", (string)c.Class!.Layer["words"]!["font"]!);
        Assert.False(c.HasPin);
        Assert.DoesNotContain(_log, l => l.Contains("Léa") || l.Contains("Tom") || l.Contains(path));
    }

    [Fact]
    public void Export_With_The_Teacher_Code_Holds_Only_Its_Hashes()
    {
        var a = Source();
        StrongPin(a.Lock, "480917");
        var path = ExportFile(a, new[] { IdOf(a.Store, "Tom") }, pin: true);
        var text = File.ReadAllText(path);
        Assert.Contains("\"enseignant\"", text);
        Assert.DoesNotContain("480917", text);
        var c = ProfileArchive.Read(path);
        Assert.True(c.HasPin);
        Assert.NotNull(c.Recovery);
        Assert.True(TeacherLock.Matches("480917", c.Pin!));
        Assert.Single(c.Pupils);                          // a subset: Tom only (+ « Classe », always)
        Assert.NotNull(c.Class);
    }

    [Fact]
    public void Round_Trip_Into_An_Empty_Class_Keeps_Effective_Settings()
    {
        var a = Source();
        var path = ExportFile(a, a.Store.Profiles.Select(e => e.Id));
        var b = NewPc("b", "{}");
        var plan = ProfileArchive.Plan(ProfileArchive.Read(path), b.Store, null, new ImportOptions());
        Assert.Equal(3, plan.Creates);
        ProfileArchive.Apply(plan, b.Svc, b.Lock, _log.Add);
        foreach (var n in new[] { "Léa", "Tom", "Zoé" })
        {
            var ea = Effective(a.Store, IdOf(a.Store, n));
            var eb = Effective(b.Store, IdOf(b.Store, n));
            Assert.Equal(ea.ToJson().ToJsonString(), eb.ToJson().ToJsonString());
        }
        // the class font now lives in each pupil's own layer (b's « Classe » has none)
        Assert.Equal("andika", (string)b.Store.Get(IdOf(b.Store, "Zoé"))!.Profile.Layer["words"]!["font"]!);
        Assert.Equal("fusée", b.Store.Get(IdOf(b.Store, "Tom"))!.Profile.Avatar.Picto);
        Assert.Equal(IdOf(a.Store, "Tom"), IdOf(b.Store, "Tom"));     // a free id is kept
    }

    [Fact]
    public void Rebase_Onto_Another_Class_Drops_What_It_Already_Gives()
    {
        var a = Source();
        var path = ExportFile(a, a.Store.Profiles.Select(e => e.Id));
        var b = NewPc("b", @"{""words"":{""font"":""andika"",""size"":""L""}}");
        var plan = ProfileArchive.Plan(ProfileArchive.Read(path), b.Store, null, new ImportOptions());
        ProfileArchive.Apply(plan, b.Svc, b.Lock, _log.Add);
        var zoe = b.Store.Get(IdOf(b.Store, "Zoé"))!.Profile.Layer;
        Assert.Null(zoe["words"]?["font"]);                          // inherited from b's class
        Assert.Equal("M", (string)zoe["words"]!["size"]!);           // b's class says L: Zoé keeps her M
        Assert.Equal(Effective(a.Store, IdOf(a.Store, "Zoé")).ToJson().ToJsonString(),
                     Effective(b.Store, IdOf(b.Store, "Zoé")).ToJson().ToJsonString());
    }

    [Fact]
    public void Replace_Class_Takes_The_Files_Class_And_Keeps_Pupils_As_They_Were()
    {
        var a = Source();
        var path = ExportFile(a, new[] { IdOf(a.Store, "Tom") });
        var b = NewPc("b", @"{""words"":{""size"":""L""}}");
        var plan = ProfileArchive.Plan(ProfileArchive.Read(path), b.Store, null, new ImportOptions(ReplaceClass: true));
        var r = ProfileArchive.Apply(plan, b.Svc, b.Lock, _log.Add);
        Assert.True(r.ClassReplaced);
        Assert.Equal(@"{""words"":{""font"":""andika""}}", b.Store.Class!.Profile.Layer.ToJsonString());
        Assert.Equal(@"{""size"":""XL""}", b.Store.Get(IdOf(b.Store, "Tom"))!.Profile.Layer["words"]!.ToJsonString());   // no font copied in
    }

    [Fact]
    public void Conflicts_Keep_Both_By_Default_Replace_Or_Skip_On_Request()
    {
        var a = Source();
        var path = ExportFile(a, a.Store.Profiles.Select(e => e.Id));
        var b = NewPc("b", "{}", ("léa", "lion", ProfileColor.Rouge, @"{""words"":{""count"":3}}"), ("Tom", null, ProfileColor.Bleu, "{}"));
        var c = ProfileArchive.Read(path);
        var plan = ProfileArchive.Plan(c, b.Store, null, new ImportOptions());
        Assert.Equal(new[] { ImportAction.KeepBoth, ImportAction.KeepBoth, ImportAction.Create }, plan.Items.Select(i => i.Action));
        Assert.Equal("Léa (2)", plan.Items[0].Name);
        Assert.Equal("Tom (2)", plan.Items[1].Name);
        Assert.NotEqual(IdOf(b.Store, "léa"), plan.Items[0].TargetId);

        var choices = new Dictionary<int, ImportChoice>
        {
            [0] = new(true, ConflictChoice.Replace),
            [1] = new(true, ConflictChoice.Skip),
            [2] = new(false, ConflictChoice.KeepBoth),
        };
        var lea = IdOf(b.Store, "léa");
        b.Svc.Switch(lea, "test");
        var plan2 = ProfileArchive.Plan(c, b.Store, choices, new ImportOptions());
        Assert.Equal(new[] { ImportAction.Replace, ImportAction.Skip, ImportAction.Skip }, plan2.Items.Select(i => i.Action));
        var r = ProfileArchive.Apply(plan2, b.Svc, b.Lock, _log.Add);
        Assert.True(r.ActiveChanged);
        var e = b.Store.Get(lea)!.Profile;                            // same id, still active, the file's content
        Assert.Equal("Léa", e.Name);
        Assert.Equal("chat", e.Avatar.Picto);
        Assert.Null(e.Layer["words"]?["count"]);
        Assert.Equal(lea, b.Store.ActiveId);
        Assert.Equal("piper:siwis", b.Svc.Settings.Current.Speech.Voice);   // the running app follows (Changed)
        Assert.Equal(3, b.Store.Count);
    }

    [Fact]
    public void Keep_Both_Names_Stay_Unique_And_Short()
    {
        Assert.Equal("Léa (3)", ProfileArchive.FreeName("Léa", new[] { "Léa", "léa (2)" }));
        var longName = new string('a', 30);
        var n = ProfileArchive.FreeName(longName, new[] { longName });
        Assert.Equal(30, n.Length);
        Assert.EndsWith(" (2)", n);
    }

    [Fact]
    public void Forty_Profiles_At_Most()
    {
        var pupils = Enumerable.Range(0, 30).Select(i => ($"Élève {i}", (string?)null, ProfileColor.Bleu, "{}")).ToArray();
        var a = NewPc("a", "{}", pupils);
        var path = ExportFile(a, a.Store.Profiles.Select(e => e.Id));
        var b = NewPc("b", "{}", Enumerable.Range(0, 20).Select(i => ($"Autre {i}", (string?)null, ProfileColor.Rose, "{}")).ToArray());
        var plan = ProfileArchive.Plan(ProfileArchive.Read(path), b.Store, null, new ImportOptions());
        Assert.Equal(11, plan.Excess);                               // 21 + 30 = 51 > 40
        Assert.False(plan.CanApply);
        var ex = Assert.Throws<ArchiveException>(() => ProfileArchive.Apply(plan, b.Svc, b.Lock, null));
        Assert.Contains("11", ex.Message);
        Assert.Equal(21, b.Store.Count);
        var choices = Enumerable.Range(0, 30).ToDictionary(i => i, i => new ImportChoice(i < 19, ConflictChoice.KeepBoth));
        var ok = ProfileArchive.Plan(ProfileArchive.Read(path), b.Store, choices, new ImportOptions());
        Assert.Equal(0, ok.Excess);
        ProfileArchive.Apply(ok, b.Svc, b.Lock, null);
        Assert.Equal(40, b.Store.Count);
    }

    /// <summary>Plan 09's budget « export / import of 40 profiles ≤ 2 s, file ≤ 2 MB ». Timed, so it runs in
    /// <see cref="TimingCollection"/> (alone, after the parallel tests: under the suite's load the same work went over 2 s
    /// in some runs), and only the export → read → plan → apply → writes-on-disk path is on the clock — building
    /// the two PCs and their queued writes is test setup, flushed before the stopwatch starts.</summary>
    [Collection(TimingCollection.Name)]
    public sealed class Timing : IDisposable
    {
        private readonly ProfileArchiveTests _t = new();
        private readonly ITestOutputHelper _out;

        public Timing(ITestOutputHelper output) => _out = output;
        public void Dispose() => _t.Dispose();

        [Fact]
        public void Forty_Profiles_Export_And_Import_Fast_And_Small()
        {
            var pupils = Enumerable.Range(0, 39).Select(i => ($"Élève {i}", (string?)"chat", (ProfileColor)(i % 8),
                                                               @"{""words"":{""size"":""L"",""font"":""lexend""},""speech"":{""rate"":-2}}")).ToArray();
            var a = _t.NewPc("a", @"{""words"":{""font"":""andika""}}", pupils);
            var b = _t.NewPc("b", "{}");
            a.Store.Flush();
            b.Store.Flush();
            var sw = Stopwatch.StartNew();
            var path = _t.ExportFile(a, a.Store.Profiles.Select(e => e.Id));
            var plan = ProfileArchive.Plan(ProfileArchive.Read(path), b.Store, null, new ImportOptions());
            ProfileArchive.Apply(plan, b.Svc, b.Lock, null);
            b.Store.Flush();                                   // the 40 profiles are on disk, not only queued
            sw.Stop();
            _out.WriteLine($"40 profiles exported + imported: {sw.ElapsedMilliseconds} ms, {new FileInfo(path).Length} bytes");
            Assert.Equal(40, b.Store.Count);
            Assert.True(new FileInfo(path).Length < 2 * 1024 * 1024);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"{sw.ElapsedMilliseconds} ms");
        }
    }

    [Theory]
    [InlineData("pas du json", "pas un fichier")]
    [InlineData(@"{""format"":""autre"",""version"":1,""profils"":[]}", "pas un fichier")]
    [InlineData(@"{""format"":""pepite-profils"",""profils"":[]}", "pas un fichier")]
    [InlineData(@"{""format"":""pepite-profils"",""version"":1}", "pas un fichier")]
    [InlineData(@"{""format"":""pepite-profils"",""version"":4,""profils"":[]}", "plus récente")]
    [InlineData(@"{""format"":""pepite-profils"",""version"":1,""classe"":{""settings"":3},""profils"":[]}", "Classe")]
    public void Bad_Files_Are_Refused_With_A_French_Message(string text, string expected)
    {
        var ex = Assert.Throws<ArchiveException>(() => ProfileArchive.Parse(text));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Oversize_File_Is_Refused_Before_Reading()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "big.pepite");
        using (var f = new FileStream(path, FileMode.Create)) f.SetLength(ProfileArchive.MaxBytes + 1);
        var ex = Assert.Throws<ArchiveException>(() => ProfileArchive.Read(path));
        Assert.Contains("trop gros", ex.Message);
        Assert.Contains("existe pas", Assert.Throws<ArchiveException>(() => ProfileArchive.Read(path + ".absent")).Message);
    }

    [Fact]
    public void Every_Profile_Is_Revalidated_And_Unknown_Keys_Are_Kept()
    {
        var c = ProfileArchive.Parse(@"{""format"":""pepite-profils"",""version"":1,""futur"":true,""profils"":[
            {""id"":""p-aaaaaa"",""name"":""  Léa  "",""avatar"":{""color"":""violet"",""picto"":""chat""},""settings"":{""words"":{""size"":""L""},""demain"":{""x"":1},""lock"":{""enabled"":true}}},
            {""name"":""LÉA"",""settings"":{}},
            {""name"":""Bad\u0007"",""settings"":{}},
            {""name"":""Sans réglages"",""settings"":[1]},
            {""name"":"""",""settings"":{}},
            {""name"":""../x"",""id"":""../../etc"",""settings"":{""speech"":{""voice"":""Microsoft Hortense Desktop""}}},
            42 ]}");
        Assert.Equal(7, c.Pupils.Count);
        var lea = c.Pupils[0];
        Assert.Null(lea.Problem);
        Assert.Equal("Léa", lea.Name);
        Assert.Equal(ProfileColor.Violet, lea.Avatar.Color);
        Assert.Equal(1, (int)lea.Layer["demain"]!["x"]!);            // unknown keys kept
        Assert.Null(lea.Layer["lock"]);                              // never a settings lock from a file
        Assert.NotNull(c.Pupils[1].Problem);                         // same name ignoring case
        Assert.NotNull(c.Pupils[2].Problem);                         // control character
        Assert.NotNull(c.Pupils[3].Problem);                         // settings not an object
        Assert.NotNull(c.Pupils[4].Problem);                         // empty name
        Assert.Null(c.Pupils[5].Problem);
        Assert.Null(c.Pupils[5].Id);                                 // an unsafe id is never used as a folder
        Assert.StartsWith("sapi:", (string)c.Pupils[5].Layer["speech"]!["voice"]!);   // voice-id migration
        Assert.NotNull(c.Pupils[6].Problem);
        var b = NewPc("b", "{}");
        var plan = ProfileArchive.Plan(c, b.Store, null, new ImportOptions());
        Assert.Equal(2, plan.Creates);
        Assert.Equal(5, plan.Skips);
        ProfileArchive.Apply(plan, b.Svc, b.Lock, null);
        Assert.All(Directory.GetDirectories(Path.Combine(b.Root, "profiles")), d => Assert.True(Profile.IsValidId(Path.GetFileName(d))));
    }

    [Fact]
    public void Pin_Is_Never_Applied_Unless_Ticked()
    {
        var a = Source();
        StrongPin(a.Lock, "480917");
        var path = ExportFile(a, a.Store.Profiles.Select(e => e.Id), pin: true);
        var b = NewPc("b", "{}");
        b.Lock.SetPin("725391");
        var c = ProfileArchive.Read(path);
        ProfileArchive.Apply(ProfileArchive.Plan(c, b.Store, null, new ImportOptions()), b.Svc, b.Lock, _log.Add);
        Assert.Equal(PinOutcome.Ok, b.Lock.Verify("725391").Outcome);
        Assert.Equal(PinOutcome.Wrong, b.Lock.Verify("480917").Outcome);

        var none = Enumerable.Range(0, 3).ToDictionary(i => i, _ => new ImportChoice(false, ConflictChoice.Skip));
        var planPin = ProfileArchive.Plan(c, b.Store, none, new ImportOptions(ApplyPin: true));
        Assert.True(planPin.CanApply);                               // the PIN line alone is work
        ProfileArchive.Apply(planPin, b.Svc, b.Lock, _log.Add);
        Assert.Equal(PinOutcome.Ok, b.Lock.Verify("480917").Outcome);
        Assert.Equal(PinOutcome.Wrong, b.Lock.Verify("725391").Outcome);
        Assert.Contains(_log, l => l.Contains("pin: imported"));
        Assert.DoesNotContain(_log, l => l.Contains("480917") || l.Contains("725391") || l.Contains(c.Pin!.Hash));

        // a file without a teacher code: the line has nothing to apply
        var noPin = ProfileArchive.Read(ExportFile(a, Array.Empty<string>()));
        Assert.False(ProfileArchive.Plan(noPin, b.Store, null, new ImportOptions(ApplyPin: true)).ApplyPin);
    }

    [Fact]
    public void Apply_Makes_A_Backup_First_And_Refuses_Without_One()
    {
        var a = Source();
        var path = ExportFile(a, a.Store.Profiles.Select(e => e.Id));
        var b = NewPc("b", "{}");
        var c = ProfileArchive.Read(path);
        var r = ProfileArchive.Apply(ProfileArchive.Plan(c, b.Store, null, new ImportOptions()), b.Svc, b.Lock, _log.Add);
        Assert.NotNull(r.Backup);
        using (var z = ZipFile.OpenRead(Path.Combine(b.Backups, r.Backup!)))
            Assert.DoesNotContain(z.Entries, e => e.FullName.Contains(IdOf(b.Store, "Tom")));   // taken before the import

        // the backup folder cannot be created (a file is in the way): nothing imported
        var d = NewPc("d", "{}");
        Directory.Delete(d.Backups, true);
        File.WriteAllText(d.Backups, "not a folder");
        var ex = Assert.Throws<ArchiveException>(() => ProfileArchive.Apply(ProfileArchive.Plan(c, d.Store, null, new ImportOptions()), d.Svc, d.Lock, null));
        Assert.Contains("sauvegarde", ex.Message);
        Assert.Equal(1, d.Store.Count);
    }

    private static string FileWithPin(JsonObject pin, JsonObject? recovery = null)
    {
        var t = new JsonObject { ["pin"] = pin };
        if (recovery != null) t["recovery"] = recovery;
        return new JsonObject
        {
            ["format"] = "pepite-profils", ["version"] = 1, ["profils"] = new JsonArray(), ["enseignant"] = t,
        }.ToJsonString();
    }

    private static JsonObject HashJ(string alg, int it, int saltBytes, int hashBytes) => new()
    {
        ["alg"] = alg, ["iterations"] = it,
        ["salt"] = Convert.ToBase64String(new byte[saltBytes]), ["hash"] = Convert.ToBase64String(new byte[hashBytes]),
    };

    [Theory]
    [InlineData("pbkdf2-sha1", 600_000, 16, 32)]
    [InlineData("pbkdf2-sha256", 1_000, 16, 32)]          // too few iterations: easy to guess offline
    [InlineData("pbkdf2-sha256", 99_999, 16, 32)]
    [InlineData("pbkdf2-sha256", 10_000_001, 16, 32)]     // too many: every check would freeze
    [InlineData("pbkdf2-sha256", 600_000, 8, 32)]         // short salt
    [InlineData("pbkdf2-sha256", 600_000, 65, 32)]
    [InlineData("pbkdf2-sha256", 600_000, 16, 16)]        // not a 32-byte hash
    [InlineData("pbkdf2-sha256", 600_000, 16, 64)]
    public void Unsafe_Teacher_Code_Parameters_Are_Refused(string alg, int it, int salt, int hash)
    {
        var c = ProfileArchive.Parse(FileWithPin(HashJ(alg, it, salt, hash)));
        Assert.False(c.HasPin);
        Assert.Contains("ne peut pas être utilisé", c.PinProblem);
        // a good PIN with a bad recovery hash: the whole teacher part is refused
        var c2 = ProfileArchive.Parse(FileWithPin(HashJ("pbkdf2-sha256", 600_000, 16, 32), HashJ(alg, it, salt, hash)));
        Assert.False(c2.HasPin);
        Assert.NotNull(c2.PinProblem);
        // and the lock itself never stores such a hash
        var l = new TeacherLock(new JsonFileStore(Path.Combine(_dir, "t-" + Guid.NewGuid().ToString("N") + ".json"), null, "profiles"), null, null);
        Assert.Throws<ArgumentException>(() => l.SetHashes(TeacherLock.ParseHash(HashJ(alg, it, salt, hash))!, null));
    }

    [Theory]
    [InlineData(100_000, 16)]
    [InlineData(10_000_000, 64)]
    public void Safe_Teacher_Code_Parameters_Are_Accepted(int it, int salt)
    {
        var c = ProfileArchive.Parse(FileWithPin(HashJ("pbkdf2-sha256", it, salt, 32)));
        Assert.True(c.HasPin);
        Assert.Null(c.PinProblem);
    }

    [Fact]
    public void A_File_With_More_Than_200_Profiles_Is_Refused()
    {
        var arr = new JsonArray();
        for (int i = 0; i < ProfileArchive.MaxFileProfiles + 1; i++) arr.Add(new JsonObject { ["name"] = "Élève " + i, ["settings"] = new JsonObject() });
        var text = new JsonObject { ["format"] = "pepite-profils", ["version"] = 1, ["profils"] = arr }.ToJsonString();
        Assert.Contains("trop de profils", Assert.Throws<ArchiveException>(() => ProfileArchive.Parse(text)).Message);
        arr.RemoveAt(0);
        var ok = new JsonObject { ["format"] = "pepite-profils", ["version"] = 1, ["profils"] = Json.Copy(arr) }.ToJsonString();
        Assert.Equal(200, ProfileArchive.Parse(ok).Pupils.Count);
    }

    [Fact]
    public void Replace_Class_Repairs_An_Unreadable_Class_And_Matches_The_Plan()
    {
        var a = Source();
        var path = ExportFile(a, new[] { IdOf(a.Store, "Tom"), IdOf(a.Store, "Zoé") });
        var b = NewPc("b", @"{""words"":{""size"":""L""}}", ("Léa", null, ProfileColor.Rose, "{}"));
        b.Store.Flush();
        // « Classe » of B becomes unreadable (bad JSON); the store is reopened as at the next start
        File.WriteAllText(Path.Combine(b.Root, "profiles", Profile.ClassId, "profile.json"), "{ pas du json");
        var store = new ProfileStore(b.Root, b.Backups, _log.Add);
        store.Load();
        Assert.True(store.Class!.Unreadable);
        var svc = new ProfileService(store, ProfileService.CreateSettings(store, MachineLayer.None, null), new Host(_calls), null);
        var plan = ProfileArchive.Plan(ProfileArchive.Read(path), store, null, new ImportOptions(ReplaceClass: true));
        var r = ProfileArchive.Apply(plan, svc, b.Lock, _log.Add);
        Assert.True(r.ClassReplaced);
        Assert.False(store.Class!.Unreadable);
        Assert.Equal(@"{""words"":{""font"":""andika""}}", store.Class.Profile.Layer.ToJsonString());
        Assert.Equal(0, store.Profiles.ToList().FindIndex(e => e.IsClass));   // still first
        store.Flush();
        var again = new ProfileStore(b.Root, b.Backups, null);
        again.Load();
        Assert.False(again.Class!.Unreadable);                              // written to disk
        // what was planned is what is applied: the pupils' effective settings equal A's
        foreach (var n in new[] { "Tom", "Zoé" })
            Assert.Equal(Effective(a.Store, IdOf(a.Store, n)).ToJson().ToJsonString(), Effective(again, IdOf(again, n)).ToJson().ToJsonString());
        Assert.Contains(_log, l => l.Contains("unreadable classe overwritten"));
    }

    [Fact]
    public void In_Memory_Session_Imports_Nothing()
    {
        var a = Source();
        var path = ExportFile(a, a.Store.Profiles.Select(e => e.Id));
        var root = Path.Combine(_dir, "mem");
        var store = new ProfileStore(root, Path.Combine(root, "b"), _log.Add);
        store.LoadInMemory(new JsonObject(), "test");
        var svc = new ProfileService(store, ProfileService.CreateSettings(store, MachineLayer.None, null), new Host(_calls), null);
        var plan = ProfileArchive.Plan(ProfileArchive.Read(path), store, null, new ImportOptions());
        Assert.Throws<ArchiveException>(() => ProfileArchive.Apply(plan, svc, null, null));
        Assert.Equal(1, store.Count);
        Assert.False(Directory.Exists(Path.Combine(root, "profiles")));
    }

    [Fact]
    public void Positions_Not_On_A_Screen_Of_This_Pc_Are_Cleared()
    {
        var a = Source();
        var tom = IdOf(a.Store, "Tom");
        a.Store.SetLayer(tom, J(@"{""words"":{""size"":""XL""},""bar"":{""position"":""free"",""free"":{""x"":5000,""y"":4000}},""reading"":{""window"":{""x"":10,""y"":10,""w"":800,""h"":600}}}"));
        var path = ExportFile(a, new[] { tom });
        var b = NewPc("b", "{}");
        bool OnScreen(int x, int y, int w, int h) => x >= 0 && y >= 0 && x + w <= 1920 && y + h <= 1080;
        var plan = ProfileArchive.Plan(ProfileArchive.Read(path), b.Store, null, new ImportOptions(IsVisible: OnScreen));
        var layer = plan.Items[0].Layer;
        Assert.Null(layer["bar"]!["free"]);
        Assert.Equal("free", (string)layer["bar"]!["position"]!);
        Assert.NotNull(layer["reading"]!["window"]);
    }
}
