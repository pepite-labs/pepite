# `design/` — index

Start with the **architecture overview**; then the note for the component you work on.

| Document | Type | Content |
|---|---|---|
| [architecture-overview.md](architecture-overview.md) | as built | components, data flow, dependencies, built vs planned, cross-cutting decisions |
| [predict-core-design.md](predict-core-design.md) | as built | the engine: candidate generators, phonetic keys, backoff LM ranking, RAM/disk split |
| [data-pipeline-design.md](data-pipeline-design.md) | as built | `prediction.sqlite` schema, Lexique import, n-gram training, corpora |
| [keyboard-design.md](keyboard-design.md) | as built | the any-app predictor: threading, key handling, insertion, bar window, caret |
| [benchmark-design.md](benchmark-design.md) | as built | `Predict.Eval`: curated cases, held-out KSS, weight sweep |

User/developer guides and scores are in [../development/](../development/).
