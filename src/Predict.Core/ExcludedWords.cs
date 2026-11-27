using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Predict.Core;

/// <summary>
/// 0.11.0 (plan offensive-words, decision D35 « Mots exclus »): the words the engine never <b>offers</b> — in the bar, the
/// next-word prediction and the ≈ list — read from the shipped <c>exclusions\fr.txt</c> (a reviewed list derived from
/// Wiktionnaire's usage labels, CC BY-SA 4.0). A word the pupil types is still accepted, checked ✓ and read like any other.
/// An immutable snapshot of three parts kept apart — the file's forms, the grammar forms added at load
/// (<see cref="WithGrammarForms"/>), the teacher's forms (<see cref="WithExtra"/>, Phase B) — so that rebuilding one part
/// never loses another; the engine swaps snapshots in one place (<see cref="PredictionEngine.UpdateExcluded"/>). Every query
/// reads one snapshot. <b>No member returns or logs a word</b>: counts and a SHA-256 only.
/// </summary>
public sealed class ExcludedWords
{
    /// <summary>The first line of the file (the same as <c>build_exclusions.py</c>'s <c>HEADER</c>).</summary>
    public const string Header = "#pepite-exclusions/1 base64-utf8";
    public const string LemmasMark = "#lemmas";
    public const string NamesMark = "#names";
    public const string CategoriesMark = "#categories";
    /// <summary>The file looked for in a folder <c>exclusions</c> next to the database.</summary>
    public const string FileName = "fr.txt";
    public const string FolderName = "exclusions";

    private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
    private readonly HashSet<string> _listed;    // the file's forms
    private readonly string[] _grammar;          // the grammar forms added at load
    private readonly string[] _extra;            // Phase B: the teacher's forms
    private readonly HashSet<string> _forms;     // the union: what IsExcluded reads
    private readonly HashSet<string> _namesOk;   // owner 2026-11-27: listed spellings a capitalised pack name keeps
    private readonly string[] _lemmas;

    /// <summary>Per engine entry: excluded (sized for the engine it was built for; empty for <see cref="Empty"/>). The
    /// lexicon passes read it before scoring, so an excluded entry costs one array read.</summary>
    internal bool[] Entry { get; }

    /// <summary>No word excluded (the results of 0.10.2).</summary>
    public static readonly ExcludedWords Empty = new(new HashSet<string>(Cmp), Array.Empty<string>(), Array.Empty<string>(),
                                                     new HashSet<string>(Cmp), Array.Empty<string>(), Array.Empty<bool>(), "",
                                                     new Dictionary<char, int>());

    /// <summary>The excluded forms (case ignored): the listed ones, the grammar forms added at load, the extra ones.</summary>
    public int Count => _forms.Count;
    /// <summary>The excluded lemmas of the file (expanded with grammar.sqlite by <see cref="WithGrammar"/>).</summary>
    public int LemmaCount => _lemmas.Length;
    /// <summary>The forms of the file itself.</summary>
    public int ListedCount => _listed.Count;
    /// <summary>The grammar forms of this snapshot (inflections known only to grammar.sqlite).</summary>
    public int GrammarCount { get; }
    /// <summary>The teacher's forms that are not already excluded (Phase B; <see cref="TeacherForms"/>).</summary>
    public int ExtraCount { get; }
    /// <summary>The listed spellings that a capitalised « Noms propres » pack name keeps (owner 2026-11-27).</summary>
    public int NamesExemptCount => _namesOk.Count;
    /// <summary>SHA-256 of the file (lowercase hex), "" for none.</summary>
    public string Sha256 { get; }
    /// <summary>The listed forms per category (from the file's <c>#categories</c> line: R H S D X V I G).</summary>
    public IReadOnlyDictionary<char, int> Categories { get; }
    public bool IsEmpty => _forms.Count == 0;
    /// <summary>The entries of the engine marked excluded (a check for the tests and the load log).</summary>
    public int EntryCount { get; }

    private ExcludedWords(HashSet<string> listed, string[] grammar, string[] extra, HashSet<string> namesOk, string[] lemmas,
                          bool[] entry, string sha, IReadOnlyDictionary<char, int> cats)
    {
        _listed = listed; _grammar = grammar; _extra = extra; _namesOk = namesOk; _lemmas = lemmas;
        Entry = entry; Sha256 = sha; Categories = cats;
        _forms = new HashSet<string>(listed, Cmp);
        int g = 0, x = 0;
        foreach (var f in grammar) if (_forms.Add(f)) g++;
        foreach (var f in extra) if (_forms.Add(f)) x++;
        GrammarCount = g; ExtraCount = x;
        int n = 0;
        foreach (var b in entry) if (b) n++;
        EntryCount = n;
    }

