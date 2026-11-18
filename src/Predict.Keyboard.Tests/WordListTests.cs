using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 10A step 2: the word-list model (entry rules, limits, tolerant parse, unknown keys kept).</summary>
public class WordListModelTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 10, 30, 0);

    [Theory]
    [InlineData("Vésuve", null)]
    [InlineData("  œsophage ", null)]
    [InlineData("Jean-Pierre", null)]
    [InlineData("aujourd’hui", null)]
    [InlineData("", "Empty")]
    [InlineData("deux mots", "BadChars")]
    [InlineData("r2d2", "BadChars")]
    [InlineData("-tiret", "BadChars")]
    [InlineData("fin-", "BadChars")]
    [InlineData("a--b", "BadChars")]
    [InlineData("abcdefghijabcdefghijabcdefghijabcdefghijx", "TooLong")]
    public void Entry_Rules(string word, string? expected) => Assert.Equal(expected, WordBook.Check(word)?.ToString());

    [Fact]
    public void Add_Keeps_Capitals_And_Refuses_Exact_Duplicates_Only()
    {
        var b = WordBook.Empty.AddWord(null, "Pierre", WordEntry.Manual, Now, false, out var p1)
                              .AddWord(null, "pierre", WordEntry.Manual, Now, false, out var p2);
        Assert.Null(p1);
        Assert.Null(p2);
        Assert.Equal(new[] { "Pierre", "pierre" }, b.Default!.Words.Select(e => e.W));
        Assert.Equal(Strings.MyWordsList, b.Default.Name);
        Assert.Equal("2026-11-14", b.Default.Words[0].Added);
        b.AddWord(null, " Pierre ", WordEntry.Manual, Now, false, out var p3);
        Assert.Equal(WordProblem.Duplicate, p3);
        Assert.Equal("aujourd'hui", WordBook.Empty.AddWord(null, "aujourd’hui", WordEntry.Manual, Now, false, out _).Default!.Words[0].W);
    }

    [Fact]
    public void Limits_5000_Pupil_10000_Class_500_Per_Lesson_50_Lists_500_Fautes()
    {
        var many = Enumerable.Range(0, 5000).Select(Name).ToList();
        var b = WordBook.Empty.AddList("a", WordListDef.Personal, many, WordEntry.Import, Now, false, out _, out var p);
        Assert.Null(p);
        Assert.Equal(5000, b.WordCount);
        b.AddWord(null, "encore", WordEntry.Manual, Now, false, out var full);
        Assert.Equal(WordProblem.TooManyWords, full);
        b.AddWord(null, "encore", WordEntry.Manual, Now, true, out var cls);   // « Classe » goes to 10 000
        Assert.Null(cls);
        WordBook.Empty.AddList("l", WordListDef.Lesson, Enumerable.Range(0, 501).Select(Name), WordEntry.Lesson, Now, false, out _, out var lesson);
        Assert.Equal(WordProblem.TooManyInList, lesson);
        var lists = WordBook.Empty;
        for (int i = 0; i < 50; i++) lists = lists.AddList("l" + i, WordListDef.Lesson, new[] { Name(i) }, WordEntry.Lesson, Now, false, out _, out _);
        lists.AddList("trop", WordListDef.Lesson, new[] { "mot" }, WordEntry.Lesson, Now, false, out _, out var tooMany);
        Assert.Equal(WordProblem.TooManyLists, tooMany);
        var f = WordBook.Empty;
        for (int i = 0; i < 500; i++) f = f.SetFaute(Name(i) + "x", Name(i), out _);
        Assert.Equal(500, f.Fautes.Count);
        f.SetFaute("autrex", "autre", out var tooManyF);
        Assert.Equal(WordProblem.TooManyFautes, tooManyF);
    }

    /// <summary>A distinct valid word for every i (letters only).</summary>
    internal static string Name(int i)
    {
        var sb = new StringBuilder("mot");
        do { sb.Append((char)('a' + i % 26)); i /= 26; } while (i > 0);
        return sb.ToString();
    }

    [Fact]
    public void Faute_Replaces_The_Same_Spelling_And_Refuses_A_Same_Pair()
    {
        var b = WordBook.Empty.SetFaute("pace", "parce", out _).SetFaute("PACE", "passe", out _);
        Assert.Single(b.Fautes);
        Assert.Equal("passe", b.Fautes[0].Mot);
        b.SetFaute("mot", "Mot", out var same);
        Assert.Equal(WordProblem.SamePair, same);
        Assert.Empty(b.RemoveFaute("PACE").Fautes);
    }

    [Fact]
    public void Edit_Delete_Hide_Lists_And_Clear()
    {
        var b = WordBook.Empty.AddWord(null, "volcan", WordEntry.Manual, Now, false, out _)
                              .AddList("Les volcans", WordListDef.Lesson, new[] { "magma", "lave", "magma", "2x" }, WordEntry.Lesson, Now, false, out var id, out _);
        Assert.Equal(new[] { "magma", "lave" }, b.Get(id!)!.Words.Select(e => e.W));
        b = b.ReplaceWord(WordBook.DefaultListId, "volcan", "Vésuve", out var pr);
        Assert.Null(pr);
        Assert.Equal("Vésuve", b.Default!.Words.Single().W);
        b = b.SetActive(id!, false);
        Assert.Equal(new[] { "Vésuve" }, WordLayering.ActiveForms(b));
        b = b.SetHidden("cratère", true).SetHidden("cratère", true);
        Assert.Single(b.Masques);
        b = b.RemoveWord(WordBook.DefaultListId, "Vésuve").RemoveList(id!);
        Assert.Equal(0, b.WordCount);
        Assert.Empty(b.SetHidden("cratère", false).Masques);
        var cleared = b.Cleared();
        Assert.True(cleared.IsEmpty);
    }

    [Fact]
    public void Json_Round_Trip_Tolerant_Parse_And_Unknown_Keys_Kept()
    {
        var b = WordBook.Empty.AddWord(null, "Léa", WordEntry.Manual, Now, false, out _).SetFaute("pace", "parce", out _).SetHidden("cratère", true)
                              .AddList("Leçon", WordListDef.Lesson, new[] { "magma" }, WordEntry.Lesson, Now, false, out _, out _);
        var json = b.ToJson();
        json["futur"] = "garde-moi";
        var back = WordBook.FromJson(JsonNode.Parse(json.ToJsonString())!.AsObject(), false, out var over);
        Assert.False(over);
        Assert.Equal(b.Lists.Select(l => (l.Id, l.Name, l.Active, l.Type, string.Join(",", l.Words.Select(e => e.W + e.Src + e.Added)))),
                     back.Lists.Select(l => (l.Id, l.Name, l.Active, l.Type, string.Join(",", l.Words.Select(e => e.W + e.Src + e.Added)))));
        Assert.Equal(b.Fautes, back.Fautes);
        Assert.Equal(b.Masques, back.Masques);
        Assert.Equal("garde-moi", (string)back.ToJson()["futur"]!);
        Assert.Equal(1, (int)back.ToJson()["schema"]!);

        var messy = JsonNode.Parse(@"{""listes"":[{""id"":""mes-mots"",""mots"":[{""w"":""ok""},{""w"":""pas bien""},{""w"":12},""nu"",{""w"":""ok""}],""extra"":1},
            ""pas un objet""],""fautes"":[{""ecrit"":""kan"",""mot"":""quand""},{""ecrit"":""KAN"",""mot"":""camp""},{""mot"":""x""}],""masques"":[""a b"",""cratère""]}")!.AsObject();
        var m = WordBook.FromJson(messy, false, out _);
        Assert.Equal(new[] { "ok", "nu" }, m.Default!.Words.Select(e => e.W));
        Assert.Equal(1, (int)m.ToJson()["listes"]![0]!["extra"]!);
        Assert.Equal("quand", m.Fautes.Single().Mot);
        Assert.Equal(new[] { "cratère" }, m.Masques);
        Assert.Empty(WordBook.FromJson(messy, true, out _).Masques);   // « Classe » hides nothing
    }

    [Fact]
    public void Parse_Applies_The_Limits_And_Says_So()
    {
        var o = new JsonObject { ["listes"] = new JsonArray(new JsonObject { ["id"] = "mes-mots", ["mots"] = new JsonArray(Enumerable.Range(0, 5001).Select(i => (JsonNode?)JsonValue.Create(Name(i))).ToArray()) }) };
        var b = WordBook.FromJson(o, false, out var over);
        Assert.True(over);
        Assert.Equal(5000, b.WordCount);
        WordBook.FromJson(o, true, out var overClass);
        Assert.False(overClass);
    }
}

