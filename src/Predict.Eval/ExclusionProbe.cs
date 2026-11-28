using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Predict.Core;

namespace PredictEval;

/// <summary>
/// 0.11.0 (plan offensive-words, § Tests « exhaustive probe »): every excluded form the engine holds (the listed ones and,
/// with <c>--grammar</c>, the grammar forms added at load) is typed every way a pupil could reach it — each prefix with no
/// context and after « le », « la », « un », « il est », « c'est », with ch/j and with all five sound pairs; its edit-1
/// variants and its fold checked (<see cref="PredictionEngine.Check"/>; <c>--edits</c> also predicts them); the next word after
/// its 20 most frequent corpus predecessors (bigrams and trigrams); 0.11.0: glued after a clitic and a subject + clitic (bar and ≈); the same prefixes with every listed form as a personal
/// word and a « Mes fautes » target. A failure is a listed form in a top-8 / ≈ list, or a word the safety net had to drop
/// (<see cref="PredictionEngine.ExcludedLate"/> grew: a source that does not filter). <b>Ids and counts only</b>: a form is
/// named by its <c>forms.tsv</c> id (<c>F00012</c>), a grammar form by its rank in ordinal order (<c>G0003</c>).
/// </summary>
internal static class ExclusionProbe
{
    private static readonly string[][] Contexts =
        { Array.Empty<string>(), new[] { "le" }, new[] { "la" }, new[] { "un" }, new[] { "il", "est" }, new[] { "c'est" } };
    /// <summary>0.11.0: the glued prefixes of the probe (« » alone = a 0.10.2 joined form; a subject = a three-word one).</summary>
    private static readonly string[] GluedSubjects = { "", "j", "je", "tu", "il", "ils", "elle", "on" };
    private static readonly string[] GluedClitics = { "m", "t", "s", "l", "n" };

