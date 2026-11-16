using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Predict.Keyboard;

/// <summary>
/// A queue served by one background MTA thread that runs a call which may block (UI Automation into a hung
/// provider). Callers never wait: <see cref="Submit"/> is a short lock + event set. A worker busy for more than
/// <c>stuckMs</c> is abandoned when the next item arrives and a new generation takes over — at most
/// <see cref="MaxAbandoned"/> abandoned threads at a time; beyond that the item is refused (the caller logs a
/// drop). Replacement threads are started from the thread pool, never on the caller's (hook) thread. An abandoned
/// worker that comes back exits without acting on its item (<c>stillCurrent()</c> is false).
/// </summary>
internal sealed class ProbeWorker<T> : IDisposable where T : class
{
    public const int MaxAbandoned = 2;

    private readonly Action<T, Func<bool>> _run;
    private readonly string _name;
    private readonly int _stuckMs, _capacity;
    private readonly Action<string>? _log;
    private readonly Action<T>? _dropped;
    private readonly object _lock = new();
    private readonly Queue<T> _items = new();   // guarded by _lock
    private readonly AutoResetEvent _signal = new(false);
    private int _generation, _abandoned;         // guarded by _lock
    private long _busySince;                     // guarded by _lock; 0 = idle
    private volatile bool _disposed;

    /// <param name="run">handles one item on the worker; the second argument says whether this worker still owns
    /// the queue (check it after anything that may block, before acting).</param>
    /// <param name="capacity">queue length; 1 = the latest item wins.</param>
    /// <param name="dropped">told of an item removed without being run (capacity, <see cref="Clear"/>), outside the lock.</param>
    public ProbeWorker(string name, Action<T, Func<bool>> run, int stuckMs, int capacity, Action<string>? log, Action<T>? dropped = null)
    {
        _dropped = dropped;
        _name = name;
        _run = run;
        _stuckMs = stuckMs;
        _capacity = Math.Max(1, capacity);
        _log = log;
        Start(0);
    }

    /// <returns>false when the worker is stuck and no replacement may be started (item refused).</returns>
    public bool Submit(T item)
    {
        if (_disposed) return true;
        int replace = -1;
        List<T>? gone = null;
        lock (_lock)
        {
            if (_busySince != 0 && Environment.TickCount64 - _busySince > _stuckMs)
            {
                if (_abandoned >= MaxAbandoned) return false;
                replace = ++_generation;
                _abandoned++;
                _busySince = 0;
            }
            while (_items.Count >= _capacity) (gone ??= new List<T>()).Add(_items.Dequeue());
            _items.Enqueue(item);
        }
        Tell(gone);
        if (replace >= 0)
        {
            _log?.Invoke($"{_name} stuck > {_stuckMs} ms: new thread (generation {replace})");
            ThreadPool.UnsafeQueueUserWorkItem(_ => Start(replace), null);
        }
        _signal.Set();
        return true;
    }

    public void Clear()
    {
        List<T> gone;
        lock (_lock) { gone = new List<T>(_items); _items.Clear(); }
        Tell(gone);
    }

    private void Tell(List<T>? gone)
    {
        if (gone is null || _dropped is null) return;
        foreach (var g in gone) { try { _dropped(g); } catch (Exception) { } }
    }

    private void Start(int gen)
    {
        var t = new Thread(() => Loop(gen)) { IsBackground = true, Name = _name };
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.MTA);   // UIA client
        t.Start();
    }

    private bool Owns(int gen) { lock (_lock) return gen == _generation && !_disposed; }

    private void Loop(int gen)
    {
        while (!_disposed)
        {
            T? next = null;
            lock (_lock)
            {
                if (gen != _generation) return;
                if (_items.Count > 0) { next = _items.Dequeue(); _busySince = Environment.TickCount64; }
            }
            if (next is null) { _signal.WaitOne(500); continue; }
            try { _run(next, () => Owns(gen)); }
            catch (Exception ex) { _log?.Invoke($"{_name} error: {ex.GetType().Name}"); }
            lock (_lock)
            {
                if (gen != _generation) { _abandoned = Math.Max(0, _abandoned - 1); return; }   // came back after being replaced
                _busySince = 0;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock) _generation++;
        _signal.Set();
    }
}

