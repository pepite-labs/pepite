namespace Predict.Core;

/// <summary>The « œ » / « æ » fold of the n-gram tables (plan corpus-light 0.1). Lexique spells « coeur », « soeur »,
/// « oeuvre », so the corpus build writes every token with « oe » / « ae »; the context words read from a document
/// must be folded the same way or « mon cœur » finds none of the « mon coeur » n-grams. Applied to one token after
/// the tokeniser's match, never to a whole line, and never to the typed prefix (a separate, deferred change).
/// Same rule as <c>fold()</c> in Data.Build/build_ngrams.py.</summary>
public static class TokenFold
{
    public static string Fold(string token) =>
        token.IndexOfAny(Ligatures) < 0 ? token
            : token.Replace("œ", "oe").Replace("æ", "ae").Replace("Œ", "Oe").Replace("Æ", "Ae");   // capitals: Predict.Eval --keep-case

    private static readonly char[] Ligatures = { 'œ', 'æ', 'Œ', 'Æ' };
}
