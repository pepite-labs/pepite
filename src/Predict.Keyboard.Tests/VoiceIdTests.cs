using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 08a: voice ids <c>engine:name</c> and the migration of the 0.4.0 bare SAPI names.</summary>
public class VoiceIdTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("Microsoft Hortense Desktop", "sapi:Microsoft Hortense Desktop")]
    [InlineData("sapi:Microsoft Hortense Desktop", "sapi:Microsoft Hortense Desktop")]
    [InlineData("SAPI:Microsoft Hortense Desktop", "sapi:Microsoft Hortense Desktop")]
    [InlineData("piper:siwis", "piper:siwis")]
    [InlineData("Piper:siwis", "piper:siwis")]
    [InlineData("vendor:voice:x", "sapi:vendor:voice:x")]   // an unknown prefix is part of a legacy name
    [InlineData("piper:", "sapi:piper:")]
    public void Normalize(string? raw, string? expected) => Assert.Equal(expected, VoiceId.Normalize(raw));

    [Fact]
    public void Parse_Gives_Engine_And_Name()
    {
        var p = VoiceId.Parse("piper:siwis")!.Value;
        Assert.True(p.IsPiper);
        Assert.Equal("siwis", p.Name);
        Assert.True(VoiceId.Parse("Microsoft Paul")!.Value.IsSapi);
    }

    [Fact]
    public void Settings_Normalise_The_Voice()
    {
        var s = AppSettings.FromJson(JsonNode.Parse(@"{""speech"":{""voice"":""Microsoft Hortense Desktop""}}") as JsonObject);
        Assert.Equal("sapi:Microsoft Hortense Desktop", s.Speech.Voice);
        Assert.Equal("sapi:Microsoft Hortense Desktop", (string?)s.ToJson()["speech"]!["voice"]);
        Assert.Null(AppSettings.FromJson(new JsonObject()).Speech.Voice);
    }

    [Fact]
    public void Store_Migrates_A_Legacy_Name_And_Writes_It_On_The_Next_Save()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kbvoice-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, @"{""speech"":{""voice"":""Microsoft Hortense Desktop"",""rate"":2}}");
            var log = new List<string>();
            var st = new SettingsStore(dir, log.Add);
            var user = st.Load();
            Assert.Equal("sapi:Microsoft Hortense Desktop", (string?)user["speech"]!["voice"]);
            Assert.Contains("settings: speech.voice migrated to sapi:Microsoft Hortense Desktop", log);
            Assert.Contains("Microsoft Hortense Desktop\"", File.ReadAllText(file));      // not written yet
            st.SaveUser(new JsonObject { ["words"] = new JsonObject { ["count"] = 3 } }).Wait();
            st.Flush();
            var root = JsonNode.Parse(File.ReadAllText(file))!;
            Assert.Equal("sapi:Microsoft Hortense Desktop", (string?)root["speech"]!["voice"]);
            Assert.Equal(2, (int)root["speech"]!["rate"]!);
            // round trip: no second migration line
            log.Clear();
            new SettingsStore(dir, log.Add).Load();
            Assert.DoesNotContain(log, l => l.Contains("migrated"));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void Machine_Layer_Voice_Is_Migrated()
    {
        var defaults = JsonNode.Parse(@"{""speech"":{""voice"":""Microsoft Paul""}}") as JsonObject;
        var locked = JsonNode.Parse(@"{""speech"":{""voice"":""piper:siwis""}}") as JsonObject;
        Assert.Equal("sapi:Microsoft Paul", VoiceId.MigrateJson(defaults));
        Assert.Null(VoiceId.MigrateJson(locked));
        var (eff, _) = SettingsLayering.Merge(defaults, null, null);
        Assert.Equal("sapi:Microsoft Paul", eff.Speech.Voice);
        var (eff2, lockedPaths) = SettingsLayering.Merge(defaults, null, locked);
        Assert.Equal("piper:siwis", eff2.Speech.Voice);
        Assert.Contains("speech.voice", lockedPaths);
    }
}