/// <summary>
/// Privacy guard of every speech of typed text — the word / sentence echo and, since 0.8.3, the inserted word and the
/// corrected word: before speaking, the focused field of the foreground window is checked on a separate
/// <c>echo-guard</c> MTA thread (UI Automation can hang in a provider). Owner decision 2026-11-15 (0.8.3, « ne rien
/// lire si on ne sait pas »): spoken ONLY when the probe proves a plain field (NotPassword) and the window is not a
/// terminal or a remote-session / VM window (<see cref="PrivateHosts"/>). Password → dropped (log <c>echo dropped
/// (password)</c>, never the text) and <c>onPassword(fg)</c> is called so the controller stops tracking too;
/// Unavailable (the field cannot be classified) → dropped (<c>echo dropped (field not proven)</c>); a terminal / remote
/// window → dropped; probe slower than <see cref="BudgetMs"/> or foreground changed since the key → dropped.
/// <see cref="Clear"/> (pause, password read) also discards the item being probed. Right-click reading (Manual) never
/// comes here: the pupil asks for that word.
/// </summary>
internal sealed class EchoGuard : IDisposable
{
    public const int BudgetMs = 300, StuckMs = 2000;

    private sealed record Item(SpeechItem Speech, IntPtr Fg, long Epoch);

    private readonly Func<IntPtr, ProbeStatus> _probe;
    private readonly Func<IntPtr> _foreground;
    private readonly Action<SpeechItem> _forward;
    private readonly Action<IntPtr>? _onPassword;
    private readonly Func<IntPtr, bool> _privateHost;
    private readonly Action<string>? _log;
    private readonly ProbeWorker<Item> _worker;
    private long _epoch;   // incremented by Clear: an item submitted before is never forwarded

    /// <param name="privateHost">the window is a terminal or a remote-session / VM window (default: never; the app passes
    /// <see cref="PrivateHosts.Check"/>).</param>
    public EchoGuard(Func<IntPtr, ProbeStatus> probe, Func<IntPtr> foreground, Action<SpeechItem> forward, Action<string>? log,
                     Action<IntPtr>? onPassword = null, Func<IntPtr, bool>? privateHost = null)
    {
        _probe = probe;
        _privateHost = privateHost ?? (_ => false);
        _foreground = foreground;
        _forward = forward;
        _onPassword = onPassword;
        _log = log;
        _worker = new ProbeWorker<Item>("echo-guard", Run, StuckMs, 8, log, Dropped);
    }

    /// <summary>Non-blocking (a short lock + event set).</summary>
    public void Submit(SpeechItem item, IntPtr fg)
    {
        var it = new Item(item, fg, Interlocked.Read(ref _epoch));
        if (!_worker.Submit(it)) { _log?.Invoke("echo dropped (guard stuck)"); Dropped(it); }
    }

    public void Clear()
    {
        Interlocked.Increment(ref _epoch);
        _worker.Clear();
    }

