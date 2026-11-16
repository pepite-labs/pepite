using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Predict.Keyboard;

/// <summary>What the word windows need from the app (plan 10A).</summary>
/// <param name="Knows">is a (lowercase) word in the lexicon (the lesson dialog's « nouveau » / « connu »).</param>
/// <param name="Frequent">the 300 most frequent lexicon forms (unticked in a lesson).</param>
/// <param name="ProfileName">the shown name of a profile (null = no name: single user).</param>
internal sealed record WordsContext(WordListService Words, Func<string, bool> Knows, Func<System.Collections.Generic.IReadOnlySet<string>> Frequent,
                                    Speaker? Speaker, Func<string, string?> ProfileName, Action<string>? Log);

/// <summary>
/// The small word dialogs (plan 10A, code-built, every control with an AutomationId): one word (« Ajouter un mot… » from
/// the tray — no PIN, Q3 — and the editors' Ajouter… / Modifier…), a « Mes fautes » pair. « Écouter » says the word
/// (never logged: it may be a first name). Modal; the check runs on « Ajouter » and its French reason stays in the dialog.
/// </summary>
internal sealed class WordDialog : Window
{
    private readonly TextBox _word = new() { Width = 300, FontSize = 18, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox? _target;
    private readonly TextBlock _error;
    private readonly Func<string, string?, string?> _commit;

    /// <param name="commit">(word, target) → null when stored, else the French reason (the dialog stays open).</param>
    private WordDialog(string title, string label, string initial, string? targetLabel, string? targetInitial, string okText, Speaker? speaker,
                       Func<string, string?, string?> commit, string? cancelText = null)
    {
        _commit = commit;
        Ui.Setup(this, title, "WordDialog");
        var p = new StackPanel { Margin = new Thickness(16) };
        var l1 = new Label { Content = label, Padding = new Thickness(0), Target = _word };
        p.Children.Add(l1);
        _word.Text = initial;
        AutomationProperties.SetAutomationId(_word, "WordText");
        AutomationProperties.SetName(_word, StripAccessKey(label));
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_word);
        if (speaker != null)
        {
            var listen = Ui.Button(Strings.WordListen, "WordListen", (_, _) => { var w = _word.Text.Trim(); if (w.Length > 0) speaker.SayName(w); }, 90);
            listen.Margin = new Thickness(10, 4, 0, 0);
            row.Children.Add(listen);
        }
        p.Children.Add(row);
        if (targetLabel != null)
        {
            _target = new TextBox { Width = 300, FontSize = 18, Margin = new Thickness(0, 4, 0, 0), Text = targetInitial ?? "" };
            AutomationProperties.SetAutomationId(_target, "WordTarget");
            AutomationProperties.SetName(_target, StripAccessKey(targetLabel));
            p.Children.Add(new Label { Content = targetLabel, Padding = new Thickness(0, 10, 0, 0), Target = _target });
            p.Children.Add(_target);
        }
        var hint = Ui.Text(targetLabel != null ? Strings.FauteHint : Strings.WordHint, "WordHint", 12.5);
        hint.Opacity = 0.75; hint.MaxWidth = 420; hint.Margin = new Thickness(0, 8, 0, 0);
        p.Children.Add(hint);
        _error = Ui.Text("", "WordError", 13);
        _error.Foreground = System.Windows.Media.Brushes.DarkRed;
        _error.MaxWidth = 420;
        _error.Margin = new Thickness(0, 6, 0, 0);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        p.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = Ui.Button(okText, "WordOk", (_, _) => Commit());
        ok.IsDefault = true;
        var cancel = Ui.Button(cancelText ?? Strings.Cancel, "WordCancel", (_, _) => { DialogResult = false; });
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        p.Children.Add(buttons);
        Content = p;
        Loaded += (_, _) => { _word.Focus(); _word.SelectAll(); };
    }

    private static string StripAccessKey(string s) => s.Replace("_", "");

    private void Commit()
    {
        var why = _commit(_word.Text, _target?.Text);
        if (why is null) { DialogResult = true; return; }
        _error.Text = why;
        Ui.Announce(_error);
        _word.Focus();
    }

    /// <summary>One word: <paramref name="commit"/> stores it (null) or says why not.</summary>
    public static bool AskWord(Window? owner, string title, string initial, Speaker? speaker, Func<string, string?> commit, string? okText = null,
                               string? cancelText = null)
    {
        var d = new WordDialog(title, Strings.WordLabel, initial, null, null, okText ?? Strings.WordAdd, speaker, (w, _) => commit(w), cancelText);
        if (owner != null && owner.IsVisible) { d.Owner = owner; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else d.Topmost = true;
        return d.ShowDialog() == true;
    }

    /// <summary>A « Mes fautes » pair: « J'écris… » → « Je veux écrire… ».</summary>
    public static bool AskPair(Window? owner, Speaker? speaker, Func<string, string, string?> commit)
    {
        var d = new WordDialog(Strings.FauteTitle, Strings.FauteWritten, "", Strings.FauteWanted, "", Strings.WordAdd, speaker,
                               (w, t) => commit(w, t ?? ""));
        if (owner != null && owner.IsVisible) { d.Owner = owner; d.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        return d.ShowDialog() == true;
    }

    /// <summary>Plan 10B: the « ? » spelling marker — « Ajouter « mot » à mes mots ? » (Oui / Non), the word filled in and still
    /// editable; into the active profile's « Mes mots » (the caller applied the PIN rule of Q3). The word is never logged.</summary>
    public static void AddSpelled(WordsContext c, string activeId, string word)
    {
        c.Log?.Invoke($"words: add-word dialog opened from the spelling marker ({activeId})");
        AskWord(null, Strings.AddSpelledTitle(word), word, c.Speaker, w =>
        {
            var p = c.Words.AddWord(activeId, w);
            return p is { } pr ? Strings.WordProblemText(pr) : null;
        }, Strings.AddSpelledOk, Strings.AddSpelledNo);
    }

    /// <summary>The tray's « Ajouter un mot… » (Q3: no PIN, adding only): into the active profile's « Mes mots ».</summary>
    public static void AddToActive(WordsContext c, string activeId)
    {
        var name = c.ProfileName(activeId);
        var title = name is null ? Strings.AddWordTitle : string.Format(Strings.AddWordTitleFor, name);
        c.Log?.Invoke($"words: add-word dialog opened ({activeId})");
        AskWord(null, title, "", c.Speaker, w =>
        {
            var p = c.Words.AddWord(activeId, w);
            return p is { } pr ? Strings.WordProblemText(pr) : null;
        });
    }
}
