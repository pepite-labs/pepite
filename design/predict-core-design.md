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
- **Tolerant variants (0.11.0, plan dys-sound-tolerance)**: /ʃ/ `C` and /ʒ/ `j` — like the other voicing pairs f/v, k/g,
  t/d, p/b — stay apart in both keys (bouge ≠ bouche). For a pupil who confuses a pair, `PhoneticKey.Variants(key, pairs)`
  gives the typed key with 1–2 of its first 4 swappable symbols swapped (« CardE » → « jardE »; at most 10 keys, never the
  key itself). The fold is symbol by symbol, so a variant of a typed prefix is a prefix of the meant word's key: the
  variants are looked up in the existing sound index, with no new index and no data rebuild (§ 5h).

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

**Elided forms' unigram (0.10.2).** Lexique has apostrophe rows only for `l' d' n' s' t'`; the other bare forms
(`j m c qu jusqu lorsqu puisqu`) are letter names or rare rows, so at a sentence start `j` never offered `j'`. At `Load`,
for each bare form without an apostrophe row (`Elision.FullForm`: j → je, qu → que…):

```
 est(b)    = lexFreq(full form) × Σ c(b → vowel / mute-h word) / Σ c(full form → ·)      (per million)
 P_uni(b)  = (max(lex(b), ElisionUnigram × est(b)) + 1) / Z
```

The estimate reproduces Lexique's own rows within ≈ 35 % (l' 13 790 vs 12 747, d' 10 053 vs 11 876, n' 11 869 vs 8 860,
s' 6 086 vs 5 392; `Predict.Cli --probe` prints them all). t' (9 111 vs 4 344) is inflated: the corpus tokens split the
euphonic « -t- » of « a-t-il » into a bare « t » before a vowel, counted as an elision (t' keeps Lexique's row anyway). `Entry.Freq` (prefix gate, spelling order) and `Z` are not
changed, so a bare form keeps its own prefix gate (`jusqu`, `lorsqu`, `puisqu` have rare Lexique rows: a partial gate);
`PredictNext` is unaffected: its candidates come from the n-gram maps, so its language model never falls back to the
unigram. `ElisionUnigram` = 1 by default, 0 restores 0.10.1 exactly (the
loaded unigrams are kept and put back). The ≈ 11 000 bigram rows it needs (≈ 50 ms of index seeks) are read on a second
read-only connection while `Load` builds its indexes, so the load grows by ≈ 10 ms (evaluation.md).

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
**0.10.2** (owner 2026-11-17, D30 amendment): with no capital that tells a name (lowercase, or a sentence start) and at
least `NamesRecognisedFrom` (4) letters, `NamesPass` also walks the tier-2 keys, keeping entries with
`Freq ≥ NamesLexicon.FreqCap × NamesRecognisedFloor` (0.02, ≈ 300 bearers; 2 072 first names in pack 2026.2) and never a
restricted figure; `NamesRecognisedFrom = 99` gives the 0.10.1 results. Pack 2026.2 also moves the 1 000 most given
first names of all time into tier 1 (rang max(recent, lowest recent-tier rang, 0.5 × all-time): Roger 481st, Martine 523rd).
`Check`: a pack form is Known; the same letters as a pack form in another case or without
accents are SoundsLike. `KnowsAny` includes exact pack forms. Measures: [evaluation.md](../development/evaluation.md) §
Names pack. **0.11.0**: hyphenated names after the hyphen is typed (« saint-n » → « Saint-Nicolas ») and in the last place
for a word that starts one (« saint » → « Saint-Marin »): § 5k.

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

## 5f. Exact typed word (0.10.2, plan exact-typed-word, owner 2026-11-17)

The word being typed, when it is spelled right — the ✓ while typing, `KnowsForMark(typed, sentenceStart)`, two letters at
least, not all capitals, not a bare elision form — is always one of the bar's words. A post-ranking step,
`PlaceExact`, after `CapNames`; the passes and the scores are unchanged.

- **Fields**: `ExactTyped` (`ExactTypedMode` Off / Present / First, default **Present**), `ExactBar` (8; the keyboard sets
  its word count, it asks for 3 spares), `ExactMargin` (+∞, First only). `Predict(..., exactMark)`: the keyboard passes
  its own ✓ while typing (`SpellingIndicator.Typing` with the tracker's sentence start, not the `start || neutral` it gives
  `Predict`), so the rule applies exactly when the ✓ would (no rule after a digit, in a compound, an address…), whether the
  marker is shown or not (the indicator setting only hides it); null = the engine judges with
  `KnowsForMark(typed, sentenceStart)` (Predict.Eval, tests).
