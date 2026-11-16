using System;
using System.Drawing;
using System.Reflection;
using WinForms = System.Windows.Forms;

namespace Predict.Keyboard;

/// <summary>The tray's profile entries (plan 09): the active profile's name while teacher management is on (null = off:
/// the menu is the single-user one), « Changer de profil… », « Espace enseignant… ».</summary>
internal sealed record TrayProfiles(Func<string?> ActiveName, Action Switch, Action Teacher);

/// <summary>
/// Notification-area icon (D5): our own icon (grey variant while paused), tooltip, and the same menu
/// Pause/Reprendre · Ajouter un mot… (plan 10A) · Réglages… · Aide (embedded help) · À propos… · Quitter on <b>left and
/// right</b> click (order: <see cref="TrayMenu.MenuItems"/>).
/// The menu is a native Win32 popup menu (SetForegroundWindow + TrackPopupMenuEx + WM_NULL, the documented
/// notification-area pattern) rather than the WinForms <c>ContextMenuStrip</c>: in the sandbox the strip's
/// first show after start ignored every mouse click (only Esc closed it) — plan 02 T4. UI thread only.
/// </summary>
internal sealed class Tray : IDisposable
{
    private const int CmdPause = 1, CmdAbout = 2, CmdQuit = 3, CmdSettings = 4, CmdProfile = 5, CmdSwitch = 6, CmdTeacher = 7, CmdAddWord = 8, CmdHelp = 9;

    private readonly WinForms.NotifyIcon _icon;
    private readonly Icon _active, _paused;
    private readonly KeyboardController _ctl;
    private readonly SuggestionWindow _win;
    private readonly Action _settings, _about, _quit;
    private readonly Action<string>? _log;
    private readonly WinForms.NativeWindow _owner = new();   // hidden top-level window that owns the popup menu
    private bool _menuOpen;
    private readonly TrayProfiles? _profiles;
    private readonly Action? _addWord;
    private readonly Action? _help;

