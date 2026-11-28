using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>LibreOffice phase 1, S1: every declared IAccessible2 / MSAA method sits at its IDL vtable slot (built-in COM
/// honours the <c>_VtblGap</c> placeholders), and the declared surface is read-only.</summary>
public class Ia2InteropTests
{
    private static readonly Type[] Interfaces =
        { typeof(IAccessibleRaw), typeof(IServiceProviderRaw), typeof(IAccessible2Raw), typeof(IAccessibleTextRaw), typeof(IAccessibleRelationRaw) };

    /// <summary>The IDL slots (IUnknown = 0–2, IDispatch = 3–6): oleacc.idl, servprov.idl, Accessible2.idl, AccessibleText.idl,
    /// AccessibleRelation.idl (phase 2).</summary>
    public static IEnumerable<object[]> Slots() => new[]
    {
        new object[] { typeof(IAccessibleRaw), "get_accChild", 9 },
        new object[] { typeof(IAccessibleRaw), "get_accRole", 13 },
        new object[] { typeof(IAccessibleRaw), "get_accState", 14 },
        new object[] { typeof(IAccessibleRaw), "get_accFocus", 18 },
        new object[] { typeof(IAccessibleRaw), "accLocation", 22 },
        new object[] { typeof(IServiceProviderRaw), "QueryService", 3 },
        new object[] { typeof(IAccessible2Raw), "get_nRelations", 28 },
        new object[] { typeof(IAccessible2Raw), "get_relation", 29 },
        new object[] { typeof(IAccessible2Raw), "role", 31 },
        new object[] { typeof(IAccessibleRelationRaw), "get_relationType", 3 },
        new object[] { typeof(IAccessibleRelationRaw), "get_nTargets", 5 },
        new object[] { typeof(IAccessibleRelationRaw), "get_target", 6 },
        new object[] { typeof(IAccessibleTextRaw), "get_caretOffset", 5 },
        new object[] { typeof(IAccessibleTextRaw), "get_characterExtents", 6 },
        new object[] { typeof(IAccessibleTextRaw), "get_nSelections", 7 },
        new object[] { typeof(IAccessibleTextRaw), "get_selection", 9 },
        new object[] { typeof(IAccessibleTextRaw), "get_text", 10 },
        new object[] { typeof(IAccessibleTextRaw), "get_nCharacters", 17 },
    };

    /// <summary>.NET 10 has no <c>Marshal.GetComSlotForMethodInfo</c>, so the slot is measured for real: a hand-built
    /// in-process COM object whose vtable slot <i>k</i> returns <i>k</i> as its HRESULT is called through the same RCW
    /// path the reader uses. A method that lands one slot off (a <c>_VtblGap</c> ignored) returns the wrong number.</summary>
    [Theory]
    [MemberData(nameof(Slots))]
    public void Each_Declared_Method_Sits_At_Its_Idl_Slot(Type itf, string method, int slot)
    {
        if (!OperatingSystem.IsWindows()) return;   // built-in COM interop: Windows only
        object rcw = SlotObject.Create();
        try { Assert.Equal(slot, SlotObject.Call(rcw, itf, method)); }
        finally { Marshal.FinalReleaseComObject(rcw); }
    }

    /// <summary>Read set: the getters the reader needs. Anything else declared (a setter, a selection or caret move, a
    /// scroll, an editable-text call) fails here; the gaps keep those slots unnamed.</summary>
    [Fact]
    public void Declared_Surface_Is_Read_Only()
    {
        var expected = Slots().Select(o => $"{((Type)o[0]).Name}.{o[1]}").ToHashSet();
        foreach (var itf in Interfaces)
        {
            Assert.True(itf.IsImport, itf.Name);
            foreach (var m in itf.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.Name.StartsWith("_VtblGap", StringComparison.Ordinal)) { Assert.Equal(typeof(void), m.ReturnType); continue; }
                Assert.Contains($"{itf.Name}.{m.Name}", expected);
                Assert.NotNull(m.GetCustomAttribute<PreserveSigAttribute>());
            }
            Assert.Empty(itf.GetProperties());
            // CoreCLR counts only the last of two adjacent gaps (measured in S1): adjacent gaps must be merged
            var names = itf.GetMethods().OrderBy(m => m.MetadataToken).Select(m => m.Name).ToArray();
            for (int i = 1; i < names.Length; i++)
                Assert.False(names[i - 1].StartsWith("_VtblGap", StringComparison.Ordinal) && names[i].StartsWith("_VtblGap", StringComparison.Ordinal),
                             $"{itf.Name}: {names[i - 1]} and {names[i]} are adjacent gaps");
        }
        // no IAccessibleEditableText, no other COM import with a mutating method anywhere in the app's IA2 surface
        var comImports = typeof(IAccessibleRaw).Assembly.GetTypes().Where(t => t.IsInterface && t.IsImport && t.Name.EndsWith("Raw", StringComparison.Ordinal));
        Assert.Equal(Interfaces.OrderBy(t => t.Name), comImports.OrderBy(t => t.Name));
        Assert.DoesNotContain(typeof(IAccessibleRaw).Assembly.GetTypes(), t => t.Name.Contains("EditableText", StringComparison.Ordinal));
        Assert.DoesNotContain(Interfaces.SelectMany(t => t.GetMethods()), m =>
            m.Name.StartsWith("put_", StringComparison.Ordinal) || m.Name.StartsWith("set", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("scroll", StringComparison.OrdinalIgnoreCase) || m.Name is "addSelection" or "removeSelection"
            or "accSelect" or "accDoDefaultAction");
    }

    [Fact]
    public void Interface_Ids_Match_The_Idl()
    {
        Assert.Equal(Ia2.IID_IAccessible, typeof(IAccessibleRaw).GUID);
        Assert.Equal(Ia2.IID_IAccessible2, typeof(IAccessible2Raw).GUID);
        Assert.Equal(Ia2.IID_IAccessibleText, typeof(IAccessibleTextRaw).GUID);
        Assert.Equal(Ia2.IID_IAccessibleRelation, typeof(IAccessibleRelationRaw).GUID);
        Assert.Equal(new Guid("7CDF86EE-C3DA-496A-BDA4-281B336E1FDC"), Ia2.IID_IAccessibleRelation);
        Assert.Equal("flowsTo", Ia2.RELATION_FLOWS_TO);
        Assert.Equal("flowsFrom", Ia2.RELATION_FLOWS_FROM);
        Assert.Equal(new Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), typeof(IServiceProviderRaw).GUID);
        Assert.Equal(0x41E, Ia2.ROLE_PARAGRAPH);
        Assert.Equal(0x414, Ia2.ROLE_HEADING);
    }
}

