// xUnit1031: blocking on a task (Wait / Result) is deliberate here: the test checks, synchronously, the state that the
// app's background write leaves behind (no xUnit sync context, so no deadlock)
#pragma warning disable xUnit1031
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan installer-upgrade step 1 (R2): a file written by a newer Pépite (higher <c>schema</c>) is read, never
/// rewritten — for the store itself and for every file built on it (settings.json, index.json, profile.json, teacher.json,
/// mots.json).</summary>
public class JsonFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kbjson-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();

    public JsonFileStoreTests() => TeacherLock.Iterations = 1000;

    public void Dispose() => TestDirs.Delete(_root);

    private string Write(string name, string json)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, json);
        return p;
    }

    /// <summary>Set the <c>schema</c> of a file on disk (keeps everything else).</summary>
    private static void SetSchema(string path, int schema)
    {
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        o["schema"] = schema;
        File.WriteAllText(path, o.ToJsonString());
    }

    [Fact]
    public void A_Newer_Schema_Is_Read_But_Never_Written()
    {
        var p = Write("a.json", @"{""schema"":2,""x"":1,""later"":{""k"":true}}");
        var before = File.ReadAllBytes(p);
        var s = new JsonFileStore(p, _log.Add, "test", schema: 1);
        var o = s.Load();
        Assert.Equal(1, (int)o["x"]!);              // the content is used
        Assert.True(s.ReadOnly);
        Assert.False(s.Unreadable);
        Assert.Equal(2, s.NewerSchema);
        s.Update(r => r["x"] = 5).Wait();
        Assert.Throws<IOException>(() => s.UpdateNow(r => r["x"] = 6));
        s.Flush();
        Assert.Equal(before, File.ReadAllBytes(p));
        Assert.Contains(_log, l => l.Contains("a.json has schema 2, newer than this version (1): read, never written this session"));
        Assert.Empty(Directory.GetFiles(_root, "a.json.bad-*"));   // nothing moved aside
    }

    [Theory]
    [InlineData(@"{""schema"":1,""x"":1}")]
    [InlineData(@"{""x"":1}")]
    [InlineData(@"{""schema"":""deux"",""x"":1}")]
    [InlineData(@"{""schema"":0,""x"":1}")]
    public void The_Same_Or_No_Schema_Stays_Writable(string json)
    {
        var p = Write("a.json", json);
        var s = new JsonFileStore(p, _log.Add, "test", schema: 1);
        s.Load();
        Assert.False(s.ReadOnly);
        Assert.Null(s.NewerSchema);
        s.Update(r => r["x"] = 5).Wait();
        var o = JsonNode.Parse(File.ReadAllText(p))!.AsObject();
        Assert.Equal(5, (int)o["x"]!);
        Assert.Equal(1, (int)o["schema"]!);
    }

    [Fact]
    public void A_Store_Without_A_Schema_Never_Refuses()
    {
        var p = Write("a.json", @"{""schema"":9,""x"":1}");
        var s = new JsonFileStore(p, _log.Add, "test");
        s.Load();
        Assert.False(s.ReadOnly);
    }

    [Fact]
    public void Settings_Json_At_Schema_2()
    {
        var p = Write(SettingsStore.FileName, @"{""schema"":2,""state"":{""welcomeShown"":true,""lastVersion"":""0.12.0""}}");
        var before = File.ReadAllBytes(p);
        var s = new SettingsStore(_root, _log.Add);
        s.Load();
        Assert.True(s.ReadOnly);
        Assert.True(s.WelcomeShown);
        Assert.Equal("0.12.0", s.LastVersion);
        s.SetWelcomeShown(false).Wait();
        s.SetLastVersion("0.11.0").Wait();
        s.Flush();
        Assert.Equal(before, File.ReadAllBytes(p));
        Assert.Contains(_log, l => l.StartsWith("settings: settings.json has schema 2"));
    }

    [Fact]
    public void Index_Profile_And_Teacher_At_Schema_2()
    {
        var s = new ProfileStore(_root, null, _log.Add);
        s.Load();
        var lea = s.Add("Léa", Avatar.Default);
        s.Flush();
        var index = ProfileStore.IndexPath(_root);
        var profile = Path.Combine(s.ProfilesDir, lea.Id, ProfileStore.ProfileFile);
        var teacher = Write(Path.Combine(ProfileStore.DirName, ProfileStore.TeacherFile), @"{""schema"":2,""pin"":null}");
        SetSchema(index, 2);
        SetSchema(profile, 2);
        var (bi, bp, bt) = (File.ReadAllBytes(index), File.ReadAllBytes(profile), File.ReadAllBytes(teacher));

        var again = new ProfileStore(_root, null, _log.Add);
        again.Load();
        Assert.Equal(2, again.Count);
        var e = again.Get(lea.Id)!;
        Assert.False(e.Unreadable);
        Assert.Equal("Léa", e.Profile.Name);                      // read
        Assert.True(e.File.ReadOnly);
        again.SetActive(lea.Id);
        again.SaveIndex();
        again.SetLayer(lea.Id, JsonNode.Parse(@"{""words"":{""count"":3}}")!.AsObject());
        var tf = again.OpenTeacherFile();
        Assert.True(tf.ReadOnly);
        tf.Update(r => r["pin"] = "x").Wait();
        again.Flush();
        Assert.Equal(bi, File.ReadAllBytes(index));
        Assert.Equal(bp, File.ReadAllBytes(profile));
        Assert.Equal(bt, File.ReadAllBytes(teacher));
        Assert.Contains(_log, l => l.Contains("index.json has schema 2"));
        Assert.Contains(_log, l => l.Contains("profile.json has schema 2"));
        Assert.Contains(_log, l => l.Contains("teacher.json has schema 2"));
    }

    [Fact]
    public void Mots_Json_At_Schema_2()
    {
        var dir = Path.Combine(_root, ProfileStore.DirName);
        var words = new WordListStore(dir, _log.Add);
        words.Set("p-aaaaaa", WordBook.Empty.AddWord(null, "Zorglub", WordEntry.Manual, DateTime.Now, false, out _));
        words.Flush();
        var p = words.PathOf("p-aaaaaa");
        SetSchema(p, 2);
        var before = File.ReadAllBytes(p);

        var again = new WordListStore(dir, _log.Add);
        Assert.Equal("Zorglub", again.Get("p-aaaaaa").Default!.Words.Single().W);   // read
        again.Set("p-aaaaaa", WordBook.Empty);
        again.Flush();
        Assert.Equal(before, File.ReadAllBytes(p));
        Assert.Contains(_log, l => l.StartsWith("words: mots.json has schema 2"));
    }
}
