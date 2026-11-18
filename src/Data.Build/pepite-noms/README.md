# pepite-noms — « Noms propres (données ouvertes) »

An open-data package of French proper names for the word-prediction program Pépite: first names (France and Belgium),
countries and continents, Belgian regions, provinces and municipalities, French regions and departments, towns, and a
reviewed list of historical figures, with the flags of the countries and of the Belgian regions and provinces.

This folder is developed inside Pépite for now and is meant to become its own repository (« pepite-noms »); nothing here
depends on the rest of Pépite. Pépite ships a **pinned edition** of the built pack (`noms\` next to `Pepite.exe`).

## Licences

- **The pack** (`noms.json`, the thumbnails, the selection files): **Creative Commons Attribution 4.0 International**
  (`LICENSE`, https://creativecommons.org/licenses/by/4.0/). Contributions are accepted under the same licence.
- **The scripts** (`build_names.py`, `tests/`): GPL-3.0-or-later, like Pépite.
- **The inputs** keep their own terms, all compatible with CC BY 4.0 and with GPL-3 (no NonCommercial, NoDerivatives,
  ShareAlike or ODbL input): INSEE (Licence Ouverte 2.0), Statbel (CC BY 4.0), Unicode CLDR 49 (Unicode License v3),
  flag-icons 7.6.0 (MIT), Wikidata (CC0 1.0), Wikimedia Commons flags (public domain / CC0 only). The attribution text
  the program shows is generated from `names.lock.json` into `noms/ATTRIBUTION-fr.txt`; the full notices are in
  `noms/LICENCE.txt`. Details per input: [SOURCES.md](SOURCES.md).

## Build (reproducible)

```
python -I build_names.py --raw <work>/raw fetch          # pinned inputs, one download at a time, SHA-256 checked
python -I build_names.py --raw <work>/raw build --out <work>/out [--previous <last edition>/noms/noms.json]
python -I -m unittest discover -s tests
```

Output: `<work>/out/noms/` (`noms.json`, `images/`, `LICENCE.txt`, `SOURCES.md`, `ATTRIBUTION-fr.txt`),
`noms-<edition>.zip`, `diff-<edition>.md` (names added / removed per category, reviewed before shipping) and
`build-report.json` (counts, sizes, SHA-256). Python 3.10+, Pillow (palette PNG); SVG flags are drawn by the pinned
**resvg 0.50.0** binary (Apache-2.0 OR MIT), a build-time tool that is never shipped.

**Snapshots.** Wikidata and the Commons licence metadata are live services, so a build never queries them: their answers
are saved once by the deliberate `snapshot <id>` step (queries in `queries/*.rq`, run on query.wikidata.org) and pinned by
SHA-256 in `names.lock.json`. Refreshing them is a reviewed change of the lock file, like a new input. A new input (or a
new edition of one) is pinned with `fetch --pin-new` after review.

## Contents (edition 2026.2)

| Category | Names | Ranking (`rang`, 0–1) | Picture |
|---|---|---|---|
| First names (INSEE ∪ Statbel, ≥ 20 births / persons) | 29 091: the 4 962 most given recently and, since 2026.2, the 264 others of the 1 000 most given since 1900 (5 226 in all) are predicted, the rest (`niveau` 2) recognised only | recent births (FR 2010–2025, BE newborns 2016–2025); the 1 000 of all time: 0.5 × their all-time rang (see below); all-time totals for the rest | none |
| Countries (CLDR `fr`, ISO 3166 codes) + continents | 247 + 7 | fixed, then below the common first names (see below) | flag-icons 4:3 |
| Belgian regions, provinces, municipalities (REFNIS 2025) | 3 + 9 + 560 | population (Wikidata) | flags of the regions and provinces (Commons, PD / CC0) |
| French regions and departments (Wikidata) | 14 + 96 | fixed | none |
| Towns (Wikidata: French communes ≥ 30 000 and prefectures, Swiss ≥ 20 000, Luxembourg ≥ 10 000, capitals, well-known large cities) | 901 | population | none |
| Historical figures (`selection/historiques.tsv`, 372 reviewed QIDs, deceased only) | 371 | below every predicted first name, then by Wikipedia articles | none |

**First names first** (owner 2026-11-15): every place is scaled below the rang of the 1 000th predicted first name,
keeping its own order, never below 0.0105 — « avec Jo » offers Joseph before Johannesbourg. Continents, countries, Belgian
regions / provinces and French regions take the top of that band (0.80–0.95 ×, since 0.8.3: « né en Bel » → Belgique
before Belgrade), municipalities, departments and towns the rest (0.05–0.80 ×). Among the countries, Belgium, France, Luxembourg,
Switzerland, the Netherlands, Germany and Canada come first (« Bel » → Belgique before Belize).

**First names of all generations** (edition 2026.2, owner 2026-11-17): the 1 000 first names most given since 1900
(France or Belgium, the higher share) are predicted too, so grown-ups' names (Roger, Martine, Rodolphe) come at a sentence
start or typed in lowercase. Such a name ranks by the highest of its recent rang, the lowest rang of the recent tier and
half its all-time rang, so it comes below the ≈ 480 most given pupils' names (Roger 481st, measured). The places' band
and the figures' scale are still computed from the recent tier alone, so places and figures are unchanged; a place that is also such a first name
(in 2026.2 only « Brecht », a Belgian municipality) keeps its 2026.1 values. The build's diff report lists the names that became predicted and every changed rang.

The French name is shown (« Ypres »); the official Dutch / German names are recognised variants (« Ieper », « Ypern »).

## Contributing

Corrections and additions go to the **selection and override files** (the historical figures' QIDs, later extra
variants), never to the generated names; errors in the data themselves are best fixed upstream (Wikidata). Every
contribution is under CC BY 4.0. A new edition is built **once a year**, after the July INSEE and Statbel releases, with
its diff report.

## Format

`noms.json` follows the documented « pepite-noms » format (version 1), the same as a teacher's import in Pépite; see
Pépite's `design/noms-format.md`.
