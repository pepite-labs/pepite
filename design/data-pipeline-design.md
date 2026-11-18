# `Data.Build` design — building `prediction.sqlite`

Source: [`src/Data.Build/`](../src/Data.Build/). How to run: [BUILD.md](../development/BUILD.md).

## 1. Pipeline

```
 Lexique383.tsv ──► build_db.py ──────► words            (≈125 k rows, ~8 MB)
                                          │
 corpora (*.tsv / *-sentences.txt / *.txt)│
   ├─► build_ngrams.py --min2 3 --min3 3 ─┼─► bigrams     (1.13 M rows)
   │                                      ├─► trigrams    (1.40 M rows)
   └─► build_4grams.py --min 4 ───────────┴─► fourgrams   (1.1 M rows)
                                                 = prediction.sqlite ≈ 163 MB (for ≈ 5.7 M sentences)
```

Each script **drops and recreates only its own tables**, so the steps are independent and re-runnable
(e.g. rebuild 4-grams with another threshold without retraining bi/trigrams).

## 2. Schema

```sql
CREATE TABLE words     (ortho TEXT, lemma TEXT, cgram TEXT, phon TEXT, freq REAL);  -- idx_ortho(ortho)
CREATE TABLE bigrams   (prev TEXT, word TEXT, c INTEGER);                          -- idx_bi_prev(prev)
CREATE TABLE trigrams  (p2 TEXT, p1 TEXT, word TEXT, c INTEGER);                   -- idx_tri_ctx(p2, p1)
CREATE TABLE fourgrams (p3 TEXT, p2 TEXT, p1 TEXT, word TEXT, c INTEGER);          -- idx_four_ctx(p3, p2, p1)
```

- Indexes cover exactly the engine's access path: "all successors of this context".
- Phonetic keys are **not** stored: the engine computes them at load (`PhoneticKey`), so changing the
  phonetic rules needs no DB rebuild.
- `lemma`, `cgram` are kept for future features (inflection, part-of-speech filtering); unused in v1.
- `n`-gram tables are optional: the engine checks `sqlite_master` and degrades gracefully
  (no n-grams → frequency ranking; no 4-grams → trigram model).

## 3. Lexicon import (`build_db.py`)

| Rule | Why |
|---|---|
| `freq = max(freqfilms2, freqlivres)` | films ≈ spoken register, books ≈ written; taking the max avoids penalising either |
| one row per lower-cased spelling, keeping the most frequent homograph row | the predictor proposes *spellings*; homographs (`est` noun/verb) collapse |
| drop multi-word entries (`" " in ortho`) | v1 predicts single words |
| fail fast if a required column is missing | guards against a different Lexique version |

## 4. N-gram training (`build_ngrams.py`, `build_4grams.py`)

- **Formats auto-detected**: Tatoeba `*.tsv` and Leipzig `*sentences.txt` (text = last tab column), else
  one sentence per line (the Vikidia and book text written by `corpus/prepare_corpus.py`).
- **Tokeniser** (identical in Python and in the engine): lower-case; `'`, `’`, `-` → space; tokens
  `[a-zà-ÿœ]+`. So `l'horizon` → `l horizon`, `peut-être` → `peut être`. Sentences are units: n-grams never
  span a line.
- **« œ » / « æ » fold** (plan corpus-light 0.1): each token is folded after the match, `œ` → `oe`, `æ` → `ae`, because
  Lexique spells « coeur », « soeur », « oeuvre »: before the fold every n-gram holding « cœur » was counted under a
  form the engine never proposes. The fold is per token, never on the whole line (a line-level replace changes the
  line length). Each script keeps its own `fold()`; `corpus/tests` checks that the copies agree.
- **Pruning** by minimum count (`--min2 3 --min3 3`, 4-grams `--min 4`): removes the long tail of
  one-off n-grams (mostly noise/names) and keeps the DB inside the resource budget.
- Counting is in-memory `Counter`s (needs a few GB of RAM for ≈ 39.5 M tokens; build-time only, run on the
  developer machine, bulk data kept on the external disk).

## 5. Corpora (since 0.10.0: written sources only)

