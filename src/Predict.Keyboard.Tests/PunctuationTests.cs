using System;
using System.Linq;
using System.Text.Json.Nodes;
using Predict.Core;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 04 (D22): elisions, the automatic space and punctuation, sentence-start capitals.</summary>
public class ElisionTests
{
    [Theory]
    [InlineData("l", "l'")] [InlineData("d", "d'")] [InlineData("j", "j'")] [InlineData("m", "m'")] [InlineData("n", "n'")]
    [InlineData("s", "s'")] [InlineData("t", "t'")] [InlineData("c", "c'")] [InlineData("qu", "qu'")]
    [InlineData("jusqu", "jusqu'")] [InlineData("lorsqu", "lorsqu'")] [InlineData("puisqu", "puisqu'")]
    [InlineData("L", "L'")] [InlineData("Qu", "Qu'")]
    [InlineData("le", "le")] [InlineData("a", "a")] [InlineData("y", "y")] [InlineData("que", "que")] [InlineData("l'", "l'")]
    public void Display(string bare, string shown) => Assert.Equal(shown, Elision.Display(bare));

    [Theory]
    [InlineData("l'", true)] [InlineData("qu’", true)] [InlineData("L'", true)] [InlineData("jusqu'", true)]
    [InlineData("l", false)] [InlineData("aujourd'", false)] [InlineData("'", false)] [InlineData("", false)]
    public void IsElided(string w, bool expected) => Assert.Equal(expected, Elision.IsElided(w));

    [Fact]
    public void Apply_Maps_Drops_H_And_Keeps_Order()
    {
        var ranked = new[] { "le", "la", "l", "h", "les", "un" }.Select((w, i) => new Suggestion(w, -i, MatchKind.Prefix)).ToList();
        Assert.Equal(new[] { "le", "la", "l'", "les", "un" }, Elision.Apply(ranked).Select(s => s.Word).ToArray());
        // a bare form and its displayed form never both show
        var dup = new[] { new Suggestion("l", 0, MatchKind.Prefix), new Suggestion("l'", -1, MatchKind.Prefix) };
        Assert.Single(Elision.Apply(dup));
    }

    [Theory]
    [InlineData("arbre", "aRbR", true)] [InlineData("horizon", "oRiz§", true)] [InlineData("homme", "Om", true)]
    [InlineData("oiseau", "wazo", true)] [InlineData("huile", "8il", true)] [InlineData("enfant", "@f@", true)]
    [InlineData("histoire", "istwaR", true)] [InlineData("heure", "9R", true)]
    [InlineData("haricot", "aRiko", false)] [InlineData("héros", "eRo", false)] [InlineData("hibou", "ibu", false)]
    [InlineData("huit", "8it", false)] [InlineData("hauteur", "ot9R", false)] [InlineData("honte", "§t", false)]
    [InlineData("maison", "mEz§", false)] [InlineData("yaourt", "jauRt", false)] [InlineData("x", "", false)]
    public void TakesElision(string word, string phon, bool expected) => Assert.Equal(expected, Elision.TakesElision(word, phon));
}

public class TokenizerTests
{
    private static string Kinds(string text) =>
        string.Join(" ", Tokenizer.Tokenize(text).Select(t => $"{t.Kind}:{t.Text}"));

    [Fact] public void Aujourdhui_Is_One_Word() => Assert.Equal("Word:aujourd'hui", Kinds("aujourd'hui"));
    [Fact] public void Quelquun_Is_One_Word() => Assert.Equal("Word:quelqu'un", Kinds("quelqu'un"));
    [Fact] public void Larbre_Is_Elision_Then_Word() => Assert.Equal("Elision:l' Word:arbre", Kinds("l'arbre"));
    [Fact] public void Compound_With_Hyphen() => Assert.Equal("Word:peut-être Space:  Punct:-", Kinds("peut-être -"));

