using System;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>WR5 (0.11.0): a foreground event against the real foreground (pure policy, no window).</summary>
public class ForegroundEventsTests
{
    private static readonly IntPtr Word = 100, Form = 200, Third = 300, Bar = 900, Owner = 400, Owned = 401,
                                   Frame = 500, Core = 501, None = IntPtr.Zero;

    [Fact]
    public void EventForTheForeground_IsAppliedAndSupersedes()
    {
        var d = ForegroundEvents.Decide(Form, Form, Form, Word, Bar);
        Assert.Equal(Form, d.Apply);
        Assert.False(d.Recheck);
        Assert.True(d.NewGeneration);
    }

    /// <summary>The WR5 sequence: the form's event while Word keeps the foreground (activation refused) → nothing now, a
    /// re-check; the real activation's event names the form again → applied (it is no longer « the same window »).</summary>
    [Fact]
    public void RefusedThenGrantedActivation_IsOneSwitchToTheNewWindow()
    {
        var first = ForegroundEvents.Decide(Form, Form, Word, Word, Bar);
        Assert.Equal(None, first.Apply);
        Assert.True(first.Recheck);
        // the tracked window is still Word, so the second event is a change
        var second = ForegroundEvents.Decide(Form, Form, Form, Word, Bar);
        Assert.Equal(Form, second.Apply);
        Assert.True(second.NewGeneration);   // cancels the first one's re-check
    }

    [Fact]
    public void RefusedActivation_RecheckFindsTheOldWindow_NothingChanges()
    {
        var d = ForegroundEvents.Decide(Form, Form, Word, Word, Bar);
        Assert.Equal(None, d.Apply);
        // Word still in front at every re-check: nothing applied, re-armed until the cap (3 re-checks in all)
        var r1 = ForegroundEvents.Recheck(1, 1, Word, Word, Bar, attempt: 1);
        Assert.Equal(None, r1.Apply);
        Assert.Equal(ForegroundEvents.RearmMs, r1.RearmMs);
        Assert.Equal(ForegroundEvents.RearmMs, ForegroundEvents.Recheck(1, 1, Word, Word, Bar, attempt: 2).RearmMs);
        var r3 = ForegroundEvents.Recheck(1, 1, Word, Word, Bar, attempt: ForegroundEvents.MaxRechecks);
        Assert.Equal(None, r3.Apply);
        Assert.Equal(0, r3.RearmMs);
    }

    /// <summary>An activation granted late, without a second foreground event: a later re-check applies it.</summary>
    [Fact]
    public void LateActivationWithoutASecondEvent_IsAppliedByARearmedRecheck()
    {
        Assert.Equal(ForegroundEvents.RearmMs, ForegroundEvents.Recheck(1, 1, Word, Word, Bar, attempt: 1).RearmMs);
        var r2 = ForegroundEvents.Recheck(1, 1, Form, Word, Bar, attempt: 2);
        Assert.Equal(Form, r2.Apply);
        Assert.Equal(0, r2.RearmMs);
    }

    [Fact]
    public void Recheck_IsCancelledByANewerEvent()
    {
        var old = ForegroundEvents.Recheck(scheduled: 1, current: 2, real: Form, last: Word, bar: Bar, attempt: 1);
        Assert.Equal(None, old.Apply);
        Assert.Equal(0, old.RearmMs);
        Assert.Equal(Form, ForegroundEvents.Recheck(scheduled: 2, current: 2, real: Form, last: Word, bar: Bar, attempt: 1).Apply);
    }

    /// <summary>No foreground yet (a switch in progress): nothing now, once more later, within the cap.</summary>
    [Fact]
    public void Recheck_WithNoForeground_Rearms()
    {
        var r = ForegroundEvents.Recheck(3, 3, None, Word, Bar, attempt: 1);
        Assert.Equal(None, r.Apply);
        Assert.Equal(ForegroundEvents.RearmMs, r.RearmMs);
        Assert.Equal(0, ForegroundEvents.Recheck(3, 3, None, Word, Bar, attempt: ForegroundEvents.MaxRechecks).RearmMs);
    }

