using System;
using System.Collections.Generic;
using System.Linq;

namespace Predict.Keyboard;

/// <summary>What a selection read means for the reading mode.</summary>
internal enum SelectionAction
{
    /// <summary>A new selection: show its tiles (Ready, no autoplay).</summary>
    Show,
    /// <summary>The same selection as the one shown: keep the view and the reading state.</summary>
    Keep,
    /// <summary>No selection any more: back to predictions.</summary>
    Exit,
    /// <summary>Nothing to do (no selection and no reading mode; or the selection the user dismissed with Esc).</summary>
    Ignore,
}

/// <summary>
/// Selection capture policy (plan 07, pure). De-duplication on the trimmed text; a selection dismissed with Esc is not
/// shown again until it changes; the simulated Ctrl+C fallback (setting, off by default) only after a selection gesture
/// (drag, double click, Shift + navigation, Ctrl+A) in a window whose own read gave no selection, never in a password
/// field (or one that could not be checked), never while a modifier is held.
/// </summary>
internal static class SelectionPolicy
{
    /// <summary>De-duplication key: the trimmed text.</summary>
    public static string Key(string? text) => text?.Trim() ?? "";

    /// <param name="active">the reading mode is shown.</param>
    /// <param name="currentKey">key of the selection shown (when active).</param>
    /// <param name="dismissedKey">key of the selection the user dismissed with Esc in this window, or null.</param>
    /// <param name="selected">the selected text just read ("" or null: no selection).</param>
    public static SelectionAction Decide(bool active, string? currentKey, string? dismissedKey, string? selected)
    {
        var key = Key(selected);
        if (key.Length == 0) return active ? SelectionAction.Exit : SelectionAction.Ignore;
        if (active && key == currentKey) return SelectionAction.Keep;
        if (!active && key == dismissedKey) return SelectionAction.Ignore;
        return SelectionAction.Show;
    }

    /// <summary>May the simulated Ctrl+C be tried? (Also re-checked by <see cref="ClipboardCopy.Run"/> right before it.)</summary>
    public static bool MayUseClipboard(bool settingOn, bool gesture, bool passwordSuppressed, ReadStatus caretRead) =>
        settingOn && gesture && !passwordSuppressed && caretRead == ReadStatus.Unavailable;

    /// <summary>A selection longer than the reader's character limit: the text up to its last space / line break (never a
    /// cut word); unchanged when it has none.</summary>
    public static string CutAtWord(string s)
    {
        int i = s.Length - 1;
        while (i > 0 && !char.IsWhiteSpace(s[i])) i--;
        return i > 0 ? s.Substring(0, i) : s;
    }

    /// <summary>The window's own read (UIA / Win32) failed or found no text pattern: may the fallback help?</summary>
    public static bool WantsFallback(ReadStatus selectionRead) => selectionRead == ReadStatus.Unavailable;
}

/// <summary>Saved clipboard content (every format that can be restored).</summary>
internal sealed record ClipboardSnapshot(IReadOnlyList<(uint Format, byte[] Data)> Items);

/// <summary>The clipboard and the input as <see cref="ClipboardCopy"/> sees them (Win32 in the app, a fake in the tests).</summary>
internal interface IClipboardOps
{
    /// <summary>GetClipboardSequenceNumber.</summary>
    uint Sequence { get; }
    /// <summary>The formats on the clipboard now.</summary>
    IReadOnlyList<uint> Formats();
    /// <summary>No owner, or the owner answers a message within a short budget (it may have to render delayed data).</summary>
    bool OwnerResponsive();
    /// <summary>Copy every format's data; null when the clipboard cannot be opened.</summary>
    ClipboardSnapshot? Save();
    /// <summary>A physical modifier (Shift, Ctrl, Alt, Win) is held.</summary>
    bool ModifiersDown();
    /// <summary>The window the selection was read in is still in front (re-checked right before Ctrl+C).</summary>
    bool StillTarget();
    /// <summary>The clipboard's owner window belongs to the target window's process (its own copy): only then may the
    /// user's content be put back. Anything else (another application, a password manager's auto-clear, a delayed-rendering
    /// helper process) is left alone — the safe direction.</summary>
    bool OwnedByTarget();
    /// <summary>The clipboard has an owner window at all (an application that emptied it with no window owns nothing).</summary>
    bool HasOwner();
    /// <summary>The user pressed a copy / cut chord (Ctrl+C, Ctrl+X, Ctrl+Insert, Shift+Delete — physical keys, not ours)
    /// since the snapshot was taken (just before our Ctrl+C): the clipboard now holds what the user wanted, never restore over it.</summary>
    bool UserCopied();
    /// <summary>Send Ctrl+C to the focused window (our marker: the hook ignores it). False when blocked (UIPI).</summary>
    bool SendCopy();
    /// <summary>CF_UNICODETEXT, or null.</summary>
    string? ReadText();
    /// <summary>Put the snapshot back (an empty snapshot empties the clipboard). False when the clipboard could not be
    /// opened (another application holds it): <see cref="ClipboardCopy"/> retries.</summary>
    bool Restore(ClipboardSnapshot s);
    void Sleep(int ms);
    long NowMs { get; }
}

