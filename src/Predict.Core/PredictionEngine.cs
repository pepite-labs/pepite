using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Predict.Core;

public enum MatchKind { Prefix, Phonetic, Edit }

/// <param name="Name">a personal word stored with a capital (plan 10 Q13: a proper name, « Pierre »): kept as it is,
/// never shown with the picture of its lowercase homograph.</param>
public readonly record struct Suggestion(string Word, double Score, MatchKind Kind, bool Name = false);

/// <summary>
/// Open French predictor. Candidate generation: literal-prefix completion + phonetic matching on TWO keys
/// (a grapheme fold and Lexique's real pronunciation, catching irregulars like <c>famme→femme</c>) +
/// Damerau distance-1 edits (letter inversion / typos). Ranking is a <b>stupid-backoff language model</b>
/// score, P(word | up to two previous words) backing off trigram → bigram → unigram, plus a
/// frequency-gated literal-prefix bonus and an edit penalty. Weights are tunable
/// (<see cref="WLm"/>/<see cref="WPrefix"/>/<see cref="WEdit"/>/<see cref="Alpha"/>). Word list in RAM;
/// n-gram tables queried on disk. Built only from Lexique 3.83 + open corpora.
/// </summary>
public sealed class PredictionEngine : IDisposable
{
    private readonly struct Entry
    {
        public readonly string Ortho, OrthoLower;
        public readonly double Freq;           // log10(rawFreq+1)
        public readonly bool Elides;           // vowel / mute-h initial: takes l' d' qu'… before it
        public Entry(string ortho, double freq, bool elides)
        { Ortho = ortho; OrthoLower = ortho.ToLowerInvariant(); Freq = freq; Elides = elides; }
    }

    private readonly Entry[] _e;
    /// <summary>Plan 11 (10D-1): a 32-bit hash of each entry's Lexique pronunciation (<c>words.phon</c>; 0 = none): an
    /// added agreeing form takes the « sounds like » colour when it is pronounced as the word it replaces (Q3).</summary>
    private readonly int[] _phon;
    /// <summary>Plan 11: the entries that are bare elision forms (<see cref="Elision.IsBare"/>), never judged by the grammar.</summary>
    private readonly bool[] _bare;
    private readonly double[] _uni;            // per-entry unigram probability (rawFreq+1)/Z
    private readonly int[] _ao; private readonly string[] _aoKeys;   // sorted by OrthoLower (literal prefix)
    private readonly int[] _fIdx; private readonly string[] _fKeys;  // sorted phonetic keys -> entry index
    private readonly Dictionary<string, List<int>> _strip;          // accent-stripped lower ortho -> entries
    /// <summary>OrthoLower → entry index (plan 11: it replaces the unigram map of 0.8.3; the forms are unique in
    /// lowercase, so <see cref="TryUni"/> gives the same values, and the personal pass, <see cref="PredictNext"/> and the
    /// grammar lexicon get the entry index for free).</summary>
    private readonly Dictionary<string, int> _idxOf;
    private readonly HashSet<string> _elides;                       // OrthoLower of the words that take an elision
    /// <summary>0.8.3: the lexicon forms the spelling reference (Grammalecte French.lex, table <c>spelling_unlisted</c>) does
    /// not have — « yeah », « mam », « team »: checked like unknown words, never ✓, never offered as a correction. Read from
    /// <c>spelling.sqlite</c> next to the database (MPL-2.0 data, kept out of the CC BY-SA <c>prediction.sqlite</c> since the
    /// publication cleanup), else from the database itself (packs up to 2026.11). Empty without either (0.8.2 behaviour).
    /// Prediction does not read it.</summary>
    private readonly HashSet<string> _unlisted;

    private readonly SqliteConnection _con;
    private readonly bool _hasBi, _hasTri, _hasFour;
    private readonly Dictionary<string, Ngram> _biCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Ngram> _triCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Ngram> _fourCache = new(StringComparer.Ordinal);

    private readonly record struct Ngram(Dictionary<string, int>? Map, long Total);
    private readonly record struct Ctx(Ngram Bi, Ngram Tri, Ngram Four);

    // tunable scoring weights (defaults tuned on held-out KSS, see development/evaluation.md)
    public double WLm = 1.0;        // scale of the log language-model probability
    public double WPrefix = 1.0;    // literal-prefix bonus (gated by frequency)
    public double WEdit = 1.0;      // penalty for edit-distance candidates
    public double Alpha = 0.2;      // stupid-backoff discount per level
    public bool UseFourgrams = true;// use the 4-gram level (if the table exists); off = trigram model
    /// <summary>D22: after an elided word (<c>l' d' qu'</c>…, bare in the context), the probability of a
    /// candidate that takes an elision (vowel / mute-h initial) is multiplied by this factor. A ranking hint,
    /// never a filter (1 = off). Tuned on held-out KSS, see development/evaluation.md.</summary>
    public double ElisionBoost = 8.0;
    private const double UniFloor = 1e-8;

    /// <summary>Plan 10A: the unigram probability added to each personal word (a cache-style lift at the unigram
    /// level): a personal word is scored as at least a word of unigram probability <c>uni + WUser · weight</c>, still
    /// under the back-off discount, so it competes with other unigram-level candidates and never displaces a context
    /// (n-gram) prediction. A fixed amount per word (not a share of the list): a long lesson list lifts each of its words
    /// as much as a short list of names. Tuned with <c>Predict.Eval --sweep-user</c>, see development/evaluation.md.</summary>
    public double WUser = 1e-4;
    /// <summary>Plan 10A: extra bonus (log10 scale, like the LM) of a « Mes fautes » target (typed = the pupil's own
    /// spelling, or its first ≥ 3 letters).</summary>
    public double WSpelling = 3.0;
    /// <summary>Plan 10A: the unigram probability given to a personal word the main lexicon does not know.</summary>
    public double UserUnigram = 1e-6;
    /// <summary>Plan 10A: the part of its share a name (a personal word stored with a capital) keeps when the typed word
    /// starts in lowercase.</summary>
    public double NameLowercaseShare = 0.1;
    /// <summary>Plan 10A: the lift of a name is multiplied by this when the typed word starts with a capital (« Lé » after
    /// « avec » is a name far more often than « lé… »); sentence-start words are checked in cases-personal.json.</summary>
    public double NameCapitalLift = 30;
    /// <summary>Plan 10A: at most this many personal words unknown to the lexicon (and to the context) are scored per
    /// pass; they all score alike, so the heaviest / shortest are kept (a 1-letter prefix over 15 000 words stays fast).</summary>
    public int ShortlistSize = 32;
    /// <summary>Plan 10A: the personal share counts from this many typed letters (1 = at once).</summary>
    public int UserBonusFrom = 1;
    private volatile UserLexicon _user = UserLexicon.Empty;
    private readonly double _z;                 // sum of (rawFreq+1): the main prefix gate of a known personal word

    /// <summary>The active profile's personal words (plan 10A). The app builds a new snapshot off the engine and swaps
    /// it here (one reference write); <see cref="UserLexicon.Empty"/> = no personal word (results identical to 0.7).</summary>
    public UserLexicon User { get => _user; set => _user = value ?? UserLexicon.Empty; }

    private volatile NamesLexicon _names = NamesLexicon.Empty;
    /// <summary>Plan 10C: the shipped « Noms propres » pack (read-only, built once at start); <see cref="NamesLexicon.Empty"/>
    /// = no pack (results identical to 0.8.1).</summary>
    public NamesLexicon Names { get => _names; set => _names = value ?? NamesLexicon.Empty; }
    /// <summary>Plan 10C: without a capital typed inside a sentence, a pack name needs this many typed letters (« C » at a
    /// sentence start never brings a name).</summary>
    public int NamesFrom = 2;
    /// <summary>Plan 10C: a pack name whose lowercase homograph in the lexicon is rarer than this (per million: « belgique »
    /// 0.27, « japon » 0.2) takes that homograph's place in the bar, with its capital.</summary>
    public double NameMergeBelow = 1.0;
    /// <summary>Plan 10C (VM recheck 2026-11-15): with a capital typed inside a sentence a name gets this much more (log10
    /// scale, 2 = ×100) on top of <see cref="NameCapitalLift"/>. French writes no common word with a capital there, and
    /// without context counts the lift alone left « avec Jo », « d'Em » to common words with strong contexts (« joie »,
    /// « emploi »). 2.5 since the places were ranked below the common first names (owner 2026-11-15): Joseph then has to
    /// beat « joie » on its own (`cases-personal` 0.439 → 0.474, keep-case KSS −0.1 pt).</summary>
    public double NameCapitalBonus = 2.5;

