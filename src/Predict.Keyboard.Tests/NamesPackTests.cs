using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 10C: the names-database format (<see cref="NamesFile"/>), the « Noms propres » layer of the engine
/// (<see cref="NamesLexicon"/>: two tiers, capital lift, variants, Check), the sentence-start spelling rule, on a small test DB.</summary>
public class NamesPackTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "names-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    private static readonly (string W, string Phon, double F)[] Words =
    {
        ("pierre", "pjER", 80), ("pied", "pje", 120), ("belgique", "bElZik", 0.27), ("bel", "bEl", 300), ("belle", "bEl", 400),
        ("le", "l°", 20000), ("la", "la", 18000), ("elle", "El", 15000), ("ce", "s°", 9000), ("comme", "kOm", 6000),
        ("c", "se", 5000), ("maison", "mEz§", 400), ("mardi", "maRdi", 30), ("marché", "maRSe", 60), ("saint", "s5", 40),
        ("emmener", "@m°ne", 20), ("avec", "avEk", 3000), ("arc", "aRk", 10), ("cela", "s°la", 2000), ("chat", "Sa", 300),
        ("pomme", "pOm", 90), ("quand", "k@", 3000), ("rose", "Roz", 60), ("robe", "ROb", 80),
    };

    private static NameRecord N(string nom, double rang = 0.6, int niveau = 1, params string[] variants) =>
        new(nom, variants, "test", rang, niveau, null, null, null, null);

    private static readonly NameRecord[] Pack =
    {
        N("Belgique", 0.6), N("Ypres", 0.3, 1, "Ieper", "Ypern"), N("La Louvière", 0.4), N("Napoléon Ier", 1.0, 1, "Napoléon"),
        N("Emma", 0.8), N("Inès", 0.6), N("Ines", 0.3), N("Pierre", 0.5), N("Camille", 0.7), N("Marie", 0.9),
        N("Elle", 0.2), N("Saint-Gilles", 0.3), N("Zoltan", 0.2, 2), N("Jeanne d'Arc", 0.8),
    };

    public NamesPackTests()
    {
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL)";
            cmd.ExecuteNonQuery();
            foreach (var (w, p, f) in Words)
            {
                cmd.CommandText = "INSERT INTO words VALUES ($o, $p, $f)";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$o", w);
                cmd.Parameters.AddWithValue("$p", p);
                cmd.Parameters.AddWithValue("$f", f);
                cmd.ExecuteNonQuery();
            }
        }
        SqliteConnection.ClearAllPools();
        _e = PredictionEngine.Load(_db);
    }

    public void Dispose()
    {
        _e.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
    }

    private NamesLexicon Lex(params NameRecord[] names) =>
        NamesLexicon.Build(names.Length == 0 ? Pack : names, _e.Knows, new HashSet<string>(new[] { "le", "la", "elle", "ce" }, StringComparer.Ordinal));

    private static string[] W(IEnumerable<Suggestion> s) => s.Select(x => x.Word).ToArray();

    // ------------------------------------------------------------------ the format

    [Theory]
    [InlineData("Belgique", true)]
    [InlineData("La Louvière", true)]
    [InlineData("Jeanne d'Arc", true)]
    [InlineData("Jeanne d’Arc", true)]
    [InlineData("J. R. R. Tolkien", true)]
    [InlineData("Saint-Gilles", true)]
    [InlineData("belgique", false)]      // a name starts with a capital
    [InlineData("SNCB", false)]          // all capitals
    [InlineData("Louis 14", false)]
    [InlineData("Paris (France)", false)]
    [InlineData("-Paris", false)]
    [InlineData("A", false)]
    public void Name_Rule(string name, bool ok) => Assert.Equal(ok, NamesFile.CheckName(name) is null);

    [Fact]
    public void Json_Format_Version_And_Licence_Resolution()
    {
        Assert.Throws<InvalidDataException>(() => NamesFile.ParseJson(@"{""format"":""autre"",""version"":1,""noms"":[]}"));
        Assert.Throws<InvalidDataException>(() => NamesFile.ParseJson(@"{""format"":""pepite-noms"",""version"":2,""noms"":[]}"));
        Assert.Throws<InvalidDataException>(() => NamesFile.ParseJson("pas du json"));
        var d = NamesFile.ParseJson(@"{""format"":""pepite-noms"",""version"":1,""titre"":""Ma classe"",""licence"":""CC0 1.0"",
            ""sources"":{""fi"":{""licence"":""MIT"",""titre"":""flags""}},
            ""noms"":[{""nom"":""Belgique"",""source"":""fi"",""image"":""images/be.png"",""variantes"":[""België"",""Belgique"",""bad name""]},
                      {""nom"":""Léa""},{""nom"":""léa""},{""nom"":""Léa""},{""nom"":""Zoé"",""licence"":""CC BY 4.0"",""niveau"":2,""rang"":3}]}");
        Assert.Equal("Ma classe", d.Titre);
        Assert.Equal(new[] { "Belgique", "Léa", "Zoé" }, d.Noms.Select(n => n.Nom));
        Assert.Equal("MIT", d.Noms[0].Licence);                     // from its source
        Assert.Equal(new[] { "België" }, d.Noms[0].Variantes);      // the name itself and a bad variant dropped
        Assert.Equal("CC0 1.0", d.Noms[1].Licence);                 // the file's licence
        Assert.Equal(2, d.Noms[2].Niveau);
        Assert.Equal(1.0, d.Noms[2].Rang);                          // clamped
        Assert.Equal(2, d.Refused.Count);                           // « léa » (lowercase) and the duplicate « Léa »
    }

    [Fact]
    public void Over_The_Limit_One_Refusal_Line_And_Sources_After_The_Names()
    {
        var d = NamesFile.ParseJson(@"{""noms"":[{""nom"":""Ana"",""source"":""s""},{""nom"":""Bea""},{""nom"":""Cléo""},{""nom"":""Dina""},{""nom"":""Eva""}],
            ""sources"":{""s"":{""licence"":""MIT""}},""format"":""pepite-noms"",""version"":1}", maxNames: 2);
        Assert.Equal(2, d.Noms.Count);
        Assert.Equal("MIT", d.Noms[0].Licence);                       // resolved although « sources » comes last
        Assert.Single(d.Refused);
        Assert.Contains("et suivantes", d.Refused[0]);
    }

    [Fact]
    public void Csv_Columns_Variants_And_Default_Licence()
    {
        var d = NamesFile.ParseCsv("\uFEFFnom;variantes;categorie;image;source;licence;auteur\r\nYpres;Ieper|Ypern;ville;;;;\r\nLéa;;prenom;lea.png;;CC0;Moi\r\n;;;;;;\r\nbad;;;;;;\r\n",
                                   "Classe", defaultLicence: "Domaine public");
        Assert.Equal(new[] { "Ypres", "Léa" }, d.Noms.Select(n => n.Nom));
        Assert.Equal(new[] { "Ieper", "Ypern" }, d.Noms[0].Variantes);
        Assert.Equal("Domaine public", d.Noms[0].Licence);
        Assert.Equal("CC0", d.Noms[1].Licence);
        Assert.Equal("lea.png", d.Noms[1].Image);
        Assert.Single(d.Refused);
        Assert.Throws<InvalidDataException>(() => NamesFile.ParseCsv("a;b\nx;y", "t"));
    }

    // ------------------------------------------------------------------ the layer

    [Fact]
    public void Layer_Indexes_Names_Variants_And_Unknown_Capitalised_Parts()
    {
        var n = Lex();
        Assert.True(n.ContainsForm("Belgique"));
        Assert.False(n.ContainsForm("belgique"));          // case significant
        Assert.True(n.ContainsForm("Ieper"));              // a variant
        Assert.True(n.ContainsForm("Louvière"));           // a part the lexicon does not know
        Assert.False(n.ContainsForm("La"));                // too short / a lexicon word
        Assert.False(n.ContainsForm("Saint"));             // a lexicon word
        Assert.True(n.ContainsForm("Gilles"));
        Assert.False(n.ContainsForm("Ier"));               // a regnal number
        Assert.False(n.ContainsForm("Arc"));               // « arc » is a word
        Assert.False(n.ContainsForm("Elle"));              // one of the most frequent words: left out
        Assert.Equal(new[] { "Inès", "Ines" }, n.SameLetters("ines"));
        Assert.Equal(new[] { "Emma" }, n.SameLetters("emma"));
    }

    [Fact]
    public void No_Pack_Gives_Identical_Results()
    {
        var probes = new[] { ("be", false), ("Be", false), ("pi", true), ("ma", false), ("C", true), ("em", false) };
        var before = probes.Select(p => _e.Predict(p.Item1, new[] { "avec" }, 8, p.Item2)).ToList();
        _e.Names = Lex();
        _e.Names = NamesLexicon.Empty;
        for (int i = 0; i < probes.Length; i++)
            Assert.Equal(before[i].Select(s => (s.Word, s.Score, s.Name)), _e.Predict(probes[i].Item1, new[] { "avec" }, 8, probes[i].Item2).Select(s => (s.Word, s.Score, s.Name)));
    }

    [Fact]
    public void Capital_Inside_A_Sentence_Lifts_Names_And_Opens_Tier_Two()
    {
        _e.Names = Lex();
        var cap = _e.Predict("Ca", new[] { "avec" }, 8, sentenceStart: false);
        Assert.Contains("Camille", W(cap));
        Assert.True(cap.First(s => s.Word == "Camille").Name);
        Assert.Contains("Zoltan", W(_e.Predict("Zo", new[] { "avec" }, 8)));          // tier 2 after a capital
        Assert.DoesNotContain("Zoltan", W(_e.Predict("zo", new[] { "avec" }, 8)));    // never without it
        Assert.DoesNotContain("Zoltan", W(_e.Predict("Zo", null, 8, sentenceStart: true)));
    }

    [Fact]
    public void No_Name_For_One_Letter_Without_A_Capital_Inside_A_Sentence()
    {
        _e.Names = Lex();
        Assert.DoesNotContain(_e.Predict("C", null, 8, sentenceStart: true), s => s.Name);   // « C » at a sentence start
        Assert.DoesNotContain(_e.Predict("c", new[] { "avec" }, 8), s => s.Name);
        Assert.Contains(_e.Predict("C", new[] { "avec" }, 8), s => s.Word == "Camille");     // a capital inside a sentence
    }

    [Fact]
    public void Names_Rank_Below_Common_Words_In_Lowercase()
    {
        _e.Names = Lex();
        var s = W(_e.Predict("mar", new[] { "le" }, 8));
        Assert.True(Array.IndexOf(s, "mardi") >= 0 && (Array.IndexOf(s, "Marie") < 0 || Array.IndexOf(s, "Marie") > Array.IndexOf(s, "mardi")));
        var p = W(_e.Predict("pie", new[] { "une" }, 8));
        Assert.True(Array.IndexOf(p, "pierre") >= 0 && (Array.IndexOf(p, "Pierre") < 0 || Array.IndexOf(p, "Pierre") > Array.IndexOf(p, "pierre")));
    }

    [Fact]
    public void A_Rare_Lowercase_Homograph_Gives_Way_To_The_Name()
    {
        _e.Names = Lex();
        var s = W(_e.Predict("belg", new[] { "la" }, 8));
        Assert.Contains("Belgique", s);
        Assert.DoesNotContain("belgique", s);
    }

    [Fact]
    public void A_Variant_Brings_Itself_And_The_Name()
    {
        _e.Names = Lex();
        var s = W(_e.Predict("Iep", new[] { "à" }, 8));
        Assert.Contains("Ieper", s);
        Assert.Contains("Ypres", s);
        Assert.True(Array.IndexOf(s, "Ieper") < Array.IndexOf(s, "Ypres"));
    }

    [Fact]
    public void Names_Follow_The_Pack_Ranking_And_Take_The_Elision_Boost()
    {
        _e.Names = Lex(N("Joseph", 0.9), N("Johnny", 0.2), N("John", 0.3), N("Emma", 0.8), N("Emmanuel", 0.85), N("Adam", 0.7),
                       N("Adolf Hitler", 0.04));
        var j = W(_e.Predict("Jo", new[] { "avec" }, 8)).Where(w => w.StartsWith("J")).ToArray();
        Assert.Equal(new[] { "Joseph", "John", "Johnny" }, j);                      // by rang, not by any context count
        var a = W(_e.Predict("Ad", new[] { "avec" }, 8));
        Assert.True(Array.IndexOf(a, "Adam") >= 0 && Array.IndexOf(a, "Adam") < Array.IndexOf(a, "Adolf Hitler"));
        double Score(string typed, string ctx, string w) => _e.Predict(typed, new[] { ctx }, 8).First(s => s.Word == w).Score;
        Assert.True(Score("Em", "d", "Emma") > Score("Em", "avec", "Emma"));       // « d'Em » → Emma lifted (D22)
    }

    [Fact]
    public void Check_Knows_Names_And_Sounds_Like_Them_In_Lowercase()
    {
        Assert.NotEqual(SpellingStatus.Known, _e.Check("Emma").Status);   // no pack: not a known word
        _e.Names = Lex();
        Assert.Equal(SpellingStatus.Known, _e.Check("Emma").Status);
        Assert.Equal(SpellingStatus.Known, _e.Check("Ieper").Status);
        Assert.Equal(SpellingStatus.Known, _e.Check("Louvière").Status);
        var low = _e.Check("emma");
        Assert.Equal(SpellingStatus.SoundsLike, low.Status);
        Assert.Equal("Emma", low.Candidate);
        Assert.True(_e.KnowsAny("Inès"));
        Assert.False(_e.KnowsAny("inès"));
    }

    // ------------------------------------------------------------------ owner decisions 2026-11-15 (D1 – D3)

    private static NameRecord Restricted(string nom) => new(nom, Array.Empty<string>(), "personnage", 0.001, 2, "images/x.png", null, null, null, true);

    [Fact]
    public void D1_Restricted_Figures_Need_A_Capital_And_Three_Letters_And_Come_Last()
    {
        _e.Names = Lex(N("Adam", 0.7), N("Adolphe", 0.01, 2), Restricted("Adolf Hitler"));
        var ad = W(_e.Predict("Ad", new[] { "avec" }, 8));
        Assert.Contains("Adam", ad);
        Assert.DoesNotContain("Adolf Hitler", ad);                                       // 2 letters: never
        var ado = W(_e.Predict("Ado", new[] { "avec" }, 8));
        Assert.True(Array.IndexOf(ado, "Adolphe") >= 0 && Array.IndexOf(ado, "Adolf Hitler") > Array.IndexOf(ado, "Adolphe"));
        Assert.DoesNotContain("Adolf Hitler", W(_e.Predict("ado", new[] { "avec" }, 8)));   // no capital: never
        Assert.DoesNotContain("Adolf Hitler", W(_e.Predict("Ado", null, 8, sentenceStart: true)));
        Assert.Contains("Hitler", W(_e.Predict("Hit", new[] { "avec" }, 8)));            // the part, with a capital and 3 letters
        Assert.DoesNotContain("Hitler", W(_e.Predict("Hi", new[] { "avec" }, 8)));
        Assert.Equal(SpellingStatus.Known, _e.Check("Hitler").Status);                   // recognised, never red
        var d = NamesFile.ParseJson(@"{""format"":""pepite-noms"",""version"":1,""noms"":[{""nom"":""Lénine"",""image"":""images/l.png"",""restreint"":true}]}");
        Assert.Null(d.Noms[0].Image);                                                    // never a picture
        Assert.True(d.Noms[0].Restreint);
        Assert.Equal(2, d.Noms[0].Niveau);
    }

    // ------------------------------------------------------------------ 0.10.2: adults' first names (owner 2026-11-17)

    private NamesLexicon AdultLex() => Lex(N("Emma", 0.8), N("Rodolphe", 0.3, 2), N("Rodrigue", 0.015, 2), N("Zoltan", 0.2, 2),
                                           Restricted("Adolf Hitler"), N("Adolphe", 0.2, 2));

    [Fact]
    public void Recognised_Tier_From_Four_Letters_Without_A_Capital()
    {
        _e.Names = AdultLex();
        Assert.Contains("Rodolphe", W(_e.Predict("rodo", new[] { "avec" }, 8)));                      // lowercase, 4 letters
        Assert.DoesNotContain("Rodolphe", W(_e.Predict("rod", new[] { "avec" }, 8)));                 // 3 letters: not yet
        Assert.Contains("Rodolphe", W(_e.Predict("Rodo", null, 8, sentenceStart: true)));             // a sentence start
        Assert.Contains("Rodolphe", W(_e.Predict("rodol", null, 8, sentenceStart: true)));
        Assert.Contains("Zoltan", W(_e.Predict("zolt", new[] { "avec" }, 8)));
        Assert.True(_e.Predict("rodo", new[] { "avec" }, 8).First(s => s.Word == "Rodolphe").Name);
    }

    [Fact]
    public void A_Very_Rare_Recognised_Name_Still_Needs_A_Capital()
    {
        _e.Names = AdultLex();   // Rodrigue: rang 0.015, below the 0.02 floor
        Assert.DoesNotContain("Rodrigue", W(_e.Predict("rodr", new[] { "avec" }, 8)));
        Assert.DoesNotContain("Rodrigue", W(_e.Predict("Rodr", null, 8, sentenceStart: true)));
        Assert.Contains("Rodrigue", W(_e.Predict("Rodr", new[] { "avec" }, 8)));                     // a capital inside a sentence
        _e.NamesRecognisedFloor = 0.01;
        try { Assert.Contains("Rodrigue", W(_e.Predict("rodr", new[] { "avec" }, 8))); }
        finally { _e.NamesRecognisedFloor = 0.02; }
    }

    [Fact]
    public void Restricted_Figures_Never_Without_A_Capital()
    {
        _e.Names = AdultLex();
        foreach (var t in new[] { "adol", "adolf", "hitler" })
        {
            Assert.DoesNotContain(_e.Predict(t, new[] { "avec" }, 8), s => s.Word is "Adolf Hitler" or "Hitler");
            var cap = char.ToUpperInvariant(t[0]) + t[1..];
            Assert.DoesNotContain(_e.Predict(cap, null, 8, sentenceStart: true), s => s.Word is "Adolf Hitler" or "Hitler");
        }
        Assert.Contains("Adolphe", W(_e.Predict("adol", new[] { "avec" }, 8)));                        // the first name is offered
        Assert.Contains("Adolf Hitler", W(_e.Predict("Adol", new[] { "avec" }, 8)));                   // a capital inside a sentence
    }

    [Fact]
    public void Recognised_From_99_Gives_The_Old_Results()
    {
        var probes = new[] { ("rodo", false), ("Rodo", true), ("rodol", true), ("zolt", false), ("Zolt", false), ("adol", false),
                             ("Adol", true), ("emma", false), ("Rodr", false), ("pierr", false) };
        _e.Names = AdultLex();
        _e.NamesRecognisedFrom = 99;
        try
        {
            var old = probes.Select(p => _e.Predict(p.Item1, new[] { "avec" }, 8, p.Item2).Select(s => (s.Word, s.Score, s.Name)).ToList()).ToList();
            Assert.DoesNotContain(old[0], s => s.Word == "Rodolphe");                                // the 0.10.1 behaviour
            Assert.DoesNotContain(old[1], s => s.Word == "Rodolphe");
            // the same as a pack whose recognised tier is never searched without a capital: the tier-1 names alone
            var tier1 = Lex(N("Emma", 0.8));
            for (int i = 0; i < probes.Length; i++)
            {
                if (probes[i].Item1[0] is var c && char.IsUpper(c) && !probes[i].Item2) continue;   // a capital inside a sentence: tier 2 as before
                _e.Names = tier1;
                var t1 = _e.Predict(probes[i].Item1, new[] { "avec" }, 8, probes[i].Item2).Select(s => (s.Word, s.Score, s.Name)).ToList();
                Assert.Equal(t1, old[i]);
                _e.Names = AdultLex();
            }
        }
        finally { _e.NamesRecognisedFrom = 4; }
    }

    [Fact]
    public void D2_A_Very_Rare_Name_That_Sounds_Like_A_Word_Is_Approx_At_A_Sentence_Start_Only()
    {
        _e.Names = Lex(N("Pome", 0.01, 2), N("Emma", 0.8));
        var c = _e.Check("Pome");
        Assert.Equal(SpellingStatus.SoundsLike, c.Status);
        Assert.True(c.WeakName);
        Assert.Contains("pomme", c.Candidates);
        Assert.Equal(SpellingMark.SoundsLike, Mark("Pome", start: true, before: '\0'));   // ≈ pomme
        Assert.Equal(SpellingMark.Known, Mark("Pome", start: false));                    // ✓ inside a sentence
        Assert.Equal(SpellingMark.Known, Mark("Emma", start: true, before: '\0'));        // a real, popular name: ✓
        Assert.True(_e.Names.IsWeak("Pome"));
        Assert.False(_e.Names.IsWeak("Emma"));
    }

    [Fact]
    public void D3_At_A_Sentence_Start_A_Common_Noun_Stays_A_Common_Noun_Unless_The_Lists_Hold_The_Name()
    {
        _e.Names = Lex(N("Rose", 0.5), N("Belgique", 0.6));
        Suggestion Tile(string typed, bool start, string w) => _e.Predict(typed, start ? null : new[] { "avec" }, 8, start).First(x => x.Word == w);
        Assert.False(Tile("Ro", true, "Rose").Name);                 // « Rose est belle »: the flower's picture
        Assert.True(Tile("Ro", false, "Rose").Name);                 // inside a sentence a capital is a name
        Assert.True(Tile("Bel", true, "Belgique").Name);             // « belgique » is rare as a common word
        _e.User = UserLexicon.Build(new[] { new UserWord("Rose") });
        Assert.True(Tile("Ro", true, "Rose").Name);                  // a pupil called Rose: a name everywhere
        _e.User = UserLexicon.Empty;
        Func<string, bool> isName = w => _e.Names.ContainsForm(w) && !_e.IsCommonWord(w);
        var d = ReadingDocument.Build("Rose est belle. Belgique, Rose.", isName: isName);
        Assert.Equal("rose", d.Tiles[0].PictureKey);
        Assert.Equal(ReadingDocument.NameKeyPrefix + "Belgique", d.Tiles.First(t => t.Text == "Belgique").PictureKey);
        Assert.Equal(ReadingDocument.NameKeyPrefix + "Rose", d.Tiles.Last(t => t.Text == "Rose").PictureKey);   // inside a sentence
    }

    // ------------------------------------------------------------------ the indicator at a sentence start (TODO item 3)

    private SpellingMark Mark(string w, bool start, char before = ' ') =>
        SpellingIndicator.Finished(new FinishedWord(w, before, ' ', start ? null : "avec", start), x => _e.Check(x));

    [Fact]
    public void Sentence_Start_Capital_Is_Judged_By_Its_Letters_But_Never_Red()
    {
        _e.Names = Lex();
        Assert.Equal(SpellingMark.SoundsLike, Mark("Maizon", start: true, before: '\0'));   // ≈ maison
        Assert.Equal(SpellingMark.None, Mark("Maizon", start: false));                      // inside a sentence: maybe a name
        Assert.Equal(SpellingMark.None, Mark("Xqzt", start: true, before: '\0'));           // never red with a capital
        Assert.Equal(SpellingMark.Known, Mark("Emma", start: true, before: '\0'));
        Assert.Equal(SpellingMark.Known, Mark("Emma", start: false));
        Assert.Equal(SpellingMark.Known, Mark("Maison", start: true, before: '\0'));
        Assert.Equal(SpellingMark.SoundsLike, Mark("emma", start: false));                  // ≈ Emma: a capital is missing
    }
}

