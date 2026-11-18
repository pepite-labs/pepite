# `Predict.Keyboard` design — prediction in any Windows application

Source: [`src/Predict.Keyboard/`](../src/Predict.Keyboard/). User guide and test log:
[keyboard.md](../development/keyboard.md). Text capture and insertion use the documented Windows interfaces:
UI Automation `TextPattern` first where the application supports it, with the Win32 caret
(`GetGUIThreadInfo`) as the fallback (Microsoft Learn, *UI Automation TextPattern* and *GetGUIThreadInfo*).

## 1. Requirements that shape the design

| Requirement | Consequence |
|---|---|
| Works in **any** app (Notepad, Word, browsers) without plug-ins | system-wide low-level hooks + `SendInput`; no per-app integration |
| The user's app must **keep focus** | bar is a non-activating window (`WS_EX_NOACTIVATE`, `MA_NOACTIVATE`) |
| **Never lose or delay a keystroke** | hooks on their own thread, never wait for prediction or UI |
| French AZERTY, AltGr, dead keys (`^` + `e` = `ê`) | layout-aware translation that does not disturb the dead-key state |
| Accessible: big targets, colour + keys, hear the word | 1–8 large buttons (S–XL), F1–F8, click, right-click = speech (SAPI 5), speech echo |
| Dyslexia-friendly reading (D16) | font choice (OpenDyslexic, Lexend, Andika bundled privately) + letter spacing |
| Per-account settings, school-lockable (D8, D11) | `settings.json` in `%APPDATA%` + read-only admin-owned machine layer |
| Install-free | self-contained publish, DB next to the exe |

## 2. Components

```
 App (startup order: --quit → tool modes → single instance → migration → app state → profiles → teacher PIN → machine
  │   layer → session start (logon time) → settings of the active profile → DB → speech → pictures → fonts → bar → hooks
  │   → profiles service + PIN gate → tray → Piper voice load → session watcher + idle poll (9B) → start-up feedback
  │   → « Qui es-tu ? » (plan 09);
  │   --log, --render-bar, --speech-bench, --autostart, --no-welcome)
  ├── SingleInstance     Local\Pepite-Prediction.{Instance (mutex), Activate, Quit (events)}, explicit DACL + low label
  ├── StartupPolicy      pure: welcome window / ready notification / nothing; second start; HelpLinks (aide\ or LISEZ-MOI)
  ├── WelcomeWindow      « Pépite est prêt ! » / « Pépite est déjà démarré », taskbar picture (vector)
  │
  ├── JsonFileStore      one JSON file kept whole (unknown keys survive), atomic background writes, .bad-* or kept
  │                      read-only on corruption (plan 09 step 2, factored out of SettingsStore)
  ├── SettingsStore      %APPDATA%\Pepite-Prediction\settings.json: app state (state.trayPromoted,
  │                      state.managementEnabled; state.json migration) + SettingsPromotionState
  ├── AppPaths           %APPDATA%\Pepite-Prediction, %LOCALAPPDATA%\Pepite-Prediction\sauvegardes, legacy PredictKeyboard
  ├── ProfileMigration   the one 0.7.0 migration (old settings.json → « Mon profil », index last, .avant-profils kept);
  │                      TeacherMode: management on (PIN + « Classe » + flag) / off (PIN cleared, profiles kept)
  ├── Profile            pure: id p-xxxxxx / classe, name rules, Avatar (pictogram word + 8 named colours), layer
  ├── ProfileStore       profiles\index.json (order, active, policy, session) + one JsonFileStore per profile; lost index
  │                      rebuilt, unreadable profile « (illisible) », zip backups (daily, before a delete / an import), ≤ 40
  ├── ProfileArchive     pure (9B): *.pepite export (Classe + chosen pupils, PIN hashes only if ticked), validated read,
  │                      Plan (create / replace / keep both / skip, rebase onto the target Classe, 40 limit), Apply
  │                      (backup first); ImportDialog (rows, conflict choices, summary, confirmation)
  ├── SessionPolicy      pure (9B): start (offered pupils), idle → Classe + picker, Win+L → Classe / unlock → picker,
  │                      logoff → Classe; « Profils proposés sur ce poste »; SessionWatcher (WTS notifications on a
  │                      hidden tool window, GetLastInputInfo)
  ├── TeacherLock        pure: PBKDF2-SHA256 PIN 4–6 digits + recovery code, back-off persisted in teacher.json
  ├── SettingsLayering   pure: builtin < machine.defaults < « Classe » < profile < (exam, 9C) < machine.locked;
  │                      NewUserLayer (class in the baseline), Rebase (import, 9B); MachineLayer (owner-SID trust)
  ├── SettingsService    UI thread: Current, Locked, Apply(edited) → the active profile's layer + Changed(old, new);
  │                      Retarget (a switch, the class edited) → Changed when the effective settings differ
  ├── ProfileService     UI thread: Switch (controller reset, speech cancelled, Réglages closed, Retarget, index.active,
  │                      PIN grace ended), CRUD; ActiveProfileTarget / ProfileEditTarget (ISettingsTarget); SessionStart
  ├── SettingsGate       PinReason, ISettingsGate, PinGate (pure: the teacher's switches, 5-minute grace; AddWord never
  │                      protected, plan 10 Q3)
  ├── WordBook           pure (plan 10A, WordList.cs): one profile's mots.json — lists (« Mes mots », lessons), « Mes
  │                      fautes », hidden class words; entry rules, limits, tolerant parse; WordLayering (Classe ∪ own −
  │                      hidden; Rebase for imports); WordTextFile (.txt lists, UTF-8 / Windows-1252)
  ├── WordListStore      one JsonFileStore per profiles\<id>\mots.json (no file until a change)
  ├── WordListService    UI thread: the active profile's UserLexicon snapshot → engine.User (start, switch, edit,
  │                      import; last two snapshots reused); add (tray, Q3), erase all (backup -mots first)
  ├── WordEditor         code-built editor of one profile's words (Réglages → « Mots », Espace enseignant → « Mots »);
  │                      WordDialog (one word / a « Mes fautes » pair; tray « Ajouter un mot… »); LessonDialog
  ├── PinDialog          the PIN (keypad, back-off live region, « Code oublié ? » → recovery code → new PIN), create
  │                      mode; RecoveryCodeWindow (shown once: Imprimer / Copier)
  ├── TeacherWindow      « Espace enseignant »: Profils · Verrouillage · Code · Exporter / importer · Mots · Fin de session; ProfileEditDialog
  │                      (first name, pictogram word, colour); ProfilePicker « Qui es-tu ? »; AvatarView; SessionInfo
  │                      (WTS logon time)
  ├── SettingsWindow     « Réglages »: tabs « Barre et voix » (working copy of an ISettingsTarget, live preview
  │                      (BarBuilder), Enregistrer / Annuler; « Plusieurs élèves ») and « Mots » (« Indicateur
  │                      d'orthographe », saved by Enregistrer, plan 10B; WordEditor, saved at once)
  ├── SuggestionWindow   WPF, topmost, never activates; ShowSuggestions / Place / Contains / WarmUp / ReWarm /
  │                      RenderBar; free-position grip (manual drag); trims words that do not fit the screen;
  │                      the spelling marker left of the words (plan 10B, §6h);
  │                      ShowReading / HideReading: the reading view of a selection (plan 07, §5d)
  ├── ReadingMode        UI thread: selection → ReadingDocument → bar (+ ReadingWindow), ReadingController, speech
  ├── ReadingView        code-built toolbar ▶❚❚ ⏮ ⏭ ⤢ + lazy word / picture tiles (bar and « grande fenêtre »)
  ├── ReadingWindow      « grande fenêtre »: the same view, resizable, place and size remembered (reading.window)
  ├── BarBuilder         one suggestion button for a BarLook (metrics, pictures, F-label, font, spacing); MakeMarker
  │                      (plan 10B: the round ✓ ≈ ? spelling marker)
  ├── BarLayout          pure: BarMetrics S/M/L/XL (+ marker diameter / heights), BarPlacement caret / top / bottom /
  │                      free (physical px), WordsThatFit (the trimming; the fixed part counts the marker)
  ├── FontCatalog        private OFL fonts from fonts\<Family>\ (FontFamily(baseUri, "./#Family")), Segoe UI fallback
  ├── SpacedText         one word drawn as a GlyphRun with extra letter spacing (SpacedLayout: pure arithmetic)
  ├── PictureStore       read-only pictos.sqlite: word→picture map (background load), picto-loader thread, LRU 320
  ├── GrammarPack        plan 11 (10D-1, 0.9.0): grammar.sqlite (French.lex-derived, MPL-2.0) → GrammarLexicon.Load on a
  │                      worker at start → engine.Grammar (one swap; the bar ranks as 0.8.3 until then or without the
  │                      file); one compacting GC after; logs `grammar: N forms, M lemmas, loaded in T ms` / `grammar:
  │                      absent`; `--grammar <path>`, `--no-grammar` (test switch only: always on, no setting, owner Q2)
  ├── Tray               NotifyIcon (own icon, grey while paused) + native popup menu on left/right click
  ├── TrayPromotion      first-run IsPromoted=1 under HKCU\Control Panel\NotifyIconSettings (once per exe path)
  ├── AboutWindow        version, ARASAAC attribution + licence, Lexique credit, OFL font credits (links → browser),
  │                      « Voix »: one block per Piper voice (its card's credits), engines, GPL notice,
  │                      THIRD-PARTY-NOTICES.txt (local)
  ├── Strings            every user-visible string, also used from XAML via {x:Static} (D14; lint test)
  ├── Speaker            SpeechWorker (own `speech` thread owns the synthesizers, SpeechQueue policy);
  │                      Read(text, progress, done): a reading sentence with word progress
  │   ├── EngineRouter   ISynth over SapiSynth + PiperSynth (plans 08a/08b): voice ids `sapi:…` / `piper:siwis` /
  │   │                  `piper:pierre`; D24 default = siwis (never pierre implicitly); each Piper voice routed by its
  │   │                  own state: hold for its preview, SAPI after a failed load; the configured voice (settings
  │   │                  applied for anything but a preview) = the engine's Resident. Make-before-break (0.6.0 fix):
  │   │                  a switch from a Ready Piper voice to one still loading keeps the old one speaking (the
  │   │                  bridge) until the new one is Ready, and for good if its load fails; SAPI is the interim
  │   │                  only when no Piper voice is loaded. Preload: a Piper voice selected in Réglages starts
  │   │                  loading at once (preview-only until Enregistrer). Isolated words (Word / Inserted /
  │   │                  Manual) are flagged for the word scales and cache. The engine's StateChanged
  │   │                  (load start / end, unload) also drives the Réglages status line and the tray tooltip
  │   ├── SapiSynth      System.Speech (SAPI 5), word progress = SpeakProgress
  │   └── PiperSynth     ONE engine for every voice pack (siwis, pierre): one `piper` thread (espeak-ng
  │                      phonemes, ONNX Runtime, unloads), a `piper-load` thread per load (a loaded voice keeps
  │                      speaking meanwhile), one `piper-clock` thread (WASAPI position → word progress / done;
  │                      residency ticks), one sink (SinkSlot: recreated only for another sample rate), one clock;
  │                      a model per loaded voice (multi-speaker: `sid`); PiperResidency pure: the configured voice
  │                      stays, a preview-only voice (loaded without ORT's memory arena) is unloaded 30 s after its
  │                      last utterance (or its load), a voice no longer configured once the new configured voice is
  │                      loaded and it is idle (`unloaded (replaced by piper:…)`; at once when a SAPI voice is
  │                      configured); a preview voice that becomes
  │                      the configured one gets its session rebuilt with the arena once idle. A voice unloaded
  │                      between the router's check and Speak is refused (false) and routed again (reload). An
  │                      isolated word (no progress) of a voice whose card has a `word_carrier` (pierre, 0.7.2;
  │                      siwis, 2026-11-16: « Le mot : {0}. »), unless the card's `word_carrier_except` lists it
  │                      (siwis: « eu », « les », said alone; trimmed, lower case), is said inside that sentence
  │                      with the voice's own scales and cut out
  │                      on the alignment (PiperCarrier, pure: quietest 5 ms before the word, 10 ms fade-in);
  │                      when the cut fails (no alignment, boundary not found) or the text has its own sentence
  │                      end, the word is said alone with `inference_word` (pierre 0.333 / 0.2). Either clip goes
  │                      to the PiperWordCache (pure LRU, ≤ 200 words / 20 MB, piper thread; carrier words keyed
  │                      apart): the same word always sounds the same. A card's `phoneme_fix` (PiperPhonemeFix,
  │                      pure; pierre `iʲ → i`) rewrites espeak's phonemes of every utterance of that voice; a
  │                      rule never adds or removes an espeak word, so the word map and the highlight are
  │                      unchanged. PiperPhonemizer, PiperWordMap, PlaybackClock pure;
  │                      EspeakNative (P/Invoke, global state: hence one engine), PiperModel (ORT), WasapiSink (NAudio)
  │                      + EchoGuard (`echo-guard` thread: PasswordProbe before a word/sentence is spoken)
  ├── PasswordGuards     ProbeWorker (blocking call on its own MTA thread, ≤ 2 abandoned replacements),
  │                      EchoGuard, FieldProbe (first character after a jump: password field?)
  └── KeyboardController the glue
        ├── hook thread: WH_KEYBOARD_LL + WH_MOUSE_LL + own GetMessage loop
        ├── WordTracker      current word + ≤3 previous words (+ suffix/next char after a read) (guarded by _sync);
        │                    plan 10B: Finished (the word a separator just finished, kept until the next letter) and
        │                    Typing (the word being typed), with their separators — nothing stored
        ├── SpellingIndicator (Predict.Core, pure, plan 10B): ✓ ≈ ? from PredictionEngine.Check; what is never
        │                    marked; LastWord (the same finished word from a text: Predict.Eval --spelling)
        ├── SpeechEcho       pure: chunks / sentence from the same keys → Word / Sentence events (same lock);
        │                    liaison grouping (plan 08b, D28): a word after only spaces is said with the previous
        │                    one when Liaison.Triggers (« les » … « les amis »); Covers drops a pending « les »,
        │                    or merges a pending chain (« ils en » + « en ont » → « ils en ont »). A sentence end
        │                    after a jump into the sentence is marked AfterJump: the controller asks its own
        │                    `sentence-reader` queue (same read as a jump's, password check first, ≤ 400 ms, stale
        │                    only on a jump) for the whole sentence (SentenceDocRead, pure) and says the document's
        │                    text; the words typed since are the fallback (D17 amendment, 2026-11-14). An unfinished
        │                    caret sentence never yields the one before it (second read 80 ms later); echoes typed
        │                    meanwhile wait behind the read and follow the sentence (SentenceEchoGate, pure; given up
        │                    after 1.1 s); the D23 skip needs the sentence spoken or being spoken
        ├── CaretTextReader  own MTA thread: reads the text around the caret after a jump (Win32 Edit / UIA)
        ├── SelectionReader  own MTA thread: the selected text (EM_GETSEL+WM_GETTEXT / UIA GetSelection), Ctrl+C fallback
        ├── SelectionPolicy, ClipboardCopy, ReadingDocument, ReadingController  pure (plan 07, unit-tested)
        ├── ContextExtractor pure: text window + caret → prefix, suffix, next char, context (= what typing gives)
        ├── KeyboardPolicy   pure: apply a read? (version, foreground, ≤ 150 ms) · trailing space? · capitals ·
        │                    punctuation after our space (AutoSpaceState, D22)
        ├── EditSession      pure: D23 mirror of the read window + edits (TextWindow, TextAnalysis, EditState) (§5c)
        ├── EditClassifier   pure: landing SameWord / SameSentence / Elsewhere / Unknown → word / sentence / nothing
        ├── Elision, Tokenizer (Predict.Core, pure): l' d' qu'… display; French word / sentence tokens with offsets
        ├── Caret            GetGUIThreadInfo → UIA TextPattern → mouse position
        └── Inserter         N×Delete + M×Backspace + Unicode SendInput, stamped with marker 0x4C584B42 ("LXKB")
```

## 3. Threading model

```
 ┌─ hook thread (AboveNormal) ─────────────┐          ┌─ UI thread (WPF dispatcher) ──────────────────┐
 │ KeyboardProc(vk)                         │          │                                                │
 │  ├ ignore events with our marker         │          │ Update():                                      │
 │  ├ F1–F8 & bar visible → swallow,        │──Invoke─►│   snapshot tracker (lock)                      │
 │  │   BeginInvoke(Insert(i))              │          │   engine.Predict / PredictNext   (only here)   │
 │  ├ Esc/arrows/Ctrl/click → Reset + hide  │          │   SuggestionWindow.ShowSuggestions + PlaceAt   │
 │  └ char → lock{tracker.Type}             │          │ Insert(i):                                     │
 │      ScheduleUpdate() ──────────────────►│─(coal.)─►│   Inserter.Replace → if all sent: tracker.Accept│
 │ returns within µs, always                │          │                                                │
 └──────────────────────────────────────────┘          └────────────────────────────────────────────────┘
   shared: _tracker + _echo (lock _sync) · _suggCount, _wordCount, _keysOn (volatile) · _updatePending (Interlocked) · bar rect (lock)

 ┌─ hook thread ─────────────┐ Echo(ev, fg)  ┌─ echo-guard (MTA) ───────────┐ Say   ┌─ speech (MTA, BelowNormal) ─────┐
 │ lock{tracker; echo.Type}  │ ────────────► │ fg changed? → drop           │ ────► │ BlockingCollection of commands  │
 │ after the lock: enqueue   │ lock + event  │ PasswordProbe(fg): Edit      │       │ SpeechQueue policy (interrupt / │
 │ (Word / Sentence only)    │               │  ES_PASSWORD, else UIA       │       │  append ≤2 words / sentence)    │
 └───────────────────────────┘               │  IsPassword → drop + log     │       │ SAPI SpeakAsync / CancelAll     │
 ┌─ UI thread ───────────────┐ Say(Inserted, │ > 300 ms → drop; stuck > 2 s │       │ owns the SpeechSynthesizer      │
 │ Insert → Say(Inserted)    │  Manual) ─────┼──────────────────────────────┼─────► │ (created, voices, audio device  │
 │ right-click → Manual      │  (no guard)   └──────────────────────────────┘       │  opened here, never on the UI)  │
 │ « Écouter » → Preview     │                                                      └─────────────────────────────────┘
 └───────────────────────────┘     log-writer (BelowNormal): --log lines are enqueued, appended by one thread

 ┌─ hook thread ──────────────┐  Request(version, fg,  ┌─ caret-text-reader (MTA, background) ─────────────┐
 │ jump (click-up, nav, Ctrl, │  delay, reason)        │ one pending slot, latest wins (debounce)           │
 │ foreground WinEvent):      │ ─────────────────────► │ wait until due → still current version? else Stale │
 │  lock{Reset; v=Version}    │  tiny lock + event set │ Read(fg): Edit → Win32 msgs · else → UIA           │
 │  _suppress=false; hide     │                        │ > 150 ms → Timeout; fg changed → Stale             │
 └────────────────────────────┘                        │ OnRead → lock{ ShouldApply ? tracker.Seed }        │
                                                       │          → ScheduleUpdate (UI thread)              │
                                                       └────────────────────────────────────────────────────┘
```

**Merge rules (read vs keys).** A jump resets the tracker and remembers its `Version`. The read result is
applied (`tracker.Seed`) only if **(1)** the tracker version is unchanged (nothing typed, no other jump
since), **(2)** the same window is still in front and **(3)** the read took ≤ 150 ms
(`KeyboardPolicy.ShouldApply`). Otherwise it is logged as `Stale`/`Timeout` and the keys stay the source.
While typing, keystrokes are the only path (no read). A worker stuck > 2 s in a hung provider is abandoned
and a new one started; the old one never delivers its result.

- **Why a dedicated thread**: Windows silently *skips* a low-level hook that does not return within its
  timeout (`LowLevelHooksTimeout`). In the first version the hook shared the UI thread; a 900 ms cold
  prediction made Windows drop the next key (`m` of `maison`). Now the hook only touches the tracker.
- **Coalescing**: `_updatePending` ensures at most one queued `Update`; keys typed during a slow update are
  all in the tracker and the next `Update` predicts from the latest state.
- **Read-only mirrors for the hook thread**: `_suggCount` (is F*n* meaningful?), `SuggestionWindow.Contains`
  (was a click inside the bar?).
