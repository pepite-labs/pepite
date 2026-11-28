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

    /// <summary>The engine's words as the bar shows and inserts them: elisions with their apostrophe in the document's
    /// style (0.10.2: <c>j’</c> after a text that writes ’), noise dropped (<see cref="Elision.Apply"/>), then cased
    /// (<see cref="CasedDistinct"/>).</summary>
    /// <param name="hyphenHead">0.11.0: the word before the typed hyphen (<see cref="WordTracker.HyphenHead"/>): a compound
    /// tile « head-… » (<see cref="PredictionEngine.PredictAfterHyphen"/>) is already written as it goes in, never cased
    /// again (« saint-N » must not make « Saint-Nicolas » into anything else, nor « peut-ê » « Peut-être »).</param>
    public static System.Collections.Generic.List<Suggestion> Shown(System.Collections.Generic.IReadOnlyList<Suggestion> ranked, bool afterHyphen,
                                                                    char apostrophe, string typed, bool sentenceStart, int max,
                                                                    string? hyphenHead = null) =>
        CasedDistinct(Elision.Apply(ranked, afterHyphen).Select(s => s with { Word = Elision.Styled(s.Word, apostrophe) }),
                      typed, sentenceStart, max, hyphenHead);

    /// <summary>
    /// The bar's words after <see cref="Cased"/> (plan 10A, Q13): « Pi » makes the lexicon's « pierre » into « Pierre »,
    /// the same text as a pupil's « Pierre »: one tile, at the first one's place, marked as a name when either is one (a
    /// name never shows the common noun's picture). Order kept; at most <paramref name="max"/> words. 0.11.0: a compound
    /// tile of <paramref name="hyphenHead"/> keeps its form (<see cref="Shown"/>).
    /// </summary>
    public static System.Collections.Generic.List<Suggestion> CasedDistinct(System.Collections.Generic.IEnumerable<Suggestion> ranked,
                                                                            string typed, bool sentenceStart, int max, string? hyphenHead = null)
    {
        var o = new System.Collections.Generic.List<Suggestion>();
        foreach (var s in ranked)
        {
            var w = hyphenHead is not null && CompoundInsert(s.Word, hyphenHead) is not null ? s.Word : Cased(s.Word, typed, sentenceStart);
            int i = o.FindIndex(x => x.Word == w);
            if (i >= 0) { if (s.Name && !o[i].Name) o[i] = o[i] with { Name = true }; continue; }
            if (o.Count >= max) continue;   // a later duplicate may still mark a kept tile as a name
            o.Add(s with { Word = w });
        }
        return o;
    }

    /// <summary>0.11.0 (plan hyphen-names): how a compound tile goes in after « <paramref name="head"/>- ». Null: not a compound
    /// of that head (« head-… », any case, with something after the hyphen). <c>ReplaceHead</c>: the tile writes the head
    /// otherwise than it was typed (« saint- » → « Saint-Nicolas », owner Q1 = B) — the head must be replaced, read first;
    /// otherwise only <c>Ending</c> (the part after the head's hyphen) is inserted at the caret, in every host.</summary>
    public static (bool ReplaceHead, string Ending)? CompoundInsert(string word, string head)
    {
        if (string.IsNullOrEmpty(word) || string.IsNullOrEmpty(head) || word.Length <= head.Length + 1 || word[head.Length] != '-'
            || !word.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return null;
        return (string.CompareOrdinal(word, 0, head, 0, head.Length) != 0, word[(head.Length + 1)..]);
    }

    /// <summary>0.11.0 (owner Q2, decided 2026-11-27: the whole tile): what a compound tile that would replace the head shows where the window's text
    /// cannot be read (Calc, Impress, Draw: <see cref="ReadableWindows"/>), where only its ending can go in at the caret
    /// (« saint-Nicolas »). <see cref="CompoundUnreadable.WholeTile"/> (the owner's choice): the same « Saint-Nicolas » tile;
    /// <see cref="CompoundUnreadable.EndingTile"/>: the tile shows « Nicolas » only. The one place to switch.</summary>
    public const CompoundUnreadable UnreadableCompoundTile = CompoundUnreadable.WholeTile;

    /// <summary>0.11.0: the bar's tiles where the head cannot be replaced (<paramref name="mayReplace"/> false), per
    /// <see cref="UnreadableCompoundTile"/>: with <see cref="CompoundUnreadable.EndingTile"/> a tile that would replace the head
    /// shows its ending (a duplicate of a plain tile then dropped); otherwise the list as it is.</summary>
    public static System.Collections.Generic.List<Suggestion> CompoundTiles(System.Collections.Generic.List<Suggestion> shown, string? head,
                                                                            bool mayReplace, CompoundUnreadable mode = UnreadableCompoundTile)
    {
        if (head is null || mayReplace || mode == CompoundUnreadable.WholeTile) return shown;
        var o = new System.Collections.Generic.List<Suggestion>(shown.Count);
        foreach (var s in shown)
        {
            var w = CompoundInsert(s.Word, head) is { ReplaceHead: true } c ? c.Ending : s.Word;
            if (!o.Exists(x => x.Word == w)) o.Add(s with { Word = w });
        }
        return o;
    }

    /// <summary>0.10.2 (glued words plan, owner Q2): the words before the finished word the ≈ list is about — the tracker's
    /// context ends with that word once a separator finished it (case kept), so it is left out; a context that does not end
    /// with it (a sentence end cleared it) is given as it is.</summary>
    public static System.Collections.Generic.IReadOnlyList<string> WordsBefore(System.Collections.Generic.IReadOnlyList<string> context, string finished) =>
        context.Count > 0 && string.Equals(context[^1], finished, StringComparison.OrdinalIgnoreCase)
            ? context.Take(context.Count - 1).ToArray() : context;

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

    /// <summary>
    /// F1…F8 in the hook (owner 2026-11-25, listen before inserting). <paramref name="index"/> = 0 for F1. A fresh plain key
    /// with a word under it: <see cref="FKeyAct.Press"/> with « Touches F1–F8 : 1er appui : écouter, 2e : insérer »
    /// (<paramref name="keysListenFirst"/>, the default), else <see cref="FKeyAct.Insert"/>. Shift alone with option A on:
    /// <see cref="FKeyAct.Listen"/>. Anything else (keys off, Ctrl, Alt, Shift without A, no word under the key):
    /// <see cref="FKeyAct.Pass"/>.
    /// <paramref name="first"/> non-null = the key is still held and this is an auto-repeat of a down already decided
    /// (<see cref="HeldFirst"/>): only that first action repeats (review of 238c58b). A passed down stays passed (a released
    /// Ctrl / Shift never turns the repeats of Ctrl+F1 / Shift+F3 into inserts); a plain « insérer tout de suite » insert
    /// inserts again while a word is under the key and no modifier is held, as before; everything else is swallowed whatever
    /// the bar shows now (review of bcf6442: a held confirm key must not auto-repeat into the application after a correction
    /// or a bar without that index). Pure, O(1).
    /// </summary>
    public static FKeyAct FKeyAction(int index, bool ctrl, bool alt, bool shift, bool keysOn, bool listenShift, bool keysListenFirst,
                                     int suggCount, FKeyAct? first)
    {
        bool word = keysOn && index >= 0 && index < suggCount;
        switch (first)
        {
            case FKeyAct.Pass: return FKeyAct.Pass;
            case FKeyAct.Insert: return word && !ctrl && !alt && !shift ? FKeyAct.Insert : FKeyAct.Swallow;
            case not null: return FKeyAct.Swallow;
        }
        if (!word || ctrl || alt) return FKeyAct.Pass;
        if (shift) return listenShift ? FKeyAct.Listen : FKeyAct.Pass;
        return keysListenFirst ? FKeyAct.Press : FKeyAct.Insert;
    }

    /// <summary>
    /// The first action of a held F-key, or null when this down is a new press. <paramref name="held"/> /
    /// <paramref name="first"/>: the hook's per-key record (<see cref="FKeyHolds"/>: the action of the key's first down,
    /// cleared at its key-up). Third review of 238c58b: for a down the hook <b>swallowed</b> the record alone decides — an
    /// event a low-level hook eats probably never reaches the asynchronous key state, so <c>GetAsyncKeyState</c> would call
    /// every auto-repeat of a swallowed F1 a new press (listen, insert, listen…; after a correction the repeat would reach
    /// the app's F1 help). Only for a down that was <b>passed</b> (its state is updated by Windows) does
    /// <paramref name="keyWasDown"/> (the state read in the hook, before Windows updates it: down = already down) tell a
    /// repeat from a new press. No timer: Filter Keys (« Touches filtres ») repeat delays of 1.5–2 s and more stay
    /// repeats. A swallowed key's lost key-up is handled by resetting the records where key-ups go missing
    /// (<see cref="FKeyHolds"/>). Pure, O(1).
    /// </summary>
    public static FKeyAct? HeldFirst(bool held, FKeyAct first, bool keyWasDown) =>
        held && (first != FKeyAct.Pass || keyWasDown) ? first : null;

    /// <summary>Is the key-up of an F-key whose first down took <paramref name="first"/> swallowed? Yes when the hook handled
    /// that down (its down was swallowed: the application must not see a lone key-up); no when it was passed.</summary>
    public static bool SwallowFKeyUp(FKeyAct first) => first != FKeyAct.Pass;

    /// <summary>Shift, Ctrl, Alt (left / right), Caps Lock, Num Lock and the Windows keys: held or toggled on their own they
    /// never cancel a word heard on the bar (2026-11-25, listen before inserting). The same keys the hook lets through
    /// untracked.</summary>
    public static bool IsModifierOrLock(uint vk) => vk is 0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C or 0x90 or (>= 0xA0 and <= 0xA5);

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

    /// <summary>BUG-1 (VM run 2026-11-26): a UI Automation call from another process (an Invoke, a SetFocus — Narrateur,
    /// Accès vocal, a test script) makes the no-activate bar the foreground for a moment, before the tile's own Invoke
    /// runs. That is not the user going to one of our windows: counted as « own » it hid the bar, so the Invoke found no
    /// tile (ElementNotAvailable). The bar is therefore classified first: no jump, the foreground is given back, the text
    /// window stays the target. Our other windows (tray menu, À propos, Réglages, the reading window) stay « own ».</summary>
    /// <param name="fg">the new foreground window.</param>
    /// <param name="last">the foreground the controller tracks (the text window).</param>
    /// <param name="bar">the bar's window handle (zero before it exists).</param>
    /// <param name="shellOrOwn"><see cref="Native.IsShellOrOwn"/> of <paramref name="fg"/>.</param>
    public static ForegroundMove ClassifyForeground(IntPtr fg, IntPtr last, IntPtr bar, bool shellOrOwn)
    {
        if (fg == last) return ForegroundMove.Same;
        if (bar != IntPtr.Zero && fg == bar) return ForegroundMove.Bar;
        return shellOrOwn ? ForegroundMove.Own : ForegroundMove.Other;
    }

    /// <summary>Where the bar gives the foreground back when something activated it (<see cref="ClassifyForeground"/>),
    /// in the order to try: the window it took it from (WM_ACTIVATE's <paramref name="deactivated"/>), then the window
    /// the controller tracks (the next one is tried when a window is gone or refuses — a window closing as the
    /// activation moved). Never the bar itself, never zero, no duplicate; empty = nothing to give back to.</summary>
    public static IntPtr[] GiveBackTargets(IntPtr deactivated, IntPtr tracked, IntPtr bar)
    {
        bool usable(IntPtr h) => h != IntPtr.Zero && h != bar;
        if (!usable(deactivated)) return usable(tracked) ? new[] { tracked } : Array.Empty<IntPtr>();
        return usable(tracked) && tracked != deactivated ? new[] { deactivated, tracked } : new[] { deactivated };
    }
}

