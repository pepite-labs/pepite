using System;
using System.Runtime.InteropServices;

namespace Predict.Keyboard;

/// <summary>
/// The Windows-side of <see cref="SessionPolicy"/> (plan 09B step 18, UI thread): a hidden tool window registered with
/// <c>WTSRegisterSessionNotification(NOTIFY_FOR_THIS_SESSION)</c> receives <c>WM_WTSSESSION_CHANGE</c> — lock (Win+L, or
/// a switch of user), unlock, logoff — and <see cref="IdleTime"/> reads <c>GetLastInputInfo</c> (the last keyboard / mouse
/// input of this session), polled by a 30 s <c>DispatcherTimer</c>, never from the keyboard hook. A hidden top-level
/// window rather than a message-only one: session notifications are documented for top-level windows.
/// </summary>
internal sealed class SessionWatcher : System.Windows.Forms.NativeWindow, IDisposable
{
    public const int WmWtsSessionChange = 0x02B1;
    public const int WtsSessionLogoff = 0x6, WtsSessionLock = 0x7, WtsSessionUnlock = 0x8;
    private const int WsExToolWindow = 0x80;

    private readonly Action<string>? _log;
    private bool _registered;

    /// <summary>A session change (<see cref="WtsSessionLock"/>, <see cref="WtsSessionUnlock"/>, <see cref="WtsSessionLogoff"/>…).</summary>
    public event Action<int>? Changed;

    public SessionWatcher(Action<string>? log)
    {
        _log = log;
        CreateHandle(new System.Windows.Forms.CreateParams { Caption = "Pepite-Prediction.Session", ExStyle = WsExToolWindow });
        _registered = WTSRegisterSessionNotification(Handle, 0 /*NOTIFY_FOR_THIS_SESSION*/);
        log?.Invoke(_registered ? "session: lock / unlock / logoff notifications registered"
                                : $"session: notifications unavailable ({Marshal.GetLastWin32Error()})");
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == WmWtsSessionChange)
        {
            int code = (int)m.WParam;
            try { Changed?.Invoke(code); }
            catch (Exception ex) { _log?.Invoke($"session: change {code} failed ({ex.GetType().Name}: {ex.Message})"); }
        }
        base.WndProc(ref m);
    }

    /// <summary>Time since the last keyboard or mouse input of this session (zero when it cannot be read).</summary>
    public static TimeSpan IdleTime()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return TimeSpan.Zero;
        uint now = unchecked((uint)Environment.TickCount);
        return TimeSpan.FromMilliseconds(unchecked(now - li.dwTime));   // both wrap every 49.7 days: the difference does not
    }

    public void Dispose()
    {
        if (_registered && Handle != IntPtr.Zero) WTSUnRegisterSessionNotification(Handle);
        _registered = false;
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);
}
