# Contribuer à Pépite

Merci de votre intérêt ! Quelques règles simples.

## Licence des contributions

Toute contribution est publiée sous la même licence que le projet : **GPL-3.0-or-later** (« inbound = outbound »). Pas de
CLA ni de DCO à signer : en proposant une modification, vous acceptez qu'elle soit diffusée sous cette licence. Une
contribution de données garde la licence de sa source, qui doit être libre et compatible (voir plus bas).

## Jamais de données d'élèves

N'ajoutez **aucune donnée d'élève** (nom, texte écrit par un élève, profil `.pepite`, journal `--log`, capture d'écran avec
un nom) dans un ticket, une demande de fusion, un test ou un fichier du dépôt. Les exemples sont inventés.

## Construire et tester

```
dotnet build Pepite.sln -c Release
dotnet test Pepite.sln -c Release
python -I -m unittest discover -s src\Data.Build -p "test_*.py"
python -I -m unittest discover -s src\Data.Build\corpus\tests
python -I -m unittest discover -s src\Data.Build\exclusions\tests -p "test*.py"
python -I -m unittest discover -s src\Data.Build\pepite-noms\tests
python -I -m unittest discover -s installer\tests
```

Le SDK .NET doit être exactement celui de `global.json` (10.0.115) ; un SDK portable décompressé dans un dossier convient
(mettez ce dossier en tête du `PATH` pour la session).
Détails : [development/BUILD.md](development/BUILD.md).

## Changer la prédiction

Toute modification du moteur (`src/Predict.Core`) est mesurée **avant** d'être proposée, et la demande de fusion donne les
chiffres avant / après :

- les cas choisis : `Predict.Eval <db> src/Predict.Eval/cases.json` (ajoutez des cas pour le comportement nouveau) ;
- les gains de frappe (KSS) sur les textes de test tenus à l'écart de l'entraînement
  ([development/evaluation.md](development/evaluation.md)).

## Budget de ressources

Pépite doit rester léger sur un PC d'école : **≈ 240 Mo disque + mémoire, voix non comprises** ; une prédiction en moins de
5 ms (à chaud) ; moins de 1 ms dans le crochet clavier (le crochet n'appelle jamais le moteur).

## Dépendances et données

- Une nouvelle dépendance ou donnée doit avoir une **licence compatible GPL-3** (MIT, BSD, Apache-2.0, LGPL, MPL-2.0, CC BY,
  CC BY-SA, CC0…) et une entrée dans [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
- **Aucun binaire ni modèle dans git** : les données, voix, polices et images sont récupérées par des scripts épinglés
  (adresse + SHA-256), dont la sortie est ignorée par git.

## Tests de l'interface

Le crochet clavier et l'insertion de texte se testent **dans une machine virtuelle**, jamais sur votre PC de travail.

## Aide d'une IA

L'aide d'un assistant d'IA est permise, à condition de la déclarer (par exemple une ligne `Co-Authored-By:` dans le
message de commit). Vous restez responsable de ce que vous proposez.

## Documentation

- Les textes pour les utilisateurs sont en français ; la documentation de développement est en anglais.
- Les mentions « plan NN » dans les documents sont l'historique interne du projet ; les décisions qui font foi sont dans
  [design/product-decisions.md](design/product-decisions.md).

## Sécurité

Une faille ne se signale pas dans un ticket public : voir [SECURITY.md](SECURITY.md).
