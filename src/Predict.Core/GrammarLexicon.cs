using Microsoft.Data.Sqlite;

namespace Predict.Core;

/// <summary>
/// Plan 11 (10D-0): the morphology of the prediction lexicon's forms, read from <c>grammar.sqlite</c> (table
/// <c>pred_index</c>, derived from Grammalecte's French.lex, MPL-2.0) and indexed by the entries of one
/// <see cref="PredictionEngine"/>. Every analysis of a form (« est » = être 3s and the noun « est ») is a pair (lemma id,
/// <see cref="MorphBits"/>); analyses with the same lemma and part of speech were OR-merged at build time. Immutable
/// arrays only (CSR layout): built once on a worker thread, then read by any thread. <see cref="Empty"/> = no grammar
/// (prediction exactly as 0.8.3). In 10D-0 nothing scores with it yet.
/// </summary>
public sealed class GrammarLexicon
{
    public const int Schema = 1;

    private readonly int[] _off;        // entry → first analysis (n + 1 values); empty when there is no grammar
    private readonly uint[] _bits;
    private readonly int[] _lemma;
    private readonly int[] _lemmaOff;   // lemma id → first of its lexicon forms (lemma count + 1 values)
    private readonly int[] _lemmaForms; // entry indices, by lemma

    public static readonly GrammarLexicon Empty = new(Array.Empty<int>(), Array.Empty<uint>(), Array.Empty<int>(),
                                                      Array.Empty<int>(), Array.Empty<int>(), 0, "");

    /// <summary>Lexicon forms that have at least one analysis.</summary>
    public int FormCount { get; }
    /// <summary>Lemmas of grammar.sqlite (ids 0 … LemmaCount − 1).</summary>
    public int LemmaCount => Math.Max(0, _lemmaOff.Length - 1);
    public int AnalysisCount => _bits.Length;
    public bool IsEmpty => _bits.Length == 0;
    /// <summary>The <c>meta.source</c> line of the file (for « À propos » / logs: no text of the pupil).</summary>
    public string Source { get; }

    private GrammarLexicon(int[] off, uint[] bits, int[] lemma, int[] lemmaOff, int[] lemmaForms, int formCount, string source)
    {
        _off = off; _bits = bits; _lemma = lemma; _lemmaOff = lemmaOff; _lemmaForms = lemmaForms; FormCount = formCount; Source = source;
    }

    /// <summary>The analyses' bits of an engine entry (empty when the form is unknown to French.lex, or no grammar).</summary>
    public ReadOnlySpan<uint> BitsOf(int entry) =>
        entry < 0 || entry + 1 >= _off.Length ? ReadOnlySpan<uint>.Empty : _bits.AsSpan(_off[entry], _off[entry + 1] - _off[entry]);

    /// <summary>The lemma id of each analysis of <see cref="BitsOf"/> (same order).</summary>
    public ReadOnlySpan<int> LemmasOf(int entry) =>
        entry < 0 || entry + 1 >= _off.Length ? ReadOnlySpan<int>.Empty : _lemma.AsSpan(_off[entry], _off[entry + 1] - _off[entry]);

    /// <summary>The engine entries (lexicon forms only) of a lemma, in entry order.</summary>
    public ReadOnlySpan<int> FormsOfLemma(int lemmaId) =>
        lemmaId < 0 || lemmaId + 1 >= _lemmaOff.Length ? ReadOnlySpan<int>.Empty : _lemmaForms.AsSpan(_lemmaOff[lemmaId], _lemmaOff[lemmaId + 1] - _lemmaOff[lemmaId]);