- **Pictures (`picto-loader`, BelowNormal, STA)**: the UI thread never touches SQLite nor a PNG decoder.
  `ShowSuggestions` sets cached pictures synchronously (`TryGetCached`, O(1) under a tiny lock), leaves an
  empty slot of the final size for the others and calls `PictureStore.Request(gen, words, ready)`. The loader
  thread (own read-only connection, `BlockingCollection`) drops what is left of a request as soon as a newer
  generation exists, reads the PNG, decodes it into a frozen `BitmapImage` (decoder injected, so the store is
  WPF-free and unit-tested) and calls `ready`, which `BeginInvoke`s to the UI thread; the UI sets the image
  only if `gen` is still the bar's generation. A startup prefetch (the 300 most frequent forms that have a
  picture) runs at the lowest priority and yields to every real request. The word→id map (30 k entries)
  loads on a background task in ≈ 15–25 ms; until then `Has` is false (no picture requested). The prefetch
  takes ≈ 0.45 s in the sandbox (220 distinct pictures). Each picture is fetched/decoded/delivered under its
  own try/catch; an id that fails once is remembered and not retried. `Dispose` cancels a token checked
  before every word and passed to `GetConsumingEnumerable`, so quitting abandons queued requests and the
  prefetch at once and never posts to a closing dispatcher; `Request`/`Prefetch` after `Dispose` are no-ops.
