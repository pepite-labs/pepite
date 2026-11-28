using System;
using System.Collections.Generic;
using System.Text;

namespace Predict.Core;

/// <summary>
/// 0.11.0 (plan negation): one word of the clause before the caret, as the keyboard saw it typed.
/// </summary>
/// <param name="Word">the letters of the word as typed (case kept, no separator).</param>
/// <param name="SepAfter">the characters between this word and the next one (« ' » in « l'ai », « " " » most often); the
/// last word's separators (the tail after the negation word) are the keyboard's, not given here (empty).</param>
/// <param name="Exact">the word and the separators before it are exactly what the document holds (typed or inserted since
/// the last jump), not rebuilt from a read nor cut by an unknown start (R4: « visible »).</param>
public readonly record struct NegToken(string Word, string SepAfter, bool Exact);

/// <summary>Which subject rule (R3) made a fix: Eval reports and the log (never a word).</summary>
public enum NegationRule { Pronoun, NounGroup, Name, Imperative, NousVous }

/// <summary>
/// 0.11.0 (plan negation): the corrected group for a negation typed without « ne ». The tokens
/// <see cref="From"/>..last of the clause (their exact text: <see cref="Original"/>, the separators between them included,
/// not the tail after the negation word) are replaced by <see cref="Replacement"/> (straight apostrophes: the keyboard
/// writes the document's).
/// </summary>
/// <param name="VerbFixed">the verb was ≈ and its spelling is fixed in the group (« veu » → veut).</param>
/// <param name="NegFixed">the negation word was ≈ (« pa » → pas, « jamé » → jamais).</param>
public sealed record NegationFix(int From, string Original, string Replacement, NegationRule Rule, string Negation,
                                 bool VerbFixed, bool NegFixed);

/// <summary>What the detector needs from the engine (pure otherwise; <see cref="PredictionEngine"/> implements it).</summary>
public interface INegationLexicon
{
    /// <summary>The morphology of a lowercase lexicon form (empty when unknown).</summary>
    ReadOnlySpan<uint> Bits(string lower);
    /// <summary>The spelling check of a word as typed (<see cref="PredictionEngine.Check"/>).</summary>
    SpellingCheck Check(string word);
    /// <summary>A capitalised name the pupil may use as a subject (a pack name, a personal word).</summary>
    bool IsName(string word);
    /// <summary>The language-model score (log10) of a token chain at a clause start (owner Q6: nous / vous).</summary>
    double ChainScore(string[] lowerTokens);
    /// <summary>D35: the corrected group holds an excluded word (never offered).</summary>
    bool Excluded(string text);
}

/// <summary>
/// 0.11.0 (plan negation, owner answers 2026-11-17): the missing « ne » of a simple negation (« mon cousin veu pa » → « ne
/// veut pas », « c'est pas » → « ce n'est pas », « j'aime plus. » → « je n'aime plus. »). Pure rules over the clause just typed:
/// <list type="bullet">
/// <item>R0 — the negation word ends the clause: pas, jamais, rien, personne, guère (or ≈ to one: « pa », « jamé »); « plus »
/// only before . ! ? … or a line end (owner 2026-11-27) (the caller says, <c>plusAllowed</c>); never next to an apostrophe or a hyphen.</item>
/// <item>R1 — right before it, a finite verb agreeing with the subject, or a ≈ word one of whose candidates is (a candidate
/// with a space or an apostrophe is skipped: glued words). A red word, an inversion hyphen: no fix.</item>
/// <item>R2 — at most three object clitics between the subject and the verb (<see cref="AgreementContext.IsObjectClitic"/>).</item>
/// <item>R3 — the subject: a subject pronoun (<see cref="AgreementContext.TryPersons"/>), a noun after its determiner
/// (or determiner + adjective), a capitalised name; at a known clause start an imperative (« ne » first). « nous » / « vous »
/// at a clause start with a verb that may be an imperative: the language model decides « ne vous V » / « vous ne V » with a
/// margin of <see cref="NousVousMargin"/>, else nothing (owner Q6).</item>
/// <item>R4 — no « ne / n' » already there; every token from the subject (its determiner) to the negation word exact.</item>
/// <item>R5 — « ne » inserted (« n' » before a vowel or a mute h), « j' » / « c' » written « je » / « ce », the ≈ verb and
/// negation word written right.</item>
/// </list>
/// « jamais », « rien », « personne » after « que », « si », « sans », a comparative or a superlative in the clause (« le plus
/// beau qu'il ait jamais vu ») are « ever / anything / anyone »: no fix. Out of scope (plan): an adverb in between, « y a pas »,
/// « faut pas » without « il », infinitives, « ne … que », glued subjects.
/// </summary>
public static class Negation
{
    /// <summary>The negation words (R0, owner Q2); « plus » is apart (punctuation only).</summary>
    private static readonly string[] Words = { "pas", "jamais", "rien", "personne", "guère" };

