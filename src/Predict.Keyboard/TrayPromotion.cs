using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Predict.Keyboard;

/// <summary>The per-icon entries Explorer keeps under <c>HKCU\Control Panel\NotifyIconSettings</c>.</summary>
internal interface INotifyIconRegistry
{
    IEnumerable<string> SubKeys();
    string? ExecutablePath(string subKey);
    /// <summary>The subkey's <c>IsPromoted</c> DWORD, or null when nobody has written one yet.</summary>
    int? IsPromoted(string subKey);
    void SetPromoted(string subKey);
}

/// <summary>Remembers, per exe path, that we already promoted our icon once.</summary>
internal interface IPromotionState
{
    bool IsPromoted(string exePath);
    void MarkPromoted(string exePath);
}

/// <summary>
/// First-run promotion of the tray icon next to the clock (D5). Windows 11 hides new notification-area icons
/// in the overflow; Explorer records each icon under <c>HKCU\Control Panel\NotifyIconSettings\&lt;id&gt;</c> with
/// its <c>ExecutablePath</c> (a known-folder GUID may replace the folder) and a DWORD <c>IsPromoted</c>.
/// That mechanism is <b>undocumented</b> (community sources only), so this is best effort: we set
/// <c>IsPromoted=1</c> once per exe path (a dev build does not use up the installed build's flag), never
/// again after that — a later "hide" by the user is respected — and never touch other icons' subkeys.
/// Every failure is logged and swallowed. Nothing happens on Windows 10 (no such key).
/// </summary>
internal static class TrayPromotion
{
    private static readonly Regex GuidPrefix = new(@"^\{([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\}(.*)$");

    /// <summary>Expand a leading known-folder GUID (<c>{6D809377-…}\App\x.exe</c> → <c>C:\Program Files\App\x.exe</c>).</summary>
    public static string ResolveExecutablePath(string raw, Func<Guid, string?> knownFolder)
    {
        var m = GuidPrefix.Match(raw ?? "");
        if (!m.Success) return raw ?? "";
        var folder = knownFolder(Guid.Parse(m.Groups[1].Value));
        if (string.IsNullOrEmpty(folder)) return raw!;
        return folder.TrimEnd('\\') + "\\" + m.Groups[2].Value.TrimStart('\\');
    }

    public static bool Matches(string? raw, string exePath, Func<Guid, string?> knownFolder) =>
        raw is not null && string.Equals(Normalize(ResolveExecutablePath(raw, knownFolder)), Normalize(exePath),
                                         StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string p) => p.Replace('/', '\\').Trim();

    /// <summary>One attempt. Returns true when there is nothing left to do (already done earlier, or done now).</summary>
    public static bool TryPromote(INotifyIconRegistry reg, IPromotionState state, string exePath,
                                  Func<Guid, string?> knownFolder, Action<string>? log)
    {
        if (state.IsPromoted(exePath)) { log?.Invoke("tray: promotion already done once for this exe (user choice kept)"); return true; }
        var sub = reg.SubKeys().FirstOrDefault(k => Matches(reg.ExecutablePath(k), exePath, knownFolder));
        if (sub is null) return false;
        if (reg.IsPromoted(sub) is int existing)
        {
            // someone already decided (the user in Settings › taskbar, or Explorer): that choice wins, now and later
            state.MarkPromoted(exePath);
            log?.Invoke($"tray: IsPromoted already {existing} on NotifyIconSettings\\{sub}, left as is");
            return true;
        }
        reg.SetPromoted(sub);
        state.MarkPromoted(exePath);
        log?.Invoke($"tray: IsPromoted=1 set on NotifyIconSettings\\{sub}");
        return true;
    }

    /// <summary>Background: poll every 250 ms for up to 5 s until Explorer has created our subkey, then
    /// promote it and call <paramref name="refresh"/> if given (not needed on Windows 11 24H2: Explorer
    /// applies <c>IsPromoted</c> live, both ways — probe of plan 02 step 0).
    /// Not found → nothing written, retried at next start.</summary>
    [SupportedOSPlatform("windows7.0")]
    public static Task PromoteOnceAsync(string exePath, Action? refresh, Action<string>? log,
                                        IPromotionState? state = null, INotifyIconRegistry? reg = null)
    {
        state ??= new JsonPromotionState(JsonPromotionState.DefaultPath);
        reg ??= new NotifyIconRegistry();
        return Task.Run(async () =>
        {
            try
            {
                var t0 = Environment.TickCount64;
                if (state.IsPromoted(exePath)) { TryPromote(reg, state, exePath, Native.KnownFolderPath, log); return; }
                for (int i = 0; i <= 20; i++)
                {
                    if (TryPromote(reg, state, exePath, Native.KnownFolderPath, log))
                    {
                        log?.Invoke($"tray: subkey found after {Environment.TickCount64 - t0} ms");
                        refresh?.Invoke();
                        return;
                    }
                    await Task.Delay(250).ConfigureAwait(false);
                }
                log?.Invoke("tray: our NotifyIconSettings subkey did not appear within 5 s (will retry next start)");
            }
            catch (Exception ex) { log?.Invoke($"tray: promotion failed: {ex.GetType().Name}: {ex.Message}"); }
        });
    }
}

[SupportedOSPlatform("windows7.0")]
internal sealed class NotifyIconRegistry : INotifyIconRegistry
{
    private const string Root = @"Control Panel\NotifyIconSettings";

    public IEnumerable<string> SubKeys()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root);
        return k?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public string? ExecutablePath(string subKey)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root + "\\" + subKey);
        return k?.GetValue("ExecutablePath") as string;
    }

    public int? IsPromoted(string subKey)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root + "\\" + subKey);
        return k?.GetValue("IsPromoted") is int v ? v : null;
    }

    public void SetPromoted(string subKey)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Root + "\\" + subKey, writable: true)
                      ?? throw new InvalidOperationException("subkey vanished");
        k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
    }
}

/// <summary><c>%APPDATA%\PredictKeyboard\state.json</c>: <c>{"trayPromoted": {"&lt;exe path&gt;": true}}</c>.
/// Unknown keys are preserved; a corrupt file is treated as empty (and rewritten).</summary>
internal sealed class JsonPromotionState : IPromotionState
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PredictKeyboard", "state.json");

    private readonly string _path;
    private static readonly object FileLock = new();

    public JsonPromotionState(string path) => _path = path;

    private JsonObject Load()
    {
        try { return File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch (Exception) { return new JsonObject(); }
    }

    public bool IsPromoted(string exePath)
    {
        lock (FileLock)
            return Load()["trayPromoted"] is JsonObject t &&
                   t.Any(kv => string.Equals(kv.Key, exePath, StringComparison.OrdinalIgnoreCase) && kv.Value?.GetValue<bool>() == true);
    }

    public void MarkPromoted(string exePath)
    {
        lock (FileLock)
        {
            var root = Load();
            if (root["trayPromoted"] is not JsonObject t) { t = new JsonObject(); root["trayPromoted"] = t; }
            t[exePath] = true;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
