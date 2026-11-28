# Pépite — prédiction de mots pour Windows

Pépite propose des mots pendant que vous écrivez, **dans toutes les applications Windows** (Bloc-notes, Word, navigateur…).
Il est pensé pour les élèves dyslexiques ou qui écrivent « comme ça se prononce » : il retrouve le mot voulu même mal
orthographié (`orizon` → horizon, `maizon` → maison, `lbeu` → bleu).

- une barre de mots près du curseur, avec un pictogramme au-dessus de chaque mot ;
- F1 à F8 ou un clic pour insérer le mot ;
- lecture à voix haute (voix Windows ou voix naturelles Siwis et Pierre) des mots, des phrases et du texte sélectionné ;
- indicateur d'orthographe, accords des mots proposés, mots personnels et noms propres ;
- profils d'élèves et espace enseignant protégé par un code ;
- polices adaptées (OpenDyslexic, Lexend, Andika) et espacement des lettres.

## Vie privée

Pépite fonctionne **entièrement hors ligne** : aucune connexion réseau, rien n'est envoyé. Les réglages, les profils et les
mots personnels restent sur l'ordinateur. Les champs de mot de passe ne sont jamais lus.

## Installation

- Windows 11, 64 bits (x64).
- Télécharger la dernière version : [**Releases** de ce dépôt](https://github.com/pepite-labs/pepite/releases) (programme d'installation, `LISEZ-MOI.txt`, empreintes
  SHA-256).
- Aide en ligne : <https://pepite-labs.github.io/pepite/> (la même aide est installée avec Pépite et fonctionne sans
  Internet).
- Installation pour vous seul, ou pour tous les utilisateurs de l'ordinateur (écoles : installation silencieuse et fichier
  de configuration de l'établissement, voir `installer/DEPLOIEMENT.txt`).

0.11.1 est une version de test publique (bêta : préversion, non signée), sous .NET 10 ; la première version stable
suivra les retours des testeurs.

## Licences

| Partie | Licence |
|---|---|
| Code de Pépite | GPL-3.0-or-later ([COPYING](COPYING)) |
| Base de mots `prediction.sqlite` (Lexique 3.83 et fréquences tirées de textes libres) | CC BY-SA 4.0 |
| `spelling.sqlite` et `grammar.sqlite` (tirés du lexique de Grammalecte) | MPL-2.0 |
| Pictogrammes ARASAAC (`pictos.sqlite`) | CC BY-NC-SA 4.0 — **usage non commercial**, composant séparé, hors GPL |
| « Noms propres » (Insee, Statbel, Wikidata, CLDR) | CC BY 4.0 |
| Voix Siwis / Pierre | CC BY 4.0 / CC BY-SA 4.0 (le modèle de base de Pierre a été entraîné sur des données réservées à la recherche : cette voix pourra être retirée) |
| Polices OpenDyslexic, Lexend, Andika | SIL OFL 1.1 |
| Aide (`aide/`) | CC BY-SA 4.0 (`prefs.js` : GPL-3.0-or-later) ; les pictogrammes ARASAAC visibles dans quelques captures d'écran restent sous CC BY-NC-SA 4.0 (légende sous chaque image) |

**Attention, usage commercial :** les pictogrammes ARASAAC (Gouvernement d'Aragon, auteur Sergio Palao) ne peuvent pas être
utilisés dans un produit commercial. Si vous réutilisez le code de Pépite dans un cadre commercial, retirez-les (`pictos.sqlite`, et
les quatre captures d'écran de l'aide qui les montrent : `aide/img/capture-qui-es-tu.png`,
`capture-espace-enseignant.png`, `capture-barre-images.png`, `capture-barre-lecture.png`) ou obtenez l'accord d'ARASAAC.

Détails, sources et versions : [DONNEES.md](DONNEES.md) et [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Construire

Prérequis : le SDK .NET indiqué dans `global.json`, Python 3.12, Windows 11.

```
dotnet build Pepite.sln -c Release
dotnet test Pepite.sln -c Release
```

La base de mots et les autres données ne sont pas reconstruites à chaque fois : elles sont fournies déjà construites,
dans la version de données `data-2026.11.3` des Releases. Une commande les télécharge dans un dossier de votre choix,
vérifie leurs empreintes SHA-256, les dépose dans `src\Data.Build\out\` et vérifie la base de mots
([development/BUILD.md](development/BUILD.md) § 0) :

```
python -I src\Data.Build\fetch_data_release.py --cache <dossier>
```

Sans ces données, le code se construit et les tests qui ont besoin des données sont ignorés. Tout le détail (reconstruction
des données, images, voix, installateur) : [development/BUILD.md](development/BUILD.md). Qualité des prédictions :
[development/evaluation.md](development/evaluation.md). Conception : [design/README.md](design/README.md).

## Demande de retrait d'une donnée

Titulaire de droits sur une source, ou concerné par une donnée ? La procédure (contact par GitHub, délai, reconstruction
de la base sans la source) est décrite dans [DONNEES.md](DONNEES.md#demande-de-retrait-ou-de-correction).

## Contribuer, sécurité

- [CONTRIBUTING.md](CONTRIBUTING.md) : comment proposer une modification.
- [SECURITY.md](SECURITY.md) : signaler une faille en privé.

Développé avec l'aide de Claude (Anthropic).
