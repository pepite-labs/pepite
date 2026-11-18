using System.Collections.Generic;
using System.Globalization;

namespace Predict.Keyboard;

/// <summary>What a read of the text around the caret means for the tracker.</summary>
/// <param name="Valid">false = the window is not trustworthy (cut word, combining mark): fall back to keys.</param>
/// <param name="Prefix">word characters right before the caret (original case) — what prediction completes.</param>
/// <param name="Suffix">word characters right after the caret, only when the caret is inside a word — deleted on insert.</param>
/// <param name="NextChar">the character after the suffix, '\0' at the end of the text — decides the trailing space.</param>
/// <param name="Context">up to 3 previous words, reading order — the language-model context.</param>
/// <param name="Separators">when Prefix is empty: separator chars between the caret and the previous word.</param>
/// <param name="AtSentenceStart">the sentence starts right before the first context word (or the prefix): start of
/// the text, or a sentence end (<c>. ! ? …</c>, line break) — D22 capitalisation.</param>
/// <param name="Apostrophe">0.10.2: the last apostrophe (<c>'</c> or <c>’</c>) in the text read before the caret, '\0' = none:
/// the bar writes its elisions the same way (owner Q2: follow the document).</param>
internal readonly record struct TextContext(bool Valid, string Prefix, string Suffix, char NextChar,
                                            IReadOnlyList<string> Context, int Separators, bool AtSentenceStart = false,
                                            char Apostrophe = '\0')
{
    public static readonly TextContext Invalid = new(false, "", "", '\0', System.Array.Empty<string>(), 0);
}

/// <summary>
/// Turns a window of text around the caret into the state <see cref="WordTracker"/> would have reached had
/// the user typed that text: same word characters as the engine tokenizer, same barriers (a digit resets,
/// a sentence end clears the context). Pure, so it is unit-tested against the tracker (equivalence test).
/// </summary>
internal static class ContextExtractor
{
    /// <summary>Characters read before the caret / after it.</summary>
    public const int WindowChars = 200, AfterChars = 64;

    /// <summary>The engine tokenizer's set <c>[a-zà-ÿœ]</c> after lower-casing, without the division sign ÷ (U+00F7, inside
    /// that range by accident); the one definition is <see cref="Predict.Core.SpellingIndicator.IsWordChar"/>.</summary>
    public static bool IsWordChar(char c) => Predict.Core.SpellingIndicator.IsWordChar(c);   // one definition (plan 10B review)

    /// <summary>Ends a sentence for the context: . ! ? …, line/paragraph breaks and other control characters
    /// (Word's \r \v \a, Chromium's \n) except Tab, and U+FFFC (embedded object).</summary>
    public static bool IsSentenceEnd(char c) =>
        c is '.' or '!' or '?' or '…' or '\u2028' or '\u2029' or '\uFFFC' || (char.IsControl(c) && c != '\t');

    private static bool IsMark(char c)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    /// <summary>Nothing left of these counts: digit (tracker resets), combining mark, sentence end.</summary>
    private static bool IsBarrier(char c) => char.IsDigit(c) || IsMark(c) || IsSentenceEnd(c);

    /// <summary>For readers that return the text before and after the caret separately (UIA): the caret
    /// is at <c>before.Length</c>, in UTF-16 units, whatever the provider counts as one character.</summary>
    public static TextContext ExtractAround(string before, string after, bool truncatedAtStart, bool truncatedAtEnd)
        => Extract((before ?? "") + (after ?? ""), (before ?? "").Length, truncatedAtStart, truncatedAtEnd);

    /// <param name="truncatedAtStart">text[0] is not the start of the document (window cut).</param>
    /// <param name="truncatedAtEnd">the document continues after the last char of text.</param>
    public static TextContext Extract(string text, int caret, bool truncatedAtStart, bool truncatedAtEnd)
    {
        text ??= "";
        if (caret < 0) caret = 0;
        if (caret > text.Length) caret = text.Length;

        // 1. prefix
        int ps = caret;
        while (ps > 0 && IsWordChar(text[ps - 1])) ps--;
        string prefix = text.Substring(ps, caret - ps);
        if (ps > 0 && IsMark(text[ps - 1])) return TextContext.Invalid;
        if (prefix.Length > 0 && ps == 0 && truncatedAtStart) return TextContext.Invalid;

        // 2. suffix (only when the caret is inside a word) and 3. next char
        int se = caret;
        if (prefix.Length > 0)
        {
            while (se < text.Length && IsWordChar(text[se])) se++;
            if (se < text.Length && IsMark(text[se])) return TextContext.Invalid;
            if (se == text.Length && truncatedAtEnd && se > caret) return TextContext.Invalid;
        }
        string suffix = text.Substring(caret, se - caret);
        char next = se < text.Length ? text[se] : '\0';

        // 5. separators between the caret and the previous word / barrier (only when no prefix)
        int i = ps;
        int seps = 0;
        if (prefix.Length == 0)
        {
            while (i > 0 && !IsWordChar(text[i - 1]) && !IsBarrier(text[i - 1])) { i--; seps++; }
        }

        // 6. context: up to 3 word runs leftwards, separated by non-barrier separators
        var ctx = new List<string>(3);
        if (prefix.Length > 0)
        {
            // skip the separators before the prefix
            while (i > 0 && !IsWordChar(text[i - 1]) && !IsBarrier(text[i - 1])) i--;
        }
        bool cut = false;
        while (ctx.Count < 3 && i > 0 && IsWordChar(text[i - 1]))
        {
            int we = i;
            while (i > 0 && IsWordChar(text[i - 1])) i--;
            if (i == 0 && truncatedAtStart) { cut = true; break; }   // cut word: drop it, stop
            ctx.Add(text.Substring(i, we - i));
            while (i > 0 && !IsWordChar(text[i - 1]) && !IsBarrier(text[i - 1])) i--;
        }
        ctx.Reverse();
        // what precedes the first context word: the start of the text or a sentence end (not a 4th word, a cut
        // word, a digit or a combining mark)
        bool start = !cut && (i == 0 ? !truncatedAtStart : IsSentenceEnd(text[i - 1]));

        // 0.10.2: the document's apostrophe, the last one before the caret (the window is enough: a style is not mixed)
        char apostrophe = '\0';
        for (int k = caret - 1; k >= 0; k--) if (Predict.Core.Elision.IsApostrophe(text[k])) { apostrophe = text[k]; break; }

        return new TextContext(true, prefix, suffix, next, ctx, prefix.Length > 0 ? 0 : seps, start, apostrophe);
    }
}
