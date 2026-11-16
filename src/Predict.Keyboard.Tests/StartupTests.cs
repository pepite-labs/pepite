using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Start-up feedback (welcome window / notification / « déjà démarré ») and the single-instance guard.</summary>
[SupportedOSPlatform("windows")]
public class StartupTests
{
    [Fact]
    public void Second_Start_Signals_At_Once_When_The_Running_Copy_Listens()
    {
        int sleeps = 0;
        Assert.Equal(StartupPolicy.SecondStartOutcome.Signalled,
                     StartupPolicy.ResolveSecondStart(() => false, () => true, _ => sleeps++));
        Assert.Equal(0, sleeps);
    }

    [Fact]
    public void Second_Start_Retries_Until_The_Event_Exists()
    {
        // the winner creates its event a little after its mutex: the third signal gets through
        int calls = 0;
        Assert.Equal(StartupPolicy.SecondStartOutcome.Signalled,
                     StartupPolicy.ResolveSecondStart(() => false, () => ++calls >= 3, _ => { }));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Second_Start_Takes_Over_When_The_Other_Copy_Exits()
    {
        int tries = 0;
        Assert.Equal(StartupPolicy.SecondStartOutcome.Acquired,
                     StartupPolicy.ResolveSecondStart(() => ++tries >= 2, () => false, _ => { }));
    }

    [Fact]
    public void Second_Start_Gives_Up_After_The_Timeout()
    {
        int slept = 0;
        Assert.Equal(StartupPolicy.SecondStartOutcome.Failed,
                     StartupPolicy.ResolveSecondStart(() => false, () => false, ms => slept += ms, timeoutMs: 1000, stepMs: 250));
        Assert.Equal(1000, slept);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Reglages_Never_Writes_The_Profile_Welcome_Value(bool edited, bool current)
    {
        var e = new AppSettings(WordCount: 5, Welcome: edited);
        var saved = StartupPolicy.ForSave(e, new AppSettings(Welcome: current));
        Assert.Equal(current, saved.Welcome);   // the app-level flag is Réglages' business now
        Assert.Equal(5, saved.WordCount);       // the other edits are kept
    }

    [Theory]
    [InlineData(new string[0], "Manual")]
    [InlineData(new[] { "--log" }, "Manual")]
    [InlineData(new[] { "--autostart" }, "Autostart")]
    [InlineData(new[] { "--log", "--AUTOSTART" }, "Autostart")]
    public void Start_Kind_From_Args(string[] args, string expected) => Assert.Equal(expected, StartupPolicy.KindOf(args).ToString());

    [Theory]
    // manual start: the welcome window while start.welcome is on (it is turned off once shown); --no-welcome (scripts) never
    [InlineData(false, true, false, "WelcomeWindow")]
    [InlineData(false, false, false, "None")]
    [InlineData(false, true, true, "None")]
    // start with Windows: only the notification, whatever the welcome setting
    [InlineData(true, true, false, "ReadyNotification")]
    [InlineData(true, false, false, "ReadyNotification")]
    [InlineData(true, true, true, "ReadyNotification")]
    public void Feedback_At_Start(bool autostart, bool welcome, bool noWelcomeFlag, string expected) =>
        Assert.Equal(expected, StartupPolicy.AtStart(autostart ? StartKind.Autostart : StartKind.Manual, welcome, noWelcomeFlag).ToString());

    [Fact]
    public void Second_Start_Signals_Unless_Autostart()
    {
        // the « déjà démarré » window answers a user action: always (the checkbox does not apply)
        Assert.Equal(SecondStartAction.SignalRunning, StartupPolicy.OnSecondStart(StartKind.Manual));
        Assert.Equal(SecondStartAction.ExitSilently, StartupPolicy.OnSecondStart(StartKind.Autostart));
    }

    [Theory]
    // stored app-level flag, start.welcome of the profile active before any switch, machine lock -> migrate to « shown »?
    [InlineData(null, false, false, true)]    // an older « Ne plus afficher » (profile off, unlocked): recorded once
    [InlineData(null, true, false, false)]    // first start ever: not shown yet
    [InlineData(null, false, true, false)]    // a locked off is the machine's choice, not a « shown »
    [InlineData(true, false, false, false)]   // already recorded: never migrated again
    [InlineData(false, false, false, false)]  // Réglages « … au prochain démarrage » cleared it: a profile cannot undo that
    public void Welcome_Flag_Migration(bool? stored, bool setting, bool locked, bool expected) =>
        Assert.Equal(expected, StartupPolicy.MigrateWelcomeShown(stored, setting, locked));

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Welcome_Shown_Before_Is_The_App_Level_Flag(bool? stored, bool expected) =>
        Assert.Equal(expected, StartupPolicy.WelcomeShownBefore(stored));

    [Theory]
    [InlineData(false, true, false, true)]    // not shown yet -> due, whatever the profile says
    [InlineData(false, false, false, true)]
    [InlineData(true, true, false, false)]    // shown once -> never again (a « Classe » or pupil switch changes nothing)
    [InlineData(true, true, true, true)]      // machine lock on -> at every manual start
    [InlineData(false, false, true, false)]   // machine lock off -> never
    public void Welcome_Due(bool shownBefore, bool setting, bool locked, bool expected) =>
        Assert.Equal(expected, StartupPolicy.WelcomeDue(shownBefore, setting, locked));

    [Fact]
    public void Welcome_Shown_Is_Recorded_Unless_Locked()
    {
        Assert.True(StartupPolicy.WelcomeShownAfter(locked: false));
        Assert.Null(StartupPolicy.WelcomeShownAfter(locked: true));
    }

    [Fact]
    public void Welcome_Shown_Flag_Is_App_Level_State()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kbwelcome-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var st = new SettingsStore(dir, null);
            st.Load();
            Assert.Null(st.WelcomeShown);
            st.SetWelcomeShown(true).Wait();
            st.Flush();
            var again = new SettingsStore(dir, null);
            again.Load();
            Assert.True(again.WelcomeShown);
            // in « state », next to the tray promotion: not in the user / profile settings
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, SettingsStore.FileName)))!.AsObject();
            Assert.True((bool)root["state"]!["welcomeShown"]!);
            Assert.Null(root["start"]);
            again.SetWelcomeShown(false).Wait();
            again.Flush();
            var third = new SettingsStore(dir, null);
            third.Load();
            Assert.False(third.WelcomeShown);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Welcome_Setting_Defaults_On_And_Round_Trips()
    {
        Assert.True(new AppSettings().Welcome);
        Assert.True(AppSettings.FromJson(new JsonObject()).Welcome);
        var off = new AppSettings(Welcome: false);
        Assert.False((bool)off.ToJson()["start"]!["welcome"]!);
        Assert.False(AppSettings.FromJson(off.ToJson()).Welcome);
        Assert.True(AppSettings.FromJson(JsonNode.Parse(@"{""start"":{""welcome"":""no""}}") as JsonObject).Welcome);
    }

    [Fact]
    public void Help_Prefers_The_Embedded_Page_Then_The_ReadMe()
    {
        var dir = @"C:\app";
        string page = Path.Combine(dir, HelpLinks.WelcomePage), readme = Path.Combine(dir, HelpLinks.ReadMe);
        Assert.Equal(page, HelpLinks.Resolve(dir, p => p == page || p == readme));
        Assert.Equal(readme, HelpLinks.Resolve(dir, p => p == readme));
        Assert.Null(HelpLinks.Resolve(dir, _ => false));
    }

    [Fact]
    public void Os_Names_Carry_The_Unique_Prefix()
    {
        // owner 2026-11-14: « Pepite-Prediction », not « Pepite » (other software has that name)
        foreach (var n in new[] { SingleInstance.InstanceName, SingleInstance.ActivateName, SingleInstance.QuitName })
            Assert.StartsWith(@"Local\Pepite-Prediction.", n);
    }

    [Fact]
    public void Mutex_Admits_One_Owner_And_Is_Released_On_Dispose()
    {
        var name = @"Local\Pepite-Prediction.Test." + Guid.NewGuid().ToString("N");
        var first = SingleInstance.TryAcquire(null, name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(null, name));   // a second copy sees it
        first!.Dispose();
        using var again = SingleInstance.TryAcquire(null, name);
        Assert.NotNull(again);
    }

    [Fact]
    public void Signal_Sets_The_Event_Once_It_Exists()
    {
        var name = @"Local\Pepite-Prediction.Test." + Guid.NewGuid().ToString("N");
        Assert.False(SingleInstance.Signal(name));            // nobody listens: the caller falls back
        using var ev = SingleInstance.CreateEvent(name);
        Assert.False(ev.WaitOne(0));
        Assert.True(SingleInstance.Signal(name));
        Assert.True(ev.WaitOne(1000));
        Assert.False(ev.WaitOne(0));                           // auto-reset: one wake-up per second start
    }
}