    /// <summary>
    /// Reads <c>pred_index</c> of <paramref name="path"/> and maps each form to <paramref name="eng"/>'s entry. Meant for a
    /// worker thread (≈ 120 000 rows); the connection is closed on return (no pooling: the file can be replaced after).
    /// Throws <see cref="InvalidDataException"/> for another schema.
    /// </summary>
    public static GrammarLexicon Load(string path, PredictionEngine eng)
    {
        var rows = new List<(int Entry, (int L, uint B)[] A)>(125_000);
        int lemmas;
        string source;
        using (var con = Open(path, out source, out lemmas))
        using (var c = con.CreateCommand())
        {
            c.CommandText = "SELECT ortho, a FROM pred_index";
            using var r = c.ExecuteReader();
            var seen = new bool[eng.Count];
            while (r.Read())
            {
                int i = eng.IndexOf(r.GetString(0).ToLowerInvariant());
                // review 10D-0: a second row for an entry already set (two spellings with one lowercase form) is skipped,
                // never added twice
                if (i < 0 || seen[i]) continue;
                seen[i] = true;
                var a = (byte[])r.GetValue(1);
                var an = new (int, uint)[a.Length / 8];
                for (int k = 0; k < an.Length; k++)
                    an[k] = ((int)BitConverter.ToUInt32(a, 8 * k), BitConverter.ToUInt32(a, 8 * k + 4));
                rows.Add((i, an));
            }
        }
        return Build(rows, lemmas, eng.Count, source);
    }

