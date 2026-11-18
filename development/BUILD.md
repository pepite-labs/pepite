# Build & run

## Prerequisites

- **.NET SDK** — exactly the version pinned in [`global.json`](../global.json) (10.0.115, `rollForward: disable`: the
  SDK patch fixes the bundled runtime packs of a self-contained publish, 10.0.15 here, which THIRD-PARTY-NOTICES.txt
  names; the CI installs the same SDK and checks `dotnet --version`, so a local rebuild matches the CI one).
  The projects target `net10.0` / `net10.0-windows` (.NET 10 LTS, since 0.10.2). A portable SDK unpacked in a folder
  works too: call its `dotnet.exe` by path, or put that folder first in `PATH` for the session (`build_installer.ps1`
  calls `dotnet` from `PATH`). No `DOTNET_ROLL_FORWARD` is needed any more. The framework-dependent exes it builds
  (`Predict.Cli.exe`, `Predict.Eval.exe`, the `Pepite.exe` of a dev build that `render_help_images.ps1` starts) do not
  search `PATH` for the runtime: with a portable SDK also set `DOTNET_ROOT` to that folder for the session, or run
  `dotnet <the .dll>` instead (`dotnet run` works as is).
  Control-flow Enforcement Technology (CET shadow stacks): .NET 10 marks the app CET-compatible by default and we keep
  that default. Fallback if a third-party DLL loaded in the process (a voice engine, an injected hook) crashes on a
  CET-enabled CPU: `<CETCompat>false</CETCompat>` in `Predict.Keyboard.csproj`, then rebuild.
- **Python 3.12** (standard library only for the build and test scripts — uses `sqlite3`); run scripts with `python -I`.
- **Lexique 3.83** — only to rebuild the word base (maintainers, § 1): `Lexique383.tsv` from <http://www.lexique.org>
  (CC BY-SA 4.0). Not committed.
- NuGet access on first build (restores `Microsoft.Data.Sqlite`).

## 0. Get the prebuilt data (default)

