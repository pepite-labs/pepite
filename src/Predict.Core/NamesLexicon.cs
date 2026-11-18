namespace Predict.Core;

/// <summary>
/// The shipped « Noms propres » pack as the engine reads it (plan 10C, owner Q2 / Q9 / Q11): an immutable, read-only layer
/// next to the main lexicon and the personal words, built once at start (≈ 31 000 names) and swapped into
/// <see cref="PredictionEngine.Names"/> in one reference write. Every form is indexed by its lowercase letters (prefix) and
/// its accent-stripped letters (<see cref="PredictionEngine.Check"/>): the names themselves, their variants (« Ieper » for
/// « Ypres »: recognised, and offered with the name shown) and the capitalised parts of compound names that the lexicon
/// does not know (« Louvière » of « La Louvière », « Hugo » of « Victor Hugo »; never « La », « Saint », « Arc »).
/// A name whose lowercase form is one of the 300 most frequent lexicon words (« Elle ») is left out: at a sentence start it
/// would take the place, and the picture, of the common word.
/// </summary>
public sealed class NamesLexicon
{
    internal readonly struct Entry
    {
        public readonly string Form, Lower, Shown;   // Shown: the name a variant stands for (the same string for a name)
        public readonly float Freq;                  // pseudo frequency per million (cap × rang)
        public readonly bool Recognised;             // tier 2: after a capital typed inside a sentence, else from 4 letters (0.10.2)
        public readonly bool Elides;                 // starts with a vowel: takes « l' », « d' » (D22: « d'Emma »)
        public readonly bool Restricted;             // owner 2026-11-15: offered only after a capital inside a sentence + 3 letters
        public Entry(string form, string shown, double freq, bool recognised, bool restricted = false)
        {
            Form = form; Lower = form.ToLowerInvariant(); Shown = form == shown ? form : shown; Freq = (float)freq; Recognised = recognised;
            Restricted = restricted;
            // 0.8.3 (10C review): a mute h takes the elision too (« d'Hélène », « d'Hugo »); the aspirated ones of the lexicon
            // list (« Hongrie ») do not — a ranking hint after « d' » / « l' », never a filter
            Elides = Lower.Length > 0 && (IsVowel(Lower[0]) || Lower.Length > 1 && Lower[0] == 'h' && IsVowel(Lower[1]) && !Elision.HasAspiratedH(Lower));
        }
        public bool IsVariant => !ReferenceEquals(Form, Shown);
        private static bool IsVowel(char c) => "aeiouyàâäéèêëîïôöùûüœæ".IndexOf(c) >= 0;
    }

    // memory (plan 10C gate ≤ +15 MB for the app): no per-key list or extra set — sorted keys, one accent-stripped map to
    // the first entry and a « next » chain for the few forms sharing the same letters
    internal readonly Entry[] Entries;
    // sorted lowercase keys, one array per tier: a query without a capital inside a sentence never walks the ≈ 24 000
    // recognised-only first names (plan 10C review, speed)
    internal readonly string[] Keys1; internal readonly int[] Idx1;   // tier 1 (predicted)
    internal readonly string[] Keys2; internal readonly int[] Idx2;   // tier 2 (recognised; predicted after a capital, or from 4 letters above a floor)
    private readonly Dictionary<string, int> _strip;            // accent-stripped lowercase → first entry
    private readonly int[] _next;                               // the next entry with the same stripped letters, or -1

