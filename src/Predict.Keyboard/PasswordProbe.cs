using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Predict.Keyboard;

/// <summary>
/// Is the focused field of a foreground window a password field? The same two checks as the caret reader,
/// without reading any text: a focused Win32 <c>Edit</c> with <c>ES_PASSWORD</c>, else the UI Automation
/// focused element's <c>IsPassword</c> (only when it belongs to the foreground process, or reports pid 0 like
/// Chromium's in-page elements). Used by <see cref="EchoGuard"/> on its own MTA thread (UIA can block).
/// LibreOffice phase 1: in a Writer window, IAccessible2 answers instead (<see cref="LibreOfficeText.ProbePassword"/>,
/// cached per focus object: no COM call per spoken word in steady typing — the UIA probe was Writer's remaining CPU cost
/// per word); a focused object that is not a paragraph / heading is <c>Unavailable</c>, so nothing is spoken in
/// LibreOffice's fields. No answer from IA2 (no focus event seen yet, off, <c>--no-ia2</c>) → the UIA probe as before.
/// </summary>
internal static class PasswordProbe
{
    public static ProbeStatus Check(IntPtr fg)
    {
        uint tid = Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return ProbeStatus.Unavailable;
        // Writer, and LibreOffice's dialogs (VM L11: UIA answered NotPassword in « Saisir le mot de passe »)
        bool loDialog = HostApps.UiaTextUnavailable(fg) && HostApps.IsDialogFrame(fg);
        if (LibreOfficeText.Shared is { } lo && HostApps.UiaTextUnavailable(fg) && HostApps.Ia2Focus(HostApps.ModuleOf(fg)) &&
            lo.ProbePassword(fg, dialog: loDialog) is { } ia2)
            return ia2;
        // review: no IA2 answer in a LibreOffice dialog (--no-ia2, the breaker off, no focus object recorded) is never asked
        // to UIA, which calls its password field NotPassword: Password (no bar, nothing logged or spoken there)
        if (loDialog) return ProbeStatus.Password;
        var gi = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        IntPtr focus = Native.GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;
        if (focus != IntPtr.Zero && string.Equals(ClassOf(focus), "Edit", StringComparison.OrdinalIgnoreCase))
            return (Native.GetWindowLongPtr(focus, Native.GWL_STYLE).ToInt64() & Native.ES_PASSWORD) != 0
                 ? ProbeStatus.Password : ProbeStatus.NotPassword;
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el is null) return ProbeStatus.Unavailable;
            int elPid = el.Current.ProcessId;
            if (elPid != 0 && elPid != (int)pid) return ProbeStatus.Unavailable;
            // 0.8.3 (owner: never in a terminal): a web terminal (xterm.js: VS Code, cloud shells) is treated as a password field
            if (KeyboardPolicy.IsWebTerminal(el.Current.ClassName, el.Current.AutomationId)) return ProbeStatus.Password;
            return el.Current.IsPassword ? ProbeStatus.Password : ProbeStatus.NotPassword;
        }
        catch (Exception) { return ProbeStatus.Unavailable; }
    }

    internal static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        int n = Native.GetClassName(h, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : "";
    }
}