- **Speech (iteration 3)**: every caller only enqueues (`BlockingCollection.Add`, or the guard's lock + event): a
  synthesizer that takes 2 s to create or speak never delays a key or the UI (unit test: `Say` < 0.1 ms per call
  against a fake synthesizer blocking 2 s). Inserted / Manual / Preview interrupt (cancel the current utterance
  and everything pending); a word is appended, the oldest dropped when more than 2 wait; a sentence drops the
  pending words. The next item starts only after the cancelled one has reported back, so two utterances never
  overlap. Pause and a password read call `CancelAll` (sandbox: `speech cancelled` 16 ms after the request).
- **Piper voice (plan 08a)**: two more background threads, both owned by `PiperSynth`.
  ```
   speech thread ─ EngineRouter.SpeakAsync ─► PiperSynth: post job ──► ┌─ piper (Normal; loads: piper-load) ───────────┐
                  CancelAll: flag + RunOptions.Terminate + Flush ─────►│ load once: SHA-256 → espeak init → ORT session │
                                                                       │  → warm-up « Bonjour. »                        │
                                                                       │ job: cleanup → espeak clauses → sentences →    │
                                                                       │  ids → ORT Run (2 intra-op threads) per        │
                                                                       │  sentence → WasapiSink.Enqueue + clock marks   │
                                                                       └────────────────────────────────────────────────┘
   WASAPI render thread (NAudio, event-driven, 40 ms) ◄─ PlaybackQueue (silence when empty) ◄─ gain ◄─ WDL resampler
   ┌─ piper-clock (AboveNormal) ─ every 10 ms while an utterance plays: GetPosition() → PlaybackClock.Tick →
   │  progress(charPos, len) / done(Done); device error → done(Error) + close; idle 5 s → device closed; then waits
   └─ (no timer at all when idle and closed)
  ```
  The app allows the load only after the bar warm-up, the hooks and the tray (`Speaker.StartVoiceLoad`); until the
  voice is ready its utterances use SAPI (`speech: using SAPI until piper:siwis is ready`, once per load) — unless
  another Piper voice is loaded: after Enregistrer with another voice, the previous one speaks until the new one is
  ready (`speech: using piper:siwis until piper:pierre is ready`). The voice
  switches only between utterances. Cancel: the job is flagged, a running inference is terminated, the queued audio is
  dropped at once (only the ≤ 40 ms device buffer plays) and `done(Cancelled)` is called once. While the device is open
  but idle, `IdleBypassProvider` hands it zeros without resampling and advances the timeline itself (open-and-idle cost
  ≈ 0 CPU). ORT runs without a memory pattern (every sentence has another length) to keep the peak working set down.
- **Log writer**: `--log` lines are timestamped by the caller and appended by a `log-writer` thread (before
  iteration 3 every line was a synchronous file append inside the hook callback). The hook callback is timed
  (`hook: n=200 mean max`); its code is JIT-compiled before the hooks are installed (`hook path warm-up`).
- **Own windows**: the foreground WinEvent includes our own process (no `WINEVENT_SKIPOWNPROCESS` any more):
  when the tray menu's owner window or À propos comes to the front, the bar is hidden and nothing is read
  (`Jump` and `CaretTextReader.Read` refuse a foreground window of our pid; the key hook ignores keys typed
  into it).

## 4. Keystroke handling

| Key | Action | then reads at caret | Passed to the app? |
|---|---|---|---|
| letter / punctuation | `ToUnicodeEx(..., flags=4)` in the foreground window's layout → `tracker.Type` | — | yes |
| `, . … ) ' ’ -` / `; : ! ?` / space **right after our automatic space** (D22, §5b) | Backspace + the character (+ U+202F before `; : ! ?`), one `SendInput` from the hook thread; a second space is absorbed | — | **no** (replaced) |
| dead key (`^`, `¨`) | translation returns nothing; dead-key state preserved (flag 4) | — | yes |
| `VK_PACKET` (0xE7) | Unicode char from an on-screen keyboard/IME → `tracker.Type` | — | yes |
| Backspace | `tracker.Backspace` (back across a separator: the previous word is the prefix again, from the kept text) | after a `Lost` Backspace, or a crossing where reads work (a check): 50 ms (`bksp`) | yes |
| F1–F8 with bar visible, F-keys on, *n* ≤ words shown, no modifier | insert suggestion *n*; **key-down and key-up swallowed** | — | **no** |
| F1–F8 otherwise | ignored | — | yes |
| arrows, Home/End, PgUp/Dn, Tab, Ins/Del | `Reset` + hide (caret moved) | after 50 ms (`nav`; with Shift: `shift`, plan 07) | yes |
| Ctrl+key (paste, undo…), Ctrl+Backspace | `Reset` + hide (unknown edit) | after 120 ms (`ctrl`) | yes |
| Esc | `Reset` + hide (reading mode: leaves it, the selection is « dismissed », §5d) | no | yes |
| Space, F9 (no modifier) **while the reading view is shown** (D24) | Play / Pause (posted to the UI thread); **key-down and key-up swallowed** (auto-repeat swallowed without toggling) | — | **no** |
| Enter, `.` `!` `?` | commit word, clear context (new sentence) | — | yes |
| mouse button down outside the bar | `Reset` + hide | after the button-**up**, 50 ms (`click`) | yes |
| foreground change (`EVENT_SYSTEM_FOREGROUND` WinEvent, out-of-context, on the hook thread; key-down check as backstop) | `Reset` + hide | after 120 ms (`focus`) | — |
| our own injected input (marker `LXKB`), incl. the Deletes of an insertion | ignored — never a jump | — | yes |

The delays exist because the low-level hook sees an event **before** the application handles it.

### WordTracker state

```
          letter                      separator (space ' - ,)          . ! ? Enter
 ┌──────┐ ─────► ┌───────────────┐ ───────────────────────► ┌──────────────┐ ──────► context cleared
 │ idle │        │ in word (buf) │                          │ after word   │
 └──────┘        └───────────────┘ ◄─────── Backspace ───── │ (_sep > 0)   │ ──letter──► in word
                  Predict(buf, ctx)         (restores word)  └──────────────┘
                                                              PredictNext(ctx)
```

**Backspace across a word boundary** (owner report, 0.6.0, Word: `Ma c` ⌫ ⌫ ⌫ then `o` predicted from `o`, not
`Mo`; the old tracker forgot the separators once a letter followed them). The tracker keeps the text it saw before the
caret since the last jump (`_text`, typed / inserted / seeded, ≤ 256 chars, the oldest half dropped beyond that) behind a
marker: a line feed (U+000A) = a known sentence start (a read found one), `0` = unknown (a digit: a barrier, not a
sentence end). A Backspace inside the word only shortens it. One that deletes a separator deletes the last char of
`_text` and **derives the whole state again** with `ContextExtractor.Extract` — the state typing that text would have
reached (property test `Equivalence_BackspaceVsTypingTheShorterText`: random texts, F1 insertions, long texts). So:

| typed, then Backspace | state |
|---|---|
| `Ma c` ⌫ ⌫ | `typed='Ma' ctx=[]` (case and accents as typed) — then `o` → `Mo` |
| `Ma, c` ⌫ ⌫ / ⌫ ⌫ ⌫ | after `Ma,` (next-word prediction, `LastSeparator=','`) / `typed='Ma'` |
| `Ma   c` ⌫ | every space is one Backspace; the third restores `Ma` |
| `le petit chat ` ⌫ × 6 / × 12 | `typed='petit' ctx=[le]` / `typed='le'` |
| `l'ami` (typed or `l'` inserted) ⌫ × 4 | after `l'`, then `typed='l'` |
| F1 `maison` + our space, ⌫ | `typed='maison' ctx=[la]` |
| `Fin. Ma` ⌫ × 3 / × 4 | after `Fin.` (sentence start: capitals) / keys only: `Lost`; after a read: `typed='Fin'` |
| `nom` Enter `x` ⌫ ⌫ | the line break is a barrier → `Lost` (Enter may have moved the focus: two-step logins) |
| a click, then `o` ⌫ ⌫ | nothing known before the caret → `Reset` (`Lost`) |

**When the kept text is not trusted** (`Backspace` returns `Lost`, the tracker resets, the controller reads the caret):
- the deleted char lies in the part **rebuilt from a read** (`Seed` writes the read's words with one space between them:
  the real separators are not in the read, and a prefix length taken from it would make F1 delete too much);
- a typed **line break**;
- **keys only** (`Origin == "keys"`, no read in this window — Word): `. ! ? … ; : " « »` and no-break spaces. The
  assumption: such hosts rewrite these behind our back (AutoCorrect `...` → `…`, a no-break space added before
  `! ? ; :` and inside « », `"` → « »), so the document no longer has the length we typed. Spaces, commas, apostrophes,
  hyphens and tabs are crossed: they stay one char for one (`'` → `’` at most). Letters rewritten inside a word
  (`oe` → `œ`) are not detected (as before this change, for the word being typed).

The controller asks a caret read (reason `bksp`, 50 ms, same password guards) after `Lost`, and after `Crossed` when
the last state came from a read (reads work in this window). After `Crossed` the read only **checks**: if it finds the
same prefix and context, the exact typed text is kept (`read bksp … agrees with the keys (kept)`); otherwise the
document wins (`Seed`). A `bksp` read seeds the echo only if the echo lost everything, and it is neither a D23 begin /
landing nor a selection decision; none is asked while a D23 leave waits for its own read. Pause and resume reset the
tracker (keys typed while paused were not followed). Hook-thread cost: one `Extract` over ≤ 256 chars per Backspace
that crosses a separator.

The speech echo mirrors it: a chunk remembers the separators it followed, so erasing it and then its separator brings
the previous chunk back (`Ma c` ⌫ ⌫ ⌫ `o` + space says « Mo »; several words back; the liaison grouping `les x` ⌫ ⌫
`amis` still says « les amis »; an inserted word restored keeps its « already said » mark). Across a sentence end the
echo resets (the sentence was said): the next sentence end is then an edit (D17 amendment).

## 5. Insertion

```
 typed "maiz" (4 chars)   chosen "maison"
 SendInput: [BS↓ BS↑]×4 + [m↓ m↑ a↓ a↑ i↓ i↑ s↓ s↑ o↓ o↑ n↓ n↑ ␠↓ ␠↑]  = 22 events, KEYEVENTF_UNICODE
 every event: dwExtraInfo = 0x4C584B42  → our hook ignores it (no feedback loop)
 SendInput returned 22/22 ? tracker.Accept("maison") : tracker.Reset()   (never pretend on failure)

 caret inside a word after a click:  "la mai|son est"   (read: prefix "mai", suffix "son", next ' ')
 SendInput: [Del↓ Del↑]×3 + [BS↓ BS↑]×3 + m a i s o n          = 24 events, no space (next is ' ')
 → "la maison est"   (Delete = VK 0x2E, scan 0x53, KEYEVENTF_EXTENDEDKEY; one SendInput, Deletes first)
```

**The whole word is replaced** (decision D3): forward Deletes remove the part after the caret (known only
from a read), Backspaces the part before. **Trailing space** (`KeyboardPolicy.NeedsSpace`):

| char after the word (from the last read) | space added? | example |
|---|---|---|
| unknown (keys only, or read invalidated by a separator/jump) | yes | `la maiz` → `la maison ` (as before) |
| end of text | yes | `la mai‸son` → `la maison ` |
| whitespace (`␠ \r \n \t`), punctuation (`, . - ’ ) »`…), control (Word `\a`) | no | `la mai‸son, oui` → `la maison, oui` |
| anything else (letter, digit, symbol) | yes | `la ‸son` → `la mot son` |
| *the word is an elision* (`l'`, `qu'`… — D22) | **never** | `vers l` → `vers l'` (then `horizon` → `vers l'horizon `) |

- Unicode injection is **layout-independent** (works for `é`, `œ`, any keyboard).
- Failure is real: UIPI blocks injection into elevated windows; `SendInput` then returns < expected.
- Case follows the user: `Maiz` → `Maison`, `MAIZ` → `MAISON` (`KeyboardPolicy.Cased`); at the start of a sentence a
  lower-case or empty prefix gives a capital (`Le`, `L'`, `Qu'`; §5b).

## 5b. Punctuation, elisions and capitals (D22, plan 04)

**Elisions.** Lexique and the corpora split `l'horizon` into `l` + `horizon`, so the engine ranks the bare forms
`l d j m n s t c qu jusqu lorsqu puisqu`. `Elision.Apply` (Predict.Core) maps them to `l'`, `qu'`… **after ranking**
(display only: the DB is not rebuilt and `Predict.Eval` keeps comparing bare tokens) and drops the pseudo-word `h`.
An elision is inserted **without a space** (`NeedsSpace` → false); `WordTracker.Accept("l'")` pushes `l` into the
context and counts the apostrophe as the separator, exactly as when it is typed, so the next-word bar follows at once
(`vers` → F3 `l'` → `ouest, est, avant…`, `h` → `hôpital, horizon…`). Right after a typed `-` the bare `t` is
not mapped (`a-t-il`: `WordTracker.LastSeparator`). The speech echo does not say `l'` alone: the
next inserted (or typed) word is said with it (« l'horizon »; `SpeechEcho.LastInserted` keeps the chunk up to its last
`' ’ -`). **Ranking**: after a bare elision in the context, candidates that take one (Lexique `phon` starting with a
vowel or `w`/`8` — *oiseau*, *huile* — minus a short list of aspirated-h words, because `phon` does not mark the
h aspiré: *haricot* = `aRiko`) get `ElisionBoost` (×8). In practice the n-grams after `l d qu…` already contain almost
only such words, so the boost changes little (see evaluation.md).

**From the first letter (0.10.2).** With no word before it (a sentence start), `j` now offers `J'` next to `je` (`m'`,
`c'`, `qu'`, `jusqu'`, `lorsqu'`, `puisqu'` alike): the engine gives these bare forms a context-free prior estimated at
load (predict-core-design §4, `ElisionUnigram`); the keyboard side is unchanged (display mapping, no space, next-word bar
at once). **Apostrophe style** (owner Q2: follow the document, no setting): `ContextExtractor` returns the last `'` or
`’` before the caret (`TextContext.Apostrophe`); `WordTracker.ApostropheStyle` (default `'`) takes it from a read
(`Seed`) or from a typed apostrophe, keeps it across a digit or an untracked Backspace, and returns to `'` on every `Reset`
(every jump: click, arrow, focus or window change) until a read or a key says otherwise. Where the text cannot be read
(keys-only hosts), the style follows the apostrophe key the pupil last typed since the jump. `KeyboardPolicy.Shown` writes the bar's elisions in that
style after `Elision.Apply` (`Elision.Styled`: after « C’est vrai. », `j` → `J’`), so the tile and the inserted text
match the document. Speech is unchanged (owner Q3): a right-click on `j'` says « j' »; the inserted elision is said with
the next word.

**Our automatic space** (`AutoSpaceState`, under `_sync`): armed by an insertion that added a space, for that tracker
version in that foreground window. Every other tracker mutation (a key, a jump, a Backspace, a read) changes the
version, so it disarms by itself; a window change too.

| typed right after our space | action (`KeyboardPolicy.AfterAutoSpace`) | result |
|---|---|---|
| `, . … ) ' ’ -` | swallow; inject Backspace + the character | `maison␣` + `,` → `maison,` |
| `; : ! ?`, « Espace avant ; : ! ? » = **Française** (default) | swallow; Backspace + **U+202F** + the character | `Bonjour␣` + `!` → `Bonjour !` (narrow no-break space) |
| `; : ! ?`, setting **Aucune** | swallow; Backspace + the character | `Bonjour!` |
| space | swallow (absorbed, once; still armed) | never a double space; `maison` F1, space, `,` → `maison,` |
| anything else | passes; disarms | — |

- The injection is **one `SendInput` made on the hook thread** before the key is swallowed (as keyboard remappers
  do), not posted to the UI thread like F1–F8: a key typed right after the punctuation cannot reach the app before
  it. If `SendInput` is blocked (UIPI) nothing was injected and the key goes through unchanged. Cost: the `SendInput`
  takes 0.17–1.4 ms in the sandbox (median 0.2 ms), logged as `auto-space: … (SendInput n/n, x ms)`. The key-up is
  swallowed (`_consumed`). The tracker and the echo are fed Backspace + the injected characters.
- **Microsoft Word: adaptive rule** (owner, 2026-11-09, `AppRules`, process `WINWORD`). Word's French AutoCorrect
  may add its own no-break space before `; : ! ?` — only when its option « espace insécable » is on **and** the text's
  proofing language is French; we never read or change Word's options. So in Word (setting Française):
  1. the punctuation key: Backspace (our space) + the punctuation alone — or, when this Word window was already seen
     to add no space, our U+202F + the punctuation at once;
  2. ~60 ms later the read-back thread (a second `ReadQueue`, UIA, the 4 characters before the caret — never in the
     hook) classifies the text (`AppRules.Check`): one space-like character before the punctuation → done; none →
     insert our space before it (Left, U+202F, Right, one marked `SendInput`: the punctuation is not retyped, so
     AutoCorrect does not run again); two (Word added one next to ours) → remove one (Left, Backspace, Right);
  3. what the window did is remembered per window handle (`adds its own space: yes/no`) and corrected after every
     read-back, so a French paragraph after an English one (or the option switched) fixes itself;
  4. if anything was typed since (tracker version) or the window changed, nothing is edited (never behind the
     user); every decision is logged (`punct check '!' 14 ms: before=[…] Missing: … U+202F inserted`).
  With « Aucune » we only take our space back and Word's AutoCorrect may still add its U+00A0 — that is Word's
  option, we never change it. Limits: Word's language auto-detection can switch the proofing language while typing;
  a read-back that fails (UIA error, > 150 ms) does nothing; LibreOffice Writer is not covered yet.
  More limits of the Word rule:
  - **Undo order**: a read-back fix is a separate edit (Left, U+202F, Right — or Left, Backspace, Right); the first
    Ctrl+Z in Word undoes our U+202F (or brings back the removed space), and the Left/Right split Word's undo group
    of the typing around it.
  - **Overtype mode** (Insert key in Word): the injected U+202F would replace the punctuation instead of going before
    it; not handled.
  - **Outlook** uses Word as its editor (process `OUTLOOK`), not covered by `AppRules`: there our U+202F plus
    Outlook's own AutoCorrect space would give a double space. Outlook is not a target app (D2); it is listed with
    LibreOffice Writer as a future `AppRules` entry.
  - The edit is sent inside the controller lock, right after re-checking the tracker version and the foreground
    window, so a key typed meanwhile waits for the lock and cannot slip in between the check and the edit (our own
    marked events never take the lock; `SendInput` only queues).
- No fix while Ctrl / Alt / AltGr is held (`AfterAutoSpace(…, ctrlOrAlt)`): a Backspace under a modifier would be a
  shortcut (Ctrl+Backspace deletes a word).
- `Insert` notes the tracker version with the word it replaces; if a key reached the hook before `Accept`, the
  tracker is reset and the auto-space is not armed.
- Side effects of the rule list (only right after our space): `fait` F1 + `-5` → `fait-5`; `dit` F1 + `'bonjour'`
  → `dit'bonjour'`; ANSI (non-Unicode) windows show U+202F as `?` (use « Aucune » or the hidden `french-nbsp`).
- Text typed by hand is never re-spaced (out of scope), nor guillemets « ».
- Hidden setting (JSON only) `text.punctuationSpace = "french-nbsp"`: U+00A0 instead of U+202F, for an app or font
  that draws the narrow one as a box.

**Capitals.** `WordTracker.AtSentenceStart` = no word yet in the sentence and its start is known: a typed `. ! ? …`
or line break, or a read (`TextContext.AtSentenceStart`: the start of the text or a sentence end right before the
first context word). Unknown after a jump without a read → no capital. Abbreviations (`M. Dupont`) end a sentence:
an accepted limit.

**Tokenizer** (`Predict.Core/Tokenizer.cs`, shared with plans 06/07): words (letters, `œ æ`, joined by an internal
`-` or a non-elision apostrophe: `peut-être`, `aujourd'hui`, `quelqu'un`), elisions as their own token keeping the
apostrophe (`’` kept for display, `'` in `Norm`), numbers with a decimal comma (`1,5`), spaces (incl. U+00A0, U+202F),
combining marks inside the word they follow (NFD text; `Norm` is NFC), sentence ends `. ! ? …` (a run is one
token; closing `» ) "` right after it stay in that sentence), breaks (`\r\n` one token), other punctuation; every token keeps its
offsets in the original text. `Sentences()` gives [start, end) ranges.

## 5c. Relecture des modifications — re-read of edits (D23, plan 06)

When the user **edits existing text** and then **leaves** it, the edited word or sentence is spoken once.
Setting « Relire le mot / la phrase modifié(e) » (`speech.rereadEdits`, **on** by default, can be locked by the machine layer).

```
applied caret read ──► EditSession.Begin(window, caret, fg)            (nothing said)
keys / F-key insert / Backspace / Delete ──► mirror edited at the caret     (hook / UI thread, under _sync, O(window))
leave: click, arrows (Ctrl too), Home/End, PgUp/PgDn, Tab, window switch, focus moved inside the window
                                          ──► session.Leave()   (O(1) in the hook; taskbar / tray / our windows: Keep)
read at the new caret (reader thread) ──► snapshot under the lock ─► Classify + Decide off the lock ─► commit
     SameWord      → nothing, the session continues (word state kept)
     SameSentence  → the modified word, then a new word level (the sentence's original text is kept)
     Elsewhere     → the whole modified sentence only (a sentence cut by the window → the word only)
     Unknown       → nothing (failed / late / overtaken read, keys typed before the read)
```

- **Mirror** (`EditSession`, pure): the read window (`TextWindow`: text, caret index, cut flags, and the absolute
  offset for a Win32 Edit) with every edit applied: typed chars (Enter = CR LF in a Win32 Edit, LF otherwise),
  the insertion as `Inserter.Replace` sends it (Delete × suffix, Backspace × prefix, word + space), Backspace (CR LF and
  surrogate pairs as one), Delete. The **anchor** is the last word-level edit point; after a separator it stays at the
  end of the word just finished, so « gros␣ » typed in the middle is the modified word. A removal that crossed a
  separator (a whole word deleted) leaves no modified word — only a modified sentence. Deleting past the window edge
  marks the session untrusted (`Dirty`, never spoken).
- **Sentences** come from `Tokenizer.Sentences` (`. ! ? …` + closing `» ) "`, line breaks); other control characters
  (Word's cell mark BEL U+0007, U+FFFC) are turned into LF by `TextWindow.Of` (same length). A caret in the spaces after a
  sentence belongs to it; a caret at the start of the next one to the next.
- **Middle edit**: the session speaks only if, when it began (or at any rebase in the same sentence), the caret was
  strictly inside a word, or followed in its sentence by something other than spaces and the final `. ! ? … » )`,
  or before that final stop when more text follows the sentence (or the document goes on past the window): fixing the
  last word of a sentence that is not the last one is an edit. Only typing at the very end of the text (or after its
  final period) is not re-read — the word / sentence echo (D7) says it (both on by default since 0.8.3; before, with
  the default settings only an inserted word was spoken there).
- **Classify** (`EditClassifier`, pure): same foreground window and the same sentence **key** (non-space tokens,
  normalised: Word's own no-break space before `!` or a curly apostrophe does not change it); in a Win32 Edit the
  absolute sentence start must match too (« Oui. Oui. » are told apart; with UIA they are not — accepted). Then the
  word index of the caret in the read vs the modified word's index decides SameWord / SameSentence. A different
  foreground window is `Elsewhere` whatever its read gave (the new window may be unreadable).
- **Read window** widened to the sentence bounds: Win32 `WM_GETTEXT` of `caret + 300 + 2` (it copies from the start
  anyway), window = 600 before / 300 after; UIA first −200 / +65 as before, then — only when that window has no
  sentence end / break on a cut side — a second move to −600 / +300 (clamped to the element). Still one read, same
  150 ms budget.
- **Focus moved inside the window** (`EVENT_OBJECT_FOCUS`, out of context, on the hook thread's message loop): an
  in-page dialog with an autofocused password field, a two-step login (Enter → password field) are leaves like a
  click: `Jump("focus", 50, Leave)` re-arms the field probe and asks a read, so keys typed in the new field are
  never placed in the mirror (they arrive while the session is left: `TypedWhileLeft`) and a `Password` read drops
  the session. Filter (`FocusFilter`, pure): only objects of the foreground window, a different object than the last
  one, no mouse button held (the click's own read follows), not within 200 ms of a window switch (its first focus);
  other windows cost one `GetForegroundWindow` + `GetAncestor`.
- **Field probes**: the first-character probe and the edit probe — any answer other than `NotPassword` (Password, or
  Unavailable, which fails open for the bar) ends the session: what is being typed may be in a password field, so it
  is never spoken. A begin / rebase is installed only if the tracker version is still the one of the read.
- **Taskbar, tray, our windows** (`Shell_TrayWnd`, `NotifyIconOverflowWindow`, `TopLevelWindowForOverflowXamlIsland`,
  the bar, Réglages, À propos, the tray menu): a click or a switch there is `Keep` — nothing read, nothing said; the
  return to the text is classified as usual.
- **Delete** with a live session is an **edit**, not a leave: the mirror deletes forward, the tracker still re-reads
  (reason `del`) and the read rebases the mirror on the app's real text. **Ctrl+key** (paste, undo, Ctrl+Backspace,
  Ctrl+Del), **Shift+Del** and **Insert** (overwrite mode) end the session silently; **Esc** keeps it. Delete after a
  leave (before its read) only marks the session typed-while-left. A mirror longer than 4 × 900 characters is no
  longer followed (`Dirty`).
- **No double speech**: when the word echo or the « inserted » echo says the modified word, the session marks it
  spoken and a move inside the sentence does not repeat it; leaving the sentence says the sentence only, never the
  word first.
- **Speech and privacy**: `SpeechKind.Reread`, queue policy of `Sentence` (drops pending words, never interrupts). It
  does not go through the `EchoGuard` (which drops anything once the foreground changed, so it would silence the
  « another window » case). Privacy rests on four rules instead: (1) a session only begins from a read that checked
  `ES_PASSWORD` / `IsPassword` before reading any text; (2) every way the focus can leave the field — keys, clicks,
  window switches **and focus moves inside the window** (`EVENT_OBJECT_FOCUS`) — freezes the mirror, so keys typed
  elsewhere never enter it; a leave while the first-character probe is still pending drops the session; (3) **edit
  probe**: while the caret is still in the field, every burst of edits is followed by a password probe of the focused
  field (one in flight at a time, on its own thread; asked again for the edits made meanwhile) — anything but
  `NotPassword` drops the session, and a re-read is spoken only when **every** edit in the mirror is covered by a
  `NotPassword` answer (the reader waits up to 150 ms for the probe of the last key); this also covers an app that
  moves the focus silently, since the probe sees the field the keys actually went to; (4) a `Password` read, a
  first-character probe that is not `NotPassword`, Enter while edits are still unproven (it often moves the focus),
  and password suppression drop the session. Edit probes are debounced: at most one per 150 ms while typing (the
  request is made at the key, it runs in its slot), one log line per answer with its latency.
- A leave right after fast edits can end in `reread skip(field not proven)` (the probe of the last keys did not answer
  within the 200 ms wait): the re-read is then silent by design. Nothing while paused: `OnPaused` drops the session, the commit re-checks
  `Enabled` under the lock, and the speech thread drops a `Reread` that arrives while paused. Setting off: nothing.
- **Read budget of the re-read**: the 150 ms timeout protects the tracker (a late read may describe a caret the user
  has moved); for the session the version check does that (any key, click or jump changes the version), so a read
  that reached the text in at most 400 ms (`KeyboardPolicy.RereadReadBudgetMs`), with nothing typed since its request
  and the same window in front, still begins or lands a session (log `-> keys (used by the re-read)`). Owner run 2:
  the begin read of a 700-character Word paragraph took 158 ms and the edit was not re-read.
- **What is said is the document's text when the landing read shows it** (owner run 2: Word's AutoCorrect wrote
  « très » where « tres » was typed; the mirror's « tres » was mispronounced). Same sentence: the read's word at the
  modified word's index; another sentence of the same window: the one sentence of the read window with the same words
  up to AutoCorrect (`DocText`: equal, equal without accents, or one letter changed for words of 4+ letters; with Win32
  offsets at the same place); none or several → the mirror's text. Only the landing read of the same window is used —
  a read the session trusts. For **sentences** the comparison is accents / case only (« tres » / « très »): a sentence
  one letter away is another sentence (« Il mange. » / « Il range. »), and without Win32 offsets no near copy is
  taken; the one-letter rule only picks the document's spelling of a word, or of a sentence whose place the offsets
  prove. A landing in a sentence AutoCorrect changed that way still counts as the same sentence.
- **Slow UIA reads** (> 40 ms) log their phases (`uia read phases: focus+pattern … selection … window … widen …`).
- **First read after a start**: in Word the first UIA read was cold (383 ms, over the 150 ms budget: no session, the
  first edit not re-read, owner run 1). The reader's warm-up now runs the same TextPattern calls once on the focused
  element (selection, document range, endpoint moves, `GetText(0)` — no text read; skipped for a password field), each
  step on its own so a failing `FocusedElement` no longer skips the rest.
- **Log** (`--log` only): `reread word|sentence landing=… <reason> <ms> (<n> chars)` or `reread skip(<reason>)` —
  never the text; the speech thread's `speak Reread '…'` line has it, then `speech started` (SAPI `SpeakStarted`).
- **Threading**: the hook only does O(1)/O(window) mirror edits under the existing lock; the session is copied
  (`EditState`) under the lock, tokenised and classified on the reader thread (`RereadFlow.Plan`, pure), and committed
  under the lock (`RereadFlow.Commit`, pure) only if nothing replaced it and no newer leave happened (leave counter);
  otherwise the newer read decides.

## 5d. Lecture de la sélection — reading the selected text (D21/D24, plan 07)

While text is **selected** in another application, the bar switches to its **reading view**: one tile per word with its
picture above (D4/D4b: the same pack and exclusions, an empty slot when there is none), punctuation shown without a
picture, tiles wrapping over several lines in the bar's size, font and letter spacing (D16), a toolbar ▶/❚❚ · ⏮ · ⏭ ·
⤢ (« grande fenêtre »). **No autoplay**: nothing is said until Play. Reading = one utterance per sentence, continuing to
the end; the **current word only** is highlighted (yellow tile, black text, orange frame), **in our bar only** — the
source application is never touched.

```
 caret read (click / drag / double click, Shift+nav, Ctrl+A, window switch) ── status Selection ──►
   SelectionReader (own MTA thread; Shift+nav: 250 ms after the last key) ── password check first ──►
     Win32 Edit: EM_GETSEL + WM_GETTEXT · UIA: TextPattern.GetSelection (focused element, else its nearest
     ancestor with a text pattern: static text of a web page) · ≤ 20 000 characters
     no text pattern + a selection gesture + setting on ──► simulated Ctrl+C (ClipboardCopy)
   ── UI thread (only if the tracker version and the window are unchanged) ──► SelectionPolicy.Decide
        Show → ReadingDocument.Build (≤ 1000 words) → bar.ShowReading → ReadingController.Show (Ready)
        Keep (same trimmed text) · Exit (empty) · Ignore (no reading mode, or the selection dismissed with Esc)
 Space / F9 / ▶ ──► ReadingController ──► Speaker.Read(sentence) ── SAPI SpeakProgress (pos, len) ──►
   UI thread (BeginInvoke at Send priority, with the utterance id) ──► ReadingDocument.TileAt ──► highlight
```

- **Selection boundaries (D26, owner 2026-11-11)** — for the reading only, the selection in the application is never
  changed (`SelectionExtend`, pure). From up to 48 characters read on each side **in the same field** (Win32 Edit: the
  same `WM_GETTEXT`, by offsets; UIA: the first / last selected range expanded by characters and clamped to the pattern's
  document range; that context is dropped when its range is, or its enclosing element contains, a password element), a word cut at the start or
  the end is completed (« Le ch[at bien] » → « chat bien », « [Le chat do]rt » → « Le chat dort »; a word glued to an
  elision is completed with it: « l'a[mi] », « l'[ami] » → « l'ami » — one reading unit, one tile); a cut run of sentence
  ends is completed; and when the selection ends on a sentence's last word, the sentence end that follows is added —
  `. ! ? …` and their runs (`?!`, `!!!`), spaces before it, closing `» ) " ”` right after — never `; : ,`, nothing when the
  selection already ends with its sentence end. The added punctuation is drawn **on the last word's tile** (« bien ! »,
  `ReadingDocument.Build(text, max, tailAt)`), and is part of the utterance (intonation). The **Ctrl+C fallback** has no
  context: what was copied is read exactly as selected. Log: `selection: completed (D26) +a before, +b after[, sentence
  end added]`.
- **Model** (`ReadingDocument`, pure): `Tokenizer` tokens with their offsets; an elision is joined to the word after it
  (`l'arbre` = one tile, picture of `arbre`; a lone `l'` has none); numbers have no picture; spaces and line breaks are
  not tiles. Sentences = `Tokenizer.Sentences`; a sentence of punctuation only is skipped by Play / ⏮ / ⏭. **1000 words**
  (D24): beyond, the text is cut before the 1001st word (an elision with it) and « Texte trop long, seuls les 1000
  premiers mots sont lus » is shown above the tiles. Utterance = the exact span of the sentence (from its start, or from
  the highlighted word after a pause), with one-for-one replacements only (no-break / narrow spaces, line breaks → space,
  `’` → `'`), so an engine position maps back by adding the utterance's start (`Utterance.ToDocument`).
- **Engine position → tile** (`TileAt`): the word tile containing the position; else the first word tile starting inside
  [pos, pos + len) (an event on the space before a word); else keep the current highlight (punctuation). A number the
  voice expands (« 2026 » → several events) stays on its tile.
- **Highlight timing with Piper (plan 08a)**: the view is unchanged — still driven by the **arrival** of progress
  events. With the Piper voice the events are raised by the `piper-clock` thread when the WASAPI device position
  (`GetPosition`, converted to the voice's 22 050 Hz timeline) passes the first sample of the word; the word starts come
  from the model's alignment output (`/Ceil_output_0` × hop 256, grouped per phoneme as Piper does) and espeak's word
  boundaries (a liaison consonant stays in the left word: « amis » starts after the /z/ of `le-z`). A number espeak
  expands (« 2026 » → 3 words) gets one event; when the word counts do not add up, units are placed in proportion to
  their characters (`piper: word map fallback (W espeak words, U units)`). Words starting with a stop may light up a
  little early (plan 05: 89 % within the window against Whisper); no lead compensation in 08a.
- **Highlight timing (SAPI)**: driven by the **arrival** of each SAPI `SpeakProgress` event, never scheduled from its
  `AudioPosition`: System.Speech gives audio positions in the voice's native format (voice-evaluation.md § 2: at a
  22.05 kHz output every time came out ×1.378), so a schedule built on them would drift. With the default audio device
  the events arrive on the audio timeline: in the sandbox (Hortense) wall-clock / audio-position slope per utterance
  0.984–1.005, the first event 110–290 ms after the sentence is handed to the speech thread (engine start).
- **State machine** (`ReadingController`, pure): Idle → Ready (tiles) → Playing ⇄ Paused → Ready; each utterance has an
  id, a progress or completion of an older one (cancelled, replaced by ⏮ / ⏭, after a click) is ignored. ⏮ / ⏭ move the
  sentence cursor while playing (the new sentence at once; at the first / last sentence it is said again) and while
  stopped (its first word is highlighted). Pause resumes at the highlighted word. A **tile click** stops the reading,
  says that word (`SpeechKind.Manual`), and the next Play starts at its sentence. After the last sentence: Ready, the
  next Play starts at the top.
- **Speech**: `SpeechKind.Reading` interrupts (like Manual / Inserted); `ISynth.SpeakAsync(text, done, progress)`;
  every item gets `Done` once (spoken, cancelled, or dropped from the queue unspoken). While the reading view is shown
  the typing echo and the re-read (Word, Sentence, Reread) are dropped; while paused (tray) a Reading item is dropped.
- **Exit → predictions**: typing, Backspace, Enter, Delete and Esc leave at once (hook → UI thread); a later caret read
  without a selection (a click elsewhere, arrows, another window, a password field, a failed read) leaves too; tray
  Pause and the setting off leave. Predictions stay hidden while reading (`Update` returns early; `_suggCount` = 0, so
  F1–F8 go to the application). A click on the bar is not seen by the hook (`Contains`); clicks and focus on our own
  windows (« grande fenêtre », Réglages) keep the reading mode. **Esc** dismisses that selection: the same text in the
  same window is not shown again (e.g. a Ctrl+C right after) until a caret read finds no selection or the selection
  changes.
- **Space / F9** (D24): swallowed — key-down and key-up (`_consumed`), the auto-repeat without toggling again — only
  while the reading view is shown, **the window whose selection is shown is in front** (`ReadingMode.ReadingWindowHandle`,
  set at Show and at Keep, read by the hook) and no modifier (Ctrl, Alt, Shift, Win) is held; otherwise they reach the
  application (after Alt+Tab to another window, Space types a space there).
  In the « grande fenêtre » (our window has the keyboard) the window handles them itself (Esc closes it).
- **Placement** (D9): the bar's rules — at the caret (the selection's active end: Win32 caret or UIA selection rectangle,
  under it or above it near the screen bottom), or docked / free. Wrap width 60 % of the work area; the tile area scrolls
  beyond 40 % of its height and scrolls to the highlighted word; the bar is re-placed once its last tiles are built.
- **Lazy tiles**: the first 40 synchronously, the rest in batches of 40 at Background priority (each batch's build +
  layout logged; ≤ 50 ms). Pictures: one `PictureStore.Request` per document (every distinct word, text order), shared by
  the bar and the large window (a second request would cancel the first: the store serves only the newest generation;
  the generation counter is shared with the suggestion bar). Tiles are templated `Button`s (UI Automation Name = the
  word, Invoke), not focusable.
- **Warm-up**: the bar warm-up also shows a 132-tile reading view off-screen. Without it the first real reading view
  took 470–560 ms in the sandbox (`view` phase: the scroll bar of an overflowing tile area is created on first use), with
  it 26–48 ms.
- **« Grande fenêtre »** (`ReadingWindow`): a normal, activating, resizable, topmost window with the same view; it
  follows the word being read; Space / F9 = Play / Pause, Esc closes it; its place and size (physical px) are saved in
  `reading.window` when it closes and restored (if still on a monitor). Resizing a non-activating window goes through
  the system size loop, which activates it anyway (§6, the drag of the bar), hence a normal window; the controller treats
  it as one of our windows (Keep).
- **Selection triggers and the Ctrl+C fallback**: a left button-up after a drag (> 4 px) or a double / triple click, Shift
  + navigation and Ctrl+A mark the tracker version as a « selection gesture » (hook, O(1)). When the window's own read
  finds no text pattern (`Unavailable`) after such a gesture and the setting « Copier la sélection par Ctrl+C… » is on
  (**off by default**), `ClipboardCopy` runs on the selection thread, the flag carried by that request: never in a
  console or terminal — by window class (`ConsoleWindowClass`, `CASCADIA_HOSTING_WINDOW_CLASS`, PuTTY, mintty,
  `VirtualConsoleClass`, `org.wezfurlong.wezterm`…) or by process image (WindowsTerminal, OpenConsole, conhost, putty,
  kitty, mintty, wezterm-gui, alacritty, Hyper, Tabby, MobaXterm, SecureCRT…) — Ctrl+C there is an interrupt; one Run at a
  time in the process (a lock; the selection worker's « stuck » limit is above the worst Run, ≈ 3.7 s, so a replaced worker
  never overlaps a new one on the clipboard); password probe of the focused field first (anything but NotPassword: no copy); waits
  ≤ 300 ms for Shift / Ctrl / Alt / Win to be released; refuses when the clipboard holds data that cannot be put back
  exactly (metafiles, owner-display, GDI-private formats; bitmaps / palettes are fine when a DIB is there); refuses when
  the owner does not answer `WM_NULL` within 100 ms (it may have to render delayed data); saves every HGLOBAL format
  (≤ 32 MB); re-checks that the same window is in front; sends Ctrl+C with our marker (the hook ignores it); waits ≤ 300 ms
  for `GetClipboardSequenceNumber` to change and reads `CF_UNICODETEXT`. **Once the snapshot is taken the user's content
  is put back whenever the target application's copy replaced it**: the restore runs in a `finally` (an exception after
  Ctrl+C cannot skip it), is retried (8 × 50 ms) while another application holds the clipboard open, a copy that lands
  after the 300 ms is watched for 1.5 s more and undone too (its text is not used), and a second write by the application
  within 200 ms after the restore is undone once more. **Never over someone else's change**: the restore happens only when
  the clipboard's owner window belongs to the target window's process, and when the user pressed no copy / cut chord of
  their own (Ctrl+C, Ctrl+X, Ctrl+Insert, Shift+Delete — physical keys, time noted by the hook) after our Ctrl+C. A copy
  the user made, another application's copy, a password manager clearing the clipboard (which a restore would undo,
  bringing the password back) are left as they are — logged `the user copied meanwhile: not restored` / `changed by another
  application: not restored`. An application that hands its copy to another process (delayed rendering by a helper) is
  not restored either: the safe direction. When the sequence never changed (the application ignored Ctrl+C) nothing is
  restored, because nothing was touched. The restored data is owned by a message-only window of
  the UI thread (a non-pumping owner would hang the next application that empties the clipboard) and carries
  `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory` = 0 and `CanUploadToCloudClipboard` = 0,
  so it is not added again to the clipboard history nor the cloud clipboard. **The application's own copy** (made by its
  Ctrl+C handler) cannot be hidden: it may appear in the history (Windows+V) — said in the setting's tooltip. Log:
  `selection: Ctrl+C fallback: copied in N ms, K format(s) restored[ (try n)]` or `… a late copy after N ms, … restored`
  (never the text).
