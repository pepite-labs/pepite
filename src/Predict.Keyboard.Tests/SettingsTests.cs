// xUnit1031: blocking on a task (Wait / Result) is deliberate here: the test checks, synchronously, the state that the
// app's background write, load or worker leaves behind (no xUnit sync context, so no deadlock)
#pragma warning disable xUnit1031
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Settings model, settings.json store (corruption, atomic write, migration) and the D11 machine layer.</summary>
public class SettingsTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "kbsettings-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void Defaults()
    {
        var s = new AppSettings();
        Assert.Equal(8, s.WordCount);
        Assert.Equal(BarSize.M, s.Size);
        Assert.True(s.Pictures);
        Assert.Equal(KeyMode.F1F8, s.Keys);
        Assert.Equal(BarFont.SegoeUI, s.Font);
        Assert.Equal(LetterSpacing.Normal, s.Spacing);
        Assert.Equal(BarPosition.Caret, s.Position);
        Assert.True(s.Speech.SpeakInserted);
        Assert.True(s.Speech.SpeakWords && s.Speech.SpeakSentences);   // owner 2026-11-15 (0.8.3): echo on by default
        Assert.Equal(100, s.Speech.Volume);
        Assert.True(s.Speech.RereadEdits);   // D23: on by default
        Assert.False(s.Lock.Enabled);
        Assert.Equal(s, AppSettings.FromJson(null));
        Assert.Equal(s, AppSettings.FromJson(new JsonObject()));
    }

    [Fact]
    public void RereadEdits_Round_Trips_And_Can_Be_Locked_Off()
    {
        var off = new AppSettings() with { Speech = new SpeechSettings(RereadEdits: false) };
        var j = off.ToJson();
        Assert.Equal(false, (bool?)j["speech"]?["rereadEdits"]);
        Assert.False(AppSettings.FromJson(j).Speech.RereadEdits);
        Assert.False(AppSettings.FromJson(JsonNode.Parse(@"{""speech"":{""rereadEdits"":false}}") as JsonObject).Speech.RereadEdits);
        Assert.True(AppSettings.FromJson(JsonNode.Parse(@"{""speech"":{""rereadEdits"":""no""}}") as JsonObject).Speech.RereadEdits);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(9, 8)]
    [InlineData(-3, 1)]
    [InlineData(5, 5)]
    public void WordCount_Is_Clamped(int given, int expected) =>
        Assert.Equal(expected, AppSettings.FromJson(JsonNode.Parse($"{{\"words\":{{\"count\":{given}}}}}") as JsonObject).WordCount);

    [Fact]
    public void Out_Of_Range_And_Unknown_Values_Fall_Back()
    {
        var o = JsonNode.Parse(@"{""words"":{""size"":""XXL"",""keys"":7,""font"":""comic-sans"",""spacing"":""huge"",""pictures"":""yes""},
                                  ""bar"":{""position"":""left"",""free"":{""x"":5}},
                                  ""speech"":{""rate"":15,""volume"":-5,""voice"":""  ""}}") as JsonObject;
        var s = AppSettings.FromJson(o);
        Assert.Equal(BarSize.M, s.Size);
        Assert.Equal(KeyMode.F1F8, s.Keys);
        Assert.Equal(BarFont.SegoeUI, s.Font);
        Assert.Equal(LetterSpacing.Normal, s.Spacing);
        Assert.True(s.Pictures);
        Assert.Equal(BarPosition.Caret, s.Position);
        Assert.Null(s.FreeX);                       // half a position is no position
        Assert.Equal(10, s.Speech.Rate);
        Assert.Equal(0, s.Speech.Volume);
        Assert.Null(s.Speech.Voice);
        Assert.Equal(BarSize.M, (new AppSettings() with { Size = (BarSize)42 }).Validated().Size);   // unknown enum value
    }

    [Fact]
    public void Round_Trip_Every_Field_Including_Font_And_Spacing()
    {
        foreach (BarFont f in Enum.GetValues(typeof(BarFont)))
            foreach (LetterSpacing sp in Enum.GetValues(typeof(LetterSpacing)))
            {
                var s = new AppSettings(WordCount: 3, Size: BarSize.XL, Pictures: false, Keys: KeyMode.Off, Font: f, Spacing: sp,
                                        Position: BarPosition.Free, FreeX: -1200, FreeY: 40,
                                        Speech: new SpeechSettings(false, true, true, "sapi:Microsoft Hortense Desktop", -4, 55),
                                        Lock: new LockSettings(true, new PinHash("PBKDF2-SHA256", 100000, "c2FsdA==", "aGFzaA==")));
                Assert.Equal(s, AppSettings.FromJson(JsonNode.Parse(s.ToJson().ToJsonString()) as JsonObject));
            }
        var j = new AppSettings(Font: BarFont.OpenDyslexic, Spacing: LetterSpacing.Widest).ToJson();
        Assert.Equal("opendyslexic", (string?)j["words"]!["font"]);
        Assert.Equal("widest", (string?)j["words"]!["spacing"]);
        Assert.Equal("F1-F8", (string?)j["words"]!["keys"]);
    }

    [Fact]
    public void Store_Missing_File_Gives_Defaults_And_Save_Keeps_Unknown_Keys_Atomically()
    {
        var dir = TempDir();
        try
        {
            var st = new SettingsStore(dir, null);
            Assert.Empty(st.Load());
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                @"{""future"":{""a"":1},""words"":{""count"":4,""extra"":true},""state"":{""trayPromoted"":{""x.exe"":true},""other"":2}}");
            st = new SettingsStore(dir, null);
            var user = st.Load();
            Assert.Equal(4, AppSettings.FromJson(user).WordCount);
            Assert.Null(user["state"]);
            st.SaveUser(new AppSettings(WordCount: 6, Font: BarFont.Lexend).ToJson()).Wait();
            st.Flush();
            var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
            var root = JsonNode.Parse(text)!.AsObject();
            Assert.Equal(1, (int)root["future"]!["a"]!);
            Assert.True((bool)root["words"]!["extra"]!);
            Assert.Equal(6, (int)root["words"]!["count"]!);
            Assert.Equal("lexend", (string?)root["words"]!["font"]);
            Assert.Equal(2, (int)root["state"]!["other"]!);
            Assert.Equal(1, (int)root["schema"]!);
            Assert.True(st.IsPromoted("X.EXE"));
            Assert.False(File.Exists(Path.Combine(dir, "settings.json.tmp")));
            Assert.Equal(BarFont.Lexend, AppSettings.FromJson(new SettingsStore(dir, null).Load()).Font);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Corrupt_File_Is_Backed_Up_And_Replaced_By_Defaults()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ \"words\": { corrupt");
            var log = new List<string>();
            var st = new SettingsStore(dir, log.Add);
            Assert.Equal(new AppSettings(), AppSettings.FromJson(st.Load()));
            var bad = Directory.GetFiles(dir, "settings.json.bad-*");
            Assert.Single(bad);
            Assert.Equal("{ \"words\": { corrupt", File.ReadAllText(bad[0]));
            Assert.Contains(log, l => l.Contains("not valid JSON"));
            Assert.False(File.Exists(Path.Combine(dir, "settings.json")));
            st.SaveUser(new AppSettings(WordCount: 2).ToJson()).Wait();
            Assert.Equal(2, AppSettings.FromJson(new SettingsStore(dir, null).Load()).WordCount);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Migrates_State_Json_Then_Deletes_It()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "state.json"), @"{""trayPromoted"":{""C:\\kb\\PredictKeyboard.exe"":true}}");
            var log = new List<string>();
            var st = new SettingsStore(dir, log.Add);
            st.Load();
            Assert.False(File.Exists(Path.Combine(dir, "state.json")));
            Assert.True(st.IsPromoted(@"c:\kb\predictkeyboard.exe"));
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
            Assert.True((bool)root["state"]!["trayPromoted"]![@"C:\kb\PredictKeyboard.exe"]!);
            Assert.Contains(log, l => l.Contains("migrated"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Migration_Write_Failure_Keeps_State_Json()
    {
        if (!OperatingSystem.IsWindows()) return;   // read-only destination makes the rename fail on Windows
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var settings = Path.Combine(dir, "settings.json");
            File.WriteAllText(settings, "{}");
            File.SetAttributes(settings, FileAttributes.ReadOnly);
            File.WriteAllText(Path.Combine(dir, "state.json"), @"{""trayPromoted"":{""a.exe"":true}}");
            var log = new List<string>();
            new SettingsStore(dir, log.Add).Load();
            Assert.True(File.Exists(Path.Combine(dir, "state.json")));
            Assert.Contains(log, l => l.Contains("migration") && l.Contains("failed"));
            File.SetAttributes(settings, FileAttributes.Normal);
            new SettingsStore(dir, null).Load();                              // retried at the next start
            Assert.False(File.Exists(Path.Combine(dir, "state.json")));
        }
        finally { Cleanup(dir); }
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void SettingsPromotionState_Satisfies_TrayPromotion()
    {
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(dir, null);
            store.Load();
            var state = new SettingsPromotionState(store);
            const string exe = @"C:\Program Files\Prediction\PredictKeyboard.exe";
            Assert.False(state.IsPromoted(exe));
            state.MarkPromoted(exe);
            Assert.True(state.IsPromoted(exe.ToUpperInvariant()));
            store.Flush();                                                    // the mark is written in the background
            var reloaded = new SettingsStore(dir, null);
            reloaded.Load();
            Assert.True(new SettingsPromotionState(reloaded).IsPromoted(exe));
            Assert.False(reloaded.IsPromoted(@"D:\dev\PredictKeyboard.exe"));
        }
        finally { Cleanup(dir); }
    }

    // ------------------------------------------------------------------ D11 layering

    private static JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public void Layer_Order_Builtin_MachineDefaults_User_Locked()
    {
        var (s, locked) = SettingsLayering.Merge(J(@"{""words"":{""count"":5,""size"":""L""},""speech"":{""rate"":2}}"),
                                                 J(@"{""words"":{""size"":""S"",""font"":""lexend""}}"),
                                                 J(@"{""words"":{""size"":""XL""}}"));
        Assert.Equal(5, s.WordCount);             // machine default (user did not set it)
        Assert.Equal(BarSize.XL, s.Size);         // locked beats user
        Assert.Equal(BarFont.Lexend, s.Font);     // user beats machine default / builtin
        Assert.Equal(2, s.Speech.Rate);
        Assert.Equal(new[] { "words.size" }, locked.ToArray());
        Assert.True(Json.IsLocked(locked, "words.size"));
        Assert.True(Json.IsLocked(locked, "words"));
        Assert.False(Json.IsLocked(locked, "words.count"));
    }

    [Fact]
    public void Locked_User_Value_Is_Ignored_But_Kept_In_The_File()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"), @"{""words"":{""size"":""S"",""count"":3}}");
            var store = new SettingsStore(dir, null);
            var machine = new MachineLayer(null, J(@"{""words"":{""size"":""XL"",""font"":""andika""}}"));
            var (eff, locked) = SettingsLayering.Merge(machine, store.Load());
            Assert.Equal(BarSize.XL, eff.Size);
            Assert.Equal(BarFont.Andika, eff.Font);    // locked font
            Assert.Equal(3, eff.WordCount);
            store.ReplaceUser(SettingsLayering.NewUserLayer(store.UserLayer(), eff, eff with { WordCount = 7, Font = BarFont.Lexend }, machine, locked)).Wait();
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))!;
            Assert.Equal("S", (string?)root["words"]!["size"]);      // the user's own value stays in their file
            Assert.Null(root["words"]!["font"]);                     // locked values are never written
            Assert.Equal(7, (int)root["words"]!["count"]!);
        }
        finally { Cleanup(dir); }
    }

    private const string Admins = "S-1-5-32-544", SystemSid = "S-1-5-18", Users = "S-1-5-32-545", Pupil = "S-1-5-21-1-2-3-1001";
    private static FileSecurityInfo Sec(string owner, bool link = false, params AceInfo[] aces) => new(owner, link, aces);
    private static readonly AceInfo[] DefaultFileAces = { new(SystemSid, 0x1F01FF, true), new(Admins, 0x1F01FF, true), new(Users, 0x1200A9, true) };
    // ProgramData default on a sub-folder: Users may create files (WriteData | AppendData | WriteEA | WriteAttributes)
    private static readonly AceInfo[] DefaultDirAces = { new(SystemSid, 0x1F01FF, true), new(Admins, 0x1F01FF, true), new(Users, 0x1200A9, true), new(Users, 0x116, true) };

    [Fact]
    public void An_Untrusted_New_Machine_File_Does_Not_Hide_The_Trusted_Old_One()
    {
        var dir = TempDir();
        try
        {
            var now = Path.Combine(dir, "Pepite-Prediction", "settings.json");
            var old = Path.Combine(dir, "PredictKeyboard", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(now)!);
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            File.WriteAllText(now, @"{""schema"":1,""locked"":{""words"":{""size"":""S""}}}");      // planted by a pupil
            File.WriteAllText(old, @"{""schema"":1,""locked"":{""words"":{""size"":""XL""}}}");     // the administrator's
            var okDir = Sec(Admins, false, DefaultDirAces);
            FileSecurityInfo? Security(string p, bool isDir) =>
                isDir ? okDir : p == now ? Sec(Pupil, false, DefaultFileAces) : Sec(Admins, false, DefaultFileAces);
            var log = new List<string>();
            var m = MachineLayer.Load(new[] { now, old }, Security, log.Add);
            Assert.Equal(BarSize.XL, SettingsLayering.Merge(m, null).Effective.Size);
            Assert.Contains(log, l => l.StartsWith($"machine settings ignored (owner {Pupil})"));
            // a trusted new file wins over the old one; none at all → None
            FileSecurityInfo? AllAdmins(string p, bool isDir) => isDir ? okDir : Sec(Admins, false, DefaultFileAces);
            Assert.Equal(BarSize.S, SettingsLayering.Merge(MachineLayer.Load(new[] { now, old }, AllAdmins, null), null).Effective.Size);
            Assert.Same(MachineLayer.None, MachineLayer.Load(new[] { now + ".x", old + ".x" }, AllAdmins, null));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Machine_File_Needs_Trusted_Owners_No_Links_And_No_Foreign_Writers()
    {
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "machine.json");
            File.WriteAllText(path, @"{""schema"":1,""defaults"":{""words"":{""count"":5}},""locked"":{""words"":{""size"":""XL""}}}");
            MachineLayer Load(FileSecurityInfo f, FileSecurityInfo d, List<string>? log = null) =>
                MachineLayer.Load(path, (_, isDir) => isDir ? d : f, log == null ? null : log.Add);
            var okDir = Sec(Admins, false, DefaultDirAces);
            foreach (var sid in new[] { Admins, SystemSid })
            {
                var m = Load(Sec(sid, false, DefaultFileAces), okDir);
                Assert.Equal(5, (int)m.Defaults!["words"]!["count"]!);
                Assert.Equal(BarSize.XL, SettingsLayering.Merge(m, null).Effective.Size);
            }
            var log = new List<string>();
            Assert.Same(MachineLayer.None, Load(Sec(Pupil, false, DefaultFileAces), okDir, log));   // a standard user planted it
            Assert.Contains(log, l => l.StartsWith($"machine settings ignored (owner {Pupil})"));
            Assert.Same(MachineLayer.None, Load(Sec(Admins, false, DefaultFileAces), Sec(Pupil, false, DefaultDirAces)));        // folder owner
            Assert.Same(MachineLayer.None, Load(Sec(Admins, true, DefaultFileAces), okDir));                                    // file is a link
            Assert.Same(MachineLayer.None, Load(Sec(Admins, false, DefaultFileAces), Sec(Admins, true, DefaultDirAces)));        // folder is a link
            Assert.Same(MachineLayer.None, Load(Sec(Admins, false, DefaultFileAces.Append(new AceInfo(Users, 0x2, true)).ToArray()), okDir));   // users may write it
            Assert.Same(MachineLayer.None, Load(Sec(Admins, false, DefaultFileAces.Append(new AceInfo(Pupil, 0x1F01FF, true)).ToArray()), okDir));
            Assert.Same(MachineLayer.None, Load(Sec(Admins, false, DefaultFileAces), Sec(Admins, false, DefaultDirAces.Append(new AceInfo(Users, 0x40, true)).ToArray())));   // delete child
            Assert.NotSame(MachineLayer.None, Load(Sec(Admins, false, DefaultFileAces.Append(new AceInfo(Users, 0x2, false)).ToArray()), okDir)); // a deny is fine
            Assert.Same(MachineLayer.None, MachineLayer.Load(Path.Combine(dir, "none.json"), (_, _) => Sec(Admins), null));
            Assert.Same(MachineLayer.None, MachineLayer.Load(path, (_, _) => throw new UnauthorizedAccessException(), null));
            File.WriteAllText(path, "{ bad");
            Assert.Same(MachineLayer.None, Load(Sec(Admins, false, DefaultFileAces), okDir));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Saving_Or_Dragging_Does_Not_Freeze_Machine_Defaults()
    {
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(dir, null);
            var user = store.Load();
            var machine = new MachineLayer(J(@"{""words"":{""count"":5}}"), null);
            var (cur, locked) = SettingsLayering.Merge(machine, user);
            Assert.Equal(5, cur.WordCount);
            // the pupil changes the size, then drags the bar
            user = SettingsLayering.NewUserLayer(user, cur, cur with { Size = BarSize.L }, machine, locked);
            (cur, locked) = SettingsLayering.Merge(machine, user);
            user = SettingsLayering.NewUserLayer(user, cur, cur with { Position = BarPosition.Free, FreeX = 10, FreeY = 20 }, machine, locked);
            store.ReplaceUser(user).Wait();
            var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Assert.DoesNotContain("count", text);                              // the default of the day is not written
            Assert.DoesNotContain("speech", text);
            // the admin lowers the default: it reaches the pupil; their own choices stay
            var machine2 = new MachineLayer(J(@"{""words"":{""count"":3}}"), null);
            var (after, _) = SettingsLayering.Merge(machine2, new SettingsStore(dir, null).Load());
            Assert.Equal(3, after.WordCount);
            Assert.Equal(BarSize.L, after.Size);
            Assert.Equal((10, 20), (after.FreeX!.Value, after.FreeY!.Value));
            Assert.Equal(BarPosition.Free, after.Position);
            // choosing the default value again removes it from the file (later defaults apply again)
            var (c2, l2) = SettingsLayering.Merge(machine2, user);
            user = SettingsLayering.NewUserLayer(user, c2, c2 with { Size = BarSize.M }, machine2, l2);
            Assert.Null(Json.Get(user, "words.size").Node);
            Assert.False(Json.Get(user, "words.size").Found);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Unreadable_File_Is_Never_Overwritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, @"{""words"":{""count"":4}}");
            var log = new List<string>();
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))   // locked: cannot even be read
            {
                var st = new SettingsStore(dir, log.Add);
                Assert.Equal(new AppSettings(), AppSettings.FromJson(st.Load()));
                st.ReplaceUser(new AppSettings(WordCount: 2).ToJson()).Wait();
                st.MarkPromoted("x.exe").Wait();
                st.Flush();
            }
            Assert.Equal(@"{""words"":{""count"":4}}", File.ReadAllText(path));
            Assert.Contains(log, l => l.Contains("cannot read") && l.Contains("not be saved"));
            Assert.Contains(log, l => l.Contains("not saved"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Corrupt_File_That_Cannot_Be_Moved_Is_Never_Overwritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = TempDir();
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{ corrupt");
            var log = new List<string>();
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))   // another process holds it: no rename
            {
                var st = new SettingsStore(dir, log.Add);
                Assert.Equal(new AppSettings(), AppSettings.FromJson(st.Load()));
                st.ReplaceUser(new AppSettings(WordCount: 2).ToJson()).Wait();
                st.Flush();
            }
            Assert.Equal("{ corrupt", File.ReadAllText(path));
            Assert.Contains(log, l => l.Contains("could not be moved aside"));
            Assert.Contains(log, l => l.Contains("not saved"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Json_Without_Removes_Leaves_And_Empty_Parents()
    {
        var r = Json.Without(J(@"{""words"":{""size"":""S""},""bar"":{""free"":{""x"":1,""y"":2},""position"":""free""}}"),
                             new[] { "words.size", "bar.free.x", "nope.x" });
        Assert.Null(r["words"]);
        Assert.Equal(2, (int)r["bar"]!["free"]!["y"]!);
        Assert.Null(r["bar"]!["free"]!["x"]);
    }
}