    [Fact]
    public void Curly_Apostrophe_Is_Kept_For_Display_And_Normalised_For_Matching()
    {
        var t = Tokenizer.Tokenize("C’est");
        Assert.Equal(TokenKind.Elision, t[0].Kind);
        Assert.Equal("C’", t[0].Text);
        Assert.Equal("c'", t[0].Norm);
        Assert.Equal("est", t[1].Text);
    }

    [Fact] public void Elision_At_End_Of_Text() => Assert.Equal("Word:vers Space:  Elision:l'", Kinds("vers l'"));
    [Fact] public void Jusqua() => Assert.Equal("Elision:jusqu' Word:à Space:  Word:demain", Kinds("jusqu'à demain"));

    [Fact]
    public void Abbreviation_Ends_A_Sentence_Accepted_Limit()
    {
        var text = "M. Dupont arrive.";
        Assert.Equal("Word:M SentenceEnd:. Space:  Word:Dupont Space:  Word:arrive SentenceEnd:.", Kinds(text));
        Assert.Equal(new[] { "M.", "Dupont arrive." }, Tokenizer.Sentences(text).Select(r => text[r.Start..r.End]).ToArray());
    }

    [Fact] public void Decimal_Number() => Assert.Equal("Number:1,5 Space:  Word:kg Punct:,", Kinds("1,5 kg,"));

    [Fact]
    public void No_Break_Spaces_Are_Spaces()
    {
        var text = "Bonjour" + (char)0x202F + "!" + (char)0x00A0 + "Oui";
        var t = Tokenizer.Tokenize(text);
        Assert.Equal(new[] { TokenKind.Word, TokenKind.Space, TokenKind.SentenceEnd, TokenKind.Space, TokenKind.Word }, t.Select(x => x.Kind).ToArray());
        Assert.True(Tokenizer.IsSpace((char)0x202F));
        Assert.True(Tokenizer.IsSpace((char)0x00A0));
    }

    [Fact]
    public void Offsets_Cover_The_Text_In_Order()
    {
        var text = "Il a dit : « l’arbre… »\r\nPuis, 1,5 kg ?!";
        var toks = Tokenizer.Tokenize(text);
        int pos = 0;
        foreach (var t in toks) { Assert.Equal(pos, t.Start); Assert.Equal(t.Text, text.Substring(t.Start, t.Length)); pos = t.End; }
        Assert.Equal(text.Length, pos);
        Assert.Contains(toks, t => t.Kind == TokenKind.Break && t.Text == "\r\n");
        Assert.Contains(toks, t => t.Kind == TokenKind.SentenceEnd && t.Text == "?!");
    }

    [Fact]
    public void Sentences_Split_At_Ends_And_Breaks()
    {
        var text = "Bonjour ! Il fait beau\nOui… non";
        Assert.Equal(new[] { "Bonjour !", "Il fait beau", "Oui…", "non" },
                     Tokenizer.Sentences(text).Select(r => text[r.Start..r.End]).ToArray());
    }

    [Fact]
    public void LmWords_Strip_Elisions() =>
        Assert.Equal(new[] { "vers", "l", "horizon", "qu", "il", "aujourd'hui" }, Tokenizer.LmWords("Vers l’horizon qu'il aujourd'hui").ToArray());
}

public class PunctuationPolicyTests
{
    [Theory] [InlineData("l'")] [InlineData("qu’")] [InlineData("jusqu'")]
    public void Elision_Never_Gets_A_Space(string w)
    {
        Assert.False(KeyboardPolicy.NeedsSpace(false, ' ', w));
        Assert.False(KeyboardPolicy.NeedsSpace(true, '\0', w));
        Assert.False(KeyboardPolicy.NeedsSpace(true, 'h', w));
    }

    [Fact]
    public void Other_Words_Keep_The_Old_Rule()
    {
        Assert.True(KeyboardPolicy.NeedsSpace(false, ' ', "maison"));
        Assert.False(KeyboardPolicy.NeedsSpace(true, ',', "maison"));
    }