/// <summary>
/// The simulated Ctrl+C fallback (plan 07, setting « Copier la sélection par Ctrl+C », off by default): password check,
/// owner responsive, every format saved, Ctrl+C sent, wait ≤ <see cref="WaitMs"/> for the clipboard to change, read the
/// text, restore the saved content. Nothing is touched when the clipboard holds data that cannot be restored as it was
/// (metafiles, owner-drawn or GDI-private formats). Once the snapshot is taken the user's clipboard is always put back
/// when it changed — and only when the change is the target application's (clipboard owner in its process) and the user
/// made no copy / cut of their own meanwhile: a copy the user made, another application's, or a password manager clearing
/// the clipboard are never overwritten. The restore runs in a <c>finally</c> (an exception between Ctrl+C and the restore cannot skip it), is
/// retried while another application holds the clipboard open, and a copy that lands after <see cref="WaitMs"/> is
/// watched for up to <see cref="LateWatchMs"/> more and undone too (its text is not used).
/// </summary>
internal static class ClipboardCopy
{
    public const int WaitMs = 300, ModifierWaitMs = 300, LateWatchMs = 1500, RestoreTries = 8, RestoreRetryMs = 50, RecheckMs = 200;

    /// <summary>Environment.TickCount64 of the user's last physical copy / cut chord (set by the keyboard hook).</summary>
    public static long LastUserCopyTick;

    /// <summary>Worst duration of <see cref="Run"/> (modifier wait, owner check, copy wait, late watch, restore retries,
    /// re-check, small pauses): the selection worker's « stuck » limit is above it.</summary>
    public const int WorstMs = ModifierWaitMs + 100 + WaitMs + LateWatchMs + RestoreTries * RestoreRetryMs * 2 + RecheckMs + 500;

    /// <summary>A physical copy / cut chord (hook thread): Ctrl+C, Ctrl+X, Ctrl+Insert, Shift+Delete.</summary>
    public static bool IsCopyChord(uint vk, bool ctrl, bool shift, bool alt) =>
        !alt && (ctrl && (vk == 0x43 || vk == 0x58 || vk == 0x2D) || shift && !ctrl && vk == 0x2E);
    public const uint CF_BITMAP = 2, CF_METAFILEPICT = 3, CF_PALETTE = 9, CF_ENHMETAFILE = 14, CF_DIB = 8, CF_DIBV5 = 17,
                      CF_OWNERDISPLAY = 0x80, CF_DSPBITMAP = 0x82, CF_DSPMETAFILEPICT = 0x83, CF_DSPENHMETAFILE = 0x8E;

    /// <summary>A format whose data is not an HGLOBAL (GDI handles): not copied by <see cref="IClipboardOps.Save"/>.</summary>
    public static bool IsHandleFormat(uint f) =>
        f is CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or CF_ENHMETAFILE or CF_OWNERDISPLAY or CF_DSPBITMAP or CF_DSPMETAFILEPICT
            or CF_DSPENHMETAFILE || f is >= 0x300 and <= 0x3FF;

    /// <summary>Can the clipboard be put back exactly? Bitmaps and palettes are re-synthesised by Windows from a DIB; metafiles,
    /// owner-display and GDI-private formats cannot be restored.</summary>
    public static bool CanRestore(IReadOnlyList<uint> formats)
    {
        bool dib = formats.Contains(CF_DIB) || formats.Contains(CF_DIBV5);
        foreach (var f in formats)
        {
            if (!IsHandleFormat(f)) continue;
            if (f is CF_BITMAP or CF_PALETTE && dib) continue;
            return false;
        }
        return true;
    }

