using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>
/// « Nouvelle leçon… » (plan 10A, Espace enseignant → Mots): a name and a lesson text (pasted, or « Ouvrir un fichier
/// .txt… »); « Chercher les mots » lists its distinct words (<see cref="LessonExtractor"/>) with a box each — ticked: new
/// words and words outside the 300 most frequent; each marked « nouveau » / « connu ». Only the ticked words are kept;
/// <b>the text itself is never stored</b> (it lives in this window only). Code-built, modal.
/// </summary>
internal sealed class LessonDialog : Window
{
    private readonly WordsContext _c;
    private readonly TextBox _name = new() { Width = 360, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _text = new()
    {
        Width = 620, Height = 150, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 4, 0, 0),
    };
    private readonly StackPanel _words = new();
    private readonly TextBlock _count;
    private readonly Button _save;

    public string LessonName => _name.Text.Trim();
    public IReadOnlyList<string> Chosen => _words.Children.OfType<CheckBox>().Where(cb => cb.IsChecked == true).Select(cb => (string)cb.Tag).ToList();

    private LessonDialog(WordsContext c)
    {
        _c = c;
        Ui.Setup(this, Strings.LessonTitle, "LessonDialog");
        var p = new StackPanel { Margin = new Thickness(16) };
        p.Children.Add(new Label { Content = Strings.LessonNameLabel, Padding = new Thickness(0), Target = _name });
        AutomationProperties.SetAutomationId(_name, "LessonName");
        AutomationProperties.SetName(_name, Strings.LessonNameLabel.Replace("_", ""));
        p.Children.Add(_name);
        p.Children.Add(new Label { Content = Strings.LessonTextLabel, Padding = new Thickness(0, 10, 0, 0), Target = _text });
        AutomationProperties.SetAutomationId(_text, "LessonText");
        AutomationProperties.SetName(_text, Strings.LessonTextLabel.Replace("_", ""));
        p.Children.Add(_text);
        var note = Ui.Text(Strings.LessonTextNote, "LessonTextNote", 12.5);
        note.Opacity = 0.75; note.MaxWidth = 620;
        p.Children.Add(note);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(Ui.Button(Strings.LessonOpenTxt, "LessonOpenTxt", (_, _) => OpenTxt(), 200));
        row.Children.Add(Ui.Button(Strings.LessonFind, "LessonFind", (_, _) => Find(), 180));
        p.Children.Add(row);
        _count = Ui.Text("", "LessonCount", 13);
        _count.Margin = new Thickness(0, 10, 0, 4);
        AutomationProperties.SetLiveSetting(_count, AutomationLiveSetting.Polite);
        p.Children.Add(_count);
        AutomationProperties.SetAutomationId(_words, "LessonWords");
        AutomationProperties.SetName(_words, Strings.LessonWordsName);
        p.Children.Add(new ScrollViewer { Content = _words, Height = 220, Width = 620, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        var tick = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        tick.Children.Add(Ui.Button(Strings.LessonTickAll, "LessonTickAll", (_, _) => SetAll(true), 140));
        tick.Children.Add(Ui.Button(Strings.LessonTickNone, "LessonTickNone", (_, _) => SetAll(false), 140));
        p.Children.Add(tick);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _save = Ui.Button(Strings.LessonSave, "LessonSave", (_, _) => { if (Chosen.Count > 0) DialogResult = true; }, 160);
        _save.IsEnabled = false;
        var cancel = Ui.Button(Strings.Cancel, "LessonCancel", (_, _) => DialogResult = false);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0);
        buttons.Children.Add(_save);
        buttons.Children.Add(cancel);
        p.Children.Add(buttons);
        Content = p;
        Loaded += (_, _) => _name.Focus();
    }

    private void OpenTxt()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Strings.TxtFilter, CheckFileExists = true };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > WordTextFile.MaxBytes) { _count.Text = Strings.TxtTooBig; return; }
            var bytes = File.ReadAllBytes(dlg.FileName);
            // the same decoding as a word list (UTF-8, else Windows-1252), kept whole here: it is a text, not a list
            string text;
            try { text = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
            catch (System.Text.DecoderFallbackException)
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                text = System.Text.Encoding.GetEncoding(1252).GetString(bytes);
            }
            _text.Text = text;
            if (LessonName.Length == 0) _name.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            Find();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _c.Log?.Invoke($"lesson: text file not read ({ex.GetType().Name})");
            _count.Text = Strings.TxtUnreadable;
        }
    }

    private void Find()
    {
        _words.Children.Clear();
        var found = LessonExtractor.Extract(_text.Text, _c.Knows, _c.Frequent());
        foreach (var w in found)
        {
            var cb = Ui.Check(w.Form + "  —  " + (w.IsNew ? Strings.LessonNew : Strings.LessonKnown), "LessonWord-" + _words.Children.Count, w.Ticked);
            cb.Tag = w.Form;
            cb.Margin = new Thickness(0, 2, 0, 0);
            cb.Checked += (_, _) => Recount();
            cb.Unchecked += (_, _) => Recount();
            _words.Children.Add(cb);
        }
        _c.Log?.Invoke($"lesson: {found.Count} words found ({found.Count(w => w.IsNew)} new, {found.Count(w => w.Ticked)} ticked)");
        Recount();
        Ui.Announce(_count);
    }

    private void SetAll(bool on)
    {
        foreach (var cb in _words.Children.OfType<CheckBox>()) cb.IsChecked = on;
        Recount();
    }

    private void Recount()
    {
        int n = _words.Children.Count, t = Chosen.Count;
        _count.Text = n == 0 ? Strings.LessonNoWords : Strings.LessonCount(n, t);
        _save.IsEnabled = t > 0;
    }

    /// <summary>The lesson's name and its ticked words, or null (cancelled).</summary>
    public static (string Name, IReadOnlyList<string> Words)? Ask(Window owner, WordsContext c)
    {
        var d = new LessonDialog(c) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        if (d.ShowDialog() != true) return null;
        return (d.LessonName, d.Chosen);
    }
}
