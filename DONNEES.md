# Données de Pépite : sources, licences, demandes de retrait

Pépite n'utilise que des données publiées sous licence libre ou ouverte (pictogrammes : usage non commercial). Le code
est sous GPL-3.0-or-later ; chaque ensemble de données garde sa propre licence et est livré comme un fichier séparé, avec
ses mentions.

| Fichier livré | Contenu | Licence | Sources et mentions |
|---|---|---|---|
| `prediction.sqlite` | liste de mots de Lexique 3.83 et fréquences des suites de mots | CC BY-SA 4.0 | `prediction-SOURCES.txt` (chaque source, sa version, son adresse, son empreinte SHA-256) |
| `spelling.sqlite` | liste de référence de l'indicateur d'orthographe, tirée de Grammalecte | MPL-2.0 | `spelling/SOURCE.txt`, `spelling/MPL-2.0.txt` |
| `grammar.sqlite` | analyses grammaticales tirées de Grammalecte | MPL-2.0 | `grammar/SOURCE.txt`, `grammar/MPL-2.0.txt` |
| `pictos.sqlite` | pictogrammes ARASAAC redimensionnés | CC BY-NC-SA 4.0 (**usage non commercial**) | `ARASAAC-LICENCE.txt` |
| `noms\` | « Noms propres » (Insee, Statbel, Wikidata, CLDR) | CC BY 4.0 | `noms\LICENCE.txt`, `noms\SOURCES.md` |
| `voices\siwis\`, `voices\pierre\` | voix Piper | CC BY 4.0 (siwis), CC BY-SA 4.0 (pierre) | `SOURCE.txt` et licences de chaque dossier |
| `fonts\` | OpenDyslexic, Lexend, Andika | SIL OFL 1.1 | `OFL.txt` de chaque police |

La liste complète, avec les versions, est dans [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Pictogrammes ARASAAC : usage non commercial

Les pictogrammes sont la propriété du Gouvernement d'Aragon (auteur Sergio Palao, ARASAAC) et sont diffusés sous licence
CC BY-NC-SA 4.0 : **ils ne peuvent pas être utilisés dans un produit commercial**. Ils forment un composant séparé, qui
n'est pas couvert par la GPL. Si vous réutilisez le code de Pépite dans un cadre commercial, retirez `pictos.sqlite`
(Pépite fonctionne sans images) ou obtenez l'accord d'ARASAAC.

## Demande de retrait ou de correction

Si vous êtes titulaire de droits sur une source utilisée par Pépite, ou si une donnée vous concerne et que vous souhaitez
la faire corriger ou retirer :

1. **Écrivez-nous par GitHub** : ouvrez un ticket sur le dépôt (sans y mettre de donnée personnelle), en indiquant la
   source concernée et ce que vous demandez. Sans compte GitHub, ou pour une demande qui ne peut pas être publique,
   utilisez la procédure de signalement de contenu de GitHub. (Le signalement privé décrit dans SECURITY.md est réservé
   aux failles de sécurité.)
2. **Délai** : nous répondons sous 30 jours. Pendant l'examen, la source peut être retirée à titre de précaution de la
   prochaine version.
3. **Retrait** : la base de mots est reconstruite sans la source concernée. Chaque source est déclarée dans
   `src/Data.Build/data.lock.json` ; les outils `src/Data.Build/corpus/` reconstruisent `prediction.sqlite` à partir de
   cette liste (voir [development/BUILD.md](development/BUILD.md) § 1). Une nouvelle version des données et de Pépite est
   alors publiée, et `prediction-SOURCES.txt` ne mentionne plus la source.

Les réserves de fouille de textes et de données (TDM) des sources ont été vérifiées le 2026-11-16 :
[development/tdm-check-2026-11-16.md](development/tdm-check-2026-11-16.md).
