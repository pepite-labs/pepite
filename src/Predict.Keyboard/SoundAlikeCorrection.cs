using System;
using System.Collections.Generic;
using System.Linq;

namespace Predict.Keyboard;

/// <summary>
/// 0.8.3 (owner test in Word, 2026-11-14): a sound-alike chosen from the ≈ marker (click / F9 list, then a click or an
/// F-key) <b>replaces</b> the misspelled word it is about — « mmmmal␣ » ≈ → « mal » gives « mal␣ », not « mmmmal mal␣ ».
/// <see cref="Target"/> is what the marker was about when its list was shown (UI thread): the word, the one separator
/// that finished it, the tracker version and the window. The replacement is made only when nothing moved since (same
/// version, same window, <see cref="WordTracker.CanCorrect"/>) and a read of the document decides (<see cref="Decide"/>,
/// D23 safeguard: never delete what is not seen). The read is ALWAYS made: whether the tracker's state came from a read
/// says nothing about the host (Pause / Reprendre, a profile switch, typing during the focus read, a failed read all
/// leave it « keys only » in Word too — review 0.8.3). A host whose text cannot be read gets the word INSERTED at the
/// caret (the 0.8.1 behaviour): the keys alone never delete there (re-review 0.8.3: LibreOffice, WPS, ONLYOFFICE, a
/// document drawn on a browser canvas… rewrite words with their own AutoCorrect and cannot be read). The only exception is
/// an explicit allow-list of hosts known not to rewrite words (<see cref="KeysOnlyHosts"/>): EMPTY in the product, filled
/// only by the test switch <c>--keys-only-replace &lt;process&gt;</c>; even then <see cref="NeverKeysOnly"/> is a hard deny.
/// Pure helpers here; the controller does the read and the injection.
/// </summary>
internal static class SoundAlikeCorrection
{
    /// <param name="Word">the finished word as the tracker has it (never logged).</param>
    /// <param name="Sep">the separator that finished it (' ', ',', '\n'…).</param>
    /// <param name="List">the sound-alike list shown (the bar's words): only a word of that list corrects.</param>
    internal sealed record Target(string Word, char Sep, long Version, IntPtr Fg, IReadOnlyList<Predict.Core.Suggestion> List);

    /// <summary>What the correction read leads to.</summary>
    public enum Action { Replace, ReplaceKeysOnly, Insert, Nothing }

    /// <summary>
    /// The decision after the correction read (pure, unit-tested): Stale (a key or a jump since) or Password → nothing;
    /// Ok → replace when the document ends with the word and its separator (<paramref name="agrees"/>), else insert at
    /// the caret (the app changed the word: AutoCorrect, a text service); Unavailable (no text to read) → insert, unless
    /// the host is on the allow-list (<paramref name="keysOnlyAllowed"/>, <see cref="KeysOnlyAllowed"/>) and the
    /// separator is a plain one (<see cref="KeysOnlyCertain"/>); anything else (Timeout, a selection) → insert.
    /// </summary>
    public static Action Decide(ReadStatus status, bool agrees, char sep, bool keysOnlyAllowed) => status switch
    {
        ReadStatus.Stale or ReadStatus.Password => Action.Nothing,
        ReadStatus.Ok => agrees ? Action.Replace : Action.Insert,
        ReadStatus.Unavailable => keysOnlyAllowed && KeysOnlyCertain(sep) ? Action.ReplaceKeysOnly : Action.Insert,
        _ => Action.Insert,
    };

    /// <summary>Hosts (process name without .exe) where a word may be replaced from the keys alone, without a read. EMPTY in
    /// the product; the test switch <c>--keys-only-replace &lt;process&gt;</c> adds one (the VM's keys-only test control).
    /// Set once at start, before the hooks run.</summary>
    public static readonly HashSet<string> KeysOnlyHosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Applications that rewrite typed words themselves (AutoCorrect, smart quotes, a canvas editor in a browser),
    /// documented and denied even if someone adds them to <see cref="KeysOnlyHosts"/>: Microsoft Office, LibreOffice, WPS,
    /// ONLYOFFICE, the browsers (Google Docs and other canvas editors), the new Outlook, Teams.</summary>
    private static readonly HashSet<string> NeverKeysOnly = new(HostApps.LibreOffice, StringComparer.OrdinalIgnoreCase)
    {
        "WINWORD", "OUTLOOK", "POWERPNT", "EXCEL", "ONENOTE", "MSACCESS", "MSPUB", "VISIO",
        "wps", "et", "wpp", "DesktopEditors",
        "chrome", "msedge", "firefox", "brave", "opera", "olk", "ms-teams", "Teams",
    };

