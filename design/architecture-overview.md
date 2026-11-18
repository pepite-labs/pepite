# Architecture overview — the tools of this repository as built (v1, 2026-11)

This is the **as-built** map of everything under `src/`: what each tool is, how they connect, and
where the design deviates from the early forward-looking design (a C++ core with KenLM and an optional
neural reranker, § 4). Each component has its own design note:

| Component | Kind | Design note |
|---|---|---|
| `Data.Build` | Python build scripts (offline, run once) | [data-pipeline-design.md](data-pipeline-design.md) |
| `Predict.Core` | C# library — the predictor | [predict-core-design.md](predict-core-design.md) |
| `Predict.Keyboard` | C# WPF desktop app — prediction in any Windows app | [keyboard-design.md](keyboard-design.md) |
| `Predict.Eval` | C# console — quality benchmark + weight tuning | [benchmark-design.md](benchmark-design.md) |
| `Predict.Cli` | C# console — try words, micro-benchmark (`--bench`, `--cold`) | (trivial; see its `Program.cs`) |

## 1. Big picture

```
 BUILD TIME (developer machine, once)                         RUN TIME (user's PC, offline)
 ─────────────────────────────────────                        ──────────────────────────────────────────
 Lexique 3.83 (TSV, CC BY-SA 4.0) ──► build_db.py ──┐
                                                    ├──► prediction.sqlite ──► Predict.Core ◄── Predict.Keyboard (end user)
 Leipzig news ─┐  corpus/prepare_corpus.py          │     (≈150 MB, read-only)  (PredictionEngine) ◄── Predict.Eval (developer)
 Tatoeba fra   ├─► build_ngrams.py (bi/tri) ────────┤                                         ◄── Predict.Cli  (developer)
 Vikidia       │   build_4grams.py (4-grams) ───────┘
 PD children's ┘
 books (Gutenberg)
 French.lex (Grammalecte, MPL-2.0) ──► build_spelling_ref.py / build_grammar.py ──► spelling.sqlite, grammar.sqlite
```

- **One data file, one library, several front-ends.** All intelligence lives in `Predict.Core` +
  `prediction.sqlite`; the front-ends never touch the DB directly.
- **Offline by construction.** No network code anywhere at run time; the DB is opened `Mode=ReadOnly`.
- **Written from scratch; data only from openly licensed sources** (Lexique 3.83, Tatoeba, Leipzig, Vikidia,
  Project Gutenberg PD, Grammalecte, ARASAAC, Insee/Statbel/Wikidata/CLDR); no third-party proprietary code or
  data.

## 2. Dependency graph

```
Predict.Keyboard (net10.0-windows, WPF + WinForms tray, System.Speech 10.0.15, UIAutomationClient)
        │
        ▼
Predict.Core (net10.0) ──► Microsoft.Data.Sqlite 10.0.15 ──► SQLite (bundled e_sqlite3)
        ▲          ▲
Predict.Eval    Predict.Cli   (net10.0 consoles)
```

`Predict.Core` has a single NuGet dependency and no Windows-specific code, so it is portable (Linux/macOS/ARM64)
as is. Everything Windows-specific (hooks, `SendInput`, UIA, SAPI) is confined to `Predict.Keyboard`.

## 3. Run-time data flow (one keystroke)

```
 key press ─► WH_KEYBOARD_LL (hook thread) ─► ToUnicodeEx ─► WordTracker {current word, ≤3 previous words}
                                                                  │ post (coalesced)
                                                                  ▼
                                   UI thread: PredictionEngine.Predict(typed, context, 8)   ~0.3 ms warm
                                              or PredictNext(context, 8) after a space
                                                                  │
                                                                  ▼
                                   SuggestionWindow at the caret (GetGUIThreadInfo → UIA → mouse)
                                                                  │ F1–F8 / click
                                                                  ▼
                                   Inserter: N×Backspace + word as Unicode SendInput (marked, ignored by hook)
```

