# The keyboard predictor (`Predict.Keyboard`)

A Windows-native (C# / WPF, .NET 10) app that predicts words **in any application** while you type, using
`Predict.Core`. Its visible name is **Pépite** (« Pépite — prédiction de mots », owner 2026-11-14) and its executable
`Pepite.exe` (0.6.1; `PredictKeyboard.exe` up to 0.6.0); the code keeps the `Predict.*` names. It is the first usable end-user piece of the freeware: install-free (self-contained).

## Using it

```
Pepite.exe [path\to\prediction.sqlite] [--log] [--exit-after <seconds>]
                    [--pictos <pictos.sqlite>] [--no-pictures] [--no-promote] [--autostart] [--no-welcome]
Pepite.exe --quit      (asks the running instance to exit cleanly — for scripted tests)
Pepite.exe --render-bar <out.png> [--dpi 96|144|192] [--pictos <p>] [--no-pictures]
                    [--size S|M|L|XL] [--no-keys] [--font segoe-ui|opendyslexic|lexend|andika]
                    [--spacing normal|wider|widest] [--probe-fonts] word1 word2 …
                                (draws the bar into a PNG and exits: no hook, nothing injected; the .txt
                                 beside it gives the size, the font file used and `missing glyphs:`)
```

- **Start-up** (0.6.1): a manual start shows the window « Pépite est prêt ! » (where the icon is, with a picture of
  the taskbar; « En savoir plus » opens the help page `aide\bienvenue.html`, else `LISEZ-MOI.txt`) once, at the very
  first start (Réglages → Démarrage « … au prochain démarrage » shows it once more). `--autostart`
  (start with Windows; the future installer's Run-key entry passes it) shows only a notification « Pépite est prêt ».
  A **second start** does not start a second copy: the running one shows « Pépite est déjà démarré » (Ouvrir les
  Réglages · Aide · OK); a second `--autostart` copy exits silently. `--no-welcome` (scripted tests) shows nothing.
- Type in any app (Notepad, Word, a browser…). A bar appears **at the text caret** (or docked / where you
  dragged it, see Réglages) with up to 8 words.
- **F1–F8** insert word 1–8 (only while the bar is visible and « Touches F1 à F8 » is on; otherwise F-keys go
  to the app as usual).
  **Click** a word to insert it; **right-click** to hear it (Windows' built-in SAPI 5 voice, French if
  installed). **Esc** hides the bar.
- Colours: **blue** = completion of what you typed, **green** = same sound (phonetic spelling),
  **orange** = letters swapped / typo.
- After a word and a space the bar shows **next-word** predictions (from up to three previous words).
- Case follows what you typed (`Maiz` → `Maison`, `MAI` → `MAISON`); at the **start of a sentence** (after
  `. ! ?`, a line break, or at the start of the text when it could be read) the words are capitalised (`Le`, `L'`).
- **Elisions** are offered with their apostrophe (`l'`, `d'`, `qu'`, `jusqu'`…) and inserted **without a space**;
  the next-word bar follows at once (`vers` → `l'` → `horizon`). The inserted-word speech says « l'horizon »
  together.
- **Punctuation after an inserted word** (D22): the space we added is taken back when you type `, . … ) ' -`
  (`maison,`); before `; : ! ?` it becomes a narrow no-break space (French typography, `Bonjour !`) — or nothing
  with « Espace avant ; : ! ? » = Aucune in Réglages; an extra space you type is absorbed (no double space). Only
  right after an insertion: text you typed yourself is never changed.
- **After you move the caret** (click, arrows, Home/End, PgUp/PgDn, Tab, Ctrl+V / Ctrl+Z / Ctrl+Backspace,
  switching window) the app **reads the text around the caret** and predicts from there: click after
  `il fait ` → next-word bar; click inside `mai|son` → completions of `mai`. Notepad (Win32 `Edit`) and
  Edge/Word (UI Automation) are supported; elsewhere it silently falls back to what you type.
- **Inside a word, the whole word is replaced**: `la mai|zon est` + F1 (`maison`) → `la maison est`
  (Delete the part after the caret, Backspace the part before).
- **Space rule**: a space is added after the inserted word only if the next character is not a space,
  punctuation or line end (`la mai|son, oui` → `la maison, oui`; at the end of the text: `maison `).
- **Password fields are never read**; while one is focused, keys are not tracked and no bar is shown.
- **Re-read of edits (D23)**: edit text that already exists (a letter inside a word, a word typed, inserted or
  deleted inside a sentence), then leave it — click, arrows, Home/End, PgUp/PgDn, Tab or another window: moving
  inside the same word says nothing, landing on another word of the sentence says the **modified word**, landing
  in another sentence or window says the **whole modified sentence** (only). Typing at the very end of the text is
  not re-read (the word / sentence echo says it when switched on); Delete inside the text counts as an edit; paste /
  undo (Ctrl+key) stop it silently; a click on the taskbar, the tray icon or our own windows is not a leave. Never in
  a password field (also when a page moves the focus to one by itself), never while paused. See [keyboard-design.md §5c](../design/keyboard-design.md).
- **Reading the selected text (D21/D24/D26, plan 07)**: select text (drag, double click, Shift + arrows, Ctrl+A) in
  Notepad, Edge or Word and the bar shows it as **word + picture tiles** with ▶/❚❚ (or **Space / F9**, only while this
  view is shown), ⏮ / ⏭ (previous / next sentence) and ⤢ (« grande fenêtre », resizable, remembered). Nothing is said until
  Play; then one sentence after the other, the **word being read highlighted** in yellow (in our bar only). Click a tile
  to hear that word (Play then starts at its sentence). Up to 1000 words (« Texte trop long, seuls les 1000 premiers mots
  sont lus »). A word cut by the selection is read whole, and the `. ! ? …` ending the sentence is added on the last
  tile (D26; the selection itself is not changed). Typing, Esc, or a click elsewhere → back to predictions. Never in a
  password field. Réglages « Lecture »: on / off, pictures, and « Copier la sélection par Ctrl+C si l'application ne la
  donne pas » (off by default; the clipboard is put back, also after a late copy, and our restored copy is kept out of
  the clipboard history — but the copy the application itself makes may appear in Windows+V's history; never in a
  console). See [keyboard-design.md §5d](../design/keyboard-design.md).
- **Pictures** (ARASAAC): above each word, a 64 × 64 picture on a white tile; an empty slot of the same size
  when the word has none (auxiliaries être/avoir, articles, elisions and abstract function words never get
  one, by design). The pack `pictos.sqlite` (+ `ARASAAC-LICENCE.txt`) is found from `--pictos`, next to the
  exe, or by walking up to `src\Data.Build\out\`; an explicit `--pictos` that does not exist, a missing pack
  or `--no-pictures` → the bar without pictures. No network at run time.
- **Personal words (plan 10A, 0.8.0, D10)**: words are added **only on request** — nothing you type is recorded.
  Tray → **Ajouter un mot…** (no teacher PIN needed, even with « Code requis pour modifier le profil »; « Écouter »
  says it) puts a word into the profile's « Mes mots »; a capital is kept (« Léa », « Vésuve »: a name, best found
  when you start it with its capital; never shown with the common noun's picture; « Pierre » and « pierre » are two
  words). Réglages → tab **Mots**: « Mes mots » (search, Ajouter… / Modifier… / Supprimer), « Mots de la classe »
  (« Ne pas me le proposer » hides one for this pupil), **« Mes fautes »** (« J'écris pace » → « Je veux écrire
  parce »: typing the spelling, or its first 3 letters, offers the word first, in green), « Effacer tous mes mots… »
  (a profiles backup `-mots` first). Espace enseignant → tab **Mots**: the same for any profile and « Classe », plus
  the lists (on / off), **« Nouvelle leçon… »** (paste the lesson or open a .txt, « Chercher les mots », tick; only
  the ticked words are kept, never the text), « Importer / Exporter une liste (.txt) » (one word per line). Lists
  travel in `.pepite` exports (« Inclure les listes de mots », version 2). Design: [keyboard-design.md §6g](../design/keyboard-design.md).
- **Spelling indicator (plan 10B, 0.8.1, D12)**: a round marker left of the words — **✓ green** known word, **≈ blue**
  « s'écrit peut-être autrement » (« maizon », « ecole »), **? red** unknown word; never a ✗. While typing it is only ✓
  (the letters already form a word: « mai », « mais »); once the word is finished (space, punctuation) it shows the
  final state until the next letter. With nothing to propose the bar shows the ≈ / ? marker alone, never a ✓ alone.
  **Click or F9** on ≈: the words that sound the same; choosing one (click or F-key) **replaces the misspelled word**
  (0.8.3: « mmmmal␣ » → « mal␣ », « Maizon␣ » → « Maison␣ », after Enter the word on the previous line) and says it — when
  nothing was typed or clicked since and the app's text, always read first, still ends with that word; in an app whose
  text cannot be read the word is added at the caret, not replaced (as before 0.8.3); otherwise too it is inserted at the
  caret; on ?: « Ajouter « mot » à mes mots ? » (no teacher PIN for a pupil; on « Classe » the
  edit PIN, as the tray's « Ajouter un mot… »). Right click: say the word. Not marked: one letter, all capitals
  (« SNCB »), a word glued to a digit, parts of « peut-être » / « aujourd'hui », addresses (« www. », « https:// »: a word
  ended by « . » or « : » is only ✓ until a space follows), a piece of word typed after a click the app could not read,
  unknown capitalised words (names); after « l' », « qu' »… the word is checked. ✓ only for words of the spelling
  reference (0.8.3: « yeah », « team », « mam », which Lexique 3.83 has because it counts film subtitles, are ≈ or ?, never ✓). **Enter**: the last word of the line keeps
  its mark — a ≈ / ? shows alone on the new line until you type (click / F9 work on it); a ✓ shows nothing; a
  single-line field (Save As, Run, a search box, a chat that sends on Enter) shows nothing. Réglages → **Mots** → « Indicateur d'orthographe » (on by
  default; per profile and « Classe »; a machine file can lock it). Narrator reads « Orthographe : mot inconnu » and the
  action. Nothing is recorded. Design: [keyboard-design.md §6h](../design/keyboard-design.md).
- **Proper names (plan 10C, 0.8.2, D30)**: the open-data pack **« Noms propres »** (folder `noms\` next to `Pepite.exe`,
  CC BY 4.0, credited in À propos → « Noms propres ») brings ≈ 29 000 first names (France and Belgium), the countries and
  continents, the Belgian regions, provinces and municipalities (« Ypres », with « Ieper » recognised), French regions,
  departments and towns, the world's capitals and 371 historical figures. Names come **after** the common words: type
  their **capital inside a sentence** (« avec Em » → « Emma ») to bring them forward (0.8.3: not after « : », « « » or a
  dash, where French writes a capital anyway, nor when the app could not be read; with 1–2 letters names fill at most
  half of the bar; countries and regions come first among the places: « né en Bel » → Belgique), and the rarer first names only
  then; at the start of a sentence (where every word has a capital) a single letter never brings a name. A name is ✓
  in the spelling indicator (« Emma », « Bruxelles »); « emma » without its capital is ≈ Emma; at the start of a sentence
  a misspelled word is ≈ like inside one (« Maizon » ≈ maison) but never red. **Pictures**: the flag of a country, a
  Belgian region or province above the name (Réglages → Mots → **« Images pour les noms propres »**, on by default, per
  profile); a name never shows a common noun's picture, in the bar or the reading view (« Pierre » is not a stone).
  Espace enseignant → Mots → **« Importer une base de noms… »**: a folder or a .zip with `noms.json` or `noms.csv` and
  its pictures (format: [noms-format.md](../design/noms-format.md)) — **pictograms or drawings only, no photos**; the
  licence of each picture is required, kept and shown (tooltip of the list). Export: « Inclure les images » makes the
  `.pepite` a zip (version 3) with those pictures. Design: [keyboard-design.md §6i](../design/keyboard-design.md).
- **Tray icon** (our own icon, next to the clock after the first start; grey while paused): left **or**
  right click → **Pause / Reprendre**, **Ajouter un mot…** (0.8.0), **Réglages…**, **À propos…** (version, ARASAAC, Lexique and font
  credits, links), **Quitter**. Pause also stops any speech at once.
- **Réglages… (iteration 3, D7–D9, D16)**: number of words (1–8); text size Petit / Moyen / Grand / Très grand
  (S 16 · M 22 · L 28 · XL 36, pictures 48 / 64 / 80 / 96); **font** of the words: Segoe UI, OpenDyslexic,
  Lexend, Andika (each shown in its own font; « (police absente) » greyed when its folder is missing);
  **letter spacing** Normal / Plus large / Très large (+0 / +0.10 / +0.18 em); pictures on/off; F1–F8 on/off;
  **position** next to the caret, docked at the top or bottom of the screen (centred, hidden when there is no
  suggestion), or **free** (drag the bar by the dotted grip at its left; it stays there, also after a restart);
  **speech**: read the inserted word, each word after a space, the sentence at `. ! ?` (all three on by default since
  0.8.3; a profile that unticked one keeps it unticked; since 0.8.3 typed text is said only in a field Pépite could check
  is not a password field, and never in a terminal — command prompt, PowerShell, Windows Terminal — nor in a Remote
  Desktop / virtual-machine window, where nothing is proposed either), **re-read the modified word / sentence** (on by default, D23), voice, speed, volume, « Écouter ». A live **Aperçu** shows the real bar buttons; **Enregistrer**
  applies and saves, **Annuler** / Esc discards. A bar wider than the screen (XL, wide spacing, long words)
  shows fewer words (`bar trimmed to N of M words`); F-keys follow the words shown.
- **Pupil profiles and teacher PIN (0.7.0, plan 09A, D29)**: off by default — one profile « Mon profil », no PIN,
  the tray unchanged. Réglages → **Plusieurs élèves** → « Activer la gestion enseignant… » asks for a teacher PIN (4–6
  digits, twice) and shows a **recovery code** once (« Imprimer… » / « Copier »); a « Classe » profile is added. Then
  the tray shows « Profil : Léa », « Changer de profil… » and « Espace enseignant… » (always behind the PIN): Profils
  (add with a first name, a pictogram word and one of 8 colours — no photos; rename, duplicate, delete, reorder, edit a
  profile's Réglages, use now), Verrouillage (PIN to change profile — off, PIN to change the profile's Réglages — on,
  PIN to quit — off, « Qui es-tu ? » at the session start — on), Code (change the PIN, new recovery code, deactivate:
  profiles kept), « Fin de session » (back to « Classe » + « Qui es-tu ? »). Pupils inherit « Classe » and keep only
  their differences. « Qui es-tu ? » (big pictures, the name is said aloud) opens at each new Windows session (after
  the welcome window at a manual start) when there are at least two pupils, back on « Classe »; restarting Pépite in
  the same session keeps the profile.
  5 wrong PINs → 30 s wait (doubling up to 15 min, kept across restarts). « Code oublié ? » → the recovery code → a new
  PIN. Child-proofing, not a security boundary. See [keyboard-design.md §6f](../design/keyboard-design.md).
- **Export / import of profiles (0.7.1, plan 09B)**: Espace enseignant → **Exporter / importer**. Export: tick the
  pupils (« Tout sélectionner » / « Aucun »), « Enregistrer sous… » → one readable `*.pepite` JSON file with « Classe »
  and the ticked pupils (first name, pictogram, colour, settings). The file contains the pupils' first names: keep it
  safe (the teacher's USB key), do not send it over the Internet. « Inclure le code enseignant » (unticked) adds only
  the code's hashes, for another PC of the same class. Import: « Importer un fichier… » → one row per profile of the
  file; a first name that exists here gets « Remplacer », « Garder les deux » (default: « Léa (2) ») or « Ignorer »
  (« pour tous » sets every row); « Remplacer la classe de ce poste » and « Utiliser le code enseignant du fichier » are
  never ticked by default; a summary, a confirmation, then a backup of the profiles before anything is written. Each
  pupil keeps the same effective settings even if this PC's « Classe » differs. A newer or foreign file is refused with
  a sentence; nothing is imported when the settings cannot be saved this session.
- **Returns to « Classe » (0.7.1)**: Espace enseignant → Verrouillage: after 10 / 20 / 30 / 60 minutes without
  keyboard or mouse (off by default) an open Espace enseignant or Réglages is closed (also on « Classe »: the PIN is
  asked again), the pupil's profile goes back to « Classe » and « Qui es-tu ? » opens; « Revenir à
  « Classe » quand la session Windows est verrouillée (Windows + L) » (off by default) does the same at the lock, with
  « Qui es-tu ? » at the unlock; at the end of the Windows session (logoff, shutdown) Pépite always goes back to
  « Classe ». « Profils proposés sur ce poste »: on this PC, « Qui es-tu ? » shows only the ticked pupils (each PC keeps
  its own list; new pupils are added to it).
- **Settings files** (per Windows account): `%APPDATA%\Pepite-Prediction\` — `settings.json` (app state: tray
  promotion flag, management on/off) and `profiles\` (`index.json`, `teacher.json`, one `<id>\profile.json` per
  profile); zip backups in `%LOCALAPPDATA%\Pepite-Prediction\sauvegardes\` (daily, before a delete or an import; 5
  kept). 0.7.0 migrates the old
  `%APPDATA%\PredictKeyboard\settings.json` once into « Mon profil » and keeps it as `settings.json.avant-profils`.
  A corrupt file is renamed `<file>.bad-<date>` and defaults are used (a corrupt profile is kept and shown
  « (illisible) »). An administrator may add a read-only
  `%PROGRAMDATA%\Pepite-Prediction\settings.json` (`defaults` + `locked`, owner must be Administrators/SYSTEM; an
  older `%PROGRAMDATA%\PredictKeyboard\settings.json` is still read);
  locked values show « Réglé par l'administrateur ». See [keyboard-design.md §6c](../design/keyboard-design.md).
  On the first start of a given exe path it sets Explorer's `IsPromoted` once so the icon shows next to the
  clock; if you hide it later, it stays hidden (`--no-promote` skips it). `--log` writes `%TEMP%\predict-keyboard.log` (debug only:
  it records typed words). Caret reads appear as
  `read <click|nav|ctrl|focus> src=<win32|uia> status=<Ok|Password|Selection|Unavailable|Timeout|Stale> <ms> ms prefix='…' suffix='…' next=… ctx=[…] sep=N`
  (`-> keys` when not applied); prediction lines show `src=` (where the state came from) and `suffix=`.
- The DB is found from the argument, next to the exe, or by walking up to `src\Data.Build\out\` (dev).
- Speech log lines (`--log`): `speak <Inserted|Word|Sentence|Reread|Reading|Manual|Preview> '…'`, `speech started`
  (SAPI began the utterance), `reread word|sentence landing=<SameSentence|Elsewhere> <reason> <ms> ms (<n> chars)` /
  `reread skip(<reason>)` (never the text), `speech done / cancelled
  (N ms after the request) / error`, `speech voice: <name> [culture], rate R, volume V`, `echo dropped
  (password|foreground changed|probe … ms)` (the dropped text is never logged); hook timing every 200 key
  events: `hook: n=200 mean=… ms max=… ms`.
- Piper voice log lines (plan 08a, `--log`; timings and counts only, never text): `speech: no Piper voice installed`,
  `speech: voice pack <id> left out (<why>)`, `speech: piper:siwis ready in T ms (verify V, espeak E, session S, warm-up W
  ms, 2 threads)`, `speech: piper:siwis unavailable (<type>: <message>), using SAPI`, `speech: using SAPI until
  piper:siwis is ready` (once per load), `speech: waiting for the Piper voice to load` (a held preview),
  `speech: piper first audio X ms (synth S ms, N sentences)`, `piper: word map fallback (W espeak words, U units)`,
  `piper: N unknown phoneme(s) skipped`, `speech: audio device open failed (<type>)`, `speech: audio device reopened
  (<reason>)`, `speech: audio device unavailable for Piper, utterance given to SAPI`, `settings: speech.voice migrated to
  sapi:<name>` (a 0.4.0 voice name), `speech voice: piper:siwis (fr_FR-siwis-medium, 22050 Hz) [fr-FR], rate R, volume V`.
- Picture log lines: `pictos: N words, M pictures, loaded in X ms`, `  bar gen=G: 8 words, C pictures cached,
  R requested, build+show T ms`, `  picto ready gen=G word=W Y ms` (after the bar was shown).

Build: `dotnet build src/Predict.Keyboard` · portable folder:
`dotnet publish src/Predict.Keyboard -c Release -r win-x64 --self-contained true -o <dir>`, then put
`prediction.sqlite` next to `Pepite.exe` (`pictos.sqlite` + `ARASAAC-LICENCE.txt` are copied by the
build when `src/Data.Build/out/` has them — see [BUILD.md](BUILD.md) step 1c; the `fonts\` folder likewise,
step 1d).

Plan 08a adds the Piper voice: run `src\Data.Build\build_espeak.ps1` and `fetch_voices.py` first (BUILD.md steps 1e
and 1f); the build then copies `libespeak-ng.dll`, `espeak-ng-data\`, `voices\siwis\`, `licenses\`,
`THIRD-PARTY-NOTICES.txt` and `COPYING` next to the exe. Without them the app runs with the SAPI voices only.
`Pepite.exe --speech-bench <outDir> [--profile normal|2core] [--audio device|null] [--sentences <file>]` is the
offline latency / highlight bench (no hook, no UI; BUILD.md step 1g).

## How it works

```
 keyboard + mouse (system-wide)
        │  WH_KEYBOARD_LL / WH_MOUSE_LL — on a DEDICATED hook thread with its own message loop
        ▼
 KeyboardController ── layout-aware ToUnicodeEx (AZERTY, AltGr, dead keys kept intact) ──► WordTracker
        │                                                           (current word + up to 3 previous words)
        │ post to UI thread (coalesced)
        ▼
 PredictionEngine.Predict / PredictNext  ──►  SuggestionWindow (WPF, topmost, WS_EX_NOACTIVATE: never
        │                                     steals focus)  placed at the caret
        │ F1–F8 / click                       caret: GetGUIThreadInfo → UI Automation TextPattern → mouse
        ▼
 Inserter: N × Delete + M × Backspace + the word as Unicode keystrokes (one SendInput), stamped with a marker
           the hook ignores

 caret jump (click / nav key / Ctrl+key / foreground WinEvent) ──► tracker.Reset ──► CaretTextReader (own
 thread, 50–120 ms later): Notepad EM_GETSEL+WM_GETTEXT · others UIA TextPattern ──► ContextExtractor ──►
 tracker.Seed (only if nothing typed meanwhile, same window, ≤ 150 ms)
```

Design points learnt the hard way:

- **The hooks must never wait.** Windows silently skips a low-level hook that does not answer in time. In
  the first version the hooks shared the UI thread, a cold prediction took ~900 ms, and the next keystroke
  (`m` of `maison`) was never seen. Fix: hooks on their own thread; they only update the tracker (under a
  lock) and post work to the UI thread; keystrokes typed during a slow prediction are coalesced, not lost.
- **Cold n-gram lookups** for very common words (`la`, `de` have tens of thousands of successors) went from
  ~900 ms to 20–80 ms by loading only the top successors (`ORDER BY c DESC LIMIT`) and taking the total
  with `SUM`. Later lookups are cached.
- **Our own injected keys** carry a marker in `dwExtraInfo` so the hook ignores them (other tools' injected
  characters — on-screen keyboard, dictation — are handled via `VK_PACKET`).
- **The bar never activates**: `WS_EX_NOACTIVATE` + answering `WM_MOUSEACTIVATE` with `MA_NOACTIVATE`, so
  clicks insert into the app you were typing in.

## Tested (Windows Sandbox, classic Notepad, 2026-11-08)

| typed | bar | key | result in Notepad |
|---|---|---|---|
| `la maiz` | **maison**, mesure, main, mais… | F1 | `la maison ` |
| `orizon` | **horizon**, horizons… | F1 | `horizon ` |
| `lbeu` | lieu, **bleu**, leu, beu | F2 | `bleu ` |
| `Maiz` | **Maison**, Mais, Maisons… | F1 | `Maison ` |
| `il fait ` | froid, chaud, partie, un, des, de, très, beau | — | next-word |
| `je ne sais ` | **pas**, plus, rien, même, quoi… | — | 3-word context |

Caret found via `GetGUIThreadInfo`. **Visual/UI retest (fresh sandbox, display attached, 2026-11-08):**

| check | result |
|---|---|
| bar look & placement | 8 coloured buttons with F-key labels, 6 px under the caret, follows the caret; long words (`horizontalement`…) clamped to the screen edge |
| mouse click | click on **était** → `la maiz était ` — focus stayed in Notepad, next-word bar followed (`pas, un, une…`) |
| right-click speech | `maison` spoken by *Microsoft Hortense* (fr-FR), `speech done` after 1.6 s; nothing inserted |
| F1 via raw `SendInput` | `la maiz` → `la maison ` (22/22 events) |

Fixed during the retest: (1) the **first** bar show took ≈ 9 s (WPF software rendering in the sandbox) —
the bar is now warmed up off-screen at startup (`bar warm-up: … ms` in the log), so the first suggestion is
instant; (2) the tooltip covered the words/typed line — now below the bar with a 1.2 s delay; (3) speech
is now logged (voice, done/error) instead of failing silently.

Test-harness note: PowerShell `SendKeys.SendWait('{F1}')` makes the insertion look broken — `SendWait`
holds input until the target processes the key, which we deliberately swallow. Press keys with
`SendInput` (as real hardware does) when testing.

## Reading the text at the caret — tested (plan 01, 2026-11-08)

Unit tests: `DOTNET_ROLL_FORWARD=Major dotnet test src/Predict.Keyboard.Tests` → **75 passed** (extractor
rules, tracker seed/suffix/version, space rule, apply rule, Inserter event order and INPUT size, read scheduling with a fake reader — latest wins, stale, timeout, stuck worker/warm-up replaced and never delivering —, emoji/decomposed accents around the caret, and a 1 000-random-string
equivalence test: extracting at the end of a text = typing it).

**Notepad (Windows Sandbox, classic Notepad, Win32 path)** — every read 0–2 ms (one 20–38 ms on a focus
switch):

| # | steps | log / result | |
|---|---|---|---|
| N1 | `il fait `, click after it, `fr`, F1 | `read click src=win32 status=Ok … prefix='' ctx=[il fait] sep=1` → `il fait froid ` | PASS |
| N2 | `la maison est belle`, click in `mai‸son`, F1 | `prefix='mai' suffix='son' next=␠` · `delete 3 right, 3 left, space=False` → unchanged text | PASS |
| N2-typo | `la mai‸zon est belle`, F1 | → `la maison est belle` | PASS |
| N2-end | click after `maison`, F1 | `prefix='maison' suffix=''` → `la maison est belle` (no double space) | PASS |
| N2b | `la mai‸son` (end of text), F1 | `next=EOT` → `la maison ` | PASS |
| N2c | `la mai‸son, oui`, F1 | → `la maison, oui` | PASS |
| N2d | `la mai‸zon est belle`, type `s`, F1 | `typed='mais' suffix='zon'` · `delete 3 right, 4 left` → `la maison est belle` | PASS |
| N3 | `je ne sais`, Ctrl+End ←→ (one batch) | one `read nav … prefix='sais' ctx=[je ne]`; space → `pas…`; `p`, F1 → `je ne sais pas ` | PASS |
| N4 | `première ligne⏎il fait `, Home / End | Home: `prefix='' ctx=[]`, no bar · End: `ctx=[il fait] sep=1`, bar | PASS |
| N5 | `la maison bleue`, Ctrl+Backspace | Notepad deleted the word (no U+007F) → `la maison `, `read ctrl … ctx=[la maison] sep=1` | PASS |
| N6 | Ctrl+V of `bonjour mon ami` | `read ctrl … prefix='ami' ctx=[bonjour mon]` | PASS |
| N7 | the typed table above (Esc between cases) | identical bars and results, `src=keys`, no `read` line, `word + " "` | PASS |
| N8 | `la maison`, Shift+Home | `read nav src=win32 status=Selection -> keys`, no bar | PASS |
| N9 | inserts with suffix > 0 (N2…) | no jump / `read` line between `insert …` and the next prediction | PASS |

**Edge (sandbox, `kbd-test.html`: textarea, password, contenteditable; UIA path)** — warm reads 9–15 ms,
no timeout in the final run (an earlier debug build logged one `Timeout 270 ms`; the reader now warms UIA
up at start: `reader warm-up: 50–100 ms`):

| # | steps | log / result | |
|---|---|---|---|
| E1 | `il fait ` in the textarea, click body, click back at the end (×2) | `read click src=uia status=Ok 12 ms … ctx=[il fait] sep=1`; `fr`, F1 → `il fait froid ` | PASS |
| E2 | `la maison bleue`, Ctrl+Backspace | `T[la maison ]`, `ctx=[la maison] sep=1`, bar | PASS |
| E3 | `la maison est belle`, End, ←×13, F1 · E3-end ←×10, F1 | `prefix='mai' suffix='son' next=␠` → unchanged · `prefix='maison'` → unchanged | PASS |
| E4 | `le chat` in the contenteditable, click its end | `prefix='chat' ctx=[le]`, bar | PASS |
| E5 | click the password field, type `secret` | `status=Password 5 ms (content not read)`, no bar, `secret` 0× in the log; textarea → predictions resume | PASS |
| E6 | Notepad `il fait ` ↔ Edge `je ne sais ` (SetForegroundWindow, then real Alt+Tab) | `read focus src=win32 … ctx=[il fait]` / `read focus src=uia … ctx=[je ne sais]` (bar with `pas`) | PASS |
| N6 in Edge | Ctrl+V in the textarea | `read ctrl src=uia … prefix='ami' ctx=[bonjour mon]` | PASS |
| E-emoji | 10× `😀👩‍💻` + `café` (decomposed accent) + ` la maison est belle`, End, ←×13, F1 | `prefix='mai' suffix='son' next=␠ ctx=[la]` → text exactly unchanged; caret between emoji: line logged with `next=\uD83D` | PASS |
| E5-fast | in the password field: ← then `secret` at 5 ms/char, Ctrl+V then `abc`, Backspace, Enter | only `status=Password` lines, **0** key/typed lines; click back in the textarea → predictions resume | PASS |

`--exit-after 25` exited by itself after 25 s (`exiting (exit-after)`); `--quit` stopped a running
instance (`quit requested`); no process left.

**Word 16 (host, owner-approved; `src/Predict.Keyboard.Tests/host/WordHostTest.ps1`, 2026-11-08)** — all
reads `src=uia`, 9–19 ms (19 ms the first):

| # | steps | log / result | |
|---|---|---|---|
| WH1 | new paragraph, type `il fait ` | `src=keys` next-word list; F1 → `il fait froid ` | PASS |
| WH2 | `la maison est belle`, click in `mai‸son`, F1 | `read click src=uia status=Ok 11 ms prefix='mai' suffix='son' next=␠ ctx=[la]` · `delete 3 right, 3 left, space=False` → unchanged | PASS |
| WH2-end | click after `maison`, F1 | `prefix='maison' suffix=''` → unchanged | PASS |
| WH2b | `la mai‸son` at the document end, F1 | `next=EOT` (the final paragraph mark is outside `DocumentRange`) → `la maison ` | PASS |
| WH3 | `je ne sais`, click end, End ← → | `read nav src=uia 10 ms prefix='sais' ctx=[je ne]`; space → `pas…`; `p`, F1 → `je ne sais pas ` | PASS |
| WH4 | `la maison bleue`, Ctrl+Backspace | `la maison `, `read ctrl … prefix='' ctx=[la maison] sep=1` | PASS |
| WH5 | Ctrl+Z after WH2 | `read ctrl 11 ms prefix=''` = oracle prefix (Word undid the whole insertion) | PASS |
| WH6 | `un deux trois⏎quatre`, click after `trois`; 1×1 table `le chat`, click its end | `prefix='trois' next=\r ctx=[un deux]` · table: `prefix='chat' next=\u0007` (cell mark), no error | PASS |
| WH7 | after a jump, type `la mai` | text ends `la mai`, prediction `maison, main…` | PASS |

Script safety: Word must be in front before every input batch (one `AttachThreadInput`-assisted activation,
else abort — the foreground lock refuses a plain `SetForegroundWindow` from a background process), every
click point must belong to Word (`WindowFromPoint` → root), F1 is pressed only when the log shows a
non-empty prediction for the current state, Word is quit only if the script started it. It needs a
**self-contained** publish (the host has no WindowsDesktop runtime). Two earlier attempts aborted on the
guard (session locked; then the foreground lock) without sending any input.

Logs and screenshots: `<work>\test-runs\01-caret-text-reading\`.

## Pictures + tray — tested (plan 02, 2026-11-08)

Sandbox (Windows 11 24H2, software rendering, classic Notepad, /IT driver), self-contained publish with the
final pack (5 063 pictures). Logs and screenshots:
`<work>\test-runs\02-pictures-tray\` (`sandbox-final\`, `sandbox-run1\`).

| # | Check | Result |
|---|---|---|
| P1 | `la mai` → bar with pictures (maison, main, mairie, maire, mains, maintenance) and aligned empty slots (mais, maintenant) | PASS (`P1f-la-mai.png`) |
| P2 | `il fait ` → next-word bar, pictures + empty slots aligned | PASS (`P2f-il-fait.png`) |
| P3 | `pictos: 30158 words, 5063 pictures, loaded in 21 ms`; 80 bars: build+show 1.39 ms mean with pictures vs 1.10 ms without (+0.3 ms); first picture ≤ 31 ms after the show (cold first bar 47 ms); 0 `slow UI` | PASS |
| P4 | `--no-pictures` and `--pictos C:\nope.sqlite` → plan 01 bar; À propos « Images : désactivées (option --no-pictures) » / « (pack absent) » | PASS (`F-P4-missing-about.png`) |
| P5 | plan 01 N1, N2, N7 with the final build | PASS (same texts as plan 01) |
| T1 | first start: subkey found 47 ms after the poll starts, `IsPromoted=1`, `state.json` written, icon next to the clock | PASS (`F-T1-1-promoted-crop.png`) |
| T2 | `IsPromoted=0` by hand, restart → « promotion already done once … (user choice kept) », stays 0 | PASS (`F-T2-hidden-kept-crop.png`) |
| T3 | left and right click open the menu (first click after a fresh start too); Pause → grey icon + tooltip « Prédiction — en pause », no bar while typing; Reprendre; Quitter → process gone, icon gone | PASS (`T3f-2-paused-grey-crop.png`, `T3f-3-menu-right-reprendre-crop.png`, `F-T3-after-quit-crop.png`) |
| T4 | À propos: version 0.2.0, verbatim ARASAAC attribution, licence and Lexique credits; arasaac.org link opens Edge; typing in Notepad with À propos open still predicts; À propos (or the menu) in front → bar hidden, keys typed into it not tracked, nothing read; exe carries our icon | PASS (`T4f-1-about.png`, `T4f-2-typing-with-about-open.png`, `F-T4-about-front.png`) |

Review follow-ups (2026-11-08, fresh sandbox, `review-fixes\`): `--quit` sent right after `prefetch … queued`
→ honoured (the quit event now exists from the start of `OnStartup`), process gone in 2.0 s (the prefetch,
0.45 s, had already finished — the abandon-on-dispose path is covered by xUnit); bars with pictures
(`X-bar.png`); mouse menu (`X-menu-mouse-crop.png`); **keyboard** Win+B, Right, Enter → menu at the icon
(`X-menu-keyboard-crop.png`); Quitter → process gone (`X-after-quit-crop.png`).

Host (no hooks): `--render-bar` at 96/144/192 dpi for `maison chat voiture tristement été manger le
exactement` → 880×137 / 1319×206 / 1759×274 px, pictures crisp at 192 dpi, `tristement`, `été`, `le`,
`exactement` with same-height empty slots (`render\bar_*.png`).

## Settings, speech, docked bar, fonts — tested (plan 03, 2026-11-08)

Sandbox (Windows 11 24H2, 3072×1656, classic Notepad + Edge, /IT driver; settings window driven through UI
Automation, tray menu by coordinates), self-contained publish of 0.3.0 with `prediction.sqlite`, `pictos.sqlite`
and `fonts\`. Logs, scripts and 119 screenshots:
the test-artefacts folder `test-runs\03-settings-speech-dock\` kept outside the repository (`sandbox\`; host sheets in
`render\sheets\`).

| # | Check | Result |
|---|---|---|
| G1 | Tray → Réglages… by mouse and by keyboard (Win+B, Right, Enter, Down×2, Enter); every section; voices listed (Hortense fr-FR, Zira en-US); fonts each in their own face; typing into the window → no bar, nothing tracked | PASS (`G1-2-window-crop.png`, `G1-1-font-combo-open-crop.png`) |
| G2 | 3 words, Enregistrer; `la mai` → maison, main, mais; F5 → Notepad inserts the date; F1 inserts | PASS (`G2-1-three-words-crop.png`) |
| G3 | S / L / XL / M → bar 502×242, 816×374, 974×444, 672×308 px (3 words); preview follows | PASS (`G3-*-1-bar-crop.png`, `G3-*-0-preview-crop.png`) |
| G4 | pictures off → no slots (598×150 px), on again → slots back, no restart | PASS (`G4-1-pictures-off-crop.png`) |
| G5 | F1–F8 off, 8 words → no F-labels; F5 reaches Notepad; a click inserts | PASS (`G5-1-no-labels-crop.png`) |
| G6 | Top / Bottom: centred (left 541 = expected), 8 px from the edge, hidden after deleting the word; Free: drag by the grip → Notepad keeps the focus (`foreground kept`, typing continues to `la maison est`), restart → same place | PASS (`G6-Free-3-next-bar.png`) |
| G7 | `state.json` → `state.trayPromoted` in settings.json, state.json deleted, « promotion already done once »; settings survive a restart | PASS |
| G8 | admin-owned machine file `defaults.words.count=5`, `locked.words.size=XL`, user `size: S` → 5 words, XL, size radios disabled « Réglé par l'administrateur », the user's S kept in their file; the same file owned by the user → `machine settings ignored (owner S-1-5-21-…)` | PASS (`G8-2-locked-size-crop.png`) |
| G9 | corrupt settings.json → defaults, `settings.json.bad-20261108-232828`, log line; Enregistrer writes valid JSON | PASS |
| G10 | each font at M: bar in the font, pictures aligned; first bar after a change 4.5–7.4 ms (`font warm-up 4–7 ms`) | PASS (`G10-*-1-bar-crop.png`) |
| G11 | OpenDyslexic Normal / Plus large / Très large → bar 2420 / 2766 / 2938 px; Segoe UI XL Plus large / Très large wider than the 3072 px screen → trimmed to 7 (or 6) words; F1 inserts the plain `maison` | PASS (`G11-od-Widest-crop.png`) |
| G12 | `fonts\Lexend` renamed with Lexend saved → Segoe UI + `font 'lexend' not available, using Segoe UI`, « Lexend (police absente) » greyed, À propos « Lexend — absente » | PASS (`G12-3-about.png`) |
| G13 | 80 bars per look (fresh start, 90 ms/key), build+show mean on a visible window: Segoe M 1.61 ms · Segoe XL 2.00 · OpenDyslexic M 1.61 · Lexend M 2.03 · Andika M 1.87 · Widest at M: Segoe 1.68, OpenDyslexic 1.73, Lexend 1.88, Andika 1.73 · OpenDyslexic XL Widest 1.78 | PASS (targets XL ≤ 2.5, font ≤ +0.5, Widest ≤ +0.5, XL+OD+Widest ≤ 3); `slow UI`: see the note |
| G14 | machine `locked.words.font = andika` → Andika, font list disabled + note | PASS (`G14-2-font-locked-crop.png`) |
| V1 | defaults, F1 on `maison` → `speak Inserted 'maison'`, `speech done` | PASS |
| V2 | word echo: `le chat dort l'arbre ` → `le`, `chat`, `dort`, `l'arbre` in order; F1 insert + space → `maison` spoken once (Inserted) | PASS |
| V3 | sentence echo: `il fait beau.` → one `speak Sentence 'il fait beau'` | PASS |
| V4 | Edge: (a) click the password field, type `secret ` → `read click status=Password`, no `speak`; (b) autofocused password page, type before any click → first run `echo dropped (password) 45 ms` but the tracker's debug line still showed the word once → **fixed** (review): the field probe at the first character → `field probe: Password 155 ms` (echo off) / `74 ms` (echo on), `tracking suspended`; key lines `(pending)`; the typed secret 0× in the whole log, no 3-letter fragment either | PASS (`sandbox-review-fixes\F1-full-predict-keyboard.log`, `F1-autofocus-echo-*.png`) |
| V5 | long sentence spoken → tray Pause → `speech cancelled (16 ms after the request)`; nothing spoken or tracked while paused | PASS (`V5-1-paused-crop.png`) |
| V6 | voice Zira, rate +5, volume 50, Écouter → `speak Preview`; Enregistrer; restart → `speech voice: Microsoft Zira Desktop [en-US], rate 5, volume 50` | PASS (`V6-1-voice-crop.png`) |
| V7 | 300 keys at 30 ms/key: hook mean 0.06–0.20 ms; max per 200 events 0.46–1.06 ms in steady windows, 2.5–5 ms in the first window after a start / under load | PARTIAL (see the note) |
| R1–R3 | plan 01 N1, N2, N7 (same texts); P1 bar; tray menu with the new item, Pause (no bar, nothing tracked), Reprendre, Quitter (process gone) | PASS |

**Notes.** *Slow UI*: at 90 ms/key 0–1 `slow UI` line per 80 bars; at 30 ms/key several per run, all in `place`
(WPF layout 30–150 ms, or `SetWindowPos`). The 0.2.0 build measured the same way in the same sandbox showed 0
and 18 in two runs, so this is the sandbox's CPU contention (software rendering), not a regression; a
`slow place: layout … ms, screens … ms, trim+move … ms` line now tells where the time goes. *Hook*: the JIT of
the hook path is paid before the hooks are installed (`hook path warm-up 9–13 ms`); the remaining spikes are
scheduling in the VM. *Memory* (idle after start): 0.2.0 163.9 MB → 0.3.0 182.9 MB (+19 MB: the synthesizer
and its voice are now created at start), +1.4 MB with OpenDyslexic, +3.3 MB with Andika, +2.6 MB after the
first speech.

Host (no hooks), `--render-bar`: 4 fonts × 3 spacings × 4 sizes × 96/192 dpi × 2 word lists, + keys off and
pictures off = 216 PNGs; 0 missing glyph, 0 fallback word, no clipped word (contact sheets
`render\sheets\sheet_<font>_<dpi>_<list>.png`, re-rendered after the kerning fix).

**Review fixes, re-checked (2026-11-09, fresh sandbox, `sandbox-review-fixes\`):**

| # | Check | Result |
|---|---|---|
| F1 | autofocused password page, `zqxwvk ` typed before any click, echo off then on | PASS — see V4; full log kept |
| F2 | Notepad, first keys after a start: `field probe: NotPassword 4 ms`, then the usual bars | PASS (`F2-first-keys-bar-crop.png`) |
| F3 | machine default count 5, the user sets L and saves → user file `{"words":{"size":"L"}}` only; default changed to 3 → 3 words at L | PASS (`F3-count3-sizeL-crop.png`) |
| F4 | access keys unique and shown with Alt (Alt+D selects « Très grand »); OpenDyslexic Normal / Plus large / Très large with kerning-corrected spacing | PASS (`F4-access-keys-crop.png`, `F4-od-*-crop.png`) |
| F5 | free position drag: `foreground kept`, typing continues | PASS (`F5-after-drag-crop.png`) |
| F6 | OpenDyslexic M Très large 1.71 ms, XL Très large 2.33 ms, Segoe M 1.69 ms (80 bars) | PASS |
| G8 | trust rule: Administrators-owned file + folder → used, size locked; folder owned by the user → ignored; Users granted write on the file → ignored | PASS (`G8fix-locked-crop.png`) |
| R1 | plan 01 N1, N2, N7 with the field probe: same texts, next-word bar right after a click | PASS |

**Follow-ups, re-checked (2026-11-09, fresh sandbox, `sandbox-followups\`):**

| # | Check | Result |
|---|---|---|
| T | two-step login page (`kbd-test-twostep.html`: user name autofocused, **Enter moves the focus to the password field**, no click): `eleve` Enter `zqxwvk `, echo off then on | PASS — echo off: Enter re-arms the probe, `field probe: Password 55 ms`, `tracking suspended`; echo on: the user name's word echo finds the password field first (`echo dropped (password) 24 ms`, `tracking suspended`); key lines after Enter only `(pending)`; the secret 0× in the whole log, no 3-letter fragment (`T-twostep-full-predict-keyboard.log`, `T-twostep-echo-*.png`) |
| D | first bar after a click into a non-password field, from the first key's hook timestamp to the bar shown (5 runs each): **Edge textarea** (UIA) 6–7 ms — the click's read already showed a non-password field, so the bar does not wait; the field probe answers in parallel in 21–30 ms; **Notepad** (Win32 Edit) 4–6 ms, probe 0 ms; **cmd.exe** console (keys only, no readable text) 10–14 ms, probe 7–16 ms | measured (`D-delays-log.txt`). When no read has seen the field (a read that timed out, e.g. right after a page load: 220–320 ms), the first bar waits for the probe (Edge 30–290 ms in these runs). WordPad is not in the 24H2 sandbox. |

## Punctuation, elisions and spacing — tested (plan 04, 2026-11-09)

Sandbox (Windows 11 24H2, 3044×1641, AZERTY fr-FR, classic Notepad + Edge `kbd-test.html` textarea, /IT driver),
self-contained publish with `prediction.sqlite`, `pictos.sqlite`, `fonts\`. Text checked by code point
(`WM_GETTEXT` for Notepad, the page title for Edge). Logs, script `p04.ps1` and 50 screenshots: the
test-artefacts folder `test-runs\04-punctuation-spacing\sandbox\` kept outside the repository. Each scenario
passed in Notepad **and** Edge in the final run (a full earlier run in a sandbox that later crashed gave the
same results).

| # | Scenario | Result (exact text) |
|---|---|---|
| P1 | `vers ` → F3 `l'` (no space, `insert 'l'' … space=False`) → next-word bar `ouest, est, avant…` → `h` → F2 `horizon` | PASS `vers l'horizon␠`; spoken « l'horizon » once (`speak Inserted 'l'horizon'`) |
| P2 | `la mai` F1 + `,` | PASS `la maison,` (`auto-space: ',' -> BS + ','`) |
| P3 | `il fait be` F1 + `.` + ` l` → bar `La, Le, Les, L', Lui…` → F2 | PASS `il fait beau. Le␠` |
| P4 | `bonj` F1 + `!` (VK_PACKET), and + `?` as a real AZERTY key (Shift + VK_OEM_COMMA) | PASS `bonjour` U+202F `!` / `bonjour` U+202F `?`; the narrow space draws correctly in Notepad (Consolas) and Edge |
| P5 | `la mai` F1 + space → `la maison␠` (absorbed); + `,` → `la maison,`; F1 + space + `e` → `la maison␠e` | PASS |
| P6 | `parce ` → F2 `qu'` → F1 `il` | PASS `parce qu'il␠`; spoken « qu'il » |
| P7 | `la mai` F1, Left, Right (caret jump), `,` | PASS `la maison␠,` (no correction after a jump) |
| P8 | Réglages → « Espace avant ; : ! ? » = Aucune, Enregistrer (`"text":{"punctuationSpace":"none"}`); `bonj` F1 `!` | PASS `bonjour!`; back to Française → the leaf is removed from settings.json |
| fast | right after an insert, `, et` / `! Oui` injected with no delay between keys | PASS `la maison, et` / `bonjour` U+202F `! Oui`: nothing overtakes the fix |

In Edge the empty field is read at the click (start of the text), so `bonj` already offers `Bonjour` (capital at the
start of the text); in Notepad the field was emptied with `WM_SETTEXT` without a jump, so the start is unknown and
no capital is given — as designed.

**Hook timing** (all 16 windows of 200 events of the final run, 60–90 ms/key): max **0.37–3.22 ms**, mean
0.04–0.10 ms. The two highest windows contain scenario keys and fixes (23:11:49 max 3.22 ms, P1–P4; 23:14:04
max 2.47 ms, Edge P4–P6); plain typing without any insertion also reached 0.94 and 1.92 ms (VM scheduling); typing
with inserts and fixes gave 0.37–1.39 ms. The `SendInput` made in the hook for a fix took 0.17–1.42 ms (median
0.20 ms, 32 fixes). **The plan's "max < 1 ms" goal is amended for the fix path**: a key that triggers a fix costs
up to ≈ +1.4 ms (still far below the system's low-level-hook timeout), because the `SendInput` is kept on the hook
thread on purpose — it is what guarantees the order of the following keys. That order was verified with
**injected** keys only (`, et` / `! Oui` sent back to back); ordering with a physical keyboard is not proven.

**Known side effects of D22** (only right after an inserted word, by design of the rule list): `il fait` F1 then
`-5` → `fait-5`; `il dit` F1 then `'bonjour'` → `dit'bonjour'` (an opening quote after our space is glued like an
apostrophe); U+202F is drawn as `?` by windows that are not Unicode (ANSI controls) — the hidden setting
`text.punctuationSpace = "french-nbsp"` (U+00A0) or « Aucune » avoids it.

**Review fixes** (2026-11-09, unit-tested; not re-run in the sandbox): no fix while Ctrl / Alt / AltGr is held (a
Backspace under a modifier would be a shortcut); a key typed while an insertion is being injected resets the tracker
and leaves the auto-space disarmed; `t` right after a typed `-` stays `t` (`a-t-il`); tokenizer: combining marks
belong to their word (NFD), closing `» ) "` stay with the sentence they end; h-aspiré list: `hallucination`,
`Haïti` are mute, `harpon` aspirated.

**Word on the host** (owner-approved exception D2, owner present, `WordHostTest.ps1 -Suite plan04`, 2026-11-09,
first run with the fixed U+202F rule): WP2 `Il dit la maison,` PASS; WP5 (extra space absorbed, then `,`) PASS;
WP4 / WP4b `bonjour` U+202F **U+00A0** `!` / `?` FAIL — Word's French AutoCorrect added its own no-break space after
ours; WP8 (« Aucune ») `bonjour` U+00A0 `!` — Word's own space. Hence the owner's decision « laisser faire Word »,
made adaptive (keyboard-design §5b): in Word we send the punctuation alone, read back, and add our U+202F only where
Word added none (English paragraph, AutoCorrect option off). The suite now expects U+00A0 alone in a French
paragraph (WP4, WP4b, WP8 — the latter is Word's doing, not ours), our U+202F in an English paragraph (WP4en,
proofing language set on the test document only) and exactly one space when going back to French in the same window
(WP4fr2); its double-space detector now flags any two consecutive space-like code points. Re-run: pending.

## Re-read of edits — tested (plan 06, 2026-11-11)

Sandbox (Windows 11 24H2, 3044×1641, AZERTY fr-FR, classic Notepad + Edge `kbd-test-06.html`: textarea, text input,
password input, contenteditable; /IT driver; SAPI Hortense), self-contained publish. What is spoken is checked from the
`--log` trace (`speak Reread '…'`), the text by `WM_GETTEXT` (Notepad) or the page title (Edge). Script `p06.ps1`,
logs, `results.txt` and screenshots: the test-artefacts folder `test-runs\06-reread-edits\` (outside the repository;
`run2\` = E1–E9, `run3\` = E10 + hook timing, `run1-crashed\` = a first run whose sandbox died, screenshots only).

| # | Scenario | Result |
|---|---|---|
| E1 | Notepad `Le ch‸t dort bien.` + `a`, click inside `bien` | PASS `speak Reread 'chat'` (landing SameSentence) |
| E2 | Notepad `Le ‸chat dort.` + `gros␣`, click in the next paragraph | PASS `'Le gros chat dort.'` only (no word) |
| E3 | Notepad, `a` inside `cht`, ← → → (3 × `skip(same word)`), → into `dort`, ↓ | PASS `'chat'`, then `'Le chat dort bien.'`; nothing while inside the word |
| E4 | Notepad Backspace in `chaat` + End; Delete in `xchat` + Ctrl+End | PASS `'chat'` / `'Le chat dort.'` (the Delete read is a rebase, `read del`, not a leave) |
| E5 | Notepad `pe` + F1 `petit` in the middle, then switch to Edge | PASS `speak Inserted 'petit'` once, then `'Le petit chat dort.'`; the word not repeated |
| E6 | Edge textarea, `a` in `cht`, click in `bien`, ↓ | PASS `'chat'`, `'Le chat dort bien.'` (UIA reads 11–12 ms) |
| E7 | Edge contenteditable `gros␣` in the middle, click on `pleut` | PASS `'Le gros chat dort.'` |
| E8 | Edge: Tab from the edited textarea to a text input; edit the input, Tab to the password field and type `zqxwvk` | PASS sentence spoken / `reread skip(password)`, nothing spoken, the secret 0× in the log |
| E9 | Pause, edit + click → nothing (`reread` 0, `speak` 0); Reprendre, same → `'chat'` | PASS |
| E10 | « Relire… » off (`"speech":{"rereadEdits":false}`) → nothing; on + word echo: `gros␣` → `speak Word 'gros'`, click in the sentence → `skip(word already spoken)`, next paragraph → the sentence; typing at the end of `Le chat` → `skip(not inside the text)` | PASS |

**Review fixes, re-run** (2026-11-11, `run4-review-fixes\` and `run5-hook-timing\`): E1–E10 PASS again, plus

| # | Scenario | Result |
|---|---|---|
| E11 | Edge `kbd-test-06b.html`: `gros␣` typed mid-sentence; 800 ms later the page shows a dialog and **focuses its password field by itself** (no key, no click); `zqxwvk` typed there; click back in the text | PASS `focus moved inside the window` → `read focus status=Password` → `reread skip(password)`; nothing spoken; the secret 0× in the log (page title `P[6]`: it did go to the password field) |
| E12 | Edge two-step login: `eleve`, Home, `x`, Enter (the page focuses the password field), `zqxwvk`, click back | PASS same trace; nothing spoken; the secret 0× in the log |
| E13 | Notepad edit, click on the tray icon (menu), Esc, back to Notepad, click in the sentence | PASS nothing said at the tray click; back in the text: `skip(same word)`, then the word on the next click |

The first E11 / E12 attempt of `run4` (marked FAIL in its `results.txt`) does not count: it was a setup failure — Edge
had restored a background tab with the same element ids, the script clicked the wrong one, the dialog never opened and
`gros zqxwvk` went into an ordinary text field (logged as typed text only because `--log` logs typed words; nothing
was a password field and nothing was spoken). The script now picks the visible element and checks the focus first;
the re-run is the one reported above.

**Second review** (`run6-second-review`): edit probe + proof rule, leave during a pending probe, begin version check,
warm-up of the mouse / focus / probe paths. E1, E6, E11, E12, E13 PASS (in E12 the edit probe itself caught the
password field: `reread: edit probe Password 21 ms, session dropped`); the hook window right after the start is now
**max 0.66 ms** (was 2.25 ms), then 0.33–0.68 ms; 23 re-reads still spoken in the mid-edit part.

Hook with the focus WinEvent installed: max 0.29–0.42 ms per 200-event window in steady state (2.25 ms in the
first window after the start), no focus jump while typing in Notepad. Two sandboxes vanished mid-run during this
iteration (the Windows Sandbox server restarted on its own; no host event logged); every result above was saved
before.

**Timing**: read → `speak Reread` 0–3 ms, read → `speech started` (SAPI `SpeakStarted`) 0–7 ms in every scenario
(the plan's ≤ 250 ms). The audible start after `SpeakStarted` is not measured. **Hook** (200-event windows): plain
typing max 0.36–0.42 ms, mid-sentence edits with Backspace / Delete / arrows / clicks max 0.36–0.62 ms (< 1 ms); the
windows that also hold D22 auto-space fixes and the settings window reached 2.5–4.3 ms (the `SendInput` of the D22
fix path, amended in plan 04; not attributable to the session code, which is O(window) string edits).

**Observed, not changed**: a click on the tray icon is a window switch (taskbar), so an edited sentence is said
before the menu opens. `reader warm-up error: COMException` appeared at two of three starts (UIA `FocusedElement`
while nothing was focused; the reads that followed worked).

**Word on the host** (owner-approved exception D2, owner present, `WordHostTest.ps1 -Suite plan06`, French
document, AutoCorrect on; output `word-host\plan06-owner-run1..3\`, outside the repository):

| Run | Result | What it led to |
|---|---|---|
| 1 (dba90df) | WR2, WR3, WR4a/b, WR8, WR9 PASS; WR1 FAIL — the first UIA read after a start was cold (383 ms > 150 ms: no session); WR5 / WR6 / WR7 harness failures; WR10 FAIL on that cold read | reader warm-up runs the TextPattern calls once (no text read); harness: own switch window, bool binding, occluded click point |
| 2 | WR1–WR8 PASS (cold first read 12 ms); WR9 FAIL — the begin read of a 700-character paragraph took 158 ms; WR10 FAIL (reads 56–89 ms, host load likely); WR6 / WR8 said the typed « tres » / « ca » where AutoCorrect wrote « très » | 400 ms budget for reads used by the re-read; the document's text is said when the landing read shows it; slow-read phases logged |
| 3 | **9 / 10 PASS**: WR1–WR7, WR9, WR10 (reads 11–91 ms after warm-up, cold first read 11 ms; edit probes all NotPassword); WR6 says « très » (document) | WR8 FAIL was the harness only: a one-element array returned by a PowerShell function comes back as a string, so `$said[0]` was its first character — document and speech were identical (Word did not AutoCorrect « ca » this time) |

Slow-read phases in run 3 (> 40 ms): `focus+pattern` 77 / 70 ms (`FocusedElement` + `TryGetCurrentPattern` on a
fresh document after the script replaced its content; the text calls themselves 1 + 5–6 ms), and the 700-character
paragraph `window 36 ms, widen 49 ms (widened)`. The cost is in Word's provider resolving the focused element, not in
our text-range work; the 400 ms re-read budget covers both. Not repeated after the harness fix (no product change).

## Selection reading — tested (plan 07, 2026-11-11)

Sandbox (Windows 11 24H2, 3044×1641, AZERTY fr-FR, classic Notepad + Edge `kbd-test-07.html`: static paragraph,
textarea, filled password field; a custom-drawn WinForms window without a UIA text pattern for S12; /IT driver; SAPI
Hortense), self-contained publish. What is shown / said is checked from the `--log` trace (`reading shown`, `speak
Reading`, `reading progress`), the controls through UI Automation (AutomationIds `ReadPlay`, `ReadNext`, `Tile9`,
`ReadTooLong`, `ReadingWindow`…), Notepad's text by `WM_GETTEXT`. Script `p07.ps1`, logs, `results.txt`, screenshots:
the test-artefacts folder `test-runs\07-selection-reading\` (outside the repository): `run3-final\` = S1–S12, `run4-final-build-confirm\`
= S1, S7, S9 on the final build, `run5-d26\` = S13 (D26) + S1, S2, S4; `run1-sandbox-died\` (the sandbox vanished after S10)
and `run2-debug\` = the runs that found the bugs below.

| # | Scenario | Result |
|---|---|---|
| S1 | Notepad drag over 47 words | PASS in run3 / run4 (78–110 ms after the button-up, show 18–48 ms); **FAIL in run5** (406 ms: WPF `Show()` of the sandbox, see the timing note) |
| S2 | Edge: drag over a static paragraph; Ctrl+A in a textarea | PASS (UIA selection read 6–22 ms; the page's document element gives the selection) |
| S3 | Word drag | PASS on the host (`-Suite plan07` WS1, owner run 1: 234 ms after the gesture) |
| S4 | double click on a word | PASS one tile, 62–63 ms |
| S5 | Shift+→ × 8 | PASS 8 caret reads, **1** selection read 250 ms after the last key |
| S6 | Ctrl+A on 1320 words | PASS 1000 words (truncated), 1083 tiles, message shown; first 40 tiles 13 ms, all in 292–394 ms in 28 batches, max batch 16.6–23.4 ms (build + layout) |
| S7 | Space / F9 / buttons: play, pause, resume at the word, ⏭, ⏮, to the end | PASS Notepad text unchanged (Space / F9 swallowed), 8 utterances; highlight = spoken word 37 / 37 |
| S8 | tile click while playing | PASS `speak Manual 'beau'`, next Play = `'Il fait beau dehors.'` |
| S9 | « grande fenêtre »: Space / F9 inside, move + resize, Esc, reopen | PASS `reading.window {300, 250, 1500, 900}` saved, reopened at the same place |
| S10 | exit by deselection, Esc (then Ctrl+C: not shown again), typing | PASS; typing → `typed='mai'` predictions again |
| S11 | Ctrl+A and a drag in a filled password field | PASS `status=Password`, nothing read or shown, the value 0× in the log |
| S12 | Ctrl+C fallback off / on in a window without text pattern | PASS off: no Ctrl+C sent (window counter 0), clipboard intact; on: copied in 358–379 ms, 6 formats restored, `Get-Clipboard` = the original |
| S13 | D26: « Il fait beau. Le ch[at dort bi]en ! Fin. » | PASS reads `'chat dort bien !'`, last tile « bien ! », Notepad selection unchanged (19,29) |

**Highlight sync** (S7 + S9, three runs, 92 progress events): highlighted tile = spoken word **100 %**; event → highlight
on screen (UI thread) median 0.4–0.5 ms, max 1.3 ms; wall clock / SAPI audio position per utterance 0.984–1.005 (the events
follow the audio), the first event 110–290 ms after the sentence is handed to the speech thread. The audible delay of the
audio device itself is not measured (no recording); the frames `S7-rec-f00..09` (every 350–800 ms, the screenshot cost)
and `S7-highlight-recording.gif` show the highlight moving, and match the log (frame 21:06:40.239 shows « Il », event at
40.212).

**Timing note (≤ 250 ms: met on the host — Word WS1 234 ms, `WPF Show()` 4.3 ms; not in every sandbox run)**: in the
sandbox, gesture → reading bar, all `reading shown` lines of the logs (Shift+→
excluded: 250 ms debounce by design; the Ctrl+C fallback excluded: 593 ms including the copy). run3 / run4: 25 of 31
shows 62–157 ms, **6 at 375–485 ms** (show 194–372 ms, during the repeated Ctrl+A of the hook-timing loop); run5 (fresh
sandbox, 2 min after its boot): 9 of 15 at 62–172 ms, **6 at 406–625 ms** (show 344–517 ms). The slow ones were spent in
the `view` phase, i.e. the first tiles + WPF's `Show()` of the hidden window, and the existing suggestion bar's re-show
measured 230–500 ms in the same run5 sandbox (12–18 ms in earlier sandboxes), so the cost is the sandbox's software
rendering of `Show()`, not the reading code; the log now splits that phase into `tiles` and `WPF Show()` so the next run
shows it directly (not re-measured after the split: see the review fixes below). Fixed during the iteration: the first reading view after a
start cost 470–560 ms (the tile area's scroll bar created on first use) → the bar warm-up now shows a 132-tile view
off-screen (26–48 ms after).

**Hook** (200-event windows): plain typing max 0.55–0.90 ms; reading mode (Space / F9 swallowed, Shift+→, typing that
leaves) max 0.30–0.34 ms in steady state; the first window after a start reached 1.33 ms (run3) and 1.27 ms (run5), first
use of those paths — above 1 ms, outside steady state.

**Bugs found by the runs and fixed**: a selection dismissed with Esc stayed dismissed after the selection had gone
(only cleared while the view was shown); tiles were `Border`s without an automation peer (not clickable through UIA,
invisible to screen readers) → templated `Button`s; first-show cost (above).

**Review fixes, re-checked** (2026-11-11, fresh sandbox, `run6-review-fixes\`). Three cases failed on their first try
in this run, then passed: **S1** (first show 531 ms, `WPF Show()` 404 ms — the sandbox's show cost; the next show 78 ms);
**S10c** (`shown again after Shift+Right=0`, text `[xla mai.]`) — explained, not a product fault: after Esc dismissed the
whole text, Shift+← deselected the final « . », and D26 added the sentence end back, so the reading text was the
dismissed one and was rightly not shown again; nothing being shown, the typed « x » then replaced the whole selection
(`xla mai.`). The test now makes a different selection (a double click); **S14** (the space did reach Edge and nothing
toggled, but the check compared the whole title `T[abc ] et 1 page supplémentaire`, Edge's restored-tabs suffix) — a
harness check, now a prefix match. Then: S7, S10, S11 PASS; **S12 a/b/c PASS** — c is a
late copy (the test window writes the clipboard 800 ms after Ctrl+C): `no copy within 300 ms, a late copy after 895 ms,
6 format(s) restored`, `Get-Clipboard` = the original; **S14 PASS** — the reading shown for Notepad, Edge brought to the
front and Space pressed at once: the space is typed in Edge's textarea, no Play / Pause. S1: first show **FAIL** 531 ms
after the gesture with the log now reading `WPF Show() 404.3 ms` (tiles 27.9 ms), the next one PASS 78 ms (`WPF Show()
5.6 ms`) — the sandbox's `Show()` cost, attributed directly. No 200-event hook window in this short run.

**Clipboard owner rule, re-checked** (2026-11-11, one short sandbox session, `run7-clipboard-owner\`): S12 a / b / c PASS
again, and **S12d PASS** — the test window copies 800 ms after our Ctrl+C and the user presses Ctrl+C meanwhile: `a late
copy after 885 ms, the user copied meanwhile: not restored`, the clipboard keeps the user's copy. A change by another
application (password manager clearing it) and a second write by the application after the restore are unit-tested (fake
clipboard / owner), not run in the sandbox.

**Word on the host** (`WordHostTest.ps1 -Suite plan07`, WS1–WS9; owner run 1, 2026-11-11, output
`word-host\plan07-owner-run1\` and `plan07-owner-run2\` outside the repository): **WS1–WS9 all PASS across runs 1 and 2**.
Run 1: **7 / 9 PASS** — WS1 drag 234 ms after the gesture (`WPF Show()`
4.3 ms), WS2, WS3, WS4 (1000 words, 297 ms, max batch 22.4 ms), WS6, WS7, WS9 (D26 in Word: `'chat dort bien !'`,
selection unchanged). Failures:
- **WS5** (harness): it waited for `reading: state Ready` since the scenario's start and found the one logged when the
  bar appeared, so it stopped right after the resume (2 utterances counted, the reading went on). Everything it did
  check passed: document unchanged, paused by F9, highlight = spoken 7 / 7. Fixed: it now waits for a Ready logged after
  the resume.
- **WS8** (product): the large window opened **behind Word** — the bar never activates, so Word keeps the foreground and
  Windows' foreground lock refused the plain activation. The sandbox S9 had hidden it (its script activated the window
  itself). Fixed: after a click on ⤢ the window is activated, and when that is refused, the foreground thread's input is
  attached for the `SetForegroundWindow` call and released (`ReadingWindow.BringToFront`; log `large window opened (in
  front: True|False)`); S9 now checks that the window comes to the front by itself.

Run 2 (`-Only WS5,WS8`, the fixed build): **WS5 PASS** — document unchanged, paused by F9, 4 utterances, highlight =
spoken 18 / 18 (event → highlight median 0.32 ms, max 1.01 ms; wall / audio 1.000–1.004); **WS8 PASS** — `large window
opened (in front: True)`, Space played, F9 paused, Esc closed it.

## Piper voice siwis — tested (plan 08a, 2026-11-12)

**Golden test** (xUnit): the C# port of Piper's phonemizer equals piper-tts 1.8.0 on the 40 plan 05 items — sentences
and ids 40/40 from Piper's clauses, and our espeak-ng build through the P/Invoke gives Piper's raw clauses 40/40 and the
same ids end to end 40/40. espeak-ng data byte-identical to the Piper wheel; two builds give the same DLL.

**Host bench** (`--speech-bench`, dev PC, 12 logical CPUs, release build, real WASAPI device, words ×4 and sentences ×1
to keep the sound short; evidence kept with the test runs, `08a-piper-voices\bench-*`):

| Gate | normal | 2core |
|---|---|---|
| Word echo first audio p95 ≤ 300 ms | 145 ms | 129 ms |
| Sentence first audio p95 ≤ 500 ms | 209 ms | 211 ms |
| Cancel → done / → audio stops p95 ≤ 100 ms | 0 / 72 ms | 1 / 71 ms |
| Highlight: units with one event, lag in [−20, +100] ms ≥ 95 % | 100 % (224/224), lag p95 14 ms | 100 % (224/224), lag p95 14 ms |
| Working set after warm-up ≤ +150 MB / peak ≤ +250 MB | +124 / +201 MB | +125 / +202 MB |
| CPU 2 s after the last utterance < 1 % | 0.00 % | 0.00 % |
| Synthesis to first chunk, words / sentences p95 (plan 05: 50 / 132, 46 / 129 ms) | 77 / 145 ms | 64 / 144 ms |
| Load (verify, espeak, session, warm-up) | 3.2 s | 3.1 s |

Two fixes came out of the bench: the ORT memory pattern is off (peak working set +250 → +202 MB) and an idle device
gets zeros without resampling (CPU after the last utterance 2.3 → 0.0 % of one core in 2core).

*Synthesis time against plan 05* (review follow-up, null audio, normal profile, words ×4, sentences ×1): the ORT run
alone (`synth S ms` of each utterance) is words p50 / p95 **45.5 / 53 ms**, sentences **126 / 145 ms** — plan 05 measured
50 / 132 ms, so the model call is on par. The bench's "synthesis to first chunk queued" adds the espeak phonemization,
the word map and the thread hand-offs (≈ 5–25 ms, noisy: 57–77 ms p95 for words across runs). A/B with ORT spinning
**on**: run alone 41 / 49 ms (words), 119 / 134 ms (sentences), CPU 2 s after the last utterance 0 % in both. Spinning
saves ≈ 4–10 ms; it stays **off** (A8: spinning threads use CPU between utterances, and the gain is far below the gates).

*Long text without punctuation* (1000 words, one espeak sentence of ≈ 5000 phonemes, `--long-text`, null audio):

| Longest ORT run | First audio | Peak working set |
|---|---|---|
| unsplit (one run) | 29.2 s | +4886 MB |
| 400 phonemes | 1.21 s | +543 MB |
| **200 phonemes (shipped)** | 0.66 s | +347 MB |
| 120 phonemes | 0.46 s | +301 MB |

A sentence longer than 200 phonemes (≈ 35 words) is now synthesised in pieces cut at a clause (`, : ;`), else at a word
boundary; none of the 40 test items comes near it (longest 52 phonemes). The word map then fell back to proportional
placement (993 espeak words for 1000 units).

**Test VM** (`predict-test-win11`, Windows 11 26H2 fr-FR, clean checkpoint, no sound device: Piper ran with the test-only
`--audio null`, a stopwatch timeline; the audible gates are the host bench's), scenario script `p08.ps1`:

| ID | Result |
|---|---|
| N1 fresh profile | PASS: Siwis selected by default; `piper:siwis ready` after `hooks installed`; À propos « Voix » credits (N1-2-about.png). First run without the VC++ runtime failed to load ORT → fixed by the app-local VC++ DLLs (A13). |
| N2 F1 within 1 s of start, then after ready | PASS: `using SAPI until piper:siwis is ready`, then `piper first audio` |
| N3 legacy `Microsoft Hortense Desktop` | PASS: `migrated to sapi:…`; Hortense preview by SAPI (Piper not loaded for an explicit SAPI voice); Siwis preview waits for the load, then Piper; saved `piper:siwis` |
| N4 word + sentence echo | PASS: 4 words + the sentence by Piper |
| N5 three sentences with N01 | PASS: 14 progress events, one each, « 2026 » one event on its tile (N5-2-highlight-mid.png) |
| N6 Space pause; tray Pause during a sentence echo | PASS: `speech cancelled (0 ms after the request)` both |
| N7 no pack / one byte changed | PASS: Hortense default, SAPI only; `piper:siwis unavailable (InvalidDataException: sha256 mismatch: fr_FR-siwis-medium.onnx), using SAPI` |
| N8 ES_PASSWORD field | PASS: `field probe: password field, tracking suspended`, nothing spoken, no typed text in the log |
| Extra: no `--audio null` (no sound device) | `audio device open failed (COMException)` ×2 → `utterance given to SAPI`; no crash |
| Extra: start → `hooks installed`, 3 starts each (plan step 10, ±10 % of 0.4.0) | **FAIL as written, because 0.5.0 was faster**: 0.4.0 9631 / 3696 / 2490 ms (mean 5272), 0.5.0 5778 / 3851 / 1387 ms (mean 3672). Very noisy (cold starts in the VM); the Piper load never delays the hooks (it starts after them). |

**Word on the host** (owner run, 2026-11-12, release 0.5.0, `word-host\CHECKLIST.md` steps 1–8, log archived in
`word-host\` outside the repository): **all steps PASS** (owner: « Tout est OK »). Siwis ready 4217 ms after start
(verify 550, espeak 128, session 3284, warm-up 91 ms); word / sentence echo first audio 89–257 ms; 23 reading progress
events, highlight applied 0.3–1.4 ms after each event; « heard = highlighted » confirmed by ear; no SAPI fallback, no
exception; password field: content not read.

## Two Piper voices and liaison grouping — tested (plan 08b, 2026-11-13)

Release 0.6.0 (private test release, D28): « Siwis — voix naturelle (femme) » (default) and « Pierre — voix naturelle
(homme) » (`piper:pierre`, speaker 1 of `fr_FR-upmc-medium`, `"public": false`). One Piper engine holds both voices;
the configured voice stays loaded, a voice loaded for « Écouter » only is unloaded 30 s after its last utterance.

New log lines (no text in them):

```
speech: piper:pierre ready in T ms (verify V, espeak E, session S, warm-up W ms, 2 threads, speaker pierre=1)
speech voice: piper:pierre (fr_FR-upmc-medium, speaker pierre, 22050 Hz) [fr-FR], rate 0, volume 100
speech: piper:pierre unloaded (idle)                      # a preview-only voice, 30 s after its last utterance
speech: piper:siwis unloaded (configured voice changed)   # a SAPI voice saved, once idle
speech: piper:siwis unloaded (replaced by piper:pierre)   # Enregistrer with pierre: once pierre is ready and siwis idle
speech: using piper:siwis until piper:pierre is ready     # make-before-break: the old Piper voice bridges the load
speech: piper:pierre could not be loaded, keeping piper:siwis
speech: preloading piper:pierre (selected in Réglages)    # selection in the voice list, before Enregistrer
speech: using SAPI until piper:pierre is ready            # per voice, only when no Piper voice is loaded
speech: piper first audio T ms (synth S ms, 1 sentences, word: N phonemes, ns 0.333 nw 0.2)   # an isolated word
speech: piper first audio T ms (word cache hit)           # the same word again: its first rendering replayed
speech: piper first audio T ms (synth S ms, word carrier: N ids, cut at C of D ms, ns 0.5 nw 0.333)   # 0.7.2, pierre
speech: piper first audio T ms (word cache hit, carrier)  # 0.7.2: a carrier word replayed
piper: word carrier fallback (<reason>)                   # 0.7.2: cut not possible: the word said alone (word scales)
sentence echo from the document: T ms (C chars, K typed) # « . » after a jump into the sentence (D17 amendment)
sentence echo: typed words (read <status> T ms)           # read failed / over 400 ms / sentence not found
reread skip(sentence already spoken) landing=…            # D23: that sentence was just said from the document
speak Word 'les amis'                                     # liaison grouping: « les » was said just before
speak Inserted 'les enfants'                              # F1 after « les »
```

**Golden test**: pierre's fixture (piper-tts 1.8.0, `--speaker pierre`) 40/40 from Piper's clauses and 40/40 end to end
through our espeak-ng; siwis unchanged 40/40; the raw clauses of both fixtures are identical. The alignment patch of
upmc reproduces `7f763f70…`.

**Host bench** (release build, real WASAPI device at volume 30, words ×4, sentences ×1; null-audio runs for synthesis;
evidence in the test-run folder `08b-pierre\bench-*`):

| Gate | siwis normal | siwis 2core | pierre normal | pierre 2core |
|---|---|---|---|---|
| Word echo first audio p95 ≤ 300 ms | 113 | 112 | 99 | 101 |
| Sentence first audio p95 ≤ 500 ms | 202 | 200 | 199 | 203 |
| Cancel → done / → audio stops p95 ≤ 100 ms | 0 / 61 | 1 / 61 | 0 / 62 | 3 / 63 |
| Highlight ≥ 95 % in [−20, +100] ms | 100 % (224/224), p95 13 | 100 %, p95 13 | 100 %, p95 13 | 100 %, p95 13 |
| Working set after warm-up ≤ +150 / peak ≤ +250 MB | +125 / +203 | +125 / +203 | +133 / +237 | +136 / +238 |
| CPU 2 s after the last utterance < 1 % | 0.00 % | 0.00 % | 0.00 % | 0.78 % |
| `_model.Run` alone, words / sentences p50 / p95 ms | 46/54, 96/141 | 47/53, 99/141 | 35/42, 99/144 | 34/43, 95/146 |

After the review fixes (arena rebuild, re-route, queue merge) one pierre normal device run was repeated: word / sentence
first audio p95 101 / 196 ms, cancel 0 / 61 ms, highlight 100 % (224/224), working set +133 / peak +237 MB, model
call alone 42 / 139 ms p95 — all gates met. CPU 2 s after the last utterance was 0.78 % (a run just before it: 1.55 %,
gate missed): the process CPU time moves in 15.6 ms clock ticks, so over the 2 s window one tick reads 0.78 % and two
read 1.55 %; the gate is at the measurement's resolution.

pierre's model call alone (device runs) is within ±30 % of plan 05 (word p95 37 → 42 ms, sentence 147 → 144 ms). Null
audio runs of the same build were slower for both voices (pierre words 51/65, sentences 155/215 ms; siwis words 68/80):
those runs leave no idle time between utterances.

*Voice switch* (`--switch`, device audio: siwis configured speaks, rests — device idle-closed, 35 s — for the
baseline; « Écouter » pierre; siwis echo; pierre's unload; 35 s rest): pierre held then spoken after its load, 4.1–4.3 s
to first audio; **both resident +262 MB** (gate ≤ +350: met; +316 MB before a preview-only load ran without ORT's
memory arena); pierre unloaded **30.2 s after its own last utterance**; 35 s after the unload **+176 MB against the
siwis-alone rested baseline of +176 MB (+0.3 MB)**. The plan's original « ≤ +170 MB » was below what one voice alone
rests at once it has spoken (first runs: +175–177 MB, failed as written); the owner re-baselined the gate like for like
to « ≤ baseline + 10 MB » (plan 08b, Goal 3): **met**. Within that measurement the unload leaves no lasting growth.

A voice loaded for a preview that becomes the configured one (Enregistrer) gets its session rebuilt with the arena
once idle, in the background, then swapped in on the `piper` thread (`speech: piper:<id> session rebuilt with the
memory arena in T ms (now the configured voice)`). A voice unloaded between the router's check and the utterance
(idle-unload race) is loaded again and the utterance waits or goes to SAPI meanwhile (`speech: piper:<id> was unloaded
meanwhile, loading it again`), never a speech error.

**Test VM** (`predict-test-win11`, clean checkpoint, `--audio null`, scenario script `p08b.ps1`):

| ID | Result |
|---|---|
| P1 fresh profile | PASS: list Siwis, Pierre, Hortense, Zira; Siwis selected; À propos one block per voice (CC BY-SA, lessac line) (P1-3-about.png); only siwis loaded |
| P2 Pierre → Écouter → Enregistrer → restart | PASS: `waiting for the Piper voice to load`, `piper:pierre ready … speaker pierre=1`; `piper:siwis unloaded (configured voice changed)`; after restart only pierre loads |
| P3 preview Pierre, Annuler, type a word | PASS: `speak Word 'maison'` by siwis, no reload; `piper:pierre unloaded (idle)` 28.5 s after the echo |
| P4 « les amis », « un enfant », « c'est un », « les héros », « et un »; F1 after « les en » | PASS: `speak Word 'les amis'`, `'un enfant'`, `'c'est un'`; « héros » and « un » alone; `speak Inserted 'les enfants'`; hook max 0.646 ms |
| P5 reading three sentences with pierre | PASS: 14 progress events, « 2026 » once |
| P6 one byte of pierre's model changed | PASS: `piper:pierre unavailable (InvalidDataException: sha256 mismatch …), using SAPI`; Siwis preview still by Piper |
| P7 saved pierre, pack removed | PASS: `voice 'piper:pierre' not installed, using the default voice`, siwis loads; Réglages show « Pierre … (non installée) » |
| P8 password field with pierre | PASS on the second run (`field probe: password field`, nothing spoken); the first run logged nothing at all (the test window probably did not get the keys) |
| P9 0.5.0 settings `piper:siwis` | PASS: kept, no migration line |

Found and fixed during the VM run: after Enregistrer arrives while « Écouter » still speaks, the configured settings
were applied only at the next utterance, so the old voice stayed loaded; the speech worker now applies the configured
settings again as soon as it is idle (P2 then logs the unload).

**Word on the host** (2026-11-14, release 0.6.0, run by the assistant at the owner's request with the owner present;
scripted SendInput into Word, logs kept outside the repository). Same steps for
siwis and pierre (`speech voice: piper:pierre (… speaker pierre=1)`, ready in 4420 ms; siwis 3144 ms):
- word echo: « les amis », « un enfant », « ils ont », « deux heures », « petit ami », « c'est un » → the trigger word, then
  the group (`speak Word 'les'` then `speak Word 'les amis'`); « les héros » and « et un » → not grouped. PASS, both voices.
- reading two paragraphs (Ctrl+Shift+↓, Space): 27 and 21 progress events, highlight applied ≤ 1.33 ms after each event,
  0 engine-word / tile mismatches; Space during the reading → `Paused`, `speech cancelled (0 ms after the request)`;
  Space again resumes at the sentence. PASS, both voices. First audio ≤ 289 ms; no SAPI fallback, no error.
- not judged (needs a listener): whether the liaison is *audible* in each group, whether « les » … « les amis » sounds
  natural, and pierre's blind sheet (step 7) — pierre keeps V1 until the owner rates it. *(Superseded 2026-11-14: the
  owner rated the sheet, pierre uses V4 — `voice-evaluation.md` § 9c.)*

**Follow-up of the owner's 0.6.0 run (2026-11-14), same version, `publish-0.6.0-new`:**

- *Voice switch said by Hortense*: Enregistrer with Pierre unloaded siwis at once and the next echoes went to SAPI for
  the 4.8 s of pierre's load. Now make-before-break: siwis keeps speaking (`using piper:siwis until piper:pierre is
  ready`) and is unloaded once pierre is ready (`unloaded (replaced by piper:pierre)`), or kept when pierre's load
  fails; both models are resident for the load (switch gate ≤ +350 MB, measured +262 MB with both). Selecting a Piper
  voice in the list starts its load at once (preview-only: unloaded 30 s after the load when not saved), so Enregistrer
  usually finds it ready. Unit-tested (`EngineRouterTests`, `PiperVoicesTests`); VM F1 below.
- *Pierre's scales*: blind-sheet winner V4 (0.5 / 1.1 / 0.333) in its card; siwis `length_scale` 1.1 (a little slower
  by default); the speed setting multiplies the voice's base (`voice-evaluation.md` § 9c).
- *Isolated words*: lower noise (`inference_word`, pierre) and a word audio cache — five « très » identical sample for
  sample (bench `--word très --word-cache on`, null audio; § 9c).
- *Sentence echo after an edit (D17 amendment)*: a sentence end typed after a jump into the sentence says the whole
  sentence read from the document (own `sentence-reader` queue: the same read as a jump's, password check first,
  ≤ 400 ms; a jump makes it stale, typing does not); the words typed since are the fallback. The D23 re-read of that
  sentence on leaving is skipped only if that sentence was spoken or is being spoken (its outcome is followed);
  the modified word is marked spoken. A read that finds the caret's sentence unfinished (the period not in the
  document yet) never takes the sentence before it: it reads once more 80 ms later, then falls back. While the read is
  pending, the next echoes (a word typed right after the period, an inserted word) wait and follow the sentence
  (`SentenceEchoGate`); a read not back within 1.1 s is given up (typed words, then what waited). Unit-tested
  (`SentenceEchoTests`); VM F3 below; not yet run in Word.
- *Réglages*: the chosen speed (« Normale (0) », « Un peu lente (−2) », « Lente (−5) », « Très lente (−8) », « Un peu
  rapide (+2) », « Rapide (+5) », « Très rapide (+8) »: bands 0 / 1–3 / 4–7 / 8–10) and volume (« 80 % ») next to the
  sliders, and in the sliders' UI Automation name (« Vitesse : Un peu lente (−2) ») and help text.
- *À propos taller than the screen*: the credits (Pictogrammes, Lexique, Polices, Voix, Licences) are Expanders in a
  ScrollViewer, closed by default; the header and « Fermer » stay outside the scroll area; the window's MaxHeight is the
  work area of its monitor minus 32 DIP (per-monitor DPI), and it moves up when an opened section makes it reach the
  bottom. Each section has an AutomationId (`AboutPictos`, `AboutLexique`, `AboutFonts`, `AboutVoices`,
  `AboutLicences`) and the UIA ExpandCollapse pattern; Tab / Space open them from the keyboard.
- *Discreet loading feedback* (owner, no popup, nothing in the bar): in Réglages a status line under the voice list,
  live (« Chargement de la voix Pierre… » → « ✓ Voix Pierre prête », or « Voix Pierre indisponible — la voix Windows
  est utilisée »; nothing for a Windows voice), UIA LiveSetting polite + a LiveRegionChanged event so a screen reader
  announces it; the tray tooltip reads « Prédiction — chargement de la voix Pierre… » while the configured voice loads
  (start-up, Enregistrer). Driven by the engine's state events (now also raised when a load starts and on an unload),
  marshalled to the UI thread.
- *A voice loads on its own worker* (`piper-load`, BelowNormal, one load at a time): the first VM run showed the
  bridge's first echo queued 6 s behind pierre's load on the `piper` thread; now the loaded voice keeps speaking during
  the load. The `piper` thread (synthesis) runs at Normal priority, in the app and the bench alike.

**VM run of the follow-up** (`predict-test-win11`, `--audio null`, scenario script `p08bf.ps1`; evidence in the test-run
folder `08b-followup\vm\`):

| ID | Result |
|---|---|
| F1 siwis ready; select Pierre (preload), Enregistrer at once, type « les amis », « chat » during the load | PASS: `preloading piper:pierre`; `using piper:siwis until piper:pierre is ready`; the three echoes by siwis, first audio 248 / 77 / 61 ms while pierre loaded (14.7 s); `siwis unloaded (replaced by piper:pierre)`; the next echo by pierre (`ns 0.333 nw 0.2`); never SAPI |
| F2 Réglages speed −2, volume 80, then +5 | PASS: « Un peu lente (−2) », « 80 % », slider names « Vitesse : Un peu lente (−2) » / « Volume : 80 % », live « Rapide (+5) » |
| F3 Notepad « Il range sa table. Puis il part. », caret after « table. », 4 × Backspace, « pe. », then End | PASS: `sentence echo from the document`, `speak Sentence 'Il range sa tape.'` (not « pe »); on leaving `reread skip(sentence already spoken)` |
| F3b the same edit, then « Elle » typed right after the period (40 ms / key) | PASS: `speak Sentence 'Il range sa tape.'` first, then `speak Word 'Elle'` (deferred behind the read, not lost) |
| F5 loading feedback | PASS: tray tooltip « Prédiction — chargement de la voix Siwis… » at start, then the normal one; Réglages status « ✓ Voix Siwis prête », « Chargement de la voix Pierre… » after selecting Pierre, « ✓ Voix Pierre prête », nothing for Hortense |
| F4 À propos, all sections opened, 1920×1080 100 % | PASS: window capped at the work area (1000 of 1032 px), scrollable, Fermer visible |
| F4b the same at 1280×720 125 % | PASS: 620 of 660 px, scrollable, Fermer visible |

Still host / owner only: everything audible (the VM has no sound device) — pierre V4 and siwis at 1.1, the word cache
in use, a switch heard end to end; Word (UIA) for the sentence echo after an edit; memory during a switch (two voices
plus a session rebuild).

## Pupil profiles and teacher PIN — tested (plan 09A, 2026-11-14)

Test VM `predict-test-win11` (Windows 11 26H2 fr-FR, 1920×1080, `clean` checkpoint), self-contained 0.7.0 publish,
`--audio null`, `--no-welcome` unless the welcome window is under test; driver script `p09.ps1` (UI Automation on our
windows, the native tray menu read through `MN_GETHMENU` / `GetMenuItemRect`, the PIN typed on the on-screen keypad);
screenshots per scenario (D15) in the `09a-profiles` test-run folder.

| # | Scenario | Result |
|---|---|---|
| V1 | 0.6 `settings.json` (count 5, size L, Lexend, speak words, rate −2, welcome off) → start 0.7.0; second start | PASS 7/7: « Mon profil » with the same 6 settings, `settings.json.avant-profils` kept, app state moved, tray menu unchanged (Pause · Réglages… · À propos… · Quitter), the second copy exits and the first shows « déjà démarré » |
| V2 | Réglages → « Activer la gestion enseignant… » | PASS 8/8: PIN created on the keypad (twice), recovery code shown once (`XXXX-XXXX-XXXX`), « Classe » + « Mon profil », tray « Profil : Mon profil · Changer de profil… · … · Espace enseignant… », the section now opens the Espace enseignant; PIN and code 0× in the log |
| V3 | « Classe » = Andika; Léa (chat, orange); Tom (fusée, vert, XL) | PASS 7/7: class file `{"words":{"font":"andika"}}`, Léa's settings `{}`, Tom's `{"words":{"size":"XL"}}`; « Réglages — Léa » shows the inherited Andika; both pictograms found |
| V4 | « Changer de profil… » (switch PIN off) → Tom | PASS 7/7: no PIN, the picker in front, tile « Profil de Tom », the name said (`speak Manual (private, 3 chars)`), Tom's XL Andika bar in Notepad |
| V5 | Réglages as a pupil (edit PIN on by default); 5 wrong PINs; restart | PASS 5/5: the reason sentence, « Code incorrect » 1–4, « Trop d'essais. Réessayez dans 30 secondes. » at the 5th, still waiting after a restart, the right PIN then opens « Réglages — Tom » |
| V6 | Verrouillage: « Code requis pour changer de profil » on | PASS 4/4: PIN asked by « Changer de profil… », the picker after it, Esc keeps the profile (the new-logon part: V12) |
| V7 | Léa (siwis) → Tom (pierre) | PASS 3/3: `speech: using piper:siwis until piper:pierre is ready` for the inserted word, then `piper:pierre ready in 12180 ms` and `piper:siwis unloaded (replaced by piper:pierre)` |
| V10 | Deactivate (PIN), reactivate | PASS 5/5 (V10c first FAILED on a wrong expectation in the script — it expected Léa, the active profile was Tom, which is what the log says was kept; script fixed): the PIN always asked, tray back to single user, 4 profiles kept, teacher.json without PIN; reactivation → new code, list restored; the old PIN refused, the new one accepted |
| V11 | The teacher edits Léa (size L) while Tom is active | PASS 3/3: « Réglages — Léa », Léa's file `{"words":{"size":"L"}}`, no reload of Tom, Tom's bar still XL |
| V12 | Quit + restart in the same logon; VM restart (`--autostart` Run key) | PASS: same logon → `session resumed`, Tom; new logon → `profiles: new session, p-… -> classe`, « Pépite est prêt » notification and the picker in front without PIN although the switch PIN is on, Léa picked (`session start`); manual start at a new logon with the welcome window → the picker only after OK |
| — | Privacy over the whole `--log` file | 0 first names, 0 PINs, 0 recovery codes |

Measures (VM): PBKDF2 600 000 iterations = 229–245 ms per check (236 ms with the process on 2 cores) → kept;
profiles loaded in 16–62 ms (4) / 16 ms (40); start-up with 40 profiles no slower than with 4, working set +2.9 MB;
switch p95 3.4 ms / max 7.5 ms over 20 switches (the synchronous part; a Piper load runs on its own thread); hook
max 0.12–0.68 ms per 200 keys. « Code oublié ? » (V14): a new recovery code from the Code tab, then the PIN dialog's
recovery path set a new PIN and showed a new code; nothing of it in the log. 40 profiles in « Qui es-tu ? » scroll
(screenshot V13-1).
Review fixes re-run (same day, folder `fixes`): V2, V6, V10 (V10c included) pass again; the Espace enseignant asks the
PIN within the grace and right after activation, « Changer le code… » / « Nouveau code de secours… » ask the PIN (the
latter after a confirmation); with « Classe » + one pupil a new logon starts on « Classe » without the picker;
« Utiliser maintenant » closes the Espace enseignant.
Not run: Narrator (owner check), printing the recovery code (no printer in the VM), elevated / multi-account cases,
a real logoff (the new logon was a VM restart, which also sends the session-ending message).

## Export / import and session returns — tested (plan 09B, 2026-11-14)

Test VM `predict-test-win11` (`clean` checkpoint twice: PC « A » then PC « B »), self-contained 0.7.1 publish,
`--audio null`; driver `p09b.ps1` (on top of `p09.ps1`), screenshots per scenario (D15) in the `09b-export-import`
test-run folder. A = « Classe » Andika, Léa (chat, orange), Tom (fusée, vert, XL); B = « Classe » OpenDyslexic + L and
an existing Léa (lion, rouge).

| # | Scenario | Result |
|---|---|---|
| V8 | Export Léa + Tom (no PIN); the file in Notepad | PASS: readable indented JSON (`pepite-profils`, « Classe » andika, « Léa », « Tom »), no `enseignant`, no hash; ticked → hashes only, never the PIN; log with counts only (0.8 KB in 16 ms) |
| V9 | Import into B (another « Classe », an existing Léa) | PASS: « Léa (2) » kept both (default), Tom created and rebased to `{size XL, font andika}` with **identical effective settings** (log line compared A ↔ B), B's « Classe » and Léa unchanged, `-import` backup before writing, summary + confirmation shown, import applied in 8 ms |
| S1 | Inactivity 10 min, Tom active | PASS: back to « Classe » + « Qui es-tu ? » ≈ 10.3 min after the last input |
| S2 | Win+L with « Revenir à « Classe » quand la session est verrouillée » | PASS: « Classe » at the lock (no picker on the lock screen), « Qui es-tu ? » at the unlock |
| S3 | Logoff / logon (start with Windows) | PASS: `session: logoff -> classe`, index active `classe`; the next logon loads « Classe » and shows the picker |
| S4 | « Profils proposés sur ce poste »: Léa + Tom only | PASS: list keyed by the computer name; the picker shows only those two |
| — | Privacy over both `--log` files | 0 first names, 0 PINs |

Script-side false alarms (V9c message-box text unreadable by the managed UIA client, V9j / P1 PowerShell variables
shadowed case-insensitively) were checked by hand (screenshot, recount) and fixed in the script. Unit tests: 893/893 at first, 912/912 after the review fixes
(`ProfileArchiveTests` 21 incl. 40 profiles exported + imported in < 2 s and < 2 MB; `SessionPolicyTests` 18).
Not run: Narrator (owner check), a cancelled logoff, an import interrupted by a disk error, a real second PC.

**Review fixes re-run** (same day, `fixes` subfolder): the Espace enseignant left open on « Classe » is closed by the
10-minute idle (no switch, no picker) and by Win+L, and the PIN grace ends (Réglages, opened without PIN before the
lock, asks the PIN after it); S1 and S2 pass again; 0 names / PINs in the log. The cancelled logoff is covered by a
unit test (the session end now switches the running app, not only the store). xUnit 912/912, run twice.

## Pierre's isolated words and « il y a » — tested (0.7.2, 2026-11-14)

Owner's blind sheet of 0.6.0 ([voice-evaluation.md](voice-evaluation.md) § 9d): an isolated word of pierre is said
inside « Le mot : {0}. » and cut out (card `word_carrier`; fallback: the word alone with `inference_word`), and
espeak's `iʲ` becomes `i` in everything pierre says (card `phoneme_fix`). Siwis is unchanged. Unit tests:
`PiperCarrierTests` (format, cut, fallback reasons, cache key, P2 on the 40 golden items: only `ʲ` removed, word map
unchanged) and `PiperSynthCarrierTests` (the engine with real espeak and a fake model: carrier with the voice's scales,
cache hit, fallback with the word scales cached under the carrier key, P2 for pierre and not siwis, a word with
progress not carried). xUnit 927/927.

Test VM `predict-test-win11` (`clean` checkpoint), self-contained 0.7.2 publish, `--audio null`, driver `p072.ps1`;
screenshots and log in the `08c-pierre-words\vm` test-run folder.

| # | Scenario | Result |
|---|---|---|
| C1 | Fresh profile with Pierre, word + sentence echo; Notepad « chat maison chat Il y a un enfant. » | PASS 7/7: `piper:pierre ready`; every new word `word carrier: N ids, cut at …` (first audio 68–111 ms), the second « chat » `word cache hit, carrier` (0 ms); no fallback, no error; the sentence said whole without the carrier; Notepad text exact |
| C2 | Reading mode with Pierre: « Il y a un enfant dans la cour. Les amis arrivent ce soir. » | PASS: 13/13 progress events highlight the spoken word, event → highlight median 0.34 ms, no word-map fallback (`iʲ → i` in « il y a » and « amis‿arrivent ») |
| C3 | Cold-word first audio in the guest (bench, 12 words × 10) | carrier p50 69.2 / p95 84.7 ms; word alone 35.9 / 49.3 ms (+35 ms p95; host +34 ms) |

Not verified: anything audible (no sound device in the VM; the owner's confirmation page compares the 0.7.2 clips with
the rated ones), the device-onset question of § 9d, a cut failing on a real word (never seen on the 14 items × 5
takes; covered by the unit tests).

## Personal words, « Mes fautes », lessons — tested (plan 10A, 0.8.0, 2026-11-14)

Test VM `predict-test-win11` (`clean` checkpoint before each machine and at the end), self-contained 0.8.0 publish and the
0.7.2 one, `--audio null`, classic Notepad; driver `p10a.ps1` (results, screenshots and logs in
`<work>\test-runs\10a-mots\`, RESULTS.md).

| # | Scenario | Result |
|---|---|---|
| W1 | Upgrade 0.7.2 → 0.8.0 | PASS: no `mots.json` until an edit, 10 Notepad probes identical, the « Mots » tab empty |
| Q3 | Tray « Ajouter un mot… » with the edit PIN on | PASS: no PIN, word stored with its capital; Réglages still asks for the PIN |
| W2 | « Vésuve », « pace → parce » | PASS: « Vé » → Vésuve first, « vé » → Vésuve in the bar (8th), « pace » → parce first; `words:` lines carry no word |
| W3 | « Classe » lesson pasted; Tom hides « lapilli » | PASS: 17 ticked words (capitals kept), the text stored nowhere, both pupils get the words, Tom never sees lapilli, Léa does |
| W6 | Export with words → import into another « Classe »; 0.7.2 opens it | PASS: v2 file, Léa's effective list identical after the rebase (+ the new class's words only), 0.7.2 says « Mettez Pépite à jour » |
| W5 | « Effacer tous mes mots… » | PASS: confirmation, `-mots` backup first, Léa's words gone, « Classe » intact |
| W9 | 20 switches with 15 000 effective words | switches PASS (p95 6.5 ms, max 153 ms = the first, cold load + build); predict lines ≤ 5 ms FAIL (3 lines at 16–47 ms in 15.6 ms ticks); hook max 1.15–1.23 ms FAIL (< 1 ms gate; 0.70 ms in an earlier run) |

**Recheck of the review fixes** (final build, two VM sessions from `clean`, driver `p10r.ps1`, `<work>\test-runs\10a-mots\recheck\`):
tray « Ajouter un mot… » on « Classe » (after « Fin de session ») asks the code, a pupil does not (R1); at a sentence
start « C » / « M » show no name (« Ce, Comme… », « Mais, Me… »), « Lé » typed inside a sentence gives « Léa » first (R2);
with « Pierre » + « pierre » and 8 words the bar keeps 8 words with one « Pierre » (R4); switches with 5 000-word lists are
applied in 1–6 ms, the snapshots are built on a worker (60–147 ms there), Tom's bar never shows Léa's words and the
predictions after the switches stay ≤ 16 ms (R3; one 172 ms line on a 1-letter prefix in the first session was not
reproduced); working set with 0 / 15 000 / 0 words 283 / 267 / 285 MB (R5, see evaluation.md).

A **4.6 s UI-thread block** was seen in W9: the first next-word prediction after « la » (`PredictNext`, the cold n-gram read
of a very frequent word's successors) — also 578 ms after « je » with 0.7.2, so it predates 10A; to fix separately
(warm the frequent contexts at start, or read them off the UI thread). Not verified: Narrator, Edge, anything audible.

## Spelling indicator — tested (plan 10B, 0.8.1, 2026-11-14)

Test VM `predict-test-win11`, three sessions each from the `clean` checkpoint and restored to it afterwards (VM left off),
self-contained 0.8.1 publish, `--audio null`, classic Notepad; driver `p10b.ps1` (results, screenshots, render sheets and
logs in `<work>\test-runs\10b-spelling\`, RESULTS.md). The first session's S1 / S2 verdicts were lost to two driver bugs
(a `$s3` variable shadowing the screenshot folder, a string cut); S1 and S2 were rerun from `clean`.

| # | Scenario | Result |
|---|---|---|
| W8 / S1 | Single user, Notepad: « mais » typed; « la maison␣ », « la maizon␣ » (click, F1, F9), « xqzt␣ » (click, Oui), « la SNCB␣ », « la maison. », « l'ecole␣ » | PASS 14/14: ✓ while typing « mais »; ✓ with the 8 next words; ≈ alone (no next word after an unknown word) with « F9 » above it; click → maison, maisons, méson, mésons (green), F1 → « la maizon maison »; F9 → the same, swallowed; ? alone (`0 words + marker Unknown`); click → « Ajouter « xqzt » à mes mots ? » with the word filled in, Oui / Non, no PIN; then « xqzt » is ✓; SNCB no marker; « la maison. » → ✓ alone, bar hidden (Q10); « l'ecole » ≈; no typed word in the `spelling:` / `words:` / `bar gen` lines |
| S2 | Réglages → Mots → « Indicateur d'orthographe » off, then on | PASS 4/4: on by default; off saved in the profile (`words.spellingIndicator: false` only); no marker; back on → ≈ again |
| S3 (Q3) | Management on; Léa (« Code requis pour modifier le profil » on) clicks ? on « blorf »; « Classe » (after « Fin de session ») clicks ? on « grompf » | PASS 3/3: Léa adds without the PIN; on « Classe » the Edit PIN is asked (« … pour modifier les réglages »), then the word goes to « Classe » |
| S4 | Machine file `{"locked":{"words":{"spellingIndicator":false}}}` owned by Administrators | PASS 3/3: `locked [words.spellingIndicator]`; checkbox disabled and off with « Réglé par l'administrateur »; no marker |
| S6 | Render sheets `--render-bar --mark known|sounds|unknown`: S / M / L / XL (Segoe UI), OpenDyslexic / Lexend / Andika at M, M without pictures, M without F-keys | 27 sheets; bar heights unchanged by the marker (M 137 DIP with pictures, 67 without, 122 without F-keys) |
| Narrator | UIA name / help text of the marker | name « Orthographe : mot inconnu » (« … s'écrit peut-être autrement », « … mot connu »), help « clic : ajouter ce mot à mes mots (F9) » — what Narrator reads; Narrator itself not run (no audio device in the VM) |

**Review recheck** (fixes of the review + the owner's option B for Enter; republished 0.8.1, `Predict.Core.dll` f7b29d87…;
three more sessions from `clean`, `<work>\test-runs\10b-spelling\vm-recheck*`):

| # | Scenario | Result |
|---|---|---|
| R1 | A keys-only host (custom-drawn control, the caret read is `Unavailable`): « Le chev » + click + « al␣ », then « maizon␣ » | PASS: « al » (a fragment) no mark; « maizon » ≈ |
| R2 | « www.ecole.be␣ », « https://www.ecole.be␣ » in Notepad | PASS: never red along the way |
| R3 | F9 held down (6 key-downs, one key-up) on a « ? » | PASS: one add offer, one dialog |
| R4 | « la maizon⏎ »: ≈ alone on the new line; click; F1; next letter; « une grande⏎ » | PASS 5/5: `spelling: line end mark=SoundsLike`, the marker alone, click → maison…, F1 → « Maison » on the new line, the next letter clears it; « grande⏎ » shows nothing |
| S1 | Regression of W8 | PASS 14/14 |

**Re-review recheck** (line-end marker on a timer with the same focused control, the caret on a new line and the field
re-probed; Esc reads; `Pepite.dll` 31945790…; `<work>\test-runs\10b-spelling\vm-recheck6`, `vm-recheck7`): S1 14/14, R1–R4
as above, and R5 Notepad « Enregistrer sous » + « zwpkfile⏎ »: no marker in 15 samples over 3 s (`line end cancelled (caret
not on a new line)`: the box stayed open 2 s while saving — a first attempt without the caret test showed a ? there);
R6 Notepad's Find box + « zwpk⏎ »: nothing (`caret not on a new line`); R7 « la maizon⏎ »: ≈ alone with its top-left at the
new line's caret (bar x 168 = caret x, bar y 276 just under the caret at 252; line 1 at 234); R8 Esc then « maizon␣ » → ≈
(the Esc read seeds the tracker). All PASS.

**Final re-review recheck** (`Pepite.dll` 6f32cac3…, `vm-recheck8`): R9 « la mai » + Esc → the bar hides and stays hidden
2 s (the Esc read seeds `prefix='mai' ctx=[la]` without an update), the next letter brings it back with `typed='mais'
ctx=[la]`; « la mais␣ » + Esc stays hidden; R4 (5/5) and R5 (Save As: no marker, `line end cancelled`) unchanged. All PASS.

The driver now presses End after its Esc (Esc resets the tracker without a read: the first word typed is, since the review,
a fragment of unknown start — a driver-side change only) and pays the cold « la » n-gram read at setup (3.2 s in the VM,
the older TODO item).

**Timing** (UI thread, `mark=… (ms)` of the prediction lines, 30 marks): mean 0.26 ms, max 3.5 ms (the first check after
the start, cold); later checks 0.00–0.3 ms. The marker-only bar's build + show 1.7 ms (window re-shown), the sound-alike bar
0.6 ms. The hook summary lines (every 200 keys) were not reached in these short sessions. Not verified: Narrator speech,
Edge, Word, the owner's classroom judgement of the colours.

## Proper names — tested (plan 10C, 0.8.2, 2026-11-14)

Test VM `predict-test-win11`, two sessions from the `clean` checkpoint (restored after, VM off), self-contained 0.8.2
publish, `--audio null`, classic Notepad; driver `p10c.ps1`; results, screenshots and logs in
`<work>\test-runs\10c-names\` (RESULTS.md).

| # | Scenario | Result |
|---|---|---|
| W10 / N1 | « Emma », « Inès » inside a sentence and at its start; « Maizon » at a start; « en Belg », « en belg », « à Ypr », « à Iep », « Ypres », « Ieper », « en Wall »; « je vois Ca » vs « ca »; « C » at a sentence start | ✓ ×4; ≈; Belgique first with its flag (lowercase: the same, no « belgique » duplicate); Ypres; Ieper then Ypres; ✓ ×2; Wallonie + Région wallonne with the flag; « Ca » → Casey first (log; the driver's own check misread the log: a case-insensitive match), « ca » → common words first; « C » → Ce, Comme, Cette… no name |
| W10 / N2 | Teacher imports a folder database (4 names, 3 drawings, licence CC0) into « Classe » | dialog with « 4 noms, 3 images », the licence and the Q17 rule; list `noms` + 3 thumbnails; Léa: « Zéb » → Zébulon with its drawing, « Ypr » → Ypres with the teacher's drawing (before the pack), « Zébulon » ✓ |
| W10 / N3 | « Images pour les noms propres » off, then on | on by default; off saved in the profile; no flag, no drawing; on → flag again |
| N4 | Reading view « Hier, Pierre a vu une pierre en Belgique. » | « Pierre » no picture, « pierre » the stone, « Belgique » the flag (2 name tiles, 1 with its own picture) |
| Start | Pack load | 31 297 names (first build; 31 299 after the 2026-11-15 rebuild), 32 110 forms, 436 ms on a worker, one compacting GC 60 ms; working set +12.2 MB in one session, not reproducible in the second (the guest trims working sets) |

**Recheck 2026-11-15 (session 3, after the review and the owner decisions D1–D3; streaming load, capital bonus):**

| # | Scenario | Result |
|---|---|---|
| W10 | N1–N4 again | unchanged; « je vois Ca » → Cali, Caire, Canton… (log) vs « ca » → car, cas… |
| D1 | « avec Ad » / « Ado » / « de Hit » / « Hi », « hit » / « Hitler » | Adam in the bar, no Adolf Hitler; Ado: Adolphe, Adolf… no Adolf Hitler; Hit → **Hitler first**, no picture; no Hitler for « Hi » or lowercase « hit »; « Hitler » ✓ |
| D2 | « Pome » at a sentence start / inside a sentence | ≈ / ✓ |
| D3 | « Rose est belle. » reading view; then « Rose » in the class list | no name tile (the flower); then 1 name tile |
| Ranking | « avec Jo », « d'Em » | Joseph 6th (after Johannesbourg, José, Jordanie, Johan, Jozef — owner question); Emma 1st |
| Session 4 (first names first, final 0.8.2) | « avec Jo », « je vois Ca », « avec Ad », « en Belg », « à Ypr », « de Hit » | **Joseph first**; **Camille first**, no Cali (lowercase « ca » → car, cas…); Adam first; Belgrade, Belgique with its flag; Ypres; Hitler first, no picture (log — the driver read too early after a 2 s cold lookup); all other checks unchanged |
| Import | 2001 names, 201 pictures (one 3600×2400) into Léa | « Import en cours… » shown, the window answered during 2.6 s; 201 thumbnails (≤ 7.6 KB); the large picture shows above « Grandimage » |

Not verified: Narrator, a CSV import and the `.pepite` v3 export in the UI (unit-tested), Word / Edge.

## 0.8.3 fixes — tested (VM predict-test-win11, 2026-11-15)

One run (`<work>\test-runs\083-fixes\`: `RESULTS.md`, `run.txt`, screenshots and the app log in `vm\`), Notepad and the
10B keys-only control, `--audio null`. PASS: echo on in a fresh profile (`speak Word 'chat'`, `speak Sentence 'le chat
dort'`) and the three boxes ticked in Réglages; « si je fè » → fais 1st; « ma mam␣ » ≈ (not ✓), no ✓ while typing
« mam »; « le mmmmal␣ » ≈ → click → F1 → « le mal␣ » (`correction: read 0 ms, the document agrees, replaced`,
`speak Inserted 'mal'`); « Maizon␣ » at a sentence start → « Maison␣ »; a jump (End) after the list, then F1 → nothing
deleted; « la maizon⏎ » ≈ alone on the new line → F1 → the word replaced on the previous line, the line break kept and the
caret on the new line; keys-only « le mmmmal␣ » → « le mal␣ » (`correction: keys only, replaced`); « il a dit : Mer » →
Merci, Mercredi, Merde, Mer… (no name); « né en Bel » → Belgique 1st; « d'Hél » → Hélène 3rd. Found and fixed after the
run, not re-run: the line-end list was capitalised for the new line (« la Maison⏎ »); a second click during the
correction read could insert the word again. Not verified in the VM: « le livre Le » (the scenario aborted on a
screenshot error; host probe: 4 names of 8) and « Classe » speaking the echo (the driver could not pick « Classe » in the
picker; unit tests cover the defaults and the layering).

Recheck after the review and the owner decisions (second run, `083-fixes\RESULTS.md` § Recheck): PASS — echo in Notepad;
« la maizon⏎ » → « la maison⏎ » (lowercase); after Pause / Reprendre the correction still reads first; keys-only control:
replaced after one space (`no text to read, keys only`), inserted after « . »; a field the probe cannot classify stays
silent (`echo dropped (field not proven)`); « Classe » speaks the typed word and the sentence. Found and fixed, not re-run:
a double click on a tile inserted the next bar's first word (now ignored within the double-click time); « le livre Le »
showed 6 names of 8 (now at most half of any first p words). Not verified: typing in a cmd / PowerShell window (the
harness found no window to type in; the log shows the terminal detection while it was in front).

## Grammatical prediction — tested (plan 11, 0.9.0, VM predict-test-win11, 2026-11-15)

One run (`<work>\test-runs\10d1-grammar\`: `RESULTS.md`, `run.txt`, screenshots and the app log in `vm\`), Notepad and
Edge, `--audio null`, shipped weights (no bonus: only forms that disagree are pushed down, agreeing forms are added).
PASS: « les mais » → `mais, maisons…` (maison not shown); « ils mang » → mangent 1st; « elle est all » → allée 1st;
« il a mang » → mangé before manger; « pour mang » → manger 1st; « les petites mais » → maisons 1st; « il et » → est in the
bar, its F-key gives « il est »; « ils son » → sont 1st; « il a manger » → mangé 1st with the « sounds like » colour (UIA help
text); « les journal » → journaux 2nd (correction colour); must not fire: « je les mang » → mange 1st, « il nous voi » →
voit 1st, « mange-t-il une » → une 1st, « le mais » still offers maison; next word after « les / des / mes »: no singular
above its plural; Edge textarea « ils mang » → mangent 1st. Without `grammar.sqlite`: `grammar: absent` and the six W-G1
bars and the next word after « les » identical to the 0.8.3 build run in the same VM. Resources: working set with / without
the file within the run-to-run spread (medians 300.0 / 304.5 MB); `grammar:` lines carry counts and times only (load 4.5 s
on the first, cold start, then 0.3–1.0 s, on a worker thread); no new log line carries text; 0 `slow UI` lines. Not
verified: Word (not installed in the VM).

Recheck after the review (`10d1-grammar\RESULTS.md` § Recheck, `vm-recheck\`): PASS — « mon vélo neuf est » → est first
(no « étais » / « êtres »); next word after « c' » without « ést » / « etait »; « le chien et le chat son » → sont first;
the W-G1 bars unchanged, « il et » → F3 « il est », « ils son » → sont. Recorded (no rule fires, the language model's
order): « le maître nous a » → `avons, a, …`, « celui qui nous a » → `a, attend, …`, « la maîtresse vous a » → `avez,
allez, a, …`.

## Known limitations / next steps

- **D22 in Word**: after a read-back fix the first Ctrl+Z undoes our narrow space; overtype mode is not handled;
  Outlook (Word editor, process `OUTLOOK`) is not covered and would get a double space before `; : ! ?` — with
  LibreOffice Writer, a future per-app rule. See [keyboard-design.md §5b](../design/keyboard-design.md).
- **KI-1:** no visible feedback at start-up and no single-instance guard — a second start looks like 'nothing
  happens'; see [known-issues.md](known-issues.md).

- **Reads only after caret jumps.** While typing, keys stay the source, so app-side autocorrect without a
  jump can still drift. Apps without a readable text (RichEdit/WordPad, Java, Electron, MSAA-only) keep
  the keystroke-only behaviour. An `Edit` with > 64 K characters cannot report its caret → keys.
- A password field focused **before** any jump (autofocus) is detected at the first character by the field
  probe (iteration 3); a page moving the focus into a password field *while* you type, with no jump, is not.
- Chromium quirks handled: in-page elements report process id 0; a field's text range runs on into the
  rest of the page and is clamped to the field.
- **UIPI:** runs as the normal user (`asInvoker`), so it cannot type into elevated (administrator) windows.
- Pictures cover ≈ 53 % of running text (measured on the subtitle held-out set, 2026-11-08; the most frequent
  function words are left empty on purpose);
  the bar is ≈ 137 DIP tall with pictures at M (smaller sizes and the docked positions help).
- Speech uses the SAPI 5 voices Windows exposes (Hortense fr-FR in the sandbox; Julie/Paul are OneCore-only).
  The word/sentence echo is built from the keys; after a mid-sentence jump the sentence end says the whole sentence
  read from the document (D17 amendment, 2026-11-14), or only the words typed since when that read fails. No colour setting; adult PIN lock designed, not shipped (iteration 6).
- Code signing still to do: global keyboard hooks + input injection look suspicious to antivirus software
  unless the program is signed.