/// <summary>Plan 10A step 2: inheritance (Classe ∪ own − hidden, the pupil's pair wins) and the import rebase.</summary>
public class WordLayeringTests
{
    private static readonly DateTime Now = new(2026, 11, 14);

    private static WordBook Book(params string[] words) =>
        words.Aggregate(WordBook.Empty, (b, w) => b.AddWord(null, w, WordEntry.Manual, Now, false, out _));

    [Fact]
    public void Effective_Is_Class_Union_Own_Minus_Hidden()
    {
        var cls = Book("Léa", "Tom", "cratère").SetFaute("kan", "quand", out _).SetFaute("pace", "parce", out _);
        var lea = Book("Vésuve").SetHidden("cratère", true).SetFaute("PACE", "passe", out _);
        var e = WordLayering.Of(lea, cls);
        Assert.Equal(new[] { "Léa", "Tom", "Vésuve" }, e.Words.Select(w => w.Form));
        Assert.Equal((1, 2, 1), (e.Own, e.Class, e.Hidden));
        var lex = UserLexicon.Build(e.Words, e.Fixes);
        Assert.Equal(2, lex.FixCount);
        // the pupil's pair is first: it wins over the class's for the same spelling
        Assert.Equal("PACE", e.Fixes[0].Typed);
        Assert.Equal(new[] { "Léa", "Tom", "cratère" }, WordLayering.Of(cls, null).Words.Select(w => w.Form));
        // a list switched off gives nothing
        var off = cls.SetActive(WordBook.DefaultListId, false);
        Assert.DoesNotContain(WordLayering.Of(lea, off).Words, w => w.Form == "Tom");
    }