    public static readonly NamesLexicon Empty = new(Array.Empty<Entry>());
    /// <summary>A regnal number (« Ier », « XIV », « IIIe »): never a name of its own.</summary>
    private static readonly System.Text.RegularExpressions.Regex Roman = new("^[IVXLC]+(er|re|e)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Indexed forms (names + variants + parts).</summary>
    public int Count => Entries.Length;
    public bool IsEmpty => Entries.Length == 0;
    /// <summary>Names read from the pack (variants and parts not counted).</summary>
    public int NameCount { get; private set; }
    public string? Edition { get; private set; }
    /// <summary>The pseudo frequency (per million) of a rang-1 name in this layer (<see cref="Build"/>'s freqCap): the engine
    /// turns a rang floor into a frequency with it (0.10.2, <see cref="PredictionEngine.NamesRecognisedFloor"/>).</summary>
    public double FreqCap { get; private set; } = 5.0;

    private NamesLexicon(Entry[] entries)
    {
        Entries = entries;
        Idx1 = Enumerable.Range(0, entries.Length).Where(i => !entries[i].Recognised).ToArray();
        Keys1 = Idx1.Select(i => entries[i].Lower).ToArray();
        Array.Sort(Keys1, Idx1, StringComparer.Ordinal);
        Idx2 = Enumerable.Range(0, entries.Length).Where(i => entries[i].Recognised).ToArray();
        Keys2 = Idx2.Select(i => entries[i].Lower).ToArray();
        Array.Sort(Keys2, Idx2, StringComparer.Ordinal);
        _strip = new Dictionary<string, int>(entries.Length, StringComparer.Ordinal);
        _next = new int[entries.Length];
        for (int i = entries.Length - 1; i >= 0; i--)
        {
            var s = PhoneticKey.StripLower(entries[i].Form);
            if (s == entries[i].Lower) s = entries[i].Lower;   // one string when there is no accent to strip
            _next[i] = _strip.TryGetValue(s, out var first) ? first : -1;
            _strip[s] = i;
        }
    }

    /// <param name="names">the pack's names (<see cref="NamesFile.ParseJson"/>).</param>
    /// <param name="isLexiconWord">the main lexicon knows this lowercase form (a compound's part it knows is not indexed).</param>
    /// <param name="frequent">the 300 most frequent lexicon forms (lowercase): names equal to one are left out.</param>
    /// <param name="freqCap">the pseudo frequency (per million) of the most popular name: below the median content word, so
    /// « mar » still gives « mardi » / « marché » before « Marie » / « Maroc » (study 10c § 2.3).</param>
    public static NamesLexicon Build(IEnumerable<NameRecord> names, Func<string, bool>? isLexiconWord = null,
                                     IReadOnlySet<string>? frequent = null, double freqCap = 5.0, string? edition = null)
    {
        var b = new Builder(isLexiconWord, frequent, freqCap);
        foreach (var n in names) b.Add(n);
        return b.Done(edition);
    }

    /// <summary>Adds names one at a time (a streaming parse feeds it: no record list is kept, plan 10C review).</summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, Entry> _byForm = new(StringComparer.Ordinal);
        private readonly Func<string, bool>? _isLexiconWord;
        private readonly IReadOnlySet<string>? _frequent;
        private readonly double _cap;
        private int _count;
        private static readonly char[] Seps = { ' ', '-', '\'' }, PartSeps = { ' ', '-', '\'', '.' };

        public Builder(Func<string, bool>? isLexiconWord, IReadOnlySet<string>? frequent, double freqCap)
        { _isLexiconWord = isLexiconWord; _frequent = frequent; _cap = freqCap; }

        private void Put(string form, string shown, double freq, bool recognised, bool restricted)
        {
            if (form.Length < 2) return;
            if (_frequent != null && _frequent.Contains(form.ToLowerInvariant())) return;
            // one entry per form: a name beats a variant or a part, then the higher frequency
            if (_byForm.TryGetValue(form, out var cur))
            {
                bool curName = cur.Form == cur.Shown, newName = form == shown;
                if (curName && !newName || curName == newName && cur.Freq >= freq) return;
            }
            _byForm[form] = new Entry(form, shown, freq, recognised, restricted);
        }

        private void Parts(string form, double freq, bool recognised, bool restricted)
        {
            if (form.IndexOfAny(Seps) < 0) return;
            foreach (var part in form.Split(PartSeps, StringSplitOptions.RemoveEmptyEntries))
                if (part.Length >= 3 && char.IsUpper(part[0]) && !Roman.IsMatch(part) && !(_isLexiconWord?.Invoke(part.ToLowerInvariant()) ?? false))
                    Put(part, part, freq, recognised, restricted);
        }

        public void Add(NameRecord n)
        {
            var nom = NamesFile.Clean(n.Nom);
            if (NamesFile.CheckName(nom) != null) return;
            _count++;
            // a restricted figure always comes after the first names: the lowest frequency of the pack
            double f = n.Restreint ? RestrictedFreq : _cap * Math.Clamp(n.Rang, 0.0, 1.0);
            bool rec = n.Niveau == 2 || n.Restreint;
            Put(nom, nom, f, rec, n.Restreint);
            Parts(nom, f, rec, n.Restreint);
            foreach (var v in n.Variantes)
            {
                var c = NamesFile.Clean(v);
                if (c == nom || NamesFile.CheckName(c) != null) continue;
                Put(c, nom, f, rec, n.Restreint);
                Parts(c, f, rec, n.Restreint);
            }
        }

        public NamesLexicon Done(string? edition) => new(_byForm.Values.ToArray()) { NameCount = _count, Edition = edition, FreqCap = _cap };
    }

