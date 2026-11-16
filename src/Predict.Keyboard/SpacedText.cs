using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Predict.Keyboard;

/// <summary>
/// One word drawn with extra letter spacing (D16). WPF has no letter-spacing property, so the glyphs are laid out
/// here: characters → glyph indices (<see cref="GlyphTypeface.CharacterToGlyphMap"/>), each advance = its advance
/// width + spacing except the last one (<see cref="SpacedLayout"/>), drawn as one frozen <see cref="GlyphRun"/>
/// cached until a property or the DPI changes. The width is exact, so the bar button grows with the word.
/// Kerning: WPF exposes no pair-kerning table, but some fonts rely on it heavily (OpenDyslexic: WPF's own layout is
/// 0.087 em per letter gap tighter than the raw advances). The word's kerning is therefore taken as a whole — the
/// width WPF gives it (<see cref="FormattedText"/>) minus the sum of advances — and spread evenly over its gaps, so a
/// spaced word is exactly "the Normal word + spacing per gap". Normal spacing keeps a plain TextBlock.
/// A character the font lacks → the whole word falls back to a TextBlock (system font fallback, no spacing).
/// </summary>
internal sealed class SpacedText : FrameworkElement
{
    private readonly string _text;
    private readonly GlyphTypeface _glyphs;
    private readonly double _em, _spacingEm;
    private readonly Brush _fg;
    private readonly Typeface _typeface;
    private GlyphRun? _run;
    private double _runDpi;
    private double _width, _height;
    private TextBlock? _fallback;

    /// <summary>True when the font lacks a character of the word (drawn by a TextBlock instead).</summary>
    public bool Fallback => _fallback != null;

    public SpacedText(string text, GlyphTypeface glyphs, FontFamily family, FontWeight weight, double emSize, double spacingEm, Brush foreground)
    {
        _text = text;
        _glyphs = glyphs;
        _em = emSize;
        _spacingEm = spacingEm;
        _fg = foreground;
        _typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
        AutomationProperties.SetName(this, text);   // screen readers / UIA see the word, not the glyph run
        var map = glyphs.CharacterToGlyphMap;
        foreach (var c in text)
            if (!map.ContainsKey(c))
            {
                _fallback = new TextBlock { Text = text, FontFamily = family, FontWeight = weight, FontSize = emSize, Foreground = foreground };
                AddVisualChild(_fallback);
                break;
            }
    }

    protected override int VisualChildrenCount => _fallback is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => _fallback ?? throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size available)
    {
        if (_fallback != null) { _fallback.Measure(available); return _fallback.DesiredSize; }
        Build();
        return new Size(_width, _height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _fallback?.Arrange(new Rect(final));
        return final;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _run = null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Build()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_run != null && dpi == _runDpi) return;
        var map = _glyphs.CharacterToGlyphMap;
        var idx = new ushort[_text.Length];
        var advEm = new double[_text.Length];
        for (int i = 0; i < _text.Length; i++)
        {
            idx[i] = map[_text[i]];
            advEm[i] = _glyphs.AdvanceWidths[idx[i]];
        }
        double kernEm = 0;
        if (_text.Length > 1)
        {
            var ft = new FormattedText(_text, System.Globalization.CultureInfo.GetCultureInfo("fr-FR"), FlowDirection.LeftToRight,
                                       _typeface, _em, _fg, dpi);
            double sumEm = 0;
            foreach (var a in advEm) sumEm += a;
            kernEm = (ft.WidthIncludingTrailingWhitespace / _em - sumEm) / (_text.Length - 1);
        }
        var (adv, width) = SpacedLayout.Advances(advEm, _em, _spacingEm + kernEm);
        _width = width;
        _height = _glyphs.Height * _em;
        if (_text.Length == 0) { _run = null; _runDpi = dpi; return; }
        _run = new GlyphRun(_glyphs, 0, false, _em, (float)dpi, idx, new Point(0, _glyphs.Baseline * _em), adv,
                            null, _text.ToCharArray(), null, null, null, XmlLanguage.GetLanguage("fr-FR"));
        _runDpi = dpi;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_fallback != null) return;
        Build();
        if (_run != null) dc.DrawGlyphRun(_fg, _run);
    }
}