    private static readonly Regex Tok = new("[a-zà-ÿœ]+", RegexOptions.Compiled);

    private PredictionEngine(Entry[] entries, int[] phon, List<(string key, int idx)> folds, SqliteConnection con,
                             string? spellingPath)
    {
        _e = entries; _phon = phon; _con = con;
        _bare = new bool[entries.Length];
        for (int i = 0; i < entries.Length; i++) _bare[i] = Elision.IsBare(entries[i].Ortho);
        (_unlisted, SpellingProblem) = ReadUnlisted(con, spellingPath);
        _hasBi = TableExists(con, "bigrams");
        _hasTri = TableExists(con, "trigrams");
        _hasFour = TableExists(con, "fourgrams");

        double z = 0;
        foreach (var e in _e) z += Math.Pow(10, e.Freq);          // sum of (rawFreq+1)
        _z = z;
        _uni = new double[_e.Length];
        _idxOf = new Dictionary<string, int>(_e.Length, StringComparer.OrdinalIgnoreCase);
        _strip = new Dictionary<string, List<int>>(_e.Length, StringComparer.Ordinal);
        _elides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _e.Length; i++)
        {
            _uni[i] = Math.Pow(10, _e[i].Freq) / z;
            if (!_idxOf.TryGetValue(_e[i].OrthoLower, out var j) || _uni[i] > _uni[j]) _idxOf[_e[i].OrthoLower] = i;
            if (_e[i].Elides) _elides.Add(_e[i].OrthoLower);
            var s = PhoneticKey.StripLower(_e[i].Ortho);
            if (!_strip.TryGetValue(s, out var l)) _strip[s] = l = new List<int>(1);
            l.Add(i);
        }