/// <summary>See <see cref="KeyboardPolicy.ClassifyForeground"/>. <c>Same</c>: no change; <c>Bar</c>: the bar was
/// activated from outside (not a jump); <c>Own</c>: our other windows or the taskbar / tray (Keep, bar hidden);
/// <c>Other</c>: another application (a leave).</summary>
internal enum ForegroundMove { Same, Bar, Own, Other }

/// <summary>See <see cref="KeyboardPolicy.AfterAutoSpace"/>.</summary>
internal enum AutoSpaceFix { Keep, Absorb, Replace }

/// <summary>See <see cref="KeyboardPolicy.UnreadableCompoundTile"/>.</summary>
internal enum CompoundUnreadable { WholeTile, EndingTile }

/// <summary>Result of reading back the text before the caret after <c>; : ! ?</c> in an app that may space it itself.</summary>
/// <summary>
/// The D22 read-back waiting for its answer (the controller keeps one under its lock): armed by the punctuation sent alone,
/// done once its answer was handled. While pending for the current version, no LibreOffice settle read is asked (it would
/// re-seed the tracker under the read-back); once done, the read-back's own settle read may run (review S3–S7: never
/// cleared, it blocked that settle read for good).
/// </summary>
internal sealed class PendingReadBack
{
    public (char Punct, bool Ours, IntPtr Fg, long Version, bool Lo) Current { get; private set; }