    /// <summary>The host rewrites typed words (hard deny of the keys-only replacement).</summary>
    public static bool RewritesWords(string? process) => !string.IsNullOrEmpty(process) && NeverKeysOnly.Contains(process);

    /// <summary>May <paramref name="process"/> get the keys-only replacement? Only an allow-listed host that is not a
    /// known rewriter.</summary>
    public static bool KeysOnlyAllowed(string? process) =>
        !string.IsNullOrEmpty(process) && KeysOnlyHosts.Contains(process) && !RewritesWords(process);

    public static bool IsLineBreak(char c) => c is '\n' or '\r';

    /// <summary>A keys-only host: the keys alone are enough only for a separator such hosts do not rewrite and that is
    /// not a line break (Enter may have changed field, an editor may indent the new line).</summary>
    public static bool KeysOnlyCertain(char sep) => !IsLineBreak(sep) && !WordTracker.IsBarrier(sep, keysOnly: true);

    /// <summary>
    /// Does the document text before the caret (<paramref name="before"/>) end with <paramref name="word"/> (any case: an
    /// app may have capitalised it) and then <paramref name="sep"/> — a line break as <c>\r\n</c>, <c>\n</c> or <c>\r</c> —
    /// with no letter glued before the word? <paramref name="docWord"/>: the word as the document has it.
    /// </summary>
    public static bool DocumentEndsWith(string before, string word, char sep, out string docWord)
    {
        docWord = "";
        if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(word)) return false;
        int sepLen;
        if (IsLineBreak(sep))
            sepLen = before.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : IsLineBreak(before[^1]) ? 1 : 0;
        else
            sepLen = before[^1] == sep ? 1 : 0;
        if (sepLen == 0) return false;
        int start = before.Length - sepLen - word.Length;
        if (start < 0) return false;
        var w = before.Substring(start, word.Length);
        if (!string.Equals(w, word, StringComparison.OrdinalIgnoreCase)) return false;
        if (start > 0 && Predict.Core.SpellingIndicator.IsWordChar(before[start - 1])) return false;
        docWord = w;
        return true;
    }

    /// <summary>The correction with the capital of the original word, when it had one (« Maizon » → « Maison »), all
    /// capitals when it was all capitals (« MAIZON » → « MAISON »); the correction's own capitals (a name) are kept.</summary>
    public static string CasedLike(string correction, string original)
    {
        if (string.IsNullOrEmpty(correction) || string.IsNullOrEmpty(original)) return correction;
        // all capitals (« MAIZON », two letters at least) → all capitals (« MAISON »)
        if (original.Length > 1 && original.Any(char.IsLetter) && original.Where(char.IsLetter).All(char.IsUpper))
            return correction.ToUpperInvariant();
        if (char.IsUpper(original[0]) && char.IsLower(correction[0]))
            return char.ToUpperInvariant(correction[0]) + correction[1..];
        return correction;
    }

    /// <summary>The keys of the replacement (pure, unit-tested). A plain separator: Backspace over the word and its
    /// separator, then the correction and the same separator. A line break: Left (back over it, to the end of the word),
    /// Backspace over the word, the correction, Right — the line break itself is never deleted nor retyped (Enter is not
    /// sent again: a chat might send, a list might continue).</summary>
    public static Native.INPUT[] Build(int wordLength, string correction, char sep, UIntPtr marker)
    {
        if (!IsLineBreak(sep)) return Inserter.Build(0, wordLength + 1, correction + sep, marker);
        var inner = Inserter.Build(0, wordLength, correction, marker);
        var arrows = Inserter.BuildBeforeLast(false, "", marker);   // Left down/up, Right down/up
        var list = new List<Native.INPUT>(inner.Length + 4) { arrows[0], arrows[1] };
        list.AddRange(inner);
        list.Add(arrows[2]); list.Add(arrows[3]);
        return list.ToArray();
    }
}
