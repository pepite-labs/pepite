using System;
using System.Collections.Generic;

namespace Predict.Keyboard;

/// <summary>
/// When the word echo says two words together so the voice can make the liaison (plan 08b step 8; TODO « The voice does
/// not make French liaisons »). The echo says each word alone, so « les » … « amis » loses the /z/ of « les‿amis »:
/// when <see cref="Triggers"/> is true the second word is spoken as « les amis » instead. This only decides the
/// grouping; the engine (espeak-ng's French rules) still decides whether and how the liaison is made. Pure, ordinal,
/// lower-cased, typographic apostrophe folded.
/// </summary>
internal static class Liaison
{
    /// <summary>Words after which a liaison is obligatory (or usual) before a vowel: determiners, numerals, pronouns,
    /// short prepositions and adverbs, « c'est », preposed adjectives. Never « et ».</summary>
    private static readonly HashSet<string> Left = new(StringComparer.Ordinal)
    {
        "les", "des", "mes", "tes", "ses", "ces", "nos", "vos", "leurs", "aux",
        "un", "aucun", "mon", "ton", "son", "quelques", "plusieurs", "certains",
        "deux", "trois", "six", "dix",
        "ils", "elles", "on", "nous", "vous", "en",
        "dans", "chez", "sans", "tr\u00e8s", "tout", "quand", "c'est",
        "petit", "petits", "grand", "grands", "bon", "bons", "gros", "premier", "dernier",
    };

    /// <summary>Common school-level words with an h aspiré (no liaison: « les héros ») and the words that block a liaison
    /// although they start with a vowel (« les huit », « les onze », « les oui », « les yaourts »).</summary>
    private static readonly HashSet<string> NoLiaison = new(StringComparer.Ordinal)
    {
        "hache", "haches", "haie", "haies", "haine", "hall", "halls", "halte", "hamac", "hamacs", "hameau", "hameaux",
        "hamster", "hamsters", "hanche", "hanches", "handball", "handicap", "handicaps", "hangar", "hangars",
        "haricot", "haricots", "harpe", "harpes", "hasard", "h\u00e2te", "haut", "hauts", "haute", "hautes", "hauteur",
        "hauteurs", "h\u00e9ros", "h\u00e9risson", "h\u00e9rissons", "hibou", "hiboux", "hockey", "homard", "homards", "honte",
        "hors", "hotte", "houx", "hublot", "hublots", "hurler", "hutte", "huttes", "huit", "huiti\u00e8me", "huiti\u00e8mes",
        "onze", "onzi\u00e8me", "onzi\u00e8mes", "oui", "yaourt", "yaourts", "yoga", "yo-yo", "yoyo", "ouistiti", "ouistitis",
        "hollandais", "hongrois", "honteux", "hamburger", "hamburgers", "h\u00e9ron", "h\u00e9rons", "harnais",
        "hargne", "hennir", "hi\u00e9rarchie", "hochet", "hochets", "hoquet", "houle", "housse", "housses",
        "huer", "hurlement", "hurlements",
    };

    private const string Vowels = "a\u00e0\u00e2e\u00e9\u00e8\u00ea\u00ebi\u00ee\u00efo\u00f4u\u00fb\u00f9\u00fcy\u0153\u00e6";

    /// <summary>True when the echo should say « <paramref name="prev"/> <paramref name="next"/> » together.</summary>
    public static bool Triggers(string? prev, string? next)
    {
        if (string.IsNullOrEmpty(prev) || string.IsNullOrEmpty(next)) return false;
        var p = Fold(prev);
        var n = Fold(next);
        if (!Left.Contains(p)) return false;
        if (NoLiaison.Contains(n)) return false;
        char c = n[0];
        if (Vowels.IndexOf(c) >= 0) return true;
        return c == 'h' && n.Length > 1;   // h muet (« les hommes »), the h aspiré words are listed above
    }

    private static string Fold(string w) => w.ToLowerInvariant().Replace('\u2019', '\'');
}