The word base and the other data are **provided already built** (owner decision 2026-11-16): a normal build — a
developer, the GitHub Actions « app » workflow — does not rebuild them. One command downloads the data release
(`src\Data.Build\data-release.lock.json`: GitHub Release `data-2026.11.2` of `pepite-labs/pepite`), checks every asset's
SHA-256 and size, unpacks it into `src\Data.Build\out\` and checks the word base with `verify_data_pack.py`:

```
python -I src\Data.Build\fetch_data_release.py --cache <work>\data-cache
```

| Asset | Unpacked into `out\` | Licence |
|---|---|---|
| `pepite-data-2026.11.1.zip` | `prediction.sqlite`, `prediction-SOURCES.txt`, `spelling.sqlite` + `spelling\`, `grammar.sqlite` + `grammar\` | CC BY-SA 4.0 + MPL-2.0 |
| `pictos-arasaac-2026-11-08.zip` | `pictos.sqlite`, `ARASAAC-LICENCE.txt` | CC BY-NC-SA 4.0 (separate component) |
| `noms-2026.2.zip` | `noms-pack\noms\` | CC BY 4.0 |
| `fonts-2026.11.zip` | `fonts\` | OFL 1.1 |
| `espeak-ng-win-x64-724808c5.zip` | `espeak\` | GPL-3.0-or-later |
| `espeak-ng-<commit>.tar.gz` | kept as a file (the source offer; `build_installer.ps1 -EspeakSourceArchive`) | GPL-3.0-or-later |
| `voice-siwis-cf910fb5.zip`, `voice-pierre-cf910fb5.zip` | `voices\siwis\`, `voices\pierre\` | CC BY 4.0 / CC BY-SA 4.0 |

`--from <dir>` takes local copies of the assets instead of downloading; `--list` prints the plan; `--only <asset>`
fetches one asset; an existing file in `out\` is never overwritten without `--replace`. Only the Visual C++ runtime is
not in the data release: `stage_vcruntime.ps1` copies it from the build machine (§ 2). A maintainer makes a new data
release with `package_data_release.py` (§ 1i).

Pack `pepite-data` 2026.11.1: `prediction.sqlite` (CC BY-SA 4.0 data, 149.3 MB, SHA-256 `1ea686f1…2992`) with its
`prediction-SOURCES.txt`; `spelling.sqlite` (MPL-2.0, 140 KB, SHA-256 `0633b963…acda9`) and `grammar.sqlite` (MPL-2.0),
each with its notices. 2026.11.1 only moved the spelling tables out of `prediction.sqlite` (pack 2026.11,
`f53f1ab6…e254`, held them) into their own MPL-2.0 file: same words and n-grams, same results. The « Noms propres » pack, the pictures,
fonts, espeak-ng and voices keep their own fetch / build scripts (§ 1c–1h).

## 1. Maintainers: rebuilding the word base (optional)

Only to make a new data pack (the future manual « data » workflow): § 1–1b3 rebuild `prediction.sqlite` and
`grammar.sqlite` from the pinned inputs; then update `data-pack.lock.json` with the new SHA-256s.

### 1a. Build the lexicon DB

```
python -I src/Data.Build/build_db.py /path/to/Lexique383.tsv src/Data.Build/out/prediction.sqlite
```

Produces `prediction.sqlite` (~8 MB, ~125 k French forms): table `words(ortho, lemma, cgram, phon, freq)`.
The runtime library computes the phonetic key itself, so this step is just a clean import.

### 1b. Add the context (bigram + trigram) model

Download one or more open French corpora and train n-grams into the same DB. Good choices:
**Tatoeba** `fra_sentences.tsv` (CC-BY, conversational) from
<https://downloads.tatoeba.org/exports/per_language/fra/>, and **Leipzig** `fra_news_2023_1M-sentences.txt`
from <https://wortschatz.uni-leipzig.de/en/download/French> (CC BY; the provider states no version). The pinned
inputs and the corpus experiment are in § 1b1.

```
python -I src/Data.Build/build_ngrams.py src/Data.Build/out/prediction.sqlite         /path/to/fra_sentences.tsv /path/to/fra_news_2023_1M-sentences.txt --min2 3 --min3 3
```

Adds tables `bigrams(prev, word, c)` and `trigrams(p2, p1, word, c)`. Optionally also add **4-grams**
(3 words of context) with a second pass:

```
python -I src/Data.Build/build_4grams.py src/Data.Build/out/prediction.sqlite <corpus...> --min 4
```

Adds `fourgrams(p3, p2, p1, word, c)`. The engine then ranks candidates by `P(word | up to three previous
words)` (4-gram → trigram → bigram → unigram stupid-backoff); without any n-grams it falls back to frequency.
The n-gram *counts* are statistics derived from the corpora — the corpora themselves are not redistributed.
With the written corpora (≈ 39.5 M tokens: Leipzig news 2023, Tatoeba, Vikidia, public-domain children's books; § 1b1)
the DB is ≈ 150 MB (within the resource budget). 4-grams help most when the text resembles the training corpus (see evaluation.md).
Both builders fold « œ » → « oe » and « æ » → « ae » in each token (Lexique spells « coeur »), as the engine does with
the context words.

### 1b1. Corpus build: pinned inputs, held-out sets, samples (plan corpus-light)

Every corpus input is pinned in `src\Data.Build\data.lock.json` (URL, version, SHA-256, size, licence, credit, use,
public). The bulk files live in a work folder outside the repository, named by `PEPITE_CORPUS_WORK` (no default).
Network jobs make one request every 2 s; jobs on the work disk run one at a time.

```
set PEPITE_CORPUS_WORK=<work>\corpus-work
python -I src\Data.Build\corpus\fetch_corpus.py adopt <id> <local file> [--archive]   # an input already on disk (recorded in <work>\local-paths.json)
python -I src\Data.Build\corpus\fetch_corpus.py fetch            # missing pinned files (resumable, never overwrites, stops on a mismatch)
python -I src\Data.Build\corpus\fetch_corpus.py pin-new          # first pin of a reviewed input (Gutendex snapshot, books)
python -I src\Data.Build\corpus\fetch_corpus.py books            # Gutendex snapshot -> corpus\pd-books.tsv + book entries in the lock
python -I src\Data.Build\corpus\fetch_corpus.py verify           # re-hash everything; exit 1 on a mismatch or a gap
python -m venv <work>\venv-corpus
<work>\venv-corpus\Scripts\python -m pip install --require-hashes --only-binary :all: -r src\Data.Build\requirements-corpus.txt
<work>\venv-corpus\Scripts\python -I src\Data.Build\corpus\prepare_corpus.py extract   # Vikidia <p> text + Gutenberg books
python -I src\Data.Build\corpus\prepare_corpus.py split          # Vikidia hash bucket, books by role (stops on a held-out id in training)
python -I src\Data.Build\corpus\prepare_corpus.py heldout        # E1, E2, E3, W, S-unseen + manifest.json (seen sentences dropped)
python -I src\Data.Build\corpus\prepare_corpus.py sample         # A1 / D1 samples of the same token mass
python -I src\Data.Build\corpus\prepare_corpus.py full           # Phase 2: full D input list (full\D.list), books capped at 20 %
python -I src\Data.Build\corpus\db_checks.py <db> --json <out>   # rows, OCR-noise mass, passé-simple mass
python -I src\Data.Build\corpus\compare_runs.py <work>\corpus-light\runs   # paired Δ KSS, bootstrap, verdict -> results.md
python -I src\Data.Build\corpus\write_sources.py --out <dir>\prediction-SOURCES.txt   # credits of a release DB, from the lock
python -I -m unittest discover -s src\Data.Build\corpus\tests
```

`pd-books.tsv` keeps a Gutendex « children » book only when every author and every translator died in 1955 or earlier,
and drops a translation without a named translator, then (owner review) adult fiction, textbooks / manuals / word lists
and pre-1835 spellings (role `excluded-content`, reason column); its `role` column holds the seeded held-out picks (E1) and the
training lists. `libzim` (GPL-3.0-or-later) is a build-time tool only and never ships. Outputs, held-out sets and
manifests stay in `<work>` and are never committed. Design: [data-pipeline-design.md](../design/data-pipeline-design.md)
§ 5 (plan corpus-light).

### 1b2. Build the spelling reference (`spelling.sqlite`, 0.8.3, D12)

After 1 and 1b (it reads the `words` table of the finished database):

```
set PREDICT_GRAMMALECTE_RAW=%PREDICT_BUILD_DIR%\grammalecte-raw
python -I src\Data.Build\build_spelling_ref.py fetch                 # French.lex at the pinned commit, SHA-256 checked (20 MB)
python -I src\Data.Build\build_spelling_ref.py build                 # reads out\prediction.sqlite -> out\spelling.sqlite + out\spelling\
python -I -m unittest discover -s src\Data.Build -p test_build_spelling_ref.py
```

`fetch --from <French.lex>` copies a local copy instead (same SHA-256 required). The pinned file is Grammalecte's
« Lexique simplifié » v7.7 (MPL-2.0), commit `08511c2…` of the GitHub mirror Pofilo/grammalecte (Grammalecte 2.3.0). The
build writes `spelling_unlisted(ortho)` — the letters-only lexicon forms French.lex lacks, also after « oe » → « œ »
(3 836 of 125 348: « yeah », « team », « mam », « till ») — and `spelling_source` (source, SHA-256, counts) into
`spelling.sqlite` (reproducible with the same Python / SQLite versions, recorded in its SOURCE.txt), with `spelling\SOURCE.txt` and `spelling\MPL-2.0.txt`: the table is MPL-2.0 data
derived from French.lex, so it is kept out of the CC BY-SA `prediction.sqlite` (publication cleanup, 2026-11-16; a
database that still holds the old tables loses them here and is VACUUMed). The engine reads `spelling.sqlite` next to the
database, else the old tables of the database. Without either, every lexicon form is ✓ as in 0.8.2. Prediction does not read it. The plan 10D morphology table reads the same
pinned file (`read_lex`).

### 1b3. Build the grammar morphology table (`grammar.sqlite`, plan 11 / 10D-0, optional)

After 1b2 (it reads the `words` table and the same pinned French.lex; pins in `src\Data.Build\grammar.lock.json`):

```
set PREDICT_GRAMMALECTE_RAW=%PREDICT_BUILD_DIR%\grammalecte-raw
python -I src\Data.Build\build_grammar.py fetch                     # the 4 pinned files, SHA-256 checked
python -I src\Data.Build\build_grammar.py check-official            # optional (O1): compares with grammalecte.net, never blocks
python -I src\Data.Build\build_grammar.py build                     # -> src\Data.Build\out\grammar.sqlite + out\grammar\
python -I src\Data.Build\build_grammar.py mapping-report --lexique \path\to\Lexique383.tsv --report <work>\grammar\mapping-report.txt
python -I -m unittest discover -s src\Data.Build -p test_build_grammar.py
```

`fetch-archive --dest <work>\grammar\archive` downloads the pinned tarball once and keeps it (SHA-256 checked); `fetch
--from-archive <tarball>` then reads the four files from it by name, so the build does not need the mirror. `fetch --from-dir <dir>` copies from an extracted copy of the pinned archive (`codeload.github.com/Pofilo/grammalecte/tar.gz/08511c2…`,
SHA-256 `065affe4…a077`) instead of downloading; every file must have its pinned SHA-256. `build` is byte-reproducible (it
prints the SHA-256 of the file: two runs must agree) and writes `out\grammar\SOURCE.txt` and `out\grammar\MPL-2.0.txt`
(from `licenses\mpl-2.0\LICENSE.txt`) — ship both next to `grammar.sqlite` (MPL-2.0). `mapping-report` exits 1 when number
on N/A or person on finite verbs agree with Lexique less than 95 % (a decoding bug). Format and numbers:
[data-pipeline-design.md](../design/data-pipeline-design.md) § 8. In 10D-0 the app does not load it yet (10D-1 does).

The grammar test sets (test text only, never committed; plan 11 Q4) go into `<work>\grammar-sets\` — one network job, one
request at a time:

```
python -I src\Data.Build\build_grammar_sets.py fetch --out <work>\grammar-sets
python -I src\Data.Build\build_grammar_sets.py build --out <work>\grammar-sets --heldout-subs <heldout_subs_40k.txt> --heldout-wiki <heldout_wiki_8k.txt>
python -I src\Data.Build\build_grammar_sets.py check <work>\grammar-sets\g-synthetic.jsonl <work>\grammar-sets\g-clean.jsonl src\Predict.Eval\grammar\g-invented.jsonl
python -I -m unittest discover -s src\Data.Build -p test_build_grammar_sets.py
```

A re-fetch gives other text (the articles keep changing): keep `<work>\grammar-sets\raw\` to reproduce the SHA-256s of
`manifest.json`.

### 1c. Build the picture pack (`pictos.sqlite`, optional)

ARASAAC pictograms (CC BY-NC-SA 4.0) for the bar. Needs **Pillow** (build time only:
`python -m pip install --user pillow`). Raw downloads (the index and ~5 k PNGs) go
to `--raw`, or the folder named by the environment variable `PREDICT_PICTOS_RAW`; there is **no default** (the
script stops with « set --raw or PREDICT_PICTOS_RAW »), so bulk data never lands in the source tree by accident —
point it at a bulk-data disk. Only the pack goes to `src/Data.Build/out/`. `report` reads the held-out texts from
`--subs` / `--wiki`, or from `PREDICT_CORPUS_DIR` (`heldout_subs_40k.txt`, `heldout_wiki_8k.txt`); without them it
stops with a message.

```
cd src/Data.Build
python -I build_pictos.py fetch-index                                   # api.arasaac.org …/pictograms/all/fr (6.9 MB)
python -I build_pictos.py map --lexique <Lexique383.tsv> --db out/prediction.sqlite   # uses picto_overrides.tsv
python -I build_pictos.py report --db out/prediction.sqlite             # coverage vs the D4b floors (work/coverage.json)
python -I build_pictos.py fetch-images                                  # ~5 k PNGs, sequential, ≥ 0.3 s apart (~30 min), resumable
python -I build_pictos.py pack                                          # → out/pictos.sqlite + out/ARASAAC-LICENCE.txt
python -I build_pictos.py sheet --forms top300 --db out/prediction.sqlite   # contact sheets for review
python -I -m unittest discover -s . -p test_build_pictos.py
```

`report` exits 1 when coverage falls below the floors the owner accepted in D4b (product-decisions.md:
subtitle held-out text 53 %, wiki 38 %, lexicon 52 %, measured on 2026-11-08 without the forms excluded on purpose); the plan-02 75/65/70 %
gates are superseded, and the distance to the plan-02 prototype is printed for information only.
`fetch-images` downloads the most frequent words' pictures first (a CDN 404 is retried once on the API URL); `pack --partial` packs what is already
fetched (the words of missing pictures get none; meta `partial` says so) — re-run `pack` once the fetch is
complete. The keyboard build copies `out/pictos.sqlite` + `ARASAAC-LICENCE.txt` next to the exe when they
exist. Icon (committed output): `python -I make_icon.py` → `src/Predict.Keyboard/Assets/app.{svg,ico}`,
`app-paused.ico` (deterministic).

### 1d. Fetch the bundled fonts (`fonts\`, optional)

OpenDyslexic v0.91.12, Lexend (commit 7894f02) and Andika 7.000 for the bar (D16), all SIL OFL 1.1. Standard
library only; every download is checked against a pinned SHA-256 and the files are copied **unmodified**
(Reserved Font Names: no subsetting, no conversion). Raw files go to `--raw` (default: the
environment variable `PREDICT_FONTS_RAW`, else `src/Data.Build/out/raw-fonts/`, git-ignored).

```
cd src/Data.Build
python -I fetch_fonts.py all          # fetch (resumable, hash-checked) + pack → out/fonts/{OpenDyslexic,Lexend,Andika}/
python -I fetch_fonts.py pack         # re-pack from the raw files only (refuses to replace a folder it did not write)
python -m unittest discover -s . -p test_fetch_fonts.py   # pin check, refuse-to-delete
```

`pack` writes each family's font files, `OFL.txt` (+ `FONTLOG.txt` / `README.txt` when upstream has them) and a
`SOURCE.txt` (URL, version/commit, SHA-256, date), and fails above 8 MB (currently 2.23 MB). The keyboard build
copies `out/fonts/` next to the exe as `fonts\` when it exists; without it only Segoe UI is offered.

### 1e. Build espeak-ng for the Piper voice (`out\espeak\`, optional; plan 08a)

The Piper voice needs `libespeak-ng.dll` at the exact commit piper1-gpl 1.8.0 uses (`724808c5`, 1.52.0.1: it has
`espeak_TextToPhonemesWithTerminator`, which no official espeak-ng release has). `build_espeak.ps1` builds it with a
pinned MSYS2 UCRT64 gcc toolchain installed **entirely in the work folder** (nothing on C:, no admin): every download is
checked against `espeak-ng.lock.json`, the DLL may import only system DLLs, and the compiled French + English data must
be byte-identical to the piper-tts 1.8.0 wheel. About 1.5 GB in the work folder, ~10 min the first time.

```
powershell -ExecutionPolicy Bypass -File src\Data.Build\build_espeak.ps1 -Work %PREDICT_BUILD_DIR%\espeak
#   PREDICT_BUILD_DIR: a folder of your choice off the system drive (e.g. D:\build\predict); the script has no default   -Pin: re-resolve the toolchain packages once and print lock entries
```

It stages `out\espeak\` (git-ignored, 1.5 MB): `libespeak-ng.dll`, `espeak-ng-data\` (phontab, phonindex, phondata,
intonations, fr_dict, en_dict, lang\roa\fr, lang\gmw\en), `COPYING` and `SOURCE.txt` (commit, toolchain versions, cmake
line, SHA-256 of every file). Two runs give the same files, DLL included. On a volume without hard links (exFAT) the
script restores the packages' hard links as copies.

### 1f. Fetch and patch the Piper voices (`out\voices\siwis\`, `out\voices\pierre\`, optional; plans 08a, 08b)

```
python -m venv %PREDICT_BUILD_DIR%\venv-voices
%PREDICT_BUILD_DIR%\venv-voices\Scripts\python -I -m pip install --require-hashes --only-binary=:all: -r src\Data.Build\requirements-voices.txt
cd src\Data.Build
%PREDICT_BUILD_DIR%\venv-voices\Scripts\python -I fetch_voices.py all --raw %PREDICT_BUILD_DIR%\voices-raw
python -I -m unittest discover -s . -p test_fetch_voices.py     # pins, bad hash, patched hash, refuse-to-overwrite
```

`--voice siwis|pierre` limits a command to one voice (`all --voice pierre`: about 77 MB downloaded).

After a test-release publish, `python -I src\Data.Build\size_report.py --new <work>\publish-X --old <work>\publish-Y
--out <work>\publish-X\size-report.md [--notes notes.md]` writes the size report; it names the folders as
`<work>\…`, never by their local path, and lists the `"public": false` packs.

`fetch` downloads the pinned `fr_FR-siwis-medium` files (piper-voices `cf910fb5`) and the CC BY 4.0 legal code;
`patch` marks the model's `Ceil` tensor as an output (onnx 1.23.2) and must give SHA-256 `d1120ace…`; `pack` writes
`out\voices\siwis\` (git-ignored, 60.3 MiB) with `voice.json` (the app's card: sizes + SHA-256), `SOURCE.txt` (the CC
BY change notice), `LICENSE.txt`, `MODEL_CARD`. The keyboard build copies `out\espeak\` and `out\voices\` next to the
exe; without them the app has the SAPI voices only.

**pierre** (plan 08b, D28) is speaker `pierre` (id 1) of the two-speaker `fr_FR-upmc-medium` model: upstream
`9abb3800…`, patched `7f763f70…` (76.7 MB), CC BY-SA 4.0 (`LICENSE.txt`, plus the dataset's `DATASET-LICENSE.md` and
`DATASET-README.md` pinned at upmc-pierre-data commit `93d6429b`; the credit quotes that README verbatim). Its card says `"speaker": "pierre"`, `"order": 2` and **`"public": false`**:
the model was fine-tuned from the lessac voice (research-only training data), so it ships in private test releases
only; a public release (plan 10) must leave it out. An optional `"inference"` block in a pin is copied to the card and
overrides the scales of the `.onnx.json` (which is never edited).

`onnxruntime.dll` needs the Visual C++ 2015–2022 x64 runtime, which a clean Windows lacks (the test VM): stage the four
DLLs for an app-local copy (A13) from a machine where Microsoft's redistributable is installed; each must be signed by
Microsoft, version ≥ 14.40:

```
powershell -ExecutionPolicy Bypass -File src\Data.Build\stage_vcruntime.ps1      # -> out\vcruntime\ (+ SOURCE.txt)
```

### 1h. Build the « Noms propres » pack (`out\noms-pack\noms\`, optional; plan 10C, D30)

```
set PEPITE_NOMS_RAW=%PREDICT_BUILD_DIR%\names-pack\raw
python -I src\Data.Build\pepite-noms\build_names.py fetch                       # pinned inputs, one at a time, SHA-256 checked
python -I src\Data.Build\pepite-noms\build_names.py build --out %PREDICT_BUILD_DIR%\names-pack\out [--previous <last>\noms\noms.json]
python -I -m unittest discover -s src\Data.Build\pepite-noms\tests
xcopy /E /I %PREDICT_BUILD_DIR%\names-pack\out\noms src\Data.Build\out\noms-pack\noms
```

Inputs and snapshots are pinned in `names.lock.json` (INSEE, Statbel, CLDR 49, flag-icons 7.6.0, 12 Commons flags, four
Wikidata query snapshots, the CC BY 4.0 legal code, the resvg 0.50.0 build tool); `snapshot <id>` re-runs a Wikidata query
on purpose and re-pins it, `fetch --pin-new` pins a new input after review. Python 3.10+ and Pillow; about 10 MB of raw
downloads. The build writes `noms\` (2.9 MB: `noms.json`, `images\`, `LICENCE.txt`, `SOURCES.md`, `ATTRIBUTION-fr.txt`),
`noms-<edition>.zip`, `diff-<edition>.md` and `build-report.json`; it fails above 10 MB, on a picture outside the licence
allow-list (MIT, public domain, CC0) or on a historical figure without a death date. The keyboard build copies
`out\noms-pack\noms\` next to the exe as `noms\` when it holds `noms.json` and `LICENCE.txt`; without it no name is proposed
(`names: off`). The folder is meant to become its own repository (« pepite-noms »): see its README.

### 1g. Golden test and speech bench (plans 08a, 08b)

`dotnet test src\Predict.Keyboard.Tests` runs the golden test: the C# port of Piper's phonemizer against
`Fixtures\piper-golden-siwis.json` and `Fixtures\piper-golden-pierre.json` (40/40 each; regenerate with
`tools\voice-lab\golden_ids.py` in the voice-lab piper venv — defaults = siwis; pierre: `--model fr_FR-upmc-medium
--speaker pierre --out …\piper-golden-pierre.json`), and, when `out\espeak\` exists (`HAVE_ESPEAK`), our espeak-ng build through the P/Invoke against Piper's own
clauses (40/40). The latency / highlight / footprint bench installs no hook and may run on the host; it plays sound
unless `--audio null`. The app needs the WindowsDesktop runtime: on this host, run it from the self-contained publish.

```
Pepite.exe --speech-bench <outDir> --profile normal --audio device ^
    --sentences development\voice-test\sentences.txt [--volume 30]
