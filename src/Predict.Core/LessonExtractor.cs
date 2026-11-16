namespace Predict.Core;

/// <param name="Form">as it will be stored: lowercase, or with its capital when the text writes it with one away from a
/// sentence start (« Vésuve »).</param>
/// <param name="IsNew">unknown to the lexicon (« nouveau »), else « connu ».</param>
/// <param name="Ticked">offered ticked: a new word, or a word outside the 300 most frequent ones (content words).</param>
public readonly record struct LessonWord(string Form, bool IsNew, bool Ticked);

/// <summary>
/// « Nouvelle leçon… » (plan 10A): the distinct words of a pasted lesson text, in reading order, for the teacher to tick.
/// Pure. Only the ticked words are stored by the app; the text itself is never kept. Words of one letter, numbers and
/// elisions (<c>l'</c>) are left out; hyphenated words (« peut-être », « Saint-Gilles ») stay whole.
/// </summary>
public static class LessonExtractor
{
    public const int MaxWords = 500;
    public const int MaxLength = 40;

    /// <param name="knows">is the (lowercase) word in the lexicon?</param>
    /// <param name="frequent">the most frequent lexicon forms (lowercase), unticked by default.</param>
    public static IReadOnlyList<LessonWord> Extract(string? text, Func<string, bool> knows, IReadOnlySet<string> frequent, int max = MaxWords)
    {
        var order = new List<string>();
        var named = new Dictionary<string, string>(StringComparer.Ordinal);   // lowercase -> the capitalised form seen mid-sentence
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool start = true;
        foreach (var t in Tokenizer.Tokenize(text))
        {
            switch (t.Kind)
            {
                case TokenKind.Space: continue;
                case TokenKind.SentenceEnd: case TokenKind.Break: start = true; continue;
                case TokenKind.Word: break;
                default: start = t.Kind == TokenKind.Punct && t.Text is "«" or "\"" or "—" or "-" ? start : false; continue;
            }
            bool atStart = start;
            start = false;
            var form = t.Text.Normalize().Replace('’', '\'');   // NFC: a decomposed « é » is the same word
            if (form.Length < 2 || form.Length > MaxLength || form.Any(char.IsDigit)) continue;
            var lower = form.ToLowerInvariant();
            if (seen.Add(lower)) order.Add(lower);
            if (!atStart && char.IsUpper(form[0]) && !named.ContainsKey(lower) && !form.All(c => !char.IsLetter(c) || char.IsUpper(c)))
                named[lower] = form;
        }
        var o = new List<LessonWord>(Math.Min(order.Count, max));
        foreach (var lower in order)
        {
            if (o.Count >= max) break;
            bool isNew = !knows(lower);
            o.Add(new LessonWord(named.TryGetValue(lower, out var n) ? n : lower, isNew, isNew || !frequent.Contains(lower)));
        }
        return o;
    }
}
