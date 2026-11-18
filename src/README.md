# `src/` — source

| Project | Lang | Role |
|---|---|---|
| **Data.Build/** | Python | `build_db.py`: Lexique 3.83 → `prediction.sqlite` (lexicon). `build_ngrams.py`: open corpora → bigram + trigram tables in the same DB. `build_pictos.py`: ARASAAC → `pictos.sqlite` (word → pictogram, 128 px PNGs; Pillow at build time) + `ARASAAC-LICENCE.txt`; `picto_overrides.tsv` = reviewed curation; `make_icon.py`: the app icon (own drawing). `fetch_fonts.py`: pinned, hash-checked download of the bundled OFL fonts (OpenDyslexic, Lexend, Andika) → `out/fonts/`, files unmodified. Tests: `test_build_pictos.py`. |
| **Predict.Core/** | C# (net10.0) | The **first library**. `PhoneticKey.Fold` (French phonetic hash) + `PredictionEngine` (prefix + phonetic candidate generation, frequency ranking). Reusable, no UI. |
| **Predict.Cli/** | C# | Minimal console to try the engine on words. |
| **Predict.Keyboard/** | C# (WPF, net10.0-windows) | The **on-screen predictor**: system-wide keyboard hook, suggestion bar at the caret with an ARASAAC picture above each word, F1–F8 / click to insert into any app, right-click to hear; speech echo; settings window (words, sizes, positions incl. docked/free, fonts + letter spacing, speech) with a read-only machine layer; tray icon (Pause/Reprendre · Réglages · À propos · Quitter). See [../development/keyboard.md](../development/keyboard.md). |
| **Predict.Eval/** | C# | Quality benchmark: scores the engine on curated cases (`cases.json`) and on held-out text (keystroke savings). |

Build/run: see [../development/BUILD.md](../development/BUILD.md). Design: see
[../design/](../design/).

## `Predict.Core` at a glance

- `PhoneticKey.Fold(word)` → a canonical "sound" key so a word and its phonetic misspelling collide
  (`horizon`/`orizon` → `orizO`, `maison`/`maizon` → `mezO`). Symmetric — applied to the lexicon and to the
  typed text alike; prefix-compatible for mid-word prediction. French rules only (silent `h`, `s→z` between
  vowels, nasals, `ph→f`, `ch`, `qu→k`, vowel digraphs…). No external G2P.
- `PhoneticKey.FromLexiquePhon(sampa)` → a second key built from Lexique's real pronunciation, matched
  against the grapheme fold of the typed text (catches irregulars: `femme` = /fam/).
- `PredictionEngine.Load(dbPath)` keeps the word list in RAM (indexed by spelling and by both phonetic
  keys) and queries the bigram/trigram/4-gram tables **on disk** (top successors only, cached per context —
  RAM stays flat as the corpus grows). `Predict(typed, context?, k)` returns top-k
  `Suggestion(Word, Score, Kind)`: candidates from literal prefix + phonetic prefix + Damerau edit-1, ranked
  by a stupid-backoff LM `P(word | up to three previous words)` + a frequency-gated literal-prefix bonus −
  an edit penalty. `PredictNext(context, k)` predicts the next word. Measured: ~0.26 ms/prediction,
  ~107 MB RAM, 163 MB DB.

Design notes per project: [../design/README.md](../design/README.md).

