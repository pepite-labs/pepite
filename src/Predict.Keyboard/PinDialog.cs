using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Predict.Keyboard;

/// <summary>
/// The teacher-PIN dialog (plan 09 step 8): <see cref="Ask"/> checks the PIN for a reason given as a sentence, with a
/// digits-only <see cref="PasswordBox"/> (typed, pasted digits only) and an on-screen 0–9 keypad, the back-off wait as a
/// live region (a screen reader announces it), and « Code oublié ? » → the recovery code → a new PIN (+ a new recovery
/// code, shown once). <see cref="Create"/> chooses a new PIN (twice). A modal window of our own: the hook neither tracks
/// nor logs what is typed in it (Native.IsOwnWindow), the PIN is never logged. PBKDF2 runs on a worker thread.
/// </summary>
internal sealed class PinDialog : Window
{
    private enum Mode { Verify, Recovery, Create }

    private readonly TeacherLock _lock;
    private readonly Action<string>? _log;
    private Mode _mode;
    private readonly StackPanel _body = new();
    private readonly TextBlock _message;
    private PasswordBox _box = new(), _confirm = new();
    private TextBox _recovery = new();
    private PasswordBox? _lastFocused;
    private Button _ok = new();
    private DispatcherTimer? _countdown;
    private bool _busy;

    /// <summary>Verify: true = right PIN. Create: the new recovery code (null = cancelled).</summary>
    public bool Verified { get; private set; }
    public string? NewRecoveryCode { get; private set; }
    /// <summary>Verify: the teacher used the recovery code and set a new PIN instead.</summary>
    public bool Recovered { get; private set; }

    private readonly string _reasonText;

