using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan dys-sound-tolerance Phase 2 (D33): « Sons que l'élève confond » — <c>words.soundPairs.*</c> in the settings
/// model, inherited from « Classe », lockable per leaf by the machine file, carried by a profile switch and a <c>.pepite</c>
/// export / import as plain JSON (no new layering code).</summary>
public class SoundPairSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kbsounds-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();

    public SoundPairSettingsTests() => TeacherLock.Iterations = 1000;

    public void Dispose() => TestDirs.Delete(_root);

    private static JsonObject J(string s) => JsonNode.Parse(s)!.AsObject();

    private sealed class Host : IProfileHost
    {
        public void ResetForSwitch() { }
        public void CancelSpeech() { }
        public void CloseProfileWindows() { }
    }

    [Fact]
    public void Defaults_Are_ChJ_On_And_The_Four_Others_Off()
    {
        var s = new AppSettings().Sounds;
        Assert.Equal(new SoundPairSettings(true, false, false, false, false), s);
        Assert.Equal(SoundPairs.ChJ, s.ToEngine());
        var w = new AppSettings().ToJson()["words"]!["soundPairs"]!.AsObject();
        Assert.Equal(new[] { "chj", "fv", "kg", "td", "pb" }, w.Select(kv => kv.Key));
        Assert.True((bool)w["chj"]!);
        Assert.False((bool)w["pb"]!);
        Assert.Equal(SoundPairs.ChJ, AppSettings.FromJson(null).Sounds.ToEngine());
    }

    [Fact]
    public void ToEngine_Maps_Every_Box_To_Its_Pair()
    {
        Assert.Equal(SoundPairs.None, new SoundPairSettings(false).ToEngine());
        Assert.Equal(SoundPairs.FV, new SoundPairSettings(false, FV: true).ToEngine());
        Assert.Equal(SoundPairs.KG, new SoundPairSettings(false, KG: true).ToEngine());
        Assert.Equal(SoundPairs.TD, new SoundPairSettings(false, TD: true).ToEngine());
        Assert.Equal(SoundPairs.PB, new SoundPairSettings(false, PB: true).ToEngine());
        Assert.Equal(SoundPairsInfo.All, new SoundPairSettings(true, true, true, true, true).ToEngine());
    }

    [Fact]
    public void Json_Round_Trip_And_Garbage_Leaves_Fall_Back_To_Their_Default()
    {
        var on = new AppSettings() with { Sounds = new SoundPairSettings(false, true, false, true, false) };
        var back = AppSettings.FromJson(on.ToJson());
        Assert.Equal(on.Sounds, back.Sounds);
        Assert.Equal(on, back);
        // a string, a number, a null, an array: each leaf alone falls back; the valid one is kept
        var g = AppSettings.FromJson(J(@"{""words"":{""soundPairs"":{""chj"":""no"",""fv"":1,""kg"":null,""td"":[true],""pb"":true}}}"));
        Assert.Equal(new SoundPairSettings(true, false, false, false, true), g.Sounds);
        Assert.Equal(new SoundPairSettings(), AppSettings.FromJson(J(@"{""words"":{""soundPairs"":true}}")).Sounds);
        Assert.Equal(new SoundPairSettings(), AppSettings.FromJson(J(@"{""words"":{""soundPairs"":{""zz"":true}}}")).Sounds);
    }

    [Fact]
    public void Class_Is_Inherited_The_Pupils_Own_Choice_Wins_And_A_Machine_Lock_Wins_Over_Both()
    {
        var cls = J(@"{""words"":{""soundPairs"":{""fv"":true,""td"":true}}}");
        var (eff, _) = SettingsLayering.Merge(null, cls, null, null, null);
        Assert.Equal(SoundPairs.ChJ | SoundPairs.FV | SoundPairs.TD, eff.Sounds.ToEngine());
        (eff, _) = SettingsLayering.Merge(null, cls, J(@"{""words"":{""soundPairs"":{""fv"":false}}}"), null, null);
        Assert.Equal(SoundPairs.ChJ | SoundPairs.TD, eff.Sounds.ToEngine());   // the pupil's own leaf wins, the others inherited
        (eff, var locked) = SettingsLayering.Merge(J(@"{""words"":{""soundPairs"":{""kg"":true}}}"), cls,
                                                    J(@"{""words"":{""soundPairs"":{""chj"":true}}}"), null,
                                                    J(@"{""words"":{""soundPairs"":{""chj"":false}}}"));
        Assert.False(eff.Sounds.ChJ);                                         // locked off over the pupil's own true
        Assert.True(eff.Sounds.KG);                                           // machine default
        Assert.True(Json.IsLocked(locked, "words.soundPairs.chj"));
        Assert.False(Json.IsLocked(locked, "words.soundPairs.fv"));
        Assert.True(Json.IsLocked(locked, "words.soundPairs"));              // the group header is « inside » a lock
    }

    [Fact]
    public void A_Saved_Edit_Writes_Only_The_Changed_Leaf_And_Never_A_Locked_One()
    {
        var machine = new MachineLayer(null, J(@"{""words"":{""soundPairs"":{""chj"":false}}}"));
        var cls = J(@"{""words"":{""soundPairs"":{""fv"":true}}}");
        var (cur, locked) = SettingsLayering.Merge(machine, cls, null);
        var edited = cur with { Sounds = cur.Sounds with { ChJ = true, PB = true } };
        var user = SettingsLayering.NewUserLayer(null, cur, edited, machine, locked, cls);
        Assert.Equal(@"{""words"":{""soundPairs"":{""pb"":true}}}", user.ToJsonString());
        // ticking back the class value removes the pupil's leaf (inherited again)
        var (cur2, _) = SettingsLayering.Merge(machine, cls, J(@"{""words"":{""soundPairs"":{""fv"":false}}}"));
        var back = SettingsLayering.NewUserLayer(J(@"{""words"":{""soundPairs"":{""fv"":false}}}"), cur2,
                                                 cur2 with { Sounds = cur2.Sounds with { FV = true } }, machine, locked, cls);
        Assert.Null(back["words"]);
    }

    [Fact]
    public void Check_Config_Accepts_The_New_Keys()
    {
        int code = MachineLayer.Check("x.json", _ => @"{""schema"":1,""defaults"":{""words"":{""soundPairs"":{""fv"":true}}}," +
                                                       @"""locked"":{""words"":{""soundPairs"":{""chj"":false}}}}", out var msg);
        Assert.Equal(MachineLayer.CheckValid, code);
        Assert.Contains("words.soundPairs.fv", msg);
        Assert.Contains("words.soundPairs.chj", msg);
    }

    /// <summary>Step 8's path: <c>KeyboardController.Configure</c> runs on <see cref="SettingsService.Changed"/>; a profile
    /// switch and a « Classe » edit raise it with the new pairs.</summary>
    [Fact]
    public void Profile_Switch_And_Class_Edit_Raise_Changed_With_The_New_Pairs()
    {
        var store = new ProfileStore(_root, null, _log.Add);
        store.Load();
        store.Add(Strings.ClassProfileName, Avatar.Default, J(@"{""words"":{""soundPairs"":{""fv"":true}}}"), Profile.ClassId);
        var lea = store.Add("Léa", new Avatar("chat", ProfileColor.Orange), J("{}")).Id;
        var tom = store.Add("Tom", new Avatar("fusée", ProfileColor.Vert), J(@"{""words"":{""soundPairs"":{""fv"":false,""chj"":false}}}")).Id;
        store.SetActive(lea);
        var settings = ProfileService.CreateSettings(store, MachineLayer.None, _log.Add);
        var seen = new List<SoundPairs>();
        settings.Changed += (_, n) => seen.Add(n.Sounds.ToEngine());
        var svc = new ProfileService(store, settings, new Host(), _log.Add);
        Assert.Equal(SoundPairs.ChJ | SoundPairs.FV, settings.Current.Sounds.ToEngine());   // Léa inherits f/v
        Assert.True(svc.Switch(tom, "test"));
        Assert.Equal(new[] { SoundPairs.None }, seen);
        svc.Switch(lea, "test");
        var cls = new ProfileEditTarget(svc, Profile.ClassId);
        cls.Apply(cls.Current with { Sounds = cls.Current.Sounds with { KG = true } });
        Assert.Equal(new[] { SoundPairs.None, SoundPairs.ChJ | SoundPairs.FV, SoundPairs.ChJ | SoundPairs.FV | SoundPairs.KG }, seen);
    }

    [Fact]
    public void A_Pepite_Export_Import_Keeps_The_Pairs()
    {
        var a = NewStore("a", @"{""words"":{""soundPairs"":{""fv"":true}}}", @"{""words"":{""soundPairs"":{""pb"":true,""chj"":false}}}");
        var path = Path.Combine(_root, "x" + ProfileArchive.Extension);
        ProfileArchive.Write(path, a.Store, a.Store.Profiles.Select(e => e.Id), false, a.Lock, "Pépite test", _log.Add);
        var b = NewStore("b", "{}", null);
        ProfileArchive.Apply(ProfileArchive.Plan(ProfileArchive.Read(path), b.Store, null, new ImportOptions()), b.Svc, b.Lock, _log.Add);
        var id = b.Store.Profiles.First(e => e.Profile.Name == "Léa").Id;
        var eff = SettingsLayering.Merge(MachineLayer.None, b.Store.ClassLayerFor(id), b.Store.Get(id)!.Profile.Layer).Effective;
        Assert.Equal(SoundPairs.FV | SoundPairs.PB, eff.Sounds.ToEngine());
    }

    private (ProfileStore Store, ProfileService Svc, TeacherLock Lock) NewStore(string name, string classLayer, string? lea)
    {
        var root = Path.Combine(_root, name);
        var store = new ProfileStore(root, Path.Combine(root, "sauvegardes"), _log.Add);
        store.Load();
        store.Add(Strings.ClassProfileName, new Avatar(null, ProfileColor.Vert), J(classLayer), Profile.ClassId);
        store.SetActive(Profile.ClassId);
        store.Delete(store.Profiles.First(e => e.Profile.Name == Strings.DefaultProfileName).Id);
        if (lea != null) store.Add("Léa", new Avatar("chat", ProfileColor.Orange), J(lea));
        var svc = new ProfileService(store, ProfileService.CreateSettings(store, MachineLayer.None, _log.Add), new Host(), _log.Add);
        var tf = new JsonFileStore(store.TeacherPath, _log.Add, "profiles", schema: 1);
        tf.Load();
        return (store, svc, new TeacherLock(tf, null, _log.Add));
    }
}
