using System;
using System.Collections.Generic;
using System.Text;

namespace Predict.Core;

/// <summary>
/// 0.11.0 (plan glued-words-followup.md, kind B): the fixed expressions a pupil who writes by ear glues into one word
/// (« ilya » → il y a, « parceque » → parce que, « keske » → qu'est-ce que). A short list of common French phrases written
/// by us from general knowledge (clean room: no external list). B1: fixed expressions; B2: a subject and « être » /
/// « avoir » (« ilé » → il est), kept only while its own gates pass (<see cref="PredictionEngine.ExprB2"/>). The engine checks
/// each row at load (<see cref="PredictionEngine.GetExpressions"/>): every token a lexicon form, the glued spelling no real
/// word (« cava », « tuas »), enough corpus counts (<see cref="PredictionEngine.ExprMinCount"/>).
/// </summary>
public static class GluedExpressions
{
    /// <param name="Written">the form shown and inserted (straight apostrophe: <see cref="Elision.Styled"/> writes the
    /// document's).</param>
    /// <param name="B2">a subject + être / avoir row (plan § B2).</param>
    public readonly record struct Row(string Written, bool B2);

    public static readonly IReadOnlyList<Row> Rows = new Row[]
    {
        // B1 (owner Q2, 2026-11-27)
        new("il y a", false), new("il n'y a", false), new("il y avait", false), new("parce que", false), new("parce qu'", false),
        new("par contre", false), new("quelque chose", false), new("tout le monde", false), new("tout de suite", false),
        new("tout à coup", false), new("tout à fait", false), new("tout d'abord", false), new("à peu près", false),
        new("est-ce que", false), new("qu'est-ce que", false), new("s'il te plaît", false), new("s'il vous plaît", false),
        new("après-midi", false), new("bien sûr", false), new("au revoir", false), new("ça va", false), new("en fait", false),
        // B2
        new("il a", true), new("il est", true), new("elle a", true), new("elle est", true), new("on a", true), new("on est", true),
        new("ils ont", true), new("ils sont", true), new("je suis", true), new("tu es", true), new("tu as", true),
    };

    /// <summary>The clitics whose mute e is not heard in fluent speech (« est-c(e) que » → « eske »): the third key of a row
    /// writes only their consonant.</summary>
    private static readonly Dictionary<string, string> MuteClitic = new(StringComparer.Ordinal)
    {
        ["ce"] = "s", ["que"] = "k", ["le"] = "l", ["de"] = "d", ["je"] = "Z", ["me"] = "m", ["te"] = "t", ["se"] = "s", ["ne"] = "n",
    };

    /// <summary>A bare form's sound as Lexique writes it (SAMPA), for the pronunciation keys (« s'il » → <c>s</c> + <c>il</c>).</summary>
    public static string BareSampa(string bare) => bare switch { "j" => "Z", "qu" => "k", "c" => "s", _ => bare };

    /// <summary>The row's tokens as the n-gram tables write them: lower case, cut at spaces, apostrophes and hyphens, « œ »
    /// folded (« qu'est-ce que » → qu est ce que).</summary>
    public static string[] Tokens(string written)
    {
        var o = new List<string>(4);
        var sb = new StringBuilder();
        foreach (var ch in written.ToLowerInvariant())
        {
            if (ch is ' ' or '-' || Elision.IsApostrophe(ch)) { if (sb.Length > 0) { o.Add(TokenFold.Fold(sb.ToString())); sb.Clear(); } }
            else sb.Append(ch);
        }
        if (sb.Length > 0) o.Add(TokenFold.Fold(sb.ToString()));
        return o.ToArray();
    }

    /// <summary>The row as a pupil glues it: its letters without the separators (« il y a » → « ilya »).</summary>
    public static string Glued(string written)
    {
        var sb = new StringBuilder(written.Length);
        foreach (var ch in written) if (!(ch is ' ' or '-' || Elision.IsApostrophe(ch))) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>
    /// The sound keys of a row (distinct, at most 3), each with the length of its first word's part (the bar needs more typed
    /// sound than the first word: « il » never shows « il y a »): (1) the fold of the glued spelling (« ilya » → <c>ilia</c>);
    /// (2) the tokens' Lexique pronunciations read as one word, its inner schwas kept (« silteplai »); (3) the same with the
    /// mute e of the clitics silent (« eske » → est-ce que, « keske », « toulmonde »). <paramref name="phon"/> gives a token's
    /// Lexique pronunciation (null: none — keys 2 and 3 are then left out).
    /// </summary>
    public static List<(string Key, int First)> Keys(string written, Func<string, string?> phon)
    {
        var toks = Tokens(written);
        var o = new List<(string, int)>(3);
        void Add(string k, int first) { if (k.Length > 0 && !o.Exists(x => x.Item1 == k)) o.Add((k, first)); }
        Add(PhoneticKey.Fold(Glued(written)), PhoneticKey.Fold(toks[0]).Length);
        var p = new string?[toks.Length];
        for (int i = 0; i < toks.Length; i++) p[i] = Elision.IsBare(toks[i]) ? BareSampa(toks[i]) : phon(toks[i]);
        if (Array.Exists(p, string.IsNullOrEmpty)) return o;
        Add(PhoneticKey.FromLexiquePhon(string.Concat(p)), PhoneticKey.FromLexiquePhon(p[0]!).Length);
        var q = new string[toks.Length];
        for (int i = 0; i < toks.Length; i++) q[i] = MuteClitic.TryGetValue(toks[i], out var c) ? c : p[i]!;
        Add(PhoneticKey.FromLexiquePhon(string.Concat(q)), PhoneticKey.FromLexiquePhon(q[0]).Length);
        return o;
    }

    /// <summary>Every token of every row (the load reads their pronunciations as it reads the lexicon).</summary>
    public static readonly IReadOnlySet<string> AllTokens = BuildAllTokens();

    private static HashSet<string> BuildAllTokens()
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in Rows) foreach (var t in Tokens(r.Written)) s.Add(t);
        return s;
    }
}