    [Fact]
    public void Rebase_Keeps_The_Effective_List()
    {
        var fileClass = Book("Léa", "Tom", "volcan", "cratère").SetFaute("kan", "quand", out _);
        var pupil = Book("Vésuve").SetHidden("cratère", true).SetHidden("Tom", true);
        var target = Book("Tom", "Inès");
        var before = WordLayering.FormSet(pupil, fileClass);
        var r = WordLayering.Rebase(fileClass, pupil, target, Now);
        var after = WordLayering.FormSet(r, target);
        // every word the pupil had is still there (the target class adds its own: Inès)
        Assert.Empty(before.Except(after));
        Assert.Equal(new[] { "Inès" }, after.Except(before));
        Assert.Equal(new[] { "Vésuve", "Léa", "volcan" }, r.Default!.Words.Select(e => e.W));
        Assert.Equal(WordEntry.Import, r.Default.Words[1].Src);
        Assert.Equal(new[] { "Tom" }, r.Masques);          // « cratère »: the target class lacks it
        Assert.Equal("quand", r.Fautes.Single().Mot);      // the class's pair travels with the pupil
        Assert.Same(pupil, WordLayering.Rebase(null, pupil, target, Now));
    }
}

/// <summary>Plan 10A step 3: the word files (no file until a change, atomic writes, bad file moved aside) and the
/// service (snapshot to the engine on switch / edit, logs without words, erase with a backup).</summary>
public class WordListServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kbwords-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();

    public void Dispose()
    {
        TestDirs.Delete(_dir);
    }

    private sealed class Host : IProfileHost
    {
        public void ResetForSwitch() { }
        public void CancelSpeech() { }
        public void CloseProfileWindows() { }
    }

    private (ProfileStore Store, ProfileService Svc, WordListService Words, List<UserLexicon> Applied, string Lea, string Tom) Pc()
    {
        var store = new ProfileStore(_dir, Path.Combine(_dir, "sauvegardes"), _log.Add);
        store.Load();
        store.Add(Strings.ClassProfileName, new Avatar(null, ProfileColor.Vert), null, Profile.ClassId);
        var lea = store.Add("Léa", Avatar.Default).Id;
        var tom = store.Add("Tom", Avatar.Default).Id;
        store.SetActive(lea);
        var settings = ProfileService.CreateSettings(store, MachineLayer.None, _log.Add);
        var svc = new ProfileService(store, settings, new Host(), _log.Add);
        var applied = new List<UserLexicon>();
        var words = new WordListService(new WordListStore(store.ProfilesDir, _log.Add, store.InMemory), store, applied.Add, _log.Add,
                                        () => new DateTime(2026, 11, 14));
        svc.Words = words;
        words.Rebuild("start");
        return (store, svc, words, applied, lea, tom);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void No_File_Until_A_Change_Then_Written_And_Read_Back()
    {
        var pc = Pc();
        Assert.False(pc.Words.Store.HasFile(pc.Lea));
        Assert.True(pc.Applied.Last().IsEmpty);
        Assert.Null(pc.Words.AddWord(pc.Lea, "Vésuve"));
        pc.Words.Store.Flush();
        Assert.True(pc.Words.Store.HasFile(pc.Lea));
        Assert.Equal("Vésuve", (string)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(pc.Words.Store.PathOf(pc.Lea)))!["listes"]![0]!["mots"]![0]!["w"]!);
        var again = new WordListStore(pc.Store.ProfilesDir, null);
        Assert.Equal("Vésuve", again.Get(pc.Lea).Default!.Words.Single().W);
        Assert.True(pc.Applied.Last().Contains("vésuve"));
        Assert.Equal(WordProblem.Duplicate, pc.Words.AddWord(pc.Lea, "Vésuve"));
    }

    [Fact]
    public void Switch_Lea_To_Tom_Swaps_The_Lists_And_Logs_No_Word()
    {
        var pc = Pc();
        pc.Words.AddWord(pc.Lea, "Vésuve");
        pc.Words.AddWord(pc.Tom, "Zorglub");
        pc.Words.AddWord(Profile.ClassId, "Inès");
        Assert.True(pc.Applied.Last().Contains("inès"));
        Assert.True(pc.Applied.Last().Contains("vésuve"));
        Assert.True(pc.Svc.Switch(pc.Tom, "test"));
        var lex = pc.Applied.Last();
        Assert.True(lex.Contains("zorglub"));
        Assert.True(lex.Contains("inès"));
        Assert.False(lex.Contains("vésuve"));
        pc.Svc.Switch(pc.Lea, "test");
        Assert.True(pc.Applied.Last().Contains("vésuve"));
        pc.Svc.Switch(pc.Tom, "test");
        Assert.Same(lex, pc.Applied.Last());   // back and forth: the snapshot is reused
        Assert.Contains(_log, l => l.StartsWith("words: profile " + pc.Tom, StringComparison.Ordinal) && l.Contains("reused"));
        Assert.DoesNotContain(_log, l => l.Contains("Vésuve") || l.Contains("Zorglub") || l.Contains("Inès"));
    }

    [Fact]
    public void Hiding_A_Class_Word_For_Tom_Only()
    {
        var pc = Pc();
        pc.Words.AddWord(Profile.ClassId, "cratère");
        pc.Svc.Switch(pc.Tom, "test");
        pc.Words.Update(pc.Tom, b => (b.SetHidden("cratère", true), null), "hide");
        Assert.False(pc.Applied.Last().Contains("cratère"));
        pc.Svc.Switch(pc.Lea, "test");
        Assert.True(pc.Applied.Last().Contains("cratère"));
    }

    [Fact]
    public void Erase_All_Makes_A_Backup_And_Keeps_The_Class()
    {
        var pc = Pc();
        pc.Words.AddWord(Profile.ClassId, "Inès");
        pc.Words.AddWord(pc.Lea, "Vésuve");
        pc.Words.Update(pc.Lea, b => { var r = b.SetFaute("pace", "parce", out var p); return (r, p); }, "pair");
        var zip = pc.Words.EraseAll(pc.Lea);
        Assert.NotNull(zip);
        Assert.EndsWith("-mots.zip", zip);
        Assert.True(File.Exists(Path.Combine(_dir, "sauvegardes", zip!)));
        Assert.True(pc.Words.BookOf(pc.Lea).IsEmpty);
        Assert.Equal("Inès", pc.Words.BookOf(Profile.ClassId).Default!.Words.Single().W);
        var lex = pc.Applied.Last();
        Assert.True(lex.Contains("inès"));
        Assert.False(lex.Contains("vésuve"));
        Assert.Equal(0, lex.FixCount);
    }

    [Fact]
    public void Delete_And_Duplicate_Follow_The_Profile()
    {
        var pc = Pc();
        pc.Words.AddWord(pc.Tom, "Zorglub");
        var copy = pc.Svc.Duplicate(pc.Tom, "Tom bis");
        pc.Words.Store.Flush();
        Assert.Equal("Zorglub", pc.Words.BookOf(copy.Id).Default!.Words.Single().W);
        pc.Svc.Delete(copy.Id);
        Assert.False(File.Exists(pc.Words.Store.PathOf(copy.Id)));
        Assert.True(pc.Words.BookOf(copy.Id).IsEmpty);
    }

    [Fact]
    public void A_Bad_File_Is_Moved_Aside()
    {
        var pc = Pc();
        Directory.CreateDirectory(Path.GetDirectoryName(pc.Words.Store.PathOf(pc.Tom))!);
        File.WriteAllText(pc.Words.Store.PathOf(pc.Tom), "{ pas du json");
        var s = new WordListStore(pc.Store.ProfilesDir, _log.Add);
        Assert.True(s.Get(pc.Tom).IsEmpty);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(pc.Words.Store.PathOf(pc.Tom))!, "mots.json.bad-*"));
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void Snapshot_Of_15000_Words_Builds_Within_The_Gate()
    {
        var words = Enumerable.Range(0, 15000).Select(i => new UserWord(i % 2 == 0 ? WordListModelTests.Name(i) : "N" + WordListModelTests.Name(i))).ToList();
        UserLexicon.Build(words.Take(100));   // JIT
        long best = long.MaxValue;
        UserLexicon lex = UserLexicon.Empty;
        for (int i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            lex = UserLexicon.Build(words);
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        Assert.Equal(15000, lex.Count);
        // the plan's gate is 30 ms (Predict.Cli --bench-user, see evaluation.md); the test suite runs in parallel, so it
        // only guards against an order-of-magnitude regression (best of 3)
        Assert.True(best < 1000, $"build took {best} ms");
    }
}

