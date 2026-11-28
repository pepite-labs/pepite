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

    // ---- 0.11.0 (plan negation): the group tile of the « ne » marker
    /// <summary>What the « ne » marker's group tile is about (UI thread): the group as typed (<see cref="Original"/>, its
    /// separators included), the separators typed after it (<see cref="Tail"/>), the corrected group (straight apostrophes),
    /// the tracker version and the window when the list was shown, and that list.</summary>
    internal sealed record GroupTarget(string Original, string Tail, string Replacement, long Version, IntPtr Fg,
                                       IReadOnlyList<Predict.Core.Suggestion> List, string Rule);

    /// <summary>
    /// The decision after the read of a group replacement (pure): only when the document read is Ok and ends with the group
    /// and its tail (<paramref name="agrees"/>) — anything else does NOTHING (plan negation, § 3): the keys alone never delete
    /// several words, so the keys-only allow-list (<see cref="KeysOnlyHosts"/>) and the hosts that cannot be read
    /// (LibreOffice: <see cref="NeverKeysOnly"/>) get nothing, and a group is never inserted at the caret.
    /// </summary>
    public static Action DecideGroup(ReadStatus status, bool agrees) =>
        status == ReadStatus.Ok && agrees ? Action.Replace : Action.Nothing;

    /// <summary>
    /// Does the document text before the caret end with the group <paramref name="span"/> and then <paramref name="tail"/>
    /// — the letters in any case, ' and ’ alike (an app may curl them), a line break as <c>\r\n</c>, <c>\n</c> or <c>\r</c>,
    /// every other separator exactly — with no letter glued before? <paramref name="docSpan"/>: the group as the document
    /// has it.
    /// </summary>
    public static bool DocumentEndsWith(string before, string span, string tail, out string docSpan)
    {
        docSpan = "";
        if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(span) || string.IsNullOrEmpty(tail)) return false;
        int at = before.Length;
        for (int k = tail.Length - 1; k >= 0; k--)
        {
            char c = tail[k];
            if (at == 0) return false;
            if (IsLineBreak(c))
            {
                if (!IsLineBreak(before[at - 1])) return false;
                at -= at >= 2 && before[at - 2] == '\r' && before[at - 1] == '\n' ? 2 : 1;
            }
            else if (Same(before[at - 1], c)) at--;
            else return false;
        }
        int start = at - span.Length;
        if (start < 0) return false;
        for (int i = 0; i < span.Length; i++) if (!Same(before[start + i], span[i])) return false;
        if (start > 0 && Predict.Core.SpellingIndicator.IsWordChar(before[start - 1])) return false;
        docSpan = before.Substring(start, span.Length);
        return true;

        static bool Same(char a, char b) =>
            a == b || Predict.Core.Elision.IsApostrophe(a) && Predict.Core.Elision.IsApostrophe(b) || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
    }

    // ---- 0.11.0 (plan hyphen-names): a compound name tile that rewrites the head (« saint-n » → « Saint-Nicolas »)
    /// <summary>What a compound tile is about (UI thread, set by the update that showed it): the head as typed, the text it
    /// replaces (<see cref="Span"/>: head, hyphen, letters typed after it), the tracker version and the window, and the bar.</summary>
    internal sealed record CompoundTarget(string Head, string Span, long Version, IntPtr Fg, IReadOnlyList<Predict.Core.Suggestion> List);

    /// <summary>
    /// The decision after the read of a compound replacement (pure). Ok and the document ends with the span → replace the
    /// span by the whole name. Ok but the document differs, Timeout, a selection, Unavailable → the ending goes in at the
    /// caret (« saint-Nicolas »: the head is never deleted unseen) — Unavailable on the keys-only allow-list
    /// (<see cref="KeysOnlyHosts"/>, empty in the product) replaces from the keys. Stale (a key or a jump came first) or a
    /// password field → nothing.
    /// </summary>
    public static Action DecideCompound(ReadStatus status, bool agrees, bool keysOnlyAllowed) => status switch
    {
        ReadStatus.Stale or ReadStatus.Password => Action.Nothing,
        ReadStatus.Ok => agrees ? Action.Replace : Action.Insert,
        // as the ≈ path: the keys alone only over a separator such hosts do not rewrite (the span's own « - »)
        ReadStatus.Unavailable => keysOnlyAllowed && KeysOnlyCertain('-') ? Action.ReplaceKeysOnly : Action.Insert,
        _ => Action.Insert,
    };

    /// <summary>Does the document text before the caret end with <paramref name="span"/> (letters in any case: an app may
    /// have capitalised it) right at the caret, with no letter or hyphen glued before? <paramref name="docSpan"/>: the span as
    /// the document has it.</summary>
    public static bool DocumentEndsWithSpan(string before, string span, out string docSpan)
    {
        docSpan = "";
        if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(span) || before.Length < span.Length) return false;
        int start = before.Length - span.Length;
        if (string.Compare(before, start, span, 0, span.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        if (start > 0 && (Predict.Core.SpellingIndicator.IsWordChar(before[start - 1]) || before[start - 1] == '-')) return false;
        docSpan = before.Substring(start);
        return true;
    }

    /// <summary>The corrected group with a capital first letter when it opens a sentence (« c'est pas » at a start → « Ce
    /// n'est pas ») or when the document has one there (<paramref name="docSpan"/>: the app may have capitalised « C'est
    /// pas »); otherwise as the detector gave it (it keeps the typed capital).</summary>
    public static string CasedGroup(string replacement, bool sentenceStart, string? docSpan = null)
    {
        if (string.IsNullOrEmpty(replacement) || !char.IsLower(replacement[0])) return replacement;
        bool upper = sentenceStart || docSpan is { Length: > 0 } d && char.IsUpper(d[0]);
        return upper ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement;
    }

    /// <summary>The apostrophe the replacement is written with: the document's (a curled one in the group read), else
    /// <paramref name="fallback"/> (the tracker's <see cref="WordTracker.ApostropheStyle"/>).</summary>
    public static char ApostropheOf(string docSpan, char fallback) =>
        docSpan.IndexOf('’') >= 0 ? '’' : docSpan.IndexOf('\'') >= 0 ? '\'' : fallback;

    /// <summary>The keys of a group replacement: <see cref="Build(int, string, char, UIntPtr)"/> over the group and its tail
    /// (the tail's last separator plays the word's separator: a line break is never deleted nor retyped).</summary>
    public static Native.INPUT[] BuildGroup(string original, string tail, string replacement, UIntPtr marker) =>
        Build(original.Length + tail.Length - 1, replacement + tail[..^1], tail[^1], marker);

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

/// <summary>
/// 0.11.0 (plan negation, owner 2026-11-27): which windows' text the keyboard can read for a group replacement — the « ne »
/// marker is shown only where choosing its group can replace the words (the replacement needs a read). A window never read
/// yet follows its host: a host known not to give its text (LibreOffice today: <see cref="HostApps.UiaTextUnavailable(IntPtr)"/>)
/// starts unreadable, any other readable. Any Ok read makes it readable (a LibreOffice window that becomes readable gets the
/// marker back by itself); only the group's own correction read coming back Unavailable makes it unreadable (re-review: a
/// focus or caret read is Unavailable for reasons of content or timing in readable apps — the caret after « 2024 », no
/// focused element yet, Word busy — and must not hide the marker). Keyed by window and process id (a recycled handle in
/// another process is « never read »). Thread-safe; bounded (cleared when full).
/// </summary>
internal sealed class ReadableWindows
{
    private readonly Dictionary<IntPtr, (uint Pid, bool Ok)> _known = new();
    private readonly object _gate = new();

    /// <param name="groupRead">the read of a « ne » group replacement (the only read whose Unavailable is learned).</param>
    public void Note(IntPtr fg, uint pid, ReadStatus status, bool groupRead)
    {
        if (fg == IntPtr.Zero) return;
        bool? ok = status == ReadStatus.Ok ? true : status == ReadStatus.Unavailable && groupRead ? false : null;
        if (ok is not { } v) return;
        lock (_gate)
        {
            if (_known.Count >= 64 && !_known.ContainsKey(fg)) _known.Clear();
            _known[fg] = (pid, v);
        }
    }

    /// <param name="unreadableHost">the window's host is known not to give its text (before any read of it).</param>
    public bool MayReplace(IntPtr fg, uint pid, bool unreadableHost)
    {
        lock (_gate) return _known.TryGetValue(fg, out var k) && k.Pid == pid ? k.Ok : !unreadableHost;
    }
}