    /// <summary>Owner Q6: the language-model margin (log10) for « nous / vous » at a clause start.</summary>
    public static double NousVousMargin = 1.0;

    /// <summary>Most words of a clause the keyboard gives (plan: ≤ 8 since the last mark, a few more for the guards).</summary>
    public const int MaxTokens = 12;

    /// <summary>Words that open a clause before « nous / vous » (« mais vous savez pas »).</summary>
    private static readonly HashSet<string> Openers = new(StringComparer.Ordinal)
    { "et", "mais", "ou", "où", "donc", "alors", "puis", "car", "quand", "si", "que", "qu", "comme", "lorsque", "lorsqu", "puisque", "puisqu", "parce" };

    /// <summary>Before the subject, these make « jamais / rien / personne » mean « ever / anything / anyone ».</summary>
    private static readonly HashSet<string> EverGuards = new(StringComparer.Ordinal)
    {
        "si", "sans", "avant", "plus", "moins", "meilleur", "meilleure", "meilleurs", "meilleures", "pire", "pires",
        "seul", "seule", "seuls", "seules", "premier", "première", "premiers", "premières", "dernier", "dernière", "derniers",
        "dernières", "aucun", "aucune", "ni", "jamais", "rien", "personne", "guère", "peu", "trop",
        // an earlier negation in the clause (« il ne dit pas qu'il a rien vu »)
        "n", "ne", "pas",
    };

    /// <summary>The bare elided forms that may end a token before an apostrophe inside a group.</summary>
    private static bool ElidedBefore(string lower) => lower is "j" or "l" or "m" or "t" or "s" or "c" or "n" or "qu";

