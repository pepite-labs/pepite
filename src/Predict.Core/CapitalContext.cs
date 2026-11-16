namespace Predict.Core;

/// <summary>
/// 0.8.3 (10C review): where a capital says nothing about a name. French writes a capital at a sentence start, but also
/// after a colon or an opening quote or dialogue dash (« il a dit : Mer… », « … : « Je », « — Je »): there the engine
/// treats the word as at a sentence start (no name bonus), as it does when the keyboard does not know what comes before
/// the word (after a jump it could not read). One definition for the keyboard's word tracker and <c>Predict.Eval</c>.
/// </summary>
public static class CapitalContext
{
    /// <summary>A separator after which a capital opens a quotation or a reply: <c>: « " “ — –</c>.</summary>
    public static bool Opens(char c) => c is ':' or '«' or '"' or '“' or '—' or '–';

    /// <summary>The separators between the previous word and this one (<paramref name="gap"/>) hold one that
    /// <see cref="Opens"/>.</summary>
    public static bool OpensIn(string gap)
    {
        foreach (var c in gap ?? "") if (Opens(c)) return true;
        return false;
    }
}