    public static int Run(PredictionEngine eng, string dbPath, string? formsTsv, bool edits)
    {
        var x = eng.Excluded;
        if (x.IsEmpty) { Console.WriteLine("probe: no exclusions loaded"); return 2; }
        var ids = ReadIds(formsTsv);
        var forms = x.ProbeForms.OrderBy(f => f, StringComparer.Ordinal).ToList();
        var id = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int g = 0;
        foreach (var f in forms) id[f] = ids.TryGetValue(f, out var fid) ? fid : $"G{++g:0000}";
        Console.WriteLine($"probe: {forms.Count} forms ({forms.Count - g} with a forms.tsv id, {g} grammar forms)");
        var fails = new SortedDictionary<string, int>(StringComparer.Ordinal);
        long queries = 0, hits = 0, late0 = eng.ExcludedLate;
        var sw = Stopwatch.StartNew();
        void Fail(string what) { fails[what] = fails.TryGetValue(what, out var n) ? n + 1 : 1; }

        void Bar(string fid, string how, Func<IReadOnlyList<Suggestion>> q)
        {
            long before = eng.ExcludedLate;
            var r = q();
            queries++;
            int h = 0;
            foreach (var s in r) if (x.IsExcluded(s.Word) && !(x.NameExempt(s.Word) && eng.Names.ContainsForm(s.Word))) h++;
            hits += h;
            if (h > 0) Fail($"{fid} {how}: listed form in the bar");
            if (eng.ExcludedLate != before) Fail($"{fid} {how}: safety net dropped {eng.ExcludedLate - before}");
        }
        void Check(string fid, string how, string w)
        {
            long before = eng.ExcludedLate;
            var c = eng.Check(w, 8);
            queries++;
            int h = 0;
            foreach (var s in c.Candidates) if (x.IsExcluded(s) && !(x.NameExempt(s) && eng.Names.ContainsForm(s))) h++;
            hits += h;
            if (h > 0) Fail($"{fid} {how}: listed form in the ≈ list");
            if (eng.ExcludedLate != before) Fail($"{fid} {how}: safety net dropped {eng.ExcludedLate - before}");
        }

        var keepTol = eng.Tolerance;
        foreach (var pairs in new[] { SoundPairs.ChJ, SoundPairsInfo.All })
        {
            eng.Tolerance = pairs;
            var tag = pairs == SoundPairs.ChJ ? "chj" : "all5";
            foreach (var f in forms)
            {
                var fid = id[f];
                for (int p = 1; p <= f.Length; p++)
                    for (int c = 0; c < Contexts.Length; c++)
                    {
                        var ctx = Contexts[c];
                        var pre = f[..p];
                        Bar(fid, $"{tag} prefix {p} ctx {c}", () => eng.Predict(pre, ctx.Length == 0 ? null : ctx, 8, ctx.Length == 0));
                    }
                Check(fid, $"{tag} check", f);
                foreach (var v in Edits1(f.ToLowerInvariant()))
                {
                    Check(fid, $"{tag} check edit-1", v);
                    if (edits) Bar(fid, $"{tag} edit-1", () => eng.Predict(v, null, 8, true));
                }
            }
        }
        eng.Tolerance = keepTol;
        Console.WriteLine(FormattableString.Invariant($"probe: prefixes, checks{(edits ? ", edit-1 bars" : "")}: {queries} queries, {sw.Elapsed.TotalSeconds:0.0} s"));

        // 0.11.0 (plan glued-words-followup.md): each form glued after a clitic and after a subject + clitic (« jt… », « ils… »,
        // « ellem… »: the three-word tiles), in the bar at a sentence start and as a finished word (≈)
        foreach (var f in forms)
        {
            var fid = id[f];
            var lower = f.ToLowerInvariant();
            foreach (var subj in GluedSubjects)
                foreach (var b in GluedClitics)
                {
                    var typed = subj + b + lower;
                    Bar(fid, "glued subject + clitic", () => eng.Predict(typed, null, 8, true));
                    Check(fid, "glued subject + clitic check", typed);
                }
        }
        Console.WriteLine(FormattableString.Invariant($"probe: + glued forms: {queries} queries, {sw.Elapsed.TotalSeconds:0.0} s"));

        // the next word after each form's corpus predecessors (one scan of each table, then the top 20 per form)
        var (bi, tri) = Predecessors(dbPath, forms);
        foreach (var f in forms)
        {
            var fid = id[f];
            if (bi.TryGetValue(f, out var bp))
                foreach (var prev in bp.OrderByDescending(t => t.C).Take(20)) Bar(fid, "next after a bigram predecessor", () => eng.PredictNext(new[] { prev.P }, 8));
            if (tri.TryGetValue(f, out var tp))
                foreach (var prev in tp.OrderByDescending(t => t.C).Take(20)) Bar(fid, "next after a trigram predecessor", () => eng.PredictNext(prev.P.Split(' '), 8));
        }
        Console.WriteLine(FormattableString.Invariant($"probe: + next word ({bi.Count} forms with a bigram predecessor, {tri.Count} with a trigram one): {queries} queries, {sw.Elapsed.TotalSeconds:0.0} s"));

        // every listed form a personal word and a « Mes fautes » target (its fold as the pupil's spelling)
        var user = eng.User;
        eng.User = UserLexicon.Build(forms.Select(f => new UserWord(f)), forms.Select(f => new SpellingFix(PhoneticKey.StripLower(f) + "x", f)));
        foreach (var f in forms)
        {
            var fid = id[f];
            for (int p = 1; p <= f.Length; p++) { var pre = f[..p]; Bar(fid, $"personal prefix {p}", () => eng.Predict(pre, new[] { "le" }, 8)); }
            var typo = PhoneticKey.StripLower(f) + "x";
            Bar(fid, "« Mes fautes » spelling", () => eng.Predict(typo, null, 8, true));
            Check(fid, "« Mes fautes » check", typo);
        }
        eng.User = user;

        // the « Noms propres » pack: its names that collide with a listed form (a candidate for a form- decision)
        int collide = 0;
        var collideIds = new List<string>();
        if (!eng.Names.IsEmpty)
            foreach (var f in forms)
            {
                var cap = f.Length > 0 ? char.ToUpperInvariant(f[0]) + f[1..] : f;
                if (eng.Names.ContainsForm(f) || eng.Names.ContainsForm(cap))
                { collide++; collideIds.Add(id[f] + (x.NameExempt(cap) ? " (exempt, owner 2026-11-27)" : " (blocked)")); }
            }

        long late = eng.ExcludedLate - late0;
        Console.WriteLine(FormattableString.Invariant($"probe: {queries} queries in {sw.Elapsed.TotalSeconds:0.0} s; listed forms offered {hits}; safety-net drops (ExcludedLate) {late}; failures {fails.Count}"));
        Console.WriteLine(eng.Names.IsEmpty ? "probe: names pack not loaded (--names)"
                                            : $"probe: pack names colliding with a listed form: {collide}{(collide > 0 ? " (" + string.Join(", ", collideIds) + ")" : "")}");
        foreach (var (what, n) in fails.Take(200)) Console.WriteLine($"  FAIL {what}{(n > 1 ? $" ×{n}" : "")}");
        return hits == 0 && late == 0 && fails.Count == 0 ? 0 : 1;
    }