    private PinDialog(TeacherLock teacherLock, Mode mode, string reasonText, Action<string>? log)
    {
        _lock = teacherLock;
        _log = log;
        _reasonText = reasonText;
        Ui.Setup(this, mode == Mode.Create ? Strings.PinCreateTitle : Strings.PinTitle, "PinDialog");
        _message = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x34, 0x12)), TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380, Visibility = Visibility.Collapsed,
        };
        AutomationProperties.SetAutomationId(_message, "PinMessage");
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Assertive);
        Content = new Border { Padding = new Thickness(22, 18, 22, 18), Child = _body };
        Build(mode);
        Loaded += (_, _) => { _box.Focus(); StartWaitIfNeeded(); };
        Closed += (_, _) => _countdown?.Stop();
    }

    public enum AskOutcome { Cancelled, Verified, Recovered }

    /// <summary>Ask the PIN; <paramref name="ok"/> runs when it was right (or after a recovery + new PIN).</summary>
    public static void Ask(TeacherLock teacherLock, PinReason reason, Action ok, Action<string>? log, string? reasonText = null)
    {
        if (AskOutcomeOf(teacherLock, reason, log, reasonText) != AskOutcome.Cancelled) ok();
    }

    /// <summary>Ask the PIN and say how it ended: <see cref="AskOutcome.Recovered"/> = « Code oublié ? » was used and a
    /// new PIN (and recovery code, already shown) is set — a « Changer le code » caller must not ask for one again.</summary>
    public static AskOutcome AskOutcomeOf(TeacherLock teacherLock, PinReason reason, Action<string>? log, string? reasonText = null)
    {
        var d = new PinDialog(teacherLock, Mode.Verify, reasonText ?? Strings.PinReasonText(reason), log);
        log?.Invoke($"pin dialog: {reason}");
        ShowModal(d);
        if (d.Recovered && d.NewRecoveryCode is { } code) RecoveryCodeWindow.ShowModal(code, teacherLock.SetAtUtc ?? DateTime.UtcNow, log);
        if (d.Recovered) return AskOutcome.Recovered;
        if (d.Verified) return AskOutcome.Verified;
        log?.Invoke($"pin dialog: {reason} cancelled");
        return AskOutcome.Cancelled;
    }

    /// <summary>Choose a new PIN; <paramref name="commit"/> stores it (on a worker thread) and returns the recovery code,
    /// which is then shown once. Returns false when cancelled.</summary>
    public static bool Create(TeacherLock teacherLock, Func<string, string> commit, Action<string>? log)
    {
        var d = new PinDialog(teacherLock, Mode.Create, Strings.PinCreateText, log) { _commit = commit };
        ShowModal(d);
        if (d.NewRecoveryCode is not { } code) return false;
        RecoveryCodeWindow.ShowModal(code, teacherLock.SetAtUtc ?? DateTime.UtcNow, log);
        return true;
    }

    private Func<string, string>? _commit;

    private static void ShowModal(Window d)
    {
        d.Topmost = true;
        d.Loaded += (_, _) => d.Dispatcher.BeginInvoke(() => d.Topmost = false, DispatcherPriority.ApplicationIdle);
        d.ShowDialog();
    }

    // ------------------------------------------------------------------ layout
    private void Build(Mode mode)
    {
        _mode = mode;
        _body.Children.Clear();
        _message.Visibility = Visibility.Collapsed;
        var reason = Ui.Text(mode == Mode.Recovery ? Strings.RecoveryPrompt : _reasonText, "PinReason", 15);
        reason.MaxWidth = 380;
        _body.Children.Add(reason);
        if (mode == Mode.Recovery)
        {
            _recovery = new TextBox { FontFamily = new FontFamily("Consolas"), FontSize = 20, MaxLength = 20, Margin = new Thickness(0, 12, 0, 0), Width = 260,
                                      HorizontalAlignment = HorizontalAlignment.Left, CharacterCasing = CharacterCasing.Upper };
            AutomationProperties.SetAutomationId(_recovery, "RecoveryBox");
            AutomationProperties.SetName(_recovery, Strings.RecoveryLabel);
            _body.Children.Add(_recovery);
            var note = Ui.Text(Strings.RecoveryNoCode, "RecoveryNoCode", 13);
            note.Margin = new Thickness(0, 10, 0, 0); note.MaxWidth = 380; note.Opacity = 0.8;
            _body.Children.Add(note);
            _body.Children.Add(_message);
            _body.Children.Add(Buttons(includeForgot: false));
            _recovery.Focus();
            return;
        }
        _box = MakeBox(mode == Mode.Create ? Strings.PinNewLabel : Strings.PinLabel, "PinBox");
        _body.Children.Add(Labelled(mode == Mode.Create ? Strings.PinNewLabel : Strings.PinLabel, _box));
        if (mode == Mode.Create)
        {
            _confirm = MakeBox(Strings.PinConfirmLabel, "PinConfirmBox");
            _body.Children.Add(Labelled(Strings.PinConfirmLabel, _confirm));
        }
        _body.Children.Add(Keypad());
        _body.Children.Add(_message);
        _body.Children.Add(Buttons(includeForgot: mode == Mode.Verify));
        _lastFocused = _box;
    }

    private static UIElement Labelled(string label, PasswordBox box)
    {
        var p = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        p.Children.Add(new Label { Content = label, Padding = new Thickness(0, 0, 0, 4), Target = box });
        p.Children.Add(box);
        return p;
    }

    private PasswordBox MakeBox(string name, string id)
    {
        var b = new PasswordBox { MaxLength = TeacherLock.MaxDigits, FontSize = 24, Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, name);
        InputMethod.SetIsInputMethodEnabled(b, false);
        b.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(c => c is >= '0' and <= '9');
        b.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Space) e.Handled = true; };   // Space bypasses TextInput
        DataObject.AddPastingHandler(b, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string t || !t.Trim().All(c => c is >= '0' and <= '9')) e.CancelCommand();
        });
        b.GotKeyboardFocus += (_, _) => _lastFocused = b;
        return b;
    }

    private UIElement Keypad()
    {
        var g = new UniformGrid4x3();
        for (int i = 1; i <= 9; i++) g.Add(Key(i.ToString()));
        g.Add(KeyButton(Strings.PinErase, "PinErase", Strings.PinEraseName, () =>
        {
            var b = _lastFocused ?? _box;
            if (b.Password.Length > 0) SetPassword(b, b.Password.Substring(0, b.Password.Length - 1));
        }));
        g.Add(Key("0"));
        g.Add(new Border());
        return g.Panel;
    }

    private Button Key(string d) => KeyButton(d, "PinKey" + d, string.Format(Strings.PinKeyName, d), () =>
    {
        var b = _lastFocused ?? _box;
        if (b.Password.Length < TeacherLock.MaxDigits) SetPassword(b, b.Password + d);
    });

    private Button KeyButton(string text, string id, string name, Action press)
    {
        var b = new Button { Content = text, Width = 72, Height = 44, Margin = new Thickness(0, 6, 6, 0), FontSize = text.Length > 1 ? 13 : 20,
                             Focusable = false };
        AutomationProperties.SetAutomationId(b, id);
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) => { if (!_busy) press(); (_lastFocused ?? _box).Focus(); };
        return b;
    }

    /// <summary>Set the box and put the caret at the end (PasswordBox has no public caret API).</summary>
    private static void SetPassword(PasswordBox b, string value)
    {
        b.Password = value;
        try { b.GetType().GetMethod("Select", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(b, new object[] { value.Length, 0 }); }
        catch (Exception) { }
    }

    private UIElement Buttons(bool includeForgot)
    {
        var row = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        if (includeForgot)
        {
            var forgot = new Button { Content = Strings.PinForgot, Padding = new Thickness(6, 2, 6, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                                      Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x56, 0xDB)), Cursor = Cursors.Hand };
            AutomationProperties.SetAutomationId(forgot, "PinForgot");
            forgot.Click += (_, _) => { if (!_busy) { _log?.Invoke("pin dialog: recovery code"); Build(Mode.Recovery); } };
            DockPanel.SetDock(forgot, Dock.Left);
            row.Children.Add(forgot);
        }
        var cancel = Ui.Button(Strings.Cancel, "PinCancel", (_, _) => Close(), 96);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0);
        _ok = Ui.Button(Strings.Ok, "PinOk", async (_, _) => await OnOk(), 96);
        _ok.IsDefault = true;
        DockPanel.SetDock(cancel, Dock.Right);
        DockPanel.SetDock(_ok, Dock.Right);
        row.Children.Add(cancel);
        row.Children.Add(_ok);
        return row;
    }

    // ------------------------------------------------------------------ actions
    private void Say(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        Ui.Announce(_message);
    }

    private void Busy(bool on)
    {
        _busy = on;
        _ok.IsEnabled = !on;
        _box.IsEnabled = _confirm.IsEnabled = _recovery.IsEnabled = !on;
        Cursor = on ? Cursors.Wait : null;
    }

    private async Task OnOk()
    {
        if (_busy) return;
        switch (_mode)
        {
            case Mode.Create:
            {
                var pin = _box.Password;
                var why = TeacherLock.Check(pin) ?? (pin != _confirm.Password ? PinProblem.Mismatch : null);
                if (why is { } p) { Say(Strings.PinProblemText(p)); _log?.Invoke($"pin dialog: new PIN refused ({p})"); return; }
                Busy(true);
                Say(Strings.PinChecking);
                var commit = _commit ?? (x => _lock.SetPin(x));
                try { NewRecoveryCode = await Task.Run(() => commit(pin)); }
                finally { Busy(false); }
                Recovered = _fromRecovery;
                Close();
                return;
            }
            case Mode.Recovery:
            {
                var code = _recovery.Text;
                if (TeacherLock.NormalizeRecovery(code).Length == 0) return;
                Busy(true);
                Say(Strings.PinChecking);
                PinResult r;
                try { r = await Task.Run(() => _lock.Verify(code, recovery: true)); }
                finally { Busy(false); }
                if (r.Outcome == PinOutcome.Ok)
                {
                    // a new PIN now (and a new recovery code, shown by the caller)
                    _commit = x => _lock.SetPin(x);
                    _fromRecovery = true;
                    Title = Strings.PinCreateTitle;
                    BuildCreateAfterRecovery();
                    return;
                }
                Show(r, recovery: true);
                return;
            }
            default:
            {
                var pin = _box.Password;
                if (pin.Length == 0) return;
                Busy(true);
                Say(Strings.PinChecking);
                PinResult r;
                try { r = await Task.Run(() => _lock.Verify(pin)); }
                finally { Busy(false); }
                if (r.Outcome is PinOutcome.Ok or PinOutcome.NoPin) { Verified = true; Close(); return; }
                SetPassword(_box, "");
                _box.Focus();
                Show(r, recovery: false);
                return;
            }
        }
    }

    private bool _fromRecovery;

    private void BuildCreateAfterRecovery()
    {
        var text = Strings.PinCreateText;
        _body.Children.Clear();
        var reason = Ui.Text(text, "PinReason", 15);
        reason.MaxWidth = 380;
        _body.Children.Add(reason);
        _mode = Mode.Create;
        _box = MakeBox(Strings.PinNewLabel, "PinBox");
        _confirm = MakeBox(Strings.PinConfirmLabel, "PinConfirmBox");
        _body.Children.Add(Labelled(Strings.PinNewLabel, _box));
        _body.Children.Add(Labelled(Strings.PinConfirmLabel, _confirm));
        _body.Children.Add(Keypad());
        _message.Visibility = Visibility.Collapsed;
        _body.Children.Add(_message);
        _body.Children.Add(Buttons(includeForgot: false));
        _lastFocused = _box;
        _box.Focus();
    }

    private void Show(PinResult r, bool recovery)
    {
        if (r.WaitSeconds > 0) { StartWait(r.WaitSeconds); return; }
        Say(recovery ? Strings.RecoveryWrong : r.Failures >= 2 ? string.Format(Strings.PinWrongCount, r.Failures) : Strings.PinWrong);
    }

    private void StartWaitIfNeeded()
    {
        int w = _lock.WaitRemaining();
        if (w > 0) StartWait(w);
    }

    /// <summary>The back-off: input disabled, « Réessayez dans N secondes » counting down (announced at the start and
    /// every 10 s, not every second).</summary>
    private void StartWait(int seconds)
    {
        _countdown?.Stop();
        var until = DateTime.UtcNow.AddSeconds(seconds);
        Busy(true);
        Cursor = null;
        Say(string.Format(Strings.PinWait, Strings.Duration(seconds)));
        int lastAnnounced = seconds;
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            int left = (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds);
            if (left <= 0)
            {
                _countdown!.Stop();
                Busy(false);
                _message.Visibility = Visibility.Collapsed;
                (_mode == Mode.Recovery ? (UIElement)_recovery : _box).Focus();
                return;
            }
            _message.Text = string.Format(Strings.PinWait, Strings.Duration(left));
            if (lastAnnounced - left >= 10) { lastAnnounced = left; Ui.Announce(_message); }
        };
        _countdown.Start();
    }

    /// <summary>A 3-column keypad grid (1 2 3 / 4 5 6 / 7 8 9 / Effacer 0 ·).</summary>
    private sealed class UniformGrid4x3
    {
        public readonly System.Windows.Controls.Primitives.UniformGrid Panel = new() { Columns = 3, Rows = 4, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        public void Add(UIElement e) => Panel.Children.Add(e);
    }
}

