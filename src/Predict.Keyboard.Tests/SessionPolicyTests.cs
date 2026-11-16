using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 09B step 18: session returns (start, inactivity, Win+L, logoff) and « Profils proposés sur ce poste ».</summary>
public class SessionPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kbsess-" + Guid.NewGuid().ToString("N"));
    private const string Lea = "p-aaaaaa";

    public void Dispose()
    {
        TestDirs.Delete(_root);
    }

    private static SessionContext Ctx(ProfilePolicy? p = null, string active = Lea, bool on = true, bool hasClass = true, int offered = 3,
                                      bool busy = false) =>
        new(on, p ?? new ProfilePolicy(), active, hasClass, offered, busy);

    private static readonly ProfilePolicy Idle10 = new(IdleReturnMin: 10);
    private static readonly ProfilePolicy OnLock = new(ReturnOnLock: true);

    [Fact]
    public void Idle_Off_By_Default_Never_Returns()
    {
        var s = new SessionPolicy();
        Assert.False(s.OnIdle(TimeSpan.FromHours(5), Ctx()).Any);
    }

    [Fact]
    public void Idle_Returns_To_Class_With_The_Picker_After_The_Delay()
    {
        var s = new SessionPolicy();
        Assert.False(s.OnIdle(TimeSpan.FromMinutes(9.9), Ctx(Idle10)).Any);
        var a = s.OnIdle(TimeSpan.FromMinutes(10), Ctx(Idle10));
        Assert.True(a.ReturnToClass);
        Assert.True(a.ShowPicker);
        Assert.True(a.Secure);
        Assert.Equal("idle", a.Reason);
    }

    [Fact]
    public void Idle_Returns_Once_Per_Idle_Period()
    {
        var s = new SessionPolicy();
        Assert.True(s.OnIdle(TimeSpan.FromMinutes(11), Ctx(Idle10)).ReturnToClass);
        Assert.False(s.OnIdle(TimeSpan.FromMinutes(11.5), Ctx(Idle10)).Any);      // still idle: done already
        Assert.False(s.OnIdle(TimeSpan.FromSeconds(3), Ctx(Idle10)).Any);         // someone typed
        Assert.True(s.OnIdle(TimeSpan.FromMinutes(10), Ctx(Idle10)).ReturnToClass);
    }

    [Fact]
    public void Idle_On_Class_Closes_The_Teacher_Windows_Without_Switch_Or_Picker()
    {
        var s = new SessionPolicy();
        var a = s.OnIdle(TimeSpan.FromMinutes(30), Ctx(Idle10, active: Profile.ClassId));
        Assert.True(a.Secure);                                     // the Espace enseignant left open is closed, grace ended
        Assert.False(a.ReturnToClass);
        Assert.False(a.ShowPicker);                                // no picker for an empty room
        Assert.False(s.OnIdle(TimeSpan.FromMinutes(31), Ctx(Idle10, active: Profile.ClassId)).Any);   // once per idle period
    }

    [Fact]
    public void Idle_Off_Never_Secures()
    {
        var s = new SessionPolicy();
        Assert.False(s.OnIdle(TimeSpan.FromMinutes(90), Ctx(active: Profile.ClassId)).Any);
    }

    [Fact]
    public void Idle_Waits_While_A_Modal_Window_Is_Open()
    {
        var s = new SessionPolicy();
        Assert.False(s.OnIdle(TimeSpan.FromMinutes(15), Ctx(Idle10, busy: true)).Any);
        Assert.True(s.OnIdle(TimeSpan.FromMinutes(15.5), Ctx(Idle10)).ReturnToClass);   // the next poll, dialog closed
    }

    [Fact]
    public void Idle_Without_Management_Or_Class_Does_Nothing()
    {
        var s = new SessionPolicy();
        Assert.False(s.OnIdle(TimeSpan.FromMinutes(60), Ctx(Idle10, on: false)).Any);
        Assert.False(s.OnIdle(TimeSpan.FromMinutes(60), Ctx(Idle10, hasClass: false)).Any);
    }

    [Theory]
    [InlineData(1, true, false)]
    [InlineData(2, true, true)]
    [InlineData(5, false, false)]
    public void Picker_Needs_Two_Offered_Pupils_And_The_Switch(int offered, bool pickerOn, bool expected)
    {
        var s = new SessionPolicy();
        var a = s.OnIdle(TimeSpan.FromMinutes(20), Ctx(Idle10 with { SessionPicker = pickerOn }, offered: offered));
        Assert.True(a.ReturnToClass);
        Assert.Equal(expected, a.ShowPicker);
    }

    [Fact]
    public void Lock_Off_By_Default_Resumes()
    {
        var s = new SessionPolicy();
        Assert.False(s.OnLock(Ctx()).Any);
        Assert.False(s.OnUnlock(Ctx()).Any);
    }

    [Fact]
    public void Lock_Returns_To_Class_Then_Unlock_Shows_The_Picker()
    {
        var s = new SessionPolicy();
        var l = s.OnLock(Ctx(OnLock));
        Assert.True(l.ReturnToClass);
        Assert.True(l.Secure);
        Assert.False(l.ShowPicker);                               // nobody sees it on the lock screen
        var u = s.OnUnlock(Ctx(OnLock, active: Profile.ClassId));
        Assert.False(u.ReturnToClass);
        Assert.True(u.ShowPicker);
        Assert.False(s.OnUnlock(Ctx(OnLock, active: Profile.ClassId)).Any);   // once
    }

    [Fact]
    public void Lock_On_Class_Closes_The_Teacher_Windows_And_Offers_The_Picker_At_Unlock()
    {
        var s = new SessionPolicy();
        var l = s.OnLock(Ctx(OnLock, active: Profile.ClassId));
        Assert.True(l.Secure);                                     // Espace enseignant open on « Classe » + Win+L: closed
        Assert.False(l.ReturnToClass);
        Assert.False(l.ShowPicker);
        Assert.True(s.OnUnlock(Ctx(OnLock, active: Profile.ClassId)).ShowPicker);
    }

    [Fact]
    public void Lock_Off_Never_Secures()
    {
        var s = new SessionPolicy();
        Assert.False(s.OnLock(Ctx(active: Profile.ClassId)).Any);
    }

    [Fact]
    public void Lock_While_Busy_Returns_At_Unlock()
    {
        var s = new SessionPolicy();
        Assert.False(s.OnLock(Ctx(OnLock, busy: true)).Any);
        var u = s.OnUnlock(Ctx(OnLock));
        Assert.True(u.ReturnToClass);
        Assert.True(u.Secure);
        Assert.True(u.ShowPicker);
    }

    [Fact]
    public void Logoff_Always_Returns_To_Class_With_Management_On()
    {
        var s = new SessionPolicy();
        Assert.True(s.OnLogoff(Ctx()).ReturnToClass);              // the default policy: Q10 « at logoff: on »
        Assert.False(s.OnLogoff(Ctx(active: Profile.ClassId)).Any);
        Assert.False(s.OnLogoff(Ctx(on: false)).Any);              // single user: « Mon profil » stays
        Assert.False(s.OnLogoff(Ctx()).ShowPicker);
    }

    private sealed class Host : IProfileHost
    {
        public readonly List<string> Calls = new();
        public void ResetForSwitch() => Calls.Add("reset");
        public void CancelSpeech() => Calls.Add("cancel");
        public void CloseProfileWindows() => Calls.Add("close");
    }

    [Fact]
    public void Session_End_Switches_The_Running_App_So_A_Cancelled_Logoff_Stays_Coherent()
    {
        var s = Store("Léa", "Tom");
        var tom = Id(s, "Tom");
        s.SetActive(tom);
        var settings = ProfileService.CreateSettings(s, MachineLayer.None, null);
        var host = new Host();
        var svc = new ProfileService(s, settings, host, null);
        var logon = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
        svc.EndSession(new SessionPolicy().OnLogoff(Ctx(active: tom)), logon, "logoff");
        // the app and the store agree: the running settings target « Classe » (a real switch, windows closed)
        Assert.Equal(Profile.ClassId, s.ActiveId);
        Assert.Equal(Profile.ClassId, svc.Settings.Target);
        Assert.Contains("close", host.Calls);
        Assert.True(s.SessionEnded);
        // the logoff is cancelled: the app keeps running; deleting the former pupil and saving settings work
        svc.Delete(tom);
        svc.Settings.Apply(svc.Settings.Current with { WordCount = 4 });
        Assert.Equal(4, (int)s.Class!.Profile.Layer["words"]!["count"]!);
        // and the session returns still apply (« Classe » active: only the teacher windows are secured)
        var a = new SessionPolicy().OnIdle(TimeSpan.FromMinutes(11), Ctx(Idle10, active: s.ActiveId));
        Assert.True(a.Secure);
        Assert.False(a.ReturnToClass);
        s.Flush();                       // the settings write is still queued: a reader racing its rename sees the file as unreadable
        var r = new ProfileStore(_root, null, null);
        r.Load();
        Assert.Equal(Profile.ClassId, r.ActiveId);
    }

    [Fact]
    public void Session_End_Without_Management_Only_Marks_The_Session()
    {
        var s = Store("Léa");
        var lea = Id(s, "Léa");
        s.SetActive(lea);
        var svc = new ProfileService(s, ProfileService.CreateSettings(s, MachineLayer.None, null), new Host(), null);
        svc.EndSession(new SessionPolicy().OnLogoff(Ctx(on: false, active: lea)), null, "shutdown");
        Assert.Equal(lea, s.ActiveId);
        Assert.True(s.SessionEnded);
    }

    private ProfileStore Store(params string[] pupils)
    {
        var s = new ProfileStore(_root, null, null) { MachineName = "salle-1" };
        s.Load();
        s.Add(Strings.ClassProfileName, Avatar.Default, new JsonObject(), Profile.ClassId);
        foreach (var p in pupils) s.Add(p, Avatar.Default);
        return s;
    }

    private static string Id(ProfileStore s, string name) => s.Profiles.First(e => e.Profile.Name == name).Id;

    [Fact]
    public void Offered_Here_Filters_The_Picker_Per_Pc_And_Persists()
    {
        var s = Store("Léa", "Tom", "Zoé");
        Assert.Equal(4, SessionPolicy.Offered(s).Count);           // « Mon profil » + 3, no list: everybody
        s.SetPolicy(s.Policy.WithOffered(s.MachineName, new[] { Id(s, "Léa"), Id(s, "Tom") }));
        Assert.Equal(new[] { "Léa", "Tom" }, SessionPolicy.Offered(s).Select(e => e.Profile.Name));
        s.Flush();
        var other = new ProfileStore(_root, null, null) { MachineName = "SALLE-2" };
        other.Load();
        Assert.Equal(4, SessionPolicy.Offered(other).Count);        // another PC: its own (absent) list
        var same = new ProfileStore(_root, null, null) { MachineName = "Salle-1" };
        same.Load();
        Assert.Equal(2, SessionPolicy.Offered(same).Count);         // the name ignoring case
        Assert.Contains("\"SALLE-1\"", File.ReadAllText(ProfileStore.IndexPath(_root)));
    }

    [Fact]
    public void Offered_Here_Adds_New_Pupils_And_Never_Empties_The_Picker()
    {
        var s = Store("Léa", "Tom");
        s.SetPolicy(s.Policy.WithOffered(s.MachineName, new[] { Id(s, "Léa") }));
        var tim = s.Add("Tim", Avatar.Default).Id;
        Assert.Contains(tim, s.Policy.OfferedOn(s.MachineName)!);   // a new pupil is offered here
        s.SetPolicy(s.Policy.WithOffered(s.MachineName, new[] { "p-zzzzzz" }));   // only a deleted one
        Assert.Equal(4, SessionPolicy.Offered(s).Count);           // fallback: everybody
        s.SetPolicy(s.Policy.WithOffered(s.MachineName, null));
        Assert.Null(s.Policy.OfferedHere);
    }

    [Fact]
    public void At_Start_Counts_Offered_Pupils_Only()
    {
        var s = Store("Léa", "Tom", "Zoé");
        var old = new DateTime(2026, 11, 13, 8, 0, 0, DateTimeKind.Utc);
        s.SetSession(old, true);
        var now = old.AddDays(1);
        Assert.True(SessionPolicy.AtStart(true, s, now).ShowPicker);
        s.SetPolicy(s.Policy.WithOffered(s.MachineName, new[] { Id(s, "Léa") }));
        var d = SessionPolicy.AtStart(true, s, now);
        Assert.True(d.ReturnToClass);
        Assert.False(d.ShowPicker);                                // one pupil offered here: nothing to choose
        Assert.False(SessionPolicy.AtStart(true, s, old).NewLogon); // same logon: resume
    }

    [Fact]
    public void Policy_Json_Round_Trip_And_Tolerant_Parse()
    {
        var p = new ProfilePolicy(IdleReturnMin: 20, ReturnOnLock: true).WithOffered("pc", new[] { Lea });
        var back = ProfilePolicy.FromJson(JsonNode.Parse(p.ToJson().ToJsonString()));
        Assert.Equal(20, back.IdleReturnMin);
        Assert.True(back.ReturnOnLock);
        Assert.Equal(new[] { Lea }, back.OfferedOn("PC"));
        var bad = ProfilePolicy.FromJson(JsonNode.Parse(@"{""idleReturnMin"":7,""offeredHere"":{""pc"":[""../x"",""classe"",3,""p-bbbbbb""]}}"));
        Assert.Equal(0, bad.IdleReturnMin);                       // only 0 / 10 / 20 / 30 / 60
        Assert.Equal(new[] { "p-bbbbbb" }, bad.OfferedOn("pc"));
    }
}
