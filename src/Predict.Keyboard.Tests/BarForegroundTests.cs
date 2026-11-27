using System;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>BUG-1 (VM run 2026-11-26, scenario L24): a UI Automation Invoke from another process made the no-activate bar
/// the foreground for a moment; the controller took that for « our window » (tray menu / À propos), hid the bar, and the
/// Invoke found no tile. The bar is classified apart (<see cref="KeyboardPolicy.ClassifyForeground"/>): no jump, the
/// foreground goes back to the text window (<see cref="KeyboardPolicy.GiveBackTargets"/>), and the Invoke follows
/// « Touches F1–F8 » on that window (<see cref="ListenFirst.InvokeAction"/>).</summary>
public class BarForegroundTests
{
    private static readonly IntPtr Notepad = new(0x202C8), Bar = new(0x4001A), Settings = new(0x5000), Taskbar = new(0x6000);

    [Fact]
    public void The_Bar_Made_Foreground_Is_Not_A_Jump_To_Our_Windows()
    {
        // IsShellOrOwn is true for the bar (same process): the bar is classified first
        Assert.Equal(ForegroundMove.Bar, KeyboardPolicy.ClassifyForeground(Bar, Notepad, Bar, shellOrOwn: true));
    }

    [Theory]
    [InlineData(0x5000, true, (int)ForegroundMove.Own)]     // Réglages, À propos, the tray menu, the reading window: hidden as today
    [InlineData(0x6000, true, (int)ForegroundMove.Own)]     // the taskbar / tray
    [InlineData(0x7000, false, (int)ForegroundMove.Other)]  // another application: a leave
    [InlineData(0x202C8, false, (int)ForegroundMove.Same)]  // the text window again (its return after a give-back)
    public void Other_Windows_Keep_Their_Meaning(long fg, bool shellOrOwn, int expected)
    {
        Assert.Equal((ForegroundMove)expected, KeyboardPolicy.ClassifyForeground(new IntPtr(fg), Notepad, Bar, shellOrOwn));
    }

    [Fact]
    public void Before_The_Bar_Exists_Nothing_Is_Taken_For_It()
    {
        Assert.Equal(ForegroundMove.Own, KeyboardPolicy.ClassifyForeground(Settings, Notepad, IntPtr.Zero, shellOrOwn: true));
        Assert.Equal(ForegroundMove.Same, KeyboardPolicy.ClassifyForeground(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, shellOrOwn: false));
    }

    [Fact]
    public void The_Foreground_Goes_Back_To_The_Window_That_Lost_It_Else_The_Tracked_One_Never_The_Bar()
    {
        // WM_ACTIVATE said who lost it; the tracked window next (the first may be closing or refuse)
        Assert.Equal(new[] { Notepad, Taskbar }, KeyboardPolicy.GiveBackTargets(Notepad, Taskbar, Bar));
        Assert.Equal(new[] { Settings, Notepad }, KeyboardPolicy.GiveBackTargets(Settings, Notepad, Bar));   // even one of ours
        Assert.Equal(new[] { Notepad }, KeyboardPolicy.GiveBackTargets(Notepad, Notepad, Bar));             // no duplicate retry
        Assert.Equal(new[] { Notepad }, KeyboardPolicy.GiveBackTargets(IntPtr.Zero, Notepad, Bar));         // lParam NULL
        Assert.Equal(new[] { Notepad }, KeyboardPolicy.GiveBackTargets(Bar, Notepad, Bar));                 // never the bar itself
        Assert.Equal(new[] { Notepad }, KeyboardPolicy.GiveBackTargets(Notepad, Bar, Bar));
        Assert.Empty(KeyboardPolicy.GiveBackTargets(IntPtr.Zero, Bar, Bar));                                // nothing to give back to:
        Assert.Empty(KeyboardPolicy.GiveBackTargets(IntPtr.Zero, IntPtr.Zero, Bar));                        // the Invoke is ignored
    }

    [Fact]
    public void Two_Invokes_On_The_Text_Window_Listen_Then_Insert()
    {
        // the controller's « uia » path with « Touches F1–F8 » = 1er appui: Press → Listen (armed on the text window),
        // Press again on the same tile and window → Insert
        Assert.Equal(ClickAct.Press, ListenFirst.InvokeAction(keysListenFirst: true));
        var l = new ListenFirst();
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(0, "maison", 9, Notepad));
        Assert.Equal(ListenFirst.Outcome.Insert, l.Press(0, "maison", 9, Notepad));
    }

    [Fact]
    public void A_Press_Tied_To_The_Bar_Would_Never_Confirm_On_The_Text_Window()
    {
        // why the give-back must come before the Invoke's press: a ring armed while the bar is the foreground does not
        // match the second Invoke made once the text window is back (a new first press, never an insertion)
        var l = new ListenFirst();
        l.Press(0, "maison", 9, Bar);
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(0, "maison", 9, Notepad));
    }

    [Fact]
    public void Insert_At_Once_Setting_Inserts_On_The_First_Invoke()
    {
        Assert.Equal(ClickAct.Insert, ListenFirst.InvokeAction(keysListenFirst: false));
    }
}