/// <summary>Plan 10A: plain-text word lists (UTF-8 with or without BOM, else Windows-1252).</summary>
public class WordTextFileTests
{
    [Fact]
    public void Utf8_With_And_Without_Bom()
    {
        var text = "Vésuve\r\nmagma\n\npas bien\nmagma\nœsophage\n";
        var (w1, r1) = WordTextFile.Parse(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray());
        var (w2, _) = WordTextFile.Parse(Encoding.UTF8.GetBytes(text));
        Assert.Equal(new[] { "Vésuve", "magma", "œsophage" }, w1);
        Assert.Equal(w1, w2);
        Assert.Equal(1, r1);
    }

    [Fact]
    public void Windows1252_When_Not_Utf8()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("Vésuve\r\nœsophage\r\n");
        Assert.Equal(new[] { "Vésuve", "œsophage" }, WordTextFile.Parse(bytes).Words);
    }

    [Fact]
    public void Format_Is_One_Word_Per_Line() => Assert.Equal("a\r\nb\r\n", WordTextFile.Format(new[] { "a", "b" }));
}

/// <summary>Plan 10A step 7: the lesson extractor (distinct words, capitals away from a sentence start, ticks).</summary>
public class LessonExtractorTests
{
    [Fact]
    public void Words_In_Order_With_Name_Capitals_And_Ticks()
    {
        var known = new HashSet<string> { "le", "est", "un", "volcan", "d", "italie", "il", "a", "détruit", "pompéi", "magma" };
        var frequent = new HashSet<string> { "le", "est", "un", "il", "a" };
        var r = LessonExtractor.Extract("Le Vésuve est un volcan d'Italie. Il a détruit Pompéi en 79. Le stratovolcan crache du magma ; le magma…",
                                        w => known.Contains(w), frequent);
        var forms = r.Select(x => x.Form).ToList();
        Assert.Equal("le", forms[0]);                       // sentence start: lowercase
        Assert.Contains("Vésuve", forms);                    // capital mid-sentence: kept
        Assert.Contains("Pompéi", forms);
        Assert.Contains("Italie", forms);
        Assert.DoesNotContain("79", forms);
        Assert.DoesNotContain("d'", forms);
        Assert.Single(forms, f => f == "magma");
        Assert.False(r.First(x => x.Form == "le").Ticked);
        Assert.True(r.First(x => x.Form == "volcan").Ticked);       // content word
        Assert.True(r.First(x => x.Form == "stratovolcan").IsNew);
        Assert.True(r.First(x => x.Form == "stratovolcan").Ticked);
        Assert.False(r.First(x => x.Form == "volcan").IsNew);
    }

