namespace Predict.Core;

/// <summary>The rules of <see cref="AgreementContext"/> that produced an <see cref="Expect"/> (tests, Eval).</summary>
[Flags]
public enum AgreementRule
{
    None = 0,
    /// <summary>S — a subject pronoun chain ends the context (« ils ne », « il nous », « je les »): verb person.</summary>
    Subject = 1,
    /// <summary>N — a determiner, or a determiner + adjective, ends the context: noun / adjective number and gender.</summary>
    Determiner = 2,
    /// <summary>NP — a noun after its determiner: a following adjective agrees, a following verb is 3s / 3p.</summary>
    NounGroup = 4,
    /// <summary>E — a finite form of « être » after its subject: participle / adjective agreement.</summary>
    Etre = 8,
    /// <summary>A — a finite form of « avoir » after its subject: a past participle.</summary>
    Avoir = 16,
    /// <summary>I — a preposition (à, de, pour, sans), a modal or « faut »: an infinitive.</summary>
    Infinitive = 32,
}

/// <summary>The morphology of a lowercase context token (its analyses' <see cref="MorphBits"/>; empty when unknown).</summary>
public delegate ReadOnlySpan<uint> MorphLookup(string lower);

/// <summary>
/// Plan 11 (10D-1): what the last three context tokens expect of the next word. Immutable; built by
/// <see cref="AgreementContext.From"/> once per context and cached by the engine. <see cref="Judge"/> says, for one analysis
/// of a candidate, whether it agrees (+1), disagrees or is disfavoured (−1), or is not concerned (0); <see cref="Term"/>
/// turns a candidate's analyses into the score term.
/// </summary>
public sealed class Expect
{
    public static readonly Expect None = new();

    public AgreementRule Rules { get; init; }
    public bool IsEmpty => Rules == AgreementRule.None;

    /// <summary>Nominal agreement: adjectives by gender and number; nouns by number only, and only when
    /// <see cref="NominalNouns"/> (after a determiner, not after a noun). A noun is never judged by gender (owner Q5).</summary>
    public bool Nominal { get; init; }
    public uint NomGender { get; init; } = MorphBits.GenderMask;
    public uint NomNumber { get; init; } = MorphBits.NumberMask;
    public bool NominalNouns { get; init; }

    /// <summary>Verb persons (finite verbs); 0 = no person constraint.</summary>
    public uint Persons { get; init; }
    /// <summary>The persons come from a noun subject (rule NP): weighted by <c>WNounSubj</c>.</summary>
    public bool PersonsFromNoun { get; init; }

    /// <summary>After « être »: past participles and adjectives agree with the subject (<see cref="PartGender"/>,
    /// <see cref="PartNumber"/>; an unknown gender or number is « both »).</summary>
    public bool Part { get; init; }
    public uint PartGender { get; init; } = MorphBits.GenderMask;
    public uint PartNumber { get; init; } = MorphBits.NumberMask;

    /// <summary>After « avoir »: a past participle, any gender / number (« il les a mangées » is correct).</summary>
    public bool PreferQ { get; init; }
    /// <summary>After a preposition or a modal: an infinitive; past participles are disfavoured.</summary>
    public bool PreferY { get; init; }

    /// <summary>Parts of speech disfavoured after a subject chain (a mask of <c>1 &lt;&lt; pos</c>).</summary>
    public uint DisfavourPos { get; init; }
    /// <summary>Finite 2p forms (« mangez ») are disfavoured (after être, avoir, a preposition or a modal).</summary>
    public bool Disfavour2p { get; init; }

    public const int FamilyNominal = 0, FamilyVerbal = 1, FamilyNounSubject = 2;

