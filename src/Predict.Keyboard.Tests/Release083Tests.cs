using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>0.8.3: the sounded final e of the phonetic keys, the spelling reference (table <c>spelling_unlisted</c>), the
/// names cap and the mute-h elision of names, the capital that says nothing (« : », « « », unknown context), the ≈
/// correction that replaces the misspelled word (tracker, document check, keys), and the echo on by default.</summary>
public class Release083Tests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "r083-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    private static readonly (string W, string Phon, double F)[] Words =
    {
        ("fais", "fE", 1390), ("fait", "fE", 2750), ("fève", "fEv", 2), ("fèves", "fEv", 1), ("fer", "fER", 60),
        ("mets", "mE", 150), ("mais", "mE", 5000), ("jamais", "ZamE", 1360), ("très", "tRE", 1590), ("ces", "se", 1540),
        ("ses", "se", 3100), ("aller", "ale", 816), ("allé", "ale", 123), ("parler", "paRle", 728), ("mal", "mal", 400),
        ("maison", "mEz§", 400), ("yeah", "jE", 14), ("mam", "mam", 1), ("maman", "mam@", 300), ("le", "l°", 20000),
        ("je", "Z°", 20000), ("si", "si", 3000), ("il", "il", 9000), ("livre", "livR", 200), ("les", "le", 14000),
        ("lequel", "l°kEl", 50), ("leur", "l9R", 900),
    };

    public Release083Tests()
    {
        using (var con = new SqliteConnection($"Data Source={_db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL); CREATE TABLE spelling_unlisted (ortho TEXT PRIMARY KEY)";
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
            cmd.CommandText = "INSERT INTO spelling_unlisted VALUES ('yeah'), ('mam')";
            cmd.Parameters.Clear();
            cmd.ExecuteNonQuery();
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

    // ------------------------------------------------------------------ item 1: the sounded final e

    [Theory]
    [InlineData("fè", "fe")] [InlineData("fais", "fe")] [InlineData("fait", "fet")] [InlineData("fé", "fe")]
    [InlineData("mè", "me")] [InlineData("très", "tre")] [InlineData("tré", "tre")] [InlineData("feu", "fe")]
    [InlineData("été", "ete")] [InlineData("fées", "fe")] [InlineData("réel", "rel")]
    [InlineData("pomme", "pom")] [InlineData("parle", "parl")] [InlineData("je", "j")] [InlineData("les", "l")]
    [InlineData("maizon", "mezO")] [InlineData("orizon", "orizO")] [InlineData("cédé", "sede")] [InlineData("geai", "je")]
    public void Fold_Keeps_A_Sounded_Final_E_Drops_A_Mute_One(string word, string key) => Assert.Equal(key, PhoneticKey.Fold(word));

    [Theory]
    [InlineData("fE", "fe")] [InlineData("mE", "me")] [InlineData("ZamE", "jame")] [InlineData("tRE", "tre")]
    [InlineData("se", "se")] [InlineData("ale", "ale")] [InlineData("paRle", "parle")] [InlineData("f2", "fe")]
    [InlineData("Z°", "j")] [InlineData("l°", "l")] [InlineData("pOm", "pom")] [InlineData("fEt", "fet")]
    public void Lexique_Key_Keeps_A_Sounded_Final_E_Drops_A_Schwa(string sampa, string key) => Assert.Equal(key, PhoneticKey.FromLexiquePhon(sampa));

    [Fact]
    public void Both_Keys_Agree_On_The_Owner_Cases()
    {
        Assert.Equal(PhoneticKey.Fold("jamè"), PhoneticKey.Fold("jamais"));
        Assert.Equal(PhoneticKey.Fold("fè"), PhoneticKey.FromLexiquePhon("fE"));
        Assert.Equal(PhoneticKey.Fold("sé"), PhoneticKey.FromLexiquePhon("se"));     // ces, ses
        Assert.Equal(PhoneticKey.Fold("alé"), PhoneticKey.FromLexiquePhon("ale"));   // aller, allé
    }

    [Theory]
    [InlineData("si je", "fè", "fais")] [InlineData("je", "mè", "mets")] [InlineData(null, "jamè", "jamais")]
    [InlineData(null, "tré", "très")] [InlineData(null, "sé", "ces")] [InlineData(null, "alé", "aller")]
    [InlineData(null, "parlé", "parler")] [InlineData("il", "fé", "fait")]
    public void A_Sounded_Final_E_Typed_Brings_The_Word(string? ctx, string typed, string intended)
    {
        var s = _e.Predict(typed, ctx is null ? null : ctx.Split(' '), 8);
        Assert.Contains(s, x => x.Word == intended);
    }

    // ------------------------------------------------------------------ item 2: the spelling reference

    [Fact]
    public void A_Form_Left_Out_Of_The_Reference_Is_Never_Green()
    {
        Assert.Equal(2, _e.UnlistedCount);                               // two forms read from the table
        Assert.True(_e.Knows("yeah"));                                   // still a lexicon word for prediction
        Assert.False(_e.KnowsSpelled("yeah"));
        Assert.NotEqual(SpellingStatus.Known, _e.Check("yeah").Status);
        var m = _e.Check("mam");
        Assert.NotEqual(SpellingStatus.Known, m.Status);
        Assert.DoesNotContain("mam", m.Candidates);                      // never its own correction
        Assert.False(_e.KnowsForMark("mam", false));                     // no ✓ while typing either
        Assert.Equal(SpellingStatus.Known, _e.Check("maman").Status);
        Assert.DoesNotContain("yeah", _e.Check("yeh").Candidates);       // never offered as a correction
        Assert.Contains(_e.Predict("yea", null, 8), x => x.Word == "yeah");   // prediction unchanged
    }

    [Fact]
    public void Without_The_Table_Every_Lexicon_Form_Is_Known()
    {
        var db = Path.Combine(Path.GetTempPath(), "r083b-" + Guid.NewGuid().ToString("N") + ".sqlite");
        using (var con = new SqliteConnection($"Data Source={db}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL); INSERT INTO words VALUES ('yeah', 'jE', 14)";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        using (var e = PredictionEngine.Load(db))
        {
            Assert.Equal(0, e.UnlistedCount);
            Assert.Equal(SpellingStatus.Known, e.Check("yeah").Status);
        }
        SqliteConnection.ClearAllPools();
        try { File.Delete(db); } catch (IOException) { }
    }

    /// <summary>Publication cleanup (2026-11-16): the MPL-2.0 reference lives in spelling.sqlite next to the database; it
    /// wins over a table left inside an older database, and an explicit path wins over both.</summary>
    [Fact]
    public void The_Reference_Is_Read_From_Spelling_Sqlite_Next_To_The_Database()
    {
        var dir = Path.Combine(Path.GetTempPath(), "r083c-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        void Make(string path, string sql)
        {
            using var con = new SqliteConnection($"Data Source={path}");
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        var db = Path.Combine(dir, "prediction.sqlite");
        Make(db, "CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL); INSERT INTO words VALUES ('yeah', 'jE', 14), ('mam', 'mam', 1), ('team', 'tim', 3);" +
                 "CREATE TABLE spelling_unlisted (ortho TEXT PRIMARY KEY); INSERT INTO spelling_unlisted VALUES ('team')");
        var other = Path.Combine(dir, "other.sqlite");
        Make(other, "CREATE TABLE spelling_unlisted (ortho TEXT PRIMARY KEY); INSERT INTO spelling_unlisted VALUES ('mam')");
        SqliteConnection.ClearAllPools();
        try
        {
            using (var e = PredictionEngine.Load(db))                        // no sibling file: the table of the database
                Assert.False(e.KnowsSpelled("team"));
            Make(Path.Combine(dir, PredictionEngine.SpellingFileName),
                 "CREATE TABLE spelling_unlisted (ortho TEXT PRIMARY KEY); INSERT INTO spelling_unlisted VALUES ('yeah')");
            SqliteConnection.ClearAllPools();
            using (var e = PredictionEngine.Load(db))                        // the sibling file wins
            {
                Assert.Equal(1, e.UnlistedCount);
                Assert.False(e.KnowsSpelled("yeah"));
                Assert.True(e.KnowsSpelled("team"));
            }
            using (var e = PredictionEngine.Load(db, other))                 // an explicit path wins over both
            {
                Assert.False(e.KnowsSpelled("mam"));
                Assert.True(e.KnowsSpelled("yeah"));
                Assert.Null(e.SpellingProblem);
            }
            SqliteConnection.ClearAllPools();
            File.WriteAllText(Path.Combine(dir, PredictionEngine.SpellingFileName), "not a database, a corrupt copy");
            using (var e = PredictionEngine.Load(db))                        // unreadable file: no crash, the old table
            {
                Assert.NotNull(e.SpellingProblem);
                Assert.Equal(1, e.UnlistedCount);
                Assert.False(e.KnowsSpelled("team"));
                Assert.Contains(e.Predict("yea", null, 8), x => x.Word == "yeah");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------ item 4: names

    private static NameRecord Name(string nom, double rang, string cat = "prenom") => new(nom, Array.Empty<string>(), cat, rang, 1, null, null, null, null);

    [Fact]
    public void Names_Take_At_Most_Half_The_Bar_Below_Three_Letters_After_A_Capital()
    {
        _e.Names = NamesLexicon.Build(new[] { "Léa", "Léo", "Léon", "Léna", "Lewis", "Lenny", "Leila", "Léonie", "Léane" }.Select(n => Name(n, 0.5)), _e.Knows);
        try
        {
            var s = _e.Predict("Le", new[] { "le", "livre" }, 8);
            Assert.True(s.Count(x => x.Name) <= 4, string.Join(", ", s.Select(x => x.Word)));
            Assert.True(s[0].Name);                                   // the best name keeps its rank
            Assert.Contains(s, x => x.Word == "les");
            // the keyboard asks for 11 and shows 8 (VM recheck): every first p words hold at most ⌈p / 2⌉ names
            var eleven = _e.Predict("Le", new[] { "le", "livre" }, 11);
            for (int p = 1; p <= eleven.Count; p++) Assert.True(eleven.Take(p).Count(x => x.Name) <= (p + 1) / 2, string.Join(", ", eleven.Select(x => x.Word)));
            var three = _e.Predict("Léo", new[] { "le", "livre" }, 8);   // from three letters, no cap
            Assert.Contains(three, x => x.Word == "Léonie");
        }
        finally { _e.Names = NamesLexicon.Empty; }
    }

    [Fact]
    public void A_Mute_H_Name_Takes_The_Elision_Boost()
    {
        _e.Names = NamesLexicon.Build(new[] { Name("Hélène", 0.2), Name("Hector", 0.9), Name("Hongrie", 0.9, "pays") }, _e.Knows);
        try
        {
            var s = _e.Predict("H", new[] { "d'" }, 8);
            int hel = s.ToList().FindIndex(x => x.Word == "Hélène"), hon = s.ToList().FindIndex(x => x.Word == "Hongrie");
            Assert.True(hel >= 0 && (hon < 0 || hel < hon), string.Join(", ", s.Select(x => x.Word)));   // aspirated h: no boost
        }
        finally { _e.Names = NamesLexicon.Empty; }
    }

    [Fact]
    public void A_Capital_After_A_Colon_A_Quote_Or_An_Unknown_Context_Is_Neutral()
    {
        Assert.True(CapitalContext.OpensIn(" : "));
        Assert.True(CapitalContext.OpensIn(" « "));
        Assert.True(CapitalContext.OpensIn("— "));
        Assert.False(CapitalContext.OpensIn(", "));
        var t = new WordTracker();
        Assert.True(t.CapitalNeutral);                          // nothing known before (after a jump)
        foreach (var c in "il a dit : Me") t.Type(c);
        Assert.True(t.CapitalNeutral);
        t.Reset(); foreach (var c in "il a dit : « Je") t.Type(c);
        Assert.True(t.CapitalNeutral);
        t.Reset(); foreach (var c in "le livre Le") t.Type(c);
        Assert.False(t.CapitalNeutral);                         // a capital inside a known sentence
        t.Reset(); foreach (var c in "Il dort. Le") t.Type(c);
        Assert.True(t.CapitalNeutral);
    }

    // ------------------------------------------------------------------ item 3: the ≈ correction

    [Fact]
    public void The_Tracker_Can_Correct_The_Word_Just_Finished_Only()
    {
        var t = new WordTracker();
        foreach (var c in "le mmmmal ") t.Type(c);
        Assert.True(t.CanCorrect("mmmmal", out var sep));
        Assert.Equal(' ', sep);
        t.CorrectFinished("mmmmal", "mal");
        Assert.Equal("mal", t.Finished!.Value.Word);
        Assert.Equal(new[] { "le", "mal" }, t.Context);
        foreach (var c in "x") t.Type(c);
        Assert.False(t.CanCorrect("mal", out _));               // a letter typed since
        t.Reset(); foreach (var c in "mmmmal  ") t.Type(c);
        Assert.False(t.CanCorrect("mmmmal", out _));            // two separators: not exactly word + separator
        t.Reset(); foreach (var c in "mmmmal ") t.Type(c);
        Assert.False(t.CanCorrect("mmmmal", out _));            // start unknown after a jump: a fragment (Split)
        t.Reset(); t.Seed("", Array.Empty<string>(), 0, "", '\0', "uia", atSentenceStart: true);
        foreach (var c in "Maizon\n") t.Type(c);
        Assert.True(t.CanCorrect("Maizon", out sep));
        Assert.Equal('\n', sep);
        t.CorrectFinished("Maizon", "Maison");
        Assert.True(t.AtSentenceStart);
    }

    [Fact]
    public void The_Document_Must_End_With_The_Word_And_Its_Separator()
    {
        Assert.True(SoundAlikeCorrection.DocumentEndsWith("le mmmmal ", "mmmmal", ' ', out var w) && w == "mmmmal");
        Assert.True(SoundAlikeCorrection.DocumentEndsWith("Maizon\r\n", "maizon", '\n', out w) && w == "Maizon");   // the app's capital
        Assert.True(SoundAlikeCorrection.DocumentEndsWith("Maizon\r", "Maizon", '\n', out _));
        Assert.False(SoundAlikeCorrection.DocumentEndsWith("le mmmmal  ", "mmmmal", ' ', out _));
        Assert.False(SoundAlikeCorrection.DocumentEndsWith("le xmmmmal ", "mmmmal", ' ', out _));   // a letter glued before
        Assert.False(SoundAlikeCorrection.DocumentEndsWith("Maizon\n    ", "Maizon", '\n', out _)); // an editor indented
        Assert.False(SoundAlikeCorrection.DocumentEndsWith("mal ", "mmmmal", ' ', out _));
        Assert.Equal("Maison", SoundAlikeCorrection.CasedLike("maison", "Maizon"));
        Assert.Equal("mal", SoundAlikeCorrection.CasedLike("mal", "mmmmal"));
        Assert.True(SoundAlikeCorrection.KeysOnlyCertain(' ') && SoundAlikeCorrection.KeysOnlyCertain(','));
        Assert.False(SoundAlikeCorrection.KeysOnlyCertain('\n') || SoundAlikeCorrection.KeysOnlyCertain('.') || SoundAlikeCorrection.KeysOnlyCertain(' '));
    }

    [Fact]
    public void The_Replacement_Keys()
    {
        var m = (UIntPtr)7;
        var plain = SoundAlikeCorrection.Build(6, "mal", ' ', m);
        Assert.Equal((7 + 4) * 2, plain.Length);                 // 7 Backspaces (word + space), then « mal␣ »
        Assert.Equal(Inserter.VK_BACK, plain[0].u.ki.wVk);
        Assert.Equal(' ', (char)plain[^1].u.ki.wScan);
        var line = SoundAlikeCorrection.Build(6, "Maison", '\n', m);
        Assert.Equal(Inserter.VK_LEFT, line[0].u.ki.wVk);        // Left over the line break, never deleted
        Assert.Equal(Inserter.VK_RIGHT, line[^2].u.ki.wVk);
        Assert.Equal(2 + 12 + 12 + 2, line.Length);              // Left, 6 Backspaces, « Maison », Right
        Assert.DoesNotContain(line, x => x.u.ki.wScan == '\n' || x.u.ki.wScan == '\r');
    }

    // ------------------------------------------------------------------ review 0.8.3: the read decides, always

    [Theory]
    [InlineData("Ok", true, ' ', false, "Replace")]
    [InlineData("Ok", true, ' ', true, "Replace")]
    [InlineData("Ok", false, ' ', false, "Insert")]      // AutoCorrect changed the word
    [InlineData("Stale", true, ' ', false, "Nothing")]   // a key or a jump came first
    [InlineData("Password", false, ' ', false, "Nothing")]
    [InlineData("Unavailable", false, ' ', false, "Insert")]           // re-review: an unreadable host gets an insertion
    [InlineData("Unavailable", false, ',', false, "Insert")]
    [InlineData("Unavailable", false, ' ', true, "ReplaceKeysOnly")]   // only an allow-listed host (test switch)
    [InlineData("Unavailable", false, ',', true, "ReplaceKeysOnly")]
    [InlineData("Unavailable", false, '\n', true, "Insert")]
    [InlineData("Unavailable", false, '.', true, "Insert")]
    [InlineData("Timeout", false, ' ', true, "Insert")]    // a failed read
    [InlineData("Selection", false, ' ', true, "Insert")]
    public void The_Correction_Read_Decides(string st, bool agrees, char sep, bool keysOnlyAllowed, string expected) =>
        Assert.Equal(Enum.Parse<SoundAlikeCorrection.Action>(expected), SoundAlikeCorrection.Decide(Enum.Parse<ReadStatus>(st), agrees, sep, keysOnlyAllowed));

    [Fact]
    public void Keys_Only_Replacement_Is_Denied_By_Default_And_Always_For_Rewriting_Hosts()
    {
        Assert.Empty(SoundAlikeCorrection.KeysOnlyHosts);                         // the product ships no host
        Assert.False(SoundAlikeCorrection.KeysOnlyAllowed("notepad") || SoundAlikeCorrection.KeysOnlyAllowed("soffice") || SoundAlikeCorrection.KeysOnlyAllowed(null));
        foreach (var p in new[] { "WINWORD", "outlook", "POWERPNT", "soffice", "soffice.bin", "swriter", "scalc", "wps", "et", "wpp", "DesktopEditors", "chrome", "msedge", "firefox", "olk", "ms-teams" })
            Assert.True(SoundAlikeCorrection.RewritesWords(p), p);
        try
        {
            SoundAlikeCorrection.KeysOnlyHosts.Add("powershell");                 // the test switch (--keys-only-replace)
            SoundAlikeCorrection.KeysOnlyHosts.Add("soffice");
            Assert.True(SoundAlikeCorrection.KeysOnlyAllowed("powershell"));
            Assert.False(SoundAlikeCorrection.KeysOnlyAllowed("soffice"));        // a rewriter stays denied
        }
        finally { SoundAlikeCorrection.KeysOnlyHosts.Clear(); }
    }

    /// <summary>0.11.0: one LibreOffice list (HostApps) for every rule; UIA is never asked there (Unavailable at once).</summary>
    [Fact]
    public void LibreOffice_Host_Classification()
    {
        foreach (var p in HostApps.LibreOffice)
        {
            Assert.True(HostApps.IsLibreOffice(p), p);
            Assert.True(HostApps.UiaTextUnavailable(p), p);
            Assert.True(SoundAlikeCorrection.RewritesWords(p), p);   // keys-only replacement stays denied
        }
        Assert.True(HostApps.IsLibreOffice("SOFFICE.BIN"));
        foreach (var p in new[] { "notepad", "WINWORD", "chrome", "msedge", "soffice.exe", "office", "", null })
        {
            Assert.False(HostApps.IsLibreOffice(p), p ?? "null");
            Assert.False(HostApps.UiaTextUnavailable(p), p ?? "null");
        }
        Assert.Null(HostApps.ProcessNameOf(IntPtr.Zero));
    }

    [Theory]
    [InlineData("xterm-helper-textarea", null, true)]                 // VS Code / xterm.js: the class name
    [InlineData(null, "xterm-helper-textarea", true)]                 // or the automation id
    [InlineData("monaco-editor xterm-helper-textarea", "", true)]
    [InlineData("Edit", "", false)]
    [InlineData("inputarea monaco-mouse-cursor-text", "", false)]   // VS Code's editor: tracked
    [InlineData(null, null, false)]
    public void A_Web_Terminal_Is_Recognised(string? className, string? automationId, bool expected) =>
        Assert.Equal(expected, KeyboardPolicy.IsWebTerminal(className, automationId));

    [Fact]
    public void A_Second_Click_Is_Ignored_Only_On_The_Same_Tile_Or_Spot()
    {
        var first = new TileClick(1000, 0, 400, 380);
        Assert.True(KeyboardPolicy.IsSecondClickOfDouble(first, new TileClick(1040, 0, 400, 380), 500, 4, 4));    // double click
        Assert.True(KeyboardPolicy.IsSecondClickOfDouble(first, new TileClick(1040, 0, 900, 380), 500, 4, 4));    // same tile (bar rebuilt)
        Assert.True(KeyboardPolicy.IsSecondClickOfDouble(first, new TileClick(1040, 3, 401, 381), 500, 4, 4));    // same spot, new bar
        Assert.False(KeyboardPolicy.IsSecondClickOfDouble(first, new TileClick(1040, 3, 700, 380), 500, 4, 4));   // another tile: a pick
        Assert.False(KeyboardPolicy.IsSecondClickOfDouble(first, new TileClick(1600, 0, 400, 380), 500, 4, 4));   // too late
        Assert.False(KeyboardPolicy.IsSecondClickOfDouble(first, new TileClick(900, 0, 400, 380), 500, 4, 4));    // older message
    }

    [Fact]
    public void After_Pause_Or_A_Profile_Switch_The_Word_Can_Still_Be_Corrected_And_The_Read_Decides()
    {
        // Pause / Reprendre and a profile switch reset the tracker (Origin « keys »): the target is kept (the word typed
        // after the reset is whole) and the read — always made — decides; Word's read agrees → replaced, disagrees → insert
        var t = new WordTracker();
        foreach (var c in "x ") t.Type(c);
        t.Reset();                                                          // Reprendre / profile switch
        foreach (var c in "le mmmmal ") t.Type(c);
        Assert.Equal("keys", t.Origin);
        Assert.True(t.CanCorrect("mmmmal", out var sep));
        Assert.Equal(SoundAlikeCorrection.Action.Replace, SoundAlikeCorrection.Decide(ReadStatus.Ok, true, sep, keysOnlyAllowed: false));
        Assert.Equal(SoundAlikeCorrection.Action.Insert, SoundAlikeCorrection.Decide(ReadStatus.Ok, false, sep, keysOnlyAllowed: false));
    }

    [Fact]
    public void All_Capitals_Stay_All_Capitals()
    {
        Assert.Equal("MAISON", SoundAlikeCorrection.CasedLike("maison", "MAIZON"));
        Assert.Equal("Maison", SoundAlikeCorrection.CasedLike("maison", "Maizon"));
        Assert.Equal("Léa", SoundAlikeCorrection.CasedLike("Léa", "léa"));   // a name keeps its capital
        // 0.10.2 (glued words): a joined correction takes the capital of the glued word (« Jai » → « J'ai », « JAI » → « J'AI »)
        Assert.Equal("J'ai", SoundAlikeCorrection.CasedLike("j'ai", "Jai"));
        Assert.Equal("J’ai", SoundAlikeCorrection.CasedLike("j’ai", "Jai"));
        Assert.Equal("J'AI", SoundAlikeCorrection.CasedLike("j'ai", "JAI"));
        Assert.Equal("l'école", SoundAlikeCorrection.CasedLike("l'école", "lécole"));
    }

    /// <summary>0.10.2 (glued words plan, owner Q2): the ≈ list is ordered by the words before the finished word: the tracker's
    /// context once a space finished « tème » ends with it, and it is left out.</summary>
    [Fact]
    public void The_List_Is_Ranked_With_The_Words_Before_The_Finished_Word()
    {
        var t = new WordTracker();
        foreach (var c in "et je tème ") t.Type(c);
        Assert.Equal(new[] { "et", "je" }, KeyboardPolicy.WordsBefore(t.Context, t.Finished!.Value.Word).ToArray());
        Assert.Equal(new[] { "et", "je" }, KeyboardPolicy.WordsBefore(new[] { "et", "je", "Tème" }, "tème").ToArray());
        Assert.Empty(KeyboardPolicy.WordsBefore(Array.Empty<string>(), "tème"));
        t.Reset();
        foreach (var c in "je tème.") t.Type(c);   // a sentence end cleared the context: nothing before
        Assert.Empty(KeyboardPolicy.WordsBefore(t.Context, t.Finished!.Value.Word));
        Assert.Equal(new[] { "le" }, KeyboardPolicy.WordsBefore(new[] { "le" }, "tème").ToArray());   // not the word: as it is
    }

    [Fact]
    public void Oe_Ligature_And_Oe_Letters_Share_A_Key()
    {
        Assert.Equal(PhoneticKey.Fold("coeur"), PhoneticKey.Fold("cœur"));
        Assert.Equal(PhoneticKey.Fold("soeur"), PhoneticKey.Fold("sœur"));
        Assert.Equal(PhoneticKey.Fold("oeil"), PhoneticKey.Fold("œil"));
        Assert.StartsWith("k", PhoneticKey.Fold("cœur"));
    }

    // ------------------------------------------------------------------ owner 2026-11-15: « ne rien lire si on ne sait pas »

    [Theory]
    [InlineData("conhost", "ConsoleWindowClass", null, true)]
    [InlineData("WindowsTerminal", "CASCADIA_HOSTING_WINDOW_CLASS", null, true)]
    [InlineData("cmd", "ConsoleWindowClass", null, true)]
    [InlineData("powershell", "ConsoleWindowClass", null, true)]
    [InlineData("mstsc", "TscShellContainerClass", "IHWindowClass", true)]
    [InlineData("vmconnect", "WindowsForms10.Window.8.app.0.1", null, true)]
    [InlineData("VirtualBoxVM", "Qt5QWindowIcon", null, true)]
    [InlineData("notepad", "Notepad", "Edit", false)]
    [InlineData("WINWORD", "OpusApp", "_WwG", false)]
    public void Terminals_And_Remote_Windows_Are_Private(string process, string top, string? focus, bool expected) =>
        Assert.Equal(expected, PrivateHosts.Is(process, top, focus));

    [Trait("Category", "Timing")]
    [Fact]
    public void Typed_Text_Is_Spoken_Only_In_A_Proven_Field_Outside_Terminals()
    {
        var fg = (IntPtr)9;
        var status = ProbeStatus.NotPassword;
        bool terminal = false;
        var forwarded = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var g = new EchoGuard(_ => status, () => fg, i => forwarded.Enqueue(i.Text), log.Enqueue, null, _ => terminal);
        g.Submit(new SpeechItem("oui", SpeechKind.Word), fg);                        // NotPassword: spoken
        Assert.True(Wait(() => forwarded.Contains("oui")));
        status = ProbeStatus.Unavailable;
        g.Submit(new SpeechItem("non1", SpeechKind.Inserted), fg);                   // the inserted word too
        Assert.True(Wait(() => log.Any(l => l.StartsWith("echo dropped (field not proven)"))));
        status = ProbeStatus.NotPassword; terminal = true;
        g.Submit(new SpeechItem("non2", SpeechKind.Sentence), fg);
        Assert.True(Wait(() => log.Any(l => l.StartsWith("echo dropped (terminal or remote window)"))));
        Assert.Equal(new[] { "oui" }, forwarded.ToArray());
        Assert.DoesNotContain(log, l => l.Contains("non1") || l.Contains("non2"));
    }

    private static bool Wait(Func<bool> cond, int ms = 30000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; System.Threading.Thread.Sleep(5); }
        return cond();
    }

    // ------------------------------------------------------------------ owner 2026-11-15: the echo on by default

    [Fact]
    public void Echo_On_By_Default_An_Explicit_False_Stays()
    {
        Assert.True(new AppSettings().Speech.SpeakWords);
        Assert.True(new AppSettings().Speech.SpeakSentences);
        var off = AppSettings.FromJson(JsonNode.Parse("{\"speech\":{\"speakWords\":false,\"speakSentences\":false}}") as JsonObject);
        Assert.False(off.Speech.SpeakWords || off.Speech.SpeakSentences);
        var other = AppSettings.FromJson(JsonNode.Parse("{\"speech\":{\"rate\":2}}") as JsonObject);   // never set: on
        Assert.True(other.Speech.SpeakWords && other.Speech.SpeakSentences);
        // the machine file still wins: its defaults over the built-in ones, its locks over the profile
        static JsonObject J(string t) => (JsonObject)JsonNode.Parse(t)!;
        var (m, _) = SettingsLayering.Merge(J(@"{""speech"":{""speakWords"":false}}"), J("{}"), J("{}"));
        Assert.False(m.Speech.SpeakWords);
        Assert.True(m.Speech.SpeakSentences);
        var (l, locked) = SettingsLayering.Merge(J("{}"), J(@"{""speech"":{""speakSentences"":true}}"), J(@"{""speech"":{""speakSentences"":false}}"));
        Assert.False(l.Speech.SpeakSentences);
        Assert.Contains("speech.speakSentences", locked);
    }
}