    /// <summary>
    /// The corrected group, or null. <paramref name="clause"/>: the words since the last clause mark (<c>. ! ? … ; : ,</c>,
    /// a line break, an unknown point), the negation word last; <paramref name="startKnown"/>: the first of them opens the
    /// clause (a mark or the text start was seen); <paramref name="plusAllowed"/>: the negation word is followed by
    /// punctuation or a line end (« plus »).
    /// </summary>
    public static NegationFix? Find(IReadOnlyList<NegToken> clause, bool startKnown, bool plusAllowed, INegationLexicon lex)
    {
        int n = clause.Count;
        if (n < 2 || n > MaxTokens) return null;
        var negTok = clause[n - 1];
        if (!negTok.Exact || AllCaps(negTok.Word)) return null;
        // R0: the negation word, typed or ≈
        string negLower = negTok.Word.ToLowerInvariant();
        string? neg = NegWord(negLower, plusAllowed);
        bool negFixed = false;
        if (neg is null)
        {
            var c = lex.Check(negTok.Word);
            if (c.Status != SpellingStatus.SoundsLike) return null;
            foreach (var cand in c.Candidates)
                if (NegWord(cand, plusAllowed) is { } w) { neg = w; negFixed = true; break; }
            if (neg is null) return null;
        }
        // R1: the verb right before it, plain spaces between (no « a-t-il pas », « jusqu'à »)
        int v = n - 2;
        var vt = clause[v];
        if (!vt.Exact || !Spaces(vt.SepAfter) || AllCaps(vt.Word)) return null;
        string vLower = vt.Word.ToLowerInvariant();
        if (vLower is "ne" or "n") return null;
        var verbForms = VerbForms(vt.Word, vLower, lex, out bool verbKnown);
        if (verbForms.Count == 0) return null;
        // « la pauvre demeure pas »: after determiner + adjective, a noun / verb homograph is the noun of that group
        // (« le bus est pas »: être and avoir are the verbs there)
        if (v >= 2 && !IsAuxiliary(vLower, lex) && IsNoun(vt.Word, lex, out _) && IsAdjective(clause[v - 1].Word, lex) && Det(clause, v - 2, out _)) return null;

        // the run of pronouns before the verb (subject pronouns and clitics)
        int r0 = v;
        while (r0 > 0 && IsPronounLike(clause[r0 - 1].Word.ToLowerInvariant()) && !SiElided(clause, r0 - 1) && !EuphonicL(clause, r0 - 1))
        {
            r0--;
            if (clause[r0].Word.ToLowerInvariant() is "qui" or "ça" or "cela") break;   // « ce qui », « tout ça »: the subject itself
        }
        for (int i = r0; i < v; i++)
        {
            var t = clause[i];
            var tl = t.Word.ToLowerInvariant();
            if (tl is "ne" or "n") return null;                                   // R4: « ne » is there
            if (!t.Exact || AllCaps(t.Word)) return null;
            if (!GroupSep(t.SepAfter, tl)) return null;                          // a hyphen: « dit-il », « est-ce »
        }

        NegationRule rule;
        uint persons;
        int subjectAt;   // the subject pronoun's index (Pronoun / NousVous subject-first), else −1
        int insertAt;    // the token « ne » goes before
        int from;        // the first replaced token
        int objectsFrom; // the first object clitic of the run
        string? verb = null;
        string first = r0 < v ? clause[r0].Word.ToLowerInvariant() : "";
        if (r0 < v && AgreementContext.TryPersons(first, out uint pp) && !(first is "nous" or "vous" && AgreesNoun(clause, r0, lex, verbForms)))
        {
            if (r0 > 0 && clause[r0 - 1].SepAfter.IndexOf('-') >= 0) return null;   // « est-ce », « as-tu »
            // « qui » takes its antecedent's person: the third, unless « moi / toi / nous / vous qui »
            if (first == "qui" && !(r0 > 0 && clause[r0 - 1].Word.ToLowerInvariant() is "moi" or "toi" or "nous" or "vous")) pp = MorphBits.P3s | MorphBits.P3p;
            persons = pp;
            rule = NegationRule.Pronoun;
            subjectAt = r0; insertAt = r0 + 1; from = r0; objectsFrom = r0 + 1;
            if (first is "nous" or "vous")
            {
                // a clause start (or an opener: « mais vous savez pas »), else « nous / vous » may be an object: nothing
                bool atStart = r0 == 0 && startKnown;
                bool afterOpener = r0 > 0 && clause[r0 - 1].Exact && Openers.Contains(clause[r0 - 1].Word.ToLowerInvariant());
                if (!atStart && !afterOpener) return null;
                if (!Agreeing(verbForms, persons, lex, imperative: false, out var vf)) return null;
                verb = vf;
                // « vous » (and object clitics) before a verb that may be an imperative (« vous inquiétez pas », « vous en
                // faites pas »): the language model decides « ne vous (en) V » / « vous ne (n') (en) V » with a margin, else
                // nothing (owner Q6). The n-gram tables write « n'en » as « n en »; after an opener, it comes first (« où
                // nous n'entrons » / « où ne nous entrons »).
                bool cliticsOnly = true;
                for (int i = r0 + 1; i < v; i++) if (!AgreementContext.IsObjectClitic(clause[i].Word.ToLowerInvariant())) cliticsOnly = false;
                if (cliticsOnly && v - r0 <= 3 && HasImperative(lex.Bits(vf), persons))
                {
                    var mid = new List<string>(3);
                    for (int i = r0 + 1; i < v; i++) mid.Add(Fold(clause[i].Word));
                    mid.Add(Fold(vf));
                    var neFirst = new List<string>(7);
                    var subjFirst = new List<string>(7);
                    if (afterOpener) { neFirst.Add(Fold(clause[r0 - 1].Word)); subjFirst.Add(Fold(clause[r0 - 1].Word)); }
                    neFirst.Add("ne"); neFirst.Add(first); neFirst.AddRange(mid); neFirst.Add(Fold(neg));
                    subjFirst.Add(first); subjFirst.Add(StartsWithVowel(mid[0]) ? "n" : "ne"); subjFirst.AddRange(mid); subjFirst.Add(Fold(neg));
                    double d = lex.ChainScore(subjFirst.ToArray()) - lex.ChainScore(neFirst.ToArray());
                    if (Math.Abs(d) < NousVousMargin) return null;
                    rule = NegationRule.NousVous;
                    if (d < 0) { subjectAt = -1; insertAt = r0; objectsFrom = r0; }
                }
            }
        }
        else
        {
            // no subject pronoun first: the run is object clitics only, the subject is before it (or an imperative)
            subjectAt = -1; objectsFrom = r0;
            if (r0 > 0)
            {
                if (!SubjectBefore(clause, r0 - 1, lex, out persons, out rule)) return null;
                insertAt = r0; from = r0;
            }
            else
            {
                // an imperative only as typed: a ≈ word read as an imperative is too often a verb whose subject is before the
                // clause mark (« …, veu pa » is « ne veut pas » there)
                if (!startKnown || !verbKnown) return null;
                rule = NegationRule.Imperative;
                persons = MorphBits.P2s | MorphBits.P1p | MorphBits.P2p;
                insertAt = 0; from = 0;
            }
        }
        // R2: object clitics only after the subject, at most three
        int objects = 0;
        for (int i = objectsFrom; i < v; i++)
        {
            var tl = clause[i].Word.ToLowerInvariant();
            if (!AgreementContext.IsObjectClitic(tl)) return null;
            if (++objects > 3) return null;
        }
        // « jamais / rien / personne » = ever / anything / anyone after « que », a superlative…
        if (neg is not ("pas" or "plus" or "guère"))
            for (int i = 0; i < from; i++)
            {
                if (EverGuards.Contains(clause[i].Word.ToLowerInvariant())) return null;
                // « est-ce qu'il peut rien comprendre ? »: a question, « rien » = anything
                if (i > 0 && clause[i].Word.ToLowerInvariant() == "ce" && clause[i - 1].SepAfter.IndexOf('-') >= 0) return null;
            }
        if (verb is null && !Agreeing(verbForms, persons, lex, rule == NegationRule.Imperative, out verb)) return null;
        return Build(clause, from, insertAt, subjectAt, v, verb, neg, rule, negFixed, verbKnown, lex);
    }