    /// <summary>
    /// Is <paramref name="word"/> excluded? The whole word (case ignored), then the part after each apostrophe (« l'… »,
    /// « qu'… »: a joined candidate is excluded with its word). A few hash lookups; no allocation without an apostrophe.
    /// </summary>
    public bool IsExcluded(string? word)
    {
        if (string.IsNullOrEmpty(word) || _forms.Count == 0) return false;
        if (_forms.Contains(word)) return true;
        var s = word.AsSpan();
        int a = s.IndexOfAny('\'', '’');
        if (a < 0) return false;
        var lookup = _forms.GetAlternateLookup<ReadOnlySpan<char>>();
        for (int i = a; i < s.Length - 1; i++)
            if (s[i] is '\'' or '’' && lookup.Contains(s[(i + 1)..])) return true;
        return false;
    }

    /// <summary>
    /// Owner 2026-11-27: may this <b>pack name</b> be offered although its letters are a listed form? Only a form written
    /// with a capital (as the « Noms propres » pack stores names) whose spelling the file's <c>#names</c> section exempts;
    /// never the lowercase lexicon word, never a personal word (the engine asks only for pack names).
    /// </summary>
    public bool NameExempt(string? form) =>
        !string.IsNullOrEmpty(form) && char.IsUpper(form[0]) && _namesOk.Count > 0 && _namesOk.Contains(form);

    /// <summary>The forms themselves — for the tests and <c>Predict.Eval --probe-exclusions</c> only, which report ids and
    /// counts, never a word.</summary>
    internal IEnumerable<string> ProbeForms => _forms;

    /// <summary>Is the entry <paramref name="i"/> of the engine this snapshot was built for excluded?</summary>
    internal bool IsEntry(int i) => (uint)i < (uint)Entry.Length && Entry[i];

    /// <summary>
    /// Reads an exclusions file for <paramref name="engine"/>: the header, an optional <c>#categories R n H n …</c> line,
    /// one base64 form (UTF-8) per line, then after <c>#lemmas</c> the excluded lemmas and after <c>#names</c> the exempt
    /// pack-name spellings, encoded the same way. Throws <see cref="InvalidDataException"/> for another file (bad header or
    /// line), <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> when it cannot be read.
    /// </summary>
    public static ExcludedWords FromFile(string path, PredictionEngine engine)
    {
        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var text = Encoding.ASCII.GetString(bytes);
        var forms = new HashSet<string>(Cmp);
        var names = new HashSet<string>(Cmp);
        var lemmas = new List<string>();
        var cats = new Dictionary<char, int>();
        bool first = true;
        int section = 0;   // 0 forms, 1 lemmas, 2 names
        int lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.TrimEnd('\r');
            if (first)
            {
                if (line != Header) throw new InvalidDataException("not a Pépite exclusions file (header)");
                first = false;
                continue;
            }
            if (line.Length == 0) continue;
            if (line == LemmasMark) { section = 1; continue; }
            if (line == NamesMark) { section = 2; continue; }
            if (line.StartsWith(CategoriesMark, StringComparison.Ordinal))
            {
                var parts = line[CategoriesMark.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                for (int k = 0; k + 1 < parts.Length; k += 2)
                    if (parts[k].Length == 1 && int.TryParse(parts[k + 1], System.Globalization.NumberStyles.None,
                                                             System.Globalization.CultureInfo.InvariantCulture, out var n))
                        cats[parts[k][0]] = n;
                continue;
            }
            if (line[0] == '#') continue;   // a later comment line
            string w;
            try { w = Encoding.UTF8.GetString(Convert.FromBase64String(line)).Normalize(NormalizationForm.FormC); }
            catch (FormatException) { throw new InvalidDataException($"exclusions file: line {lineNo} is not base64"); }
            if (w.Length == 0) continue;
            if (section == 1) lemmas.Add(w); else if (section == 2) names.Add(w); else forms.Add(w);
        }
        if (first) throw new InvalidDataException("not a Pépite exclusions file (empty)");
        return new ExcludedWords(forms, Array.Empty<string>(), Array.Empty<string>(), names, lemmas.ToArray(),
                                 Mark(forms, engine), sha, cats);
    }