    /// <summary>The frequency (per million) of a restricted figure: below any name of the pack (whose floor is cap × 0.001).</summary>
    public const double RestrictedFreq = 0.0001;

    /// <summary>A recognised-tier name at the pack's popularity floor (rang 0.01 or less): very rare. At a sentence start such a
    /// name that sounds like a common word is judged by its sound (owner 2026-11-15: « Pome » ≈ pomme, « Kan » ≈ quand).</summary>
    public const double WeakFreq = 5.0 * 0.01 + 1e-6;

    /// <summary>Read a pack (<c>noms.json</c>, or the folder holding it) and build its layer for <paramref name="engine"/>.
    /// Throws <see cref="InvalidDataException"/> / IO exceptions (the caller logs and runs without the pack).</summary>
    /// <param name="also">sees each record as it is read (the app collects the pictures); the returned document has no
    /// names (streaming: nothing but the layer is kept).</param>
    public static (NamesLexicon Lexicon, NamesDocument Doc) Load(string path, PredictionEngine engine, double freqCap = 5.0,
                                                                  Action<NameRecord>? also = null)
    {
        var file = Directory.Exists(path) ? Path.Combine(path, "noms.json") : path;
        var b = new Builder(engine.Knows, engine.MostFrequent300, freqCap);
        var doc = NamesFile.ParseJson(File.ReadAllBytes(file), r => { b.Add(r); also?.Invoke(r); });
        return (b.Done(doc.Edition), doc);
    }

    /// <summary>Is <paramref name="form"/> (exactly) known only as a very rare recognised-tier name (<see cref="WeakFreq"/>)?
    /// False when any entry of that form is more popular, or it is not a pack form.</summary>
    public bool IsWeak(string form)
    {
        if (string.IsNullOrEmpty(form) || Entries.Length == 0) return false;
        var lo = form.ToLowerInvariant();
        bool any = false;
        for (int i = PredictionEngine.LowerBound(Keys1, lo); i < Keys1.Length && Keys1[i] == lo; i++)
            if (Entries[Idx1[i]].Form == form) return false;   // a predicted name is never weak
        for (int i = PredictionEngine.LowerBound(Keys2, lo); i < Keys2.Length && Keys2[i] == lo; i++)
        {
            var e = Entries[Idx2[i]];
            if (e.Form != form) continue;
            if (e.Restricted || e.Freq > WeakFreq) return false;
            any = true;
        }
        return any;
    }

    /// <summary>Is <paramref name="form"/> exactly a name, a variant or a part of the pack (case significant)?</summary>
    public bool ContainsForm(string form)
    {
        if (string.IsNullOrEmpty(form) || Entries.Length == 0 || !char.IsUpper(form[0])) return false;   // every form has a capital
        var lo = form.ToLowerInvariant();
        for (int i = PredictionEngine.LowerBound(Keys1, lo); i < Keys1.Length && Keys1[i] == lo; i++)
            if (Entries[Idx1[i]].Form == form) return true;
        for (int i = PredictionEngine.LowerBound(Keys2, lo); i < Keys2.Length && Keys2[i] == lo; i++)
            if (Entries[Idx2[i]].Form == form) return true;
        return false;
    }

    /// <summary>The forms whose accent-stripped lowercase letters are those of <paramref name="word"/> (« ines » → « Inès »,
    /// « Ines »; « emma » → « Emma »), names before variants, the most popular first, at most <paramref name="max"/>.</summary>
    public List<string> SameLetters(string word, int max = 4)
    {
        var o = new List<string>();
        if (Entries.Length == 0 || !_strip.TryGetValue(PhoneticKey.StripLower(word), out var i)) return o;
        var idx = new List<int>();
        for (; i >= 0; i = _next[i]) idx.Add(i);
        idx.Sort((a, b) => Entries[a].IsVariant != Entries[b].IsVariant ? (Entries[a].IsVariant ? 1 : -1) : Entries[b].Freq.CompareTo(Entries[a].Freq));
        foreach (var j in idx) { if (o.Count >= max) break; o.Add(Entries[j].Form); }
        return o;
    }
}