- **Character limit**: the reader takes at most 20 000 characters; a longer selection (fewer than 1000 words is possible
  with long words) is cut at its last space, marked cut, and the « Texte trop long… » message is shown.
- **Settings** (`reading.*`): `enabled` (true), `clipboardFallback` (false), `pictures` (absent = follow `words.pictures`),
  `window {x, y, w, h}`; Réglages section « Lecture » (three check boxes, lockable by the machine layer). The wrap width
  and scroll height come from the work area of the monitor the bar goes to; a font / spacing / size change re-warms the
  reading tiles too.
- **Log** (`--log` only): `selection read <reason> src=<win32|uia|clipboard> status=… N ms (C chars)`, `reading shown: W
  words, T tiles, S sentences, … show X ms (model+pictures+caret, tiles, WPF Show(), other, place), Y ms after the selection gesture`,
  `reading view bar|window: …`, `reading: state …`, `reading: play/pause (Space|F9|button|large window key) -> …`,
  `reading: sentence n id=i (c chars from offset o)`, `reading progress id=… pos=… len=… audio=… ms wall=… ms engine='…'
  tile=t '…' highlight +x ms`, `reading: exit (<why>)`; the speech thread's `speak Reading '…'` has the sentence.

## 6. The bar window

- `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`, plus `WM_MOUSEACTIVATE → MA_NOACTIVATE`: clicks
  are delivered to the buttons but focus stays in the user's app, so `SendInput` lands there.
- **Placement**: 6 px under the caret's bottom-left corner, clamped to the monitor work area; flipped above
  the caret near the bottom edge. Coordinates are physical pixels (Per-Monitor-V2 DPI manifest).
- **Colours = why it was proposed** (`MatchKind`): blue = completion, green = same sound, orange = typo/inversion.
- Tooltip below the bar, 1.2 s delay, so it never covers the words nor the line being typed.
- **Pictures (D4/D4b, plan 02)**: each button = F-label 11 pt, a **64 × 64 DIP slot** (corner radius 4,
  margin 0,2,0,4, padding 2), the word 22 pt — top to bottom. With a pack every button gets a slot, with or
  without a picture (bar height constant, late pictures never move the bar); the slot turns **white** when a
  picture is shown (ARASAAC's black outlines are unreadable on blue/green/orange). Button width = max(word,
  64 DIP) + padding. Without a pack (`--no-pictures`, missing file) there is no slot: the plan 01 bar.
  Pictures are 128 px, i.e. 1:1 at 200 % scaling; `BitmapScalingMode=HighQuality`, `UseLayoutRounding`.
  Bar height ≈ 137 DIP with pictures (67 without); `PlaceAt` already flips it above the caret near the
  bottom; sizes S/M/L/XL and a docked mode come with D9.
- **Timing (sandbox, software rendering; warm-up excluded)**: continuous typing, 80 bars: `ShowSuggestions`
  build+show 1.39 ms mean (max 6.2) with pictures vs 1.10 ms (max 5.8) without → **+0.3 ms** per update.
  Review-fix run: 60 bars on an already visible window 1.48 ms mean (max 10.0); 5 bars that had to **re-show
  the hidden window** (first letter after Esc / a click / an empty prediction) 12.2 ms mean (max 17.9) —
  the log marks them `(window re-shown)`. That cost is WPF's `Show()` under software rendering, not the
  pictures: the same first-letter bars measured 17.5–18.9 ms **without** a pack. First picture of a bar
  ready **15 ms** mean, **31 ms** max after the show (47 ms on the very first, cold bar); 0 `slow UI` lines.
- **Warm-up**: the first WPF window show is expensive (measured **≈ 9 s** under software rendering in
  Windows Sandbox; typically well under a second on real GPUs). `WarmUp()` shows the bar once
  off-screen at startup, so the first real suggestion is instant. Before this fix, the first prediction
  appeared 9 s late and a click during that time reset the tracker. With a pack, the warm-up also decodes
  and draws one real picture (`maison`), so the imaging pipeline's first use is paid at startup too.
- **Render mode** `--render-bar <png> --dpi N [--size] [--no-keys] [--font] [--spacing] words…` lays the bar out
  off-screen and saves it with `RenderTargetBitmap`: no hook, no tray, nothing injected (safe on the developer's
  host). The `.txt` beside the PNG records the DIP size, the font file actually used, its line height and the
  `missing glyphs:` of the French test string (+ capitals and `' - ’`); `--probe-fonts` lists what WPF sees in
  each `fonts\<Family>` folder.
- **Sizes (D9, D17)** — `BarMetrics`, font sizes in WPF units:

  | size | F-label | word | picture slot | button padding |
  |---|---|---|---|---|
  | S | 9 | 16 | 48 DIP | 8,2,8,4 |
  | M (iterations 1–2) | 11 | 22 | 64 DIP | 12,3,12,6 |
  | L | 13 | 28 | 80 DIP | 14,4,14,7 |
  | XL | 16 | 36 | 96 DIP | 16,4,16,8 |

  Pictures stay 128 px (XL at 200 % upsamples 1.5×, HighQuality). F-labels stay Segoe UI (chrome); only the word
  uses the chosen font. F-keys off → no F-label at all. Pictures off → no slot (the pack stays open).
- **Positions (D9)** — `BarPlacement.Compute`, physical px, the result always kept inside the work area used:
  *caret* = the rule above (no `Caret.Get()` in the other modes); *top* / *bottom* = centred on the work area of
  the **foreground window's monitor**, 8 px from the edge; *free* = the saved top-left, clamped into the monitor
  that holds it (or that the bar overlaps most), else bottom-centre of the primary monitor. Docked and free
  bars are hidden when there is no suggestion, like the caret bar (owner decision 2). A bar **wider than the
  monitor** (XL + wide spacing + 8 long words) drops words from the end until it fits; `_suggCount` and
  `Insert` follow `VisibleCount`, so F*n* never inserts an invisible word.
- **Free position grip**: a 14 DIP border with six dots (shapes, no font), `AutomationId=Grip`, cursor
  SizeAll, visible only in free mode. A **manual drag** — mouse capture on the grip, `SetWindowPos(SWP_NOACTIVATE)`
  on every move, position saved on release (`SettingsService.SetFreePosition`). `Window.DragMove` was tried
  first: in the sandbox its system move loop **activated** the bar (the foreground WinEvent reported our
  window: bar hidden, tracker reset), the risk the plan foresaw; with the manual drag the user's application
  keeps the focus and the typing continues (G6).
- **Look changes** (`Configure(BarLook, position, free)`): a visible bar is rebuilt in place; a font / spacing /
  size change re-warms the new look off-screen at Background priority (`font warm-up 4–7 ms` in the sandbox).

## 6b. Tray icon, menu and promotion (D5, plan 02)

- **Icon** (0.6.1): the « pépite » cookie logo, own drawing (`branding/pepite.svg`; `Data.Build/make_icon.py` redraws
  it: a bitten two-tone cookie, chocolate chips, an elongated chip for the caret; up to 32 px a simplified drawing —
  one deep bite, three larger chips and the caret chip — so it stays recognisable at 16 px), embedded as
  `Assets/app.ico` (16, 20, 24, 32, 40, 48, 64 px DIB + 256 px PNG) and `app-paused.ico` (grey cookie). Loaded at
  `GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem())` (16 px at 100 %, 20 at 125 %, 24 at 150 %, 32 at 200 %).
  Also the exe's `ApplicationIcon`, the windows' icon and the logo of À propos and the welcome window.
  *(Up to 0.6.0: slate square with three coloured pills, « Prédiction ».)*
- **Tooltip**: « Pépite — F1–F8 pour insérer » / « Pépite — en pause » + grey icon while paused; with teacher
  management on, the active profile's name: « Pépite (Léa) — F1–F8 pour insérer » (≤ 127 characters; plan 09).
- **Keyboard**: Win+B → arrows to our icon → Enter/Space opens the same menu **at the icon** (the Shell
  reports the keyboard selection to the version-0 `NotifyIcon` as a button message; the origin is taken from
  `Shell_NotifyIconGetRect` when the cursor is not on the icon). Sandbox: Win+B, Right, Enter → menu next to
  the icon — the Shell itself moved the cursor onto the icon (log `tray: menu at mouse …`), so the
  `Shell_NotifyIconGetRect` branch is a fallback that the sandbox run never needed (not exercised).
- **Menu** Pause/Reprendre · Réglages… · À propos… · Quitter, the same on **left and right** click; with teacher
  management on (plan 09, §6f) « Profil : Léa » (greyed) · « Changer de profil… » first and « Espace enseignant… »
  after Réglages. It is a **native
  popup menu** (`SetForegroundWindow(owner)` + `TrackPopupMenuEx(TPM_RETURNCMD)` + `WM_NULL`, owner = a
  hidden top-level `NativeWindow`), not the planned WinForms `ContextMenuStrip`: in the sandbox the strip's
  first show after every start ignored all mouse clicks (only Esc closed it), even with its handle created
  in advance; the native menu worked on the first click after a fresh start, left and right (3/3).
- **Promotion next to the clock** (`TrayPromotion`; undocumented mechanism, community sources only):
  Windows 11 records each icon under `HKCU\Control Panel\NotifyIconSettings\<id>` with `ExecutablePath`
  (a literal path, or a known-folder GUID prefix such as `{F38BF404-…}\explorer.exe`, expanded with
  `SHGetKnownFolderPath`) and a DWORD `IsPromoted`. **Probe (Windows 11 24H2 sandbox, 2026-11-08)**: our
  subkey appears ≈ 4.5 s after `Start-Process` (that includes the app's own start; the poll, started right
  after the icon is added, found it after 31 ms) with no `IsPromoted` value (icon in the overflow); setting
  `IsPromoted=1` moves the icon next to the clock **at once** (< 3 s; no `Visible` toggle, no Explorer
  restart), `0` hides it again at once; the subkey survives the process. So no refresh is done. At startup a
  background task polls every 250 ms for ≤ 5 s for the subkey whose path matches the exe (case-insensitive),
  sets `IsPromoted=1`, then writes `%APPDATA%\PredictKeyboard\state.json` → `trayPromoted[<exe path>] = true`
  (now `state.trayPromoted` in settings.json; the flag is per exe path, so `Pepite.exe` is promoted once again).
  If our subkey already carries an `IsPromoted` value (0 or 1: the user's or Explorer's choice), nothing is
  written — only the flag, so it is never revisited.
  Once the flag exists the promotion never runs again for that exe path: a later "hide" by the user is
  respected. Not found → nothing written, retried next start. Never restarts Explorer, never touches other
  subkeys; failures are logged and swallowed; nothing happens where the key does not exist (Windows 10).
- **À propos** (`AboutWindow`, one instance, activating, NoResize): logo, « Pépite — prédiction de mots », « © 2026 Les
  auteurs de Pépite », « Site : https://github.com/pepite-labs/pepite » (`Strings.SiteUrl`, one
  constant; D18: no personal name or e-mail), version from
  `AssemblyInformationalVersion` (csproj `<Version>0.2.0</Version>`), tagline, the ARASAAC attribution (pack
  meta `attribution_fr`, else the same verbatim constant), links arasaac.org + CC BY-NC-SA 4.0, pack date
  (« Pictogrammes redimensionnés ; pack généré le … ») or « Images : désactivées (pack absent / option
  --no-pictures) », Lexique 3.83 credit + link. Links open the user's browser through the shell
  (`UseShellExecute`); the app itself has no network code (D13).

## 6c. Settings (D8, D11, iteration 3; profiles D29, plan 09A)

```
 builtin defaults (AppSettings())
   < machine.defaults   %PROGRAMDATA%\Pepite-Prediction\settings.json  { "schema":1, "defaults":{…}, "locked":{…} }
                        (else an older, trusted %PROGRAMDATA%\PredictKeyboard\settings.json: the first TRUSTED file wins,
                        so a file a pupil plants under the new name cannot hide the administrator's old one)
   < « Classe »         %APPDATA%\Pepite-Prediction\profiles\classe\profile.json → settings  (a pupil's profile only)
   < the profile        %APPDATA%\Pepite-Prediction\profiles\<id>\profile.json → settings     { "words":{…}, "bar":{…}, … }
   < exam overlay       (9C, not yet)
   < machine.locked
 = effective (AppSettings.Validated: every field clamped / defaulted on its own)  + lockedPaths (e.g. words.size)
```

Up to 0.6.1 the user layer was `%APPDATA%\PredictKeyboard\settings.json`; 0.7.0 migrates it once into the profile
« Mon profil » (§6f). The bullets below describe the layer of the active profile ("user layer").

- **JSON** (camelCase, enums as short strings so an admin can write it by hand): `words.count` 1–8, `words.size`
  S/M/L/XL, `words.pictures`, `words.keys` `F1-F8`/`off`, `words.font` `segoe-ui|opendyslexic|lexend|andika`,
  `words.spacing` `normal|wider|widest`, `bar.position` `caret|top|bottom|free`, `bar.free {x, y}` (physical px),
  `text.punctuationSpace` `french|none` (+ hidden `french-nbsp`, D22),
  `words.spellingIndicator` (plan 10B, on by default),
  `speech.{speakInserted, speakWords, speakSentences, voice, rate −10..10, volume 0..100}` (the three echoes on by
  default — `speakWords` / `speakSentences` since 0.8.3, owner 2026-11-15), `lock.{enabled, pin}`
  (0.6 only, unused; the migration moves a `lock.pin` to `teacher.json`);
  `state.trayPromoted` and `state.managementEnabled` are app state (`settings.json` at the root), outside the layering.
- **Only what the user changed is written** (`SettingsLayering.NewUserLayer`): Enregistrer or a drag compares the
  edited settings with the effective ones and writes only the changed, non-locked leaves into the previous user
  layer (the active profile's file, `ProfileStore.SetLayer`); a changed leaf equal to the baseline (built-in + machine
  defaults + the « Classe » layer of a pupil + locked) is removed instead — a pupil who chooses the class's font
  inherits it again, and a later change of the class reaches every pupil who did not choose otherwise. Saving therefore never freezes the defaults of the day: when an administrator later changes
  `defaults.words.count` 5 → 3, everybody who did not choose a value different from the default gets 3 (sandbox F3: user file
  `{"words":{"size":"L"}}` only, count 3 after the change).
- **Store** (`JsonFileStore`, the same for `settings.json`, `index.json`, `teacher.json` and every `profile.json`):
  one small file read at startup (UI thread, logged in ms); the whole document is kept as a
  `JsonObject`, so keys a later version adds survive a save by this one. Writes go through a background task
  chain (roaming / redirected `%APPDATA%` in schools), atomic (`.tmp` → `File.Move(overwrite)`). Bad JSON →
  renamed `settings.json.bad-<yyyyMMdd-HHmmss>`, defaults, log; if it cannot be moved aside (locked by another
  process, ACL) it is left untouched and nothing is saved this session. Each write is flushed to disk
  (`FileStream.Flush(true)`) before the rename. `state.json` (iteration 2) is migrated into
  `state.trayPromoted` once and deleted only after the new file is written (a failed write keeps it and retries).
- **Locked values are never written**; a value the user had for a path that is now locked stays in their file
  but has no effect.