**Re-read of edits (D23, plan 06).** Every caret read (after a click, arrow, focus change…) also starts an edit
session: a mirror of the text window that the same keys and insertions edit. When the caret leaves, the next read
says where it landed — same word (nothing), same sentence (the modified word is spoken), elsewhere or another window
(the whole modified sentence) — and the text goes to the speech thread as a `Reread` item. Pure classes
`EditSession` / `EditClassifier`; tokenising happens on the reader thread, never in the hook
([keyboard-design.md §5c](keyboard-design.md)).

**Reading the selected text (D21/D24, plan 07).** When a caret read reports a selection, a `SelectionReader` thread reads
the selected text (Win32 Edit or UIA; optional simulated Ctrl+C, off by default, clipboard restored), and the bar switches
to a reading view: word + picture tiles (≤ 1000 words), Play / Pause (also Space / F9, swallowed only then), previous /
next sentence, a resizable « grande fenêtre ». Each sentence is one SAPI utterance; its `SpeakProgress` events, marshalled
to the UI thread with the utterance id, highlight the current word in our bar only. Pure parts: `ReadingDocument`,
`ReadingController`, `SelectionPolicy` / `ClipboardCopy`; UI: `ReadingMode`, `ReadingView`, `ReadingWindow`
([keyboard-design.md §5d](keyboard-design.md)). Typing, Esc or the end of the selection go back to predictions.

## 4. As built vs. planned

The design docs proposed a C++ core with KenLM and optional ONNX. v1 deliberately took a simpler route,
because measurements showed it was already fast enough:

| Topic | Planned (early design) | Built (v1) | Why |
|---|---|---|---|
| Core language | C++ DLL behind a C ABI | **C#** library | lookups are light (0.26 ms/prediction); one language, simpler build |
| Context model | KenLM (Kneser-Ney) | **stupid backoff**, 4-gram → unigram, in SQLite | KN skipped: small expected ranking gain at this data size vs. complexity; SQLite keeps RAM flat (see [evaluation.md](../development/evaluation.md)) |
| G2P | espeak-ng or rules | **rules** (`PhoneticKey.Fold`) + Lexique's own `phon` column | permissive licence, no GPL dependency |
| UI | WinUI 3 or WPF | **WPF** (.NET 6, .NET 10 since 0.10.2) | available with the installed SDK, mature `WS_EX_NOACTIVATE` story |
| Caret / text | UIA TextPattern | Win32 caret first, **UIA** fallback; word tracked from keys | UIA word *reading* is next step ([keyboard.md](../development/keyboard.md)) |
| TTS | SAPI 5 | **SAPI 5** (`System.Speech`), word progress (`SpeakProgress`) for the reading highlight | as planned; neural voices behind the same `ISynth` next (plan 08) |
| Neural reranker | optional ONNX | not built | not needed: the benchmark targets are met without it |