    /// <summary>The bar in front at a re-check is never applied (BUG-1): only re-armed.</summary>
    [Fact]
    public void Recheck_BarInFront_NotApplied()
    {
        var r = ForegroundEvents.Recheck(1, 1, Bar, Word, Bar, attempt: 1);
        Assert.Equal(None, r.Apply);
        Assert.Equal(ForegroundEvents.RearmMs, r.RearmMs);
    }

    /// <summary>A UWP app: the event names its inner window, whose root is the frame GetForegroundWindow returns → the frame,
    /// handled as an ordinary event (no re-check, no « not in front » log line).</summary>
    [Fact]
    public void FrameAndInnerWindow_IsTheFrame_NoRecheck()
    {
        var d = ForegroundEvents.Decide(Core, Frame, Frame, Word, Bar);
        Assert.Equal(Frame, d.Apply);
        Assert.False(d.Recheck);
        Assert.True(d.NewGeneration);
    }

    /// <summary>An inner window whose root is not the foreground: like any other window (re-check).</summary>
    [Fact]
    public void InnerWindowOfAnotherFrame_Rechecks()
    {
        var d = ForegroundEvents.Decide(Core, Frame, Word, Word, Bar);
        Assert.Equal(None, d.Apply);
        Assert.True(d.Recheck);
    }

    /// <summary>An owned window (a dialog) named while its owner is the real foreground, the owner being tracked already:
    /// nothing now, a re-check (the dialog's own activation event follows when it gets the foreground).</summary>
    [Fact]
    public void OwnedWindowNamed_OwnerInFront_WaitsForTheRealActivation()
    {
        var d = ForegroundEvents.Decide(Owned, Owned, Owner, Owner, Bar);
        Assert.Equal(None, d.Apply);
        Assert.True(d.Recheck);
        Assert.Equal(Owned, ForegroundEvents.Decide(Owned, Owned, Owned, Owner, Bar).Apply);
    }

    /// <summary>BUG-1: the bar activated by UI Automation is handed to the foreground path as before (which ignores it); it
    /// neither cancels a pending re-check nor asks one, whatever the real foreground says.</summary>
    [Theory]
    [InlineData(900)]   // the bar is the real foreground
    [InlineData(100)]   // the bar has given it back already
    public void BarEvent_IsUnchanged(int real)
    {
        var d = ForegroundEvents.Decide(Bar, Bar, (IntPtr)real, Word, Bar);
        Assert.Equal(Bar, d.Apply);
        Assert.False(d.Recheck);
        Assert.False(d.NewGeneration);
    }

    /// <summary>An event for another window while the bar is the real foreground: never apply the bar from here.</summary>
    [Fact]
    public void EventElsewhere_BarInFront_NotApplied()
    {
        var d = ForegroundEvents.Decide(Form, Form, Bar, Word, Bar);
        Assert.Equal(None, d.Apply);
        Assert.True(d.Recheck);
    }

    /// <summary>Rapid A → B → C: B's event arrives with C already in front → C now; C's own event is then the tracked
    /// window (ignored by the foreground path) and cancels B's re-check.</summary>
    [Fact]
    public void RapidDoubleSwitch_EndsOnTheLastWindow()
    {
        var b = ForegroundEvents.Decide(Form, Form, Third, Word, Bar);
        Assert.Equal(Third, b.Apply);
        Assert.True(b.Recheck);
        var c = ForegroundEvents.Decide(Third, Third, Third, Third, Bar);
        Assert.Equal(Third, c.Apply);         // the same window: OnForeground returns at once
        Assert.True(c.NewGeneration);
        var stale = ForegroundEvents.Recheck(1, 2, Third, Third, Bar, attempt: 1);
        Assert.Equal(None, stale.Apply);
        Assert.Equal(0, stale.RearmMs);
    }

    [Fact]
    public void EventDuringASwitch_NoForeground_OnlyARecheck()
    {
        var d = ForegroundEvents.Decide(Form, Form, None, Word, Bar);
        Assert.Equal(None, d.Apply);
        Assert.True(d.Recheck);
    }

    [Fact]
    public void NoBarYet_ZeroBarHandle_IsNotMistakenForTheBar()
    {
        var d = ForegroundEvents.Decide(Form, Form, Form, Word, None);
        Assert.Equal(Form, d.Apply);
        Assert.True(d.NewGeneration);
    }
}
