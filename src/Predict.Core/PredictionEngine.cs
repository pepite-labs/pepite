using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Predict.Core;

public enum MatchKind { Prefix, Phonetic, Edit }

/// <summary>0.10.2 (plan exact-typed-word): how the word typed in full and spelled right is placed in the bar
/// (<see cref="PredictionEngine.ExactTyped"/>).</summary>
public enum ExactTypedMode { Off, Present, First }

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
    private volatile int _tol = (int)SoundPairs.ChJ;
    /// <summary>0.11.0 (plan dys-sound-tolerance): the voicing pairs the pupil confuses. The typed sound key is also looked up
    /// with those symbols swapped (<see cref="PhoneticKey.Variants"/>), at a penalty of <see cref="WTolerant"/>: « le chard »
    /// → jardin, a « same sound » (green) candidate ranked after the exact sound-alikes; <see cref="Check"/> gives « chardin »
    /// ≈ jardin in a second tier. One int write: the app may change it from the UI thread. Default ch/j (owner 2026-11-26, the
    /// Phase 1 gates met); None = the 0.10.2 results.</summary>
    public SoundPairs Tolerance { get => (SoundPairs)_tol; set => _tol = (int)value; }
    /// <summary>0.11.0: the penalty (log10 scale, like <see cref="WEdit"/>) of a candidate found only through a confused sound
    /// (owner Q-A: a fixed penalty — a confusion counts as this many tens of times less likely, so it can still pass a rare
    /// exact sound-alike when the words before make it far more likely). 1.5: the only value of 0.5 / 1.0 / 1.5 that leaves
    /// every curated set unchanged (evaluation.md § Sound confusions).</summary>
    public double WTolerant = 1.5;
    /// <summary>0.11.0: the typed sound key needs this many symbols before the tolerant pass of <see cref="Predict"/> (« je »
    /// / « chez »: 2 symbols get none).</summary>
    public int TolerantFrom = 3;
    /// <summary>0.11.0: the whole sound key of a finished word needs this many symbols for the tolerant tier of
    /// <see cref="Check"/>.</summary>
    public int TolerantCheckFrom = 2;
    /// <summary>0.11.0: at most this many confused symbols swapped in one variant (<see cref="PhoneticKey.Variants"/>).</summary>
    public int TolerantMaxSwaps = 2;
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
    /// <summary>0.10.2 (owner 2026-11-17): with no capital to tell a name (typed in lowercase, or at a sentence start), the
    /// « recognised » tier is searched too from this many typed letters (« rodol », « Rodol » → Rodolphe); 99 = the 0.10.1
    /// behaviour (that tier after a capital inside a sentence only).</summary>
    public int NamesRecognisedFrom = 4;
    /// <summary>0.10.2: in that search a recognised name needs at least this rang (frequency ≥ pack cap × floor; 0.02 ≈ 300
    /// bearers or more): the very rare ones still need a capital, so they never push the corrections out of the bar.
    /// Restricted figures are never offered without a capital (D30).</summary>
    public double NamesRecognisedFloor = 0.02;
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
                             string? spellingPath, Func<List<BareCounts>> bareCounts, Func<GluedCounts> gluedCounts,
                             Dictionary<string, (string Phon, double Freq)> exprPhon)
    {
        _e = entries; _phon = phon; _con = con;
        _noExcluded = new bool[entries.Length];
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

        // 0.10.2: last, so that the counts read meanwhile on a second connection are there by now (see Load)
        var counts = bareCounts();
        _bareEst = BareEstimates(counts, calibratedOnly: true);
        ApplyElisionUnigram();
        (_bareNext, _bareIdx, _bareAposIdx) = BareSuccessors(counts);
        // 0.11.0 (glued words follow-up): « est » after c' s' n' qu', the subject + clitic counts, the expressions
        _estIdx = _idxOf.TryGetValue("est", out var ei) ? ei : -1;
        _estForm = new bool[Elision.SplitForms.Count];
        for (int b = 0; b < _estForm.Length; b++) _estForm[b] = Elision.EstForms.Contains(Elision.SplitForms[b]);
        BuildExpressions(exprPhon);
        // the counts the new kinds need arrive on another connection after the load; the first query that needs them waits
        _glued = new Lazy<GluedState>(() =>
        {
            try { return ResolveGlued(gluedCounts()); }
            catch (Exception ex)
            {   // a read that failed twice (both connections): the new kinds find nothing, every query still works
                GluedProblem = $"glued words follow-up counts unreadable ({ex.GetType().Name}); three words and expressions off";
                return ResolveGlued(new GluedCounts(new Dictionary<string, Ngram>(StringComparer.Ordinal),
                                                    new Dictionary<string, long>(StringComparer.Ordinal), Array.Empty<long>(), Array.Empty<long>()));
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    // ---- 0.10.2: the context-free prior of the elided forms (plan « elided forms from the first letter »)
    /// <summary>One bare form's calibration: its entries, its Lexique frequency (per million), the estimate, and the
    /// entries' unigrams as loaded (restored exactly when the estimate does not apply).</summary>
    private readonly record struct BareEst(string Bare, string Full, int[] Entries, double Lex, double Est, double[] Uni0, bool Calibrated);
    private readonly BareEst[] _bareEst;
    private double _elisionUni = 1.0;

    /// <summary>
    /// 0.10.2: Lexique has apostrophe rows only for <c>l' d' n' s' t'</c>; the other bare forms (<c>j m c qu jusqu lorsqu
    /// puisqu</c>) are letter names or rare rows, so « j » at a sentence start never offered « j' ». At load, for each bare
    /// form without an apostrophe row, its elided frequency is estimated as <c>lexFreq(full form) × Σ c(bare → vowel /
    /// mute-h word) / Σ c(full form → ·)</c> (the share of the full form's uses the corpus writes elided; checked against
    /// Lexique's own l' d' n' s' rows), and its unigram becomes <c>(max(lex, ElisionUnigram × est) + 1) / Z</c>. The entry's
    /// <c>Freq</c> (prefix gate, spelling order) and <c>Z</c> stay as loaded; <see cref="PredictNext"/> is unaffected: its
    /// candidates come from the n-gram maps, so its language model never falls back to the unigram. 0 = the 0.10.1 behaviour exactly; tuned with <c>Predict.Eval --elision-uni</c>.
    /// </summary>
    public double ElisionUnigram
    {
        get => _elisionUni;
        set { _elisionUni = double.IsFinite(value) && value > 0 ? value : 0; ApplyElisionUnigram(); }
    }

    private void ApplyElisionUnigram()
    {
        foreach (var b in _bareEst)
        {
            double v = _elisionUni * b.Est;
            for (int k = 0; k < b.Entries.Length; k++) _uni[b.Entries[k]] = v > b.Lex ? (v + 1) / _z : b.Uni0[k];
        }
    }

    /// <summary>One line of <see cref="GetElisionEstimates"/>: <paramref name="Lexique"/> is the per-million frequency of the
    /// apostrophe row when Lexique has one (then <paramref name="Calibrated"/> is false: the engine keeps Lexique's value),
    /// else of the bare row.</summary>
    public readonly record struct ElisionEstimate(string Bare, string Full, double Lexique, double Estimate, bool Calibrated);

    /// <summary>0.10.2 (Predict.Cli --probe): the estimate of every bare form that has a full form in the
    /// lexicon, including those Lexique has an apostrophe row for (computed here on demand, not at load: a check of the
    /// estimate against Lexique).</summary>
    public IReadOnlyList<ElisionEstimate> GetElisionEstimates() =>
        BareEstimates(ReadBareCounts(_con, all: true), calibratedOnly: false)
            .Select(b => new ElisionEstimate(b.Bare, b.Full, b.Lex, b.Est, b.Calibrated)).ToArray();

    /// <summary>The corpus counts of one bare form: its bigram successors and the total count of its full form's successors.
    /// <paramref name="ApostropheRow"/>: the database has the form with its apostrophe (« l' »).</summary>
    private sealed record BareCounts(string Bare, string Full, bool ApostropheRow, List<(string Word, long C)> Next, long FullTotal);

    /// <summary>The counts the calibration needs (<paramref name="all"/> false: the full form's total only for the forms
    /// without an apostrophe row) and, 0.10.2 (glued words), the bigram successors of all 12 bare forms (the split pass's
    /// attested filter and language model). About 20 000 bigram rows read through the <c>prev</c> index: <see cref="Load"/>
    /// reads them on a second connection while the engine builds its indexes, so the load does not grow by that much.</summary>
    private static List<BareCounts> ReadBareCounts(SqliteConnection con, bool all)
    {
        var o = new List<BareCounts>();
        if (!TableExists(con, "bigrams")) return o;
        foreach (var (bare, full) in Elision.FullForm)
        {
            bool apostropheRow;
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT 1 FROM words WHERE ortho=$o LIMIT 1";
                cmd.Parameters.AddWithValue("$o", bare + "'");
                apostropheRow = cmd.ExecuteScalar() is not null;
            }
            var next = new List<(string, long)>();
            long total = 0;
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT word, c FROM bigrams WHERE prev=$p";
                cmd.Parameters.AddWithValue("$p", bare);
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!r.IsDBNull(0) && !r.IsDBNull(1)) next.Add((r.GetString(0), r.GetInt64(1)));
            }
            if (all || !apostropheRow)
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = "SELECT SUM(c) FROM bigrams WHERE prev=$p";
                    cmd.Parameters.AddWithValue("$p", full);
                    if (cmd.ExecuteScalar() is long t) total = t;
                }
            o.Add(new BareCounts(bare, full, apostropheRow, next, total));
        }
        return o;
    }

    private static List<BareCounts> ReadBareCounts(string dbPath, Func<string, SqliteConnection>? openSecond)
    {
        using var con = OpenSecond(dbPath, openSecond);
        return ReadBareCounts(con, all: false);
    }

    /// <summary>0.11.0: the glued words follow-up's counts, read on another connection once the bare forms' are in (read with
    /// them they made the load 0.3–0.4 s longer than the indexes they hide behind).</summary>
    private static GluedCounts ReadGluedCounts(string dbPath, Func<string, SqliteConnection>? openSecond)
    {
        using var con = OpenSecond(dbPath, openSecond);
        return ReadGluedCounts(con);
    }

    private static GluedCounts ReadGluedCounts(SqliteConnection con)
    {
        var (s3, subj) = ReadSplit3Counts(con);
        var (ec, ef) = ReadExprCounts(con);
        return new GluedCounts(s3, subj, ec, ef);
    }

    private static SqliteConnection OpenSecond(string dbPath, Func<string, SqliteConnection>? openSecond)
    {
        var con = openSecond?.Invoke(dbPath) ?? new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        if (con.State != System.Data.ConnectionState.Open) con.Open();
        return con;
    }

    /// <summary>0.11.0 (plan glued-words-followup.md): the trigram successors of each subject + clitic (« je t » → aime 436,
    /// « il m » → a 2 276; 45 contexts, about 2 500 rows), the bigram total of each subject, and per expression row its
    /// n-gram count and the bigram total of its first token.</summary>
    private sealed record GluedCounts(Dictionary<string, Ngram> S3, Dictionary<string, long> SubjectTotal, long[] ExprCount, long[] ExprFirstTotal);

    private static long BiTotal(SqliteConnection con, Dictionary<string, long> totals, string p)
    {
        if (totals.TryGetValue(p, out var t)) return t;
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT SUM(c) FROM bigrams WHERE prev=$p";
        cmd.Parameters.AddWithValue("$p", p);
        return totals[p] = cmd.ExecuteScalar() is long v ? v : 0;
    }

    /// <summary>0.11.0 (C): the trigram successors of each subject + clitic and the bigram total of each subject.</summary>
    private static (Dictionary<string, Ngram> S3, Dictionary<string, long> SubjectTotal) ReadSplit3Counts(SqliteConnection con)
    {
        var s3 = new Dictionary<string, Ngram>(StringComparer.Ordinal);
        var subj = new Dictionary<string, long>(StringComparer.Ordinal);
        if (TableExists(con, "bigrams")) foreach (var s in Elision.Subjects) subj[s] = BiTotal(con, subj, s);
        if (TableExists(con, "trigrams"))
            foreach (var s in Elision.Subjects)
                foreach (var b in Elision.Split3Clitics)
                {
                    var bare = Elision.SplitForms[b];
                    var map = new Dictionary<string, int>(StringComparer.Ordinal);
                    long total = 0;
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = "SELECT word, c FROM trigrams WHERE p2=$a AND p1=$b";
                    cmd.Parameters.AddWithValue("$a", s); cmd.Parameters.AddWithValue("$b", bare);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                        if (!r.IsDBNull(0) && !r.IsDBNull(1)) { long n = r.GetInt64(1); total += n; map[r.GetString(0)] = (int)Math.Min(int.MaxValue, n); }
                    map.TrimExcess();
                    s3[S3Key(s, bare)] = new Ngram(map, total);
                }
        return (s3, subj);
    }

    /// <summary>0.11.0 (B): per expression row its n-gram count and the bigram total of its first token.</summary>
    private static (long[] Count, long[] FirstTotal) ReadExprCounts(SqliteConnection con)
    {
        var rows = GluedExpressions.Rows;
        var ec = new long[rows.Count];
        var ef = new long[rows.Count];
        bool bi = TableExists(con, "bigrams"), tri = TableExists(con, "trigrams"), four = TableExists(con, "fourgrams");
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        long Count(string sql, string[] toks)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            for (int k = 0; k < toks.Length; k++) cmd.Parameters.AddWithValue("$t" + k, toks[k]);
            return cmd.ExecuteScalar() is long v ? v : 0;
        }
        for (int i = 0; i < rows.Count; i++)
        {
            var t = GluedExpressions.Tokens(rows[i].Written);
            ec[i] = t.Length switch
            {
                2 when bi => Count("SELECT SUM(c) FROM bigrams WHERE prev=$t0 AND word=$t1", t),
                3 when tri => Count("SELECT SUM(c) FROM trigrams WHERE p2=$t0 AND p1=$t1 AND word=$t2", t),
                4 when four => Count("SELECT SUM(c) FROM fourgrams WHERE p3=$t0 AND p2=$t1 AND p1=$t2 AND word=$t3", t),
                _ => 0,
            };
            ef[i] = bi ? BiTotal(con, totals, t[0]) : 0;
        }
        return (ec, ef);
    }

    private static string S3Key(string subject, string bare) => subject + "\u0001" + bare;

    // ---- 0.10.2: glued words (plan glued-words.md), the bare forms' successors kept from the load
    /// <summary>Per form of <see cref="Elision.SplitForms"/>: its bigram successors that are lexicon forms taking an elision
    /// (keys: the lexicon's own lowercase strings), and the count of all its successors (the bigram level of the split's
    /// language model, and the attested filter: a glued word splits only into a pair the corpus wrote).</summary>
    private readonly Ngram[] _bareNext;
    /// <summary>Per split form: the lexicon entries of the bare form and of its apostrophe row (−1 = none).</summary>
    private readonly int[] _bareIdx, _bareAposIdx;

    private (Ngram[] Next, int[] Idx, int[] Apos) BareSuccessors(List<BareCounts> counts)
    {
        var forms = Elision.SplitForms;
        var next = new Ngram[forms.Count];
        var idx = new int[forms.Count];
        var apos = new int[forms.Count];
        for (int b = 0; b < forms.Count; b++)
        {
            idx[b] = _idxOf.TryGetValue(forms[b], out var i) ? i : -1;
            apos[b] = _idxOf.TryGetValue(forms[b] + "'", out var a) ? a : -1;
            next[b] = new Ngram(new Dictionary<string, int>(0, StringComparer.Ordinal), 0);
        }
        foreach (var c in counts)
        {
            int b = 0;
            while (b < forms.Count && !string.Equals(forms[b], c.Bare, StringComparison.Ordinal)) b++;
            if (b == forms.Count) continue;
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            long total = 0;
            foreach (var (w, n) in c.Next)
            {
                total += n;
                if (_idxOf.TryGetValue(w, out var i) && _e[i].Elides && string.Equals(_e[i].OrthoLower, w, StringComparison.Ordinal))
                    map[_e[i].OrthoLower] = (int)Math.Min(int.MaxValue, n);
            }
            map.TrimExcess();
            next[b] = new Ngram(map, total);
        }
        return (next, idx, apos);
    }

    /// <summary>The context-free unigram probability of split form <paramref name="b"/>: its bare row or its apostrophe row,
    /// whichever is higher (the calibrated estimate for j' m' c' qu'…, Lexique's l' d' n' s' t').</summary>
    private double BareUni(int b)
    {
        double u = 0;
        if (_bareIdx[b] >= 0) u = _uni[_bareIdx[b]];
        if (_bareAposIdx[b] >= 0) u = Math.Max(u, _uni[_bareAposIdx[b]]);
        return u;
    }

    /// <summary>0.10.2 (Predict.Cli --probe): per split form, the number of successors kept and their total count.</summary>
    public IReadOnlyList<(string Bare, int Successors, long Total, double PerMillion)> GetBareSuccessors()
    {
        var o = new List<(string, int, long, double)>(_bareNext.Length);
        for (int b = 0; b < _bareNext.Length; b++)
            o.Add((Elision.SplitForms[b], _bareNext[b].Map?.Count ?? 0, _bareNext[b].Total, BareUni(b) * _z - 1));
        return o;
    }

    private BareEst[] BareEstimates(List<BareCounts> counts, bool calibratedOnly)
    {
        var o = new List<BareEst>();
        foreach (var c in counts)
        {
            if (!_idxOf.TryGetValue(c.Full, out var fi)) continue;
            bool apostropheRow = c.ApostropheRow || _idxOf.ContainsKey(c.Bare + "'");
            if (calibratedOnly && apostropheRow) continue;
            // the lexicon forms are unique in lowercase: one entry at most
            int[] entries = _idxOf.TryGetValue(c.Bare, out var bi) ? new[] { bi } : Array.Empty<int>();
            if (entries.Length == 0 && !apostropheRow) continue;
            long elided = 0;
            foreach (var (w, n) in c.Next) if (_elides.Contains(w)) elided += n;   // before a vowel / mute-h word: an elision
            double est = c.FullTotal > 0 ? (Math.Pow(10, _e[fi].Freq) - 1) * elided / c.FullTotal : 0;
            double lex = apostropheRow && _idxOf.TryGetValue(c.Bare + "'", out var ai) ? Math.Pow(10, _e[ai].Freq) - 1
                       : entries.Length == 0 ? 0 : Math.Pow(10, _e[entries[0]].Freq) - 1;
            o.Add(new BareEst(c.Bare, c.Full, entries, lex, est, entries.Select(i => _uni[i]).ToArray(), !apostropheRow));
        }
        return o.ToArray();
    }

    /// <summary>The spelling reference file looked for next to a database (publication cleanup, 2026-11-16).</summary>
    public const string SpellingFileName = "spelling.sqlite";

    /// <param name="dbPath">prediction.sqlite.</param>
    /// <param name="spellingPath">the spelling reference; null = <see cref="SpellingFileName"/> next to
    /// <paramref name="dbPath"/> when it exists, else the tables of the database itself.</param>
    /// <param name="exclusionsPath">0.11.0: the « mots exclus » file; null = <c>exclusions\fr.txt</c> next to
    /// <paramref name="dbPath"/> when it exists, else none (<see cref="ExcludedWords.Empty"/>). Set before the engine is
    /// returned, so no query ever runs without it; an unreadable file gives none and <see cref="ExclusionsProblem"/>.</param>
    public static PredictionEngine Load(string dbPath, string? spellingPath = null, string? exclusionsPath = null)
    {
        var eng = Load(dbPath, spellingPath, (Func<string, SqliteConnection>?)null);
        eng.LoadExclusions(exclusionsPath ?? ExclusionsNextTo(dbPath));
        return eng;
    }

    /// <summary>0.11.0: <c>exclusions\fr.txt</c> next to a database, or null when there is none.</summary>
    public static string? ExclusionsNextTo(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        var p = dir is null ? null : Path.Combine(dir, ExcludedWords.FolderName, ExcludedWords.FileName);
        return p is not null && File.Exists(p) ? p : null;
    }

    private void LoadExclusions(string? path)
    {
        if (path is null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { _excluded = ExcludedWords.FromFile(path, this); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _excluded = ExcludedWords.Empty;
            ExclusionsProblem = $"{Path.GetFileName(path)} unreadable ({ex.GetType().Name}); no word is excluded";
        }
        ExclusionsLoadMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <param name="openSecond">tests: opens the second connection of the elided forms' counts (null = read-only, no pool).</param>
    internal static PredictionEngine Load(string dbPath, string? spellingPath, Func<string, SqliteConnection>? openSecond)
    {
        var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        con.Open();
        var entries = new List<Entry>(140_000);
        var folds = new List<(string, int)>(260_000);
        var phons = new List<int>(140_000);
        // 0.11.0: the Lexique pronunciation of the expressions' tokens (their sound keys, BuildExpressions)
        var exprPhon = new Dictionary<string, (string Phon, double Freq)>(StringComparer.Ordinal);
        var exprTokens = GluedExpressions.AllTokens;
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
                if (exprTokens.Contains(ortho) && (!exprPhon.TryGetValue(ortho, out var ep) || freq > ep.Freq)) exprPhon[ortho] = (phon, freq);
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
        // 0.10.2: the elided forms' counts on a second connection while the engine builds its indexes (see ReadBareCounts)
        var countsTask = Task.Run(() => ReadBareCounts(dbPath, openSecond));
        // 0.11.0: the glued words follow-up's counts, on another connection once those are in (not delaying the load)
        var gluedTask = countsTask.ContinueWith(_ => ReadGluedCounts(dbPath, openSecond), TaskScheduler.Default);
        List<BareCounts> Counts()
        {
            try { return countsTask.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            { return ReadBareCounts(con, all: false); }   // the second connection failed: the same counts on the first one
        }
        GluedCounts Glued()
        {
            try { return gluedTask.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            { return ReadGluedCounts(con); }   // on the first connection (the thread of the query that needs them)
        }
        return new PredictionEngine(entries.ToArray(), phons.ToArray(), folds, con, spellingPath, Counts, Glued, exprPhon);
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

    // ---- 0.11.0 (plan offensive-words): the « mots exclus »
    private volatile ExcludedWords _excluded = ExcludedWords.Empty;
    private readonly bool[] _noExcluded;
    private long _excludedLate;

    /// <summary>0.11.0: the words never offered (bar, next word, ≈ list); a typed one stays ✓ Known. Every lexicon pass skips
    /// an excluded entry before scoring it, every string source (personal words, « Mes fautes » targets, names, the n-gram
    /// successors) checks <see cref="ExcludedWords.IsExcluded"/>, and a safety net on each public output drops what got
    /// through (<see cref="ExcludedLate"/>). One snapshot per query; the app swaps a new one in one reference write (the
    /// grammar's forms once it is loaded; Phase B the teacher's words). <see cref="ExcludedWords.Empty"/> = the 0.10.2
    /// results.</summary>
    public ExcludedWords Excluded { get => _excluded; set => _excluded = value ?? ExcludedWords.Empty; }

    /// <summary>0.11.0: the one place that changes the snapshot when several parts change from different threads (the
    /// grammar forms from the grammar worker, Phase B the teacher's words from the UI): <paramref name="change"/> gets the
    /// current snapshot and returns the new one (<see cref="ExcludedWords.WithGrammarForms"/>, <see cref="ExcludedWords.WithExtra"/>
    /// keep the other parts); retried until no other swap happened in between, so a later rebuild never loses a part. Keep
    /// <paramref name="change"/> cheap: it may run more than once.</summary>
    public ExcludedWords UpdateExcluded(Func<ExcludedWords, ExcludedWords> change)
    {
        while (true)
        {
            var cur = _excluded;
            var next = change(cur) ?? ExcludedWords.Empty;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _excluded, next, cur), cur)) return next;
        }
    }
    /// <summary>0.11.0: why the exclusions file could not be read (null when it was, or when there is none).</summary>
    public string? ExclusionsProblem { get; private set; }
    /// <summary>0.11.0: the time <see cref="Load(string, string?, string?)"/> took to read the exclusions file (ms; the load log).</summary>
    public double ExclusionsLoadMs { get; private set; }
    /// <summary>0.11.0: the excluded words the safety net dropped from a final output — a source that forgot to filter. The
    /// tests and the probe assert it stays 0.</summary>
    public long ExcludedLate => Interlocked.Read(ref _excludedLate);

    /// <summary>The snapshot's entry flags, or all false when it was built for another engine (the string checks still
    /// apply).</summary>
    private bool[] Xe(ExcludedWords x) => x.Entry.Length == _e.Length ? x.Entry : _noExcluded;

    /// <summary>0.11.0: the safety net of <see cref="Predict"/> and <see cref="PredictNext"/>.</summary>
    private IReadOnlyList<Suggestion> Net(IReadOnlyList<Suggestion> res, ExcludedWords x)
    {
        if (x.IsEmpty) return res;
        int bad = 0;
        foreach (var s in res) if (Blocked(x, s.Word)) bad++;
        if (bad == 0) return res;
        Interlocked.Add(ref _excludedLate, bad);
        var o = new Suggestion[res.Count - bad];
        int j = 0;
        foreach (var s in res) if (!Blocked(x, s.Word)) o[j++] = s;
        return o;
    }

    /// <summary>0.11.0: an excluded word, unless it is a pack name the owner exempted (written with its capital).</summary>
    private bool Blocked(ExcludedWords x, string w) => x.IsExcluded(w) && !(x.NameExempt(w) && _names.ContainsForm(w));

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
    private GTerm GrammarTerm(string? p1, string? p2, string? p3) => GrammarTerm(p1, p2, p3, ref _expect);

    /// <param name="cache">the one-entry cache to use (0.10.2: the split pass has its own, so that the query's context stays
    /// cached for the next keystroke).</param>
    private GTerm GrammarTerm(string? p1, string? p2, string? p3,
                              ref (GrammarLexicon? G, string? P3, string? P2, string? P1, bool Par, bool Wide, Expect Ex) cache)
    {
        var g = _grammar;
        if (!GrammarOn || g.IsEmpty || WNominal == 0 && WVerbal == 0 || p1 is null) return default;
        var c = cache;
        Expect ex;
        if (ReferenceEquals(c.G, g) && c.Par == ParInfinitive && c.Wide == WideDisfavour && c.P1 == p1 && c.P2 == p2 && c.P3 == p3) ex = c.Ex;
        else
        {
            ex = AgreementContext.From(p3, p2, p1, w => g.BitsOf(IndexOf(w)), ParInfinitive, WideDisfavour);
            cache = (g, p3, p2, p1, ParInfinitive, WideDisfavour, ex);
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
        var x = _excluded;                              // 0.11.0: a listed word typed is Known (✓), never offered
        var xe = Xe(x);
        if (KnowsSpelled(w) || u.Contains(w)) return SpellingCheck.Known;
        // owner 2026-11-15: a very rare recognised-tier pack name (« Pome », « Kan ») is checked by its sound too, so that at a
        // sentence start it may be ≈ (the indicator decides: WeakName); any other pack form is Known
        bool packOnly = nm.ContainsForm(w);
        if (packOnly && !nm.IsWeak(w)) return SpellingCheck.Known;
        // no LINQ / closure in this path (it runs on the UI thread for each finished word): plain loops, one cached comparer
        List<string>? found = null;
        List<int>? hits = null;
        int tolerant = 0;

        var strip = PhoneticKey.StripLower(w);
        int fx = u.FixKeys.Length > 0 ? LowerBound(u.FixKeys, strip) : 0;
        if (fx < u.FixKeys.Length && u.FixKeys[fx] == strip) AddFound(ref found, u.FixTargets[fx], max, x);   // « Mes fautes »
        if (_strip.TryGetValue(strip, out var si))
        {
            hits = new List<int>(si.Count);
            foreach (var i in si) if (Offerable(i, w, xe)) hits.Add(i);
            AddRanked(ref found, hits, max);
        }
        if (u.Strip.TryGetValue(strip, out var us)) foreach (var i in us) AddFound(ref found, u.Entries[i].Form, max, x);
        // plan 10C: the same letters as a pack name, but another case or accents (« emma » → Emma, « ines » → Inès)
        if (!nm.IsEmpty)
            foreach (var f in nm.SameLetters(w))
                if (f != w) AddFound(ref found, f, max, x.NameExempt(f) ? ExcludedWords.Empty : x);   // owner 2026-11-27
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
                    if (Offerable(i, w, xe) && !hits.Contains(i)) hits.Add(i);   // a word may have both keys equal
                }
                AddRanked(ref found, hits, max);
            }
            for (int p = LowerBound(u.FoldKeys, fold); p < u.FoldKeys.Length && u.FoldKeys[p] == fold; p++) AddFound(ref found, u.Entries[u.FoldIdx[p]].Form, max, x);
            // 0.10.2 (glued words): « lécole » → l'école, « jai » → j'ai (a pack name is judged by its sound only)
            if (Splits && !packOnly) CheckSplits(ref found, w, strip, fold, max, fx < u.FixKeys.Length && u.FixKeys[fx] == strip, xe);
            // 0.11.0 (plan dys-sound-tolerance, owner Q5): the whole key with the pupil's confused sounds swapped (« chardin »
            // ≈ jardin), in a second tier: it only fills what the exact candidates left of max, at the end of the list
            var tol = Tolerance;
            if (!packOnly && tol != SoundPairs.None && fold.Length >= TolerantCheckFrom && (found?.Count ?? 0) < max)
            {
                int before = found?.Count ?? 0;
                var vs = PhoneticKey.Variants(fold, tol, TolerantMaxSwaps);
                if (hits is null) hits = new List<int>(); else hits.Clear();
                for (int k = 0; k < vs.Count; k++)
                {
                    var v = vs[k];
                    for (int p = LowerBound(_fKeys, v); p < _fKeys.Length && _fKeys[p] == v; p++)
                    {
                        int i = _fIdx[p];
                        if (Offerable(i, w, xe) && !hits.Contains(i)) hits.Add(i);
                    }
                }
                AddRanked(ref found, hits, max);
                tolerant = (found?.Count ?? 0) - before;
            }
        }
        // 0.11.0: the safety net (every source above filters; a word removed here counts in ExcludedLate)
        if (found is { Count: > 0 } && !x.IsEmpty)
            for (int q = found.Count - 1; q >= 0; q--)
                if (Blocked(x, found[q]))
                {
                    Interlocked.Increment(ref _excludedLate);
                    if (q >= found.Count - tolerant) tolerant--;
                    found.RemoveAt(q);
                }
        if (packOnly) return found is { Count: > 0 } ? new SpellingCheck(SpellingStatus.SoundsLike, found, WeakName: true) : SpellingCheck.Known;
        return found is { Count: > 0 } ? new SpellingCheck(SpellingStatus.SoundsLike, found, Tolerant: tolerant) : SpellingCheck.Unknown;
    }

    /// <summary>A lexicon entry the ≈ list may offer for <paramref name="typed"/>: not excluded (0.11.0), no noise, a form of
    /// the spelling reference (0.8.3: never « yeah »), and not the typed word itself (an unlisted word is never its own
    /// correction).</summary>
    private bool Offerable(int i, string typed, bool[] xe) =>
        !xe[i] && !Elision.IsNoise(_e[i].Ortho) && !(_unlisted.Count > 0 && _unlisted.Contains(_e[i].OrthoLower)) &&
        !string.Equals(_e[i].OrthoLower, typed, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 0.10.2 (glued words plan, owner Q2): the ≈ list of a finished word ordered by the words before it, when it opens
    /// (click or F9). Each lexicon candidate scores <c>Lm(ctx, word) + grammar(ctx, word)</c> (and the elision boost after
    /// l' d' qu'…, as in <see cref="Predict"/>); a joined one (« t'aime ») the chain of <see cref="SplitPass"/> without its
    /// penalty (all are corrections here): <c>Lm(ctx, bare) + Lm(ctx ⊕ bare, word) + grammar(ctx ⊕ bare, word)</c>. After a
    /// pronoun a verb comes first, after a determiner a noun (« je tème » → t'aime, « le tème » → thème). The other candidates
    /// — a personal word, a name, and the pupil's own « Mes fautes » target for <paramref name="typed"/> — keep their places,
    /// the ranked ones fill the others, best first (stable). No context: the list as it is (<see cref="Check"/>'s order by
    /// estimated frequency). For all ≈ lists, splits on or off.
    /// 0.11.0 (plan dys-sound-tolerance, owner Q5): the last <paramref name="tolerant"/> candidates (<see cref="SpellingCheck.Tolerant"/>,
    /// the confused sounds) are ranked among themselves only, after the others: a confusion never jumps ahead of an exact
    /// sound-alike. 0 = one tier (0.10.2).
    /// </summary>
    public IReadOnlyList<string> RankCorrections(IReadOnlyList<string> candidates, IReadOnlyList<string>? context, string? typed = null,
                                                 int tolerant = 0)
    {
        if (candidates is null || candidates.Count < 2) return candidates ?? Array.Empty<string>();
        int tier2 = candidates.Count - Math.Clamp(tolerant, 0, candidates.Count);   // first index of the tolerant tier
        var (c1, c2, c3) = PrevWords(context);
        if (c1 is null) return candidates;
        var ctx = ContextMaps(c1, c2, c3);
        double eb = ElisionBonus(c1);
        var gt = GrammarTerm(c1, c2, c3);
        double lift = ChainLift(c1, c2);
        var u = _user;
        string? fix = null;
        if (typed is { Length: > 0 } && u.FixKeys.Length > 0)
        {
            var strip = PhoneticKey.StripLower(typed);
            int fx = LowerBound(u.FixKeys, strip);
            if (fx < u.FixKeys.Length && u.FixKeys[fx] == strip) fix = u.FixTargets[fx];
        }
        var slots = new List<int>(candidates.Count);
        var ranked = new List<(double Score, int Order)>(candidates.Count);
        for (int k = 0; k < candidates.Count; k++)
        {
            var c = candidates[k];
            if (fix is not null && string.Equals(c, fix, StringComparison.Ordinal)) continue;
            double? sc = null;
            int i = LexEntry(c);
            if (i >= 0 && !u.Contains(c)) sc = Lm(ctx, _e[i].OrthoLower, _uni[i]) + (_e[i].Elides ? eb : 0) + (gt.On ? gt.Of(i) : 0);
            else if (c.IndexOf(' ') > 0 && !u.Contains(c) && ChainOf(c) is { } chain)
            {
                // 0.11.0: a three-word form or an expression (« je t'aime », « il y a ») is scored as a chain, without its
                // penalty, its last word with the agreement term after the others
                sc = ChainScore(ctx, c1, c2, chain.Tok, chain.Uni);
                int n = chain.Tok.Length;
                var g3 = GrammarTerm(chain.Tok[n - 2], n >= 3 ? chain.Tok[n - 3] : c1, n >= 4 ? chain.Tok[n - 4] : n == 3 ? c1 : c2, ref _split3Expect);
                if (g3.On) sc += g3.Of(chain.Idx[n - 1]);
            }
            else if (Elision.IsJoined(c, out var bare, out var main) && LexEntry(main) is var mi && mi >= 0)
            {
                int b = 0;
                while (b < Elision.SplitForms.Count && !string.Equals(Elision.SplitForms[b], bare, StringComparison.OrdinalIgnoreCase)) b++;
                if (b < _bareNext.Length)
                {
                    var bl = Elision.SplitForms[b];
                    var after = new Ctx(_bareNext[b], Successors2(c1, bl), !UseFourgrams || c2 is null ? default : Successors3(c2, c1, bl));
                    var g2 = GrammarTerm(bl, c1, c2, ref _splitExpect);
                    sc = Lm(ctx, bl, BareUni(b)) + Lm(after, _e[mi].OrthoLower, _uni[mi]) + lift + (g2.On ? g2.Of(mi) : 0);
                }
            }
            if (sc is { } s) { slots.Add(k); ranked.Add((s, k)); }
        }
        if (ranked.Count < 2) return candidates;
        // the slots are in list order, so the exact tier's slots come first: sorting by tier first keeps each tier in its own
        ranked.Sort((a, b) => (a.Order >= tier2) != (b.Order >= tier2) ? (a.Order >= tier2 ? 1 : -1)
                              : a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Order.CompareTo(b.Order));
        var o = new string[candidates.Count];
        for (int k = 0; k < candidates.Count; k++) o[k] = candidates[k];
        for (int j = 0; j < slots.Count; j++) o[slots[j]] = candidates[ranked[j].Order];
        return o;
    }

    /// <summary>0.11.0: the tokens of a multi-word candidate (lower case, the n-gram tables' tokens) when it has no capital
    /// (a name keeps its place) and every token is a lexicon form; else null.</summary>
    private (string[] Tok, int[] Idx, double[] Uni)? ChainOf(string c)
    {
        foreach (var ch in c) if (char.IsUpper(ch)) return null;
        var m = Tok.Matches(c.ToLowerInvariant());
        if (m.Count < 2) return null;
        var t = new string[m.Count];
        var ix = new int[m.Count];
        var un = new double[m.Count];
        for (int k = 0; k < t.Length; k++)
        {
            t[k] = TokenFold.Fold(m[k].Value);
            if (!_idxOf.TryGetValue(t[k], out ix[k])) return null;
            un[k] = TokenUni(t[k], ix[k]);
        }
        return (t, ix, un);
    }

    // ---- 0.11.0 (plan negation): the missing « ne » of a simple negation
    /// <summary>0.11.0 (plan negation, owner Q5): the detector on and off (the app's <c>--no-negation</c> test switch; no
    /// setting: on together with the spelling indicator). <see cref="Predict"/> never uses it: KSS is unchanged.</summary>
    public bool NegationOn = true;

    /// <summary>
    /// 0.11.0 (plan negation): the corrected group for the clause just typed, when its last word is a negation without « ne »
    /// (<see cref="Negation.Find"/>), else null. Needs the morphology (<see cref="Grammar"/>): without it, null. A few
    /// dictionary lookups and <see cref="Check"/> calls; the « nous / vous » case reads a few n-gram rows (cached).
    /// </summary>
    public NegationFix? MissingNe(IReadOnlyList<NegToken> clause, bool startKnown, bool plusAllowed = false)
    {
        if (!NegationOn || clause is null || _grammar.IsEmpty) return null;
        return Negation.Find(clause, startKnown, plusAllowed, new NegLex(this, _grammar, _excluded));
    }

    /// <summary>The engine as <see cref="INegationLexicon"/>: one grammar and exclusions snapshot per call.</summary>
    private sealed class NegLex(PredictionEngine e, GrammarLexicon g, ExcludedWords x) : INegationLexicon
    {
        // « sœur » typed, « soeur » in the lexicon (TokenFold)
        public ReadOnlySpan<uint> Bits(string lower) => g.BitsOf(e.IndexOf(TokenFold.Fold(lower)));
        public SpellingCheck Check(string word) => e.Check(TokenFold.Fold(word));
        public bool IsName(string word) => e._names.ContainsForm(word) || e._user.Contains(word);
        public bool Excluded(string text) => !x.IsEmpty && e.Blocked(x, text);
        public double ChainScore(string[] toks)
        {
            var unis = new double[toks.Length];
            for (int k = 0; k < toks.Length; k++) unis[k] = e.TokenUni(toks[k], e._idxOf.TryGetValue(toks[k], out var i) ? i : -1);
            return e.ChainScore(default, null, null, toks, unis);
        }
    }

    /// <summary>0.10.2 (glued words): the ≈ list may hold at most this many joined forms.</summary>
    public int CheckSplitMax = 3;

    /// <summary>
    /// 0.10.2 (glued words, plan step 4): the joined forms of an unknown finished word (« lécole » → l'école), context-free
    /// as the rest of <see cref="Check"/>: its fold splits into a bare form's sound and a rest whose <b>whole</b> key is a word
    /// that takes an elision, may be offered, and follows that bare form in the corpus. Each gets an estimated frequency
    /// (<c>log10(f(bare) × c(bare, word) / c(bare, ·) + 1)</c>, f per million: the scale of the lexicon's <c>Freq</c>); the
    /// best <see cref="CheckSplitMax"/> are merged into the list by that frequency, after a « Mes fautes » target, and the list
    /// is cut at <paramref name="max"/>. No SQL, no LINQ: a few binary searches and dictionary lookups. 0.11.0: « est » under
    /// the rest <c>e</c> after c' s' n' qu' (<see cref="EstSplits"/>), and the three-word forms and expressions of
    /// <see cref="CheckMore"/>, merged in the same frequency order.
    /// </summary>
    private void CheckSplits(ref List<string>? found, string w, string strip, string fold, int max, bool fixFirst, bool[] xe)
    {
        bool more = Split3 && CheckSplit3Max > 0 || Expressions && CheckExprMax > 0;
        if (fold.Length < 2 || CheckSplitMax <= 0 && !more || _bareNext.Length == 0) return;
        foreach (var ch in w) if (ch is '\'' or '’' or '-') return;
        Span<int> forms = stackalloc int[Elision.MaxSplits];
        int nf = CheckSplitMax > 0 ? Elision.SplitsOf(fold, forms) : 0;
        if (nf == 0 && !more) return;
        int keepN = Math.Min(Math.Max(CheckSplitMax, 0), 8);
        Span<int> kb = stackalloc int[keepN], ki = stackalloc int[keepN];
        Span<double> kf = stackalloc double[keepN];
        int n = 0;
        bool est = EstSplits && _estIdx >= 0;
        for (int s = 0; s < nf; s++)
        {
            int b = forms[s];
            var next = _bareNext[b];
            if (next.Map is not { Count: > 0 } map || next.Total <= 0) continue;
            int at = Elision.SoundLength(b);
            int restLen = fold.Length - at;
            double perMillion = Math.Max(0, BareUni(b) * _z - 1);
            // 0.11.0 (A): « est » under the whole rest `e` after c' s' n' qu' (« cé » ≈ c'est; an unknown word only, as Check),
            // looked at once the sound index's own words are; as in the bar, only for a word of 3 letters at most ending in an
            // e (review: « sai », « cai », « nei » are sais / quai / neige more often, and c'est pushed « sait » off the list)
            bool estPending = est && restLen == 1 && fold[at] == 'e' && _estForm[b] && EstShape(w);
            for (int p = LowerBound(_fKeys, fold, at); ; p++)
            {
                int i;
                if (p < _fKeys.Length && _fKeys[p].Length == restLen && string.CompareOrdinal(_fKeys[p], 0, fold, at, restLen) == 0) i = _fIdx[p];
                else if (estPending) { i = _estIdx; estPending = false; }
                else break;
                if (!_e[i].Elides || !Injectable(i, xe) || !map.TryGetValue(_e[i].OrthoLower, out var c) || !SplitPair(b, i, c)) continue;
                double f = Math.Log10(perMillion * c / next.Total + 1);
                bool dup = false;
                for (int j = 0; j < n; j++) if (kb[j] == b && ki[j] == i) { dup = true; break; }
                if (dup || n == keepN && f <= kf[n - 1]) continue;
                int q = n < keepN ? n++ : n - 1;
                while (q > 0 && kf[q - 1] < f) { kb[q] = kb[q - 1]; ki[q] = ki[q - 1]; kf[q] = kf[q - 1]; q--; }
                kb[q] = b; ki[q] = i; kf[q] = f;
            }
        }
        // 0.11.0 (C, B): the three-word forms and the expressions, merged with the joined ones by their estimated frequency
        List<(string W, double F)>? extra = more ? CheckMore(fold, strip, xe) : null;
        int total = n + (extra?.Count ?? 0);
        if (total == 0) return;
        var ws = new string[total];
        var fs = new double[total];
        for (int j = 0; j < n; j++) { ws[j] = Elision.Join(Elision.SplitForms[kb[j]], _e[ki[j]].Ortho); fs[j] = kf[j]; }
        for (int j = n; j < total; j++)
        {
            // a stable insertion by frequency (the joined forms, then the three-word forms, then the expressions on ties)
            var (xw, xf) = extra![j - n];
            int q = j;
            while (q > 0 && fs[q - 1] < xf) { ws[q] = ws[q - 1]; fs[q] = fs[q - 1]; q--; }
            ws[q] = xw; fs[q] = xf;
        }
        found ??= new List<string>(max);
        int from = fixFirst && found.Count > 0 ? 1 : 0;   // the pupil's own « Mes fautes » target stays first
        for (int j = 0; j < total; j++)
        {
            var joined = ws[j];
            // the list is not sorted by frequency (each stage is): the joined form goes before the first place from which every
            // remaining entry is rarer than it (a personal word, a name: never passed), so « sai » keeps « sais » ahead
            bool dupe = false;
            foreach (var f in found) if (string.Equals(f, joined, StringComparison.Ordinal)) { dupe = true; break; }
            if (dupe) continue;
            int pos = found.Count;
            double rest = double.NegativeInfinity;   // the highest frequency of found[q..]
            for (int q = found.Count - 1; q >= from; q--)
            {
                int ix = LexEntry(found[q]);
                rest = Math.Max(rest, ix >= 0 ? _e[ix].Freq : double.PositiveInfinity);
                if (rest < fs[j]) pos = q; else break;
            }
            if (pos >= max) continue;
            found.Insert(pos, joined);
            from = pos + 1;   // the next (rarer) joined form after this one
            if (found.Count > max) found.RemoveAt(found.Count - 1);
        }
    }

    /// <summary>
    /// 0.11.0 (C, B): the ≈ candidates of the new kinds for an unknown finished word, each with an estimated frequency on the
    /// lexicon's scale. Three words: the whole key (subject sound + clitic + a word's whole key, or « est » after s' n'),
    /// <c>log10(f(S) × c(S, b, w) / c(S, ·) + 1)</c>, the best <see cref="CheckSplit3Max"/>. Expressions: a row's whole sound key
    /// or its accent-stripped glued letters, its load estimate, the best <see cref="CheckExprMax"/>. Null when none.
    /// </summary>
    private List<(string W, double F)>? CheckMore(string fold, string strip, bool[] xe)
    {
        List<(string W, double F)>? o = null;
        void Put(List<(string W, double F)> into, int cap, string w, double f)
        {
            foreach (var x in into) if (string.Equals(x.W, w, StringComparison.Ordinal)) return;
            if (into.Count == cap && f <= into[^1].F) return;
            int at = into.Count;
            while (at > 0 && into[at - 1].F < f) at--;
            into.Insert(at, (w, f));
            if (into.Count > cap) into.RemoveAt(into.Count - 1);
        }
        if (Split3 && CheckSplit3Max > 0 && Gl.S3.Count > 0)
        {
            Span<int> forms = stackalloc int[Elision.MaxSplits];
            List<(string W, double F)>? c3 = null;
            foreach (var (sound, subjects) in Elision.SubjectSounds)
            {
                if (fold.Length <= sound.Length + 1 || !fold.StartsWith(sound, StringComparison.Ordinal)) continue;
                string rest = Split3Rest(fold, sound.Length);
                int nf = Elision.SplitsOf(rest, forms);
                for (int s = 0; s < nf; s++)
                {
                    int b = forms[s];
                    if (!IsSplit3Clitic(b)) continue;
                    int at = Elision.SoundLength(b), len = rest.Length - at;
                    bool estPending = len == 1 && rest[at] == 'e' && b is 3 or 5 && _estIdx >= 0;
                    for (int p = LowerBound(_fKeys, rest, at); ; p++)
                    {
                        int i;
                        if (p < _fKeys.Length && _fKeys[p].Length == len && string.CompareOrdinal(_fKeys[p], 0, rest, at, len) == 0) i = _fIdx[p];
                        else if (estPending) { i = _estIdx; estPending = false; }
                        else break;
                        if (!_e[i].Elides || !Injectable(i, xe)) continue;
                        foreach (var subj in subjects)
                        {
                            int c = S3Count(subj, b, _e[i].OrthoLower);
                            if (!Split3Pair(b, i, c) || !_idxOf.TryGetValue(subj, out var si) || xe[si]
                                || !Gl.SubjTotal.TryGetValue(subj, out var tot) || tot <= 0) continue;
                            double f = Math.Log10((Math.Pow(10, _e[si].Freq) - 1) * c / tot + 1);
                            Put(c3 ??= new List<(string, double)>(CheckSplit3Max + 1), CheckSplit3Max,
                                subj + " " + Elision.Join(Elision.SplitForms[b], _e[i].Ortho), f);
                        }
                    }
                }
            }
            if (c3 is not null) (o ??= new List<(string, double)>()).AddRange(c3);
        }
        if (Expressions && CheckExprMax > 0 && _exprKeys.Length > 0)
        {
            List<(string W, double F)>? ce = null;
            for (int p = LowerBound(_exprKeys, fold); p < _exprKeys.Length && _exprKeys[p] == fold; p++)
                if (ExprOn(_exprKeyRow[p], xe) && !EndsElided(_exprKeyRow[p])) { var r = _expr[_exprKeyRow[p]]; Put(ce ??= new List<(string, double)>(CheckExprMax + 1), CheckExprMax, r.Written, Gl.ExprFreq[_exprKeyRow[p]]); }
            if (_exprStrip.TryGetValue(strip, out var sr) && ExprOn(sr, xe) && !EndsElided(sr))
                Put(ce ??= new List<(string, double)>(CheckExprMax + 1), CheckExprMax, _expr[sr].Written, Gl.ExprFreq[sr]);
            if (ce is not null) (o ??= new List<(string, double)>()).AddRange(ce);
        }
        return o;
    }

    /// <summary><see cref="LowerBound(string[], string)"/> of the key <c>s[from..]</c>, without the substring.</summary>
    private static int LowerBound(string[] keys, string s, int from)
    {
        int len = s.Length - from;
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            int m = (lo + hi) >> 1;
            int c = string.CompareOrdinal(keys[m], 0, s, from, Math.Max(keys[m].Length, len));
            if (c < 0) lo = m + 1; else hi = m;
        }
        return lo;
    }

    private Comparison<int>? _byFreq;   // most frequent first, then by form (stable order)

    private void AddRanked(ref List<string>? found, List<int> idx, int max)
    {
        if (idx.Count == 0) return;
        idx.Sort(_byFreq ??= (a, b) => _e[a].Freq != _e[b].Freq ? _e[b].Freq.CompareTo(_e[a].Freq) : string.CompareOrdinal(_e[a].Ortho, _e[b].Ortho));
        foreach (var i in idx) AddFound(ref found, _e[i].Ortho, max, ExcludedWords.Empty);   // entries: Offerable checked them
    }

    /// <param name="x">0.11.0: a string source (« Mes fautes » target, personal word, pack name) is checked here.</param>
    private static void AddFound(ref List<string>? found, string c, int max, ExcludedWords x)
    {
        found ??= new List<string>(max);
        if (found.Count >= max || x.IsExcluded(c)) return;
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
    /// <param name="exactMark">0.10.2: the ✓ while typing as the caller computed it (the keyboard:
    /// <see cref="SpellingIndicator.Typing"/> with the tracker's own sentence start, whether the marker is shown or not), so the
    /// exact typed word rule (<see cref="ExactTyped"/>) follows it exactly; null = the engine judges the letters itself
    /// (<see cref="KnowsForMark"/> with <paramref name="sentenceStart"/>: Predict.Eval, the tests).</param>
    public IReadOnlyList<Suggestion> Predict(string typed, IReadOnlyList<string>? context = null, int k = 8, bool sentenceStart = false,
                                             bool? exactMark = null)
    {
        var x = _excluded;   // 0.11.0: one snapshot for the query and its safety net
        return Net(PredictCore(typed, context, k, sentenceStart, exactMark, x), x);
    }

    private IReadOnlyList<Suggestion> PredictCore(string typed, IReadOnlyList<string>? context, int k, bool sentenceStart,
                                                  bool? exactMark, ExcludedWords x, bool slot = true, bool prefixOnly = false)
    {
        typed = (typed ?? "").Trim();
        if (typed.Length == 0) return Array.Empty<Suggestion>();
        var xe = Xe(x);   // 0.11.0: an excluded entry is skipped before it is scored
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
            if (xe[i]) continue;
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

        // 0.11.0 (prefixOnly: the compounds after a hyphen keep only literal prefixes): no sound, tolerant, edit or split pass
        var f = prefixOnly ? "" : PhoneticKey.Fold(typed);
        // 0.8.3: a capital typed inside a sentence asks for a name; a common word that only sounds like it (« avec Lé » →
        // « les », now that « lé » keeps its sounded e) is held back by the names' own bonus
        double soundCap = !sentenceStart && char.IsUpper(typed[0]) ? -NameCapitalBonus : 0;
        if (f.Length >= 2)
            foreach (var pos in PrefixRange(_fKeys, f))
            {
                int i = _fIdx[pos];
                if (xe[i]) continue;
                // 0.8.3 (speed): a word already scored — as a literal prefix (prefix bonus ≥ 0) or by its other sound key — can
                // only keep its score here, so its LM is not computed again (same results, fewer lookups)
                if (WPrefix >= 0 && best.ContainsKey(_e[i].Ortho)) continue;
                double sc = Lm(ctx, _e[i].OrthoLower, _uni[i], UB(_e[i].OrthoLower)) + (_e[i].Elides ? eb : 0) + soundCap;
                // plan 11: the agreement term depends on the word only, so the « already scored » shortcuts stay valid
                if (gt.On) { if (lazy) top!.Offer(i, sc + T(i)); else { sc += T(i); top!.Offer(i, sc); } }
                Consider(best, _e[i].Ortho, sc, MatchKind.Phonetic);
            }

        // 0.11.0 (plan dys-sound-tolerance): the pupil's confused voicing pairs — the typed key with those symbols swapped,
        // looked up as the sound pass does, at a fixed penalty (owner Q-A); green like the sound-alikes (owner Q4). A word
        // already scored keeps its score (the penalty only lowers it), so the exact candidates are never touched.
        var tol = Tolerance;
        // the words this pass added, only when a tolerant score may be below an edit's (WTolerant > WEdit): the edit pass then
        // compares with them instead of skipping them; every other word already scored is skipped there as before
        HashSet<string>? tolWords = null;
        if (!prefixOnly && tol != SoundPairs.None && f.Length >= TolerantFrom)
            foreach (var v in PhoneticKey.Variants(f, tol, TolerantMaxSwaps))
                foreach (var pos in PrefixRange(_fKeys, v))
                {
                    int i = _fIdx[pos];
                    if (xe[i] || WTolerant >= 0 && best.ContainsKey(_e[i].Ortho)) continue;
                    double sc = Lm(ctx, _e[i].OrthoLower, _uni[i], UB(_e[i].OrthoLower)) + (_e[i].Elides ? eb : 0) + soundCap - WTolerant;
                    if (gt.On) { if (lazy) top!.Offer(i, sc + T(i)); else { sc += T(i); top!.Offer(i, sc); } }
                    Consider(best, _e[i].Ortho, sc, MatchKind.Phonetic);
                    if (WTolerant > WEdit) (tolWords ??= new HashSet<string>(StringComparer.Ordinal)).Add(_e[i].Ortho);
                }

        List<string>? edits = !prefixOnly && typed.Length >= 4 ? Edits1(PhoneticKey.StripLower(typed)).ToList() : null;
        if (edits != null)
            foreach (var v in edits)
                if (_strip.TryGetValue(v, out var idxs))
                    foreach (var i in idxs)
                    {
                        // an edit (− WEdit) never beats a score already there, but a tolerant one when WTolerant > WEdit
                        if (xe[i]) continue;
                        bool tolerant = tolWords is not null && tolWords.Contains(_e[i].Ortho);
                        if (WEdit >= 0 && best.ContainsKey(_e[i].Ortho) && !tolerant) continue;
                        double sc = Lm(ctx, _e[i].OrthoLower, _uni[i], UB(_e[i].OrthoLower)) - WEdit + (_e[i].Elides ? eb : 0);
                        if (gt.On) { if (lazy) top!.Offer(i, sc + T(i)); else { sc += T(i); top!.Offer(i, sc); } }
                        // owner 2026-11-27 (D33): a word reached through a ticked pair stays green « même son » even when the
                        // edit pass scores it higher (« la dable » → table): the better score, the tolerant kind
                        if (tolerant) { if (best.TryGetValue(_e[i].Ortho, out var had) && sc > had.Item1) best[_e[i].Ortho] = (sc, MatchKind.Phonetic); }
                        else Consider(best, _e[i].Ortho, sc, MatchKind.Edit);
                    }

        if (Splits && !prefixOnly) SplitPass(best, typed, f, ctx, c1, c2, sentenceStart, exactMark, xe);
        if (user) UserPass(u, best, typed, f, edits, ctx, eb, share, sentenceStart, lazy ? default : gt, x);
        var nm = _names;
        if (!nm.IsEmpty) NamesPass(nm, best, typed, eb, sentenceStart, k, x);
        if (gt.On) Inject(best, typed, gt, top!, lazy, xe);

        bool capital = !sentenceStart && char.IsUpper(typed[0]);
        bool Name(string key) => key.Length > 0 && char.IsUpper(key[0]) && (user && (u.Contains(key) || u.IsFixTarget(key)) ||
                                                                            nm.ContainsForm(key) && !(sentenceStart && IsCommonWord(key)));
        // 0.10.2 (plan exact-typed-word): the word typed in full and spelled right is always in the bar. First ranks the
        // whole bar (so that Predict(k) stays the prefix of Predict(n) whatever k); Present only acts on a full bar of two
        // words or more (in a one-word bar the last slot is the first: the « first » variant the owner rejected)
        var mode = ExactTyped;
        bool exact = mode != ExactTypedMode.Off && exactMark != false
                     && (mode == ExactTypedMode.First || k >= ExactBar && ExactBar >= 2)
                     && ExactEligible(typed, sentenceStart, exactMark == true, x);
        int n = exact && mode == ExactTypedMode.First ? Math.Max(k, ExactBar) : k;
        bool capped = capital && typed.Length < NamesCapUntil;
        Suggestion[] res;
        Func<string, double, double>? adj = null;
        if (lazy)
        {
            // the term of a lexicon form (its key is the lexicon spelling; a capitalised name is not judged)
            double Adj(string key, double score) =>
                _idxOf.TryGetValue(key, out var ix) && string.Equals(_e[ix].Ortho, key, StringComparison.Ordinal) ? score + gt.Of(ix) : score;
            adj = Adj;
            var all = new List<(string, double, MatchKind)>(best.Count);
            foreach (var (key, v) in best) all.Add((key, v.score, v.kind));
            var rk = LazyTop(all, capped ? int.MaxValue : n, adj, byLength: true).Select(x => new Suggestion(x.Key, x.Score, x.Kind, Name(x.Key)));
            res = capped ? CapNames(rk, n) : rk.Take(n).ToArray();
        }
        else
        {
            var ranked = best.Select(kv => new Suggestion(kv.Key, kv.Value.score, kv.Value.kind, Name(kv.Key)))
                       .OrderByDescending(s => s.Score).ThenBy(s => s.Word.Length);
            res = capped ? CapNames(ranked, n) : ranked.Take(n).ToArray();
        }
        if (exact) res = PlaceExact(res, best, typed, k, sentenceStart, mode, adj, Name, u, nm);
        // 0.11.0 (review): never where the context expects a verb (« elle marie » is no « Marie-… »)
        bool verb = gt.On && (gt.Ex.Rules & AgreementRule.Subject) != 0;
        return slot && CompoundNameSlot && !verb ? CompoundSlot(res, typed, k, sentenceStart, u, nm, x) : res;
    }

    // ---- 0.10.2: the « exact typed word » rule (plan exact-typed-word.md, owner 2026-11-17)
    /// <summary>0.10.2: the word being typed, spelled right (the ✓ while typing), is guaranteed a tile.
    /// <see cref="ExactTypedMode.Present"/>: today's ranking, the word put in the last slot of the bar when it is not in it.
    /// <see cref="ExactTypedMode.First"/>: the word comes first, after the words that sound like it and rank higher (« il et »
    /// → est, et; « ils mange » → mangent, mange). Default Present (owner 2026-11-17): First and First with a margin of 1.0
    /// cost 1.8–2.9 and 0.2–0.4 KSS points on the held-out sets (development/evaluation.md § Exact typed word).</summary>
    public ExactTypedMode ExactTyped = ExactTypedMode.Present;
    /// <summary>0.10.2: the number of tiles the bar shows (the keyboard sets its word count; it asks the engine for spares).</summary>
    public int ExactBar = 8;
    /// <summary>0.10.2 (First): a word that is not a homophone still stays ahead of the typed word when it scores more than
    /// this above it (log10 scale; +∞ = only homophones go ahead, owner Q4).</summary>
    public double ExactMargin = double.PositiveInfinity;

    /// <summary>0.10.2: may the rule apply to <paramref name="typed"/>? Two letters at least, not all capitals (an acronym),
    /// not a bare elision form (« qu »), and ✓ while typing: the caller's (<paramref name="ticked"/>), else
    /// <see cref="KnowsForMark"/>. 0.11.0: never an excluded word (it stays ✓, but gets no tile).</summary>
    private bool ExactEligible(string typed, bool sentenceStart, bool ticked, ExcludedWords x)
    {
        if (typed.Length < 2 || Elision.IsBare(typed) || x.IsExcluded(typed) && !(x.NameExempt(typed) && _names.ContainsForm(typed))) return false;
        bool lower = false;
        foreach (var ch in typed) if (char.IsLower(ch)) { lower = true; break; }
        return lower && (ticked || KnowsForMark(typed, sentenceStart));
    }

    /// <summary>0.10.2: the lexicon entry whose stored form is exactly <paramref name="key"/>, else −1 (a name, a personal
    /// form with a capital).</summary>
    private int LexEntry(string key) =>
        _idxOf.TryGetValue(key, out var i) && string.Equals(_e[i].Ortho, key, StringComparison.Ordinal) ? i : -1;

    /// <summary>0.10.2: do two candidates sound alike? Two lexicon forms: the same Lexique pronunciation or the same homophone
    /// family (« et » / « est »); otherwise (a name, a personal word, 0.10.2 a joined word: « m'a » / « ma ») the same grapheme
    /// fold, apostrophes left out.</summary>
    private bool Homophone(string a, string x)
    {
        int ia = LexEntry(a), ix = LexEntry(x);
        if (ia >= 0 && ix >= 0)
        {
            if (_phon[ia] != 0 && _phon[ia] == _phon[ix]) return true;
            return HomophoneFamilies.Of(_e[ia].OrthoLower) is { } fam && fam.Contains(_e[ix].OrthoLower);
        }
        return string.Equals(PhoneticKey.Fold(NoApostrophe(a)), PhoneticKey.Fold(NoApostrophe(x)), StringComparison.Ordinal);

        static string NoApostrophe(string w) => w.IndexOf('\'') < 0 && w.IndexOf('’') < 0 ? w : w.Replace("'", "").Replace("’", "");
    }

    /// <summary>
    /// 0.10.2: put the exact typed word X in the ranked window <paramref name="res"/> (after <see cref="CapNames"/>), then keep
    /// <paramref name="k"/>. X is the result equal to the typed letters ignoring case, else the candidate of the typed form, the
    /// lexicon form (first at a sentence start), a personal or a pack form of the same letters, scored as the ranking scores
    /// it; a pack or personal form no pass kept gets the window's lowest score − 0.001. Present: X outside the bar goes to its last slot. First: the
    /// candidates ranked above X that sound like it (or beat it by more than <see cref="ExactMargin"/>) keep their order ahead
    /// of it, X follows (at the bar's last slot at worst), then the rest in order.
    /// </summary>
    private Suggestion[] PlaceExact(Suggestion[] res, Dictionary<string, (double score, MatchKind kind)> best, string typed, int k,
                                    bool sentenceStart, ExactTypedMode mode, Func<string, double, double>? adj, Func<string, bool> name,
                                    UserLexicon u, NamesLexicon nm)
    {
        int bar = Math.Max(1, ExactBar);
        int at = -1;
        for (int i = 0; i < res.Length; i++)
            if (string.Equals(res[i].Word, typed, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
        Suggestion x;
        if (at >= 0)
        {
            if (mode == ExactTypedMode.Present && at < bar) return Trim(res, k);
            x = res[at];
        }
        else
        {
            string? key = null, fallback = null;
            var lower = typed.ToLowerInvariant();
            // at a sentence start the capital says nothing: « Mai » is the month (the bar writes its capital), not a name Mai
            string? lex = _idxOf.TryGetValue(lower, out var li) && best.ContainsKey(_e[li].Ortho) ? _e[li].Ortho : null;
            if (sentenceStart && lex is not null) key = lex;
            else if (best.ContainsKey(typed)) key = typed;
            else if (lex is not null) key = lex;
            else
            {
                for (int p = LowerBound(u.LowerKeys, lower); key is null && p < u.LowerKeys.Length && u.LowerKeys[p] == lower; p++)
                {
                    var f = u.Entries[u.LowerIdx[p]].Form;
                    if (best.ContainsKey(f)) key = f; else fallback ??= f;
                }
                // a restricted figure only where the names pass offers it: a capital inside a sentence, 3 letters (D30)
                bool restrictedOk = !sentenceStart && char.IsUpper(typed[0]) && typed.Length >= 3;
                void Pack(string[] keys, int[] idx)
                {
                    for (int p = LowerBound(keys, lower); key is null && p < keys.Length && keys[p] == lower; p++)
                    {
                        if (nm.Entries[idx[p]].Restricted && !restrictedOk) continue;
                        var f = nm.Entries[idx[p]].Form;
                        if (best.ContainsKey(f)) key = f; else if (fallback is null && f == typed) fallback = f;
                    }
                }
                if (!nm.IsEmpty) { Pack(nm.Keys1, nm.Idx1); Pack(nm.Keys2, nm.Idx2); }
            }
            if (key is not null)
            {
                var (sc, kind) = best[key];
                x = new Suggestion(key, adj is null ? sc : adj(key, sc), kind, name(key));
            }
            else if (fallback is not null)
                x = new Suggestion(fallback, res.Length == 0 ? -20 : res[^1].Score - 0.001, MatchKind.Prefix, name(fallback));
            else return Trim(res, k);
        }

        var list = new List<Suggestion>(res.Length + 4);
        if (mode == ExactTypedMode.Present)
        {
            foreach (var s in res) if (!string.Equals(s.Word, x.Word, StringComparison.Ordinal)) list.Add(s);
            list.Insert(Math.Min(bar - 1, list.Count), x);
            return Trim(list, k);
        }

        // First: who stays ahead of X
        bool Above(in Suggestion s) => s.Score > x.Score || s.Score == x.Score && s.Word.Length < x.Word.Length;
        var lead = new List<Suggestion>();
        var rest = new List<Suggestion>(res.Length);
        for (int i = 0; i < res.Length; i++)
        {
            var s = res[i];
            if (string.Equals(s.Word, x.Word, StringComparison.Ordinal)) continue;
            bool above = at >= 0 ? i < at : true;   // X outside the window: every word in it ranks higher
            if (above && (s.Score - x.Score > ExactMargin || Homophone(s.Word, x.Word))) lead.Add(s);
            else rest.Add(s);
        }
        if (at < 0)
        {
            // a homophone ranked above X but outside the window (« mais » far down while « mai » was typed) still goes first
            foreach (var (key, v) in best)
            {
                if (v.score <= x.Score && !(v.score == x.Score && key.Length < x.Word.Length)) continue;   // the term is never positive
                if (string.Equals(key, x.Word, StringComparison.Ordinal) || !Homophone(key, x.Word)) continue;
                bool inWindow = false;
                foreach (var s in res) if (string.Equals(s.Word, key, StringComparison.Ordinal)) { inWindow = true; break; }
                if (inWindow) continue;
                var c = new Suggestion(key, adj is null ? v.score : adj(key, v.score), v.kind, name(key));
                if (Above(c)) lead.Add(c);
            }
            lead.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score)
                              : a.Word.Length != b.Word.Length ? a.Word.Length.CompareTo(b.Word.Length) : string.CompareOrdinal(a.Word, b.Word));
        }
        list.AddRange(lead);
        list.AddRange(rest);
        list.Insert(Math.Min(Math.Min(lead.Count, bar - 1), list.Count), x);
        return Trim(list, k);

        static Suggestion[] Trim(IReadOnlyList<Suggestion> l, int k)
        {
            var o = new Suggestion[Math.Min(k, l.Count)];
            for (int i = 0; i < o.Length; i++) o[i] = l[i];
            return o;
        }
    }

    // ---- 0.10.2: glued words (plan glued-words.md, owner 2026-11-17: « je tème » → « t'aime »)
    /// <summary>0.10.2: the split pass (and the joined forms of <see cref="Check"/>) on and off; on by default (owner
    /// 2026-11-18). Off: the results are those without it, byte for byte.</summary>
    public bool Splits = true;
    /// <summary>0.10.2: the penalty (log10 scale, like the LM) of a joined candidate: its score is
    /// <c>Lm(ctx, bare) + Lm(ctx ⊕ bare, word) + grammar(ctx ⊕ bare, word) − WSplit</c>. Chosen by the gates of the plan
    /// (development/evaluation.md § Glued words: 0 met every gate with the best glued top-1; <see cref="ChainLift"/> already puts
    /// a chain and a single word on one scale).</summary>
    public double WSplit = 0;
    /// <summary>0.10.2: at most this many joined candidates per query (the best).</summary>
    public int SplitMax = 2;
    /// <summary>0.10.2: the split pass needs this many typed letters.</summary>
    public int SplitFrom = 2;
    /// <summary>0.10.2 (owner Q3): splits are offered when the typed word is itself a word (✓) too (« il ma » → m'a), ranked
    /// by the context; false = only when it is not (the plan's fallback).</summary>
    public bool SplitKnown = true;
    /// <summary>0.10.2 (review): a pair needs at least this many bigram counts (« bare word ») to split: below it the corpus
    /// pairs are mostly tokenisation noise (« s e » 47, « j o » 23, « t i » 13, « j ou » 6), the rarest wanted pair of the
    /// curated sets is « m aime » (241).</summary>
    public int SplitMinCount = 50;

    // ---- 0.11.0: glued words follow-up (plan glued-words-followup.md, owner 2026-11-27). Each kind has its own switch, on
    // by default; with the three off the results are those of 0.10.2 byte for byte. Splits = false turns them all off.
    /// <summary>0.11.0 (A): « est » after c' s' n' qu' under the one-symbol sound <c>e</c> (« il cé » → c'est, « nè » →
    /// n'est), and « ce » / « se » typed in full (their fold <c>s</c> has no vowel) → c'est / s'est in the bar. On by default
    /// (plan step 7, the gates of development/evaluation.md § Glued words follow-up).</summary>
    public bool EstSplits = true;
    /// <summary>0.11.0 (A, fallback 1): « est » is offered for a typed word that is itself a word too (« ces », « ses », « né »);
    /// false = only for an unknown one (« ce » / « se » keep their c'est / s'est).</summary>
    public bool EstFromKnown = true;
    /// <summary>0.11.0 (A): the « c'est » / « s'est » tile of « ce » / « se » typed in full gets the literal-prefix bonus (the
    /// typed letters start « cest »); false = the plan's plain chain score (it leaves c'est out of the bar after « ce »).</summary>
    public bool MuteEPrefix = true;
    /// <summary>0.11.0 (C): subject + clitic + word glued (« jtaime » → je t'aime, « ilsapelle » → il s'appelle).</summary>
    public bool Split3 = true;
    /// <summary>0.11.0 (C): the penalty (log10 scale) of a three-word tile: <c>ChainScore(ctx, [S, b, w]) + grammar − WSplit3</c>.</summary>
    public double WSplit3 = 0;
    /// <summary>0.11.0 (C): typed letters needed in the bar.</summary>
    public int Split3From = 4;
    /// <summary>0.11.0 (C, owner Q1: no): three-word tiles for a typed word that is itself a word (« jeté »).</summary>
    public bool Split3Known = false;
    /// <summary>0.11.0 (C): the trigram count « subject clitic word » a three-word split needs (as <see cref="SplitMinCount"/>).</summary>
    public int Split3MinCount = 50;
    /// <summary>0.11.0 (C): at most this many three-word forms in the ≈ list.</summary>
    public int CheckSplit3Max = 2;
    /// <summary>0.11.0 (B): the fixed expressions of <see cref="GluedExpressions"/> (« ilya » → il y a).</summary>
    public bool Expressions = true;
    /// <summary>0.11.0 (B2): the subject + être / avoir rows (« ilé » → il est). Off by default: B2 failed its own gates
    /// (plan fallback « drop B2 »: `--glued` full hit@8 0.59–0.64 on two sets, « je suis » in the dys sets' bars;
    /// development/evaluation.md § Glued words follow-up).</summary>
    public bool ExprB2 = false;
    /// <summary>0.11.0 (B): the penalty (log10 scale) of an expression tile: <c>ChainScore(ctx, tokens) − WExpr</c>.</summary>
    public double WExpr = 0;
    /// <summary>0.11.0 (B): typed letters needed in the bar.</summary>
    public int ExprFrom = 4;
    /// <summary>0.11.0 (B): no expression in the bar while the typed letters are the start of a lexicon word (« Sylv » is the
    /// start of « sylvie », not « s'il vous plaît »: `cases-names`); false = the plan's rule alone (its fallback for that was
    /// <see cref="ExprFrom"/> = 5, which also loses « ilya »).</summary>
    public bool ExprNotPrefix = true;
    /// <summary>0.11.0 (B, owner Q1: no): expressions for a typed word that is itself a word (« entrain »).</summary>
    public bool ExprKnown = false;
    /// <summary>0.11.0 (B): the n-gram count of the whole expression a row needs (rows below it are left out; read at load,
    /// applied per query).</summary>
    public int ExprMinCount = 100;
    /// <summary>0.11.0 (B): at most this many expressions in the ≈ list.</summary>
    public int CheckExprMax = 2;

    /// <summary>0.11.0: the entry of « est » (−1 = none), and per split form whether « est » may follow it (<see cref="Elision.EstForms"/>).</summary>
    private readonly int _estIdx;
    private readonly bool[] _estForm;
    /// <summary>0.11.0: the counts of the new kinds, read after the load (<c>ReadGluedCounts</c>): the trigram successors of
    /// « subject clitic » (<see cref="S3Key"/>) and the bigram total of each subject (C); per expression row its n-gram count
    /// and its Check estimate <c>log10(f(t1) × c(t1..tn) / c(t1, ·) + 1)</c> (B).</summary>
    private sealed record GluedState(Dictionary<string, Ngram> S3, Dictionary<string, long> SubjTotal, long[] ExprCount, double[] ExprFreq);
    private readonly Lazy<GluedState> _glued;
    /// <summary>0.11.0: why the new kinds' counts could not be read (null when they were, or not read yet); for the load log.</summary>
    public string? GluedProblem { get; private set; }
    /// <summary>The counts of the new kinds; the first caller waits for the read started at load (deterministic results).</summary>
    private GluedState Gl => _glued.Value;

    private GluedState ResolveGlued(GluedCounts gc)
    {
        var freq = new double[_expr.Length];
        var count = new long[_expr.Length];
        for (int r = 0; r < _expr.Length; r++)
        {
            var row = _expr[r];
            count[r] = gc.ExprCount.Length > r ? gc.ExprCount[r] : 0;
            long first = gc.ExprFirstTotal.Length > r ? gc.ExprFirstTotal[r] : 0;
            double f1 = 0;
            if (row.Idx[0] >= 0)
            {
                int b0 = BareForm(row.Tok[0]);
                f1 = b0 >= 0 ? Math.Max(0, BareUni(b0) * _z - 1) : Math.Pow(10, _e[row.Idx[0]].Freq) - 1;
            }
            freq[r] = first > 0 ? Math.Log10(f1 * count[r] / first + 1) : 0;
        }
        return new GluedState(gc.S3, gc.SubjectTotal, count, freq);
    }
    private (GrammarLexicon? G, string? P3, string? P2, string? P1, bool Par, bool Wide, Expect Ex) _split3Expect;

    /// <summary>0.11.0 (B): one expression row as loaded: its tokens and their entries, its sound keys, and why it was left out
    /// (null = kept; its counts are in <see cref="Gl"/>).</summary>
    private sealed record ExprRow(string Written, bool B2, string[] Tok, int[] Idx, string Strip, string[] Keys, int KeyCollisions, string? Dropped);
    private ExprRow[] _expr = Array.Empty<ExprRow>();
    /// <summary>The kept rows' sound keys, sorted, with their row and the length of the first word's part.</summary>
    private string[] _exprKeys = Array.Empty<string>();
    private int[] _exprKeyRow = Array.Empty<int>(), _exprKeyFirst = Array.Empty<int>();
    /// <summary>The kept rows by their accent-stripped glued letters (« apeupres »: the same letters, Check).</summary>
    private Dictionary<string, int> _exprStrip = new(StringComparer.Ordinal);

    /// <summary>One line of <see cref="GetExpressions"/> (Predict.Cli --probe, Predict.Eval --glued): a row, its n-gram count,
    /// its sound keys, how many of them equal a lexicon word's whole key (logged, kept: the ranking decides, « parceque » ≈
    /// parce que before parsec), and why it was left out (null = kept).</summary>
    public readonly record struct ExpressionInfo(string Written, bool B2, long Count, IReadOnlyList<string> Keys, int KeyCollisions, string? Dropped);

    /// <summary>0.11.0: the expression rows as loaded (<see cref="ExprMinCount"/> applied as now).</summary>
    public IReadOnlyList<ExpressionInfo> GetExpressions()
    {
        var o = new List<ExpressionInfo>(_expr.Length);
        var g = Gl;
        for (int i = 0; i < _expr.Length; i++)
        {
            var r = _expr[i];
            o.Add(new ExpressionInfo(r.Written, r.B2, g.ExprCount[i], r.Keys, r.KeyCollisions,
                                     r.Dropped ?? (g.ExprCount[i] < ExprMinCount ? $"count < {ExprMinCount}" : null)));
        }
        return o;
    }

    /// <summary>0.11.0 (Predict.Cli --probe): the subject + clitic contexts loaded, their rows, and the rows at or above
    /// <see cref="Split3MinCount"/>.</summary>
    public (int Contexts, int Rows, int Attested) GetSplit3Info()
    {
        int rows = 0, att = 0;
        var s3 = Gl.S3;
        foreach (var (_, g) in s3)
            if (g.Map is { } m) { rows += m.Count; foreach (var c in m.Values) if (c >= Split3MinCount) att++; }
        return (s3.Count, rows, att);
    }

    /// <summary>
    /// 0.11.0 (B): the expression table at load. A row is kept when every token is a lexicon form and its glued letters are
    /// no word of their own (« cava » is a word: « ça va » left out; owner Q2). Its sound keys (<see cref="GluedExpressions.Keys"/>)
    /// go into one sorted array; a key equal to a lexicon word's whole key is counted, not dropped (the ranking orders them).
    /// The count minimum is applied per query, so <see cref="ExprMinCount"/> may change after the load.
    /// </summary>
    private void BuildExpressions(Dictionary<string, (string Phon, double Freq)> exprPhon)
    {
        var rows = GluedExpressions.Rows;
        var o = new ExprRow[rows.Count];
        var keys = new List<(string Key, int Row, int First)>();
        _exprStrip = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int r = 0; r < rows.Count; r++)
        {
            var w = rows[r].Written;
            var tok = GluedExpressions.Tokens(w);
            var idx = new int[tok.Length];
            string? dropped = null;
            for (int k = 0; k < tok.Length; k++)
                if (!_idxOf.TryGetValue(tok[k], out idx[k])) { idx[k] = -1; dropped ??= "a token is no lexicon form"; }
            var strip = PhoneticKey.StripLower(GluedExpressions.Glued(w));
            if (dropped is null && _strip.ContainsKey(strip)) dropped = "its glued letters are a word";
            var ks = GluedExpressions.Keys(w, t => exprPhon.TryGetValue(t, out var p) ? p.Phon : null);
            int coll = 0;
            foreach (var (k, _) in ks) { int p = LowerBound(_fKeys, k); if (p < _fKeys.Length && _fKeys[p] == k) coll++; }
            o[r] = new ExprRow(w, rows[r].B2, tok, idx, strip, ks.Select(x => x.Key).ToArray(), coll, dropped);
            if (dropped is not null) continue;
            foreach (var (k, first0) in ks) keys.Add((k, r, first0));
            _exprStrip.TryAdd(strip, r);
        }
        keys.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key) is var c && c != 0 ? c : a.Row.CompareTo(b.Row));
        _expr = o;
        _exprKeys = keys.Select(k => k.Key).ToArray();
        _exprKeyRow = keys.Select(k => k.Row).ToArray();
        _exprKeyFirst = keys.Select(k => k.First).ToArray();
    }

    /// <summary>The index in <see cref="Elision.SplitForms"/> of a bare form (−1 = none).</summary>
    private static int BareForm(string w)
    {
        var f = Elision.SplitForms;
        for (int b = 0; b < f.Count; b++) if (string.Equals(f[b], w, StringComparison.OrdinalIgnoreCase)) return b;
        return -1;
    }

    /// <summary>0.11.0 (B): may expression row <paramref name="r"/> be offered now? Kept at load, enough counts, B2 on for a
    /// B2 row, and no token excluded (D35: a bare form by its entry flag, any other through <see cref="Injectable"/>).</summary>
    private bool ExprOn(int r, bool[] xe)
    {
        var row = _expr[r];
        if (row.Dropped is not null || row.B2 && !ExprB2 || Gl.ExprCount[r] < ExprMinCount) return false;
        foreach (var i in row.Idx)
            if (i < 0 || (Elision.IsBare(_e[i].Ortho) ? xe[i] : !Injectable(i, xe))) return false;
        return true;
    }

    /// <summary>0.11.0 (B): a row ending in an apostrophe (« parce qu' »): a bar tile only, never a ≈ replacement (it would
    /// leave « parce qu' il »).</summary>
    private bool EndsElided(int r) => Elision.IsApostrophe(_expr[r].Written[^1]);

    /// <summary>The context-free probability of a chain token: a bare form's (<see cref="BareUni"/>), else its entry's.</summary>
    private double TokenUni(string tok, int i)
    {
        int b = Elision.IsBare(tok) ? BareForm(tok) : -1;
        return b >= 0 ? BareUni(b) : i >= 0 ? _uni[i] : UniFloor;
    }

    /// <summary>
    /// 0.11.0: the language-model score of a chain of tokens after the context (« je t aime », « il y a »):
    /// <c>Lm(ctx, t1) + Σ k≥2 [Lm(ctx ⊕ t1..tk−1, tk) + Lift k]</c>, Lift k = <c>WLm × (3 − min(have k, reach)) × −log10 α</c>
    /// — <see cref="ChainLift"/> for each later token (have k: the context words token k can use). The successors of a bare
    /// form come from the load (<see cref="_bareNext"/>), those of « subject clitic » too (<see cref="Gl"/>), the others from
    /// the cached n-gram reads.
    /// </summary>
    private double ChainScore(Ctx ctx, string? c1, string? c2, string[] toks, double[] unis)
    {
        double s = Lm(ctx, toks[0], unis[0]);
        int reach = UseFourgrams && _hasFour ? 3 : _hasTri ? 2 : _hasBi ? 1 : 0;
        int nctx = c1 is null ? 0 : c2 is null ? 1 : 2;
        for (int k = 1; k < toks.Length; k++)
        {
            string h1 = toks[k - 1];
            string? h2 = k >= 2 ? toks[k - 2] : c1;
            string? h3 = k >= 3 ? toks[k - 3] : k == 2 ? c1 : c2;
            int b = Elision.IsBare(h1) ? BareForm(h1) : -1;
            var bi = b >= 0 ? _bareNext[b] : Successors1(h1);
            var tri = h2 is null ? default : b >= 0 && Gl.S3.TryGetValue(S3Key(h2, h1), out var pre) ? pre : Successors2(h2, h1);
            var four = !UseFourgrams || h2 is null || h3 is null ? default : Successors3(h3, h2, h1);
            s += Lm(new Ctx(bi, tri, four), toks[k], unis[k]);
            int missing = 3 - Math.Min(Math.Min(3, nctx + k), reach);
            if (missing > 0 && Alpha > 0) s += WLm * missing * -Math.Log10(Alpha);
        }
        return s;
    }

    /// <summary>0.11.0 (C): the trigram count of « subject clitic word » (0 when the context was not loaded).</summary>
    private int S3Count(string subject, int b, string word) =>
        Gl.S3.TryGetValue(S3Key(subject, Elision.SplitForms[b]), out var g) && g.Map is { } m && m.TryGetValue(word, out var c) ? c : 0;

    /// <summary>0.11.0 (C): <see cref="SplitPair"/> for a three-word split, on the trigram count.</summary>
    private bool Split3Pair(int b, int i, int count)
    {
        if (count < Split3MinCount) return false;
        var w = _e[i].OrthoLower;
        if (w.Length == 1) return w is "a" or "à" or "y";
        if (w is "au" or "aux") return Elision.SplitForms[b].EndsWith("qu", StringComparison.Ordinal);
        return true;
    }

    /// <summary>0.10.2 (review): may the bare form <paramref name="b"/> (an index into <see cref="Elision.SplitForms"/>) join
    /// the entry <paramref name="i"/>, seen <paramref name="count"/> times after it? A count of <see cref="SplitMinCount"/> at
    /// least; a one-letter word only « a », « à », « y » (never « s'e », « j'o », « t'i »); « au » / « aux » only after
    /// qu' jusqu' lorsqu' puisqu' (« qu'au », never « d'au », « l'au », « m'au »).</summary>
    private bool SplitPair(int b, int i, int count)
    {
        if (count < SplitMinCount) return false;
        var w = _e[i].OrthoLower;
        if (w.Length == 1) return w is "a" or "à" or "y";
        if (w is "au" or "aux") return Elision.SplitForms[b].EndsWith("qu", StringComparison.Ordinal);
        return true;
    }
    private (GrammarLexicon? G, string? P3, string? P2, string? P1, bool Par, bool Wide, Expect Ex) _splitExpect;

    /// <summary>
    /// 0.10.2: the joined candidates of a typed word that sounds like an elided word glued to the next one (« tème » →
    /// « t'aime », « jai » → « j'ai », « lécole » → « l'école »). The fold of the typed letters splits into a bare form's sound
    /// and a rest that starts with a vowel sound (<see cref="Elision.SplitsOf"/>); the rest is looked up in the sound index (its
    /// exact key for one letter, a prefix range from two); a word is kept only if it takes an elision, may be offered, and the
    /// corpus wrote it after that bare form (the successors kept at load). Score: <c>Lm(ctx, bare) + Lm(ctx ⊕ bare, word) +
    /// grammar(ctx ⊕ bare, word) − WSplit</c> (the grammar term is in the score: the ranking finds no entry for the joined
    /// form). The best <see cref="SplitMax"/> go in as one « sounds like » tile each. 0.11.0: « est » after c' s' n' qu' under
    /// the rest <c>e</c> and « ce » / « se » typed in full (A), the three-word splits (<see cref="Split3Pass"/>, C) and the
    /// expressions (<see cref="ExprPass"/>, B) share that one cap.
    /// </summary>
    private void SplitPass(Dictionary<string, (double, MatchKind)> best, string typed, string fold, Ctx ctx, string? c1, string? c2,
                           bool sentenceStart, bool? exactMark, bool[] xe)
    {
        if (typed.Length < SplitFrom || SplitMax <= 0 || _bareNext.Length == 0) return;
        foreach (var ch in typed) if (ch is '\'' or '’' or '-') return;
        if (!sentenceStart && char.IsUpper(typed[0])) return;   // a capital inside a sentence asks for a name
        bool? known = exactMark;
        bool Known() => known ??= KnowsForMark(typed, sentenceStart);
        // every glued tile (0.10.2 joined, 0.11.0 « est », three words, expressions) shares the one cap SplitMax; a tile is
        // (B, I) for a joined form (W null), its written string otherwise
        List<(double Score, int B, int I, string? W)>? keep = null;
        bool est = EstSplits && _estIdx >= 0;
        if (SplitKnown || !Known())
        {
            Span<int> forms = stackalloc int[Elision.MaxSplits];
            int nf = fold.Length >= 2 ? Elision.SplitsOf(fold, forms) : 0;
            // 0.11.0 (A, the mute e): « ce » / « se » fold to `s` (no vowel after the bare sound): the rest `e` for the bare form
            // of the typed letter only (« ce » → c'est, never s'est)
            string? muteRest = null;
            if (nf == 0 && est && fold == "s" && typed.Length == 2 && PhoneticKey.StripLower(typed) is "ce" or "se")
            { forms[0] = typed[0] is 'c' or 'C' ? 4 : 3; nf = 1; muteRest = "e"; }
            if (nf > 0)
            {
                double lift = ChainLift(c1, c2);
                // in the bar the sound `e` must be the typed word's last letter, an e (« cé », « nè », « kè »): « nei », « sai » are
                // mostly the start of « neige », « saison » (the dys guard « nei » → neige; the ≈ check keeps n'est for them)
                bool estHere = est && (muteRest is not null || EstFromKnown || !Known()) && EstShape(typed);
                for (int s = 0; s < nf; s++)
                {
                    int b = forms[s];
                    var next = _bareNext[b];
                    if (next.Map is not { Count: > 0 } map) continue;
                    string rest = muteRest ?? fold[Elision.SoundLength(b)..];
                    bool exact = rest.Length == 1;
                    bool ready = false;
                    double lmBare = 0;
                    Ctx after = default;
                    GTerm gt = default;
                    void Score(int i)
                    {
                        if (!ready)
                        {
                            // the bare form in the context, and the context with the bare form at its end (the trigram / 4-gram
                            // successors are read as any context's, cached)
                            var bare = Elision.SplitForms[b];
                            lmBare = Lm(ctx, bare, BareUni(b));
                            after = new Ctx(next, c1 is null ? default : Successors2(c1, bare),
                                            !UseFourgrams || c1 is null || c2 is null ? default : Successors3(c2, c1, bare));
                            gt = GrammarTerm(bare, c1, c2, ref _splitExpect);
                            ready = true;
                        }
                        double sc = lmBare + Lm(after, _e[i].OrthoLower, _uni[i]) + lift + (gt.On ? gt.Of(i) : 0) - WSplit;
                        // 0.11.0 (A): « ce » / « se » are the first letters of « c'est » / « s'est » without the apostrophe: the
                        // tile gets the literal-prefix bonus a completion of those letters gets, gated by its estimated frequency
                        // (without it the frequent « ce… » completions keep c'est out of the bar: development/evaluation.md)
                        if (muteRest is not null && i == _estIdx && MuteEPrefix && next.Total > 0 && next.Map!.TryGetValue(_e[i].OrthoLower, out var mc))
                            sc += WPrefix * Math.Clamp(Math.Log10(Math.Max(0, BareUni(b) * _z - 1) * mc / next.Total + 1) / 1.5, 0.0, 1.0);
                        Offer(ref keep, sc, b, i, null);
                    }
                    for (int p = LowerBound(_fKeys, rest); p < _fKeys.Length; p++)
                    {
                        var key = _fKeys[p];
                        if (exact ? key.Length != 1 || key[0] != rest[0] : !key.StartsWith(rest, StringComparison.Ordinal)) break;
                        int i = _fIdx[p];
                        if (!_e[i].Elides || !map.TryGetValue(_e[i].OrthoLower, out var pc) || !SplitPair(b, i, pc) || !Injectable(i, xe)) continue;
                        Score(i);
                    }
                    // 0.11.0 (A): « est » under the sound `e` after c' s' n' qu' (Lexique's key `e` is too short to be indexed)
                    if (estHere && rest == "e" && _estForm[b] && map.TryGetValue(_e[_estIdx].OrthoLower, out var ec) && SplitPair(b, _estIdx, ec)
                        && Injectable(_estIdx, xe))
                        Score(_estIdx);
                }
            }
        }
        if (Split3 && typed.Length >= Split3From && (Split3Known || !Known())) Split3Pass(ref keep, fold, ctx, c1, c2, xe);
        if (Expressions && typed.Length >= ExprFrom && (ExprKnown || !Known()) && !(ExprNotPrefix && StartsAWord(typed)))
            ExprPass(ref keep, fold, ctx, c1, c2, xe);
        if (keep is null) return;
        foreach (var (sc, b, i, w) in keep) Consider(best, w ?? Elision.Join(Elision.SplitForms[b], _e[i].Ortho), sc, MatchKind.Phonetic);
    }

    /// <summary>0.11.0 (A): may « est » come from the sound `e` of this typed word? 3 letters at most, the last one an e
    /// (« cé », « nè », « kè », « qué »).</summary>
    private static bool EstShape(string typed) =>
        typed.Length is > 0 and <= 3 && PhoneticKey.StripLower(typed[^1].ToString()) is "e";

    /// <summary>0.11.0: is <paramref name="typed"/> the literal start of a lexicon form (« sylv » → sylvie)? One binary search.</summary>
    private bool StartsAWord(string typed)
    {
        var lower = typed.ToLowerInvariant();
        int p = LowerBound(_aoKeys, lower);
        return p < _aoKeys.Length && _aoKeys[p].StartsWith(lower, StringComparison.Ordinal);
    }

    /// <summary>The best <see cref="SplitMax"/> glued tiles of a query, best first (a stable insertion: the first of equal
    /// scores stays ahead).</summary>
    private void Offer(ref List<(double Score, int B, int I, string? W)>? k, double sc, int b, int i, string? w)
    {
        k ??= new List<(double, int, int, string?)>(SplitMax + 1);
        for (int j = 0; j < k.Count; j++)
            if (w is null ? k[j].W is null && k[j].B == b && k[j].I == i : string.Equals(k[j].W, w, StringComparison.Ordinal)) return;   // a word may have both keys in the range
        if (k.Count == SplitMax && sc <= k[^1].Score) return;
        int at = k.Count;
        while (at > 0 && k[at - 1].Score < sc) at--;
        k.Insert(at, (sc, b, i, w));
        if (k.Count > SplitMax) k.RemoveAt(k.Count - 1);
    }

    /// <summary>
    /// 0.11.0 (C): subject + clitic + word glued (« jtaime » → je t'aime, « jemapelle » → je m'appelle, « ilsapelle » → il
    /// s'appelle). The fold starts with a subject's sound (<see cref="Elision.SubjectSounds"/>), its rest splits as a 0.10.2 glued
    /// word over m' t' s' n' l' (and « est » after s' n'); the word is looked up as the split pass does (exact for one symbol,
    /// a prefix range from two) and kept only if the corpus wrote « subject clitic word » <see cref="Split3MinCount"/> times.
    /// Score <c>ChainScore(ctx, [S, b, w]) + grammar(ctx ⊕ S ⊕ b, w) − WSplit3</c>; the tile is <c>S + " " + b'w</c>.
    /// </summary>
    private void Split3Pass(ref List<(double Score, int B, int I, string? W)>? keep, string fold, Ctx ctx, string? c1, string? c2, bool[] xe)
    {
        if (Gl.S3.Count == 0) return;
        Span<int> forms = stackalloc int[Elision.MaxSplits];
        foreach (var (sound, subjects) in Elision.SubjectSounds)
        {
            if (fold.Length <= sound.Length + 1 || !fold.StartsWith(sound, StringComparison.Ordinal)) continue;
            string rest = Split3Rest(fold, sound.Length);
            int nf = Elision.SplitsOf(rest, forms);
            for (int s = 0; s < nf; s++)
            {
                int b = forms[s];
                if (!IsSplit3Clitic(b)) continue;
                string wk = rest[Elision.SoundLength(b)..];
                bool exact = wk.Length == 1;
                for (int p = LowerBound(_fKeys, wk); p < _fKeys.Length; p++)
                {
                    var key = _fKeys[p];
                    if (exact ? key.Length != 1 || key[0] != wk[0] : !key.StartsWith(wk, StringComparison.Ordinal)) break;
                    Try(ref keep, _fIdx[p], b, subjects);
                }
                if (wk == "e" && b is 3 or 5 && _estIdx >= 0) Try(ref keep, _estIdx, b, subjects);   // the A exception after s' n'
            }
        }

        void Try(ref List<(double Score, int B, int I, string? W)>? keep, int i, int b, string[] subjects)
        {
            if (!_e[i].Elides || !Injectable(i, xe)) return;
            foreach (var subj in subjects)
            {
                if (!Split3Pair(b, i, S3Count(subj, b, _e[i].OrthoLower)) || !_idxOf.TryGetValue(subj, out var si) || xe[si]) continue;
                var bare = Elision.SplitForms[b];
                var toks = new[] { subj, bare, _e[i].OrthoLower };
                double sc = ChainScore(ctx, c1, c2, toks, new[] { _uni[si], BareUni(b), _uni[i] });
                var gt = GrammarTerm(bare, subj, c1, ref _split3Expect);
                Offer(ref keep, sc + (gt.On ? gt.Of(i) : 0) - WSplit3, -1, -1, subj + " " + Elision.Join(bare, _e[i].Ortho));
            }
        }
    }

    /// <summary>0.11.0 (C): the fold after a subject's sound; a « s' » there reads <c>z</c> after a vowel (« ellesapelle » →
    /// <c>ele</c> + <c>zapel</c>: the fold voices an s between vowels), so it is read back as <c>s</c>.</summary>
    private static string Split3Rest(string fold, int from) =>
        fold.Length > from + 1 && fold[from] == 'z' && Elision.IsVowelKey(fold[from + 1]) ? "s" + fold[(from + 1)..] : fold[from..];

    private static bool IsSplit3Clitic(int b)
    {
        foreach (var c in Elision.Split3Clitics) if (c == b) return true;
        return false;
    }

    /// <summary>
    /// 0.11.0 (B): the fixed expressions whose sound key the typed fold starts (« ilya » → il y a, « parcont » → par contre),
    /// the fold longer than the first word's part (« il » never shows « il y a »). Score <c>ChainScore(ctx, tokens) − WExpr</c>.
    /// </summary>
    private void ExprPass(ref List<(double Score, int B, int I, string? W)>? keep, string fold, Ctx ctx, string? c1, string? c2, bool[] xe)
    {
        if (_exprKeys.Length == 0 || fold.Length < 2) return;
        List<int>? seen = null;
        for (int p = LowerBound(_exprKeys, fold); p < _exprKeys.Length && _exprKeys[p].StartsWith(fold, StringComparison.Ordinal); p++)
        {
            int r = _exprKeyRow[p];
            if (fold.Length <= _exprKeyFirst[p] || seen is not null && seen.Contains(r) || !ExprOn(r, xe)) continue;
            (seen ??= new List<int>(4)).Add(r);
            var row = _expr[r];
            var unis = new double[row.Tok.Length];
            for (int k = 0; k < unis.Length; k++) unis[k] = TokenUni(row.Tok[k], row.Idx[k]);
            Offer(ref keep, ChainScore(ctx, c1, c2, row.Tok, unis) - WExpr, -1, -1, row.Written);
        }
    }

    /// <summary>
    /// 0.10.2: the second word of a chain (« aime » after « je t ») is scored by <see cref="Lm"/> with the context and the
    /// bare form, whose levels are discounted from the 4-gram down. With fewer than three words of context the top levels
    /// cannot exist at all, and every candidate of the query carries that missing-level discount once — the chain would carry
    /// it twice (« jai » at a sentence start: α³ for « j », then α² for « ai » after it). This gives back, for the chain's
    /// second word only, the levels its context cannot reach (<c>WLm × m × log10(1 / Alpha)</c>, m = the levels above the
    /// longest context it has, or the tables have), so that a chain and a single word compare as probabilities with the same
    /// offset (a deviation from the plan's literal formula, see development/evaluation.md § Glued words).
    /// </summary>
    private double ChainLift(string? c1, string? c2)
    {
        int reach = UseFourgrams && _hasFour ? 3 : _hasTri ? 2 : _hasBi ? 1 : 0;   // the context words a level of the tables can use
        int have = c1 is null ? 1 : c2 is null ? 2 : 3;                           // the context with the bare form at its end
        int missing = 3 - Math.Min(have, reach);                                  // Lm's scale: 4-gram 1, trigram α, bigram α², unigram α³
        return missing <= 0 || Alpha <= 0 ? 0 : WLm * missing * -Math.Log10(Alpha);
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
    /// ≈ 24 000 rarer first names, a key array of their own that other queries walk only from
    /// <see cref="NamesRecognisedFrom"/> letters, above <see cref="NamesRecognisedFloor"/>, 0.10.2). Without that capital a name
    /// needs <see cref="NamesFrom"/> letters. A vowel-initial name takes the elision boost after « d' », « l' » (D22). A
    /// variant (« Ieper ») brings itself and the name it stands for (« Ypres »). A name whose lowercase homograph is a rare
    /// lexicon form (<see cref="NameMergeBelow"/>: « belgique ») replaces it. As the score only depends on the frequency
    /// within one query, the best k + 4 names of the prefix range are chosen by frequency and only they are scored.
    /// </summary>
    private void NamesPass(NamesLexicon n, Dictionary<string, (double, MatchKind)> best, string typed, double eb,
                           bool sentenceStart, int k, ExcludedWords x)
    {
        bool capital = !sentenceStart && char.IsUpper(typed[0]);
        if (!capital && typed.Length < NamesFrom) return;
        double mult = capital ? NameCapitalLift : sentenceStart ? 1.0 : NameLowercaseShare;
        // 0.10.2: no capital that tells a name, enough letters → the recognised tier above its floor, no restricted figure
        float floor = (float)(n.FreqCap * NamesRecognisedFloor);
        int keep = k + 4;
        var top = new List<(float Freq, int Idx)>(keep + 1);
        var lo = typed.ToLowerInvariant();
        void Walk(string[] keys, int[] idx, bool rare)
        {
            for (int pos = LowerBound(keys, lo); pos < keys.Length && keys[pos].StartsWith(lo, StringComparison.Ordinal); pos++)
            {
                int i = idx[pos];
                ref readonly var e0 = ref n.Entries[i];
                if (e0.Restricted && (rare || typed.Length < 3)) continue;   // owner 2026-11-15: a capital and 3 letters at least
                float f = e0.Freq;
                if (rare && f < floor) continue;
                if (top.Count == keep && f <= top[^1].Freq) continue;
                int at = top.Count;
                while (at > 0 && top[at - 1].Freq < f) at--;
                top.Insert(at, (f, i));
                if (top.Count > keep) top.RemoveAt(top.Count - 1);
            }
        }
        Walk(n.Keys1, n.Idx1, false);
        if (capital) Walk(n.Keys2, n.Idx2, false);
        else if (typed.Length >= NamesRecognisedFrom) Walk(n.Keys2, n.Idx2, true);
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
            // 0.11.0: a pack name with a listed spelling is not offered, but the owner's exempted ones (2026-11-27)
            if (x.IsExcluded(form) && !x.NameExempt(form)) return;
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
                          Ctx ctx, double eb, double share, bool sentenceStart, GTerm gt, ExcludedWords x)
    {
        // 0.11.0: a personal word or a « Mes fautes » target on the list is never offered (a grammar-only form may be one)
        bool xOn = !x.IsEmpty;
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
                if (xOn && x.IsExcluded(e.Form)) continue;
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
                        if (xOn && x.IsExcluded(e.Form)) continue;
                        Consider(best, e.Form, Score(e.Lower, W(e), out _, e.Name) - WEdit + B(e), MatchKind.Edit);
                    }
        if (u.FixKeys.Length > 0)
        {
            var key = PhoneticKey.StripLower(typed);
            foreach (var pos in PrefixRange(u.FixKeys, key))
            {
                if (u.FixKeys[pos].Length != key.Length && key.Length < 3) continue;   // a prefix counts from 3 letters (Q12)
                var target = u.FixTargets[pos];
                if (xOn && x.IsExcluded(target)) continue;
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
    private void Inject(Dictionary<string, (double, MatchKind)> best, string typed, GTerm gt, TopSources top, bool lazy, bool[] xe)
    {
        var g = gt.G;
        // (1) first, so that a family member keeps the « sounds like » colour
        var lower = typed.ToLowerInvariant();
        if (HomophoneFamilies.Of(lower) is { } fam && _idxOf.TryGetValue(lower, out int ti) && best.TryGetValue(_e[ti].Ortho, out var typedHave))
        {
            double raw = lazy ? typedHave.Item1 : typedHave.Item1 - gt.Of(ti);
            foreach (var m in fam)
            {
                if (m == lower || !HomophoneFamilies.RuleMayChoose(m) || !_idxOf.TryGetValue(m, out int mi) || xe[mi] || gt.Agrees(mi) <= 0) continue;
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
                    if (f == src || FirstLetter(_e[f].OrthoLower) != first || !Injectable(f, xe) || gt.AgreesAs(f, lem[k], classes) <= 0) continue;
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

    /// <summary>A lexicon form the bar may receive as an added agreeing form: not excluded (0.11.0), not an elision or noise
    /// form, and a form of the spelling reference (never « yeah »).</summary>
    private bool Injectable(int i, bool[] xe) =>
        !xe[i] && !Elision.IsBare(_e[i].Ortho) && !Elision.IsNoise(_e[i].Ortho) && !(_unlisted.Count > 0 && _unlisted.Contains(_e[i].OrthoLower));

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
        var x = _excluded;   // 0.11.0: one snapshot for the query and its safety net
        return Net(PredictNextCore(context, k, x), x);
    }

    // ---- 0.11.0: words after a typed hyphen (plan hyphen-names, owner 2026-11-27: « saint-n » → Saint-Nicolas; Q1 = B, the
    // whole name). A typed « - » has always ended the word: the bar then predicted the part after it alone (« n » → « ne »).
    /// <summary>0.11.0: the compounds of the word before a typed hyphen (<see cref="PredictAfterHyphen"/>) on and off; off =
    /// the results of <see cref="Predict"/> / <see cref="PredictNext"/> byte for byte.</summary>
    public bool HyphenCompounds = true;
    /// <summary>0.11.0: at most this many compound tiles ahead of the plain words. 5 (the plan's fallback): unbounded, a head
    /// with many compounds (« ex- », « sous- », « vice- ») filled the bar and pushed out a next word its context made sure of
    /// (`--hyphen-report` E2: hit@8 with 0 letters 0.667 → 0.548); with 5 every set's hit@8 holds or rises
    /// (development/evaluation.md § Noms à trait d'union).</summary>
    public int HyphenMax = 5;
    /// <summary>0.11.0: a head longer than this (letters and inner hyphens) is no word a compound starts with.</summary>
    public const int HyphenHeadMax = 40;
    /// <summary>0.11.0: a word typed with no capital that tells a name (« saint », or « Saint » at a sentence start), 4 letters
    /// or more, that starts a known hyphenated name gets that name in the bar's last place (<see cref="CompoundSlot"/>): the
    /// pupil sees « Saint-Marin » before typing the hyphen. Off = the results without it, byte for byte.</summary>
    public bool CompoundNameSlot = true;
    private long _compoundSlotFired;
    /// <summary>0.11.0: how many bars the compound name slot changed (Predict.Eval prints it).</summary>
    public long CompoundSlotFired => Interlocked.Read(ref _compoundSlotFired);

    /// <summary>
    /// 0.11.0: the bar right after « <paramref name="head"/>- » (the keyboard: the word before a single typed hyphen, letters
    /// joined by single hyphens), <paramref name="typed"/> being the letters after it (may be empty). Today's list — the
    /// prediction of <paramref name="typed"/> alone (<see cref="Predict"/>), or the next word when nothing is typed yet
    /// (<see cref="PredictNext"/>) — gets the known compounds « head-… », as <see cref="Predict"/> finds them for « head-typed »
    /// with no context (literal prefixes only): the names' (« Saint-Nicolas », pack forms and capitalised personal words) go
    /// ahead of today's list; the lexicon's (« porte-monnaie ») are merged with it by score, never ahead of its first word
    /// (review: « va-[t] », « qu'est-[c] » keep « t », « ce »), and one whose ending is one of today's words is dropped
    /// (« être » after « peut- » writes « peut-être »); a misspelt one (« est-ce-que ») never shows. A name or capitalised personal word keeps its own form (« saint-n » → « Saint-Nicolas »: the keyboard
    /// replaces the head where it can read the text); a lexicon compound is written with the head as typed and its ending
    /// cased like the typed letters (« Peut-ê » → « Peut-être »). The capital of the head (inside a sentence:
    /// <paramref name="headNeutral"/> false) asks for names; a lowercase head gets the names' full share, not
    /// <see cref="NameLowercaseShare"/> (the hyphen already says « compound »). A restricted figure (D30) needs a capitalised
    /// head inside a sentence and 3 letters after the hyphen. A plain word equal to a name's ending (« nicolas » after
    /// « Saint-Nicolas ») is dropped (both write the same text); the exact typed word kept in the bar by the Present rule stays
    /// in it. At most <see cref="HyphenMax"/> compounds.
    /// Off (<see cref="HyphenCompounds"/>), an empty head or one over <see cref="HyphenHeadMax"/>: today's list unchanged.
    /// </summary>
    /// <param name="sentenceStart">as <see cref="Predict"/>'s, for today's list (the letters after the hyphen).</param>
    /// <param name="headNeutral">a capital on the head says nothing about a name (a sentence start, after « : », unknown).</param>
    public IReadOnlyList<Suggestion> PredictAfterHyphen(string head, string typed, IReadOnlyList<string>? context, int k = 8,
                                                        bool sentenceStart = false, bool headNeutral = false, bool? exactMark = null)
    {
        var x = _excluded;   // one snapshot for both queries and the safety net
        typed = (typed ?? "").Trim();
        bool on = HyphenCompounds && !string.IsNullOrEmpty(head) && head.Length <= HyphenHeadMax && HyphenMax > 0 && k > 0;
        // after a hyphen the word is already a compound: no « typed-… » name slot for the letters after it
        var today = typed.Length > 0 ? PredictCore(typed, context, k, sentenceStart, exactMark, x, slot: !on) : PredictNextCore(context, k, x);
        if (!on) return Net(today, x);
        bool capHead = char.IsUpper(head[0]) && !headNeutral;
        var pre = head + "-";
        var found = PredictCore(pre + typed, null, k + 4, !capHead, false, x, prefixOnly: true);
        int max = Math.Min(k, HyphenMax);
        // names (pack forms, capitalised personal words) go ahead of today's list; the lexicon's compounds, found without the
        // context, are merged with it by score and never ahead of its top-1 (review: « va-[t] », « qu'est-[c] »)
        var names = new List<Suggestion>(max);
        var lex = new List<Suggestion>(max);
        foreach (var s in found)
        {
            if (names.Count + lex.Count >= max) break;
            if (s.Kind != MatchKind.Prefix || s.Word.Length <= pre.Length || !s.Word.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) continue;
            if (_names.IsRestricted(s.Word) && !(capHead && typed.Length >= 3)) continue;   // D30
            bool name = char.IsUpper(s.Word[0]);
            if (!name && MisspeltCompound(s.Word)) continue;
            var w = name ? s.Word : pre + CaseLike(s.Word[pre.Length..], typed, head);
            bool dup = false;
            foreach (var c in names) if (string.Equals(c.Word, w, StringComparison.Ordinal)) { dup = true; break; }
            foreach (var c in lex) if (string.Equals(c.Word, w, StringComparison.Ordinal)) { dup = true; break; }
            if (!dup) (name ? names : lex).Add(s with { Word = w });
        }
        // a lexicon compound whose ending is one of today's words writes the same text: today's word stays
        lex.RemoveAll(c => today.Any(t => string.Equals(t.Word, c.Word[pre.Length..], StringComparison.OrdinalIgnoreCase)));
        if (names.Count == 0 && lex.Count == 0) return Net(today, x);

        bool NameEnds(string word)
        {
            foreach (var c in names) if (string.Equals(c.Word[pre.Length..], word, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        var o = new List<Suggestion>(k + 1);
        o.AddRange(names);
        var rest = new List<Suggestion>(today.Count);
        foreach (var s in today)
        {
            if (NameEnds(s.Word)) continue;   // a name tile writes it (« nicolas » after « Saint-Nicolas »)
            bool dup = false;
            foreach (var c in o) if (string.Equals(c.Word, s.Word, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            if (!dup) rest.Add(s);
        }
        // today's top-1 first, then today's order and the lexicon compounds merged by score
        int i0 = 0, j0 = 0;
        if (rest.Count > 0) o.Add(rest[i0++]);
        while (o.Count < k + 1 && (i0 < rest.Count || j0 < lex.Count))
            o.Add(j0 < lex.Count && (i0 >= rest.Count || lex[j0].Score > rest[i0].Score) ? lex[j0++] : rest[i0++]);
        // the exact typed word the Present rule kept in the bar stays in it (last place), unless a name writes it
        int bar = Math.Max(1, ExactBar);
        if (ExactTyped == ExactTypedMode.Present && exactMark != false && k >= ExactBar && ExactBar >= 2 && typed.Length > 0)
        {
            int at = -1;
            for (int i = 0; i < Math.Min(bar, today.Count); i++)
                if (string.Equals(today[i].Word, typed, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            if (at >= 0 && !NameEnds(today[at].Word))
            {
                int now = o.FindIndex(s => string.Equals(s.Word, today[at].Word, StringComparison.Ordinal));
                if (now < 0 || now >= bar)
                {
                    if (now >= 0) o.RemoveAt(now);
                    o.Insert(Math.Min(bar - 1, o.Count), today[at]);
                }
            }
        }
        if (o.Count > k) o.RemoveRange(k, o.Count - k);
        return Net(o, x);
    }

    /// <summary>0.11.0 (review): a lexicon compound that is a frequent misspelling of a group the bar writes better —
    /// « est-ce-que » for « est-ce que » (the corpus spelling; « qu'est-c » must give « ce »). A small deny rule in code,
    /// no data change; names are never denied.</summary>
    private static bool MisspeltCompound(string word)
    {
        var w = word.ToLowerInvariant();
        return w.EndsWith("-ce-que", StringComparison.Ordinal) || w.Contains("-ce-qu'", StringComparison.Ordinal) || w.Contains("-ce-qu’", StringComparison.Ordinal);
    }

    /// <summary>0.11.0: a lexicon compound's ending cased like the letters typed after the hyphen (« Ê » → « Être », « ÊT » →
    /// « ÊTRE »); a head in capitals (« PEUT », « SAINT ») gives capitals with none or capitals typed.</summary>
    private static string CaseLike(string ending, string typed, string head)
    {
        if (ending.Length == 0) return ending;
        static bool AllCaps(string s)
        {
            bool any = false;
            foreach (var c in s) { if (!char.IsLetter(c)) continue; if (!char.IsUpper(c)) return false; any = true; }
            return any;
        }
        bool capsHead = head.Length > 1 && AllCaps(head);
        if (typed.Length == 0) return capsHead ? ending.ToUpperInvariant() : ending;
        if (!char.IsUpper(typed[0])) return ending;
        if (capsHead && AllCaps(typed)) return ending.ToUpperInvariant();   // « SAINT-N » → « SAINT-NECTAIRE »
        if (typed.Length > 1 && AllCaps(typed)) return ending.ToUpperInvariant();
        return char.ToUpperInvariant(ending[0]) + ending[1..];
    }

    /// <summary>
    /// 0.11.0 (<see cref="CompoundNameSlot"/>): after the ranking and the exact typed word rule, a word typed with no capital
    /// that tells a name (lowercase, or at a sentence start), letters only, 4 or more, with no name « typed-… » among the bar's
    /// first <see cref="ExactBar"/> places, gets the best such name in the bar's last place (the one before when the Present
    /// rule put the typed word there): a capitalised personal word first (the shortest), else the pack's most popular
    /// predicted-tier name (then the shortest) — never a restricted figure (D30), never an excluded one unless exempted.
    /// Only on a full bar (k ≥ <see cref="ExactBar"/>, as the Present rule), so the top-1 (KSS) never changes.
    /// </summary>
    private Suggestion[] CompoundSlot(Suggestion[] res, string typed, int k, bool sentenceStart, UserLexicon u, NamesLexicon nm, ExcludedWords x)
    {
        int bar = ExactBar;
        typed = (typed ?? "").Trim();
        if (k < bar || bar < 2 || typed.Length < 4 || !sentenceStart && char.IsUpper(typed[0])) return res;
        foreach (var ch in typed) if (!char.IsLetter(ch)) return res;
        var pre = typed.ToLowerInvariant() + "-";
        for (int i = 0; i < Math.Min(bar, res.Length); i++)
            if (res[i].Word.Length > 0 && char.IsUpper(res[i].Word[0]) && res[i].Word.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return res;
        string? best = null;
        if (!u.IsEmpty)
            for (int p = LowerBound(u.LowerKeys, pre); p < u.LowerKeys.Length && u.LowerKeys[p].StartsWith(pre, StringComparison.Ordinal); p++)
            {
                var f = u.Entries[u.LowerIdx[p]].Form;
                if (f.Length == 0 || !char.IsUpper(f[0]) || Blocked(x, f)) continue;
                if (best is null || f.Length < best.Length) best = f;
            }
        if (best is null && !nm.IsEmpty)
        {
            float bf = -1;
            for (int p = LowerBound(nm.Keys1, pre); p < nm.Keys1.Length && nm.Keys1[p].StartsWith(pre, StringComparison.Ordinal); p++)
            {
                ref readonly var e = ref nm.Entries[nm.Idx1[p]];
                if (e.Restricted || e.IsVariant || x.IsExcluded(e.Form) && !x.NameExempt(e.Form)) continue;
                if (e.Freq > bf || e.Freq == bf && e.Form.Length < best!.Length) { best = e.Form; bf = e.Freq; }
            }
        }
        if (best is null) return res;
        var list = new List<Suggestion>(res.Length + 1);
        foreach (var s in res) if (!string.Equals(s.Word, best, StringComparison.Ordinal)) list.Add(s);
        int at = Math.Min(bar - 1, list.Count);
        double score = list.Count == 0 ? -20 : list[^1].Score - 0.001;
        var name = new Suggestion(best, score, MatchKind.Prefix, true);
        if (at == bar - 1 && at < list.Count && string.Equals(list[at].Word, typed, StringComparison.OrdinalIgnoreCase))
        {
            // the Present rule's typed word keeps the last place; the name goes just before it
            var exactWord = list[at];
            list.RemoveAt(at);
            list.Insert(at - 1, name);
            list.Insert(at, exactWord);
        }
        else list.Insert(at, name);
        Interlocked.Increment(ref _compoundSlotFired);
        if (list.Count > k) list.RemoveRange(k, list.Count - k);
        return list.ToArray();
    }

    private IReadOnlyList<Suggestion> PredictNextCore(IReadOnlyList<string>? context, int k, ExcludedWords x)
    {
        var xe = Xe(x);
        bool xOn = !x.IsEmpty;
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
            // 0.11.0: an excluded successor — a lexicon entry, or a form known only to grammar.sqlite (2 in the bigrams, 1 in
            // the trigrams, measured 2026-11-27) — is never the next word
            if (xOn && (known ? xe[i] : x.IsExcluded(w))) return;
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
