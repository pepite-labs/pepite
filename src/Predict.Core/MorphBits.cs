namespace Predict.Core;

/// <summary>
/// Plan 11 (10D-0): the feature bits of one analysis of a lexicon form, our own layout (uint32), decoded at build time from
/// the Grammalecte French.lex tags by <c>Data.Build/build_grammar.py</c> (the two must stay in step: schema 1). The engine
/// reads bits only; it never parses a tag. Gender « both » = epicene, number « both » = invariable.
/// </summary>
public static class MorphBits
{
    // bits 0–3: the part of speech (a value, not a mask)
    public const uint PosMask = 0xF;
    public const uint Noun = 1, Adjective = 2, Determiner = 3, SubjectPronoun = 4, OtherPronoun = 5, FiniteVerb = 6,
                      PastParticiple = 7, Infinitive = 8, PresentParticiple = 9, Preposition = 10, Conjunction = 11,
                      Adverb = 12, Other = 15;
    // 4–5 gender, 6–7 number (nominal analyses: N, A, Det, pronouns, past participle)
    public const uint Masc = 1u << 4, Fem = 1u << 5, GenderMask = Masc | Fem;
    public const uint Sing = 1u << 6, Plur = 1u << 7, NumberMask = Sing | Plur;
    // 8–13 persons, 14–17 moods (finite verbs only)
    public const uint P1s = 1u << 8, P2s = 1u << 9, P3s = 1u << 10, P1p = 1u << 11, P2p = 1u << 12, P3p = 1u << 13,
                      PersonMask = 0x3Fu << 8;
    public const uint Indicative = 1u << 14, Subjunctive = 1u << 15, Imperative = 1u << 16, Conditional = 1u << 17;
    // 18–21 verb flags (every verbal analysis of the lemma)
    public const uint Etre = 1u << 18, Avoir = 1u << 19, AuxEtre = 1u << 20, Modal = 1u << 21;

    public static uint Pos(uint bits) => bits & PosMask;
    public static bool IsVerbal(uint bits) => Pos(bits) is FiniteVerb or PastParticiple or Infinitive or PresentParticiple;

    /// <summary>A short readable form for tests and reports (« V 3s I être »), never logged by the app.</summary>
    public static string Describe(uint bits)
    {
        var p = Pos(bits) switch
        {
            Noun => "N", Adjective => "A", Determiner => "Det", SubjectPronoun => "PronS", OtherPronoun => "Pron",
            FiniteVerb => "V", PastParticiple => "Q", Infinitive => "Y", PresentParticiple => "P", Preposition => "Prep",
            Conjunction => "Conj", Adverb => "Adv", _ => "Other",
        };
        var sb = new System.Text.StringBuilder(p);
        if ((bits & GenderMask) != 0) sb.Append(' ').Append((bits & GenderMask) == GenderMask ? "e" : (bits & Masc) != 0 ? "m" : "f");
        if ((bits & NumberMask) != 0) sb.Append(' ').Append((bits & NumberMask) == NumberMask ? "i" : (bits & Sing) != 0 ? "s" : "p");
        string[] persons = { "1s", "2s", "3s", "1p", "2p", "3p" };
        for (int k = 0; k < 6; k++) if ((bits & (1u << (8 + k))) != 0) sb.Append(' ').Append(persons[k]);
        if ((bits & Indicative) != 0) sb.Append(" I");
        if ((bits & Subjunctive) != 0) sb.Append(" S");
        if ((bits & Imperative) != 0) sb.Append(" E");
        if ((bits & Conditional) != 0) sb.Append(" K");
        if ((bits & Etre) != 0) sb.Append(" être");
        if ((bits & Avoir) != 0) sb.Append(" avoir");
        if ((bits & AuxEtre) != 0) sb.Append(" aux-être");
        if ((bits & Modal) != 0) sb.Append(" modal");
        return sb.ToString();
    }
}
