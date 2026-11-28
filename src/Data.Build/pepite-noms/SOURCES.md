# Sources of the « Noms propres » pack (edition 2026.3)

Every input is pinned in [names.lock.json](names.lock.json) (URL, version, SHA-256); the built pack repeats the table with
the snapshot dates in `noms/SOURCES.md`, plus the author and licence of every Commons flag. Licence statements below were
read on 2026-11-14 (an internal study of Pépite gives the quotes and URLs).

| Input | Version / file | Licence | Attribution required |
|---|---|---|---|
| INSEE, Fichier des prénoms | édition juillet 2026 (naissances 1900-2025, mise à jour du 16/07/2026), `prenoms-2025-nat_csv.zip` | Licence Ouverte / Open Licence 2.0 (Etalab) | « Source : Insee » + the date of the last update |
| Statbel, first names of the total population by municipality | 2026, `TA_POP_MALE_2026.zip`, `TA_POP_FEMALE_2026.zip` | CC BY 4.0 | « Source: Statbel », link to the licence, say the data were modified, no endorsement |
| Statbel, first names of newborns by municipality | 2016–2025, `TA_BIRTHS_FEMALE_2016_2025.zip`, `TA_BIRTHS_MALE_2016_2025.zip` (open-data catalogue, read 2026-11-15) | CC BY 4.0 | as above |
| Statbel, REFNIS codes | valid on 2025-01-01, `TU_COM_REFNIS-20250101.zip` | CC BY 4.0 | as above |
| Unicode CLDR | 49 (cldr-json 49.0.0), `cldr-localenames-full/main/fr/territories.json` + `LICENSE` | Unicode License v3 | keep the copyright and permission notice |
| flag-icons | 7.6.0 (2026-10-15), `flags/4x3/*.svg` | MIT | keep the copyright line and permission notice |
| Wikidata | query snapshots of 2026-11-14 (`queries/*.rq`) | CC0 1.0 | none (credited as a courtesy) |
| Wikimedia Commons | 12 flag files of the Belgian regions and provinces | public domain / CC0 only (per file; « Flag of the Province of Liège.svg », CC BY-SA 3.0, is left out) | none for PD / CC0; author listed anyway |
| Pépite (own drawing) | `images/saint-nicolas.svg` (2026-11-27, SHA-256 `f7e8a455…a6a3fe33`): an original drawing made for Pépite with the help of Claude, no third-party art | CC0 1.0 | none; credited as « Dessin de Saint-Nicolas : Pépite (dessin original, réalisé avec l'aide de Claude), CC0 1.0 » |
| Creative Commons | `legalcode.txt` of CC BY 4.0 | — | the pack's own licence text |
| resvg | 0.50.0, `resvg-win-x86_64.zip` (build-time tool, never shipped) | Apache-2.0 OR MIT | — |

## Processing (« données modifiées »)

- First names: INSEE names title-cased (« JEAN-PIERRE » → « Jean-Pierre »), births summed per name; Statbel persons summed
  over municipalities and sexes; Statbel newborns 2016–2025 summed per name (they rank the Belgian names of the predicted tier); names with fewer than 20 births since 1900 and fewer than 20 Belgian persons dropped;
  two tiers (owner Q2 / Q11); since edition 2026.2 the predicted tier also holds the 1 000 names most given since 1900
  (France or Belgium, the higher share), ranked by max(recent rang, lowest recent-tier rang, 0.5 × all-time rang).
- Places: current REFNIS lines only; French name shown, disambiguation in brackets removed, Dutch / German names and the
  Wikidata French label as variants; regions and provinces also take their Wikidata French aliases (« Wallonie »).
- Countries: ISO 3166-1 alpha-2 codes only (macro-regions and reserved codes left out), the typographic apostrophe
  normalised, the CLDR short form used when the long one is not a name (« Hong Kong »); continents from the UN M49 codes.
- Towns, regions, departments, historical figures: the Wikidata French label, else the multilingual one; a historical
  figure's alias is kept as a variant only when all its words are words of the label (« Napoléon » for « Napoléon Ier »).
- Flags: SVG rendered by resvg, fitted into 128 × 96 px, 128-colour palette PNG. Pépite's drawings: the same, fitted
  into 128 × 128 px; the Saint-Nicolas drawing goes to the entry « Saint-Nicolas » only, not to its variant « Sint-Niklaas »
  (`"image_variantes": false`).
- A name shared by several sources is one entry (priority: continent, country, Belgian region, province, municipality,
  French region, department, town, historical figure, first name).
