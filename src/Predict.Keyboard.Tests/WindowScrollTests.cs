using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.11.1: Réglages and the Espace enseignant are capped at the work area, so every tab must scroll — at
/// 1920×1080 with a taskbar the last sections of « Barre et voix » were under the button row. The test project has no
/// WPF, so this reads the sources (the layout itself was checked in the test VM).</summary>
public class WindowScrollTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string SrcDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "src", "Predict.Keyboard", "SettingsWindow.xaml"))) return Path.Combine(d.FullName, "src", "Predict.Keyboard");
        throw new DirectoryNotFoundException("src\\Predict.Keyboard not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Settings_Tabs_Scroll_And_Buttons_Stay_Outside()
    {
        var doc = XDocument.Load(Path.Combine(SrcDir(), "SettingsWindow.xaml"));
        var tabs = doc.Descendants(Wpf + "TabItem").ToList();
        Assert.Equal(2, tabs.Count);
        // a tab declared with content wraps it in a ScrollViewer that is not a tab stop; the « Mots » tab is filled in code
        foreach (var t in tabs.Where(t => t.Elements().Any(e => !e.Name.LocalName.Contains('.'))))
        {
            var content = t.Elements().Single(e => !e.Name.LocalName.Contains('.'));
            Assert.Equal("ScrollViewer", content.Name.LocalName);
            Assert.Equal("False", (string?)content.Attribute("Focusable"));
            Assert.Equal("Disabled", (string?)content.Attribute("HorizontalScrollBarVisibility"));
        }
        // Enregistrer / Annuler are docked outside the tabs (always visible)
        var save = doc.Descendants(Wpf + "Button").Single(b => (string?)b.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == "SaveButton");
        Assert.DoesNotContain(save.Ancestors(), a => a.Name == Wpf + "TabControl");

        var code = File.ReadAllText(Path.Combine(SrcDir(), "SettingsWindow.xaml.cs"));
        Assert.Matches(new Regex(@"WordsTab\.Content\s*=\s*Ui\.Scroll\("), code);
        Assert.Matches(new Regex(@"Ui\.ForwardWheel\(\s*GeneralScroll\s*\)"), code);
        Assert.Contains("Ui.FitToWorkArea(this);", code);
    }

    [Fact]
    public void Teacher_Tabs_Scroll()
    {
        var code = File.ReadAllText(Path.Combine(SrcDir(), "TeacherWindow.cs"));
        Assert.Contains("Ui.FitToWorkArea(this);", code);
        // every tab goes through Tab(...), which wraps its content
        Assert.Matches(new Regex(@"new TabItem\s*\{[^}]*Content\s*=\s*Ui\.Scroll\(content\)"), code);
        Assert.Single(Regex.Matches(code, @"new TabItem\b"));
    }
}
