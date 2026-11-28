#!/usr/bin/env python3
"""French release notes of a GitHub pre-release (plan 13, A5). Stdlib only; run with ``python -I``.

    python -I installer/release_notes.py --version 0.11.0 --sums <SHA256SUMS.txt> --repo pepite-labs/pepite
                                         --out <notes.md> [--notes installer/notes/<version>.txt] [--signed]

Writes the body of the release (Markdown, French): the test-version banner, what is new (``installer/notes/<ver>.txt``),
the SmartScreen steps, the Smart App Control note, a note for managed school PCs, how to check the SHA-256 and the
build-provenance attestation, the GPL source offer, the data licences (ARASAAC non-commercial, the risk note of the
pierre voice) and the help site. The installer's SHA-256 is read from ``SHA256SUMS.txt``. ``--signed`` (Phase B) drops
the « non signée » wording. The data release (repo, tag) comes from ``src/Data.Build/data-release.lock.json``
(``--data-lock``). Exit code 0 = written, 1 = a missing input (installer or espeak-ng source line, notes file), 2 = bad arguments.
"""
import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SITE = "https://pepite-labs.github.io/pepite/"
DATA_LOCK = os.path.join(HERE, "..", "src", "Data.Build", "data-release.lock.json")
PLACEHOLDER = re.compile(r"\{[a-z_]+\}")


def read_sums(path):
    """{file name: sha256} of a ``sha256sum`` file (``<hash> *<name>`` or ``<hash>  <name>``)."""
    out = {}
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            m = re.match(r"^([0-9a-f]{64}) [ *](.+)$", line.rstrip("\r\n"))
            if m:
                out[m.group(2)] = m.group(1)
    return out


def read_data_release(path=DATA_LOCK):
    """{"repo", "tag"} of the data release the build used (``src/Data.Build/data-release.lock.json``)."""
    with open(path, encoding="utf-8") as f:
        lock = json.load(f)
    if not re.match(r"^[\w.-]+/[\w.-]+$", lock.get("repo", "")) or not re.match(r"^data-\d{4}\.\d{2}(\.\d+)?$", lock.get("tag", "")):
        raise ValueError("%s: bad repo or tag" % path)
    return {"repo": lock["repo"], "tag": lock["tag"]}