- **Trust rule of the machine file**: by default `C:\ProgramData` lets a standard user **create** files, so a
  planted file could lock settings for every account. The file is used only when its **owner** is
  BUILTIN\Administrators (S-1-5-32-544), SYSTEM (S-1-5-18) or TrustedInstaller; otherwise
  `machine settings ignored (owner S-1-5-21-…)`. Also rejected (`MachineLayer.Untrusted`, unit-tested with
  injected security data): the file or its folder being a **reparse point** (link to a user-controlled place), a
  **folder** owner that is not trusted, an *allow* entry giving a non-trusted SID write / append / delete / change-ACL
  / take-ownership on the **file**, or delete-child / delete / change-ACL / take-ownership on the **folder** (creating
  new files in the folder, which the default ProgramData ACL grants to Users, is harmless). Creating it with an
  admin-only ACL is the installer's job (plan 12: `/CONFIG=` or its « Configuration de l'établissement » page — owner
  Administrators, no inheritance, Administrators and SYSTEM full control, Users read; § 8). By hand: create the machine file in place from an elevated
  prompt (it then inherits the folder's protection), or reset a copied file's ACL with `icacls <file> /reset` —
  a file *moved* from a user folder keeps its old owner and ACL and is rejected. Sandbox: Administrators-owned file and folder → used; folder
  owned by the user → `folder owner S-1-5-21-…`; `icacls /grant Users:(W)` on the file → `S-1-5-32-545 may write
  the file`.
- **Window**: edits a working copy; the preview is the real `BarBuilder` buttons; Enregistrer → `Apply` (save,
  `Changed` → controller word count / keys / echo flags, bar look and position, speaker voice); Annuler, Esc or
  closing discard. Locked controls are disabled with « Réglé par l'administrateur ». Checkboxes listen to
  `Checked`/`Unchecked`, not `Click` (UI Automation's Toggle and the keyboard raise those too).
- **Adult PIN**: designed here in iteration 3 (`lock.pin`, 4–8 digits, Réglages only), superseded by D29's teacher
  PIN (4–6 digits, `PinGate`, §6f). Every way into Réglages goes through `ISettingsGate.Open(PinReason, …)`.

## 6d. Fonts and letter spacing (D16, iteration 3)

| family | files shipped (unmodified) | source, version | weight used for words | SHA-256 (first 16) |
|---|---|---|---|---|
| Segoe UI | — (Windows) | system | SemiBold (as before) | — |
| OpenDyslexic | `OpenDyslexic-Regular.otf` 215 772 B, `-Bold.otf` 230 716 B (CFF), `OFL.txt`, `FONTLOG.txt` | forge.hackers.town/antijingoist/opendyslexic tag **v0.91.12** (2019-10-17); the archived GitHub repo serves identical bytes | Regular (letter shapes already heavy; owner decision 6) | `c1a0cbf814a75a61…` / `0901b984a2c3fdc6…` |
| Lexend | `Lexend-Regular.ttf` 100 264 B, `Lexend-SemiBold.ttf` 102 264 B, `OFL.txt` | github.com/googlefonts/lexend commit **7894f02** (2023-03-02), static TTFs (WPF draws only the default instance of a variable font) | SemiBold | `e2082c28389e9871…` / `204875848cc79812…` |
| Andika | `Andika-Regular.ttf` 802 572 B, `Andika-SemiBold.ttf` 812 808 B, `OFL.txt`, `FONTLOG.txt`, `README.txt` | software.sil.org **Andika 7.000** (2025-06-02), from `Andika-7.000.zip` | SemiBold (7.000 has Regular, Medium, SemiBold, Bold + italics) | `27484fdc98d0d63f…` / `73c4df767769af2e…` |

- **Licences**: all SIL Open Font License 1.1 with **Reserved Font Names** — « OpenDyslexic »; « RevReading
  Lexend » (Lexend's OFL.txt); « Andika » and « SIL ». A Modified Version may not use those names, so the files
  are shipped **byte-identical** (no subsetting, no format conversion, no renaming), each folder with its own
  `OFL.txt` (+ FONTLOG / README when upstream has one) and a generated `SOURCE.txt`. À propos lists each family
  with the copyright line of its OFL.txt and links openfontlicense.org. Itch.io's « OpenDyslexic 3 » is a release
  candidate without a scriptable download; the forge tag stays the source.
- **Fetching**: `src/Data.Build/fetch_fonts.py` (stdlib, `python -I`): `fetch` downloads the pinned URLs to
  `--raw` (default `PREDICT_FONTS_RAW`, else `out/raw-fonts/`; resumable `.part`, SHA-256 must match, else
  abort); `pack` writes `src/Data.Build/out/fonts/` (git-ignored) and enforces the 8 MB gate (2.23 MB); the
  csproj copies the folder next to the exe when present.
- **Private loading**: `new FontFamily(new Uri(dir + "\\"), "./#Family")` — nothing is installed into Windows,
  other applications never see the fonts. Probe (host, F0): WPF groups each static pair under its typographic
  family (« Andika » Regular + SemiBold, « Lexend » Regular + SemiBold, « OpenDyslexic » Normal + ExtraBold —
  the Bold OTF declares weight 800); `TryGetGlyphTypeface` succeeds for all six, also for the CFF `.otf`. A
  family is *available* when its files exist and the glyph typeface comes from its folder at the word weight;
  else Segoe UI + `font '<x>' not available, using Segoe UI`, and the settings list greys it « (police absente) ».
- **Coverage**: `missing glyphs:` empty for `aàâäbcçdeéèêëfghiîïjklmnoôöœpqrstuùûüvwxyÿz`, its capitals and
  `' - ’` in all four fonts (216 render sheets). A word with a missing character would fall back to a plain
  `TextBlock` (system fallback, no spacing), logged once per word.
- **Line heights** at 22 pt (M): Segoe UI 1.33 em (29.3 DIP), Lexend 1.25 em (27.5), Andika 1.61 em (35.4),
  OpenDyslexic 1.82 em (40). The bar is therefore slightly taller with OpenDyslexic/Andika (8 words with
  pictures at 96 dpi: 137 / 136 / 144 / 148 px for Segoe / Lexend / Andika / OpenDyslexic); `Place` uses the
  real window size, picture slots keep their size.
- **Letter spacing**: WPF has no letter-spacing property (`CharacterSpacing` is UWP/WinUI). `SpacedText`
  (FrameworkElement) maps the characters through `GlyphTypeface.CharacterToGlyphMap`, gives each glyph its
  `AdvanceWidths[g]·em` + spacing except the last one (`SpacedLayout`, unit-tested), and draws **one
  `GlyphRun`** (pixelsPerDip from `VisualTreeHelper.GetDpi`, rebuilt on `OnDpiChanged`). Its measured width is
  exact, so the button grows with the word. Normal spacing keeps the plain `TextBlock` (pixel-identical to
  iteration 2). **Kerning**: WPF exposes no pair-kerning table, and some fonts depend on it: WPF's own layout
  minus the raw glyph advances, per letter gap (`--render-bar` `kerning:` line) = −0.087 em for OpenDyslexic
  (−0.060 em on the second word list), −0.003 to −0.006 em for Segoe UI, Lexend, Andika. Without correction
  OpenDyslexic « Plus large » would have been only +0.013 em wider than Normal. `SpacedText` therefore measures
  the word with `FormattedText` (WPF's kerned width), spreads (kerned − raw) evenly over the gaps and adds the
  spacing on top: a spaced word is exactly the Normal word + spacing per gap (host, 34 gaps at 22 pt:
  +74.6–75.5 DIP for Plus large and +134.3–135.1 for Très large in all four fonts, expected 74.8 / 134.6). In the
  sandbox window (200 %) the measured growth is ≈ 13 % larger (≈ 0.113 em per gap), not investigated further.
  Kerning inside the word is spread evenly rather than per pair (pairs are not exposed).
  The inserted text is always the plain word; UI Automation sees the word (`AutomationProperties.Name`).
- **Measured cost** (sandbox, 80 bars, visible window): Segoe M 1.61 ms, XL 2.00; OpenDyslexic / Lexend /
  Andika at M 1.61 / 2.03 / 1.87; Très large 1.68–1.88; OpenDyslexic XL Très large 1.78 (with the kerning
  measure: Très large 1.71, XL Très large 2.33). First bar after a
  font change 4.5–7.4 ms. Working set +1.4 MB (OpenDyslexic) to +3.3 MB (Andika); `fonts\` = 2.23 MB on disk.
  WPF maps the font files while the app runs: an update replaces them only after quitting (the installer asks the
  running copy to quit first, plan 12).

## 6e. Start-up feedback and single instance (0.6.1; KI-1)

- **Single instance** (`SingleInstance`): right after the `--quit` branch and the two tool modes (`--render-bar`,
  `--speech-bench`, which install nothing and may run beside the app), the app creates the mutex
  `Local\Pepite-Prediction.Instance`. Already there → **second start**: `--autostart` exits silently; otherwise it calls
  `AllowSetForegroundWindow(ASFW_ANY)` and sets the auto-reset event `Local\Pepite-Prediction.Activate`, then exits. The
  running copy's listener thread (created before the settings / DB load, so a start while loading is answered once
  the dispatcher is free) shows « Pépite est déjà démarré ». When the signal fails (the running copy has not created its event yet, or is
  exiting: `OnExit` closes the events first) the second copy retries the signal and the mutex for ≈ 4 s
  (`StartupPolicy.ResolveSecondStart`): the mutex freed → it starts normally; still nothing → it shows the message
  itself. The `Pepite-Prediction` prefix avoids other software
  named Pépite (owner 2026-11-14); `Local\` = per session.
- **Across elevation levels**: the mutex and the events are created through `CreateMutexW` / `CreateEventW` with the
  SDDL `D:(A;;GA;;;<user SID>)(A;;GA;;;SY)S:(ML;;NW;;;ME)` (medium label, no-write-up): the same user, elevated or not, can open them both ways
  (an elevated copy's default DACL and high integrity label would refuse a medium one). Limits: another user in the
  same session is refused by the DACL — `ERROR_ACCESS_DENIED` still means « exists », and the second copy, which
  cannot set the event, shows « déjà démarré » itself (without « Ouvrir les Réglages »); separate sessions (fast
  user switching, RDP) each run their own copy (wanted: hooks are per desktop); an older build (≤ 0.6.0, no mutex)
  is not detected. Not tested elevated in the VM (single account, see the 0.6.1 run).
- **Start-up feedback** (`StartupPolicy`, pure): manual start → `WelcomeWindow` « Pépite est prêt ! » while
  `start.welcome` (default true) — text, a vector picture of the right end of the taskbar (generic tiles, the « ^ »
  arrow, our icon circled, the time; no screenshot of Windows), « Pour la garder visible : faites-la glisser près de
  l'horloge », the Windows 11 setting name « Autres icônes de barre d’état système » (read on fr-FR 26H2),
  « En savoir plus » and OK. **Shown once** (owner Q3 of the embedded-help plan, 2026-11-16): an app-level flag
  `state.welcomeShown` in `settings.json` (`SettingsStore.WelcomeShown`, the same for every profile, so turning
  teacher management on or switching to « Classe » or a pupil never brings it back) is set as soon as the window is
  shown. Migration, once, at start and before any profile switch: no flag yet and the active profile's
  `start.welcome` off (the former « Ne plus afficher ce message ») → the flag is recorded as shown
  (`StartupPolicy.MigrateWelcomeShown`); afterwards no profile decides it (`WelcomeShownBefore` / `WelcomeDue`). A machine lock of `start.welcome`
  wins: locked on = at every manual start, locked off = never, and the flag is not written. Réglages → Démarrage
  « Afficher le message de bienvenue au prochain démarrage » shows and clears that flag (`WelcomeFlag`; only when its
  box was changed there; the profile's `start.welcome` is no longer written by Réglages, `StartupPolicy.ForSave`).
  The « Ne plus afficher ce message » box is gone. `--autostart` → only a
  NotifyIcon balloon « Pépite est prêt » (a toast on Windows 10/11, silent under « Ne pas déranger »). Second start → the same window in mode « déjà démarré » (Ouvrir les
  Réglages · Aide · OK, no checkbox), always. `--no-welcome` (scripted tests) → nothing.
- **Help**: « En savoir plus » opens `aide\bienvenue.html`, « Aide » (« déjà démarré ») `aide\index.html`, through
  `HelpLinks` (§6j; fallback `LISEZ-MOI.txt`); with neither file the button is disabled with the tooltip
  `Strings.HelpMissing`.
- The window is our own (never read; the bar stays hidden while it has the focus); topmost while it appears, then
  normal (shown above other windows even when Windows refuses the foreground to a start not made by a click).
  Keyboard: Tab order = layout, Enter/Esc = OK; AutomationIds `WelcomeWindow` / `AlreadyRunningWindow`,
  `WelcomeHeading`, `TaskbarPicture` (with a screen-reader name), `More`, `OpenSettings`, `Ok`.
- **`--autostart`** is the contract with the installer (D13, plan 12): its Run-key value (name `Pepite-Prediction`,
  HKCU for a per-user install, HKLM for all users) starts `"<app>\Pepite.exe" --autostart`.
- **`--import <file.pepite>`** (plan 12, Q3; the `.pepite` association runs `"<app>\Pepite.exe" --import "%1"`): an
  option with a value (`CommandLine.ValueOptions`), so the path is never taken as the prediction database. **First
  start**: the request is queued and handled once start-up ends (no welcome window then). **Second start**: the copy
  writes the full path to `%LOCALAPPDATA%\Pepite-Prediction\ouvrir.txt` (temp file + rename) and sets the new event
  `Local\Pepite-Prediction.Open` (same SDDL as `.Activate`), then exits; the running copy's `open-listener` thread takes
  (reads and deletes) the file. The path is checked, never trusted (`OpenRequest.Acceptable`): an absolute path to an
  existing `*.pepite` of at most 50 MB, else « Ce fichier ne peut pas être importé… ». Management off → « Pour importer
  des profils, activez d'abord la gestion enseignant dans Réglages. » (the file is not read); on → the Espace
  enseignant's PIN gate (`PinReason.Teacher`, its grace applies), then the Espace enseignant on its « Exporter /
  importer » tab and `ImportDialog` with the file already chosen (`TeacherWindow.ImportFromFile`). Log lines
  `import: …` carry no path.
- **`--check-config <file>`** (plan 12): before anything else (no UI, no hook, no log, nothing written), parses the file
  with the machine layer's parser (`MachineLayer.Parse`, also used by the app; no trust check) and exits 0 = valid,
  2 = invalid JSON, not an object, `defaults` / `locked` not objects, or a `schema` other than 1, 3 = missing or
  unreadable. The installer runs it on a school's `/CONFIG` file before copying it.
- **Start log line** (plan 12, with `--log`): `start: elevated=<yes|no>, install=<per-user|all-users|portable>`
  (`InstallInfo`: the installer's uninstall entry `…\Uninstall\{999AA879-…}_is1` under HKLM / HKCU whose
  `InstallLocation` is this exe's folder; neither = a portable folder).

## 6f. Pupil profiles and the teacher PIN (D29, plan 09A 0.7.0, plan 09B 0.7.1)

```
 Management OFF (default, single user)          Management ON (Réglages → Plusieurs élèves → « Activer… »: PIN created)
 one profile « Mon profil », no PIN             « Classe » (inherited layer) + pupils, ≤ 40 in total
 tray: Pause · Réglages… · À propos · Quitter   tray: « Profil : Léa » (greyed) · Changer de profil… [PIN if switch]
                                                      · Pause · Réglages… [PIN, default on] · Espace enseignant… [PIN]
                                                      · À propos… · Quitter [PIN if switch, default off]

 %APPDATA%\Pepite-Prediction\                     (per Windows account)
   settings.json              app state: state.trayPromoted, state.managementEnabled
   profiles\index.json        order, default (classe), active, policy {pinToSwitch, pinToEdit, pinToQuit, sessionPicker,
                              idleReturnMin, returnOnLock, offeredHere {<COMPUTERNAME>: [ids]}}, session {logonTimeUtc, ended}
   profiles\teacher.json      pin {alg, iterations, salt, hash}, recovery {…}, setAtUtc, failures {count, untilUtc}
   profiles\<id>\profile.json { schema, id, name, avatar {picto, color}, created, settings {…difference layer…} }
 %LOCALAPPDATA%\Pepite-Prediction\sauvegardes\profils-<stamp>-<reason>.zip   (daily, before a delete / an import; 5 kept per reason)
 <anywhere>\*.pepite         export file (9B): { format "pepite-profils", version 1, app, created, remarque,
                              classe {name, avatar, settings}, profils [{id, name, avatar, settings}],
                              enseignant? {pin, recovery} (hashes, only when « Inclure le code enseignant ») }
```

- **Migration** (`ProfileMigration`, once): at the first start of 0.7.0 the old `%APPDATA%\PredictKeyboard\settings.json`
  becomes « Mon profil » (`p-` + 6 random base32 characters). Order: create `profiles` (nothing else is done if it fails); app `state` → new `settings.json`; a `lock.pin`
  → `teacher.json`; the profile file; **`index.json` last**; then the old file is renamed `settings.json.avant-profils`
  (kept, never deleted). Idempotent: an index (or profile folders, from which the store rebuilds the index) means
  done; a crash before the index reads the old file — or its renamed copy — again. Bad JSON → an empty « Mon
  profil », the file still kept. The voice-id migration of plan 08a is applied to the layer. A fresh install gets an
  empty « Mon profil ». A file that cannot be **read** (locked, ACL) is not "bad content": the migration stops before
  writing anything and runs again at the next start.
- **Start-up never fails on the files** (`ProfileMigration.OpenStores`): any IO / access error of the migration or of
  the stores (a file where the folder should be, an ACL, an offline redirected `%APPDATA%`) is logged
  (`profiles: start-up failed (…)`) and the session runs on an in-memory « Mon profil » built from the old settings
  (`profiles: … in memory only, nothing saved this session`). Nothing is written, renamed or moved in that session:
  the migration tries to create `profiles\` before any other step; `settings.json` and `teacher.json` are made
  read-only before they are read (a bad file is not moved aside, `state.json` is neither migrated nor deleted); a PIN
  typed then would live in memory only, so « Activer la gestion enseignant… » is disabled with « Les réglages ne
  peuvent pas être enregistrés pour le moment. Redémarrez Pépite. » The next start retries (0.6.1 fell back to
  defaults the same way).
- **Profiles** (`Profile`, `ProfileStore`): a name of 1–30 characters, no control / format character, unique ignoring
  case; « Classe » has the fixed id `classe`, is always first, cannot be deleted or renamed; the active profile cannot
  be deleted; « Dupliquer » is an independent copy of the layer. A lost or corrupt index is rebuilt from the folders
  (`profiles: index rebuilt from N folders`); a profile file that is not valid JSON is listed « (illisible) », kept
  untouched (`JsonFileStore` with `moveBadAside: false`), never activated, and can be deleted. Picture (Q9): an ARASAAC
  pictogram chosen by its word (`PictureStore.Has / GetNow`) on one of 8 named colours (rouge, orange, jaune, vert,
  bleu, violet, rose, marron; the colour name is given to screen readers), else the initial — **no personal picture**.
- **Layers**: a pupil inherits « Classe » (`SettingsLayering.Merge(machine.defaults, classe, pupil, exam, machine.locked)`);
  « Classe » itself and a profile while there is no « Classe » have no class layer. `Rebase(sourceClass, pupil,
  targetClass)` (for the 9B import) keeps the effective values, dropping what the target class already gives.
- **Teacher management** (`TeacherMode`): on = the PIN (and its recovery code, shown once: « Imprimer… » / « Copier »)
  + « Classe » (empty layer: nothing changes for the pupil) + `state.managementEnabled`; off (after the PIN, always
  asked) = PIN and recovery code removed, the flag cleared, every profile kept and the active one still active; on
  again → a new PIN, the list is back. Management counts as on only with the flag **and** a PIN.
- **PIN** (`TeacherLock`, D29 Q7 — supersedes D8): 4–6 ASCII digits, a short weak list (one digit repeated, runs up or
  down, two digits alternating, 2580, 1004…); `Rfc2898DeriveBytes.Pbkdf2(UTF-8 pin, 16-byte salt, iterations, SHA256,
  32)` compared with `CryptographicOperations.FixedTimeEquals`; the iteration count is stored with each hash
  (600 000, OWASP 2023). **Measured** in the test VM (3 vCPU): one verification 229–245 ms, also with the process
  limited to 2 cores (236 ms) — within the ≤ 600 ms budget, so 600 000 is kept. Back-off: failures 1–4 free, the 5th →
  30 s, then 60, 120… up to 15 min, persisted in `teacher.json` (a restart keeps it); during the wait nothing is
  checked. Recovery code `XXXX-XXXX-XXXX` (32-letter alphabet without 0/O/1/I, 60 bits), hashed the same way;
  « Code oublié ? » → the code → a new PIN (and a new code). Without it: the admin reset of 9C. Logs: `pin: verified;
  verify N ms (… iterations)`, `pin: wrong pin (n)`, `pin: wait N s`, `pin: changed`, `pin: removed` — never a digit.
- **Gate** (`PinGate`): Teacher (the Espace enseignant) → always, **also within the grace** (D29: always behind the
  PIN); Edit (Réglages, default on), Switch (default off), Quit (default off) → the teacher's switches (Espace
  enseignant → Verrouillage); management off → never; the session-start pick → never. A right PIN opens a 5-minute
  grace for Edit / Switch / Quit (e.g. Réglages right after the Espace enseignant); any profile switch ends it, so the
  next pupil starts locked. Inside the Espace enseignant, « Changer le code… », « Nouveau code de secours… » (after a
  confirmation: the old code stops working) and « Désactiver… » each ask the current PIN again. The Espace enseignant
  closes on « Utiliser maintenant » and on any profile switch.
- **PIN dialog** (`PinDialog`, our own window: the hook neither tracks nor logs it): the reason as a sentence, a
  digits-only `PasswordBox` (typed or pasted digits only, IME off), an on-screen keypad 0–9 + Effacer (not focusable:
  the digits go to the box), the wait « Trop d'essais. Réessayez dans 30 secondes. » as an assertive live region
  (announced at the start and every 10 s), « Code oublié ? ». PBKDF2 runs on a worker thread. Create mode: the PIN
  twice. AutomationIds `PinDialog`, `PinReason`, `PinBox`, `PinConfirmBox`, `PinKey0`…`PinKey9`, `PinErase`, `PinOk`,
  `PinCancel`, `PinForgot`, `PinMessage`, `RecoveryBox`; `RecoveryWindow`, `RecoveryCode`, `RecoveryPrint`,
  `RecoveryCopy`, `RecoveryDone`.
- **Switch** (`ProfileService.Switch`, UI thread): `KeyboardController.ResetForSwitch` (tracked word, echo, D23
  session, pending sentence read, auto-space, reading view; bar hidden) → `Speaker.CancelAll` → Réglages closed →
  `SettingsService.Retarget` (its `Changed` reconfigures the controller, the bar and the voice: Piper make-before-break,
  `speech: using piper:siwis until piper:pierre is ready`) → `index.active` → grace ended → `profile: switch <id> -> <id>
  (<reason>) applied in N ms`. The teacher may edit any profile (`ProfileEditTarget`): saved into its file only; the
  running app reloads when it is the active profile or « Classe » under the active pupil.
- **« Qui es-tu ? »** (`ProfilePicker`): pupils only (not « Classe »), 96 DIP tiles with the colour as a 4 px border, the
  name in the class font; selecting a tile (click, arrows) **says the name** (`Speaker.SayName`: the log line is
  `speak Manual (private, N chars)`); « C'est moi ✓ », Enter or a double click confirm; Esc keeps the current profile;
  UIA name « Profil de Léa ». Opened by « Changer de profil… », by « Fin de session » (back to « Classe » first) and at
  a **new Windows session** (`SessionStart.Decide`): the logon time (`WTSQuerySessionInformation` → `WTSINFOW.LogonTime`)
  differs from the one saved by the previous run → back to « Classe » (Q10: return at logoff) and the picker when it is
  on and there are ≥ 2 readable **pupil** profiles (« Classe » is not counted: with « Classe » + one pupil there is
  nothing to choose, the session starts on « Classe »); the same logon (a restart of the app) → the active profile
  resumes, no picker.
  `OnSessionEnding` (logoff / shutdown) writes `session.ended`. At a manual start the picker opens **after** the
  welcome window; with `--autostart` after the « Pépite est prêt » notification, in front (topmost while it appears).
  A switch cancels the speech, so a name still being said when « C'est moi » is pressed is cut.
- **Espace enseignant** (`TeacherWindow`, behind the PIN): Profils (list with picture, name, « classe » / « actif »;
  Ajouter… / Nom et image… / Dupliquer… / Supprimer… / Monter / Descendre / Modifier les réglages… / Utiliser
  maintenant; « N profils sur 40 »), Verrouillage (the three switches + « Proposer “Qui es-tu ?” au début de la session
  Windows »), Code (Changer le code…, Nouveau code de secours…, Désactiver la gestion enseignant…), « Fin de session ».
  Réglages of a profile are titled « Réglages — Léa » (with management on). 9B adds, in Verrouillage, the returns to
  « Classe » and « Profils proposés sur ce poste », and a tab « Exporter / importer ». The new windows are built in code
  (`PinDialog.cs`, `TeacherWindow.cs`, `ProfileEditDialog.cs`, `ProfilePicker.cs`, `ImportDialog.cs`, helpers in
  `ProfileUi.cs`); their text comes from `Strings`.
- **Export** (9B, `ProfileArchive.Export / Write`; Espace enseignant → Exporter / importer): the pupils ticked in a list
  (all by default; « Tout sélectionner » / « Aucun ») and always « Classe » (pupils keep only their differences from
  it), into one indented UTF-8 JSON file `*.pepite` that Notepad shows as is (« Léa », not `\u00e9`); a French
  `remarque` says it holds first names. Help text: the file contains the pupils' first names (keep it safe, do not
  send it over the Internet). « Inclure le code enseignant » (unticked) adds `enseignant {pin, recovery}` — the
  PBKDF2 **hashes** of teacher.json, never the code; a stray old `lock` part of a layer is never written. Atomic write;
  log `profiles: export of N pupils + classe, teacher code (not) included, K KB in T ms` (no path: it may hold a name).
- **Import** (`ProfileArchive.Read / Plan / Apply`, `ImportDialog`; « Importer un fichier… », disabled in an in-memory
  session): the file is refused with a French sentence when it is > 20 MB, not JSON, not `pepite-profils`, has no
  `profils` list or more than 200 entries in it (before any window is built), a version newer than 1 (« Mettez Pépite
  à jour ») or an unreadable « Classe ». A teacher code in the file is kept only with sane parameters
  (`TeacherLock.IsImportable`: our algorithm, 100 000–10 000 000 iterations, a 16–64-byte salt, a 32-byte hash, for the
  PIN and the recovery code alike); otherwise the dialog says « Le code enseignant de ce fichier ne peut pas être
  utilisé : il n'est pas importé. » instead of offering it. Voice ids from a file need no extra check: a voice is only
  looked up in the installed catalogue (an unknown or path-like `piper:…` id means the default voice). Each profile is
  re-validated (name rules incl. duplicates inside the file, settings an object; an id that is not `p-` + 6 base32 is
  dropped, so a file can never name a folder), unknown setting keys are kept, the voice-id migration is applied. The
  dialog lists the file's profiles with a checkbox each (unusable rows disabled, with the reason); a first name that
  exists here (ignoring case) gets **Remplacer / Garder les deux (default) / Ignorer**, and « Pour tous les prénoms déjà
  présents » sets every row. « Remplacer la classe de ce poste » and « Utiliser le code enseignant du fichier » (shown
  only when the file has one) are unticked. The summary (« 1 à créer, 0 à remplacer, 1 gardé(s) en double, 0
  ignoré(s). ») is recomputed at each change and asked again in a Oui / Non confirmation (« Une sauvegarde des profils
  est faite avant. »). `Plan` (pure): each pupil is **rebased** from the file's « Classe » onto this PC's (or the
  file's, when replacing it) with `SettingsLayering.Rebase`, so its effective settings stay the same; a free bar
  position or reading window not on a screen of this PC is cleared; « Garder les deux » names the copy « Léa (2) »
  (« (3) »…, ≤ 30 characters) with a new id (the file's id is kept when it is free here); « Remplacer » keeps the local
  id, place and active state; a conflict with « Classe » itself is kept both; more than 40 profiles → refused (« décochez
  au moins N profil(s) »). `Apply`: a zip backup of `profiles\` first (`-import`; none possible → nothing imported),
  then « Classe » (an unreadable local « Classe » is overwritten, i.e. repaired, when it is replaced — the plan rebased
  onto the file's class already), the rows, and the PIN hashes **only** through their own line (`pin: imported`); replacing the active
  profile or « Classe » reloads the running settings. Log: `profiles: import applied in T ms: created N, replaced N,
  kept both N, skipped N, class kept|replaced, teacher code unchanged|imported, active unchanged|reloaded [ids]`.
- **Session returns** (9B, `SessionPolicy` pure + `SessionWatcher`; Verrouillage): « Revenir à « Classe » après une
  inactivité de » Jamais (default) / 10 / 20 / 30 / 60 minutes — `GetLastInputInfo` polled by a 30 s `DispatcherTimer`
  (never in the hook); once per idle period the Espace enseignant and Réglages are closed and the PIN grace ended — also
  when « Classe » is already active (an adult who walked away must not leave them open) —, then the active pupil goes
  back to « Classe » and « Qui es-tu ? » opens (no picker when « Classe » was already active: an empty room). Nothing
  happens while a modal window of ours is open — a WPF dialog (`ComponentDispatcher.IsThreadModal`) or a Win32 one (a
  message box, a file dialog: one of our window handles is disabled) —; the next poll retries. « Revenir à « Classe »
  quand la session Windows est verrouillée (Windows + L) » (off): `WM_WTSSESSION_CHANGE` (`WTSRegisterSessionNotification`,
  a hidden tool window) — at the lock the same closing + grace end, the pupil goes back to « Classe » (the picker is not
  shown on the lock screen), at the unlock « Qui es-tu ? » opens; a lock while a modal window is open does all of it at
  the unlock. **Logoff** (D29 Q10, always): the session-ending message (and `WTS_SESSION_LOGOFF` if it arrives) makes
  a real switch to « Classe » (`ProfileService.EndSession`: the running app and the store stay in step if another
  program cancels the logoff) and writes `session.ended` at once, so the next logon starts on « Classe » even when its
  logon time cannot be read. Logs: `session: locked / unlocked`, `session: idle|lock|unlock -> teacher windows closed,
  grace ended + classe + picker`, `session: logoff -> classe (was <id>)`, the switch line `profile: switch <id> ->
  classe (idle|lock|logoff)`.
- **« Profils proposés sur ce poste »** (9B): Verrouillage → « Ne proposer dans « Qui es-tu ? » que les élèves cochés,
  sur cet ordinateur (<NAME>) » + a checkbox per pupil (its hint says that with no pupil ticked, or none of the ticked
  ones left, every pupil is offered). Stored in `policy.offeredHere` **keyed by the computer name**,
  so a roaming or (9C) shared profiles folder keeps one list per PC. « Qui es-tu ? » and the session-start count use
  the offered pupils; a pupil added (or duplicated, or imported) while this PC has a list is added to it; a list that
  matches no readable pupil falls back to everybody (the picker is never empty). The Espace enseignant still lists and
  can « Utiliser maintenant » any profile.
- **Logs and privacy**: the logs carry profile ids, never first names (also the speech line of a name) and never the PIN
  or the recovery code (unit test with a log capture; VM run: 0 occurrences of the names, PINs and codes in the whole
  `--log` file). First names stay in the profile files, the backups and on screen; nothing leaves the PC (D13).
- **Threat model**: child-proofing, not a security boundary. A pupil using the same Windows account can delete or edit
  `teacher.json` (management then counts as off) or the profile files; an administrator can always reset everything.
  « Code requis pour quitter » protects the tray's « Quitter » only: `Pepite.exe --quit` (the scripted teardown) and
  ending the process in the Task Manager bypass it, by design.
  The robust school path is the admin-owned machine file with a preset PIN (`locked.teacher.pin`, 9C).
- **Measured** (VM, 2026-11-14): profiles loaded in 16–62 ms (4 profiles) and 16 ms (40); start-up with 40 profiles
  not slower than with 4 (1 232 vs 1 392 ms to the start-up feedback: noise), working set +2.9 MB (314.4 vs 311.5 MB);
  a switch's synchronous part (reset, cancel, close, retarget + the `Changed` handlers) p95 3.4 ms, max 7.5 ms over 20
  switches Léa ↔ Tom (0–35 ms in the first runs, integer ms; plan: ≤ 150 ms); hook max 0.12–0.68 ms per 200 keys
  with management on (< 1 ms).

## 6g. Personal words, « Mes fautes », lessons (D10, plan 10A, 0.8.0)

Words are **only added on request** (owner, 2026-11-14: no automatic learning; nothing the pupil types is recorded): the
tray's « Ajouter un mot… », Réglages → « Mots », the Espace enseignant → « Mots » (lists, lessons, `.txt` import), and
the « ? » of the spelling indicator (0.8.1, §6h).

```
 profiles\classe\mots.json ──┐                       (no file until the first change; .bad-* on corruption)
 profiles\<id>\mots.json ────┤ WordLayering.Of: Classe active lists ∪ own active lists − own masques;
                              │                  « Mes fautes »: the pupil's pair first (wins), then the class's
                              ▼
          WordListService.Rebuild ── worker thread: read the files (first time) + UserLexicon.Build (Predict.Core)
            ──► UI thread (dispatcher): engine.User = snapshot (one reference swap; an older result is dropped)
            at start, on a profile switch, an edit of the active profile or of « Classe », an import, management on/off;
            the last two snapshots are kept and swapped in at once (Léa ↔ Tom does not rebuild); while a new one is
            built the engine keeps the same profile's previous snapshot, or none after a switch (never another pupil's)
```

- **Files and backups**: the word files are flushed before every profiles backup (`ProfileStore.BeforeBackup`: delete,
  import, `-mots`), and a deleted profile's words are flushed and forgotten before its folder goes (no write recreates it).
  Once read, a file's JSON tree is dropped (the book is the model). An export without word lists is a version 1 file.
- **File** `mots.json` = `{ schema: 1, listes: [ { id, nom, active, type: perso|lecon, mots: [ { w, src: manuel|lecon|import,
  ajoute: "2026-11-14" } ] } ], fautes: [ { ecrit, mot } ], masques: [ … ] }` — a day, never a time; unknown keys kept.
  The default list `mes-mots` is « Mes mots » (« Mots de la classe » for « Classe »).
- **Rules**: 1–40 characters, letters (any case, accents, œ æ ç), an inner `-` or `'` (`’` is stored as `'`); unique per
  list as stored (« Pierre » and « pierre » may both exist, Q13). Limits (Q15): 5 000 words for a pupil, 10 000 for
  « Classe », 500 « Mes fautes », 50 lists, 500 words per lesson — refused with a French sentence
  (`Strings.WordProblemText`). Lessons never expire: on / off by hand (Q11).
- **Inheritance (Q2)**: a pupil gets « Classe »'s active lists plus its own; « Ne pas me le proposer » (Réglages →
  Mots → « Mots de la classe ») hides one class word for this pupil (`masques`). « Classe » itself, or a profile with no
  readable « Classe », has its own words only (the same rule as the settings layers).
- **PIN (Q3)**: the tray's « Ajouter un mot… » never asks a pupil for the PIN (`PinReason.AddWord`), even with « Code
  requis pour modifier le profil »; with teacher management on and « Classe » active (after « Fin de session ») the word
  would go to every pupil, so it is an edit of « Classe » (`PinGate.AddWordReason` → `Edit`, which follows that switch); editing, deleting, hiding, lists and lessons are in Réglages / the Espace enseignant, behind
  the PIN as before.
- **Capitals and pictures (Q13)**: a word stored with a capital is a name: it is suggested with its capital
  (`KeyboardPolicy.Cased` only ever upper-cases), « Pierre » and « pierre » are two tiles; when the casing of the typed
  prefix makes them the same text (« Pi » → « Pierre ») they become one tile (`KeyboardPolicy.CasedDistinct`) marked as a
  name (the controller asks the engine for 3 spare words so a merge never shortens the bar). A capital lifts the names
  only when typed inside a sentence (`Predict(…, sentenceStart)`: at a sentence start every word has one); a
  sentence-start capital, like a lowercase start, gives a name a tenth of the lift. A capitalised
  « Mes fautes » target is a name too. A name never shows the ARASAAC picture of its lowercase homograph (`Suggestion.Name` → `PictureStore.KeyFor`
  null: an empty slot); 10C gives names their own pictures. Hyphenated names (Q16) are suggested whole and by each
  capitalised part (« Jean-Baptiste » → « Baptiste »; not « les » of « Valbrune-les-Pins »).
- **Engine**: see [predict-core-design.md](predict-core-design.md) § Personal words (a unigram-level share of the list,
  names favoured when the pupil starts with a capital, « Mes fautes » exact or ≥ 3-letter prefix with `WSpelling`).
- **Lessons**: Espace enseignant → Mots → « Nouvelle leçon… » (`LessonDialog`): a name and a pasted text (or « Ouvrir un
  fichier .txt… »); « Chercher les mots » lists its distinct words (`LessonExtractor`, Predict.Core) with « nouveau » /
  « connu »; new words and words outside the 300 most frequent are ticked; a word written with a capital away from a
  sentence start keeps it (« Vésuve »). Only the ticked words are stored: **the text itself is never stored**.
  « Importer une liste (.txt)… » / « Exporter la liste (.txt)… »: one word per line, UTF-8 (BOM or not; a file that is not
  UTF-8 is read as Windows-1252), ≤ 1 MB.
- **Erase**: « Effacer tous mes mots… » (« … de la classe… » for « Classe ») after a confirmation: a profiles backup
  `profils-<stamp>-mots.zip` first (none possible → nothing erased), then the profile's lists, « Mes fautes » and hidden
  words go; « Classe »'s words stay (unless « Classe » is the profile erased).
- **Export / import (9B, now version 2, Q14)**: « Inclure les listes de mots » (ticked) writes each profile's `mots`
  (lists, « Mes fautes », hidden words) into the `.pepite`; version 1 files are still read (no words: the words here
  stay); 0.7 refuses version 2 with its « Mettez Pépite à jour ». Import: « Remplacer » replaces the profile's words,
  « Garder les deux » / a new profile gets a copy; the **word rebase** keeps a pupil's effective list: the words it had
  from the file's « Classe » that this PC's « Classe » lacks go into its « Mes mots » (`src: import`), the class's spelling
  pairs likewise, and hidden words the target « Classe » does not have are dropped. « Remplacer la classe de ce poste »
  also replaces « Classe »'s words when the file has them. A list over the limits refuses its row (or the file, for
  « Classe »).
- **Logs**: counts and ids only — `words: profile <id> N own + M classe (K hidden) + F fautes -> X forms, built|reused in
  T ms (<reason>)`, `words: word added on <id> (now N words, …)`, `words: all words of <id> erased (backup …)`; never a
  word (the existing `--log` key and suggestion lines show typed text, as documented in §8).
- **Measured** (VM run of 0.8.0 before the review fixes, 2026-11-14: the snapshot was then built on the UI thread):
  13 ms at start with no words, 0.1–5 ms after an edit; with 15 000 effective words 164 ms at start (three files read)
  and 144 ms on the first switch to a pupil (his file + the build), then 1–3 ms (reused); 20 switches p95 6.5 ms, max
  153 ms (plan ≤ 150 ms p95). Since the review fix this work runs on a worker thread; the UI thread only swaps the
  snapshot (not re-measured in the VM). Memory: development/evaluation.md § Personal words. Engine numbers: development/evaluation.md
  § Personal words; VM run: development/keyboard.md.

## 6h. Spelling indicator (D12, plan 10B, 0.8.1)

A round marker left of the bar's words tells whether the word is spelled as a known word (owner answers Q7–Q10,
2026-11-14). Nothing is recorded: the tracker keeps the last finished word only until the next letter, and the log
has the state, never the word.

```
 hook thread: WordTracker.Type(separator) ── Finished = (word, separator before, separator after, previous word,
              sentence start, after a digit, caret inside a word)            (under _sync; no other work in the hook)
 UI thread:   Update ── typing word?   SpellingIndicator.Typing(w, engine.KnowsAny)        → ✓ or nothing
                    └─ finished word?  SpellingIndicator.Finished(w, engine.Check)          → ✓ ≈ ? or nothing
              → SuggestionWindow.ShowSuggestions(words, mark, word): the marker in MarkerHost, left of the words
                (no word + ≈ / ? → the marker alone; ✓ alone → the bar stays hidden, Q10)
```

- **`PredictionEngine.Check(word)`** (Predict.Core): **Known** = the lowercase form is in the lexicon or the active
  personal words (accents significant; a « Classe » word the pupil hid is not in the snapshot); **SoundsLike** = a
  « Mes fautes » spelling of the profile, or the same letters without accents (« ecole »), or the same *whole* sound key
  (grapheme fold or Lexique pronunciation: « maizon », « famme ») — candidates best first (the « Mes fautes » target,
  then by frequency); else **Unknown**. A typo that is not a sound-alike (« maisno ») is Unknown. A few lookups, no LINQ
  or closure: 1.4 µs mean, p99 7.6 µs, ≈ 245 bytes per call (`Predict.Eval --spelling`). Letters = `SpellingIndicator.IsWordChar`,
  the one definition the tracker and `ContextExtractor` use too (ASCII, Latin-1 accented letters except ÷, œ).
- **Rules (Q8)**: on a **finished** word (a separator typed after it) the final state, kept until the next letter, a
  Backspace, a jump or an insertion (an inserted word is finished too); while **typing**, ✓ only when the letters already
  form a known word (« mai », « mais »), nothing otherwise. **Never marked** (no wrong red; decided for 10B and measured on
  `spelling-cases.json`): one letter; all capitals (« SNCB »); a word glued to a digit (« 3e », « 10h »); the caret inside
  a word; next to a hyphen (« peut-être », « Jean-Pierre », « a-t-il »: parts of compounds — a hyphenated misspelling is not
  marked); before an apostrophe (« l' », « aujourd' », « presqu' ») and after one unless the word before is an elision
  (« l'école » is checked, the « hui » of « aujourd'hui » is not); next to `@ / \ _` or right after a dot
  (« jean@ecole », « ecole.be »); a word finished by « . » or « : » is only ✓ (else nothing) until a space or a line
  break follows (`FinishedWord.Then`: « www. », « https:// » are never red; « la maizon. » + space → ≈); **a word whose
  start is unknown** — typed right after a jump the reader could not read (keys-only hosts: « Le chev| » + click + « al␣ »
  is a fragment; `WordTracker.StartUnknown` → `Split`); **a capitalised word that is not known inside a sentence** (a name
  neither the lists nor the « Noms propres » pack hold). **Plan 10C (owner 2026-11-14)**: a name of the lists or of the
  pack is ✓ (« Emma », « Bruxelles », « Ieper »); at a **sentence start** an unknown capitalised word is judged by its
  letters but never red — ≈ when it sounds like a known word (« Maizon » ≈ maison), else nothing (it may be a rare name);
  the same letters as a pack name without its capital or accents are ≈ that name (« emma » ≈ Emma, « ines » ≈ Inès). A known
  capitalised word (« Maison » at a sentence start, a personal « Léa ») is ✓. Punctuation finishes a word like a space.
  A real word used wrongly (« ver » for « vert ») stays ✓: no grammar here.
- **Look (Q9)**: a circle in D12's colour — green `#2E9E5B` ✓, blue `#2D7DD2` ≈, red `#C62828` ? (never ✗) — with the
  white symbol (Segoe UI Symbol), so the colour is never the only cue. Diameter = F-label line + word line with pictures
  (room enough above the word), the word line alone without them: the marker is never taller than a tile (unit-tested
  for S / M / L / XL × pictures × F-keys). Above it, like a tile's F-label, « F9 » for ≈ / ? (an empty line for ✓).
  UI Automation: `AutomationId=SpellingMarker`, name « Orthographe : mot connu / s'écrit peut-être autrement / mot
  inconnu », help text the action (« clic : ajouter ce mot à mes mots (F9) »), tooltip below the bar. The marker sits
  outside the words' panel, so `VisibleCount` and F1–F8 are unchanged; the trimming of a bar wider than the monitor
  counts it in the fixed part (`BarPlacement.WordsThatFit`, unit-tested).
- **Bar (Q10)**: shown with the marker alone for ≈ and ? when there is no word to propose (after « xqzt␣ » the context
  predicts nothing); never for ✓ alone (« la maison. »: hidden as before, owner Q5 of plan exact-typed-word). **0.10.2**:
  while typing, a ✓ word is itself one of the bar's words (`PredictionEngine.ExactTyped` Present, at worst the last
  slot). `Update` passes the ✓ it computes for the marker as `exactMark`, so the rule applies exactly when the ✓ does —
  also when the spelling indicator is off in the settings (the marker is hidden, the rule still applies). `Configure`
  sets `ExactBar` to the word count; after a bar trimmed to fit the screen (XL on a small monitor) it is the slots that
  bar showed (`SyncExactBar` after each `Place` and in `SyncVisibleCount`, back to the count when a full bar fits again).
  With a one-word bar (« Nombre de mots proposés » 1, or trimmed to one) the rule is off: the only tile stays the most
  probable word. So Q10 only concerns a finished word. The tile looks like any other (owner Q6); choosing it retypes the
  word with its space (and its capital at a sentence start).
- **The last word of a line** (owner decision 2026-11-14, option B: « je m'attends à une correction »): Enter finishes the
  word like a space. The hook records the focused control and its **system caret** (`GetGUIThreadInfo`: caret window and
  top) before the app handles Enter and arms a one-shot timer (`LineEndDelayMs` = 130 ms, UI thread); any key, jump, focus
  or foreground event bumps a generation and cancels it. At the tick, the marker is offered only when nothing moved (same
  generation, tracker version, foreground window, focused control) **and the caret went down to a new line in the same
  control** (`KeyboardPolicy.NewLineAfterEnter`, unit-tested): a single-line field never passes — Save As / Run / a Find
  box (the VM run saw the Save As box still open 2 s after Enter while it saved: only the caret test caught it), a chat
  that sends on Enter (the caret goes back up). For ≈ / ? the field probe is then asked again (Enter re-armed it: a
  two-step login can put a password field there); only a **NotPassword** answer schedules the normal `Update`, which shows
  the marker alone at the caret read then, on the new line (never ✓ alone). The next letter or any jump clears it; click /
  F9 act on that word. Not shown (conservative): a host without a system caret, or a caret that stays at the same height
  because Enter scrolled the text (typing on the last visible line). Logs `spelling: line end mark=<state> (<ms>), field
  probe asked`, `spelling: line end cancelled (<reason>)`, `spelling: line end not shown (field not proven)`.
- **Esc** now reads the text at the caret (50 ms, like the navigation keys) so a readable host re-seeds the tracker; that
  read only seeds — no update (the bar Esc hid stays hidden until the next key, which continues the seeded word), no D23
  begin / landing and no selection decision (a selection dismissed with Esc stays dismissed). A word finished by a line
  break is marked only through the line-end check (`KeyboardPolicy.FinishedForMark`: a later update — a space after a chat
  sent its line — never marks it).
- **Actions**: a click on the marker, or **F9** while a ≈ / ? marker is shown with F-keys on (the hook reads a volatile
  flag and swallows F9 down / up, as F1–F8; an auto-repeated F9 acts once, until its key-up; the reading view's F9 keeps
  priority while it is shown): ≈ → the bar shows the
  sound-alike candidates as green tiles; choosing one **replaces the misspelled word** since 0.8.3 when it is safe (below),
  else it is inserted at the caret like any suggestion; ? → « Ajouter « mot » à mes mots ? » (`WordDialog.AddSpelled`: the
  word filled in and editable, « Oui » / « Non ») with the PIN rule of the tray's « Ajouter un mot… » (Q3:
  `PinGate.AddWordReason` — a pupil never needs the PIN, « Classe » with management on asks the Edit PIN). Right-click =
  say the word. ✓ has no click action.
- **Setting**: `words.spellingIndicator` (on by default, Q7), Réglages → « Mots » → « Indicateur d'orthographe » (saved by
  Enregistrer like the other settings, with a hint line), per profile and « Classe » (layered), lockable by the machine
  file (`"locked":{"words":{"spellingIndicator":false}}` → the checkbox disabled with « Réglé par l'administrateur »).
- **Cost**: the check runs on the UI thread in `Update` (one per update while a finished word is kept); the log line
  `typed=… mark=SoundsLike (0.02 ms)` measures it. The marker's first use (symbol font) is paid at the bar warm-up and
  in `ReWarm`. Render sheets: `--render-bar <png> --mark known|sounds|unknown …`.
- **Logs**: `mark=<state> (<ms>)` appended to the prediction line, `bar gen=G: N words + marker <state>`, `spelling:
  sound-alike words shown (N, click|F9)`, `spelling: add offer (click|F9)`, `words: add-word dialog opened from the
  spelling marker (<id>)` — never the word (the `--log` key / typed lines show typed text, as documented in §8).
- **0.8.3 — the ≈ choice replaces the word** (owner test in Word, 2026-11-14; `SoundAlikeCorrection`, unit-tested). When
  the ≈ list is shown, the controller notes the target: the finished word, the one separator that finished it, the tracker
  version and the window — only if `WordTracker.CanCorrect` (the kept text ends with exactly that word and separator,
  typed or read, no letter glued before, not a fragment). A word chosen from **that** list (click or F-key; the next
  `Update` forgets the target) then: if anything moved (version, window, kept text) → inserted at the caret as before;
  else the list is hidden at once (a second click cannot insert again) and the document is **always read first**
  (`ReadAround`, its own `correct-reader` queue, the password check first) — whatever the tracker's history: after
  Pause / Reprendre, a profile switch, typing during the focus read or a failed read the tracker says « keys » in Word too
  (review 0.8.3). `SoundAlikeCorrection.Decide` on the read: Stale (a key, a jump) or Password → nothing; Ok → replaced
  when the text before the caret ends with the word (any case) and its separator — a line break as CR LF, LF or CR,
  nothing after it (an editor's indent fails it) — else inserted at the caret (AutoCorrect or a text service changed the
  word); Unavailable (no text API) → **inserted** at the caret (re-review: LibreOffice, WPS, ONLYOFFICE, browser canvas
  editors rewrite words and cannot be read — deleting from the keys alone could delete the wrong letters). A keys-only
  replacement exists only for an explicit allow-list (`KeysOnlyHosts`), EMPTY in the product and filled only by the test
  switch `--keys-only-replace <process>` (the VM's keys-only control), after one plain separator (`KeysOnlyCertain`: not
  a line break, not `WordTracker.IsBarrier`); `NeverKeysOnly` (Office, LibreOffice, WPS, ONLYOFFICE, browsers, new
  Outlook, Teams) is a hard deny even then. Timeout or a selection → inserted. The keys are sent under the
  lock only if still nothing was typed and no keyboard event is inside our hook (`_keysInHook`: a physical key the hook
  is still handling could reach the app before our Backspaces; the punctuation read-back follows the same rule). The keys:
  Backspace × (word + 1), the correction and the same separator; after a line break **Left**, Backspace × word, the
  correction, **Right** — the line break is never deleted nor retyped (no second Enter for a chat or a list). The
  correction takes the original's case (the document's, when read: « Maizon » → « Maison », « MAIZON » → « MAISON »); the
  list itself takes the case of the word's own place (after Enter the new line is a sentence start, « la maizon⏎ » is
  not). The tracker (`CorrectFinished`), the echo (the chunk replaced, said as an inserted word through the echo guard)
  and the D23 mirror (`Insert`; dropped after a line break) follow. Logs: `correction: read <ms> ms, the document agrees,
  replaced (SendInput …)`, `correction: no text to read, keys only, replaced`, `correction: read <status> <ms> ms
  [(Office)], not replaced, inserted at the caret`, `correction: typed meanwhile, nothing done`, `spelling: sound-alike
  words shown (…), may replace` / `insert only` — never the words.
