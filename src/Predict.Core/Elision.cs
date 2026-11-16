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

    public static bool IsApostrophe(char c) => c is '\'' or '’';

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
    /// order kept (the first occurrence of a duplicate wins; case-sensitive: the main lexicon is lowercase).</summary>
    public static IReadOnlyList<Suggestion> Apply(IReadOnlyList<Suggestion> ranked, bool afterHyphen = false)
    {
        var o = new List<Suggestion>(ranked.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);   // plan 10 Q13: « Pierre » (a name) and « pierre » both stay
        foreach (var s in ranked)
        {
            if (IsNoise(s.Word)) continue;
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