    /// <param name="settings">opens « Réglages… » (through the settings gate).</param>
    /// <param name="profiles">plan 09: the profile entries, shown while teacher management is on.</param>
    /// <param name="addWord">plan 10A: « Ajouter un mot… » (never behind the PIN: adding only, Q3).</param>
    /// <param name="help">« Aide »: the embedded help's home page (never behind the PIN).</param>
    public Tray(KeyboardController ctl, SuggestionWindow win, Action settings, Action about, Action quit, Action<string>? log,
                TrayProfiles? profiles = null, Action? addWord = null, Action? help = null)
    {
        _help = help;
        _profiles = profiles;
        _addWord = addWord;
        _ctl = ctl;
        _win = win;
        _settings = settings;
        _about = about;
        _quit = quit;
        _log = log;
        _active = LoadIcon("app.ico");
        _paused = LoadIcon("app-paused.ico");
        _owner.CreateHandle(new WinForms.CreateParams { Caption = "Pepite-Prediction.TrayMenu" });
        _icon = new WinForms.NotifyIcon { Icon = _active, Text = Strings.TrayText(false, null, profiles?.ActiveName()), Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is WinForms.MouseButtons.Left or WinForms.MouseButtons.Right) ShowMenu();
        };
    }

    public bool Paused => !_ctl.Enabled;

    private string? _loading;

    /// <summary>The configured voice is loading (<c>piper:&lt;id&gt;</c>), or null: the tooltip says so (UI thread).</summary>
    public void SetVoiceLoading(string? voiceId)
    {
        _loading = voiceId;
        RefreshText();
    }

    /// <summary>The tooltip again (pause, voice loading, the active profile's name when management is on).</summary>
    public void RefreshText() => _icon.Text = Strings.TrayText(Paused, _loading, _profiles?.ActiveName());

    /// <summary>Start with Windows (<c>--autostart</c>): a discreet « Pépite est prêt » notification instead of the welcome
    /// window. A NotifyIcon balloon: on Windows 10/11 the Shell shows it as a toast that closes by itself (and respects
    /// « Ne pas déranger »).</summary>
    public void ShowReady()
    {
        _icon.ShowBalloonTip(5000, Strings.ReadyBalloonTitle, Strings.ReadyBalloonText, WinForms.ToolTipIcon.None);
        _log?.Invoke("tray: ready notification shown");
    }

    public void TogglePause()
    {
        _ctl.Enabled = !_ctl.Enabled;
        bool paused = !_ctl.Enabled;
        _icon.Icon = paused ? _paused : _active;
        RefreshText();
        if (paused) { _win.HideBar(); _ctl.OnPaused(); }
        _log?.Invoke(paused ? "tray: paused" : "tray: resumed");
    }

    private void ShowMenu()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        IntPtr menu = Native.CreatePopupMenu();
        try
        {
            var name = _profiles?.ActiveName();
            // management on (D29): « Profil : Léa » (greyed: a label) · Changer de profil… first
            foreach (var item in TrayMenu.MenuItems(name != null, _addWord != null, _help != null))
            {
                switch (item)
                {
                    case TrayItem.Profile:
                        Native.AppendMenu(menu, Native.MF_STRING | Native.MF_GRAYED, (UIntPtr)CmdProfile, string.Format(Strings.MenuProfile, name!.Replace("&", "&&")));
                        break;
                    case TrayItem.SwitchProfile: Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdSwitch, Strings.MenuSwitchProfile); break;
                    case TrayItem.Separator: Native.AppendMenu(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null); break;
                    case TrayItem.Pause: Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdPause, Paused ? Strings.MenuResume : Strings.MenuPause); break;
                    case TrayItem.AddWord: Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdAddWord, Strings.MenuAddWord); break;
                    case TrayItem.Settings: Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdSettings, Strings.MenuSettings); break;
                    case TrayItem.Teacher: Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdTeacher, Strings.MenuTeacher); break;
                    case TrayItem.Help: Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdHelp, Strings.MenuHelp); break;
                    case TrayItem.About: Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdAbout, Strings.MenuAbout); break;
                    case TrayItem.Quit: Native.AppendMenu(menu, Native.MF_STRING, (UIntPtr)CmdQuit, Strings.MenuQuit); break;
                }
            }
            var (x, y, how) = MenuOrigin();
            // the owner must be the foreground window, or the menu does not close when clicking elsewhere
            Native.SetForegroundWindow(_owner.Handle);
            int cmd = Native.TrackPopupMenuEx(menu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN,
                                              x, y, _owner.Handle, IntPtr.Zero);
            Native.PostMessage(_owner.Handle, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);
            _log?.Invoke($"tray: menu at {how} ({x},{y}) -> {cmd}");
            switch (cmd)
            {
                case CmdPause: TogglePause(); break;
                case CmdSettings: _settings(); break;
                case CmdAbout: _about(); break;
                case CmdQuit: _quit(); break;
                case CmdSwitch: _profiles?.Switch(); break;
                case CmdTeacher: _profiles?.Teacher(); break;
                case CmdAddWord: _addWord?.Invoke(); break;
                case CmdHelp: _help?.Invoke(); break;
            }
        }
        catch (Exception ex) { _log?.Invoke($"tray: menu failed: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            Native.DestroyMenu(menu);
            _menuOpen = false;
        }
    }

    /// <summary>Where to open the menu: at the mouse when the click came from the mouse (cursor on the icon),
    /// else — keyboard activation (Win+B, arrows, Enter/Space), which the Shell reports to a version-0
    /// <c>NotifyIcon</c> as a button message too — at the icon itself (<c>Shell_NotifyIconGetRect</c>).</summary>
    private (int X, int Y, string How) MenuOrigin()
    {
        Native.GetCursorPos(out var pt);
        if (IconRect() is { } r)
        {
            if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom) return (pt.X, pt.Y, "mouse");
            return ((r.Left + r.Right) / 2, r.Top, $"icon [{r.Left},{r.Top},{r.Right},{r.Bottom}]");
        }
        return (pt.X, pt.Y, "mouse (icon rect unknown)");
    }

    /// <summary>Screen rectangle of our icon; null when it cannot be found (hidden in the overflow is fine:
    /// the Shell then returns the overflow position). The NotifyIcon's window handle and id are private
    /// fields — read by reflection, with a null fallback (menu at the mouse).</summary>
    private Native.RECT? IconRect()
    {
        try
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
            var t = typeof(WinForms.NotifyIcon);
            var win = (t.GetField("_window", F) ?? t.GetField("window", F))?.GetValue(_icon) as WinForms.NativeWindow;
            var idObj = (t.GetField("_id", F) ?? t.GetField("id", F))?.GetValue(_icon);
            if (win is null || win.Handle == IntPtr.Zero || idObj is null) return null;
            var nid = new Native.NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.NOTIFYICONIDENTIFIER>(),
                hWnd = win.Handle, uID = Convert.ToUInt32(idObj),
            };
            return Native.Shell_NotifyIconGetRect(ref nid, out var r) == 0 ? r : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Embedded .ico at the system small-icon size for the current DPI (16 px at 100 %, 24 at 150 %…).</summary>
    public static Icon LoadIcon(string name, int size = 0)
    {
        if (size <= 0)
        {
            try { size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, Native.GetDpiForSystem()); }
            catch (EntryPointNotFoundException) { size = WinForms.SystemInformation.SmallIconSize.Width; }
        }
        using var s = typeof(Tray).Assembly.GetManifestResourceStream("Predict.Keyboard.Assets." + name)
                      ?? throw new InvalidOperationException("missing resource " + name);
        return new Icon(s, size, size);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _active.Dispose();
        _paused.Dispose();
        _owner.DestroyHandle();
    }
}
