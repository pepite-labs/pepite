# `aide/` — l'aide intégrée de Pépite (notes pour les contributeurs)

Pages HTML statiques en français, installées dans `{app}\aide\` et ouvertes dans le navigateur par défaut (menu de
l'icône « Aide », boutons « ? » et <kbd>F1</kbd> dans Réglages, « En savoir plus » de la fenêtre de bienvenue). Ce
fichier n'est pas installé.

Licence : pages, dessins, captures d'écran et feuille de style **CC BY-SA 4.0**, sauf les pictogrammes ARASAAC visibles
dans quatre captures (`capture-qui-es-tu`, `capture-espace-enseignant`, `capture-barre-images`, `capture-barre-lecture`), qui restent sous **CC BY-NC-SA
4.0** (légende « Pictogrammes : Sergio Palao, ARASAAC, Gouvernement d'Aragon, CC BY-NC-SA 4.0 » sous chacune) ; `prefs.js` **GPL-3.0-or-later**. Les polices
(`fonts/`) sont copiées à la construction depuis `src/Data.Build/out/fonts` (SIL OFL 1.1) et ne sont jamais
versionnées.

## Fichiers

| Fichier | Rôle |
|---|---|
| `index.html` … `pour-les-curieux.html` | pages des élèves (`<meta name="pepite-public" content="enfant">`, « tu ») |
| `enseignants.html`, `installation.html`, `vie-privee.html`, `a-propos.html` | pages des adultes (`content="adulte"`, « vous ») |
| `aller/reglages-*.html` | renvois vers une partie de `reglages.html` : Windows perd le `#fragment` d'un fichier ouvert par l'application, le renvoi le garde (`meta refresh` + lien visible) |
| `style.css`, `prefs.js` | une seule feuille de style ; le script garde les cases « Lettres OpenDyslexic » / « Texte plus grand » d'une page à l'autre (rien d'autre, aucun réseau) |
| `img/*.svg` | dessins originaux |
| `img/capture-*.png` | vraies captures d'écran (machine de test, 0.10.1), recadrées et réduites à 256 couleurs ; une capture qui montre des pictogrammes ARASAAC porte la légende de leur licence |
| `img/barre-*.png` | la vraie barre, dessinée par `Pepite.exe --render-bar --no-pictures` : `src/Data.Build/render_help_images.ps1` les refait |

## Règles d'écriture

- Pages des élèves au **« tu »**, pages des adultes au **« vous »** ; l'interface reste à l'infinitif.
- Phrases courtes (12 mots visés, 20 au plus), une idée par paragraphe, commencer par le geste (« Clique sur… »).
- Les libellés de l'écran en **gras**, recopiés de `src/Predict.Keyboard/Strings.cs` (sans le « _ » des raccourcis) ;
  les touches dans `<kbd>` ; un texte affiché par Windows ou Pépite dans `<q>`.
- Jamais « IA » ni « intelligence artificielle » : Pépite, c'est un dictionnaire, des statistiques sur des textes
  écrits et des sons ; les voix sont des « voix de synthèse locales ».
- Pas d'adresse e-mail ; le contact est le site du projet, écrit en texte. Les sites extérieurs sont écrits en texte,
  jamais en lien cliquable. Pas de numéro de version dans l'aide.
- Pas d'italique long, pas de majuscules pour insister, texte aligné à gauche.
- Chaque image a un texte de remplacement qui dit ce qu'elle apprend.

Quand un libellé de `Strings.cs` change : `grep` l'ancien texte dans `aide/` et corriger les pages.

## Vérifier

```
python -I installer/help_lint.py aide                 # les pages du dépôt
python -I installer/help_lint.py --stage <publish>\aide   # une aide construite (fichiers liés et polices présents)
python -I -m unittest discover -s installer/tests -p test_help_lint.py
```

`help_lint.py` vérifie l'enveloppe des pages (CSP stricte, `lang="fr"`), les titres, les textes de remplacement, les
liens et ancres, la navigation identique sur toutes les pages, le « tu » / « vous », les mots interdits, la taille des
polices et le poids de l'aide. `installer/release_lint.py` le lance aussi sur l'installation.