    /// <summary>One analysis: +1 agrees with the expectation, −1 disagrees (a constrained part of speech) or is
    /// disfavoured, 0 not concerned. <paramref name="family"/> names the weight that applies.</summary>
    public int Judge(uint b, out int family)
    {
        uint pos = MorphBits.Pos(b);
        family = FamilyVerbal;
        switch (pos)
        {
            case MorphBits.Noun:
                if (Nominal && NominalNouns) { family = FamilyNominal; return Overlaps(b & MorphBits.NumberMask, NomNumber) ? 1 : -1; }
                break;
            case MorphBits.Adjective:
                if (Nominal) { family = FamilyNominal; return Agrees(b, NomGender, NomNumber) ? 1 : -1; }
                if (Part) return Agrees(b, PartGender, PartNumber) ? 1 : -1;
                break;
            case MorphBits.PastParticiple:
                if (Part) return Agrees(b, PartGender, PartNumber) ? 1 : -1;
                if (PreferQ) return 1;
                if (PreferY) return -1;
                break;
            case MorphBits.Infinitive:
                if (PreferY) return 1;
                if (Part || PreferQ) return -1;
                break;
            case MorphBits.FiniteVerb:
                if (Persons != 0) { family = PersonsFromNoun ? FamilyNounSubject : FamilyVerbal; return (b & Persons) != 0 ? 1 : -1; }
                if (Disfavour2p && (b & MorphBits.P2p) != 0) return -1;
                break;
        }
        return (DisfavourPos & (1u << (int)pos)) != 0 ? -1 : 0;
    }

    /// <summary>
    /// The score term of a candidate with these analyses: + the weight of its agreeing analysis (the largest) when one
    /// agrees; − the (smallest) weight when none agrees and every analysis is constrained or disfavoured; else 0 (unknown
    /// word, another part of speech, nothing expected). Log10 scale, like the language model.
    /// </summary>
    public double Term(ReadOnlySpan<uint> analyses, double wNominal, double wVerbal, double wNounSubj, double bonus = 1.0)
    {
        if (analyses.Length == 0 || Rules == AgreementRule.None) return 0;
        double plus = 0, minus = double.MaxValue;
        bool agrees = false, allAgainst = true;
        foreach (var b in analyses)
        {
            int j = Judge(b, out int f);
            if (j == 0) { allAgainst = false; continue; }
            double w = f == FamilyNominal ? wNominal : f == FamilyVerbal ? wVerbal : wVerbal * wNounSubj;
            if (j > 0) { agrees = true; if (w > plus) plus = w; }
            else if (w < minus) minus = w;
        }
        if (agrees) return plus * bonus;
        return allAgainst && minus != double.MaxValue ? -minus : 0;
    }

    /// <summary>The weight of the candidate's agreeing analysis (the largest), 0 when none agrees: « does it agree? » for
    /// adding a form, whatever share of it the score gets as a bonus.</summary>
    public double AgreeWeight(ReadOnlySpan<uint> analyses, double wNominal, double wVerbal, double wNounSubj)
    {
        if (Rules == AgreementRule.None) return 0;
        double plus = 0;
        foreach (var b in analyses)
            if (Judge(b, out int f) > 0)
            {
                double w = f == FamilyNominal ? wNominal : f == FamilyVerbal ? wVerbal : wVerbal * wNounSubj;
                if (w > plus) plus = w;
            }
        return plus;
    }

    /// <summary>The sign of <see cref="Term"/> with every weight at 1 (tests).</summary>
    public int Sign(ReadOnlySpan<uint> analyses) => Math.Sign(Term(analyses, 1, 1, 1));

    private static bool Overlaps(uint have, uint want) => have == 0 || (have & want) != 0;

    private static bool Agrees(uint b, uint gender, uint number) =>
        Overlaps(b & MorphBits.GenderMask, gender) && Overlaps(b & MorphBits.NumberMask, number);