    /// <summary>
    /// The clause before the caret as <see cref="Find"/> reads it, from the text typed (<paramref name="text"/> ends with the
    /// last word and its <paramref name="tail"/>: one separator, or two such as « . » + space, « ␣! »). Words are
    /// <see cref="SpellingIndicator.IsWordChar"/> runs; the separators allowed between two words of a clause are spaces,
    /// apostrophes and hyphens (<see cref="Find"/> judges them); any other character (<c>. ! ? … ; : ,</c>, a line break, a
    /// quote, a digit) ends the clause. <paramref name="exactFrom"/>: the text before this index is not exact (rebuilt from a
    /// read, or after an unknown start): a word that starts before it is not exact. <paramref name="textStartKnown"/>: the
    /// text's first character opens a sentence. <paramref name="startKnown"/>: the first word returned opens the clause.
    /// <paramref name="plusAllowed"/>: the tail holds a sentence end (. ! ? …) or a line end (« plus », R0). False when the text does not
    /// end with a word and a tail of one or two separators, or the tail is a lone « . » / « : » (an address may follow:
    /// <see cref="SpellingIndicator.Pending"/>).
    /// </summary>
    public static bool ClauseOf(string text, int exactFrom, bool textStartKnown, out List<NegToken> tokens, out bool startKnown,
                                out string tail, out bool plusAllowed)
    {
        tokens = new List<NegToken>(8);
        startKnown = false; tail = ""; plusAllowed = false;
        if (string.IsNullOrEmpty(text)) return false;
        int i = text.Length;
        while (i > 0 && !SpellingIndicator.IsWordChar(text[i - 1])) i--;
        int tailLen = text.Length - i;
        if (i == 0 || tailLen is < 1 or > 2) return false;
        tail = text[i..];
        foreach (var c in tail) if (char.IsDigit(c)) return false;
        if (tailLen == 1 && tail[0] is '.' or ':') return false;
        if (tailLen == 2 && IsLineBreak(tail[0]) && !(tail[0] == '\r' && tail[1] == '\n')) return false;
        // owner 2026-11-27: « plus » is negative only before a sentence end or a line end (« j'en veux plus, merci »: « more »)
        foreach (var c in tail) if (c is '.' or '!' or '?' or '…' || IsLineBreak(c)) plusAllowed = true;
        var rev = new List<NegToken>(8);
        int end = i;   // one past the current word's last letter
        string sepAfter = "";
        while (true)
        {
            int s = end;
            while (s > 0 && SpellingIndicator.IsWordChar(text[s - 1])) s--;
            // a word at the very start of the text is whole only when that start is known (else it may be a fragment)
            rev.Add(new NegToken(text[s..end], sepAfter, s >= exactFrom && (s > 0 || textStartKnown)));
            if (s == 0) { startKnown = textStartKnown; break; }
            // the separators before this word
            int p = s;
            while (p > 0 && !SpellingIndicator.IsWordChar(text[p - 1])) p--;
            var sep = text[p..s];
            bool inClause = true, digit = false;
            foreach (var c in sep)
            {
                if (char.IsDigit(c)) digit = true;
                if (!(c is ' ' or ' ' or ' ' or '-' || Elision.IsApostrophe(c))) inClause = false;
            }
            if (!inClause) { startKnown = !digit; break; }   // a mark opens the clause; a digit says nothing
            if (p == 0) { startKnown = textStartKnown; break; }   // separators at the very start of the text
            if (rev.Count >= MaxTokens) { startKnown = false; break; }
            sepAfter = sep;
            end = p;
        }
        rev.Reverse();
        tokens = rev;
        return true;
    }

