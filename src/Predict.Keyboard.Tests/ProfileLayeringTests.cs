using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 09 step 4: the « Classe » layer between the machine defaults and a pupil, and the import rebase.</summary>
public class ProfileLayeringTests
{
    private static JsonObject J(string s) => JsonNode.Parse(s)!.AsObject();

    [Fact]
    public void Class_Font_Reaches_A_Pupil_Without_A_Font()
    {
        var cls = J(@"{""words"":{""font"":""andika"",""size"":""L""}}");
        var pupil = J(@"{""words"":{""size"":""XL""}}");
        var (eff, _) = SettingsLayering.Merge(MachineLayer.None, cls, pupil);
        Assert.Equal(BarFont.Andika, eff.Font);    // inherited
        Assert.Equal(BarSize.XL, eff.Size);        // the pupil's own
    }

    [Fact]
    public void Order_Is_Builtin_Machine_Class_Pupil_Exam_Locked()
    {
        var md = J(@"{""words"":{""count"":3,""size"":""S"",""font"":""lexend"",""pictures"":false}}");
        var cls = J(@"{""words"":{""count"":4,""size"":""M""}}");
        var pupil = J(@"{""words"":{""count"":5}}");
        var exam = J(@"{""words"":{""pictures"":true}}");
        var locked = J(@"{""words"":{""count"":6}}");
        var (eff, lk) = SettingsLayering.Merge(md, cls, pupil, exam, locked);
        Assert.Equal(6, eff.WordCount);
        Assert.Equal(BarSize.M, eff.Size);
        Assert.Equal(BarFont.Lexend, eff.Font);
        Assert.True(eff.Pictures);
        Assert.Contains("words.count", lk);
        // the old overload = no class, no exam
        Assert.Equal(SettingsLayering.Merge(md, null, pupil, null, locked).Effective, SettingsLayering.Merge(md, pupil, locked).Effective);
    }

    [Fact]
    public void A_Pupil_Choosing_The_Class_Value_Drops_The_Leaf()
    {
        var cls = J(@"{""words"":{""font"":""andika""}}");
        var pupil = J(@"{""words"":{""font"":""lexend"",""size"":""XL""}}");
        var (cur, locked) = SettingsLayering.Merge(MachineLayer.None, cls, pupil);
        var edited = cur with { Font = BarFont.Andika };
        var layer = SettingsLayering.NewUserLayer(pupil, cur, edited, MachineLayer.None, locked, cls);
        Assert.Null(layer["words"]!["font"]);                     // inherits the class again
        Assert.Equal("XL", (string?)layer["words"]!["size"]);
        // a value equal to the builtin default but different from the class is written (it differs from what is inherited)
        var edited2 = cur with { Font = BarFont.SegoeUI };
        var layer2 = SettingsLayering.NewUserLayer(pupil, cur, edited2, MachineLayer.None, locked, cls);
        Assert.Equal("segoe-ui", (string?)layer2["words"]!["font"]);
        Assert.Equal(BarFont.SegoeUI, SettingsLayering.Merge(MachineLayer.None, cls, layer2).Effective.Font);
    }

    [Fact]
    public void A_Later_Class_Change_Reaches_Pupils_Who_Did_Not_Choose()
    {
        var pupil = new JsonObject();
        var (cur, locked) = SettingsLayering.Merge(MachineLayer.None, J(@"{""words"":{""count"":5}}"), pupil);
        var layer = SettingsLayering.NewUserLayer(pupil, cur, cur with { Size = BarSize.L }, MachineLayer.None, locked, J(@"{""words"":{""count"":5}}"));
        Assert.Null(layer["words"]!["count"]);   // saving did not freeze the class value of the day
        Assert.Equal(3, SettingsLayering.Merge(MachineLayer.None, J(@"{""words"":{""count"":3}}"), layer).Effective.WordCount);
    }

    public static IEnumerable<object[]> RebaseCases() => new[]
    {
        new object[] { @"{""words"":{""font"":""andika"",""size"":""L""}}", @"{""words"":{""size"":""XL""}}", @"{""words"":{""font"":""lexend""}}" },
        new object[] { @"{}", @"{""speech"":{""rate"":-3}}", @"{""speech"":{""rate"":2},""words"":{""count"":4}}" },
        new object[] { @"{""words"":{""count"":4}}", @"{}", @"{}" },
        new object[] { @"{""bar"":{""position"":""free"",""free"":{""x"":10,""y"":20}}}", @"{""words"":{""keys"":""off""}}", @"{""bar"":{""position"":""top""}}" },
    };

    [Theory]
    [MemberData(nameof(RebaseCases))]
    public void Rebase_Keeps_Effective_Values(string src, string pupil, string tgt)
    {
        var before = SettingsLayering.Merge(MachineLayer.None, J(src), J(pupil)).Effective;
        var rebased = SettingsLayering.Rebase(J(src), J(pupil), J(tgt));
        var after = SettingsLayering.Merge(MachineLayer.None, J(tgt), rebased).Effective;
        Assert.Equal(before, after);
    }

    [Fact]
    public void Rebase_Drops_What_The_Target_Class_Gives_And_Keeps_Unknown_Keys()
    {
        var rebased = SettingsLayering.Rebase(J(@"{}"), J(@"{""words"":{""font"":""andika""},""later"":{""k"":1}}"), J(@"{""words"":{""font"":""andika""}}"));
        Assert.Null(rebased["words"]);
        Assert.Equal(1, (int)rebased["later"]!["k"]!);
        var kept = SettingsLayering.Rebase(J(@"{""words"":{""font"":""andika""}}"), J(@"{}"), J(@"{}"));
        Assert.Equal("andika", (string?)kept["words"]!["font"]);   // the source class value becomes the pupil's own
    }

    [Fact]
    public void No_Class_Layer_Is_The_Old_Behaviour()
    {
        var user = J(@"{""words"":{""size"":""L""}}");
        var (a, la) = SettingsLayering.Merge(MachineLayer.None, user);
        var (b, lb) = SettingsLayering.Merge(MachineLayer.None, null, user);
        Assert.Equal(a, b);
        Assert.Equal(la.OrderBy(x => x), lb.OrderBy(x => x));
    }
}
