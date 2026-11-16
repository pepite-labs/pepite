using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Predict.Core;

public enum TokenKind
{
    /// <summary>Letters (incl. <c>œ æ</c>), joined by an internal <c>-</c> (<c>peut-être</c>) or a non-elision
    /// apostrophe (<c>aujourd'hui</c>, <c>quelqu'un</c>).</summary>
    Word,
    /// <summary>An elided word with its apostrophe (<c>l'</c>, <c>qu’</c>): a token of its own.</summary>
    Elision,
    /// <summary>Digits, with an internal decimal comma / point (<c>1,5</c>).</summary>
    Number,
    /// <summary>One run of spaces (incl. the no-break spaces U+00A0 and U+202F) and tabs.</summary>
    Space,
    /// <summary><c>. ! ? …</c> (a run of them is one token) — ends a sentence.</summary>
    SentenceEnd,
    /// <summary>A line or paragraph break (<c>\r\n</c> is one token) — ends a sentence too.</summary>
    Break,
    /// <summary>Any other character (<c>, ; : ( ) « »</c>, symbols…), one per token.</summary>
    Punct,
}

/// <param name="Start">offset in the original text (UTF-16 units).</param>
/// <param name="Text">the original characters (display / insertion keep <c>’</c>).</param>
/// <param name="Norm">lower case, <c>’</c> → <c>'</c> (matching).</param>
public readonly record struct Token(TokenKind Kind, int Start, int Length, string Text, string Norm)
{
    public int End => Start + Length;
}

/// <summary>
/// The French tokenizer shared by the predictor's punctuation rules (plan 04), the re-read of edited text
/// (plan 06) and the selection reading with highlighting (plan 07). Pure; every token keeps its offsets in the
/// original text. Abbreviations (<c>M. Dupont</c>) end a sentence: an accepted limit.
/// </summary>
public static class Tokenizer
{
    public static bool IsLetter(char c) => char.IsLetter(c);

    /// <summary>A combining mark (NFD text: <c>e</c> + U+0301): part of the word it follows.</summary>
    public static bool IsMark(char c) =>
        CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static bool InWord(char c) => IsLetter(c) || IsMark(c);

    /// <summary>Closing punctuation that stays with the sentence it ends (<c>« Il pleut. »</c>).</summary>
    public static bool IsClosing(char c) => c is '»' or ')' or ']' or '"' or '”' or '’' or '›';

    /// <summary>Spaces, including the no-break spaces U+00A0 and U+202F, and Tab (not line breaks).</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\u00A0' or '\u202F' or '\u2007' or '\u2009' ||
                                          (char.IsWhiteSpace(c) && !IsBreak(c));

    public static bool IsBreak(char c) => c is '\r' or '\n' or '\v' or '\f' or '\u2028' or '\u2029' or '\u0085';

    public static bool IsSentenceEnd(char c) => c is '.' or '!' or '?' or '…';

    /// <summary>Split <paramref name="text"/> into tokens covering every character, in order.</summary>
    public static IReadOnlyList<Token> Tokenize(string? text)
    {
        var o = new List<Token>();
        if (string.IsNullOrEmpty(text)) return o;
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            int s = i;
            if (IsLetter(c))
            {
                // a word: letters, joined by '-' or an apostrophe when a letter follows
                while (true)
                {
                    while (i < n && InWord(text[i])) i++;
                    if (i + 1 < n && IsLetter(text[i + 1]))
                    {
                        if (text[i] == '-') { i++; continue; }
                        if (Elision.IsApostrophe(text[i]))
                        {
                            // the run since the word start or the last joiner: "l" in "l'arbre", "jusqu" in "jusqu'ici"
                            int runStart = i;
                            while (runStart > s && InWord(text[runStart - 1])) runStart--;
                            if (runStart == s && Elision.IsBare(text.Substring(s, i - s))) break;   // elision: own token
                            i++;
                            continue;
                        }
                    }
                    break;
                }
                if (i < n && Elision.IsApostrophe(text[i]) && Elision.IsBare(text.Substring(s, i - s)))
                {
                    i++;   // keep the apostrophe in the elision token
                    o.Add(Make(TokenKind.Elision, text, s, i));
                }
                else o.Add(Make(TokenKind.Word, text, s, i));
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < n && (char.IsDigit(text[i]) ||
                                 (text[i] is ',' or '.' && i + 1 < n && char.IsDigit(text[i + 1]) && i > s))) i++;
                o.Add(Make(TokenKind.Number, text, s, i));
                continue;
            }
            if (IsSpace(c))
            {
                while (i < n && IsSpace(text[i])) i++;
                o.Add(Make(TokenKind.Space, text, s, i));
                continue;
            }
            if (IsBreak(c))
            {
                i++;
                if (c == '\r' && i < n && text[i] == '\n') i++;
                o.Add(Make(TokenKind.Break, text, s, i));
                continue;
            }
            if (IsSentenceEnd(c))
            {
                while (i < n && IsSentenceEnd(text[i])) i++;
                o.Add(Make(TokenKind.SentenceEnd, text, s, i));
                continue;
            }
            i += char.IsHighSurrogate(c) && i + 1 < n && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            o.Add(Make(TokenKind.Punct, text, s, i));
        }
        return o;
    }

    /// <summary>Sentences as [start, end) ranges of the original text: each ends after its <c>. ! ? …</c> (and the
    /// closing <c>» ) "</c> right after it, spaces between allowed) or before its line break; leading spaces and
    /// breaks are not part of a sentence; empty ones are skipped.</summary>
    public static IReadOnlyList<(int Start, int End)> Sentences(string? text)
    {
        var o = new List<(int, int)>();
        int start = -1, end = -1;
        bool closing = false;   // a sentence end was seen: closing quotes / brackets may still follow
        foreach (var t in Tokenize(text))
        {
            if (closing)
            {
                if (t.Kind == TokenKind.Space) continue;
                if (t.Kind == TokenKind.Punct && t.Length == 1 && IsClosing(t.Text[0])) { end = t.End; continue; }
                o.Add((start, end));
                start = -1; closing = false;
            }
            switch (t.Kind)
            {
                case TokenKind.Space:
                    break;
                case TokenKind.Break:
                    if (start >= 0) o.Add((start, end));
                    start = -1;
                    break;
                case TokenKind.SentenceEnd:
                    if (start < 0) start = t.Start;
                    end = t.End;
                    closing = true;
                    break;
                default:
                    if (start < 0) start = t.Start;
                    end = t.End;
                    break;
            }
        }
        if (start >= 0) o.Add((start, end));
        return o;
    }

    /// <summary>The words of <paramref name="text"/> as the language model sees them: elisions stripped of their
    /// apostrophe (<c>l'</c> → <c>l</c>), lower case.</summary>
    public static IReadOnlyList<string> LmWords(string? text)
    {
        var o = new List<string>();
        foreach (var t in Tokenize(text))
            if (t.Kind == TokenKind.Word) o.Add(t.Norm);
            else if (t.Kind == TokenKind.Elision) o.Add(t.Norm[..^1]);
        return o;
    }

    private static Token Make(TokenKind k, string text, int s, int e)
    {
        var t = text.Substring(s, e - s);
        return new Token(k, s, e - s, t, Normalise(t));
    }

    private static string Normalise(string t)
    {
        var sb = new StringBuilder(t.Length);
        foreach (var c in t) sb.Append(c == '’' ? '\'' : char.ToLowerInvariant(c));
        return sb.ToString().Normalize(NormalizationForm.FormC);   // NFD "réalité" matches the lexicon
    }
}