| Corpus | Size used | Register | Licence / use |
|---|---|---|---|
| Tatoeba `fra_sentences.tsv` | ≈ 730 k sentences | short everyday sentences | CC BY 2.0 FR |
| Leipzig `fra_news_2023_1M` | 1 M sentences | news (formal) | CC BY, version not stated by the provider (Leipzig Corpora Collection, <https://wortschatz.uni-leipzig.de/en/usage>); credit « Leipzig Corpora Collection » with a link to the download page (owner, 2026-11-15) |
| Vikidia (Kiwix ZIM 2026-09) | training bucket, 10.7 M tokens | encyclopaedia for 8–13-year-olds | CC BY-SA 3.0 → DB under CC BY-SA 4.0 |
| Public-domain children's books (Project Gutenberg) | 56 books, 2.5 M tokens (cap 20 %) | stories, 1860–1940 | public domain (author and translator died ≤ 1955) |
| OpenSubtitles fr (OPUS) — **not used since 0.10.0** | (was 4 M sentences up to 0.9.0) | conversational | no licence stated; test-only / private (`public: false`); its held-out slice remains a context measure |

Held-out evaluation sets are **separate slices** never fed to training: E1–E3 (held-out books, Vikidia articles, invented
sentences; the decision sets), W (`heldout_wiki_8k.txt` and the Leipzig `fra_wikipedia_2021_1M` draw) and the subtitle
slice `heldout_subs_40k.txt` (context only); see [benchmark-design.md](benchmark-design.md).

Only **counts** derived from the corpora ship in the DB; the corpora themselves are not redistributed,
and neither the DB nor the corpora are committed to git.

**Prebuilt data pack (owner decision 2026-11-16).** The word base is provided already built: normal builds (developers,
the future GitHub Actions « app » workflow) take the published data pack — `prediction.sqlite` + `prediction-SOURCES.txt`,
`spelling.sqlite` and `grammar.sqlite` + their notices — pinned by SHA-256 in `src/Data.Build/data-pack.lock.json` and checked by
`verify_data_pack.py`; they never rebuild it from the corpora. Rebuilding (the pins below, `corpus/`, `build_*.py`) is a
maintainer-only, optional path (the future manual « data » workflow), which ends by updating that lock. Publication
(a versioned GitHub Release asset `data-YYYY.MM`) is plan 13.

**Pinned inputs** (plan corpus-light): every corpus input — Lexique, Leipzig news and Wikipedia, the Tatoeba export
(the weekly file is overwritten upstream, so the archived local copy is the pin), the subtitles (`public: false`,
test-only since 0.10.0), the Vikidia ZIM, the Gutendex snapshot, each public-domain book and `g-invented.jsonl` — has an
entry in [`src/Data.Build/data.lock.json`](../src/Data.Build/data.lock.json): URL, version / date, SHA-256, size,
licence, credit, use (train / heldout / test-only / private / selection) and `public`. The bulk files live in the work
folder named by `PEPITE_CORPUS_WORK` (no default; never a path in the repository). `corpus/fetch_corpus.py` fetches
(one request every 2 s, resumable, never overwrites, stops on a mismatch), verifies and pins them; `corpus/pd-books.tsv`
lists the Gutendex children's books with the owner's rule (every author and translator died in 1955 or earlier; a
translation without a named translator is excluded) and the held-out / training picks; `corpus/prepare_corpus.py`
extracts, splits, draws the held-out sets and samples the training text; `corpus/write_sources.py` writes
`prediction-SOURCES.txt` from the lock.

## 6. Known limits / next steps

- **Domain**: 4-grams help a lot in-domain (KSS 69.8 → 78.8 %) but barely cross-domain (45.0 → 45.4 %).
  The next real lever is a corpus matching the target users' writing (school texts, messages).
- No sentence-start token: the first word of a sentence gets no context, only unigram.
- Apostrophe splitting means `l'` is stored as `l`: since plan 04 (D22) the keyboard shows and inserts it as `l'`
  (display mapping `Elision.Apply` after ranking, no DB rebuild), and after an elision the engine favours
  vowel / mute-h words (`ElisionBoost`). Elision-aware tokens in the corpus build remain possible. Since 0.10.2 the
  context-free frequency of `j' m' c' qu' jusqu' lorsqu' puisqu'` (no Lexique apostrophe row) is estimated by the
  engine at load from the `words` and `bigrams` tables already shipped (predict-core-design §4): still no rebuild and no
  new data release.

## 7. Picture pack (`build_pictos.py` → `pictos.sqlite`, plan 02)

```
 api.arasaac.org …/pictograms/all/fr ──fetch-index──► raw/index/arasaac_all_fr_<date>.json
 Lexique383.tsv + prediction.sqlite (forms) ──map──► work/mapping.tsv, excluded.tsv, needed_ids.txt
 static.arasaac.org/pictograms/{id}/{id}_300.png ──fetch-images──► raw/png300/{id}.png (+ manifest.tsv, missing.tsv)
 ──pack──► out/pictos.sqlite + out/ARASAAC-LICENCE.txt          ──report / sheet──► work/coverage.json, sheet_*.png
```

**Schema** (`schema` = 1): `picto(id INTEGER PRIMARY KEY, png BLOB)`, `word_picto(form TEXT PRIMARY KEY,
picto_id INTEGER) WITHOUT ROWID`, `meta(key, value)`: `source`, `licence`, `licence_url`, `attribution_fr`
(verbatim, see `arasaac_attribution.txt`), `author`, `owner`, `index_date`, `build_date`, `px`, `count`,
`changes`, `schema` (+ `partial` for a partial pack).

**Mapping rules** (normalise NFC, lower case, `’`→`'`):
1. Index: pictograms flagged `sex` or `violence` are skipped (D4b); keys = single-token `keyword`/`plural`
   (`^[a-zà-ÿœ]+$`; multi-word and hyphenated keys dropped), with type, rank in the pictogram's keywords,
   plural flag, `aac`, `schematic`.
2. Readings of a form = Lexique rows with that `ortho`, one per (lemma, cgram), freq = max(films, books);
   **dominant** readings = freq ≥ 0.2 × the top one.
3. Type compatibility: NOM→{2}; VER/AUX→{3}; ADJ→{4,2}; ADV→{4,6}; PRE/CON→{6,4}; ART→{6}; PRO→{6,4,1};
   ONO→{5,6}. *Measured deviation from the plan*: ARASAAC files personal pronouns (je, tu, il, nous, moi…)
   under type 1 "proper name", so type 1 is accepted for PRO only (never for anything else) and
   interrogatives/comparatives (où, comme) are type 4. Keyword without a type = compatible with all.
4. Owner exclusions first: **`aux-none`** — dominant lemma être/avoir (frequencies summed over cgrams:
   Lexique splits `suis` into suivre 1819 vs être AUX 1272 + VER 669); **`art-none`** — dominant reading
   ART* or one of le/la/les/l/un/une/des/du/au/aux; **`letter-none`** — single-letter forms (elisions d', l',
   j', s'… showed letters of the alphabet).
5. For each dominant reading in order: form match (keyword or plural) with a compatible type, else lemma
   match (keyword only); the first reading with a candidate wins. None and a single reading → form match of
   any type (`form-anytype`). (`maisons`→maison, `grande`→grand, `mange`→manger.)
6. Tie-break: **keyword before plural** (added: `fils` matched the plural of `fil`, a spool), then
   `kw_rank` 0, `aac`, not `schematic`, lowest `_id`.
7. `picto_overrides.tsv` (committed curation) always wins: `form<TAB>id` or `form<TAB>-` + reason.

**Images**: 300 px PNG (actually 298 × 298, palette) → RGBA → LANCZOS fit into 128 px → FASTOCTREE
quantization to 128 colours with alpha; nearly-transparent palette entries (α < 16) snapped to fully
transparent (the octree turned transparent pixels into α 1–5 black, a faint grey square on the white slot)
→ PNG optimize. Banding check on a 10 % sample (PSNR composited on white): mean 37.8 dB, 1st percentile
32.8 dB, worst 30.6 dB (line-art nets, visually fine) → 128 colours kept.

**Numbers (index 2026-11-08: 13 906 pictograms; final pack 2026-11-08)**:

| | before owner exclusions | + aux/art/letter rules | + review overrides (final) |
|---|---|---|---|
| held-out subtitles, % tokens with a picture (excl. ruled-out tokens) | 78.7 | 74.8 | **53.4** (raw, all tokens: 42.3) |
| held-out wiki | 73.1 | 67.4 | **38.9** (raw 30.8) |
| lexicon, frequency-weighted | 75.4 | 72.4 | **52.7** (raw 44.3) |
| forms with a picture | 30 322 | 30 227 | 30 159 |
| distinct pictures | 5 025 | 5 017 | 5 064 |

The plan's coverage gate (75 / 65 / 70 %) passed before the owner's exclusions and failed after them; the
owner accepted the final figures in D4b, so `report` now checks the **D4b floors 53 / 38 / 52 %** — by design: the excluded forms are the most frequent function words (de, et, en, que, pour, dans, plus…,
auxiliaries, articles), whose ARASAAC symbols are abstract. Pack: **5 064 pictures, 30 159 words,
20.2 MB** (PNG 15.0 MB), built in 24 s; fetch of 5 080 images ≈ 31 min. One picture (id 4695) is a 404
on the CDN: `fetch-images` now retries a CDN 404 once on the API URL (500 px), which served it (« goûters »).

**Review (contact sheets, top 300 forms + 40 homographs)**: before review 217 of the top 300 had a picture;
9 were wrong (à → letter Á, se → compass SE, ce → "CE" organisation, y → letter Y, suis → suivre,
temps → weather, quelle → "!", raison → spiral, fils → spool) = **95.9 % right-or-none**; homographs 35/40
with a picture, 2 wrong (fils, temps) + 2 debatable (mine = coal mine, mousse = moss) = 94.3 % (88.6 % if
the debatable ones count as wrong). All of them fixed (rule or override); 42 more abstract symbols removed
(see `picto_overrides.tsv`).

## 8. Grammar morphology (`build_grammar.py` → `grammar.sqlite`, plan 11 / 10D-0)

```
 grammar.lock.json (pins) ──fetch──► <raw>/French.lex, French.tagset.txt, rules.grx, phonet_simil.txt (SHA-256 checked)
 <raw>/French.lex + prediction.sqlite (words) ──build──► out/grammar.sqlite + out/grammar/SOURCE.txt, MPL-2.0.txt
 Lexique383.tsv + out/grammar.sqlite ──mapping-report──► <work>/grammar/mapping-report.txt (report only, never shipped)
```

`prediction.sqlite` keeps one Lexique row per form, without gender, number or person; grammatical prediction needs every
analysis of a form (« est » = être 3s and the noun « est »). The source is Grammalecte's « Lexique simplifié » v7.7
(`lexicons/French.lex`, MPL-2.0; the same pinned file as the spelling reference of § 1b2). `grammar.sqlite` is a separate
file: **MPL-2.0 data derived from French.lex, indexed by the prediction lexicon's spellings** (the `pred_index` keys are
the Lexique 3.83 forms, CC BY-SA 4.0 — 201 of them are not French.lex spellings, e.g. « coeur », « aujourd'hui »). Under
the MPL it is Covered Software (a Modification of French.lex, distributed in Executable Form, § 3.2); Pépite
(GPL-3.0-or-later) is the Larger Work (§ 3.3); its Source Code Form is French.lex at the pinned commit plus
`build_grammar.py`. It ships with `SOURCE.txt` (source URL, commit, SHA-256 of French.lex, of the prediction spellings
list and of the file, counts, the MPL wording) and the MPL text. Only French.lex is read in 10D-0; `rules.grx` (GPL-3.0-or-later) and `phonet_simil.txt` are pinned for 10D-4 / 10D-2.

**Schema 1**:

```sql
meta(key TEXT PRIMARY KEY, value TEXT)            -- schema, source, commit, French.lex SHA-256, prediction_words_sha256
                                                  -- (sorted words.ortho joined by LF), counts, licence, bit layout
lemma(id INTEGER PRIMARY KEY, lemma TEXT)         -- sorted by lemma, id = rank (0-based)
tag(id INTEGER PRIMARY KEY, tag TEXT UNIQUE, bits INTEGER, bits2 INTEGER)   -- tag without its /dictionary part;
                                                  -- bits2 = the second part of speech of « :N:A », « :Q:A », « :G:R:D » (else 0)
morph(form TEXT, lemma_id INTEGER, tag_id INTEGER, PRIMARY KEY(form, lemma_id, tag_id)) WITHOUT ROWID
CREATE INDEX morph_lemma ON morph(lemma_id);      -- lemma → forms (10D-2 / 10D-4)
pred_index(ortho TEXT PRIMARY KEY, a BLOB) WITHOUT ROWID   -- one row per form of prediction.sqlite that French.lex knows;
                                                  -- a = n × (lemma_id u32 LE, MorphBits u32 LE), sorted, OR-merged per (lemma, POS)
```

**Feature bits** (`MorphBits`, uint32, our own layout; `Predict.Core/MorphBits.cs` must match): 0–3 POS (1 N, 2 A, 3 Det
— `:D…` and cardinal numbers `:B` —, 4 subject pronoun `:Os`, 5 other pronoun, 6 finite verb `:Ip/Iq/Is/If/Sp/Sq/K/E`,
7 past participle `:Q`, 8 infinitive `:Y`, 9 present participle `:P`, 10 preposition `:R`, 11 conjunction `:C…`, 12
adverb `:W/X/U`, 15 other: proper names, interjections…); 4–5 gender m / f (both = `:e`); 6–7 number s / p (both = `:i`),
on nominal analyses (N, A, Det, pronouns, Q); 8–13 persons 1s…3p and 14–17 moods I / S / E / K on the finite analysis
(the interrogative `1ś` / `1ŝ` count as 1s, the `!` of `3p!` is dropped); 18 être (`V0e`), 19 avoir (`V0a`), 20
conjugated with être (8th column of the verb field: « aller »), 21 modal (our list: pouvoir, vouloir, devoir, aller,
savoir, falloir — per lemma, so in `pred_index` only, not in `tag`). A tag with two parts of speech gives two analyses.

**Normalisation** (prediction form → French.lex form): as it is, lowercase, « oe » → « œ » only when French.lex has the
result (`coeur` → `cœur`, `coefficient` stays), then `'` → `’` (`aujourd'hui`). Two French.lex lines that differ only by
their dictionary mark (`/M` and `/*`: 79 lines) are one analysis; both 1990 spellings (`évènement` / `événement`) are kept.

**Reproducibility**: rows inserted sorted, `page_size` 4096, `journal_mode` DELETE, `VACUUM`, no timestamp in the file:
two builds give the same SHA-256 (checked: `d0c4122a…8301`, 26.8 MB, 2026-11-15, Python 3.12.9 / SQLite 3.45.3; prediction
spellings list `d9f6c0ca…6d74`). The pinned tarball is kept in `<work>` (`fetch-archive`, SHA-256 `065affe4…a077`) and `fetch
--from-archive` reads the four files from it, so the build does not depend on the mirror staying online.

**Numbers (2026-11-15)**: 515 693 French.lex lines → 515 614 analyses (`morph`), 84 795 lemmas, 6 228 tags (578 with two
parts of speech); `pred_index` 119 849 of the 125 348 prediction forms (the 5 499 others: the 3 836 letter forms French.lex
lacks, as `spelling_unlisted` of `spelling.sqlite`, then 1 644 hyphenated compounds such as « aide-cuisinier » and 19 forms with an apostrophe or a
dot such as « d'abord », « jusqu'à », « etc. »).

**Mapping report** (`mapping-report --lexique`, Lexique 3.83 `cgram` / `genre` / `nombre` / `infover`, every homograph row
of the 119 849 shared forms): POS 0.979 (133 793 / 136 711; Lexique tags many participles and present participles as
NOM / ADJ), gender on N/A 0.998, **number on N/A 0.9996** (gate ≥ 0.95), **person on finite verbs 0.996** (gate ≥ 0.95;
the disagreements are mostly Lexique's, e.g. « abandonner » ind:pre:2p). The report also gives the vacuous share of
each rate, the agreements that hold only because our bits allow every value: gender 0.046 (epicene), number 0.005
(invariable), person 0.000. Exit 1 below a gate.

**10D-1 alternative (measured, not chosen)**: ship no `pred_index` and build the entry index at load from `morph`
(`GrammarLexicon.LoadFromMorph`). The arrays are identical and the file is 23.9 MB. The load takes 0.83 s instead of
0.17 s, with a 166 MB peak working set instead of 92 MB ([evaluation.md](../development/evaluation.md) § Grammar).

**Test sets** (`build_grammar_sets.py`, test text only, in `<work>/grammar-sets/`, never committed): see
[evaluation.md](../development/evaluation.md) § Grammar.

