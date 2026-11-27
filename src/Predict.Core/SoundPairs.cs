namespace Predict.Core;

/// <summary>
/// 0.11.0 (plan dys-sound-tolerance, owner Q2): the voicing pairs a pupil may confuse (« chardin » for jardin). Each pair
/// links two symbols of the <see cref="PhoneticKey"/> alphabet: ch/j <c>C</c>↔<c>j</c> (ch ↔ j / g / ge), f/v <c>f</c>↔<c>v</c>,
/// k/g <c>k</c>↔<c>g</c>, t/d <c>t</c>↔<c>d</c>, p/b <c>p</c>↔<c>b</c>. No s/z (too noisy) and no nasal vowels (later): the
/// study's measurements (development/studies/dys-sound-confusions.md § 4.1).
/// </summary>
[Flags]
public enum SoundPairs { None = 0, ChJ = 1, FV = 2, KG = 4, TD = 8, PB = 16 }

public static class SoundPairsInfo
{
    /// <summary>The five pairs, in the order of the setting (ch/j first).</summary>
    public static readonly SoundPairs All = SoundPairs.ChJ | SoundPairs.FV | SoundPairs.KG | SoundPairs.TD | SoundPairs.PB;

    /// <summary>The other symbol of <paramref name="c"/>'s pair when that pair is enabled, else <c>'\0'</c>.</summary>
    public static char Partner(char c, SoundPairs pairs) => c switch
    {
        'C' when (pairs & SoundPairs.ChJ) != 0 => 'j',
        'j' when (pairs & SoundPairs.ChJ) != 0 => 'C',
        'f' when (pairs & SoundPairs.FV) != 0 => 'v',
        'v' when (pairs & SoundPairs.FV) != 0 => 'f',
        'k' when (pairs & SoundPairs.KG) != 0 => 'g',
        'g' when (pairs & SoundPairs.KG) != 0 => 'k',
        't' when (pairs & SoundPairs.TD) != 0 => 'd',
        'd' when (pairs & SoundPairs.TD) != 0 => 't',
        'p' when (pairs & SoundPairs.PB) != 0 => 'b',
        'b' when (pairs & SoundPairs.PB) != 0 => 'p',
        _ => '\0',
    };

    /// <summary>Short names for logs and tools: « chj, fv » (« none » when empty).</summary>
    public static string Describe(SoundPairs pairs)
    {
        if (pairs == SoundPairs.None) return "none";
        var parts = new List<string>(5);
        if ((pairs & SoundPairs.ChJ) != 0) parts.Add("chj");
        if ((pairs & SoundPairs.FV) != 0) parts.Add("fv");
        if ((pairs & SoundPairs.KG) != 0) parts.Add("kg");
        if ((pairs & SoundPairs.TD) != 0) parts.Add("td");
        if ((pairs & SoundPairs.PB) != 0) parts.Add("pb");
        return string.Join(", ", parts);
    }

    /// <summary>Parse « none », « all5 » or a comma list of chj / fv / kg / td / pb (tools' <c>--sound-pairs</c>).</summary>
    public static SoundPairs Parse(string s)
    {
        var r = SoundPairs.None;
        foreach (var raw in (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            r |= raw.ToLowerInvariant() switch
            {
                "none" => SoundPairs.None,
                "all5" or "all" => All,
                "chj" => SoundPairs.ChJ,
                "fv" => SoundPairs.FV,
                "kg" => SoundPairs.KG,
                "td" => SoundPairs.TD,
                "pb" => SoundPairs.PB,
                _ => throw new ArgumentException("sound pairs: none | all5 | chj,fv,kg,td,pb — not « " + raw + " »"),
            };
        return r;
    }
}
