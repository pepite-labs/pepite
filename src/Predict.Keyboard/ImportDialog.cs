using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Predict.Keyboard;

/// <summary>
/// « Importer des profils » (plan 09B step 17, D29): the file's profiles with a checkbox each (ticked when they can be
/// imported); a profile whose first name exists here gets its own choice Remplacer / Garder les deux (default) /
/// Ignorer, and « Pour tous les prénoms déjà présents » sets them all; « Remplacer la classe de ce poste » and « Utiliser
/// le code enseignant du fichier » (only when the file has one) are unticked. The summary is recomputed at each change
/// (<see cref="ProfileArchive.Plan"/>) and asked again before applying; a backup is made first. Modal, owned by the
/// Espace enseignant; every control has an AutomationId.
/// </summary>
internal sealed class ImportDialog : Window
{
    private readonly ArchiveContent _content;
    private readonly ProfileService _svc;
    private readonly TeacherLock? _lock;
    private readonly Func<int, int, int, int, bool>? _isVisible;
    private readonly Action<string>? _log;
    private readonly List<(CheckBox Check, ComboBox? Choice)> _rows = new();
    private readonly CheckBox _replaceClass;
    private readonly CheckBox? _usePin;
    private readonly TextBlock _summary;
    private readonly Button _apply;
    private ImportPlan? _plan;

    /// <summary>The import's result, or null when nothing was imported.</summary>
    public ImportResult? Result { get; private set; }

