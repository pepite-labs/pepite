using System;
using System.Collections.Generic;

namespace Predict.Keyboard;

/// <summary>What the hook does with F1…F8 (owner 2026-11-25, listen before inserting; see
/// <see cref="KeyboardPolicy.FKeyAction"/>).</summary>
internal enum FKeyAct
{
    /// <summary>The key goes to the application (keys off, Ctrl / Alt, Shift without option A, no word under it).</summary>
    Pass,
    /// <summary>Insert the word at once (« Touches F1–F8 : insérer tout de suite »: the behaviour before 2026-11-25, an
    /// auto-repeat inserts again).</summary>
    Insert,
    /// <summary>A plain press with « Touches F1–F8 : 1er appui : écouter, 2e : insérer » (the default): the first press
    /// speaks, a second one inserts.</summary>
    Press,
    /// <summary>Option A (Maj + F1…F8): speak the word only.</summary>
    Listen,
    /// <summary>An auto-repeated press of a key already handled: swallowed, nothing done (never to the application, even
    /// when the bar changed under the held key).</summary>
    Swallow,
}

/// <summary>
/// Owner request 2026-11-25: listen before inserting. Two independent settings decide the plain presses: « Touches F1–F8 »
/// (1st press listens and rings the tile, a 2nd press on it inserts — the default — or insert at once) and « Clic sur un
/// mot » (<see cref="ClickMode"/>: insert at once; 1st click listens, 2nd inserts; or — the default — a click listens and a
/// double click or a right-click inserts, with no ring). A listen gesture (right-click, Maj+F<i>n</i>, the speaker band)
/// arms the tile when a confirming press exists (<see cref="Rings"/>). Pure state and decisions, owned by the controller
/// on the UI thread.
/// <para>A pending tile is identified by its index, its word, the tracker version and the foreground window: a press that
/// finds any of them changed is a new first press (it speaks the word now under that key) — never a blind insert. No
/// timeout (owner answer 6). Cancelled by the controller on any other key, a jump, a hidden or rebuilt bar with another word
/// at that index, the ≈ list, a profile switch, a pause, the reading mode.</para>
/// </summary>
internal sealed class ListenFirst
{
    /// <summary>The tile whose word was heard and waits for its confirming press.</summary>
    public sealed record Pending(int Index, string Word, long Version, IntPtr Fg);

    public enum Outcome
    {
        /// <summary>Speak the word; the tile is now armed.</summary>
        Listen,
        /// <summary>The press confirms the pending tile: insert (the pending state is consumed).</summary>
        Insert,
    }

    public Pending? Current { get; private set; }
    public bool Armed => Current != null;

    /// <summary>Does a press on tile <paramref name="index"/> showing <paramref name="word"/> confirm the pending one?</summary>
    public bool Matches(int index, string word, long version, IntPtr fg) =>
        Current is { } p && p.Index == index && p.Word == word && p.Version == version && p.Fg == fg;

    /// <summary>A plain press (F<i>n</i>, click, tap) on a tile: <see cref="Outcome.Insert"/> when it confirms the pending
    /// tile, else <see cref="Outcome.Listen"/> with the tile armed (a press on another tile moves the ring there).</summary>
    public Outcome Press(int index, string word, long version, IntPtr fg)
    {
        if (Matches(index, word, version, fg)) { Current = null; return Outcome.Insert; }
        Current = new Pending(index, word, version, fg);
        return Outcome.Listen;
    }

    /// <summary>A listen gesture (right-click, Maj+F<i>n</i>, the speaker band): the tile is armed (re-armed when it is the
    /// pending one: it stays armed); the next plain press on it inserts.</summary>
    public void Arm(int index, string word, long version, IntPtr fg) => Current = new Pending(index, word, version, fg);

    /// <summary>Drop the pending tile and forget the last heard word (a key, a jump, a hidden bar, an insertion).</summary>
    /// <returns>true when a pending tile was dropped.</returns>
    public bool Cancel()
    {
        bool had = Current != null;
        Current = null;
        LastHeard = null;
        return had;
    }

    /// <summary>
    /// The bar was rebuilt without any cancelling event (a picture arrived, a settings change, a field-probe answer, a caret
    /// read that re-seeded the tracker): the same word still at the same index, among the <paramref name="visible"/> tiles,
    /// in the same window → kept (its version re-stamped: the explicit cancel events, not the version, guard against typing,
    /// so a read's version churn never drops it — plan risk « version churn »); anything else → cancelled (also when the word
    /// moved to another index).
    /// </summary>
    /// <returns>the index to ring again, or null (nothing pending, or cancelled). The last heard word is kept (re-stamped) or
    /// forgotten on its own, by the same rule, whatever happens to the ring (review of 238c58b: a rebuild with no ring must
    /// not forget it, or the double click that follows inserts as if nothing had been heard).</returns>
    public int? KeepAfterRebuild(IReadOnlyList<string> words, int visible, long version, IntPtr fg)
    {
        // the last heard word (mode ListenDouble: no ring) follows the same rule, silently
        if (LastHeard is { } h)
            LastHeard = h.Index < words.Count && h.Index < visible && words[h.Index] == h.Word && h.Fg == fg ? h with { Version = version } : null;
        if (Current is not { } p) return null;
        if (p.Index < words.Count && p.Index < visible && words[p.Index] == p.Word && p.Fg == fg)
        {
            Current = p with { Version = version };
            return p.Index;
        }
        Current = null;
        return null;
    }

