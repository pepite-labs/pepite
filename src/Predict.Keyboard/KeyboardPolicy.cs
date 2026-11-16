using System;
using System.Linq;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>Small pure decisions of the keyboard predictor, kept apart so they are unit-tested.</summary>
internal static class KeyboardPolicy
{
    /// <summary>A caret read slower than this is discarded (keys stay the source): a late result would
    /// describe a caret the user may already have moved.</summary>
    public const int ReadTimeoutMs = 150;

    /// <summary>Apply a read only if nothing was typed since it was requested (same tracker version), the
    /// same window is still in front, and it was fast enough.</summary>
    public static bool ShouldApply(long reqVersion, long curVersion, IntPtr reqFg, IntPtr curFg, long ms)
        => reqVersion == curVersion && reqFg == curFg && ms <= ReadTimeoutMs;

    /// <summary>Budget of a read used by the D23 re-read (begin / landing of an edit session). The 150 ms tracker budget
    /// guards against a caret the user moved meanwhile; for the session the version check does that (any key, click or
    /// jump changes the tracker version), so a slower read of a long Word paragraph (158 ms in the owner's run) is still
    /// exact. Beyond this, it is dropped anyway (silence rather than a late re-read).</summary>
    public const int RereadReadBudgetMs = 400;

    /// <summary>A read past the tracker budget (status Timeout) that reached the text (it has a window: the password
    /// check passed before any text was read) may still begin or land a re-read session when nothing was typed since
    /// its request, the same window is in front, and it took at most <see cref="RereadReadBudgetMs"/>.</summary>
    public static bool UsableLateForReread(bool timedOut, bool hasWindow, long ms, long reqVersion, long curVersion, IntPtr reqFg, IntPtr curFg)
        => timedOut && hasWindow && ms <= RereadReadBudgetMs && reqVersion == curVersion && reqFg == curFg;

    /// <summary>Add a space after the inserted word unless the word is an elision (<c>l'</c>, <c>qu’</c>: the next
    /// word follows without a space, D22) or the text already continues with whitespace, punctuation or a control
    /// char (Word's paragraph/cell marks). Unknown next char (keys only) or end of text → space, as before.</summary>
    public static bool NeedsSpace(bool known, char next, string word = "")
        => !(word.Length > 0 && Elision.IsApostrophe(word[^1])) &&
           (!known || next == '\0' || !(char.IsWhiteSpace(next) || char.IsPunctuation(next) || char.IsControl(next)));

    /// <summary>Match the case the user started with (<c>Mai</c> → <c>Maison</c>, <c>MAI</c> → <c>MAISON</c>); at the
    /// start of a sentence (D22) a lower-case or empty prefix gives a capital too (<c>Le</c>, <c>L'</c>, <c>Qu'</c>).</summary>
    public static string Cased(string word, string typed, bool sentenceStart)
    {
        if (word.Length == 0) return word;
        if (typed.Length > 0 && char.IsUpper(typed[0]))
        {
            if (typed.Length > 1 && typed.All(c => !char.IsLetter(c) || char.IsUpper(c))) return word.ToUpperInvariant();
            return char.ToUpperInvariant(word[0]) + word[1..];
        }
        return sentenceStart ? char.ToUpperInvariant(word[0]) + word[1..] : word;
    }

    /// <summary>
    /// The bar's words after <see cref="Cased"/> (plan 10A, Q13): « Pi » makes the lexicon's « pierre » into « Pierre »,
    /// the same text as a pupil's « Pierre »: one tile, at the first one's place, marked as a name when either is one (a
    /// name never shows the common noun's picture). Order kept; at most <paramref name="max"/> words.
    /// </summary>
    public static System.Collections.Generic.List<Suggestion> CasedDistinct(System.Collections.Generic.IEnumerable<Suggestion> ranked,
                                                                            string typed, bool sentenceStart, int max)
    {
        var o = new System.Collections.Generic.List<Suggestion>();
        foreach (var s in ranked)
        {
            var w = Cased(s.Word, typed, sentenceStart);
            int i = o.FindIndex(x => x.Word == w);
            if (i >= 0) { if (s.Name && !o[i].Name) o[i] = o[i] with { Name = true }; continue; }
            if (o.Count >= max) continue;   // a later duplicate may still mark a kept tile as a name
            o.Add(s with { Word = w });
        }
        return o;
    }

    /// <summary>U+202F NARROW NO-BREAK SPACE: French typography before <c>; : ! ?</c>.</summary>
    public const char NarrowNbsp = (char)0x202F;
    /// <summary>U+00A0 NO-BREAK SPACE: the fallback for apps / fonts that draw U+202F badly (hidden setting).</summary>
    public const char Nbsp = (char)0x00A0;

    /// <summary>Removed when typed right after our automatic space: <c>maison␣</c> + <c>,</c> → <c>maison,</c>.</summary>
    public static bool Hugs(char c) => c is ',' or '.' or '…' or ')' or '\'' or '’' or '-';

