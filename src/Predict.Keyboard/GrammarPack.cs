using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>
/// Plan 11 (10D-1): the morphology table <c>grammar.sqlite</c> (derived from Grammalecte's French.lex, MPL-2.0, a separate
/// file next to Pepite.exe with <c>grammar\SOURCE.txt</c> and <c>grammar\MPL-2.0.txt</c>), loaded once on a worker thread at
/// start (0.3–0.5 s on the development PC, 0.3–1 s in the test VM and up to 4.5 s on a cold first start) and swapped into <see cref="PredictionEngine.Grammar"/> in one reference write, as the « Noms propres »
/// pack. Until it is there, or without the file, the bar ranks as 0.8.3. Grammatical prediction is always on in 0.9.0 (owner
/// Q2): <c>--no-grammar</c> is a test switch only. The log gets counts and times, never a word.
/// </summary>
internal static class GrammarPack
{
    /// <summary>Read the table on a worker thread and hand it to the engine; null when there is none (logged).</summary>
    public static Task<GrammarLexicon?> LoadAsync(string? path, PredictionEngine engine, bool disabled, Action<string>? log) => Task.Run(() =>
    {
        if (disabled) { log?.Invoke("grammar: off (--no-grammar)"); return null; }
        var g = Load(path, engine, log);
        if (g != null)
        {
            // the rows read (≈ 20 MB of short-lived arrays) may leave the working set soon: a collection the runtime may
            // schedule (Optimized), not a forced one that would pause the UI thread at start
            GC.Collect(2, GCCollectionMode.Optimized, false);
        }
        return g;
    });

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static GrammarLexicon? Load(string? path, PredictionEngine engine, Action<string>? log)
    {
        if (path is null || !File.Exists(path)) { log?.Invoke("grammar: absent"); return null; }
        try
        {
            var sw = Stopwatch.StartNew();
            var g = GrammarLexicon.Load(path, engine);
            engine.Grammar = g;
            log?.Invoke($"grammar: {g.FormCount} forms, {g.LemmaCount} lemmas, loaded in {sw.ElapsedMilliseconds} ms");
            ExpandExclusions(path, engine, log);
            return g;
        }
        catch (Exception ex)
        {
            // a damaged or other-schema file: the bar stays as 0.8.3 (the exception names no text of the pupil)
            log?.Invoke($"grammar: absent ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>0.11.0 (plan offensive-words A1): the excluded lemmas' inflections known only to French.lex join the list once
    /// the grammar is in (before, only the listed forms apply). The forms are read here, then swapped in through
    /// <see cref="PredictionEngine.UpdateExcluded"/>, which keeps the other parts (Phase B: the teacher's words). A failure
    /// leaves the listed forms in force and is logged on its own: the grammar itself stays loaded. Counts only.</summary>
    private static void ExpandExclusions(string path, PredictionEngine engine, Action<string>? log)
    {
        if (engine.Excluded.LemmaCount == 0) return;
        try
        {
            var sw = Stopwatch.StartNew();
            var forms = engine.Excluded.ReadGrammarForms(path, engine);
            var x = engine.UpdateExcluded(cur => cur.WithGrammarForms(forms));
            log?.Invoke(FormattableString.Invariant($"exclusions: +{x.GrammarCount} grammar forms ({x.Count} in all), {sw.Elapsed.TotalMilliseconds:0.0} ms"));
        }
        catch (Exception ex)
        {
            log?.Invoke($"exclusions: grammar expansion failed ({ex.GetType().Name}); the listed forms stay in force");
        }
    }
}
