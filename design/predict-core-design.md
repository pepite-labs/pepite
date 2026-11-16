# `Predict.Core` design — the prediction engine (as built)

Source: [`src/Predict.Core/PredictionEngine.cs`](../src/Predict.Core/PredictionEngine.cs),
[`src/Predict.Core/PhoneticKey.cs`](../src/Predict.Core/PhoneticKey.cs). Background papers:
[architecture-overview.md](architecture-overview.md#references). Scores: [evaluation.md](../development/evaluation.md).

## 1. Contract

```csharp
var eng = PredictionEngine.Load("prediction.sqlite");      // a few seconds (builds the RAM indexes), read-only DB
IReadOnlyList<Suggestion> s = eng.Predict("orizon", new[] { "vers", "l" }, k: 8);   // word being typed
IReadOnlyList<Suggestion> n = eng.PredictNext(new[] { "il", "fait" }, k: 8);        // after a space
bool known = eng.Knows("horizon");

record struct Suggestion(string Word, double Score, MatchKind Kind);   // Kind: Prefix | Phonetic | Edit
```

- `context` = previous words, any granularity (`"je ne sais"` or `["je","ne","sais"]`): it is re-tokenised
  with `[a-zà-ÿœ]+` and only the **last three** tokens are used.
- **Not thread-safe** (n-gram caches are plain dictionaries, one `SqliteConnection`). Callers serialise
  (the keyboard app calls it only from its UI thread).
- Tunables are public fields: `WLm`, `WPrefix`, `WEdit`, `Alpha`, `UseFourgrams` (defaults tuned, § 4).

## 2. Two stages: candidates, then ranking

```
 typed "orizon" + context ["vers","l"]
   │
   ├─ (a) literal prefix ─── binary search in words sorted by lowercase spelling ─► horizon? no; ori… words
   ├─ (b) phonetic prefix ── Fold("orizon") = "orizO" ─ binary search in sorted phonetic keys ─► horizon, horizons…
   │                         (keys = grapheme fold of each word  +  key from Lexique's real pronunciation)
   └─ (c) edit distance 1 ── only if |typed| ≥ 4: swaps, deletions, substitutions, insertions of the
                              accent-stripped word, looked up in a hash of accent-stripped spellings ─► lbeu → bleu
   │
   ▼  merge: best score per word, remembering which generator found it (→ colour in the UI)
 score(w) = LM(w | context) + WPrefix·gate(freq)·[literal prefix] − WEdit·[edit candidate]
   │
   ▼  sort by score desc, then shorter word first; take k
```

| Generator | Catches | Index (in RAM) | Cost |
|---|---|---|---|
| literal prefix | normal completion `mais → maison` | `_aoKeys` sorted array, `LowerBound` | O(log n + hits) |
| phonetic prefix | sound-alike spelling `maizon`, `orizon`, `famme` | `_fKeys` sorted array of ≈ 250 k keys (two per word when they differ) | O(log n + hits) |
| edit-1 (Damerau) | inversions/typos `lbeu`, `jadrin` | `_strip` hash: accent-stripped spelling → entries | ≈ 90·L variants, O(1) each |

Why **prefix** search on phonetic keys: `Fold` is prefix-compatible (fold of a prefix = prefix of the fold),
so phonetic matching works mid-word, not only on whole words.

## 3. Phonetic keys

Two keys per lexicon word, one for the input:

```
 lexicon word "femme" ──► Fold("femme")            = "fem"   (grapheme rules)
                     └──► FromLexiquePhon("fam")    = "fam"   (Lexique SAMPA → same alphabet)
 typed "famme"       ──► Fold("famme")             = "fam"   ─── matches the 2nd key ─► femme
```

- `Fold`: French grapheme → sound rules (silent `h`, intervocalic `s→z`, nasals → `A E O U`, `ph→f`,
  `ch→C`, `gn→N`, `qu→k`, vowel digraphs `ai/ei→e`, `au/eau→o`, double consonants, final silent letters…).
  Alphabet: `a e i o u` + nasals `A E O U` + consonants + `C j N`.
- `FromLexiquePhon`: maps Lexique's pronunciation to the same alphabet, so **irregular** words are found by
  their real sound (`femme`, `monsieur`, `second`), which grapheme rules cannot do.
- A Lexique key is stored only if it is ≥ 2 symbols and differs from the grapheme key (no duplicates).
- **Final e (0.8.3)**: /e/ /ɛ/ /ø/ /œ/ /ə/ are one symbol `e` (dyslexic spellers mix é / è / ai / eu), but only a
  **mute** final e is dropped — a written unaccented `e` on its own (`pomme` → `pom`, `parle` → `parl`) or Lexique's
  schwa `°` (`je` = `Z°` → `j`); a sounded one stays in both keys: `fè`, `fais` → `fe`, `très`, `tré` → `tre`,
  `jamè`, `jamais` → `jame`, Lexique `fE` → `fe`, `se` (ces / ses) → `se`, `paRle` (parler) → `parle`. Before, both keys
  dropped every final `e`, so « fè » folded to `f` (one symbol: no sound pass) and only literal « fè… » words came. Inside
  the fold an internal mark tells a sounded e (é è ê ë, ai, ei, eu, oe) from a plain one until the end, where both are
  written `e` (a double « éé », « ée » collapses to one sounded e). The accent pass is one switch over the French letters
  (no Unicode normalisation per word; other letters still go through it).

## 4. Ranking: stupid-backoff language model

```
          ┌ c(p3 p2 p1 w)/c(p3 p2 p1 ·)            if the 4-gram exists       (UseFourgrams)
 S(w) =   │ α  · c(p2 p1 w)/c(p2 p1 ·)             else if the trigram exists
          │ α² · c(p1 w)/c(p1 ·)                   else if the bigram exists
          └ α³ · P_uni(w)                          else   (P_uni from Lexique frequency, +1 smoothed)

 LM(w) = WLm · log10(S(w) + 1e-12)
 score(w) = LM(w) + WPrefix · clamp(log10(freq+1)/1.5, 0, 1) · [literal prefix]  −  WEdit · [edit-1]
```

| Weight | Default | Role |
|---|---|---|
| `WLm` | 1.0 | scale of the LM term (kept at 1; the others are relative to it) |
| `WPrefix` | 1.0 | bonus for exact-spelling matches, **gated by frequency** so rare literal prefixes (`oto…`) do not beat a frequent phonetic match (`auto`) |
| `WEdit` | 1.0 | penalty for typo candidates, so a real prefix/phonetic match wins ties |
| `Alpha` | 0.2 | backoff discount per level |

Defaults come from `Predict.Eval --sweep` (grid on a disjoint dev split, confirmed on a test split; see
[benchmark-design.md](benchmark-design.md)). Stupid backoff is not a normalised probability; it is used
only for ranking, where it is known to work as well as Kneser-Ney at large data sizes (Brants et al. 2007).

`PredictNext` uses the same `S(w)` over the union of the successor lists of the available n-gram levels
(no letters typed → no generators).

## 5. Memory vs. disk

| In RAM (built at `Load`) | On disk (queried lazily) |
|---|---|
| `Entry[]` ≈ 125 k words (spelling, log-freq) + unigram probabilities | `bigrams(prev, word, c)` 1.13 M rows |
| sorted spelling index, sorted phonetic index, accent-stripped hash | `trigrams(p2, p1, word, c)` 1.40 M rows |
| per-context n-gram caches (grow with use) | `fourgrams(p3, p2, p1, word, c)` 1.1 M rows |

Per context, one indexed query loads the **top successors only** plus the total:

```sql
SELECT word, c, (SELECT SUM(c) FROM bigrams WHERE prev=$p)
FROM bigrams WHERE prev=$p ORDER BY c DESC LIMIT 3000      -- trigrams: 2000, 4-grams: 1000
```

The `LIMIT` matters: `la` / `de` have tens of thousands of successors; loading all of them made the first
lookup ~900 ms, the capped query takes 20–80 ms, and the result is cached, so later lookups are ~0 ms.
Truncation is safe for ranking: a word beyond the top 3000 successors falls back to the next level.

Result: RAM stays ~107 MB whatever the corpus size; disk 163 MB.

## 5b. Personal words (plan 10A, 0.8.0, D10)

`PredictionEngine.User` holds an immutable `UserLexicon` snapshot (`UserLexicon.Empty` by default: results **identical
to 0.7**, measured byte for byte on the curated and both held-out sets). The app builds it off the engine
(`UserLexicon.Build(words, fixes)`, ≤ 30 ms target for 15 000 words) and swaps it in one reference write; a query reads
the reference once.

```
 UserLexicon: entries (display form with its capital, weight, Name = stored with a capital)
   LowerKeys / FoldKeys   sorted lowercase forms (literal prefix) / PhoneticKey.Fold (sound prefix: no Lexique phon)
   Strip                  accent-stripped lowercase → entries (edit-1, shares the main pass's Edits1 list)
   LowerBonus             forms stored in lowercase (they lift the main lexicon's own entry)
   FixKeys / FixTargets   « Mes fautes »: StripLower(spelling) → target (the pupil's pair first, then the class's)
 Hyphenated / apostrophised entries are indexed whole and by each capitalised part of ≥ 2 letters.
```

**Scoring.** A personal word is scored by the same LM, but its unigram level is lifted: `s = max(s_ngram, α³ · (uni +
WUser · w))`, where `uni` is the main unigram (or `UserUnigram` for a word the lexicon does not know) and `w` its weight
(1.0; a **name** gets `w × NameCapitalLift` when the typed word starts with a capital inside a sentence
(`Predict(…, sentenceStart: false)`; at a sentence start every word has a capital) and `w × NameLowercaseShare`
otherwise — French writes names with a capital, so a lowercase « l » keeps meaning « le, la, les »). The lift is a fixed
amount per word (not a share of the list), so it only overtakes unigram-level candidates and never a context (n-gram)
prediction. The literal-prefix gate is the main one (frequency) for a known word, 1.0 for a new one; edit-1 keeps
`−WEdit`. « Mes fautes »: the typed form (accent-stripped) equal to a pair's spelling, or a prefix of it from 3
letters → the target with `+WSpelling` (log scale), kind `Phonetic` (the bar's green). The candidate map is now
`Ordinal`: the main forms are unique lowercase (nothing changes for them) and « Pierre » (a name) is a candidate of
its own next to « pierre »; `Suggestion.Name` marks it (and a capitalised « Mes fautes » target); new words the
lexicon and the context do not know all score alike, so a pass scores only the `ShortlistSize` (32) heaviest / shortest
of them (the app never shows the common noun's picture on it).
`KnowsAny(word)` = main ∪ personal; `MostFrequent300` feeds `LessonExtractor` (the « Nouvelle leçon » tick rule).
`PredictNext` is unchanged (personal words have no n-grams). Weights and measures: [../development/evaluation.md](../development/evaluation.md) § Personal words.

No automatic learning (owner, 2026-11-14): the engine never adds a word by itself.

## 5c. Spelling check (plan 10B, 0.8.1, D12)

`Check(word)` → `SpellingCheck(Status, Candidates)`: **Known** when the lowercase form is in the lexicon — and, since
0.8.3, not in the table `spelling_unlisted` (`KnowsSpelled`) — or the active `User` snapshot (accents significant); **SoundsLike** when not known but a « Mes fautes » spelling (accent-stripped,
exact), the same letters without accents (`_strip`, « ecole » → école), or the same **whole** sound key as known words
(an exact match on `_fKeys`, grapheme fold or Lexique pronunciation: « maizon », « famme »; the personal words' fold and
accent-stripped maps too) — candidates in that order, by frequency, at most 6; else **Unknown**. Separate from `Predict`
(which is unchanged): binary searches and dictionary lookups, no LINQ, ≈ 1.4 µs. The rules of what the keyboard marks are the pure
`SpellingIndicator` (same project; [keyboard-design.md §6h](keyboard-design.md)); measures: `Predict.Eval --spelling`,
[../development/evaluation.md](../development/evaluation.md) § Spelling indicator.

**Spelling reference (0.8.3)**: `Data.Build/build_spelling_ref.py` (pinned Grammalecte `French.lex` v7.7, MPL-2.0, commit
08511c2, SHA-256 checked) writes `spelling_unlisted(ortho)` — the 3 836 letters-only lexicon forms that are neither a
French.lex form nor one once « oe » is written « œ » (« yeah », « team », « mam », « till ») — and `spelling_source` into
`spelling.sqlite`, a separate MPL-2.0 file (publication cleanup, 2026-11-16; up to pack 2026.11 the tables were inside
`prediction.sqlite`). `Load(db, spellingPath = null)` reads `spelling.sqlite` next to the database, else the old tables
of the database (`UnlistedCount`; none = every lexicon form trusted, the 0.8.2 behaviour): an unlisted form is
checked like an unknown word and is never a ≈ candidate (`Offerable`), nor its own. `KnowsForMark(word, sentenceStart)` is
the typing ✓ by the same rules (and no ✓ for a very rare pack name at a sentence start). `Predict` never reads the table.

## 5d. Proper names — the « Noms propres » layer (plan 10C, 0.8.2, D30)

`NamesFile` parses the documented `pepite-noms` format (JSON with a forward-only `Utf8JsonReader`, CSV; [noms-format.md](noms-format.md)),
shared by the shipped pack and the teacher's import. `NamesLexicon.Build(names, isLexiconWord, frequent300)` is a third,
read-only layer next to the lexicon and `User`: sorted lowercase keys + an accent-stripped map (first entry + a « next »
chain, no per-key lists), entries `(Form, Lower, Shown, Freq = 5 × rang per million, Recognised)`; variants point to the
name they stand for; capitalised parts of compounds the lexicon does not know are indexed (no regnal numbers); names
equal to a top-300 form are left out. `PredictionEngine.Names` (volatile swap, `Empty` = 0.8.1 results byte for byte).
`Predict`: after the main and personal passes, `NamesPass` takes the best k + 4 names of the prefix range by frequency
(tier 1 keys always, tier 2 keys only with a capital typed inside a sentence) and scores them as unigrams of that frequency
without any context counts (×0.1 lowercase inside a sentence, ×1 at a sentence start, ×30 with a capital inside a
sentence, plus `NameCapitalBonus` 2.5 in log10 — also given to the pupil's / class's names in the personal pass, so they
stay ahead; ≥ 2 letters without that capital; restricted figures only with a capital and ≥ 3 letters; the elision boost for a vowel-initial name; a rare lowercase homograph
≤ 1 per million is replaced), then merges them; a capitalised candidate that is a pack form
is flagged `Suggestion.Name`. **0.8.3**: with a capital typed inside a sentence and fewer than `NamesCapUntil` (3)
letters, every first p words hold at most ⌈p / 2⌉ names — whatever number the bar shows of the k asked (the best name keeps
its rank; names fill only when nothing else is left);
a mute-h name (h + vowel, not an aspirated h of `Elision.HasAspiratedH`) takes the elision boost; the caller passes
`sentenceStart` true where a capital says nothing (`CapitalContext`: after `: « " — –`, or no known word before).
`Check`: a pack form is Known; the same letters as a pack form in another case or without
accents are SoundsLike. `KnowsAny` includes exact pack forms. Measures: [evaluation.md](../development/evaluation.md) §
Names pack.

## 5e. Grammar data and grammatical prediction (plan 11: 10D-0 data, 10D-1 agreement, 0.9.0)

`GrammarLexicon.Load(path, engine)` (worker thread) reads `pred_index` of `grammar.sqlite` (Grammalecte French.lex,
MPL-2.0; [data-pipeline-design.md](data-pipeline-design.md) § 8), maps each form to the engine entry through the internal
`IndexOf(lower)` and keeps immutable CSR arrays: `Off[n + 1]`, `Bits[]` (`MorphBits`, our layout), `Lemma[]` per entry,
and lemma → lexicon entries (`FormsOfLemma`). The connection is closed on return (no pooling). The file's schema must be 1.
Readable by any thread. `PredictionEngine.Grammar` is a volatile slot as `Names`; `GrammarLexicon.Empty` is the default.
In 10D-0 no pass read it (every result equalled 0.8.3, checked on `cases.json` and both held-out `--dump` files); 10D-1
scores with it (below). Real data: 119 849 forms, 84 795 lemmas, 152 062 analyses. A second row for an entry already set is skipped.
`LoadFromMorph` (internal, review 10D-0) builds the same arrays from `morph` alone (no `pred_index`, so no Lexique
spellings in the file). It is measured for the 10D-1 choice and not used by the app.

Refactor that came with it (output identical): the unigram map `OrthoLower → probability` became `_idxOf: OrthoLower →
entry index` (`TryUni(lower) => _uni[idx]`; the forms are unique in lowercase and the most frequent entry is kept as
before), so the personal pass, `PredictNext` and the grammar lexicon get the entry index for free. `FormAt` / `FreqAt`
(internal) serve `Predict.Eval` (`InternalsVisibleTo` Predict.Eval and the tests).

### Grammatical prediction (plan 11, 10D-1, 0.9.0)

`AgreementContext.From(p3, p2, p1, lookup)` (pure, `AgreementContext.cs`) reads the **last three context tokens** — the
very tokens the n-grams see (`l'école` → `l école`, `mange-t-il` → `mange t il`) — and returns an immutable `Expect`.
The function words are our own closed lists; context nouns, adjectives and verbs are read from their `MorphBits`. Rules,
in this order (at most one verbal source):

| Rule | When | Expect |
|---|---|---|
| **S** subject chain | p1 ends a run of clitics (`ne n me m te t se s le la les l lui leur y en nous vous`) whose leftmost subject pronoun is `je j tu il elle on nous vous ils elles ce c` — not after a finite verb of the subject's person or with a `t` before the subject (inversion `mange t il`, `dit il`; « la cantine, ils » is no inversion; an elided `c'` / `j'` is never inverted), not after a preposition (`pour nous`), and `nous` / `vous` not after a verb (`va nous voir`: an object) | verb person (`ce` 3s\|3p); preposition, conjunction, determiner, subject pronoun disfavoured; **wide** (our extension): noun and adjective too, and past participle / infinitive after a bare subject — the subject alone, + `ne`, or + object / reflexive clitics (`je les`, `je ne les`, `il nous`, `nous les`; owner 2026-11-16), `nous` / `vous` only with a clitic after them, never after a word that can be a verb (`puis-je vous raconter`); after a verb there is no subject chain (`je vais les`, `il faut les` + infinitive) |
| **N** determiner | p1 a determiner (`les des ces mes tes ses nos vos leurs aux quelques plusieurs deux…vingt cent mille` p; `le un du au ce cet mon ton son` m s; `la une cette ma ta sa` f s; `l notre votre` s) not in a subject chain (`je les` → S), or an adjective after one | nouns by number, adjectives by gender and number (the adjective's gender when not epicene: `les petites` → f p); **wide**: a finite verb disfavoured (`les mange`) |
| **NP** noun group | p1 a noun after its determiner, or after determiner + adjective (unless p1 is a finite être / avoir after its subject: `le petit est`) | a following adjective agrees with the noun (`les maisons blanc…` → `blanches`); a verb 3s / 3p, weighted `WNounSubj` |
| **E** être | p1 a **finite** form of être after its subject (pronoun chain or noun group at p2): « il a été » does not fire | participles and adjectives agree with the subject (gender unknown for `je tu nous vous on`, number open for `vous on ce`); infinitives and 2p forms disfavoured |
| **A** avoir | p1 a finite form of avoir after its subject | a past participle, any gender / number (`il les a mangées`); infinitives and 2p disfavoured |
| **I** infinitive | p1 ∈ `à de d pour sans`, a finite modal (`pouvoir vouloir devoir aller savoir falloir`) or `faut` (`par` only as a sweep option) | an infinitive; past participles and 2p disfavoured |

**Score term** (`Expect.Term`): for a candidate's analyses, + `AgreeBonus` · W when one analysis agrees (the largest
weight), − W when none agrees and every analysis is of a constrained or disfavoured part of speech (the smallest weight),
else 0 (unknown word, another part of speech, nothing expected). The plan's formula is `AgreeBonus = 1`; measured, a full
bonus lifted every agreeing verb over the clitics and adverbs that often come first after a subject (« je ne », « je
vous », « je te »: −0.7 pt of subtitle KSS), so the sweep also tries 0 and 0.5 (only the forms that disagree lose; the
siblings' order is the same). W is `WNominal` for nouns / adjectives after N / NP, `WVerbal` for the
verbal rules and the disfavoured parts of speech, `WNounSubj · WVerbal` for NP's person. A noun is never judged by gender
(owner Q5: « le mais » still offers `maison`). The term is added in the literal, sound and edit passes (the entry index is
known), in the personal pass for list words the lexicon knows (else `Consider`'s max would drop a penalty; names stay
neutral, as the pack's) and on `PredictNext`'s successors. It depends on the word only, so the « already scored »
shortcuts of the sound and edit passes stay valid.

The **wide** lists (`WideDisfavour`, on) are ours: with the plan's lists a verb that is also a noun, an adjective or a
participle (`fait`, `dit`, `est`, `porte`) was never judged after a subject (« tu fait » stayed first), and the subtitle
KSS gained +0.24 pt on the dev slice with them. Their first version also disfavoured infinitives after any chain, which
lost « puis-je vous raconter », « il va nous voir » (P-auto Q/Y/2p swap −3 pts): hence the bare-subject and object
`nous` / `vous` limits.

**Adding the agreeing form** (`Inject`, after the names pass, only when a rule applies): (1) when the typed word is a
member of a homophone family (`HomophoneFamilies`: {a, as, à} · {est, es, et} · {sont, son} · {ont, on} · {sait, sais, ces,
ses}) and another member agrees, it is added — never `ces` / `ses`; (2) for the best `InjectSources` (5) lexicon
candidates that do not agree themselves and are the word typed in full or a sound-alike / correction of it (a mere
completion of the typed letters lends nothing: typing on reaches the agreeing form, and adding forms while a word is
being typed cost 1.7 pt of KSS in the first measurement), the other forms of their lemmas that agree, start with the same
letter (accent folded): the `InjectPerSource` (2) most frequent; one that is no candidate yet is added, one that is a
candidate already with a lower score is lifted to that score (« tu fait » → `fais`, found by its sound far below; a
literal-prefix candidate keeps its colour). The score is the source's score without its own term − `InjectGap` + the
form's own term (`il a manger` → `mangé`, `les journal` → `journaux`, `il et` → `est`, `ils son`
→ `sont`). Colour (Q3): « sounds like » (Phonetic) when the two forms have the same Lexique pronunciation (an FNV hash of
`words.phon` per entry), and always inside a family; the correction colour (Edit) otherwise. An agreeing sibling already
found as an edit (`manger` → `mangé`) takes the « sounds like » colour when it is pronounced as the source.

**Weights** (public fields; defaults from `Predict.Eval --sweep-grammar`, [evaluation.md](../development/evaluation.md)
§ Grammar 10D-1): `WNominal`, `WVerbal`, `WNounSubj`, `InjectGap`, `AgreeBonus`, `WideDisfavour`, `ParInfinitive`, `InjectSources`, `InjectPerSource` — chosen: `WNominal = 1`,
`WVerbal = 1`, `WNounSubj = 0.5`, `InjectGap = 0.5`, `AgreeBonus = 0`, `WideDisfavour = true`, `ParInfinitive = false`,
5 sources × 2 forms;
`GrammarOn` (the app's `--no-grammar` test switch loads nothing instead). `GrammarOn = false`, `WNominal = WVerbal = 0`, or
`Grammar = Empty` give 0.8.3 byte for byte (`cases.json`, both held-out `--dump` files; xUnit on 500 queries), except the
next-word lists that held an accent variant / unlisted form (the review's next-word filter, which applies with or without
grammar; counted in evaluation.md § Grammar 10D-1).

**Cost**: the `Expect` of the last context is cached (one entry, keyed by the grammar snapshot, p3 / p2 / p1 and the
options). With `AgreeBonus = 0` every term is ≤ 0, so it is not added in the passes: `LazyTop` takes the candidates out of a
heap by their score without the term, adds the term (a lexicon form only: the key must be the lexicon spelling, so a name
with a capital is never judged) and stops as soon as a score without its term is below the k-th score with its term.
This gives exactly the order of a full sort of the final scores, and it is faster than the full sort it replaces (the
analyses of thousands of 1-letter candidates are random memory reads). In that mode the personal pass gets no term either
(the ranking adds it to a list word the lexicon knows), and an injected form is stored without its own term. One nuance
against the in-pass mode: when a pack name replaces its rare lowercase homograph (`NameMergeBelow`), the homograph's score
is taken before its term (the name itself is never judged). With a bonus the term is added in the passes. The best sources
of injection are kept while the passes run. Thread: the UI thread, inside `Predict` / `PredictNext`, as before;
`GrammarLexicon` stays immutable for the 10D-2 / 10D-4 workers.

**Review 10D-1 fixes**: a sibling is lent only through an analysis of the lending lemma and of the source's part-of-speech
class (a verb lends verb forms, a noun / adjective nouns / adjectives: « être » no longer lends « étais » as the plural of
the noun « étai » after « deux », « les », « mon vélo neuf »); `nous` / `vous` after a noun, an unknown word (a name) or
« qui » are objects (« le maître nous a », « celui qui nous a », « Léa vous dit »: no rule); a numeral after a noun is an
adjective, not a determiner (« un vélo neuf »); a noun group after « et » / « ou » (« le chien et le chat ») gives no verb
person and leaves the participle's gender / number open. Next word, grammar or not (a correctness fix): a corpus spelling
that is no lexicon form but the accent variant of one (« ést », « etait ») is never proposed; an unknown word that is no
such variant (a name) still may be. The lexicon forms left out of the spelling reference are kept: they hold the lowercase
names after « en », « à » (« amérique », « berlin », « york »), and filtering them cost 91 words of subtitle KSS.

Limits: three tokens only (« les enfants de la classe mang… », « elle n'est pas allé » are out of reach: neutral, never
wrong), participle agreement with a preceding object after avoir is not attempted, `ces` / `ses` stay with the language
model.

## 6. Design choices and rejected alternatives

| Choice | Alternative rejected | Reason |
|---|---|---|
| SQLite for n-grams | everything in RAM | RAM would scale with the corpus (≥ 1 GB); SQLite lookups are fast when cached |
| stupid backoff | Kneser-Ney / interpolated KN | more complexity for a small expected ranking gain at 5.7 M sentences (Brants et al. 2007); deliberately skipped |
| rules + Lexique `phon` | espeak-ng G2P | GPL; Lexique already holds the pronunciations of every word in the lexicon |
| edit-1 only, ≥ 4 letters | edit-2 / always on | edit-2 explodes the candidate set; on short prefixes edit-1 drowns the bar in noise |
| per-word kind (`MatchKind`) | score only | the UI colours suggestions by why they were proposed |
| personal words: a fixed unigram-level lift (plan 10A) | a log-scale bonus (× 10–100 on the probability); a mixture share λ/N of the list | the log bonus beat context predictions and the list's own inflections (lesson set −0.7 to −3 pts, Wikipedia neutrality +0.1 to +1.0 pt); a λ/N share gave short lists of names a huge per-word weight (measured, evaluation.md) |

## 7. Extending

- **New generator** (e.g. keyboard-adjacency typos): add a loop in `Predict` calling `Consider(best, …)`
  with its own penalty and `MatchKind`, then measure with `Predict.Eval` (curated + held-out).
- **New LM level / neural reranker**: replace `Lm(ctx, w, uni)`; keep the candidate stage. A reranker
  would only re-score the top ~50 candidates.
- **Native port**: the public surface (`Load`, `Predict`, `PredictNext`, `Knows`) maps one-to-one to the
  C ABI of the early design (a flat `extern "C"` surface over the same four calls).