```

`--sentences` is needed by the default bench and by `--wav` alone: an installed or published copy has no
`development\voice-test\` (only a build output under `bin\` finds it by itself); without it the bench exits 2 and says
so in `bench.md` (0.10.2; in the 0.10.1 VM the reason was only in `bench-error.txt`). `--wav … --word`,
`--wav … --word-items` and `--switch` do not read it. Exit codes: 0 = done, 1 = a gate failed (device audio), 2 = could
not run (reason in `bench.md`, `bench.log` or `bench-error.txt`).

Options: `--voice siwis|pierre` (default siwis), `--switch` (siwis configured, a preview of pierre, its unload 30 s
later: switch time and working sets), `--wav <dir> [--items L01,…] [--variant ns,ls,nw]` (each item once, saved as a
WAV, with the liaison consonant durations in `liaison.json`; the listening page of plan 08b step 7),
`--wav <dir> --word-items <file> [--word-suffix .] [--carrier "Le mot : {0}."] [--phoneme-fix a=b;…]` (each item of a
sentences.txt-format file, category `word` said as a word echo, N takes with the word cache off; take 1 saved as
`<id>.wav`, per-take length / silences / onset-offset level / alignment / ORT time in `words.json`; voice-evaluation.md
§ 9d), `--word-reps N`, `--sentence-reps N` (default 10 / 3), `--long-text <file>` (one long utterance: first audio
and peak working set), `--ort-spinning on` (A/B only; the app never spins). It writes `bench.json`, `bench.md`,
`bench.log`; exit code 1 when a gate fails (device audio only).

### 1i. Package a data release (maintainers; plan 13)

After § 1–1h (`out\` holds the pinned data pack, pictures, names, fonts, espeak-ng build and patched voices), one heavy
job on the work disk:

```
python -I src\Data.Build\package_data_release.py --dest <work>\data-release\data-YYYY.MM ^
    --espeak-source <espeak-ng-<commit>.tar.gz> [--replace] [--write-lock]
