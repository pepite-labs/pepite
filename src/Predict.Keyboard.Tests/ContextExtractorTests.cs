using System;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

public class ContextExtractorTests
{
    // code points built at run time so the sources stay plain text
    private static readonly string Acute = ((char)0x0301).ToString();
    private static readonly string Obj = ((char)0xFFFC).ToString();

    private static TextContext X(string text, int caret, bool ts = false, bool te = false)
        => ContextExtractor.Extract(text, caret, ts, te);

    private static TextContext End(string text, bool ts = false, bool te = false) => X(text, text.Length, ts, te);

    private static void Ctx(TextContext c, params string[] words) => Assert.Equal(words, c.Context.ToArray());

    [Fact]
    public void MidWord_GivesPrefixSuffixNext()
    {
        var c = X("la maison est", 6);
        Assert.True(c.Valid);
        Assert.Equal("mai", c.Prefix); Assert.Equal("son", c.Suffix); Assert.Equal(' ', c.NextChar);
        Ctx(c, "la");
    }

    [Fact]
    public void CaretAtWordEnd()
    {
        var c = X("la maison est", 9);
        Assert.Equal("maison", c.Prefix); Assert.Equal("", c.Suffix); Assert.Equal(' ', c.NextChar); Ctx(c, "la");
    }

    [Fact]
    public void SuffixReachesEndOfText()
    {
        var c = X("la maison", 6);
        Assert.Equal("mai", c.Prefix); Assert.Equal("son", c.Suffix); Assert.Equal('\0', c.NextChar);
    }

    [Fact]
    public void HyphenAfterSuffix()
    {
        var c = X("maison-là", 3);
        Assert.Equal("mai", c.Prefix); Assert.Equal("son", c.Suffix); Assert.Equal('-', c.NextChar); Ctx(c);
    }

    [Fact]
    public void CaretAtWordStart_IsNotInsideTheWord()
    {
        var c = X("la son", 3);
        Assert.Equal("", c.Prefix); Assert.Equal("", c.Suffix); Assert.Equal('s', c.NextChar);
        Ctx(c, "la"); Assert.Equal(1, c.Separators);
    }

    [Fact]
    public void CaseKeptBothSides()
    {
        var c = X("MaiSON.", 3);
        Assert.Equal("Mai", c.Prefix); Assert.Equal("SON", c.Suffix); Assert.Equal('.', c.NextChar);
    }

    [Fact]
    public void SuffixCutByWindow_IsInvalid()
        => Assert.False(X("mai" + new string('s', 65), 3, te: true).Valid);

    [Fact]
    public void CombiningMark_IsInvalid()
    {
        Assert.False(X("cafe" + Acute, 2).Valid);
        Assert.False(End("cafe" + Acute).Valid);
    }

    [Fact]
    public void EndOfWord_NoSeparators()
    {
        var c = End("la mai");
        Assert.Equal("mai", c.Prefix); Assert.Equal("", c.Suffix); Assert.Equal('\0', c.NextChar);
        Ctx(c, "la"); Assert.Equal(0, c.Separators);
    }

    [Fact]
    public void AfterSpace_NextWordContext()
    {
        var c = End("il fait ");
        Assert.Equal("", c.Prefix); Ctx(c, "il", "fait"); Assert.Equal(1, c.Separators);
        Assert.Equal(2, End("le  ").Separators);
    }

    [Fact] public void AtMostThreeContextWords() => Ctx(End("je ne sais pas si"), "ne", "sais", "pas");
    [Fact] public void SentenceEndClearsContext() { var c = End("Fin. Le ch"); Assert.Equal("ch", c.Prefix); Ctx(c, "Le"); }

    [Fact]
    public void Apostrophes()
    {
        foreach (var s in new[] { "l'homme", "l’homme" })
        { var c = End(s); Assert.Equal("homme", c.Prefix); Ctx(c, "l"); }
    }

    [Fact] public void Hyphens() { var c = End("arc-en-ci"); Assert.Equal("ci", c.Prefix); Ctx(c, "arc", "en"); }

    [Fact]
    public void CaseOfPrefixKept()
    {
        Assert.Equal("Maiz", End("Maiz").Prefix);
        Assert.Equal("CŒUR", End("CŒUR").Prefix);
    }

    [Fact] public void Cedilla() { var c = End("ça va"); Assert.Equal("va", c.Prefix); Ctx(c, "ça"); }
    [Fact] public void LineBreakIsSentenceEnd() { var c = End("ligne\r\nsuite"); Assert.Equal("suite", c.Prefix); Ctx(c); }
    [Fact] public void TabIsSeparator() { var c = End("le\tch"); Assert.Equal("ch", c.Prefix); Ctx(c, "le"); }
    [Fact] public void DigitIsBarrier() { var c = End("abc12de"); Assert.Equal("de", c.Prefix); Ctx(c); }
    [Fact] public void ObjectReplacementIsBarrier() { var c = End("un" + Obj + "deux"); Assert.Equal("deux", c.Prefix); Ctx(c); }