    /// <summary>A short readable form (tests and the Eval; never logged by the app).</summary>
    public override string ToString()
    {
        if (IsEmpty) return "none";
        var parts = new List<string> { Rules.ToString() };
        static string GN(uint g, uint n) =>
            (g == MorphBits.GenderMask ? "" : g == MorphBits.Masc ? "m" : "f") + (n == MorphBits.NumberMask ? "" : n == MorphBits.Sing ? "s" : "p");
        if (Nominal) parts.Add("nominal " + GN(NomGender, NomNumber) + (NominalNouns ? "" : " (adjectives)"));
        if (Persons != 0)
        {
            string[] names = { "1s", "2s", "3s", "1p", "2p", "3p" };
            var p = string.Join("|", Enumerable.Range(0, 6).Where(k => (Persons & (1u << (8 + k))) != 0).Select(k => names[k]));
            parts.Add("verb " + p + (PersonsFromNoun ? " (noun)" : ""));
        }
        if (Part) parts.Add("part " + GN(PartGender, PartNumber));
        if (PreferQ) parts.Add("Q");
        if (PreferY) parts.Add("Y");
        if (DisfavourPos != 0) parts.Add("disfavour");
        return string.Join("; ", parts);
    }
}

/// <summary>
/// Plan 11 (10D-1): the agreement rules over the last three context tokens — exactly what the engine's context gives
/// (lowercase <c>[a-zà-ÿœ]+</c> tokens, apostrophes and hyphens removed: « l'école » → « l école », « mange-t-il » →
/// « mange t il »). Pure: our own closed lists of French function words, plus the context nouns' and adjectives' feature
/// bits (<see cref="MorphLookup"/>). Rules, in this order (at most one verbal source): S subject chain, N determiner group,
/// NP noun group, E « être », A « avoir », I infinitive. See predict-core-design § 5e.
/// </summary>
public static class AgreementContext
{
    // ---- closed lists (our own)
    /// <summary>Object / reflexive clitics and « ne » that may stand between a subject pronoun and its verb.</summary>
    private static readonly HashSet<string> Clitics = new(StringComparer.Ordinal)
    { "ne", "n", "me", "m", "te", "t", "se", "s", "le", "la", "les", "l", "lui", "leur", "y", "en", "nous", "vous" };

    /// <summary>Subject pronouns → persons, and the gender / number of a participle after « être ».</summary>
    private static readonly Dictionary<string, (uint Persons, uint Gender, uint Number)> Subjects = new(StringComparer.Ordinal)
    {
        ["je"] = (MorphBits.P1s, MorphBits.GenderMask, MorphBits.Sing),
        ["j"] = (MorphBits.P1s, MorphBits.GenderMask, MorphBits.Sing),
        ["tu"] = (MorphBits.P2s, MorphBits.GenderMask, MorphBits.Sing),
        ["il"] = (MorphBits.P3s, MorphBits.Masc, MorphBits.Sing),
        ["elle"] = (MorphBits.P3s, MorphBits.Fem, MorphBits.Sing),
        ["on"] = (MorphBits.P3s, MorphBits.GenderMask, MorphBits.NumberMask),
        ["ce"] = (MorphBits.P3s | MorphBits.P3p, MorphBits.Masc, MorphBits.NumberMask),   // « ce sont »
        ["c"] = (MorphBits.P3s, MorphBits.Masc, MorphBits.NumberMask),
        ["nous"] = (MorphBits.P1p, MorphBits.GenderMask, MorphBits.Plur),
        ["vous"] = (MorphBits.P2p, MorphBits.GenderMask, MorphBits.NumberMask),   // « vous êtes » : one person or several
        ["ils"] = (MorphBits.P3p, MorphBits.Masc, MorphBits.Plur),
        ["elles"] = (MorphBits.P3p, MorphBits.Fem, MorphBits.Plur),
    };

    /// <summary>Elided subjects (« j' », « c' ») are never the inverted subject of a verb before them (« regarde, c'est »).</summary>
    private static bool Elided(string s) => s is "j" or "c";

    /// <summary>Determiners → gender, number.</summary>
    private static readonly Dictionary<string, (uint Gender, uint Number)> Determiners = BuildDeterminers();

