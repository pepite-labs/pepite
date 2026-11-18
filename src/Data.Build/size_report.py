#!/usr/bin/env python3
r"""Size report of a test release against the previous one (plans 08a, 08b). Stdlib only; run with ``python -I``.

    python -I size_report.py --new <publish-0.6.0> [--old <publish-0.5.0>] --out <size-report.md> [--notes <file.md>]

Sizes are sums of file sizes in MiB (decimal MB in brackets). The report names the folders by their base name under
the ``<work>`` placeholder, never by their full local path. Voice packs whose ``voice.json`` says ``"public": false``
are listed: a public release (plan 10) must leave them out (D28). ``--notes`` appends a hand-written section.
0.8.3: the release must hold the spelling reference (table ``spelling_unlisted``, rows > 0; build_spelling_ref.py),
else the report is refused. Since the publication cleanup (2026-11-16) it is ``spelling.sqlite`` (MPL-2.0, with
``spelling/SOURCE.txt`` and ``spelling/MPL-2.0.txt``), no longer a table of ``prediction.sqlite``.
0.9.0 (plan 11): ``grammar.sqlite`` (MPL-2.0, build_grammar.py) is listed with its size; when it is missing, or ships
without ``grammar/SOURCE.txt`` / ``grammar/MPL-2.0.txt``, the report and the console carry a warning (the bar would rank
as 0.8.3, or the MPL-2.0 data would ship without its notices).
0.10.0 (plan 12): ``--installer <exe>`` adds the installer's size; the report lists the installer components (``app``,
``images``, ``voix\siwis``, ``voix\pierre``) and the installed size of a public and of a private (test) install; the
PDBs (shipped in ``symbols-<ver>.zip``, never installed) are left out of the installed sizes.
0.10.1 (plan 13): ``--old`` is optional (a CI build has no earlier release folder): the report then shows the new
folder alone, without a comparison column.
"""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from build_spelling_ref import unlisted_count  # noqa: E402

MIB = 1048576


def size(path):
    if os.path.isfile(path):
        return os.path.getsize(path)
    total = 0
    for root, _, files in os.walk(path):
        total += sum(os.path.getsize(os.path.join(root, f)) for f in files)
    return total


def packs(release):
    vdir = os.path.join(release, "voices")
    out = []
    if os.path.isdir(vdir):
        for name in sorted(os.listdir(vdir)):
            card = os.path.join(vdir, name, "voice.json")
            if os.path.isfile(card):
                with open(card, encoding="utf-8") as f:
                    c = json.load(f)
                out.append((name, c.get("licence", "?"), c.get("public", True), size(os.path.join(vdir, name))))
    return out


GRAMMAR_FILES = ("grammar.sqlite", os.path.join("grammar", "SOURCE.txt"), os.path.join("grammar", "MPL-2.0.txt"))


def grammar_missing(release):
    """The grammar files a release lacks (plan 11): empty when grammar.sqlite ships with its notices."""
    return [f for f in GRAMMAR_FILES if not os.path.isfile(os.path.join(release, f))]


def components(release):
    """Installed bytes per installer component (plan 12, installer/pepite.iss); PDBs are excluded."""
    sizes = {"app": 0, "images": 0}
    for root, _, files in os.walk(release):
        rel = os.path.relpath(root, release)
        parts = [] if rel == "." else rel.split(os.sep)
        for f in files:
            if f.lower().endswith(".pdb"):
                continue
            n = os.path.getsize(os.path.join(root, f))
            if parts[:1] == ["voices"] and len(parts) >= 2:
                key = "voix\\" + parts[1]
            elif not parts and f == "pictos.sqlite":   # ARASAAC-LICENCE.txt is in app since 0.10.2 (the help shows pictograms)
                key = "images"
            else:
                key = "app"
            sizes[key] = sizes.get(key, 0) + n
    return sizes


def install_rows(release, installer=None):
    """Markdown rows: installer size, components, installed size of a public and of a private (test) install."""
    comp = components(release)
    public = {k: v for k, v in comp.items() if k in ("app", "images")}
    for name, _, pub, _ in packs(release):
        if pub and ("voix\\" + name) in comp:
            public["voix\\" + name] = comp["voix\\" + name]
    lines = ["", "## Installer (plan 12)", "", "| Item | Size |", "|---|---|"]
    if installer:
        isz = os.path.getsize(installer)
        lines.append(f"| Installer `{os.path.basename(installer)}` | {isz / MIB:.2f} MB ({isz / 1e6:.1f} MB) |")
    for k in sorted(comp):
        lines.append(f"| component `{k}` | {comp[k] / MIB:.2f} MB |")
    pub_total, all_total = sum(public.values()), sum(comp.values())
    lines.append(f"| **Installed, public build** (app + images + public voices) | {pub_total / MIB:.2f} MB ({pub_total / 1e6:.1f} MB) |")
    lines.append(f"| Installed without voices or images (`/COMPONENTS=\"app\"`) | {comp['app'] / MIB:.2f} MB |")
    if any(not pub for _, _, pub, _ in packs(release)):
        lines.append(f"| **Installed, private test build** (every component) | {all_total / MIB:.2f} MB ({all_total / 1e6:.1f} MB) |")
    else:
        lines.append("| Installed, private test build | — (no non-public pack in this folder: a public build) |")
    return lines


