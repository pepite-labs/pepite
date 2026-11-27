# `Predict.Eval` design — the predictor benchmark

Source: [`src/Predict.Eval/Program.cs`](../src/Predict.Eval/Program.cs),
[`src/Predict.Eval/cases.json`](../src/Predict.Eval/cases.json). Current scores:
[evaluation.md](../development/evaluation.md).

## 1. Purpose

1. **Regression gate**: every engine change is measured before it becomes a default.
2. **Tuning**: choose the weights (`WPrefix`, `WEdit`, `Alpha`) on data, not by feel.

## 2. Modes (chosen by the second argument)

```
Predict.Eval <db> cases.json                      → curated cases
Predict.Eval <db> heldout.txt [--max N]           → large held-out benchmark (top-k by prefix, KSS)
Predict.Eval <db> heldout.txt --sweep [--max N --n M]  → weight grid search, report KSS delta
             … --no4gram                          → same, with the 4-gram level disabled (A/B)
```

## 3. Curated cases

```json
{ "category": "phonetic", "context": "vers l'", "typed": "orizon", "intended": "horizon" }
```

- Categories: completion, phonetic, transposition/typo, context phrases (31 cases).
- For each case: rank of `intended` in our top-8 → top-1, hit@8, MRR per category, and a grade
  (GOOD: hit@8 ≥ 0.85 and top-1 ≥ 0.55; FAIR: hit@8 ≥ 0.65; else POOR).

## 4. Held-out benchmark

For every word *w* (≥ 2 letters, in the lexicon) of a held-out text, with the previous words of its
sentence as context:

```
 p = 0 (next word): PredictNext(ctx, 8)           p = 1, 2, 3: Predict(w[0..p], ctx, 8)
 → rank of w → top-1, top-3, hit@8, MRR per p
 KSS: smallest p (0..8) at which w is the top-1 suggestion; letters saved = |w| − p
      KSS = 1 − Σ p_used / Σ |w|
```

- **KSS** (keystroke savings) is the standard AAC word-prediction metric: the share of letters a user does
  *not* type if they accept the word as soon as it is first. It is a single number aligned with user value.
- Out-of-lexicon words are skipped and reported as coverage (they are unpredictable by construction).
- Accepting costs a key (F*n*) that KSS does not count; this is the usual convention, and comparisons stay fair.
- **Selection cost** (`--select-cost C`, 2026-11-25, listen before inserting): with « Touches F1–F8 : 1er appui : écouter, 2e :
  insérer » (the default) the pupil presses the key twice, so choosing a word is not free. KSS_c charges c keys per choice: per word
  cost = min(p + c, |w|) at the first p where w is top-1 (typing the word in full when choosing does not pay), else |w|;
  KSS_c = 1 − Σ cost / Σ |w|. KSS_0 is the headline KSS exactly (same queries, no extra prediction); c = 1 is the F-key
  under listen-first (one key more than today), c = 2 a pessimistic bound. The ranking is not tuned on KSS_1 / KSS_2:
  they are reported for information, the decision metric stays KSS_0.
- **Several held-out sets** on purpose (plan corpus-light): the decision sets are written for children — E1 (held-out
  public-domain books), E2 (held-out Vikidia articles), E3 (invented school sentences); W (Leipzig Wikipedia 2021) is the
  cross-domain check. The older subtitle held-out set (`heldout_subs_40k.txt`, no longer trained on since 0.10.0) is kept
  as a context measure of the spoken register only. The gaps between sets are the honest uncertainty on real users.

## 5. Weight sweep (`--sweep`)

```
 held-out file:  [ test slice: first N words ][ dev slice: next M words ]
 1. KSS(test) with current weights                        (baseline)
 2. grid WPrefix∈{1,1.5,2,3,4} × WEdit∈{1,1.5,2.5} × Alpha∈{0.2,0.4,0.6}
      scored on DEV with a fast proxy: top-1 at 1 and 2 letters
 3. KSS(test) with the best dev weights                   (reported delta)
```

The dev/test split prevents tuning on the numbers we report. The proxy (two `Predict` calls per word)
is ~5× cheaper than full KSS, which makes the 45-point grid practical.

## 6. Design rules

| Rule | Why |
|---|---|
| deterministic (no randomness, file order) | reruns are comparable; a delta is a real change |
| held-out text never in training corpora | otherwise KSS measures memorisation |
| metrics computed by the same tokeniser as training/engine | no artificial misses from tokenisation |
| curated cases kept small and readable | they document *behaviours* (`lbeu → bleu`), held-out measures *quality* |
| A/B switches as flags (`--no4gram`) | measure one change at a time |

## 7. Extending

- New behaviour → add a case to `cases.json`.
- New metric → add it in `RunHeldout` next to top-k/KSS; keep KSS as the headline.
- New tunable → expose it as a public field on `PredictionEngine` and add it to the grid in `RunSweep`.
