using System;
using System.Collections.Generic;
using System.Text;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>What a tile of the reading view shows: a word (with its picture slot), a number, or punctuation.</summary>
internal enum TileKind { Word, Number, Punct }

/// <summary>One tile of the reading view (D21): a word, a number or a punctuation mark, with its place in the original
/// text. An elision is joined to the word after it (<c>l'arbre</c> = one tile, picture of <c>arbre</c>).</summary>
/// <param name="PictureKey">the word whose picture goes above the tile (lower case), or null (numbers, punctuation, a
/// lone elision).</param>
internal readonly record struct ReadingTile(TileKind Kind, int Start, int Length, string Text, int Sentence, string? PictureKey)
{
    public int End => Start + Length;
    public bool IsWord => Kind != TileKind.Punct;
}

/// <summary>A sentence of the reading document: [Start, End) in the text, its tiles [FirstTile, EndTile).</summary>
internal readonly record struct ReadingSentence(int Start, int End, int FirstTile, int EndTile, bool HasWords);

/// <summary>What is handed to the speech engine for one sentence (or the rest of one, after a pause): the text, and
/// where its first character is in the document. The text has the same length as the original span (a few characters
/// replaced one for one: no-break spaces, line breaks, ’), so an engine position maps back by adding <see cref="Start"/>.</summary>
internal sealed record Utterance(int Sentence, int Start, string Text)
{
    /// <summary>Document offset of an engine character position (clamped to the utterance).</summary>
    public int ToDocument(int charPos) => Start + Math.Clamp(charPos, 0, Math.Max(0, Text.Length - 1));
}

/// <summary>
/// The selected text as the reading mode shows and reads it (plan 07, D21/D24). Pure: built from <see cref="Tokenizer"/>
/// tokens with their offsets; at most <see cref="MaxWords"/> words (D24: beyond, the text is cut before the next word
/// and <see cref="Truncated"/> is set). One utterance per sentence (<see cref="Tokenizer.Sentences"/>), and the mapping
/// from an engine character position back to the tile being spoken (<see cref="TileAt"/>).
/// </summary>
internal sealed class ReadingDocument
{
    public const int MaxWords = 1000;

    /// <summary>The text actually read (cut after the last word kept).</summary>
    public string Text { get; }
    public IReadOnlyList<ReadingTile> Tiles { get; }
    public IReadOnlyList<ReadingSentence> Sentences { get; }
    public int WordCount { get; }
    /// <summary>The selection had more than <see cref="MaxWords"/> words.</summary>
    public bool Truncated { get; }
    /// <summary>LibreOffice phase 2: only part of the selection could be read (its own message).</summary>
    public bool Partial { get; private init; }
    /// <summary>De-duplication key of the selection it was built from (trimmed).</summary>
    public string Key { get; }

    private ReadingDocument(string text, List<ReadingTile> tiles, List<ReadingSentence> sentences, int words, bool truncated, string key)
    {
        Text = text; Tiles = tiles; Sentences = sentences; WordCount = words; Truncated = truncated; Key = key;
    }

    /// <param name="tailAt">D26: index where the sentence-end punctuation added after the selection starts (-1 = none): those
    /// punctuation tiles are attached to the last word's tile (« bien ! »), not shown apart.</param>
    /// <param name="cut">the text was already cut by the reader's character limit: <see cref="Truncated"/> (message shown).</param>
    /// <param name="partial">LibreOffice phase 2: only part of the selection was read (<see cref="Partial"/>).</param>
    public static ReadingDocument Build(string? selection, int maxWords = MaxWords, int tailAt = -1, bool cut = false,
                                        Func<string, bool>? isName = null, bool partial = false)
    {
        string full = selection ?? "";
        var toks = Tokenizer.Tokenize(full);
        // 1. cut before the (maxWords+1)-th word; an elision glued to its word counts once
        int words = 0, cutAt = full.Length;
        bool truncated = cut;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            bool word = t.Kind is TokenKind.Word or TokenKind.Number || t.Kind == TokenKind.Elision && !GluedWord(toks, i);
            if (!word) continue;
            if (words == maxWords)
            {
                cutAt = i > 0 && GluedWord(toks, i - 1) ? toks[i - 1].Start : t.Start;   // « l'ami »: cut before the elision
                truncated = true;
                break;
            }
            words++;
        }
        string text = full.Substring(0, cutAt).TrimEnd();
        if (truncated) toks = Tokenizer.Tokenize(text);