/// <summary>The recovery code, shown once after a PIN is created or changed (plan 09 step 7): big, selectable, with
/// « Imprimer… » and « Copier ».</summary>
internal sealed class RecoveryCodeWindow : Window
{
    public static void ShowModal(string code, DateTime createdUtc, Action<string>? log)
    {
        var w = new RecoveryCodeWindow(code, createdUtc, log);
        w.Topmost = true;
        w.Loaded += (_, _) => w.Dispatcher.BeginInvoke(() => w.Topmost = false, DispatcherPriority.ApplicationIdle);
        w.ShowDialog();
        log?.Invoke("recovery code window closed");
    }

    private RecoveryCodeWindow(string code, DateTime createdUtc, Action<string>? log)
    {
        Ui.Setup(this, Strings.RecoveryTitle, "RecoveryWindow");
        var body = new StackPanel { Margin = new Thickness(22, 18, 22, 18), MaxWidth = 460 };
        body.Children.Add(Ui.Heading(Strings.RecoveryHeading, "RecoveryHeading"));
        var t = Ui.Text(Strings.RecoveryText, "RecoveryText");
        t.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(t);
        var box = new TextBox
        {
            Text = code, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 30, Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
        };
        AutomationProperties.SetAutomationId(box, "RecoveryCode");
        AutomationProperties.SetName(box, Strings.RecoveryHeading);
        body.Children.Add(box);
        var status = Ui.Text("", "RecoveryStatus", 13);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        row.Children.Add(Ui.Button(Strings.RecoveryPrint, "RecoveryPrint", (_, _) => Print(code, createdUtc, log)));
        row.Children.Add(Ui.Button(Strings.RecoveryCopy, "RecoveryCopy", (_, _) =>
        {
            try { Clipboard.SetText(code); status.Text = Strings.RecoveryCopied; Ui.Announce(status); }
            catch (Exception) { }
        }));
        var done = Ui.Button(Strings.RecoveryDone, "RecoveryDone", (_, _) => Close(), 150);
        done.IsDefault = true;
        row.Children.Add(done);
        body.Children.Add(row);
        status.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(status);
        Content = body;
        Loaded += (_, _) => done.Focus();
    }

