using System;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

public class KeyboardPolicyTests
{
    [Fact] public void UnknownNext_Space() => Assert.True(KeyboardPolicy.NeedsSpace(false, ' '));
    [Fact] public void EndOfText_Space() => Assert.True(KeyboardPolicy.NeedsSpace(true, '\0'));

    [Theory]
    [InlineData(' ')] [InlineData('\r')] [InlineData('\n')] [InlineData('\t')] [InlineData('\v')] [InlineData('\a')]
    [InlineData(',')] [InlineData('.')] [InlineData('-')] [InlineData('’')] [InlineData(')')] [InlineData('»')]
    public void NoSpaceBeforeWhitespacePunctuationControl(char c) => Assert.False(KeyboardPolicy.NeedsSpace(true, c));

    [Theory]
    [InlineData('s')] [InlineData('1')] [InlineData('€')]
    public void SpaceBeforeOtherChars(char c) => Assert.True(KeyboardPolicy.NeedsSpace(true, c));

    [Fact]
    public void ShouldApply()
    {
        IntPtr a = (IntPtr)1, b = (IntPtr)2;
        Assert.False(KeyboardPolicy.ShouldApply(1, 2, a, a, 10));
        Assert.False(KeyboardPolicy.ShouldApply(1, 1, a, b, 10));
        Assert.False(KeyboardPolicy.ShouldApply(1, 1, a, a, 151));
        Assert.True(KeyboardPolicy.ShouldApply(1, 1, a, a, 150));
    }
}

public class WordTrackerTests
{
    [Fact]
    public void SeedMidWord_ThenTypeAndBackspace()
    {
        var t = new WordTracker();
        t.Seed("mai", new[] { "la" }, 0, "son", ' ', "win32");
        Assert.Equal("mai", t.Current); Assert.Equal("son", t.Suffix); Assert.True(t.HasNextChar);
        Assert.Equal(' ', t.NextChar); Assert.Equal("win32", t.Origin); Assert.Equal(new[] { "la" }, t.Context.ToArray());

        t.Type('z');
        Assert.Equal("maiz", t.Current); Assert.Equal("son", t.Suffix);
        for (int i = 0; i < 4; i++) t.Backspace();
        Assert.Equal("", t.Current); Assert.Equal("son", t.Suffix); Assert.True(t.HasNextChar);
        t.Backspace();
        Assert.Equal("", t.Suffix); Assert.False(t.HasNextChar);
    }

    [Fact]
    public void SeedThenSeparator_ClearsRight()
    {
        var t = new WordTracker();
        t.Seed("mai", new[] { "la" }, 0, "son", ' ', "uia");
        t.Type(' ');
        Assert.Equal("", t.Suffix); Assert.False(t.HasNextChar);
        Assert.Equal(new[] { "la", "mai" }, t.Context.ToArray()); Assert.True(t.AfterSeparator);
    }

    [Fact]
    public void SeedWithoutPrefix_HasNoSuffix()
    {
        var t = new WordTracker();
        t.Seed("", new[] { "il", "fait" }, 1, "son", 's', "win32");
        Assert.Equal("", t.Suffix); Assert.True(t.AfterSeparator); Assert.True(t.HasNextChar); Assert.Equal('s', t.NextChar);
    }

    [Fact]
    public void Accept_SpacedOrNot()
    {
        var t = new WordTracker();
        t.Seed("mai", new[] { "la" }, 0, "son", ' ', "win32");
        t.Accept("maison", false);
        Assert.False(t.AfterSeparator); Assert.Equal("maison", t.Context[^1]); Assert.Equal("", t.Suffix); Assert.False(t.HasNextChar);
        t.Type(' ');
        Assert.True(t.AfterSeparator);
        t.Accept("est", true);
        Assert.True(t.AfterSeparator); Assert.Equal("est", t.Context[^1]);
    }

    [Fact]
    public void Seed_KeepsLastThreeContextWords()
    {
        var t = new WordTracker();
        t.Seed("x", new[] { "a", "b", "c", "d", "e" }, 0, "", '\0', "uia");
        Assert.Equal(new[] { "c", "d", "e" }, t.Context.ToArray());
    }

