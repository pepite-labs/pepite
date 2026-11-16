using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 09 step 9: switching profiles in the running app, editing another profile, the session-start decision.</summary>
public class ProfileServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kbsvc-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();
    private readonly List<string> _calls = new();

    private sealed class Host : IProfileHost
    {
        private readonly List<string> _c;
        public Host(List<string> c) => _c = c;
        public void ResetForSwitch() => _c.Add("reset");
        public void CancelSpeech() => _c.Add("cancel");
        public void CloseProfileWindows() => _c.Add("close");
    }

    public void Dispose()
    {
        TestDirs.Delete(_root);
    }

    private static JsonObject J(string s) => JsonNode.Parse(s)!.AsObject();

    private (ProfileService Svc, ProfileStore Store, string Lea, string Tom, List<(AppSettings Old, AppSettings New)> Changes) Setup()
    {
        var store = new ProfileStore(_root, null, _log.Add);
        store.Load();
        store.Add(Strings.ClassProfileName, Avatar.Default, J(@"{""words"":{""font"":""andika""}}"), Profile.ClassId);
        var lea = store.Add("Léa", new Avatar("chat", ProfileColor.Orange), J(@"{""speech"":{""voice"":""piper:siwis""}}")).Id;
        var tom = store.Add("Tom", new Avatar("fusée", ProfileColor.Vert), J(@"{""words"":{""size"":""XL""},""speech"":{""voice"":""piper:pierre""}}")).Id;
        store.SetActive(lea);
        var settings = ProfileService.CreateSettings(store, MachineLayer.None, _log.Add);
        var changes = new List<(AppSettings, AppSettings)>();
        settings.Changed += (o, n) => changes.Add((o, n));
        var svc = new ProfileService(store, settings, new Host(_calls), _log.Add);
        return (svc, store, lea, tom, changes);
    }

    [Fact]
    public void Switch_Stops_The_Previous_Profile_Then_Applies_The_New_One()
    {
        var (svc, store, lea, tom, changes) = Setup();
        Assert.Equal(BarFont.Andika, svc.Settings.Current.Font);   // Léa inherits the class font
        Assert.True(svc.Switch(tom, "picker"));
        Assert.Equal(new[] { "reset", "cancel", "close" }, _calls);
        Assert.Equal(tom, store.ActiveId);
        var (old, now) = Assert.Single(changes);
        Assert.Equal("piper:siwis", old.Speech.Voice);
        Assert.Equal("piper:pierre", now.Speech.Voice);             // the speaker gets Configure → make-before-break
        Assert.Equal(BarSize.XL, now.Size);
        Assert.Equal(BarFont.Andika, now.Font);
        Assert.Contains(_log, l => l.Contains($"profile: switch {lea} -> {tom} (picker) applied in"));
        Assert.False(svc.Switch(tom, "again"));                      // already active: nothing stopped
        Assert.Equal(3, _calls.Count);
        store.Flush();
        var r = new ProfileStore(_root, null, null);
        r.Load();
        Assert.Equal(tom, r.ActiveId);
        Assert.DoesNotContain(_log, l => l.Contains("Léa") || l.Contains("Tom"));
    }

    [Fact]
    public void Apply_Saves_Into_The_Active_Profile_Only()
    {
        var (svc, store, lea, tom, _) = Setup();
        svc.Settings.Apply(svc.Settings.Current with { WordCount = 4 });
        Assert.Equal(4, (int)store.Get(lea)!.Profile.Layer["words"]!["count"]!);
        Assert.Null(store.Get(tom)!.Profile.Layer["words"]!["count"]);
        svc.Switch(tom, "test");
        svc.Settings.SetFreePosition(10, 20);
        Assert.Equal(10, (int)store.Get(tom)!.Profile.Layer["bar"]!["free"]!["x"]!);
        Assert.Null(store.Get(lea)!.Profile.Layer["bar"]);
    }

    [Fact]
    public void The_Teacher_Edits_Lea_While_Tom_Is_Active()
    {
        var (svc, store, lea, tom, changes) = Setup();
        svc.Switch(tom, "test");
        changes.Clear();
        var target = new ProfileEditTarget(svc, lea);
        Assert.Equal("Léa", target.ProfileName);
        Assert.Equal(BarSize.M, target.Current.Size);
        target.Apply(target.Current with { Size = BarSize.L, Font = BarFont.Andika });
        Assert.Equal("L", (string?)store.Get(lea)!.Profile.Layer["words"]!["size"]);
        Assert.Null(store.Get(lea)!.Profile.Layer["words"]!["font"]);   // equal to the class: inherited, not written
        Assert.Empty(changes);                                          // Tom unchanged (V11)
        Assert.Equal(BarSize.XL, svc.Settings.Current.Size);
    }

    [Fact]
    public void Editing_The_Class_Reaches_The_Active_Pupil()
    {
        var (svc, store, lea, _, changes) = Setup();
        var target = new ProfileEditTarget(svc, Profile.ClassId);
        target.Apply(target.Current with { Font = BarFont.Lexend, Spacing = LetterSpacing.Wider });
        Assert.Equal(BarFont.Lexend, svc.Settings.Current.Font);
        Assert.Equal(LetterSpacing.Wider, svc.Settings.Current.Spacing);
        Assert.Single(changes);
        Assert.Equal(lea, store.ActiveId);
    }

    [Fact]
    public void A_Switch_Ends_The_Pin_Grace()
    {
        var (svc, _, _, tom, _) = Setup();
        var now = new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
        var gate = new PinGate(() => true, () => new ProfilePolicy(), (_, ok) => ok(), () => now, null);
        svc.Gate = gate;
        gate.Granted();
        Assert.True(gate.InGrace);
        svc.Switch(tom, "picker");
        Assert.False(gate.InGrace);
    }

    [Fact]
    public void Unknown_Or_Unreadable_Profiles_Are_Refused()
    {
        var (svc, store, lea, _, _) = Setup();
        Assert.False(svc.Switch("p-zzzzzz", "test"));
        Assert.Equal(lea, store.ActiveId);
        Assert.Empty(_calls);
    }

    [Theory]
    // management, pupils (« Classe » not counted), class, picker on, saved logon (min), current logon (min) → new logon, return, picker
    [InlineData(false, 3, true, true, 0, 60, false, false, false)]   // management off: resume
    [InlineData(true, 3, true, true, 0, 60, true, true, true)]       // new logon: Classe + picker
    [InlineData(true, 3, true, true, 60, 60, false, false, false)]   // same logon (restart): resume
    [InlineData(true, 3, true, false, 0, 60, true, true, false)]     // picker off: Classe, no picker
    [InlineData(true, 1, false, true, 0, 60, true, false, false)]    // one profile: nothing to pick
    [InlineData(true, 1, true, true, 0, 60, true, true, false)]      // « Classe » + one pupil: back to Classe, no picker
    [InlineData(true, 2, true, true, 0, 60, true, true, true)]       // two pupils: the picker
    [InlineData(true, 3, true, true, -1, 60, true, true, true)]      // never saved: a new logon
    [InlineData(true, 3, true, true, 0, -1, false, false, false)]    // logon time unknown: resume
    public void Session_Start(bool on, int count, bool hasClass, bool picker, int saved, int current, bool newLogon, bool toClass, bool show)
    {
        var t0 = new DateTime(2026, 11, 14, 7, 0, 0, DateTimeKind.Utc);
        DateTime? S(int m) => m < 0 ? null : t0.AddMinutes(m);
        Assert.Equal(new SessionStartDecision(newLogon, toClass, show), SessionStart.Decide(on, count, hasClass, picker, S(saved), S(current)));
    }
}