- **The word X**: the result equal to the typed letters ignoring case; else the candidate of the typed form, the lexicon
  form (first at a sentence start: « Mai » is the month), a personal or a pack form of the same letters, with its ranking
  score (the lazy agreement term included); a pack or personal form no pass kept gets the window's lowest score − 0.001. A
  restricted figure (D30) only where the names pass would offer it (a capital inside a sentence, 3 letters).
- **Present (default)**: when k ≥ `ExactBar` and X is not among the first `ExactBar` results, X goes to slot `ExactBar` − 1;
  every other result keeps its order. With k < `ExactBar` (the KSS queries, k = 1) nothing changes, so `Predict(k)` stays
  the prefix of `Predict(n)`. **With a one-word bar (`ExactBar` < 2) the rule is off**: its last slot is its first, the
  « first » variant the owner rejected (E3 KSS 54.6 → 50.7 with bar 1).
- **First (measured, rejected)**: the window is N = max(k, `ExactBar`); the results ranked above X that sound like it
  (`Homophone`: the same Lexique pronunciation or `HomophoneFamilies` family for two lexicon forms, else the same grapheme
  fold) — or beat it by more than `ExactMargin` — stay ahead in score order, X follows (slot `ExactBar` − 1 at worst), then
  the rest. Homophones beyond the window are found by a scan of the candidates scored above X.

**Why Present** (gate table: [evaluation.md](../development/evaluation.md) § Exact typed word): First, the owner's
preference, made every typed prefix that is itself a word win over its completions (« mai » before maison, « par » before
partir): target top-1 at such a prefix 0.53 → 0.18 (E1), KSS −1.8 to −2.9 pt on the five held-out sets, `cases-personal`
0.491 → 0.333, « Mes fautes » 1.000 → 0.789, grammar sets −9 to −19 pts. A margin of 1.0 (a completion ten times more
probable stays ahead) still cost 0.21–0.38 KSS pt, `cases-personal` 0.456, « Mes fautes » 0.974. Present leaves KSS and
top-1 unchanged (dumps byte-identical; hit@8 at 2–3 letters 0 to −0.4 pt, when the typed word takes the target's 8th
slot) and puts X in the 8 for 0.9998–1.0000 of the held-out words typed in full (0.994–1.000 without the rule). `cases.json` top-1 is 0.64 with or without the rule (below its 0.66 gate since the names pack, before
this work). Cost: `--bench` +0.001–0.003 ms.

## 5g. Glued words (0.10.2, plan glued-words, owner 2026-11-17)