    private static bool IsLineBreak(char c) => c is '\n' or '\r';

    /// <summary>The negation word <paramref name="lower"/> is (« plus » only when allowed), else null.</summary>
    private static string? NegWord(string lower, bool plusAllowed)
    {
        if (plusAllowed && lower == "plus") return "plus";
        foreach (var w in Words) if (lower == w) return w;
        return null;
    }

    /// <summary>The verb's candidate forms (lowercase): the typed word when the lexicon knows it, else the ≈ candidates that
    /// are single plain words (no space, no apostrophe: glued words follow-up), in the check's order; a red word gives none.</summary>
    private static List<string> VerbForms(string typed, string lower, INegationLexicon lex, out bool known)
    {
        var o = new List<string>(4);
        known = false;
        if (lex.Bits(lower).Length > 0 && lex.Check(typed).Status == SpellingStatus.Known) { known = true; o.Add(lower); return o; }
        var c = lex.Check(typed);
        if (c.Status != SpellingStatus.SoundsLike) return o;
        foreach (var cand in c.Candidates)
        {
            if (cand.IndexOf(' ') >= 0 || cand.IndexOf('\'') >= 0 || cand.IndexOf('’') >= 0 || cand.IndexOf('-') >= 0) continue;
            bool upper = false;
            foreach (var ch in cand) if (char.IsUpper(ch)) { upper = true; break; }
            if (!upper) o.Add(cand);
        }
        return o;
    }