    private void Run(Item it, Func<bool> stillCurrent)
    {
        var sw = Stopwatch.StartNew();
        ProbeStatus st;
        bool fgChanged, priv = false;
        try
        {
            fgChanged = _foreground() != it.Fg;
            if (!fgChanged) priv = _privateHost(it.Fg);
            st = fgChanged || priv ? ProbeStatus.Unavailable : _probe(it.Fg);
        }
        catch (Exception) { st = ProbeStatus.Unavailable; fgChanged = false; }
        long ms = sw.ElapsedMilliseconds;
        if (!stillCurrent()) { Dropped(it); return; }   // abandoned while probing: a newer guard owns the queue
        if (fgChanged) { _log?.Invoke("echo dropped (foreground changed)"); Dropped(it); return; }
        if (st == ProbeStatus.Password)
        {
            _log?.Invoke($"echo dropped (password) {ms} ms");
            Dropped(it);
            _onPassword?.Invoke(it.Fg);
            return;
        }
        if (priv) { _log?.Invoke("echo dropped (terminal or remote window)"); Dropped(it); return; }
        if (st != ProbeStatus.NotPassword) { _log?.Invoke($"echo dropped (field not proven) {ms} ms"); Dropped(it); return; }
        if (ms > BudgetMs) { _log?.Invoke($"echo dropped (probe {ms} ms > {BudgetMs})"); Dropped(it); return; }
        if (Interlocked.Read(ref _epoch) != it.Epoch) { _log?.Invoke("echo dropped (cancelled)"); Dropped(it); return; }
        try { if (_foreground() != it.Fg) { _log?.Invoke("echo dropped (foreground changed)"); Dropped(it); return; } } catch (Exception) { }
        _forward(it.Speech);
    }

    /// <summary>An item with an owner (the whole-sentence echo) hears that it was not spoken.</summary>
    private void Dropped(Item it)
    {
        try { it.Speech.Done?.Invoke(SpeechOutcome.Cancelled); } catch (Exception) { }
    }

    public void Dispose() => _worker.Dispose();
}

/// <summary>
/// "Is the field I am typing into a password field?" asked at the first character after a caret jump whose read
/// did not answer (timed out, overtaken by typing, or no jump yet: an autofocused field). Latest request wins, on
/// its own <c>field-probe</c> MTA thread; <c>done(id, fg, status, ms)</c> is called on that thread. Until the
/// controller has the answer it neither logs nor shows what is typed.
/// </summary>
internal sealed class FieldProbe : IDisposable
{
    public const int StuckMs = 2000;

    private sealed record Req(long Id, IntPtr Fg);

    private readonly Func<IntPtr, ProbeStatus> _probe;
    private readonly Action<long, IntPtr, ProbeStatus, long> _done;
    private readonly ProbeWorker<Req> _worker;
    private long _nextId;

    public FieldProbe(Func<IntPtr, ProbeStatus> probe, Action<long, IntPtr, ProbeStatus, long> done, Action<string>? log)
    {
        _probe = probe;
        _done = done;
        _worker = new ProbeWorker<Req>("field-probe", Run, StuckMs, 1, log);
    }

    /// <returns>the request id, or 0 when refused (worker stuck): the caller stays in its "unknown" state.</returns>
    public long Request(IntPtr fg)
    {
        long id = Interlocked.Increment(ref _nextId);
        return _worker.Submit(new Req(id, fg)) ? id : 0;
    }

    private void Run(Req r, Func<bool> stillCurrent)
    {
        var sw = Stopwatch.StartNew();
        ProbeStatus st;
        try { st = _probe(r.Fg); } catch (Exception) { st = ProbeStatus.Unavailable; }
        if (stillCurrent()) _done(r.Id, r.Fg, st, sw.ElapsedMilliseconds);
    }

    public void Dispose() => _worker.Dispose();
}