        // 2. sentences, then one tile per word / number / punctuation mark
        var ranges = Tokenizer.Sentences(text);
        var tiles = new List<ReadingTile>();
        int s = 0;
        int SentenceOf(int offset)
        {
            while (s + 1 < ranges.Count && offset >= ranges[s].End) s++;
            return ranges.Count == 0 ? 0 : s;
        }
        // plan 10C (owner Q13): a word written with a capital inside a sentence is a name — never the picture of its lowercase
        // homograph (« Pierre » is not a stone); it gets the name's own picture (a flag, the teacher's), if any. At a sentence
        // start the capital says nothing: a known name (pack or lists) is a name, any other word keeps its common picture.
        int lastWordSentence = -1;
        string? KeyOf(Token w, int sentence, string? norm)
        {
            bool first = sentence != lastWordSentence;
            lastWordSentence = sentence;
            if (norm is null || w.Text.Length == 0 || !char.IsUpper(w.Text[0]) || IsAllCaps(w.Text)) return norm;
            var form = w.Text.Normalize().Replace('’', '\'');
            return !first || (isName?.Invoke(form) ?? false) ? NameKeyPrefix + form : norm;
        }
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            switch (t.Kind)
            {
                case TokenKind.Space:
                case TokenKind.Break:
                    continue;
                case TokenKind.Elision when GluedWord(toks, i):
                {
                    var w = toks[i + 1];
                    int sn = SentenceOf(t.Start);
                    lastWordSentence = sn;   // « L'Afrique »: the elision opens the sentence, « Afrique » is inside it
                    tiles.Add(new ReadingTile(TileKind.Word, t.Start, w.End - t.Start, text.Substring(t.Start, w.End - t.Start), sn, KeyOf(w, sn, w.Norm)));
                    i++;
                    continue;
                }
                case TokenKind.Elision:   // a lone elision (« l' » at the end): no picture (D4b)
                    lastWordSentence = SentenceOf(t.Start);
                    tiles.Add(new ReadingTile(TileKind.Word, t.Start, t.Length, t.Text, SentenceOf(t.Start), null));
                    continue;
                case TokenKind.Word:
                {
                    int sn = SentenceOf(t.Start);
                    tiles.Add(new ReadingTile(TileKind.Word, t.Start, t.Length, t.Text, sn, KeyOf(t, sn, t.Norm)));
                    continue;
                }
                case TokenKind.Number:
                    tiles.Add(new ReadingTile(TileKind.Number, t.Start, t.Length, t.Text, SentenceOf(t.Start), null));
                    continue;
                default:
                    tiles.Add(new ReadingTile(TileKind.Punct, t.Start, t.Length, t.Text, SentenceOf(t.Start), null));
                    continue;
            }
        }

        // D26: the added sentence end joins the last word's tile (its text runs from the word to the last mark)
        if (tailAt >= 0)
            for (int i = 1; i < tiles.Count; i++)
            {
                var t = tiles[i];
                if (t.Kind != TileKind.Punct || t.Start < tailAt) continue;
                var w = tiles[i - 1];
                if (w.Kind == TileKind.Punct) continue;
                tiles[i - 1] = w with { Length = t.End - w.Start, Text = text.Substring(w.Start, t.End - w.Start) };
                tiles.RemoveAt(i--);
            }

        var sentences = new List<ReadingSentence>();
        for (int k = 0, ti = 0; k < ranges.Count; k++)
        {
            int first = ti;
            bool hasWords = false;
            while (ti < tiles.Count && tiles[ti].Sentence == k) { hasWords |= tiles[ti].IsWord; ti++; }
            sentences.Add(new ReadingSentence(ranges[k].Start, ranges[k].End, first, ti, hasWords));
        }
        return new ReadingDocument(text, tiles, sentences, words, truncated, SelectionPolicy.Key(full)) { Partial = partial };
    }

    /// <summary>Plan 10C: the picture key of a name tile is this prefix + the name as written (« \u0001Belgique »): looked up in
    /// <see cref="WordPictures"/>, never in the ARASAAC pack.</summary>
    public const string NameKeyPrefix = "\u0001";

    private static bool IsAllCaps(string w)
    {
        int letters = 0;
        foreach (var c in w) if (char.IsLetter(c)) { if (!char.IsUpper(c)) return false; letters++; }
        return letters >= 2;   // « SNCB », « ONU »: an acronym keeps its (lowercase) picture key
    }

    /// <summary>An elision immediately followed by a word (no space): one tile.</summary>
    private static bool GluedWord(IReadOnlyList<Token> toks, int i) =>
        toks[i].Kind == TokenKind.Elision && i + 1 < toks.Count && toks[i + 1].Kind == TokenKind.Word && toks[i + 1].Start == toks[i].End;

    /// <summary>The utterance for <paramref name="sentence"/>, from its start or from tile <paramref name="fromTile"/>
    /// (resume after a pause) to its end.</summary>
    public Utterance UtteranceOf(int sentence, int fromTile = -1)
    {
        var s = Sentences[sentence];
        int start = fromTile >= s.FirstTile && fromTile < s.EndTile ? Tiles[fromTile].Start : s.Start;
        return new Utterance(sentence, start, SpeakText(Text, start, s.End - start));
    }

    /// <summary>The span as given to the engine: same length, one-for-one replacements only (no-break and narrow spaces →
    /// space, line breaks and tabs → space, ’ → '), so engine positions need no re-mapping.</summary>
    public static string SpeakText(string text, int start, int length)
    {
        var sb = new StringBuilder(length);
        for (int i = start; i < start + length; i++)
        {
            char c = text[i];
            sb.Append(c switch
            {
                '\u00A0' or '\u202F' or '\u2007' or '\u2009' or '\t' or '\r' or '\n' or '\v' or '\f' or '\u2028' or '\u2029' or '\u0085' => ' ',
                '’' => '\'',
                _ => c,
            });
        }
        return sb.ToString();
    }

    /// <summary>
    /// The word tile an engine progress event points at (document offset <paramref name="offset"/>, <paramref name="length"/>
    /// characters), in <paramref name="sentence"/>: the word tile containing the offset; else the first word tile that
    /// starts inside [offset, offset + length) (an event on the space before a word); else -1 = keep the current
    /// highlight (punctuation, spaces). A number the voice expands into several words (« 2026 ») gives several events
    /// inside the same tile: it stays highlighted.
    /// </summary>
    public int TileAt(int sentence, int offset, int length)
    {
        if (sentence < 0 || sentence >= Sentences.Count) return -1;
        var s = Sentences[sentence];
        for (int i = s.FirstTile; i < s.EndTile; i++)
        {
            var t = Tiles[i];
            if (t.Start > offset) break;
            if (t.IsWord && offset < t.End) return i;
        }
        int end = offset + Math.Max(1, length);
        for (int i = s.FirstTile; i < s.EndTile; i++)
        {
            var t = Tiles[i];
            if (t.Start >= end) break;
            if (t.IsWord && t.Start >= offset) return i;
        }
        return -1;
    }

    /// <summary>First word tile of a sentence, or -1.</summary>
    public int FirstWordOf(int sentence)
    {
        if (sentence < 0 || sentence >= Sentences.Count) return -1;
        var s = Sentences[sentence];
        for (int i = s.FirstTile; i < s.EndTile; i++) if (Tiles[i].IsWord) return i;
        return -1;
    }
}
