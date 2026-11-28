namespace Predict.Core;

/// <summary>
/// 0.11.0 (plan hyphen-names): the word before a typed hyphen — the head <see cref="PredictionEngine.PredictAfterHyphen"/>
/// completes (« saint- », « Saint-Pierre- », « a-t- »). One definition for the keyboard's word tracker (on the text it kept)
/// and for Predict.Cli / Predict.Eval: letters (<see cref="SpellingIndicator.IsWordChar"/>) joined by single inner hyphens,
/// right before one hyphen, at most <see cref="PredictionEngine.HyphenHeadMax"/> chars, with nothing glued before it (not a
/// letter, not a hyphen: « --x » or a head cut by the start of the known text is no head).
/// </summary>
public static class HyphenHead
{
    /// <summary>The head ending right before <paramref name="text"/>[<paramref name="hyphen"/>] (a <c>-</c>), found at or after
    /// <paramref name="min"/>; <paramref name="start"/> = its first letter. False when there is none.</summary>
    public static bool Find(string text, int hyphen, int min, out int start)
    {
        start = hyphen;
        if (text is null || hyphen <= min || hyphen >= text.Length || text[hyphen] != '-') return false;
        int i = hyphen - 1;
        while (i >= min)
        {
            if (SpellingIndicator.IsWordChar(text[i])) { start = i; i--; continue; }
            // a single inner hyphen between two letters (« Saint-Pierre »)
            if (text[i] == '-' && start < hyphen && start == i + 1 && i - 1 >= min && SpellingIndicator.IsWordChar(text[i - 1])) { i--; continue; }
            break;
        }
        if (start == hyphen || hyphen - start > PredictionEngine.HyphenHeadMax) return false;
        // glued to something before (a letter or hyphen before min, a double hyphen): not a head
        if (start - 1 >= 0 && (SpellingIndicator.IsWordChar(text[start - 1]) || text[start - 1] == '-')) return false;
        return true;
    }

    /// <summary>Does a capital at <paramref name="start"/> say nothing about a name — only separators back to a sentence end,
    /// a <see cref="CapitalContext.Opens"/> sign, or the start of the known text (<paramref name="min"/>)?</summary>
    public static bool NeutralBefore(string text, int start, int min)
    {
        for (int i = start - 1; i >= min; i--)
        {
            char c = text[i];
            if (SpellingIndicator.IsWordChar(c)) return false;
            if (c is '.' or '!' or '?' or '…' or '\n' or '\r' || CapitalContext.Opens(c)) return true;
        }
        return true;
    }
}