    /// <summary>The first form with a finite analysis of these persons (an imperative one when asked).</summary>
    private static bool Agreeing(List<string> forms, uint persons, INegationLexicon lex, bool imperative, out string verb)
    {
        foreach (var f in forms)
            foreach (var b in lex.Bits(f))
                if (MorphBits.Pos(b) == MorphBits.FiniteVerb && (b & persons) != 0 && (!imperative || (b & MorphBits.Imperative) != 0))
                { verb = f; return true; }
        verb = "";
        return false;
    }

    private static bool HasImperative(ReadOnlySpan<uint> bits, uint persons)
    {
        foreach (var b in bits)
            if (MorphBits.Pos(b) == MorphBits.FiniteVerb && (b & MorphBits.Imperative) != 0 && (b & persons) != 0) return true;
        return false;
    }

    /// <summary>« nous / vous » first in the run, but a noun or a name before them whose person the verb agrees with: they
    /// are objects (« Paul vous aime pas »).</summary>
    private static bool AgreesNoun(IReadOnlyList<NegToken> clause, int r0, INegationLexicon lex, List<string> verbForms) =>
        r0 > 0 && SubjectBefore(clause, r0 - 1, lex, out uint persons, out _) && Agreeing(verbForms, persons, lex, false, out _);

    /// <summary>R3 for a subject that is not a pronoun, ending at <paramref name="s"/>: a capitalised name (a pack or
    /// personal name, or a capitalised word the lexicon does not know: « Rusty », « M. Keefer »), or a noun group — a noun
    /// after its determiner, with an adjective before or after it (« le petit chien », « la situation économique »), or a
    /// complement after « de » (« le phare de remplacement », « le prix des médicaments »: both nouns' numbers count). The
    /// group's words exact and spaced plainly (« l'élève »: the apostrophe of « l' »).</summary>
    private static bool SubjectBefore(IReadOnlyList<NegToken> clause, int s, INegationLexicon lex, out uint persons, out NegationRule rule)
    {
        persons = 0; rule = NegationRule.NounGroup;
        var t = clause[s];
        if (!t.Exact || !Spaces(t.SepAfter) || AllCaps(t.Word)) return false;
        var w = t.Word.ToLowerInvariant();
        if (IsPronounLike(w) || AgreementContext.IsDeterminer(w)) return false;
        if (char.IsUpper(t.Word[0]) && (lex.IsName(t.Word) || lex.Bits(w).Length == 0 && lex.Check(t.Word).Status != SpellingStatus.Known))
        {
            persons = MorphBits.P3s; rule = NegationRule.Name;
            return true;
        }
        int start;
        if (Group(clause, s, lex, out uint number, out start)) { }
        else if (IsNoun(clause[s].Word, lex, out _) && s >= 2 && IsDe(clause[s - 1]) && Group(clause, s - 2, lex, out number, out start)) { }
        else return false;
        // the group is a complement itself (« des médicaments », « de mon frère »): the noun it completes counts too
        int head = clause[start].Word.ToLowerInvariant() is "du" or "des" ? start - 1 : start >= 1 && IsDe(clause[start - 1]) ? start - 2 : -1;
        if (head >= 0 && Group(clause, head, lex, out uint headNumber, out int headStart)) { number |= headNumber; start = headStart; }
        if ((number & MorphBits.Sing) != 0) persons |= MorphBits.P3s;
        if ((number & MorphBits.Plur) != 0) persons |= MorphBits.P3p;
        return persons != 0;
    }

