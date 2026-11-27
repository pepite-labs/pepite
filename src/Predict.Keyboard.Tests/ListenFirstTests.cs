using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Owner request 2026-11-25, listen before inserting: the pending tile (<see cref="ListenFirst"/>), the F-key
/// decision of the hook (<see cref="KeyboardPolicy.FKeyAction"/>), the click decision (<see cref="ListenFirst.ClickAction"/>,
/// every click mode × the speaker band), the settings (« Clic sur un mot » listenDouble and « Touches F1–F8 » 1st press
/// listens by default, A and D off; layered, lockable), the non-interrupting speech of a confirmation and the bar geometry
/// of option D (the band under the word: taller, never wider).</summary>
public class ListenFirstTests
{
    private static readonly IntPtr Fg = new(0x1234), Other = new(0x5678);

    // ------------------------------------------------------------------ pending tile
    [Fact]
    public void Same_Tile_Twice_Listens_Then_Inserts()
    {
        var l = new ListenFirst();
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(0, "maison", 7, Fg));
        Assert.True(l.Armed);
        Assert.Equal(ListenFirst.Outcome.Insert, l.Press(0, "maison", 7, Fg));
        Assert.False(l.Armed);                                                   // consumed
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(0, "maison", 7, Fg));   // a third press starts again
    }

    [Fact]
    public void Another_Tile_Moves_The_Ring_And_Speaks()
    {
        var l = new ListenFirst();
        l.Press(0, "maison", 7, Fg);
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(1, "mairie", 7, Fg));
        Assert.Equal(1, l.Current!.Index);
        Assert.Equal(ListenFirst.Outcome.Insert, l.Press(1, "mairie", 7, Fg));
    }

    [Theory]
    [InlineData(0, "maisons", 7, false)]   // another word under the same key
    [InlineData(0, "maison", 8, false)]    // the tracker moved (typing, a jump)
    [InlineData(0, "maison", 7, true)]     // another window in front
    public void A_Changed_Identity_Is_A_New_First_Press_Never_A_Blind_Insert(int index, string word, long version, bool otherWindow)
    {
        var l = new ListenFirst();
        l.Press(0, "maison", 7, Fg);
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(index, word, version, otherWindow ? Other : Fg));
        Assert.Equal(word, l.Current!.Word);   // re-armed on what was pressed now
    }

    [Fact]
    public void A_Listen_Gesture_Arms_And_The_Next_Plain_Press_Inserts()
    {
        var l = new ListenFirst();
        l.Arm(2, "voiture", 3, Fg);                                               // right-click, Maj+F3, the speaker band
        Assert.Equal(ListenFirst.Outcome.Insert, l.Press(2, "voiture", 3, Fg));   // F3 or a click
        l.Arm(2, "voiture", 3, Fg);
        l.Arm(2, "voiture", 3, Fg);                                               // the same tile again: stays armed
        Assert.True(l.Matches(2, "voiture", 3, Fg));
    }

    [Fact]
    public void Cancel_Reports_Whether_Something_Was_Pending()
    {
        var l = new ListenFirst();
        Assert.False(l.Cancel());
        l.Press(0, "maison", 7, Fg);
        Assert.True(l.Cancel());
        Assert.False(l.Armed);
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(0, "maison", 7, Fg));
    }

    [Fact]
    public void A_Rebuilt_Bar_Keeps_The_Ring_Only_On_The_Same_Word_At_The_Same_Index()
    {
        var l = new ListenFirst();
        l.Press(1, "mairie", 7, Fg);
        // a picture arrived, a field-probe answer: same words → kept, version re-stamped (a read's churn never drops it)
        Assert.Equal(1, l.KeepAfterRebuild(new[] { "maison", "mairie" }, 8, 9, Fg));
        Assert.Equal(ListenFirst.Outcome.Insert, l.Press(1, "mairie", 9, Fg));

        l.Press(1, "mairie", 7, Fg);
        Assert.Null(l.KeepAfterRebuild(new[] { "mairie", "maison" }, 8, 7, Fg));   // the word moved to another index
        Assert.False(l.Armed);

        l.Press(1, "mairie", 7, Fg);
        Assert.Null(l.KeepAfterRebuild(new[] { "maison", "mairie" }, 1, 7, Fg));   // trimmed away (fewer tiles fit)
        l.Press(1, "mairie", 7, Fg);
        Assert.Null(l.KeepAfterRebuild(new[] { "maison" }, 8, 7, Fg));             // fewer words
        l.Press(1, "mairie", 7, Fg);
        Assert.Null(l.KeepAfterRebuild(new[] { "maison", "mairie" }, 8, 7, Other)); // another window
        Assert.Null(new ListenFirst().KeepAfterRebuild(new[] { "maison" }, 8, 7, Fg));
    }

    [Theory]
    [InlineData("maison", "maison", false)]   // owner answer 5: not said again
    [InlineData("Maison", "maison", false)]   // case is not heard
    [InlineData("les amis", "amis", true)]    // the liaison group differs: said
    [InlineData("l'école", "école", true)]    // an elision group (owner, review of bcf6442): said
    [InlineData("peut-être", "être", true)]   // a compound group
    [InlineData("maison", null, true)]        // not a confirmation: said as before
    public void Say_At_Insert(string said, string? heard, bool say) => Assert.Equal(say, ListenFirst.SayAtInsert(said, heard));

    // ------------------------------------------------------------------ click modes (owner addendum 2026-11-25)
    [Theory]
    // mode, band, second click of a double → act (the word under the click was the one heard there)
    [InlineData((int)ClickMode.Insert, false, false, (int)ClickAct.Insert)]
    [InlineData((int)ClickMode.Insert, false, true, (int)ClickAct.Ignore)]         // the 2nd click reached the rebuilt bar (0.8.3)
    [InlineData((int)ClickMode.ListenFirst, false, false, (int)ClickAct.Press)]
    [InlineData((int)ClickMode.ListenFirst, false, true, (int)ClickAct.Ignore)]    // owner answer 7: a double click = one press
    [InlineData((int)ClickMode.ListenDouble, false, false, (int)ClickAct.Listen)]  // mode 3: a click speaks, nothing armed
    [InlineData((int)ClickMode.ListenDouble, false, true, (int)ClickAct.Insert)]   // mode 3: the double click inserts
    // owner answer 4 kept: with the band, a click on the word inserts at once whatever the click mode
    [InlineData((int)ClickMode.Insert, true, false, (int)ClickAct.Insert)]
    [InlineData((int)ClickMode.Insert, true, true, (int)ClickAct.Ignore)]
    [InlineData((int)ClickMode.ListenFirst, true, false, (int)ClickAct.Insert)]
    [InlineData((int)ClickMode.ListenFirst, true, true, (int)ClickAct.Ignore)]
    [InlineData((int)ClickMode.ListenDouble, true, false, (int)ClickAct.Insert)]
    [InlineData((int)ClickMode.ListenDouble, true, true, (int)ClickAct.Ignore)]
    public void Click_Action_Every_Mode_And_Band(int mode, bool band, bool second, int act) =>
        Assert.Equal((ClickAct)act, ListenFirst.ClickAction((ClickMode)mode, band, second, heardHere: true));

    /// <summary>Review of 238c58b: in mode 3 the second click of a double click inserts only the word just heard on that tile;
    /// a word not heard there (bar rebuilt under the click, an adjacent tile inside the double-click rectangle, click →
    /// right-click → click) is listened to — never a blind insert. The other modes do not depend on it.</summary>
    [Theory]
    [InlineData((int)ClickMode.ListenDouble, false, true, (int)ClickAct.Listen)]   // the double click, word not heard there
    [InlineData((int)ClickMode.ListenDouble, false, false, (int)ClickAct.Listen)]
    [InlineData((int)ClickMode.ListenFirst, false, true, (int)ClickAct.Ignore)]
    [InlineData((int)ClickMode.ListenFirst, false, false, (int)ClickAct.Press)]
    [InlineData((int)ClickMode.Insert, false, false, (int)ClickAct.Insert)]
    [InlineData((int)ClickMode.ListenDouble, true, false, (int)ClickAct.Insert)]   // band: the click inserts whatever was heard
    [InlineData((int)ClickMode.ListenDouble, true, true, (int)ClickAct.Ignore)]
    public void Mode_3_Second_Click_Inserts_Only_The_Word_Heard_There(int mode, bool band, bool second, int act) =>
        Assert.Equal((ClickAct)act, ListenFirst.ClickAction((ClickMode)mode, band, second, heardHere: false));

    [Fact]
    public void Mode_3_Double_Click_After_A_Rebuild_Or_On_Another_Tile()
    {
        var l = new ListenFirst();
        l.NoteHeard(1, "mairie", 7, Fg);                                            // the first click spoke tile 2
        // a picture arrived: bar rebuilt, same word there, no ring → still heard (re-stamped), the double click confirms
        Assert.Null(l.KeepAfterRebuild(new[] { "maison", "mairie" }, 8, 9, Fg));
        Assert.Equal(ClickAct.Insert, ListenFirst.ClickAction(ClickMode.ListenDouble, false, true, l.WasHeard(1, "mairie", 9, Fg)));
        // the second click landed on the adjacent tile (inside the double-click rectangle): not heard there → listens
        Assert.Equal(ClickAct.Listen, ListenFirst.ClickAction(ClickMode.ListenDouble, false, true, l.WasHeard(0, "maison", 9, Fg)));
        // the bar was rebuilt with another word under the click → forgotten → listens
        l.KeepAfterRebuild(new[] { "maison", "maire" }, 8, 10, Fg);
        Assert.Equal(ClickAct.Listen, ListenFirst.ClickAction(ClickMode.ListenDouble, false, true, l.WasHeard(1, "maire", 10, Fg)));
    }

    /// <summary>Review of 238c58b, the heard word's life cycle: kept with the ring on the same rule, independent of whether a
    /// ring existed; forgotten by <see cref="ListenFirst.Cancel"/> (a key, a jump, a hidden bar, an insertion).</summary>
    [Fact]
    public void Last_Heard_Life_Cycle()
    {
        var l = new ListenFirst();
        l.Arm(2, "voiture", 3, Fg); l.NoteHeard(2, "voiture", 3, Fg);            // Maj+F3 with a ring
        Assert.Equal(2, l.KeepAfterRebuild(new[] { "a", "b", "voiture" }, 8, 4, Fg));
        Assert.True(l.WasHeard(2, "voiture", 4, Fg));
        Assert.Null(l.KeepAfterRebuild(new[] { "a", "b", "voitures" }, 8, 5, Fg));   // another word there: ring and word dropped
        Assert.False(l.Armed);
        Assert.Null(l.LastHeard);
        l.NoteHeard(0, "maison", 5, Fg);                                          // mode 3 click: no ring
        Assert.Null(l.KeepAfterRebuild(new[] { "maison" }, 8, 6, Fg));            // nothing to ring …
        Assert.Equal(new ListenFirst.Pending(0, "maison", 6, Fg), l.LastHeard);    // … but the word is kept, re-stamped
        Assert.Null(l.KeepAfterRebuild(new[] { "maison" }, 8, 7, Other));         // another window: forgotten
        Assert.Null(l.LastHeard);
        l.NoteHeard(0, "maison", 7, Fg);
        Assert.False(l.Cancel());                                                 // typing: nothing was ringed …
        Assert.Null(l.LastHeard);                                                 // … yet the word is forgotten
    }

    // ------------------------------------------------------------------ UI Automation Invoke (owner 2026-11-25)
    /// <summary>An Invoke follows « Touches F1–F8 », whatever « Clic sur un mot », the speaker band, the F-keys being off
    /// or the ≈ list (none of them is an input of the decision: every combination gives the keys' answer), and is never
    /// ignored as the second click of a double click.</summary>
    [Theory]
    [InlineData(true, (int)ClickAct.Press)]
    [InlineData(false, (int)ClickAct.Insert)]
    public void Invoke_Follows_The_Keys_Setting_Not_The_Click_Mode(bool keysListenFirst, int expected)
    {
        Assert.Equal((ClickAct)expected, ListenFirst.InvokeAction(keysListenFirst));
        Assert.NotEqual(ClickAct.Ignore, ListenFirst.InvokeAction(keysListenFirst));
    }

    /// <summary>Where the mouse and an Invoke part ways (keys in the default « 1er appui »): the same tile, the same settings
    /// — the mouse decision follows the click mode and the band, the Invoke stays Press.</summary>
    [Theory]
    [InlineData((int)ClickMode.ListenDouble, false, (int)ClickAct.Listen)]   // default mode 3: a click only listens
    [InlineData((int)ClickMode.ListenFirst, true, (int)ClickAct.Insert)]     // band: a click on the word inserts at once
    [InlineData((int)ClickMode.Insert, false, (int)ClickAct.Insert)]
    [InlineData((int)ClickMode.ListenFirst, false, (int)ClickAct.Press)]     // the one case where both agree
    public void Invoke_Is_Not_The_Mouse_Click(int mode, bool band, int mouse)
    {
        var click = ListenFirst.ClickAction((ClickMode)mode, band, secondOfDouble: false, heardHere: false);
        Assert.Equal((ClickAct)mouse, click);
        Assert.Equal(click == ClickAct.Press, ListenFirst.InvokeAction(keysListenFirst: true) == click);
    }

    /// <summary>Default « 1er appui : écouter »: two Invokes on the same tile (index, word, version, window unchanged) —
    /// listen then insert; the F-keys off do not matter (an Invoke arms through <see cref="ListenFirst.Press"/>, not through
    /// <see cref="ListenFirst.Rings"/>).</summary>
    [Fact]
    public void Two_Invokes_On_The_Same_Tile_Listen_Then_Insert()
    {
        var l = new ListenFirst();
        Assert.Equal(ClickAct.Press, ListenFirst.InvokeAction(true));
        Assert.False(ListenFirst.Rings(ClickMode.ListenDouble, false, keysListenFirst: false));   // keys off: other gestures do not ring …
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(0, "maison", 7, Fg));                   // … the 1st Invoke still arms
        Assert.True(l.Armed);
        Assert.Equal(ListenFirst.Outcome.Insert, l.Press(0, "maison", 7, Fg));
        Assert.False(l.Armed);
    }

    /// <summary>The identity rules of the F-key confirm apply to an Invoke: another word, version or window → a new first
    /// Invoke (it listens), never a blind insert — also when the bar became the ≈ list (another word at that index).</summary>
    [Theory]
    [InlineData(1, "maison", 7, false)]    // another tile
    [InlineData(0, "mèson", 7, false)]     // the ≈ list replaced the bar: another word under tile 1
    [InlineData(0, "maison", 8, false)]    // typing / a jump moved the tracker
    [InlineData(0, "maison", 7, true)]     // another window in front
    public void A_Second_Invoke_With_A_Changed_Identity_Listens(int index, string word, long version, bool otherWindow)
    {
        var l = new ListenFirst();
        l.Press(0, "maison", 7, Fg);
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(index, word, version, otherWindow ? Other : Fg));
        Assert.Equal(ListenFirst.Outcome.Insert, l.Press(index, word, version, otherWindow ? Other : Fg));   // then a 2nd one inserts
    }

    /// <summary>≈ list: a rebuild into the ≈ list with another word at the ringed index drops the ring (the controller also
    /// cancels on F9 / the marker); an Invoke on a ≈ tile then follows the same two steps.</summary>
    [Fact]
    public void Invoke_On_The_Sound_Alike_List_Starts_Again()
    {
        var l = new ListenFirst();
        l.Press(0, "maison", 7, Fg);
        Assert.Null(l.KeepAfterRebuild(new[] { "mèson", "maisons" }, 8, 8, Fg));
        Assert.False(l.Armed);
        Assert.Equal(ListenFirst.Outcome.Listen, l.Press(1, "maisons", 8, Fg));
        Assert.Equal(ListenFirst.Outcome.Insert, l.Press(1, "maisons", 8, Fg));
    }

    [Theory]
    [InlineData((int)ClickMode.Insert, false, false)]
    [InlineData((int)ClickMode.ListenFirst, false, false)]
    [InlineData((int)ClickMode.ListenDouble, false, true)]   // mode 3: a right-click (press-and-hold) inserts
    [InlineData((int)ClickMode.Insert, true, false)]
    [InlineData((int)ClickMode.ListenFirst, true, false)]
    [InlineData((int)ClickMode.ListenDouble, true, false)]   // with the band the click mode does not apply: it listens
    public void Right_Click_Inserts_Only_In_Mode_3(int mode, bool band, bool inserts) =>
        Assert.Equal(inserts, ListenFirst.RightClickInserts((ClickMode)mode, band));

    /// <summary>A listen gesture rings only when a confirming press exists; mode 3 never rings by itself (addendum 5: a click
    /// then F3 is not a confirmation, F3 follows the keyboard setting — which may ring).</summary>
    [Theory]
    // mode, band, keys listen first (and keys on) → rings
    [InlineData((int)ClickMode.Insert, false, false, false)]
    [InlineData((int)ClickMode.Insert, false, true, true)]
    [InlineData((int)ClickMode.ListenFirst, false, false, true)]
    [InlineData((int)ClickMode.ListenFirst, true, false, false)]   // band: the click inserts at once, nothing to confirm
    [InlineData((int)ClickMode.ListenFirst, true, true, true)]
    [InlineData((int)ClickMode.ListenDouble, false, false, false)]
    [InlineData((int)ClickMode.ListenDouble, false, true, true)]
    [InlineData((int)ClickMode.ListenDouble, true, false, false)]
    [InlineData((int)ClickMode.ListenDouble, true, true, true)]
    public void Rings_Only_With_A_Confirming_Press(int mode, bool band, bool keysListenFirst, bool rings) =>
        Assert.Equal(rings, ListenFirst.Rings((ClickMode)mode, band, keysListenFirst));

    [Fact]
    public void Mode_3_Click_Is_Heard_Without_A_Ring_And_The_Double_Click_Confirms_It()
    {
        var l = new ListenFirst();
        l.NoteHeard(1, "mairie", 7, Fg);                 // the first click spoke it (no Arm: no ring)
        Assert.False(l.Armed);
        Assert.True(l.WasHeard(1, "mairie", 7, Fg));     // the double click / right-click: not said again
        Assert.False(l.WasHeard(0, "mairie", 7, Fg));    // another tile
        Assert.False(l.WasHeard(1, "mairie", 8, Fg));    // typed meanwhile
        Assert.False(l.WasHeard(1, "mairie", 7, Other)); // another window
        // a rebuilt bar with the same word there re-stamps it; another word there forgets it
        Assert.Null(l.KeepAfterRebuild(new[] { "maison", "mairie" }, 8, 9, Fg));   // nothing to ring
        Assert.True(l.WasHeard(1, "mairie", 9, Fg));
        l.KeepAfterRebuild(new[] { "maison", "maire" }, 8, 9, Fg);
        Assert.False(l.WasHeard(1, "mairie", 9, Fg));
        l.NoteHeard(1, "mairie", 7, Fg);
        l.Cancel();                                       // a key, a jump, a hidden bar
        Assert.False(l.WasHeard(1, "mairie", 7, Fg));
    }

    [Fact]
    public void A_Ringed_Tile_Counts_As_Heard()
    {
        var l = new ListenFirst();
        l.Arm(2, "voiture", 3, Fg);   // right-click with « Touches F1–F8 : 1er appui : écouter »
        Assert.True(l.WasHeard(2, "voiture", 3, Fg));
    }

    /// <summary>The whole matrix in one place: what a press does for every click mode × keyboard mode × band. A press here is
    /// a fresh F1 (keys on, word under it) and a single click; the band does not change the F-keys.</summary>
    [Theory]
    [InlineData((int)ClickMode.Insert, false)] [InlineData((int)ClickMode.Insert, true)]
    [InlineData((int)ClickMode.ListenFirst, false)] [InlineData((int)ClickMode.ListenFirst, true)]
    [InlineData((int)ClickMode.ListenDouble, false)] [InlineData((int)ClickMode.ListenDouble, true)]
    public void Click_And_Keyboard_Modes_Are_Independent(int mode, bool keysListenFirst)
    {
        foreach (var band in new[] { false, true })
        {
            var key = KeyboardPolicy.FKeyAction(0, false, false, false, true, false, keysListenFirst, 3, first: null);
            Assert.Equal(keysListenFirst ? FKeyAct.Press : FKeyAct.Insert, key);   // whatever the click mode and the band
            var click = ListenFirst.ClickAction((ClickMode)mode, band, secondOfDouble: false, heardHere: false);
            var expected = band ? ClickAct.Insert : (ClickMode)mode switch
            {
                ClickMode.ListenFirst => ClickAct.Press, ClickMode.ListenDouble => ClickAct.Listen, _ => ClickAct.Insert,
            };
            Assert.Equal(expected, click);   // whatever the keyboard mode
        }
    }

    // ------------------------------------------------------------------ the hook's decision
    private const int Fresh = -1;   // no held record: a new press

    [Theory]
    // index, ctrl, alt, shift, keysOn, listenShift, keysListenFirst, count, first action of the held key → act
    [InlineData(0, false, false, false, true, false, false, 3, Fresh, (int)FKeyAct.Insert)]                // « insérer tout de suite »
    [InlineData(0, false, false, false, true, false, false, 3, (int)FKeyAct.Insert, (int)FKeyAct.Insert)]  // … auto-repeat inserts again (as before)
    [InlineData(0, false, false, false, true, false, true, 3, Fresh, (int)FKeyAct.Press)]                  // 1st press listens (default)
    [InlineData(0, false, false, false, true, false, true, 3, (int)FKeyAct.Press, (int)FKeyAct.Swallow)]   // … held: one press only
    // review of bcf6442: a held key whose down was handled never auto-repeats into the app, even when the bar changed
    [InlineData(3, false, false, false, true, false, true, 3, (int)FKeyAct.Press, (int)FKeyAct.Swallow)]   // no word under it any more
    [InlineData(0, false, false, false, true, false, true, 0, (int)FKeyAct.Press, (int)FKeyAct.Swallow)]   // bar hidden (after a correction)
    [InlineData(0, false, false, false, true, false, false, 0, (int)FKeyAct.Insert, (int)FKeyAct.Swallow)] // same in « insérer tout de suite »
    [InlineData(0, true, false, false, true, false, false, 3, (int)FKeyAct.Insert, (int)FKeyAct.Swallow)]  // Ctrl pressed while F1 held
    [InlineData(0, false, false, false, true, false, true, 3, (int)FKeyAct.Insert, (int)FKeyAct.Insert)]   // setting changed while held: the first action repeats
    [InlineData(2, false, false, true, true, true, true, 3, Fresh, (int)FKeyAct.Listen)]                   // A: Maj+F3
    [InlineData(2, false, false, true, true, true, false, 3, Fresh, (int)FKeyAct.Listen)]                  // A with « insérer »: speaks only
    [InlineData(2, false, false, true, true, true, true, 3, (int)FKeyAct.Listen, (int)FKeyAct.Swallow)]    // A held
    [InlineData(2, false, false, false, true, true, false, 3, (int)FKeyAct.Listen, (int)FKeyAct.Swallow)]  // A held, Shift released: never an insert
    [InlineData(2, false, false, true, true, false, true, 3, Fresh, (int)FKeyAct.Pass)]                    // Maj+F3 without A: to the app (Word)
    [InlineData(2, false, false, false, true, false, false, 3, (int)FKeyAct.Pass, (int)FKeyAct.Pass)]      // … Shift released while held: still passed
    [InlineData(2, false, false, true, false, true, true, 3, Fresh, (int)FKeyAct.Pass)]                    // A needs the F-keys
    [InlineData(0, true, false, false, true, true, true, 3, Fresh, (int)FKeyAct.Pass)]                     // Ctrl+F1
    [InlineData(0, false, false, false, true, true, false, 3, (int)FKeyAct.Pass, (int)FKeyAct.Pass)]       // … Ctrl released while held: still passed, never an insert
    [InlineData(0, false, true, false, true, true, true, 3, Fresh, (int)FKeyAct.Pass)]                     // Alt+F1
    [InlineData(3, false, false, false, true, true, true, 3, Fresh, (int)FKeyAct.Pass)]                    // no word under F4
    [InlineData(3, false, false, false, true, true, true, 4, (int)FKeyAct.Pass, (int)FKeyAct.Pass)]        // … a word appears while held: still passed
    [InlineData(3, false, false, true, true, true, true, 3, Fresh, (int)FKeyAct.Pass)]                     // Maj+F4, no word
    [InlineData(0, false, false, false, true, true, true, 0, Fresh, (int)FKeyAct.Pass)]                    // bar hidden / reading mode
    [InlineData(0, false, false, false, false, false, true, 3, Fresh, (int)FKeyAct.Pass)]                  // keys off
    public void FKey_Action_Matrix(int i, bool ctrl, bool alt, bool shift, bool keysOn, bool listenShift, bool keysListenFirst, int count,
                                   int first, int act) =>
        Assert.Equal((FKeyAct)act, KeyboardPolicy.FKeyAction(i, ctrl, alt, shift, keysOn, listenShift, keysListenFirst, count,
                                                             first == Fresh ? null : (FKeyAct)first));

    /// <summary>Third review of 238c58b: the per-key record of a held F-key. A down the hook swallowed is decided by its record
    /// alone — the key state of a swallowed key is probably never set, so <c>keyWasDown</c> is false at its repeats too and
    /// must not make them new presses; only a passed down uses the key state (Windows updates it).</summary>
    [Fact]
    public void Held_First_Swallowed_By_Record_Passed_By_Key_State()
    {
        foreach (var swallowed in new[] { FKeyAct.Press, FKeyAct.Insert, FKeyAct.Listen, FKeyAct.Swallow })
        {
            Assert.Equal(swallowed, KeyboardPolicy.HeldFirst(true, swallowed, keyWasDown: false));   // a repeat, state not set
            Assert.Equal(swallowed, KeyboardPolicy.HeldFirst(true, swallowed, keyWasDown: true));
            Assert.Null(KeyboardPolicy.HeldFirst(false, swallowed, keyWasDown: true));              // no record: a new press
            Assert.True(KeyboardPolicy.SwallowFKeyUp(swallowed));
        }
        Assert.Equal(FKeyAct.Pass, KeyboardPolicy.HeldFirst(true, FKeyAct.Pass, keyWasDown: true));     // passed, still held
        Assert.Null(KeyboardPolicy.HeldFirst(true, FKeyAct.Pass, keyWasDown: false));                   // passed, released unseen
        Assert.False(KeyboardPolicy.SwallowFKeyUp(FKeyAct.Pass));                                       // its key-up goes too
    }

    /// <summary>A simulated hook over a key timeline (<see cref="FKeyHolds"/>, the controller's record). The swallowed key's
    /// state is never set (<c>wasDown</c> false at every down), as the round-3 review expects of Windows.</summary>
    private static Func<bool, FKeyAct> Hook(FKeyHolds h, Func<long> gen, bool keysListenFirst, int count = 3) => wasDown =>
    {
        var first = h.Held(1, wasDown, gen());
        var act = KeyboardPolicy.FKeyAction(1, false, false, false, true, false, keysListenFirst, count, first);
        h.Record(1, first ?? act);
        return act;
    };

    /// <summary>Held F2 in the default « 1er appui : écouter, 2e : insérer », with the usual repeat and under Filter Keys
    /// (« Touches filtres », a 2 s repeat delay: no timer is involved, the record decides): one listen, the repeats
    /// swallowed — never an insert, never to the app (bcf6442); in « insérer tout de suite » the repeats insert again.</summary>
    [Theory]
    [InlineData(true, (int)FKeyAct.Press, (int)FKeyAct.Swallow)]
    [InlineData(false, (int)FKeyAct.Insert, (int)FKeyAct.Insert)]
    public void A_Held_Swallowed_Key_Repeats_Only_Its_First_Action(bool keysListenFirst, int firstAct, int repeatAct)
    {
        var h = new FKeyHolds();
        var down = Hook(h, () => 0, keysListenFirst);
        Assert.Equal((FKeyAct)firstAct, down(false));     // t = 0: F2 pressed
        Assert.Equal((FKeyAct)repeatAct, down(false));    // t = 2000 ms (Filter Keys) or 500 ms: the first repeat
        Assert.Equal((FKeyAct)repeatAct, down(false));    // further repeats
        Assert.True(h.Up(1));                             // released: its key-up is swallowed
        Assert.Equal((FKeyAct)firstAct, down(false));     // pressed again: a new press
        // after a correction the bar is hidden: the held key's repeats are still swallowed, never the app's F1 help
        var h2 = new FKeyHolds();
        Assert.Equal(FKeyAct.Press, Hook(h2, () => 0, true)(false));
        Assert.Equal(FKeyAct.Swallow, Hook(h2, () => 0, true, count: 0)(false));
    }

    /// <summary>A passed key (Ctrl+F2, or no word under it): Windows updates its state, so its repeats read « already down »
    /// and stay passed — even once Ctrl is released or a word appears — and its key-up passes; once released (state up),
    /// the next down is a new press.</summary>
    [Fact]
    public void A_Held_Passed_Key_Repeats_Pass_By_Key_State()
    {
        var h = new FKeyHolds();
        var down = Hook(h, () => 0, true, count: 0);      // no word under F2: passed
        Assert.Equal(FKeyAct.Pass, down(false));          // t = 0
        var withWord = Hook(h, () => 0, true, count: 3);  // a word appears under the held key
        Assert.Equal(FKeyAct.Pass, withWord(true));       // the repeat: key state down → still passed, never a listen
        Assert.Equal(FKeyAct.Pass, withWord(true));
        Assert.False(h.Up(1));                            // its key-up passes
        Assert.Equal(FKeyAct.Press, withWord(false));     // pressed again with a word: a new press
        // a key-up of a passed key not seen by the hook: the state reads up at the next down → a new press
        h.Up(1);
        Assert.Equal(FKeyAct.Pass, down(false));
        Assert.Equal(FKeyAct.Press, withWord(false));
    }

    /// <summary>Where key-ups go missing the records are dropped: a session lock / unlock (generation bump), a foreground
    /// change (<see cref="FKeyHolds.Clear"/>), a down passed untouched (<see cref="FKeyHolds.Forget"/>) — the next down is a
    /// new press and, with no bar, goes to the application.</summary>
    [Fact]
    public void Lost_Key_Ups_Reset_The_Record()
    {
        long gen = 0;
        var h = new FKeyHolds();
        var down = Hook(h, () => gen, true, count: 0);    // no bar
        Assert.Equal(FKeyAct.Pass, down(false));
        h.Record(1, FKeyAct.Press);                       // a handled F2, its key-up then lost to Win+L
        gen++;                                            // session lock / unlock, Pause / Reprendre
        Assert.Equal(FKeyAct.Pass, down(false));          // a new press: to the app, not swallowed
        Assert.False(h.Up(1));

        h.Record(1, FKeyAct.Press);
        h.Clear();                                        // foreground change
        Assert.Equal(FKeyAct.Pass, down(false));
        h.Record(1, FKeyAct.Press);
        h.Forget(1);                                      // passed untouched in our own window
        Assert.False(h.Up(1));
    }

    [Theory]
    [InlineData(0x10u, true)] [InlineData(0xA1u, true)] [InlineData(0x11u, true)] [InlineData(0x12u, true)] [InlineData(0x14u, true)]
    [InlineData(0x5Bu, true)] [InlineData(0x90u, true)] [InlineData(0x41u, false)] [InlineData(0x1Bu, false)] [InlineData(0x78u, false)]
    [InlineData(0x08u, false)] [InlineData(0x25u, false)]
    public void Modifiers_Alone_Do_Not_Cancel(uint vk, bool modifier) => Assert.Equal(modifier, KeyboardPolicy.IsModifierOrLock(vk));

    // ------------------------------------------------------------------ settings
    [Fact]
    public void Defaults_Click_ListenDouble_Keys_ListenFirst_A_And_D_Off()
    {
        var s = new AppSettings();
        Assert.Equal(ClickMode.ListenDouble, s.Click);   // owner, « Default changed »
        Assert.True(s.KeysListenFirst);
        Assert.False(s.ListenShiftKeys);
        Assert.False(s.ListenButton);
        var w = s.ToJson()["words"]!;
        Assert.Equal("listenDouble", (string?)w["clickMode"]);
        Assert.Equal(true, (bool?)w["keysListenFirst"]);
        Assert.Equal(false, (bool?)w["listenShiftKeys"]);
        Assert.Equal(false, (bool?)w["listenButton"]);
        Assert.Null(w["listenFirst"]);                     // the never-released key is gone
    }

    [Theory]
    [InlineData((int)ClickMode.Insert, "insert")]
    [InlineData((int)ClickMode.ListenFirst, "listenFirst")]
    [InlineData((int)ClickMode.ListenDouble, "listenDouble")]
    public void Click_Mode_Round_Trip(int mode, string json)
    {
        var s = new AppSettings() with { Click = (ClickMode)mode };
        Assert.Equal(json, (string?)s.ToJson()["words"]!["clickMode"]);
        Assert.Equal(s, AppSettings.FromJson(s.ToJson()));
        // an admin writing the machine file by hand: case does not matter
        Assert.Equal((ClickMode)mode, AppSettings.FromJson(J($@"{{""words"":{{""clickMode"":""{json.ToUpperInvariant()}""}}}}")).Click);
    }

    [Fact]
    public void Round_Trip_And_Missing_Means_Default()
    {
        var all = new AppSettings() with { Click = ClickMode.Insert, KeysListenFirst = false, ListenShiftKeys = true, ListenButton = true };
        Assert.Equal(all, AppSettings.FromJson(all.ToJson()));
        // a user file written before 2026-11-25 has none of the keys: mode 3 and 1st press listens (owner), A and D off
        var old = AppSettings.FromJson(J(@"{""words"":{""count"":6,""keys"":""F1-F8""}}"));
        Assert.Equal(ClickMode.ListenDouble, old.Click);
        Assert.True(old.KeysListenFirst);
        Assert.False(old.ListenShiftKeys || old.ListenButton);
        // the never-released words.listenFirst is ignored (no migration: it never left the developer's machine)
        var dev = AppSettings.FromJson(J(@"{""words"":{""listenFirst"":false}}"));
        Assert.Equal(ClickMode.ListenDouble, dev.Click);
        Assert.True(dev.KeysListenFirst);
        // an explicit value is kept; a mistyped value falls back to the default of that field only
        Assert.False(AppSettings.FromJson(J(@"{""words"":{""keysListenFirst"":false}}")).KeysListenFirst);
        Assert.Equal(ClickMode.ListenFirst, AppSettings.FromJson(J(@"{""words"":{""clickMode"":""listenFirst""}}")).Click);
        Assert.True(AppSettings.FromJson(J(@"{""words"":{""keysListenFirst"":""no""}}")).KeysListenFirst);
        var bad = AppSettings.FromJson(J(@"{""words"":{""clickMode"":""triple"",""count"":5}}"));
        Assert.Equal(ClickMode.ListenDouble, bad.Click);
        Assert.Equal(5, bad.WordCount);
        Assert.Equal(ClickMode.ListenDouble, (new AppSettings() with { Click = (ClickMode)42 }).Validated().Click);
        Assert.Equal(1, AppSettings.Schema);   // no schema bump
    }

    private static JsonObject J(string s) => (JsonObject)JsonNode.Parse(s)!;

    [Fact]
    public void Class_Gives_Them_And_The_Machine_Lock_Wins()
    {
        // « Classe » sets mode C, keys at once and D for a pupil who never set them; a pupil's own value stays
        var (eff, _) = SettingsLayering.Merge(MachineLayer.None,
            J(@"{""words"":{""clickMode"":""listenFirst"",""keysListenFirst"":false,""listenButton"":true}}"), new JsonObject());
        Assert.Equal(ClickMode.ListenFirst, eff.Click);
        Assert.False(eff.KeysListenFirst);
        Assert.True(eff.ListenButton);
        var (own, _) = SettingsLayering.Merge(MachineLayer.None, J(@"{""words"":{""clickMode"":""insert"",""keysListenFirst"":false}}"),
                                              J(@"{""words"":{""clickMode"":""listenDouble"",""keysListenFirst"":true}}"));
        Assert.Equal(ClickMode.ListenDouble, own.Click);
        Assert.True(own.KeysListenFirst);
        // the machine file's lock wins over the pupil, on both paths
        var m = MachineLayer.Parse(@"{""schema"":1,""locked"":{""words"":{""clickMode"":""insert"",""keysListenFirst"":false,""listenShiftKeys"":true}}}", out var err)!;
        Assert.Null(err);
        var (locked, paths) = SettingsLayering.Merge(m, J(@"{""words"":{""clickMode"":""listenDouble"",""keysListenFirst"":true,""listenShiftKeys"":false}}"));
        Assert.Equal(ClickMode.Insert, locked.Click);
        Assert.False(locked.KeysListenFirst);
        Assert.True(locked.ListenShiftKeys);
        Assert.Contains("words.clickMode", paths);
        Assert.Contains("words.keysListenFirst", paths);
        Assert.Contains("words.listenShiftKeys", paths);
        Assert.DoesNotContain("words.listenButton", paths);
    }

    // ------------------------------------------------------------------ speech of a confirmation (addendum 4)
    [Fact]
    public void An_After_Item_Never_Interrupts_And_Waits_Behind_The_Heard_Word()
    {
        var q = new SpeechQueue();
        Assert.True(q.Enqueue(new SpeechItem("amis", SpeechKind.Manual)));                     // the click: interrupts
        Assert.Equal("amis", q.Next()!.Text);                                                    // being spoken
        Assert.False(q.Enqueue(new SpeechItem("les amis", SpeechKind.Inserted, After: true)));  // the double click
        Assert.Single(q.Pending);
        Assert.Equal("les amis", q.Pending[0].Text);
        Assert.True(q.Enqueue(new SpeechItem("maison", SpeechKind.Inserted)));                  // a plain insertion still interrupts
        // a sentence read does not drop it (it is not a word echo)
        var q2 = new SpeechQueue();
        q2.Enqueue(new SpeechItem("l'école", SpeechKind.Inserted, After: true));
        q2.Enqueue(new SpeechItem("Une phrase.", SpeechKind.Sentence));
        Assert.Equal(new[] { "l'école", "Une phrase." }, q2.Pending.Select(p => p.Text));
    }

    // ------------------------------------------------------------------ tooltips and UIA help (no product named)
    [Theory]
    [InlineData((int)ClickMode.Insert, false, " — clic : insérer, clic droit : écouter")]
    [InlineData((int)ClickMode.ListenFirst, false, " — 1er clic : écouter, 2e clic : insérer, clic droit : écouter")]
    [InlineData((int)ClickMode.ListenDouble, false, " — clic : écouter, double-clic ou clic droit : insérer")]
    [InlineData((int)ClickMode.ListenDouble, true, " — clic : insérer, clic droit : écouter")]
    [InlineData((int)ClickMode.ListenFirst, true, " — clic : insérer, clic droit : écouter")]
    public void Tooltip_Follows_The_Click_Mode(int mode, bool band, string tip) =>
        Assert.Equal(tip, Strings.TipActionsFor((ClickMode)mode, band));

    [Fact]
    public void Tile_Help_Has_The_Key_Part_Then_The_Click_Part()
    {
        Assert.Equal("F3 : écouter, puis encore une fois : insérer ; clic : écouter ; double-clic ou clic droit : insérer ; " +
                     "activer : écouter, puis encore une fois : insérer",
                     Strings.TileHelp(3, true, ClickMode.ListenDouble, false));
        Assert.Equal("F1 : insérer ; clic : écouter, puis encore une fois : insérer ; clic droit : écouter ; activer : insérer",
                     Strings.TileHelp(1, false, ClickMode.ListenFirst, false));
        // F-keys off: the Invoke part still follows « Touches F1–F8 »
        Assert.Equal("Clic : insérer ; clic droit : écouter ; activer : écouter, puis encore une fois : insérer",
                     Strings.TileHelp(null, true, ClickMode.ListenDouble, true));
        Assert.Equal("Clic : écouter ; double-clic ou clic droit : insérer ; activer : insérer",
                     Strings.TileHelp(null, false, ClickMode.ListenDouble, false));
    }

    // ------------------------------------------------------------------ option D geometry
    [Fact]
    public void Band_Heights_S_M_L_XL()
    {
        Assert.Equal(24, BarMetrics.For(BarSize.S).ListenBandDip);
        Assert.Equal(26, BarMetrics.For(BarSize.M).ListenBandDip);
        Assert.Equal(30, BarMetrics.For(BarSize.L).ListenBandDip);
        Assert.Equal(36, BarMetrics.For(BarSize.XL).ListenBandDip);
    }

    [Theory]
    [InlineData((int)BarSize.S)] [InlineData((int)BarSize.M)] [InlineData((int)BarSize.L)] [InlineData((int)BarSize.XL)]
    public void The_Band_Adds_Height_Only(int size)
    {
        var m = BarMetrics.For((BarSize)size);
        foreach (var pictures in new[] { true, false })
            foreach (var keys in new[] { true, false })
            {
                Assert.Equal(m.TileHeight(pictures, keys), m.TileHeight(pictures, keys, band: false));   // D off: unchanged
                Assert.Equal(m.TileHeight(pictures, keys) + m.ListenBandDip, m.TileHeight(pictures, keys, band: true), 6);
            }
    }

    /// <summary>Owner answer 8: the taller bar at the caret never covers the line being typed — under the caret when it fits,
    /// else entirely above the caret's line (the iterations 1–2 rule: 32 px above the caret point), near the screen bottom too.</summary>
    [Theory]
    [InlineData(400)] [InlineData(700)] [InlineData(800)] [InlineData(900)] [InlineData(1000)] [InlineData(1030)]
    public void A_Taller_Bar_At_The_Caret_Never_Covers_The_Typed_Line(int caretY)
    {
        var wa = new RectI(0, 0, 1920, 1040);
        foreach (var dpiScale in new[] { 1.0, 1.5, 2.0 })
        {
            var m = BarMetrics.For(BarSize.XL);
            int h = (int)Math.Ceiling((m.TileHeight(true, true, band: true) + 12) * dpiScale);   // + the bar's frame
            var bar = new SizeI(800, h);
            var at = BarPlacement.Compute(BarPosition.Caret, new PointI(300, caretY), bar, wa, null, new[] { wa });
            bool below = at.Y >= caretY + 6;
            bool above = at.Y + bar.H <= caretY - 32;
            Assert.True(below || above, $"caret y={caretY} scale {dpiScale}: bar {at.Y}..{at.Y + bar.H}");
            Assert.True(at.Y >= wa.Top && at.Y + bar.H <= wa.Bottom);
        }
    }

    [Fact]
    public void Render_Switches_Take_Their_Value()
    {
        Assert.Empty(CommandLine.Positional(new[] { "--render-bar", "x.png", "--listening", "2", "--listen-button" }));
        Assert.Equal("2", CommandLine.Option(new[] { "--listening", "2" }, "--listening"));
    }
}