    /// <param name="probe">password check of the focused field, made right before (anything but NotPassword: no copy).</param>
    /// <returns>the copied text (null when nothing was copied) and what happened (for the log, never the text).</returns>
    public static (string? Text, string Outcome) Run(IClipboardOps ops, Func<ProbeStatus> probe)
    {
        var field = probe();
        if (field != ProbeStatus.NotPassword) return (null, $"field {field}: no copy");
        long t0 = ops.NowMs;
        while (ops.ModifiersDown())
        {
            if (ops.NowMs - t0 > ModifierWaitMs) return (null, "modifier held: no copy");
            ops.Sleep(10);
        }
        if (!CanRestore(ops.Formats())) return (null, "clipboard holds data that cannot be restored: no copy");
        if (!ops.OwnerResponsive()) return (null, "clipboard owner busy: no copy");
        var saved = ops.Save();
        if (saved is null) return (null, "clipboard busy: no copy");
        uint seq = ops.Sequence;
        string? text = null;
        string what = "";
        long t1 = ops.NowMs;
        try
        {
            if (!ops.StillTarget()) return (null, "window changed: no copy");
            if (!ops.SendCopy()) return (null, "Ctrl+C blocked");
            t1 = ops.NowMs;
            while (ops.Sequence == seq && ops.NowMs - t1 <= WaitMs) ops.Sleep(10);
            if (ops.Sequence != seq)
            {
                ops.Sleep(20);   // an app may write several formats in a row
                text = ops.ReadText();
                what = $"copied in {ops.NowMs - t1} ms";
            }
            else
            {
                // too late for this read: watch a little longer so that a late copy is undone, never left in place
                long t2 = ops.NowMs;
                while (ops.Sequence == seq && ops.NowMs - t2 <= LateWatchMs) ops.Sleep(20);
                if (ops.Sequence != seq) { ops.Sleep(20); what = $"no copy within {WaitMs} ms, a late copy after {ops.NowMs - t1} ms"; }
                else return (null, $"no copy within {WaitMs + LateWatchMs} ms (clipboard untouched)");
            }
        }
        catch (Exception ex) { what = $"error {ex.GetType().Name} after Ctrl+C"; text = null; }
        finally
        {
            // the clipboard changed since the snapshot: put the user's content back, but only over the target's own copy
            if (ops.Sequence != seq) what += RestoreIfOurs(ops, saved);
        }
        return (text, what);
    }

    private static string RestoreIfOurs(IClipboardOps ops, ClipboardSnapshot saved)
    {
        if (ops.UserCopied()) return ", the user copied meanwhile: not restored";
        if (!ops.HasOwner()) return ", no clipboard owner: not restored";
        if (!ops.OwnedByTarget()) return ", changed by another application: not restored";
        string r = Restore(ops, saved);
        if (r.Contains("NOT")) return r;
        // the application may write once more (delayed formats, a second SetClipboardData): one more look
        uint after = ops.Sequence;
        long t = ops.NowMs;
        while (ops.Sequence == after && ops.NowMs - t <= RecheckMs) ops.Sleep(20);
        if (ops.Sequence != after && !ops.UserCopied() && ops.OwnedByTarget()) r += ", rewritten by the application" + Restore(ops, saved);
        return r;
    }

    /// <summary>Restore with retries while another application keeps the clipboard open.</summary>
    private static string Restore(IClipboardOps ops, ClipboardSnapshot saved)
    {
        for (int i = 1; i <= RestoreTries; i++)
        {
            bool ok;
            try { ok = ops.Restore(saved); } catch (Exception) { ok = false; }
            if (ok) return $", {saved.Items.Count} format(s) restored{(i > 1 ? $" (try {i})" : "")}";
            ops.Sleep(RestoreRetryMs);
        }
        return $", {saved.Items.Count} format(s) NOT restored after {RestoreTries} tries";
    }

    /// <summary>Terminal applications by process image name (no extension): Ctrl+C there is an interrupt.</summary>
    public static bool IsTerminalProcess(string? name) =>
        name != null && TerminalProcesses.Contains(name);

    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // not the shells themselves (cmd, powershell…): their own windows are console classes, caught by the class check,
        // and a GUI they host (a WinForms tool) is not a terminal
        "WindowsTerminal", "OpenConsole", "conhost", "putty", "kitty", "mintty", "wezterm-gui",
        "alacritty", "Hyper", "Tabby", "MobaXterm", "SecureCRT", "ConEmu", "ConEmu64", "Xshell",
    };

    /// <summary>Console and terminal windows: Ctrl+C there is an interrupt (SIGINT), never a copy — the fallback is not used.</summary>
    public static bool IsTerminalClass(string? cls) =>
        cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "PuTTY" or "mintty" or "VirtualConsoleClass"
            or "TMobaXtermForm" or "KiTTY" or "Xshell::MainView" or "ConEmuMainClass" or "Hyper" or "org.wezfurlong.wezterm" ||
        cls != null && (cls.StartsWith("PuTTY", StringComparison.Ordinal) || cls.StartsWith("ConEmu", StringComparison.Ordinal) ||
                        cls.Contains("Terminal", StringComparison.OrdinalIgnoreCase));
}

