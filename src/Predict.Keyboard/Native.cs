using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Predict.Keyboard;

/// <summary>Win32 interop used by the keyboard predictor (hooks, layout-aware key translation, caret,
/// input injection, window placement).</summary>
internal static class Native
{
    public const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    public const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    public const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14;
    public const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll")] public static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    /// <summary>wFlags bit 2 (value 4) = do not change the keyboard state, so dead keys (^, ¨) keep
    /// working in the target application (Windows 10 1607+).</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buf, int cch, uint flags, IntPtr hkl);

    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);

    // ---- SendInput ----
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion u; }
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint n, INPUT[] inputs, int size);

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // ---- message loop for the dedicated hook thread ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    public const uint WM_QUIT = 0x0012;

    // ---- reading the text at the caret (CaretTextReader) ----
    public const int EM_GETSEL = 0x00B0, WM_GETTEXT = 0x000D, GWL_STYLE = -16, ES_PASSWORD = 0x0020;
    public const uint SMTO_ABORTIFHUNG = 0x2, SMTO_ERRORONEXIT = 0x20;
    public const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MBUTTONUP = 0x0208;
    public const ushort VK_DELETE = 0x2E;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);

    /// <summary>A top-level window of another process: its stored title, no message sent (never hangs).</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    /// <summary>Each cross-process message is bounded: a hung app costs at most the timeout.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, IntPtr l, uint flags, uint ms, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, StringBuilder l, uint flags, uint ms, out IntPtr result);

    // ---- foreground-change notification (out-of-context WinEvent, delivered to the hook thread's loop) ----
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    /// <summary>Plan 06 review: the keyboard focus moved (also inside a window: in-page dialogs, two-step logins).</summary>
    public const uint EVENT_OBJECT_FOCUS = 0x8005;
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);

    // ---- LibreOffice Writer through IAccessible2 (Ia2Interop.cs): the MSAA entry points, read-only ----
    public const int OBJID_CLIENT = -4, CHILDID_SELF = 0;
    /// <summary>The object a WinEvent named (here the out-of-context EVENT_OBJECT_FOCUS of a Writer paragraph).
    /// <paramref name="child"/> comes back as a VARIANT: VT_I4 child id, CHILDID_SELF for the object itself.</summary>
    [DllImport("oleacc.dll")]
    public static extern int AccessibleObjectFromEvent(IntPtr hwnd, uint objectId, uint childId, out IntPtr acc,
                                                       [MarshalAs(UnmanagedType.Struct)] out object? child);
    /// <summary>A window's own accessible object (OBJID_CLIENT): the second-resort resolve, and what starts LibreOffice's
    /// accessibility bridge (its first WM_GETOBJECT).</summary>
    [DllImport("oleacc.dll")]
    public static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid, out IntPtr obj);

    // ---- clipboard (plan 07: simulated Ctrl+C fallback of the selection reading, off by default) ----
    [DllImport("user32.dll", SetLastError = true)] public static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] public static extern bool CloseClipboard();
    [DllImport("user32.dll")] public static extern bool EmptyClipboard();
    [DllImport("user32.dll")] public static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] public static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    /// <summary>The time (ms, 32-bit tick count) the message being dispatched was posted: a click handled late keeps its own time.</summary>
    [DllImport("user32.dll")] public static extern int GetMessageTime();
    /// <summary>The screen point of the message being dispatched (packed x | y &lt;&lt; 16, signed 16-bit each).</summary>
    [DllImport("user32.dll")] public static extern uint GetMessagePos();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    public const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")] public static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] public static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] public static extern UIntPtr GlobalSize(IntPtr mem);
    [DllImport("kernel32.dll")] public static extern IntPtr GlobalFree(IntPtr mem);
    public const uint GMEM_MOVEABLE = 0x0002, CF_UNICODETEXT = 13;
    public const ushort VK_C = 0x43, VK_F9 = 0x78, VK_SPACE = 0x20;

    // ---- tray icon (Tray, TrayPromotion) ----
    public const int SM_CXSMICON = 49;
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    public const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [StructLayout(LayoutKind.Sequential)]
    public struct NOTIFYICONIDENTIFIER { public uint cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }
    /// <summary>Screen rectangle of a notification-area icon (S_OK = 0).</summary>
    [DllImport("shell32.dll")] public static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT rect);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")]
    public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);

    /// <summary>Path of a known folder (e.g. {6D809377-…} = Program Files), null if unknown.</summary>
    public static string? KnownFolderPath(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) { CoTaskMemFree(p); return null; }
        try { return Marshal.PtrToStringUni(p); } finally { CoTaskMemFree(p); }
    }

    /// <summary>The taskbar, its notification area and overflow, or one of our windows (bar, Réglages, À propos, tray
    /// menu): going there is not « leaving » the text (D23 re-read: Keep, no landing, no read). Pass a root window.</summary>
    public static bool IsShellOrOwn(IntPtr root)
    {
        if (root == IntPtr.Zero) return false;
        if (IsOwnWindow(root)) return true;
        var sb = new StringBuilder(64);
        int n = GetClassName(root, sb, sb.Capacity);
        if (n <= 0) return false;
        var c = sb.ToString(0, n);
        return c is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    /// <summary>Is this window one of ours (tray menu, À propos)? Then the predictor must leave it alone.</summary>
    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }
}
