using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan « aide intégrée »: where each help entry point leads, the fallback chain, the tray menu order, and the
/// repository's help pages against <see cref="HelpLinks"/>.</summary>
public class HelpTests
{
    private const string Dir = @"C:\app";

    [Fact]
    public void Candidate_Chain_Is_Stub_Page_Home_ReadMe()
    {
        Assert.Equal(new[] { @"aide\aller\reglages-voix.html", @"aide\reglages.html", HelpLinks.HomePage, HelpLinks.ReadMe },
                     HelpLinks.Candidates(HelpTopic.SettingsSpeech));
        Assert.Equal(new[] { HelpLinks.WelcomePage, HelpLinks.HomePage, HelpLinks.ReadMe }, HelpLinks.Candidates(HelpTopic.Welcome));
        Assert.Equal(new[] { HelpLinks.HomePage, HelpLinks.ReadMe }, HelpLinks.Candidates(HelpTopic.Home));   // no duplicate
    }

    [Fact]
    public void Every_Target_Is_Under_Aide()
    {
        foreach (HelpTopic t in Enum.GetValues(typeof(HelpTopic)))
        {
            var (page, stub) = HelpLinks.Target(t);
            Assert.StartsWith(@"aide\", page);
            Assert.EndsWith(".html", page);
            if (stub != null) Assert.StartsWith(@"aide\aller\", stub);
        }
    }

    [Fact]
    public void Resolve_Falls_Back_Along_The_Chain()
    {
        string P(string rel) => Path.Combine(Dir, rel);
        var all = new HashSet<string> { P(@"aide\aller\reglages-mots.html"), P(@"aide\reglages.html"), P(HelpLinks.HomePage), P(HelpLinks.ReadMe) };
        Assert.Equal(P(@"aide\aller\reglages-mots.html"), HelpLinks.Resolve(Dir, HelpTopic.SettingsWords, all.Contains));
        all.Remove(P(@"aide\aller\reglages-mots.html"));
        Assert.Equal(P(@"aide\reglages.html"), HelpLinks.Resolve(Dir, HelpTopic.SettingsWords, all.Contains));
        all.Remove(P(@"aide\reglages.html"));
        Assert.Equal(P(HelpLinks.HomePage), HelpLinks.Resolve(Dir, HelpTopic.SettingsWords, all.Contains));
        all.Remove(P(HelpLinks.HomePage));
        Assert.Equal(P(HelpLinks.ReadMe), HelpLinks.Resolve(Dir, HelpTopic.SettingsWords, all.Contains));
        Assert.Null(HelpLinks.Resolve(Dir, HelpTopic.SettingsWords, _ => false));
    }

    [Fact]
    public void Old_Resolve_Overload_Is_The_Welcome_Page()
    {
        string page = Path.Combine(Dir, HelpLinks.WelcomePage), readme = Path.Combine(Dir, HelpLinks.ReadMe);
        Assert.Equal(page, HelpLinks.Resolve(Dir, p => p == page || p == readme));
        Assert.Equal(readme, HelpLinks.Resolve(Dir, p => p == readme));
        Assert.Null(HelpLinks.Resolve(Dir, _ => false));
        foreach (var exists in new Func<string, bool>[] { p => p == page || p == readme, p => p == readme, _ => true, _ => false })
            Assert.Equal(HelpLinks.Resolve(Dir, HelpTopic.Welcome, exists), HelpLinks.Resolve(Dir, exists));
    }

    [Theory]
    [InlineData("mots", false, "SettingsWords")]
    [InlineData("position", false, "SettingsPosition")]
    [InlineData("voix", false, "SettingsSpeech")]
    [InlineData("lecture", false, "SettingsReading")]
    [InlineData("demarrage", false, "SettingsStart")]
    [InlineData("eleves", false, "Pupils")]
    [InlineData("orthographe", true, "Spelling")]
    [InlineData("mes-mots", true, "MyWords")]
    [InlineData("sons", true, "SettingsSounds")]     // D33: « Sons que l'élève confond » on the Mots tab
    [InlineData(null, false, "Settings")]        // Aperçu, the Enregistrer / Annuler buttons
    [InlineData(null, true, "MyWords")]          // elsewhere on the « Mots » tab
    [InlineData("inconnu", false, "Settings")]
    public void Settings_Section_Maps_To_A_Topic(string? section, bool wordsTab, string expected) =>
        Assert.Equal(expected, HelpLinks.ForSettings(section, wordsTab).ToString());

    [Fact]
    public void Tray_Menu_Puts_Help_Right_Before_About()
    {
        Assert.Equal(new[] { TrayItem.Pause, TrayItem.Separator, TrayItem.AddWord, TrayItem.Settings, TrayItem.Help, TrayItem.About, TrayItem.Quit },
                     TrayMenu.MenuItems(management: false, addWord: true, help: true));
        Assert.Equal(new[] { TrayItem.Profile, TrayItem.SwitchProfile, TrayItem.Separator, TrayItem.Pause, TrayItem.Separator, TrayItem.AddWord,
                             TrayItem.Settings, TrayItem.Teacher, TrayItem.Help, TrayItem.About, TrayItem.Quit },
                     TrayMenu.MenuItems(management: true, addWord: true, help: true));
        Assert.DoesNotContain(TrayItem.Help, TrayMenu.MenuItems(false, true, help: false));
        Assert.Equal("Aide", Strings.MenuHelp);
    }

    [Fact]
    public void Open_Starts_The_First_Existing_Candidate_And_Logs_Relative_Paths()
    {
        var started = new List<string>();
        var log = new List<string>();
        bool ok = HelpLinks.Open(HelpTopic.SettingsSpeech, log.Add, Dir, p => !p.EndsWith("reglages-voix.html"), p => { started.Add(p); return true; });
        Assert.True(ok);
        Assert.Equal(new[] { Path.Combine(Dir, @"aide\reglages.html") }, started);
        Assert.All(log, l => Assert.DoesNotContain(Dir, l));
    }

    [Fact]
    public void Open_Skips_The_Other_Html_Candidates_After_A_Win32Exception()
    {
        var started = new List<string>();
        bool ok = HelpLinks.Open(HelpTopic.SettingsSpeech, null, Dir, _ => true, p =>
        {
            started.Add(p);
            if (p.EndsWith(".html")) throw new Win32Exception(1155);   // ERROR_NO_ASSOCIATION
            return true;
        });
        Assert.True(ok);
        Assert.Equal(new[] { Path.Combine(Dir, @"aide\aller\reglages-voix.html"), Path.Combine(Dir, HelpLinks.ReadMe) }, started);
    }

    [Fact]
    public void Open_Stops_When_The_User_Cancels_Open_With()
    {
        var started = new List<string>();
        bool ok = HelpLinks.Open(HelpTopic.Home, null, Dir, _ => true, p =>
        {
            started.Add(p);
            throw new Win32Exception(1223);   // ERROR_CANCELLED: « Ouvrir avec » closed
        });
        Assert.True(ok);                                    // handled: no LISEZ-MOI, no « aide absente » message
        Assert.Equal(new[] { Path.Combine(Dir, HelpLinks.HomePage) }, started);
    }

    [Fact]
    public void Open_Says_False_When_Nothing_Opens()
    {
        Assert.False(HelpLinks.Open(HelpTopic.Home, null, Dir, _ => false, _ => throw new InvalidOperationException("not called")));
        Assert.False(HelpLinks.Open(HelpTopic.Home, null, Dir, _ => true, _ => throw new Win32Exception(2)));
    }

    // ------------------------------------------------------------------ the repository's help

    private static string AppLikeDir()
    {
        // the repository laid out like the app folder: freeware\aide is the app's aide\
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "aide", "index.html")) && Directory.Exists(Path.Combine(d.FullName, "src"))) return d.FullName;
        throw new DirectoryNotFoundException("freeware\\aide not found above " + AppContext.BaseDirectory);
    }

    private static readonly Regex Refresh = new(@"http-equiv=""refresh"" content=""0; url=\.\./([a-z0-9-]+\.html)#([a-z0-9-]+)""");

    [Fact]
    public void Every_Topic_Has_Its_Page_And_Stub_And_The_Stub_Anchor_Exists()
    {
        var root = AppLikeDir();
        foreach (HelpTopic t in Enum.GetValues(typeof(HelpTopic)))
        {
            var (page, stub) = HelpLinks.Target(t);
            Assert.True(File.Exists(Path.Combine(root, page)), page);
            if (stub is null) continue;
            var html = File.ReadAllText(Path.Combine(root, stub));
            var m = Refresh.Match(html);
            Assert.True(m.Success, stub + ": no redirect");
            Assert.Equal(Path.GetFileName(page), m.Groups[1].Value);
            var target = File.ReadAllText(Path.Combine(root, "aide", m.Groups[1].Value));
            Assert.Contains($"id=\"{m.Groups[2].Value}\"", target);
        }
    }

    [Fact]
    public void Settings_Sections_In_Xaml_Map_To_Topics()
    {
        // every « ? » of Réglages (aide:<name> tags) leads to a topic other than the default page
        var xaml = File.ReadAllText(Path.Combine(AppLikeDir(), "src", "Predict.Keyboard", "SettingsWindow.xaml"));
        var names = Regex.Matches(xaml, @"Tag=""aide:([a-z-]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(6, names.Count);
        Assert.All(names, n => Assert.NotEqual(HelpTopic.Settings, HelpLinks.ForSettings(n, false)));
    }
}