def report(new, old=None, notes=None, installer=None):
    """The Markdown report; ``old`` None (plan 13, a CI build) = the new folder alone, no comparison column."""
    if old is None:
        n_name, n_total = os.path.basename(os.path.normpath(new)), size(new)
        n_app = n_total - size(os.path.join(new, "voices"))
        n_g = os.path.join(new, "grammar.sqlite")
        lines = [f"# Size report — {n_name}", "",
                 f"Folder: `<work>\\{n_name}` (`<work>` = the bulk-data folder; no earlier release to compare with). MB = MiB,",
                 "sums of file sizes (decimal megabytes in brackets).", "",
                 f"| Part | {n_name} |", "|---|---|",
                 f"| Whole folder | {n_total / MIB:.2f} MB ({n_total / 1e6:.1f} MB) |"]
        lines += [f"| `voices\\{name}\\` ({lic}{'' if public else ', not public'}) | {sz / MIB:.2f} MB ({sz / 1e6:.2f} MB) |"
                  for name, lic, public, sz in packs(new)]
        lines.append(f"| `grammar.sqlite` (MPL-2.0) | "
                     f"{f'{size(n_g) / MIB:.2f} MB ({size(n_g) / 1e6:.1f} MB)' if os.path.isfile(n_g) else '**absent**'} |")
        lines.append(f"| **App without voices** | {n_app / MIB:.2f} MB |")
        return _sections(lines, new, notes, installer)
    n_name, o_name = os.path.basename(os.path.normpath(new)), os.path.basename(os.path.normpath(old))
    n_total, o_total = size(new), size(old)
    n_voices, o_voices = size(os.path.join(new, "voices")), size(os.path.join(old, "voices"))
    n_app, o_app = n_total - n_voices, o_total - o_voices
    lines = [
        f"# Size report — {n_name} against {o_name}",
        "",
        f"Folders: `<work>\\{n_name}` and `<work>\\{o_name}` (`<work>` = the local bulk-data folder). MB = MiB, sums of",
        "file sizes (decimal megabytes in brackets).",
        "",
        f"| Part | {o_name} | {n_name} |",
        "|---|---|---|",
        f"| Whole folder | {o_total / MIB:.2f} MB | {n_total / MIB:.2f} MB ({n_total / 1e6:.1f} MB) |",
    ]
    old_packs = {p[0]: p for p in packs(old)}
    for name, lic, public, sz in packs(new):
        o = old_packs.get(name)
        lines.append(f"| `voices\\{name}\\` ({lic}{'' if public else ', not public'}) | "
                     f"{f'{o[3] / MIB:.2f} MB' if o else '—'} | {sz / MIB:.2f} MB ({sz / 1e6:.2f} MB) |")
    n_g, o_g = os.path.join(new, "grammar.sqlite"), os.path.join(old, "grammar.sqlite")
    lines.append(f"| `grammar.sqlite` (MPL-2.0) | {f'{size(o_g) / MIB:.2f} MB' if os.path.isfile(o_g) else '—'} | "
                 f"{f'{size(n_g) / MIB:.2f} MB ({size(n_g) / 1e6:.1f} MB)' if os.path.isfile(n_g) else '**absent**'} |")
    lines.append(f"| **App without voices** | {o_app / MIB:.2f} MB | {n_app / MIB:.2f} MB (**{(n_app - o_app) / MIB:+.2f} MB**) |")
    return _sections(lines, new, notes, installer)


def _sections(lines, new, notes, installer):
    """The sections after the size table: packs that are not public, grammar, installer rows, notes."""
    private = [p for p in packs(new) if not p[2]]
    lines += ["", "## Packs that are not public (`\"public\": false`)", ""]
    if private:
        lines += ["| Pack | Licence | Size |", "|---|---|---|"]
        lines += [f"| `voices\\{name}\\` | {lic} | {sz / MIB:.2f} MB |" for name, lic, _, sz in private]
        lines += ["", "This is a private test release. A public release (plan 10) must leave out every pack whose",
                  "`voice.json` says `\"public\": false` (D28)."]
    else:
        lines.append("None.")
    missing = grammar_missing(new)
    lines += ["", "## Grammar (plan 11)", ""]
    if missing:
        lines.append("**Warning:** missing " + ", ".join(f"`{m}`" for m in missing) + ". Without `grammar.sqlite` the bar "
                     "ranks as 0.8.3; the MPL-2.0 data must ship with `grammar/SOURCE.txt` and `grammar/MPL-2.0.txt`.")
    else:
        lines.append("`grammar.sqlite` ships with `grammar/SOURCE.txt` and `grammar/MPL-2.0.txt`.")
    lines += install_rows(new, installer)
    text = "\n".join(lines) + "\n"
    if notes:
        with open(notes, encoding="utf-8") as f:
            text += "\n" + f.read()
    return text


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--new", required=True)
    p.add_argument("--old", help="the earlier release folder to compare with (optional since plan 13: CI builds)")
    p.add_argument("--out", required=True)
    p.add_argument("--notes")
    p.add_argument("--installer", help="the installer .exe (plan 12): its size is reported")
    a = p.parse_args(argv)
    db = os.path.join(a.new, "spelling.sqlite")
    n = unlisted_count(db)
    if not n:
        raise SystemExit("%s: no spelling reference (table spelling_unlisted %s): run build_spelling_ref.py build"
                         % (db, "empty" if n == 0 else "absent"))
    for m in ("spelling/SOURCE.txt", "spelling/MPL-2.0.txt"):
        if not os.path.isfile(os.path.join(a.new, *m.split("/"))):
            raise SystemExit("%s missing: the MPL-2.0 spelling.sqlite ships only with its notices" % m)
    print("spelling reference: %d unlisted forms" % n)
    for m in grammar_missing(a.new):
        print("WARNING: %s missing in the release (plan 11, grammatical prediction)" % m, file=sys.stderr)
    text = report(a.new, a.old, a.notes, a.installer)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print(f"wrote {a.out} ({len(text)} chars)")


if __name__ == "__main__":
    main()