    private ImportDialog(Window? owner, string fileName, ArchiveContent content, ProfileService svc, TeacherLock? teacherLock, PictureStore? pictures,
                         Func<int, int, int, int, bool>? isVisible, Action<string>? log)
    {
        _content = content;
        _svc = svc;
        _lock = teacherLock;
        _isVisible = isVisible;
        _log = log;
        Ui.Setup(this, Strings.ImportTitle, "ImportDialog");
        if (owner != null) { Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; }

        var root = new StackPanel { Margin = new Thickness(16), MaxWidth = 720 };
        root.Children.Add(Ui.Heading(Strings.ImportHeading, "ImportHeading"));
        var file = Ui.Text(string.Format(Strings.ImportFileLabel, fileName), "ImportFile", 13);
        file.Opacity = 0.8;
        file.Margin = new Thickness(0, 4, 0, 8);
        root.Children.Add(file);

        var list = new StackPanel();
        AutomationProperties.SetName(list, Strings.ImportListName);
        AutomationProperties.SetAutomationId(list, "ImportList");
        var store = svc.Store;
        bool anyConflict = false;
        for (int i = 0; i < content.Pupils.Count; i++)
        {
            var p = content.Pupils[i];
            var existing = ProfileArchive.ConflictOf(store, p);
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(AvatarView.Create(p.Avatar, Initial(p.Name), pictures, 32));
            head.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
            var check = new CheckBox { Content = head, IsChecked = p.Problem is null, IsEnabled = p.Problem is null, VerticalContentAlignment = VerticalAlignment.Center, Width = 250 };
            string status = p.Problem != null ? string.Format(Strings.ImportRowProblem, p.Problem)
                          : existing != null ? Strings.ImportRowExists : Strings.ImportRowNew;
            AutomationProperties.SetName(check, string.Format(Strings.ImportRowName, p.Name, status));
            AutomationProperties.SetAutomationId(check, "ImportRow-" + i);
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            ComboBox? choice = null;
            if (p.Problem is null && existing != null)
            {
                anyConflict = true;
                choice = ChoiceBox("ImportChoice-" + i, string.Format(Strings.ConflictChoiceName, p.Name), ConflictChoice.KeepBoth);
                DockPanel.SetDock(choice, Dock.Right);
                row.Children.Add(choice);
                choice.SelectionChanged += (_, _) => Recompute();
                check.Checked += (_, _) => choice.IsEnabled = true;
                check.Unchecked += (_, _) => choice.IsEnabled = false;
            }
            var st = new TextBlock { Text = status, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 8, 0) };
            row.Children.Add(st);
            check.Checked += (_, _) => Recompute();
            check.Unchecked += (_, _) => Recompute();
            _rows.Add((check, choice));
            list.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = SystemParameters.WorkArea.Height * 0.45, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        root.Children.Add(scroll);

        if (anyConflict)
        {
            var all = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            all.Children.Add(new TextBlock { Text = Strings.ImportForAll, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var forAll = ChoiceBox("ImportForAll", Strings.ImportForAllName, null);
            forAll.SelectionChanged += (_, _) =>
            {
                if (forAll.SelectedItem is not ComboBoxItem { Tag: ConflictChoice c }) return;
                foreach (var (_, ch) in _rows) if (ch != null) Select(ch, c);
            };
            all.Children.Add(forAll);
            root.Children.Add(all);
        }

        _replaceClass = Ui.Check(Strings.ImportReplaceClass, "ImportReplaceClass", false);
        _replaceClass.Margin = new Thickness(0, 12, 0, 0);
        AutomationProperties.SetHelpText(_replaceClass, Strings.ImportReplaceClassHint);
        _replaceClass.Checked += (_, _) => Recompute();
        _replaceClass.Unchecked += (_, _) => Recompute();
        root.Children.Add(_replaceClass);
        root.Children.Add(Hint(Strings.ImportReplaceClassHint));
        if (content.PinProblem != null)
        {
            var refused = Ui.Text(content.PinProblem, "ImportPinRefused", 13);
            refused.Margin = new Thickness(0, 6, 0, 4);
            root.Children.Add(refused);
        }
        else if (content.HasPin && teacherLock != null)
        {
            _usePin = Ui.Check(Strings.ImportUsePin, "ImportUsePin", false);
            AutomationProperties.SetHelpText(_usePin, Strings.ImportUsePinHint);
            _usePin.Checked += (_, _) => Recompute();
            _usePin.Unchecked += (_, _) => Recompute();
            root.Children.Add(_usePin);
            root.Children.Add(Hint(Strings.ImportUsePinHint));
        }

        _summary = Ui.Text("", "ImportSummary", 14);
        _summary.Margin = new Thickness(0, 12, 0, 0);
        _summary.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetLiveSetting(_summary, AutomationLiveSetting.Polite);
        root.Children.Add(_summary);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        _apply = Ui.Button(Strings.ImportApply, "ImportApply", (_, _) => ApplyNow(), 130);
        _apply.IsDefault = true;
        var cancel = Ui.Button(Strings.Close, "ImportCancel", (_, _) => Close(), 110);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0);
        buttons.Children.Add(_apply);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
        Loaded += (_, _) => { Recompute(); (_rows.FirstOrDefault(r => r.Check.IsEnabled).Check ?? (UIElement)_apply).Focus(); };
    }

    /// <summary>Show the dialog for <paramref name="content"/>; returns the result, or null when nothing was imported.</summary>
    public static ImportResult? Show(Window? owner, string fileName, ArchiveContent content, ProfileService svc, TeacherLock? teacherLock,
                                     PictureStore? pictures, Func<int, int, int, int, bool>? isVisible, Action<string>? log)
    {
        var d = new ImportDialog(owner, fileName, content, svc, teacherLock, pictures, isVisible, log);
        log?.Invoke($"import dialog: {content.Pupils.Count} profiles in the file ({content.Pupils.Count(p => p.Problem != null)} unusable), " +
                    $"teacher code {(content.HasPin ? "present" : "absent")}");
        d.ShowDialog();
        return d.Result;
    }

    private static string Initial(string name) => name.Length == 0 ? "?" : new System.Globalization.StringInfo(name).SubstringByTextElements(0, 1).ToUpperInvariant();

    private static TextBlock Hint(string text)
    {
        var t = Ui.Text(text, null, 12.5);
        t.Opacity = 0.75;
        t.Margin = new Thickness(22, 0, 0, 4);
        return t;
    }

    private static ComboBox ChoiceBox(string id, string name, ConflictChoice? selected)
    {
        var cb = new ComboBox { MinWidth = 160, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(cb, id);
        AutomationProperties.SetName(cb, name);
        foreach (var (c, text, tag) in new[] { (ConflictChoice.Replace, Strings.ConflictReplace, "Replace"), (ConflictChoice.KeepBoth, Strings.ConflictKeepBoth, "KeepBoth"),
                                              (ConflictChoice.Skip, Strings.ConflictSkip, "Skip") })
        {
            var it = new ComboBoxItem { Content = text, Tag = c };
            AutomationProperties.SetAutomationId(it, id + "-" + tag);
            cb.Items.Add(it);
        }
        if (selected is { } s) Select(cb, s);
        return cb;
    }

    private static void Select(ComboBox cb, ConflictChoice c) =>
        cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().First(i => (ConflictChoice)i.Tag == c);

    private Dictionary<int, ImportChoice> Choices() =>
        _rows.Select((r, i) => (i, new ImportChoice(r.Check.IsChecked == true,
                                                    r.Choice?.SelectedItem is ComboBoxItem { Tag: ConflictChoice c } ? c : ConflictChoice.KeepBoth)))
             .ToDictionary(x => x.i, x => x.Item2);

    private void Recompute()
    {
        if (_summary is null || _apply is null) return;   // during construction
        _plan = ProfileArchive.Plan(_content, _svc.Store, Choices(), new ImportOptions(_replaceClass.IsChecked == true, _usePin?.IsChecked == true, _isVisible,
                                                                                     _svc.Words is { } ws ? ws.BookOf : null, DateTime.Now));
        _summary.Text = SummaryOf(_plan);
        _apply.IsEnabled = _plan.CanApply;
        Ui.Announce(_summary);
    }

    public static string SummaryOf(ImportPlan p)
    {
        var s = string.Format(Strings.ImportSummary, p.Creates, p.Replaces, p.KeepBoths, p.Skips);
        if (p.ReplaceClass) s += Strings.ImportSummaryClass;
        if (p.ApplyPin) s += Strings.ImportSummaryPin;
        if (p.Excess > 0) s += " " + string.Format(Strings.ImportTooMany, Profile.MaxProfiles, p.Excess);
        return s;
    }

    private void ApplyNow()
    {
        Recompute();
        if (_plan is not { CanApply: true } plan) return;
        if (MessageBox.Show(this, string.Format(Strings.ImportConfirm, SummaryOf(plan)), Strings.ImportTitle, MessageBoxButton.YesNo,
                            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            Result = ProfileArchive.Apply(plan, _svc, _lock, _log);
            Close();
        }
        catch (Exception ex) when (ex is ArchiveException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _log?.Invoke($"import failed ({ex.GetType().Name})");
            MessageBox.Show(this, string.Format(Strings.ImportFailed, ex is ArchiveException ? ex.Message : ""), Strings.ImportTitle,
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
