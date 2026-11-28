using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Predict.Keyboard;

// LibreOffice Writer phase 1: read-only IAccessible2 interop, built-in COM ([ComImport], [PreserveSig], vtable gaps).
// Interface identifiers and method order transcribed from the IAccessible2 IDL (LinuxA11y/IAccessible2
// master@c9ae003d9c85eb707716928de97e055f5b77189c, 2025-05-05; BSD-3-Clause, licenses\iaccessible2\LICENSE.txt) and the
// MSAA / COM headers (IAccessible, IServiceProvider). Only the reading methods are declared: every other slot, mutating
// ones included, is a _VtblGap, so no caller can name it. IAccessibleEditableText is never declared. Ia2InteropTests checks
// each declared slot and the read-only surface.

/// <summary>MSAA <c>IAccessible</c> (oleacc.idl), reading subset. Slots 3–6 are IDispatch.</summary>
[ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAccessibleRaw
{
    // One gap for 3–8: CoreCLR (.NET 10) does not add up two consecutive _VtblGap methods (only the last one counts,
    // measured by Ia2InteropTests), so adjacent gaps are always merged into one.
    void _VtblGap1_6();                                                                                      // IDispatch 3–6, get_accParent, get_accChildCount
    [PreserveSig] int get_accChild([MarshalAs(UnmanagedType.Struct)] object child,
                                   [MarshalAs(UnmanagedType.IDispatch)] out object? disp);                    // 9
    void _VtblGap2_3();                                                                                      // name, value, description
    [PreserveSig] int get_accRole([MarshalAs(UnmanagedType.Struct)] object child,
                                  [MarshalAs(UnmanagedType.Struct)] out object? role);                        // 13
    [PreserveSig] int get_accState([MarshalAs(UnmanagedType.Struct)] object child,
                                   [MarshalAs(UnmanagedType.Struct)] out object? state);                      // 14
    void _VtblGap3_3();                                                                                      // help, helpTopic, keyboardShortcut
    [PreserveSig] int get_accFocus([MarshalAs(UnmanagedType.Struct)] out object? focus);                      // 18
    void _VtblGap4_3();                                                                                      // selection, defaultAction, accSelect
    [PreserveSig] int accLocation(out int left, out int top, out int width, out int height,
                                  [MarshalAs(UnmanagedType.Struct)] object child);                            // 22
    // accNavigate … put_accValue (23–27): not declared
}

/// <summary>COM <c>IServiceProvider</c>: IAccessible2 is reachable only through QueryService (phase 0 probe).</summary>
[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProviderRaw
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IntPtr obj);                           // 3
}