/// <summary>The text the reading mode reads for a selection (D26): <see cref="Text"/> = the selection completed to whole
/// words, plus the sentence-end punctuation that follows its last word; <see cref="TailAt"/> = index in <see cref="Text"/>
/// where that added punctuation starts (spaces before it included), -1 when none was added.</summary>
internal readonly record struct SelectionText(string Text, int TailAt, int AddedBefore, int AddedAfter);

/// <summary>
/// D26 (owner, 2026-11-11), pure: the selection's boundaries for the reading only — the selection in the source application
/// is never changed. From a little text read around it (<see cref="Context"/> characters each side, same field):
/// <list type="bullet">
/// <item>a word cut at the start or the end is completed (« at bien » → « chat bien », « Le chat do » → « Le chat dort »);
/// a word glued to an elision is completed with it (« l'a|mi » and « l'|ami » → « l'ami »: one reading unit, one tile);
/// a cut run of sentence ends is completed (« ? » of « ?! »);</item>
/// <item>if the selection ends on a sentence's last word, the sentence-end punctuation after it is added: <c>. ! ? …</c> and
/// their runs, plus closing <c>» ) " ”</c> right after (spaces between allowed: « bien ! », « Viens ! »); never
/// <c>; : ,</c>; nothing when the selection already ends with its sentence end.</item>
/// </list>
/// Word boundaries are <see cref="Predict.Core.Tokenizer"/>'s (elisions, internal hyphens and apostrophes, <c>œ</c>).
/// </summary>
internal static class SelectionExtend
{
    public const int Context = 48;

    private static bool Wordy(Predict.Core.Token t) =>
        t.Kind is Predict.Core.TokenKind.Word or Predict.Core.TokenKind.Number or Predict.Core.TokenKind.Elision;

    private static bool Glued(IReadOnlyList<Predict.Core.Token> k, int i) =>
        k[i].Kind == Predict.Core.TokenKind.Elision && i + 1 < k.Count && k[i + 1].Kind == Predict.Core.TokenKind.Word && k[i + 1].Start == k[i].End;

    /// <param name="before">text right before the selection (may be cut at its start), "" when unknown.</param>
    /// <param name="after">text right after it (may be cut at its end), "" when unknown.</param>
    public static SelectionText Apply(string before, string selected, string after)
    {
        before ??= ""; after ??= ""; selected ??= "";
        if (selected.Trim().Length == 0 || before.Length + after.Length == 0) return new(selected, -1, 0, 0);
        string full = before + selected + after;
        int b = before.Length, e = b + selected.Length, nb = b, ne = e;
        var k = Predict.Core.Tokenizer.Tokenize(full);
        for (int i = 0; i < k.Count; i++)
        {
            var t = k[i];
            if (Wordy(t) && t.Start < b && t.End > b) nb = t.Start;
            if ((Wordy(t) || t.Kind == Predict.Core.TokenKind.SentenceEnd) && t.Start < e && t.End > e) ne = t.End;
        }
        // an elision glued to the word: one unit (« l'a|mi », « l'|ami » → « l'ami »; « … l|'ami » → « … l'ami »)
        for (int i = 0; i < k.Count; i++)
        {
            if (!Glued(k, i)) continue;
            if (k[i + 1].Start == nb && k[i].Start < nb) nb = k[i].Start;
            if (k[i].End == ne && k[i + 1].End > ne && k[i].Start >= nb) ne = k[i + 1].End;
        }
        // the sentence-end punctuation after the selection's last word
        int tail = -1, end = ne;
        int last = -1;
        for (int i = 0; i < k.Count && k[i].End <= ne; i++)
            if (k[i].Start >= nb && k[i].Kind is not (Predict.Core.TokenKind.Space or Predict.Core.TokenKind.Break)) last = i;
        if (last >= 0 && Wordy(k[last]))
        {
            int j = last + 1;
            while (j < k.Count && k[j].Kind == Predict.Core.TokenKind.Space) j++;
            if (j < k.Count && k[j].Kind == Predict.Core.TokenKind.SentenceEnd)
            {
                tail = k[last].End;
                end = Math.Max(ne, k[j].End);
                for (int c = j + 1; c < k.Count; c++)
                {
                    if (k[c].Kind == Predict.Core.TokenKind.Space) continue;
                    if (k[c].Kind == Predict.Core.TokenKind.Punct && k[c].Length == 1 && Predict.Core.Tokenizer.IsClosing(k[c].Text[0])) { end = k[c].End; continue; }
                    break;
                }
            }
        }
        string text = full.Substring(nb, end - nb);
        return new(text, tail >= 0 ? tail - nb : -1, b - nb, end - e);
    }
}