    private static Dictionary<string, string> ReadIds(string? path)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path is null || !File.Exists(path)) return o;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var c = line.Split('\t');
            if (c.Length >= 2) o.TryAdd(c[1].Normalize(System.Text.NormalizationForm.FormC), c[0]);
        }
        return o;
    }

    private static (Dictionary<string, List<(string P, long C)>> Bi, Dictionary<string, List<(string P, long C)>> Tri)
        Predecessors(string dbPath, List<string> forms)
    {
        var bi = new Dictionary<string, List<(string, long)>>(StringComparer.OrdinalIgnoreCase);
        var tri = new Dictionary<string, List<(string, long)>>(StringComparer.OrdinalIgnoreCase);
        using var con = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
        con.Open();
        // the n-gram tables hold lowercase tokens; one temp table joins them in one scan each
        using (var c = con.CreateCommand()) { c.CommandText = "CREATE TEMP TABLE probe_w(w TEXT PRIMARY KEY)"; c.ExecuteNonQuery(); }
        using (var tx = con.BeginTransaction())
        {
            using var c = con.CreateCommand();
            c.CommandText = "INSERT OR IGNORE INTO probe_w VALUES ($w)";
            var p = c.Parameters.Add("$w", SqliteType.Text);
            foreach (var f in forms) { p.Value = f.ToLowerInvariant(); c.ExecuteNonQuery(); }
            tx.Commit();
        }
        void Read(string sql, Dictionary<string, List<(string, long)>> into)
        {
            using var c = con.CreateCommand();
            c.CommandText = sql;
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                var w = r.GetString(0);
                if (!into.TryGetValue(w, out var l)) into[w] = l = new List<(string, long)>();
                l.Add((r.GetString(1), r.GetInt64(2)));
            }
        }
        Read("SELECT b.word, b.prev, b.c FROM bigrams b JOIN probe_w w ON w.w = b.word", bi);
        Read("SELECT t.word, t.p2 || ' ' || t.p1, t.c FROM trigrams t JOIN probe_w w ON w.w = t.word", tri);
        return (bi, tri);
    }

    private static IEnumerable<string> Edits1(string w)
    {
        const string A = "abcdefghijklmnopqrstuvwxyzàâäçéèêëîïôöùûüœ";
        var seen = new HashSet<string>(StringComparer.Ordinal) { w };
        int n = w.Length;
        for (int i = 0; i < n - 1; i++) { var a = w.ToCharArray(); (a[i], a[i + 1]) = (a[i + 1], a[i]); var s = new string(a); if (seen.Add(s)) yield return s; }
        for (int i = 0; i < n; i++) { var s = w.Remove(i, 1); if (s.Length > 0 && seen.Add(s)) yield return s; }
        for (int i = 0; i < n; i++) foreach (var ch in A) { var s = w[..i] + ch + w[(i + 1)..]; if (seen.Add(s)) yield return s; }
        for (int i = 0; i <= n; i++) foreach (var ch in A) { var s = w[..i] + ch + w[i..]; if (seen.Add(s)) yield return s; }
    }
}
