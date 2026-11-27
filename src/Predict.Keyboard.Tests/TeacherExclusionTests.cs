using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>
/// 0.11.0 (plan offensive-words, Phase B, D35): the teacher's « Mots à ne jamais proposer » — « Classe » only, the machine
/// file's <c>locked.motsExclus</c> joined to it, both handed to the engine's teacher part; a pupil can neither see, hide nor
/// remove one; the .pepite file carries them. <b>Invented words only</b>.
/// </summary>
public class TeacherExclusionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "texcl-" + Guid.NewGuid().ToString("N"));
    private readonly string _db, _g;
    private readonly PredictionEngine _e;
    private readonly List<string> _log = new();
    private static readonly DateTime Now = new(2026, 11, 27);

    public TeacherExclusionTests()
    {
        TeacherLock.Iterations = 1000;
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "prediction.sqlite");
        _g = Path.Combine(_dir, "grammar.sqlite");
        Exec(_db,
             "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL)",
             "INSERT INTO words VALUES ('zorblate','zORblat',300),('zorblates','zORblat',60),('zorblon','zORbl§',40),('zorbe','zORb',5)," +
             "('zorbi','zORbi',4),('zorbo','zORbo',4),('zorbu','zORby',3),('zorga','zORga',3),('zorgu','zORgy',3),('zorpi','zORpi',2)," +
             "('zorvan','zORv@',2),('zorquin','zORk5',2),('trambule','tR@byl',200),('trambules','tR@byl',90),('trambulons','tR@byl§',150),('glimpou','glimpu',200)," +
             "('zorbal','zORbal',80),('zorbals','zORbal',30),('zorbale','zORbal',70),('zorbales','zORbal',20),('glorber','glORbe',60),('glorbe','glORb',50),('glorbons','glORb§',20),('glorbée','glORbe',40),('glorbés','glORbe',15)," +
             "('glorbier','glORbje',30),('glorbiers','glORbje',10),('glorbière','glORbjER',20),('glorbions','glORbj§',5)," +
             "('le','l°',3000),('la','la',3000),('un','1',2000)",
             "CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER)",
             "INSERT INTO bigrams VALUES ('le','zorblate',500),('le','zorbe',10),('le','zorblates',300),('un','glimpou',200)",
             "CREATE TABLE trigrams (p2 TEXT, p1 TEXT, word TEXT, c INTEGER)");
        // grammar: zorblate (0) has zorblate, zorblates and zorblon — zorblon is also a form of pimbal (1); trambule is a
        // form of the verb trambuler (2) and of the noun trambule (3), trambules too, trambulons of the verb only; the noun
        // zorbal (4) has a masculine and a feminine in both numbers; the verb glorber (5) an infinitive, finite forms and
        // past participles
        const uint N = MorphBits.Noun, V = MorphBits.FiniteVerb, Q = MorphBits.PastParticiple, Y = MorphBits.Infinitive,
                   M = MorphBits.Masc, F = MorphBits.Fem, S = MorphBits.Sing, P = MorphBits.Plur;
        var index = new (string O, (int L, uint B)[] A)[]
        {
            ("zorblate", new[] { (0, N | M | S) }), ("zorblates", new[] { (0, N | M | P) }), ("zorblon", new[] { (0, N | M | S), (1, N | M | S) }),
            ("trambule", new[] { (2, V | MorphBits.P3s), (3, N | F | S) }), ("trambules", new[] { (2, V | MorphBits.P2s), (3, N | F | P) }),
            ("trambulons", new[] { (2, V | MorphBits.P1p) }),
            ("zorbal", new[] { (4, N | M | S) }), ("zorbals", new[] { (4, N | M | P) }), ("zorbale", new[] { (4, N | F | S) }),
            ("zorbales", new[] { (4, N | F | P) }),
            ("glorber", new[] { (5, Y) }), ("glorbe", new[] { (5, V | MorphBits.P3s) }), ("glorbons", new[] { (5, V | MorphBits.P1p) }),
            ("glorbée", new[] { (5, Q | F | S) }), ("glorbés", new[] { (5, Q | M | P) }),
            // a masculine noun that is also an infinitive of the same lemma (6), with a feminine noun and a verb form
            ("glorbier", new[] { (6, N | M | S), (6, Y) }), ("glorbiers", new[] { (6, N | M | P) }), ("glorbière", new[] { (6, N | F | S) }),
            ("glorbions", new[] { (6, V | MorphBits.P1p) }),
        };
        var rows = new List<string>
        {
            "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)", "INSERT INTO meta VALUES ('schema', '1')",
            "INSERT INTO meta VALUES ('source', 'test')", "CREATE TABLE lemma(id INTEGER PRIMARY KEY, lemma TEXT)",
            "CREATE TABLE pred_index(ortho TEXT PRIMARY KEY, a BLOB) WITHOUT ROWID",
            "INSERT INTO lemma VALUES (0,'zorblate'),(1,'pimbal'),(2,'trambuler'),(3,'trambule'),(4,'zorbal'),(5,'glorber'),(6,'glorbier')",
        };
        foreach (var (o, a) in index)
            rows.Add($"INSERT INTO pred_index VALUES ('{o}', X'{string.Concat(a.Select(x => Hex((uint)x.L) + Hex(x.B)))}')");
        Exec(_g, rows.ToArray());
        SqliteConnection.ClearAllPools();
        _e = PredictionEngine.Load(_db);
    }

    public void Dispose()
    {
        _e.Dispose();
        SqliteConnection.ClearAllPools();
        TestDirs.Delete(_dir);
    }

    private static string Hex(uint v) => string.Concat(BitConverter.GetBytes(v).Select(b => b.ToString("X2")));

    private static void Exec(string path, params string[] sql)
    {
        using var con = new SqliteConnection($"Data Source={path};Pooling=False");
        con.Open();
        foreach (var s in sql)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = s;
            cmd.ExecuteNonQuery();
        }
    }

    private string[] Bar(string typed, string? ctx = null) =>
        _e.Predict(typed, ctx is null ? null : ctx.Split(' '), 8).Select(s => s.Word).ToArray();

    private void LoadGrammar() => _e.Grammar = GrammarLexicon.Load(_g, _e);

    // ------------------------------------------------------------------ B1: the book

    [Fact]
    public void Class_Book_Takes_Banned_Words_With_The_Limits()
    {
        var b = WordBook.Empty.AddExclu("zorblate", true, out var p);
        Assert.Null(p);
        Assert.Equal(new[] { "zorblate" }, b.Exclus);
        Assert.Same(b, b.AddExclu("ZORBLATE", true, out p));                 // unique ignoring case
        Assert.Equal(WordProblem.Duplicate, p);
        b.AddExclu(new string('z', 41), true, out p);
        Assert.Equal(WordProblem.TooLong, p);
        b.AddExclu("zor blate", true, out p);
        Assert.Equal(WordProblem.BadChars, p);
        b.AddExclu("zor2", true, out p);
        Assert.Equal(WordProblem.BadChars, p);
        Assert.Equal(2, b.AddExclu("zor-blate", true, out p).Exclus.Count);    // an inner hyphen
        Assert.Null(p);
        Assert.Equal(2, b.AddExclu("zor’blate", true, out p).Exclus.Count);    // an inner apostrophe (typographic: stored as ')
        Assert.Equal("zor'blate", b.AddExclu("zor’blate", true, out _).Exclus[1]);
        var full = WordBook.Empty;
        for (int i = 0; full.Exclus.Count < WordBook.MaxExclus; i++) full = full.AddExclu("zorb" + Letters(i), true, out _);
        Assert.Same(full, full.AddExclu("glimpou", true, out p));
        Assert.Equal(WordProblem.TooManyExclus, p);
        Assert.Equal(new[] { "zorblate" }, b.AddExclu("glimpou", true, out _).RemoveExclu("glimpou").Exclus);
        // a pupil's book never takes one
        Assert.Same(WordBook.Empty, WordBook.Empty.AddExclu("zorblate", false, out p));
        Assert.Equal(WordProblem.ClassOnly, p);
        // « Effacer tous les mots de la classe » keeps them (a banned word must not come back by accident)
        Assert.Equal(new[] { "zorblate" }, b.AddWord(null, "Léa", WordEntry.Manual, Now, true, out _).Cleared().Exclus);
    }

    private static string Letters(int i)
    {
        var s = new StringBuilder();
        do { s.Append((char)('a' + i % 26)); i /= 26; } while (i > 0);
        return s.ToString();
    }

    [Fact]
    public void Json_Round_Trip_Class_Only()
    {
        var b = WordBook.Empty.AddExclu("zorblate", true, out _).AddExclu("glimpou", true, out _);
        var json = b.ToJson();
        Assert.Equal(2, ((JsonArray)json["exclus"]!).Count);
        var back = WordBook.FromJson(JsonNode.Parse(json.ToJsonString())!.AsObject(), true, out var over);
        Assert.False(over);
        Assert.Equal(b.Exclus, back.Exclus);
        // a pupil's file: dropped, and the key is not kept as an unknown one (never written back)
        var pupil = WordBook.FromJson(JsonNode.Parse(json.ToJsonString())!.AsObject(), false, out _);
        Assert.Empty(pupil.Exclus);
        Assert.Null(pupil.ToJson()["exclus"]);
        Assert.Null(WordBook.Empty.ToJson()["exclus"]);                         // no key without words
        // invalid entries dropped, duplicates ignoring case, more than 500: over the limit
        var bad = JsonNode.Parse("{\"exclus\":[\"zorblate\",\"ZORBLATE\",\"zor blate\",3,null]}")!.AsObject();
        Assert.Equal(new[] { "zorblate" }, WordBook.FromJson(bad, true, out over).Exclus);
        Assert.False(over);
        var many = new JsonObject { ["exclus"] = new JsonArray(Enumerable.Range(0, 501).Select(i => (JsonNode?)JsonValue.Create("zorb" + Letters(i))).ToArray()) };
        Assert.Equal(WordBook.MaxExclus, WordBook.FromJson(many, true, out over).Exclus.Count);
        Assert.True(over);
    }

    [Fact]
    public void Layering_Hands_Them_On_And_A_Pupil_Cannot_Hide_One()
    {
        var cls = WordBook.Empty.AddWord(null, "zorbe", WordEntry.Manual, Now, true, out _).AddExclu("zorblate", true, out _);
        var pupil = WordBook.Empty.SetHidden("zorblate", true).SetHidden("zorbe", true);
        var eff = WordLayering.Of(pupil, cls);
        Assert.Equal(new[] { "zorblate" }, eff.ExclusOrEmpty);                   // hidden words do not touch them
        Assert.Equal(1, eff.Hidden);
        Assert.Equal(new[] { "zorblate" }, WordLayering.Of(cls, null).ExclusOrEmpty);   // « Classe » itself
        Assert.Empty(WordLayering.Of(pupil, null).ExclusOrEmpty);                 // no class
    }

    // ------------------------------------------------------------------ B2: the machine file

    [Fact]
    public void Machine_File_Locked_MotsExclus()
    {
        var m = MachineLayer.Parse("{\"schema\":1,\"locked\":{\"words\":{\"spellingIndicator\":false},\"motsExclus\":[\"glimpou\",\"Glimpou\",\"bad word\",7,\"zorblate\"]}}", out var err);
        Assert.Null(err);
        Assert.Equal(new[] { "glimpou", "zorblate" }, m!.MotsExclus);
        Assert.Equal(new[] { "words.spellingIndicator" }, Json.LeafPaths(m.Locked));   // not a setting
        Assert.Equal(MachineLayer.CheckValid, MachineLayer.Check("x", _ => "{\"locked\":{\"motsExclus\":[\"glimpou\"]}}", out var msg));
        Assert.Contains("motsExclus 1 words", msg);
        Assert.Equal(2, m.MotsExclusIgnored);                                    // « bad word » and 7
        MachineLayer.Check("x", _ => "{\"locked\":{\"motsExclus\":[\"glimpou\",\"bad word\"]}}", out msg);
        Assert.Contains("motsExclus 1 words (1 ignored)", msg);
        Assert.DoesNotContain("glimpou", msg);
        // the settings are unaffected
        Assert.False(SettingsLayering.Merge(m, null).Effective.SpellingIndicator);
        // not an array: no word, the file still valid; the limit of the « Classe » list
        Assert.Empty(MachineLayer.Parse("{\"locked\":{\"motsExclus\":\"glimpou\"}}", out _)!.MotsExclus);
        var many = "{\"locked\":{\"motsExclus\":[" + string.Join(",", Enumerable.Range(0, 600).Select(i => $"\"zorb{Letters(i)}\"")) + "]}}";
        Assert.Equal(WordBook.MaxExclus, MachineLayer.Parse(many, out _)!.MotsExclus.Count);
        Assert.Empty(MachineLayer.None.MotsExclus);
    }

    // ------------------------------------------------------------------ B3: the service and the engine

    private sealed class Host : IProfileHost
    {
        public void ResetForSwitch() { }
        public void CancelSpeech() { }
        public void CloseProfileWindows() { }
    }

    private sealed record Pc(ProfileStore Store, ProfileService Svc, WordListService Words, string Lea);

    private Pc NewPc(string name, IReadOnlyList<string>? machine = null)
    {
        var root = Path.Combine(_dir, name);
        var store = new ProfileStore(root, Path.Combine(root, "sauvegardes"), _log.Add);
        store.Load();
        store.Add(Strings.ClassProfileName, new Avatar(null, ProfileColor.Vert), null, Profile.ClassId);
        var lea = store.Add("Léa", Avatar.Default).Id;
        store.SetActive(lea);
        var settings = ProfileService.CreateSettings(store, MachineLayer.None, _log.Add);
        var svc = new ProfileService(store, settings, new Host(), _log.Add);
        var e = _e;
        var words = new WordListService(new WordListStore(store.ProfilesDir, _log.Add), store, lex => e.User = lex, _log.Add, () => Now)
        {
            MachineExclus = machine ?? Array.Empty<string>(),
            ApplyExcluded = WordListService.EngineExclusions(_e, _log.Add),
        };
        svc.Words = words;
        words.Rebuild("start");
        return new Pc(store, svc, words, lea);
    }

    private WordProblem? Ban(Pc pc, string id, string w) =>
        pc.Words.Update(id, b => { var r = b.AddExclu(w, id == Profile.ClassId, out var p); return (r, p); }, "banned word added");

    [Fact]
    public void A_Banned_Word_Is_Never_Offered_By_Any_Source()
    {
        // before: every source offers them
        Assert.Contains("glimpou", Bar("glimp"));
        var pc = NewPc("a", machine: new[] { "glimpou" });
        Assert.DoesNotContain("glimpou", Bar("glimp"));                          // the machine's word, from the start
        Assert.Contains("zorblate", Bar("zorb"));
        Assert.Contains("zorblate", _e.PredictNext(new[] { "le" }).Select(s => s.Word));
        Assert.Null(Ban(pc, Profile.ClassId, "zorblate"));
        long late = _e.ExcludedLate;
        foreach (var q in new[] { "zorb", "zorbl", "zorblat", "zorblate", "sorblate", "zorblaet", "glimp", "glimpou", "climpou" })
        {
            var bar = Bar(q).Concat(Bar(q, "le")).ToArray();
            Assert.DoesNotContain("zorblate", bar);
            Assert.DoesNotContain("glimpou", bar);
        }
        Assert.DoesNotContain("zorblate", _e.PredictNext(new[] { "le" }).Select(s => s.Word));
        Assert.DoesNotContain("glimpou", _e.PredictNext(new[] { "un" }).Select(s => s.Word));
        Assert.DoesNotContain("zorblate", _e.Check("zorblatte").Candidates);
        Assert.DoesNotContain("zorblate", _e.Check("zôrblate").Candidates);
        // a personal word with those letters and a « Mes fautes » target: blocked too
        Assert.Null(pc.Words.AddWord(pc.Lea, "glimpou"));
        Assert.Null(pc.Words.Update(pc.Lea, b => { var r = b.SetFaute("blipo", "zorblate", out var p); return (r, p); }, "pair"));
        Assert.DoesNotContain("zorblate", Bar("blipo"));
        Assert.DoesNotContain("glimpou", Bar("glimp"));
        Assert.Equal(late, _e.ExcludedLate);                                     // nothing left for the safety net
        // the typed word stays ✓ (known), and the other words stay
        Assert.Equal(SpellingStatus.Known, _e.Check("zorblate").Status);
        Assert.True(_e.KnowsForMark("zorblate", false));
        Assert.Contains("zorblates", Bar("zorbl"));                              // no grammar yet: the form alone
        Assert.Equal(8, Bar("zor").Length);
        Assert.DoesNotContain(_log, l => l.Contains("zorblate") || l.Contains("glimpou"));
        // removed: offered again
        pc.Words.Update(Profile.ClassId, b => (b.RemoveExclu("zorblate"), null), "banned word removed");
        Assert.Contains("zorblate", Bar("zorb"));
        Assert.DoesNotContain("glimpou", Bar("glimp"));                          // the machine's word stays
    }

    [Fact]
    public void The_Built_In_List_And_The_Grammar_Part_Are_Kept()
    {
        var file = Path.Combine(_dir, "fr.txt");
        File.WriteAllText(file, ExcludedWords.Header + "\n" + Convert.ToBase64String(Encoding.UTF8.GetBytes("zorbe")) + "\n", Encoding.ASCII);
        _e.Excluded = ExcludedWords.FromFile(file, _e).WithGrammarForms(new[] { "snorfle" });
        var pc = NewPc("a");
        Ban(pc, Profile.ClassId, "zorgu");
        Assert.True(_e.Excluded.IsExcluded("zorbe") && _e.Excluded.IsExcluded("snorfle") && _e.Excluded.IsExcluded("zorgu"));
        Assert.Equal((1, 1, 1), (_e.Excluded.ListedCount, _e.Excluded.GrammarCount, _e.Excluded.ExtraCount));
    }

    [Fact]
    public void A_Pupil_Profile_Gets_The_List_And_Cannot_Change_It()
    {
        var pc = NewPc("a");
        Ban(pc, Profile.ClassId, "zorblate");
        Assert.Equal(WordProblem.ClassOnly, Ban(pc, pc.Lea, "glimpou"));
        Assert.Empty(pc.Words.BookOf(pc.Lea).Exclus);
        // hiding it changes nothing; switching profiles keeps it
        pc.Words.Update(pc.Lea, b => (b.SetHidden("zorblate", true), null), "hide");
        Assert.DoesNotContain("zorblate", Bar("zorb"));
        var tom = pc.Store.Add("Tom", Avatar.Default).Id;
        Assert.True(pc.Svc.Switch(tom, "test"));
        Assert.DoesNotContain("zorblate", Bar("zorb"));
        Assert.True(pc.Svc.Switch(Profile.ClassId, "test"));
        Assert.DoesNotContain("zorblate", Bar("zorb"));
        // a pupil's mots.json with the key: ignored
        pc.Words.Store.Flush();
        var path = pc.Words.Store.PathOf(pc.Lea);
        var o = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        o["exclus"] = new JsonArray("glimpou");
        File.WriteAllText(path, o.ToJsonString());
        Assert.Empty(new WordListStore(pc.Store.ProfilesDir, null).Get(pc.Lea).Exclus);
    }

    private string[] Forms(params string[] w) => ExcludedWords.TeacherForms(w, _e).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void With_The_Grammar_A_Word_Brings_Only_What_The_Teacher_Meant()
    {
        Assert.Equal(new[] { "zorblate" }, Forms("zorblate"));                  // no grammar yet: the word alone
        LoadGrammar();
        // a noun: the other number, the same gender; never a form shared with another lemma
        Assert.Equal(new[] { "zorblate", "zorblates" }, Forms("zorblate"));
        // a feminine whose lemma is the masculine: its plural only, never the masculine
        Assert.Equal(new[] { "zorbale", "zorbales" }, Forms("zorbale"));
        Assert.Equal(new[] { "zorbal", "zorbals" }, Forms("zorbal"));
        // a past participle and a conjugated form: alone; the infinitive: the whole conjugation
        Assert.Equal(new[] { "glorbée" }, Forms("glorbée"));
        Assert.Equal(new[] { "glorbe" }, Forms("glorbe"));
        Assert.Equal(new[] { "glorbe", "glorber", "glorbons", "glorbés", "glorbée" }.OrderBy(x => x, StringComparer.Ordinal), Forms("glorber"));
        // a masculine noun that is also an infinitive: its plural and its verb forms, never the feminine noun
        Assert.Equal(new[] { "glorbier", "glorbiers", "glorbions" }, Forms("glorbier"));
        // a noun / verb homograph: its noun plural, not the verb's forms
        Assert.Equal(new[] { "trambule", "trambules" }, Forms("trambule"));
        // no analysis: the word alone; the same word twice (case): once
        Assert.Equal(new[] { "glimpou", "zorblate", "zorblates" }, Forms("glimpou", "zorblate", "Zorblate"));
    }

    [Fact]
    public void The_Editor_Shows_The_List_Only_Behind_The_Pin()
    {
        Assert.True(WordLayering.ShowsExclus(isClass: true, teacher: true, settingsProtected: false));    // Espace enseignant
        Assert.True(WordLayering.ShowsExclus(true, false, true));                                         // Réglages, PIN asked
        Assert.False(WordLayering.ShowsExclus(true, false, false));                                       // Réglages without PIN
        Assert.False(WordLayering.ShowsExclus(false, true, true));                                        // a pupil, anywhere
        Assert.False(WordLayering.ShowsExclus(false, false, true));
    }

    [Fact]
    public void The_Machine_List_Applies_Even_When_No_Snapshot_Comes()
    {
        var root = Path.Combine(_dir, "m");
        var store = new ProfileStore(root, Path.Combine(root, "sauvegardes"), _log.Add);
        store.Load();
        // a dispatcher that never runs the snapshot (as a failed or dropped one): the machine words are in anyway
        var words = new WordListService(new WordListStore(store.ProfilesDir, _log.Add), store, lex => _e.User = lex, _log.Add, () => Now, _ => { })
        {
            MachineExclus = new[] { "glimpou" },
            ApplyExcluded = WordListService.EngineExclusions(_e, _log.Add),
        };
        words.Rebuild("start");
        Assert.DoesNotContain("glimpou", Bar("glimp"));
        Assert.Equal(new[] { "glimpou" }, words.AppliedExclus);
    }

    [Fact]
    public void The_Class_List_Applies_Without_A_Classe_Profile_And_Never_Leaks_To_A_Pupil()
    {
        var root = Path.Combine(_dir, "n");
        var store = new ProfileStore(root, Path.Combine(root, "sauvegardes"), _log.Add);
        store.Load();                                                            // « Mon profil » only, no « Classe » profile
        var words = new WordListService(new WordListStore(store.ProfilesDir, _log.Add), store, lex => _e.User = lex, _log.Add, () => Now)
        {
            MachineExclus = new[] { "Glimpou" },
            ApplyExcluded = WordListService.EngineExclusions(_e, _log.Add),
        };
        words.Rebuild("start");
        words.Update(Profile.ClassId, b => { var r = b.AddExclu("zorblate", true, out var p); return (r, p); }, "banned word added");
        Assert.DoesNotContain("zorblate", Bar("zorb"));
        Assert.True(words.BannedWords().SetEquals(new[] { "ZORBLATE", "glimpou" }));
        // « Dupliquer » of « Classe », or a book handed to a pupil's store: never with the list
        words.OnDuplicated(Profile.ClassId, "p-copy");
        Assert.Empty(words.BookOf("p-copy").Exclus);
        words.Store.Set("p-other", WordBook.Empty.AddExclu("zorblate", true, out _));
        words.Store.Flush();
        Assert.Empty(words.BookOf("p-other").Exclus);
        Assert.DoesNotContain("exclus", File.Exists(words.Store.PathOf("p-other")) ? File.ReadAllText(words.Store.PathOf("p-other")) : "");
    }
    [Fact]
    public void The_Grammar_Coming_In_Later_Rebuilds_Without_Losing_A_Part()
    {
        var pc = NewPc("a", machine: new[] { "glimpou" });
        Ban(pc, Profile.ClassId, "zorblate");
        Assert.Contains("zorblates", Bar("zorbl"));                              // the form alone before the grammar
        // the grammar worker: the grammar, then its exclusion forms; then the UI thread's rebuild (App posts it)
        LoadGrammar();
        _e.UpdateExcluded(c => c.WithGrammarForms(new[] { "snorfle" }));
        pc.Words.RefreshExcluded("grammar");
        Assert.DoesNotContain("zorblates", Bar("zorbl"));
        Assert.True(_e.Excluded.IsExcluded("snorfle") && _e.Excluded.IsExcluded("glimpou") && _e.Excluded.IsExcluded("zorblate"));
        // and the reverse order: the teacher's rebuild first, the grammar part next — neither undoes the other
        Ban(pc, Profile.ClassId, "zorgu");
        _e.UpdateExcluded(c => c.WithGrammarForms(new[] { "snorfle", "plouf" }));
        Assert.True(_e.Excluded.IsExcluded("zorgu") && _e.Excluded.IsExcluded("zorblates") && _e.Excluded.IsExcluded("plouf"));
    }

    [Fact]
    public async Task Concurrent_Grammar_And_Teacher_Updates_Lose_Nothing()
    {
        var teacher = WordListService.EngineExclusions(_e, null);
        var words = new[] { "zorblate" };
        var a = Task.Run(() => { for (int i = 0; i < 300; i++) _e.UpdateExcluded(c => c.WithGrammarForms(new[] { "snorfle" })); });
        var b = Task.Run(() => { for (int i = 0; i < 300; i++) teacher(words); });
        await Task.WhenAll(a, b);
        Assert.True(_e.Excluded.IsExcluded("snorfle") && _e.Excluded.IsExcluded("zorblate"));
        Assert.DoesNotContain("zorblate", Bar("zorb"));
    }

    // ------------------------------------------------------------------ B4: the .pepite file

    private string Export(Pc pc)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ProfileArchive.Extension);
        pc.Words.Store.Flush();
        ProfileArchive.Write(path, pc.Store, pc.Store.Profiles.Where(e => !e.IsClass).Select(e => e.Id), false, null, "Pépite test", _log.Add,
                             pc.Words.BookOf);
        return path;
    }

    private void Import(Pc pc, string path, bool replaceClass)
    {
        var content = ProfileArchive.Read(path);
        var choices = Enumerable.Range(0, content.Pupils.Count).ToDictionary(i => i, _ => new ImportChoice(true, ConflictChoice.KeepBoth));
        var plan = ProfileArchive.Plan(content, pc.Store, choices, new ImportOptions(replaceClass, false, null, pc.Words.BookOf, Now));
        ProfileArchive.Apply(plan, pc.Svc, null, _log.Add);
    }

    [Fact]
    public void Export_Import_Carries_The_Class_List()
    {
        var a = NewPc("a");
        Ban(a, Profile.ClassId, "zorblate");
        Ban(a, Profile.ClassId, "glimpou");
        var file = Export(a);
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal(2, ((JsonArray)root["classe"]!["mots"]!["exclus"]!).Count);
        Assert.Null(root["profils"]![0]!["mots"]!["exclus"]);                    // a pupil carries none
        // « Remplacer la classe de ce poste »: the file's list
        var b = NewPc("b");
        Ban(b, Profile.ClassId, "zorgu");
        Import(b, file, replaceClass: true);
        Assert.Equal(new[] { "zorblate", "glimpou" }, b.Words.BookOf(Profile.ClassId).Exclus);
        Assert.DoesNotContain("zorblate", Bar("zorb"));
        // the class kept: its own list stays, and no pupil gets a copy
        var c = NewPc("c");
        Ban(c, Profile.ClassId, "zorgu");
        Import(c, file, replaceClass: false);
        Assert.Equal(new[] { "zorgu" }, c.Words.BookOf(Profile.ClassId).Exclus);
        Assert.All(c.Store.Profiles.Where(e => !e.IsClass), e => Assert.Empty(c.Words.BookOf(e.Id).Exclus));
    }
}