    private static SqliteConnection Open(string path, out string source, out int lemmas)
    {
        var con = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        con.Open();
        string Meta(string key)
        {
            using var c = con.CreateCommand();
            c.CommandText = "SELECT value FROM meta WHERE key = $k";
            c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string ?? "";
        }
        var schema = Meta("schema");
        if (schema != Schema.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            con.Dispose();
            throw new InvalidDataException($"grammar.sqlite: schema {schema}, expected {Schema}");
        }
        source = Meta("source");
        using (var c = con.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*) FROM lemma";
            lemmas = Convert.ToInt32(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        return con;
    }

    /// <summary>Our modal lemmas (bit 21), as build_grammar.MODALS.</summary>
    private static readonly HashSet<string> Modals = new(StringComparer.Ordinal) { "pouvoir", "vouloir", "devoir", "aller", "savoir", "falloir" };

    /// <summary>
    /// Review 10D-0 (a measurement for the 10D-1 choice; the app does not use it): the same lexicon built without
    /// <c>pred_index</c>. Every engine form is looked up in <c>morph</c> at load with build_grammar.lookup's normalisation
    /// (as it is, lowercase, « oe » → « œ », ' → ’) and its analyses are OR-merged per (lemma, POS) here, so a shipped file
    /// without <c>pred_index</c> would hold French.lex-derived data only. Gives the same arrays as <see cref="Load"/>
    /// (<see cref="SameAs"/>).
    /// </summary>
    internal static GrammarLexicon LoadFromMorph(string path, PredictionEngine eng)
    {
        int lemmas;
        string source;
        var byForm = new Dictionary<string, List<(int L, int T)>>(480_000, StringComparer.Ordinal);
        uint[] tb1, tb2;
        bool[] modal;
        using (var con = Open(path, out source, out lemmas))
        {
            modal = new bool[lemmas];
            using (var c = con.CreateCommand())
            {
                c.CommandText = "SELECT id, lemma FROM lemma";
                using var r = c.ExecuteReader();
                while (r.Read()) if (Modals.Contains(r.GetString(1))) modal[r.GetInt32(0)] = true;
            }
            int tags;
            using (var c = con.CreateCommand())
            {
                c.CommandText = "SELECT COUNT(*) FROM tag";
                tags = Convert.ToInt32(c.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            }
            tb1 = new uint[tags];
            tb2 = new uint[tags];
            using (var c = con.CreateCommand())
            {
                c.CommandText = "SELECT id, bits, bits2 FROM tag";
                using var r = c.ExecuteReader();
                while (r.Read()) { int id = r.GetInt32(0); tb1[id] = (uint)r.GetInt64(1); tb2[id] = (uint)r.GetInt64(2); }
            }
            using (var c = con.CreateCommand())
            {
                c.CommandText = "SELECT form, lemma_id, tag_id FROM morph";
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var f = r.GetString(0);
                    if (!byForm.TryGetValue(f, out var l)) byForm[f] = l = new List<(int, int)>(2);
                    l.Add((r.GetInt32(1), r.GetInt32(2)));
                }
            }
        }
        var rows = new List<(int Entry, (int L, uint B)[] A)>(125_000);
        var merged = new Dictionary<(int L, uint Pos), uint>();
        for (int e = 0; e < eng.Count; e++)
        {
            var hit = Lookup(eng.FormAt(e), byForm);
            if (hit is null) continue;
            merged.Clear();
            foreach (var (l, t) in hit)
                for (int part = 0; part < 2; part++)
                {
                    uint b = part == 0 ? tb1[t] : tb2[t];
                    if (b == 0) continue;
                    if (modal[l] && MorphBits.IsVerbal(b)) b |= MorphBits.Modal;
                    var k = (l, MorphBits.Pos(b));
                    merged[k] = merged.TryGetValue(k, out var o) ? o | b : b;
                }
            rows.Add((e, merged.OrderBy(x => x.Key.L).ThenBy(x => x.Key.Pos).Select(x => (x.Key.L, x.Value)).ToArray()));
        }
        return Build(rows, lemmas, eng.Count, source);

        static List<(int L, int T)>? Lookup(string form, Dictionary<string, List<(int L, int T)>> known)
        {
            var c = new List<string>(6) { form, form.ToLowerInvariant() };
            for (int i = 0, n = c.Count; i < n; i++) if (c[i].Contains("oe", StringComparison.Ordinal)) c.Add(c[i].Replace("oe", "œ", StringComparison.Ordinal));
            for (int i = 0, n = c.Count; i < n; i++) if (c[i].Contains('\'')) c.Add(c[i].Replace('\'', '’'));
            foreach (var x in c) if (known.TryGetValue(x, out var l)) return l;
            return null;
        }
    }

    /// <summary>Review 10D-0: the same analyses for every entry and the same lemma → forms lists.</summary>
    internal bool SameAs(GrammarLexicon o) =>
        _off.AsSpan().SequenceEqual(o._off) && _bits.AsSpan().SequenceEqual(o._bits) && _lemma.AsSpan().SequenceEqual(o._lemma) &&
        _lemmaOff.AsSpan().SequenceEqual(o._lemmaOff) && _lemmaForms.AsSpan().SequenceEqual(o._lemmaForms);

    private static GrammarLexicon Build(List<(int Entry, (int L, uint B)[] A)> rows, int lemmas, int n, string source)
    {
        var off = new int[n + 1];
        foreach (var (e, a) in rows) off[e + 1] = a.Length;
        for (int i = 0; i < n; i++) off[i + 1] += off[i];
        var bits = new uint[off[n]];
        var lemma = new int[off[n]];
        var lemmaCount = new int[lemmas + 1];
        foreach (var (e, a) in rows)
        {
            int at = off[e];
            foreach (var (l, b) in a)
            {
                if ((uint)l >= (uint)lemmas) throw new InvalidDataException("grammar.sqlite: lemma id out of range");
                lemma[at] = l;
                bits[at++] = b;
            }
        }
        // lemma → its lexicon forms (each form once per lemma, even with two parts of speech for it)
        for (int e = 0; e < n; e++)
            for (int k = off[e]; k < off[e + 1]; k++)
                if (!SeenBefore(lemma, off[e], k)) lemmaCount[lemma[k] + 1]++;
        for (int l = 0; l < lemmas; l++) lemmaCount[l + 1] += lemmaCount[l];
        var forms = new int[lemmaCount[lemmas]];
        var fill = (int[])lemmaCount.Clone();
        for (int e = 0; e < n; e++)
            for (int k = off[e]; k < off[e + 1]; k++)
                if (!SeenBefore(lemma, off[e], k)) forms[fill[lemma[k]]++] = e;
        return new GrammarLexicon(off, bits, lemma, lemmaCount, forms, rows.Count, source);

        static bool SeenBefore(int[] lemma, int from, int k)
        {
            for (int j = from; j < k; j++) if (lemma[j] == lemma[k]) return true;
            return false;
        }
    }
}