- **0.8.3 — double click on a tile** (VM recheck): the second click of a double click reached the bar the first click
  had just rebuilt and inserted its first word; a mouse pick whose input message was posted (`GetMessageTime` /
  `GetMessagePos`, not when the busy UI thread handles it: third run, posted 40 ms apart, handled 525 ms apart) within
  the system double-click time of the previous one, on the same tile or within the double-click rectangle
  (`KeyboardPolicy.IsSecondClickOfDouble`), is ignored — a quick pick of another tile still works (`tile click ignored (second click of a double click)`); F-keys are not concerned.
- **0.8.3 — the spelling reference**: `PredictionEngine.Check` gives ✓ only to a lexicon form that is in the table
  `spelling_unlisted`'s complement (Grammalecte `French.lex`, see predict-core-design.md), a personal word or a pack form;
  the typing ✓ (`KnowsForMark`) follows the same rule and also leaves out a very rare pack name at a sentence start
  (« Pome »: no ✓ while typed, then ≈ — the 10C nit).

## 6i. Proper names and their pictures (D30, D19 amended, plan 10C, 0.8.2)

```
 start (worker): NamesPack.LoadAsync ── noms\noms.json (NamesFile, JsonDocument) → NamesLexicon.Build → engine.Names (swap)
                                        └─ picture map (names + variants → noms\images\…png) → WordPictures.SetPack (UI)
                                        one compacting GC (the parsed file leaves the working set)
 words snapshot applied (UI): WordListService.PictureMaps(active) → WordPictures.SetLists(own, classe)
 bar (UI): tile → WordPictures.For(form, name) ── pupil's picture → « Classe »'s → pack flag (names only) → ARASAAC (never a name)
 reading view: ReadingDocument.Build(..., isName) → name tiles keyed « \u0001Form » → WordPictures only
```