    private static Dictionary<string, (uint, uint)> BuildDeterminers()
    {
        var d = new Dictionary<string, (uint, uint)>(StringComparer.Ordinal);
        foreach (var w in new[] { "les", "des", "ces", "mes", "tes", "ses", "nos", "vos", "leurs", "aux", "quelques", "plusieurs",
                                  "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf", "dix", "onze", "douze", "treize",
                                  "quatorze", "quinze", "seize", "vingt", "cent", "mille" })
            d[w] = (MorphBits.GenderMask, MorphBits.Plur);
        foreach (var w in new[] { "le", "un", "du", "au", "ce", "cet", "mon", "ton", "son" }) d[w] = (MorphBits.Masc, MorphBits.Sing);
        foreach (var w in new[] { "la", "une", "cette", "ma", "ta", "sa" }) d[w] = (MorphBits.Fem, MorphBits.Sing);
        foreach (var w in new[] { "l", "notre", "votre" }) d[w] = (MorphBits.GenderMask, MorphBits.Sing);
        return d;
    }

    /// <summary>Rule I: prepositions followed by an infinitive (« par » only when <c>par</c> is on: a sweep option).</summary>
    private static readonly HashSet<string> InfinitivePreps = new(StringComparer.Ordinal) { "à", "de", "d", "pour", "sans" };

    /// <summary>The parts of speech disfavoured right after a subject chain: preposition, conjunction, determiner, another
    /// subject pronoun (« il à », « il et », « ils on »).</summary>
    private const uint AfterSubject = (1u << (int)MorphBits.Preposition) | (1u << (int)MorphBits.Conjunction) |
                                      (1u << (int)MorphBits.Determiner) | (1u << (int)MorphBits.SubjectPronoun);

    /// <summary>10D-1 measurement (wide option): right after a subject chain no noun, adjective, past participle or infinitive
    /// either (« tu fait »: « fait » is also a noun, an adjective and a participle, so the plan's list left it neutral and
    /// « fais » could not pass it); right after a determiner, no finite verb (« les mange »). Our extension of the plan's
    /// lists, measured on and off (development/evaluation.md § Grammar 10D-1).</summary>
    private const uint AfterSubjectWide = (1u << (int)MorphBits.Noun) | (1u << (int)MorphBits.Adjective);
    /// <summary>Past participle and infinitive, disfavoured (wide option) only after a subject that is still in force
    /// (<see cref="BareSubject"/>): alone, + « ne », or + object clitics (« je les », owner 2026-11-16). Not when the chain
    /// may belong to an infinitive to come: after a word that can be a verb (« puis-je vous raconter »), after a verb
    /// (« il va nous voir »: no subject chain), « nous » / « vous » alone, or a clitic chain whose first word is unknown
    /// (« veux tu nous les donner »).</summary>
    private const uint AfterBareSubjectWide = (1u << (int)MorphBits.PastParticiple) | (1u << (int)MorphBits.Infinitive);
    private const uint AfterDeterminerWide = 1u << (int)MorphBits.FiniteVerb;

    public static bool IsDeterminer(string? w) => w is not null && Determiners.ContainsKey(w);

    /// <summary>The numerals of the determiner list: after a noun they are adjectives (« un vélo neuf », « page cent »).</summary>
    private static readonly HashSet<string> Numerals = new(StringComparer.Ordinal)
    { "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf", "dix", "onze", "douze", "treize", "quatorze", "quinze",
      "seize", "vingt", "cent", "mille" };

    /// <summary><c>t[i]</c> is a determiner here: in the list, and a numeral only when the word before it is not a noun.</summary>
    private static bool DetAt(string?[] t, int i, MorphLookup bits, out (uint Gender, uint Number) d)
    {
        d = default;
        if (i < 0 || t[i] is not { } w || !Determiners.TryGetValue(w, out d)) return false;
        return !(Numerals.Contains(w) && i > 0 && t[i - 1] is { } before && Has(bits(before), MorphBits.Noun, out _));
    }