    public void Arm(char punct, bool ours, IntPtr fg, long version, bool lo) => Current = (punct, ours, fg, version, lo);

    public bool IsPending(IntPtr fg, long version) => Current.Fg != IntPtr.Zero && Current.Fg == fg && Current.Version == version;

    /// <summary>The answer for (<paramref name="fg"/>, <paramref name="version"/>) was handled; a newer one stays armed.</summary>
    public void Done(IntPtr fg, long version)
    {
        if (Current.Fg == fg && Current.Version == version) Current = default;
    }
}

/// <summary>The edit a D22 read-back makes (<see cref="AppRules.ReadBack"/>).</summary>
internal enum ReadBackFix { None, InsertOurs, RemoveOne }

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
/// <see cref="Check"/>). LibreOffice (0.11.0): our space is taken back too and the punctuation sent alone — its own
/// AutoCorrect puts one U+00A0 in French text; in a Writer window that answers through IAccessible2 (phase 1, owner Q1)
/// it is read back and our space added when LibreOffice added none — never a second one, its U+00A0 never removed;
/// without IA2 nothing is read back. Everywhere else the setting applies directly.
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

    /// <summary>Processes whose own autocorrection spaces <c>; : ! ?</c> and that we can read back (name without .exe,
    /// case-insensitive): the adaptive rule (<see cref="ReadsBackSpacing"/>).</summary>
    private static readonly string[] SpacesHighPunctuationItself = { "WINWORD" };

    /// <summary>The app spaces <c>; : ! ?</c> itself: Word (adaptive, read back) and LibreOffice (0.11.0, owner Q4 of the
    /// LibreOffice study: its French AutoCorrect deletes the spaces before the sign and puts one U+00A0 — a U+202F of ours
    /// was kept next to it, a double space; LibreOffice cannot be read back, so its U+00A0 is accepted as is and its options
    /// are never changed).</summary>
    public static bool AppSpacesPunctuation(string? process) => ReadsBackSpacing(process) || HostApps.IsLibreOffice(process);

    /// <summary>Word only: after the punctuation, read back what the app produced and add / remove one space (D22 adaptive).
    /// LibreOffice without IAccessible2 gives no text to read (keyboard-design § 5b): no read-back there.</summary>
    public static bool ReadsBackSpacing(string? process) =>
        !string.IsNullOrEmpty(process) &&
        Array.Exists(SpacesHighPunctuationItself, n => string.Equals(n, process, StringComparison.OrdinalIgnoreCase));

    /// <summary>LibreOffice phase 1 (owner Q1 = yes, 2026-11-27): Word, or LibreOffice when its Writer window answers through
    /// IAccessible2 (<paramref name="ia2Live"/>) — the sign alone first, then a read-back adds our space when LibreOffice
    /// added none (its AutoCorrect off, a paragraph in another language). IA2 not live: the 0.11.0 quick fix (no read-back).</summary>
    public static bool ReadsBackSpacing(string? process, bool ia2Live) =>
        ReadsBackSpacing(process) || ia2Live && HostApps.IsLibreOffice(process);

    /// <summary>
    /// What the read-back does with <paramref name="check"/>: Missing → our space (unless « Aucune »); Double → one removed
    /// in Word, never in LibreOffice (its U+00A0 is never touched: logged only); Ok / Unknown → nothing. In LibreOffice the
    /// sign always goes alone first (Word's shortcut « this window adds none: ours with the sign at once » is never used
    /// there, owner Q4), so only this read-back adds our space.
    /// </summary>
    public static ReadBackFix ReadBack(SpacingCheck check, PunctuationSpacing setting, bool libreOffice) => check switch
    {
        SpacingCheck.Missing => SpaceFor(setting) == '\0' ? ReadBackFix.None : ReadBackFix.InsertOurs,
        SpacingCheck.Double => libreOffice ? ReadBackFix.None : ReadBackFix.RemoveOne,
        _ => ReadBackFix.None,
    };

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