A pupil who writes by ear glues an elided word to the next one: « je tème » for « je t'aime », « jai », « lécole »,
« cest », « quil », « daccord », « jusqua », « il ma » (m'a). Minimal scope (owner Q1): one bare form + one word; fixed
expressions (« ilya », « parceque »), double clitics (« jtaime ») and 3-word splits are for 0.11.0. No data rebuild.

- **Sound map** (`Elision.Sound`, `SplitsOf`): the fold of each bare form before a vowel — `j m t s n l d` as themselves,
  `c` → `s` (c'est), `qu` → `k`, `jusqu` → `jusk`, `lorsqu` → `lorsk`, `puisqu` → `puisk` (checked against
  `PhoneticKey.Fold` by a test). A typed key splits where it starts with one of them and the next key letter is a vowel
  sound (`a e i o u A E O U w`); « setet » (cétait) splits as both s' and c'.
- **Attested pairs only**: at load the second connection (`ReadBareCounts`) also reads the bigram successors of all 12
  bare forms; the engine keeps those that are lexicon forms taking an elision (13 000 on the shipped DB) and each form's
  successor total. A split word must follow its bare form in the corpus.
- **Bar (`SplitPass`, between the edit pass and the personal pass)**: from `SplitFrom` = 2 typed letters, no capital inside
  a sentence (a name), no apostrophe / hyphen typed. The rest of the key is looked up in the sound index — its exact key for
  one letter, a prefix range from two — and each word that takes an elision, may be offered (`Injectable`) and is attested
  scores `Lm(ctx, bare) + Lm(ctx ⊕ bare, word) + ChainLift + grammar(ctx ⊕ bare, word) − WSplit`. The best `SplitMax` = 2
  go in as one tile each, kind Phonetic (green « même son », owner Q4), written `bare'word` (`Elision.Join`). The grammar
  term is in the score (the lazy ranking finds no entry for « t'aime »), from the context with the bare form at its end
  (« je t » → a verb 1s), with its own one-entry cache. `SplitKnown` (owner Q3): a typed word that is itself a word splits
  too (« il ma » → m'a), ranked by the context. **`Splits` is on by default, `WSplit` = 0** (owner 2026-11-18);
  `Splits = false` turns off the pass and the joined forms of the ≈ check only — the results are then those without
  them, byte for byte; `RankCorrections` (the ≈ list ordered by the context), `Styled` (the document's apostrophe) and the
  hyphen rule of `Elision.Apply` stay on. A pair splits only when it is attested at least `SplitMinCount` = 50 times
  (review 2026-11-18: below that the corpus pairs are mostly tokenisation noise), a one-letter main word only « a », « à »,
  « y », and « au » / « aux » only after qu' jusqu' lorsqu' puisqu' (`SplitPair`, in the bar and in the ≈ check).
- **ChainLift** (a deviation from the plan's literal formula): `Lm` discounts each level from the 4-gram down (α per
  level), and with fewer than three context words every candidate of a query carries the missing levels once — a chain
  would carry them twice (« jai » at a sentence start: α³ for j, then α² for ai). The chain's second word gets back the
  levels its context (or the tables) cannot reach, so a chain and a single word share one offset.
- **Display**: `Elision.Apply` (right after a typed « - » a joined `t'` + il / ils / elle / elles / on is dropped: « a-til »
  never gives t'il; « va-t'en » stays) →
  `Styled` (every apostrophe in the document's style, « aujourd'hui » and « quelqu'un » too, owner Q5) → `Cased`
  (« Jai » → « J'ai »). The tile's picture is its main word's (`PictureStore.PictureForm`, owner Q6: « l'école » →
  école), but a name keeps its full form (« N'Diaye », « M'Bappé »). `Homophone` folds without apostrophes, so a joined
  form counts as a homophone of the typed letters — used only by the exact typed word rule's First mode, not by the
  default Present.
- **≈ check (`CheckSplits`)**: context-free and µs-fast. An unknown finished word whose key splits into a bare form and
  the **whole** key of an attested word gets that joined form as a sound-alike (« lécole »: red → ≈ l'école); at most
  `CheckSplitMax` = 3, each with an estimated frequency `log10(f(bare) × c(bare, word) / c(bare, ·) + 1)` (f per million:
  the calibrated or Lexique elided form), merged into the list by that frequency after a « Mes fautes » target: before the
  first place from which every remaining entry is rarer (the list is sorted per stage only: « sai » keeps « sais » ahead).
- **≈ list ordered by the context (`RankCorrections`, owner Q2)**: when the list opens (click / F9) the keyboard ranks it
  with the words before the finished word: a lexicon candidate by `Lm + grammar` (+ the elision boost), a joined one by the
  chain without its penalty; personal words, names and the « Mes fautes » target keep their places; no context = the
  Check order. For every ≈ list, splits on or off.

Weights and gates: [evaluation.md](../development/evaluation.md) § Glued words. Speed: `--bench` +0.002–0.008 ms, `Check`
p99 12.7–13.6 µs, `--bench-split` p99 0.6 ms once warm; its isolated maxima (12–26 ms) also occur with splits off
(10–17 ms): pre-existing, accepted by the owner (2026-11-18), investigated separately (TODO: GC or scheduling).

### 0.11.0 — « cé », « jtaime », « ilya » (plan glued-words-followup, owner 2026-11-27)

Three narrow kinds on the same machinery, each with its switch (`EstSplits`, `Split3`, `Expressions`; on by default;
`Splits = false` turns them all off; the three off give the 0.10.2 results byte for byte). They share the one cap
`SplitMax` = 2 of glued tiles, kind Phonetic (green), no capital inside a sentence, nothing when an apostrophe / hyphen
is typed.

```
 typed fold f ─┬─ 0.10.2: bare sound + rest ──► word (exact for one symbol, prefix range) ─► b'w      [any typed word, SplitKnown]
               │    + A: rest = e after c s n qu ──► « est » (Lexique's `E` is too short to be indexed)
               │    + A: « ce » / « se » typed in full (f = s) ──► c'est / s'est (+ the literal-prefix bonus)
               ├─ C: subject sound (je j tu il el ele O nu vu) + clitic m t s n l + word ─► « S b'w »  [unknown word, ≥ 4 letters]
               │     attested c(S, b, w) ≥ 50 in the trigrams preloaded at load (45 contexts)
               └─ B: f a prefix of an expression's sound key, longer than its first word ─► « il y a »  [unknown word, ≥ 4 letters]
 finished unknown word ─► Check: the same, on the WHOLE key (B also by its accent-stripped letters), merged by an
                          estimated frequency ─► ≈ list ─► RankCorrections: any multi-word candidate scored by ChainScore
```

- **A — « est »** (`Elision.EstForms` = c s n qu; `_estIdx`): when the rest after the bare sound is exactly `e`, « est »
  joins the words of that key (« es », « ai ») with the same filters and score (`SplitPair`, `Injectable`); « et » never
  (it shares the sound, and « c et » is corpus noise). The **mute e**: « ce » / « se » fold to `s` (no vowel after the
  bare sound), so the typed letter's own bare form gets the rest `e` (« ce » → c'est, never s'est; bar only — « ce » is ✓).
  That tile also gets the **literal-prefix bonus** (`MuteEPrefix`, a deviation from the plan's « same score »: « ce » is the
  start of « cest »; without it the frequent « ce… » completions kept c'est out of the 25 first candidates).
  In the bar the sound `e` must be the typed word's last letter, an e, and the word 3 letters at most (« cé », « nè »,
  « kè », « qué »; another narrowing of the plan: « nei » is the start of neige, a dys guard). `EstFromKnown` (default
  true; false = no « est » for a typed word that is a word, « ce » / « se » excepted).
- **C — three words** (`Elision.SubjectSounds`, `Split3Pass`): the fold starts with a subject's sound — also with the
  letters a glued subject keeps (« je » `je` / `j`, « elle » `el` / `ele`, « ils » `il` / `ils`, « vous » `vu` / `vus`, « on »
  `O` / `on`) — the rest splits as a glued word over m' t' s' n' l' (a « s' » read `z` after a vowel is read back
  as `s`: « ellesapelle »), the word is looked up as above (+ « est » after s' n'). Attested: the trigram « subject clitic
  word » ≥ `Split3MinCount` = 50 (`ReadGluedCounts` on the second connection: 45 contexts, ≈ 2 500 rows, 209 ≥ 50). Score
  `ChainScore(ctx, [S, b, w]) + grammar(ctx ⊕ S ⊕ b, w) − WSplit3`; tile « S b'w ». Only for a typed word that is no word
  (`Split3Known` = false, owner Q1), from `Split3From` = 4 letters.
- **B — expressions** (`GluedExpressions`: 22 B1 rows, 11 B2 rows, ours): at load a row is kept when every token is a
  lexicon form and its glued letters are no word (« cava », « tues », « tuas » are: ça va, tu es, tu as left out; owner
  Q2); its n-gram count (≥ `ExprMinCount` = 100) and Check estimate `log10(f(t1) × c(t1..tn) / c(t1, ·) + 1)` are read
  then. Sound keys (≤ 3): the fold of the glued letters; the tokens' Lexique pronunciations read as one word; the same
  with the clitics' mute e silent (« eske » → est-ce que, « keske », « toulmonde »). A key equal to a word's whole key is
  counted (10 on the shipped data), not dropped: the ranking orders them (« parceque » ≈ parce que, then parsec). Bar:
  from `ExprFrom` = 4 letters, unknown typed word (`ExprKnown` = false), the typed fold a prefix of a key and longer than
  its first word's part (« il » never shows « il y a »), and not the start of a lexicon word (`ExprNotPrefix`: « Sylv » is
  Sylvie, not « s'il vous plaît »; the plan's fallback `ExprFrom` = 5 would also lose « ilya »); score `ChainScore − WExpr`.
  **B2** (« ilest » → il est, « ilé » ≈ il est, `ExprB2`) is **off**: it failed its own gates (plan fallback « drop B2 »).
- **`ChainScore(ctx, tokens)`** = `Lm(ctx, t1) + Σ k≥2 [Lm(ctx ⊕ t1..tk−1, tk) + Lift k]`, Lift k = ChainLift for each later
  token (`WLm × (3 − min(have k, reach)) × −log10 α`); a bare form's successors from the load, « subject clitic » ones from
  the preload, the others from the cached n-gram reads. `RankCorrections` scores any multi-word candidate (no capital,
  every token a lexicon form) with it, its last word with the agreement term after the others.
- **D35**: every lexicon token passes `Injectable` (a bare form its entry flag), and **`ExcludedWords.IsExcluded` also
  tests each space-separated part** (and the part after each apostrophe in it): a listed word is never inside a tile, the
  safety net included (`Predict.Eval --probe-exclusions` types every listed form glued after a clitic and a subject +
  clitic too). Side effect: a personal word or name of several words with a listed word in it is now never offered either.

Numbers and gates: [evaluation.md](../development/evaluation.md) § Glued words follow-up (0.11.0).

## 5h. Sound confusions (0.11.0, plan dys-sound-tolerance, owner 2026-11-26)

Voiced / voiceless confusions are « frequent and persistent » in phonological dysorthographia, and the pairs differ per
child (study `dys-sound-confusions.md`). A pupil who writes « chardin », « jien », « gien », « mancher » wrote a
*different* sound key, one symbol away (`CardE` / `jardE`), which neither the sound pass nor the edit pass (ch ↔ j is two
letter edits) bridges.

```
 typed ──► Fold = f ──┬─► literal prefix                      + WPrefix·gate
                      ├─► sound prefix (f)                     + 0
                      ├─► tolerant: v ∈ Variants(f, Tolerance), f ≥ TolerantFrom symbols   − WTolerant, green (Phonetic)
                      └─► edit-1, splits, user, names, grammar …
 finished word ──► Check: Mes fautes, same letters, same whole key, splits │ then whole key = v  ──► ≈ list, tier 2
```

- **`Tolerance`** (`SoundPairs` flags: ChJ, FV, KG, TD, PB; one volatile int, the app may set it per profile): the enabled
  pairs. `None` gives the 0.10.2 results byte for byte (measured: curated logs and held-out dumps).
- **Bar (`Predict`)**: after the sound-prefix pass, from `TolerantFrom` = 3 key symbols (« je » / « chez » get none), each
  variant's prefix range in `_fKeys`; a word not scored yet gets the sound pass's score − `WTolerant` (owner Q-A: a fixed
  penalty, log10 scale like `WEdit`), kind `Phonetic` (the green « même son » tile, owner Q4). A word already scored keeps
  its score, so the literal prefixes and exact sound-alikes are never touched. When `WTolerant` > `WEdit` the edit pass
  compares with a tolerant score instead of skipping it. `Homophone`, `PlaceExact`, the split, personal and names passes
  are unchanged (no tolerance for personal words or names).
- **≈ check (`Check`)**: after the splits, from `TolerantCheckFrom` = 2 symbols, the words whose whole key equals a
  variant fill what the exact candidates left of the 6 slots, most frequent first; `SpellingCheck.Tolerant` = how many of
  the last candidates came that way. « chardin » becomes ≈ jardin (owner Q5) instead of « ? »; a known word stays ✓.
- **≈ list in two tiers (`RankCorrections(…, tolerant)`)**: the exact candidates are ranked by the context among
  themselves, then the tolerant ones among themselves: a confusion never goes ahead of an exact sound-alike.
- **Weights**: `WTolerant` = 1.5, the only value tried that leaves every curated set identical (0.5 is best on `chj-full`
  top-1, but 0.5 and 1.0 move one `cases-names` rank); held-out KSS unchanged at 1.0 and 1.5 (0.5 not run) (evaluation.md § Sound confusions). **Default `Tolerance = ChJ`, `WTolerant` = 1.5**
  (owner 2026-11-26, D33).
- **Per profile (Phase 2, D33)**: the keyboard sets `Tolerance` from the profile's « Sons que l'élève confond »
  (`words.soundPairs.*`, keyboard-design §6c / §6h): ch/j on by default, f/v, k/g, t/d, p/b ticked per pupil. Measured
  with each pair (evaluation.md § Sound confusions — the other pairs): `<pair>-full` hit@8 0.91–1.00, the guards never
  move, KSS ≤ 0.01 pt per pair and −0.10 pt at most with all five (W). `Check` p99 on a loaded host: all five 19.0 µs,
  ch/j 19.1 µs (`--spelling`; ch/j alone also over 20 µs in other runs): it measures the host, so the plan's fallback
  « one swap only in Check » is **not applied** (2026-11-27: one call per finished word, all five opt-in, and one swap
  would miss the double confusions of the pupils who tick several pairs); a quiet-host re-measure is due before release.
  A word reached through a ticked pair is **green** even when the edit pass scores it higher (owner 2026-11-27: « la
  dable » → table with t/d ticked; the better score is kept, so the rank does not move).
- Speed: ≤ 10 extra binary searches per query and per `Check`; `--bench` within noise, `Check` p99 13.5 µs.

## 5i. Mots exclus (0.11.0, plan offensive-words, D35)

Lexique keeps every attested word, so nothing stopped a slur reaching the bar (the dys-sound review found one, reached
by the ch/j pass for a typed first name). A reviewed list of words is now **never offered**; a word the pupil **types**
is accepted, ✓, inserted, echoed and read like any other (owner Q1).

```
 exclusions\fr.txt ──► ExcludedWords.FromFile (at Load, before the engine is returned)
   (base64 forms, #categories, #lemmas)      │ Entry[] (one bool per lexicon entry) + Forms (case ignored)
 grammar.sqlite loaded ──► WithGrammar: + the French.lex forms of the excluded lemmas whose grammar lemmas are all
                                          excluded and that are no lexicon form (homograph guard) ──► swapped in
 query ──► one snapshot x ──┬─ lexicon passes (prefix, sound, tolerant, edit, splits, Inject family / siblings,
                            │  Check's Offerable): skip Entry[i] before scoring
                            ├─ string sources (personal words, « Mes fautes » targets, pack names, n-gram successors,
                            │  Check's AddFound): skip x.IsExcluded(word)
                            └─ safety net on Predict / PredictNext / Check: drop and count (ExcludedLate, must stay 0)
```

- **Data**: `exclusions\fr.txt` next to the database (`src/Data.Build/exclusions/build_exclusions.py ship`): a header, the
  forms per category, one base64 form per line (owner Q4: out of a child's sight, not a secret), then after `#lemmas` the
  excluded lemmas. Only Lexique forms (CC BY-SA 4.0) are listed; the inflections French.lex alone knows are added at run
  time from the lemmas, so the file holds no MPL-2.0 content. Before the grammar is in, only the listed forms apply.
- **Matching**: `IsExcluded` = the whole word, case ignored, then the part after each apostrophe (« l'… », « qu'… »: a
  joined candidate goes with its word). `ExcludedWords.Empty` gives the 0.10.2 results byte for byte (`--exclusions none`
  measured equal to the previous build on every curated and dys log).
- **Typed word**: `Check`, `KnowsSpelled`, `KnowsForMark` are unchanged (the mark is the one it had without the list);
  `ExactEligible` is false for a listed word, so `PlaceExact` never puts it in the bar. `RankCorrections` only reorders.
- **Names-pack exemption** (owner 2026-11-27): two listed spellings (`lemmas.tsv` rows N01, N02, the `#names` section)
  stay offered as capitalised « Noms propres » pack names only, also at a sentence start; never the lowercase lexicon word.
- **Not filtered** (out of scope): what the pupil types, reads or has echoed; the lesson extractor; pictures in reading mode.
- **No switch** for the built-in list (owner Q3); `Predict.Eval` / `Predict.Cli` take `--exclusions <file>|none` for A/B
  runs only. Phase B adds the Classe's « Mots à ne jamais proposer » through `ExcludedWords.With`.
- **Load log**: counts per category, the file's SHA-256 and the time — never a word.
- Cost: one array read per scored entry (an excluded entry is cheaper than before: it is not scored), a few hash
  lookups per string candidate; measured in evaluation.md § Mots exclus.

## 5j. The missing « ne » (0.11.0, plan negation, owner 2026-11-17)

Spoken French drops the « ne » of a negation; a pupil who writes by ear writes « il veut pas ». `Negation.Find` (pure,
`Predict.Core/Negation.cs`) reads the clause just typed and gives the corrected group, or nothing; the engine's entry point
is `MissingNe(clause, startKnown, plusAllowed)` (`NegationOn`, default on; null without the grammar). `Predict` and `Check`
never call it: KSS and the ≈ lists are unchanged.

```
 clause (≤ 12 words since . ! ? … ; : , or a line break; each exact or not) + tail (« ␣ », « .␣ », « ␣! », « .⏎ »)
   R0  last word: pas jamais rien personne guère, or ≈ to one (« pa », « jamé »); « plus » only when the tail holds
       . ! ? … or a line end (owner 2026-11-27: not , ; :); never next to ' or -
   R1  the word before it: a finite verb (French.lex bits) agreeing with R3, or a ≈ word whose first agreeing
       candidate is one (candidates with a space, an apostrophe or a hyphen are skipped: glued words) — red → nothing;
       a noun / verb homograph after determiner + adjective is that group's noun (« la pauvre demeure pas ») → nothing
   R2  0–3 object clitics before the verb (AgreementContext.IsObjectClitic: me te se le la les l lui leur y en nous vous)
   R3  the subject: a subject pronoun (TryPersons: je … elles, ça, cela, qui → 3s/3p unless « moi / toi … qui »);
       a noun group — [det] [adj] noun [adj], « N de N » (both numbers), « leur » as a determiner — → 3s / 3p;
       a capitalised name (pack, personal, or a capitalised word French.lex does not know) → 3s;
       at a known clause start, a typed imperative → « ne » first;
       « nous / vous » first at a clause start or after an opener (et, mais, où, que…): a verb that may be an imperative
       → the n-grams decide « ne vous V » / « vous ne V » (ChainScore, « n » before a vowel, the opener as context) with a
       margin ≥ NousVousMargin = 1.0 log10, else nothing (owner Q6); otherwise the subject
   R4  no « ne / n' » in the run; every word from the subject (its determiner) to the negation word exact
   R5  « ne » (« n' » before a vowel or a mute h), « j' / c' » → « je / ce », the ≈ verb and negation word written right,
       the first word's capital kept (« Ce n'est pas », « Ne mange pas »); D35: a group holding an excluded word is dropped
```

- **Guards** for « jamais / rien / personne » (« ever / anything / anyone »): a superlative or comparative, « si »,
  « sans », « avant », another negation word (« ne », « n' », « pas » too: « il ne dit pas qu'il a rien vu »), or
  « est-ce » before the subject → nothing (« le meilleur ami qu'il ait
  jamais eu », « est-ce qu'il peut rien comprendre »). « que » alone is no guard (measured: it cost recall, no precision).
- **« s'il » / « l'on »**: « s' » before « il / ils » is « si », « l' » before « on » is euphonic — the pronoun run stops
  there. « qui », « ça », « cela » end the run too (« ce qui serait pas »).
- **Output**: `NegationFix(From, Original, Replacement, Rule, Negation, VerbFixed, NegFixed)`: the words `From`..last are
  replaced, `Original` their exact text (separators between them included), `Replacement` with straight apostrophes
  (the keyboard writes the document's). `Negation.ClauseOf(text, exactFrom, textStartKnown, …)` builds the clause from a
  text exactly as the keyboard's tracker does (an xUnit test checks both agree); Eval and the Cli use it.
- **Known false alarm** (accepted, plan): « j'ai pas mal de » (« a lot of ») gets « je n'ai pas mal de ».
- **Cost**: a few dictionary lookups and `Check` calls per finished word; the « nous / vous » case reads a few n-gram rows
  (cached). `Predict.Cli --bench-negation`: p99 ≈ 17 µs (evaluation.md § Négation).
- **Out of scope** (fuller scope, 10D-2): an adverb in between (« veut toujours pas »), « y a pas » / « faut pas » without
  « il », « pour pas + infinitive », « ne … que », « personne / rien » as subject, glued subjects (« jai pas »), the
  sentence-end re-offer, a direct bar tile, a per-profile setting.

## 5k. Compounds after a typed hyphen (0.11.0, plan hyphen-names, owner 2026-11-27)

« saint-n » gave « ne, non, nom… »: a typed `-` ends the word (keyboard tracker, `SpellingIndicator.IsWordChar`), so the
letters after it were predicted alone — not a regression, a gap since the start. Two parts, each with its switch (both off
= the results without them, byte for byte; Predict.Eval / Cli `--no-hyphen`, `--no-compound-slot`):

```
 « je vais à saint-n »                       head = « saint » (HyphenHead.Find, as the tracker finds it)
   compounds  = Predict(« saint-n », no context, k + 4, literal prefixes only,
                        sentenceStart: head not capitalised inside a sentence)     ≤ min(k, HyphenMax = 5) of them
                → names (pack forms, capitalised personal words): Saint-Nicolas, Saint-Nazaire (their own form)
                → lexicon compounds: saint-nectaire (head as typed)
   today      = Predict(« n », context, k)   (PredictNext when nothing is typed after the hyphen)
   bar        = names + today's top-1 + (today's others ⋈ lexicon compounds, merged by score, today's order kept)
                − today's words a name writes (« nicolas ») − lexicon compounds whose ending is a today word (« être »)
                + the exact typed word kept in the bar (Present rule)              → Net (one ExcludedWords snapshot)
```

- **`PredictAfterHyphen(head, typed, context, k, sentenceStart, headNeutral, exactMark)`** (`HyphenCompounds`, on): the
  compounds are found by the ordinary prefix machinery on « head-typed » with no context, literal prefixes only (no sound,
  tolerant, edit or split pass); a capitalised head inside a sentence asks as a capital does (names lifted, the recognised
  tier open), any other head as at a sentence start — so a lowercase « saint- » gets the names' full share, not
  `NameLowercaseShare` (the hyphen already says « compound »). **Only names go ahead of today's list** (review 2026-11-27):
  a lexicon compound found without context is merged with today's list by score and never ahead of its first word
  (« il va-[t] » keeps today's order, « va-tout » after it; « qu'est-[c] » → « ce »), and one whose ending is one of
  today's words is dropped — that word writes the same text after the head (« peut-ê » → « être »). A misspelt compound
  the bar writes better is denied in code (`MisspeltCompound`: « est-ce-que », no data change). A name or capitalised
  personal word keeps its own form (« Saint-Nicolas »: the keyboard replaces the head, read first); a lexicon compound is
  written with the head as typed and its ending cased like the typed letters (« Porte-m » → « Porte-monnaie »,
  « PORTE-M » → « PORTE-MONNAIE »). A restricted figure (D30) needs a capitalised head inside a sentence and 3 letters
  after the hyphen (`NamesLexicon.IsRestricted`). An empty head, one over `HyphenHeadMax` (40), or no compound → today's
  list unchanged; no compound name slot for the letters after a hyphen. **`HyphenMax` = 5** (the plan's fallback):
  before the review every compound went first and a head with many of them (« ex- », « sous- », « vice- ») filled the 8
  tiles (`--hyphen-report` E2 hit@8 −11.9 pt). The inversion pronouns keep their place (pronoun subset unchanged on the
  four held-out sets).
- **Compound name slot** (`CompoundNameSlot`, on): after the ranking and the exact typed word rule, a typed word with no
  name-marking capital (lowercase inside a sentence, or any case at a sentence start), letters only, ≥ 4, with no name
  « typed-… » in the first `ExactBar` places, gets the best such name in the bar's last place (just before the Present
  rule's typed word when it holds it): a capitalised personal word first (the shortest), else the pack's most popular
  predicted-tier name (then the shortest); never a restricted figure, a variant, or an excluded name unless exempted, and
  never where the context expects a verb (`AgreementRule.Subject`: « elle marie »). Only on a full bar
  (`k ≥ ExactBar`), so the top-1 — and KSS — never changes. « saint » → « …, Saint-Marin ». Held-out (`--slot-report`,
  4–8 letters): it fires in 0.7 % of bars and never pushed a target out of the 8. `CompoundSlotFired` counts the bars it
  changed (Predict.Eval prints it).
- **Cost**: one more `Predict` call (no context: no n-gram rows, literal prefixes only) per update after a hyphen;
  `Predict.Cli --bench-hyphen` (evaluation.md § Noms à trait d'union).

## 6. Design choices and rejected alternatives

| Choice | Alternative rejected | Reason |
|---|---|---|
| SQLite for n-grams | everything in RAM | RAM would scale with the corpus (≥ 1 GB); SQLite lookups are fast when cached |
| stupid backoff | Kneser-Ney / interpolated KN | more complexity for a small expected ranking gain at 5.7 M sentences (Brants et al. 2007); deliberately skipped |
| rules + Lexique `phon` | espeak-ng G2P | GPL; Lexique already holds the pronunciations of every word in the lexicon |
| edit-1 only, ≥ 4 letters | edit-2 / always on | edit-2 explodes the candidate set; on short prefixes edit-1 drowns the bar in noise |
| per-word kind (`MatchKind`) | score only | the UI colours suggestions by why they were proposed |
| sound confusions: variants of the typed key, looked up in the sound index, penalised (0.11.0) | weighted grapheme edits in `Edits1` (ch↔j, f↔v…); merging ch/j in the key itself | edits match finished words only (no gain while typing) and duplicate the fold's context rules (g before e/i); a merged key makes bouge / bouche homophones for `Check`, the colours and the exact typed word rule, and cannot be switched off per pupil |
| personal words: a fixed unigram-level lift (plan 10A) | a log-scale bonus (× 10–100 on the probability); a mixture share λ/N of the list | the log bonus beat context predictions and the list's own inflections (lesson set −0.7 to −3 pts, Wikipedia neutrality +0.1 to +1.0 pt); a λ/N share gave short lists of names a huge per-word weight (measured, evaluation.md) |

## 7. Extending

- **New generator** (e.g. keyboard-adjacency typos): add a loop in `Predict` calling `Consider(best, …)`
  with its own penalty and `MatchKind`, then measure with `Predict.Eval` (curated + held-out).
- **New LM level / neural reranker**: replace `Lm(ctx, w, uni)`; keep the candidate stage. A reranker
  would only re-score the top ~50 candidates.
- **Native port**: the public surface (`Load`, `Predict`, `PredictNext`, `Knows`) maps one-to-one to the
  C ABI of the early design (a flat `extern "C"` surface over the same four calls).
