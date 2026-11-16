using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Predict.Keyboard;

/// <summary>Replaces the word at the caret in the focused application: N forward Deletes (the part after
/// the caret, known only from a read), M Backspaces (the part before it), then the chosen text as Unicode
/// keystrokes (works in any app, any keyboard layout) — all in one SendInput so nothing typed by the user
/// can land in between. Every injected event carries <c>marker</c> in dwExtraInfo so our own hook ignores
/// it (an injected Delete must not count as a caret jump).</summary>
internal static class Inserter
{
    public const ushort VK_BACK = 0x08, SCAN_DELETE = 0x53, VK_LEFT = 0x25, VK_RIGHT = 0x27;

    /// <summary>The events of <see cref="Replace"/>, in order (pure, unit-tested).</summary>
    public static Native.INPUT[] Build(int deletes, int backspaces, string text, UIntPtr marker)
    {
        var list = new List<Native.INPUT>((deletes + backspaces + text.Length) * 2);
        for (int i = 0; i < deletes; i++)
        {
            list.Add(Key(Native.VK_DELETE, SCAN_DELETE, Native.KEYEVENTF_EXTENDEDKEY, marker));
            list.Add(Key(Native.VK_DELETE, SCAN_DELETE, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP, marker));
        }
        for (int i = 0; i < backspaces; i++)
        {
            list.Add(Key(VK_BACK, 0, 0, marker));
            list.Add(Key(VK_BACK, 0, Native.KEYEVENTF_KEYUP, marker));
        }
        foreach (var ch in text)
        {
            list.Add(Key(0, ch, Native.KEYEVENTF_UNICODE, marker));
            list.Add(Key(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP, marker));
        }
        return list.ToArray();
    }

    /// <summary>Left, then Backspace (<paramref name="removeOne"/>) or <paramref name="text"/>, then Right: edits just
    /// before the character left of the caret without retyping it (no app autocorrect re-run). Pure.</summary>
    public static Native.INPUT[] BuildBeforeLast(bool removeOne, string text, UIntPtr marker)
    {
        var list = new List<Native.INPUT>
        {
            Key(VK_LEFT, 0, Native.KEYEVENTF_EXTENDEDKEY, marker),
            Key(VK_LEFT, 0, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP, marker),
        };
        if (removeOne)
        {
            list.Add(Key(VK_BACK, 0, 0, marker));
            list.Add(Key(VK_BACK, 0, Native.KEYEVENTF_KEYUP, marker));
        }
        else
            foreach (var ch in text)
            {
                list.Add(Key(0, ch, Native.KEYEVENTF_UNICODE, marker));
                list.Add(Key(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP, marker));
            }
        list.Add(Key(VK_RIGHT, 0, Native.KEYEVENTF_EXTENDEDKEY, marker));
        list.Add(Key(VK_RIGHT, 0, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP, marker));
        return list.ToArray();
    }

    public static (uint Sent, int Expected, int Error) Send(Native.INPUT[] arr)
    {
        uint sent = Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Native.INPUT>());
        return (sent, arr.Length, sent == arr.Length ? 0 : Marshal.GetLastWin32Error());
    }

    /// <returns>(events injected, events expected, Win32 error) — SendInput fails silently when blocked
    /// (UIPI, secure desktop, disconnected session).</returns>
    public static (uint Sent, int Expected, int Error) Replace(int deletes, int backspaces, string text, UIntPtr marker)
    {
        var arr = Build(deletes, backspaces, text, marker);
        uint sent = Native.SendInput((uint)arr.Length, arr, Marshal.SizeOf<Native.INPUT>());
        return (sent, arr.Length, sent == arr.Length ? 0 : Marshal.GetLastWin32Error());
    }

    private static Native.INPUT Key(ushort vk, ushort scan, uint flags, UIntPtr marker) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = marker }
        }
    };
}