    [Theory]
    [InlineData(',')] [InlineData('.')] [InlineData('…')] [InlineData(')')] [InlineData('\'')] [InlineData('’')] [InlineData('-')]
    public void Hugging_Punctuation_Replaces_The_Space(char c)
    {
        foreach (var mode in Enum.GetValues<PunctuationSpacing>())
            Assert.Equal((AutoSpaceFix.Replace, c.ToString()), KeyboardPolicy.AfterAutoSpace(c, mode, false));
    }

    [Theory] [InlineData(';')] [InlineData(':')] [InlineData('!')] [InlineData('?')]
    public void High_Punctuation_Follows_The_Setting(char c)
    {
        Assert.Equal((AutoSpaceFix.Replace, ((char)0x202F).ToString() + c), KeyboardPolicy.AfterAutoSpace(c, PunctuationSpacing.French, false));
        Assert.Equal((AutoSpaceFix.Replace, ((char)0x00A0).ToString() + c), KeyboardPolicy.AfterAutoSpace(c, PunctuationSpacing.FrenchNbsp, false));
        Assert.Equal((AutoSpaceFix.Replace, c.ToString()), KeyboardPolicy.AfterAutoSpace(c, PunctuationSpacing.None, false));
    }

    [Theory] [InlineData('a')] [InlineData('1')] [InlineData('(')] [InlineData('«')] [InlineData('"')]
    public void Other_Characters_Pass(char c) =>
        Assert.Equal(AutoSpaceFix.Keep, KeyboardPolicy.AfterAutoSpace(c, PunctuationSpacing.French, false).Fix);

    [Fact]
    public void Second_Space_Absorbed_Once()
    {
        Assert.Equal(AutoSpaceFix.Absorb, KeyboardPolicy.AfterAutoSpace(' ', PunctuationSpacing.French, false).Fix);
        Assert.Equal(AutoSpaceFix.Keep, KeyboardPolicy.AfterAutoSpace(' ', PunctuationSpacing.French, true).Fix);
    }

    private static readonly IntPtr Fg = (IntPtr)7, Other = (IntPtr)8;

    [Fact]
    public void State_Machine_Comma_Then_Disarmed()
    {
        var st = new AutoSpaceState();
        st.Arm(10, Fg);
        Assert.Equal((AutoSpaceFix.Replace, ","), st.OnChar(',', 10, Fg, PunctuationSpacing.French));
        Assert.False(st.IsArmed(10, Fg));   // applied once
        Assert.Equal(AutoSpaceFix.Keep, st.OnChar('.', 10, Fg, PunctuationSpacing.French).Fix);
    }

    [Fact]
    public void State_Machine_Space_Absorbed_Then_Punctuation_Still_Fixed()
    {
        var st = new AutoSpaceState();
        st.Arm(10, Fg);
        Assert.Equal(AutoSpaceFix.Absorb, st.OnChar(' ', 10, Fg, PunctuationSpacing.French).Fix);
        Assert.True(st.IsArmed(10, Fg));
        Assert.Equal((AutoSpaceFix.Replace, ((char)0x202F) + "!"), st.OnChar('!', 10, Fg, PunctuationSpacing.French));
    }

    [Fact]
    public void State_Machine_Third_Space_Goes_Through_And_Disarms()
    {
        var st = new AutoSpaceState();
        st.Arm(10, Fg);
        st.OnChar(' ', 10, Fg, PunctuationSpacing.French);
        Assert.Equal(AutoSpaceFix.Keep, st.OnChar(' ', 10, Fg, PunctuationSpacing.French).Fix);
        Assert.False(st.IsArmed(10, Fg));
    }

    [Fact]
    public void State_Machine_Letter_Disarms()
    {
        var st = new AutoSpaceState();
        st.Arm(10, Fg);
        Assert.Equal(AutoSpaceFix.Keep, st.OnChar('e', 10, Fg, PunctuationSpacing.French).Fix);
        Assert.False(st.IsArmed(10, Fg));
    }