/// <summary>Plan 10C: the teacher's names databases (<see cref="NamesImport"/>), the pictures lookup (<see cref="WordPictures"/>),
/// the « noms » lists in <c>mots.json</c>, the reading view's picture keys of names, the setting.</summary>
public class NamesImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "namesimp-" + Guid.NewGuid().ToString("N"));
    public NamesImportTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>A minimal PNG header (IHDR) of <paramref name="w"/> × <paramref name="h"/> (enough for the header check).</summary>
    private static byte[] Png(int w, int h, byte tag = 0)
    {
        var b = new byte[40];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        b[39] = tag;
        return b;
    }

    private static byte[] Jpeg(int w, int h) =>
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 1, 0, 0, 0, 0, 0 };

    [Theory]
    [InlineData("images/be.png", true)]
    [InlineData("be.png", true)]
    [InlineData("../be.png", false)]
    [InlineData("images/../../be.png", false)]
    [InlineData("/be.png", false)]
    [InlineData("C:/be.png", false)]
    [InlineData("images//be.png", false)]
    [InlineData("", false)]
    public void Picture_Paths_Stay_Inside_The_Database(string rel, bool ok) => Assert.Equal(ok, NamesImport.SafeRelative(rel));

    [Fact]
    public void Picture_Size_Is_Read_From_The_Header_Before_Decoding()
    {
        Assert.True(NamesImport.TryImageSize(Png(96, 72), out var w, out var h) && w == 96 && h == 72);
        Assert.True(NamesImport.TryImageSize(Jpeg(640, 480), out w, out h) && w == 640 && h == 480);
        Assert.Null(NamesImport.CheckImage(Png(4096, 4096)));
        Assert.NotNull(NamesImport.CheckImage(Png(5000, 10)));
        Assert.NotNull(NamesImport.CheckImage(new byte[] { 1, 2, 3, 4 }));
        Assert.NotNull(NamesImport.CheckImage(null));
        Assert.True(NamesImport.IsStoredImageName("0123456789abcdef.png"));
        Assert.False(NamesImport.IsStoredImageName("../0123456789abcdef.png"));
    }

    private string Folder(string json, params (string Rel, byte[] Bytes)[] files)
    {
        var d = Path.Combine(_dir, "base-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "noms.json"), json);
        foreach (var (rel, b) in files)
        {
            var p = Path.Combine(d, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, b);
        }
        return d;
    }

    private const string NamesJson =
        @"{""format"":""pepite-noms"",""version"":1,""titre"":""Ma classe"",
           ""noms"":[{""nom"":""Belgique"",""image"":""images/be.png"",""licence"":""Domaine public"",""variantes"":[""België""]},
                     {""nom"":""Léa"",""image"":""images/lea.png""},
                     {""nom"":""Tom"",""image"":""../secret.png"",""licence"":""CC0""},
                     {""nom"":""Zoé"",""image"":""images/grand.png"",""licence"":""CC0""},
                     {""nom"":""Ypres"",""image"":""images/be.png"",""licence"":""Domaine public""}]}";

    [Fact]
    public void Folder_Import_Stores_Thumbnails_And_Keeps_Names_Without_A_Licensed_Picture()
    {
        var src = Folder(NamesJson, ("images/be.png", Png(64, 48, 1)), ("images/lea.png", Png(64, 48, 2)), ("images/grand.png", Png(9000, 10)));
        File.WriteAllBytes(Path.Combine(_dir, "secret.png"), Png(10, 10));
        var images = Path.Combine(_dir, "profile", "images");
        int calls = 0;
        using var s = NamesImport.Open(src);
        var r = NamesImport.Build(s, images, b => { calls++; return b; }, Array.Empty<string>(), new DateTime(2026, 11, 14));
        Assert.Equal(5, r.Names);
        Assert.Equal(1, r.Images);                                  // be.png once (Ypres reuses it); Léa has no licence
        Assert.Equal(1, calls);
        var list = r.List!;
        Assert.Equal(WordListDef.Names, list.Type);
        var be = list.Words.Single(e => e.W == "Belgique").Meta!;
        Assert.True(NamesImport.IsStoredImageName(be.Image!));
        Assert.True(File.Exists(Path.Combine(images, be.Image!)));
        Assert.Equal("Domaine public", be.Licence);
        Assert.Equal(be.Image, list.Words.Single(e => e.W == "Ypres").Meta!.Image);
        Assert.Null(list.Words.Single(e => e.W == "Léa").Meta?.Image);
        Assert.Null(list.Words.Single(e => e.W == "Tom").Meta?.Image);    // ../secret.png refused
        Assert.Null(list.Words.Single(e => e.W == "Zoé").Meta?.Image);    // 9000 px refused before decoding
        Assert.Equal(3, r.Problems.Count);
        Assert.Contains("Domaine public", r.Licences);
    }

    [Fact]
    public void Zip_Import_Finds_The_Index_In_A_Top_Folder_And_Limits_Pictures()
    {
        var zip = Path.Combine(_dir, "base.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, byte[] b) { var e = z.CreateEntry(name); using var s = e.Open(); s.Write(b, 0, b.Length); }
            Add("ma-base/noms.json", System.Text.Encoding.UTF8.GetBytes(NamesJson));
            Add("ma-base/images/be.png", Png(64, 48, 1));
            Add("ma-base/images/lea.png", Png(64, 48, 2));
        }
        using var s = NamesImport.Open(zip);
        Assert.Equal("Ma classe", s.Doc.Titre);
        var full = Enumerable.Range(0, NamesImport.MaxImagesPerProfile).Select(i => i.ToString("x16") + ".png").ToList();
        var r = NamesImport.Build(s, Path.Combine(_dir, "p2"), b => b, full, DateTime.Now);
        Assert.Equal(0, r.Images);                                  // the profile already has 500 pictures
        Assert.Throws<InvalidDataException>(() => NamesImport.Open(Path.Combine(_dir, "nothing.txt.zip")));
    }

    [Fact]
    public void Names_List_Round_Trips_In_Mots_Json_With_Its_Pictures_And_Variants()
    {
        var meta = new NameMeta("0123456789abcdef.png", "CC0", "https://exemple", "Moi", "pays", new[] { "België" });
        var list = new WordListDef("", "Drapeaux", true, WordListDef.Names,
                                   new[] { new WordEntry("Belgique", WordEntry.Import, "2026-11-14", meta), new WordEntry("La Louvière", WordEntry.Import) },
                                   null, "Domaine public");
        var b = WordBook.Empty.AddNamesList(list, false, out var id, out var p);
        Assert.Null(p);
        Assert.NotNull(id);
        var back = WordBook.FromJson(JsonNode.Parse(b.ToJson().ToJsonString()) as JsonObject, false, out var over);
        Assert.False(over);
        var l = back.Get(id!)!;
        Assert.True(l.IsNames);
        Assert.Equal("Domaine public", l.Licence);
        Assert.Equal(meta.Image, l.Words[0].Meta!.Image);
        Assert.Equal(new[] { "België" }, l.Words[0].Meta!.Variantes);
        Assert.Equal("La Louvière", l.Words[1].W);                  // a space is allowed in a « noms » list
        Assert.Equal(new[] { "Belgique", "België", "La Louvière" }, WordLayering.ActiveForms(back).ToArray());
        // a picture name that is not a stored thumbnail name is dropped on reading
        var bad = JsonNode.Parse(b.ToJson().ToJsonString().Replace("0123456789abcdef.png", "..\\\\evil.png")) as JsonObject;
        Assert.Null(WordBook.FromJson(bad, false, out _).Get(id!)!.Words[0].Meta!.Image);
        // the limits: 5 000 words for a pupil
        static string Letters(int i) { var c = new char[4]; for (int k = 3; k >= 0; k--) { c[k] = (char)('a' + i % 26); i /= 26; } return "Nom" + new string(c); }
        var big = list with { Words = Enumerable.Range(0, 5001).Select(i => new WordEntry(Letters(i))).ToList() };
        WordBook.Empty.AddNamesList(big, false, out _, out var tooMany);
        Assert.Equal(WordProblem.TooManyWords, tooMany);
    }

    [Fact]
    public void A_Picture_Already_In_Use_Is_Not_A_New_One_And_Unused_Pictures_Are_Deleted()
    {
        var src = Folder(NamesJson, ("images/be.png", Png(64, 48, 1)), ("images/lea.png", Png(64, 48, 2)), ("images/grand.png", Png(9000, 10)));
        var images = Path.Combine(_dir, "p3", "images");
        using var s = NamesImport.Open(src);
        var first = NamesImport.Build(s, images, b => b, Array.Empty<string>(), DateTime.Now);
        var be = first.List!.Words.Single(e => e.W == "Belgique").Meta!.Image!;
        // the same database again, the profile already using be.png and at the limit: be.png is not a new picture
        var full = Enumerable.Range(0, NamesImport.MaxImagesPerProfile - 1).Select(i => i.ToString("x16") + ".png").Append(be).ToList();
        var again = NamesImport.Build(s, images, b => b, full, DateTime.Now);
        Assert.Equal(be, again.List!.Words.Single(e => e.W == "Belgique").Meta!.Image);
        Assert.Equal(0, again.Images);
        File.WriteAllBytes(Path.Combine(images, "0123456789abcdef.png"), Png(8, 8));
        File.WriteAllText(Path.Combine(images, "notes.txt"), "kept");
        Assert.Equal(1, NamesImport.DeleteUnreferenced(images, new[] { be }));
        Assert.True(File.Exists(Path.Combine(images, be)));
        Assert.True(File.Exists(Path.Combine(images, "notes.txt")));   // only stored thumbnail names are ever deleted
    }

    [Fact]
    public void Picture_Lookup_Order_And_The_Setting()
    {
        var own = Path.Combine(_dir, "own.png");
        var cls = Path.Combine(_dir, "cls.png");
        var pack = Path.Combine(_dir, "pack.png");
        var wp = new WordPictures(b => b);
        wp.SetPack(new Dictionary<string, string> { ["Belgique"] = pack, ["Ypres"] = pack });
        wp.SetLists(new Dictionary<string, string> { ["Léa"] = own }, new Dictionary<string, string> { ["Léa"] = cls, ["Belgique"] = cls, ["volcan"] = cls });
        Assert.Equal(own, wp.For("Léa", true));               // the pupil's before « Classe »'s
        Assert.Equal(cls, wp.For("Belgique", true));          // a teacher's picture before the pack's
        Assert.Equal(pack, wp.For("Ypres", true));
        Assert.Null(wp.For("Ypres", false));                  // the pack serves names only
        Assert.Equal(cls, wp.For("volcan", false));           // a teacher's picture of a common word
        wp.NamesOn = false;
        Assert.Null(wp.For("Belgique", true));
        Assert.Null(wp.For("Léa", true));
        Assert.Equal(cls, wp.For("volcan", false));           // « Images pour les noms propres » is about names only
        File.WriteAllBytes(pack, Png(8, 8));
        wp.NamesOn = true;
        Assert.NotNull(wp.Get(pack));
        Assert.Null(wp.Get(Path.Combine(_dir, "missing.png")));
    }

    [Fact]
    public void Reading_View_Never_Gives_A_Name_The_Common_Picture()
    {
        var d = ReadingDocument.Build("Hier, Pierre a trouvé une pierre en Belgique. Pierre rit. Maison rouge.",
                                      isName: w => w is "Pierre" or "Belgique");
        string? Key(int i) => d.Tiles.Where(t => t.IsWord).ElementAt(i).PictureKey;
        Assert.Equal("hier", Key(0));
        Assert.Equal(ReadingDocument.NameKeyPrefix + "Pierre", Key(1));       // inside a sentence
        Assert.Equal("pierre", Key(5));
        Assert.Equal(ReadingDocument.NameKeyPrefix + "Belgique", Key(7));
        Assert.Equal(ReadingDocument.NameKeyPrefix + "Pierre", Key(8));       // a sentence start, but a known name
        Assert.Equal("maison", Key(10));                                       // a sentence start, not a name
        var noNames = ReadingDocument.Build("Pierre rit. Il voit Pierre.");
        Assert.Equal("pierre", noNames.Tiles[0].PictureKey);                    // unknown at a sentence start: the common word
        Assert.Equal(ReadingDocument.NameKeyPrefix + "Pierre", noNames.Tiles.Where(t => t.IsWord).Last().PictureKey);
        Assert.Equal("sncb", ReadingDocument.Build("Il prend la SNCB.").Tiles[3].PictureKey);   // an acronym is not a name
    }

    [Fact]
    public void Name_Pictures_Setting_Defaults_On_Round_Trips_And_Locks()
    {
        Assert.True(new AppSettings().NamePictures);
        var off = new AppSettings() with { NamePictures = false };
        Assert.Equal(false, (bool?)off.ToJson()["words"]?["namePictures"]);
        Assert.False(AppSettings.FromJson(off.ToJson()).NamePictures);
        var (eff, locked) = SettingsLayering.Merge(null, null, JsonNode.Parse(@"{""words"":{""namePictures"":true}}") as JsonObject, null,
                                                   JsonNode.Parse(@"{""words"":{""namePictures"":false}}") as JsonObject);
        Assert.False(eff.NamePictures);
        Assert.True(Json.IsLocked(locked, "words.namePictures"));
    }
}
