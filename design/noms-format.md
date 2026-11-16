# The « pepite-noms » names database format (version 1)

Plan 10C, owner Q19b / Q20, decision D30. One format, one parser (`Predict.Core/NamesFile.cs`), for the shipped « Noms
propres » pack (`noms\` next to `Pepite.exe`, built by `src/Data.Build/pepite-noms/`) and for a **teacher's names
database** imported in Espace enseignant → Mots → « Importer une base de noms… ».

## Files

A **folder** or a **`.zip`** (≤ 50 MB) holding:

```
noms.json      the canonical index (below)          — or —
noms.csv       nom;variantes;categorie;image;source;licence;auteur   (UTF-8, a header line, variants separated by |)
images/…       PNG or JPEG pictures, referenced by a relative path
```

The teacher may also pick the `noms.json` / `noms.csv` file itself (its folder holds the pictures). In a zip, the index may
sit at the root or in one top folder.

## noms.json

```json
{ "format": "pepite-noms", "version": 1,
  "titre": "Ma classe", "edition": "2026.1", "licence": "CC BY 4.0", "attribution": "texte à afficher…",
  "sources": { "flag-icons": { "titre": "flag-icons 7.6.0", "licence": "MIT", "url": "https://…" } },
  "noms": [
    { "nom": "Belgique", "variantes": ["België"], "categorie": "pays", "rang": 0.6,
      "image": "images/pays/be.png", "source": "flag-icons", "licence": "MIT", "auteur": "…" },
    { "nom": "Zoltan", "categorie": "prenom", "rang": 0.2, "niveau": 2 } ] }
```

| Field | Rule |
|---|---|
| `format`, `version` | `"pepite-noms"`, `1`; a newer version is refused (« mettez Pépite à jour ») |
| `nom` | **2–40 characters, starting with a capital**; letters (accents, any script) with inner spaces, hyphens, apostrophes (`’` → `'`) or a dot after a letter followed by a space (« J. R. R. Tolkien »); never digits, brackets or other signs, never all capitals. Unique in the file |
| `variantes` | other spellings, same rule: recognised (✓) and offered with the name (« Ieper » brings « Ieper » and « Ypres ») |
| `categorie` | free text (`prenom`, `pays`, `commune-be`, `ville`, `personnage`…) |
| `rang` | popularity 0–1 (default 1): the name's weight in the bar; the pack maps it to at most 5 occurrences per million words, below the common content words |
| `niveau` | 1 (default) = predicted; 2 = recognised (✓) and predicted only after a capital typed inside a sentence |
| `restreint` | `true`: a figure taught for the harm done (owner 2026-11-15) — recognised, offered only after a capital typed inside a sentence and at least 3 letters, after every other name, never with a picture (an `image` is ignored); implies `niveau` 2 |
| `image` | a relative path inside the database; PNG / JPEG ≤ 5 MB, ≤ 4096 × 4096 px (read from the header before any decoding) |
| `source` | an id of the `sources` table, or a URL / free text |
| `licence` | the licence of the entry — of its picture when it has one; resolved from the entry, else its source, else the file's `licence` |
| `auteur` | the author of the picture |

## Import rules (teacher)

- At most **5 000 names**; a « noms » list counts in the profile's word limit (5 000 for a pupil, 10 000 for « Classe »).
- Every picture path must stay inside the database: no absolute path, drive, `..`, empty part or reparse point.
- **A picture needs a licence** (owner Q8); a picture without one is left out and the name kept. Any licence is accepted
  for the teacher's own pictures, at the teacher's responsibility; it is stored with each picture and shown (tooltip of
  the list in Espace enseignant → Mots). For a CSV, the dialog asks one licence for the pictures of rows without one.
- **Pictograms or drawings only, never photos** (owner Q17) — in particular no pupil's face. The dialog says it; Pépite
  cannot check it.
- Pictures are re-encoded as PNG thumbnails ≤ 256 px, named by their hash, in `profiles\<id>\images\` (≤ 500 per
  profile); the original files are never kept. Rejected lines and pictures are listed in the dialog.
- The names land in a new list of type `noms` of « Classe » or of a pupil (Espace enseignant → Mots → profile chooser),
  switched on / off like a lesson. A teacher's pictures come before the pack's flags and are never put in a shared pack.

## In `mots.json` (profile words, schema 1)

A « noms » list: `{ "id", "nom", "active", "type": "noms", "licence", "mots": [ { "w", "src": "import", "ajoute",
"image": "<16 hex>.png", "licence", "source", "auteur", "categorie", "variantes": [ … ] } ] }`. An `image` that is not a
bare stored thumbnail name is dropped on reading. Exports (`.pepite` v2, « Inclure les listes de mots ») carry the list and
these fields; with « Inclure les images » the file is a **version 3 zip** (`pepite.json` + `images/<profile id | classe>/
<16 hex>.png`, ≤ 50 MB) and the import writes each picture whose name matches its content's hash into the target
profile's folder.
