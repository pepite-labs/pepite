using System;
using System.Collections.Generic;

namespace Predict.Core;

/// <summary>
/// French elisions (D22). Lexique and the n-gram corpora split <c>l'horizon</c> into <c>l</c> + <c>horizon</c>, so
/// the engine ranks the bare forms <c>l d j m n s t c qu jusqu lorsqu puisqu</c>; the bar must show and insert
/// them with their apostrophe (<c>l'</c>). The mapping is a display step applied after ranking (the DB is not
/// rebuilt), so held-out measures keep comparing bare tokens. Also: which words take an elision before them
/// (vowel or mute-h initial), used by the engine's ranking boost after an elided word.
/// </summary>
public static class Elision
{
    /// <summary>The bare (apostrophe-less) forms that only occur elided in written French.</summary>
    public static readonly IReadOnlySet<string> Bare = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "l", "d", "j", "m", "n", "s", "t", "c", "qu", "jusqu", "lorsqu", "puisqu" };

    /// <summary>0.10.2: the full (unelided) form of each bare form (<c>j</c> → <c>je</c>, <c>qu</c> → <c>que</c>). The engine
    /// estimates the context-free frequency of an elided form Lexique has no apostrophe row for from its full form's
    /// frequency and the corpus counts (<see cref="PredictionEngine.ElisionUnigram"/>).</summary>
    public static readonly IReadOnlyDictionary<string, string> FullForm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["j"] = "je", ["m"] = "me", ["t"] = "te", ["s"] = "se", ["n"] = "ne", ["d"] = "de", ["c"] = "ce", ["qu"] = "que",
        ["l"] = "le", ["jusqu"] = "jusque", ["lorsqu"] = "lorsque", ["puisqu"] = "puisque",
    };

    public static bool IsApostrophe(char c) => c is '\'' or '’';

    /// <summary>0.10.2 (owner Q2, glued words Q5: follow the document): every apostrophe of the word written with
    /// <paramref name="apostrophe"/> — an elided word (<c>j'</c> → <c>j’</c>), a joined one (<c>t'aime</c>), « aujourd'hui »,
    /// « quelqu'un »; a word without an apostrophe, or a character that is no apostrophe, leaves the word unchanged.</summary>
    public static string Styled(string word, char apostrophe)
    {
        if (!IsApostrophe(apostrophe) || string.IsNullOrEmpty(word)) return word;
        char other = apostrophe == '\'' ? '’' : '\'';
        return word.IndexOf(other) < 0 ? word : word.Replace(other, apostrophe);
    }

    // ---- 0.10.2: glued words (plan glued-words.md: « je tème » → « t'aime »)
    /// <summary>0.10.2: the sound of each bare form at the start of a glued word, in the alphabet of
    /// <see cref="PhoneticKey.Fold"/> (« tème » → <c>tem</c> = <c>t</c> + <c>em</c>; « cétait » → <c>s</c> + <c>etet</c>): the
    /// key of the bare form before a vowel, as the fold writes it (c → s before e / i, qu → k).</summary>
    public static readonly IReadOnlyDictionary<string, string> Sound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["j"] = "j", ["m"] = "m", ["t"] = "t", ["s"] = "s", ["c"] = "s", ["n"] = "n", ["l"] = "l", ["d"] = "d", ["qu"] = "k",
        ["jusqu"] = "jusk", ["lorsqu"] = "lorsk", ["puisqu"] = "puisk",
    };

    /// <summary>The bare forms in a fixed order, for the split loops (<see cref="SplitsOf"/> gives indexes into it).</summary>
    public static readonly IReadOnlyList<string> SplitForms = new[] { "j", "m", "t", "s", "c", "n", "l", "d", "qu", "jusqu", "lorsqu", "puisqu" };
    private static readonly string[] SplitSounds = { "j", "m", "t", "s", "s", "n", "l", "d", "k", "jusk", "lorsk", "puisk" };

    /// <summary>The most splits one key can have (« s » and « c » share a sound).</summary>
    public const int MaxSplits = 4;

    /// <summary>A letter of a fold key that is a vowel sound (oral, nasal, or the <c>w</c> of « oi »): the rest of a glued
    /// word starts with one, as a word that takes an elision does.</summary>
    public static bool IsVowelKey(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u' or 'A' or 'E' or 'O' or 'U' or 'w';

    /// <summary>0.10.2: the ways a fold key splits into a bare form's <see cref="Sound"/> and a rest that starts with a vowel
    /// sound (<c>tem</c> → t; <c>setet</c> → s and c), in a fixed order: indexes into <see cref="SplitForms"/>; the rest
    /// starts at <see cref="SoundLength"/>. Fills <paramref name="into"/> (at most <see cref="MaxSplits"/>), allocates
    /// nothing.</summary>
    public static int SplitsOf(string key, Span<int> into)
    {
        int n = 0;
        if (string.IsNullOrEmpty(key)) return 0;
        for (int b = 0; b < SplitSounds.Length; b++)
        {
            var s = SplitSounds[b];
            if (n < into.Length && key.Length > s.Length && key.StartsWith(s, StringComparison.Ordinal) && IsVowelKey(key[s.Length]))
                into[n++] = b;
        }
        return n;
    }

    /// <summary>The length of the sound of <see cref="SplitForms"/>[<paramref name="form"/>] in a fold key.</summary>
    public static int SoundLength(int form) => SplitSounds[form].Length;

    /// <summary>The joined form of a bare form and a word (<c>t</c> + <c>aime</c> → <c>t'aime</c>), with a straight
    /// apostrophe (<see cref="Styled"/> writes the document's).</summary>
    public static string Join(string bare, string word) => bare + "'" + word;

    // ---- 0.11.0: glued words follow-up (plan glued-words-followup.md)
    /// <summary>0.11.0 (A): the bare forms after which « est » is looked up under the one-symbol sound <c>e</c> (« cé » →
    /// c'est, « nè » → n'est, « kè » → qu'est): Lexique writes « est » <c>E</c>, a key too short to be indexed, and the key
    /// <c>e</c> also holds « es » / « ai ». Only « est » — « et » shares the sound, and « c et » is corpus noise.</summary>
    public static readonly IReadOnlySet<string> EstForms = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "c", "s", "n", "qu" };

    /// <summary>0.11.0 (C): the clitics a subject pronoun may glue before a word (« jtaime » → je t'aime), as indexes into
    /// <see cref="SplitForms"/>: m t s n l.</summary>
    public static readonly IReadOnlyList<int> Split3Clitics = new[] { 1, 2, 3, 5, 6 };

    /// <summary>0.11.0 (C): the sound of each subject pronoun at the start of a glued word, in the alphabet of
    /// <see cref="PhoneticKey.Fold"/>, with the subjects it stands for (« jemapelle » → <c>je</c> + <c>mapel</c>, « jtaime » →
    /// <c>j</c> + <c>tem</c>; « il » and « ils », « elle » and « elles » sound alike: the corpus counts choose). A glued subject
    /// keeps letters its fold alone drops: the mute e (« elle » <c>ele</c>), the plural s before a consonant (« ilslont »
    /// <c>ils</c>, « vousmavez » <c>vus</c>), the n of « on » before m / n (« onma » <c>on</c>).</summary>
    public static readonly IReadOnlyList<(string Sound, string[] Subjects)> SubjectSounds = new (string, string[])[]
    {
        ("je", new[] { "je" }), ("j", new[] { "je" }), ("tu", new[] { "tu" }), ("il", new[] { "il", "ils" }), ("ils", new[] { "ils" }),
        ("el", new[] { "elle", "elles" }), ("ele", new[] { "elle", "elles" }), ("els", new[] { "elles" }), ("eles", new[] { "elles" }),
        ("O", new[] { "on" }), ("on", new[] { "on" }), ("nu", new[] { "nous" }), ("nus", new[] { "nous" }), ("vu", new[] { "vous" }),
        ("vus", new[] { "vous" }),
    };

    /// <summary>0.11.0: the subject pronouns of <see cref="SubjectSounds"/>, each once.</summary>
    public static readonly IReadOnlyList<string> Subjects = new[] { "je", "tu", "il", "ils", "elle", "elles", "on", "nous", "vous" };

    /// <summary>0.10.2: a joined word — a bare form, an apostrophe, a word (<c>t'aime</c>, <c>J’ai</c>, <c>qu'il</c>); never
    /// an elided word alone (<c>l'</c>) nor « aujourd'hui » / « quelqu'un » (their first part is no bare form).</summary>
    public static bool IsJoined(string? w) => IsJoined(w, out _, out _);

    /// <param name="bare">the bare form (<c>t</c>), as written.</param>
    /// <param name="main">the main word after the apostrophe (<c>aime</c>): its picture is the tile's (owner Q6).</param>
    public static bool IsJoined(string? w, out string bare, out string main)
    {
        bare = main = "";
        if (string.IsNullOrEmpty(w)) return false;
        int at = -1;
        for (int i = 0; i < w.Length; i++) if (IsApostrophe(w[i])) { at = i; break; }
        if (at <= 0 || at >= w.Length - 1 || !Bare.Contains(w[..at])) return false;
        bare = w[..at];
        main = w[(at + 1)..];
        return true;
    }

    /// <summary>A bare elidable form (<c>l</c>, <c>Qu</c>…).</summary>
    public static bool IsBare(string? w) => !string.IsNullOrEmpty(w) && Bare.Contains(w);

    /// <summary>An elided word with its apostrophe (<c>l'</c>, <c>Qu’</c>…).</summary>
    public static bool IsElided(string? w) =>
        !string.IsNullOrEmpty(w) && w.Length >= 2 && IsApostrophe(w[^1]) && Bare.Contains(w[..^1]);

    /// <summary><c>l'</c> → <c>l</c>; any other word unchanged.</summary>
    public static string Strip(string w) => IsElided(w) ? w[..^1] : w;

    /// <summary>The form shown in the bar and inserted: a bare elidable form gets its apostrophe (<c>l</c> →
    /// <c>l'</c>, case kept); any other word is unchanged. Right after a typed <c>-</c>, <c>t</c> is the
    /// euphonic t of an inversion (<c>a-t-il</c>), not <c>t'</c>.</summary>
    public static string Display(string word, bool afterHyphen = false) =>
        IsBare(word) && !(afterHyphen && string.Equals(word, "t", StringComparison.OrdinalIgnoreCase)) ? word + "'" : word;

    /// <summary>Pseudo-words of the corpus splits that are never worth showing: <c>h</c> comes from <c>l'h…</c>
    /// tokenised oddly (and is not a French word on its own).</summary>
    public static bool IsNoise(string word) => string.Equals(word, "h", StringComparison.OrdinalIgnoreCase);

    /// <summary>The ranked suggestion list as the bar shows it: elisions with their apostrophe, noise dropped,
    /// order kept (the first occurrence of a duplicate wins; case-sensitive: the main lexicon is lowercase). 0.10.2: right
    /// after a typed <c>-</c> a joined word <c>t'</c> + il / ils / elle / elles / on is dropped (« a-til » is the euphonic t of
    /// an inversion, never « t'il »); « va-t'en » stays (review 8).</summary>
    public static IReadOnlyList<Suggestion> Apply(IReadOnlyList<Suggestion> ranked, bool afterHyphen = false)
    {
        var o = new List<Suggestion>(ranked.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);   // plan 10 Q13: « Pierre » (a name) and « pierre » both stay
        foreach (var s in ranked)
        {
            if (IsNoise(s.Word)) continue;
            if (afterHyphen && IsJoined(s.Word, out var bare, out var main) && string.Equals(bare, "t", StringComparison.OrdinalIgnoreCase)
                && main.ToLowerInvariant() is "il" or "ils" or "elle" or "elles" or "on") continue;
            var w = Display(s.Word, afterHyphen);
            if (seen.Add(w)) o.Add(s with { Word = w });
        }
        return o;
    }

    /// <summary>Does a Lexique pronunciation (SAMPA-like <c>phon</c>) start with a vowel or a semivowel
    /// (<c>w</c> oiseau, <c>8</c> huile) — i.e. the word takes <c>l'</c>? The yod (<c>j</c>: yaourt, yoga) does
    /// not.</summary>
    public static bool StartsWithVowelSound(string? phon) =>
        !string.IsNullOrEmpty(phon) && "aAeEiouyOo2915@§°w8".IndexOf(phon[0]) >= 0;

    /// <summary>
    /// Lexique's <c>phon</c> does not mark the aspirated h (<c>haricot</c> = <c>aRiko</c>, like <c>horizon</c>),
    /// so the common h-aspiré words are listed here (start of the word, lower case): <c>le haricot</c>, not
    /// <c>l'haricot</c>. A ranking hint only — never a filter.
    /// </summary>
    private static readonly string[] AspiratedH =
    {
        "hach", "haie", "haill", "haine", "hair", "haï", "hâl", "hall", "halte", "hamac", "hamburger", "hameau",
        "hamster", "hanche", "handball", "handicap", "hangar", "hann", "hant", "happ", "harang", "harc", "hardi", "hareng", "harpon",
        "hargn", "haricot", "harn", "harpe", "hasard", "hât", "hauss", "haut", "havre", "hennir", "hériss", "hernie",
        "héron", "héros", "herse", "hêtre", "heurt", "hibou", "hic", "hideu", "hiérarch", "hiss", "hobby", "hoch",
        "hockey", "hollandais", "homard", "hongr", "hont", "hoquet", "horde", "hors", "hotte", "houblon", "houle",
        "houppe", "housse", "houx",
    };

    /// <summary>Mute-h words a prefix of <see cref="AspiratedH"/> would catch: <c>hall</c> ≠ <c>hallucination</c>,
    /// <c>haï</c> ≠ <c>Haïti</c>.</summary>
    private static readonly string[] MuteH = { "halluc", "haïti" };

    /// <summary>The word starts with an aspirated h (see <see cref="AspiratedH"/>). Words in <c>hu…</c> are mostly
    /// mute (<c>huile</c>, <c>huître</c>, <c>humain</c>, <c>humeur</c>): only the aspirated ones are listed there.</summary>
    public static bool HasAspiratedH(string word)
    {
        if (word.Length < 2 || char.ToLowerInvariant(word[0]) != 'h') return false;
        var w = word.ToLowerInvariant();
        if (w.StartsWith("hu", StringComparison.Ordinal))
        {
            if (w.StartsWith("huit", StringComparison.Ordinal) || w.StartsWith("hurl", StringComparison.Ordinal) ||
                w.StartsWith("hutte", StringComparison.Ordinal) || w.StartsWith("hulotte", StringComparison.Ordinal) ||
                w.StartsWith("hublot", StringComparison.Ordinal) || w.StartsWith("huer", StringComparison.Ordinal) ||
                w is "hue" or "hué" or "huée" or "huées" or "hués" or "hues")
                return true;
            return false;
        }
        foreach (var x in MuteH)
            if (w.StartsWith(x, StringComparison.Ordinal)) return false;
        foreach (var p in AspiratedH)
            if (w.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The word takes an elided article / pronoun before it (<c>l'arbre</c>, <c>l'horizon</c>,
    /// <c>l'oiseau</c>; not <c>le haricot</c>).</summary>
    public static bool TakesElision(string ortho, string? phon) => StartsWithVowelSound(phon) && !HasAspiratedH(ortho);
}
