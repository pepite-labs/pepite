using System;
using System.Collections.Generic;

namespace Predict.Keyboard;

/// <summary>Physical-pixel geometry, independent of WPF / WinForms (unit-tested).</summary>
internal readonly record struct PointI(int X, int Y);
internal readonly record struct SizeI(int W, int H);
internal readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(PointI p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
}

/// <summary>Sizes of the bar per <see cref="BarSize"/> (D9, owner decision 3). Font sizes are WPF units (DIP);
/// M is the bar of iterations 1–2, unchanged. Pictures are 128 px: 1:1 for M at 200 %.</summary>
internal readonly record struct BarMetrics(double LabelPt, double WordPt, double PictureDip,
                                           double PadH, double PadTop, double PadBottom)
{
    public static BarMetrics For(BarSize s) => s switch
    {
        BarSize.S => new(9, 16, 48, 8, 2, 4),
        BarSize.L => new(13, 28, 80, 14, 4, 7),
        BarSize.XL => new(16, 36, 96, 16, 4, 8),
        _ => new(11, 22, 64, 12, 3, 6),
    };

    /// <summary>Line height of Segoe UI, in em (the F-label and word lines of a tile).</summary>
    public const double LineEm = 1.33;

    /// <summary>Plan 10B: diameter (DIP) of the round spelling marker — the F-label line + the word line when the bar has
    /// pictures, the word line alone without them, so the marker (with its key line above) is never taller than a tile.</summary>
    public double MarkerDiameter(bool pictures) => Math.Round(WordPt * LineEm + (pictures ? LabelPt * LineEm : 0));

    /// <summary>Approximate height (DIP) of a word tile: padding, F-label line, picture slot (+ its 2 + 4 margin), word line.</summary>
    public double TileHeight(bool pictures, bool keys) =>
        PadTop + (keys ? LabelPt * LineEm : 0) + (pictures ? PictureDip + 6 : 0) + WordPt * LineEm + PadBottom;

    /// <summary>Approximate height (DIP) of the spelling marker: padding, key line, circle.</summary>
    public double MarkerHeight(bool pictures, bool keys) =>
        PadTop + (keys ? LabelPt * LineEm : 0) + MarkerDiameter(pictures) + PadBottom;
}

/// <summary>Where the bar goes (D9): next to the caret, docked at the top / bottom of the work area, or where the
/// user dragged it. Every result keeps the whole bar inside the work area used.</summary>
internal static class BarPlacement
{
    public const int DockMargin = 8;

    /// <param name="caret">caret point (Caret mode only).</param>
    /// <param name="workArea">work area of the monitor of the caret (Caret) or of the foreground window (Top/Bottom).</param>
    /// <param name="free">saved top-left (Free mode), or null.</param>
    /// <param name="workAreas">all monitors' work areas, primary first (Free mode).</param>
    public static PointI Compute(BarPosition mode, PointI caret, SizeI bar, RectI workArea, PointI? free,
                                 IReadOnlyList<RectI> workAreas)
    {
        switch (mode)
        {
            case BarPosition.Top:
                return Clamp(new PointI(workArea.Left + (workArea.Width - bar.W) / 2, workArea.Top + DockMargin), bar, workArea);
            case BarPosition.Bottom:
                return Clamp(new PointI(workArea.Left + (workArea.Width - bar.W) / 2, workArea.Bottom - bar.H - DockMargin), bar, workArea);
            case BarPosition.Free:
                if (free is { } f && FreeArea(f, bar, workAreas) is { } b) return Clamp(f, bar, b);
                return DefaultFree(bar, workAreas.Count > 0 ? workAreas[0] : workArea);
            default:
            {
                // under the caret; above it when it would not fit below (iterations 1–2 rule)
                int x = Math.Max(workArea.Left, Math.Min(caret.X, workArea.Right - bar.W));
                int y = caret.Y + 6;
                if (y + bar.H > workArea.Bottom) y = Math.Max(workArea.Top, caret.Y - bar.H - 32);
                return Clamp(new PointI(x, y), bar, workArea);
            }
        }
    }

    /// <summary>Work area of a free position: the monitor holding the top-left, else the one the bar overlaps most
    /// (a bar dropped partly off the left edge has its top-left outside every monitor); null → default place.
    /// Also used to decide how many words fit (the same monitor as the placement).</summary>
    public static RectI? FreeArea(PointI f, SizeI bar, IReadOnlyList<RectI> workAreas)
    {
        RectI? best = null;
        long bestArea = 0;
        foreach (var wa in workAreas)
        {
            if (wa.Contains(f)) return wa;
            long w = Math.Min(wa.Right, f.X + bar.W) - (long)Math.Max(wa.Left, f.X);
            long h = Math.Min(wa.Bottom, f.Y + bar.H) - (long)Math.Max(wa.Top, f.Y);
            if (w > 0 && h > 0 && w * h > bestArea) { bestArea = w * h; best = wa; }
        }
        return best;
    }

    /// <summary>Free mode without a (valid) saved place: bottom-centre of the primary work area.</summary>
    public static PointI DefaultFree(SizeI bar, RectI primary) =>
        Clamp(new PointI(primary.Left + (primary.Width - bar.W) / 2, primary.Bottom - bar.H - DockMargin), bar, primary);

    /// <summary>How many words, from the first, fit in <paramref name="available"/> px next to the bar's fixed part
    /// (border, grip and — plan 10B — the spelling marker, all in <paramref name="fixedWidth"/>); at least one word when
    /// there is any (the bar always shows its first word), none when there is none (a marker-only bar).</summary>
    public static int WordsThatFit(int fixedWidth, IReadOnlyList<int> wordWidths, int available)
    {
        int n = 0;
        long w = fixedWidth;
        foreach (var x in wordWidths)
        {
            if (n > 0 && w + x > available) break;
            w += x;
            n++;
        }
        return n;
    }

    /// <summary>Keep the bar inside <paramref name="wa"/> (left/top win when the bar is larger than the area).</summary>
    public static PointI Clamp(PointI p, SizeI bar, RectI wa) =>
        new(Math.Max(wa.Left, Math.Min(p.X, wa.Right - bar.W)), Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - bar.H)));
}
