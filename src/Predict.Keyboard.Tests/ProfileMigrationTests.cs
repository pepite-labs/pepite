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

/// <summary>Plan 09 step 6: the one migration of 0.7.0 (old settings.json → « Mon profil ») and teacher management on/off.</summary>
public class ProfileMigrationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "kbmig-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_base, "Pepite-Prediction");
    private string Legacy => Path.Combine(_base, "PredictKeyboard");
    private readonly List<string> _log = new();

    public ProfileMigrationTests() => TeacherLock.Iterations = 1000;

    public void Dispose()
    {
        TestDirs.Delete(_base);
    }

    private void WriteLegacy(string json)
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "settings.json"), json);
    }

    /// <summary>Load, then wait for the writes it queued: a load that rebuilds the index saves it on a background task, and a
    /// test that then deletes or re-migrates the folder must not race that write (it holds <c>index.json.tmp</c> open and
    /// would recreate <c>profiles\</c> behind the test's back).</summary>
    private ProfileStore Load()
    {
        var s = new ProfileStore(Root, null, _log.Add);
        s.Load();
        s.Flush();
        return s;
    }

    private const string Old061 = @"{""schema"":1,""words"":{""count"":5,""size"":""L"",""font"":""andika""},
        ""speech"":{""voice"":""Microsoft Hortense Desktop"",""rate"":-2},""start"":{""welcome"":false},
        ""state"":{""trayPromoted"":{""C:\\kb\\Pepite.exe"":true}},""later"":{""k"":1}}";

    [Fact]
    public void Old_Settings_Become_Mon_Profil_And_The_File_Is_Kept()
    {
        WriteLegacy(Old061);
        Assert.Equal(ProfileMigration.Outcome.Migrated, ProfileMigration.Run(Root, Legacy, _log.Add));
        var s = Load();
        var p = s.Active.Profile;
        Assert.Equal(Strings.DefaultProfileName, p.Name);
        Assert.Single(s.Profiles);
        var eff = SettingsLayering.Merge(MachineLayer.None, p.Layer).Effective;
        Assert.Equal(5, eff.WordCount);
        Assert.Equal(BarSize.L, eff.Size);
        Assert.Equal(BarFont.Andika, eff.Font);
        Assert.Equal(-2, eff.Speech.Rate);
        Assert.False(eff.Welcome);
        Assert.Equal("sapi:Microsoft Hortense Desktop", eff.Speech.Voice);   // the voice-id migration is preserved
        Assert.Equal(1, (int)p.Layer["later"]!["k"]!);                        // unknown keys kept
        Assert.Null(p.Layer["state"]);
        Assert.Null(p.Layer["schema"]);
        // app state moved to the new settings.json
        var app = new SettingsStore(Root, null);
        app.Load();
        Assert.True(app.IsPromoted(@"c:\kb\pepite.exe"));
        Assert.False(app.ManagementEnabled);
        // the old file: renamed, content intact
        Assert.False(File.Exists(Path.Combine(Legacy, "settings.json")));
        Assert.Equal(Old061, File.ReadAllText(Path.Combine(Legacy, "settings.json.avant-profils")));
        // idempotent
        Assert.Equal(ProfileMigration.Outcome.AlreadyDone, ProfileMigration.Run(Root, Legacy, _log.Add));
        Assert.Single(Load().Profiles);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void A_Crash_Before_The_Index_Migrates_Again_From_The_Kept_Or_Original_File()
    {
        WriteLegacy(Old061);
        ProfileMigration.Run(Root, Legacy, _log.Add);
        // simulate a crash after the profile file, before the index: no index, but the profile folder exists
        File.Delete(ProfileStore.IndexPath(Root));
        Assert.Equal(ProfileMigration.Outcome.AlreadyDone, ProfileMigration.Run(Root, Legacy, _log.Add));
        var s = Load();                                  // the store rebuilds the index from the folder (flushed by Load)
        Assert.Single(s.Profiles);
        Assert.True(File.Exists(ProfileStore.IndexPath(Root)));
        Assert.Equal(5, SettingsLayering.Merge(MachineLayer.None, s.Active.Profile.Layer).Effective.WordCount);

        // a crash before the profile was written: nothing in profiles\, the old file maybe already renamed → read the kept copy
        Directory.Delete(Path.Combine(Root, ProfileStore.DirName), true);
        Assert.False(File.Exists(Path.Combine(Legacy, "settings.json")));
        Assert.Equal(ProfileMigration.Outcome.Migrated, ProfileMigration.Run(Root, Legacy, _log.Add));
        Assert.Equal(BarFont.Andika, SettingsLayering.Merge(MachineLayer.None, Load().Active.Profile.Layer).Effective.Font);
        Assert.True(File.Exists(Path.Combine(Legacy, "settings.json.avant-profils")));
    }

    [Fact]
    public void A_Bad_Source_Gives_Defaults_And_Is_Kept()
    {
        WriteLegacy("{ \"words\": broken");
        Assert.Equal(ProfileMigration.Outcome.Migrated, ProfileMigration.Run(Root, Legacy, _log.Add));
        var s = Load();
        Assert.Empty(s.Active.Profile.Layer);
        Assert.Equal("{ \"words\": broken", File.ReadAllText(Path.Combine(Legacy, "settings.json.avant-profils")));
        Assert.Contains(_log, l => l.Contains("not usable"));
    }

    [Fact]
    public void An_Unreadable_Old_File_Aborts_Without_An_Index_And_Retries()
    {
        if (!OperatingSystem.IsWindows()) return;
        WriteLegacy(Old061);
        using (new FileStream(Path.Combine(Legacy, "settings.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => ProfileMigration.Run(Root, Legacy, _log.Add));
            Assert.False(File.Exists(ProfileStore.IndexPath(Root)));
            // the start-up sequence does not throw: an in-memory « Mon profil » (defaults: the old file cannot be read)
            var (_, mem) = ProfileMigration.OpenStores(Root, Legacy, null, _log.Add);
            Assert.True(mem.InMemory);
            Assert.Single(mem.Profiles);
            Assert.False(File.Exists(ProfileStore.IndexPath(Root)));
        }
        // next start: the file is free again, the migration runs
        Assert.Equal(ProfileMigration.Outcome.Migrated, ProfileMigration.Run(Root, Legacy, _log.Add));
        Assert.Equal(5, SettingsLayering.Merge(MachineLayer.None, Load().Active.Profile.Layer).Effective.WordCount);
    }

    [Fact]
    public void An_Unusable_Folder_Does_Not_Throw_And_Writes_Nothing()
    {
        WriteLegacy(Old061);
        // a file where the folder should be: nothing can be created under it
        Directory.CreateDirectory(_base);
        File.WriteAllText(Root, "not a folder");
        var (app, profiles) = ProfileMigration.OpenStores(Root, Legacy, Path.Combine(_base, "backups"), _log.Add);
        Assert.True(profiles.InMemory);
        var p = Assert.Single(profiles.Profiles);
        Assert.Equal(5, SettingsLayering.Merge(MachineLayer.None, p.Profile.Layer).Effective.WordCount);   // the old settings
        profiles.SetLayer(p.Id, new JsonObject());                 // edits stay in memory, never thrown
        profiles.SetSession(DateTime.UtcNow, false);
        Assert.Null(profiles.Backup("test"));
        app.MarkPromoted("x.exe").Wait();
        profiles.Flush();
        Assert.Equal("not a folder", File.ReadAllText(Root));
        Assert.True(File.Exists(Path.Combine(Legacy, "settings.json")));   // the old file is not renamed: retried next start
        Assert.Contains(_log, l => l.Contains("in memory only"));
    }

    [Theory]
    [InlineData(null)]                                                     // no settings.json yet
    [InlineData(@"{""schema"":1,""state"":{""trayPromoted"":{""a.exe"":true}}}")]  // a valid one
    [InlineData("{ broken")]                                               // a bad one: not moved aside either
    public void An_In_Memory_Session_Writes_Nothing(string? existing)
    {
        TeacherLock.Iterations = 1000;
        WriteLegacy(Old061);
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, ProfileStore.DirName), "not a folder");   // only profiles\ is unusable
        var settings = Path.Combine(Root, SettingsStore.FileName);
        var state = Path.Combine(Root, SettingsStore.LegacyStateFile);
        if (existing != null) File.WriteAllText(settings, existing);
        File.WriteAllText(state, @"{""trayPromoted"":{""b.exe"":true}}");
        var before = Snapshot();

        var (app, profiles) = ProfileMigration.OpenStores(Root, Legacy, Path.Combine(_base, "backups"), _log.Add);
        Assert.True(profiles.InMemory);
        Assert.True(app.ReadOnly);
        app.MarkPromoted("x.exe").Wait();
        app.SetManagementEnabled(true).Wait();
        var tf = profiles.OpenTeacherFile();
        var lk = new TeacherLock(tf, null, _log.Add);
        lk.SetPin("480917");
        Assert.True(lk.HasPin);                    // in memory for this session
        lk.Flush(); app.Flush(); profiles.Flush();

        Assert.Equal(before, Snapshot());          // settings.json, state.json, the profiles file: byte-identical / absent
        Assert.False(File.Exists(ProfileStore.IndexPath(Root)));
        Assert.Empty(Directory.GetFiles(Root, "*.bad-*"));
        Assert.True(File.Exists(Path.Combine(Legacy, "settings.json")));     // the old file is not renamed
    }

    /// <summary>Every file under the test folder with its bytes (absent files simply missing).</summary>
    private string Snapshot() => string.Join("\n", Directory.GetFiles(_base, "*", SearchOption.AllDirectories).OrderBy(f => f)
        .Select(f => f + "=" + Convert.ToBase64String(File.ReadAllBytes(f))));

    [Fact]
    public void Fresh_Install_Creates_One_Empty_Profile()
    {
        Assert.Equal(ProfileMigration.Outcome.Fresh, ProfileMigration.Run(Root, Legacy, _log.Add));
        var s = Load();
        Assert.Single(s.Profiles);
        Assert.Empty(s.Active.Profile.Layer);
        Assert.False(Directory.Exists(Legacy));
    }

    [Fact]
    public void An_Old_Lock_Pin_Moves_To_Teacher_Json()
    {
        var h = TeacherLock.Hash("4826", 1000);
        WriteLegacy($@"{{""lock"":{{""enabled"":true,""pin"":{{""alg"":""{h.Alg}"",""iterations"":{h.Iterations},""salt"":""{h.Salt}"",""hash"":""{h.Hash}""}}}}}}");
        ProfileMigration.Run(Root, Legacy, _log.Add);
        var s = Load();
        Assert.Null(s.Active.Profile.Layer["lock"]);
        var tf = new JsonFileStore(s.TeacherPath, null);
        tf.Load();
        var lk = new TeacherLock(tf, null, null);
        Assert.True(lk.HasPin);
        Assert.Equal(PinOutcome.Ok, lk.Verify("4826").Outcome);
    }
}

