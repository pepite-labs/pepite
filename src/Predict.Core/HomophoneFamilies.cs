namespace Predict.Core;

/// <summary>
/// Plan 11 (10D-1): our five homophone families — {a, as, à} · {est, es, et} · {sont, son} · {ont, on} · {sait, sais, ces,
/// ses}. When the pupil types one member in full and the agreement context favours another, the engine adds that member to
/// the bar (« il » + « et » → « est »). « ces » / « ses » are never chosen by a rule: between them the language model
/// decides. A table of our own (the grammar data has no notion of homophones); moved here from Predict.Eval in 10D-1.
/// </summary>
public static class HomophoneFamilies
{
    /// <summary>The families, lowercase.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> All = new[]
    {
        new[] { "a", "as", "à" }, new[] { "est", "es", "et" }, new[] { "sont", "son" }, new[] { "ont", "on" },
        new[] { "sait", "sais", "ces", "ses" },
    };

    private static readonly Dictionary<string, IReadOnlyList<string>> ByMember = Build();

    private static Dictionary<string, IReadOnlyList<string>> Build()
    {
        var d = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var f in All) foreach (var m in f) d[m] = f;
        return d;
    }

    /// <summary>The family of a lowercase word, or null.</summary>
    public static IReadOnlyList<string>? Of(string lower) => ByMember.TryGetValue(lower, out var f) ? f : null;

    /// <summary>Is the lowercase word a member of some family?</summary>
    public static bool IsMember(string lower) => ByMember.ContainsKey(lower);

    /// <summary>May an agreement rule add this member? Never « ces » / « ses » (the language model chooses between them).</summary>
    public static bool RuleMayChoose(string lower) => lower is not ("ces" or "ses");
}
