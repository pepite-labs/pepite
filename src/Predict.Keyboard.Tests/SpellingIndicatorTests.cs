using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 10B: the spelling indicator — the engine's <c>Check</c> (known / sounds like / unknown, personal words),
/// the pure rules of <see cref="SpellingIndicator"/> (what is never marked), the tracker's finished word (the same as the
/// Eval scanner on every case of spelling-cases.json), the setting (default, round trip, machine lock, « Classe » layer)
/// and the bar geometry with the marker.</summary>
public class SpellingIndicatorTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "spell-" + Guid.NewGuid().ToString("N") + ".sqlite");
    private readonly PredictionEngine _e;

    private static readonly (string W, string Phon, double F)[] Words =
    {
        ("maison", "mEz§", 400), ("maisons", "mEz§", 90), ("mais", "mE", 5000), ("mai", "mE", 50), ("école", "ekOl", 200),
        ("femme", "fam", 500), ("pomme", "pOm", 60), ("paume", "pom", 10), ("le", "l°", 20000), ("la", "la", 18000),
        ("l", "l", 900), ("vert", "vER", 60), ("ver", "vER", 10), ("chat", "Sa", 300), ("pierre", "pjER", 80),
    };

    public SpellingIndicatorTests()
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

    // ------------------------------------------------------------------ engine Check

    [Fact]
    public void Check_Known_SoundsLike_Unknown()
    {
        Assert.Equal(SpellingStatus.Known, _e.Check("maison").Status);
        Assert.Equal(SpellingStatus.Known, _e.Check("Maison").Status);           // case does not matter
        var z = _e.Check("maizon");                                               // same whole sound key
        Assert.Equal(SpellingStatus.SoundsLike, z.Status);
        Assert.Equal("maison", z.Candidate);                                      // the most frequent first
        Assert.Contains("maisons", z.Candidates);
        var e = _e.Check("ecole");                                                // accents missing
        Assert.Equal(SpellingStatus.SoundsLike, e.Status);
        Assert.Equal("école", e.Candidate);
        Assert.Equal("femme", _e.Check("famme").Candidate);                       // Lexique pronunciation key
        Assert.Equal(SpellingStatus.Unknown, _e.Check("xqzt").Status);
        Assert.Equal(SpellingStatus.Unknown, _e.Check("maisn").Status);           // a typo is not a sound-alike
        Assert.Equal(SpellingStatus.Unknown, _e.Check("").Status);
        Assert.Equal(SpellingStatus.Known, _e.Check("ver").Status);               // a homophone that is a real word stays known
    }

    [Fact]
    public void Check_Personal_Words_And_Mes_Fautes()
    {
        Assert.Equal(SpellingStatus.Unknown, _e.Check("Vésuve").Status);
        _e.User = UserLexicon.Build(new[] { new UserWord("Vésuve"), new UserWord("zorglub") }, new[] { new SpellingFix("pome", "pomme") });
        try
        {
            Assert.Equal(SpellingStatus.Known, _e.Check("Vésuve").Status);       // a personal word is known
            Assert.Equal(SpellingStatus.Known, _e.Check("zorglub").Status);
            var v = _e.Check("Vesuve");                                           // its accent missing: sounds like it
            Assert.Equal(SpellingStatus.SoundsLike, v.Status);
            Assert.Equal("Vésuve", v.Candidate);
            Assert.Equal("pomme", _e.Check("pome").Candidate);                    // « Mes fautes » first
            // a « Classe » word hidden by the pupil is not in the snapshot: not a personal word any more
            _e.User = UserLexicon.Build(new[] { new UserWord("Vésuve") });
            Assert.Equal(SpellingStatus.Unknown, _e.Check("zorglub").Status);
        }
        finally { _e.User = UserLexicon.Empty; }
    }

    // ------------------------------------------------------------------ the pure rules

    private SpellingMark Fin(string w, char before = ' ', char after = ' ', string? prev = "la", bool start = false, bool digit = false, bool split = false,
                             char then = '\0') =>
        SpellingIndicator.Finished(new FinishedWord(w, before, after, prev, start, digit, split, then), x => _e.Check(x));

    [Fact]
    public void A_Word_Finished_By_A_Dot_Or_Colon_Is_Only_Green_Until_A_Space()
    {
        Assert.Equal(SpellingMark.None, Fin("xqzt", after: '.'));                 // « www. »: maybe an address
        Assert.Equal(SpellingMark.None, Fin("maizon", after: ':'));               // « https: »
        Assert.Equal(SpellingMark.None, Fin("xqzt", after: ':', then: '/'));      // « http:// »
        Assert.Equal(SpellingMark.Known, Fin("maison", after: '.'));              // ✓ at once
        Assert.Equal(SpellingMark.SoundsLike, Fin("maizon", after: '.', then: ' '));   // a space follows: judged in full
        Assert.Equal(SpellingMark.Unknown, Fin("xqzt", after: '.', then: '\n'));
        Assert.False(SpellingIndicator.IsWordChar('\u00F7'));                     // ÷ is not a letter
        Assert.True(SpellingIndicator.IsWordChar('\u00F8') && SpellingIndicator.IsWordChar('\u00E9'));
    }

    [Fact]
    public void A_Fragment_After_An_Unread_Jump_Is_Not_Marked()
    {
        // keys-only host: « Le chev| », a click (Reset, the read fails), then « al␣ »: « al » is a fragment
        var t = new WordTracker();
        foreach (var c in "Le chev") t.Type(c);
        t.Reset();
        foreach (var c in "al") t.Type(c);
        Assert.True(t.Typing!.Value.Split);
        t.Type(' ');
        Assert.True(t.Finished!.Value.Split);
        Assert.Equal(SpellingMark.None, SpellingIndicator.Finished(t.Finished.Value, x => _e.Check(x)));
        foreach (var c in "maizon ") t.Type(c);                                   // the next word's start is known
        Assert.Equal(SpellingMark.SoundsLike, SpellingIndicator.Finished(t.Finished!.Value, x => _e.Check(x)));
        // a word whose start a read gave is whole
        t.Reset();
        t.Seed("mai", Array.Empty<string>(), 0, "", '\0', "uia");
        foreach (var c in "zon ") t.Type(c);
        Assert.False(t.Finished!.Value.Split);
        Assert.Equal("maizon", t.Finished.Value.Word);
    }

    [Fact]
    public void Line_End_Needs_The_Caret_On_A_New_Line_Of_The_Same_Control()
    {
        var edit = new IntPtr(0x1234);
        Assert.True(KeyboardPolicy.NewLineAfterEnter(edit, 10, edit, 32));                    // Notepad: one line down
        Assert.False(KeyboardPolicy.NewLineAfterEnter(edit, 10, edit, 10));                   // Save As box still open: same line
        Assert.False(KeyboardPolicy.NewLineAfterEnter(edit, 32, edit, 10));                   // a chat sent the line: caret back up
        Assert.False(KeyboardPolicy.NewLineAfterEnter(edit, 10, new IntPtr(0x5678), 32));     // another control has the caret
        Assert.False(KeyboardPolicy.NewLineAfterEnter(IntPtr.Zero, 0, IntPtr.Zero, 0));       // no system caret: never
    }

    [Fact]
    public void A_Word_Finished_By_A_Line_Break_Is_Marked_Only_By_The_Line_End_Check()
    {
        // a chat sends « maizon⏎ », then the pupil types a space: the sent word must not get a marker from a later update
        var t = new WordTracker();
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "win32", atSentenceStart: true);
        foreach (var c in "la maizon\n") t.Type(c);
        long accepted = t.Version;                                                          // the line-end check accepted this state
        Assert.NotNull(KeyboardPolicy.FinishedForMark(t.Finished, t.Version, accepted));
        Assert.Null(KeyboardPolicy.FinishedForMark(t.Finished, t.Version, -1));            // never accepted: nothing to mark
        t.Type(' ');
        Assert.Equal('\n', t.Finished!.Value.After);                                        // still the sent word …
        Assert.Null(KeyboardPolicy.FinishedForMark(t.Finished, t.Version, accepted));      // … but no longer markable
        var space = new FinishedWord("maizon", ' ', ' ', "la", false);
        Assert.Equal(space, KeyboardPolicy.FinishedForMark(space, 7, -1));                   // other separators: unchanged
    }

    [Fact]
    public void The_Last_Word_Of_A_Line_Keeps_Its_Mark()
    {
        // owner decision (option B): Enter finishes the word; ≈ / ? are shown alone on the new line, ✓ is not
        var t = new WordTracker();
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "win32", atSentenceStart: true);
        foreach (var c in "la maizon\n") t.Type(c);
        Assert.Equal(SpellingMark.SoundsLike, SpellingIndicator.Finished(t.Finished!.Value, x => _e.Check(x)));
        t.Reset();
        foreach (var c in "la maison\n") t.Type(c);
        Assert.Equal(SpellingMark.Known, SpellingIndicator.Finished(t.Finished!.Value, x => _e.Check(x)));   // never alone in the bar
        foreach (var c in "www.e") t.Type(c);
        Assert.Null(t.Finished);                                                  // the next letter clears it
    }

    [Fact]
    public void Finished_Word_States()
    {
        Assert.Equal(SpellingMark.Known, Fin("maison"));
        Assert.Equal(SpellingMark.SoundsLike, Fin("maizon"));
        Assert.Equal(SpellingMark.Unknown, Fin("xqzt"));
        Assert.Equal(SpellingMark.Known, Fin("maison", after: '.'));
        Assert.Equal(SpellingMark.Known, Fin("maison", after: ','));
    }

    [Fact]
    public void Never_Marked_Where_The_Letters_Cannot_Be_Judged()
    {
        Assert.Equal(SpellingMark.None, Fin("a"));                                // one letter
        Assert.Equal(SpellingMark.None, Fin("SNCB"));                             // acronym
        Assert.Equal(SpellingMark.None, Fin("XQ"));
        Assert.Equal(SpellingMark.None, Fin("xqzt", digit: true));                // « 3xqzt »
        Assert.Equal(SpellingMark.None, Fin("xqzt", split: true));                // the caret was inside a word
        Assert.Equal(SpellingMark.None, Fin("xqzt", before: '-'));                // a part of a compound
        Assert.Equal(SpellingMark.None, Fin("xqzt", after: '-'));
        Assert.Equal(SpellingMark.None, Fin("aujourd", after: '\''));             // before an apostrophe
        Assert.Equal(SpellingMark.None, Fin("hui", before: '\'', prev: "aujourd")); // after an apostrophe that is no elision
        Assert.Equal(SpellingMark.None, Fin("xqzt", before: '@'));                // an address, a path
        Assert.Equal(SpellingMark.None, Fin("xqzt", after: '/'));
        Assert.Equal(SpellingMark.None, Fin("be", before: '.'));                  // « ecole.be »
    }

    [Fact]
    public void After_An_Elision_The_Word_Is_Checked()
    {
        Assert.Equal(SpellingMark.Known, Fin("école", before: '\'', prev: "l"));
        Assert.Equal(SpellingMark.SoundsLike, Fin("ecole", before: '’', prev: "l"));
        Assert.Equal(SpellingMark.Unknown, Fin("xqzt", before: '\'', prev: "qu"));
    }

    [Fact]
    public void Capitalised_Words_Are_Never_Red()
    {
        Assert.Equal(SpellingMark.None, Fin("Bruxelles"));                        // a name mid-sentence
        Assert.Equal(SpellingMark.None, Fin("Bruxelles", start: true, prev: null)); // at a sentence start too
        Assert.Equal(SpellingMark.Known, Fin("Maison", start: true, prev: null));
        // plan 10C (owner 2026-11-14): at a sentence start a capitalised word is judged by its letters (≈), never red
        Assert.Equal(SpellingMark.SoundsLike, Fin("Maizon", start: true, prev: null));
        Assert.Equal(SpellingMark.None, Fin("Maizon"));
        Assert.Equal(SpellingMark.SoundsLike, Fin("maizon", start: true, prev: null));
    }

    [Fact]
    public void While_Typing_Only_Green()
    {
        SpellingMark T(string w) => SpellingIndicator.Typing(new FinishedWord(w, ' ', '\0', null, false), _e.KnowsAny);
        Assert.Equal(SpellingMark.Known, T("mai"));
        Assert.Equal(SpellingMark.Known, T("mais"));
        Assert.Equal(SpellingMark.None, T("maiz"));                               // never ≈ nor ? while typing (Q8)
        Assert.Equal(SpellingMark.None, T("xqzt"));
        Assert.Equal(SpellingMark.None, T("m"));
        Assert.Equal(SpellingMark.None, T("MAIS"));
    }

    // ------------------------------------------------------------------ the tracker's finished word

    [Fact]
    public void Tracker_Keeps_The_Finished_Word_Until_The_Next_Letter()
    {
        var t = new WordTracker();
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "win32", atSentenceStart: true);
        foreach (var c in "la maizon") t.Type(c);
        Assert.Null(t.Finished);
        Assert.Equal("maizon", t.Typing!.Value.Word);
        t.Type(' ');
        Assert.Equal(new FinishedWord("maizon", ' ', ' ', "la", false), t.Finished);
        t.Type(',');                                                              // more separators: kept
        Assert.Equal("maizon", t.Finished!.Value.Word);
        t.Type('e');                                                              // the next letter: gone
        Assert.Null(t.Finished);
        t.Type(' ');
        t.Backspace();                                                            // a Backspace: gone
        Assert.Null(t.Finished);
        t.Reset();
        foreach (var c in "3e ") t.Type(c);
        Assert.True(t.Finished!.Value.AfterDigit);
        t.Reset();
        t.Accept("maison", true);                                               // an inserted word is finished too
        Assert.Equal(new FinishedWord("maison", '\0', ' ', null, false), t.Finished);
        t.Accept("l'", false);
        Assert.Equal(new FinishedWord("l", ' ', '\'', "maison", false), t.Finished);
    }

    private static string CasesPath()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "Predict.Eval", "spelling-cases.json");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("Predict.Eval/spelling-cases.json not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Tracker_And_Eval_Scanner_Agree_On_Every_Case()
    {
        var cases = JsonNode.Parse(File.ReadAllText(CasesPath()))!.AsArray();
        Assert.True(cases.Count >= 200, "cases: " + cases.Count);
        foreach (var c in cases)
        {
            var text = (string)c!["text"]!;
            var t = new WordTracker();
            t.Seed("", Array.Empty<string>(), 0, "", '\0', "win32", atSentenceStart: true);   // a click into an empty document
            foreach (var ch in text) t.Type(ch);
            var tracker = t.Typing ?? t.Finished;
            Assert.True(SpellingIndicator.LastWord(text) == tracker, $"« {text} »: scanner {SpellingIndicator.LastWord(text)} tracker {tracker}");
            // the same text after a jump the keyboard could not read (start unknown: the first word is a fragment)
            var u = new WordTracker();
            u.Reset();
            foreach (var ch in text) u.Type(ch);
            var ut = u.Typing ?? u.Finished;
            Assert.True(SpellingIndicator.LastWord(text, startKnown: false) == ut, $"« {text} » (unknown start): scanner {SpellingIndicator.LastWord(text, false)} tracker {ut}");
        }
    }

    // ------------------------------------------------------------------ setting and bar geometry

    [Fact]
    public void Setting_Default_On_Round_Trip_Lock_And_Class_Layer()
    {
        Assert.True(new AppSettings().SpellingIndicator);
        var off = new AppSettings() with { SpellingIndicator = false };
        Assert.Equal(false, (bool?)off.ToJson()["words"]?["spellingIndicator"]);
        Assert.False(AppSettings.FromJson(off.ToJson()).SpellingIndicator);
        Assert.True(AppSettings.FromJson(JsonNode.Parse(@"{""words"":{""spellingIndicator"":""non""}}") as JsonObject).SpellingIndicator);
        JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;
        // « Classe » turns it off for every pupil who did not choose; a pupil's own choice wins
        Assert.False(SettingsLayering.Merge(null, J(@"{""words"":{""spellingIndicator"":false}}"), null, null, null).Effective.SpellingIndicator);
        Assert.True(SettingsLayering.Merge(null, J(@"{""words"":{""spellingIndicator"":false}}"), J(@"{""words"":{""spellingIndicator"":true}}"), null, null)
                                    .Effective.SpellingIndicator);
        // the machine file locks it: the pupil's value is ignored and the path is reported locked (checkbox disabled)
        var (eff, locked) = SettingsLayering.Merge(null, null, J(@"{""words"":{""spellingIndicator"":true}}"), null,
                                                   J(@"{""words"":{""spellingIndicator"":false}}"));
        Assert.False(eff.SpellingIndicator);
        Assert.True(Json.IsLocked(locked, "words.spellingIndicator"));
    }

    [Fact]
    public void Marker_Never_Makes_The_Bar_Taller()
    {
        foreach (var size in new[] { BarSize.S, BarSize.M, BarSize.L, BarSize.XL })
            foreach (var pictures in new[] { true, false })
                foreach (var keys in new[] { true, false })
                {
                    var m = BarMetrics.For(size);
                    Assert.True(m.MarkerHeight(pictures, keys) <= m.TileHeight(pictures, keys) + 0.5, $"{size} pictures={pictures} keys={keys}");
                    Assert.True(m.MarkerDiameter(pictures) >= m.WordPt, $"{size}: the marker is at least a word high");
                }
    }

    [Fact]
    public void Trimming_Counts_The_Marker()
    {
        var words = new[] { 100, 100, 100, 100 };
        Assert.Equal(4, BarPlacement.WordsThatFit(20, words, 420));               // grip / border only
        Assert.Equal(3, BarPlacement.WordsThatFit(20 + 60, words, 420));          // + a 60 px marker: one word less
        Assert.Equal(1, BarPlacement.WordsThatFit(500, words, 420));              // the first word always stays
        Assert.Equal(0, BarPlacement.WordsThatFit(60, Array.Empty<int>(), 420));  // a marker-only bar
    }
}
