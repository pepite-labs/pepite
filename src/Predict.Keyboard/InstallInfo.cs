using System;
using System.IO;

namespace Predict.Keyboard;

/// <summary>How this copy was installed (plan 12 step 2c), for the start log line
/// <c>start: elevated=&lt;yes|no&gt;, install=&lt;per-user|all-users|portable&gt;</c>.</summary>
internal enum InstallKind { Portable, PerUser, AllUsers }

/// <summary>
/// The installer's uninstall entry tells per-user from all-users: Inno Setup writes
/// <c>Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}_is1</c> with <c>InstallLocation</c> under HKCU
/// (per-user) or HKLM (all-users). Why the registry and not the folder: a school may install for all users anywhere
/// (<c>/ALLUSERS /DIR=D:\…</c>), and a per-user copy may sit in Program Files' look-alikes. A folder no entry points to is
/// a portable copy (a test-release folder).
/// </summary>
internal static class InstallInfo
{
    /// <summary>The installer's fixed <c>AppId</c> (<c>installer\pepite.iss</c>; committed once, never changed).</summary>
    public const string AppId = "{999AA879-43C2-4C22-9785-691AA95A3455}";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";

    /// <summary>Pure: <paramref name="hklmLocation"/> / <paramref name="hkcuLocation"/> = the entries' <c>InstallLocation</c>
    /// (null when absent). All-users wins when both point here (it cannot happen: the installer refuses the mixed mode).</summary>
    public static InstallKind Classify(string baseDir, string? hklmLocation, string? hkcuLocation)
    {
        if (Same(baseDir, hklmLocation)) return InstallKind.AllUsers;
        if (Same(baseDir, hkcuLocation)) return InstallKind.PerUser;
        return InstallKind.Portable;
    }

    public static string Name(InstallKind k) => k switch
    {
        InstallKind.PerUser => "per-user", InstallKind.AllUsers => "all-users", _ => "portable",
    };

    private static bool Same(string a, string? b)
    {
        if (string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>The live values: this exe's folder against both uninstall entries.</summary>
    public static InstallKind Current()
    {
        static string? Read(bool machine)
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var k = (machine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser).OpenSubKey(UninstallKey);
                return k?.GetValue("InstallLocation") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
        return Classify(AppContext.BaseDirectory, Read(true), Read(false));
    }

    /// <summary>Whether this process runs with an elevated (administrator) token.</summary>
    public static bool Elevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (System.Security.SecurityException) { return false; }
    }
}