    [Fact]
    public void At_Most_500_Words()
    {
        var text = string.Join(" ", Enumerable.Range(0, 700).Select(WordListModelTests.Name));
        Assert.Equal(500, LessonExtractor.Extract(text, _ => false, new HashSet<string>()).Count);
    }
}

/// <summary>Plan 10A step 6: capitals and pictures — a stored capital survives the casing, duplicates after casing make
/// one tile, a name never takes the common noun's picture.</summary>
public class PersonalCasingTests
{
    [Theory]
    [InlineData("Léa", "l", false, "Léa")]
    [InlineData("Léa", "l", true, "Léa")]
    [InlineData("Léa", "LÉ", false, "LÉA")]
    [InlineData("Vésuve", "", true, "Vésuve")]
    public void Stored_Capital_Survives(string word, string typed, bool start, string shown) =>
        Assert.Equal(shown, KeyboardPolicy.Cased(word, typed, start));

    [Fact]
    public void Same_Text_After_Casing_Is_One_Tile_Marked_As_A_Name()
    {
        var ranked = new[]
        {
            new Suggestion("pierre", 0, MatchKind.Prefix), new Suggestion("Pierre", -1, MatchKind.Prefix, Name: true),
            new Suggestion("pied", -2, MatchKind.Prefix),
        };
        var lower = KeyboardPolicy.CasedDistinct(ranked, "pi", false, 8);
        Assert.Equal(new[] { "pierre", "Pierre", "pied" }, lower.Select(s => s.Word));
        var upper = KeyboardPolicy.CasedDistinct(ranked, "Pi", false, 8);
        Assert.Equal(new[] { "Pierre", "Pied" }, upper.Select(s => s.Word));
        Assert.True(upper[0].Name);
        Assert.Equal(2, KeyboardPolicy.CasedDistinct(ranked, "pi", false, 2).Count);
    }

    [Fact]
    public void A_Name_Has_No_Picture_Key()
    {
        Assert.Null(PictureStore.KeyFor(new Suggestion("Pierre", 0, MatchKind.Prefix, Name: true)));
        Assert.Equal("pierre", PictureStore.KeyFor(new Suggestion("Pierre", 0, MatchKind.Prefix)));   // a sentence-start capital
    }

    [Fact]
    public void Adding_A_Word_Never_Asks_For_The_Pin()
    {
        var strict = new ProfilePolicy(PinToSwitch: true, PinToEdit: true, PinToQuit: true);
        Assert.False(PinGate.Protects(PinReason.AddWord, true, strict));
        Assert.True(PinGate.Protects(PinReason.Edit, true, strict));
    }
}
