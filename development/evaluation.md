# Predictor quality benchmark

How we judge whether the open predictor is good. Implemented in
[`src/Predict.Eval`](../src/Predict.Eval); run it as in [BUILD.md](BUILD.md) § 3.

Commit ids cited below refer to the pre-publication history, which is not public.

The tool (`Predict.Eval`) has **two modes**, auto-detected by the second argument:

- **`cases`** (a `.json`) — the curated, hand-labelled cases. Good for *specific* behaviours (phonetic,
  transposition, named context phrases).
- **`heldout`** (a `.txt` of sentences) — a **large-scale** automatic benchmark on text the model was not
  trained on: next-word and completion accuracy by prefix length, plus keystroke savings. Good for
  *overall* quality and for tuning, because it is a number over thousands of predictions.

```
dotnet run --project Predict.Eval -- <db> cases.json
dotnet run --project Predict.Eval -- <db> heldout.txt --max 20000
```

## Large held-out benchmark (the honest overall quality number)

For every word of held-out text, give the model the previous words plus the first *p* letters of the word
and check the rank of the true word. Metrics per *p*: top-1 / top-3 / hit@8 / MRR, plus **keystroke
savings** (KSS — the share of letters you *don't* type if you accept the word as soon as it is the top
suggestion; the standard AAC word-prediction metric). `p = 0` is pure **next-word** prediction from context
alone (`PredictNext`, the grey ghost word a keyboard shows).

Current model — **stupid-backoff LM (4-gram) + tuned weights**, measured on two held-out sets *not in
training*: OpenSubtitles (same conversational domain as part of the corpus) and Wikipedia (a different,
formal domain). The gap between them is the honest spread of real-world quality.

```
                       in-domain (subtitles)        cross-domain (Wikipedia)
prefix     top1  hit@8                   top1  hit@8
next       0.502  0.770                  0.141  0.349     <- context only, before any letter
1 letter   0.765  0.948                  0.362  0.665
2 letter   0.876  0.985                  0.568  0.798
3 letter   0.930  0.996                  0.636  0.846
KSS        78.7 %                        44.2 %
```

Re-measured 2026-11-08 with the current DB (`Predict.Eval <db> <heldout> --max 20000`, first 20 000 held-out
words of each set; identical in the plan-02 and plan-03 runs). The figures first published here (KSS 78.8 % /
45.4 %) came from an earlier run of the same model and are superseded; the 4-gram comparison table below
keeps the numbers of that earlier run.

**Elisions (plan 04, D22, 2026-11-09).** The engine now favours vowel / mute-h words after a bare elision in the
context (`ElisionBoost`, ×8 on the probability; `--elision-boost F` overrides it). Held-out, `--max 20000`, same
sets — KSS overall and on the subset of words that follow an elision (`l d j m n s t c qu jusqu lorsqu puisqu`):

| boost | subtitles KSS | after an elision (1 602 words) | Wikipedia KSS | after an elision (1 000 words) |
|---|---|---|---|---|
| 1 (off, before) | 78.7 % (19 062 / 89 416) | 81.6 % (1 321 / 7 177) | 44.2 % (53 838 / 96 515) | 52.6 % (2 857 / 6 027) |
| 4 | 78.7 % (19 062) | 81.6 % (1 321) | 44.2 % (53 837) | 52.6 % (2 856) |
| **8 (default)** | 78.7 % (19 062) | 81.6 % (1 321) | 44.2 % (53 836) | 52.6 % (2 855) |
| 16 / 32 | 78.7 % (19 062) | 81.6 % (1 321) | 44.2 % (53 836) | 52.6 % (2 855) |

The boost is neutral in-domain and saves 2 letters cross-domain: after `l d qu…` the n-gram tables already list
almost only vowel-initial words, so it only matters when the model backs off to unigrams (rare words). It is kept
because it is a ranking hint that cannot hurt (8 = smallest value reaching the best score). The display mapping
(`l` → `l'`) is applied after ranking, so it does not change these measures. Curated set: 15 new `elision` cases
(`[vers] → l'`, `[vers l'] h → horizon`, `[parce qu'] → il`, `[je] m → m'`, `[l'] hu → huile`…, empty `typed` =
next-word): hit@8 1.00, top-1 0.47; the 31 earlier cases are unchanged (hit@8 1.00, top-1 0.81).

Reading it: on conversational text (the target use case — messages, everyday sentences) the user types
~21 % of the letters; on formal encyclopedic prose, ~55 %. The truth for a given user is between, nearer the
conversational end. Corpus: Tatoeba + Leipzig news 1M + **OpenSubtitles fr** ≈ 5.7 M sentences;
`prediction.sqlite` ≈ 163 MB; 0.26 ms/prediction, 107 MB RAM (within the ≈ 240 MB budget).

### Smoothing + weight tuning (what moved these numbers)

The ranking was changed from a crude blend (`TriWeight·tri + bi`) to a proper **stupid-backoff language
model** — `P(word | prev2, prev)` backing off trigram → bigram → unigram (the unigram from Lexique
frequency) — plus the frequency-gated prefix bonus and the edit penalty. Then the weights were tuned on a
**disjoint dev split** by maximising a fast proxy (top-1 at 1 + 2 letters), with **keystroke savings
re-measured on a held-out test split** (`Predict.Eval … --sweep`):

| | top-1 (next) | top-1 (1 letter) | **KSS** |
|---|---|---|---|
| old crude blend | 0.186 | 0.496 | 56.6 % |
| **backoff LM + tuned** (`WPrefix=1, WEdit=1, Alpha=0.2`) | **0.321** | **0.653** | **69.8 %** |

The smoothing is the dominant gain (KSS ≈ +12 pts); the weight sweep added a further **+0.59 pt** on the
clean test split (69.18 % → 69.77 %). The tuned weights are the engine defaults. The curated set is
unchanged (top-1 0.81, hit@8 1.00) — the backoff did not cost any specific behaviour.

### 4-grams — a big *in-domain* gain, but domain-sensitive

Adding a 4-gram level (3 words of context) was measured with the engine's `--no4gram` toggle on the same
test sets:

| held-out | trigram KSS | +4-gram KSS | real gain |
|---|---|---|---|
| subtitles (in-domain) | 69.8 % | **78.8 %** | +9.0 |
| Wikipedia (cross-domain, unseen) | 45.0 % | **45.4 %** | **+0.4** |

So 4-grams help **a lot** when the text resembles the training corpus and **almost nothing** on novel formal
text — most of the in-domain gain is remembered stock phrases (subtitles repeat them between train and
test). This is exactly why the benchmark now carries a **cross-domain** test: a single in-domain KSS number
(78.8 %) would badly overstate general quality. 4-grams are kept on by default (they help the conversational
use case and cost ~50 MB), but the realistic everyday-writing KSS is between the two columns. The lesson for
a shipped product: **train the n-grams on text like what the user writes**, and tune the weights on that
domain.

## Method (curated cases)

A **case** is `{ category, context?, typed, intended }`
([cases.json](../src/Predict.Eval/cases.json)):

- `typed` — what the user has entered (often a phonetic misspelling), `intended` — the word they meant.
  An empty `typed` asks for next-word prediction from the context; suggestions are compared as the bar shows
  them (elisions with their apostrophe, `Elision.Apply`).
- `category` — `phonetic` (dyslexic/sound spelling) or `completion` (normal prefix).

For each case we ask the engine for its top suggestions and find the **rank** of `intended`. Metrics:

- **top-1** — intended is the first suggestion.
- **hit@8** — intended is in the top 8 (the bar shows up to 8 words).
- **MRR** — mean reciprocal rank (1/rank), rewards ranking it higher.

Aggregated per category and overall, with a grade: **GOOD** (hit@8 ≥ 0.85 and top-1 ≥ 0.55), **FAIR**
(hit@8 ≥ 0.65), else **POOR**.

## Score — v4 (trigram context + two phonetic keys + **transposition/edit feature**)

(MRR re-measured 2026-11-08 with the current 4-gram DB: completion 0.70, overall 0.89; top-1 and hit@8 unchanged.)

```
category        cases   top1   hit@8   MRR
completion          6   0.50   1.00    0.70
context2            5   1.00   1.00    1.00
phonetic           14   0.86   1.00    0.92
transposition       6   0.83   1.00    0.92
overall            31   0.81   1.00    0.89   => GOOD
```

**Reading it.** The intended word is in the top 8 on **every** case (hit@8 = 1.00), top-1 0.81.
The remaining non-top-1 cases are reasonable (`tortu→torture` outranks `tortue` by frequency — a labelling
nuance).

### What changed, step by step (each verified by the benchmark)

| step | change | fixed |
|---|---|---|
| v2 | **bigram** context `P(word\|prev)` (Tatoeba) | `sou→souris`, `bo→beau` |
| v3 | **trigram** context `P(word\|prev2,prev)` (+ Leipzig news 1M; 1.73 M sentences) | 2-word-context cases |
| v3 | **2nd phonetic key** from Lexique's `phon` (real pronunciation) | `famme→femme` (irregular) |
| v3 | **frequency-gated** literal-prefix bonus | `oto→auto` |
| v4 | **transposition / edit-distance feature** (Damerau-1, on by default) | all letter-inversions: `lbeu→bleu`, `porbleme→problème`, `jadrin→jardin`, `bojnour→bonjour`, `masion→maison`, `oridnateur→ordinateur` |

Ranking: `log10(freq+1)` + `CtxWeight·log10(TriWeight·tri + bi + 1)` + `PrefixBonus·gate(freq)`, with
edit-distance candidates added at `−EditPenalty` (they rank below clean matches but still surface). Context
counts settle ties — `une souris` 105 ≫ `une sous` 5, `fait beau` 81 > `fait bon` 32.

### Transposition / inversion

Prefix and sound-alike matching alone catch a transposition only when the mangled word still begins
correctly or still sounds close, and miss the rest (`lbeu→bleu`). The engine therefore adds an explicit
**Damerau distance-1** branch (transposition, deletion, substitution, insertion) over an accent-insensitive
lexicon index — **on by default** — so letter inversions are handled as a first-class feature
(`lbeu→bleu`, `jadrin→jardin`, `oridnateur→ordinateur`).

## Personal words (plan 10A, 0.8.0, 2026-11-14)

Commands (`Predict.Eval`, same DB and sets as above; `<work>` = the local bulk-data folder):

```
Predict.Eval <db> cases.json | heldout_subs_40k.txt | heldout_wiki_8k.txt [--max 20000]           # no list: identical to 0.7
Predict.Eval <db> <set> --user personal-fixture.json [--oov-typed]                                 # an unrelated class loaded
Predict.Eval <db> cases-personal.json --user personal-fixture.json                                 # names, lessons, « Mes fautes »
python -I src/Data.Build/build_lesson_set.py <work>/lesson-set-wiki                                # 20 Wikipedia articles
Predict.Eval <db> <work>/lesson-set-wiki --lesson-set [--sweep-user --cases … --user … --neutral a.txt,b.txt]
Predict.Cli <db> --bench-user                                                                      # speed, 0 / 1 000 / 15 000 words
```