The C ABI boundary stays the plan for a future native/neural tier; nothing in v1 prevents it
(`PredictionEngine`'s public surface is three methods — see [predict-core-design.md](predict-core-design.md)).

## 5. Cross-cutting decisions

| Concern | Decision |
|---|---|
| **Resource budget** | ≈ 240 MB disk + memory, voices excluded. Measured: 163 MB disk, ~107 MB RAM, 0.26 ms warm prediction. |
| **Licensing** | Code under **GPL-3.0-or-later** (D19): the Piper voices link espeak-ng (GPL-3) in-process, so the whole program is GPL-3; every dependency must be GPL-3-compatible (MIT, BSD, Apache-2.0, LGPL, MPL-2.0). `prediction.sqlite` is *generated* (Lexique CC BY-SA 4.0 + n-gram statistics); neither it nor the corpora are committed (`.gitignore`). |
| **Pictures** | `pictos.sqlite` (built by `Data.Build/build_pictos.py` from ARASAAC, ≈ 20 MB, 5 k pictures) is a **separate file** shipped beside the exe with `ARASAAC-LICENCE.txt`: the resized pictures are a CC BY-NC-SA 4.0 derivative; keeping them out of the code and out of `prediction.sqlite` keeps that licence from touching either. The app works without it (no picture slots). Never committed; raw downloads stay in `<work>`. |
| **Fonts** | OpenDyslexic, Lexend and Andika (SIL OFL 1.1, Reserved Font Names) are shipped **unmodified** as loose files in `fonts\<Family>\` beside the exe, each with its `OFL.txt` (+ FONTLOG/README) and a generated `SOURCE.txt`; fetched by `Data.Build/fetch_fonts.py` from pinned URLs with SHA-256 checks, never committed. They are loaded privately by the app (nothing installed into Windows) and credited in À propos; the OFL covers only the font files, not the code. Missing folder → Segoe UI. |
| **Privacy** | Nothing leaves the PC. The only thing ever written is the optional `--log` debug file (it contains typed and selected words — debug only). Password fields are never read (caret, selection, Ctrl+C fallback). |
| **Threading** | Engine is single-threaded by contract (UI thread in the keyboard app); hooks never call it. |
| **Quality gate** | Every model change is measured with `Predict.Eval` (curated + held-out KSS) before it becomes a default. |
| **Packaging** | Self-contained `dotnet publish -r win-x64` (no .NET install needed), packed by an Inno Setup 7.1.1 installer (plan 12, `installer\`: per-user or all-users, start with Windows, `.pepite` association, school machine file; reproducible `files.sha256`). Signing: before the public release (SignPath, Q4). |

## 6. Repository layout

```
(repository root)
├─ design/       this file + one design note per component + the product decisions
├─ development/  BUILD.md, evaluation.md (scores), keyboard.md (user/dev guide)
└─ src/
   ├─ Data.Build/        build_db.py, build_ngrams.py, build_4grams.py   (out/ is git-ignored)
   ├─ Predict.Core/      PhoneticKey.cs, PredictionEngine.cs
   ├─ Predict.Cli/       Program.cs
   ├─ Predict.Eval/      Program.cs, cases.json
   └─ Predict.Keyboard/  App, KeyboardController, WordTracker, SuggestionWindow, Caret, Inserter, Speaker, Native,
                         CaretTextReader, EditSession, SelectionReader, ReadingDocument / ReadingController / ReadingMode …
```

## References

- Lexique 3.83 — New, Pallier et al., <http://www.lexique.org> (CC BY-SA 4.0).
- ARASAAC pictograms — Sergio Palao, Gobierno de Aragón, <https://arasaac.org> (CC BY-NC-SA 4.0); index
  `api.arasaac.org/v1/pictograms/all/fr` and `static.arasaac.org/pictograms/{id}/{id}_300.png`, 2026-11-08.
- Brants et al., *Large Language Models in Machine Translation* (stupid backoff), EMNLP-CoNLL 2007.
- Kernighan, Church, Gale, *A Spelling Correction Program Based on a Noisy Channel Model*, COLING 1990 — the
  noisy-channel view behind the phonetic and edit candidates.
- Brill, Moore, *An Improved Error Model for Noisy Channel Spelling Correction*, ACL 2000 (P00-1037) — substring
  edits, the idea behind the transposition / sound-alike penalties.
- Jurafsky & Martin, *Speech and Language Processing*, 3rd ed. draft, Appendix D (noisy-channel spelling),
  <https://web.stanford.edu/~jurafsky/slp3/D.pdf>.
- Microsoft Learn: *LowLevelKeyboardProc*, *SendInput*, *GetGUIThreadInfo*, *UI Automation TextPattern* (Win32 / .NET docs, 2024–2026, read for .NET 6; the Win32 calls do not depend on the .NET version).
- Microsoft.Data.Sqlite 10.0.15; .NET SDK 10.0.115 (runtime 10.0.15; .NET 6.0 SDK 6.0.424 until 0.10.1).
