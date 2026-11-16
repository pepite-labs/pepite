using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>
/// Entry point: migrates and loads the profiles (plan 09) and the settings of the active one (+ read-only machine layer), the prediction DB, the speech thread,
/// the picture pack and the bundled fonts, shows the (hidden-until-needed) suggestion bar, installs the hooks and
/// the tray icon (Pause/Reprendre · Ajouter un mot… · Réglages… · À propos… · Quitter) and the personal words (plan 10A).
/// Usage: Pepite.exe [path\to\prediction.sqlite] [--log] [--exit-after &lt;s&gt;] [--pictos &lt;pack&gt;]
///                           [--no-pictures] [--no-promote] [--autostart] [--no-welcome]
///        (--autostart: started with Windows — the future installer's Run key passes it: a notification instead of the
///         welcome window; --no-welcome: scripted tests, no welcome window)
///        Pepite.exe --quit   (asks the running instance to exit normally; for scripted tests)
///        Pepite.exe --render-bar &lt;out.png&gt; [--dpi 96|144|192] [--pictos &lt;pack&gt;] [--no-pictures]
///                           [--size S|M|L|XL] [--no-keys] [--font segoe-ui|opendyslexic|lexend|andika]
///                           [--spacing normal|wider|widest] word1 word2 …
///                           (draws the bar into a PNG and exits; installs no hook, injects nothing)
///        Pepite.exe --speech-bench &lt;outDir&gt; [--profile normal|2core] [--audio device|null] [--voices &lt;dir&gt;]
///                           [--sentences &lt;file&gt;] [--volume 0..100] [--voice siwis|pierre] [--switch]
///                           [--wav &lt;dir&gt; [--items L01,…] [--variant ns,ls,nw] [--word &lt;text&gt; [--word-reps N] [--word-cache off]]
///                                 [--word-items &lt;file&gt; [--word-suffix .] [--carrier "…{0}…"] [--phoneme-fix a=b;…]]]
///                           (plans 08a/08b: Piper speech latency / highlight / footprint bench; no hook, no UI)
/// </summary>
public partial class App : Application
{
    private PredictionEngine? _engine;
    private KeyboardController? _ctl;
    private Speaker? _speaker;
    private Tray? _tray;
    private PictureStore? _pictures;
    private SettingsService? _settings;
    private SettingsStore? _appState;
    private ProfileStore? _profiles;
    private TeacherLock? _teacherLock;
    private TeacherMode? _mode;
    private SessionWatcher? _session;
    private DispatcherTimer? _idleTimer;
    private readonly SessionPolicy _sessionPolicy = new();
    private ProfileService? _profileSvc;
    private WordListService? _words;
    private NamesPack? _namesPack;   // plan 10C: set on the UI thread once loaded (null = none)
    private DateTime? _logon;
    private WaitHandle? _quit, _activate, _open;
    private SingleInstance? _instance;
    private volatile bool _exiting;
    private Action? _openSettings;
    private AsyncLog? _logWriter;
    private System.Windows.Forms.NativeWindow? _clip;
    /// <summary>Plan 12: a <c>.pepite</c> import asked before start-up ended (first start with <c>--import</c>, or an Open
    /// request while loading): handled once the app is ready.</summary>
    private Action<string>? _importFile;
    private string? _pendingImport;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        if (args.Contains(CommandLine.CheckConfig))
        {
            // plan 12: the installer validates a school's machine file — no UI, no hook, no log, nothing written
            int rc = MachineLayer.Check(Option(args, CommandLine.CheckConfig), File.ReadAllText, out _);
            Shutdown(rc);
            return;
        }
        if (args.Contains("--quit"))
        {
            // scripted teardown: signal the running instance, which then exits through OnExit (hooks, tray removed)
            SingleInstance.Signal(SingleInstance.QuitName);
            Shutdown(0);
            return;
        }
        if (args.Contains("--log")) _logWriter = new AsyncLog(Path.Combine(Path.GetTempPath(), "predict-keyboard.log"));
        Action<string>? log = _logWriter is { } lw ? lw.Write : null;
        var positional = Positional(args);

