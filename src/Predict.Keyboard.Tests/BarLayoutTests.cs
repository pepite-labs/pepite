using System;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Bar sizes (D17) and placement (D9): caret rule of iterations 1–2, docked top/bottom, free position.</summary>
public class BarLayoutTests
{
    private static readonly RectI Wa = new(0, 0, 1920, 1040);                 // 1080p, taskbar 40 px
    private static readonly RectI Right = new(1920, 0, 1920 + 2560, 1400);    // second monitor
    private static readonly RectI[] Monitors = { Wa, Right };
    private static readonly SizeI Bar = new(600, 120);

    [Fact]
    public void Metrics_Table()
    {
        Assert.Equal(new BarMetrics(9, 16, 48, 8, 2, 4), BarMetrics.For(BarSize.S));
        Assert.Equal(new BarMetrics(11, 22, 64, 12, 3, 6), BarMetrics.For(BarSize.M));   // iterations 1–2, unchanged
        Assert.Equal(new BarMetrics(13, 28, 80, 14, 4, 7), BarMetrics.For(BarSize.L));
        Assert.Equal(new BarMetrics(16, 36, 96, 16, 4, 8), BarMetrics.For(BarSize.XL));
        Assert.Equal(BarMetrics.For(BarSize.M), BarMetrics.For((BarSize)99));
    }

    // the three cases of the iteration-2 PlaceAt: x = clamp(px), y = py + 6, flip above near the bottom
    [Theory]
    [InlineData(300, 400, 300, 406)]       // plain: under the caret
    [InlineData(1800, 400, 1320, 406)]     // right edge: shifted left to stay inside
    [InlineData(500, 1000, 500, 848)]      // bottom: above the caret (1000 - 120 - 32)
    public void Caret_Mode_Is_Todays_Rule(int px, int py, int x, int y) =>
        Assert.Equal(new PointI(x, y), BarPlacement.Compute(BarPosition.Caret, new PointI(px, py), Bar, Wa, null, Monitors));

    [Fact]
    public void Docked_Top_And_Bottom_Are_Centred_And_Inside()
    {
        Assert.Equal(new PointI(660, 8), BarPlacement.Compute(BarPosition.Top, default, Bar, Wa, null, Monitors));
        Assert.Equal(new PointI(660, 1040 - 120 - 8), BarPlacement.Compute(BarPosition.Bottom, default, Bar, Wa, null, Monitors));
        Assert.Equal(new PointI(1920 + 980, 8), BarPlacement.Compute(BarPosition.Top, default, Bar, Right, null, Monitors));
        var huge = new SizeI(2500, 120);   // wider than the monitor: left edge wins
        Assert.Equal(new PointI(0, 8), BarPlacement.Compute(BarPosition.Top, default, huge, Wa, null, Monitors));
    }

    [Fact]
    public void Free_Position_Is_Clamped_Into_Its_Monitor()
    {
        Assert.Equal(new PointI(812, 40), BarPlacement.Compute(BarPosition.Free, default, Bar, Wa, new PointI(812, 40), Monitors));
        Assert.Equal(new PointI(1320, 920), BarPlacement.Compute(BarPosition.Free, default, Bar, Wa, new PointI(1900, 1030), Monitors));
        // on the second monitor (multi-monitor rect), clamped into that one, not the primary
        Assert.Equal(new PointI(2000 + 1920 - 1920, 1280), BarPlacement.Compute(BarPosition.Free, default, Bar, Wa, new PointI(2000, 1390), Monitors));
        Assert.Equal(new PointI(4480 - 600, 100), BarPlacement.Compute(BarPosition.Free, default, Bar, Wa, new PointI(4470, 100), Monitors));
    }

    [Fact]
    public void Free_Partly_Off_The_Left_Edge_Is_Pulled_Back_In()
    {
        // dropped with its top-left 12 px left of the primary monitor: still that monitor, clamped
        Assert.Equal(new PointI(0, 632), BarPlacement.Compute(BarPosition.Free, default, Bar, Wa, new PointI(-12, 632), Monitors));
        // straddling both monitors, mostly on the second one
        Assert.Equal(new PointI(1920, 0), BarPlacement.Compute(BarPosition.Free, default, Bar, Wa, new PointI(1900, -30), Monitors));
    }

    [Fact]
    public void Free_Off_Screen_Or_Unset_Goes_Bottom_Centre_Of_Primary()
    {
        var bc = new PointI(660, 1040 - 120 - 8);
        Assert.Equal(bc, BarPlacement.Compute(BarPosition.Free, default, Bar, Right, new PointI(-5000, 300), Monitors));
        Assert.Equal(bc, BarPlacement.Compute(BarPosition.Free, default, Bar, Right, null, Monitors));
        Assert.Equal(bc, BarPlacement.DefaultFree(Bar, Wa));
    }

    [Fact]
    public void Spaced_Layout_Arithmetic()
    {
        var adv = new[] { 0.5, 0.6, 0.4 };
        var (a0, w0) = SpacedLayout.Advances(adv, 20, 0);
        Assert.Equal(new[] { 10.0, 12.0, 8.0 }, a0);
        Assert.Equal(30, w0, 9);                                   // spacing 0 = sum of advances
        var (a1, w1) = SpacedLayout.Advances(adv, 20, 0.18);
        Assert.Equal(30 + 0.18 * 20 * 2, w1, 9);                   // n glyphs → n−1 gaps
        Assert.Equal(8.0, a1[2], 9);                               // no trailing gap
        var (_, w2) = SpacedLayout.Advances(adv, 40, 0.18);
        Assert.Equal(2 * w1, w2, 9);                               // scales with emSize
        var (ae, we) = SpacedLayout.Advances(Array.Empty<double>(), 20, 0.1);   // empty word
        Assert.Empty(ae);
        Assert.Equal(0.0, we);
        Assert.Equal(11.0, SpacedLayout.Advances(new[] { 0.55 }, 20, 0.18).Width, 9);   // one char: no gap
    }

    [Fact]
    public void Font_Catalog_Has_Every_Font_And_The_Spacing_Values()
    {
        foreach (BarFont f in Enum.GetValues(typeof(BarFont)))
        {
            var s = FontSpecs.For(f);
            Assert.Equal(f, s.Font);
            Assert.False(string.IsNullOrEmpty(s.FamilyName));
            Assert.True(f == BarFont.SegoeUI ? s.Folder is null : s.FileNames.Length >= 3);
        }
        Assert.Equal(400, FontSpecs.For(BarFont.OpenDyslexic).WordWeight);
        Assert.Equal(600, FontSpecs.For(BarFont.Lexend).WordWeight);
        Assert.Equal(0.0, FontSpecs.SpacingEm(LetterSpacing.Normal));
        Assert.Equal(0.10, FontSpecs.SpacingEm(LetterSpacing.Wider));
        Assert.Equal(0.18, FontSpecs.SpacingEm(LetterSpacing.Widest));
    }
}