def body(version, sums, repo, notes, signed=False, espeak_archive=None, data=None):
    data = data or read_data_release()
    exe = "Pepite-%s-installation.exe" % version
    if exe not in sums:
        raise ValueError("SHA256SUMS.txt has no line for %s" % exe)
    if not espeak_archive:
        raise ValueError("SHA256SUMS.txt has no espeak-ng source archive: the GPL source offer needs it")
    tag = "v" + version
    banner = ("> **Version de test — pour essai, pas encore pour les élèves.**" if signed else
              "> **Version de test, non signée — pour essai, pas encore pour les élèves.**")
    lines = [
        banner,
        "",
        "Pépite propose des mots pendant que l'on écrit, dans toutes les applications Windows, avec une image et une voix "
        "qui peut les lire. Tout fonctionne sur l'ordinateur, sans Internet : Pépite n'envoie rien.",
        "",
        "## Nouveautés",
        "",
        "```text",
        notes.strip("\n"),
        "```",
        "",
        "## Télécharger et installer",
        "",
        "1. Téléchargez **`%s`** (Windows 11, 64 bits) et, pour lire avant d'installer, `LISEZ-MOI.txt`." % exe,
        "2. Vérifiez le fichier (conseillé, voir plus bas), puis double-cliquez dessus.",
    ]
    if not signed:
        lines += [
            "3. **« Windows a protégé votre ordinateur » (SmartScreen)** : cette version n'est pas encore signée, Windows ne "
            "connaît pas encore son auteur. Cliquez sur **« Informations complémentaires »**, puis sur **« Exécuter quand "
            "même »**.",
            "4. **Smart App Control** : si Windows bloque le fichier sans proposer « Exécuter quand même », c'est Smart App "
            "Control (Windows 11). Il bloque tout programme non signé et ne propose aucun contournement : cette version ne "
            "peut pas être installée sur cet ordinateur. Attendez la version signée, ou demandez au service informatique.",
            "",
            "**Ordinateurs gérés (écoles)** : sur un poste géré par le service informatique (Intune, GPO, antivirus "
            "d'entreprise, liste d'applications autorisées), une version non signée peut être refusée. Demandez au service "
            "informatique ; l'installation sans question pour tous les postes est décrite dans `DEPLOIEMENT.txt`.",
        ]
    lines += [
        "",
        "## Vérifier le fichier téléchargé",
        "",
        "SHA-256 du programme d'installation :",
        "",
        "```text",
        "%s  %s" % (sums[exe], exe),
        "```",
        "",
        "Dans le dossier du fichier, Maj + clic droit → « Ouvrir dans le terminal », puis :",
        "",
        "```powershell",
        "(Get-FileHash .\\%s -Algorithm SHA256).Hash" % exe,
        "```",
        "",
        "Le résultat doit être celui ci-dessus (en majuscules, c'est la même valeur) et celui de `SHA256SUMS.txt`, qui "
        "donne aussi l'empreinte de chaque autre fichier de la version (`sha256sum -c SHA256SUMS.txt` sous Linux).",
        "",
        "Attestation de provenance (le fichier a été construit par le workflow `app.yml` de GitHub Actions à partir du "
        "code de l'étiquette `%s` de ce dépôt), avec l'outil `gh` de GitHub, après `gh auth login` :" % tag,
        "",
        "```text",
        "gh attestation verify %s --repo %s --signer-workflow %s/.github/workflows/app.yml --source-ref refs/tags/%s"
        % (exe, repo, repo, tag),
        "```",
        "",
        "## Code source (GPL-3.0-or-later)",
        "",
        "Pépite est un logiciel libre, licence GNU GPL version 3 ou plus récente (`COPYING`). Le code source "
        "correspondant à cette version est celui de l'étiquette [`%s`](https://github.com/%s/tree/%s) (archives « Source "
        "code » ci-dessous), y compris le script du programme d'installation (`installer/pepite.iss`). Le code source "
        "d'espeak-ng correspondant à la bibliothèque livrée est joint à cette version (`%s`). Les données sont dans la "
        "version de données [`%s`](https://github.com/%s/releases/tag/%s)."
        % (tag, repo, tag, espeak_archive, data["tag"], data["repo"], data["tag"]),
        "",
        "## Données et licences",
        "",
        "- Base de mots (`prediction.sqlite`) : CC BY-SA 4.0 (Lexique 3.83 et fréquences de textes écrits ; sources dans "
        "`prediction-SOURCES.txt`).",
        "- Orthographe et accords (`spelling.sqlite`, `grammar.sqlite`) : MPL-2.0, dérivés de Grammalecte.",
        "- **Pictogrammes ARASAAC** : CC BY-NC-SA 4.0 — **usage non commercial uniquement**. Ils forment un composant "
        "séparé, facultatif à l'installation, hors de la licence GPL (`ARASAAC-LICENCE.txt`).",
        "- Voix Siwis : CC BY 4.0.",
        "- **Voix Pierre** : CC BY-SA 4.0. Elle a été affinée à partir d'une voix anglaise (lessac) dont les données "
        "d'entraînement sont réservées à la recherche : cette voix pourra être retirée d'une version future si la licence "
        "de ce modèle de base est contestée (`voices/pierre/SOURCE.txt`).",
        "- Noms propres : CC BY 4.0. Polices : SIL Open Font License 1.1.",
        "- Tous les composants et leurs licences : `THIRD-PARTY-NOTICES.txt` (installé avec Pépite).",
        "",
        "## Aide",
        "",
        "L'aide est installée avec Pépite et fonctionne sans Internet (menu de l'icône → « Aide », ou F1 dans les "
        "Réglages). Copie en ligne : %s" % SITE,
        "",
        "Fichiers joints : le programme d'installation, `SHA256SUMS.txt`, `LISEZ-MOI.txt`, `DEPLOIEMENT.txt`, "
        "`exemple-etablissement.json`, les symboles de débogage (`symbols-%s.zip`), la source d'espeak-ng, "
        "`BUILD-INFO.txt`, `files.sha256` (chaque fichier installé) et `signatures.txt`." % version,
        "",
    ]
    text = "\n".join(lines)
    left = PLACEHOLDER.findall(text.replace(notes, ""))
    if left:
        raise ValueError("placeholder left: %s" % ", ".join(left))
    return text


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", required=True)
    ap.add_argument("--sums", required=True, help="SHA256SUMS.txt of the release")
    ap.add_argument("--repo", required=True, help="owner/name of the GitHub repository")
    ap.add_argument("--out", required=True)
    ap.add_argument("--notes", help="what is new (default installer/notes/<version>.txt)")
    ap.add_argument("--signed", action="store_true", help="Phase B: a signed release")
    ap.add_argument("--data-lock", default=DATA_LOCK, help="data-release.lock.json (the data release named in the notes)")
    a = ap.parse_args(argv)
    if not re.match(r"^\d+\.\d+\.\d+$", a.version) or not re.match(r"^[\w.-]+/[\w.-]+$", a.repo):
        print("bad --version (x.y.z) or --repo (owner/name)", file=sys.stderr)
        return 2
    notes_path = a.notes or os.path.join(HERE, "notes", "%s.txt" % a.version)
    try:
        with open(notes_path, encoding="utf-8-sig") as f:
            notes = f.read()
        sums = read_sums(a.sums)
        espeak = next((n for n in sums if re.match(r"^espeak-ng-[0-9a-f]{40}\.tar\.gz$", n)), None)
        text = body(a.version, sums, a.repo, notes, a.signed, espeak, read_data_release(a.data_lock))
    except (OSError, ValueError, KeyError) as e:
        print("release notes: %s" % e, file=sys.stderr)
        return 1
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote %s (%d chars)" % (a.out, len(text)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
