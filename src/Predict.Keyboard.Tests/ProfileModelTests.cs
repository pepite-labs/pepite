using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 09 step 3: the profile model (ids, names, avatar, tolerant JSON).</summary>
public class ProfileModelTests
{
    [Fact]
    public void New_Ids_Are_Valid_Unique_And_Avoid_Taken_Ones()
    {
        var ids = Enumerable.Range(0, 500).Select(_ => Profile.NewId()).ToList();
        Assert.All(ids, id => Assert.True(Profile.IsValidId(id), id));
        Assert.All(ids, id => Assert.Matches("^p-[a-z2-7]{6}$", id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.True(Profile.IsValidId("classe"));
        foreach (var bad in new[] { null, "", "p-", "p-abc", "p-ABCDEF", "p-abcde1", "x-abcdef", "..", "p-abcdefg", @"p-ab\cde" })
            Assert.False(Profile.IsValidId(bad), bad);
    }

    [Theory]
    [InlineData("Léa", null)]
    [InlineData("  Léa   Marie ", null)]
    [InlineData("", NameProblem.Empty)]
    [InlineData("   ", NameProblem.Empty)]
    [InlineData("abcdefghijabcdefghijabcdefghij", null)]
    [InlineData("abcdefghijabcdefghijabcdefghijk", NameProblem.TooLong)]
    [InlineData("Lé\u0007a", NameProblem.ControlChars)]
    [InlineData("Lé​a", NameProblem.ControlChars)]
    [InlineData("TOM", NameProblem.Duplicate)]
    [InlineData("tom", NameProblem.Duplicate)]
    public void Names_Are_Checked(string name, object? expected) =>
        Assert.Equal((NameProblem?)expected, Profile.CheckName(name, new[] { "Tom", "Classe" }));

    [Fact]
    public void Clean_Name_Trims_And_Collapses_Spaces() => Assert.Equal("Léa Marie", Profile.CleanName("  Léa \t  Marie "));

    [Fact]
    public void Round_Trip_Keeps_Unknown_Keys_And_Layer()
    {
        var raw = JsonNode.Parse(@"{""schema"":1,""id"":""p-abcdef"",""name"":""Léa"",""avatar"":{""picto"":""chat"",""color"":""orange""},
            ""created"":""2026-11-14T10:00:00Z"",""settings"":{""words"":{""size"":""XL""},""future"":1},""later"":{""x"":true}}")!.AsObject();
        var p = Profile.FromJson(raw, "p-abcdef", "?");
        Assert.Equal("Léa", p.Name);
        Assert.Equal(new Avatar("chat", ProfileColor.Orange), p.Avatar);
        Assert.Equal(new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc), p.CreatedUtc);
        Assert.Equal("XL", (string?)p.Layer["words"]!["size"]);
        var back = p.ToJson();
        Assert.True((bool)back["later"]!["x"]!);
        Assert.Equal(1, (int)back["settings"]!["future"]!);
        Assert.Equal("orange", (string?)back["avatar"]!["color"]);
        Assert.Equal("L", p.Initial.Substring(0, 1));
    }

    [Fact]
    public void Tolerant_Parse_Falls_Back_Per_Field()
    {
        var raw = JsonNode.Parse(@"{""id"":""p-zzzzzz"",""name"":42,""avatar"":{""picto"":"" "",""color"":""fuchsia""},""created"":""yesterday"",""settings"":[1]}")!.AsObject();
        var p = Profile.FromJson(raw, "p-abcdef", "Mon profil");
        Assert.Equal("p-abcdef", p.Id);                 // the folder wins
        Assert.Equal("Mon profil", p.Name);
        Assert.Null(p.Avatar.Picto);
        Assert.Equal(Avatar.Default.Color, p.Avatar.Color);
        Assert.Equal(DateTime.UnixEpoch, p.CreatedUtc);
        Assert.Empty(p.Layer);
    }

    [Fact]
    public void Legacy_Voice_In_A_Profile_Is_Normalised()
    {
        var raw = JsonNode.Parse(@"{""name"":""Tom"",""settings"":{""speech"":{""voice"":""Microsoft Hortense Desktop""}}}")!.AsObject();
        Assert.Equal("sapi:Microsoft Hortense Desktop", (string?)Profile.FromJson(raw, "p-abcdef", "?").Layer["speech"]!["voice"]);
    }

    [Fact]
    public void Eight_Named_Colours_With_Distinct_Values_And_Names()
    {
        var all = Enum.GetValues(typeof(ProfileColor)).Cast<ProfileColor>().ToList();
        Assert.Equal(8, all.Count);
        Assert.Equal(8, all.Select(Avatar.Argb).Distinct().Count());
        Assert.Equal(8, all.Select(Strings.ColorName).Distinct().Count());
        foreach (var c in all) Assert.Equal(c, Avatar.FromJson(new Avatar(null, c).ToJson()).Color);
    }

    [Fact]
    public void Max_Is_40_Class_Included() => Assert.Equal(40, Profile.MaxProfiles);
}