    /// <summary>French typography puts a (narrow no-break) space before these.</summary>
    public static bool HighPunct(char c) => c is ';' or ':' or '!' or '?';

    /// <summary>
    /// Plan 10B (re-review): did Enter really start a new line? The system caret of the focused control before Enter (read in
    /// the hook, before the app handles the key) and after the line-end delay: the same caret window, and its top lower on
    /// the screen. A single-line field never passes — Save As / Run / a search box (the caret stays on its line while a save
    /// keeps the dialog open), a chat that sends on Enter (the caret goes back up) — and neither does a host without a system
    /// caret (zero), where no line-end marker is shown.
    /// </summary>
    public static bool NewLineAfterEnter(IntPtr caretBefore, int topBefore, IntPtr caretAfter, int topAfter) =>
        caretBefore != IntPtr.Zero && caretAfter == caretBefore && topAfter > topBefore;

    /// <summary>0.8.3 (owner: never in a terminal): is the focused UI Automation element the hidden input of an xterm.js
    /// terminal (VS Code's integrated terminal, cloud shells)? Its class name or automation id holds
    /// <c>xterm-helper-textarea</c>. Pure.</summary>
    public static bool IsWebTerminal(string? className, string? automationId) =>
        className != null && className.Contains("xterm-helper-textarea", StringComparison.Ordinal) ||
        automationId != null && automationId.Contains("xterm-helper-textarea", StringComparison.Ordinal);

    /// <summary>0.8.3: is <paramref name="b"/> the second click of a double click begun by <paramref name="a"/> — within the
    /// double-click time, on the same tile or within the double-click rectangle (<paramref name="cx"/> × <paramref name="cy"/>
    /// centred on the first point)? A quick click on another tile further away is a pick of its own. Pure.</summary>
    public static bool IsSecondClickOfDouble(TileClick a, TileClick b, uint dblMs, int cx, int cy)
    {
        int dt = unchecked(b.Time - a.Time);
        if (dt < 0 || dt >= dblMs) return false;
        return b.Tile == a.Tile || Math.Abs(b.X - a.X) <= cx / 2 && Math.Abs(b.Y - a.Y) <= cy / 2;
    }

    /// <summary>Plan 10B (final re-review): the finished word an update may mark. A word finished by a line break is marked
    /// only for the tracker version the line-end check accepted (<paramref name="acceptedVersion"/>); any later state — a
    /// space typed after a chat sent its line on Enter — never marks it.</summary>
    public static Predict.Core.FinishedWord? FinishedForMark(Predict.Core.FinishedWord? finished, long version, long acceptedVersion) =>
        finished is { After: '\n' or '\r' } && version != acceptedVersion ? null : finished;

    /// <summary>
    /// What to do with character <paramref name="c"/> typed right after the space we added behind an inserted word
    /// (D22). <see cref="AutoSpaceFix.Keep"/>: the key goes through untouched. <see cref="AutoSpaceFix.Absorb"/>: the
    /// key is swallowed (a second space, only once: a deliberate third one goes through).
    /// <see cref="AutoSpaceFix.Replace"/>: the key is swallowed and our space is replaced by <c>Text</c> (one
    /// Backspace, then <c>Text</c>, in one SendInput).
    /// </summary>
    public static (AutoSpaceFix Fix, string Text) AfterAutoSpace(char c, PunctuationSpacing mode, bool spaceAbsorbed,
                                                                 bool ctrlOrAlt = false)
    {
        if (ctrlOrAlt) return (AutoSpaceFix.Keep, "");   // AltGr / Ctrl / Alt held: a Backspace would be a shortcut
        if (c == ' ') return spaceAbsorbed ? (AutoSpaceFix.Keep, "") : (AutoSpaceFix.Absorb, "");
        if (Hugs(c)) return (AutoSpaceFix.Replace, c.ToString());
        if (HighPunct(c))
            return mode switch
            {
                PunctuationSpacing.None => (AutoSpaceFix.Replace, c.ToString()),
                PunctuationSpacing.FrenchNbsp => (AutoSpaceFix.Replace, Nbsp.ToString() + c),
                _ => (AutoSpaceFix.Replace, NarrowNbsp.ToString() + c),
            };
        return (AutoSpaceFix.Keep, "");
    }
}

/// <summary>See <see cref="KeyboardPolicy.AfterAutoSpace"/>.</summary>
internal enum AutoSpaceFix { Keep, Absorb, Replace }

/// <summary>Result of reading back the text before the caret after <c>; : ! ?</c> in an app that may space it itself.</summary>
internal enum SpacingCheck
{
    /// <summary>The text does not end with the punctuation (the caret moved, something else was typed): do nothing.</summary>
    Unknown,
    /// <summary>Exactly one space-like character before the punctuation.</summary>
    Ok,
    /// <summary>No space before the punctuation: add ours.</summary>
    Missing,
    /// <summary>Two or more (the app added one next to ours): remove one.</summary>
    Double,
}

