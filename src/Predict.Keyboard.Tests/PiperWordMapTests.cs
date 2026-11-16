using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 08a step 7: word spans from the recorded piper-tts 1.8.0 alignments of the golden fixture.</summary>
public class PiperWordMapTests
{
    // espeak words of tokens phonemized alone (from the fixture's in-context phonemes)
    private static int Isolated(string t) => t switch { "10" => 1, "2026" => 3, "15,50" => 3, "8" => 1, "30" => 1, "9" => 1, _ => 1 };

    private static (List<WordMark> Marks, PiperWordMap.Plan Plan, List<ReadingUnit> Units) Map(string id, int sentence = 0)
    {
        var it = PiperGolden.Get(id);
        var units = PiperWordMap.Units(it.Text, Isolated);
        var plan = PiperWordMap.Assign(units, it.Sentences);
        return (PiperWordMap.Marks(plan, sentence, it.Align![sentence]), plan, units);
    }

    private static string At(string id, WordMark m) => PiperGolden.Get(id).Text.Substring(m.CharPos, m.CharLen);

    /// <summary>Sample offset where phoneme number <paramref name="index"/> (0 = first after BOS) starts.</summary>
    private static long Offset(string id, int index) => PiperGolden.Get(id).Align![0].Take(index + 1).Sum(a => a.Samples);

    [Fact]
    public void L01_Amis_Starts_After_The_Liaison_Z()
    {
        var (marks, plan, _) = Map("L01");
        Assert.False(plan.Fallback);
        Assert.Equal(new[] { "Les", "amis", "arrivent", "ce", "soir" }, marks.Select(m => At("L01", m)));
        // phonemes: l e - z ␠ a m … : « amis » starts at /a/ (index 5), after the /z/ kept in « le-z »
        Assert.Equal(Offset("L01", 5), marks[1].Sample);
    }

    [Fact]
    public void L02_Un_Keeps_Its_N()
    {
        var (marks, plan, _) = Map("L02");
        Assert.False(plan.Fallback);
        var it = PiperGolden.Get("L02");
        Assert.Equal(new[] { "Il", "y", "a", "un", "enfant", "dans", "la", "cour" }, marks.Select(m => At("L02", m)));
        // « enfant » starts at the first phoneme of the word after « œ̃n »
        int idx = it.Sentences[0].IndexOf("ɑ");
        Assert.Equal(Offset("L02", idx), marks[4].Sample);
    }

    [Fact]
    public void S13_Elisions_Are_One_Unit_Each()
    {
        var (marks, plan, _) = Map("S13");
        Assert.False(plan.Fallback);
        Assert.Equal(new[] { "Qu'est-ce", "qu'il", "y", "a", "dans", "ton", "cartable" }, marks.Select(m => At("S13", m)));
    }

    [Fact]
    public void S06_Est_Il_Is_One_Unit()
    {
        var (marks, _, _) = Map("S06");
        Assert.Contains("est-il", marks.Select(m => At("S06", m)));
        Assert.Equal(5, marks.Count);
    }

    [Fact]
    public void N01_A_Year_Gets_One_Mark()
    {
        var (marks, plan, _) = Map("N01");
        Assert.False(plan.Fallback);
        Assert.Equal(new[] { "Nous", "sommes", "le", "10", "novembre", "2026" }, marks.Select(m => At("N01", m)));
    }

    [Fact]
    public void N02_A_Price_Gets_One_Mark_Then_Euros()
    {
        var (marks, plan, _) = Map("N02");
        Assert.False(plan.Fallback);
        Assert.Equal(new[] { "Le", "livre", "coûte", "15,50", "euros" }, marks.Select(m => At("N02", m)));
    }

    [Fact]
    public void W01_One_Word()
    {
        var (marks, _, _) = Map("W01");
        Assert.Equal("maison", At("W01", marks.Single()));
    }

    [Fact]
    public void Marks_Strictly_Increase_And_Stay_In_The_Audio()
    {
        foreach (var id in new[] { "L01", "L02", "S06", "S13", "N01", "N02", "W01" })
        {
            var (marks, _, _) = Map(id);
            long total = PiperGolden.Get(id).Align![0].Sum(a => a.Samples);
            for (int i = 0; i < marks.Count; i++)
            {
                Assert.InRange(marks[i].Sample, 0, total - 1);
                if (i > 0) Assert.True(marks[i].Sample > marks[i - 1].Sample, id);
            }
        }
    }

    [Fact]
    public void Two_Sentences_In_One_Utterance()
    {
        // L01 twice in one utterance: the second sentence's units map to the second sentence's words
        var it = PiperGolden.Get("L01");
        var text = it.Text + " " + it.Text;
        var units = PiperWordMap.Units(text, Isolated);
        var plan = PiperWordMap.Assign(units, new[] { it.Sentences[0], it.Sentences[0] });
        Assert.False(plan.Fallback);
        var m1 = PiperWordMap.Marks(plan, 0, it.Align![0]);
        var m2 = PiperWordMap.Marks(plan, 1, it.Align![0]);
        Assert.Equal(5, m1.Count);
        Assert.Equal(5, m2.Count);
        Assert.Equal(it.Text.Length + 1, m2[0].CharPos);
        Assert.Equal(m1.Select(m => m.Sample), m2.Select(m => m.Sample));   // relative to each sentence's audio
    }

    [Fact]
    public void Mismatch_Falls_Back_To_Proportional_With_One_Mark_Per_Unit()
    {
        var it = PiperGolden.Get("L01");
        var units = PiperWordMap.Units(it.Text, Isolated).Take(4).ToList();   // 4 units for 5 espeak words
        var plan = PiperWordMap.Assign(units, it.Sentences);
        Assert.True(plan.Fallback);
        var marks = PiperWordMap.Marks(plan, 0, it.Align![0]);
        Assert.Equal(4, marks.Count);
        Assert.Equal(units.Select(u => u.Start), marks.Select(m => m.CharPos));
        Assert.Equal(Offset("L01", 0), marks[0].Sample);   // first voiced phoneme
    }

    [Fact]
    public void Punctuation_Only_Gives_No_Mark()
    {
        var units = PiperWordMap.Units(", ; « » !", _ => 0);
        Assert.Empty(units);
        var plan = PiperWordMap.Assign(units, new List<IReadOnlyList<string>> { new List<string> { "!" } });
        Assert.Empty(PiperWordMap.Marks(plan, 0, new List<(string, long)> { ("^", 4096), ("!", 512), ("$", 256) }));
    }

    [Fact]
    public void Spoken_Symbols_Are_Units()
    {
        var units = PiperWordMap.Units("10 € et 5 %", t => t is "€" or "%" ? 1 : 1);
        Assert.Equal(5, units.Count);
        Assert.Empty(PiperWordMap.Units("« »", _ => 1));
    }

    [Fact]
    public void Oeuf_Is_One_Unit()
    {
        var units = PiperWordMap.Units("un œuf", _ => 1);
        Assert.Equal(new[] { (0, 2), (3, 3) }, units.Select(u => (u.Start, u.Length)));
    }
}