    [Fact]
    public void State_Machine_Stale_Version_Or_Other_Window_Does_Nothing()
    {
        var st = new AutoSpaceState();
        st.Arm(10, Fg);
        Assert.Equal(AutoSpaceFix.Keep, st.OnChar(',', 11, Fg, PunctuationSpacing.French).Fix);      // a jump / key since
        Assert.Equal(AutoSpaceFix.Keep, st.OnChar(',', 10, Other, PunctuationSpacing.French).Fix);   // another window
        Assert.Equal(AutoSpaceFix.Keep, new AutoSpaceState().OnChar(',', 0, Fg, PunctuationSpacing.French).Fix);   // never armed
        st.Disarm();
        Assert.Equal(AutoSpaceFix.Keep, st.OnChar(',', 10, Fg, PunctuationSpacing.French).Fix);
    }

    /// <summary>What the controller feeds the tracker after a fix: Backspace, then the injected text.</summary>
    [Fact]
    public void Tracker_After_Comma_Fix_Has_The_Word_Back_In_Context()
    {
        var t = new WordTracker();
        foreach (var c in "la mai") t.Type(c);
        t.Accept("maison", true);
        t.Backspace();
        t.Type(',');
        Assert.Equal(new[] { "la", "maison" }, t.Context.ToArray());
        Assert.True(t.AfterSeparator);
    }

    [Fact]
    public void Tracker_After_Exclamation_Fix_Starts_A_Sentence()
    {
        var t = new WordTracker();
        foreach (var c in "bonj") t.Type(c);
        t.Accept("bonjour", true);
        t.Backspace();
        t.Type((char)0x202F);
        t.Type('!');
        Assert.Empty(t.Context);
        Assert.True(t.AtSentenceStart);
    }
}

public class ElisionTrackingTests
{
    [Fact]
    public void Accept_Elision_Then_Next_Word_Prediction()
    {
        var t = new WordTracker();
        foreach (var c in "vers ") t.Type(c);
        t.Accept("l'", spaced: false);
        Assert.True(t.AfterSeparator);                       // next-word prediction follows at once
        Assert.Equal(new[] { "vers", "l" }, t.Context.ToArray());   // bare in the context, as when typed
        Assert.Equal("", t.Current);
    }

    [Fact]
    public void Accept_Elision_Matches_Typing_It()
    {
        var a = new WordTracker(); foreach (var c in "vers l'") a.Type(c);
        var b = new WordTracker(); foreach (var c in "vers ") b.Type(c); b.Accept("l'", false);
        Assert.Equal(a.Context.ToArray(), b.Context.ToArray());
        Assert.Equal(a.AfterSeparator, b.AfterSeparator);
        // Backspace over the apostrophe brings "l" back as the word being typed, in both
        a.Backspace(); b.Backspace();
        Assert.Equal("l", a.Current); Assert.Equal("l", b.Current);
    }

    [Fact]
    public void Curly_And_Capital_Elision()
    {
        var t = new WordTracker();
        t.Accept("Qu’", false);
        Assert.Equal(new[] { "Qu" }, t.Context.ToArray());
        Assert.True(t.AfterSeparator);
    }

    [Fact]
    public void Echo_Says_The_Elision_With_The_Next_Word()
    {
        var e = new SpeechEcho { SpeakWords = true };
        foreach (var c in "vers ") e.Type(c);
        Assert.Null(e.Inserted("l'", spaced: false, spoken: false));
        Assert.Equal("l'", e.LastInserted);
        Assert.Null(e.Inserted("horizon", spaced: true, spoken: true));
        Assert.Equal("l'horizon", e.LastInserted);
    }

    [Fact]
    public void Echo_Keeps_A_Typed_Elision_When_The_Rest_Is_Inserted()
    {
        var e = new SpeechEcho { SpeakWords = true };
        foreach (var c in "l'ho") e.Type(c);
        e.Inserted("horizon", spaced: false, spoken: false);
        Assert.Equal("l'horizon", e.LastInserted);
        Assert.Equal("l'horizon", e.Type(' ')!.Text);   // the word echo says it whole
    }