/// <summary>
/// The hook's record of F1…F8 held down (third review of 238c58b; hook thread only, fixed array, no allocation, O(1)).
/// Each key keeps the action of its first down until its key-up (<see cref="KeyboardPolicy.HeldFirst"/>). Where key-ups
/// really go missing the records are dropped: a foreground change (WinEvent or seen at a key-down: an elevated or secure
/// window takes the key-up), Pause / Reprendre (key-ups are not followed while paused),
/// a session lock / unlock (Win+L: the key-up goes to the secure desktop) — signalled by a generation counter the UI
/// thread increments and the hook compares at the next F-key down — and the hook install (a new instance).
/// </summary>
internal sealed class FKeyHolds
{
    private readonly (bool Held, FKeyAct First)[] _keys = new (bool, FKeyAct)[8];
    private long _seenGeneration;

    /// <summary>At a down of key <paramref name="index"/> (0 = F1): the first action of that key still held, or null for a
    /// new press. All records are dropped first when <paramref name="generation"/> moved (a session lock / unlock).</summary>
    public FKeyAct? Held(int index, bool keyWasDown, long generation)
    {
        if (generation != _seenGeneration) { _seenGeneration = generation; Clear(); }
        var h = _keys[index];
        return KeyboardPolicy.HeldFirst(h.Held, h.First, keyWasDown);
    }

    /// <summary>The action this down took (the first one of the hold is kept by the caller: <c>first ?? act</c>).</summary>
    public void Record(int index, FKeyAct first) => _keys[index] = (true, first);

    /// <summary>The key-up: the record ends. True = swallow it (its down was handled), false = pass it.</summary>
    public bool Up(int index)
    {
        var h = _keys[index];
        _keys[index] = default;
        return h.Held && KeyboardPolicy.SwallowFKeyUp(h.First);
    }

    /// <summary>A down passed untouched (our own window, a private host): no record, so its key-up passes too.</summary>
    public void Forget(int index) => _keys[index] = default;

    /// <summary>A foreground change: every key-up may have gone to another (elevated, secure) window.</summary>
    public void Clear() => Array.Clear(_keys);
}
