using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Predict.Keyboard;

/// <summary>A place of the embedded help (<c>aide\</c>, plan « aide intégrée »): what the tray « Aide », the « ? »
/// buttons and F1 of Réglages, and the welcome windows open.</summary>
internal enum HelpTopic { Home, Welcome, Settings, SettingsWords, SettingsPosition, SettingsSpeech, SettingsReading, SettingsStart, Pupils, MyWords, Spelling }

/// <summary>
/// Where the help entry points lead, and how a page is opened: a <b>file path</b> handed to the shell (the default
/// browser; never a URI). Windows drops the <c>#fragment</c> of a file opened this way, so a section is reached through a
/// small redirect stub (<c>aide\aller\reglages-voix.html</c> → <c>../reglages.html#voix</c>, WCAG H76). Fallback chain:
/// stub → page → <c>aide\index.html</c> → <c>LISEZ-MOI.txt</c>; a <see cref="Win32Exception"/> (no program for .html)
/// skips the other .html candidates. UI thread only — never called from the hook. Logs relative paths only.
/// </summary>
internal static class HelpLinks
{
    public const string HomePage = @"aide\index.html";
    public const string WelcomePage = @"aide\bienvenue.html";
    public const string ReadMe = "LISEZ-MOI.txt";
    /// <summary>ERROR_CANCELLED: the user dismissed the shell's « Ouvrir avec » dialog.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>The page of a topic and its redirect stub (null = the page top is enough), relative to the app folder.</summary>
    public static (string Page, string? Stub) Target(HelpTopic t) => t switch
    {
        HelpTopic.Welcome => (WelcomePage, null),
        HelpTopic.Settings => (@"aide\reglages.html", null),
        HelpTopic.SettingsWords => (@"aide\reglages.html", @"aide\aller\reglages-mots.html"),
        HelpTopic.SettingsPosition => (@"aide\reglages.html", @"aide\aller\reglages-position.html"),
        HelpTopic.SettingsSpeech => (@"aide\reglages.html", @"aide\aller\reglages-voix.html"),
        HelpTopic.SettingsReading => (@"aide\reglages.html", @"aide\aller\reglages-lecture.html"),
        HelpTopic.SettingsStart => (@"aide\reglages.html", @"aide\aller\reglages-demarrage.html"),
        HelpTopic.Pupils => (@"aide\enseignants.html", null),
        HelpTopic.MyWords => (@"aide\mes-mots.html", null),
        HelpTopic.Spelling => (@"aide\orthographe.html", null),
        _ => (HomePage, null),
    };

    /// <summary>What to try, in order: the stub, the page, the help's home, the LISEZ-MOI (no duplicates).</summary>
    public static IReadOnlyList<string> Candidates(HelpTopic t)
    {
        var (page, stub) = Target(t);
        var list = new List<string>();
        foreach (var c in new[] { stub, page, HomePage, ReadMe })
            if (c != null && !list.Contains(c)) list.Add(c);
        return list;
    }

    /// <summary>The first existing candidate under <paramref name="baseDir"/>, or null.</summary>
    public static string? Resolve(string baseDir, HelpTopic t, Func<string, bool> exists)
    {
        foreach (var rel in Candidates(t))
        {
            var p = Path.Combine(baseDir, rel);
            if (exists(p)) return p;
        }
        return null;
    }

    /// <summary>The welcome page, or the LISEZ-MOI (the « En savoir plus » button is enabled when this is not null).</summary>
    public static string? Resolve(string baseDir, Func<string, bool> exists) => Resolve(baseDir, HelpTopic.Welcome, exists);

    /// <summary>The topic of a Réglages section (the <c>aide:&lt;name&gt;</c> tag of the section's panel); no section →
    /// the Réglages page, or « Mes mots » on the « Mots » tab.</summary>
    public static HelpTopic ForSettings(string? sectionName, bool wordsTab) => sectionName switch
    {
        "mots" => HelpTopic.SettingsWords,
        "position" => HelpTopic.SettingsPosition,
        "voix" => HelpTopic.SettingsSpeech,
        "lecture" => HelpTopic.SettingsReading,
        "demarrage" => HelpTopic.SettingsStart,
        "eleves" => HelpTopic.Pupils,
        "orthographe" => HelpTopic.Spelling,
        "mes-mots" => HelpTopic.MyWords,
        _ => wordsTab ? HelpTopic.MyWords : HelpTopic.Settings,
    };

    /// <summary>Open the help of <paramref name="t"/>. <paramref name="start"/> hands a path to the shell (default:
    /// <c>ShellExecute</c>) and may throw <see cref="Win32Exception"/>; then the next candidate with another extension is
    /// tried; ERROR_CANCELLED (the user closed « Ouvrir avec ») stops there and counts as handled. Returns false when
    /// nothing could be opened (the caller tells the user).</summary>
    public static bool Open(HelpTopic t, Action<string>? log, string? baseDir = null,
                            Func<string, bool>? exists = null, Func<string, bool>? start = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        exists ??= File.Exists;
        start ??= ShellOpen;
        string? failedExt = null;
        foreach (var rel in Candidates(t))
        {
            var ext = Path.GetExtension(rel);
            if (failedExt != null && string.Equals(ext, failedExt, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.Combine(baseDir, rel);
            if (!exists(path)) continue;
            try
            {
                if (start(path)) { log?.Invoke($"help: {t} -> {rel}"); return true; }
                log?.Invoke($"help: {rel} not opened");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                // the user closed Windows' « Ouvrir avec » dialog: a choice, not a failure — no LISEZ-MOI, no message
                log?.Invoke($"help: {rel} cancelled by the user");
                return true;
            }
            catch (Win32Exception ex)
            {
                log?.Invoke($"help: {rel} not opened ({ex.NativeErrorCode})");
                failedExt = ext;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"help: {rel} not opened ({ex.GetType().Name})");
            }
        }
        log?.Invoke($"help: {t}: nothing to open");
        return false;
    }

    /// <summary>The user's default program for the file (local file only, no network, D13).</summary>
    private static bool ShellOpen(string path)
    {
        using var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return true;   // null = handed to an already running program: opened too
    }
}

/// <summary>The tray menu's entries, in order (pure: <see cref="Tray"/> draws them, the tests check the order).</summary>
internal enum TrayItem { Profile, SwitchProfile, Separator, Pause, AddWord, Settings, Teacher, Help, About, Quit }

internal static class TrayMenu
{
    /// <summary>Profile entries while teacher management is on · Pause · Ajouter un mot… · Réglages… · Espace
    /// enseignant… (management on) · Aide · À propos… · Quitter (« Aide » right before « À propos… »).</summary>
    public static IReadOnlyList<TrayItem> MenuItems(bool management, bool addWord, bool help)
    {
        var l = new List<TrayItem>();
        if (management) l.AddRange(new[] { TrayItem.Profile, TrayItem.SwitchProfile, TrayItem.Separator });
        l.Add(TrayItem.Pause);
        l.Add(TrayItem.Separator);
        if (addWord) l.Add(TrayItem.AddWord);
        l.Add(TrayItem.Settings);
        if (management) l.Add(TrayItem.Teacher);
        if (help) l.Add(TrayItem.Help);
        l.Add(TrayItem.About);
        l.Add(TrayItem.Quit);
        return l;
    }
}