/// <summary>Plan 09 step 6: teacher management on / off (D29 Q1, Q4).</summary>
public class TeacherModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kbteacher-" + Guid.NewGuid().ToString("N"));

    public TeacherModeTests() => TeacherLock.Iterations = 1000;

    public void Dispose()
    {
        TestDirs.Delete(_root);
    }

    private (SettingsStore App, ProfileStore Profiles, TeacherLock Lock, TeacherMode Mode) Open()
    {
        var app = new SettingsStore(_root, null);
        app.Load();
        var p = new ProfileStore(_root, null, null);
        p.Load();
        var tf = new JsonFileStore(p.TeacherPath, null, "profiles", schema: 1);
        tf.Load();
        var lk = new TeacherLock(tf, null, null);
        return (app, p, lk, new TeacherMode(app, p, lk, null));
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void Off_By_Default_Then_Activate_Creates_Pin_And_Classe()
    {
        var (app, p, lk, mode) = Open();
        Assert.False(mode.Enabled);
        Assert.Single(p.Profiles);
        var mine = p.ActiveId;
        var code = mode.Activate("4826");
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.True(mode.Enabled);
        Assert.NotNull(p.Class);
        Assert.Equal(2, p.Count);
        Assert.Equal(mine, p.ActiveId);              // nothing changes for the pupil
        Assert.Empty(p.Class!.Profile.Layer);
        app.Flush(); p.Flush(); lk.Flush();
        var (app2, p2, lk2, mode2) = Open();
        Assert.True(mode2.Enabled);
        Assert.Equal(PinOutcome.Ok, lk2.Verify("4826").Outcome);
        Assert.Equal(PinOutcome.Ok, lk2.Verify(code, recovery: true).Outcome);
    }

    [Fact]
    public void Deactivate_Keeps_Profiles_And_Active_And_Removes_The_Pin()
    {
        var (app, p, lk, mode) = Open();
        mode.Activate("4826");
        var tom = p.Add("Tom", Avatar.Default);
        p.SetActive(tom.Id);
        mode.Deactivate();
        Assert.False(mode.Enabled);
        Assert.False(lk.HasPin);
        Assert.Equal(3, p.Count);
        Assert.Equal(tom.Id, p.ActiveId);
        app.Flush(); p.Flush(); lk.Flush();
        var (_, p2, lk2, mode2) = Open();
        Assert.False(mode2.Enabled);
        Assert.Equal(3, p2.Count);
        // reactivate: the list is back, a new PIN is asked
        mode2.Activate("7391");
        Assert.True(mode2.Enabled);
        Assert.Equal(3, p2.Count);
        Assert.Equal(PinOutcome.Wrong, lk2.Verify("4826").Outcome);
        Assert.Equal(PinOutcome.Ok, lk2.Verify("7391").Outcome);
    }

    [Fact]
    public void Activate_Refuses_A_Weak_Pin()
    {
        var (_, p, _, mode) = Open();
        Assert.Throws<ArgumentException>(() => mode.Activate("1234"));
        Assert.False(mode.Enabled);
        Assert.Null(p.Class);
    }
}