    private static void Print(string code, DateTime createdUtc, Action<string>? log)
    {
        try
        {
            var pd = new PrintDialog();
            if (pd.ShowDialog() != true) return;
            var page = new StackPanel { Margin = new Thickness(60) };
            page.Children.Add(new TextBlock { Text = Strings.RecoveryPrintHeading, FontFamily = new FontFamily("Segoe UI"), FontSize = 20, FontWeight = FontWeights.SemiBold });
            page.Children.Add(new TextBlock { Text = code, FontFamily = new FontFamily("Consolas"), FontSize = 36, Margin = new Thickness(0, 24, 0, 0) });
            page.Children.Add(new TextBlock { Text = Strings.RecoveryPrintDate(createdUtc.ToLocalTime()), FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Margin = new Thickness(0, 16, 0, 0) });
            page.Children.Add(new TextBlock { Text = Strings.RecoveryText, FontFamily = new FontFamily("Segoe UI"), FontSize = 14, TextWrapping = TextWrapping.Wrap,
                                              Margin = new Thickness(0, 16, 0, 0), MaxWidth = 500 });
            var size = new Size(pd.PrintableAreaWidth, pd.PrintableAreaHeight);
            page.Measure(size);
            page.Arrange(new Rect(size));
            pd.PrintVisual(page, Strings.RecoveryTitle);
            log?.Invoke("recovery code printed");
        }
        catch (Exception ex) { log?.Invoke($"recovery code: print failed ({ex.GetType().Name})"); }
    }
}
