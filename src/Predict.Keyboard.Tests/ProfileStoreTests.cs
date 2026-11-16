using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 09 step 5: the profiles folder (index, one file per profile, lost index, unreadable profile, backups).</summary>
public class ProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kbprofiles-" + Guid.NewGuid().ToString("N"));
    private string Backups => Path.Combine(_root, "backups");
    private readonly List<string> _log = new();
    private DateTime _now = new(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        TestDirs.Delete(_root);
    }

    private ProfileStore Open()
    {
        var s = new ProfileStore(_root, Backups, _log.Add, () => _now);
        s.Load();
        return s;
    }

    [Fact]
    public void Empty_Folder_Starts_Single_User_With_Mon_Profil()
    {
        var s = Open();
        Assert.Single(s.Profiles);
        Assert.Equal(Strings.DefaultProfileName, s.Active.Profile.Name);
        Assert.Null(s.Class);
        s.Flush();
        Assert.True(File.Exists(ProfileStore.IndexPath(_root)));
    }

    [Fact]
    public void Add_Rename_Reorder_Delete_Persist()
    {
        var s = Open();
        var first = s.ActiveId;
        var cls = s.Add(Strings.ClassProfileName, Avatar.Default, new JsonObject(), Profile.ClassId);
        var lea = s.Add("Léa", new Avatar("chat", ProfileColor.Orange));
        var tom = s.Add("Tom", new Avatar("fusée", ProfileColor.Vert), JsonNode.Parse(@"{""words"":{""size"":""XL""}}")!.AsObject());
        Assert.Equal(Profile.ClassId, s.Profiles[0].Id);                    // « Classe » first
        Assert.Throws<ArgumentException>(() => s.Add("léa", Avatar.Default)); // unique ignoring case
        s.Rename(lea.Id, "Léa B");
        Assert.True(s.Move(tom.Id, -1));
        Assert.False(s.Move(tom.Id, -5));
        Assert.False(s.Move(Profile.ClassId, 1));
        Assert.Throws<InvalidOperationException>(() => s.Delete(Profile.ClassId));
        Assert.Throws<InvalidOperationException>(() => s.Delete(first));     // the active one
        s.SetActive(tom.Id);
        s.Delete(first);
        s.Flush();

        var r = Open();
        Assert.Equal(new[] { Profile.ClassId, tom.Id, lea.Id }, r.Profiles.Select(e => e.Id));
        Assert.Equal(tom.Id, r.ActiveId);
        Assert.Equal("Léa B", r.Get(lea.Id)!.Profile.Name);
        Assert.Equal("XL", (string?)r.Get(tom.Id)!.Profile.Layer["words"]!["size"]);
        Assert.Equal(new Avatar("fusée", ProfileColor.Vert), r.Get(tom.Id)!.Profile.Avatar);
        Assert.False(Directory.Exists(Path.Combine(r.ProfilesDir, first)));
        Assert.NotEmpty(Directory.GetFiles(Backups, "profils-*-suppression.zip"));
        Assert.DoesNotContain(_log, l => l.Contains("Léa") || l.Contains("Tom"));   // ids only in logs
    }

    [Fact]
    public void Duplicate_Is_An_Independent_Copy()
    {
        var s = Open();
        var lea = s.Add("Léa", Avatar.Default, JsonNode.Parse(@"{""words"":{""font"":""andika""}}")!.AsObject());
        var copy = s.Duplicate(lea.Id, "Léa 2");
        s.SetLayer(lea.Id, new JsonObject());
        Assert.Equal("andika", (string?)s.Get(copy.Id)!.Profile.Layer["words"]!["font"]);
        Assert.Equal(s.Profiles.ToList().FindIndex(e => e.Id == lea.Id) + 1, s.Profiles.ToList().FindIndex(e => e.Id == copy.Id));
    }

    [Fact]
    public void Forty_Profiles_At_Most()
    {
        var s = Open();
        for (int i = s.Count; i < Profile.MaxProfiles; i++) s.Add("Élève " + i, Avatar.Default);
        Assert.False(s.CanAdd);
        Assert.Throws<InvalidOperationException>(() => s.Add("Encore", Avatar.Default));
    }

    [Fact]
    public void Lost_Index_Is_Rebuilt_From_The_Folders()
    {
        var s = Open();
        s.Add(Strings.ClassProfileName, Avatar.Default, null, Profile.ClassId);
        var lea = s.Add("Léa", Avatar.Default);
        s.SetActive(lea.Id);
        s.Flush();
        File.Delete(ProfileStore.IndexPath(_root));
        var r = Open();
        Assert.Equal(3, r.Count);
        Assert.Equal(Profile.ClassId, r.Profiles[0].Id);
        Assert.Equal(Profile.ClassId, r.ActiveId);   // unknown active → « Classe »
        r.Flush();
        Assert.True(File.Exists(ProfileStore.IndexPath(_root)));

        File.WriteAllText(ProfileStore.IndexPath(_root), "{ broken");
        var c = Open();
        Assert.Equal(3, c.Count);
        Assert.Contains(_log, l => l.Contains("index rebuilt"));
        Assert.NotEmpty(Directory.GetFiles(c.ProfilesDir, "index.json.bad-*"));
    }

    [Fact]
    public void An_Unreadable_Profile_Is_Listed_Kept_And_Not_Activated()
    {
        var s = Open();
        var lea = s.Add("Léa", Avatar.Default);
        var tom = s.Add("Tom", Avatar.Default);
        s.SetActive(tom.Id);
        s.Flush();
        var path = Path.Combine(s.ProfilesDir, tom.Id, ProfileStore.ProfileFile);
        File.WriteAllText(path, "{ not json");
        var r = Open();
        var t = r.Get(tom.Id)!;
        Assert.True(t.Unreadable);
        Assert.Equal(Strings.ProfileUnreadable, t.Profile.Name);
        Assert.NotEqual(tom.Id, r.ActiveId);
        Assert.Throws<ArgumentException>(() => r.SetActive(tom.Id));
        Assert.Throws<InvalidOperationException>(() => r.Rename(tom.Id, "X"));
        r.Rename(lea.Id, "Léa C");                                         // the others work
        r.Flush();
        Assert.Equal("{ not json", File.ReadAllText(path));                // never overwritten
        r.Delete(tom.Id);                                                  // can be deleted
        Assert.Null(r.Get(tom.Id));
    }

    [Fact]
    public void Policy_And_Session_Round_Trip()
    {
        var s = Open();
        Assert.Equal(new ProfilePolicy(), s.Policy);
        Assert.False(s.Policy.PinToSwitch);
        Assert.True(s.Policy.PinToEdit);
        Assert.False(s.Policy.PinToQuit);
        Assert.True(s.Policy.SessionPicker);
        s.SetPolicy(new ProfilePolicy(PinToSwitch: true, SessionPicker: false));
        var t = new DateTime(2026, 11, 14, 7, 59, 1, 250, DateTimeKind.Utc);
        s.SetSession(t, true);
        s.Flush();
        var r = Open();
        Assert.True(r.Policy.PinToSwitch);
        Assert.False(r.Policy.SessionPicker);
        Assert.Equal(t, r.SessionLogonUtc);
        Assert.True(r.SessionEnded);
    }

    [Fact]
    public void Backups_Daily_And_Five_Kept_Per_Reason()
    {
        var s = Open();
        s.Add("Léa", Avatar.Default);
        Assert.True(s.BackupIfDue());
        Assert.False(s.BackupIfDue());                  // not twice a day
        for (int i = 0; i < 7; i++) { _now = _now.AddMinutes(1); Assert.NotNull(s.Backup("test")); }
        Assert.Equal(ProfileStore.KeepBackups, Directory.GetFiles(Backups, "profils-*-test.zip").Length);
        Assert.Single(Directory.GetFiles(Backups, "profils-*-jour.zip"));   // 7 other backups never push out the daily one
        var newest = Directory.GetFiles(Backups, "profils-*-test.zip").OrderBy(f => f).Last();
        using var z = ZipFile.OpenRead(newest);
        Assert.Contains(z.Entries, e => e.FullName == "index.json");
        Assert.Contains(z.Entries, e => e.FullName.EndsWith("/profile.json"));
    }
}