    /// <summary>
    /// The load-time expansion (plan A1): every form grammar.sqlite (French.lex, MPL-2.0) has for the excluded lemmas, kept
    /// when <b>all</b> its grammar lemmas are excluded lemmas (the homograph guard of <c>build_exclusions.py</c>) and it is no
    /// prediction-lexicon form (those were decided when the list was built). The lemma spellings themselves count as forms
    /// (as in the build). Keeps the shipped list free of French.lex content: half the inflections are only there. Meant for
    /// the grammar worker thread (one read-only connection, ≈ 25 ms); throws <see cref="SqliteException"/> /
    /// <see cref="IOException"/> when the file cannot be read, so the caller can log it.
    /// </summary>
    public string[] ReadGrammarForms(string grammarPath, PredictionEngine engine)
    {
        if (_lemmas.Length == 0) return Array.Empty<string>();
        if (!File.Exists(grammarPath)) throw new FileNotFoundException("grammar.sqlite not found", grammarPath);
        var excl = new HashSet<string>(_lemmas, StringComparer.Ordinal);
        var lemmasOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);   // candidate form -> its lemmas
        using (var con = new SqliteConnection($"Data Source={grammarPath};Mode=ReadOnly;Pooling=False"))
        {
            con.Open();
            var ids = new List<long>();
            var p = string.Join(',', Enumerable.Range(0, _lemmas.Length).Select(k => "$l" + k));
            using (var c = con.CreateCommand())
            {
                c.CommandText = $"SELECT id FROM lemma WHERE lemma IN ({p})";
                for (int k = 0; k < _lemmas.Length; k++) c.Parameters.AddWithValue("$l" + k, _lemmas[k]);
                using var r = c.ExecuteReader();
                while (r.Read()) ids.Add(r.GetInt64(0));
            }
            void Read(string sql)
            {
                using var c = con.CreateCommand();
                c.CommandText = sql;
                for (int k = 0; k < _lemmas.Length; k++) c.Parameters.AddWithValue("$l" + k, _lemmas[k]);
                using var r = c.ExecuteReader();
                while (r.Read())
                {
                    var f = r.GetString(0);
                    if (!lemmasOf.TryGetValue(f, out var set)) lemmasOf[f] = set = new HashSet<string>(StringComparer.Ordinal);
                    if (!r.IsDBNull(1)) set.Add(r.GetString(1));
                }
            }
            if (ids.Count > 0)
                // every form of an excluded lemma, with every lemma that form has (ids are integers: inlined)
                Read("SELECT m2.form, l.lemma FROM morph m JOIN morph m2 ON m2.form = m.form JOIN lemma l ON l.id = m2.lemma_id " +
                     $"WHERE m.lemma_id IN ({string.Join(',', ids)})");
            // the lemma spellings: their lemmas in morph when they are a form there, none otherwise
            Read($"SELECT m.form, l.lemma FROM morph m JOIN lemma l ON l.id = m.lemma_id WHERE m.form IN ({p})");
            foreach (var l in _lemmas) if (!lemmasOf.ContainsKey(l)) lemmasOf[l] = new HashSet<string>(StringComparer.Ordinal) { l };
        }
        var o = new List<string>();
        foreach (var (f, ls) in lemmasOf)
        {
            if (ls.Count == 0 || engine.IndexOf(f.ToLowerInvariant()) >= 0) continue;   // a lexicon form: decided at build time
            bool all = true;
            foreach (var l in ls) if (!excl.Contains(l)) { all = false; break; }
            if (all) o.Add(f.Normalize(NormalizationForm.FormC));
        }
        o.Sort(StringComparer.Ordinal);
        return o.ToArray();
    }

    /// <summary>This snapshot with its grammar part <b>replaced</b> by <paramref name="forms"/> (the file's and the teacher's
    /// parts kept). Grammar forms are no lexicon forms, so the entry flags are shared.</summary>
    public ExcludedWords WithGrammarForms(IReadOnlyCollection<string> forms) =>
        forms.Count == 0 && _grammar.Length == 0 ? this
        : new ExcludedWords(_listed, forms.ToArray(), _extra, _namesOk, _lemmas, Entry, Sha256, Categories);

    /// <summary><see cref="ReadGrammarForms"/> then <see cref="WithGrammarForms"/> (Eval, Cli, tests); an unreadable file
    /// gives this snapshot back unchanged.</summary>
    public ExcludedWords WithGrammar(string grammarPath, PredictionEngine engine)
    {
        try { return WithGrammarForms(ReadGrammarForms(grammarPath, engine)); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { return this; }
    }

    /// <summary>Phase B: this snapshot with its teacher part <b>replaced</b> by <paramref name="extraForms"/> (the teacher's
    /// « Mots à ne jamais proposer », whole words), the file's and the grammar parts kept.</summary>
    public ExcludedWords WithExtra(IEnumerable<string> extraForms, PredictionEngine engine)
    {
        var extra = new List<string>();
        var seen = new HashSet<string>(Cmp);
        foreach (var f in extraForms)
        {
            var w = (f ?? "").Trim().Normalize(NormalizationForm.FormC);
            if (w.Length > 0 && seen.Add(w)) extra.Add(w);
        }
        var marked = new HashSet<string>(_listed, Cmp);
        marked.UnionWith(extra);
        return new ExcludedWords(_listed, _grammar, extra.ToArray(), _namesOk, _lemmas, Mark(marked, engine), Sha256, Categories);
    }

    /// <summary>
    /// Phase B: the forms a teacher's « Mots à ne jamais proposer » excludes: each word itself and, once
    /// <see cref="PredictionEngine.Grammar"/> is loaded, a few of its inflections — never more than the teacher meant
    /// (reviewer 2026-11-27):
    /// <list type="bullet">
    /// <item>a noun or adjective analysis brings the other <b>number</b> only: the forms of the same lemma with the same part
    /// of speech and a compatible gender (the same, or epicene) — « zorbale » (feminine) brings « zorbales », never the
    /// masculine « zorbal »;</item>
    /// <item>a verb is expanded only when the banned word <b>is</b> its infinitive (it brings the whole conjugation); a
    /// conjugated form or a participle stays alone;</item>
    /// <item>a noun / verb homograph (« trambule ») gets its noun plural through its noun analysis, not the verb's forms.</item>
    /// </list>
    /// Every added form must have no lemma beyond the banned word's own lemmas (the homograph guard: a form shared with an
    /// innocent lemma stays offered). Without the grammar, the words alone (the caller rebuilds once it is in).
    /// </summary>
    public static IReadOnlyList<string> TeacherForms(IEnumerable<string> words, PredictionEngine engine)
    {
        var o = new List<string>();
        var seen = new HashSet<string>(Cmp);
        var g = engine.Grammar;
        foreach (var raw in words)
        {
            var w = (raw ?? "").Trim().Normalize(NormalizationForm.FormC);
            if (w.Length == 0 || !seen.Add(w)) continue;
            o.Add(w);
            if (g.IsEmpty) continue;
            int i = engine.IndexOf(w.ToLowerInvariant());
            if (i < 0) continue;
            var bits = g.BitsOf(i);
            var ls = g.LemmasOf(i);
            var own = new HashSet<int>();
            foreach (var l in ls) own.Add(l);
            for (int k = 0; k < bits.Length; k++)
            {
                uint b = bits[k], pos = MorphBits.Pos(b);
                bool nominal = pos is MorphBits.Noun or MorphBits.Adjective;
                if (!nominal && pos != MorphBits.Infinitive) continue;
                foreach (var f in g.FormsOfLemma(ls[k]))
                {
                    if (f == i) continue;
                    var fb = g.BitsOf(f);
                    var fl = g.LemmasOf(f);
                    bool ok = true, match = false;
                    for (int j = 0; j < fl.Length && ok; j++)
                    {
                        if (!own.Contains(fl[j])) ok = false;   // the homograph guard
                        else if (fl[j] != ls[k]) continue;
                        // a noun / adjective: its other number; an infinitive: the verbal forms of its lemma only (a masculine
                        // noun that is also an infinitive never brings the feminine noun through its verb)
                        else if (nominal ? MorphBits.Pos(fb[j]) == pos && GenderFits(b, fb[j]) : MorphBits.IsVerbal(fb[j])) match = true;
                    }
                    if (ok && match && seen.Add(engine.FormAt(f))) o.Add(engine.FormAt(f));
                }
            }
        }
        return o;
    }

    /// <summary>The same gender, or either side epicene / without gender.</summary>
    private static bool GenderFits(uint a, uint b)
    {
        uint ga = a & MorphBits.GenderMask, gb = b & MorphBits.GenderMask;
        return ga == 0 || gb == 0 || (ga & gb) != 0;
    }

    private static bool[] Mark(HashSet<string> forms, PredictionEngine engine)
    {
        var entry = new bool[engine.Count];
        foreach (var f in forms)
        {
            int i = engine.IndexOf(f.ToLowerInvariant());
            if (i >= 0) entry[i] = true;
        }
        return entry;
    }

    /// <summary>The load log's line (counts and the SHA-256 only, never a word).</summary>
    public string Describe()
    {
        if (IsEmpty && Sha256.Length == 0) return "none";
        var cats = string.Join(" ", "RHSDXVIG".Where(Categories.ContainsKey).Select(c => $"{c} {Categories[c]}"));
        return $"{ListedCount} forms, {LemmaCount} lemmas" + (GrammarCount > 0 ? $", +{GrammarCount} grammar forms" : "") +
               (ExtraCount > 0 ? $", +{ExtraCount} extra" : "") + $" ({EntryCount} lexicon entries)" +
               (NamesExemptCount > 0 ? $", {NamesExemptCount} pack names exempt" : "") +
               (cats.Length > 0 ? $", categories {cats}" : "") + $", sha256 {Sha256}";
    }
}
