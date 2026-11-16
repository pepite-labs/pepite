using System;
using System.Runtime.InteropServices;

namespace Predict.Keyboard;

/// <summary>The current Windows session's logon time (plan 09 step 12): a new value = a new logon, so « Qui es-tu ? »
/// and the return to « Classe »; the same value = the app was only restarted. <c>WTSQuerySessionInformation</c>
/// (<c>WTSSessionInfo</c> → <c>WTSINFOW.LogonTime</c>, a FILETIME). Null when it cannot be read.</summary>
internal static class SessionInfo
{
    public static DateTime? LogonTimeUtc(Action<string>? log = null)
    {
        IntPtr buf = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, -1 /*WTS_CURRENT_SESSION*/, 24 /*WTSSessionInfo*/, out buf, out int bytes) ||
                buf == IntPtr.Zero || bytes < Marshal.SizeOf<WTSINFOW>())
            {
                log?.Invoke($"session: logon time unavailable ({Marshal.GetLastWin32Error()})");
                return null;
            }
            var info = Marshal.PtrToStructure<WTSINFOW>(buf);
            if (info.LogonTime <= 0) { log?.Invoke("session: logon time unavailable (0)"); return null; }
            return DateTime.FromFileTimeUtc(info.LogonTime);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentOutOfRangeException)
        {
            log?.Invoke($"session: logon time unavailable ({ex.GetType().Name})");
            return null;
        }
        finally { if (buf != IntPtr.Zero) WTSFreeMemory(buf); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WTSINFOW
    {
        public int State;
        public int SessionId;
        public int IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