`personal-fixture.json` (committed, invented): 25 first names (one hyphenated), a school, a hyphenated town, two lessons
(volcanoes, digestion: 30 words) and 20 « Mes fautes ». `cases-personal.json`: each fixture word typed with 1 and 2
letters in a short context (names with their capital), each pair's spelling and its first 3 letters, and 22
sentence-start words typed with a capital (« Le », « Nous »…: they must not lose to the names). The **lesson-domain
set**: 20 French Wikipedia articles on school topics (volcan, dinosaures, système solaire, Égypte antique… — titles,
revision ids and SHA-256 in `<work>/lesson-set-wiki/manifest.json`; Wikipedia is not in the training corpora); the first
half of each article (≤ 10 000 characters) is the lesson pasted into `LessonExtractor` (its default ticks: 8 136 list
words in all), the second half the test text; KSS with `--oov-typed` (a word the engine cannot propose costs all its
letters, so both runs count the same words). A first, keyword-selected set (sentences of Leipzig `fra_wikipedia_2021_1M`
mentioning the topic) was dropped: the two halves shared too few rare words (0.4 % of the test letters were new words
of the list, against 1.8 % for the articles).

**Weights** (`--sweep-user`, dev = even topics, test = odd topics; neutrality on the first 2 000 words of each held-out set
with the fixture). Three scoring forms were tried:

| Form | lesson gain (dev / test) | names top-1 ≤ 2 letters | Wikipedia loss with the fixture |
|---|---|---|---|
| log bonus `+WUser` (× 10 / × 100 on the probability), keyword set | +0.75 / +0.50 (WUser 1, from 2 letters); −0.7 / −1.0 (WUser 2) | 0.21–0.37 | −0.07 … +1.00 pt |
| mixture `P + λ/N` (share of the list), keyword set | −0.6 … −1.8 | 0.56–0.63 | +0.46 … +1.82 pt |
| **unigram-level lift `max(s, α³(uni + WUser·w))`**, article set | **+0.99 / +0.80** (3e-4), +0.71 / +0.56 (1e-3), +0.36 / +0.11 (3e-3) | 0.39–0.44 | +0.10 … +0.13 pt |

The log bonus lifts a list word over context (n-gram) predictions and over its own inflections (« éruption » boosted,
« éruptions » typed); the mixture gives each word of a short list (names) a huge share. The kept form lifts only the
unigram level: it never displaces a context prediction. Names (stored with a capital) get `× NameCapitalLift` when the
pupil starts with a capital and `× NameLowercaseShare` (0.1) otherwise. Chosen: **`WUser` 1e-4, `NameCapitalLift` 30,
`NameLowercaseShare` 0.1, `WSpelling` 3, `UserUnigram` 1e-6** (lesson gain +0.98 at 1e-4, +0.91 at 3e-5; names 0.386 and
sentence-start words unchanged at 30, 0.318 at 100).

**Results** (final build):

| Measure | Target (plan) | Result | |
|---|---|---|---|
| No list: `cases.json`, subtitles, Wikipedia | identical to 0.7.2 | identical (output byte for byte, timings aside): 0.70 / 1.00 GOOD; KSS 78.7 % (19 062 / 89 416), 44.2 % (53 836 / 96 515) | met |
| Unrelated fixture loaded: KSS loss | ≤ 0.1 pt each | subtitles **−0.02** (19 041: a gain), Wikipedia **+0.06** (53 897); `cases.json` unchanged (0.70 / 1.00) | met |
| The same with the text's capitals kept (`--keep-case`: each word typed as written, a word after `. ! ? …` is a sentence start) | ≤ 0.1 pt each | without a list identical to the lowercase runs (19 062 / 53 836); with the fixture subtitles **−0.02** (19 041), Wikipedia **+0.07** (53 901) | met |
| Lesson-domain set, `--oov-typed` | ≥ +2.0 pts (reject < +1.0) | **+0.98 pt** (36.11 % → 37.09 %, 93 151 → 91 720 of 145 793 letters) | **not met, accepted by the owner 2026-11-14** |
| `cases-personal.json`: names / lesson words top-1 within 2 letters | ≥ 0.8, hit@8 = 1.0 | **0.386** pooled — names at 2 letters top-1 0.67, hit@8 1.00; lesson words at 2 letters top-1 0.04, hit@8 0.19 | **not met, accepted by the owner 2026-11-14** |
| « Mes fautes » exact → top-1 | 1.0 | exact 1.00 (20), 3-letter prefix 1.00 (18) | met |
| Sentence-start words (capital typed: no context, or after « Il pleut. », « Bonjour ! »…; 30 cases) | not worse than without a list | top-1 0.53 / 0.43 / 0.50 (1 letter, 2 letters, after a sentence) — the names are not lifted by a sentence-start capital | met |

Why the two gaps: the oracle of the article set caps what a list can give — words of the list that are new to the
lexicon are 1.8 % of the test letters and rare known ones (rank > 20 000) 7.7 %; lifting known words further costs more
(context predictions and inflections) than it saves. The personal cases put each lesson word after a short context that
strongly predicts common words (« on parle du » → « mal », « marché »), and several list words share their first two
letters (« ma »: Maël, magma, magmatique) — only one can be first. Reaching 0.8 would need the list to override context
predictions, which the neutrality and lesson measures above reject. Next measured steps (not in 10A): a next-word
(n-gram) bonus for list words after their own lesson context, lemma families (lift « éruptions » with « éruption »).

**Speed** (`Predict.Cli --bench-user`, this PC, quiet: **one** set of 2 000 queries — prefixes of 1–7 letters, half from
the 15 000-word list, half common inputs, context « il fait » — run with 0, 1 000 and 15 000 words; 200 warm-up queries,
3 rounds; rounds 2–3 shown, round 1 in brackets):

| User words | Build | Snapshot (managed) | mean | p50 | p95 | max |
|---|---|---|---|---|---|---|
| 0 | — | — | 0.32 ms (0.52) | 0.09 | 1.20–1.21 (2.07) | 17–23 ms |
| 1 000 (983 forms + 466 pairs) | 1.5–1.6 ms (4.1) | +0.3 MB | 0.39 | 0.11 | 1.42–1.43 | 16–19 |
| 15 000 (10 718 forms + 466 pairs) | 19–20 ms (30.3) | +2.6 MB | 0.45 | 0.13 | 1.74–1.77 | 16–18 |

On the same queries 15 000 words add **≈ 0.55 ms to the p95** and nothing to the max (which comes from the engine's
1-letter prefixes, as with no list). The plan's absolute gate (p95 ≤ 0.6 ms, max < 5 ms) is not met by the 0.7 engine
itself on this PC; the personal pass adds the 0.55 ms. A short prefix over thousands of new words scores only the 32
heaviest / shortest of them (`ShortlistSize`: new words the context never saw all score alike): without it a 1-letter
prefix over 10 000 class words took 15–16 ms in the VM.

**Snapshot build**: 19–20 ms warm, 30 ms the first time, for 15 000 words (gate ≤ 30 ms: met warm). Since the review
fixes the app reads the files and builds the snapshot **on a worker thread** and only swaps it in on the UI thread; before
that the 0.8.0 VM run measured 164 ms at start (three files read) and 144 ms on the first switch to a pupil, on the UI
thread. The last two snapshots are kept, so going back and forth between two pupils reuses them (VM: 1–5 ms).

**Memory** (xUnit `Word_Store_Memory_With_15000_Words`, managed heap after a full GC): three word files (« Classe » 10 000 +
two pupils × 5 000) loaded and both pupils' 15 000-word snapshots cached = **+9.9 MB** (13.9 MB before the review fix that
drops each file's JSON tree once read). The plan's gate is ≤ +5 MB working set: **not met, accepted by the owner 2026-11-14** (re-measure if more than two
snapshots are ever cached) for 20 000 stored words and two
cached snapshots (one 15 000-word snapshot alone is +2.6 MB). **App, VM recheck** (final build, the same steps three
times — start, switch Léa → Tom → Léa, 20 s idle — with no word file, with « Classe » 10 000 + Léa 5 000 + Tom 5 000, and
with no word file again): working set 283 / 267 / 285 MB, private bytes 263 / 266 / 253 MB. The difference with 15 000
words (+3 MB private against the first run, +13 MB against the third) is within the run-to-run spread (10 MB between
the two runs without words); the plan's ≤ +5 MB gate cannot be confirmed or refuted at that resolution. (The 917 MB seen
at the end of the first W9 run came after long typing bursts and many switches, with no same-run baseline.)

## Spelling indicator (plan 10B, 0.8.1, 2026-11-14)

```
Predict.Eval <db> spelling-cases.json --spelling [--user personal-fixture.json]
```