    /// <summary>A noun group ending at <paramref name="s"/>: [determiner] [adjective] noun [adjective], the determiner
    /// required; its number (the noun's ∩ the determiner's, else the determiner's) and its first index.</summary>
    private static bool Group(IReadOnlyList<NegToken> clause, int s, INegationLexicon lex, out uint number, out int start)
    {
        number = 0; start = s;
        if (s < 1) return false;
        if (!Plain(clause[s], lex)) return false;
        if (IsNoun(clause[s].Word, lex, out uint nounNumber) && GroupAt(clause, s, nounNumber, lex, out number, out start)) return true;
        // a noun and the adjective (or participle) after it (« la situation économique », « une fourmi isolée »)
        if (s < 2 || !IsAdjective(clause[s].Word, lex, participle: true)) return false;
        int nAt = s - 1;
        if (!Plain(clause[nAt], lex) || !Spaces(clause[nAt].SepAfter) || !IsNoun(clause[nAt].Word, lex, out nounNumber)) return false;
        return GroupAt(clause, nAt, nounNumber, lex, out number, out start);
    }

    /// <summary>The determiner (and the adjective before the noun) of the noun at <paramref name="nAt"/>.</summary>
    private static bool GroupAt(IReadOnlyList<NegToken> clause, int nAt, uint nounNumber, INegationLexicon lex, out uint number, out int start)
    {
        number = 0; start = nAt;
        int d = nAt - 1;
        uint detNumber;
        if (d >= 0 && Det(clause, d, out detNumber)) { }
        else if (d >= 1 && Det(clause, d - 1, out detNumber) && Plain(clause[d], lex) && Spaces(clause[d].SepAfter) && IsAdjective(clause[d].Word, lex)) d--;
        else return false;
        number = nounNumber == 0 ? detNumber : (nounNumber & detNumber) != 0 ? nounNumber & detNumber : detNumber;
        start = d;
        return true;
    }

    /// <summary>An exact, known word that is no pronoun nor determiner.</summary>
    private static bool Plain(NegToken t, INegationLexicon lex)
    {
        var w = t.Word.ToLowerInvariant();
        return t.Exact && !AllCaps(t.Word) && !IsPronounLike(w) && !AgreementContext.IsDeterminer(w) && lex.Check(t.Word).Status == SpellingStatus.Known;
    }

    private static bool IsDe(NegToken t) => t.Exact && t.Word.ToLowerInvariant() is "de" or "d" && (Spaces(t.SepAfter) || IsApostrophes(t.SepAfter));

    private static bool IsNoun(string word, INegationLexicon lex, out uint number)
    {
        number = 0;
        bool noun = false;
        foreach (var b in lex.Bits(word.ToLowerInvariant())) if (MorphBits.Pos(b) == MorphBits.Noun) { noun = true; number |= b & MorphBits.NumberMask; }
        return noun;
    }

    private static bool Det(IReadOnlyList<NegToken> clause, int i, out uint number)
    {
        number = 0;
        var t = clause[i];
        var w = t.Word.ToLowerInvariant();
        if (w == "leur") number = MorphBits.Sing;   // « leur crâne »: a clitic elsewhere
        else if (!t.Exact || !AgreementContext.TryDeterminer(w, out number)) return false;
        if (!t.Exact) return false;
        return w == "l" ? IsApostrophes(t.SepAfter) : Spaces(t.SepAfter);
    }

    private static bool IsAuxiliary(string lower, INegationLexicon lex)
    {
        foreach (var b in lex.Bits(lower))
            if (MorphBits.Pos(b) == MorphBits.FiniteVerb && (b & (MorphBits.Etre | MorphBits.Avoir)) != 0) return true;
        return false;
    }

    private static bool IsAdjective(string word, INegationLexicon lex, bool participle = false)
    {
        foreach (var b in lex.Bits(word.ToLowerInvariant()))
            if (MorphBits.Pos(b) == MorphBits.Adjective || participle && MorphBits.Pos(b) == MorphBits.PastParticiple) return true;
        return false;
    }

    /// <summary>« l' » before « on » is euphonic (« et l'on est pas »), not an object.</summary>
    private static bool EuphonicL(IReadOnlyList<NegToken> clause, int i) =>
        clause[i].Word is "l" or "L" && IsApostrophes(clause[i].SepAfter) && i + 1 < clause.Count && clause[i + 1].Word.ToLowerInvariant() == "on";