    [Fact]
    public void ControlAndLineSeparatorsAreBarriers()
    {
        foreach (var sep in new[] { "\v", "\a", ((char)0x2028).ToString(), ((char)0x2029).ToString() })
        { var c = End("un deux" + sep + "trois ch"); Assert.Equal("ch", c.Prefix); Ctx(c, "trois"); }
        var nb = End("un" + (char)0x00A0 + "ch");            // no-break space: a separator, not a barrier
        Assert.Equal("ch", nb.Prefix); Ctx(nb, "un");
    }

    /// <summary>UIA path: the caret index is before.Length in UTF-16 units, so emoji (surrogate pairs,
    /// ZWJ sequences) before the word must not shift the prefix/suffix.</summary>
    [Fact]
    public void ExtractAround_EmojiBeforeTheWord()
    {
        string emoji = char.ConvertFromUtf32(0x1F600) + char.ConvertFromUtf32(0x1F469) + (char)0x200D + char.ConvertFromUtf32(0x1F4BB);
        string before = string.Concat(Enumerable.Repeat(emoji, 10)) + " la mai";
        var c = ContextExtractor.ExtractAround(before, "son est", false, false);
        Assert.True(c.Valid);
        Assert.Equal("mai", c.Prefix); Assert.Equal("son", c.Suffix); Assert.Equal(' ', c.NextChar); Ctx(c, "la");
    }

    [Fact]
    public void ExtractAround_DecomposedAccentAway_StillValid_Adjacent_Invalid()
    {
        var far = ContextExtractor.ExtractAround("cafe" + Acute + " la mai", "son", false, false);
        Assert.True(far.Valid); Assert.Equal("mai", far.Prefix); Ctx(far, "la");     // mark = barrier
        Assert.False(ContextExtractor.ExtractAround("la e" + Acute + "te", "", false, false).Valid);
    }

    [Fact]
    public void EmptyText()
    {
        var c = X("", 0);
        Assert.True(c.Valid); Assert.Equal("", c.Prefix); Assert.Equal("", c.Suffix); Assert.Equal('\0', c.NextChar);
        Ctx(c); Assert.Equal(0, c.Separators);
    }

    [Fact]
    public void CaretClamped()
    {
        Assert.Equal("", X("la mai", -1).Prefix);
        Assert.Equal('l', X("la mai", -1).NextChar);
        Assert.Equal("mai", X("la mai", 99).Prefix);
    }

    [Fact]
    public void TruncatedStart_DropsCutContextWord()
    {
        var c = End("xxxxmot un ch", ts: true);
        Assert.True(c.Valid); Assert.Equal("ch", c.Prefix); Ctx(c, "un");
        Assert.False(End("motcoupé", ts: true).Valid);
    }

    [Theory]
    [InlineData('a', true)] [InlineData('Z', true)] [InlineData('é', true)] [InlineData('Ç', true)]
    [InlineData('œ', true)] [InlineData('Œ', true)] [InlineData('ÿ', true)]
    [InlineData('1', false)] [InlineData('\'', false)] [InlineData('-', false)] [InlineData(' ', false)] [InlineData('ß', false)]
    public void WordChars(char c, bool expected) => Assert.Equal(expected, ContextExtractor.IsWordChar(c));

    /// <summary>Reading text must give exactly what typing it would: 1 000 random strings typed into a
    /// tracker vs extracted at their end.</summary>
    [Fact]
    public void Equivalence_WithTyping()
    {
        const string alphabet = "abcéèàçœ AÉŒ'’-,.!?1\n";
        var rnd = new Random(42);
        for (int n = 0; n < 1000; n++)
        {
            int len = rnd.Next(0, 41);
            var s = new string(Enumerable.Range(0, len).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
            var t = new WordTracker();
            foreach (var ch in s) t.Type(ch);
            var c = End(s);
            Assert.True(c.Valid, s);
            Assert.True(t.Current == c.Prefix, $"prefix: [{s}] tracker '{t.Current}' extract '{c.Prefix}'");
            Assert.True(t.Context.SequenceEqual(c.Context), $"context: [{s}] tracker [{string.Join(" ", t.Context)}] extract [{string.Join(" ", c.Context)}]");
            Assert.True(t.AfterSeparator == (c.Prefix == "" && c.Separators > 0), $"after-separator: [{s}]");
            Assert.Equal("", c.Suffix);
        }
    }
}