    [Fact]
    public void Echo_Plain_Insert_Unchanged()
    {
        var e = new SpeechEcho();
        foreach (var c in "la mai") e.Type(c);
        e.Inserted("maison", true, true);
        Assert.Equal("maison", e.LastInserted);
    }
}

public class CapitalisationTests
{
    [Theory]
    [InlineData("le", "", true, "Le")] [InlineData("l'", "", true, "L'")] [InlineData("qu'", "q", true, "Qu'")]
    [InlineData("maison", "mai", true, "Maison")] [InlineData("le", "", false, "le")] [InlineData("maison", "mai", false, "maison")]
    [InlineData("maison", "Mai", false, "Maison")] [InlineData("maison", "MAI", false, "MAISON")] [InlineData("maison", "MAI", true, "MAISON")]
    [InlineData("été", "", true, "Été")]
    public void Cased(string word, string typed, bool start, string expected) => Assert.Equal(expected, KeyboardPolicy.Cased(word, typed, start));

    [Theory] [InlineData('.')] [InlineData('?')] [InlineData('!')] [InlineData('…')] [InlineData('\n')]
    public void Tracker_After_Sentence_End(char end)
    {
        var t = new WordTracker();
        foreach (var c in "il fait beau") t.Type(c);
        Assert.False(t.AtSentenceStart);
        t.Type(end); t.Type(' ');
        Assert.True(t.AtSentenceStart);
        t.Type('l');
        Assert.True(t.AtSentenceStart);   // the first word is still being typed
        t.Type('e'); t.Type(' ');
        Assert.False(t.AtSentenceStart);
        t.Backspace();                    // back into the first word
        Assert.True(t.AtSentenceStart);
    }

    [Fact]
    public void Tracker_Unknown_After_Jump_Known_From_A_Read()
    {
        var t = new WordTracker();
        Assert.False(t.AtSentenceStart);   // nothing known: no capital
        t.Seed("", Array.Empty<string>(), 0, "", '\0', "win32", atSentenceStart: true);
        Assert.True(t.AtSentenceStart);
        t.Reset();
        Assert.False(t.AtSentenceStart);
        t.Seed("mai", new[] { "la" }, 0, "", ' ', "win32", atSentenceStart: true);
        Assert.False(t.AtSentenceStart);   // "la" opened the sentence, not "mai"
    }

    [Fact]
    public void Tracker_Fourth_Word_Forgets_The_Start_Until_Backspace_Brings_It_Back()
    {
        var t = new WordTracker();
        t.Type('.');
        foreach (var c in "a b c d") t.Type(c);
        Assert.False(t.AtSentenceStart);
        for (int i = 0; i < 6; i++) t.Backspace();   // back to "a": the kept text still has the sentence start before it
        Assert.Equal("a", t.Current);
        Assert.True(t.AtSentenceStart);
    }

    [Theory]
    [InlineData("", true)] [InlineData("Il fait beau. ", true)] [InlineData("Il fait beau. L", true)] [InlineData("Quoi ? Bon", true)]
    [InlineData("ligne\nmo", true)] [InlineData("il fait ", false)] [InlineData("il fait b", false)] [InlineData("a 12 b", false)]
    [InlineData("Fin. un deux trois quatre", false)] [InlineData("Fin. un deux tr", false)]
    public void Extractor_Sentence_Start(string text, bool expected)
    {
        var c = ContextExtractor.Extract(text, text.Length, false, false);
        Assert.Equal(expected, c.AtSentenceStart && c.Context.Count == 0);   // what capitalisation uses (tracker)
    }

    [Theory]
    [InlineData("il fait ", true)] [InlineData("Fin. un deux tr", true)] [InlineData("Fin. un deux trois quatre ", false)]
    public void Extractor_Start_Is_Before_The_First_Context_Word(string text, bool expected) =>
        Assert.Equal(expected, ContextExtractor.Extract(text, text.Length, false, false).AtSentenceStart);