`spelling-cases.json` (committed, 275 cases written for this test): each case is a short text typed from the start of a
document; the mark of its last word is computed as the keyboard does (`SpellingIndicator.LastWord` gives the same finished
word as the keyboard's tracker — an xUnit test types every case into `WordTracker` and compares) and compared with the
expected one. Categories: correct lexicon words in sentences (64, with accents, inflections, a capital at a sentence start,
punctuation after them), **phonetic misspellings** of known words (75: silent letters dropped or added, o/au/eau, s/ss/c/ç,
an/en, ai/è, f/ph, k/qu/c, g/j, h dropped, double letters — « maizon », « famme », « abitude », « kado », « bocou »), accents
missing (15), numbers, acronyms, proper names, elisions, hyphenated words, punctuation and addresses, random letters, typos,
homophones that are real words, and the word being typed.

| Category | Cases | ✓ | ≈ | ? | none | as expected |
|---|---|---|---|---|---|---|
| lexicon (correct words) | 64 | 64 | 0 | 0 | 0 | 64 |
| phonetic misspellings | 75 | 5 | 62 | 8 | 0 | 62 |
| accents missing | 15 | 0 | 15 | 0 | 0 | 15 |
| elisions (« l'école », « l'ecole », « aujourd'hui », « l' ») | 18 | 11 | 2 | 0 | 5 | 18 |
| hyphenated (« peut-être », « Jean-Pierre », « a-t-il ») | 10 | 0 | 0 | 0 | 10 | 10 |
| numbers (« 3pommes », « le 2e », « 10h », « J'ai 3 pommes ») | 11 | 2 | 0 | 0 | 9 | 11 |
| acronyms (« SNCB », « la SNCF ») | 8 | 0 | 0 | 0 | 8 | 8 |
| proper names (« Bruxelles », « Emma », « avec Léa ») | 17 | 2 | 0 | 0 | 15 | 17 (never red) |
| punctuation, addresses (« www. », « https://www.ecole.be », « ecole. »), line ends (« la maizon⏎ ») | 26 | 12 | 4 | 0 | 10 | 26 |
| random letters (« xqzt ») | 7 | 0 | 0 | 7 | 0 | 7 |
| typos (« maisno », informational) | 5 | 0 | 1 | 4 | 0 | 4 |
| homophones that are words (« ver », « foret », informational) | 7 | 7 | 0 | 0 | 0 | 7 (✓ by construction) |
| the word being typed (« mai », « mais » ✓; « maiz », « SNC » none) | 12 | 5 | 0 | 0 | 7 | 12 |

| Gate (plan 10B) | Target | Result | |
|---|---|---|---|
| Correct lexicon words (and every number, acronym, name, elision, hyphen, punctuation case) red | 0 | **0** | met |
| Every finished word before the last one along each case (317 judged as typed: « www. » before « ecole », « l » before « école »…) red | 0 | **0** (✓ 244, ≈ 1, none 72) | met |
| SoundsLike recall on the phonetic misspellings | ≥ 0.8 | **0.827** (62 / 75; 0.886 = 62 / 70 without the 5 misspellings that are lexicon forms: « tortu », « souri », « nui », « kan », « fau ») | met |
| No list / indicator off: `cases.json`, subtitles, Wikipedia identical to 0.8.0 | identical | `cases.json` output **byte for byte** identical to a 0.8.0 build; KSS **78.7 % (19 062 / 89 416)**, **44.2 % (53 836 / 96 515)**, after-elision 81.6 % / 52.6 % — the recorded 0.8.0 numbers (the engine's `Predict` / `PredictNext` are unchanged; `Check` is a separate call) | met |

The intended word is the first candidate for 50 / 75 and among the candidates for 60 / 75 (what a click on ≈ shows). Misses
(red instead of ≈): « pharmasie », « ivert », « samdi », « bisiclette », « cocho », « profeseur », « zoizo », « pingoin » —
the grapheme fold differs (s between vowels → z, a dropped or added final consonant) and no Lexique pronunciation matches
the *whole* key. A typo is red, not ≈ (« maisno »; « chein » gets ≈ « chaîne » from the fold). With `--user
personal-fixture.json` the fixture's names become ✓ (9 of 17 names cases) and nothing else changes.

**Proper names** (decided in 10B): an unknown word with a capital is never marked. A first version kept ≈ at a sentence
start (« Maizon » → ≈ maison); the names cases then showed « Emma » ≈ « aimât » and « Inès » ≈ « in » — dropped.

**Review fixes (0.8.1)**: a word finished by « . » / « : » is only ✓ until a space follows (« www. » and « https: » were red
before, « ecole. » ≈ for one key); a fragment typed after a jump the keyboard could not read is not marked; the Eval now
judges every finished word of each case, not only the last; « ca va » became « ca » (the case was about « ca »).

**Speed** (`Check` over the 271 case words × 200 = 54 200 calls, this PC, after the review's rewrite without LINQ / closures):
mean **1.4 µs**, p99 **7.6 µs**, max 0.9–1.1 ms (0–1 call over 1 ms), **2 gen-0 collections and none of gen 1 / 2** during the
loop, ≈ 245 bytes allocated per call. The first version (LINQ) measured mean 2.4 µs, p99 11 µs and single 9–13 ms outliers;
their cause was not measured then (GC counts were not recorded), so it stays unverified. In the app the check runs on the UI
thread once per update while a finished word is kept; VM figures: development/keyboard.md § Spelling indicator.

## Names pack (plan 10C, 0.8.2, 2026-11-14)

```
Predict.Eval <db> <cases.json | heldout.txt | spelling-cases.json --spelling> [--names <noms folder>] [--names-twice]
Predict.Cli <db> --bench-names <noms folder>
```

The « Noms propres » pack, edition 2026.1 (`src/Data.Build/pepite-noms/`, D30): **31 299 names** after the 2026-11-15
rebuild (Statbel newborns, places below first names; 6 `restreint`); the breakdown below is of the first build (31 297):
29 089 first names
(4 964 predicted, 24 125 recognised only), 247 countries + 7 continents, 3 + 9 + 560 Belgian regions / provinces /
municipalities, 14 + 96 French regions / departments, 901 towns, 371 historical figures; 258 flags. Folder **2.88 MB**
(`noms.json` 2.6 MB + 258 PNG), zip **0.46 MB**. In the engine: 32 110 indexed forms (names, variants, parts).

| Measure (gate, plan 10 / study 10c) | Result | |
|---|---|---|
| No pack: `cases.json`, subtitles, Wikipedia identical to 0.8.1 | `cases.json` output identical; held-out results identical to the 0.8.1 build of HEAD `3c6c148` (KSS **78.7 % (19 062 / 89 416)**, **44.2 % (53 836 / 96 515)**, every rate line equal) | met |
| Pack loaded, held-out KSS loss ≤ 0.1 pt | subtitles **78.7 % → 78.7 %** (19 062 → 19 062 letters); Wikipedia **44.2 % → 44.2 %** (53 836 → 53 844 letters: −0.01 pt; top-1 / hit@8 at 3 letters 0.636 → 0.636 / 0.846 → 0.845) | met |
| Pack loaded, `cases.json` top-1 loss ≤ 0.02, hit@8 unchanged | output identical to the run without the pack | met |
| `cases-personal.json` with `personal-fixture.json` | identical with and without the pack; since the capital bonus (`NameCapitalBonus` 2.5, which also lifts capitalised list words without the pack) top-1 within 2 letters **0.474** (0.8.1: 0.386), « Mes fautes » 1.000, sentence-start words 0.500. « No pack = 0.8.1 » holds for profiles without capitalised list words | met (improved) |
| `--spelling`: 0 wrong reds, no name ≈ | wrong reds **0** (and 0 among the 371 inner words); **87 name cases** (17 of 10B + 70 new): ✓ 86, ≈ **0**, ? 0, none 1 (« Mme Dupont »: a surname, not in the pack). Without the pack: ✓ 14, ≈ 6 | met |
| Sentence-start misspellings (35 new cases, « Maizon », « Bocou »…) | with the pack ≈ **30 / 35 = 0.857** after the owner decision of 2026-11-15 (« Pome », a very rare name, is now ≈ pomme at a sentence start; no name case flipped: 86 ✓, 0 ≈), red 0; misses « Kan » (✓: a Lexique form, not a name), « Samdi », « Cocho », « Profeseur », « Zoizo » (no mark: the same fold misses as in lowercase, 10B); without the pack 30 / 35 | reported |
| Phonetic recall (10B gate ≥ 0.8) | 62 / 75 = **0.827** (unchanged); intended word first 49 / 75 (50 without the pack) | met |
| Memory ≤ +15 MB | `Predict.Cli --mem-names`, 3 fresh processes (identical runs; .NET 8 runtime on this host, the app ships .NET 6): parse **+4.1 MB managed** (records, freed after the build), layer **+4.9 MB managed kept**; after a compacting GC the process keeps **GC committed +8.8 MB, private bytes +14.7–15.6 MB (median 14.9), working set +16.3–16.5 MB (median 16.3)**. Attribution: 4.9 MB the layer; ≈ 3.9 MB GC memory committed for the parse's peak and not given back; ≈ 6 MB outside the GC heap (JIT code of the parser and the build, first use of the JSON reader). The first parse used `JsonNode` (+30.8 MB managed), then `JsonDocument` (+19.9), now a forward-only `Utf8JsonReader` with no document. VM (app, .NET 6): +12.2 MB in one session, not reproducible in a second (the guest trimmed working sets) | private bytes at the limit before the streaming load (working set +16.3 MB, 1.3 MB over). **After the streaming load** (owner request 2026-11-15: one pass straight into the layer, no record list; `--mem-names-stream`, 3 fresh processes): managed +4.9 MB, GC committed +8.0 MB, **private +13.4 / 14.0 / 14.4 MB (median 14.0)**, **working set +15.4 / 15.5 / 16.0 MB (median 15.5)** — 0.5 MB over the gate on the working set, within it on private bytes | **working set: not met (+15.5 MB), accepted by the owner 2026-11-15**; a later reduction is in TODO.md |
| Prediction speed unchanged | `--bench-names` after the review rewrite (no context, tier key arrays, the best k + 4 picked by frequency): rounds 2–3 mean **0.330 → 0.359 ms** and **0.343 → 0.344 ms**, p95 1.10–1.12 → 1.14–1.15 ms (round 1 is warm-up). Before the rewrite: +0.03–0.05 ms (+11 %) | within noise of 0.8.1 on one round, +9 % on the other |
| Load time | 0.33–0.44 s on a worker thread (parse + build), then one compacting GC (60 ms in the VM) | — |

**Names typed with their capital** (`--keep-case --oov-typed`, 20 000 words, a capitalised word the pack knows counts as
predictable): Wikipedia KSS **39.5 % → 40.3 % (+0.8 pt)** (coverage 91.2 % → 94.3 %); subtitles **74.6 % → 76.7 % (+2.1 pt)**
(coverage 96.2 % → 98.8 %: first names). The first version, which took the names' own context counts, gave +1.1 pt on
Wikipedia but broke the pack's ranking (« je vois ca » → Casey 3rd; « avec Jo » → John, Joey, Johnny without Joseph;
« avec Lé » → Lénine before Léa): the reviewer's fix makes names unigrams only.

**Capital bonus** (VM recheck 2026-11-15): `NameCapitalBonus` 2.0 (log10; **2.5** since the first-names-first pack, below) for names — pack and lists — after a capital typed
inside a sentence (bonus 0 / 0.5 / 1.0 / 1.5 / 2.0 on `cases-personal` with the pack, pack names alone: 0.386 / 0.386 / 0.351 / 0.193 / 0.053;
with the bonus on the lists' names too, 2.0: **0.439** with or without the pack). Keep-case KSS with the pack: subtitles
**74.6 → 76.7 %**, Wikipedia **39.5 → 40.5 %**; `cases.json`, held-out lowercase and `--spelling` unchanged (no capital
inside a sentence there; spelling: 0 wrong reds, names ✓ 86 / ≈ 0, sentence-start recall 0.857).

**First names first** (owner 2026-11-15; pack rebuilt, zip `6f87d5d6…`, `noms.json` `5f0646f5…`): places scaled below the
1 000th predicted first name, Belgian first names by newborns 2016–2025 (Statbel). Ranks (`rang`): Joseph 0.292 > Johan 0.199
> Jonas 0.188 > Johannesbourg 0.130 > Jozef 0.056 (before: Johannesbourg / Cali 1.0, Johan 0.571, Jozef 0.442, Joseph 0.419);
Camille 0.661 > Cali 0.130; Belgique 0.081; Ypres 0.039. With the bonus at 2.0, « avec Jo » gave **joie**, Joseph (the
towns no longer pushed « joie » out), so the bonus went to **2.5** (sweep: 2.5 and 3.0 give the same results here):

| Measure (with the pack) | Round 5 (bonus 2.0, old pack) | Final (bonus 2.5, new pack) |
|---|---|---|
| `cases.json` | = without the pack | = without the pack (and the no-pack run = round 5) |
| Held-out KSS subtitles / Wikipedia | 78.7 % (19 062) / 44.2 % (53 844) | 78.7 % (19 062) / 44.2 % (**53 836** = without the pack) |
| Keep-case KSS subtitles / Wikipedia | 76.7 % (21 148) / 40.5 % (58 965) | 76.7 % (21 125) / 40.5 % (59 013) |
| `cases-personal` names top-1 within 2 letters (with / without the pack) | 0.439 / 0.439 | **0.474 / 0.474** |
| `--spelling` | 0 wrong reds, names ✓ 86 ≈ 0, recall 0.827, start 30/35 | identical |
| Probe (« avec Ad / Jo / d'Em / de Hit / Ca », « en Belg », « à Ypr ») | Adam 6th, Joseph 6th, Emma 1st, Hitler 1st, Cali … | **Adam, Joseph, Emma, Hitler, Camille 1st**; Belgique 2nd (after Belgrade); Ypres 1st |

At a sentence start and in lowercase the first names that are common words stay out of the bar (« Ca » → car, cas…;
« Ro » → route, …, rose; « lou » → loup, …): no exclusion from tier 1 was needed.

**Ranking** (defaults, no sweep): name frequency cap 5 per million × `rang`, lowercase share 0.1, capital lift 30, 2
letters without a capital, rare-homograph merge ≤ 1 per million; historical figures scaled below the lowest predicted first
name by the builder (« Ad » → Adam before Adolf); vowel-initial names take the elision boost (« d'Em » → Emma).

## 0.8.3 fixes (2026-11-15)

Same commands as above; « before » = the 0.8.2 build (HEAD `d828bdd`, its database and its pack), « after » = 0.8.3 (the
database with `spelling_unlisted`, the rebuilt pack). Every run with the pack (`--names`), as the app.

| Measure | 0.8.2 | 0.8.3 | |
|---|---|---|---|
| `cases.json`, the 46 earlier cases | top-1 0.70, hit@8 1.00 | **identical** (line for line) | met |
| `cases.json`, 11 new `sounded-e` cases (« si je fè » → fais, « je mè » → mets, « jamè », « tré », « sé » → ces / ses, « alé » → aller / allé, « parlé », « il fé » → fait, « tu fé » → fais) | hit@8 5 / 11, top-1 5 / 11 | **hit@8 11 / 11, top-1 8 / 11** (« mè » → mets 2nd after mère, « sé » → ses 2nd, ces 5th) | improved |
| Held-out KSS subtitles | 78.7 % (19 062 / 89 416) | 78.7 % (**19 088**) | −0.03 pt |
| Held-out KSS Wikipedia | 44.2 % (53 836 / 96 515) | 44.2 % (**53 856**) | −0.02 pt |
| Keep-case KSS subtitles / Wikipedia (`--keep-case --oov-typed`) | 76.7 % (21 125) / 40.5 % (59 013) | 76.7 % (**21 164**) / 40.5 % (**58 992**) | −0.04 / +0.02 pt |
| `cases-personal` (names / lesson words top-1 within 2 letters) | 0.474 | **0.474** (« Mes fautes » 1.000, sentence starts 0.500) | met (gate ≥ 0.474) |
| `--spelling` wrong reds (all cases, 387 inner words) | 0 | **0** | met |
| SoundsLike recall on the 75 phonetic misspellings | 62 / 75 = 0.827; intended first 49, among the candidates 60 | 62 / 75 = 0.827; intended first **51**, among **62** | up (first / among) |
| 11 new sounded-e misspellings (`--spelling`) | ≈ 11 / 11 | ≈ 11 / 11 | — |
| 11 new reference cases (« mam », « yeah », « team », « till » not ✓; « coeur », « oeil », « soeur », « oeuf » ✓) | ✓ 10 (« mam », « yeah »… were ✓) | ✓ 4, ≈ 4, ? 2, none 1 — 11 / 11 as expected | met |
| Names: 87 cases ✓ / sentence-start misspellings | ✓ 86, ≈ 0 / 30 of 35 | ✓ 86, ≈ 0 / 30 of 35 | unchanged |
| `Check` speed (81 600 calls) | mean 1.8 µs, p99 10.1 µs | mean 1.7 µs, p99 7.3 µs | — |
| `Predict.Cli --bench` (20 000 predictions, alternating runs) | 0.195 / 0.197 ms | **0.182 / 0.185 ms** | faster |
| `Predict.Cli --bench-names` rounds 2–3, pack on (two alternating runs) | 0.329–0.348 ms, p95 1.10–1.15 | 0.366–0.374 ms, p95 1.21–1.28 | **+7 to +13 %** |
| Engine load | 778–784 ms | 726–756 ms | — |

**Pack rebuilt** (same edition 2026.1, same 31 299 names, only the ranks of about 1 820 places changed): zip `a03421a8…`, `noms.json`
`41e3b2b5…` (`build-report.json` in the build output); « né en Bel » → Belgique, Belize, Belo, Belo Horizonte…
(0.8.2: Belo, Belo Horizonte, Bella, Belém, Belgrade, Belgique).

**Why no KSS gain from the sounded e**: the held-out sets are correctly spelled, so the fix cannot help them; it costs a
little because the sound pass now also runs for prefixes ending in a sounded e that used to fold to one symbol (« deu »
→ `de`, « pé » → `pe`: before, `d` / `p` and no sound pass), and a sound-alike sometimes beats the literal completion
with the context (« sur deu » → des before deux). A per-word diff of the Wikipedia run (`--dump`): 109 words worse by 136
letters, 108 better by 116. The same reason explains the names bench (« vé », « Be » now have a sound pass: more
candidates scored); the `--bench` set got faster because a candidate already scored (a literal prefix, or its other sound
key) is no longer scored again — a change that leaves every result identical (the held-out and curated outputs are equal
before / after it). Absolute cost +0.03 ms per prediction, far below the 5 ms target. One phonetic case lost its ≈:
« ausi » was ≈ « osier » only because both keys dropped osier's sounded final e (now red: no French word sounds like it);
« il fau » (a Lexique form missing from the reference) is ≈ faut now, so the recall count stays 62.

**Review fixes (final run, `final083-*`)**: « œ » is folded after the c / g rule (« cœur » and « coeur » share one key,
`keur` — in 0.8.2 `cœur` folded as `ser`): every result identical except keep-case Wikipedia 58 994 → 58 992 letters;
`Predict.Eval` prints `spelling reference: 3836 unlisted` (or `absent`), the app logs the same line at load, and
`size_report.py` refuses a release whose `prediction.sqlite` lacks the table.

**Capital after « : », « « » or an unknown context, names at most half the bar, sound-alikes after a capital**: with
« Lé » keeping its sounded e, « avec Lé » first gave « les » (its Lexique key `le`) before Léa (`cases-personal` 0.456);
a sound-alike common word now loses `NameCapitalBonus` when the word starts with a capital inside a sentence (back to
0.474). `--keep-case` counts « : « " — » as sentence starts like the keyboard (`--old-starts`: . ! ? … only).

## Grammar (10D-0 baseline, 2026-11-15)

Plan 11, 10D-0 delivers the measuring tools and the **baseline of the 0.8.3 engine**, which has no grammar term yet; 10D-1
adds one. Every run has the names pack (`--names`), as in the app, and the morphology table loaded but not used for
ranking (`--grammar out\grammar.sqlite --no-grammar-rank`). The engine refactor of 10D-0 (`_idxOf`, the `Grammar` slot)
changes no result: the output of `cases.json` and the held-out runs (`--dump`, with and without the pack, plus keep-case
Wikipedia) is byte-identical to the 0.8.3 build (HEAD `f4accae`). KSS stays 78.7 % (19 088 / 89 416) on subtitles and
44.2 % (53 856 / 96 515) on Wikipedia.

**Sets**

| Set | Where | Size | SHA-256 |
|---|---|---|---|
| P-agreement `grammar-cases.json` (invented; 13 categories × 17 cases, `fam-ces` 9 for information, 20 precision cases — 26 since the 10D-1 review; dev = even ids, test = odd ids) | `src/Predict.Eval/grammar/` | 250 cases (256 since the 10D-1 review) | `b0eb1d4e…cb33` (250); `4579064e…bdce6` (256, after the 10D-1 review) |
| G-invented `g-invented.jsonl` (invented, CE2–6e level, P-agreement categories, 100 sentences with 2–4 errors) | `src/Predict.Eval/grammar/` | 400 sentences, 525 errors | `55f095fe…3b33` |
| G-synthetic `g-synthetic.jsonl` (Wikipedia + Vikidia school articles, same-sound injection, seed 20261115) | `<work>\grammar-sets\` (never committed) | 5 000 sentences, 5 934 errors | `bcd84b5f…a7a0` |
| G-clean `g-clean.jsonl` (3 606 untouched Wikipedia + 1 394 Vikidia sentences, plus 2 500 + 2 500 held-out sentences; for the 10D-4 false-alarm count) | `<work>\grammar-sets\` | 10 000 sentences | `ab4c6803…57f7` |
| `manifest.json` (117 titles per site with revision ids and timestamps; licences read from each site's `rightsinfo` on 2026-11-15: CC BY-SA 4.0 and CC BY-SA 3.0; terms pages; held-out SHA-256s; seed) | `<work>\grammar-sets\` | — | `004537a9…1bfb` (records grammar.sqlite `d0c4122a…8301`) |

Synthetic classes: `num` 2 138, `family` 1 489, `person` 1 001, `qy2p` 557, `qgn` 413, `gender` 336. A substitution is either
a form of the same French.lex lemma with the same Lexique pronunciation, or a member of the same homophone family. The builder
never uses a word that is both a noun and a verb, or that is also a function word. It never touches capitalised words inside
a sentence or parts of hyphenated compounds. Two builds from the same `raw\` give the same files. **A re-fetch gives other text** (the articles keep changing): keep
`<work>\grammar-sets\raw\` to reproduce the SHA-256s. G-clean is not run in 10D-0: it counts the false alarms of the
10D-4 checker.

The two committed sets are written by `src/Predict.Eval/grammar/gen_cases.py` and `gen_invented.py` (GPL-3.0-or-later,
our own sentences in a compact table): `python -I -X utf8 Predict.Eval/grammar/gen_cases.py <out>` reproduces
`grammar-cases.json` byte for byte, and `gen_invented.py` reproduces `g-invented.jsonl`; edit the generator, never the output.

**Commands**

```
Predict.Eval <db> Predict.Eval\grammar\grammar-cases.json --grammar-predict --split dev|test|all [--json out.json] --grammar <g> --no-grammar-rank --names <noms>
Predict.Eval <db> Predict.Eval\grammar\g-invented.jsonl --grammar-predict  …        (same for <work>\grammar-sets\g-synthetic.jsonl)
Predict.Eval <db> heldout_subs_40k.txt --siblings --max 20000  …                     (same for heldout_wiki_8k.txt)
Predict.Eval <db> Predict.Eval\grammar\grammar-cases.json --sweep-grammar --heldout <subs>,<wiki>  …
```

**P-agreement** (each cell is top-1 / hit@8):
- p0 to p3: number of letters of the intended word typed; p0 = next-word prediction.
- swap: the wrong sibling is typed in full.
- pooled: top-1 over p0 to p3.

| Category | p0 | p1 | p2 | p3 | swap | pooled top-1 |
|---|---|---|---|---|---|---|
| det-num | 0.06 / 0.06 | 0.06 / 0.35 | 0.24 / 0.82 | 0.59 / 0.88 | 0.94 / 0.94 | 0.24 |
| adj-gender | 0.06 / 0.24 | 0.29 / 0.82 | 0.59 / 1.00 | 1.00 / 1.00 | 0.88 / 0.94 | 0.49 |
| post-adj | 0.00 / 0.35 | 0.53 / 0.71 | 0.59 / 0.82 | 0.71 / 0.88 | 0.71 / 0.94 | 0.46 |
| subj-verb | 0.00 / 0.06 | 0.24 / 0.59 | 0.35 / 0.76 | 0.71 / 0.88 | 0.29 / 0.59 | 0.32 |
| noun-subj | 0.00 / 0.12 | 0.18 / 0.47 | 0.35 / 0.53 | 0.41 / 0.53 | 0.35 / 0.47 | 0.24 |
| etre-part | 0.12 / 0.24 | 0.24 / 0.47 | 0.29 / 0.65 | 0.47 / 0.82 | 0.53 / 1.00 | 0.28 |
| avoir-ppas | 0.00 / 0.06 | 0.18 / 0.59 | 0.47 / 0.94 | 0.65 / 0.94 | 0.71 / 0.76 | 0.32 |
| prep-inf | 0.00 / 0.06 | 0.29 / 0.47 | 0.41 / 0.76 | 0.53 / 0.94 | 0.82 / 0.82 | 0.31 |
| modal-inf | 0.00 / 0.00 | 0.24 / 0.59 | 0.35 / 0.76 | 0.76 / 0.82 | 0.71 / 0.88 | 0.34 |
| fam-a | 0.18 / 0.76 | 0.76 / 1.00 | — | — | 0.00 / 0.00 | 0.47 |
| fam-et | 0.35 / 1.00 | 0.76 / 1.00 | 1.00 / 1.00 | 1.00 / 1.00 | 0.00 / 0.00 | 0.76 |
| fam-son | 0.35 / 0.65 | 0.71 / 1.00 | 0.94 / 1.00 | 1.00 / 1.00 | 0.71 / 1.00 | 0.75 |
| fam-on | 0.24 / 0.76 | 1.00 / 1.00 | 1.00 / 1.00 | 1.00 / 1.00 | 0.59 / 0.59 | 0.79 |
| fam-ces (information) | 0.00 / 0.11 | 0.11 / 1.00 | 0.67 / 1.00 | 0.89 / 1.00 | 0.00 / 0.00 | 0.42 |
| precision | 0.10 / 0.30 | 0.25 / 0.55 | 0.35 / 0.80 | 0.60 / 0.80 | 0.21 / 0.36 | 0.33 |
| domain conv (131) | 0.12 / 0.40 | 0.51 / 0.81 | 0.66 / 0.91 | 0.81 / 0.93 | 0.59 / 0.68 | 0.52 |
| domain school (119) | 0.08 / 0.24 | 0.27 / 0.57 | 0.40 / 0.77 | 0.61 / 0.83 | 0.43 / 0.60 | 0.33 |
| **all (250)** | 0.10 / 0.32 | 0.40 / 0.70 | 0.54 / 0.84 | 0.71 / 0.88 | 0.51 / 0.64 | **0.43** |

By split, pooled top-1 is **0.42 on dev** and **0.43 on test** (hit@8 at p1: 0.66 dev, 0.73 test). The 10D-1 gate on test is
pooled top-1 ≥ 0.53.

Swap mode is the weak point. When the typed word is itself a lexicon form, it stays first:
- after « il », typing « à » never gives « a » (0 of 17);
- typing « et » never gives « est » (0 of 17).

Swap hit@8 already reaches 0.80 for det-num, etre-part, prep-inf and fam-son; avoir-ppas is just below at 0.76.

**G-invented and G-synthetic**: each error is typed in full, with the typed sentence before it as the context. « Typed
first » means the typed form itself stays first in the bar.

| G-invented class | errors | gold top-1 / hit@8 | typed first |
|---|---|---|---|
| det-num | 53 | 0.92 / 0.98 | 0.06 |
| adj-gender | 33 | 0.76 / 0.82 | 0.15 |
| post-adj | 33 | 0.64 / 0.91 | 0.30 |
| subj-verb | 34 | 0.50 / 0.65 | 0.41 |
| noun-subj | 43 | 0.16 / 0.58 | 0.49 |
| etre-part | 40 | 0.28 / 0.93 | 0.40 |
| avoir-ppas | 38 | 0.50 / 0.71 | 0.45 |
| prep-inf | 28 | 0.93 / 1.00 | 0.00 |
| modal-inf | 35 | 0.60 / 0.89 | 0.29 |
| fam-a | 48 | 0.00 / 0.00 | 0.65 |
| fam-et | 40 | 0.00 / 0.00 | 0.80 |
| fam-ces | 28 | 0.00 / 0.00 | 0.93 |
| fam-son | 39 | 0.69 / 1.00 | 0.31 |
| fam-on | 33 | 0.55 / 0.67 | 0.45 |
| **all** | **525** | **0.46 / 0.65** | 0.40 |

| G-synthetic class | errors | gold top-1 / hit@8 | typed first |
|---|---|---|---|
| num | 2 138 | 0.74 / 0.99 | 0.17 |
| gender | 336 | 0.53 / 0.96 | 0.27 |
| person | 1 001 | 0.29 / 0.68 | 0.41 |
| qy2p | 557 | 0.44 / 0.82 | 0.37 |
| qgn | 413 | 0.26 / 0.92 | 0.22 |
| family | 1 489 | 0.12 / 0.19 | 0.69 |
| **all** | **5 934** | **0.43 / 0.72** | 0.37 |

**P-auto** (`--siblings --max 20000`) counts the occurrences of held-out words that have a sound-alike sibling: a form of the
same lemma with the same `PhoneticKey.Fold`, or a member of the same homophone family. Prefix mode types the common prefix of
the word and its siblings, capped at the word's length − 1. Swap mode types the most frequent sibling in full.

| Class | n | letters | sib-top1 | top1 | hit@8 | swap top1 / hit@8 |
|---|---|---|---|---|---|---|
| **Subtitles** | | | | | | |
| num | 11 630 | 4.5 | 0.99 | 0.96 | 1.00 | 0.91 / 0.93 |
| person | 2 536 | 3.8 | 0.97 | 0.93 | 0.98 | 0.78 / 0.81 |
| family | 2 344 | 1.0 | 0.91 | 0.84 | 0.95 | 0.12 / 0.14 |
| qgn | 2 335 | 4.2 | 0.95 | 0.91 | 0.99 | 0.90 / 0.99 |
| gender | 1 155 | 4.6 | 0.96 | 0.93 | 1.00 | 0.88 / 1.00 |
| all | 20 000 | 4.0 | **0.97** | 0.93 | 0.99 | 0.80 / 0.84 |
| **Wikipedia** | | | | | | |
| num | 13 274 | 5.3 | 0.91 | 0.81 | 0.98 | 0.78 / 0.95 |
| person | 1 138 | 4.5 | 0.91 | 0.67 | 0.94 | 0.70 / 0.98 |
| family | 2 406 | 1.2 | 0.74 | 0.65 | 0.92 | 0.15 / 0.27 |
| qgn | 1 846 | 5.5 | 0.69 | 0.55 | 0.95 | 0.50 / 0.97 |
| gender | 1 336 | 5.4 | 0.81 | 0.74 | 0.98 | 0.67 / 0.99 |
| all | 20 000 | 4.8 | **0.86** | 0.75 | 0.97 | 0.67 / 0.87 |

The first 10D-1 gate « sib-top1 ≥ baseline + 5 pts » could not be met on the subtitles (baseline 0.97). Plan 11 was
revised after the 10D-0 review: prefix sib-top1 error reduction ≥ 30 % on both sets (≥ 0.979 / ≥ 0.902), swap top-1
≥ + 5 pts on both (0.80 → 0.85, 0.67 → 0.72), and no class losing more than 1 pt.

**Q / Y / 2p row (`qy2p-phon`, added after the 10D-0 review)**: the grapheme fold keeps `-é`, `-er` and `-ez` apart
(`mangé` ≠ `manger`), so these pairs are never « same fold » siblings. They get their own row: siblings of the same
lemma with the **same Lexique pronunciation** (`words.phon`) whose pair is Q / Y / 2p. It covers the same stretch of
text, is measured apart and is **not counted in `all`**, so the rows above are unchanged.

| qy2p-phon | n | letters | sib-top1 | top1 | hit@8 | swap top1 / hit@8 |
|---|---|---|---|---|---|---|
| Subtitles | 2 405 | 4.9 | 0.98 | 0.91 | 0.99 | 0.89 / 0.98 |
| Wikipedia | 1 663 | 5.6 | 0.76 | 0.56 | 0.89 | 0.48 / 0.81 |

The 4-decimal values used by the revised 10D-1 gates (plan 11) are in `<work>\grammar\review\siblings_*.json`.

**Sweep baseline point** (`--sweep-grammar`). 10D-0 runs only the grammar-off point; the grid of 648 joint points is
printed for 10D-1.

| Measure | Baseline |
|---|---|
| P-agree dev, pooled top-1 | 0.42 (475 queries) |
| P-auto dev sib-top1 (subtitle occurrences 20 001–25 000) | 0.96 |
| Dev KSS, words 20 001–25 000, subtitles | 78.34 % |
| Dev KSS, words 20 001–25 000, Wikipedia | 45.47 % |

**Load**: `GrammarLexicon` reads 119 849 forms, 84 795 lemmas and 152 062 analyses in 215–236 ms, for +2.5 MB of managed
memory; `grammar.sqlite` takes 26.8 MB on disk.

**Load alternative for 10D-1 (review 10D-0, owner's choice; not switched)**. The `pred_index` keys are Lexique spellings.
The alternative ships no `pred_index`: the engine builds the entry index at load from `morph`, with the same
normalisation (`GrammarLexicon.LoadFromMorph`, `Predict.Eval … --grammar-from-morph`). Both give identical arrays
(`--grammar-load-compare`, xUnit `An_Index_Built_From_Morph_Equals_Pred_Index`). Host measurements, 3 fresh processes
each, after the engine load:

| Loader | load | managed kept | working set after load | peak working set | file |
|---|---|---|---|---|---|
| `pred_index` (shipped index) | 162–167 ms | +2.5 MB | +2–3 MB | 92–93 MB | 26.8 MB |
| from `morph` (no `pred_index`) | 826–839 ms | +2.5 MB | +58 MB (not trimmed yet) | 166 MB | 23.9 MB |

In-process alternating rounds: 111–135 ms and 19 MB allocated, against 624–720 ms and 160 MB allocated. The `morph`
route fits the 300 ms budget only if the index is built once and cached, or if `morph` is read lazily. It keeps the file
free of Lexique data, at about 5× the load time and +70 MB transient memory on a worker thread.

## Grammar (10D-1 grammatical prediction, 2026-11-15) — test release 0.9.0

Plan 11, steps 10–13: `AgreementContext` (rules S / N / NP / E / A / I over the last three tokens), `HomophoneFamilies`,
the agreement term in the passes, the personal pass and `PredictNext`, and the added (or lifted) agreeing form; design in
[predict-core-design.md](../design/predict-core-design.md) § 5e. Every run as the 10D-0 baseline: names pack loaded,
`--grammar out\grammar.sqlite`, now **with** the ranking (engine defaults). Outputs: `<work>\grammar\10d1\` (`sweep\`,
`final3\`, `identity*\`, bench and memory files).

**Byte identity.** `GrammarOn = false`, every weight at 0 (`--w-nominal 0 --w-verbal 0 --w-noun-subj 0 --inject-gap 0`)
or no `grammar.sqlite`: `cases.json` (with and without the pack) and the held-out `--dump` files (subtitles, Wikipedia,
subtitles with the pack, Wikipedia keep-case with the pack) are byte-identical to the 0.8.3 build (`identity.sh`, run
again after each engine change: `identity*.log`). xUnit: 500 queries identical with weights 0 / `GrammarOn` false. Since the review's next-word filter (below), identical except the next-word lists that held an accent variant (5 subtitle words, 2 keep-case Wikipedia words).

**What changed on the way (measured, then kept)**:

| Change | Why (measured) |
|---|---|
| `AgreeBonus` = 0: an agreeing form gets no bonus, only the forms that disagree lose W (plan formula = 1) | with + W every agreeing verb passed the clitics and adverbs that often follow a subject (« je ne », « je vous », « je te »): subtitle KSS 78.7 → 78.0 %; with 0: 78.7 % (19 061 letters, better than 0.8.3) |
| a lemma sibling is lent only by the word typed in full (or its sound-alike / correction), not by a completion of the typed letters | adding forms while a word is being typed cost 1.7 pt of subtitle KSS in the first run (20 756 letters) |
| « wide » disfavoured lists (our extension): after a subject also noun and adjective, and participle / infinitive after a bare subject; after a determiner a finite verb | verbs that are also nouns / adjectives / participles (`fait`, `dit`, `est`) were never judged (« tu fait » stayed first); dev KSS +0.24 / +0.48 pt with them |
| the infinitive / participle part only after a bare subject (not `nous` / `vous`, not after a word that can be a verb); `nous` / `vous` after a verb are objects; an inversion needs a verb of the subject's person (« la cantine, ils ») | the first wide version lost « puis-je vous raconter », « il va nous voir » (P-auto Q/Y/2p swap 0.89 → 0.86) |
| bare elision forms (`s l m qu`…) are never judged | French.lex knows them as letters (nouns): « il s' » lost its `s'` (`cases.json` elision 0.47 → 0.40; back to 0.47) |
| an agreeing sibling already in the list is lifted to the injection score | « tu fait » → `fais`, found by its sound far below |
| the term is added at the ranking, only to the best candidates (`LazyTop`, a heap; exact when the bonus is 0) | speed gate: in the passes it cost +0.07–0.16 ms per query in grammar contexts and +0.45 ms per next-word query (random memory reads of the analyses of thousands of candidates); same outputs (dump and P-agreement JSON compared) |

**Sweep** (`--sweep-grammar`, dev slices only: P-agreement even ids, P-auto subtitle occurrences 20 001–25 000, KSS
words 20 001–25 000 of both sets; objective = P-agree + sib-top1 + swap top-1 under a dev KSS loss ≤ 0.05 pt; three runs,
the last 34 points in 17 min, `sweep\sweep3.txt`):

| Point | P-agree dev | sib-top1 | swap top-1 | objective | dKSS subs | dKSS wiki |
|---|---|---|---|---|---|---|
| grammar off | 0.4189 | 0.9554 | 0.7692 | 2.1435 | — | — |
| N=1 V=1 NS=0.5 gap=0.5 bonus=0 wide=1 (**chosen**) | 0.4253 | 0.9588 | 0.7846 | 2.1687 | +0.24 | +0.48 |
| N=1.5 V=1.5 (best objective) | 0.4274 | 0.9590 | 0.7844 | 2.1708 | +0.16 | +0.56 |
| … bonus 0.5 | 0.4358 | 0.9620 | 0.7732 | 2.1710 | **−0.56** | +0.87 |
| … bonus 1 | 0.4295 | 0.9610 | 0.7754 | 2.1659 | **−1.54** | −0.35 |
| … wide 0 | 0.4189 | 0.9580 | 0.7846 | 2.1615 | +0.01 | +0.43 |
| N=0 V=1 | 0.4253 | 0.9560 | 0.7798 | 2.1611 | +0.22 | +0.16 |

Above W ≈ 1 the results stop moving (a penalty larger than the language-model gap between two siblings changes nothing
more). Pick (smallest weights within 0.5 pt of the best): `WNominal = 1`, `WVerbal = 1`, `WNounSubj = 0.5` (NS 0 / 1:
± 0.0005), `InjectGap = 0.5`, `AgreeBonus = 0`, `WideDisfavour = true`, `par` off (no effect). Per-family rule (O7):
`WNounSubj` and `par` show no loss on test either way; kept as picked.

**Gates on the test splits** (`final3\`):

| Measure | Baseline (10D-0) | 10D-1 | Gate | |
|---|---|---|---|---|
| P-auto prefix sib-top1, subtitles | 0.9721 | 0.9735 (error −5.0 %) | ≥ 0.9805 (−30 %); reject < −10 % | **not met, accepted by the owner 2026-11-15** |
| P-auto prefix sib-top1, Wikipedia | 0.8608 | 0.8785 (error −12.7 %) | ≥ 0.9026 | **not met, accepted by the owner 2026-11-15** |
| P-auto swap top-1, subtitles | 0.7970 | 0.8121 (+1.5 pts) | ≥ 0.85 | **not met, accepted by the owner 2026-11-15** |
| P-auto swap top-1, Wikipedia | 0.6666 | 0.6886 (+2.2 pts) | ≥ 0.72 | **not met, accepted by the owner 2026-11-15** |
| P-auto per class (sib-top1, swap top-1) | | worst: Q/Y/2p subtitles sib-top1 −0.12 pt, family Wikipedia sib-top1 −0.08 pt | no class −1 pt | met |
| P-agreement test, pooled top-1 (p0–p3) | 0.43 | 0.44 | ≥ 0.53 | **not met, accepted by the owner 2026-11-15** |
| P-agreement test, any category pooled | | all equal or better (subj-verb 0.34 → 0.38) | ≥ −2 pts | met |
| P-agreement precision cases | pooled 0.30, swap 0.25 / 0.50 | pooled 0.30, swap 0.75 / 0.88 | unchanged or better | met at category level (not checked case by case) |
| P-agreement swap hit@8 ≥ 0.80 | | det-num, subj-verb, etre-part, avoir-ppas, prep-inf, fam-son 1.00; fam-et 0.78, fam-on 0.67, fam-a 0.50 | reported | 6 of 9 |
| Held-out KSS subtitles (`--max 20000`) | 19 088 letters (78.7 %) | **19 013** (78.7 %) | ≤ 19 177 | met (better) |
| Held-out KSS Wikipedia | 53 856 (44.2 %) | **53 441** (44.6 %) | ≤ 53 953 | met (better) |
| Wikipedia keep-case with the pack | 58 992 (40.5 %) | 58 602 (40.9 %) | — | better |
| `cases.json` (with the pack) | top-1 0.70, hit@8 1.00 | top-1 **0.72**, hit@8 1.00 | loss ≤ 0.02, hit@8 unchanged | met |
| `cases-personal` + fixture | 0.474, « Mes fautes » 1.000 | 0.474, 1.000 | ≥ 0.474; 1.000 | met |
| `--spelling` | 0 wrong reds | identical (0) | identical | met |
| Weights 0 / no grammar.sqlite | | byte-identical | byte-identical | met |

**Why the main gates are out of reach for this design.** The rules fire on 68 % of the P-auto occurrences (subtitles;
65 % Wikipedia: `--siblings` now prints the share with a rule) and there sib-top1 is already 0.96–0.98 without grammar.
Only the forms that disagree are demoted, and in most failures the language model's first choice already agrees (the
wrong sibling is not first) or the deciding word is outside the three-word window (« les enfants de la classe mang… »,
« a été créée »). P-agreement pooled top-1 measures the exact word after 0–3 letters: the competitors are other words of
the right form (« les » + `c` → `choses` before `chats`), which agreement cannot separate; only the swap columns move
(test swap top-1 0.57 → 0.67, hit@8 0.67 → 0.88). A bonus for agreeing forms (`AgreeBonus` 0.5 / 1) lifts P-agree dev to
0.43–0.44 only, and costs 0.6–1.5 pt of subtitle KSS.

| Swap mode (the typed sibling in full) | Baseline | 10D-1 |
|---|---|---|
| P-agreement test top-1 / hit@8 | 0.57 / 0.67 | 0.67 / 0.88 |
| P-auto swap hit@8 subtitles / Wikipedia | 0.84 / 0.87 | 0.91 / 0.89 |
| P-auto family swap top-1 / hit@8, subtitles | 0.12 / 0.14 | 0.21 / 0.54 |
| G-invented gold top-1 / hit@8 | 0.46 / 0.65 | 0.54 / 0.83 |
| G-synthetic gold top-1 / hit@8 | 0.43 / 0.72 | 0.47 / 0.78 |

**Speed** (`Predict.Cli --bench-grammar`, host, rounds 2–3, grammar off → on, after the lazy ranking): the `--bench`
inputs (no rule) 0.244–0.258 → 0.250–0.259 ms mean, p95 0.86–0.88 → 0.85–0.89 ms; 2 000 queries in grammar contexts
0.80–0.81 → 0.72–0.74 ms mean (faster: the heap replaces a full sort), p95 2.95–3.05 → 2.52–2.54 ms; 2 000 next-word
queries 1.48–1.52 → 1.49–1.52 ms mean, p95 3.22–3.24 → 3.19–3.22 ms. Gate (mean ≤ +0.05 ms, p95 ≤ +0.10 ms): met. A
second run on a busier host (`bench_grammar_final.txt`) varied by ± 0.1 ms between rounds in both directions.

**Memory** (`Predict.Cli --mem-grammar`, 3 fresh processes): load 291 / 319 / 468 ms (10D-0: 162–236 ms; the host was
slower that day), managed +2.4 MB, private +2.2–2.5 MB, working set +2.5–2.8 MB (+1.9–3.2 MB after a grammar query).
Gate ≤ +5 MB: met; load ≤ 300 ms: 2 of 3 above it, off the UI thread. Disk: `grammar.sqlite` 26.8 MB (≤ 30 MB). In the test VM
(W-G6): the load took 4.5 s at the first, cold start, then 0.3–1.0 s (worker thread; the bar ranks as 0.8.3 meanwhile), and
the working set with / without the file stayed within the run-to-run spread (medians 300.0 / 304.5 MB). Size report
`<work>\publish-0.9.0-size-report.md`: app without voices +26.9 MB (grammar.sqlite 26.8 MB).

**Owner decision (2026-11-15)**: publish 0.9.0 as it is, with the measured gains. The P-agreement pooled top-1, P-auto
prefix sib-top1 and P-auto swap top-1 gates are « not met, accepted by the owner 2026-11-15 »: the baselines are saturated
(the language model already ranks the agreeing form first in most cases where a rule fires), a bonus for agreeing forms
costs KSS, and the real gain is in swap mode and hit@8 (P-agreement swap hit@8 0.67 → 0.88, P-auto swap hit@8 0.84 → 0.91
on subtitles). Further gains are expected from 10D-2 (correction of the previous words), which reaches beyond the
three-word window.

**Review round (2026-11-15, after the VM run; `<work>\grammar\10d1\final5\`, `identity5\`).** Fixes:

- A sibling is lent only through an analysis of the lending lemma and of the source's part-of-speech class. Before,
  « être » lent « étais » (plural of the noun « étai ») and « êtres » after « mon vélo neuf est », « deux est »,
  « les est », « mes été »: they came above the correct typed word.
- `nous` / `vous` after a noun, an unknown word (a name) or « qui » are objects: no rule (« le maître nous a », « celui
  qui nous a », « la maîtresse vous a », « Léa nous a »).
- A numeral after a noun is an adjective (« un vélo neuf »).
- A noun group after « et » / « ou » gives no verb person (« le chien et le chat » + `son` → `sont` first).
- The next-word list never proposes a corpus spelling that is no lexicon form but the accent variant of one (« ést »,
  « etait » after « c' »), grammar or not.
  - The reviewer also asked to drop the lexicon forms left out of the spelling reference. Measured, that removed the
    lowercase names the next-word bar needs (« amérique », « berlin », « york », « reich », « hui »): 91 subtitle words,
    +85 letters (19 173). So it is not done.
- `grammar-cases.json` has 6 more precision cases (256; ids 251–256, generated by `gen_cases.py`).

**Identity, re-defined.** With the grammar off / weights 0 / no `grammar.sqlite`, outputs are identical to 0.8.3 except
the next-word lists that held such an accent variant:

| Run | Words that differ | Letters typed |
|---|---|---|
| `cases.json` (with and without the pack) | 0 | identical |
| Held-out Wikipedia `--dump` | 0 | identical |
| Held-out subtitles `--dump` (with and without the pack) | 5 | −5 letters: 19 083 vs 19 088 |
| Wikipedia keep-case with the pack | 2 | +8 letters: 59 000 vs 58 992 (two capitalised names whose lowercase is an accent variant of a word: « the », « Denis ») |

**Gates after the review** (final5; baseline = the 0.8.3 engine on the same 256-case file where it changed):

| Measure | 0.8.3 | before the review | after the review |
|---|---|---|---|
| P-agreement test, pooled top-1 (128 cases) | 0.4312 | — | 0.4333 |
| P-agreement test, swap top-1 / hit@8 | 0.57 / 0.67 | — | 0.67 / 0.87 |
| P-agreement test, precision | pooled 0.29, swap 0.25 / 0.50 | — | pooled 0.29, swap 0.63 / 0.75 |
| P-agreement, any category below 0.8.3 − 2 pts | — | — | none |
| P-auto sib-top1 / swap top-1, subtitles | 0.9721 / 0.7970 | 0.9735 / 0.8121 | 0.9735 / **0.8144** |
| P-auto sib-top1 / swap top-1, Wikipedia | 0.8608 / 0.6666 | 0.8785 / 0.6886 | 0.8784 / 0.6889 |
| Held-out KSS, letters typed (subtitles / Wikipedia) | 19 088 / 53 856 | 19 013 / 53 441 | **19 009 / 53 442** |
| Wikipedia keep-case with the pack | 58 992 | 58 602 | 58 612 |
| G-invented top-1 / hit@8 | 0.46 / 0.65 | 0.54 / 0.83 | 0.54 / 0.84 |
| G-synthetic top-1 / hit@8 | 0.43 / 0.72 | 0.47 / 0.78 | 0.47 / 0.78 |

Unchanged:
- `cases.json` 0.72 / 1.00.
- `cases-personal` 0.474 / « Mes fautes » 1.000.
- `--spelling` 0 wrong reds.
- No P-auto class loses more than 1 pt (worst: Q/Y/2p subtitles sib-top1 −0.12 pt).

## Installer (plan 12, 0.10.0, 2026-11-15) — prediction unchanged

The installer ships the scored database unchanged: `prediction.sqlite` SHA-256
`9ec68fafa57c087e264475820870e86ea208eba8a9ade0515e3c688877d045cd` in `src\Data.Build\out\`, in the 0.9.0 publish,
in the stage folder (`files.sha256`, checked by `build_installer.ps1`) and in the installed folder in the VM (T1i: the
installed tree equals `files.sha256`; T3a: the SHA-256 of the installed file). No Eval rerun is needed: the curated and
held-out numbers of the 10D-1 section above stand for 0.10.0. VM figures of the installed copy (hook time, start-up,
first prediction): **not measured** — plan 12 step 10 was deferred by the owner (TODO « System requirements »).
Since 2026-11-16 the 0.10.0 installer ships the written-corpus database instead (§ Full written-corpus database D:
`f53f1ab6cf0ed57554e2469d3c7bb9463df059762f36df28f08bcdb5a32de254`, 149.5 MB, with `prediction-SOURCES.txt`); the 0.9.0
file is kept as `src\Data.Build\out\prediction-0.9.0.sqlite` (git-ignored) for comparisons.

## Light corpus experiment A′ vs D (plan corpus-light, Phase 1, 2026-11-16)

Two databases from samples of the same token mass (study « corpus written children », plan corpus-light): **A1** = today's sources with the « œ » fold (Leipzig news, Tatoeba,
OpenSubtitles, each line kept with probability 0.15: 7 321 231 tokens); **D1** = the same news and Tatoeba lines, no
subtitles, Vikidia (training bucket, solved rate 0.205: 2 192 124 tokens) + 27 public-domain children's books
(1 193 647 tokens, 16.3 % of D1): 7 321 242 tokens. Phase 1 used the book roles **before** the owner's content review:
its 27 light books include 6 that Phase 2 excludes (Moll Flanders, Sébastien Roch, Le Vicaire de Wakefield, La
Maternelle, the cosmography and French Reader textbooks, the 1822 « Conversations »); the committed `pd-books.tsv` is
the post-review list. Same Lexique `words` table and spelling reference; release thresholds (2/3-grams ≥ 3, 4-grams ≥ 4); A1 32.4 MB,
D1 33.1 MB. Eval as the app ships (`--grammar out\grammar.sqlite`, no names pack, lower case), Predict.Eval with the
« œ » fold. Held-out sets drawn with seed 20261115, every sentence seen in a training pool dropped (`<work>\corpus-light\
heldout\manifest.json`): E1 = 3 held-out books (Porchat, Carroll tr. Bué, Stevenson tr. Grousset), E2 = Vikidia
articles with `sha256(title) mod 20 == 0`, E3 = the corrected G-invented sentences (69 of 400 dropped as seen in
training), W = Leipzig Wikipedia 2021 (random draw), S-unseen = the subtitle held-out minus seen sentences.

| Set (evaluated words) | A1 KSS | D1 KSS | Δ (pt) | 0.9.0 DB |
|---|---|---|---|---|
| E1 books (5 000) | 41.75 | 43.50 | +1.75 | 43.85 |
| E2 Vikidia (5 000) | 42.39 | 47.53 | +5.14 | 46.17 |
| E3 pupil-level sentences (1 536) | 47.33 | 49.28 | +1.95 | 52.89 |
| **KSS(E)**, mean of E1–E3 | **43.83** | **46.77** | **+2.95**, 95 % bootstrap by sentence [+2.53, +3.38] | 47.64 |
| W Wikipedia (10 000, context) | 40.43 | 43.12 | +2.69 | 43.81 |
| S-unseen subtitles (10 000, context) | 46.00 | 44.63 | −1.37 | 50.07 |

Gates for D1: `cases.json` hit@8 **0.98** (A1 1.00; top-1 0.72 vs 0.70) — **fails**: « vers l' » + « h » → horizon
falls out of the 8 because the trigram « vers l horizon » (4 in A1, from the subtitles) is below the count threshold in
the light D1 sample; `cases-personal` 0.509 / « Mes fautes » 1.000 (pass); `--spelling` 0 wrong reds (pass); OCR mass
(strict list, without « ii » / « ia », which in D1 are Roman numeral II and « IA ») 0.0009 % vs A1 0.12 % (pass; with
them 0.10 %; the strict list and its 0.02 % threshold were defined **after** seeing D1's « ii » / « ia » mass, accepted by
the owner); G-invented top-1 / hit@8 0.50 / 0.83 vs A1 0.48 / 0.83 (pass); passé-simple mass 0.119 % vs A1 0.020 %
(≤ A1 + 0.5 pt: pass). grammar-cases test pooled top-1: A1 0.35, D1 0.37, 0.9.0 0.43 (information). The bootstrap
resamples sentence blocks (reconstructed from the dumps' contexts; review 2026-11-16), not single words.

**« œ » fold check (step 0.1) on the 0.9.0 DB**, Predict.Eval before / after the fold (`--grammar`, `--max 20000
--dump`): the dumps are identical except at words that are or follow an « œ » word — subtitles 6 lines (plus the last 7
words of the run, shifted out of the 20 000 because the folded « cœur » words now count), KSS 78.7 % both (19 009 / 89 416
→ 19 029 / 89 426 letters); Wikipedia 41 lines (plus 18 tail words), KSS 44.6 % both (53 442 / 96 515 → 53 463 / 96 525).
xUnit `ContextFoldTests` (« mon cœur » ≡ « mon coeur ») passes; the whole suite 1 312 / 1 312.

**Verdict by the plan's rule: D-LOSES** (a gate failed), although D1 beats A1 by +2.95 pt on KSS(E) with an interval
far from 0 and on every decision set. The failing gate is one curated case lost to the light sample's size; the owner
decides between the rule's next step (D′: Tatoeba × 2) and a full-size D (Phase 2), where that trigram is likely back.
**Owner (2026-11-16): Phase 2** — see the next section.
`results.md`, the runs and the dumps are in `<work>\corpus-light\` (Phase 1 wall time ≈ 18 min: extract + split +
held-out + sample 6 min, both builds 46 s, three Eval suites 10 min).

## Full written-corpus database D (plan corpus-light, Phase 2, 2026-11-16) — release candidate

Owner decision after Phase 1: build the full D despite the light verdict (the lost « horizon » case was judged a
sample-size artefact), after removing from training the books that are not children's reading (`pd-books.tsv` role
`excluded-content`: 6 adult novels, 4 textbooks / manuals / word lists / reports, 2 books in pre-1835 spelling — 12 books,
0.82 M tokens; the held-out E1 books do not move). **Inputs:** all of Leipzig news 2023 (20.5 M tokens), Tatoeba (5.8 M),
the Vikidia training bucket (10.7 M), 56 public-domain books (2.5 M, 6.3 % of 39.5 M; cap 20 %); no subtitles. Build
2.5 min (n-grams 87 s, 4-grams 54 s): 1 070 787 bigrams, 1 637 796 trigrams, 626 961 4-grams, **149.5 MB** (0.9.0:
163.5 MB), SHA-256 `f53f1ab6…e254`, now `src\Data.Build\out\prediction.sqlite` with `prediction-SOURCES.txt` (adopted,
below; data pack `pepite-data` 2026.11, `data-pack.lock.json`). `grammar.sqlite` rebuilt from it: same SHA-256
`d0c4122a…8301`.

Same runs as Phase 1 (as shipped: `--grammar`, no names pack), D full against the 0.9.0 DB, paired
(`<work>\corpus-light\results-phase2.md`):

| Set (evaluated words) | 0.9.0 KSS | D full KSS | Δ (pt) |
|---|---|---|---|
| E1 books (5 000) | 43.85 | 45.25 | +1.41 |
| E2 Vikidia (5 000) | 46.17 | 51.98 | +5.80 |
| E3 pupil-level sentences (1 536) | 52.89 | 54.57 | +1.68 |
| **KSS(E)** | **47.64** | **50.60** | **+2.96**, 95 % bootstrap by sentence [+2.51, +3.40] |
| W Wikipedia, random draw (10 000) | 43.81 | 46.95 | +3.14 |
| S-unseen subtitles (10 000) | 50.07 | 47.26 | −2.81 |

| Gate (D full vs 0.9.0) | 0.9.0 | D full | |
|---|---|---|---|
| `cases.json` hit@8 / top-1 | 1.00 / 0.72 | **1.00** / **0.68** | hit@8 met (« vers l' » + `h` → horizon 4th); top-1 −0.04 (`h` → habitude 1st → 5th, `mè` → mets 1st → 2nd; also `sou` → souris 2 → 3, `sé` → ses 2 → 6): **not met against 0.9.0** (gate: ≥ baseline − 0.02); met against the light A1 (0.70 − 0.02) |
| `cases-personal` / « Mes fautes » | 0.474 / 1.000 | 0.491 / 1.000 | met |
| `--spelling` wrong reds | 0 | 0 | met |
| OCR mass, strict list (max of the tables) | 0.14 % | 0.0021 % | met |
| Passé-simple mass (all tables) | 0.025 % | 0.070 % | met (≤ + 0.5 pt) |
| G-invented top-1 / hit@8 | 0.54 / 0.84 | 0.55 / 0.84 | met |

10D-1 suite (names pack + `--grammar`, as the 0.9.0 measurements; `<work>\corpus-light\runs-p2\`):

| Measure | 0.9.0 | D full |
|---|---|---|
| `cases.json` with the pack, top-1 / hit@8 | 0.72 / 1.00 | 0.68 / 1.00 |
| Held-out subtitles `heldout_subs_40k` (`--max 20000`) | 78.7 % (19 029 letters) | **52.0 %** (42 942) — expected: the set leaks 51.9 % of its sentences from the subtitle training (study § 1.3) |
| Held-out Wikipedia `heldout_wiki_8k` | 44.6 % (53 462) | **47.7 %** (50 469) |
| P-auto subtitles sib-top1 / swap top-1 (all) | 0.97 / 0.81 | 0.92 / 0.72 (every class lower: family 0.91 → 0.79, gender 0.96 → 0.87, qgn 0.96 → 0.82, Q/Y/2p 0.98 → 0.88) |
| P-auto Wikipedia sib-top1 / swap top-1 (all) | 0.88 / 0.69 | 0.89 / 0.71 (every class equal or better) |
| P-agreement test, pooled top-1 | 0.43 | 0.48 |
| G-synthetic gold top-1 / hit@8 | 0.47 / 0.78 | 0.51 / 0.78 |
| G-invented gold top-1 / hit@8 | 0.54 / 0.84 | 0.55 / 0.84 |
| `Predict.Cli --bench` (3 runs) | 0.182–0.185 ms, working set 101–108 MB, load 730–782 ms | 0.180–0.183 ms, 101–108 MB, load 754–924 ms |
| `--bench-grammar` next-word rounds 2–3, grammar on | 0.78–0.81 ms | 0.76–0.78 ms |
| Picture coverage (`build_pictos.py report`) | gate OK | identical (it reads only the `words` table) |
| App size (`size_report.py` not run: no publish) | — | −14 MB (`prediction.sqlite` 163.5 → 149.5 MB) |

Reading: on written and children's text D full is better everywhere (+1.4 to +5.8 KSS points, P-agreement +5 pts,
G-synthetic +4 pts, Wikipedia P-auto equal or better); on subtitle text it is worse (unseen subtitles −2.8 pt, P-auto
subtitles −5 pts sib-top1, −9 pts swap) — the spoken register the subtitles brought. One gate is not met against 0.9.0:
`cases.json` top-1 0.68 (four curated « h » / short-prefix cases rank lower).

**Owner decision (2026-11-16): adopt D full** for 0.10.0, accepting the two misses: `cases.json` top-1 0.68 vs 0.72
(« not met, accepted by the owner 2026-11-16 »; hit@8 stays 1.00) and the subtitle-register P-auto drop (subtitles
sib-top1 0.97 → 0.92, swap top-1 0.81 → 0.72; Wikipedia equal or better). It is now `src\Data.Build\out\prediction.sqlite`,
with `prediction-SOURCES.txt` next to it (copied next to the exe and installed). Follow-up in TODO: the short-prefix top-1.

**Clitic chain fix (owner 2026-11-16).** With the written base, « je les » + `mang` gave « manger » before « mange »: the
wide disfavour of the infinitive / participle applied only after a bare subject (alone or + « ne »). It now also applies
when object / reflexive clitics follow the subject pronoun (« je les », « je ne les », « tu le », « il nous », « nous les »;
`nous` / `vous` only with a clitic after them; never after a word that can be a verb, and after a verb there is no
subject chain: « je vais les », « il faut les » keep the infinitive first). xUnit `A_Clitic_Chain_Keeps_The_Subject`: je les
mang → mange, je ne les mang → mange (by the language model since the review below), tu le prend → prends, il nous regard → regarde 2nd after « regardait » (a 3s form
first; the infinitive below), je vais les / il faut les mang → manger. The unit case « je vous » + manger moved from
neutral to disfavoured; « puis je vous » + manger stays neutral. Review (2026-11-16): a clitic chain that fills the
three-word window has an unknown word before its subject, which may be an inverted verb (« veux tu nous les donner »,
« voulez vous me le dire »): neutral (« je ne les », « tu nous les »); « veux tu nous les » + `donn` keeps « donner » in
the top 3. Measured on the new base after the review (`<work>\corpus-light\results-clitics.md`): KSS(E) 50.60 → 50.61
(E1 +0.03, E2 0, E3 +0.01; W +0.01, S-unseen +0.02; bootstrap [+0.00, +0.03]), `cases.json` identical (0.68 / 1.00),
grammar-cases test pooled top-1 0.48 → 0.48, G-invented 0.55 / 0.84 → 0.55 / 0.84, cases-personal 0.491 and « Mes
fautes » 1.000, spelling 0 wrong reds; « je les » / « je ne les » + `mang` → « mange » first.
The « œ » fold reaches `AgreementContext` already: the engine passes it the folded `PrevWords` tokens.

## Spelling reference moved to `spelling.sqlite` (publication cleanup, 2026-11-16) — results unchanged

The MPL-2.0 tables `spelling_unlisted` / `spelling_source` left `prediction.sqlite` (CC BY-SA 4.0) for their own file
(data pack 2026.11.1; `prediction.sqlite` VACUUMed, same `words`, bigram, trigram and 4-gram row counts; the 3 836
unlisted forms and the source row identical). Measured with the 2026.11 database (tables inside) against the 2026.11.1
pair (`spelling.sqlite` next to it):

| Run | 2026.11 | 2026.11.1 |
|---|---|---|
| `cases.json` | 0.68 / 1.00 | identical output |
| `--spelling --names` | 0 wrong reds, recall 0.827, names ✓ 86 ≈ 0 | identical output (timings aside) |
| held-out subtitles `--max 20000` (context set) | KSS 51.7 % (43 154 / 89 426) | identical output |
| held-out Wikipedia `--max 20000` | KSS 47.4 % (50 800 / 96 525) | identical output |

## Performance / footprint (the resource budget)

Measured (`Predict.Cli --bench`, .NET 6, 125 k words, 746 k bigrams, 1.1 M trigrams):

| | measured |
|---|---|
| per prediction | **0.20 ms** |
| load time | ~0.8 s (once) |
| RAM (working set) | **105 MB** |
| data on disk | **82 MB** |

So the whole model is small and ~instant per keystroke — comfortably inside the « ≈ 240 MB disk + memory,
fast on a home PC » budget. The word list is in RAM; the n-gram tables are queried on disk (indexed,
cached per context), so memory stays flat as the corpus grows.

## Further gains (optional)

- A bigger/again-more-conversational corpus (OpenSubtitles fr) and 4-grams would lift top-1.
- Interpolated/Kneser-Ney smoothing instead of the current stupid-backoff-style blend.
- A small quantised neural LM (ONNX) as a Tier-B reranker — only if the n-gram top-1 plateaus.

## Extending

Add rows to `cases.json` (`{ category, context?, typed, intended }`). A larger, balanced
set (more everyday sentences, more error types) makes the score trustworthy; 20 cases is a smoke test.