    [Fact]
    public void Reset_ClearsRightAndOrigin()
    {
        var t = new WordTracker();
        t.Seed("mai", new[] { "la" }, 0, "son", ' ', "uia");
        t.Reset();
        Assert.Equal("", t.Suffix); Assert.False(t.HasNextChar); Assert.Equal("keys", t.Origin); Assert.Empty(t.Context);
    }

    [Fact]
    public void Digit_Resets()
    {
        var t = new WordTracker();
        t.Seed("mai", new[] { "la" }, 0, "son", ' ', "uia");
        t.Type('1');
        Assert.Equal("", t.Current); Assert.Equal("", t.Suffix); Assert.Empty(t.Context);
    }

    [Fact]
    public void Version_IncrementsOnEveryCall()
    {
        var t = new WordTracker();
        long v = t.Version;
        void Step(Action a) { a(); Assert.True(t.Version > v); v = t.Version; }
        Step(() => t.Type('a'));
        Step(() => t.Type(' '));
        Step(() => t.Backspace());
        Step(() => t.Backspace());
        Step(() => t.Accept("le", true));
        Step(() => t.Seed("x", Array.Empty<string>(), 0, "", '\0', "win32"));
        Step(() => t.Type('1'));
        Step(() => t.Reset());
    }

    [Fact]
    public void KeysOnly_BehaviourUnchanged()
    {
        var t = new WordTracker();
        foreach (var c in "la maiz") t.Type(c);
        Assert.Equal("maiz", t.Current); Assert.Equal(new[] { "la" }, t.Context.ToArray());
        Assert.False(t.HasNextChar); Assert.Equal("", t.Suffix); Assert.Equal("keys", t.Origin);
        var u = new WordTracker();
        foreach (var c in "la ") u.Type(c);
        u.Backspace();                                     // back over the space restores the word
        Assert.Equal("la", u.Current); Assert.Empty(u.Context);
    }
}

public class InserterTests
{
    private static readonly UIntPtr M = (UIntPtr)0x4C584B42u;

    [Fact]
    public void Build_DeleteThenBackspaceThenText()
    {
        var ev = Inserter.Build(3, 3, "maison", M);
        Assert.Equal(24, ev.Length);
        Assert.All(ev, e => { Assert.Equal(Native.INPUT_KEYBOARD, e.type); Assert.Equal(M, e.u.ki.dwExtraInfo); });
        for (int i = 0; i < 6; i++)
        {
            var k = ev[i].u.ki;
            Assert.Equal(Native.VK_DELETE, k.wVk); Assert.Equal(0x53, k.wScan);
            Assert.True((k.dwFlags & Native.KEYEVENTF_EXTENDEDKEY) != 0);
            Assert.Equal(i % 2 == 1, (k.dwFlags & Native.KEYEVENTF_KEYUP) != 0);
        }
        for (int i = 6; i < 12; i++)
        {
            var k = ev[i].u.ki;
            Assert.Equal(0x08, k.wVk); Assert.Equal(i % 2 == 1, (k.dwFlags & Native.KEYEVENTF_KEYUP) != 0);
        }
        var text = new string(Enumerable.Range(0, 6).Select(j => (char)ev[12 + 2 * j].u.ki.wScan).ToArray());
        Assert.Equal("maison", text);
        Assert.All(ev.Skip(12), e => Assert.True((e.u.ki.dwFlags & Native.KEYEVENTF_UNICODE) != 0));
    }

    [Fact]
    public void InputStructSize_MatchesWin32()
        => Assert.Equal(Environment.Is64BitProcess ? 40 : 28, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());

    [Fact]
    public void Build_KeysOnlyLayout()
    {
        var ev = Inserter.Build(0, 2, "le ", M);
        Assert.Equal(10, ev.Length);
        Assert.Equal(0x08, ev[0].u.ki.wVk); Assert.Equal(0x08, ev[3].u.ki.wVk);
        Assert.Equal('l', (char)ev[4].u.ki.wScan); Assert.Equal(' ', (char)ev[8].u.ki.wScan);
        Assert.Equal(Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP, ev[9].u.ki.dwFlags);
    }
}