    [Fact]
    public void Extractor_Truncated_Window_Is_Not_A_Start() =>
        Assert.False(ContextExtractor.Extract("bonjour", 7, truncatedAtStart: true, false).AtSentenceStart);

    /// <summary>Same randomised equivalence as the context: typing from the start of a text vs reading it.</summary>
    [Fact]
    public void Equivalence_With_Typing()
    {
        const string alphabet = "abcé AÉ'’-,.!?1\n";
        var rnd = new Random(7);
        for (int n = 0; n < 1000; n++)
        {
            var s = new string(Enumerable.Range(0, rnd.Next(0, 30)).Select(_ => alphabet[rnd.Next(alphabet.Length)]).ToArray());
            var t = new WordTracker();
            t.Seed("", Array.Empty<string>(), 0, "", '\0', "keys", atSentenceStart: true);   // start of the text
            foreach (var ch in s) t.Type(ch);
            var c = ContextExtractor.Extract(s, s.Length, false, false);
            Assert.True(t.AtSentenceStart == (c.AtSentenceStart && c.Context.Count == 0), $"[{s}]");
        }
    }
}

public class PunctuationSettingsTests
{
    [Fact]
    public void Default_Is_French()
    {
        Assert.Equal(PunctuationSpacing.French, new AppSettings().Punctuation);
        Assert.Equal(PunctuationSpacing.French, AppSettings.FromJson(new JsonObject()).Punctuation);
    }

    [Theory] [InlineData("none", (int)PunctuationSpacing.None)] [InlineData("french", (int)PunctuationSpacing.French)]
    [InlineData("french-nbsp", (int)PunctuationSpacing.FrenchNbsp)] [InlineData("bogus", (int)PunctuationSpacing.French)]
    public void Parse(string v, int expected) =>
        Assert.Equal((PunctuationSpacing)expected, AppSettings.FromJson(new JsonObject { ["text"] = new JsonObject { ["punctuationSpace"] = v } }).Punctuation);

    [Fact]
    public void Round_Trip()
    {
        var s = new AppSettings(Punctuation: PunctuationSpacing.None);
        Assert.Equal("none", s.ToJson()["text"]!["punctuationSpace"]!.GetValue<string>());
        Assert.Equal(s, AppSettings.FromJson(s.ToJson()));
    }

    [Fact]
    public void Machine_Lock_Wins_Over_The_User()
    {
        var machine = new MachineLayer(null, new JsonObject { ["text"] = new JsonObject { ["punctuationSpace"] = "none" } });
        var user = new JsonObject { ["text"] = new JsonObject { ["punctuationSpace"] = "french" } };
        var (eff, locked) = SettingsLayering.Merge(machine, user);
        Assert.Equal(PunctuationSpacing.None, eff.Punctuation);
        Assert.Contains("text.punctuationSpace", locked);
    }
}

/// <summary>Review fixes (plan 04).</summary>
public class PunctuationReviewTests
{
    private static readonly IntPtr Fg = (IntPtr)7;

    [Theory] [InlineData(',')] [InlineData('!')] [InlineData(' ')] [InlineData('.')]
    public void Modifier_Held_Never_Corrects(char c)
    {
        Assert.Equal(AutoSpaceFix.Keep, KeyboardPolicy.AfterAutoSpace(c, PunctuationSpacing.French, false, ctrlOrAlt: true).Fix);
        var st = new AutoSpaceState();
        st.Arm(3, Fg);
        Assert.Equal(AutoSpaceFix.Keep, st.OnChar(c, 3, Fg, PunctuationSpacing.French, ctrlOrAlt: true).Fix);
        Assert.False(st.IsArmed(3, Fg));   // the key went through: disarmed
    }

