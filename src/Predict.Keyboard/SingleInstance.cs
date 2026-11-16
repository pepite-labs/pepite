using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Predict.Keyboard;

/// <summary>
/// One running copy per Windows session (TODO « Start-up feedback and single instance »): a named mutex
/// <c>Local\Pepite-Prediction.Instance</c>; a second copy sets the event <c>Local\Pepite-Prediction.Activate</c> (the
/// running copy then shows « Pépite est déjà démarré ») and exits. <c>Local\Pepite-Prediction.Quit</c> is the scripted
/// <c>--quit</c>. The « Pepite-Prediction » prefix avoids collisions with other software named Pépite (owner 2026-11-14).
/// <para>Across elevation levels: the objects are created with an explicit security descriptor — full access for the
/// current user's SID and SYSTEM, and a <b>medium</b> mandatory label with no-write-up — so a non-elevated copy can open
/// and set the objects of an elevated one (whose default DACL and high label would refuse it), and the reverse.
/// Limits: another user in the same session (« Exécuter en tant qu'autre utilisateur ») is denied by the DACL — the
/// mutex still says « already running » (access denied = it exists) but the event cannot be set, so the second copy
/// shows the message itself; separate sessions (fast user switching, RDP) each have their own <c>Local\</c> namespace,
/// one copy per session, which is wanted (hooks are per desktop). An older build (PredictKeyboard.exe ≤ 0.6.0) has no
/// mutex and is not detected.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SingleInstance : IDisposable
{
    public const string Prefix = @"Local\Pepite-Prediction.";
    public const string InstanceName = Prefix + "Instance";
    public const string ActivateName = Prefix + "Activate";
    public const string QuitName = Prefix + "Quit";
    /// <summary>Plan 12: a double-clicked <c>.pepite</c> — the path is in <c>ouvrir.txt</c> (<see cref="OpenRequest"/>).</summary>
    public const string OpenName = Prefix + "Open";

    private readonly SafeWaitHandle _mutex;

    private SingleInstance(SafeWaitHandle mutex) => _mutex = mutex;

    /// <summary>Take the instance mutex: the owner object (keep it alive until exit), or null when another copy runs.</summary>
    public static SingleInstance? TryAcquire(Action<string>? log, string name = InstanceName)
    {
        using var sa = new SecurityAttributes();
        var h = CreateMutexW(sa.Ptr, false, name);
        int err = Marshal.GetLastWin32Error();
        if (h.IsInvalid)
        {
            // ERROR_ACCESS_DENIED: the object exists but belongs to someone we may not open (another user): running
            if (err == ErrorAccessDenied) { log?.Invoke("single instance: mutex exists (access denied): another copy runs"); return null; }
            // any other failure: do not block the start on it (a second copy is less harmful than none)
            log?.Invoke($"single instance: CreateMutex failed ({err}); starting without the guard");
            return new SingleInstance(h);
        }
        if (err == ErrorAlreadyExists) { h.Dispose(); return null; }
        return new SingleInstance(h);
    }

    /// <summary>Create (or open) a named auto-reset event with the shared security descriptor.</summary>
    public static WaitHandle CreateEvent(string name)
    {
        using var sa = new SecurityAttributes();
        var h = CreateEventW(sa.Ptr, false, false, name);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEvent " + name);
        return new NativeEvent(h);
    }

    /// <summary>Set an existing named event (second copy → running copy). False when there is none or it cannot be
    /// opened. Lets the running copy take the foreground first: we were just started by the user, so we may.</summary>
    public static bool Signal(string name)
    {
        AllowSetForegroundWindow(AsfwAny);
        using var h = OpenEventW(EventModifyState, false, name);
        return !h.IsInvalid && SetEvent(h);
    }

    public void Dispose() => _mutex.Dispose();

    private const int ErrorAlreadyExists = 183, ErrorAccessDenied = 5;
    private const uint EventModifyState = 0x0002;
    private const int AsfwAny = -1;

    private sealed class NativeEvent : WaitHandle
    {
        public NativeEvent(SafeWaitHandle h) => SafeWaitHandle = h;
    }

    /// <summary>SECURITY_ATTRIBUTES for: full access to the current user and SYSTEM; mandatory label medium, no-write-up (an
    /// elevated creator lowers its objects to medium so the user's non-elevated copy can open them; nothing lower can).</summary>
    private sealed class SecurityAttributes : IDisposable
    {
        private IntPtr _sd, _sa;
        public IntPtr Ptr => _sa;

        public SecurityAttributes()
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null) return;   // no SA: default security (same-level only)
            var sddl = $"D:(A;;GA;;;{sid})(A;;GA;;;SY)S:(ML;;NW;;;ME)";
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out _sd, IntPtr.Zero)) { _sd = IntPtr.Zero; return; }
            _sa = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
            Marshal.StructureToPtr(new SECURITY_ATTRIBUTES
            { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = _sd, bInheritHandle = 0 }, _sa, false);
        }

        public void Dispose()
        {
            if (_sa != IntPtr.Zero) { Marshal.FreeHGlobal(_sa); _sa = IntPtr.Zero; }
            if (_sd != IntPtr.Zero) { LocalFree(_sd); _sd = IntPtr.Zero; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateMutexW(IntPtr sa, bool initialOwner, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateEventW(IntPtr sa, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle OpenEventW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(SafeWaitHandle h);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr sd, IntPtr size);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
