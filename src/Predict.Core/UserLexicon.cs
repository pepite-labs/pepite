namespace Predict.Core;

/// <summary>One personal word as the app hands it to <see cref="UserLexicon.Build"/>: its display form (capitals
/// kept, plan 10 Q13) and a source weight (manual / lesson / class = 1.0).</summary>
public readonly record struct UserWord(string Form, double Weight = 1.0);

/// <summary>« Mes fautes » (plan 10 Q12): what the pupil writes → the word they want. Manual only, never learned.</summary>
public readonly record struct SpellingFix(string Typed, string Target);

/// <summary>
/// The personal words of the active profile (plan 10A, D10): an immutable snapshot built off the main engine on the UI
/// thread (≤ 30 ms for 15 000 words) and swapped into <see cref="PredictionEngine.User"/> in one reference write. Arrays
/// sorted by the lowercase form (literal prefix) and by <see cref="PhoneticKey.Fold"/> (sound prefix: new words have no
/// Lexique pronunciation), an accent-stripped map (edit-1), a lowercase → bonus map for the forms stored in lowercase
/// (they lift the main lexicon's own entry) and the « Mes fautes » keys. A hyphenated or apostrophised entry
/// (« Jean-Pierre », plan 10 Q16) is indexed whole and by each capitalised part of 2 letters or more (« Valbrune-les-Pins »: not « les »). Nothing here is ever learned:
/// the app adds words only when asked (owner 2026-11-14).
/// </summary>
public sealed class UserLexicon
{
    internal readonly struct Entry
    {
        public readonly string Form, Lower;
        public readonly double Weight;
        public readonly bool Name;   // stored with a capital: a proper name (never the common noun's picture, Q13)
        public Entry(string form, double weight)
        {
            Form = form; Lower = form.ToLowerInvariant(); Weight = weight;
            Name = form.Length > 0 && char.IsUpper(form[0]);
        }
    }

    internal readonly Entry[] Entries;
    internal readonly string[] LowerKeys; internal readonly int[] LowerIdx;   // sorted lowercase forms
    internal readonly string[] FoldKeys; internal readonly int[] FoldIdx;     // sorted phonetic folds
    internal readonly Dictionary<string, List<int>> Strip;                    // accent-stripped lowercase → entries
    internal readonly Dictionary<string, double> LowerBonus;                  // forms stored in lowercase → weight
    internal readonly string[] FixKeys; internal readonly string[] FixTargets;// sorted StripLower(typed) → target

    public static readonly UserLexicon Empty = new(Array.Empty<UserWord>(), Array.Empty<SpellingFix>());

    /// <summary>Distinct indexed forms (parts of hyphenated entries included).</summary>
    public int Count => Entries.Length;
    public int FixCount => FixKeys.Length;
    public bool IsEmpty => Entries.Length == 0 && FixKeys.Length == 0;

    private UserLexicon(IEnumerable<UserWord> words, IEnumerable<SpellingFix> fixes)
    {
        // one entry per distinct form (Ordinal: « Pierre » and « pierre » are two words); the highest weight wins
        var byForm = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string f, double w)
        {
            if (f.Length == 0) return;
            if (!byForm.TryGetValue(f, out var cur) || w > cur) byForm[f] = w;
        }
        foreach (var uw in words)
        {
            var f = (uw.Form ?? "").Trim().Replace('’', '\'');
            if (f.Length == 0) continue;
            Add(f, uw.Weight);
            // a names-database entry may hold spaces (« La Louvière », plan 10C): its capitalised parts are indexed the same way
            if (f.IndexOf('-') >= 0 || f.IndexOf('\'') >= 0 || f.IndexOf(' ') >= 0)
                foreach (var part in f.Split(new[] { '-', '\'', ' ', '.' }, StringSplitOptions.RemoveEmptyEntries))
                    if (part.Length >= (f.IndexOf(' ') >= 0 ? 3 : 2) && char.IsUpper(part[0])) Add(part, uw.Weight);   // « les », « de », « sur », « La » stay out
        }
        Entries = byForm.Select(kv => new Entry(kv.Key, kv.Value)).ToArray();