    /// <summary>
    /// The expectation of the word after <paramref name="p3"/> <paramref name="p2"/> <paramref name="p1"/> (p1 the last
    /// token; null where the context is shorter). <paramref name="parInfinitive"/>: « par » counts as an infinitive
    /// preposition (sweep option); <paramref name="wide"/>: the wider disfavoured parts of speech (<c>AfterSubjectWide</c>,
    /// <c>AfterDeterminerWide</c>). <see cref="Expect.None"/> when no rule applies.
    /// </summary>
    public static Expect From(string? p3, string? p2, string? p1, MorphLookup bits, bool parInfinitive = false, bool wide = false)
    {
        if (string.IsNullOrEmpty(p1)) return Expect.None;
        var t = new[] { p3, p2, p1 };

        // ---- S: walk back from p1 over clitics and subject pronouns; the subject is the leftmost pronoun of the chain
        var subj = SubjectChain(t, 2, bits, out int chainStart);
        bool sApplies = subj is not null;

        // ---- N: a determiner at p1 (unless it is an object clitic of a subject chain: « je les »), or an adjective after one
        bool nApplies = false; uint nG = MorphBits.GenderMask, nN = MorphBits.NumberMask;
        if (DetAt(t, 2, bits, out var d1) && (!sApplies || p1 == "ce"))
        { nApplies = true; nG = d1.Gender; nN = d1.Number; }
        else if (!sApplies && p2 is not null && DetAt(t, 1, bits, out var d2) && !IsPronoun(p1) && Has(bits(p1), MorphBits.Adjective, out uint adj))
        {
            nApplies = true; nN = d2.Number;
            uint ag = adj & MorphBits.GenderMask;
            nG = ag != 0 && ag != MorphBits.GenderMask && (d2.Gender & ag) != 0 ? d2.Gender & ag : d2.Gender;
        }

        // ---- NP: a noun after its determiner (p2), or after determiner + adjective (p3, p2) — unless p1 is rather a finite
        // « être » / « avoir » after its subject (« le petit est »: « est » is also a noun, the East)
        var b1 = bits(p1);
        bool auxAfterSubject = (HasFinite(b1, MorphBits.Etre) || HasFinite(b1, MorphBits.Avoir)) && SubjectBefore(t, bits, out _, out _);
        bool npApplies = false; uint npG = MorphBits.GenderMask, npN = MorphBits.NumberMask;
        bool coordinated = false;
        if (!sApplies && !auxAfterSubject && !DetAt(t, 2, bits, out _) && !IsPronoun(p1) && NounGroup(t, 2, bits, out npG, out npN, out coordinated)) npApplies = true;

        if (sApplies)
        {
            var (persons, _, _) = Subjects[subj!];
            return new Expect
            {
                Rules = AgreementRule.Subject | (nApplies ? AgreementRule.Determiner : 0),
                Persons = persons,
                DisfavourPos = AfterSubject | (wide && !nApplies ? AfterSubjectWide | (BareSubject(t, chainStart, subj!, bits) ? AfterBareSubjectWide : 0) : 0),
                Nominal = nApplies, NomGender = nG, NomNumber = nN, NominalNouns = nApplies,
            };
        }
        if (nApplies || npApplies)
        {
            uint persons = 0;
            // « le chien et le chat » + son: a coordinated subject is plural whatever its last noun (no person then)
            if (npApplies && !coordinated)
            {
                if ((npN & MorphBits.Sing) != 0) persons |= MorphBits.P3s;
                if ((npN & MorphBits.Plur) != 0) persons |= MorphBits.P3p;
            }
            uint g = nApplies ? nG : npG, n = nApplies ? nN : npN;
            if (nApplies && npApplies) { uint both = nG & npG; g = both != 0 ? both : nG; }
            return new Expect
            {
                Rules = (nApplies ? AgreementRule.Determiner : 0) | (npApplies ? AgreementRule.NounGroup : 0),
                Nominal = true, NomGender = g, NomNumber = n, NominalNouns = nApplies,
                Persons = persons, PersonsFromNoun = true,
                DisfavourPos = wide && nApplies && !npApplies ? AfterDeterminerWide : 0,
            };
        }

        // ---- E: a finite form of « être » after its subject (« il a été » does not fire: « été » is a participle)
        if (HasFinite(b1, MorphBits.Etre) && SubjectBefore(t, bits, out uint sg, out uint sn))
            return new Expect { Rules = AgreementRule.Etre, Part = true, PartGender = sg, PartNumber = sn, Disfavour2p = true };
        // ---- A: a finite form of « avoir » after its subject
        if (HasFinite(b1, MorphBits.Avoir) && SubjectBefore(t, bits, out _, out _))
            return new Expect { Rules = AgreementRule.Avoir, PreferQ = true, Disfavour2p = true };
        // ---- I: a preposition, a modal, « faut »
        if (InfinitivePreps.Contains(p1) || parInfinitive && p1 == "par" || p1 == "faut" || HasFinite(b1, MorphBits.Modal))
            return new Expect { Rules = AgreementRule.Infinitive, PreferY = true, Disfavour2p = true };
        return Expect.None;
    }