/// <summary><c>IAccessible2</c> (Accessible2.idl), reading subset: IDispatch 4 + IAccessible 21 = 25 slots skipped, then the
/// relations (phase 2: the selection walk follows <c>flowsFrom</c> / <c>flowsTo</c>) and <c>role</c>.</summary>
[ComImport, Guid("E89F726E-C4F4-4C19-BB19-B647D7FA8478"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAccessible2Raw
{
    void _VtblGap1_25();                                                                                     // 3–27
    [PreserveSig] int get_nRelations(out int count);                                                         // 28
    /// <summary>An AddRef'd <c>IAccessibleRelation</c> (the caller releases it).</summary>
    [PreserveSig] int get_relation(int index, out IntPtr relation);                                          // 29
    void _VtblGap2_1();                                                                                      // relations
    [PreserveSig] int role(out int role);                                                                    // 31
}

/// <summary><c>IAccessibleRelation</c> (AccessibleRelation.idl), reading subset (phase 2).</summary>
[ComImport, Guid("7CDF86EE-C3DA-496A-BDA4-281B336E1FDC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAccessibleRelationRaw
{
    [PreserveSig] int get_relationType([MarshalAs(UnmanagedType.BStr)] out string? type);                    // 3
    void _VtblGap1_1();                                                                                      // localizedRelationType
    [PreserveSig] int get_nTargets(out int count);                                                           // 5
    /// <summary>An AddRef'd <c>IUnknown</c> (the caller releases it).</summary>
    [PreserveSig] int get_target(int index, out IntPtr target);                                              // 6
    // targets (7): not declared
}

/// <summary><c>IAccessibleText</c> (AccessibleText.idl), reading subset. Offsets are paragraph-relative.</summary>
[ComImport, Guid("24FD2FFB-3AAD-4A08-8335-A3AD89C0FB4B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAccessibleTextRaw
{
    void _VtblGap1_2();                                                                                      // addSelection, get_attributes
    [PreserveSig] int get_caretOffset(out int offset);                                                       // 5
    [PreserveSig] int get_characterExtents(int offset, int coordType, out int x, out int y, out int width, out int height); // 6
    [PreserveSig] int get_nSelections(out int count);                                                        // 7
    void _VtblGap2_1();                                                                                      // get_offsetAtPoint
    [PreserveSig] int get_selection(int index, out int start, out int end);                                  // 9
    [PreserveSig] int get_text(int start, int end, [MarshalAs(UnmanagedType.BStr)] out string? text);        // 10
    void _VtblGap3_6();                                                                                      // textBefore/After/AtOffset, removeSelection, setCaretOffset, setSelection
    [PreserveSig] int get_nCharacters(out int count);                                                        // 17
    // scrollSubstringTo … oldText (18–21): not declared
}

/// <summary>Constants of the IA2 IDL and MSAA used by the reader.</summary>
internal static class Ia2
{
    public static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");
    public static readonly Guid IID_IAccessible2 = new("E89F726E-C4F4-4C19-BB19-B647D7FA8478");
    public static readonly Guid IID_IAccessibleText = new("24FD2FFB-3AAD-4A08-8335-A3AD89C0FB4B");
    public static readonly Guid IID_IServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    public static readonly Guid IID_IAccessibleRelation = new("7CDF86EE-C3DA-496A-BDA4-281B336E1FDC");

    /// <summary>AccessibleRelation.idl relation types (phase 2 walk): the next / previous content in reading order.</summary>
    public const string RELATION_FLOWS_TO = "flowsTo", RELATION_FLOWS_FROM = "flowsFrom";

    public const int ROLE_HEADING = 0x414, ROLE_PARAGRAPH = 0x41E, ROLE_EDITBAR = 0x409;   // AccessibleRole.idl
    public const int COORDTYPE_SCREEN_RELATIVE = 0;                                         // IA2CommonTypes.idl
    public const int STATE_SYSTEM_SELECTED = 0x2, STATE_SYSTEM_FOCUSED = 0x4, STATE_SYSTEM_PROTECTED = 0x20000000;   // oleacc.h

    // HRESULTs the reader tells apart (LibreOfficeText.Classify)
    public const int S_OK = 0, S_FALSE = 1;
    public const int E_NOINTERFACE = unchecked((int)0x80004002), E_FAIL = unchecked((int)0x80004005);
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int REGDB_E_IIDNOTREG = unchecked((int)0x80040155);
    public const int RPC_E_CALL_REJECTED = unchecked((int)0x80010001), RPC_E_DISCONNECTED = unchecked((int)0x80010108);
    public const int CO_E_OBJNOTCONNECTED = unchecked((int)0x800401FD);
    public const int RPC_E_SERVER_DIED = unchecked((int)0x80010007), RPC_E_SERVER_DIED_DNE = unchecked((int)0x80010012);
    public const int RPC_S_SERVER_UNAVAILABLE = unchecked((int)0x800706BA);
}

/// <summary>One focused accessible object of LibreOffice, resolved down to its <c>IAccessibleText</c> (the seam that
/// <see cref="LibreOfficeText"/> is tested through). Every method returns an HRESULT and never throws; never logs text.
/// Called from MTA threads only; <see cref="IDisposable.Dispose"/> by the last lease holder.</summary>
internal interface ILoParagraph : IDisposable
{
    /// <summary>The IA2 role, read once with the object (0 = unknown / no IAccessible2).</summary>
    int Role { get; }
    int State(out int state);
    int CaretOffset(out int offset);
    int SelectionCount(out int count);
    int Selection(int index, out int start, out int end);
    int CharacterCount(out int count);
    int Text(int start, int end, out string? text);
    /// <summary>Screen coordinates.</summary>
    int CharacterExtents(int offset, out int x, out int y, out int width, out int height);
    /// <summary>The object's own screen rectangle (<c>accLocation</c>, CHILDID_SELF).</summary>
    int Location(out int x, out int y, out int width, out int height);
    /// <summary>Phase 2: the next (<c>flowsTo</c>) or previous (<c>flowsFrom</c>) content object in reading order, resolved
    /// like a focused object (role, IAccessibleText); S_FALSE and null when there is none (first / last paragraph, header,
    /// footer, text frame). The caller owns and disposes it.</summary>
    int Neighbour(bool next, out ILoParagraph? paragraph);
}

/// <summary>How <see cref="LibreOfficeText"/> reaches LibreOffice and the window manager: <see cref="ComLoBackend"/> in
/// the app, a fake in the tests.</summary>
internal interface ILoBackend
{
    uint PidOf(IntPtr fg);
    IntPtr Foreground();
    bool WindowRect(IntPtr fg, out int left, out int top, out int right, out int bottom);
    /// <summary>soffice.bin's FileVersion for the once-per-pid log line (no message sent to LibreOffice); null if unknown.</summary>
    string? ProductVersion(uint pid);
    /// <summary>A monotonic clock in milliseconds (budgets, the breaker's stuck detection, the caret-point cache).</summary>
    double NowMs();
    /// <summary>The object a focus WinEvent named: find, child, QueryService, IAccessibleText, role.</summary>
    int FromEvent(IntPtr hwnd, int objectId, int childId, out ILoParagraph? paragraph);
    /// <summary>Second resort: the focused child window's OBJID_CLIENT object, then the <c>accFocus</c> chain.</summary>
    int FromFocusWalk(IntPtr fg, out ILoParagraph? paragraph);
}

/// <summary>
/// The real backend: <c>oleacc</c> + Windows' own <c>ia2comproxy.dll</c> (nothing installed or shipped). Every kept RCW
/// is a unique one (<see cref="Marshal.GetUniqueObjectForIUnknown"/>), so releasing a superseded paragraph never breaks
/// another entry that names the same LibreOffice object; transient RCWs (the walk, a child lookup) are released at once.
/// Call it from MTA threads only.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ComLoBackend : ILoBackend
{
    private const int WalkDepth = 30;
    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    public uint PidOf(IntPtr fg) { Native.GetWindowThreadProcessId(fg, out uint pid); return pid; }
    public IntPtr Foreground() => Native.GetForegroundWindow();
    public double NowMs() => Stopwatch.GetTimestamp() * TickMs;

    public bool WindowRect(IntPtr fg, out int left, out int top, out int right, out int bottom)
    {
        bool ok = Native.GetWindowRect(fg, out var r);
        (left, top, right, bottom) = (r.Left, r.Top, r.Right, r.Bottom);
        return ok;
    }

    public string? ProductVersion(uint pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!Native.QueryFullProcessImageName(h, 0, sb, ref size)) return null;
            return FileVersionInfo.GetVersionInfo(sb.ToString(0, size)).FileVersion;
        }
        catch (Exception) { return null; }
        finally { Native.CloseHandle(h); }
    }

    public int FromEvent(IntPtr hwnd, int objectId, int childId, out ILoParagraph? paragraph)
    {
        paragraph = null;
        object? acc = null;
        try
        {
            int hr = Native.AccessibleObjectFromEvent(hwnd, unchecked((uint)objectId), unchecked((uint)childId), out IntPtr p, out object? child);
            if (hr < 0 || p == IntPtr.Zero) return hr < 0 ? hr : Ia2.E_FAIL;
            try { acc = Marshal.GetUniqueObjectForIUnknown(p); } finally { Marshal.Release(p); }
            if (child is int id && id != Native.CHILDID_SELF)
            {
                hr = ChildOf(acc, id, out object? obj);
                Marshal.FinalReleaseComObject(acc);
                acc = obj;
                if (hr < 0) return hr;
            }
            hr = Complete(acc!, out paragraph);
            acc = null;   // owned by the paragraph, or released by Complete
            return hr;
        }
        catch (Exception ex) { return HrOf(ex); }
        finally { if (acc is not null) Free(acc); }
    }

    public int FromFocusWalk(IntPtr fg, out ILoParagraph? paragraph)
    {
        paragraph = null;
        object? acc = null;
        try
        {
            uint tid = Native.GetWindowThreadProcessId(fg, out _);
            var gi = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
            IntPtr focus = Native.GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;
            if (focus == IntPtr.Zero) return Ia2.E_FAIL;
            var iid = Ia2.IID_IAccessible;
            int hr = Native.AccessibleObjectFromWindow(focus, unchecked((uint)Native.OBJID_CLIENT), ref iid, out IntPtr p);
            if (hr < 0 || p == IntPtr.Zero) return hr < 0 ? hr : Ia2.E_FAIL;
            try { acc = Marshal.GetUniqueObjectForIUnknown(p); } finally { Marshal.Release(p); }
            for (int i = 0; i < WalkDepth; i++)
            {
                hr = Qi(acc, Ia2.IID_IAccessible);
                if (hr < 0) return hr;
                hr = ((IAccessibleRaw)acc).get_accFocus(out object? f);
                if (hr < 0) return hr;
                object? next = null;
                if (f is int id && id != Native.CHILDID_SELF)
                {
                    if (ChildOf(acc, id, out next) < 0) break;   // a simple child: acc is the deepest object
                }
                else if (f is not null && Marshal.IsComObject(f))
                {
                    next = Unique(f);
                    Marshal.ReleaseComObject(f);
                }
                if (next is null) break;                        // VT_EMPTY / CHILDID_SELF: acc has the focus itself
                if (SameObject(next, acc)) { Free(next); break; }
                Free(acc);
                acc = next;
            }
            hr = Complete(acc, out paragraph);
            acc = null;
            return hr;
        }
        catch (Exception ex) { return HrOf(ex); }
        finally { if (acc is not null) Free(acc); }
    }

    /// <summary>QueryService for IAccessible2 (a direct QI when the object has no IServiceProvider: a relation target), the
    /// role, then IAccessibleText (direct QI first, else on the IA2 pointer).
    /// Takes ownership of <paramref name="acc"/>. Every interface is asked with <see cref="Qi"/>, so a dead or
    /// disconnected server comes back as its own HRESULT (the breaker forgets the pid / drops the cache) and only a real
    /// E_NOINTERFACE is structural. A known role that is not text (a toolbar, a dialog's button) without IAccessibleText is
    /// a paragraph with no text (S_OK): the reader's role filter makes it <c>Unavailable</c>, a normal failure — only no
    /// IAccessible2, or a paragraph / heading / unknown role without IAccessibleText, is structural.</summary>
    private static int Complete(object acc, out ILoParagraph? paragraph)
    {
        paragraph = null;
        object? ia2 = null;
        try
        {
            int hr = Qi(acc, Ia2.IID_IAccessible);
            if (hr < 0) { Free(acc); return hr; }
            var a = (IAccessibleRaw)acc;
            hr = Qi(acc, Ia2.IID_IServiceProvider);
            if (hr >= 0)
            {
                var sp = (IServiceProviderRaw)acc;
                Guid sid = Ia2.IID_IAccessible, iid = Ia2.IID_IAccessible2;
                hr = sp.QueryService(ref sid, ref iid, out IntPtr p2);
                if (hr < 0 || p2 == IntPtr.Zero) { Free(acc); return hr < 0 ? hr : Ia2.E_NOINTERFACE; }
                try { ia2 = Marshal.GetUniqueObjectForIUnknown(p2); } finally { Marshal.Release(p2); }
            }
            else if (hr == Ia2.E_NOINTERFACE)
            {
                // a relation target without IServiceProvider (phase 2): IAccessible2 by a direct QI, its own HRESULT if refused
                hr = Qi(acc, Ia2.IID_IAccessible2);
                if (hr < 0) { Free(acc); return hr; }
                ia2 = Unique(acc);
            }
            else { Free(acc); return hr; }
            int role = 0;
            if (Qi(ia2, Ia2.IID_IAccessible2) >= 0 && ((IAccessible2Raw)ia2).role(out int rv) == Ia2.S_OK) role = rv;
            IAccessibleTextRaw? text = null;
            hr = Qi(acc, Ia2.IID_IAccessibleText);
            if (hr >= 0) text = (IAccessibleTextRaw)acc;
            else if (hr == Ia2.E_NOINTERFACE && (hr = Qi(ia2, Ia2.IID_IAccessibleText)) >= 0) text = (IAccessibleTextRaw)ia2;
            if (text is null)
            {
                bool knownNonText = role != 0 && role != Ia2.ROLE_PARAGRAPH && role != Ia2.ROLE_HEADING;
                if (hr == Ia2.E_NOINTERFACE && knownNonText)
                {
                    paragraph = new ComParagraph(a, null, ia2, role);
                    return Ia2.S_OK;
                }
                Free(acc); Free(ia2);
                return hr < 0 ? hr : Ia2.E_NOINTERFACE;
            }
            paragraph = new ComParagraph(a, text, ia2, role);
            return Ia2.S_OK;
        }
        catch (Exception ex)
        {
            Free(acc);
            if (ia2 is not null) Free(ia2);
            return HrOf(ex);
        }
    }

    /// <summary>QueryInterface with its HRESULT (a cast would turn RPC_E_DISCONNECTED or a dead server into « no
    /// interface », which the breaker counts as structural). The proxy caches the interface, so the cast that follows is
    /// local.</summary>
    private static int Qi(object o, Guid iid)
    {
        IntPtr unk = Marshal.GetIUnknownForObject(o);
        try
        {
            int hr = Marshal.QueryInterface(unk, in iid, out IntPtr p);
            if (p != IntPtr.Zero) Marshal.Release(p);
            return hr >= 0 && p == IntPtr.Zero ? Ia2.E_NOINTERFACE : hr;
        }
        finally { Marshal.Release(unk); }
    }

    private static int ChildOf(object parent, int id, out object? child)
    {
        child = null;
        int hr = Qi(parent, Ia2.IID_IAccessible);
        if (hr < 0) return hr;
        var a = (IAccessibleRaw)parent;
        hr = a.get_accChild(id, out object? disp);
        if (hr < 0) return hr;
        if (disp is null) return Ia2.E_FAIL;
        child = Unique(disp);
        Marshal.ReleaseComObject(disp);
        return Ia2.S_OK;
    }

    private static object Unique(object shared)
    {
        IntPtr p = Marshal.GetIUnknownForObject(shared);
        try { return Marshal.GetUniqueObjectForIUnknown(p); } finally { Marshal.Release(p); }
    }

    private static bool SameObject(object x, object y)
    {
        IntPtr a = Marshal.GetIUnknownForObject(x), b = Marshal.GetIUnknownForObject(y);
        try { return a == b; } finally { Marshal.Release(a); Marshal.Release(b); }
    }

    internal static void Free(object? o)
    {
        if (o is null || !Marshal.IsComObject(o)) return;
        try { Marshal.FinalReleaseComObject(o); } catch (Exception) { }
    }

    internal static int HrOf(Exception ex) => ex switch
    {
        InvalidCastException => Ia2.E_NOINTERFACE,
        InvalidComObjectException => Ia2.CO_E_OBJNOTCONNECTED,
        _ => ex.HResult < 0 ? ex.HResult : Ia2.E_FAIL,
    };

    /// <summary>The kept RCWs of one paragraph. A call never throws: a COM error comes back as its HRESULT.</summary>
    private sealed class ComParagraph : ILoParagraph
    {
        private static readonly object Self = Native.CHILDID_SELF;   // VT_I4 0
        private readonly IAccessibleRaw _acc;
        // null: a known non-text role (toolbar, button…): the reader's role filter stops before any text call, and a text
        // call returns E_NOTIMPL (a normal failure, never counted as structural)
        private readonly IAccessibleTextRaw? _text;
        private readonly object? _ia2;
        private int _disposed;

        public ComParagraph(IAccessibleRaw acc, IAccessibleTextRaw? text, object? ia2, int role)
        {
            _acc = acc; _text = text; _ia2 = ia2; Role = role;
        }

        public int Role { get; }

        public int State(out int state)
        {
            state = 0;
            try
            {
                int hr = _acc.get_accState(Self, out object? v);
                if (hr >= 0 && v is int s) state = s;
                return hr;
            }
            catch (Exception ex) { return HrOf(ex); }
        }

        public int CaretOffset(out int offset)
        {
            offset = -1;
            try { return _text is null ? Ia2.E_NOTIMPL : _text.get_caretOffset(out offset); } catch (Exception ex) { return HrOf(ex); }
        }

        public int SelectionCount(out int count)
        {
            count = 0;
            try { return _text is null ? Ia2.E_NOTIMPL : _text.get_nSelections(out count); } catch (Exception ex) { return HrOf(ex); }
        }

        public int Selection(int index, out int start, out int end)
        {
            start = end = 0;
            try { return _text is null ? Ia2.E_NOTIMPL : _text.get_selection(index, out start, out end); } catch (Exception ex) { return HrOf(ex); }
        }

        public int CharacterCount(out int count)
        {
            count = 0;
            try { return _text is null ? Ia2.E_NOTIMPL : _text.get_nCharacters(out count); } catch (Exception ex) { return HrOf(ex); }
        }

        public int Text(int start, int end, out string? text)
        {
            text = null;
            try { return _text is null ? Ia2.E_NOTIMPL : _text.get_text(start, end, out text); } catch (Exception ex) { return HrOf(ex); }
        }

        public int CharacterExtents(int offset, out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            try { return _text is null ? Ia2.E_NOTIMPL : _text.get_characterExtents(offset, Ia2.COORDTYPE_SCREEN_RELATIVE, out x, out y, out width, out height); }
            catch (Exception ex) { return HrOf(ex); }
        }

        public int Location(out int x, out int y, out int width, out int height)
        {
            x = y = width = height = 0;
            try { return _acc.accLocation(out x, out y, out width, out height, Self); } catch (Exception ex) { return HrOf(ex); }
        }

        /// <summary>At most this many relations are looked at (Writer paragraphs have 0–2).</summary>
        private const int MaxRelations = 16;

        public int Neighbour(bool next, out ILoParagraph? paragraph)
        {
            paragraph = null;
            if (_ia2 is null) return Ia2.S_FALSE;
            object? rel = null;
            try
            {
                var a2 = (IAccessible2Raw)_ia2;
                int hr = a2.get_nRelations(out int count);
                if (hr < 0) return hr;
                string want = next ? Ia2.RELATION_FLOWS_TO : Ia2.RELATION_FLOWS_FROM;
                for (int i = 0; i < Math.Min(count, MaxRelations); i++)
                {
                    hr = a2.get_relation(i, out IntPtr rp);
                    if (hr < 0) return hr;
                    if (rp == IntPtr.Zero) continue;
                    try { rel = Marshal.GetUniqueObjectForIUnknown(rp); } finally { Marshal.Release(rp); }
                    hr = Qi(rel, Ia2.IID_IAccessibleRelation);
                    if (hr < 0) return hr;
                    var r = (IAccessibleRelationRaw)rel;
                    hr = r.get_relationType(out string? type);
                    if (hr < 0) return hr;
                    if (type != want) { Free(rel); rel = null; continue; }
                    hr = r.get_nTargets(out int targets);
                    if (hr < 0) return hr;
                    if (targets < 1) return Ia2.S_FALSE;
                    hr = r.get_target(0, out IntPtr tp);
                    if (hr < 0) return hr;
                    if (tp == IntPtr.Zero) return Ia2.S_FALSE;
                    object target;
                    try { target = Marshal.GetUniqueObjectForIUnknown(tp); } finally { Marshal.Release(tp); }
                    return Complete(target, out paragraph);   // takes ownership of the target
                }
                return Ia2.S_FALSE;
            }
            catch (Exception ex) { return HrOf(ex); }
            finally { if (rel is not null) Free(rel); }
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Free(_acc);
            if (_text is not null && !ReferenceEquals(_text, _acc)) Free(_text);
            if (_ia2 is not null && !ReferenceEquals(_ia2, _acc) && !ReferenceEquals(_ia2, _text)) Free(_ia2);
        }
    }
}