    /// <summary>« s' » before « il / ils » is « si » (« s'il était pas »), not the clitic « se ».</summary>
    private static bool SiElided(IReadOnlyList<NegToken> clause, int i) =>
        clause[i].Word is "s" or "S" && IsApostrophes(clause[i].SepAfter) && i + 1 < clause.Count && clause[i + 1].Word.ToLowerInvariant() is "il" or "ils";

    /// <summary>A subject pronoun or a clitic (« ne » included: R4 finds it), lowercase.</summary>
    private static bool IsPronounLike(string lower) =>
        lower is "ne" or "n" || AgreementContext.IsObjectClitic(lower) || AgreementContext.TryPersons(lower, out _);

    /// <summary>The corrected group (R5).</summary>
    private static NegationFix? Build(IReadOnlyList<NegToken> clause, int from, int insertAt, int subjectAt, int v, string verb,
                                      string neg, NegationRule rule, bool negFixed, bool verbKnown, INegationLexicon lex)
    {
        int n = clause.Count;
        for (int i = from; i < n; i++) if (!clause[i].Exact) return null;   // R4: every token visible
        var orig = new StringBuilder();
        for (int i = from; i < n; i++) { orig.Append(clause[i].Word); if (i < n - 1) orig.Append(clause[i].SepAfter); }
        var o = new StringBuilder();
        bool capital = char.IsUpper(clause[from].Word[0]);
        for (int i = from; i < n; i++)
        {
            string w = i == v && !verbKnown ? verb : i == n - 1 ? (negFixed ? neg : clause[i].Word) : clause[i].Word;
            string sep = i < n - 1 ? clause[i].SepAfter : "";
            if (i == insertAt)
            {
                bool elide = StartsWithVowel(w);
                string ne = elide ? "n'" : "ne ";
                if (i == from && capital) { ne = "N" + ne[1..]; w = LowerFirst(w); }
                o.Append(ne);
            }
            if (i == subjectAt && Elision.IsApostrophe(sep.Length == 1 ? sep[0] : '\0'))
            {
                // « j' » / « c' » before « ne »: their full form (« je ne », « ce n'est »)
                var lw = w.ToLowerInvariant();
                if (lw is "j" or "c") { w = w + "e"; sep = " "; }
                else return null;
            }
            o.Append(w).Append(sep);
        }
        var rep = o.ToString();
        if (lex.Excluded(rep)) return null;   // D35: never offered
        return new NegationFix(from, orig.ToString(), rep, rule, neg, !verbKnown, negFixed);
    }

    /// <summary>The word starts with a vowel or a mute h: « n' » (« n'a », « n'y », « n'habite »).</summary>
    private static bool StartsWithVowel(string w)
    {
        if (w.Length == 0) return false;
        char c = char.ToLowerInvariant(w[0]);
        if ("aeiouyàâäéèêëîïôöùûüœ".IndexOf(c) >= 0) return true;
        return c == 'h' && !Elision.HasAspiratedH(w);
    }

    private static string LowerFirst(string w) => w.Length == 0 || AllCaps(w) ? w : char.ToLowerInvariant(w[0]) + w[1..];

    private static string Fold(string w) => TokenFold.Fold(w.ToLowerInvariant());

    private static bool AllCaps(string w)
    {
        if (w.Length < 2) return false;
        foreach (var c in w) if (char.IsLetter(c) && !char.IsUpper(c)) return false;
        return true;
    }

    /// <summary>Plain spaces only (one or more; a no-break space too).</summary>
    private static bool Spaces(string sep)
    {
        if (sep.Length == 0) return false;
        foreach (var c in sep) if (c is not (' ' or ' ' or ' ')) return false;
        return true;
    }

    private static bool IsApostrophes(string sep) => sep.Length == 1 && Elision.IsApostrophe(sep[0]);

    /// <summary>The separator after a token of the pronoun run: spaces, or one apostrophe after an elided form (« l'ai »).</summary>
    private static bool GroupSep(string sep, string lower) => Spaces(sep) || IsApostrophes(sep) && ElidedBefore(lower);
}