    private static bool IsPronoun(string w) => Clitics.Contains(w) || Subjects.ContainsKey(w);

    /// <summary>The chain from <paramref name="start"/> to p1 begins with its subject pronoun, followed by nothing, « ne », or
    /// object / reflexive clitics (« je les », « je ne les », « il nous », « tu le »: owner 2026-11-16, the subject stays in
    /// force through them, so the infinitive and the participle are disfavoured as after a bare subject); and the word
    /// before it cannot be a finite verb (« puis je vous » + raconter). « nous » / « vous » count as the subject only with
    /// a clitic after them (« nous les »), never alone (« nous » + manger after a word outside the window may be an object).
    /// Never after a verb: « je vais les », « il faut les » have no subject in the chain (<see cref="SubjectChain"/>).</summary>
    private static bool BareSubject(string?[] t, int start, string subj, MorphLookup bits)
    {
        if (t[start] != subj) return false;
        bool clitic = false;
        for (int i = start + 1; i <= 2; i++)
        {
            if (t[i] is not { } w || !Clitics.Contains(w)) return false;
            if (w is not ("ne" or "n")) clitic = true;
        }
        if (subj is "nous" or "vous" && !clitic) return false;
        // a chain with clitics that fills the window: the word before the subject is unknown, and it may be a verb of an
        // inversion (« veux tu nous les donner », « voulez vous me le dire »): neutral
        if (clitic && start == 0) return false;
        if (start > 0 && t[start - 1] is { } before)
            foreach (var x in bits(before)) if (MorphBits.Pos(x) == MorphBits.FiniteVerb) return false;
        return true;
    }

    /// <summary>The subject pronoun of the chain ending at <c>t[end]</c> (the leftmost pronoun of a run of clitics and
    /// subject pronouns), or null: no pronoun, an inversion (« mange t il », « dit il »: the chain follows a finite verb or
    /// « t »), or a stressed pronoun after a preposition (« pour nous »). <paramref name="start"/> = the chain's first index.</summary>
    private static string? SubjectChain(string?[] t, int end, MorphLookup bits, out int start)
    {
        int k = end;
        string? subj = null;
        int subjAt = -1;
        while (k >= 0 && t[k] is { } w && IsPronoun(w))
        {
            if (Subjects.ContainsKey(w)) { subj = w; subjAt = k; }
            k--;
        }
        start = k + 1;
        if (subj is null) return null;
        // « t » before the subject inside the chain: the euphonic t of an inversion
        for (int i = start; i < subjAt; i++) if (t[i] == "t") return null;
        if (k >= 0 && t[k] is { } before && !Elided(subj))
        {
            var b = bits(before);
            if (IsFiniteVerbWord(b, Subjects[subj].Persons)) return null;   // inversion: « dit il », « fait elle »
            // « il va nous voir », « je peux vous aider »: « nous » / « vous » after a verb are its object, not a subject
            if (subj is "nous" or "vous" && IsFiniteVerbWord(b, MorphBits.PersonMask)) return null;
            // « le maître nous a », « celui qui nous a », « Léa vous dit »: after a noun, a name (unknown word) or « qui »,
            // « nous » / « vous » are objects too: no rule
            if (subj is "nous" or "vous" && (before == "qui" || b.Length == 0 || Has(b, MorphBits.Noun, out _) && !Has(b, MorphBits.Conjunction, out _) && !Has(b, MorphBits.Adverb, out _))) return null;
            if (Has(b, MorphBits.Preposition, out _)) return null;      // « pour nous », « avec elle »: not a subject
        }
        return subj;
    }

