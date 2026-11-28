using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 12 step 2a: the walk-up to Data.Build\out only from a build output.</summary>
public class DataSearchTests
{
    [Theory]
    [InlineData(@"C:\src\freeware\src\Predict.Keyboard\bin\Release\net10.0-windows\win-x64\", true)]
    [InlineData(@"C:\src\freeware\src\Predict.Keyboard.Tests\bin\Debug\net10.0", true)]
    [InlineData(@"C:\Program Files\Pepite-Prediction\", false)]
    [InlineData(@"C:\Users\eleve\AppData\Local\Programs\Pepite-Prediction", false)]
    [InlineData(@"D:\bin\Pepite-Prediction\", false)]          // a bare \bin\ chosen at install time
    [InlineData(@"D:\work\publish-0.10.0\", false)]
    [InlineData(@"D:\Release\bin\", false)]
    public void MayWalkUp_Only_From_A_Build_Output(string dir, bool expected) => Assert.Equal(expected, DataSearch.MayWalkUp(dir, _ => true));

    [Fact]
    public void MayWalkUp_Needs_A_Project_File_Next_To_Bin()
    {
        const string bin = @"C:\x\src\Predict.Keyboard\bin\Release\net10.0-windows\win-x64";
        Assert.True(DataSearch.MayWalkUp(bin, d => d == @"C:\x\src\Predict.Keyboard"));
        Assert.False(DataSearch.MayWalkUp(bin, _ => false));                       // a look-alike folder without a project
        Assert.False(DataSearch.MayWalkUp(@"D:\bin\Release\Pepite", _ => true));   // bin right under the drive root
    }

    [Fact]
    public void Installed_Copy_Never_Finds_A_Planted_File_In_A_Parent()
    {
        var planted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Data.Build\out\pictos.sqlite", @"C:\Program Files\Data.Build\out\pictos.sqlite" };
        Assert.Null(DataSearch.Find(@"C:\Program Files\Pepite-Prediction\", "pictos.sqlite", planted.Contains, _ => true));
    }

    [Fact]
    public void Next_To_The_Exe_Wins_Then_The_Nearest_Parent_In_A_Dev_Build()
    {
        const string bin = @"C:\r\src\Predict.Keyboard\bin\Release\net10.0-windows\win-x64";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\r\src\Data.Build\out\pictos.sqlite", @"C:\Data.Build\out\pictos.sqlite" };
        Assert.Equal(@"C:\r\src\Data.Build\out\pictos.sqlite", DataSearch.Find(bin, "pictos.sqlite", files.Contains, _ => true));
        files.Add(Path.Combine(bin, "pictos.sqlite"));
        Assert.Equal(Path.Combine(bin, "pictos.sqlite"), DataSearch.Find(bin, "pictos.sqlite", files.Contains, _ => true));
        Assert.Empty(DataSearch.DevParents(@"C:\Program Files\Pepite-Prediction", _ => true));
        Assert.Contains(DataSearch.DevParents(bin, _ => true), d => d.FullName.TrimEnd('\\') == @"C:\r\src");
    }
}

/// <summary>Plan 12 step 2b: <c>--check-config</c> and the shared machine-file parser.</summary>
public class CheckConfigTests
{
    private static int Check(string text, out string msg) => MachineLayer.Check("x.json", _ => text, out msg);

    [Fact]
    public void Valid_Files_Exit_0()
    {
        Assert.Equal(0, Check("{}", out _));
        Assert.Equal(0, Check("{\"schema\":1,\"defaults\":{\"words\":{\"size\":\"L\"}},\"locked\":{}}", out var m));
        Assert.Contains("words.size", m);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{\"schema\":2}")]
    [InlineData("{\"schema\":\"1\"}")]
    [InlineData("{\"defaults\":[1]}")]
    [InlineData("{\"locked\":true}")]
    [InlineData("{\"schema\":1,\"schema\":1}")]
    [InlineData("{\"defaults\":{\"words\":{\"size\":\"L\",\"size\":\"S\"}}}")]
    public void Invalid_Json_Or_Unknown_Schema_Exit_2(string text) => Assert.Equal(2, Check(text, out _));

    [Fact]
    public void Unreadable_Or_Missing_Exit_3()
    {
        Assert.Equal(3, MachineLayer.Check(null, File.ReadAllText, out _));
        Assert.Equal(3, MachineLayer.Check(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "none.json"), File.ReadAllText, out _));
        Assert.Equal(3, MachineLayer.Check("x", _ => throw new UnauthorizedAccessException(), out _));
    }

    /// <summary>The school example shipped next to the installer (installer\exemple-etablissement.json) must pass.</summary>
    [Fact]
    public void The_Example_File_Is_Valid_And_Means_What_It_Says()
    {
        string? path = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && path is null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "installer", "exemple-etablissement.json");
            if (File.Exists(p)) path = p;
        }
        Assert.NotNull(path);
        Assert.Equal(0, MachineLayer.Check(path, File.ReadAllText, out var msg));
        var m = MachineLayer.Parse(File.ReadAllText(path!), out _)!;
        var (eff, locked) = SettingsLayering.Merge(m, null);
        Assert.Equal(BarSize.L, eff.Size);
        Assert.Equal(BarFont.OpenDyslexic, eff.Font);
        Assert.Equal(6, eff.WordCount);
        Assert.False(eff.Welcome);
        Assert.Contains("start.welcome", locked);
        Assert.Contains("words.spellingIndicator", locked);
        Assert.Contains("valid", msg);
    }

    [Fact]
    public void Load_Uses_The_Same_Parser()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pepite-cc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "{\"schema\":7,\"defaults\":{\"words\":{\"size\":\"XL\"}}}");
            var admins = new FileSecurityInfo("S-1-5-32-544", false, new[] { new AceInfo("S-1-5-32-544", 0x1F01FF, true) });
            var log = new List<string>();
            Assert.Same(MachineLayer.None, MachineLayer.Load(path, (_, _) => admins, log.Add));
            Assert.Contains(log, l => l.Contains("unknown schema 7"));
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>Plan 12 step 2d: the <c>.pepite</c> open request (<c>ouvrir.txt</c>) and the <c>--import</c> parsing.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class OpenRequestTests
{
    [Fact]
    public void Write_Then_Take_Round_Trips_Once()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pepite-or-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Null(OpenRequest.Take(dir));
            var file = Path.Combine(dir, "Classe CM1 é.pepite");
            Assert.True(OpenRequest.Write(dir, file));
            Assert.False(File.Exists(Path.Combine(dir, OpenRequest.FileName + ".tmp")));
            Assert.Equal(Path.GetFullPath(file), OpenRequest.Take(dir));
            Assert.Null(OpenRequest.Take(dir));   // taken = deleted
            Assert.True(OpenRequest.Write(dir, file + "1"));
            Assert.True(OpenRequest.Write(dir, file));   // a newer request replaces an untaken one
            Assert.Equal(Path.GetFullPath(file), OpenRequest.Take(dir));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Only_An_Existing_Pepite_Of_50_MB_At_Most_Is_Accepted()
    {
        long? Len(string p) => p.Contains("missing") ? null : p.Contains("big") ? OpenRequest.MaxBytes + 1 : 1000;
        Assert.True(OpenRequest.Acceptable(@"C:\Users\eleve\Documents\classe.pepite", Len));
        Assert.True(OpenRequest.Acceptable(@"\\serveur\partage\CLASSE.PEPITE", Len));
        Assert.False(OpenRequest.Acceptable(@"C:\x\missing.pepite", Len));
        Assert.False(OpenRequest.Acceptable(@"C:\x\big.pepite", Len));
        Assert.False(OpenRequest.Acceptable(@"C:\x\prediction.sqlite", Len));
        Assert.False(OpenRequest.Acceptable(@"classe.pepite", Len));            // relative: never resolved against our folder
        Assert.False(OpenRequest.Acceptable(@"C:\x\classe.pepite.exe", Len));
        Assert.False(OpenRequest.Acceptable("", Len));
        Assert.False(OpenRequest.Acceptable(null, Len));
        Assert.Equal(50L * 1024 * 1024, OpenRequest.MaxBytes);
    }

    [Fact]
    public void Import_And_Check_Config_Values_Are_Never_The_Database()
    {
        var args = new[] { "--import", @"C:\x\classe.pepite" };
        Assert.Empty(CommandLine.Positional(args));
        Assert.Equal(@"C:\x\classe.pepite", CommandLine.Option(args, CommandLine.Import));
        Assert.Empty(CommandLine.Positional(new[] { "--check-config", @"C:\x\settings.json" }));
        Assert.Empty(CommandLine.Positional(new[] { "--grammar", @"C:\x\g.sqlite", "--names", @"C:\x\noms" }));
        Assert.Equal(new[] { @"C:\db.sqlite" }, CommandLine.Positional(new[] { "--log", @"C:\db.sqlite", "--import", "a.pepite", "--autostart" }));
        Assert.Null(CommandLine.Option(new[] { "--import" }, CommandLine.Import));
    }

    [Fact]
    public void The_Open_Event_Shares_The_Prefix() =>
        Assert.Equal(@"Local\Pepite-Prediction.Open", SingleInstance.OpenName);
}

/// <summary>Plan 12 step 2c: the start log line's install kind.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class InstallInfoTests
{
    [Fact]
    public void Kind_From_The_Uninstall_Entries()
    {
        const string pf = @"C:\Program Files\Pepite-Prediction", pu = @"C:\Users\eleve\AppData\Local\Programs\Pepite-Prediction";
        Assert.Equal(InstallKind.AllUsers, InstallInfo.Classify(pf + "\\", pf, null));
        Assert.Equal(InstallKind.PerUser, InstallInfo.Classify(pu + "\\", null, pu + "\\"));
        Assert.Equal(InstallKind.PerUser, InstallInfo.Classify(pu.ToUpperInvariant() + "\\", pf, pu));
        Assert.Equal(InstallKind.Portable, InstallInfo.Classify(@"D:\tests\publish-0.10.0\", pf, pu));
        Assert.Equal(InstallKind.Portable, InstallInfo.Classify(pf, "", null));
        Assert.Equal(new[] { "portable", "per-user", "all-users" },
                     new[] { InstallKind.Portable, InstallKind.PerUser, InstallKind.AllUsers }.Select(InstallInfo.Name));
    }

    [Fact]
    public void AppId_Is_The_Installer_Script_One()
    {
        string? iss = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && iss is null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "installer", "pepite.iss");
            if (File.Exists(p)) iss = p;
        }
        Assert.NotNull(iss);
        var text = File.ReadAllText(iss!);
        // the script defines the bare GUID once (AppId={{GUID}} and the uninstall key are built from it)
        Assert.Contains("#define AppIdGuid \"" + InstallInfo.AppId.Trim('{', '}') + "\"", text);
        Assert.Contains("AppId={{{#AppIdGuid}}", text);
        Assert.Contains(SingleInstance.QuitName.Replace(@"Local\", ""), text);
    }

    /// <summary>0.10.2 (.NET 6 → .NET 10): an upgrade from 0.10.1 must delete the 45 .NET 6 runtime files the new
    /// publish no longer ships (an app-local ucrtbase.dll next to the .NET 10 runtime); one exact name per line, never a
    /// wildcard over {app}.</summary>
    [Fact]
    public void Upgrade_Deletes_The_Net6_Runtime_Files_One_By_One()
    {
        string? iss = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null && iss is null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "installer", "pepite.iss");
            if (File.Exists(p)) iss = p;
        }
        Assert.NotNull(iss);
        var lines = File.ReadAllLines(iss!);
        int start = Array.IndexOf(lines, "[InstallDelete]");
        Assert.True(start >= 0);
        var section = lines.Skip(start + 1).TakeWhile(l => !l.StartsWith("[", StringComparison.Ordinal)).ToList();
        var files = section.Where(l => l.StartsWith("Type: files; Name: \"{app}\\", StringComparison.Ordinal))
                           .Select(l => l.Substring("Type: files; Name: \"{app}\\".Length).Split('"')[0]).ToList();
        Assert.DoesNotContain(section, l => l.StartsWith("Type:", StringComparison.Ordinal) && (l.Contains('*') || l.Contains('?')));
        Assert.Contains("ucrtbase.dll", files);
        Assert.Contains("dbgshim.dll", files);
        Assert.Contains("mscordaccore_amd64_amd64_6.0.3224.31407.dll", files);
        Assert.Equal(27, files.Count(f => f.StartsWith("api-ms-win-core-", StringComparison.Ordinal)));
        Assert.Equal(15, files.Count(f => f.StartsWith("api-ms-win-crt-", StringComparison.Ordinal)));
        // nothing the .NET 10 build ships (InstallDelete runs before the copy, but a listed live file would be a typo)
        Assert.DoesNotContain(files, f => f is "Pepite.exe" or "Pepite.dll" or "coreclr.dll" or "hostfxr.dll" or "e_sqlite3.dll");
    }

    private static string RepoFile(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }

    /// <summary>The body of one Pascal function / procedure of pepite.iss (up to the next top-level <c>end;</c>).</summary>
    private static string CodeOf(string iss, string header)
    {
        int i = iss.IndexOf(header, StringComparison.Ordinal);
        Assert.True(i >= 0, header);
        int end = iss.IndexOf("\nend;", i, StringComparison.Ordinal);
        return iss.Substring(i, end - i);
    }

    /// <summary>Plan installer-upgrade step 3 (R2): an older setup over a newer install stops in InitializeSetup (exit 1),
    /// with a French message that says how to go back.</summary>
    [Fact]
    public void Downgrade_Is_Refused_In_InitializeSetup()
    {
        var iss = File.ReadAllText(RepoFile("installer", "pepite.iss")).Replace("\r\n", "\n");
        Assert.Contains("Result := CheckInstalledVersion();", CodeOf(iss, "function InitializeSetup(): Boolean;"));
        var check = CodeOf(iss, "function CheckInstalledVersion(): Boolean;");
        Assert.Contains("StrToVersion(PrevVersion, P)", check);
        Assert.Contains("ComparePackedVersion(P, N)", check);
        Assert.Contains("'DisplayVersion'", check);
        Assert.Contains("plus récente", check);
        Assert.Contains("désinstallez d''abord", check);
        Assert.Contains("downgrade refused: installed", check);
        var dep = File.ReadAllText(RepoFile("installer", "DEPLOIEMENT.txt"));
        Assert.Contains("version plus récente", dep);
    }

    /// <summary>Step 4 (R4): at an upgrade the information page is never shown again and the choice pages only with
    /// « Modifier les choix »; « Lancer Pépite » stays not elevated and never after a silent run.</summary>
    [Fact]
    public void Upgrade_Skips_The_Choice_Pages_Unless_Asked()
    {
        var iss = File.ReadAllText(RepoFile("installer", "pepite.iss")).Replace("\r\n", "\n");
        var skip = CodeOf(iss, "function ShouldSkipPage(PageID: Integer): Boolean;");
        foreach (var page in new[] { "wpInfoBefore", "wpSelectComponents", "wpSelectTasks", "wpReady", "ConfigPage.ID", "ChangeChoices.Checked", "IsUpgrade" })
            Assert.Contains(page, skip);
        Assert.Contains("CreateCustomPage(wpWelcome, 'Mise à jour de Pépite'", iss);
        Assert.Contains("'Mettre à jour'", iss);
        // the upgrade page names the previous tasks from the uninstall entry (the tasks list is empty before its page)
        Assert.Contains("'Inno Setup: Selected Tasks'", CodeOf(iss, "function CheckInstalledVersion(): Boolean;"));
        Assert.DoesNotContain("WizardIsTaskSelected('demarrage')", CodeOf(iss, "function UpgradeSummary(): String;"));
        var run = iss.Split("\n").Single(l => l.StartsWith("Filename: \"{app}\\Pepite.exe\"", StringComparison.Ordinal));
        Assert.Contains("skipifsilent", run);
        Assert.Contains("runasoriginaluser", run);
    }

    /// <summary>Step 5 (R6): Pépite is restarted after a silent upgrade only through ExecAsOriginalUser, and /NOLANCER
    /// (documented in DEPLOIEMENT.txt) prevents it.</summary>
    [Fact]
    public void Silent_Upgrade_Restart_Uses_ExecAsOriginalUser_And_NOLANCER()
    {
        var iss = File.ReadAllText(RepoFile("installer", "pepite.iss")).Replace("\r\n", "\n");
        var code = iss[iss.IndexOf("\n[Code]\n", StringComparison.Ordinal)..];
        var restart = CodeOf(iss, "procedure RestartAfterSilentInstall();");
        Assert.Contains("CmdSwitch('NOLANCER')", restart);
        Assert.Contains("WizardSilent", restart);
        Assert.Contains("PepiteWasRunning", restart);
        var autostart = code.Split("\n").Where(l => l.Contains("--autostart")).ToList();
        Assert.Single(autostart);
        Assert.Contains("ExecAsOriginalUser(", autostart[0]);
        Assert.Contains("RestartAfterSilentInstall();", CodeOf(iss, "procedure CurStepChanged(CurStep: TSetupStep);"));
        Assert.Contains("/NOLANCER", File.ReadAllText(RepoFile("installer", "DEPLOIEMENT.txt")));
    }

    /// <summary>The Finished page never claims desktop icons (that task is unticked by default).</summary>
    [Fact]
    public void Finished_Text_Does_Not_Promise_Desktop_Icons()
    {
        var lines = File.ReadAllLines(RepoFile("installer", "pepite.iss"));
        var finished = lines.Single(l => l.StartsWith("FinishedLabel=", StringComparison.Ordinal));
        Assert.DoesNotContain("Bureau", finished);
        Assert.Contains("menu Démarrer", finished);
    }

    /// <summary>High DPI: the three check lists get a wider, scaled gap left of their boxes (else a top-level box is
    /// drawn half outside the list, Inno Setup 7.1.1).</summary>
    [Fact]
    public void Check_Lists_Get_A_Scaled_Offset()
    {
        var iss = File.ReadAllText(RepoFile("installer", "pepite.iss")).Replace("\r\n", "\n");
        var init = CodeOf(iss, "procedure InitializeWizard();");
        foreach (var list in new[] { "ComponentsList", "TasksList", "RunList" })
            Assert.Contains($"WizardForm.{list}.Offset := WizardForm.{list}.Offset + ScaleX(8);", init);
    }
}