/// <summary>A native-layout COM object: IUnknown (QI answers the five IA2 interfaces with itself) plus 40 slots, slot k
/// returning k. The delegates and the memory live as long as the test process (a few hundred bytes).</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class SlotObject
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QiFn(IntPtr self, ref Guid iid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RefFn(IntPtr self);

    private const int SlotCount = 40;
    private static readonly List<Delegate> Keep = new();
    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid[] Answered =
    {
        IID_IUnknown, typeof(IAccessibleRaw).GUID, typeof(IServiceProviderRaw).GUID, typeof(IAccessible2Raw).GUID,
        typeof(IAccessibleTextRaw).GUID, typeof(IAccessibleRelationRaw).GUID,
    };
    private static IntPtr _vtable;

    private static IntPtr VTable()
    {
        lock (Keep)
        {
            if (_vtable != IntPtr.Zero) return _vtable;
            var vt = Marshal.AllocHGlobal(SlotCount * IntPtr.Size);
            QiFn qi = (IntPtr self, ref Guid iid, out IntPtr ppv) =>
            {
                foreach (var g in Answered)
                    if (g == iid) { ppv = self; return 0; }
                ppv = IntPtr.Zero;
                return Ia2.E_NOINTERFACE;
            };
            RefFn one = _ => 1;
            Keep.Add(qi); Keep.Add(one);
            Marshal.WriteIntPtr(vt, 0, Marshal.GetFunctionPointerForDelegate(qi));
            Marshal.WriteIntPtr(vt, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(one));
            Marshal.WriteIntPtr(vt, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(one));
            for (int k = 3; k < SlotCount; k++)
            {
                int slot = k;
                RefFn f = _ => slot;   // x64: the callee ignores the arguments the caller passed
                Keep.Add(f);
                Marshal.WriteIntPtr(vt, k * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(f));
            }
            return _vtable = vt;
        }
    }

    public static object Create()
    {
        IntPtr obj = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(obj, VTable());
        return Marshal.GetUniqueObjectForIUnknown(obj);
    }

    /// <summary>Calls one declared method with harmless arguments; returns its HRESULT (= the slot reached).</summary>
    public static int Call(object rcw, Type itf, string method)
    {
        const int self = Native.CHILDID_SELF;
        switch (itf.Name, method)
        {
            case (nameof(IAccessibleRaw), "get_accChild"): return ((IAccessibleRaw)rcw).get_accChild(self, out _);
            case (nameof(IAccessibleRaw), "get_accRole"): return ((IAccessibleRaw)rcw).get_accRole(self, out _);
            case (nameof(IAccessibleRaw), "get_accState"): return ((IAccessibleRaw)rcw).get_accState(self, out _);
            case (nameof(IAccessibleRaw), "get_accFocus"): return ((IAccessibleRaw)rcw).get_accFocus(out _);
            case (nameof(IAccessibleRaw), "accLocation"): return ((IAccessibleRaw)rcw).accLocation(out _, out _, out _, out _, self);
            case (nameof(IServiceProviderRaw), "QueryService"):
                Guid a = Ia2.IID_IAccessible, b = Ia2.IID_IAccessible2;
                return ((IServiceProviderRaw)rcw).QueryService(ref a, ref b, out _);
            case (nameof(IAccessible2Raw), "role"): return ((IAccessible2Raw)rcw).role(out _);
            case (nameof(IAccessible2Raw), "get_nRelations"): return ((IAccessible2Raw)rcw).get_nRelations(out _);
            case (nameof(IAccessible2Raw), "get_relation"): return ((IAccessible2Raw)rcw).get_relation(0, out _);
            case (nameof(IAccessibleRelationRaw), "get_relationType"): return ((IAccessibleRelationRaw)rcw).get_relationType(out _);
            case (nameof(IAccessibleRelationRaw), "get_nTargets"): return ((IAccessibleRelationRaw)rcw).get_nTargets(out _);
            case (nameof(IAccessibleRelationRaw), "get_target"): return ((IAccessibleRelationRaw)rcw).get_target(0, out _);
            case (nameof(IAccessibleTextRaw), "get_caretOffset"): return ((IAccessibleTextRaw)rcw).get_caretOffset(out _);
            case (nameof(IAccessibleTextRaw), "get_characterExtents"):
                return ((IAccessibleTextRaw)rcw).get_characterExtents(0, Ia2.COORDTYPE_SCREEN_RELATIVE, out _, out _, out _, out _);
            case (nameof(IAccessibleTextRaw), "get_nSelections"): return ((IAccessibleTextRaw)rcw).get_nSelections(out _);
            case (nameof(IAccessibleTextRaw), "get_selection"): return ((IAccessibleTextRaw)rcw).get_selection(0, out _, out _);
            case (nameof(IAccessibleTextRaw), "get_text"): return ((IAccessibleTextRaw)rcw).get_text(0, 0, out _);
            case (nameof(IAccessibleTextRaw), "get_nCharacters"): return ((IAccessibleTextRaw)rcw).get_nCharacters(out _);
            default: throw new ArgumentException($"{itf.Name}.{method}: no call written for it");
        }
    }
}
