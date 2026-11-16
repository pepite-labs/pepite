using System;
using System.Collections.Generic;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 09 step 8: what the teacher PIN protects (4 reasons × switches × management on/off), the session-start
/// exemption and the 5-minute grace.</summary>
public class PinGateTests
{
    private DateTime _now = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);
    private bool _on = true;
    private ProfilePolicy _policy = new();
    private readonly List<PinReason> _asked = new();
    private bool _answerRight = true;

    private PinGate Gate() => new(() => _on, () => _policy, (r, ok) => { _asked.Add(r); if (_answerRight) ok(); }, () => _now, null);

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var on in new[] { false, true })
        foreach (var sw in new[] { false, true })
        foreach (var ed in new[] { false, true })
        foreach (var qu in new[] { false, true })
        foreach (PinReason r in Enum.GetValues(typeof(PinReason)))
            yield return new object[] { on, sw, ed, qu, r };
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Rule(bool on, bool pinToSwitch, bool pinToEdit, bool pinToQuit, object reasonBoxed)
    {
        var reason = (PinReason)reasonBoxed;
        var p = new ProfilePolicy(PinToSwitch: pinToSwitch, PinToEdit: pinToEdit, PinToQuit: pinToQuit);
        bool expected = on && reason switch
        {
            PinReason.Teacher => true,
            PinReason.Switch => pinToSwitch,
            PinReason.Edit => pinToEdit,
            PinReason.Quit => pinToQuit,
            _ => false,                      // the session-start pick: never
        };
        Assert.Equal(expected, PinGate.Protects(reason, on, p));
    }

    [Fact]
    public void Defaults_Edit_Yes_Switch_No_Quit_No_Teacher_Always()
    {
        var g = Gate();
        Assert.True(g.Needs(PinReason.Edit));
        Assert.False(g.Needs(PinReason.Switch));
        Assert.False(g.Needs(PinReason.Quit));
        Assert.True(g.Needs(PinReason.Teacher));
        Assert.False(g.Needs(PinReason.SessionStart));
        _on = false;
        Assert.False(g.Needs(PinReason.Teacher));   // management off: never
    }

    [Fact]
    public void Open_Asks_Then_Runs_Only_When_Right()
    {
        var g = Gate();
        int opened = 0;
        _answerRight = false;
        g.Open(PinReason.Edit, () => opened++);
        Assert.Equal(0, opened);
        Assert.Equal(new[] { PinReason.Edit }, _asked);
        _answerRight = true;
        g.Open(PinReason.Edit, () => opened++);
        Assert.Equal(1, opened);
        g.Open(PinReason.Switch, () => opened++);   // not protected: no prompt
        Assert.Equal(2, opened);
        Assert.Equal(2, _asked.Count);
    }

    [Fact]
    public void Grace_Five_Minutes_Cleared_By_A_Switch()
    {
        _policy = new ProfilePolicy(PinToSwitch: true);
        var g = Gate();
        int opened = 0;
        g.Open(PinReason.Teacher, () => opened++);
        Assert.Single(_asked);
        _now = _now.AddMinutes(4);
        g.Open(PinReason.Edit, () => opened++);      // within the grace: no prompt
        g.Open(PinReason.Switch, () => opened++);
        Assert.Single(_asked);
        _now = _now.AddMinutes(1).AddSeconds(1);
        g.Open(PinReason.Edit, () => opened++);      // grace over
        Assert.Equal(2, _asked.Count);
        g.ClearGrace();                               // a profile switch
        g.Open(PinReason.Edit, () => opened++);
        Assert.Equal(3, _asked.Count);
        Assert.Equal(5, opened);
    }

    [Fact]
    public void The_Espace_Enseignant_Always_Asks_Even_Within_The_Grace()
    {
        var g = Gate();
        g.Granted();
        Assert.True(g.InGrace);
        Assert.False(g.Needs(PinReason.Edit));
        Assert.True(g.Needs(PinReason.Teacher));      // D29: always behind the PIN
        int opened = 0;
        g.Open(PinReason.Teacher, () => opened++);
        Assert.Equal(new[] { PinReason.Teacher }, _asked);
        Assert.Equal(1, opened);
    }
}