    /// <summary>Owner answer 5 (amended 2026-11-25): the confirming press does not speak the word again — unless what the
    /// insertion says differs from what was heard: a liaison group (« les amis » after hearing « amis ») or an elision /
    /// compound group (« l'école » after hearing « école »; d', j', qu', n', s', « peut-être »). Case is not heard.
    /// <paramref name="heard"/> null = not a confirmation (the word is spoken as before).</summary>
    public static bool SayAtInsert(string said, string? heard) =>
        heard is null || !string.Equals(said, heard, StringComparison.OrdinalIgnoreCase);

    /// <summary>The last tile whose word was spoken by a listen gesture (any mode, ringed or not): an insertion of that same
    /// tile (same word, tracker version, window) is a confirmation — the word is not said again (owner answer 5). Mode
    /// <see cref="ClickMode.ListenDouble"/> needs it: its click speaks without arming anything (no ring), and the double
    /// click or right-click that follows inserts the word just heard.</summary>
    public Pending? LastHeard { get; private set; }

    public void NoteHeard(int index, string word, long version, IntPtr fg) => LastHeard = new Pending(index, word, version, fg);

    /// <summary>Was tile <paramref name="index"/> showing <paramref name="word"/> heard just before (pending, or the last
    /// listen gesture), with nothing typed nor moved since?</summary>
    public bool WasHeard(int index, string word, long version, IntPtr fg) =>
        Matches(index, word, version, fg) ||
        LastHeard is { } h && h.Index == index && h.Word == word && h.Version == version && h.Fg == fg;

    /// <summary>
    /// Owner addendum 2026-11-25: what a left click / tap on the word part of tile does. With the speaker band (D) on, the
    /// band is the listen gesture of the mouse and touch, so a click on the word inserts at once whatever the click mode
    /// (owner answer 4 kept; the Réglages list is greyed). <paramref name="secondOfDouble"/>: this click is the second one of
    /// a double click (<see cref="KeyboardPolicy.IsSecondClickOfDouble"/>, the Windows double-click time) — ignored by the
    /// insert-at-once and listen-first modes (one press, owner answer 7), the insertion in
    /// <see cref="ClickMode.ListenDouble"/> — but only when <paramref name="heardHere"/>: the word now under it is the one
    /// just heard on that tile (<see cref="WasHeard"/>). Anything else (the bar was rebuilt, an adjacent tile inside the
    /// double-click rectangle, click → right-click → click) listens: never a blind insert (review of 238c58b).
    /// </summary>
    public static ClickAct ClickAction(ClickMode mode, bool band, bool secondOfDouble, bool heardHere) =>
        (band ? ClickMode.Insert : mode) switch
        {
            ClickMode.ListenDouble => secondOfDouble && heardHere ? ClickAct.Insert : ClickAct.Listen,
            ClickMode.ListenFirst => secondOfDouble ? ClickAct.Ignore : ClickAct.Press,
            _ => secondOfDouble ? ClickAct.Ignore : ClickAct.Insert,
        };

    /// <summary>
    /// Owner 2026-11-25 (« UIA Invoke »): what a UI Automation Invoke on a tile's word does (screen reader, voice access,
    /// switch tools that go through UIA rather than a simulated mouse). It follows « Touches F1–F8 », not « Clic sur un
    /// mot »: <paramref name="keysListenFirst"/> (« 1er appui : écouter, 2e : insérer », the default) → <see cref="ClickAct.Press"/>
    /// (the 1st Invoke speaks and rings the tile, a 2nd one on the same tile — same index, word, tracker version, window, as
    /// <see cref="Press"/> — inserts); « Insérer tout de suite » → <see cref="ClickAct.Insert"/>. Neither the click mode, nor
    /// the speaker band (whose own Invoke still listens: it is its Click), nor the F-keys being off (an Invoke is no key), nor
    /// the ≈ list (its tiles are tiles) changes it. Never <see cref="ClickAct.Ignore"/>: an Invoke is never a double click.
    /// </summary>
    public static ClickAct InvokeAction(bool keysListenFirst) => keysListenFirst ? ClickAct.Press : ClickAct.Insert;

    /// <summary>A right-click (touch: press-and-hold) on a tile inserts in <see cref="ClickMode.ListenDouble"/> (band off);
    /// everywhere else it listens.</summary>
    public static bool RightClickInserts(ClickMode mode, bool band) => !band && mode == ClickMode.ListenDouble;

    /// <summary>Does a listen gesture (right-click, Maj+F<i>n</i>, the speaker band) arm and ring the tile? Only when a
    /// confirming press exists: the F-keys in « 1er appui : écouter, 2e : insérer » (<paramref name="keysListenFirst"/>, with
    /// the F-keys on), or a click in <see cref="ClickMode.ListenFirst"/> without the band. In
    /// <see cref="ClickMode.ListenDouble"/> a click never arms (no ring, owner addendum 5): a click then F3 is not a
    /// confirmation, F3 follows the keyboard setting.</summary>
    public static bool Rings(ClickMode mode, bool band, bool keysListenFirst) =>
        keysListenFirst || !band && mode == ClickMode.ListenFirst;
}

/// <summary>What a left click / tap on a tile's word does (<see cref="ListenFirst.ClickAction"/>).</summary>
internal enum ClickAct
{
    /// <summary>Insert the word (a word heard just before is not said again).</summary>
    Insert,
    /// <summary>« 1er clic : écouter, 2e clic : insérer »: a first press speaks and rings, a second one inserts.</summary>
    Press,
    /// <summary><see cref="ClickMode.ListenDouble"/>: speak the word, nothing armed (no ring).</summary>
    Listen,
    /// <summary>The second click of a double click, in a mode where a double click counts as one press.</summary>
    Ignore,
}