    /// <summary>A word read as the verb of an inversion before a subject of these persons: a finite analysis of one of them
    /// (« dit il », « sont ils »; « la cantine, ils » is no inversion: « cantine » as a verb is 1s / 2s / 3s) and no
    /// conjunction / adverb / preposition / pronoun / determiner analysis (« puis » is a conjunction too).</summary>
    private static bool IsFiniteVerbWord(ReadOnlySpan<uint> b, uint persons)
    {
        bool finite = false;
        foreach (var x in b)
        {
            uint p = MorphBits.Pos(x);
            if (p == MorphBits.FiniteVerb) { if ((x & persons) != 0) finite = true; }
            else if (p is MorphBits.Conjunction or MorphBits.Adverb or MorphBits.Preposition or MorphBits.OtherPronoun
                     or MorphBits.SubjectPronoun or MorphBits.Determiner) return false;
        }
        return finite;
    }

    /// <summary>NP at <c>t[at]</c>: a noun after its determiner (<c>t[at−1]</c>), or after determiner + adjective; the group's
    /// gender and number (the noun's ∩ the determiner's; the determiner's number when they disagree).</summary>
    private static bool NounGroup(string?[] t, int at, MorphLookup bits, out uint gender, out uint number, out bool coordinated)
    {
        gender = MorphBits.GenderMask; number = MorphBits.NumberMask; coordinated = false;
        if (at < 1 || t[at] is not { } w || t[at - 1] is not { } prev) return false;
        if (IsPronoun(w) || DetAt(t, at, bits, out _)) return false;
        if (!Has(bits(w), MorphBits.Noun, out uint nb)) return false;
        (uint G, uint N) det;
        int detAt;
        if (DetAt(t, at - 1, bits, out var d)) { det = d; detAt = at - 1; }
        else if (at >= 2 && DetAt(t, at - 2, bits, out var d2) && !IsPronoun(prev) && Has(bits(prev), MorphBits.Adjective, out _)) { det = d2; detAt = at - 2; }
        else return false;
        coordinated = detAt > 0 && t[detAt - 1] is "et" or "ou";
        uint ng = nb & MorphBits.GenderMask, nn = nb & MorphBits.NumberMask;
        if (ng == 0) ng = MorphBits.GenderMask;
        if (nn == 0) nn = MorphBits.NumberMask;
        gender = (ng & det.G) != 0 ? ng & det.G : ng;
        number = (nn & det.N) != 0 ? nn & det.N : det.N;
        return true;
    }

    /// <summary>The subject of the verb at p1 (rules E and A): a pronoun chain ending at p2, or a noun group ending at p2;
    /// its gender / number for a participle (« both » when unknown).</summary>
    private static bool SubjectBefore(string?[] t, MorphLookup bits, out uint gender, out uint number)
    {
        gender = MorphBits.GenderMask; number = MorphBits.NumberMask;
        if (t[1] is null) return false;
        var s = SubjectChain(t, 1, bits, out _);
        if (s is not null) { (_, gender, number) = Subjects[s]; return true; }
        if (!NounGroup(t, 1, bits, out gender, out number, out bool coordinated)) return false;
        if (coordinated) { gender = MorphBits.GenderMask; number = MorphBits.NumberMask; }   // « le chien et la chatte sont »
        return true;
    }

    /// <summary>Has an analysis of this part of speech; <paramref name="or"/> = the OR of those analyses.</summary>
    private static bool Has(ReadOnlySpan<uint> b, uint pos, out uint or)
    {
        or = 0;
        bool any = false;
        foreach (var x in b) if (MorphBits.Pos(x) == pos) { any = true; or |= x; }
        return any;
    }

    private static bool HasFinite(ReadOnlySpan<uint> b, uint flag)
    {
        foreach (var x in b) if (MorphBits.Pos(x) == MorphBits.FiniteVerb && (x & flag) != 0) return true;
        return false;
    }
}