/// <summary>
/// Per-application rules of D22, by the foreground window's process name (pure). Owner decision « Laisser faire
/// Word », made adaptive (2026-11-09): Microsoft Word's French AutoCorrect may add its own no-break space before
/// <c>; : ! ?</c> — only when that AutoCorrect option is on and the text's proofing language is French. In Word we
/// therefore take our automatic space back and send the punctuation alone (or, when this window was seen not to add
/// one, our space + the punctuation), then read back what Word produced and add or remove one space (controller,
/// <see cref="Check"/>). Everywhere else the setting applies directly. Only Word for now; LibreOffice Writer (also
/// autocorrects French punctuation) is the next candidate.
/// </summary>
/// <summary>A click on a bar tile: its input message's time (32-bit tick count), the tile, the screen point.</summary>
internal readonly record struct TileClick(int Time, int Tile, int X, int Y);

internal static class AppRules
{
    /// <summary>U+0020, U+00A0, U+202F and the other Unicode space separators (U+2009 thin space…).</summary>
    public static bool IsSpaceLike(char c) => c is ' ' or '\u00A0' or '\u202F' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator;

    /// <summary>Classify <paramref name="before"/> (the text right before the caret) after <paramref name="punct"/> was typed.</summary>
    public static SpacingCheck Check(string? before, char punct)
    {
        if (string.IsNullOrEmpty(before) || before[^1] != punct) return SpacingCheck.Unknown;
        int n = 0;
        for (int i = before.Length - 2; i >= 0 && IsSpaceLike(before[i]); i--) n++;
        return n == 0 ? SpacingCheck.Missing : n == 1 ? SpacingCheck.Ok : SpacingCheck.Double;
    }

    /// <summary>The space our setting puts before <c>; : ! ?</c> ('\0' for « Aucune »).</summary>
    public static char SpaceFor(PunctuationSpacing setting) => setting switch
    {
        PunctuationSpacing.None => '\0',
        PunctuationSpacing.FrenchNbsp => KeyboardPolicy.Nbsp,
        _ => KeyboardPolicy.NarrowNbsp,
    };

    /// <summary>Processes whose own autocorrection spaces <c>; : ! ?</c> (name without .exe, case-insensitive).</summary>
    private static readonly string[] SpacesHighPunctuationItself = { "WINWORD" };

    public static bool AppSpacesPunctuation(string? process) =>
        !string.IsNullOrEmpty(process) &&
        Array.Exists(SpacesHighPunctuationItself, n => string.Equals(n, process, StringComparison.OrdinalIgnoreCase));

    /// <summary>The spacing actually used before <c>; : ! ?</c> in <paramref name="process"/>: <see cref="PunctuationSpacing.None"/>
    /// in an app that adds its own space (we must not add a second one), else the user's setting.</summary>
    public static PunctuationSpacing Effective(PunctuationSpacing setting, string? process) =>
        AppSpacesPunctuation(process) ? PunctuationSpacing.None : setting;
}

/// <summary>
/// The "our automatic space" state of D22 (the controller keeps one under its lock). Armed by an insertion that added
/// a space, for that tracker version in that foreground window: any other mutation of the tracker (a key, a jump, a
/// Backspace) changes the version and so disarms it, as does a window change. Pure, unit-tested.
/// </summary>
internal sealed class AutoSpaceState
{
    private long _version = -1;
    private IntPtr _fg;
    private bool _absorbed;
    private PunctuationSpacing? _mode;   // the app's effective spacing, fixed when armed (AppRules)

    /// <param name="mode">spacing for this window (<see cref="AppRules.Effective"/>); null = the mode given to OnChar.</param>
    public void Arm(long version, IntPtr fg, PunctuationSpacing? mode = null) { _version = version; _fg = fg; _absorbed = false; _mode = mode; }
    public void Disarm() => _version = -1;
    public bool IsArmed(long version, IntPtr fg) => _version >= 0 && _version == version && _fg == fg;

    /// <summary>Decide for a character typed at tracker <paramref name="version"/> in <paramref name="fg"/>. Keep and
    /// Replace disarm (Replace is applied once); Absorb keeps it armed so punctuation after the extra space still
    /// takes our space back.</summary>
    public (AutoSpaceFix Fix, string Text) OnChar(char c, long version, IntPtr fg, PunctuationSpacing mode, bool ctrlOrAlt = false)
    {
        if (!IsArmed(version, fg)) return (AutoSpaceFix.Keep, "");
        var d = KeyboardPolicy.AfterAutoSpace(c, _mode ?? mode, _absorbed, ctrlOrAlt);
        if (d.Fix == AutoSpaceFix.Absorb) _absorbed = true;
        else Disarm();
        return d;
    }
}
