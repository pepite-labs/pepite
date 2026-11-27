using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Predict.Keyboard;

/// <summary>
/// The word editor of one profile (plan 10A), code-built, shared by Réglages → « Mots » (the profile being edited,
/// behind the Edit PIN as the rest of Réglages) and Espace enseignant → « Mots » (<paramref name="teacher"/>: any
/// profile, plus the named lists). Sections: « Mes mots » (« Mots de la classe » for « Classe ») with a search box,
/// Ajouter… / Modifier… / Supprimer; for a pupil, « Mots de la classe » read-only with « Ne pas me le proposer »
/// (hidden for this pupil, Q2); « Mes fautes » (J'écris … → Je veux écrire …); teacher: the lists with on / off,
/// « Nouvelle leçon… », « Importer une liste (.txt)… », « Exporter la liste (.txt)… », « Supprimer la liste »;
/// « Effacer tous mes mots… » (a profiles backup first). Changes are saved at once (no Enregistrer). Every control has an
/// AutomationId; every string comes from <see cref="Strings"/>.
/// </summary>
internal sealed class WordEditor : DockPanel
{
    private readonly WordsContext _c;
    private readonly string _id;
    private readonly bool _teacher;
    private readonly TextBox _search = new() { Width = 220 };
    private readonly ListBox _own = new() { Height = 200, Width = 300 };
    private readonly ListBox? _class;
    private readonly ListBox _fautes = new() { Height = 120, Width = 300 };
    private readonly ListBox? _lists;
    private readonly Button? _hide;
    private readonly ListBox? _exclus;        // « Classe » only: « Mots à ne jamais proposer »
    private readonly Button? _exclusRemove;
    private readonly TextBlock _status, _ownCount;
    private bool _refreshing;

    private bool IsClass => _id == Profile.ClassId;