python -I src\Data.Build\fetch_data_release.py --from <work>\data-release\data-YYYY.MM --out <work>\data-release\verify-out
python -I -m unittest discover -s src\Data.Build -p "test_*_data_release.py"
```

It checks the sources first (the data pack against `data-pack.lock.json`, `libespeak-ng.dll` against
`espeak-ng.lock.json`, each voice model against `voices.json`, the commit recorded in the `git archive` header of the
espeak-ng source archive), then writes one asset per component named as in `data-release.lock.json`, plus
`SHA256SUMS.txt` and `BUILD-INFO.txt`. The zips are **deterministic** (sorted members, the first day of the tag's month
as date, mode 644, deflate level 9): two runs on the same files and the same Python give the same SHA-256 (checked for
`data-2026.11`: identical `SHA256SUMS.txt` over two runs). `--write-lock` writes the new SHA-256 and sizes into the lock.
Before uploading, scan the unpacked tree (`export.py … --scan-data <verify-out>`) and decide each note. The release is
created by the owner (`gh release create data-YYYY.MM`, published, not a pre-release); a published asset is never
replaced: a new pack is a new tag and a new lock.

**`data-2026.11.2` (0.10.2, made 2026-11-18):** the same eight asset names as `data-2026.11` except `noms-2026.2.zip`
(replaces `noms-2026.1.zip`). Six assets are **byte-identical** to `data-2026.11` (pepite-data 2026.11.1, pictures,
fonts, espeak-ng build and source, pierre). Two changed: `noms-2026.2.zip` (`2df79a67…cd5a`, 464 079 bytes; its
`noms.json` is `54b08005…78fe`; the zip `0f03da2b…8568` of `build_names.py` holds the same files, only its entry date
differs: 2026-01-01 there, the tag's month here) and `voice-siwis-cf910fb5.zip` (`5de127f0…d90a`: `fetch_voices.py pack
--voice siwis` again, so its `voice.json` carries `word_carrier` « Le mot : {0}. » and `word_carrier_except` « eu »,
« les »; `SOURCE.txt` records the new pack date). Its name is unchanged (the name follows the piper-voices revision), so
an old download cache holding the `data-2026.11` siwis zip fails the SHA-256 check and is fetched again. Checked with
`fetch_data_release.py --from` (8 × ok, 314 files, data pack 0 problems) and the `Data.Build` Python suite. The
release command, for the release session — run it **before pushing the commits that pin `data-2026.11.2` / opening
their PR to `pepite-labs/pepite`**: the `test` job of `app.yml` (every PR and push to `main`) and `pages.yml` fetch
the tag the lock names, so the release must exist first; `--target main` works without those commits. `--latest`
follows the `data-2026.11` precedent: the app releases are still pre-releases, which GitHub never marks Latest, so the
data release keeps that mark (bash, `S` = the staging folder on the work disk; title, body and asset
list as `data-2026.11`: « Données … », `BUILD-INFO.txt` as notes, the eight lock assets + `SHA256SUMS.txt` +
`BUILD-INFO.txt`; published, not a pre-release, on `main`):

```
S=<work>/data-release/data-2026.11.2-local-staging
(cd "$S" && sha256sum -c --strict SHA256SUMS.txt)
gh release create data-2026.11.2 --repo pepite-labs/pepite --target main --title "Données 2026.11.2" --latest \
    --notes-file "$S/BUILD-INFO.txt" "$S"/*.zip "$S"/espeak-ng-*.tar.gz "$S/SHA256SUMS.txt" "$S/BUILD-INFO.txt"
gh release view data-2026.11.2 --repo pepite-labs/pepite --json tagName,isDraft,isImmutable,assets   # 10 assets, tag set
```

## 2. Installer (plan 12, from 0.10.0)

One command builds the installer from a clean checkout with the pinned data of § 1 in `src\Data.Build\out\`
(`prediction.sqlite` + `prediction-SOURCES.txt`, `grammar.sqlite` + notices, `pictos.sqlite` + `ARASAAC-LICENCE.txt`, `noms-pack\noms\`, `fonts\`,
`espeak\`, `vcruntime\`, `voices\`). `<work>` is a bulk-data folder outside the repository; nothing it produces is
committed.

```
powershell -ExecutionPolicy Bypass -File installer\fetch_innosetup.ps1 -WorkDir <work> -Pin     # once: Inno Setup 7.1.1
powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1 -Version 0.10.2 -WorkDir <work> [-Private] [-VerifyReproducible]
python -I -m unittest discover -s installer\tests -p test_release_lint.py
# the CI form (plan 13, .github/workflows/app.yml): no word list, no earlier release, the data release's source archive
powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1 -Version 0.10.2 -WorkDir <work> -NoForbiddenList ^
    -OldRelease '' -EspeakSourceArchive <cache>\espeak-ng-<commit>.tar.gz
```

- **Inno Setup** (`installer\innosetup.lock.json`): `fetch_innosetup.ps1` downloads the official installer, checks its
  Authenticode signature on the first `-Pin` run (signer, URL, SHA-256 recorded), then only accepts that SHA-256
  (« sha256 mismatch » otherwise), and installs the compiler without admin rights in `<work>\tools\innosetup-7.1.1\`
  (`/CURRENTUSER /PORTABLE=1`: no uninstall entry). A build tool, never shipped; its licence allows any use.
- **`build_installer.ps1`**, one heavy job at a time (the test VM must be off): preconditions (clean working tree —
  `-AllowDirty` only for a test build, recorded in `BUILD-INFO.txt` —, commit, SDK, pinned data and licence files) →
  a non-incremental `dotnet build` + `dotnet publish` (self-contained win-x64, `PathMap` so that no local path ends up
  in the binaries) to `<work>\build\stage-<ver>` → voice packs (a public build removes every `"public": false` pack and
  fails if one remains, D28; `-Private` keeps them and adds the `-test` suffix) → `LISEZ-MOI.txt` from
  `installer\LISEZ-MOI.txt.in` (placeholders `{VERSION}`, `{PRIVE}`, `{SUFFIXE}`, `{NOTES}` = `installer\notes\<ver>.txt`,
  `{CREDIT_PIERRE}` = `installer\notes\credit-pierre.txt` when pierre ships, `{DONNEES}` = the tag of
  `data-release.lock.json`, the link of the credits; the text opens with the « préversion non signée » banner and step 1
  of INSTALLER shows `gh attestation verify … --source-ref refs/tags/v<ver>` (`{ATTESTATION}` =
  `installer\notes\attestation.txt`, public builds only: a `-Private` build has no attestation)) → **release lint** → PDBs to `symbols-<ver>.zip`
  (not installed) → `files.sha256` (one line per installed file; the installed `prediction.sqlite` must equal the
  source one) → `ISCC.exe installer\pepite.iss` → `size-report.md` (`size_report.py --installer`; `-OldRelease ''` =
  no comparison) → the espeak-ng source archive (`git archive` of the pinned commit, or `-EspeakSourceArchive` = the
  one of the data release; the GPL-3 source offer) → `signatures.txt` (Authenticode status of every PE file of the
  stage and of the installer) → `BUILD-INFO.txt` (commit, SDK, Python, Inno Setup, VC++ runtime versions, the
  forbidden-term pass; on GitHub Actions the runner image and the run URL) → `SHA256SUMS.txt` (flat names) → with
  `-VerifyReproducible`, a second build of the same commit compared file by file. Outputs in `<work>\release-<ver>\`,
  all at the top of the folder, as the release assets are (plan 13).
- **Release lint** (`installer\release_lint.py`): forbidden words from a list kept **outside the repository**
  (`-Forbidden`, default `<work>\release-config\release-lint-forbidden.txt`; a line `own:<words>` applies only to our
  own texts and binaries, for generic words that third-party licence texts legitimately contain), absolute drive paths (except the
  documented install locations), build paths inside `Pepite.exe` / `Pepite.dll` / `Predict.Core.dll`, plain `http://`
  links and leftover placeholders in our own texts, and `"public": false` voice packs in a public build.
  `-NoForbiddenList` (`release_lint.py --no-forbidden`; the CI build: the list never goes to GitHub) skips the word
  pass only and records it in `BUILD-INFO.txt`; such a build is published only after the owner's local rebuild from
  the same tag passes the full lint (plan 13, A8). The embedded
  help (`aide\`, fonts excepted) counts as our own texts; `installer\help_lint.py` runs on it, and a public build
  requires `aide\index.html`.
  It also checks that every runtime pack and package of `Pepite.deps.json` that ships files has its version in
  `THIRD-PARTY-NOTICES.txt`, and that the publish carries the licence texts `licenses\dotnet\`, `licenses\windowsdesktop\`,
  `licenses\system-speech\` and `licenses\apache-2.0\` (non-empty). The csproj takes the two runtime-pack licences from
  the packs the SDK resolved (target `PepiteRuntimePackLicenses`) and fails a self-contained build or publish when one
  is missing; a framework-dependent dev build does not copy them.
- **Embedded help** (`freeware/aide/`, plan « aide intégrée »): copied by the csproj to `aide\` with the fonts of
  `src/Data.Build/out/fonts` in `aide\fonts\`. Check the pages with `python -I installer\help_lint.py aide` (tests:
  `python -I -m unittest discover -s installer\tests -p test_help_lint.py`). The bar pictures `aide\img\barre-*.png` (the real screenshots are
  `aide\img\capture-*.png`, never redrawn by the script)
  are redrawn by `powershell -File src\Data.Build\render_help_images.ps1` (a Release build with its fonts;
  `Pepite.exe --render-bar --no-pictures`, no hook) after a change of the bar's look; contributor notes in
  `aide\README.md`.
- **Reproducibility**: `files.sha256` is the gate (identical across two builds of one commit). The installer's
  installed-file dates are the commit date (`TouchDate` / `TouchTime`, UTC). Checked on 2026-11-15 with an earlier
  build of the 0.10.0 tree (identical `files.sha256`, byte-identical installer); `-VerifyReproducible` writes
  `reproducibility.txt` next to the installer when it is run.
- **Preconditions of `build_installer.ps1`** (0.10.2): it stops with a clear message when `git status` fails (not a
  repository, « dubious ownership », git missing: never read as a clean tree), when `dotnet` is not on `PATH` or does
  not start as `global.json` asks (it names the `dotnet.exe` it found and its SDKs: put the folder of SDK 10.0.115 first
  in `PATH`), and when a version variable is set in the environment (`VERSION`, `VersionPrefix`, `VersionSuffix`,
  `AssemblyVersion`, `FileVersion`, `InformationalVersion`, `PackageVersion`). MSBuild reads every environment variable
  as a property, names compared without case, so `VERSION` became the `Version` of each project without its own: in
  0.10.1 the CI's `Predict.Core.dll` said 0.10.1 and a local rebuild 1.0.0. The workflow now passes the version in
  `PEPITE_VERSION` (checked locally: `VERSION=9.9.9` → `Predict.Core.dll` 9.9.9.0, `PEPITE_VERSION=9.9.9` → 1.0.0.0).
- **Comparing a CI build with a local rebuild** (plan 13 A8; byte-equal from 0.10.2). The CI publish uses SDK 10.0.115,
  whose bundled 10.0.15 runtime also runs the compiler, so a host with the same SDK (installed or portable, first in
  `PATH`; `DOTNET_ROOT` = that folder; no `DOTNET_ROLL_FORWARD`) gives the same compiled bytes. In 0.10.1 the host ran the
  compiler on another runtime (no 6.0 runtime there) and the PDB / DLL bytes differed. Steps, on the work disk:

  ```
  git clone --branch v<ver> https://github.com/pepite-labs/pepite <work>\rebuild\src     # a fresh clone of the tag
  cd <work>\rebuild\src
  python -I src\Data.Build\fetch_data_release.py --cache <work>\rebuild\data-cache        # the lock's data release
  powershell -ExecutionPolicy Bypass -File src\Data.Build\stage_vcruntime.ps1
  powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1 -Version <ver> -WorkDir <work>\rebuild ^
      -OldRelease '' -EspeakSourceArchive <work>\rebuild\data-cache\espeak-ng-<commit>.tar.gz   # full forbidden list
  gh release download v<ver> --repo pepite-labs/pepite --pattern files.sha256 --pattern BUILD-INFO.txt --dir <work>\rebuild\ci
  python -I -c "import sys; r=lambda p: dict(l.rstrip('\n').split('  ',1)[::-1] for l in open(p,encoding='utf-8') if l.strip()); a,b=r(sys.argv[1]),r(sys.argv[2]); d=sorted(k for k in a.keys()|b.keys() if a.get(k)!=b.get(k)); print(len(a),len(b),'differ:',len(d)); print('\n'.join(d))" <work>\rebuild\ci\files.sha256 <work>\rebuild\release-<ver>\files.sha256
  ```

  `files.sha256` lists every installed file (`<sha256>  <path>`), so the comparison is file by file. Expected: no
  difference except `msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` and
  `licenses\vcruntime\SOURCE.txt`, which `stage_vcruntime.ps1` takes from the build machine (the runner image's Visual
  C++ runtime; compare the versions in the two `BUILD-INFO.txt`). Any other line is a finding (0.10.1 had two more,
  `Pepite.dll` and `Predict.Core.dll`: the version variable and the compiler's runtime above). The installer itself is byte-equal only when `files.sha256` is fully
  identical (same VC++ runtime, same Inno Setup of `innosetup.lock.json`, same commit date). `-NoForbiddenList` is the
  CI's switch only: the local rebuild runs the full lint.
- **The installer script** (`installer\pepite.iss`, GPL-3 « Corresponding Source »): fixed `AppId`
  `{999AA879-43C2-4C22-9785-691AA95A3455}` (never change it; `InstallInfo.AppId` in the app reads the same uninstall
  entry), per-user by default, all-users on demand, Windows 11 x64 only, components `app` (fixed), `images`,
  `voix\siwis`, `voix\pierre` (private builds), tasks `demarrage`, `pepite`, `bureau`, the school's machine file
  (`/CONFIG=`, checked with `Pepite.exe --check-config`), the uninstall data question. Behaviour and switches:
  [../design/keyboard-design.md](../design/keyboard-design.md) § 8 and `installer\DEPLOIEMENT.txt`.
- **Tests in the VM**: `installer\tests\Test-PepiteInstall.ps1` (scenarios T1–T11, dot-sourced inside the Windows 11
  test VM; never run an installer on a development PC: it installs global hooks and writes the Run key).

## 2a. GitHub Actions (plan 13)

`.github/workflows/app.yml`; every action is a GitHub-owned action pinned to a full commit SHA (the tag in a comment),
the workflow token is read-only except in the `release` job, `pull_request_target` is never used.

| Job | When | What |
|---|---|---|
| `test` (windows-2025) | every pull request, push to `main` | the word base of the data release (`--only pepite-data-…zip`), `dotnet build` / `dotnet test` of `Pepite.sln`, the Python suites, `help_lint` — the required check |
| `installer` | a `v<x.y.z>` tag, or « Run workflow » with `installer` ticked | tag = csproj `Version` (else the job fails); `fetch_data_release.py`, `stage_vcruntime.ps1` (the runner's VC++ runtime), `fetch_innosetup.ps1`, `build_installer.ps1 -NoForbiddenList -OldRelease '' -EspeakSourceArchive …`; artifact kept 14 days (approve the `release` environment within 14 days, else re-run the workflow) |
| `smoke` | after `installer` | `installer\tests\Smoke-PepiteInstall.ps1` on the disposable runner (refuses to run elsewhere): silent per-user install, installed files = `files.sha256`, `--check-config` example → 0 and broken JSON → 2, HKCU uninstall key, silent uninstall removes the folder and the key; `--speech-bench --audio null` reported, never fatal |
| `release` (ubuntu) | tags only, environment `release` (owner approval) | `sha256sum -c SHA256SUMS.txt` and the asset list, `actions/attest` on the installer and the symbols, `installer\release_notes.py`, `gh release create --draft --prerelease --verify-tag` with every asset (immutable releases: assets are uploaded while it is a draft; the owner publishes after the checks of plan 13 A8) |

The forbidden-term list never goes to GitHub: a CI build records « SKIPPED » in `BUILD-INFO.txt`, and the owner's
local rebuild from the same tag runs the full lint before the draft is published. `actionlint` (a downloaded, hash-checked
binary; third-party actions are not allowed in the workflow) is run locally on the workflow files. The online help has
its own workflow, `pages.yml` (§ 2c).

## 2c. Online help on GitHub Pages (plan 13, A10)

`.github/workflows/pages.yml` publishes <https://pepite-labs.github.io/pepite/>: one folder per **minor** version
(`0.10/aide/…`; a patch release replaces its minor's folder), `/` redirects to the latest. Triggers: « Run workflow » on
`main` (input `minor`, default `0.10`, must be the csproj's minor: the site can go live right after the first public
push) and every `v<x.y.z>` tag; environment `github-pages` (owner approval). The other minor folders are rebuilt from the
latest published release tag of each minor (a Pages deployment replaces the whole site).

```
python -I installer\build_help_site.py --out <site> --fonts <out>\fonts --latest 0.10 --version 0.10=<source tree>
python -I -m unittest discover -s installer\tests -p test_build_help_site.py
```

The site holds the same files as `aide\` laid out as in an installed copy (`<minor>\aide\…`, fonts in `aide\fonts\`,
next to `THIRD-PARTY-NOTICES.txt`, `COPYING`, `DEPLOIEMENT.txt` and `LISEZ-MOI.txt` — rendered from the template as a
public build does). The transform only adds a banner « Aide en ligne de Pépite <x.y> — l'aide installée avec Pépite
fonctionne sans Internet. » on every page (style rule appended to `style.css`), and a paragraph on the privacy page
(GitHub Pages logs the visits; no audience measurement, no cookie). The CSP and the no-external-link rule stay;
`help_lint --stage` runs on every version folder and fails the build.

## 2b. Try the predictor

```
dotnet run --project src/Predict.Cli -c Release -- src/Data.Build/out/prediction.sqlite orizon maizon boku
```

Expected: `orizon → horizon …`, `maizon → maison …`, `boku → beaucoup …`.

## 3. Run the quality benchmark

```
dotnet run --project src/Predict.Eval -c Release -- src/Data.Build/out/prediction.sqlite src/Predict.Eval/cases.json
```

Prints per-case hits and a per-category scorecard (top-1 / hit@8 / MRR + a GOOD/FAIR/POOR grade). Add cases to
[`src/Predict.Eval/cases.json`](../src/Predict.Eval/cases.json).

For the **large held-out benchmark** (overall quality + keystroke savings) pass a `.txt` of sentences the
model was *not* trained on:

```
dotnet run --project src/Predict.Eval -c Release -- src/Data.Build/out/prediction.sqlite held_out.txt --max 20000
```

Methodology and current numbers are in [evaluation.md](evaluation.md).

## Notes

- The C# projects are a plain SDK solution; open `src/` in Visual Studio / Rider, or use the `dotnet` CLI.
- `Predict.Core` is the reusable library; `Predict.Cli` / `Predict.Eval` are thin drivers.
