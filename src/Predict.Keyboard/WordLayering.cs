using System;
using System.Collections.Generic;
using System.Linq;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>The words a profile actually gets, as handed to <see cref="UserLexicon.Build"/>; counts for the log.</summary>
/// <param name="Exclus">0.11.0 (plan offensive-words B1): the « Classe » « Mots à ne jamais proposer » this profile gets —
/// all of them, whatever its hidden words (a pupil cannot hide or remove one).</param>
internal sealed record EffectiveWords(IReadOnlyList<UserWord> Words, IReadOnlyList<SpellingFix> Fixes, int Own, int Class, int Hidden,
                                     IReadOnlyList<string>? Exclus = null)
{
    public IReadOnlyList<string> ExclusOrEmpty => Exclus ?? Array.Empty<string>();
}

/// <summary>
/// Inheritance of the word lists (plan 10A, Q2), pure: a pupil's effective words = the active lists of « Classe » ∪ its
/// own active lists − its hidden words (<c>masques</c>); « Mes fautes »: the pupil's pair wins over the class's for the
/// same spelling. « Classe » itself (or a profile with no class) has its own words only. <see cref="Rebase"/> keeps a
/// pupil's effective list unchanged when an import moves it from the file's « Classe » to this PC's.
/// </summary>
internal static class WordLayering
{
    /// <summary>The forms of the active lists, in list order (duplicates possible across lists). Plan 10C: a names entry
    /// brings its variants too (« Ieper » with « Ypres »: recognised and offered).</summary>
    public static IEnumerable<string> ActiveForms(WordBook b) =>
        b.Lists.Where(l => l.Active).SelectMany(l => l.Words).SelectMany(e => e.Meta?.Variantes is { Count: > 0 } v ? v.Prepend(e.W) : new[] { e.W });

    /// <param name="cls">the class's book, or null (« Classe » itself, no class).</param>
    public static EffectiveWords Of(WordBook own, WordBook? cls)
    {
        var hidden = new HashSet<string>(own.Masques, StringComparer.Ordinal);
        var words = new List<UserWord>();
        int nClass = 0, nHidden = 0;
        if (cls != null)
            foreach (var w in ActiveForms(cls))
            {
                if (hidden.Contains(w)) { nHidden++; continue; }
                words.Add(new UserWord(w));
                nClass++;
            }
        int nOwn = 0;
        foreach (var w in ActiveForms(own)) { words.Add(new UserWord(w)); nOwn++; }
        // UserLexicon keeps the first pair of a spelling: the pupil's own first
        var fixes = own.Fautes.Select(p => new SpellingFix(p.Ecrit, p.Mot)).ToList();
        if (cls != null) fixes.AddRange(cls.Fautes.Select(p => new SpellingFix(p.Ecrit, p.Mot)));
        // « Classe »'s banned words: from the class book for a pupil, its own for « Classe » itself (a pupil's book has none)
        return new EffectiveWords(words, fixes, nOwn, nClass, nHidden, (cls ?? own).Exclus);
    }

    /// <summary>
    /// Where « Mots à ne jamais proposer » is shown (reviewer 2026-11-27): « Classe » only, and only behind the PIN — always
    /// in the Espace enseignant (<paramref name="teacher"/>), in Réglages only when Réglages asks for the PIN
    /// (<paramref name="settingsProtected"/>: management on and « Code requis pour modifier le profil » ticked). Otherwise a
    /// pupil could open Réglages of « Classe » and see or change the list.
    /// </summary>
    internal static bool ShowsExclus(bool isClass, bool teacher, bool settingsProtected) => isClass && (teacher || settingsProtected);

    /// <summary>The distinct effective forms (Ordinal), for comparisons.</summary>
    public static ISet<string> FormSet(WordBook own, WordBook? cls) => new HashSet<string>(Of(own, cls).Words.Select(w => w.Form), StringComparer.Ordinal);

    /// <summary>
    /// Import (plan 10A step 8), like <see cref="SettingsLayering.Rebase"/>: the words the pupil got from the file's
    /// « Classe » but that this PC's « Classe » lacks are copied into the pupil's « Mes mots » (src « import »), and its
    /// spelling pairs likewise into the pupil's « Mes fautes »; hidden words that the target « Classe » does not have
    /// are dropped. The effective list stays the same. <paramref name="fileClass"/> null (the file has no class words):
    /// nothing to rebase.
    /// </summary>
    public static WordBook Rebase(WordBook? fileClass, WordBook pupil, WordBook? targetClass, DateTime now)
    {
        if (fileClass is null) return pupil;
        var target = targetClass ?? WordBook.Empty;
        var targetForms = new HashSet<string>(ActiveForms(target), StringComparer.Ordinal);
        var own = new HashSet<string>(ActiveForms(pupil), StringComparer.Ordinal);
        var hidden = new HashSet<string>(pupil.Masques, StringComparer.Ordinal);
        // one pass (an import plan is recomputed at every checkbox): over the limit the remaining words are left out
        var copy = ActiveForms(fileClass).Distinct(StringComparer.Ordinal).Where(w => !hidden.Contains(w) && !targetForms.Contains(w) && !own.Contains(w));
        var r = pupil.AddWords(copy, WordEntry.Import, now, false);
        // a hidden word means nothing where the class does not offer it
        r = r with { Masques = r.Masques.Where(targetForms.Contains).ToList() };
        var pupilKeys = new HashSet<string>(r.Fautes.Select(p => PhoneticKey.StripLower(p.Ecrit)), StringComparer.Ordinal);
        var targetPairs = new HashSet<string>(target.Fautes.Select(p => PhoneticKey.StripLower(p.Ecrit) + "\u0001" + p.Mot), StringComparer.Ordinal);
        var pairs = r.Fautes.ToList();
        foreach (var p in fileClass.Fautes)
        {
            var k = PhoneticKey.StripLower(p.Ecrit);
            if (pairs.Count >= WordBook.MaxFautes || pupilKeys.Contains(k) || targetPairs.Contains(k + "\u0001" + p.Mot)) continue;
            pairs.Add(p);
            pupilKeys.Add(k);
        }
        return pairs.Count == r.Fautes.Count ? r : r with { Fautes = pairs };
    }
}