        _ao = Enumerable.Range(0, _e.Length).ToArray();
        Array.Sort(_ao, (x, y) => string.CompareOrdinal(_e[x].OrthoLower, _e[y].OrthoLower));
        _aoKeys = _ao.Select(i => _e[i].OrthoLower).ToArray();
        folds.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        _fKeys = folds.Select(f => f.key).ToArray();
        _fIdx = folds.Select(f => f.idx).ToArray();
    }

    /// <summary>The spelling reference file looked for next to a database (publication cleanup, 2026-11-16).</summary>
    public const string SpellingFileName = "spelling.sqlite";

    /// <param name="dbPath">prediction.sqlite.</param>
    /// <param name="spellingPath">the spelling reference; null = <see cref="SpellingFileName"/> next to
    /// <paramref name="dbPath"/> when it exists, else the tables of the database itself.</param>
    public static PredictionEngine Load(string dbPath, string? spellingPath = null)
    {
        var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        con.Open();
        var entries = new List<Entry>(140_000);
        var folds = new List<(string, int)>(260_000);
        var phons = new List<int>(140_000);
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT ortho, phon, freq FROM words";
            using var r = cmd.ExecuteReader();
            int idx = 0;
            while (r.Read())
            {
                var ortho = r.GetString(0);
                var phon = r.IsDBNull(1) ? "" : r.GetString(1);
                var freq = r.IsDBNull(2) ? 0.0 : r.GetDouble(2);
                entries.Add(new Entry(ortho, Math.Log10(freq + 1.0), Elision.TakesElision(ortho, phon)));
                phons.Add(PhonHash(phon));
                var gk = PhoneticKey.Fold(ortho);
                folds.Add((gk, idx));
                var pk = PhoneticKey.FromLexiquePhon(phon);
                if (pk.Length >= 2 && pk != gk) folds.Add((pk, idx));
                idx++;
            }
        }
        if (spellingPath is null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            var sibling = dir is null ? null : Path.Combine(dir, SpellingFileName);
            if (sibling is not null && File.Exists(sibling)) spellingPath = sibling;
        }
        return new PredictionEngine(entries.ToArray(), phons.ToArray(), folds, con, spellingPath);
    }

    /// <summary>FNV-1a of a pronunciation (0 for none; a collision only changes the colour of an added form).</summary>
    private static int PhonHash(string phon)
    {
        if (phon.Length == 0) return 0;
        uint h = 2166136261;
        foreach (var c in phon) { h ^= c; h *= 16777619; }
        return h == 0 ? 1 : (int)h;
    }

    /// <summary>A spelling.sqlite that cannot be read (corrupt, locked, not a database) never stops the engine: the
    /// reference then comes from the database's own tables, else none (0.8.2 behaviour), and the reason is returned for
    /// the caller's log.</summary>
    private static (HashSet<string> Set, string? Problem) ReadUnlisted(SqliteConnection db, string? spellingPath)
    {
        if (spellingPath is not null && File.Exists(spellingPath))
        {
            try
            {
                using var own = new SqliteConnection($"Data Source={spellingPath};Mode=ReadOnly;Pooling=False");
                own.Open();
                return (ReadUnlistedFrom(own), null);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
                var problem = $"{Path.GetFileName(spellingPath)} unreadable ({ex.GetType().Name}: {ex.Message.Trim()}); " +
                              "the database's own spelling tables are used if it has them";
                try { return (ReadUnlistedFrom(db), problem); }
                catch (SqliteException) { return (new HashSet<string>(StringComparer.Ordinal), problem); }
            }
        }
        return (ReadUnlistedFrom(db), null);
    }

    private static HashSet<string> ReadUnlistedFrom(SqliteConnection con)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!TableExists(con, "spelling_unlisted")) return set;
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT ortho FROM spelling_unlisted";
        using var r = cmd.ExecuteReader();
        while (r.Read()) if (!r.IsDBNull(0)) set.Add(r.GetString(0).ToLowerInvariant());
        return set;
    }

    /// <summary>Why the spelling reference file could not be read (null when it was, or when there is none).</summary>
    public string? SpellingProblem { get; }

    public int Count => _e.Length;
    /// <summary>0.8.3: lexicon forms left out of the spelling reference (0 without the table).</summary>
    public int UnlistedCount => _unlisted.Count;
    /// <summary>0.8.3: the lexicon knows <paramref name="word"/> and the spelling reference has it (« yeah », « mam »: false).</summary>
    public bool KnowsSpelled(string word) => Knows(word) && !_unlisted.Contains(word.ToLowerInvariant());
    /// <summary>
    /// 0.8.3: the ✓ of the word being typed, by the rules of <see cref="Check"/>: a trusted lexicon form, a personal word,
    /// or a form of the pack — except a very rare pack name at a sentence start (« Pome »), which <see cref="Check"/> judges
    /// by its sound there (no ✓ while typing, then ≈ once finished: the 10C nit).
    /// </summary>
    public bool KnowsForMark(string word, bool sentenceStart)
    {
        if (string.IsNullOrEmpty(word)) return false;
        if (KnowsSpelled(word) || _user.Contains(word)) return true;
        var nm = _names;
        return nm.ContainsForm(word) && !(sentenceStart && nm.IsWeak(word));
    }
    public bool HasContext => _hasBi || _hasTri;
    public bool Knows(string word) => word != null && _idxOf.ContainsKey(word.ToLowerInvariant());

    /// <summary>Plan 11: the entry index of a lowercase lexicon form, else −1 (<see cref="GrammarLexicon.Load"/>).</summary>
    internal int IndexOf(string lower) => _idxOf.TryGetValue(lower, out var i) ? i : -1;
    /// <summary>Plan 11: the form of an entry (as stored: lowercase for the lexicon).</summary>
    internal string FormAt(int entry) => _e[entry].Ortho;
    /// <summary>Plan 11: log10(frequency per million + 1) of an entry (the Eval's « most frequent sibling »).</summary>
    internal double FreqAt(int entry) => _e[entry].Freq;

    /// <summary>The unigram probability of a lexicon form (the most frequent entry of its lowercase letters).</summary>
    private bool TryUni(string lower, out double uni)
    {
        if (_idxOf.TryGetValue(lower, out var i)) { uni = _uni[i]; return true; }
        uni = 0; return false;
    }

    private volatile GrammarLexicon _grammar = GrammarLexicon.Empty;
    /// <summary>Plan 11: the morphology of the lexicon forms (<c>grammar.sqlite</c>, Grammalecte French.lex, MPL-2.0),
    /// loaded off the UI thread and swapped in one reference write, as <see cref="Names"/>; one snapshot is read per query.
    /// It must have been loaded for this engine (its arrays are indexed by this engine's entries).
    /// <see cref="GrammarLexicon.Empty"/> = none: the results are those of 0.8.3 byte for byte.</summary>
    public GrammarLexicon Grammar { get => _grammar; set => _grammar = value ?? GrammarLexicon.Empty; }

    // ---- plan 11 (10D-1): grammatical prediction. With GrammarOn false, every weight at 0, or Grammar = Empty, the
    // results are those of 0.8.3 byte for byte. Defaults from Predict.Eval --sweep-grammar (development/evaluation.md).
    /// <summary>10D-1: the agreement term on and off (the app's <c>--no-grammar</c> test switch; no setting in 0.9.0).</summary>
    public bool GrammarOn = true;
    /// <summary>10D-1: the weight (log10 scale, like the LM) of noun / adjective agreement after a determiner or a noun.</summary>
    public double WNominal = 1.0;
    /// <summary>10D-1: the weight of verb agreement (person after a subject pronoun; participle after être / avoir;
    /// infinitive after a preposition or a modal) and of the parts of speech disfavoured after a subject.</summary>
    public double WVerbal = 1.0;
    /// <summary>10D-1: the person of a verb after a noun subject (« les enfants mang… »), as a fraction of <see cref="WVerbal"/>.</summary>
    public double WNounSubj = 0.5;
    /// <summary>10D-1: an agreeing form the typed letters cannot reach (a lemma sibling, a homophone family member) is added
    /// at the score of the word it comes from minus this, plus its own agreement term.</summary>
    public double InjectGap = 0.5;
    /// <summary>10D-1: the share of its weight an agreeing candidate gets as a bonus (1 = + W, as the plan's formula; 0 =
    /// only the forms that disagree lose W). The sweep showed that a full bonus lifts every agreeing verb over the clitics
    /// and adverbs that often come first (« je ne », « je vous »): see development/evaluation.md § Grammar 10D-1.</summary>
    public double AgreeBonus = 0.0;
    /// <summary>10D-1: « par » counts as a preposition followed by an infinitive (sweep option).</summary>
    public bool ParInfinitive = false;
    /// <summary>10D-1: the wider disfavoured parts of speech (no noun / adjective / participle / infinitive right after a
    /// subject, no finite verb right after a determiner; <see cref="AgreementContext.From"/>).</summary>
    public bool WideDisfavour = true;
    /// <summary>10D-1: how many of the best candidates lend their lemma's agreeing forms, and how many forms each adds at
    /// most (the most frequent first).</summary>
    public int InjectSources = 5, InjectPerSource = 2;

    /// <summary>The agreement term of one query: the context's <see cref="Expect"/> and the weights (default = off).</summary>
    private readonly struct GTerm
    {
        public readonly Expect Ex;
        public readonly GrammarLexicon G;
        private readonly double _wn, _wv, _wns, _bonus;
        private readonly bool[] _bare;
        public GTerm(Expect ex, GrammarLexicon g, double wn, double wv, double wns, double bonus, bool[] bare)
        { Ex = ex; G = g; _wn = wn; _wv = wv; _wns = wns; _bonus = bonus; _bare = bare; }
        public bool On => G is not null;
        // the bare elision forms (« s », « l », « qu »: French.lex knows them as letters, nouns) are never judged: « il s' »
        public double Of(int entry) => _bare[entry] ? 0 : Ex.Term(G.BitsOf(entry), _wn, _wv, _wns, _bonus);
        /// <summary>The weight of the entry's agreeing analysis (0 = it does not agree).</summary>
        public double Agrees(int entry) => _bare[entry] ? 0 : Ex.AgreeWeight(G.BitsOf(entry), _wn, _wv, _wns);

        /// <summary>Does the entry agree as a form of <paramref name="lemma"/>, through an analysis of the part-of-speech
        /// classes <paramref name="classes"/> (review 10D-1: « étais » must not pass as the plural of the noun « étai » when
        /// the verb « être » lends it)?</summary>
        public double AgreesAs(int entry, int lemma, int classes)
        {
            if (_bare[entry]) return 0;
            var b = G.BitsOf(entry);
            var l = G.LemmasOf(entry);
            Span<uint> keep = stackalloc uint[b.Length];
            int n = 0;
            for (int i = 0; i < b.Length; i++) if (l[i] == lemma && (PosClass(b[i]) & classes) != 0) keep[n++] = b[i];
            return n == 0 ? 0 : Ex.AgreeWeight(keep[..n], _wn, _wv, _wns);
        }

        /// <summary>Part-of-speech class of an analysis for lending siblings: 1 verb (finite, participles, infinitive), 2 noun
        /// or adjective, 0 other.</summary>
        public static int PosClass(uint bits) =>
            MorphBits.IsVerbal(bits) ? 1 : MorphBits.Pos(bits) is MorphBits.Noun or MorphBits.Adjective ? 2 : 0;
    }

    private (GrammarLexicon? G, string? P3, string? P2, string? P1, bool Par, bool Wide, Expect Ex) _expect;

    /// <summary>The agreement term for this context, or off (default) when there is no grammar, no rule applies, or the
    /// weights are 0. The <see cref="Expect"/> of the last context is cached (one entry).</summary>
    private GTerm GrammarTerm(string? p1, string? p2, string? p3)
    {
        var g = _grammar;
        if (!GrammarOn || g.IsEmpty || WNominal == 0 && WVerbal == 0 || p1 is null) return default;
        var c = _expect;
        Expect ex;
        if (ReferenceEquals(c.G, g) && c.Par == ParInfinitive && c.Wide == WideDisfavour && c.P1 == p1 && c.P2 == p2 && c.P3 == p3) ex = c.Ex;
        else
        {
            ex = AgreementContext.From(p3, p2, p1, w => g.BitsOf(IndexOf(w)), ParInfinitive, WideDisfavour);
            _expect = (g, p3, p2, p1, ParInfinitive, WideDisfavour, ex);
        }
        return ex.IsEmpty ? default : new GTerm(ex, g, WNominal, WVerbal, WNounSubj, AgreeBonus, _bare);
    }

    /// <summary>10D-1 (tests, Eval): the expectation the engine reads from a context (<see cref="Expect.None"/> without
    /// grammar).</summary>
    public Expect ExpectOf(IReadOnlyList<string>? context)
    {
        var (p1, p2, p3) = PrevWords(context);
        var g = _grammar;
        return g.IsEmpty || p1 is null ? Expect.None : AgreementContext.From(p3, p2, p1, w => g.BitsOf(IndexOf(w)), ParInfinitive, WideDisfavour);
    }
    /// <summary>Known to the main lexicon or to the active personal words (plan 10A).</summary>
    /// <summary>Plan 10C (owner 2026-11-15): is the lowercase form of <paramref name="word"/> a common lexicon word (more
    /// frequent than <see cref="NameMergeBelow"/>: « rose », « lune », « ange »)? At a sentence start such a word is the common
    /// noun (its picture kept) unless the lists hold it as a name.</summary>
    public bool IsCommonWord(string word) =>
        !string.IsNullOrEmpty(word) && TryUni(word.ToLowerInvariant(), out var u) && u * _z - 1 > NameMergeBelow;

    public bool KnowsAny(string word) => Knows(word) || _user.Contains(word) || _names.ContainsForm(word);

    /// <summary>
    /// Plan 10B (D12): is <paramref name="word"/> spelled as a known word? <b>Known</b> = its lowercase form is in the
    /// lexicon and, since 0.8.3, in the spelling reference (« yeah », « mam » are not: they are checked as below), or in the
    /// active personal words (accents significant; a hidden « Classe » word is not a personal word).
    /// <b>SoundsLike</b> = not known, but a « Mes fautes » spelling of the profile, or the same letters as known words
    /// without their accents (« ecole »), or the same <i>whole</i> sound key as known words (« maizon », « famme »: either
    /// key, the grapheme fold or Lexique's pronunciation) — the candidates come back best first. Else <b>Unknown</b>.
    /// Plan 10C: a name of the « Noms propres » pack (its exact form, a variant or a part) is Known; the same letters as a
    /// pack name in another case or without its accents (« emma », « ines ») sound like it.
    /// A few binary searches and dictionary lookups (≈ µs); no state, nothing recorded.
    /// </summary>
    public SpellingCheck Check(string word, int max = 6)
    {
        var w = (word ?? "").Trim();
        if (w.Length == 0) return SpellingCheck.Unknown;
        var u = _user;                                  // one snapshot for the whole check
        var nm = _names;
        if (KnowsSpelled(w) || u.Contains(w)) return SpellingCheck.Known;
        // owner 2026-11-15: a very rare recognised-tier pack name (« Pome », « Kan ») is checked by its sound too, so that at a
        // sentence start it may be ≈ (the indicator decides: WeakName); any other pack form is Known
        bool packOnly = nm.ContainsForm(w);
        if (packOnly && !nm.IsWeak(w)) return SpellingCheck.Known;
        // no LINQ / closure in this path (it runs on the UI thread for each finished word): plain loops, one cached comparer
        List<string>? found = null;
        List<int>? hits = null;

        var strip = PhoneticKey.StripLower(w);
        int fx = u.FixKeys.Length > 0 ? LowerBound(u.FixKeys, strip) : 0;
        if (fx < u.FixKeys.Length && u.FixKeys[fx] == strip) AddFound(ref found, u.FixTargets[fx], max);   // « Mes fautes »
        if (_strip.TryGetValue(strip, out var si))
        {
            hits = new List<int>(si.Count);
            foreach (var i in si) if (Offerable(i, w)) hits.Add(i);
            AddRanked(ref found, hits, max);
        }
        if (u.Strip.TryGetValue(strip, out var us)) foreach (var i in us) AddFound(ref found, u.Entries[i].Form, max);
        // plan 10C: the same letters as a pack name, but another case or accents (« emma » → Emma, « ines » → Inès)
        if (!nm.IsEmpty) foreach (var f in nm.SameLetters(w)) if (f != w) AddFound(ref found, f, max);
        var fold = PhoneticKey.Fold(w);
        if (fold.Length >= 2)
        {
            int p0 = LowerBound(_fKeys, fold);
            if (p0 < _fKeys.Length && _fKeys[p0] == fold)
            {
                if (hits is null) hits = new List<int>(); else hits.Clear();
                for (int p = p0; p < _fKeys.Length && _fKeys[p] == fold; p++)
                {
                    int i = _fIdx[p];
                    if (Offerable(i, w) && !hits.Contains(i)) hits.Add(i);   // a word may have both keys equal
                }
                AddRanked(ref found, hits, max);
            }
            for (int p = LowerBound(u.FoldKeys, fold); p < u.FoldKeys.Length && u.FoldKeys[p] == fold; p++) AddFound(ref found, u.Entries[u.FoldIdx[p]].Form, max);
        }
        if (packOnly) return found is { Count: > 0 } ? new SpellingCheck(SpellingStatus.SoundsLike, found, WeakName: true) : SpellingCheck.Known;
        return found is { Count: > 0 } ? new SpellingCheck(SpellingStatus.SoundsLike, found) : SpellingCheck.Unknown;
    }

    /// <summary>A lexicon entry the ≈ list may offer for <paramref name="typed"/>: no noise, a form of the spelling reference
    /// (0.8.3: never « yeah »), and not the typed word itself (an unlisted word is never its own correction).</summary>
    private bool Offerable(int i, string typed) =>
        !Elision.IsNoise(_e[i].Ortho) && !(_unlisted.Count > 0 && _unlisted.Contains(_e[i].OrthoLower)) &&
        !string.Equals(_e[i].OrthoLower, typed, StringComparison.OrdinalIgnoreCase);

    private Comparison<int>? _byFreq;   // most frequent first, then by form (stable order)

    private void AddRanked(ref List<string>? found, List<int> idx, int max)
    {
        if (idx.Count == 0) return;
        idx.Sort(_byFreq ??= (a, b) => _e[a].Freq != _e[b].Freq ? _e[b].Freq.CompareTo(_e[a].Freq) : string.CompareOrdinal(_e[a].Ortho, _e[b].Ortho));
        foreach (var i in idx) AddFound(ref found, _e[i].Ortho, max);
    }

    private static void AddFound(ref List<string>? found, string c, int max)
    {
        found ??= new List<string>(max);
        if (found.Count >= max) return;
        foreach (var f in found) if (string.Equals(f, c, StringComparison.Ordinal)) return;
        found.Add(c);
    }

    private IReadOnlySet<string>? _top300;
    /// <summary>The 300 most frequent lexicon forms (lowercase): a lesson's words outside them are content words
    /// (<see cref="LessonExtractor"/>).</summary>
    public IReadOnlySet<string> MostFrequent300 => _top300 ??= _e.OrderByDescending(e => e.Freq).Select(e => e.OrthoLower)
                                                                .Distinct().Take(300).ToHashSet(StringComparer.Ordinal);

    /// <param name="sentenceStart">the word starts a sentence: a capital typed there says nothing about a name (plan 10A:
    /// only a capital typed inside a sentence lifts the personal names).</param>
    public IReadOnlyList<Suggestion> Predict(string typed, IReadOnlyList<string>? context = null, int k = 8, bool sentenceStart = false)
    {
        typed = (typed ?? "").Trim();
        if (typed.Length == 0) return Array.Empty<Suggestion>();
        var (c1, c2, c3) = PrevWords(context);
        var ctx = ContextMaps(c1, c2, c3);
        double eb = ElisionBonus(c1);
        var gt = GrammarTerm(c1, c2, c3);   // plan 11: off (default) unless a rule applies to this context
        var top = gt.On ? new TopSources(InjectSources) : null;
        var typedLower = typed.ToLowerInvariant();
        double T(int i) => gt.Of(i);
        // speed (10D-1 bench): with no bonus every term is <= 0, so it is added at the ranking only to the few best candidates
        // (« lazy », the same results as adding it in the passes); with a bonus it is added in the passes
        bool lazy = gt.On && AgreeBonus <= 0;
        var u = _user;                      // one snapshot for the whole query (the app may swap it meanwhile)
        bool user = !u.IsEmpty;
        // Ordinal (plan 10, Q13): the main forms are unique lowercase, so nothing changes for them; a personal form
        // with a capital (« Pierre ») is a candidate of its own next to « pierre »
        var best = new Dictionary<string, (double score, MatchKind kind)>(StringComparer.Ordinal);
        double share = user && typed.Length >= UserBonusFrom ? WUser : 0;
        double UB(string lower) => share != 0 && u.LowerBonus.TryGetValue(lower, out var w) ? share * w : 0;

        foreach (var pos in PrefixRange(_aoKeys, typed.ToLowerInvariant()))
        {
            int i = _ao[pos];
            double gate = Math.Clamp(_e[i].Freq / 1.5, 0.0, 1.0);
            double sc = Lm(ctx, _e[i].OrthoLower, _uni[i], UB(_e[i].OrthoLower)) + WPrefix * gate + (_e[i].Elides ? eb : 0);
            // plan 11: only the word typed in full can lend its siblings among the literal prefixes (see Inject)
            if (gt.On)
            {
                if (lazy) { if (_e[i].OrthoLower.Length == typedLower.Length) top!.Offer(i, sc + T(i)); }
                else { sc += T(i); if (_e[i].OrthoLower.Length == typedLower.Length) top!.Offer(i, sc); }
            }
            Consider(best, _e[i].Ortho, sc, MatchKind.Prefix);
        }

        var f = PhoneticKey.Fold(typed);
        // 0.8.3: a capital typed inside a sentence asks for a name; a common word that only sounds like it (« avec Lé » →
        // « les », now that « lé » keeps its sounded e) is held back by the names' own bonus
        double soundCap = !sentenceStart && char.IsUpper(typed[0]) ? -NameCapitalBonus : 0;
        if (f.Length >= 2)
            foreach (var pos in PrefixRange(_fKeys, f))
            {
                int i = _fIdx[pos];
                // 0.8.3 (speed): a word already scored — as a literal prefix (prefix bonus ≥ 0) or by its other sound key — can
                // only keep its score here, so its LM is not computed again (same results, fewer lookups)
                if (WPrefix >= 0 && best.ContainsKey(_e[i].Ortho)) continue;
                double sc = Lm(ctx, _e[i].OrthoLower, _uni[i], UB(_e[i].OrthoLower)) + (_e[i].Elides ? eb : 0) + soundCap;
                // plan 11: the agreement term depends on the word only, so the « already scored » shortcuts stay valid
                if (gt.On) { if (lazy) top!.Offer(i, sc + T(i)); else { sc += T(i); top!.Offer(i, sc); } }
                Consider(best, _e[i].Ortho, sc, MatchKind.Phonetic);
            }

        List<string>? edits = typed.Length >= 4 ? Edits1(PhoneticKey.StripLower(typed)).ToList() : null;
        if (edits != null)
            foreach (var v in edits)
                if (_strip.TryGetValue(v, out var idxs))
                    foreach (var i in idxs)
                    {
                        if (best.ContainsKey(_e[i].Ortho) && WEdit >= 0) continue;   // an edit (− WEdit) never beats a score already there
                        double sc = Lm(ctx, _e[i].OrthoLower, _uni[i], UB(_e[i].OrthoLower)) - WEdit + (_e[i].Elides ? eb : 0);
                        if (gt.On) { if (lazy) top!.Offer(i, sc + T(i)); else { sc += T(i); top!.Offer(i, sc); } }
                        Consider(best, _e[i].Ortho, sc, MatchKind.Edit);
                    }

        if (user) UserPass(u, best, typed, f, edits, ctx, eb, share, sentenceStart, lazy ? default : gt);
        var nm = _names;
        if (!nm.IsEmpty) NamesPass(nm, best, typed, eb, sentenceStart, k);
        if (gt.On) Inject(best, typed, gt, top!, lazy);

        bool capital = !sentenceStart && char.IsUpper(typed[0]);
        if (lazy)
        {
            bool Name(string key) => key.Length > 0 && char.IsUpper(key[0]) && (user && (u.Contains(key) || u.IsFixTarget(key)) ||
                                                                                nm.ContainsForm(key) && !(sentenceStart && IsCommonWord(key)));
            // the term of a lexicon form (its key is the lexicon spelling; a capitalised name is not judged)
            double Adj(string key, double score) =>
                _idxOf.TryGetValue(key, out var ix) && string.Equals(_e[ix].Ortho, key, StringComparison.Ordinal) ? score + gt.Of(ix) : score;
            bool capped = capital && typed.Length < NamesCapUntil;
            var all = new List<(string, double, MatchKind)>(best.Count);
            foreach (var (key, v) in best) all.Add((key, v.score, v.kind));
            var rk = LazyTop(all, capped ? int.MaxValue : k, Adj, byLength: true).Select(x => new Suggestion(x.Key, x.Score, x.Kind, Name(x.Key)));
            return capped ? CapNames(rk, k) : rk.Take(k).ToArray();
        }
        var ranked = best.Select(kv => new Suggestion(kv.Key, kv.Value.score, kv.Value.kind,
                                                kv.Key.Length > 0 && char.IsUpper(kv.Key[0]) &&
                                                (user && (u.Contains(kv.Key) || u.IsFixTarget(kv.Key)) ||
                                                 nm.ContainsForm(kv.Key) && !(sentenceStart && IsCommonWord(kv.Key)))))
                   .OrderByDescending(s => s.Score).ThenBy(s => s.Word.Length);
        return capital && typed.Length < NamesCapUntil ? CapNames(ranked, k) : ranked.Take(k).ToArray();
    }

    /// <summary>0.8.3 (10C review): with a capital typed inside a sentence and fewer than this many letters, names take at
    /// most half of the words (« le livre Le », a capital the keyboard could not see as a sentence start): the best name
    /// keeps its rank, the other words stay in the bar.</summary>
    public int NamesCapUntil = 3;

    /// <summary>The best <paramref name="k"/> of <paramref name="ranked"/> (best first) so that every first p words hold at
    /// most ⌈p / 2⌉ names — whatever number of them the bar shows (the keyboard asks for spares and shows 8 of 11: VM
    /// recheck 0.8.3, « le livre Le » had 6 names of 8 with a plain « half of k » cap). At each place the best remaining word
    /// is taken unless it is a name over the allowance, then the best other word; names fill only when no other word is
    /// left. The best name keeps its place.</summary>
    private static Suggestion[] CapNames(IEnumerable<Suggestion> ranked, int k)
    {
        var names = new Queue<Suggestion>();
        var others = new Queue<Suggestion>();
        foreach (var s in ranked) { if (s.Name) names.Enqueue(s); else others.Enqueue(s); if (names.Count >= k && others.Count >= k) break; }
        var keep = new List<Suggestion>(k);
        int taken = 0;
        while (keep.Count < k && (names.Count > 0 || others.Count > 0))
        {
            int allowed = (keep.Count + 2) / 2;   // ⌈(p + 1) / 2⌉ for the word at place p + 1
            bool nameFirst = names.Count > 0 && (others.Count == 0 || Better(names.Peek(), others.Peek()));
            if (nameFirst && (taken < allowed || others.Count == 0)) { keep.Add(names.Dequeue()); taken++; }
            else if (others.Count > 0) keep.Add(others.Dequeue());
            else { keep.Add(names.Dequeue()); taken++; }
        }
        return keep.ToArray();

        static bool Better(in Suggestion a, in Suggestion b) => a.Score != b.Score ? a.Score > b.Score : a.Word.Length < b.Word.Length;
    }

    /// <summary>
    /// Plan 10C: the « Noms propres » candidates (literal prefix only). A name scores as a unigram of frequency
    /// <c>cap × rang</c> per million (≤ 5: below the common content words), with the same back-off discount and prefix gate
    /// as a lexicon word but <b>no context counts</b> (review: a name's own subtitle n-grams, « avec Casey », would override
    /// the pack's ranking, which follows recent births — owner Q11); typed in lowercase inside a sentence it keeps
    /// <see cref="NameLowercaseShare"/> of that, at a sentence start all of it (the capital says nothing there), and a
    /// capital typed inside a sentence multiplies it by <see cref="NameCapitalLift"/> and opens the « recognised » tier (the
    /// ≈ 24 000 rarer first names, a key array of their own that other queries never walk). Without that capital a name
    /// needs <see cref="NamesFrom"/> letters. A vowel-initial name takes the elision boost after « d' », « l' » (D22). A
    /// variant (« Ieper ») brings itself and the name it stands for (« Ypres »). A name whose lowercase homograph is a rare
    /// lexicon form (<see cref="NameMergeBelow"/>: « belgique ») replaces it. As the score only depends on the frequency
    /// within one query, the best k + 4 names of the prefix range are chosen by frequency and only they are scored.
    /// </summary>
    private void NamesPass(NamesLexicon n, Dictionary<string, (double, MatchKind)> best, string typed, double eb,
                           bool sentenceStart, int k)
    {
        bool capital = !sentenceStart && char.IsUpper(typed[0]);
        if (!capital && typed.Length < NamesFrom) return;
        double mult = capital ? NameCapitalLift : sentenceStart ? 1.0 : NameLowercaseShare;
        int keep = k + 4;
        var top = new List<(float Freq, int Idx)>(keep + 1);
        var lo = typed.ToLowerInvariant();
        void Walk(string[] keys, int[] idx)
        {
            for (int pos = LowerBound(keys, lo); pos < keys.Length && keys[pos].StartsWith(lo, StringComparison.Ordinal); pos++)
            {
                int i = idx[pos];
                ref readonly var e0 = ref n.Entries[i];
                if (e0.Restricted && typed.Length < 3) continue;   // owner 2026-11-15: a capital and 3 letters at least
                float f = e0.Freq;
                if (top.Count == keep && f <= top[^1].Freq) continue;
                int at = top.Count;
                while (at > 0 && top[at - 1].Freq < f) at--;
                top.Insert(at, (f, i));
                if (top.Count > keep) top.RemoveAt(top.Count - 1);
            }
        }
        Walk(n.Keys1, n.Idx1);
        if (capital) Walk(n.Keys2, n.Idx2);
        foreach (var (_, i) in top)
        {
            var e = n.Entries[i];
            double fq = e.Freq * mult;
            double sc = WLm * Math.Log10(Alpha * Alpha * Alpha * (fq + 1) / _z + 1e-12) + WPrefix * Math.Clamp(Math.Log10(fq + 1) / 1.5, 0.0, 1.0)
                        + (e.Elides ? eb : 0) + (capital ? NameCapitalBonus : 0);
            Merge(e.Form, e.Lower, sc);
            if (e.IsVariant) Merge(e.Shown, e.Shown.ToLowerInvariant(), sc - 0.01);   // « Ieper » → « Ypres » just after
        }

        void Merge(string form, string lower, double sc)
        {
            if (lower != form && TryUni(lower, out var u) && u * _z - 1 <= NameMergeBelow
                && best.TryGetValue(lower, out var low))
            {
                best.Remove(lower);
                sc = Math.Max(sc, low.Item1);
            }
            Consider(best, form, sc, MatchKind.Prefix);
        }
    }

    /// <summary>
    /// Plan 10A: the personal words' candidates — literal prefix (with the main frequency gate for a word the lexicon
    /// knows, a full gate for a new one), sound prefix (≥ 2 fold keys), edit-1 (typed ≥ 4); each scored by the LM with
    /// the main unigram when the word is known, else <see cref="UserUnigram"/>, mixed with the list share (<see cref="WUser"/>).
    /// « Mes fautes »: the typed form (accent-stripped) equal to a pair's spelling, or its first ≥ 3 letters → the
    /// target with <see cref="WSpelling"/> on top, kind Phonetic (the bar's « sounds like » colour).
    /// </summary>
    private void UserPass(UserLexicon u, Dictionary<string, (double, MatchKind)> best, string typed, string fold, List<string>? edits,
                          Ctx ctx, double eb, double share, bool sentenceStart, GTerm gt)
    {
        // a name (stored with a capital) gets its full share when the pupil starts the word with a capital inside a
        // sentence, as French writes names (at a sentence start every word has one); typed in lowercase it is still offered, with NameLowercaseShare of it (common words come first)
        bool capital = !sentenceStart && typed.Length > 0 && char.IsUpper(typed[0]);
        double W(in UserLexicon.Entry e) => !e.Name ? e.Weight : capital ? e.Weight * NameCapitalLift : e.Weight * NameLowercaseShare;
        // plan 10C: the capital bonus of the pack's names applies to the pupil's / class's names too, so that they stay
        // ahead of the pack (a classmate before a town)
        double B(in UserLexicon.Entry e) => capital && e.Name ? NameCapitalBonus : 0;
        // plan 11: a list word the lexicon knows gets its agreement term here too, or the max in Consider would drop a
        // penalty the main passes gave it (names, stored with a capital, stay neutral as the pack's)
        double Score(string lower, double weight, out double gate, bool name = false)
        {
            double uni;
            int ix = -1;
            if (_idxOf.TryGetValue(lower, out ix)) { double mu = _uni[ix]; uni = mu; gate = Math.Clamp(Math.Log10(mu * _z) / 1.5, 0.0, 1.0); }
            else { ix = -1; uni = UserUnigram; gate = 1.0; }
            double s = Lm(ctx, lower, uni, share * weight) + (eb != 0 && _elides.Contains(lower) ? eb : 0);
            return gt.On && ix >= 0 && !name ? s + gt.Of(ix) : s;
        }
        // A short prefix can match thousands of list words. Words the lexicon does not know and the context never saw all
        // score the same for a given weight (and the bar breaks ties by length): only the ShortlistSize heaviest /
        // shortest of them are scored; known words and words the context saw are always scored (plan 10A, W9 speed gate).
        var shortlist = new List<(double W, int Len, int Idx)>();
        void Shortlist(int i, double w)
        {
            int len = u.Entries[i].Lower.Length;
            if (shortlist.Count < ShortlistSize) { shortlist.Add((w, len, i)); return; }
            int worst = 0;
            for (int j = 1; j < shortlist.Count; j++)
                if (shortlist[j].W < shortlist[worst].W || shortlist[j].W == shortlist[worst].W && shortlist[j].Len > shortlist[worst].Len) worst = j;
            var c = shortlist[worst];
            if (w > c.W || w == c.W && len < c.Len) shortlist[worst] = (w, len, i);
        }
        bool InContext(string lower) =>
            ctx.Four.Map is not null && ctx.Four.Map.ContainsKey(lower) || ctx.Tri.Map is not null && ctx.Tri.Map.ContainsKey(lower) ||
            ctx.Bi.Map is not null && ctx.Bi.Map.ContainsKey(lower);
        void Pass(IEnumerable<int> idxs, MatchKind kind, bool prefix)
        {
            shortlist.Clear();
            foreach (var i in idxs)
            {
                var e = u.Entries[i];
                if (!_idxOf.ContainsKey(e.Lower) && !InContext(e.Lower)) { Shortlist(i, W(e)); continue; }
                double sc = Score(e.Lower, W(e), out var gate, e.Name) + B(e);
                Consider(best, e.Form, prefix ? sc + WPrefix * gate : sc, kind);
            }
            foreach (var (w, _, i) in shortlist)
            {
                var e = u.Entries[i];
                double sc = Score(e.Lower, w, out var gate, e.Name) + B(e);
                Consider(best, e.Form, prefix ? sc + WPrefix * gate : sc, kind);
            }
        }
        Pass(PrefixRange(u.LowerKeys, typed.ToLowerInvariant()).Select(pos => u.LowerIdx[pos]), MatchKind.Prefix, true);
        if (fold.Length >= 2)
            Pass(PrefixRange(u.FoldKeys, fold).Select(pos => u.FoldIdx[pos]), MatchKind.Phonetic, false);
        if (edits != null)
            foreach (var v in edits)
                if (u.Strip.TryGetValue(v, out var idxs))
                    foreach (var i in idxs)
                    {
                        var e = u.Entries[i];
                        Consider(best, e.Form, Score(e.Lower, W(e), out _, e.Name) - WEdit + B(e), MatchKind.Edit);
                    }
        if (u.FixKeys.Length > 0)
        {
            var key = PhoneticKey.StripLower(typed);
            foreach (var pos in PrefixRange(u.FixKeys, key))
            {
                if (u.FixKeys[pos].Length != key.Length && key.Length < 3) continue;   // a prefix counts from 3 letters (Q12)
                var target = u.FixTargets[pos];
                Consider(best, target, Score(target.ToLowerInvariant(), 1.0, out _, target.Length > 0 && char.IsUpper(target[0])) + WSpelling, MatchKind.Phonetic);
            }
        }
    }

    /// <summary>Plan 11: the best <c>n</c> lexicon candidates of a query (entry, score), kept while the passes run — the
    /// candidates map is keyed by the word, and looking every key up again would cost more than the query.</summary>
    private sealed class TopSources
    {
        public readonly int[] Idx;
        public readonly double[] Score;
        public int Count;
        public TopSources(int n) { Idx = new int[Math.Max(0, n)]; Score = new double[Math.Max(0, n)]; }

        public void Offer(int i, double s)
        {
            if (Idx.Length == 0 || Count == Idx.Length && s <= Score[Count - 1]) return;
            for (int k = 0; k < Count; k++)
                if (Idx[k] == i) { if (s > Score[k]) { Remove(k); break; } return; }
            int at = Count < Idx.Length ? Count : Count - 1;
            if (Count < Idx.Length) Count++;
            while (at > 0 && Score[at - 1] < s) { Idx[at] = Idx[at - 1]; Score[at] = Score[at - 1]; at--; }
            Idx[at] = i; Score[at] = s;
        }

        private void Remove(int k)
        {
            for (int j = k; j < Count - 1; j++) { Idx[j] = Idx[j + 1]; Score[j] = Score[j + 1]; }
            Count--;
        }
    }

    /// <summary>
    /// Plan 11 (10D-1): add the agreeing form the typed letters cannot reach. (1) Homophone families: when the typed word
    /// is a member (« et »), another member the context favours (« est ») is added — never « ces » / « ses ». (2) Lemma
    /// siblings: for the best <see cref="InjectSources"/> lexicon candidates that do not agree themselves and are the word
    /// typed in full or a sound-alike / correction of it (a completion of the typed letters lends nothing: typing on reaches
    /// the agreeing form), the other lexicon forms of their lemmas that agree with the context, start with the same letter
    /// (« manger » → « mangé », « journal » → « journaux »; never « sont » → « est ») and are no elision / noise form: the
    /// <see cref="InjectPerSource"/> most frequent. Each is put at the source's score without its own term −
    /// <see cref="InjectGap"/> + its own term — added when it is no candidate yet, lifted to that score when it is one
    /// already lower (« tu fait » → « fais », a sound-alike far below). The colour (Q3): « sounds like » when both forms have
    /// the same Lexique pronunciation (always within a family), else the correction colour; a literal-prefix candidate keeps
    /// its colour.
    /// </summary>
    /// <param name="lazy">the candidates' scores do not hold their terms yet (they are added at the ranking): a form put here
    /// is stored without its own term, so that the ranking gives it the same score.</param>
    private void Inject(Dictionary<string, (double, MatchKind)> best, string typed, GTerm gt, TopSources top, bool lazy)
    {
        var g = gt.G;
        // (1) first, so that a family member keeps the « sounds like » colour
        var lower = typed.ToLowerInvariant();
        if (HomophoneFamilies.Of(lower) is { } fam && _idxOf.TryGetValue(lower, out int ti) && best.TryGetValue(_e[ti].Ortho, out var typedHave))
        {
            double raw = lazy ? typedHave.Item1 : typedHave.Item1 - gt.Of(ti);
            foreach (var m in fam)
            {
                if (m == lower || !HomophoneFamilies.RuleMayChoose(m) || !_idxOf.TryGetValue(m, out int mi) || gt.Agrees(mi) <= 0) continue;
                Put(mi, raw - InjectGap + gt.Of(mi), MatchKind.Phonetic);
            }
        }

        List<(int F, double Freq)>? cands = null;
        for (int s = 0; s < top.Count; s++)
        {
            int src = top.Idx[s];
            if (!best.TryGetValue(_e[src].Ortho, out var have)) continue;   // a pack name took its place
            // a candidate that agrees already is the form wanted: it lends nothing (« mangé » after « il a » adds no « mangées »)
            if (gt.Agrees(src) > 0) continue;
            if (have.Item2 == MatchKind.Prefix && !string.Equals(_e[src].OrthoLower, lower, StringComparison.Ordinal)) continue;
            double srcScore = lazy ? have.Item1 : have.Item1 - gt.Of(src);   // the source as scored without its own (negative) term
            var lem = g.LemmasOf(src);
            char first = FirstLetter(_e[src].OrthoLower);
            cands?.Clear();
            for (int k = 0; k < lem.Length; k++)
            {
                bool seen = false;
                for (int j = 0; j < k; j++) if (lem[j] == lem[k]) { seen = true; break; }
                if (seen) continue;
                // the source's classes for this lemma: a verb lends verb forms, a noun / adjective lends nouns / adjectives
                int classes = 0;
                var sb = g.BitsOf(src);
                for (int j = 0; j < lem.Length; j++) if (lem[j] == lem[k]) classes |= GTerm.PosClass(sb[j]);
                if (classes == 0) continue;
                foreach (int f in g.FormsOfLemma(lem[k]))
                {
                    if (f == src || FirstLetter(_e[f].OrthoLower) != first || !Injectable(f) || gt.AgreesAs(f, lem[k], classes) <= 0) continue;
                    cands ??= new List<(int, double)>(8);
                    bool dup = false;
                    foreach (var c in cands) if (c.F == f) { dup = true; break; }
                    if (!dup) cands.Add((f, _e[f].Freq));
                }
            }
            if (cands is null || cands.Count == 0) continue;
            cands.Sort((a, b) => a.Freq != b.Freq ? b.Freq.CompareTo(a.Freq) : a.F.CompareTo(b.F));
            for (int r = 0; r < cands.Count && r < InjectPerSource; r++)
            {
                int f = cands[r].F;
                // the most frequent first among forms put at the same score (a tie would go to the shorter one)
                Put(f, srcScore - InjectGap + gt.Of(f) - r * 1e-6, _phon[f] != 0 && _phon[f] == _phon[src] ? MatchKind.Phonetic : MatchKind.Edit);
            }
        }

        // add the form, or lift it when it is a candidate already with a lower score (a literal prefix keeps its colour)
        void Put(int f, double score, MatchKind kind)
        {
            var w = _e[f].Ortho;
            if (lazy) score -= gt.Of(f);   // stored as the passes store it: the ranking adds the term
            if (!best.TryGetValue(w, out var there)) { best[w] = (score, kind); return; }
            var k2 = there.Item2 == MatchKind.Prefix ? MatchKind.Prefix : there.Item2 == MatchKind.Edit ? kind : there.Item2;
            if (score > there.Item1 || k2 != there.Item2) best[w] = (Math.Max(score, there.Item1), k2);
        }

        // the first letter without its accent (« écrit » / « écrire » / « ecrit »)
        static char FirstLetter(string lower) => lower.Length == 0 ? '\0' : lower[0] switch
        {
            'à' or 'â' or 'ä' => 'a', 'é' or 'è' or 'ê' or 'ë' => 'e', 'î' or 'ï' => 'i', 'ô' or 'ö' or 'œ' => 'o',
            'ù' or 'û' or 'ü' => 'u', 'ç' => 'c', var c => c,
        };
    }

    /// <summary>A lexicon form the bar may receive as an added agreeing form: not an elision or noise form, and a form of
    /// the spelling reference (never « yeah »).</summary>
    private bool Injectable(int i) =>
        !Elision.IsBare(_e[i].Ortho) && !Elision.IsNoise(_e[i].Ortho) && !(_unlisted.Count > 0 && _unlisted.Contains(_e[i].OrthoLower));

    /// <summary>
    /// Plan 11 (speed): the candidates in final order (score with its term descending, then — <paramref name="byLength"/> —
    /// the shorter word, then the candidates' order), computing the term (<paramref name="adj"/>, never positive) only
    /// while a candidate could still enter the best <paramref name="m"/>: once a score without its term is below the m-th
    /// score with its term, no later one can. The same order as a full sort of every candidate's final score.
    /// </summary>
    private static List<(string Key, double Score, MatchKind Kind)> LazyTop(IReadOnlyList<(string Key, double Base, MatchKind Kind)> all,
                                                                        int m, Func<string, double, double> adj, bool byLength)
    {
        int n = all.Count;
        var kept = new List<(string Key, double Score, MatchKind Kind, int Order)>(m == int.MaxValue ? n : Math.Min(n, m * 4));
        if (m == int.MaxValue)
            for (int i = 0; i < n; i++) kept.Add((all[i].Key, adj(all[i].Key, all[i].Base), all[i].Kind, i));
        else
        {
            // a heap on the score without the term (highest first, then the candidates' order): only the few best are taken out
            var heap = new PriorityQueue<int, (double, int)>(n);
            for (int i = 0; i < n; i++) heap.Enqueue(i, (-all[i].Base, i));
            var lows = new List<double>(m + 1);   // the best m final scores so far, ascending
            while (heap.TryDequeue(out int i, out _))
            {
                var a = all[i];
                if (lows.Count >= m && a.Base < lows[0]) break;
                double sc = adj(a.Key, a.Base);
                kept.Add((a.Key, sc, a.Kind, i));
                int at = lows.BinarySearch(sc);
                if (at < 0) at = ~at;
                lows.Insert(at, sc);
                if (lows.Count > m) lows.RemoveAt(0);
            }
        }
        kept.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score)
                          : byLength && a.Key.Length != b.Key.Length ? a.Key.Length.CompareTo(b.Key.Length) : a.Order.CompareTo(b.Order));
        var o = new List<(string, double, MatchKind)>(kept.Count);
        foreach (var x in kept) o.Add((x.Key, x.Score, x.Kind));
        return o;
    }

    /// <summary>Pure next-word prediction from context only (the grey ghost word a keyboard shows).</summary>
    public IReadOnlyList<Suggestion> PredictNext(IReadOnlyList<string>? context, int k = 8)
    {
        var (c1, c2, c3) = PrevWords(context);
        var ctx = ContextMaps(c1, c2, c3);
        if (ctx.Bi.Map is null && ctx.Tri.Map is null && ctx.Four.Map is null) return Array.Empty<Suggestion>();
        double eb = ElisionBonus(c1);
        var gt = GrammarTerm(c1, c2, c3);   // plan 11: the agreement term on the successors (« les » → plurals first)
        bool lazy = gt.On && AgreeBonus <= 0;   // as in Predict: the term (<= 0) only for the few best successors
        var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Add(string w)
        {
            bool known = _idxOf.TryGetValue(w, out var i);
            // review 10D-1 (a correctness fix, grammar or not): a corpus spelling that is no lexicon form but the accent
            // variant of one (« ést », « etait ») is never proposed as the next word; an unknown word that is no such variant
            // (a name) still may be. The lexicon forms left out of the spelling reference stay: they hold the lowercase names
            // the bar shows after « en », « à » (« amérique », « berlin », « york »: removing them cost 91 words of KSS)
            if (!known && _strip.ContainsKey(PhoneticKey.StripLower(w))) return;
            var s = Lm(ctx, w, known ? _uni[i] : UniFloor) + (eb != 0 && _elides.Contains(w) ? eb : 0);
            if (gt.On && !lazy && known) s += gt.Of(i);
            if (!best.TryGetValue(w, out var c) || s > c) best[w] = s;
        }
        if (ctx.Four.Map is not null) foreach (var w in ctx.Four.Map.Keys) Add(w);
        if (ctx.Tri.Map is not null) foreach (var w in ctx.Tri.Map.Keys) Add(w);
        if (ctx.Bi.Map is not null) foreach (var w in ctx.Bi.Map.Keys) Add(w);
        if (lazy)
        {
            var b2 = new List<(string, double, MatchKind)>(best.Count);
            foreach (var (w, v) in best) b2.Add((w, v, MatchKind.Prefix));
            return LazyTop(b2, k, (w, sc) => _idxOf.TryGetValue(w, out var ix) ? sc + gt.Of(ix) : sc, byLength: false)
                   .Take(k).Select(x => new Suggestion(x.Key, x.Score, MatchKind.Prefix)).ToArray();
        }
        return best.OrderByDescending(x => x.Value).Take(k)
                   .Select(x => new Suggestion(x.Key, x.Value, MatchKind.Prefix)).ToArray();
    }

    // ---- stupid-backoff language model: WLm * log10( P(word | up to 3 previous words) ) ----
    // backs off 4-gram -> trigram -> bigram -> unigram, discounting by Alpha per level.
    /// <param name="add">plan 10A: the personal list's unigram share for this word (0 = none: the 0.7 score exactly).</param>
    private double Lm(Ctx ctx, string word, double uni, double add = 0)
    {
        double s;
        if (ctx.Four.Map is not null && ctx.Four.Total > 0 && ctx.Four.Map.TryGetValue(word, out var qc))
            s = qc / (double)ctx.Four.Total;
        else if (ctx.Tri.Map is not null && ctx.Tri.Total > 0 && ctx.Tri.Map.TryGetValue(word, out var tc))
            s = Alpha * tc / (double)ctx.Tri.Total;
        else if (ctx.Bi.Map is not null && ctx.Bi.Total > 0 && ctx.Bi.Map.TryGetValue(word, out var bc))
            s = Alpha * Alpha * bc / (double)ctx.Bi.Total;
        else
            s = Alpha * Alpha * Alpha * uni;
        // a personal word is at least as likely as a word of unigram probability uni + its list share (never lower
        // than its own n-gram estimate): the share competes at the unigram level, under any context prediction
        if (add != 0) s = Math.Max(s, Alpha * Alpha * Alpha * (uni + add));
        return WLm * Math.Log10(s + 1e-12);
    }

    /// <summary>log10 of <see cref="ElisionBoost"/> (scaled like the LM) when the last context word is a bare
    /// elision (<c>l</c>, <c>qu</c>…), else 0.</summary>
    private double ElisionBonus(string? p1)
    {
        if (ElisionBoost == 1.0) return 0;
        return Elision.IsBare(p1) ? WLm * Math.Log10(ElisionBoost) : 0;
    }

    private Ctx ContextMaps(string? p1, string? p2, string? p3)
    {
        var bi = p1 is null ? default : Successors1(p1);
        var tri = (p1 is null || p2 is null) ? default : Successors2(p2, p1);
        var four = (!UseFourgrams || p1 is null || p2 is null || p3 is null) ? default : Successors3(p3, p2, p1);
        return new Ctx(bi, tri, four);
    }

    private (string?, string?, string?) PrevWords(IReadOnlyList<string>? context)
    {
        if (context is null || context.Count == 0) return (null, null, null);
        var toks = Tok.Matches(string.Join(" ", context).ToLowerInvariant());
        if (toks.Count == 0) return (null, null, null);
        // context tokens folded like the n-gram tables (« cœur » → « coeur », TokenFold); the typed prefix is not
        return (TokenFold.Fold(toks[^1].Value),
                toks.Count >= 2 ? TokenFold.Fold(toks[^2].Value) : null,
                toks.Count >= 3 ? TokenFold.Fold(toks[^3].Value) : null);
    }

    private Ngram Successors1(string prev)
    {
        if (!_hasBi) return default;
        if (_biCache.TryGetValue(prev, out var hit)) return hit;
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); long tot = 0;
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT word, c, (SELECT SUM(c) FROM bigrams WHERE prev=$p) FROM bigrams WHERE prev=$p ORDER BY c DESC LIMIT 3000";
        cmd.Parameters.AddWithValue("$p", prev);
        using (var r = cmd.ExecuteReader()) while (r.Read()) { d[r.GetString(0)] = r.GetInt32(1); tot = r.GetInt64(2); }
        return _biCache[prev] = new Ngram(d, tot);
    }

    private Ngram Successors2(string p2, string p1)
    {
        if (!_hasTri) return default;
        var key = p2 + "\u0001" + p1;
        if (_triCache.TryGetValue(key, out var hit)) return hit;
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); long tot = 0;
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT word, c, (SELECT SUM(c) FROM trigrams WHERE p2=$a AND p1=$b) FROM trigrams WHERE p2=$a AND p1=$b ORDER BY c DESC LIMIT 2000";
        cmd.Parameters.AddWithValue("$a", p2); cmd.Parameters.AddWithValue("$b", p1);
        using (var r = cmd.ExecuteReader()) while (r.Read()) { d[r.GetString(0)] = r.GetInt32(1); tot = r.GetInt64(2); }
        return _triCache[key] = new Ngram(d, tot);
    }

    private Ngram Successors3(string p3, string p2, string p1)
    {
        if (!_hasFour) return default;
        var key = p3 + "\u0001" + p2 + "\u0001" + p1;
        if (_fourCache.TryGetValue(key, out var hit)) return hit;
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); long tot = 0;
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT word, c, (SELECT SUM(c) FROM fourgrams WHERE p3=$a AND p2=$b AND p1=$c) FROM fourgrams WHERE p3=$a AND p2=$b AND p1=$c ORDER BY c DESC LIMIT 1000";
        cmd.Parameters.AddWithValue("$a", p3); cmd.Parameters.AddWithValue("$b", p2); cmd.Parameters.AddWithValue("$c", p1);
        using (var r = cmd.ExecuteReader()) while (r.Read()) { d[r.GetString(0)] = r.GetInt32(1); tot = r.GetInt64(2); }
        return _fourCache[key] = new Ngram(d, tot);
    }

    private static IEnumerable<string> Edits1(string w)
    {
        const string A = "abcdefghijklmnopqrstuvwxyzàâäçéèêëîïôöùûüœ";
        int n = w.Length;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < n - 1; i++) { var s = Swap(w, i); if (seen.Add(s)) yield return s; }
        for (int i = 0; i < n; i++) { var s = w.Remove(i, 1); if (seen.Add(s)) yield return s; }
        for (int i = 0; i < n; i++) foreach (var c in A) { var s = w[..i] + c + w[(i + 1)..]; if (seen.Add(s)) yield return s; }
        for (int i = 0; i <= n; i++) foreach (var c in A) { var s = w[..i] + c + w[i..]; if (seen.Add(s)) yield return s; }
    }
    private static string Swap(string w, int i) { var a = w.ToCharArray(); (a[i], a[i + 1]) = (a[i + 1], a[i]); return new string(a); }

    private static void Consider(Dictionary<string, (double, MatchKind)> best, string word, double score, MatchKind kind)
    { if (!best.TryGetValue(word, out var cur) || score > cur.Item1) best[word] = (score, kind); }

    internal static IEnumerable<int> PrefixRange(string[] keys, string prefix)
    { int lo = LowerBound(keys, prefix); for (int i = lo; i < keys.Length && keys[i].StartsWith(prefix, StringComparison.Ordinal); i++) yield return i; }

    internal static int LowerBound(string[] keys, string prefix)
    { int lo = 0, hi = keys.Length; while (lo < hi) { int m = (lo + hi) >> 1; if (string.CompareOrdinal(keys[m], prefix) < 0) lo = m + 1; else hi = m; } return lo; }

    private static bool TableExists(SqliteConnection con, string name)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return cmd.ExecuteScalar() is not null;
    }

    public void Dispose() => _con.Dispose();
}