        LowerIdx = Enumerable.Range(0, Entries.Length).ToArray();
        LowerKeys = Entries.Select(e => e.Lower).ToArray();
        Array.Sort(LowerKeys, LowerIdx, StringComparer.Ordinal);   // keys and indexes together (no per-compare lookup)

        var folds = new List<(string key, int idx)>(Entries.Length);
        Strip = new Dictionary<string, List<int>>(Entries.Length, StringComparer.Ordinal);
        LowerBonus = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < Entries.Length; i++)
        {
            var e = Entries[i];
            var fk = PhoneticKey.Fold(e.Form);
            if (fk.Length > 0) folds.Add((fk, i));
            var s = PhoneticKey.StripLower(e.Form);
            if (!Strip.TryGetValue(s, out var l)) Strip[s] = l = new List<int>(1);
            l.Add(i);
            if (e.Form == e.Lower && (!LowerBonus.TryGetValue(e.Lower, out var b) || e.Weight > b)) LowerBonus[e.Lower] = e.Weight;
        }
        FoldKeys = folds.Select(f => f.key).ToArray();
        FoldIdx = folds.Select(f => f.idx).ToArray();
        Array.Sort(FoldKeys, FoldIdx, StringComparer.Ordinal);

        // « Mes fautes »: keyed by the accent-stripped lowercase typed form; the first pair for a key wins (the caller
        // puts the pupil's own pairs before the class's)
        var fx = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in fixes)
        {
            var k = PhoneticKey.StripLower((p.Typed ?? "").Trim());
            var t = (p.Target ?? "").Trim();
            if (k.Length == 0 || t.Length == 0 || fx.ContainsKey(k)) continue;
            fx[k] = t;
        }
        FixKeys = fx.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        FixTargets = FixKeys.Select(k => fx[k]).ToArray();
    }

    /// <summary>Build a snapshot. Pure; the caller swaps it into <see cref="PredictionEngine.User"/>.</summary>
    public static UserLexicon Build(IEnumerable<UserWord> words, IEnumerable<SpellingFix>? fixes = null)
    {
        var w = words as ICollection<UserWord> ?? words.ToList();
        var f = fixes is null ? Array.Empty<SpellingFix>() : fixes as ICollection<SpellingFix> ?? fixes.ToList();
        return w.Count == 0 && f.Count == 0 ? Empty : new UserLexicon(w, f);
    }

    /// <summary>Is <paramref name="word"/> one of the personal forms (ignoring case)?</summary>
    public bool Contains(string word)
    {
        if (string.IsNullOrEmpty(word) || Entries.Length == 0) return false;
        var lo = word.ToLowerInvariant();
        int i = PredictionEngine.LowerBound(LowerKeys, lo);
        return i < LowerKeys.Length && LowerKeys[i] == lo;
    }

    /// <summary>Is <paramref name="form"/> (exactly) the target of a « Mes fautes » pair? A capitalised target (« Pierre »)
    /// is a name even when no list holds it.</summary>
    public bool IsFixTarget(string form) => Array.IndexOf(FixTargets, form) >= 0;

    /// <summary>The stored form of <paramref name="word"/> with a capital, when the list has one (« Pierre »), else
    /// null.</summary>
    public string? NameForm(string word)
    {
        if (string.IsNullOrEmpty(word) || Entries.Length == 0) return null;
        var lo = word.ToLowerInvariant();
        for (int i = PredictionEngine.LowerBound(LowerKeys, lo); i < LowerKeys.Length && LowerKeys[i] == lo; i++)
            if (Entries[LowerIdx[i]].Name) return Entries[LowerIdx[i]].Form;
        return null;
    }
}
