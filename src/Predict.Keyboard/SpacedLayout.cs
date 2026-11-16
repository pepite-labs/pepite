using System;

namespace Predict.Keyboard;

/// <summary>
/// Letter spacing arithmetic of <c>SpacedText</c> (pure, unit-tested). WPF has no letter-spacing property, so the
/// bar lays the glyphs out itself: each glyph advances by its own advance width (which already contains its
/// left and right side bearings) plus the extra spacing — except the last one, so the word gets no trailing gap
/// and the button stays centred on the ink.
/// </summary>
internal static class SpacedLayout
{
    /// <param name="glyphAdvancesEm">advance width of each glyph in em (GlyphTypeface.AdvanceWidths).</param>
    /// <param name="emSize">font size in DIP.</param>
    /// <param name="spacingEm">extra space between letters in em (0 / 0.10 / 0.18, D17).</param>
    /// <returns>the advance of each glyph in DIP and the total width.</returns>
    public static (double[] Advances, double Width) Advances(double[] glyphAdvancesEm, double emSize, double spacingEm)
    {
        var a = new double[glyphAdvancesEm.Length];
        double w = 0;
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = glyphAdvancesEm[i] * emSize + (i < a.Length - 1 ? spacingEm * emSize : 0);
            w += a[i];
        }
        return (a, w);
    }
}

/// <summary>The four bar fonts (D16): where each lives and which weight the words use (owner decision 6:
/// SemiBold where the family has it, else Bold; OpenDyslexic Regular, its letter shapes are already heavy).</summary>
/// <param name="Folder">subfolder of <c>fonts\</c> next to the exe; null = installed with Windows.</param>
/// <param name="WordWeight">OpenType weight class: 400 Regular, 600 SemiBold, 700 Bold.</param>
internal sealed record FontSpec(BarFont Font, string? Folder, string FamilyName, int WordWeight, string[] FileNames);

internal static class FontSpecs
{
    public static readonly FontSpec[] All =
    {
        new(BarFont.SegoeUI, null, "Segoe UI", 600, Array.Empty<string>()),
        new(BarFont.OpenDyslexic, "OpenDyslexic", "OpenDyslexic", 400, new[] { "OpenDyslexic-Regular.otf", "OpenDyslexic-Bold.otf", "OFL.txt" }),
        new(BarFont.Lexend, "Lexend", "Lexend", 600, new[] { "Lexend-Regular.ttf", "Lexend-SemiBold.ttf", "OFL.txt" }),
        new(BarFont.Andika, "Andika", "Andika", 600, new[] { "Andika-Regular.ttf", "Andika-SemiBold.ttf", "OFL.txt" }),
    };

    public static FontSpec For(BarFont f) => Array.Find(All, s => s.Font == f) ?? All[0];

    /// <summary>Extra letter spacing in em (owner decision 4; widest ≈ Zorzi et al. 2012).</summary>
    public static double SpacingEm(LetterSpacing s) => s switch
    {
        LetterSpacing.Wider => 0.10,
        LetterSpacing.Widest => 0.18,
        _ => 0.0,
    };
}
