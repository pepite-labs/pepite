using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace Predict.Keyboard;

/// <summary>What the Espace enseignant needs from the app (to open Réglages of a profile, the picker, to deactivate).</summary>
/// <param name="AppLabel">« Pépite 0.7.1 », written into an export.</param>
/// <param name="IsVisible">whether a saved position lies on a screen of this PC (an import clears the others).</param>
/// <param name="Words">plan 10A: the word lists (tab « Mots », words in exports); null = no words.</param>
internal sealed record TeacherContext(ProfileService Profiles, TeacherMode Mode, PinGate Gate, PictureStore? Pictures,
                                      Action<string> EditSettings, Action EndSession, Action ManagementChanged, Action<string>? Log,
                                      string AppLabel = "", Func<int, int, int, int, bool>? IsVisible = null, WordsContext? Words = null);

/// <summary>
/// « Espace enseignant » (plan 09 step 11, D29; behind the PIN): <b>Profils</b> — the list (picture, name, « classe » /
/// « actif » marks) with Ajouter / Nom et image / Dupliquer / Supprimer / Monter / Descendre / Modifier les réglages /
/// Utiliser maintenant; <b>Verrouillage</b> — the three PIN switches and « Qui es-tu ? » at the session start;
/// <b>Code</b> — change the PIN, a new recovery code, deactivate. « Fin de session »: back to « Classe » + the picker.
/// 9B: <b>Verrouillage</b> also holds the returns to « Classe » (inactivity, Win+L) and « Profils proposés sur ce poste »;
/// <b>Exporter / importer</b> — the export list (checkboxes, « Tout sélectionner » / « Aucun », « Inclure le code
/// enseignant » unticked, « Enregistrer sous… ») and « Importer un fichier… » (<see cref="ImportDialog"/>; disabled in an
/// in-memory session). One at a time; code-built (every control has an AutomationId).
/// </summary>
internal sealed class TeacherWindow : Window
{
    private static TeacherWindow? _open;

    private readonly TeacherContext _c;
    private readonly TabControl _tabs;
    private readonly ListBox _list = new();
    private readonly TextBlock _count;
    private readonly Button _edit, _dup, _del, _up, _down, _settings, _use, _add;
    private readonly CheckBox _sw, _ed, _qu, _pk, _onLock, _offeredOnly, _includePin;
    private readonly CheckBox? _includeWords, _includeImages;
    private readonly ComboBox? _wordsProfile;
    private readonly ContentControl? _wordsHost;
    private readonly ComboBox _idle;
    private readonly StackPanel _offeredList = new(), _exportList = new();
    private readonly HashSet<string> _exportOff = new(StringComparer.Ordinal);
    private readonly TextBlock _exchangeStatus;
    private bool _loading;

    public static void ShowSingle(TeacherContext c)
    {
        if (_open is { } w) { w.Activate(); return; }
        var win = _open = new TeacherWindow(c);
        win.Closed += (_, _) => { if (_open == win) _open = null; };
        Ui.ShowInFront(win);
        c.Log?.Invoke("teacher window opened");
    }

    public static void CloseOpen() => _open?.Close();

    /// <summary>Plan 12 (Q3): a double-clicked <c>.pepite</c> — the window (behind the PIN, asked by the caller) on its
    /// « Exporter / importer » tab, then the import dialog with <paramref name="file"/> already chosen.</summary>
    public static void ImportFromFile(TeacherContext c, string file)
    {
        ShowSingle(c);
        if (_open is not { } w) return;
        if (w._tabs.Items.Cast<TabItem>().FirstOrDefault(t => AutomationProperties.GetAutomationId(t) == "TabExchange") is { } tab)
            w._tabs.SelectedItem = tab;
        w.Import(file);
    }
    public static void RefreshOpen() => _open?.Refresh();

