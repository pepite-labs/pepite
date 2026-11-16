using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 10A step 8: export / import version 2 — words in the file (ticked by default), a v1 file still
/// imported, the word rebase keeps a pupil's effective list, Replace replaces / Keep both copies, oversize lists
/// refused, a version 4 file refused; plan 10C: the version 3 zip with the pictures of the names lists.</summary>
public class ProfileArchiveWordsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kbarchw-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();
    private static readonly DateTime Now = new(2026, 11, 14);

    public ProfileArchiveWordsTests() => TeacherLock.Iterations = 1000;

    public void Dispose()
    {
        TestDirs.Delete(_dir);
    }

    private sealed class Host : IProfileHost
    {
        public void ResetForSwitch() { }
        public void CancelSpeech() { }
        public void CloseProfileWindows() { }
    }

    private sealed record Pc(ProfileStore Store, ProfileService Svc, WordListService Words);

    private Pc NewPc(string name, string[] classWords, params (string Name, string[] Words, string[] Hidden)[] pupils)
    {
        var root = Path.Combine(_dir, name);
        var store = new ProfileStore(root, Path.Combine(root, "sauvegardes"), _log.Add);
        store.Load();
        store.Add(Strings.ClassProfileName, new Avatar(null, ProfileColor.Vert), null, Profile.ClassId);
        store.SetActive(Profile.ClassId);
        store.Delete(store.Profiles.First(e => e.Profile.Name == Strings.DefaultProfileName).Id);
        var settings = ProfileService.CreateSettings(store, MachineLayer.None, _log.Add);
        var svc = new ProfileService(store, settings, new Host(), _log.Add);
        var words = new WordListService(new WordListStore(store.ProfilesDir, _log.Add), store, _ => { }, _log.Add, () => Now);
        svc.Words = words;
        foreach (var w in classWords) words.AddWord(Profile.ClassId, w);
        foreach (var p in pupils)
        {
            var id = store.Add(p.Name, Avatar.Default).Id;
            foreach (var w in p.Words) words.AddWord(id, w);
            foreach (var h in p.Hidden) words.Update(id, b => (b.SetHidden(h, true), null), "hide");
        }
        words.Rebuild("test");
        return new Pc(store, svc, words);
    }

    private static string IdOf(Pc pc, string name) => pc.Store.Profiles.First(e => e.Profile.Name == name).Id;

    private string Export(Pc pc, bool words = true)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ProfileArchive.Extension);
        pc.Words.Store.Flush();
        ProfileArchive.Write(path, pc.Store, pc.Store.Profiles.Where(e => !e.IsClass).Select(e => e.Id), false, null, "Pépite test", _log.Add,
                             words ? pc.Words.BookOf : null);
        return path;
    }

    private ImportResult Import(Pc pc, string path, bool replaceClass = false, ConflictChoice conflict = ConflictChoice.KeepBoth)
    {
        var content = ProfileArchive.Read(path);
        var choices = Enumerable.Range(0, content.Pupils.Count).ToDictionary(i => i, _ => new ImportChoice(true, conflict));
        var plan = ProfileArchive.Plan(content, pc.Store, choices, new ImportOptions(replaceClass, false, null, pc.Words.BookOf, Now));
        return ProfileArchive.Apply(plan, pc.Svc, null, _log.Add);
    }

    [Fact]
    public void Round_Trip_With_Words_Into_A_Pc_With_Another_Class()
    {
        var a = NewPc("a", new[] { "Léa", "Tom", "cratère" }, ("Léa", new[] { "Vésuve" }, new[] { "cratère" }), ("Tom", new[] { "Zorglub" }, Array.Empty<string>()));
        var a2 = a.Words.Update(IdOf(a, "Léa"), b => { var r = b.SetFaute("pace", "parce", out var p); return (r, p); }, "pair");
        var file = Export(a);
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal(2, (int)root["version"]!);
        Assert.NotNull(root["classe"]!["mots"]);
        Assert.NotNull(root["profils"]![0]!["mots"]);
        Assert.Null(root["classe"]!["mots"]!["schema"]);

        var leaBefore = WordLayering.FormSet(a.Words.BookOf(IdOf(a, "Léa")), a.Words.BookOf(Profile.ClassId));
        var b = NewPc("b", new[] { "Inès", "cratère" });
        Import(b, file);
        var lea = IdOf(b, "Léa");
        var leaAfter = WordLayering.FormSet(b.Words.BookOf(lea), b.Words.BookOf(Profile.ClassId));
        Assert.Empty(leaBefore.Except(leaAfter));                       // nothing lost
        Assert.Equal(new[] { "Inès" }, leaAfter.Except(leaBefore));     // the new class's own word
        Assert.Contains("cratère", b.Words.BookOf(lea).Masques);        // still hidden: this class has it too
        Assert.Equal("parce", b.Words.BookOf(lea).Fautes.Single().Mot);
        Assert.Equal(new[] { "Inès", "cratère" }, b.Words.BookOf(Profile.ClassId).Default!.Words.Select(e => e.W));   // class kept
        Assert.DoesNotContain(_log, l => l.Contains("Vésuve") || l.Contains("Zorglub"));
    }

    [Fact]
    public void Replace_Class_Takes_The_File_Class_Words()
    {
        var a = NewPc("a", new[] { "Léa", "volcan" }, ("Léa", new[] { "Vésuve" }, Array.Empty<string>()));
        var b = NewPc("b", new[] { "Inès" });
        Import(b, Export(a), replaceClass: true);
        Assert.Equal(new[] { "Léa", "volcan" }, b.Words.BookOf(Profile.ClassId).Default!.Words.Select(e => e.W));
        Assert.Equal(new[] { "Vésuve" }, b.Words.BookOf(IdOf(b, "Léa")).Default!.Words.Select(e => e.W));   // no rebase needed
    }

    [Fact]
    public void Replace_Replaces_The_Words_Keep_Both_Copies_Them()
    {
        var a = NewPc("a", Array.Empty<string>(), ("Léa", new[] { "Vésuve" }, Array.Empty<string>()));
        var file = Export(a);
        var b = NewPc("b", Array.Empty<string>(), ("Léa", new[] { "Etna" }, Array.Empty<string>()));
        Import(b, file, conflict: ConflictChoice.Replace);
        Assert.Equal(new[] { "Vésuve" }, b.Words.BookOf(IdOf(b, "Léa")).Default!.Words.Select(e => e.W));
        var c = NewPc("c", Array.Empty<string>(), ("Léa", new[] { "Etna" }, Array.Empty<string>()));
        Import(c, file, conflict: ConflictChoice.KeepBoth);
        Assert.Equal(new[] { "Etna" }, c.Words.BookOf(IdOf(c, "Léa")).Default!.Words.Select(e => e.W));
        Assert.Equal(new[] { "Vésuve" }, c.Words.BookOf(IdOf(c, "Léa (2)")).Default!.Words.Select(e => e.W));
    }

    [Fact]
    public void Without_Words_Or_From_A_V1_File_The_Words_Stay()
    {
        var a = NewPc("a", new[] { "Léa" }, ("Léa", new[] { "Vésuve" }, Array.Empty<string>()));
        var file = Export(a, words: false);
        Assert.Null(JsonNode.Parse(File.ReadAllText(file))!["classe"]!["mots"]);
        var b = NewPc("b", new[] { "Inès" }, ("Léa", new[] { "Etna" }, Array.Empty<string>()));
        Import(b, file, replaceClass: true, conflict: ConflictChoice.Replace);
        Assert.Equal(new[] { "Etna" }, b.Words.BookOf(IdOf(b, "Léa")).Default!.Words.Select(e => e.W));
        Assert.Equal(new[] { "Inès" }, b.Words.BookOf(Profile.ClassId).Default!.Words.Select(e => e.W));
        // a 0.7 (version 1) file is still read
        var v1 = Path.Combine(_dir, "v1.pepite");
        File.WriteAllText(v1, @"{""format"":""pepite-profils"",""version"":1,""classe"":{""settings"":{}},""profils"":[{""name"":""Zoé"",""settings"":{}}]}");
        Import(b, v1);
        Assert.True(b.Words.BookOf(IdOf(b, "Zoé")).IsEmpty);
    }

    [Fact]
    public void Oversize_Lists_Are_Refused()
    {
        var big = new JsonArray(Enumerable.Range(0, 5001).Select(i => (JsonNode?)new JsonObject { ["w"] = WordListModelTests.Name(i) }).ToArray());
        var o = new JsonObject
        {
            ["format"] = "pepite-profils", ["version"] = 2, ["classe"] = new JsonObject { ["settings"] = new JsonObject() },
            ["profils"] = new JsonArray(new JsonObject { ["name"] = "Léa", ["mots"] = new JsonObject { ["listes"] = new JsonArray(new JsonObject { ["id"] = "mes-mots", ["mots"] = big }) } }),
        };
        var content = ProfileArchive.Parse(o.ToJsonString());
        Assert.Equal(Strings.ImportRowBadWords, content.Pupils[0].Problem);
        var bigClass = Enumerable.Range(0, 10001).Select(i => (JsonNode?)new JsonObject { ["w"] = WordListModelTests.Name(i) }).ToArray();
        o["classe"] = new JsonObject { ["mots"] = new JsonObject { ["listes"] = new JsonArray(new JsonObject { ["mots"] = new JsonArray(bigClass) }) } };
        o["profils"] = new JsonArray();
        var ex = Assert.Throws<ArchiveException>(() => ProfileArchive.Parse(o.ToJsonString()));
        Assert.Equal(Strings.ImportClassWordsBad, ex.Message);
    }

    /// <summary>A stored thumbnail: a PNG header (enough for the header check) named by the hash of its bytes.</summary>
    private static (string Name, byte[] Bytes) Thumb(byte tag)
    {
        var b = new byte[40];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 64, 0, 0, 0, 48 }.CopyTo(b, 0);
        b[39] = tag;
        return (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(b)).ToLowerInvariant()[..16] + ".png", b);
    }

    [Fact]
    public void Version_3_Zip_Carries_The_Pictures_Of_The_Names_Lists_Only_When_Asked()
    {
        var a = NewPc("a3", new[] { "Léa" }, ("Tom", Array.Empty<string>(), Array.Empty<string>()));
        var (fc, bc) = Thumb(1);
        var (ft, bt) = Thumb(2);
        void AddNames(string id, string file, byte[] bytes, string name)
        {
            Directory.CreateDirectory(a.Words.ImagesDir(id)!);
            File.WriteAllBytes(Path.Combine(a.Words.ImagesDir(id)!, file), bytes);
            var list = new WordListDef("", "Drapeaux", true, WordListDef.Names,
                                       new[] { new WordEntry(name, WordEntry.Import, "2026-11-14", new NameMeta(file, "CC0", null, null, "pays", null)) }, null, "CC0");
            Assert.Null(a.Words.Update(id, b => { var r = b.AddNamesList(list, id == Profile.ClassId, out _, out var p); return (r, p); }, "names"));
        }
        var tom = IdOf(a, "Tom");
        AddNames(Profile.ClassId, fc, bc, "Belgique");
        AddNames(tom, ft, bt, "Ypres");
        a.Words.Store.Flush();
        var ids = a.Store.Profiles.Where(e => !e.IsClass).Select(e => e.Id).ToList();

        // without « Inclure les images »: a version 2 JSON file, the names lists without their pictures' files
        var json = Path.Combine(_dir, "sans.pepite");
        ProfileArchive.Write(json, a.Store, ids, false, null, "Pépite test", _log.Add, a.Words.BookOf);
        Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllText(json))!["version"]!);

        // with it: a version 3 zip
        var zip = Path.Combine(_dir, "avec.pepite");
        ProfileArchive.Write(zip, a.Store, ids, false, null, "Pépite test", _log.Add, a.Words.BookOf, a.Words.ImagesDir);
        using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
        {
            Assert.NotNull(z.GetEntry(ProfileArchive.ZipIndex));
            Assert.NotNull(z.GetEntry("images/classe/" + fc));
            Assert.NotNull(z.GetEntry("images/" + tom + "/" + ft));
        }
        var content = ProfileArchive.Read(zip);
        Assert.Equal(3, content.Version);
        Assert.Equal(2, content.Images!.Count);

        // into another PC: Tom's picture lands in the new profile's folder; « Classe »'s with « Remplacer la classe »
        var b2 = NewPc("b3", Array.Empty<string>());
        var choices = Enumerable.Range(0, content.Pupils.Count).ToDictionary(i => i, _ => new ImportChoice(true, ConflictChoice.KeepBoth));
        var plan = ProfileArchive.Plan(content, b2.Store, choices, new ImportOptions(true, false, null, b2.Words.BookOf, Now));
        ProfileArchive.Apply(plan, b2.Svc, null, _log.Add);
        var newTom = IdOf(b2, "Tom");
        Assert.True(File.Exists(Path.Combine(b2.Words.ImagesDir(newTom)!, ft)));
        Assert.True(File.Exists(Path.Combine(b2.Words.ImagesDir(Profile.ClassId)!, fc)));
        var (own, _) = b2.Words.PictureMaps(newTom);
        Assert.Equal(Path.Combine(b2.Words.ImagesDir(newTom)!, ft), own["Ypres"]);

        // a picture whose content does not match its name is ignored
        var bad = Path.Combine(_dir, "bad.pepite");
        File.Copy(zip, bad);
        using (var z = System.IO.Compression.ZipFile.Open(bad, System.IO.Compression.ZipArchiveMode.Update))
        {
            z.GetEntry("images/" + tom + "/" + ft)!.Delete();
            using var s = z.CreateEntry("images/" + tom + "/" + ft).Open();
            s.Write(bc, 0, bc.Length);
        }
        Assert.Single(ProfileArchive.Read(bad).Images!);
    }
}