/// <summary>
/// 0.8.3, owner decision 2026-11-15: windows where nothing is tracked nor spoken — terminals (consoles, Windows Terminal,
/// SSH clients: the same lists as the Ctrl+C fallback, <see cref="ClipboardCopy.IsTerminalClass"/> /
/// <see cref="ClipboardCopy.IsTerminalProcess"/>) and remote-session / virtual-machine windows (Remote Desktop, Hyper-V
/// connection, VirtualBox, VMware, remote-control and VNC clients): what is typed there belongs to another computer or
/// may be a command line, and their fields cannot be classified. Window class (top level and focused control) + process
/// image name; the last answer is cached per window and focused control. A web terminal inside a page (VS Code, a cloud
/// shell: its focused element is an <c>xterm-helper-textarea</c>) is caught by <see cref="PasswordProbe"/>, which handles
/// it as a password field. Known limit: a remote session shown inside a browser page (Chrome Remote Desktop, a web RDP
/// gateway) is not recognised.
/// </summary>
internal static class PrivateHosts
{
    private static readonly HashSet<string> RemoteProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc", "msrdc", "vmconnect", "VirtualBoxVM", "vmware", "vmplayer", "vmware-vmx", "vmware-remotemks", "TeamViewer",
        "AnyDesk", "vncviewer", "tvnviewer", "mRemoteNG", "RDCMan", "wfica32", "CDViewer", "parsecd", "rustdesk",
        "RemoteDesktopManager", "RemoteDesktopManager64", "Windows365", "RdClient.Windows", "msrdcw",
    };

    /// <summary>Remote Desktop's own window classes (also when the process name is unknown).</summary>
    private static bool IsRemoteClass(string? cls) => cls is "TscShellContainerClass" or "IHWindowClass" or "OPContainerClass";

    /// <summary>Pure rule (unit-tested).</summary>
    public static bool Is(string? process, string? topClass, string? focusClass) =>
        ClipboardCopy.IsTerminalClass(topClass) || ClipboardCopy.IsTerminalClass(focusClass) || ClipboardCopy.IsTerminalProcess(process) ||
        IsRemoteClass(topClass) || IsRemoteClass(focusClass) || process != null && RemoteProcesses.Contains(process);

    private static readonly object Lock = new();
    private static IntPtr _lastFg, _lastFocus;
    private static bool _last, _lastUnknown;

    private static string ClassOf(IntPtr h)
    {
        var sb = new System.Text.StringBuilder(64);
        int n = Native.GetClassName(h, sb, sb.Capacity);
        return n > 0 ? sb.ToString(0, n) : "";
    }

    /// <summary>The image name (no folder, no .exe) of a process, from <c>OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)</c>
    /// + <c>QueryFullProcessImageName</c> — a few µs, no <see cref="System.Diagnostics.Process"/> object on the hook thread;
    /// null when it cannot be read.</summary>
    internal static string? ImageName(uint pid)
    {
        if (pid == 0) return null;
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int len = sb.Capacity;
            if (!Native.QueryFullProcessImageName(h, 0, sb, ref len)) return null;
            return System.IO.Path.GetFileNameWithoutExtension(sb.ToString(0, len));
        }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Is <paramref name="fg"/> a terminal or a remote / VM window? Any thread (the hook thread at each foreground
    /// change and each focus move inside a window); a few Win32 calls, cached per (window, focused control). A process that
    /// cannot be identified counts as private (silent, nothing tracked) — « ne rien lire si on ne sait pas ».</summary>
    public static bool Check(IntPtr fg) => Check(fg, out _);

    /// <param name="unidentified">the window's process could not be named (counted as private).</param>
    public static bool Check(IntPtr fg, out bool unidentified)
    {
        unidentified = false;
        if (fg == IntPtr.Zero) return false;
        bool r;
        try
        {
            uint tid = Native.GetWindowThreadProcessId(fg, out uint pid);
            var gi = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>() };
            IntPtr focus = tid != 0 && Native.GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;
            lock (Lock) if (fg == _lastFg && focus == _lastFocus) { unidentified = _lastUnknown; return _last; }
            string? image = ImageName(pid);
            unidentified = image is null;
            r = unidentified || Is(image, ClassOf(fg), focus != IntPtr.Zero ? ClassOf(focus) : null);
            lock (Lock) { _lastFg = fg; _lastFocus = focus; _last = r; _lastUnknown = unidentified; }
        }
        catch (Exception) { r = true; unidentified = true; }
        return r;
    }
}