        if (Option(args, "--render-bar") is { } outPng) { int rc = RenderBar(args, outPng, positional, log); _logWriter?.Dispose(); Shutdown(rc); return; }
        if (Option(args, "--speech-bench") is { } benchDir)
        {
            int rc;
            try { rc = SpeechBench.Run(args, benchDir, Option); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(benchDir, "bench-error.txt"), ex.ToString()); rc = 2; }
            _logWriter?.Dispose();
            Shutdown(rc);
            return;
        }

        // plan 12: how this copy was started and installed (no path, no user name)
        log?.Invoke($"start: elevated={(InstallInfo.Elevated() ? "yes" : "no")}, install={InstallInfo.Name(InstallInfo.Current())}");

        // one copy per session: a second start asks the running copy to say « déjà démarré » and exits
        var kind = StartupPolicy.KindOf(args);
        // plan 12 (Q3): a double-clicked .pepite file; never taken as the prediction database (CommandLine.ValueOptions)
        var importArg = Option(args, CommandLine.Import);
        if (args.Contains(CommandLine.Import)) log?.Invoke(importArg is null ? "import: --import without a file" : "import: requested at start");
        _instance = SingleInstance.TryAcquire(log);
        if (_instance is null)
        {
            var outcome = StartupPolicy.SecondStartOutcome.Failed;
            // an import goes to the running copy through ouvrir.txt + the Open event; otherwise « déjà démarré »
            bool asImport = importArg != null && OpenRequest.Write(OpenRequest.DefaultDir, importArg);
            if (importArg != null) log?.Invoke(asImport ? "second start: import request written" : "second start: import request could not be written");
            if (StartupPolicy.OnSecondStart(kind) == SecondStartAction.ExitSilently) log?.Invoke("second start (autostart): exiting silently");
            else
            {
                // the winner may not have created its event yet, or may be exiting: retry the signal and the mutex ~4 s
                var signalName = asImport ? SingleInstance.OpenName : SingleInstance.ActivateName;
                outcome = StartupPolicy.ResolveSecondStart(() => (_instance = SingleInstance.TryAcquire(log)) != null,
                                                           () => SingleInstance.Signal(signalName), Thread.Sleep);
                log?.Invoke($"second start: {outcome}");
                if (outcome == StartupPolicy.SecondStartOutcome.Failed)
                    // the running copy cannot be signalled (another user in this session): say it ourselves
                    new WelcomeWindow(WelcomeWindow.Mode.AlreadyRunning, null, log).ShowDialog();
            }
            if (outcome != StartupPolicy.SecondStartOutcome.Acquired)
            {
                _logWriter?.Dispose();
                Shutdown(0);
                return;
            }
            log?.Invoke("second start: the other copy has exited; starting normally");
            // the request file we wrote is ours again: take it back (the path is still in importArg)
            if (importArg != null) OpenRequest.Take(OpenRequest.DefaultDir);
        }
        if (importArg != null) _pendingImport = importArg;
        // listen from the start: a request while loading is honoured once startup ends (the dispatcher runs it then)
        var activate = _activate = SingleInstance.CreateEvent(SingleInstance.ActivateName);
        new Thread(() =>
        {
            while (true)
            {
                try { activate.WaitOne(); } catch { return; }
                if (_exiting) return;
                log?.Invoke("activation: second start");
                Dispatcher.BeginInvoke(() =>
                {
                    if (_openSettings is { } open) WelcomeWindow.ShowSingle(WelcomeWindow.Mode.AlreadyRunning, null, open, log);
                });
            }
        }) { IsBackground = true, Name = "activate-listener" }.Start();

        // plan 12: a .pepite double-clicked while we run — take ouvrir.txt and import it (queued until startup ends)
        var open = _open = SingleInstance.CreateEvent(SingleInstance.OpenName);
        new Thread(() =>
        {
            while (true)
            {
                try { open.WaitOne(); } catch { return; }
                if (_exiting) return;
                var path = OpenRequest.Take(OpenRequest.DefaultDir);
                log?.Invoke(path is null ? "import: open request without a file" : "import: open request");
                if (path is null) continue;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_importFile is { } import) import(path);
                    else _pendingImport = path;
                });
            }
        }) { IsBackground = true, Name = "open-listener" }.Start();

        // listen for --quit from the start: a quit requested while loading is honoured as soon as startup ends
        _quit = SingleInstance.CreateEvent(SingleInstance.QuitName);
        var quit = _quit;
        new Thread(() =>
        {
            try { quit.WaitOne(); } catch { return; }
            log?.Invoke("quit requested");
            Dispatcher.BeginInvoke(() => Shutdown());
        }) { IsBackground = true, Name = "quit-listener" }.Start();

        int.TryParse(Option(args, "--exit-after"), out int exitAfter);

        // 1. settings (plan 09, D29): the one migration of 0.7.0 -> app state -> profiles -> teacher PIN -> the read-only,
        //    admin-owned machine layer (D11) -> the effective settings of the active profile
        //    (never throws: an unusable folder gives an in-memory « Mon profil » for this session)
        var (store, profiles) = ProfileMigration.OpenStores(AppPaths.Root, AppPaths.LegacyRoot, AppPaths.Backups, log);
        _appState = store;
        _profiles = profiles;
        var teacherFile = profiles.OpenTeacherFile();
        var tlock = _teacherLock = new TeacherLock(teacherFile, null, log);
        var mode = _mode = new TeacherMode(store, profiles, tlock, log);
        var machine = MachineLayer.Load(MachineLayer.DefaultPaths, MachineSecurity.Security, log);
        // a new Windows logon -> back to « Classe » and « Qui es-tu ? »; the same logon -> the active profile resumes
        var logon = _logon = SessionInfo.LogonTimeUtc(log);
        // (counting the pupils offered on this PC, « Profils proposés sur ce poste », 9B)
        var session = SessionPolicy.AtStart(mode.Enabled, profiles, logon);
        log?.Invoke($"profiles: management {(mode.Enabled ? "on" : "off")}, session {(session.NewLogon ? "new logon" : "resumed")}" +
                    (session.ShowPicker ? ", picker" : ""));
        // the welcome « shown once » flag is app-level (plan « aide intégrée »): migrate a former « Ne plus afficher » of the
        // profile active before any switch, so that « Classe » or a pupil never decides it later
        {
            var pre = ProfileService.CreateSettings(profiles, machine, null);
            if (StartupPolicy.MigrateWelcomeShown(store.WelcomeShown, pre.Current.Welcome, pre.IsLocked("start.welcome")))
            {
                _ = store.SetWelcomeShown(true);
                log?.Invoke("start: welcome flag migrated (shown)");
            }
        }
        if (session.ReturnToClass && profiles.ActiveId != Profile.ClassId && profiles.Class is { Unreadable: false })
        {
            log?.Invoke($"profiles: new session, {profiles.ActiveId} -> {Profile.ClassId}");
            profiles.SetActive(Profile.ClassId);
        }
        profiles.SetSession(logon ?? profiles.SessionLogonUtc, false);
        var settings = _settings = ProfileService.CreateSettings(profiles, machine, log);
        var cur = settings.Current;

        var db = FindDb(positional.FirstOrDefault());
        if (db is null)
        {
            MessageBox.Show(Strings.DbNotFound, Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var eng = _engine = PredictionEngine.Load(db);
        log?.Invoke($"engine loaded: {_engine.Count} words from {db}");
        // 0.8.3: a database without the spelling reference marks every lexicon form ✓ (the 0.8.2 behaviour): say which
        log?.Invoke(_engine.UnlistedCount > 0 ? $"spelling reference: {_engine.UnlistedCount} unlisted" : "spelling reference: absent");
        if (_engine.SpellingProblem is { } sp) log?.Invoke("spelling reference: " + sp);
        // the synthesizers are created on the speech thread; the Piper voice (plan 08a) loads after startup (StartVoiceLoad)
        var speaker = _speaker = new Speaker(cur.Speech, log, () => PiperSynth.Scan(AppContext.BaseDirectory, log,
                                                                                    Option(args, "--audio") == "null"));

        bool noPictures = args.Contains("--no-pictures");
        if (noPictures) log?.Invoke("pictures: off (--no-pictures)");
        else
        {
            var pack = FindPictos(Option(args, "--pictos"));
            _pictures = PictureStore.Open(pack, SuggestionWindow.DecodePng, log);
            if (_pictures != null)
            {
                log?.Invoke($"pictures: {pack}");
                if (cur.Pictures) Prefetch(_pictures, db, log);
                else log?.Invoke("pictures: hidden by the settings (pack open, no prefetch)");
            }
        }

        var fonts = new FontCatalog(FontCatalog.FindFontsDir(), log);
        log?.Invoke($"fonts: {fonts.FontsDir ?? "(no fonts folder: Segoe UI only)"}");

        // plan 10C: the « Noms propres » pack (worker thread) and the pictures of names (flags, the teacher's pictures)
        var wordPics = new WordPictures(b => SuggestionWindow.DecodePng(b, NamesImport.Thumb), log) { NamesOn = cur.NamePictures };
        var namesTask = NamesPack.LoadAsync(args.Contains("--no-names") ? null : NamesPack.Find(Option(args, "--names")), eng, log);
        namesTask.ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _namesPack = t.Result;
            if (t.Result is { } np) wordPics.SetPack(np.Pictures);
        }), System.Threading.Tasks.TaskScheduler.Default);
        // plan 11 (10D-1): the morphology table for grammatical prediction (worker thread; the bar ranks as 0.8.3 until it is in)
        _ = GrammarPack.LoadAsync(FindGrammar(Option(args, "--grammar")), eng, args.Contains("--no-grammar"), log);
        var win = new SuggestionWindow { Pictures = _pictures, WordPictures = wordPics, Log = log };
        win.Configure(LookOf(cur, fonts), cur.Position, FreeOf(cur));
        log?.Invoke($"bar warm-up: {win.WarmUp()} ms ({AppSettings.FontNames[win.Look!.Font.Font]}, {AppSettings.SpacingNames[cur.Spacing]}, size {cur.Size})");
        win.SpeakRequested += w => speaker.Say(w, SpeechKind.Manual);
        win.FreeMoved += (x, y) => settings.SetFreePosition(x, y);
        // plan 07: reading mode of the selection; the clipboard owner window lives on this (pumping) UI thread
        _clip = new System.Windows.Forms.NativeWindow();
        _clip.CreateHandle(new System.Windows.Forms.CreateParams { Caption = "Pepite-Prediction.Clipboard", Parent = (IntPtr)(-3) /*HWND_MESSAGE*/ });
        var reading = new ReadingMode(win, speaker, () => settings.Current, (x, y, w, h) => settings.SetReadingWindow(x, y, w, h))
        { Log = log, Pictures = _pictures, WordPictures = wordPics, IsName = w => _words?.Current.NameForm(w) == w || eng.Names.ContainsForm(w) && !eng.IsCommonWord(w) };   // owner 2026-11-15: « Rose » at a sentence start is the flower unless the lists hold it
        // test switch only (0.8.3 re-review): a host allowed to get the ≈ replacement from the keys alone (none in the product)
        if (Option(args, "--keys-only-replace") is { Length: > 0 } kh)
        {
            SoundAlikeCorrection.KeysOnlyHosts.Add(kh);
            log?.Invoke($"correction: keys-only replacement allowed in {kh} (test switch){(SoundAlikeCorrection.RewritesWords(kh) ? " - denied: it rewrites words" : "")}");
        }
        _ctl = new KeyboardController(_engine, win, speaker, reading) { Log = log, ClipboardWindow = _clip.Handle };
        _ctl.Configure(cur);
        _ctl.Start();

        settings.Changed += (old, now) =>
        {
            _ctl.Configure(now);
            win.Configure(LookOf(now, fonts), now.Position, FreeOf(now));
            _ctl.SyncVisibleCount();   // a re-trimmed bar may show fewer (or more) words
            if (now.Font != old.Font || now.Spacing != old.Spacing || now.Size != old.Size) win.ReWarm();
            if (now.Speech != old.Speech) speaker.Configure(now.Speech);
            wordPics.NamesOn = now.NamePictures;
        };

        var pictures = _pictures;
        // plan 09: profiles of the running app, the teacher-PIN gate (D29)
        var ctl = _ctl;
        var profileSvc = _profileSvc = new ProfileService(profiles, settings, new AppProfileHost(ctl, speaker), log);
        var gate = new PinGate(() => mode.Enabled, () => profiles.Policy, (r, ok) => PinDialog.Ask(tlock, r, ok, log), null, log);
        profileSvc.Gate = gate;
        Func<string?> activeName = () => mode.Enabled ? profiles.Active.Profile.Name : null;
        // plan 10A: the personal words of the active profile (Classe ∪ own lists), swapped into the engine
        var engine = _engine;
        var words = _words = new WordListService(new WordListStore(profiles.ProfilesDir, log, profiles.InMemory), profiles, lex => engine.User = lex, log,
                                                post: act => Dispatcher.BeginInvoke(act));
        profileSvc.Words = words;
        // plan 10C: the pictures of the active profile's names lists follow its words
        words.Applied += () =>
        {
            var (own, cls) = words.PictureMaps(profiles.ActiveId);
            wordPics.SetLists(own, cls);
        };
        words.Rebuild("start");
        var wordsCtx = new WordsContext(words, w => engine.Knows(w), () => engine.MostFrequent300, speaker,
                                        id => mode.Enabled ? profiles.Get(id)?.Profile.Name : null, log);
        Tray? trayRef = null;
        PupilsSection? pupils = null;
        // Réglages → Démarrage « … au prochain démarrage » reads and clears the app-level flag (not a profile setting)
        var welcomeFlag = new WelcomeFlag(() => !StartupPolicy.WelcomeShownBefore(store.WelcomeShown),
                                          due => { _ = store.SetWelcomeShown(!due); store.Flush(); });
        void ShowSettings(ISettingsTarget target) => SettingsWindow.ShowSingle(target, pupils, speaker, pictures, fonts, log, wordsCtx, welcomeFlag);
        void ShowPicker(string reason) => ProfilePicker.ShowSingle(profileSvc, pictures, fonts, speaker, reason, log);
        void ManagementChanged()
        {
            profileSvc.ManagementChanged();
            trayRef?.RefreshText();
            SettingsWindow.RefreshPupils();
        }
        TeacherContext teacherCtx = null!;
        void OpenTeacher() => gate.Open(PinReason.Teacher, () => TeacherWindow.ShowSingle(teacherCtx));
        teacherCtx = new TeacherContext(profileSvc, mode, gate, pictures,
            id => ShowSettings(id == profiles.ActiveId ? new ActiveProfileTarget(settings, activeName) : new ProfileEditTarget(profileSvc, id)),
            () => { profileSvc.Switch(Profile.ClassId, "end of session"); ShowPicker("end of session"); },
            ManagementChanged, log, Strings.AppName + " " + AboutWindow.Version, IsOnScreen, wordsCtx);
        pupils = new PupilsSection(() => mode.Enabled, () => !profiles.InMemory, () =>
        {
            // Réglages -> « Activer la gestion enseignant… »: create the PIN (hashed on a worker thread), then « Classe » + the flag
            if (!PinDialog.Create(tlock, pin => tlock.SetPin(pin), log)) return;
            mode.EnableAfterPin();
            gate.Granted();
            ManagementChanged();
        }, OpenTeacher);
        var openSettings = _openSettings = () => gate.Open(PinReason.Edit, () => ShowSettings(new ActiveProfileTarget(settings, activeName)));
        _tray = new Tray(_ctl, win, openSettings,
                         () => AboutWindow.ShowSingle(pictures, noPictures, fonts, _namesPack), () => gate.Open(PinReason.Quit, () => Shutdown()), log,
                         new TrayProfiles(activeName, () => gate.Open(PinReason.Switch, () => ShowPicker("picker")), OpenTeacher),
                         // Q3: a pupil adds a word without the PIN; on « Classe » (management on) it is an edit of the class
                         () =>
                         {
                             var id = profiles.ActiveId;
                             gate.Open(PinGate.AddWordReason(mode.Enabled, id), () => WordDialog.AddToActive(wordsCtx, id));
                         },
                         // « Aide »: the embedded help's home page, never behind the PIN
                         () => HelpUi.Open(HelpTopic.Home, null, log));
        // plan 10B: the « ? » spelling marker offers to add the word — the same PIN rule as the tray (Q3)
        ctl.AddWordRequested = word =>
        {
            var id = profiles.ActiveId;
            gate.Open(PinGate.AddWordReason(mode.Enabled, id), () => WordDialog.AddSpelled(wordsCtx, id, word));
        };
        // the tray tooltip says when the configured voice is loading (owner, 2026-11-14: no popup)
        var tray = trayRef = _tray;
        profileSvc.ProfilesChanged += () => tray.RefreshText();
        // the daily backup of the profiles, once the start is over (a few small files: on the UI thread, idle priority)
        Dispatcher.BeginInvoke(() => profiles.BackupIfDue(), DispatcherPriority.ApplicationIdle);
        // recomputed from the configured voice at every event: a switch to an already loaded voice clears it too
        speaker.VoiceStateChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var c = speaker.ConfiguredPiperVoice;
            tray.SetVoiceLoading(c != null && speaker.VoiceState(c) == PiperState.Loading ? c : null);
        });
        // plan 08a: bar warmed up, hooks and tray installed: the Piper voice may load now (its own thread, BelowNormal)
        speaker.StartVoiceLoad();
        // Explorer applies IsPromoted live (step 0 probe, Win 11 24H2): no icon re-add needed
        if (!args.Contains("--no-promote") && Environment.ProcessPath is { } exe)
            _ = TrayPromotion.PromoteOnceAsync(exe, null, log, new SettingsPromotionState(store));

        // plan 09B: session returns to « Classe » — Win+L / unlock / logoff notifications and the inactivity poll (30 s,
        // GetLastInputInfo; never in the hook). The policy decides (pure), the app switches and opens the picker.
        void DoSession(SessionAction a)
        {
            if (!a.Any) return;
            log?.Invoke($"session: {a.Reason} -> {(a.Secure ? "teacher windows closed, grace ended" : "")}{(a.Secure && a.ReturnToClass ? " + " : "")}" +
                        $"{(a.ReturnToClass ? "classe" : "")}{((a.Secure || a.ReturnToClass) && a.ShowPicker ? " + " : "")}{(a.ShowPicker ? "picker" : "")}");
            if (a.Secure)
            {
                // the adult has left: the Espace enseignant and Réglages close, the PIN grace ends — also on « Classe »
                TeacherWindow.CloseOpen();
                SettingsWindow.CloseOpen();
                gate.ClearGrace();
            }
            if (a.ReturnToClass) profileSvc.Switch(Profile.ClassId, a.Reason);
            if (a.ShowPicker) ShowPicker(a.Reason);
        }
        _session = new SessionWatcher(log);
        _session.Changed += code =>
        {
            switch (code)
            {
                case SessionWatcher.WtsSessionLock: log?.Invoke("session: locked"); DoSession(_sessionPolicy.OnLock(SessionCtx())); break;
                case SessionWatcher.WtsSessionUnlock: log?.Invoke("session: unlocked"); DoSession(_sessionPolicy.OnUnlock(SessionCtx())); break;
                case SessionWatcher.WtsSessionLogoff: log?.Invoke("session: logoff notification"); SessionEnds("logoff notification"); break;
            }
        };
        var idleTimer = _idleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SessionPolicy.PollInterval };
        idleTimer.Tick += (_, _) => DoSession(_sessionPolicy.OnIdle(SessionWatcher.IdleTime(), SessionCtx()));
        idleTimer.Start();

        // start-up feedback: welcome window (manual start), notification (start with Windows), or nothing; then, at a new
        // Windows session with management on, « Qui es-tu ? » (after the welcome window when it is shown)
        // (a start for an import goes straight to it: no welcome window over the PIN / import dialogs)
        bool welcomeLocked = settings.IsLocked("start.welcome");
        bool welcomeDue = StartupPolicy.WelcomeDue(StartupPolicy.WelcomeShownBefore(store.WelcomeShown), cur.Welcome, welcomeLocked);
        var feedback = StartupPolicy.AtStart(kind, welcomeDue, StartupPolicy.NoWelcome(args) || _pendingImport != null);
        log?.Invoke($"start: {kind}, feedback {feedback}");
        void SessionPicker() { if (session.ShowPicker) ShowPicker("session start"); }
        if (feedback == StartFeedback.WelcomeWindow)
        {
            // shown once (owner Q3, embedded-help plan): the app-level flag, recorded as soon as it is shown
            if (StartupPolicy.WelcomeShownAfter(welcomeLocked) is bool shown) { _ = store.SetWelcomeShown(shown); store.Flush(); }
            WelcomeWindow.ShowSingle(WelcomeWindow.Mode.Welcome, SessionPicker, null, log);
        }
        else
        {
            if (feedback == StartFeedback.ReadyNotification) tray.ShowReady();
            SessionPicker();
        }

        // plan 12 (Q3): the import of a double-clicked .pepite — the Espace enseignant's PIN, then its import dialog with
        // the file already chosen; with management off, only a message (the file is never read then)
        _importFile = path =>
        {
            if (!OpenRequest.Acceptable(path, OpenRequest.FileLength))
            {
                log?.Invoke("import: refused (not an existing .pepite of 50 MB at most)");
                MessageBox.Show(Strings.ImportOpenRefused, Strings.ImportTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!mode.Enabled || profiles.InMemory)
            {
                log?.Invoke(profiles.InMemory ? "import: refused (profiles in memory)" : "import: management off, message shown");
                MessageBox.Show(Strings.ImportNeedsManagement, Strings.ImportTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            log?.Invoke("import: PIN gate");
            gate.Open(PinReason.Teacher, () => TeacherWindow.ImportFromFile(teacherCtx, path));
        };
        if (_pendingImport is { } pending)
        {
            _pendingImport = null;
            Dispatcher.BeginInvoke(() => _importFile(pending), DispatcherPriority.ApplicationIdle);
        }

        if (exitAfter > 0)
        {
            log?.Invoke($"exit-after {exitAfter} s");
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(exitAfter) };
            t.Tick += (_, _) => { t.Stop(); log?.Invoke("exiting (exit-after)"); Shutdown(); };
            t.Start();
        }
    }

    /// <summary>What <see cref="SessionPolicy"/> looks at now. Busy = a modal window of ours is open: a WPF dialog (PIN,
    /// import: <c>ComponentDispatcher.IsThreadModal</c>) or a Win32 one (a message box, a file dialog), which disables
    /// its owner's window handle.</summary>
    private SessionContext SessionCtx()
    {
        var p = _profiles!;
        bool busy = System.Windows.Interop.ComponentDispatcher.IsThreadModal || OwnWindowDisabled();
        return new SessionContext(_mode?.Enabled == true, p.Policy, p.ActiveId, p.Class is { Unreadable: false }, SessionPolicy.Offered(p).Count, busy);
    }

    /// <summary>One of our windows is disabled by a modal Win32 dialog (WPF's <c>IsEnabled</c> does not follow the
    /// handle, so the handle is asked).</summary>
    private bool OwnWindowDisabled()
    {
        foreach (Window w in Windows)
        {
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero && w.IsVisible && !IsWindowEnabled(h)) return true;
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>The Windows session ends (logoff, shutdown): a real switch to « Classe » (D29 Q10; the app and the store
    /// stay in step if another program then cancels the logoff) and the session marked ended, written now.</summary>
    private void SessionEnds(string why)
    {
        if (_profiles is not { } p) return;
        if (_profileSvc is { } svc) svc.EndSession(_sessionPolicy.OnLogoff(SessionCtx()), _logon, why);
        else if (!p.InMemory) { p.SetSession(_logon ?? p.SessionLogonUtc, true); p.Flush(); }   // start-up not finished
    }

    /// <summary>Whether a saved position (physical px; w = h = 0 for the bar's top-left point) is on a screen of this PC.</summary>
    private static bool IsOnScreen(int x, int y, int w, int h)
    {
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
            if (w <= 0 || h <= 0 ? s.Bounds.Contains(x, y) : s.Bounds.IntersectsWith(new System.Drawing.Rectangle(x, y, w, h))) return true;
        return false;
    }

    private static BarLook LookOf(AppSettings s, FontCatalog fonts) =>
        new(BarMetrics.For(s.Size), s.Pictures, s.Keys == KeyMode.F1F8, fonts.Resolve(s.Font), s.Spacing);

    private static PointI? FreeOf(AppSettings s) => s.FreeX is int x && s.FreeY is int y ? new PointI(x, y) : null;

    protected override void OnExit(ExitEventArgs e)
    {
        // first: a second start from now on finds no event and retries the mutex (released when the process ends); the
        // listener is woken so that it lets go of the handle (a wait keeps it open)
        _exiting = true;
        if (_activate != null) SingleInstance.Signal(SingleInstance.ActivateName);
        _activate?.Dispose();
        if (_open != null) SingleInstance.Signal(SingleInstance.OpenName);
        _open?.Dispose();
        _quit?.Dispose();
        _idleTimer?.Stop();
        _session?.Dispose();
        _ctl?.Dispose();
        _engine?.Dispose();
        _speaker?.Dispose();
        _tray?.Dispose();
        _pictures?.Dispose();
        _clip?.DestroyHandle();
        _appState?.Flush();
        _profiles?.Flush();
        _words?.Store.Flush();
        _teacherLock?.Flush();
        _logWriter?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Windows is ending the session (logoff, shutdown): « Classe » and the session marked ended, so that the next
    /// start — a new logon — starts there (D29 Q10; the logon time decides it too, this is the explicit trace).</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        try
        {
            SessionEnds(e.ReasonSessionEnding == ReasonSessionEnding.Logoff ? "logoff" : "shutdown");
            _appState?.Flush();
        }
        catch (Exception) { }
        base.OnSessionEnding(e);
    }

    /// <summary>What a profile switch stops in the running app.</summary>
    private sealed class AppProfileHost : IProfileHost
    {
        private readonly KeyboardController _ctl;
        private readonly Speaker _speaker;
        public AppProfileHost(KeyboardController ctl, Speaker speaker) { _ctl = ctl; _speaker = speaker; }
        public void ResetForSwitch() => _ctl.ResetForSwitch();
        public void CancelSpeech() => _speaker.CancelAll();
        public void CloseProfileWindows()
        {
            SettingsWindow.CloseOpen();
            TeacherWindow.CloseOpen();   // the teacher's window must not stay open for the next pupil
        }
    }

    private static string? Option(string[] args, string name) => CommandLine.Option(args, name);

    /// <summary>Arguments that are neither an option nor an option's value.</summary>
    private static List<string> Positional(string[] args) => CommandLine.Positional(args);

    private static T OptionEnum<T>(string[] args, string name, IReadOnlyDictionary<T, string> names, T def) where T : struct
    {
        var v = Option(args, name);
        if (v is null) return def;
        foreach (var kv in names) if (string.Equals(kv.Value, v, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        throw new ArgumentException($"{name} {v}: expected one of {string.Join(", ", names.Values)}");
    }

    /// <summary>--render-bar: no DB, no hooks, no tray. Exit code 0 = PNG written. The .txt beside the PNG records
    /// the size, the font actually used and the glyphs the font lacks (F0 probe).</summary>
    private static int RenderBar(string[] args, string outPng, List<string> words, Action<string>? log)
    {
        try
        {
            double dpi = double.TryParse(Option(args, "--dpi"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 96;
            var size = OptionEnum(args, "--size", AppSettings.SizeNames, BarSize.M);
            var font = OptionEnum(args, "--font", AppSettings.FontNames, BarFont.SegoeUI);
            var spacing = OptionEnum(args, "--spacing", AppSettings.SpacingNames, LetterSpacing.Normal);
            // plan 10B: the spelling marker left of the words (render sheets of the three states)
            var mark = Option(args, "--mark") switch
            {
                "known" => Predict.Core.SpellingMark.Known, "sounds" => Predict.Core.SpellingMark.SoundsLike,
                "unknown" => Predict.Core.SpellingMark.Unknown, _ => Predict.Core.SpellingMark.None,
            };
            using var store = args.Contains("--no-pictures") ? null
                            : PictureStore.Open(FindPictos(Option(args, "--pictos")), SuggestionWindow.DecodePng, log);
            if (words.Count == 0) words = new List<string> { "maison", "chat", "voiture", "tristement" };
            var notes = new List<string>();
            var fonts = new FontCatalog(FontCatalog.FindFontsDir(), m => { notes.Add(m); log?.Invoke(m); });
            var rf = fonts.Resolve(font);
            var look = new BarLook(BarMetrics.For(size), store != null, !args.Contains("--no-keys"), rf, spacing);
            var t = Environment.TickCount64;
            var (w, h, dip, fallbacks) = SuggestionWindow.RenderBar(words, store, look, dpi, outPng, mark);
            var with = store is null ? 0 : words.Count(x => store.Has(x));
            var test = Strings.GlyphTest + Strings.GlyphTest.ToUpperInvariant() + "'-’";
            var sb = new StringBuilder();
            sb.AppendLine(FormattableString.Invariant(
                $"render-bar: {words.Count} words ({with} with a picture), {w}x{h} px at {dpi} dpi ({dip.Width:0.#}x{dip.Height:0.#} DIP), {Environment.TickCount64 - t} ms -> {outPng}"));
            sb.AppendLine($"look: size {size} font {AppSettings.FontNames[font]} -> {AppSettings.FontNames[rf.Font]} ({rf.Family.Source}, weight {rf.Weight}, file {Path.GetFileName(rf.FontFile ?? "?")}) " +
                          $"spacing {AppSettings.SpacingNames[spacing]} keys {(look.ShowKeys ? "on" : "off")} marker {mark}");
            sb.AppendLine($"missing glyphs: {FontCatalog.MissingGlyphs(rf.Glyphs, test)}");
            if (rf.Glyphs is { } g)
                sb.AppendLine(FormattableString.Invariant($"metrics: line height {g.Height:0.###} em ({g.Height * BarMetrics.For(size).WordPt:0.#} DIP at {BarMetrics.For(size).WordPt} pt), baseline {g.Baseline:0.###} em, family line spacing {rf.Family.LineSpacing:0.###}"));
            if (fallbacks.Count > 0) sb.AppendLine($"fallback words: {string.Join(" ", fallbacks)}");
            if (rf.Glyphs is { } gk && words.Count > 0)
            {
                // what SpacedText (glyph advances only) loses against WPF's own layout (pair kerning): mean em per gap
                var tf = new System.Windows.Media.Typeface(rf.Family, FontStyles.Normal, rf.Weight, FontStretches.Normal);
                double sum = 0; int gaps = 0;
                foreach (var kw in words)
                {
                    if (kw.Length < 2 || kw.Any(c => !gk.CharacterToGlyphMap.ContainsKey(c))) continue;
                    double adv = kw.Sum(c => gk.AdvanceWidths[gk.CharacterToGlyphMap[c]]);
                    var ft = new System.Windows.Media.FormattedText(kw, CultureInfo.GetCultureInfo("fr-FR"), FlowDirection.LeftToRight, tf, 100,
                                                                    System.Windows.Media.Brushes.Black, 1.0);
                    sum += ft.WidthIncludingTrailingWhitespace / 100 - adv;
                    gaps += kw.Length - 1;
                }
                if (gaps > 0) sb.AppendLine(FormattableString.Invariant($"kerning: WPF layout - glyph advances = {sum / gaps:+0.0000;-0.0000} em per letter gap ({gaps} gaps)"));
            }
            foreach (var n in notes) sb.AppendLine("note: " + n);
            if (args.Contains("--probe-fonts") && fonts.FontsDir is { } fd)
                foreach (var dir in Directory.GetDirectories(fd))
                {
                    foreach (var fam in System.Windows.Media.Fonts.GetFontFamilies(dir + "\\"))
                        sb.AppendLine($"probe {Path.GetFileName(dir)}: family '{string.Join("|", fam.FamilyNames.Values)}' source {fam.Source}");
                    foreach (var tf in System.Windows.Media.Fonts.GetTypefaces(dir + "\\"))
                        sb.AppendLine($"probe {Path.GetFileName(dir)}: typeface '{string.Join("|", tf.FontFamily.FamilyNames.Values)}' weight {tf.Weight} glyphs {(tf.TryGetGlyphTypeface(out var gt) ? Path.GetFileName(gt.FontUri.LocalPath) + " missing:" + FontCatalog.MissingGlyphs(gt, test) : "FAILED")}");
                }
            var text = sb.ToString();
            log?.Invoke(text.TrimEnd());
            File.WriteAllText(outPng + ".txt", text);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outPng + ".txt", "render-bar failed: " + ex + Environment.NewLine); } catch { }
            log?.Invoke("render-bar failed: " + ex);
            return 1;
        }
    }

    /// <summary>Warm the picture cache with the most frequent forms that have a picture (background).</summary>
    private static void Prefetch(PictureStore store, string db, Action<string>? log)
    {
        Task.Run(async () =>
        {
            try
            {
                await store.Loaded.ConfigureAwait(false);
                var words = new List<string>();
                using (var con = new SqliteConnection($"Data Source={db};Mode=ReadOnly"))
                {
                    con.Open();
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = "SELECT ortho FROM words ORDER BY freq DESC LIMIT 3000";
                    using var r = cmd.ExecuteReader();
                    while (r.Read() && words.Count < 300) { var w = r.GetString(0); if (store.Has(w)) words.Add(w.ToLowerInvariant()); }
                }
                store.Prefetch(words);
                log?.Invoke($"pictos: prefetch of {words.Count} frequent words queued");
            }
            catch (Exception ex) { log?.Invoke("pictos: prefetch failed: " + ex.Message); }
        });
    }

    /// <summary>Argument, else next to the exe, else walk up to src\Data.Build\out\prediction.sqlite (dev).</summary>
    private static string? FindDb(string? arg) => Find(arg, "prediction.sqlite");

    /// <summary>--pictos (an explicit path is never second-guessed: missing = no pictures), else next to the
    /// exe, else walk up to src\Data.Build\out\pictos.sqlite (dev).</summary>
    private static string? FindPictos(string? arg) =>
        arg is not null ? (File.Exists(arg) ? arg : null) : Find(null, "pictos.sqlite");

    /// <summary>Plan 11: --grammar (an explicit path is never second-guessed: missing = no grammar), else next to the exe,
    /// else walk up to src\Data.Build\out\grammar.sqlite (dev).</summary>
    private static string? FindGrammar(string? arg) =>
        arg is not null ? (File.Exists(arg) ? arg : null) : Find(null, "grammar.sqlite");

    /// <summary>Plan 12: the walk-up to Data.Build\out happens only for development builds (<see cref="DataSearch"/>).</summary>
    private static string? Find(string? arg, string name)
    {
        if (!string.IsNullOrEmpty(arg) && File.Exists(arg)) return arg;
        return DataSearch.FindFile(name);
    }
}

/// <summary>The --log file writer: callers (the keyboard hook among them) only timestamp and enqueue; a background
/// thread appends to the file. Before iteration 3 every line was a synchronous file append, which put disk latency
/// inside the low-level hook callback and in its timing.</summary>
internal sealed class AsyncLog : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, false);
    private readonly BlockingCollection<string> _lines = new();
    private readonly Thread _thread;

    public AsyncLog(string path)
    {
        _thread = new Thread(() =>
        {
            foreach (var first in _lines.GetConsumingEnumerable())
            {
                var sb = new StringBuilder(first);
                while (_lines.TryTake(out var more)) sb.Append(more);
                try { File.AppendAllText(path, sb.ToString(), Utf8); } catch { }   // lenient: a lone surrogate never loses a batch
            }
        }) { IsBackground = true, Name = "log-writer", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Write(string m)
    {
        try { _lines.Add($"{DateTime.Now:HH:mm:ss.fff} {m}\n"); } catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        _lines.CompleteAdding();
        _thread.Join(2000);
    }
}