    [Fact]
    public void T_After_Hyphen_Is_Not_An_Elision()
    {
        Assert.Equal("t", Elision.Display("t", afterHyphen: true));
        Assert.Equal("T", Elision.Display("T", afterHyphen: true));
        Assert.Equal("t'", Elision.Display("t"));
        Assert.Equal("l'", Elision.Display("l", afterHyphen: true));   // only t is the inversion t
        var s = new[] { new Suggestion("t", 0, MatchKind.Prefix), new Suggestion("te", -1, MatchKind.Prefix) };
        Assert.Equal(new[] { "t", "te" }, Elision.Apply(s, afterHyphen: true).Select(x => x.Word).ToArray());
    }

    [Fact]
    public void Tracker_Last_Separator()
    {
        var t = new WordTracker();
        foreach (var c in "a-t") t.Type(c);
        Assert.Equal('-', t.LastSeparator);
        Assert.Equal("t", t.Current);
        t.Reset();
        foreach (var c in "il a ") t.Type(c);
        Assert.Equal(' ', t.LastSeparator);
        t.Accept("l'", false);
        Assert.Equal('\'', t.LastSeparator);
        t.Reset();
        Assert.Equal('\0', t.LastSeparator);
    }

    [Fact]
    public void Tokenizer_Combining_Marks_Stay_In_The_Word()
    {
        var nfd = "re" + (char)0x0301 + "alite" + (char)0x0301 + " l'e" + (char)0x0301 + "te" + (char)0x0301;
        var t = Tokenizer.Tokenize(nfd);
        Assert.Equal(new[] { TokenKind.Word, TokenKind.Space, TokenKind.Elision, TokenKind.Word }, t.Select(x => x.Kind).ToArray());
        Assert.Equal(nfd.Substring(0, 9), t[0].Text);
        Assert.Equal("réalité", t[0].Norm);   // NFC for matching
        Assert.Equal("été", t[3].Norm);
    }

    [Fact]
    public void Sentences_Keep_Closing_Quotes()
    {
        var text = "« Il pleut. » Puis (vraiment !) il part.";
        Assert.Equal(new[] { "« Il pleut. »", "Puis (vraiment !)", "il part." },
                     Tokenizer.Sentences(text).Select(r => text[r.Start..r.End]).ToArray());
        var nb = "« Il pleut." + (char)0x00A0 + "» Puis";
        Assert.Equal(nb.Length - 5, Tokenizer.Sentences(nb)[0].End);
    }

    [Theory]
    [InlineData("hall", "ol", false)] [InlineData("hallucination", "alysinasj§", true)] [InlineData("hallucinant", "alysin@", true)]
    [InlineData("haïr", "aiR", false)] [InlineData("Haïti", "aiti", true)] [InlineData("harpon", "aRp§", false)]
    [InlineData("hamster", "amstER", false)] [InlineData("handicap", "@dikap", false)] [InlineData("harpe", "aRp", false)]
    [InlineData("hérisson", "eRis§", false)] [InlineData("homard", "omaR", false)] [InlineData("hors", "OR", false)]
    [InlineData("housse", "us", false)] [InlineData("hublot", "yblo", false)] [InlineData("hôpital", "opital", true)]
    [InlineData("hiver", "ivER", true)] [InlineData("humour", "ymuR", true)]
    public void Aspirated_H(string word, string phon, bool takesElision) => Assert.Equal(takesElision, Elision.TakesElision(word, phon));
}

/// <summary>Owner decision « Laisser faire Word » (D22 addition): Word spaces ; : ! ? itself.</summary>
public class AppRulesTests
{
    private static readonly IntPtr Fg = (IntPtr)9;

    [Theory] [InlineData("WINWORD", true)] [InlineData("winword", true)] [InlineData("notepad", false)] [InlineData("msedge", false)]
    [InlineData("soffice.bin", false)] [InlineData(null, false)] [InlineData("", false)]
    public void Word_Spaces_Punctuation_Itself(string? process, bool expected) => Assert.Equal(expected, AppRules.AppSpacesPunctuation(process));