    /// <param name="headingExtra">Réglages: the « ? » help button, next to the first heading (none in the Espace
    /// enseignant, owner Q5 of the embedded-help plan).</param>
    public WordEditor(WordsContext c, string profileId, bool teacher, UIElement? headingExtra = null)
    {
        _c = c;
        _id = profileId;
        _teacher = teacher;
        LastChildFill = false;
        var left = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        var right = new StackPanel();

        // « Mes mots » / « Mots de la classe »
        var heading = Ui.Heading(IsClass ? Strings.ClassWordsList : Strings.MyWordsList, "OwnWordsHeading", 15);
        if (headingExtra is null) left.Children.Add(heading);
        else
        {
            var hp = new StackPanel { Orientation = Orientation.Horizontal };
            hp.Children.Add(heading);
            hp.Children.Add(headingExtra);
            left.Children.Add(hp);
        }
        var sr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        sr.Children.Add(new Label { Content = Strings.WordSearch, Target = _search, Padding = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetAutomationId(_search, "WordSearch");
        AutomationProperties.SetName(_search, Strings.WordSearch.Replace("_", ""));
        _search.TextChanged += (_, _) => Refresh();
        sr.Children.Add(_search);
        left.Children.Add(sr);
        AutomationProperties.SetAutomationId(_own, "OwnWords");
        AutomationProperties.SetName(_own, IsClass ? Strings.ClassWordsList : Strings.MyWordsList);
        left.Children.Add(_own);
        _ownCount = Ui.Text("", "OwnWordsCount", 12.5);
        _ownCount.Opacity = 0.75;
        left.Children.Add(_ownCount);
        var ob = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        ob.Children.Add(Ui.Button(Strings.WordAddDots, "WordAdd", (_, _) => AddWord(), 100));
        ob.Children.Add(Ui.Button(Strings.WordEdit, "WordEdit", (_, _) => EditWord(), 100));
        ob.Children.Add(Ui.Button(Strings.WordDelete, "WordDelete", (_, _) => DeleteWord(), 100));
        left.Children.Add(ob);
        _own.MouseDoubleClick += (_, _) => EditWord();

        if (!IsClass)
        {
            var h = Ui.Heading(Strings.ClassWordsHeading, "ClassWordsHeading", 15);
            h.Margin = new Thickness(0, 14, 0, 4);
            left.Children.Add(h);
            _class = new ListBox { Height = 140, Width = 300 };
            AutomationProperties.SetAutomationId(_class, "ClassWords");
            AutomationProperties.SetName(_class, Strings.ClassWordsHeading);
            left.Children.Add(_class);
            _hide = Ui.Button(Strings.WordHide, "WordHide", (_, _) => ToggleHidden(), 220);
            _hide.Margin = new Thickness(0, 6, 0, 0);
            _hide.HorizontalAlignment = HorizontalAlignment.Left;
            _class.SelectionChanged += (_, _) => UpdateHide();
            left.Children.Add(_hide);
        }
        else if (WordLayering.ShowsExclus(IsClass, teacher, c.SettingsProtected?.Invoke() ?? false))
        {
            // 0.11.0 (plan offensive-words B3, D35): « Classe » only — behind the PIN like the rest of its words; a pupil's
            // editor has no such section, so a pupil can neither see nor change the list
            var h = Ui.Heading(Strings.ExclusHeading, "ExclusHeading", 15);
            h.Margin = new Thickness(0, 14, 0, 0);
            left.Children.Add(h);
            var xh = Ui.Text(Strings.ExclusHelp, "ExclusHelp", 12.5);
            xh.Opacity = 0.75; xh.MaxWidth = 300; xh.HorizontalAlignment = HorizontalAlignment.Left;
            left.Children.Add(xh);
            _exclus = new ListBox { Height = 110, Width = 300, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(_exclus, "ExclusWords");
            AutomationProperties.SetName(_exclus, Strings.ExclusHeading);
            left.Children.Add(_exclus);
            var xb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            xb.Children.Add(Ui.Button(Strings.WordAddDots, "ExclusAdd", (_, _) => AddExclu(), 100));
            _exclusRemove = Ui.Button(Strings.ExclusRemove, "ExclusRemove", (_, _) => RemoveExclu(), 100);
            xb.Children.Add(_exclusRemove);
            left.Children.Add(xb);
            _exclus.SelectionChanged += (_, _) => UpdateExclusRemove();
        }

        // « Mes fautes »
        right.Children.Add(Ui.Heading(Strings.FautesHeading, "FautesHeading", 15));
        var fh = Ui.Text(Strings.FautesHelp, "FautesHelp", 12.5);
        fh.Opacity = 0.75; fh.MaxWidth = 320;
        right.Children.Add(fh);
        AutomationProperties.SetAutomationId(_fautes, "Fautes");
        AutomationProperties.SetName(_fautes, Strings.FautesHeading);
        _fautes.Margin = new Thickness(0, 4, 0, 0);
        right.Children.Add(_fautes);
        var fb = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        fb.Children.Add(Ui.Button(Strings.WordAddDots, "FauteAdd", (_, _) => AddFaute(), 100));
        fb.Children.Add(Ui.Button(Strings.WordDelete, "FauteDelete", (_, _) => DeleteFaute(), 100));
        right.Children.Add(fb);

        if (teacher)
        {
            var lh = Ui.Heading(Strings.ListsHeading, "ListsHeading", 15);
            lh.Margin = new Thickness(0, 14, 0, 0);
            right.Children.Add(lh);
            var lhint = Ui.Text(Strings.ListsHelp, "ListsHelp", 12.5);
            lhint.Opacity = 0.75; lhint.MaxWidth = 320;
            right.Children.Add(lhint);
            _lists = new ListBox { Height = 120, Width = 320, Margin = new Thickness(0, 4, 0, 0) };
            AutomationProperties.SetAutomationId(_lists, "WordLists");
            AutomationProperties.SetName(_lists, Strings.ListsHeading);
            right.Children.Add(_lists);
            var lb = new WrapPanel { Width = 330, Margin = new Thickness(0, 6, 0, 0) };
            Button LB(string t, string id, Action a) { var b = Ui.Button(t, id, (_, _) => a(), 150); b.Margin = new Thickness(0, 0, 8, 6); lb.Children.Add(b); return b; }
            LB(Strings.LessonNew_, "LessonNew", NewLesson);
            LB(Strings.ListImportTxt, "ListImportTxt", ImportTxt);
            LB(Strings.ListExportTxt, "ListExportTxt", ExportTxt);
            LB(Strings.ListDelete, "ListDelete", DeleteList);
            LB(Strings.NamesImportButton, "NamesImport", ImportNames);   // plan 10C
            right.Children.Add(lb);
        }

        var erase = Ui.Button(IsClass ? Strings.EraseClassWords : Strings.EraseAllWords, "EraseAllWords", (_, _) => EraseAll(), 220);
        erase.Margin = new Thickness(0, 16, 0, 0);
        erase.HorizontalAlignment = HorizontalAlignment.Left;
        right.Children.Add(erase);
        _status = Ui.Text("", "WordsStatus", 13);
        _status.MaxWidth = 330;
        _status.Margin = new Thickness(0, 8, 0, 0);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        right.Children.Add(_status);

        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Left);
        Children.Add(left);
        Children.Add(right);
        Margin = new Thickness(8);
        AutomationProperties.SetAutomationId(this, "WordEditor");
        // subscribed only while shown: an editor never shown (a tab not visited) or in a closed window holds no reference
        // from the service, so it can be collected
        Loaded += (_, _) => { c.Words.Changed -= OnChanged; c.Words.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => c.Words.Changed -= OnChanged;
        Refresh();
    }

    private void OnChanged(string id)
    {
        if (!IsLoaded) return;
        if (id.Length == 0 || id == _id || id == Profile.ClassId) Dispatcher.BeginInvoke(Refresh);
    }

    private WordBook Book => _c.Words.BookOf(_id);
    private Window? Owner => Window.GetWindow(this);

    private void Say(string text)
    {
        _status.Text = text;
        Ui.Announce(_status);
    }

    private void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var b = Book;
            var q = _search.Text.Trim();
            var keepOwn = (_own.SelectedItem as ListBoxItem)?.Tag as string;
            _own.Items.Clear();
            var own = b.Default?.Words ?? Array.Empty<WordEntry>();
            // « Classe » ∪ machine banned words: marked in « Classe »'s own list, left out of a pupil's view of it
            var banned = _c.Words.BannedWords();
            foreach (var e in own.Where(e => q.Length == 0 || e.W.Contains(q, StringComparison.OrdinalIgnoreCase)))
                _own.Items.Add(Item(IsClass && banned.Contains(e.W) ? string.Format(Strings.WordBannedMark, e.W) : e.W, e.W, "Own-"));
            Select(_own, keepOwn);
            _ownCount.Text = Strings.WordsCount(own.Count, b.WordCount, WordBook.MaxWords(IsClass));

            if (_class != null)
            {
                var keep = (_class.SelectedItem as ListBoxItem)?.Tag as string;
                _class.Items.Clear();
                var cls = _c.Words.ClassBookFor(_id);
                var hidden = new HashSet<string>(b.Masques, StringComparer.Ordinal);
                if (cls != null)
                    foreach (var w in WordLayering.ActiveForms(cls).Distinct(StringComparer.Ordinal).Where(w => q.Length == 0 || w.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (banned.Contains(w)) continue;   // never offered: not shown to a pupil either (reviewer 2026-11-27)
                        _class.Items.Add(Item(hidden.Contains(w) ? string.Format(Strings.WordHiddenMark, w) : w, w, "Class-"));
                    }
                _class.IsEnabled = cls != null;
                Select(_class, keep);
                UpdateHide();
            }

            if (_exclus != null)
            {
                var keepX = (_exclus.SelectedItem as ListBoxItem)?.Tag as string;
                _exclus.Items.Clear();
                foreach (var w in b.Exclus.Where(w => q.Length == 0 || w.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    _exclus.Items.Add(Item(w, w, "Exclu-"));
                // the machine file's words: shown, not removable here (a tag of their own, so a selection survives a refresh)
                foreach (var w in _c.Words.MachineExclus.Where(w => q.Length == 0 || w.Contains(q, StringComparison.OrdinalIgnoreCase)))
                {
                    var it = Item(string.Format(Strings.ExclusMachineMark, w), MachineTag + w, "ExcluMachine-");
                    AutomationProperties.SetAutomationId(it, "ExcluMachine-" + w);
                    it.Foreground = System.Windows.SystemColors.GrayTextBrush;
                    _exclus.Items.Add(it);
                }
                Select(_exclus, keepX);
                UpdateExclusRemove();
            }

            var keepF = (_fautes.SelectedItem as ListBoxItem)?.Tag as string;
            _fautes.Items.Clear();
            foreach (var p in b.Fautes) _fautes.Items.Add(Item(string.Format(Strings.FauteRow, p.Ecrit, p.Mot), p.Ecrit, "Faute-"));
            Select(_fautes, keepF);

            if (_lists != null)
            {
                var keepL = (_lists.SelectedItem as ListBoxItem)?.Tag as string;
                _lists.Items.Clear();
                foreach (var l in b.Lists)
                {
                    var cb = new CheckBox
                    {
                        Content = Strings.ListRow(l.Name, l.Words.Count) + (l.IsLesson ? Strings.ListLessonMark : l.IsNames ? Strings.NamesListMark : ""),
                        IsChecked = l.Active, Tag = l.Id,
                    };
                    // plan 10C (owner Q8): the licence of a names database's pictures is kept and shown
                    if (l.IsNames && l.Licence is { Length: > 0 } lic)
                    {
                        cb.ToolTip = string.Format(Strings.NamesListLicence, lic);
                        AutomationProperties.SetHelpText(cb, string.Format(Strings.NamesListLicence, lic));
                    }
                    AutomationProperties.SetAutomationId(cb, "ListOn-" + l.Id);
                    var id = l.Id;
                    cb.Checked += (_, _) => SetActive(id, true);
                    cb.Unchecked += (_, _) => SetActive(id, false);
                    var it = new ListBoxItem { Content = cb, Tag = l.Id };
                    AutomationProperties.SetAutomationId(it, "List-" + l.Id);
                    AutomationProperties.SetName(it, l.Name);
                    _lists.Items.Add(it);
                }
                Select(_lists, keepL);
            }
        }
        finally { _refreshing = false; }
    }

    private static ListBoxItem Item(string text, string tag, string idPrefix)
    {
        var it = new ListBoxItem { Content = text, Tag = tag };
        AutomationProperties.SetName(it, text);
        AutomationProperties.SetAutomationId(it, idPrefix + tag);
        return it;
    }

    private static void Select(ListBox l, string? tag)
    {
        if (tag is null) return;
        l.SelectedItem = l.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);
    }

    private void UpdateHide()
    {
        if (_hide is null || _class is null) return;
        var w = (_class.SelectedItem as ListBoxItem)?.Tag as string;
        _hide.IsEnabled = w != null;
        _hide.Content = w != null && Book.Masques.Contains(w) ? Strings.WordUnhide : Strings.WordHide;
    }

    private string? Apply(Func<WordBook, (WordBook, WordProblem?)> f, string what)
    {
        var p = _c.Words.Update(_id, f, what);
        return p is { } pr ? Strings.WordProblemText(pr) : null;
    }

    private void AddWord()
    {
        WordDialog.AskWord(Owner, Strings.AddWordTitle, _search.Text.Trim(), _c.Speaker, w =>
        {
            var why = Apply(b => { var r = b.AddWord(null, w, WordEntry.Manual, _c.Words.Now, IsClass, out var p); return (r, p); }, "word added");
            if (why is null) Say(Strings.WordAdded);
            return why;
        });
    }

    private void EditWord()
    {
        if ((_own.SelectedItem as ListBoxItem)?.Tag is not string old) return;
        WordDialog.AskWord(Owner, Strings.EditWordTitle, old, _c.Speaker, w =>
            Apply(b => { var r = b.ReplaceWord(WordBook.DefaultListId, old, w, out var p); return (r, p); }, "word changed"), Strings.WordSave);
    }

    private void DeleteWord()
    {
        if ((_own.SelectedItem as ListBoxItem)?.Tag is not string w) return;
        Apply(b => (b.RemoveWord(WordBook.DefaultListId, w), null), "word deleted");
        Say(Strings.WordDeleted);
    }

    private void ToggleHidden()
    {
        if ((_class?.SelectedItem as ListBoxItem)?.Tag is not string w) return;
        bool hide = !Book.Masques.Contains(w);
        Apply(b => (b.SetHidden(w, hide), null), hide ? "class word hidden" : "class word shown");
        Say(hide ? Strings.WordHiddenDone : Strings.WordShownDone);
    }

    /// <summary>The tag prefix of a machine row (never a word: words hold letters only).</summary>
    private const string MachineTag = "\u0001machine:";

    private void UpdateExclusRemove()
    {
        if (_exclusRemove is null || _exclus is null) return;
        _exclusRemove.IsEnabled = (_exclus.SelectedItem as ListBoxItem)?.Tag is string { Length: > 0 } t && !t.StartsWith(MachineTag, StringComparison.Ordinal);
    }

    private void AddExclu()
    {
        WordDialog.AskWord(Owner, Strings.ExclusAddTitle, _search.Text.Trim(), _c.Speaker, w =>
        {
            var why = Apply(b => { var r = b.AddExclu(w, IsClass, out var p); return (r, p); }, "banned word added");
            if (why is null) Say(Strings.ExclusAdded);
            return why;
        });
    }

    private void RemoveExclu()
    {
        if ((_exclus?.SelectedItem as ListBoxItem)?.Tag is not string { Length: > 0 } w || w.StartsWith(MachineTag, StringComparison.Ordinal)) return;
        Apply(b => (b.RemoveExclu(w), null), "banned word removed");
        Say(Strings.ExclusRemoved);
    }

    private void AddFaute()
    {
        WordDialog.AskPair(Owner, _c.Speaker, (e, m) =>
            Apply(b => { var r = b.SetFaute(e, m, out var p); return (r, p); }, "spelling pair added"));
    }

    private void DeleteFaute()
    {
        if ((_fautes.SelectedItem as ListBoxItem)?.Tag is not string e) return;
        Apply(b => (b.RemoveFaute(e), null), "spelling pair deleted");
    }

    private string? SelectedList => (_lists?.SelectedItem as ListBoxItem)?.Tag as string;

    private void SetActive(string listId, bool on)
    {
        if (_refreshing) return;
        Apply(b => (b.SetActive(listId, on), null), on ? "list on" : "list off");
    }

    private void NewLesson()
    {
        if (Owner is not { } owner || LessonDialog.Ask(owner, _c) is not { } r) return;

        var why = Apply(b => { var nb = b.AddList(r.Name, WordListDef.Lesson, r.Words, WordEntry.Lesson, _c.Words.Now, IsClass, out _, out var p); return (nb, p); },
                        "lesson added");
        Say(why ?? Strings.LessonAdded(r.Words.Count));
    }

    private void ImportTxt()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Strings.TxtFilter, CheckFileExists = true };
        if (dlg.ShowDialog(Owner) != true) return;
        IReadOnlyList<string> words;
        int refused;
        try { (words, refused) = WordTextFile.Read(dlg.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _c.Log?.Invoke($"words: list file not read ({ex.GetType().Name})");
            Say(Strings.TxtUnreadable);
            return;
        }
        if (words.Count == 0) { Say(Strings.TxtNoWords); return; }
        var name = Path.GetFileNameWithoutExtension(dlg.FileName);
        var why = Apply(b => { var nb = b.AddList(name, WordListDef.Lesson, words, WordEntry.Import, _c.Words.Now, IsClass, out _, out var p); return (nb, p); },
                        "list imported");
        Say(why ?? Strings.TxtImported(words.Count, refused));
    }

    private void ExportTxt()
    {
        var b = Book;
        var l = SelectedList is { } id ? b.Get(id) : b.Default;
        if (l is null) { Say(Strings.ListChooseFirst); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Strings.TxtFilter, DefaultExt = WordTextFile.Extension, AddExtension = true, OverwritePrompt = true,
            FileName = string.Join("_", l.Name.Split(Path.GetInvalidFileNameChars())),
        };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            WordTextFile.Write(dlg.FileName, l.Words.Select(e => e.W));
            _c.Log?.Invoke($"words: list {l.Id} of {_id} exported ({l.Words.Count} words)");
            Say(Strings.TxtExported(l.Words.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _c.Log?.Invoke($"words: list export failed ({ex.GetType().Name})");
            Say(Strings.TxtNotWritten);
        }
    }

    /// <summary>Plan 10C: « Importer une base de noms… » into this profile (« Classe » or a pupil).</summary>
    private async void ImportNames()
    {
        var target = IsClass ? Strings.ClassWordsList : (_c.ProfileName(_id) ?? Strings.MyWordsList);
        try
        {
            if (await NamesImportDialog.Run(Owner, _c, _id, target, Say) is { } msg) Say(msg);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _c.Log?.Invoke($"names import: failed ({ex.GetType().Name})");
            Say(string.Format(Strings.NamesImportFailed, Strings.TxtUnreadable));
        }
    }

    private void DeleteList()
    {
        if (SelectedList is not { } id || Book.Get(id) is not { } l) { Say(Strings.ListChooseFirst); return; }
        if (MessageBox.Show(Owner!, Strings.ListDeleteConfirm(l.Name, l.Words.Count), Strings.AppName, MessageBoxButton.YesNo,
                            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Apply(b => (b.RemoveList(id), null), "list deleted");
    }

    private void EraseAll()
    {
        if (MessageBox.Show(Owner!, IsClass ? Strings.EraseClassConfirm : Strings.EraseAllConfirm, Strings.AppName, MessageBoxButton.YesNo,
                            MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var zip = _c.Words.EraseAll(_id);
        Say(zip is null ? Strings.EraseFailed : Strings.EraseDone);
    }
}