    private TeacherWindow(TeacherContext c)
    {
        _c = c;
        Ui.Setup(this, Strings.TeacherTitle, "TeacherWindow");
        // 0.11.1: the « Exporter / importer » and « Mots » tabs are taller than a 768-pixel screen: every tab scrolls (Tab)
        // and « Terminer la session » / « Fermer » stay visible
        Ui.FitToWorkArea(this);
        var tabs = _tabs = new TabControl { Margin = new Thickness(12, 12, 12, 0) };
        AutomationProperties.SetAutomationId(tabs, "TeacherTabs");

        // ---------------------------------------------------------------- Profils
        var prof = new DockPanel { Margin = new Thickness(12) };
        _count = Ui.Text("", "ProfilesCount", 13);
        _count.Opacity = 0.8;
        DockPanel.SetDock(_count, Dock.Top);
        prof.Children.Add(_count);
        var hint = Ui.Text(Strings.ProfileClassHint, "ProfileClassHint", 13);
        hint.Opacity = 0.8; hint.MaxWidth = 620; hint.Margin = new Thickness(0, 2, 0, 8);
        DockPanel.SetDock(hint, Dock.Top);
        prof.Children.Add(hint);
        var buttons = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        Button B(string t, string id, Action a) { var b = Ui.Button(t, id, (_, _) => a(), 190); b.Margin = new Thickness(0, 0, 0, 6); buttons.Children.Add(b); return b; }
        _add = B(Strings.ProfileAdd, "ProfileAdd", Add);
        _edit = B(Strings.ProfileEdit, "ProfileEdit", EditSelected);
        _dup = B(Strings.ProfileDuplicate, "ProfileDuplicate", DuplicateSelected);
        _del = B(Strings.ProfileDelete, "ProfileDelete", DeleteSelected);
        _up = B(Strings.ProfileUp, "ProfileUp", () => MoveSelected(-1));
        _down = B(Strings.ProfileDown, "ProfileDown", () => MoveSelected(1));
        _settings = B(Strings.ProfileSettings, "ProfileSettings", () => { if (Selected is { } id) _c.EditSettings(id); });
        // the Espace enseignant closes on a switch: the pupil takes over (any switch also closes it, App's profile host)
        _use = B(Strings.ProfileUse, "ProfileUse", () => { if (Selected is { } id) { Close(); _c.Profiles.Switch(id, "teacher"); } });
        DockPanel.SetDock(buttons, Dock.Right);
        prof.Children.Add(buttons);
        _list.Width = 400;
        AutomationProperties.SetName(_list, Strings.ProfileListName);
        _list.Height = 380;
        AutomationProperties.SetAutomationId(_list, "ProfileList");
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.MouseDoubleClick += (_, _) => EditSelected();
        prof.Children.Add(_list);
        tabs.Items.Add(Tab(Strings.TabProfiles, "TabProfiles", prof));

        // ---------------------------------------------------------------- Verrouillage
        var lk = new StackPanel { Margin = new Thickness(12), MaxWidth = 640 };
        var p = c.Profiles.Store.Policy;
        _sw = Ui.Check(Strings.LockSwitch, "LockSwitch", p.PinToSwitch);
        _ed = Ui.Check(Strings.LockEdit, "LockEdit", p.PinToEdit);
        _qu = Ui.Check(Strings.LockQuit, "LockQuit", p.PinToQuit);
        _pk = Ui.Check(Strings.LockPicker, "LockPicker", p.SessionPicker);
        _onLock = Ui.Check(Strings.ReturnOnLock, "LockReturnOnLock", p.ReturnOnLock);
        foreach (var cb in new[] { _sw, _ed, _qu, _pk })
        {
            cb.Checked += (_, _) => SavePolicy();
            cb.Unchecked += (_, _) => SavePolicy();
            lk.Children.Add(cb);
        }
        var note = Ui.Text(Strings.LockTeacherNote, "LockTeacherNote", 13);
        note.Opacity = 0.8; note.Margin = new Thickness(0, 6, 0, 12);
        lk.Children.Add(note);
        // returns to « Classe » (plan 09B, D29 Q10): inactivity (off), Win+L (off); logoff always
        var idleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var idleLabel = new Label { Content = Strings.IdleReturnLabel, Padding = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _idle = new ComboBox { MinWidth = 120 };
        AutomationProperties.SetAutomationId(_idle, "IdleReturn");
        AutomationProperties.SetName(_idle, Strings.IdleReturnLabel);
        idleLabel.Target = _idle;
        foreach (var m in ProfilePolicy.IdleChoices)
        {
            var it = new ComboBoxItem { Content = m == 0 ? Strings.IdleNever : string.Format(Strings.IdleMinutes, m), Tag = m };
            AutomationProperties.SetAutomationId(it, "Idle-" + m);
            _idle.Items.Add(it);
        }
        _idle.SelectedItem = _idle.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == p.IdleReturnMin) ?? _idle.Items[0];
        _idle.SelectionChanged += (_, _) => SavePolicy();
        idleRow.Children.Add(idleLabel);
        idleRow.Children.Add(_idle);
        lk.Children.Add(idleRow);
        _onLock.Checked += (_, _) => SavePolicy();
        _onLock.Unchecked += (_, _) => SavePolicy();
        lk.Children.Add(_onLock);
        var logoff = Ui.Text(Strings.LogoffNote, "LogoffNote", 13);
        logoff.Opacity = 0.8; logoff.Margin = new Thickness(0, 6, 0, 12);
        lk.Children.Add(logoff);
        // « Profils proposés sur ce poste »: this PC's list (by computer name)
        lk.Children.Add(Ui.Heading(Strings.OfferedHeading, "OfferedHeading", 15));
        _offeredOnly = Ui.Check(string.Format(Strings.OfferedOnly, c.Profiles.Store.MachineName), "OfferedOnly",
                                p.OfferedOn(c.Profiles.Store.MachineName) != null);
        _offeredOnly.Checked += (_, _) => SaveOffered();
        _offeredOnly.Unchecked += (_, _) => SaveOffered();
        lk.Children.Add(_offeredOnly);
        AutomationProperties.SetName(_offeredList, Strings.OfferedListName);
        AutomationProperties.SetAutomationId(_offeredList, "OfferedList");
        lk.Children.Add(new ScrollViewer { Content = _offeredList, MaxHeight = 150, Margin = new Thickness(22, 4, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        var offeredHint = Ui.Text(Strings.OfferedHint, "OfferedHint", 12.5);
        offeredHint.Opacity = 0.75; offeredHint.Margin = new Thickness(22, 4, 0, 0);
        lk.Children.Add(offeredHint);
        tabs.Items.Add(Tab(Strings.TabLocking, "TabLocking", lk));

        // ---------------------------------------------------------------- Code
        var code = new StackPanel { Margin = new Thickness(12) };
        var status = Ui.Text("", "CodeStatus", 13);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        Button C(string t, string id, Action a) { var b = Ui.Button(t, id, (_, _) => a(), 260); b.Margin = new Thickness(0, 0, 0, 8); b.HorizontalAlignment = HorizontalAlignment.Left; code.Children.Add(b); return b; }
        // the code itself: the current PIN again first, as for « Désactiver » (never within the grace)
        C(Strings.CodeChange, "CodeChange", () =>
        {
            var how = PinDialog.AskOutcomeOf(_c.Mode.Lock, PinReason.Teacher, _c.Log, Strings.PinReasonChangeCode);
            // « Code oublié ? » inside this dialog already set a new PIN (and showed a new recovery code): done
            bool changed = how == PinDialog.AskOutcome.Recovered ||
                           how == PinDialog.AskOutcome.Verified && PinDialog.Create(_c.Mode.Lock, pin => _c.Mode.Lock.SetPin(pin), _c.Log);
            if (changed) { status.Text = Strings.CodeChanged; Ui.Announce(status); }
        });
        C(Strings.CodeNewRecovery, "CodeNewRecovery", () =>
        {
            if (MessageBox.Show(this, Strings.NewRecoveryConfirm, Strings.TeacherTitle, MessageBoxButton.YesNo, MessageBoxImage.Question,
                                MessageBoxResult.No) != MessageBoxResult.Yes) return;
            // a recovery inside the dialog has already made (and shown) a new code
            if (PinDialog.AskOutcomeOf(_c.Mode.Lock, PinReason.Teacher, _c.Log, Strings.PinReasonNewRecovery) != PinDialog.AskOutcome.Verified) return;
            _ = NewRecoveryAsync();
        });
        C(Strings.CodeDeactivate, "CodeDeactivate", Deactivate);
        code.Children.Add(status);
        tabs.Items.Add(Tab(Strings.TabCode, "TabCode", code));

        // ---------------------------------------------------------------- Exporter / importer (9B)
        var ex = new StackPanel { Margin = new Thickness(12), MaxWidth = 640 };
        ex.Children.Add(Ui.Heading(Strings.ExportHeading, "ExportHeading", 16));
        ex.Children.Add(Ui.Text(Strings.ExportHelp, "ExportHelp", 13));
        var privacy = Ui.Text(Strings.ExportPrivacy, "ExportPrivacy", 13);
        privacy.FontWeight = FontWeights.SemiBold; privacy.Margin = new Thickness(0, 4, 0, 6);
        ex.Children.Add(privacy);
        var exRow = new DockPanel();
        var exButtons = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var all = Ui.Button(Strings.ExportAll, "ExportAll", (_, _) => { _exportOff.Clear(); RefreshExport(); }, 170);
        all.Margin = new Thickness(0, 0, 0, 6);
        var none = Ui.Button(Strings.ExportNone, "ExportNone", (_, _) =>
        {
            foreach (var e in Pupils) _exportOff.Add(e.Id);
            RefreshExport();
        }, 170);
        none.Margin = new Thickness(0);
        exButtons.Children.Add(all);
        exButtons.Children.Add(none);
        DockPanel.SetDock(exButtons, Dock.Right);
        exRow.Children.Add(exButtons);
        AutomationProperties.SetName(_exportList, Strings.ExportListName);
        AutomationProperties.SetAutomationId(_exportList, "ExportList");
        exRow.Children.Add(new ScrollViewer { Content = _exportList, Height = 170, Width = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        ex.Children.Add(exRow);
        _includePin = Ui.Check(Strings.ExportIncludePin, "ExportIncludePin", false);
        AutomationProperties.SetHelpText(_includePin, Strings.ExportIncludePinHint);
        ex.Children.Add(_includePin);
        var pinHint = Ui.Text(Strings.ExportIncludePinHint, null, 12.5);
        pinHint.Opacity = 0.75; pinHint.Margin = new Thickness(22, 0, 0, 6);
        ex.Children.Add(pinHint);
        if (c.Words != null)
        {
            // plan 10A, Q14: the word lists travel with the profiles, ticked by default
            _includeWords = Ui.Check(Strings.ExportIncludeWords, "ExportIncludeWords", true);
            AutomationProperties.SetHelpText(_includeWords, Strings.ExportIncludeWordsHint);
            ex.Children.Add(_includeWords);
            var wHint = Ui.Text(Strings.ExportIncludeWordsHint, null, 12.5);
            wHint.Opacity = 0.75; wHint.Margin = new Thickness(22, 0, 0, 6);
            ex.Children.Add(wHint);
            // plan 10C (Q20): the pictures of the names databases, only when ticked (the file becomes a zip)
            _includeImages = Ui.Check(Strings.ExportIncludeImages, "ExportIncludeImages", false);
            AutomationProperties.SetHelpText(_includeImages, Strings.ExportIncludeImagesHint);
            ex.Children.Add(_includeImages);
            var iHint = Ui.Text(Strings.ExportIncludeImagesHint, null, 12.5);
            iHint.Opacity = 0.75; iHint.Margin = new Thickness(22, 0, 0, 6);
            ex.Children.Add(iHint);
        }
        var save = Ui.Button(Strings.ExportSave, "ExportSave", (_, _) => Export(), 190);
        save.HorizontalAlignment = HorizontalAlignment.Left;
        ex.Children.Add(save);
        var impHead = Ui.Heading(Strings.ImportHeading, "ImportHeadingTab", 16);
        impHead.Margin = new Thickness(0, 18, 0, 0);
        ex.Children.Add(impHead);
        ex.Children.Add(Ui.Text(Strings.ImportHelp, "ImportHelp", 13));
        var open = Ui.Button(Strings.ImportOpen, "ImportOpen", (_, _) => Import(), 190);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 6, 0, 0);
        // an in-memory session writes nothing (start-up failure): no import
        if (c.Profiles.Store.InMemory) { open.IsEnabled = false; open.ToolTip = Strings.CannotSaveHint; ToolTipService.SetShowOnDisabled(open, true); }
        ex.Children.Add(open);
        _exchangeStatus = Ui.Text("", "ExchangeStatus", 13);
        _exchangeStatus.Margin = new Thickness(0, 10, 0, 0);
        AutomationProperties.SetLiveSetting(_exchangeStatus, AutomationLiveSetting.Polite);
        ex.Children.Add(_exchangeStatus);
        tabs.Items.Add(Tab(Strings.TabExchange, "TabExchange", ex));

        // ---------------------------------------------------------------- Mots (plan 10A)
        if (c.Words != null)
        {
            var wp = new DockPanel { Margin = new Thickness(12) };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            _wordsProfile = new ComboBox { MinWidth = 220 };
            AutomationProperties.SetAutomationId(_wordsProfile, "WordsProfile");
            AutomationProperties.SetName(_wordsProfile, Strings.WordsProfileLabel.Replace("_", ""));
            top.Children.Add(new Label { Content = Strings.WordsProfileLabel, Target = _wordsProfile, Padding = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            top.Children.Add(_wordsProfile);
            DockPanel.SetDock(top, Dock.Top);
            wp.Children.Add(top);
            _wordsHost = new ContentControl();
            wp.Children.Add(_wordsHost);
            _wordsProfile.SelectionChanged += (_, _) => ShowWords();
            tabs.Items.Add(Tab(Strings.TabWords, "TabWords", wp));
        }

        var root = new DockPanel();
        var bottom = new DockPanel { Margin = new Thickness(12), LastChildFill = false };
        var end = Ui.Button(Strings.EndSession, "EndSession", (_, _) => { Close(); _c.EndSession(); }, 150);
        end.ToolTip = Strings.EndSessionHint;
        AutomationProperties.SetHelpText(end, Strings.EndSessionHint);
        DockPanel.SetDock(end, Dock.Left);
        var close = Ui.Button(Strings.Close, "TeacherClose", (_, _) => Close(), 110);
        close.IsCancel = true;
        close.Margin = new Thickness(0);
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(end);
        bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(tabs);
        Content = root;
        c.Profiles.ProfilesChanged += OnChanged;
        Closed += (_, _) => c.Profiles.ProfilesChanged -= OnChanged;
        Refresh();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(Refresh);

    /// <summary>A new recovery code: PBKDF2 (a few hundred ms) on a worker thread, the window disabled meanwhile.</summary>
    private async System.Threading.Tasks.Task NewRecoveryAsync()
    {
        IsEnabled = false;
        Cursor = System.Windows.Input.Cursors.Wait;
        try
        {
            var lk = _c.Mode.Lock;
            var rc = await System.Threading.Tasks.Task.Run(() => lk.NewRecovery());
            RecoveryCodeWindow.ShowModal(rc, DateTime.UtcNow, _c.Log);
        }
        catch (Exception ex) { _c.Log?.Invoke($"teacher window: new recovery code failed ({ex.GetType().Name})"); }
        finally
        {
            IsEnabled = true;
            Cursor = null;
        }
    }

    private static TabItem Tab(string header, string id, UIElement content)
    {
        var t = new TabItem { Header = header, Content = Ui.Scroll(content) };
        AutomationProperties.SetAutomationId(t, id);
        return t;
    }

    private string? Selected => (_list.SelectedItem as ListBoxItem)?.Tag as string;

    private void Refresh()
    {
        var keep = Selected;
        var store = _c.Profiles.Store;
        _list.Items.Clear();
        foreach (var e in store.Profiles)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
            row.Children.Add(AvatarView.Create(e.Profile, _c.Pictures, 40));
            var name = new TextBlock { Text = e.Profile.Name, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            if (e.Unreadable) name.FontStyle = FontStyles.Italic;
            row.Children.Add(name);
            var marks = new[] { e.IsClass ? Strings.ClassMark : null, e.Id == store.ActiveId ? Strings.ActiveMark : null }.Where(m => m != null).ToList();
            if (marks.Count > 0)
                row.Children.Add(new TextBlock { Text = " (" + string.Join(", ", marks) + ")", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Tag = e.Id };
            AutomationProperties.SetName(item, e.Profile.Name + (marks.Count > 0 ? " (" + string.Join(", ", marks) + ")" : ""));
            AutomationProperties.SetAutomationId(item, "Row-" + e.Id);
            _list.Items.Add(item);
        }
        _count.Text = string.Format(Strings.ProfilesCount, store.Count, Profile.MaxProfiles);
        RefreshWordsProfiles();
        RefreshExport();
        RefreshOffered();
        var again = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == keep);
        _list.SelectedItem = again ?? (_list.Items.Count > 0 ? _list.Items[0] : null);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var store = _c.Profiles.Store;
        var e = Selected is { } id ? store.Get(id) : null;
        bool ok = e is { Unreadable: false };
        _add.IsEnabled = store.CanAdd;
        _add.ToolTip = store.CanAdd ? null : Strings.ProfileFull;
        _edit.IsEnabled = ok;
        _dup.IsEnabled = ok && store.CanAdd;
        _del.IsEnabled = e is { IsClass: false } && e.Id != store.ActiveId;
        _del.ToolTip = e != null && e.Id == store.ActiveId ? Strings.DeleteActive : null;
        int i = e is null ? -1 : store.Profiles.ToList().FindIndex(x => x.Id == e.Id);
        int min = store.Class != null ? 1 : 0;
        _up.IsEnabled = e is { IsClass: false } && i > min;
        _down.IsEnabled = e is { IsClass: false } && i >= 0 && i < store.Count - 1;
        _settings.IsEnabled = ok;
        _use.IsEnabled = ok && e!.Id != store.ActiveId;
    }

    private void Add()
    {
        var store = _c.Profiles.Store;
        var r = ProfileEditDialog.Edit(this, "", Avatar.Default with { Color = NextColor() }, _c.Pictures, n => store.CheckName(n));
        if (r is not { } v) return;
        var e = _c.Profiles.Add(v.Name, v.Avatar);
        Refresh();
        Select(e.Id);
    }

    /// <summary>A colour not used yet, if any (new pupils do not all start blue).</summary>
    private ProfileColor NextColor()
    {
        var used = _c.Profiles.Store.Profiles.Select(e => e.Profile.Avatar.Color).ToHashSet();
        foreach (ProfileColor c in Enum.GetValues(typeof(ProfileColor))) if (!used.Contains(c)) return c;
        return (ProfileColor)(_c.Profiles.Store.Count % 8);
    }

    private void EditSelected()
    {
        if (Selected is not { } id || _c.Profiles.Store.Get(id) is not { Unreadable: false } e) return;
        var r = ProfileEditDialog.Edit(this, e.Profile.Name, e.Profile.Avatar, _c.Pictures, n => _c.Profiles.Store.CheckName(n, id), nameEditable: !e.IsClass);
        if (r is not { } v) return;
        if (!e.IsClass && v.Name != e.Profile.Name) _c.Profiles.Rename(id, v.Name);
        if (v.Avatar != e.Profile.Avatar) _c.Profiles.SetAvatar(id, v.Avatar);
        Refresh();
    }

    private void DuplicateSelected()
    {
        if (Selected is not { } id || _c.Profiles.Store.Get(id) is not { Unreadable: false } e) return;
        var name = string.Format(Strings.CopyName, e.Profile.Name);
        if (name.Length > Profile.MaxName) name = name.Substring(0, Profile.MaxName);
        var r = ProfileEditDialog.Edit(this, name, e.Profile.Avatar, _c.Pictures, n => _c.Profiles.Store.CheckName(n));
        if (r is not { } v) return;
        var copy = _c.Profiles.Duplicate(id, v.Name);
        if (v.Avatar != copy.Profile.Avatar) _c.Profiles.SetAvatar(copy.Id, v.Avatar);
        Refresh();
        Select(copy.Id);
    }

    private void DeleteSelected()
    {
        if (Selected is not { } id || _c.Profiles.Store.Get(id) is not { } e || e.IsClass) return;
        if (id == _c.Profiles.Store.ActiveId) { MessageBox.Show(this, Strings.DeleteActive, Strings.TeacherTitle); return; }
        if (MessageBox.Show(this, string.Format(Strings.DeleteConfirm, e.Profile.Name), Strings.TeacherTitle, MessageBoxButton.YesNo,
                            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _c.Profiles.Delete(id);
        Refresh();
    }

    private void MoveSelected(int delta)
    {
        if (Selected is not { } id) return;
        _c.Profiles.Move(id, delta);
        Refresh();
        Select(id);
    }

    private void Select(string id)
    {
        var it = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == id);
        if (it is null) return;
        _list.SelectedItem = it;
        _list.ScrollIntoView(it);
        it.Focus();
    }

    private void SavePolicy()
    {
        if (_loading) return;
        _c.Profiles.SetPolicy(_c.Profiles.Store.Policy with
        {
            PinToSwitch = _sw.IsChecked == true, PinToEdit = _ed.IsChecked == true, PinToQuit = _qu.IsChecked == true, SessionPicker = _pk.IsChecked == true,
            IdleReturnMin = _idle.SelectedItem is ComboBoxItem { Tag: int m } ? m : 0, ReturnOnLock = _onLock.IsChecked == true,
        });
    }

    private IEnumerable<ProfileEntry> Pupils => _c.Profiles.Store.Profiles.Where(e => !e.IsClass && !e.Unreadable);

    /// <summary>« Profils proposés sur ce poste »: ticking the switch starts from every pupil; unticking forgets the list.</summary>
    private void SaveOffered()
    {
        if (_loading) return;
        var store = _c.Profiles.Store;
        var ids = _offeredOnly.IsChecked == true ? Pupils.Select(e => e.Id).ToList() : null;
        _c.Profiles.SetPolicy(store.Policy.WithOffered(store.MachineName, ids));
        RefreshOffered();
    }

    /// <summary>A pupil's box in the list: saved as it is (the list is not rebuilt, the focus stays on the box).</summary>
    private void SaveOfferedBoxes()
    {
        if (_loading || _offeredOnly.IsChecked != true) return;
        var store = _c.Profiles.Store;
        var ids = _offeredList.Children.OfType<CheckBox>().Where(cb => cb.IsChecked == true).Select(cb => (string)cb.Tag).ToList();
        _c.Profiles.SetPolicy(store.Policy.WithOffered(store.MachineName, ids));
    }

    private void RefreshOffered()
    {
        _loading = true;
        try
        {
            var store = _c.Profiles.Store;
            var list = store.Policy.OfferedOn(store.MachineName);
            _offeredOnly.IsChecked = list != null;
            _offeredList.Children.Clear();
            foreach (var e in Pupils)
            {
                var cb = Ui.Check(e.Profile.Name, "Offered-" + e.Id, list is null || list.Contains(e.Id));
                cb.Tag = e.Id;
                cb.IsEnabled = list != null;
                cb.Margin = new Thickness(0, 2, 0, 0);
                cb.Checked += (_, _) => SaveOfferedBoxes();
                cb.Unchecked += (_, _) => SaveOfferedBoxes();
                _offeredList.Children.Add(cb);
            }
        }
        finally { _loading = false; }
    }

    private void RefreshExport()
    {
        _exportList.Children.Clear();
        foreach (var e in Pupils)
        {
            var cb = Ui.Check(e.Profile.Name, "Export-" + e.Id, !_exportOff.Contains(e.Id));
            cb.Margin = new Thickness(0, 2, 0, 0);
            var id = e.Id;
            cb.Checked += (_, _) => _exportOff.Remove(id);
            cb.Unchecked += (_, _) => _exportOff.Add(id);
            _exportList.Children.Add(cb);
        }
    }

    private void Say(string text)
    {
        _exchangeStatus.Text = text;
        Ui.Announce(_exchangeStatus);
    }

    /// <summary>« Enregistrer sous… »: the ticked pupils + « Classe », the code's hashes only when ticked.</summary>
    private void Export()
    {
        var ids = Pupils.Where(e => !_exportOff.Contains(e.Id)).Select(e => e.Id).ToList();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Strings.FileFilter, DefaultExt = ProfileArchive.Extension, AddExtension = true, OverwritePrompt = true,
            FileName = string.Format(Strings.ExportFileName, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var words = _c.Words is { } wc && _includeWords?.IsChecked == true ? (Func<string, WordBook>)wc.Words.BookOf : null;
            if (words != null) _c.Words!.Words.Store.Flush();
            var images = words != null && _includeImages?.IsChecked == true ? (Func<string, string?>)_c.Words!.Words.ImagesDir : null;
            int n = ProfileArchive.Write(dlg.FileName, _c.Profiles.Store, ids, _includePin.IsChecked == true, _c.Mode.Lock, _c.AppLabel, _c.Log, words, images);
            Say(string.Format(Strings.ExportDone, n));
        }
        catch (ArchiveException ae)
        {
            _c.Log?.Invoke("profiles: export refused (pictures over 50 MB)");
            Say(ae.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _c.Log?.Invoke($"profiles: export failed ({ex.GetType().Name})");
            Say(Strings.ExportFailed);
        }
    }

    /// <summary>« Importer un fichier… »: read and validate, then the import dialog (backup, apply). Plan 12: with
    /// <paramref name="file"/> (a double-clicked <c>.pepite</c>) the file dialog is skipped.</summary>
    private void Import(string? file = null)
    {
        if (_c.Profiles.Store.InMemory) return;
        if (file is null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = Strings.FileFilter, DefaultExt = ProfileArchive.Extension, CheckFileExists = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog(this) != true) return;
            file = dlg.FileName;
        }
        ArchiveContent content;
        try { content = ProfileArchive.Read(file); }
        catch (ArchiveException ae)
        {
            _c.Log?.Invoke("profiles: import file refused");
            MessageBox.Show(this, ae.Message, Strings.ImportTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var r = ImportDialog.Show(this, Path.GetFileName(file), content, _c.Profiles, _c.Mode.Lock, _c.Pictures, _c.IsVisible, _c.Log);
        if (r is { } res)
        {
            Say(string.Format(Strings.ImportDone, res.Created, res.Replaced, res.KeptBoth, res.Skipped) +
                (res.ClassReplaced ? Strings.ImportDoneClass : "") + (res.PinApplied ? Strings.ImportDonePin : ""));
            Refresh();
        }
    }

    /// <summary>The profile chooser of the « Mots » tab (« Classe » first, readable profiles), the selection kept.</summary>
    private void RefreshWordsProfiles()
    {
        if (_wordsProfile is null) return;
        var keep = (_wordsProfile.SelectedItem as ComboBoxItem)?.Tag as string;
        _wordsProfile.Items.Clear();
        foreach (var e in _c.Profiles.Store.Profiles.Where(e => !e.Unreadable))
        {
            var it = new ComboBoxItem { Content = e.Profile.Name, Tag = e.Id };
            AutomationProperties.SetAutomationId(it, "WordsProfile-" + e.Id);
            _wordsProfile.Items.Add(it);
        }
        _wordsProfile.SelectedItem = _wordsProfile.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == keep)
                                     ?? (_wordsProfile.Items.Count > 0 ? _wordsProfile.Items[0] : null);
    }

    private string? _wordsShown;

    private void ShowWords()
    {
        if (_wordsHost is null || _c.Words is null) return;
        var id = (_wordsProfile?.SelectedItem as ComboBoxItem)?.Tag as string;
        if (id == _wordsShown && _wordsHost.Content != null) return;
        _wordsShown = id;
        _wordsHost.Content = id is null ? null : new WordEditor(_c.Words, id, teacher: true);
    }

    private void Deactivate()
    {
        if (MessageBox.Show(this, Strings.CodeDeactivateConfirm, Strings.TeacherTitle, MessageBoxButton.YesNo, MessageBoxImage.Question,
                            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        // always the PIN again here (Q4), even within the grace
        PinDialog.Ask(_c.Mode.Lock, PinReason.Teacher, () =>
        {
            _c.Mode.Deactivate();
            _c.Gate.ClearGrace();
            Close();
            _c.ManagementChanged();
        }, _c.Log, Strings.PinReasonDeactivate);
    }
}