    [Fact]
    public void Effective_Spacing()
    {
        Assert.Equal(PunctuationSpacing.None, AppRules.Effective(PunctuationSpacing.French, "WINWORD"));
        Assert.Equal(PunctuationSpacing.None, AppRules.Effective(PunctuationSpacing.FrenchNbsp, "WINWORD"));
        Assert.Equal(PunctuationSpacing.French, AppRules.Effective(PunctuationSpacing.French, "notepad"));
        Assert.Equal(PunctuationSpacing.None, AppRules.Effective(PunctuationSpacing.None, "msedge"));
    }

    [Fact]
    public void In_Word_Only_Our_Space_Is_Taken_Back()
    {
        var st = new AutoSpaceState();
        st.Arm(5, Fg, AppRules.Effective(PunctuationSpacing.French, "WINWORD"));
        Assert.Equal((AutoSpaceFix.Replace, "!"), st.OnChar('!', 5, Fg, PunctuationSpacing.French));   // Word adds its U+00A0
        st.Arm(5, Fg, AppRules.Effective(PunctuationSpacing.French, "WINWORD"));
        Assert.Equal((AutoSpaceFix.Replace, ","), st.OnChar(',', 5, Fg, PunctuationSpacing.French));   // other rules unchanged
        st.Arm(5, Fg, AppRules.Effective(PunctuationSpacing.French, "notepad"));
        Assert.Equal((AutoSpaceFix.Replace, ((char)0x202F) + "?"), st.OnChar('?', 5, Fg, PunctuationSpacing.None));   // armed mode wins
    }
}

/// <summary>Adaptive Word rule: read back the text before the caret and add / remove one space.</summary>
public class AdaptiveWordTests
{
    [Theory]
    [InlineData("jour!", (int)SpacingCheck.Missing)] [InlineData("jour\u00A0!", (int)SpacingCheck.Ok)] [InlineData("jour\u202F!", (int)SpacingCheck.Ok)]
    [InlineData("jour !", (int)SpacingCheck.Ok)] [InlineData("our\u202F\u00A0!", (int)SpacingCheck.Double)] [InlineData("ur\u2009 !", (int)SpacingCheck.Double)]
    [InlineData("jour!x", (int)SpacingCheck.Unknown)] [InlineData("", (int)SpacingCheck.Unknown)] [InlineData("!", (int)SpacingCheck.Missing)]
    public void Check(string before, int expected) => Assert.Equal((SpacingCheck)expected, AppRules.Check(before, '!'));

    [Fact]
    public void Space_For_Setting()
    {
        Assert.Equal((char)0x202F, AppRules.SpaceFor(PunctuationSpacing.French));
        Assert.Equal((char)0x00A0, AppRules.SpaceFor(PunctuationSpacing.FrenchNbsp));
        Assert.Equal('\0', AppRules.SpaceFor(PunctuationSpacing.None));
    }

    [Fact]
    public void Edit_Before_The_Last_Char_Is_Left_Text_Right_All_Marked()
    {
        var m = (UIntPtr)0x4C584B42u;
        var add = Inserter.BuildBeforeLast(false, ((char)0x202F).ToString(), m);
        Assert.Equal(6, add.Length);
        Assert.Equal(Inserter.VK_LEFT, add[0].u.ki.wVk);
        Assert.Equal(0x202F, add[2].u.ki.wScan);
        Assert.Equal(Inserter.VK_RIGHT, add[4].u.ki.wVk);
        Assert.All(add, i => Assert.Equal(m, i.u.ki.dwExtraInfo));
        var del = Inserter.BuildBeforeLast(true, "", m);
        Assert.Equal(new ushort[] { Inserter.VK_LEFT, Inserter.VK_LEFT, Inserter.VK_BACK, Inserter.VK_BACK, Inserter.VK_RIGHT, Inserter.VK_RIGHT },
                     del.Select(i => i.u.ki.wVk).ToArray());
    }
}
