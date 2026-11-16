#!/usr/bin/env python3
"""Write prediction-SOURCES.txt from data.lock.json (plan corpus-light 0.6; used in Phase 2 only).

    python -I write_sources.py --out <dir>/prediction-SOURCES.txt [--books pd-books.tsv] [--roles train-light,train-full]
                               [--thresholds "2-grams ≥ 3, 3-grams ≥ 3, 4-grams ≥ 4"] [--private]

Lists every input whose `use` is « train » (the words table and the n-gram counts), with its version, URL, licence,
SHA-256 and size, the credit line, and what was done to it. Gutenberg books are listed only when their pd-books.tsv role
is one of --roles. An input with `public: false` (the subtitles) makes the command fail unless --private is given: a
public database must not carry them (D19, owner answer Q2 of the corpus study).
"""
import argparse
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
LOCK = os.path.join(os.path.dirname(HERE), "data.lock.json")

HEADER = """prediction.sqlite — sources
===========================

prediction.sqlite is a data file distributed under the Creative Commons Attribution-ShareAlike 4.0 International
licence (CC BY-SA 4.0, https://creativecommons.org/licenses/by-sa/4.0/). It holds the word list of Lexique 3.83 and
counts of 2-, 3- and 4-word sequences (n-grams) extracted from the texts below. No text of these sources is included:
only lower-cased n-gram counts, pruned by minimum count ({thresholds}). Changes made: n-gram counts extracted
(lower-cased, apostrophes and hyphens split, « œ » / « æ » written « oe » / « ae »), pruned.
The spelling reference of the spelling indicator (the Lexique forms of letters absent from Grammalecte's « Lexique
simplifié » French.lex v7.7, https://grammalecte.net) is not in this file: it is MPL-2.0 data, shipped separately as
spelling.sqlite with spelling/SOURCE.txt and spelling/MPL-2.0.txt (Data.Build/build_spelling_ref.py).
No Project Gutenberg text or licence is distributed; the name only identifies the source of the public-domain books.
"""


def load_roles(path):
    roles = {}
    with open(path, encoding="utf-8") as f:
        rows = [l.rstrip("\n").split("\t") for l in f if not l.startswith("#")]
    head = rows[0]
    for r in rows[1:]:
        d = dict(zip(head, r))
        roles[int(d["gutenberg_id"])] = d.get("role", "")
    return roles


def render(lock, roles=None, keep_roles=("train-light", "train-full"), thresholds="2-grams ≥ 3, 3-grams ≥ 3, 4-grams ≥ 4",
           private=False):
    out = [HEADER.format(thresholds=thresholds)]
    main, books = [], []
    for e in lock["inputs"]:
        if e.get("use") != "train":
            continue
        if e.get("group") == "gutenberg":
            if roles is not None and roles.get(e["gutenberg_id"]) not in keep_roles:
                continue
            books.append(e)
        else:
            main.append(e)
        if not e.get("public", True) and not private:
            raise SystemExit("%s is not allowed in a public build (public: false); use --private for a private build" % e["id"])
        if not e.get("sha256"):
            raise SystemExit("%s is not pinned (sha256 null): run fetch_corpus.py pin-new first" % e["id"])
    for e in main:
        out.append("- %s" % e["credit"])
        out.append("  version: %s; date: %s" % (e.get("version"), e.get("date")))
        out.append("  source: %s" % (e.get("url") or e.get("page")))
        out.append("  licence: %s%s" % (e["licence"], "; %s" % e["licence_page"] if e.get("licence_page") else ""))
        out.append("  sha256: %s (%s bytes)" % (e["sha256"], e.get("bytes")))
        out.append("  used for: %s" % ("the words table (imported, one row per spelling)" if e["id"].startswith("lexique")
                                       else "n-gram counts"))
        out.append("")
    if books:
        out.append("Public-domain books (Project Gutenberg, https://www.gutenberg.org; every author and translator died in "
                   "1955 or earlier; used for n-gram counts; no Project Gutenberg text or licence is distributed, the name "
                   "only identifies the source):")
        for e in sorted(books, key=lambda x: x["gutenberg_id"]):
            out.append("- %s; sha256 %s" % (e["credit"], e["sha256"]))
        out.append("")
    return "\n".join(out)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lock", default=LOCK)
    ap.add_argument("--books", default=os.path.join(HERE, "pd-books.tsv"))
    ap.add_argument("--roles", default="train-light,train-full")
    ap.add_argument("--thresholds", default="2-grams ≥ 3, 3-grams ≥ 3, 4-grams ≥ 4")
    ap.add_argument("--private", action="store_true")
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)
    with open(a.lock, encoding="utf-8") as f:
        lock = json.load(f)
    roles = load_roles(a.books) if os.path.exists(a.books) else None
    text = render(lock, roles, tuple(a.roles.split(",")), a.thresholds, a.private)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote %s" % a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