- **The pack** is a separate, CC BY 4.0 open-data folder `noms\` (`noms.json`, `images\`, `LICENCE.txt`, `SOURCES.md`,
  `ATTRIBUTION-fr.txt`), format `pepite-noms` ([noms-format.md](noms-format.md)), built by `src/Data.Build/pepite-noms/`
  and copied next to the exe by the csproj when it was built. Never in `prediction.sqlite`. Missing pack → `names: off`,
  the app behaves as 0.8.1 (the 0.8.2 sentence-start ≈ rule stays). `--names <dir>` / `--no-names` for tests.
- **Engine layer** (`Predict.Core/NamesLexicon`, `PredictionEngine.Names`): every name, variant (« Ieper » for « Ypres »)
  and capitalised part of a compound the lexicon does not know (« Louvière », « Hugo »; never « La », « Saint », « Ier »)
  indexed by its lowercase letters and its accent-stripped letters; names equal to one of the 300 most frequent words
  (« Elle ») left out. ≈ 5 MB for 32 110 forms (a second, warm build in `Predict.Eval --names-twice`).
- **Ranking** (study 10c § 2.3, owner Q2 / Q11): a name scores like a unigram of frequency `5 × rang` per million
  (below the common content words: « mar » gives « mardi », « marché » before « Marie »), with the same back-off discount
  and prefix gate but **no context counts** (review: a name's own subtitle n-grams, « avec Casey », overrode the pack's
  ranking by recent births); historical figures rank below every predicted first name (« Ad » → Adam before Adolf);
  a vowel-initial name takes the elision boost after « d' », « l' » (« d'Em » → Emma). Tier 1 and tier 2 have separate
  key arrays: only a capital typed inside a sentence walks the recognised names; the best k + 4 of a prefix are picked by
  frequency and only they are scored. **VM recheck 2026-11-15**: without context counts the ×30 lift alone left « avec Jo »,
  « d'Em » to common words with strong contexts (« joie », « emploi »); a capital typed inside a sentence now also adds
  `NameCapitalBonus` (log10 2.5 ≈ ×300; 2.0 until places were ranked below the common first names) to the pack's names **and to the pupil's / class's names** (which stay ahead of the
  pack): `cases-personal` top-1 within 2 letters 0.386 → 0.474, keep-case KSS subtitles +2.1 pt, Wikipedia +1.0 pt; places rank after the 1 000 most given first names (owner 2026-11-15, the pack builder). Typed in lowercase inside a sentence it keeps 10 % of that (`NameLowercaseShare`), at a sentence start all of it
  (the capital says nothing there), and **a capital typed inside a sentence** multiplies it by 30 (`NameCapitalLift`) and
  opens the **recognised tier** (`niveau` 2: the ≈ 24 000 rarer first names). Without that capital a name needs 2 letters
  (`NamesFrom`): « C » at a sentence start never brings a name. A name typed in lowercase never takes the context counts
  of a common homograph (« une pierre »); a name whose lowercase homograph is a rare lexicon form (≤ 1 per million:
  « belgique », « japon ») replaces it in the bar (`NameMergeBelow`). A variant brings itself and the name it stands for,
  just after (« Iep » → « Ieper », « Ypres »). Only the best k + 4 names of a prefix enter the merge.
- **Owner decisions 2026-11-15**: a `restreint` figure needs a capital inside a sentence and 3 letters, ranks below every
  name and never gets a picture (`NamesPack` skips its image); at a sentence start a pack name whose lowercase is a common
  word (`PredictionEngine.IsCommonWord`, > 1 per million: « rose », « lune ») is not flagged `Name` — the common noun's
  picture stays, in the bar and the reading view — unless the lists hold it; a very rare tier-2 name that sounds like a
  word (`NamesLexicon.IsWeak`, `SpellingCheck.WeakName`) is ≈ at a sentence start, ✓ inside a sentence. The pack is read in
  one streaming pass straight into the layer (`NamesFile.ParseJson(bytes, emit)`, `NamesLexicon.Builder`): no list of
  records is kept.
- **Check**: a pack form (name, variant, part) is Known; the same letters in another case / without accents sound like
  it (« emma » ≈ Emma). `KnowsAny` (the ✓ while typing) takes exact pack forms. Sentence-start rule: § 6h.
- **Pictures** (`WordPictures`, owner Q13 / Q17 / Q18): the same slot above the word, bar height unchanged; the slot exists
  only with the ARASAAC pack and « Afficher les images » on (no slot appears late when the names pack loads). Lookup: the
  pupil's « noms » lists → « Classe »'s → the pack's flag (a tile marked `Name` only) → ARASAAC, never for a name. PNG
  thumbnails decoded on demand on the UI thread (≤ 10 KB flags, ≤ 256 px teacher pictures, a wider file decoded straight
  to 256 px with `DecodePixelWidth`; a cache of 200). Setting
  `words.namePictures` « Images pour les noms propres » (Réglages → Mots, on by default, per profile / « Classe »,
  lockable): off → no picture on any capitalised form (flags and the teacher's pictures of names); a teacher's picture
  of a common word follows « Afficher les images » only.
- **Reading view** (TODO left from 10A): a word written with a capital **inside a sentence** is a name — key
  `ReadingDocument.NameKeyPrefix + form`, never the ARASAAC picture of its lowercase homograph (« Pierre » is not a
  stone); at a sentence start, only a known name (pack or lists: `ReadingMode.IsName`) is a name, any other word keeps
  its common picture (« Maison rouge. »). Acronyms (« SNCB ») keep their lowercase key.
- **Teacher's names database** (Espace enseignant → Mots → « Importer une base de noms… », `NamesImport`,
  `NamesImportDialog`): folder, zip (≤ 50 MB) or the `noms.json` / `noms.csv` itself; ≤ 5 000 names; picture paths
  relative and inside the database (no `..`, root, drive, reparse point); PNG / JPEG ≤ 5 MB and ≤ 4096 px from the header
  before decoding, re-encoded by WPF into a ≤ 256 px PNG named by its hash in `profiles\<id>\images\` (≤ 500 distinct
  pictures in use per profile; a picture already in use is not a new one), on an STA worker thread while the editor says
  « Import en cours… »; pictures no list uses any more are deleted after a list is removed, the words erased or an import
  refused by the limits; a picture without a licence is left out (the name kept); the dialog shows the count, the licence(s), the lines
  left out and the Q17 rule (pictograms or drawings, never photos); for a CSV it asks one licence for the pictures. The
  names land in a new list `type: "noms"` of the chosen profile, shown « — noms » with its licence as tooltip; entries keep
  `image`, `licence`, `source`, `auteur`, `categorie`, `variantes` in `mots.json`. The tile's word, its speech and its
  accessible name are unchanged.
- **Export (Q14 / Q20)**: `.pepite` stays version 2 JSON (lists with their fields, no picture files) unless « Inclure les
  images » (unticked by default) is ticked and a names list has pictures: then a **version 3 zip** (`pepite.json` +
  `images/<profile id | classe>/<16 hex>.png`, ≤ 50 MB, else refused with a French sentence). Import reads both; a zip
  picture is kept only when its name matches its content's hash and its header passes the checks, and is written into the
  target profile's folder (« Classe »'s with « Remplacer la classe »). 0.8.1 refuses a version 3 file as « not a Pépite file » (its reader sees a zip, not
  JSON; it never had the « Mettez Pépite à jour » path for it).
- **0.8.3 (10C review)**: `WordTracker.CapitalNeutral` — at a sentence start, right after `: « " “ — –`
  (`Predict.Core.CapitalContext`) or with no word known before (after a jump the reader could not read) — is passed to
  `Predict` as a sentence start, so a capital there brings no name bonus (« il a dit : Mer » → merci, mercredi, mer…); the
  bar's capitalisation still follows `AtSentenceStart`. With a capital inside a sentence and fewer than 3 letters
  (`NamesCapUntil`) every first p words hold at most ⌈p / 2⌉ names, so the 8 shown of the 11 asked are at most half names
  (VM recheck: a cap on the 11 left 6 names of 8) (« le livre Le »: le, les, Leon, Lewis, Lena, leurs, Lenny,
  leur). Mute-h names take the elision boost (« d'Hu » → Hugo). The pack ranks countries, continents and regions above the
  towns (« né en Bel » → Belgique).
- **Logs**: `names: pack « … » edition E, N names, F forms, P pictured forms, loaded in T ms`, `names: memory compacted in
  T ms`, `bar gen=G: … K name / teacher pictures`, `names import: N names, I pictures, R left out, into <id>`,
  `profiles: export of … I pictures` — never a name typed by the pupil.

## 6j. Embedded help (D32, plan « aide intégrée », 0.10.1)

Static French HTML pages installed in `{app}\aide\` (source `freeware/aide/`, CC BY-SA 4.0; `prefs.js` GPL), opened
in the user's default browser. Local only: every page carries the CSP `default-src 'none'; script-src 'self';
style-src 'self'; img-src 'self'; font-src 'self'; form-action 'none'; base-uri 'none'`; external sites are written as
text, never links; no e-mail; no pictogram in the drawings and the bar renders (`--render-bar --no-pictures`; the
drawings are ours). Amended (owner 2026-11-17, D32): « les pictogrammes ARASAAC sont admis dans les vraies captures
d'écran de l'aide, avec la légende de crédit ; ils restent hors de la licence CC BY-SA 4.0 de l'aide » — 0.10.2: the
real screenshots `aide\img\capture-*.png`, each with pictograms captioned « Pictogrammes : Sergio Palao, ARASAAC,
Gouvernement d'Aragon, CC BY-NC-SA 4.0 ». The same pages are also published as an online copy on GitHub Pages (plan 13, A10; `pepite-labs.github.io/pepite`,
one folder per minor version, a banner saying the installed help works offline, and a privacy line for the web host;
`installer/build_help_site.py`, `development/BUILD.md` § 2c); the « À propos » page names that address as plain text.

```
 tray « Aide » ─────────────┐                      HelpLinks.Candidates(topic): stub? → page → aide\index.html → LISEZ-MOI.txt
 Réglages « ? » / F1 ───────┼─► HelpUi.Open(topic) ─► HelpLinks.Open: first existing file → ShellExecute(path)
 welcome « En savoir plus » ┤                          Win32Exception → skip the other .html, try LISEZ-MOI.txt
 « déjà démarré » « Aide » ─┘                          nothing opened → message HelpMissing
```

- **Pages**: 9 for pupils (`<meta name="pepite-public" content="enfant">`, « tu ») and 4 for adults (« vous »):
  enseignants, installation, vie-privee, a-propos. Two-row navigation (pupils / « Pour les adultes »), identical on
  every page. Two display toggles (« Lettres OpenDyslexic », « Texte plus grand ») in pure CSS (`body:has(...)`);
  `prefs.js` carries them across pages (`?od=1&grand=1` on the help's own links) and sessions (`localStorage`, where the
  browser keeps it for local files). Fonts: copies of the bundled ones in `aide\fonts\` (Firefox refuses a font from
  a parent folder of a local page), made by the csproj from `src/Data.Build/out/fonts`.
- **Sections**: ShellExecute drops a `#fragment`, so the app opens a redirect stub
  (`aide\aller\reglages-voix.html`: `<meta http-equiv="refresh" content="0; url=../reglages.html#voix">` + a visible
  link). Topics (`HelpTopic`): Home, Welcome, Settings, SettingsWords / Position / Speech / Reading / Start (stubs),
  Pupils (enseignants.html), MyWords (mes-mots.html), Spelling (orthographe.html).
- **Entry points**: tray « Aide » right before « À propos… » (`TrayMenu.MenuItems`, never behind the PIN); in
  Réglages a « ? » button (26×26, tooltip « Aide sur cette partie (F1) », UIA name « Aide : <section> ») on each section
  of « Barre et voix » except Aperçu, and on the « Mots » tab next to « Indicateur d'orthographe » and « Mes mots »; F1
  through `ApplicationCommands.Help` in that window only: the focused element's nearest `aide:<section>` tag →
  `HelpLinks.ForSettings`. No « ? » in the Espace enseignant (owner Q5).
- **F1 and the hook**: `KeyboardController.OnKeyDown` returns before the F-keys when the foreground window is ours, and
  a jump to our window hides the bar — F1 in Réglages is never taken as « insert word 1 ». Elsewhere F1–F8 keep their
  meaning while the bar is shown.
- **Checks**: `installer/help_lint.py` (document shell, headings, alternatives, forbidden markup, links and anchors,
  tu / vous by audience, banned wording such as « IA », navigation, stubs, font sizes, ≤ 1.2 MB without fonts, images
  ≤ 300 KB); `release_lint.py` runs it on the stage, treats `aide\` as strict own texts and requires
  `aide\index.html` for `--public`. Logs: `help: <topic> -> aide\…` (relative paths only).
- **Packaging**: `pepite.iss` deletes `{app}\aide` before installing (pages renamed or removed do not stay);
  `installer\DEPLOIEMENT.txt` is installed next to the exe (linked from installation.html#ecole).

## 7. Caret location

| Order | Method | Works for |
|---|---|---|
| 1 | `GetGUIThreadInfo(tid).rcCaret` + `ClientToScreen` | Win32 edit controls (Notepad, many dialogs) |
| 2 | UI Automation `TextPattern.GetSelection()` → bounding rectangle | modern apps exposing TextPattern (Word, browsers, WinUI) |
| 3 | mouse position + 20 px | last resort |

## 7b. Reading the text at the caret

| | Win32 path (class `Edit`: Notepad, classic dialogs) | UIA path (everything else: Edge/Chromium, Word) |
|---|---|---|
| password | `GetWindowLongPtr(GWL_STYLE) & ES_PASSWORD` → no text message sent | `AutomationElement.IsPassword` checked before any pattern call |
| caret | `EM_GETSEL` (start ≠ end → `Selection`; −1 above 64 K chars → `Unavailable`) | `TextPattern.GetSelection()` on the **focused element only** (exactly 1 range; non-empty → `Selection`) |
| text | `WM_GETTEXT` of `caret + 300 + 2` chars; window = 600 before, 300 after (plan 06) | range clone moved by `Character` −200 / +65, **clamped to the element's `DocumentRange`**; widened to −600 / +300 when that cut the sentence (§5c) |
| text length | exact | whole moved ranges (`GetText(-1)`): a provider "character" may be several UTF-16 units (emoji, ZWJ sequences), so a UTF-16 cap could cut the text next to the caret |
| bound | each message via `SendMessageTimeout(ABORTIFHUNG\|ERRORONEXIT, 50 ms)` | none per call → whole read > 150 ms ⇒ `Timeout`; worker replaced if stuck > 2 s |
| indices | exact UTF-16 units = what one Backspace/Delete removes | characters, tokenised by us (not `TextUnit.Word`) |

**Extraction** (`ContextExtractor.Extract`, pure, unit-tested): word chars = the engine tokenizer
`[a-zà-ÿœ]` after lower-casing. *Prefix* = word chars before the caret; *suffix* = word chars after it, only
when the caret is inside a word; *NextChar* = char after the suffix (`\0` at end of text). Left barriers
(nothing left of them counts): digits (the tracker resets), combining marks, sentence ends (`. ! ? …`,
control chars except Tab — Word's `\r \v \a`, Chromium's `\n` —, U+2028/2029, U+FFFC). Up to 3 context
words leftwards; *separators* = non-word chars between the caret and the previous word when there is no
prefix. A word cut by the window, or a combining mark touching the word → `Valid=false` → keys. A
1 000-string test proves that extracting at the end of a text gives exactly the state typing it gives.

**Step 0 probe (sandbox, classic Notepad, 2026-11-08):** `AutomationElement.FromHandle(<Edit>)` exposes
**no** pattern (`GetSupportedPatterns()` empty; `TextPattern` → "Unsupported Pattern"), `IsPassword` =
false. Classic Notepad's Edit has no UIA TextPattern in this configuration → the Win32 path is required, not
just faster.

**Per-app quirks found while testing:**
- **Chromium** in-page elements report `ProcessId = 0` → a known pid must match the foreground window's,
  pid 0 is accepted and the foreground re-check after the read is the guard.
- **Chromium** lets a textarea's range move **past the field** into the rest of the page (masked password
  bullets, other fields, body text). Clamping to the focused element's `DocumentRange` fixes it; without
  it the text end looked like `\n` (no space added) and other fields' text entered the window.
- **Emoji** before the caret count as separators (as when typed), never shift the indices: verified with
  10 emoji + ZWJ sequences + a decomposed accent before `mai‸son` (E-emoji).
- **Word** (input-free probe on the host, COM-placed caret): the document pane `_WwG` gives correct windows (cold 44 ms, warm 3–7 ms). A paragraph mark `\r` inside the text is a sentence end and, as NextChar, means no space; the **final** paragraph mark lies outside `DocumentRange`, so after clamping the document end reads as end of text (space added, as in Notepad).

## 8. Security & privacy

- Runs `asInvoker` (no admin). Consequence: cannot read or type into elevated windows (UIPI) — by design.
- No network, no persistence of typed text. `--log` writes typed and spoken words to `%TEMP%` for debugging
  only (the speech echo's dropped text never).
- **Piper voice (plan 08a)**: everything runs locally (espeak-ng, ONNX Runtime on the CPU); ONNX Runtime telemetry is
  switched off (`DisableTelemetryEvents`) before the session exists; no network code. The Piper log lines carry timings
  and counts only (`speech: piper first audio X ms (synth S ms, N sentences)`, `piper: word map fallback (W, U)`), never
  text; the only text remains the existing `speak <kind> '…'` line.
- **Grammatical prediction (plan 11, 0.9.0)**: local only; the new log lines are `grammar: <n> forms, <m> lemmas, loaded in
  <t> ms` and `grammar: absent` (or `grammar: off (--no-grammar)`): no text, no form, no rule hit is logged, nothing new is
  spoken. The rules read the three context words the bar already had. Before it is loaded the voice pack gets an
  integrity check (SHA-256 of every file against its `voice.json`): it catches a damaged or partial copy, not a deliberate
  replacement (whoever can change the files can change the card too); a changed file → `piper:siwis unavailable (sha256
  mismatch: <file>), using SAPI`. The password guard is
  unchanged (it runs before any engine).
- **Persistent state**: one HKCU value written once (`Control Panel\NotifyIconSettings\<our id>\IsPromoted
  = 1`, Explorer's own key for our icon) and, since 0.7.0 (plan 09), `%APPDATA%\Pepite-Prediction\` (`settings.json`:
  app state; `profiles\`: the index, `teacher.json` with the PIN hash, one folder per profile with its first name,
  pictogram word, colour and settings) plus `%LOCALAPPDATA%\Pepite-Prediction\sauvegardes\` (zip copies of
  `profiles\`, 5 kept); since 0.8.0 (plan 10A) each profile folder may hold `mots.json` (its word lists, which can hold
  first names and lesson words; written only when a word is added, never from what is typed). **First names are personal data**: they stay in these local files and on screen, never in a log
  (ids only) and never on the network. An export (`*.pepite`, 9B) is a file the teacher chooses to write, with the
  first names and settings of the ticked pupils (the UI and the file itself say so); the teacher code travels in it only
  as PBKDF2 hashes and only when « Inclure le code enseignant » is ticked; an import never applies it without its own
  unticked line. The 0.6 `%APPDATA%\PredictKeyboard\settings.json` is renamed
  `settings.json.avant-profils` by the migration and kept. The machine file
  `%PROGRAMDATA%\Pepite-Prediction\settings.json` (or the older `PredictKeyboard` one) is **only read**, and only when an
  administrator / SYSTEM owns it.
  The bundled fonts are read-only files next to the exe, loaded privately: nothing is installed system-wide.
  À propos links are opened by the shell in the browser.
- **Installation (plan 12, 0.10.0, Inno Setup)**: per-user by default in `%LOCALAPPDATA%\Programs\Pepite-Prediction`
  (no admin), or « pour tous les utilisateurs » / `/ALLUSERS` in `C:\Program Files\Pepite-Prediction` (the school
  choice: AppLocker's default rules block `%LOCALAPPDATA%\Programs`). The app writes nothing next to the exe, so Program
  Files works. Run value and `.pepite` association in HKCU or HKLM to match (`HKA`); one install per PC — the installer
  refuses the other mode while one exists (exit 1). The installer never starts Pépite elevated (« Lancer Pépite » runs
  as the original user; a silent install starts nothing) and asks a running copy to quit first (the `.Quit` event, then
  Restart Manager). **Data search** (`DataSearch.MayWalkUp`): the walk-up to `<parent>\Data.Build\out\` (prediction
  DB, pictures, grammar, fonts) happens only when the exe's folder has a `bin\Debug\` or `bin\Release\` segment whose
  parent holds a `*.csproj` — an install or a publish folder finds its data next to the exe or not at all (a planted
  `C:\Data.Build\out\pictos.sqlite` is never read by an installed copy). **All users = Program Files only** (owner decision 2026-11-15, plan 12 review): in
  all-users mode the installer refuses (exit 7; in the wizard already on the folder page, a silent run reaches the
  same refusal) any folder that is not a direct child `<Program Files>\<name>` or that is a link — elsewhere a parent
  folder's rights (another drive, a vendor folder inside Program Files) could let a standard user rename or replace the
  folder, or plant files and links in it, and every pupil would run them.
  A per-user install keeps the free choice of folder. **Machine folder**: `%PROGRAMDATA%\Pepite-Prediction`, written
  only by the installer (the `/CONFIG` file after `--check-config`). An existing folder that is a link or not owned by
  Administrators / SYSTEM is refused (exit 2, IT deletes it); otherwise the folder gets the same ACL (this folder only,
  never `/T`: icacls would follow a junction planted inside), and only the one `settings.json` is reset and owned by
  Administrators; no `/CONFIG` and an existing file → left as it is; the uninstaller never deletes through a link. **Uninstall**:
  the files of `{app}`, the Run value, the association and the shortcuts go; the pupils' data stays unless the user
  answers Oui to « Supprimer aussi les réglages, les profils des élèves et leurs mots enregistrés sur ce compte
  Windows ? » (default Non; `%APPDATA%\Pepite-Prediction`, `%LOCALAPPDATA%\Pepite-Prediction`, the legacy
  `%APPDATA%\PredictKeyboard`, `%TEMP%\predict-keyboard.log` of that account only), and, for an all-users install, a
  second question for the machine folder; silent: `/SUPPRIMERDONNEES`, `/SUPPRIMERCONFIG`. No network, no telemetry, no
  updater in the installer either (no `Download*` in the script). Logged at start (with `--log`):
  `start: elevated=…, install=…`.
- **Speech echo privacy**: before a word or sentence is spoken, the echo guard checks the focused field of the
  foreground window (Win32 `ES_PASSWORD`, else UIA `IsPassword`) on its own thread; a password field →
  `echo dropped (password)`, the text is never logged nor spoken. This also covers a password field focused
  **before any jump** (autofocus), which the reader cannot see; a `Password` answer also stops tracking
  (`echo guard: password field, tracking suspended`). **0.8.3 (owner, « ne rien lire si on ne sait pas »)**: spoken only
  on a `NotPassword` answer — `Unavailable` (the field cannot be classified) → `echo dropped (field not proven)`; a
  terminal or remote-session / VM window (`PrivateHosts`: the terminal classes / processes of the Ctrl+C fallback, plus
  mstsc, msrdc, vmconnect, VirtualBox, VMware, remote-control and VNC clients) → `echo dropped (terminal or remote
  window)`; the inserted and corrected words go through the same guard (`Speaker.SayChecked`). In such a window the
  controller also tracks nothing, reads nothing and shows no bar (`_privateHost`, set at each foreground change; log
  `terminal or remote window in front: nothing tracked nor said`; re-evaluated at each foreground change and each focus
  move inside a window; the process is named with `OpenProcess` + `QueryFullProcessImageName`, and a process that cannot
  be named counts as private). A web terminal inside a page (xterm.js: VS Code, cloud shells — its focused element is an
  `xterm-helper-textarea`, `KeyboardPolicy.IsWebTerminal`) is answered `Password` by `PasswordProbe` and by the caret
  read, so it is neither tracked nor spoken; as for a password field, the suppression is lifted by the read after the next
  caret move (a click, an arrow, Ctrl+key, a focus move) that lands in a plain field — in VS Code, clicking back into the
  editor; until then nothing typed in that window is tracked. A window whose process cannot be named is private too
  (log `process not identified: treated as private`). Known limits: a
  remote session shown in a browser page (Chrome Remote Desktop, a web RDP gateway) is not recognised; the « key inside
  the hook » check before our injected keys (`_keysInHook`) leaves a window of a few microseconds between the check and
  `SendInput`. Right-click speech bypasses the guard (the pupil asks
  for a word of our bar). `CancelAll` (pause, password read) also discards an item being probed.
- **Field probe (independent of speech)**: at the first character after every jump, after **Enter** (two-step
  logins move the focus to the password field on Enter) and after the start, the
  controller asks `FieldProbe` (`field-probe` MTA thread, latest request wins) whether the focused field is a
  password field — a page may focus one after the jump's read, or with no jump at all (autofocus). Until it
  answers, key lines read `key vk=0x.. -> (pending)` and `typed=(pending field probe)`, without the characters.
  Every jump, Enter and suppression increments an epoch under the tracker lock; a probe answer (or a caret read)
  is applied only if it was asked in the current epoch, so an answer that crosses a jump can never mark the new
  field as clear;
  the bar shows only when a read or the probe has seen a non-password field. `Password` → the same suppression
  as a read. Sandbox (autofocused password page, typing before any click, echo off and on): `field probe:
  Password 155 ms` / `74 ms`, the typed secret 0× in the whole log, not even as a 3-letter fragment. Notepad: the
  probe answers in 2–4 ms, the bar is unchanged.
- **Password fields** are never read: the Win32 style bit / UIA `IsPassword` is checked before any text
  request. When the latest read reports a password field, `_suppress` is set: keys (incl. Backspace and
  Enter) are not tracked nor logged, no bar. It **survives jumps inside the same window** (arrows, Ctrl+V
  in the password field) and ends only on a foreground change or when the latest read finds a
  non-password field. (A password field focused before any jump — autofocus — is not
  detected in v1.)
- The reader reads at most 200 chars before / 64 after the caret, only after a jump, never continuously;
  the text is not stored (only the extracted words, in memory).
- **Selection reading (plan 07)**: read only when a caret read reported a selection, after the same password checks
  (Win32 `ES_PASSWORD`, UIA `IsPassword` of the focused element and of the ancestors climbed); a `Password` answer
  suppresses tracking and leaves the reading mode. Nothing is read, shown or logged for a selection in a password field
  (sandbox S11: Ctrl+A and a drag in a filled password field → `status=Password`, the value 0× in the log). The text is
  kept in memory while the reading view is shown, never written (`--log` lines aside). The simulated Ctrl+C is off by
  default, never in a password field or one that cannot be checked, and puts the clipboard back as it was (§5d).
- Scripted control for tests: `--exit-after <s>` (timer → normal shutdown) and `--quit` (sets the named
  event `Local\Pepite-Prediction.Quit`, §6e; the running instance exits through `OnExit`: hooks, reader and tray
  icon removed). **Host-test exception** (owner-approved, D2): the app may run on the developer's host only
  for the automated Microsoft Word test (`src/Predict.Keyboard.Tests/host/WordHostTest.ps1`); Notepad and
  Edge are tested in Windows Sandbox.
- Global hooks + input injection look like a keylogger to antivirus heuristics → code signing is required
  before distribution.

## 9. Known limitations → next design steps

| Limitation | Next step |
|---|---|
| text read only after jumps; autocorrect while typing (no jump) still drifts | read after an app-side change too (TextChanged events) — out of scope v1 |
| RichEdit (WordPad), Java, Electron, MSAA-only apps: no read (keys only) | per-app adapters (LibreOffice/Chrome/Firefox next, D14) |
| `Edit` with > 64 K chars: EM_GETSEL can't report the caret → keys | `EM_GETSEL` with pointer args / `EM_EXGETSEL` |
| focus moved into a password field by the page *while* typing (no jump, after the probe answered) is not seen | probe again on UIA focus-changed events |
| UWP / packaged apps hosted by `ApplicationFrameHost`: the foreground window's pid is the host's, not the app's, so a UIA element with a real pid is rejected (`Unavailable` → keys) | resolve the hosted `CoreWindow`'s pid before comparing |
| a foreground change noticed only by the key-down backstop resets the tracker under an F-key: that F1 is swallowed without inserting | let the backstop skip F1–F8 while the bar is visible |
| suffix not used for scoring | use the suffix to rank (`mai|son` → favour words ending in `son`) |
| plan 11 (0.9.0): the agreement rules see the last three context words only: a far subject (« les enfants de la classe mang… », « elle n'est pas allé ») is out of reach (neutral, never wrong); a previous word typed wrong is not corrected | 10D-2: read the tracker's kept text for 2–5 words, propose a correction of the previous words |
| no colour setting | colours later |
| plan 09A: the teacher PIN protects against pupils on the same Windows account, not against an adult with that account (who can delete `teacher.json`) | admin-owned `locked.teacher.pin` (9C) |
| plan 09B: an import interrupted by a disk error after the backup can be partly applied (no transaction over several files) | restore the `-import` backup by hand; a restore button later |
| plan 09B: « Profils proposés sur ce poste » is keyed by the computer name: a renamed PC loses its list (everybody offered again) | — (accepted) |
| plan 10A: a personal word gets no next-word (n-gram) help: after « avec », « Léa » competes at the unigram level only | a measured next-word bonus (out of scope, D10) |
| plan 10A: the reading view (plan 07) still shows the common noun's picture for a capitalised name in the read text | the 10C picture lookup |
| the first next-word prediction after a very frequent word (« la », « je ») reads its n-gram successors on the UI thread: 0.6 s (0.7.2) to 4.6 s (VM under memory pressure, plan 10A W9) | warm the frequent contexts at start, or read them off the UI thread |
| plan 10A: a capitalised name typed in lowercase gets only a tenth of its list share (« léa » is less favoured than « Léa ») | — (by design: names are written with a capital; measured in evaluation.md) |
| plan 09A: a forgotten PIN without the recovery code has no reset yet | admin marker `reinitialiser-code` (9C) |
| plan 09A: Réglages show only the administrator's locks; which settings come from « Classe » is not shown per setting | per-setting reason later |
| SAPI 5 lists only the Desktop voices (Hortense fr-FR); OneCore voices (Julie, Paul) need WinRT | plan 08a: the Piper voice siwis is the default when installed (D24); OneCore voices not planned |
| plan 08a: Piper reads numbers and units as espeak does (« 8 h 30 » → « huit h trente ») | plan 08c: text normaliser |
| plan 08b: the word echo groups only after a closed list of liaison words (Liaison.cs) and before a vowel or h muet; the sentence echo, the re-read (D23) and the reading are not grouped; espeak decides the liaison itself (« les huit », h aspiré list of ≈ 50 school words) | extend the lists if the owner hears a miss |
| plan 08b: pierre ships in private test releases only (`"public": false`, D28: base model fine-tuned from research-only data) | public decision before plan 10; if refused, evaluate `fr_FR-mls-medium` male speakers |
| plan 08a: the first utterances of a session use SAPI while the Piper voice loads (≈ 3 s on the dev PC, ≈ 10 s in the test VM, cold disk): the voice changes once | — (accepted, A5) |
| plan 08a: the highlight may start early on words beginning with a stop / fricative (alignment, not the clock) | plan 08b: per-phoneme-class lead, if the owner's eye check asks for it |
| plan 08a: `onnxruntime.dll` needs the Visual C++ 2015–2022 runtime; without it the Piper load fails and SAPI is used | A13: app-local VC++ DLLs if a clean PC lacks them |
| XL + wide spacing + 8 long words do not fit a 1080p/1440p screen: the bar shows fewer words | docked mode on two rows (later), or fewer words in the settings |
| `slow UI` lines (WPF layout 30–150 ms) under fast typing in the sandbox (software rendering), as in 0.2.0 | measure on real hardware |
| D22 punctuation acts only right after **our** space; text typed by hand is never re-spaced; « » not handled; `-t-il` inversions not special-cased | out of scope (plan 04) |
| sentence start unknown after a jump that could not read the text → no capital | — |
| D23 re-read: an edit made unseen (Word AutoCorrect, IME composition, undo) is not in the mirror; paste / undo end the session silently; a key typed between a leave and its read (≈ 50–120 ms) cancels the re-read | read again on TextChanged events (out of scope) |
| D23 re-read: identical sentences are told apart only in a Win32 Edit (absolute offsets) | — |
| D23 re-read: residual privacy risk — the focus returns to a plain field before the probe covering the password keys runs (slow UIA provider, no focus event); the window is up to the edit-probe latency (debounce slot ≤ 150 ms + the probe itself) | none planned |
| Win32 caret only in some apps | prefer UIA when `GetGUIThreadInfo` has no caret; per-app quirks list |
| elevated windows | optional signed `uiAccess=true` build installed in Program Files |
| pictures on ≈ 53 % of running-text tokens (measured on the subtitle held-out set, 2026-11-08) after the owner's exclusions (auxiliaries, articles, letters, abstract function-word symbols) | other symbol sets / own drawings for frequent abstract words (out of scope, plan 02) |
| bar ≈ 137 DIP tall with pictures at M: covers more of the text | sizes S/M/L/XL and docked / free positions (iteration 3, done) |
| `IsPromoted` is undocumented; a moved exe gets a new subkey (and a new one-time promotion) | per-exe-path flag; stable install path (plan 12: the installer) |
| plan 12: the installer is unsigned (SmartScreen « éditeur inconnu »; Smart App Control blocks it on a fresh Windows 11); Windows 11 x64 only; no MSI | signing through the SignPath Foundation (Q4); MSI only if GPO deployment is asked for (Q6) |
| plan 12: an all-users install elevated with another administrator's account asks the uninstall data question for that account, and cannot signal a copy run by the pupil (Restart Manager closes it) | documented in DEPLOIEMENT.txt |
| tray icon size not refreshed after a DPI change | reload on `WM_DPICHANGED` (out of scope) |
| plan 07: the reading view was tested at the caret only (docked / free positions not run); Chrome not in the sandbox (Edge only); Word by the owner's host suite (`-Suite plan07`) | — |
| plan 07: one highlight per SAPI word event; the audible delay of the audio device after an event is not measured (no screen / audio recording in the sandbox) | measure with a recording on real hardware |
| plan 07: a selection that an application reports late or not at all through its caret (no `Selection` status) is not seen without the Ctrl+C fallback; a UIA `TextSelectionChanged` subscription is not used | optional later |
| plan 07: Win32 `Edit` selections beyond 64 K characters (EM_GETSEL packed result) are not read | `EM_GETSEL` with pointer arguments |
