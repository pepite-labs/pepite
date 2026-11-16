using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Tray promotion logic against a fake NotifyIconSettings registry, the state file, and the icons.</summary>
public class TrayPromotionTests
{
    private static readonly Guid ProgramFiles = new("6D809377-6AF0-444B-8957-A3773F02200E");
    private static string? Kf(Guid g) => g == ProgramFiles ? @"C:\Program Files" : null;
    private const string Exe = @"C:\Program Files\Prediction\PredictKeyboard.exe";

    private sealed class FakeRegistry : INotifyIconRegistry
    {
        public readonly Dictionary<string, string?> Paths = new();
        public readonly List<string> Promoted = new();
        public IEnumerable<string> SubKeys() => Paths.Keys.ToList();
        public string? ExecutablePath(string k) => Paths[k];
        public readonly Dictionary<string, int> Values = new();
        public int? IsPromoted(string k) => Values.TryGetValue(k, out var v) ? v : null;
        public void SetPromoted(string k) => Promoted.Add(k);
    }

    private sealed class FakeState : IPromotionState
    {
        public readonly HashSet<string> Done = new(StringComparer.OrdinalIgnoreCase);
        public bool IsPromoted(string exe) => Done.Contains(exe);
        public void MarkPromoted(string exe) => Done.Add(exe);
    }

    [Fact]
    public void Resolves_A_Known_Folder_Guid_Prefix()
    {
        Assert.Equal(Exe, TrayPromotion.ResolveExecutablePath(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Prediction\PredictKeyboard.exe", Kf));
        Assert.Equal(@"D:\x\a.exe", TrayPromotion.ResolveExecutablePath(@"D:\x\a.exe", Kf));
        var unknown = @"{00000000-0000-0000-0000-000000000001}\a.exe";
        Assert.Equal(unknown, TrayPromotion.ResolveExecutablePath(unknown, Kf));
    }

    [Fact]
    public void Match_Is_Case_Insensitive()
    {
        Assert.True(TrayPromotion.Matches(@"{6d809377-6af0-444b-8957-a3773f02200e}\PREDICTION\predictkeyboard.EXE", Exe, Kf));
        Assert.True(TrayPromotion.Matches(@"c:\program files\prediction\PredictKeyboard.exe", Exe, Kf));
        Assert.False(TrayPromotion.Matches(@"C:\Program Files\Prediction\Other.exe", Exe, Kf));
        Assert.False(TrayPromotion.Matches(null, Exe, Kf));
    }

    [Fact]
    public void Promotes_Only_Our_Subkey_Once_And_Marks_After_Success()
    {
        var reg = new FakeRegistry();
        reg.Paths["111"] = @"C:\Windows\explorer.exe";
        reg.Paths["222"] = null;
        var state = new FakeState();
        var log = new List<string>();

        Assert.False(TrayPromotion.TryPromote(reg, state, Exe, Kf, log.Add));   // not created yet
        Assert.Empty(reg.Promoted);
        Assert.False(state.IsPromoted(Exe));                                      // nothing written

        reg.Paths["333"] = @"{6D809377-6AF0-444B-8957-A3773F02200E}\Prediction\PredictKeyboard.exe";
        Assert.True(TrayPromotion.TryPromote(reg, state, Exe, Kf, log.Add));
        Assert.Equal(new[] { "333" }, reg.Promoted);                               // other subkeys untouched
        Assert.True(state.IsPromoted(Exe));

        Assert.True(TrayPromotion.TryPromote(reg, state, Exe, Kf, log.Add));      // second start: no-op
        Assert.Single(reg.Promoted);
        Assert.Contains(log, l => l.Contains("already done"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void An_Existing_IsPromoted_Value_Is_Left_Alone_And_Remembered(int value)
    {
        var reg = new FakeRegistry();
        reg.Paths["a"] = Exe;
        reg.Values["a"] = value;          // the user (or Explorer) already chose
        var state = new FakeState();
        var log = new List<string>();
        Assert.True(TrayPromotion.TryPromote(reg, state, Exe, Kf, log.Add));
        Assert.Empty(reg.Promoted);       // nothing written
        Assert.True(state.IsPromoted(Exe));
        Assert.Contains(log, l => l.Contains("already " + value));
    }

    [Fact]
    public void Flag_Is_Per_Exe_Path()
    {
        var reg = new FakeRegistry();
        reg.Paths["a"] = Exe;
        reg.Paths["b"] = @"D:\dev\PredictKeyboard.exe";
        var state = new FakeState();
        Assert.True(TrayPromotion.TryPromote(reg, state, @"D:\dev\PredictKeyboard.exe", Kf, null));
        Assert.True(TrayPromotion.TryPromote(reg, state, Exe, Kf, null));
        Assert.Equal(new[] { "b", "a" }, reg.Promoted);
    }

    [Fact]
    public void State_File_Round_Trip_Keeps_Other_Keys_And_Survives_Corruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "traystate-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "state.json");
        try
        {
            var s = new JsonPromotionState(path);
            Assert.False(s.IsPromoted(Exe));
            s.MarkPromoted(Exe);
            Assert.True(new JsonPromotionState(path).IsPromoted(Exe.ToUpperInvariant()));
            Assert.False(s.IsPromoted(@"D:\dev\PredictKeyboard.exe"));
            File.WriteAllText(path, "{\"other\": 3, \"trayPromoted\": {\"x.exe\": true}}");
            s.MarkPromoted(Exe);
            var text = File.ReadAllText(path);
            Assert.Contains("\"other\": 3", text);
            Assert.True(s.IsPromoted("x.exe") && s.IsPromoted(Exe));
            File.WriteAllText(path, "{ corrupt");
            Assert.False(s.IsPromoted(Exe));
            s.MarkPromoted(Exe);
            Assert.True(s.IsPromoted(Exe));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("app.ico")]
    [InlineData("app-paused.ico")]
    public void Icon_Loads_At_Every_Size(string name)
    {
        if (!OperatingSystem.IsWindows()) return;   // System.Drawing icons: Windows only
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", name);
        // 256 px is a PNG entry (Explorer/WPF read it; System.Drawing.Icon skips PNG entries): check its header
        var bytes = File.ReadAllBytes(path);
        int count = BitConverter.ToUInt16(bytes, 4);
        Assert.Equal(8, count);
        int png = Enumerable.Range(0, count).Single(i => bytes[6 + 16 * i] == 0);   // width byte 0 = 256
        int off = BitConverter.ToInt32(bytes, 6 + 16 * png + 12);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, bytes[off..(off + 4)]);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 0, 0, 1, 0 }, bytes[(off + 16)..(off + 24)]);   // IHDR 256 × 256
        foreach (int size in new[] { 16, 20, 24, 32, 40, 48, 64 })
        {
            using var icon = new Icon(path, size, size);
            Assert.Equal(size, icon.Width);
            using var bmp = icon.ToBitmap();
            Assert.Equal(size, bmp.Width);
            var centre = bmp.GetPixel(size / 2, size / 2);
            Assert.Equal(255, centre.A);                          // opaque body
            Assert.True(bmp.GetPixel(0, size - 1).A < 128);      // rounded, transparent corner
        }
    }
}
