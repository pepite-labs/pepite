#!/usr/bin/env python3
"""Quick content checks of a prediction.sqlite (plan corpus-light 0.6): sizes, OCR noise, old-fashioned language.

    python -I db_checks.py <prediction.sqlite> [--json <out.json>] [--label A1]

Reports, per n-gram table (bigrams, trigrams, fourgrams):
  - rows and total count mass, and the file size;
  - « ocr »: the share of the count mass of n-grams that contain a token of the OCR / accent-stripping list
    (« strict »: without « ii » and « ia », which are also Roman numeral II and « IA »)
    (subtitle noise: « ca » for « ça », « ii » for « il », « ést »…), which a written-only corpus should bring to ≈ 0;
  - « passé simple »: the share of the count mass whose **predicted** word (the last one) is an unambiguous passé simple
    or imperfect subjunctive form (19th-century books carry 8× more than subtitles; gate: D ≤ A′ + 0.5 pt).
Tokens in the DB are already folded (« oe »); the lists below are folded with the same fold().
"""
import argparse
import json
import os
import re
import sqlite3
import sys

# the study's list (corpus-written-children.md § 1.1 and § 5.3): subtitle OCR / accent-stripping tokens
OCR = {"ést", "ii", "ca", "ie", "ia", "ll", "ecoute", "etre"}
# two of them are also real written French: « II » (Henri II, la Seconde Guerre mondiale… in Vikidia) and « IA »
# (intelligence artificielle). The strict share leaves them out; it is the one the corpus gate reads (measured
# 2026-11-16: in the written-only sample « ii » / « ia » are 95 % of the list's mass, all of it legitimate).
OCR_AMBIGUOUS = {"ii", "ia"}
PASSE_SIMPLE_WORDS = {"fut", "furent", "eut", "eurent", "fit", "firent", "vint", "vinrent", "mit", "mirent", "prit",
                      "prirent", "dirent", "fûmes", "fûtes", "eûmes", "fît", "fût", "eût", "vînt"}
# endings of the study's list minus the ambiguous ones: « -assent » (passent) and « -aît / -oît » (plaît, connaît) are present tense
PASSE_SIMPLE_END = re.compile(r"(èrent|âmes|âtes|ât|(?<![ao])ît|ûmes|ûtes|înmes|întes)$")


def fold(token):
    """« œ » -> « oe », « æ » -> « ae » (same function as build_ngrams.py; a unit test checks they agree)."""
    return token.replace("œ", "oe").replace("æ", "ae")


OCR = {fold(w) for w in OCR}


def is_passe_simple(word):
    w = fold(word)
    return w in PASSE_SIMPLE_WORDS or bool(PASSE_SIMPLE_END.search(w)) and len(w) > 4


TABLES = {"bigrams": ["prev", "word"], "trigrams": ["p2", "p1", "word"], "fourgrams": ["p3", "p2", "p1", "word"]}


def check(db):
    con = sqlite3.connect("file:%s?mode=ro" % db, uri=True)
    have = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    out = {"file": os.path.basename(db), "bytes": os.path.getsize(db), "tables": {}}
    if "words" in have:
        out["tables"]["words"] = {"rows": con.execute("SELECT COUNT(*) FROM words").fetchone()[0]}
    for t, cols in TABLES.items():
        if t not in have:
            continue
        rows = mass = ocr = strict = ps = 0
        for r in con.execute("SELECT %s, c FROM %s" % (", ".join(cols), t)):
            c = r[-1]
            toks = r[:-1]
            rows += 1
            mass += c
            if any(x in OCR for x in toks):
                ocr += c
                if any(x in OCR and x not in OCR_AMBIGUOUS for x in toks):
                    strict += c
            if is_passe_simple(toks[-1]):
                ps += c
        out["tables"][t] = {"rows": rows, "mass": mass, "ocr_share": ocr / mass if mass else 0.0,
                            "ocr_strict_share": strict / mass if mass else 0.0,
                            "passe_simple_share": ps / mass if mass else 0.0}
    con.close()
    return out


def summary(res):
    lines = ["%s: %.1f MB" % (res["file"], res["bytes"] / 1048576)]
    for t, s in res["tables"].items():
        if "mass" in s:
            lines.append("  %-10s %9d rows  mass %11d  OCR %.4f %% (strict %.4f %%)  passé simple %.3f %%"
                         % (t, s["rows"], s["mass"], 100 * s["ocr_share"], 100 * s["ocr_strict_share"],
                            100 * s["passe_simple_share"]))
        else:
            lines.append("  %-10s %9d rows" % (t, s["rows"]))
    return "\n".join(lines)


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("db")
    ap.add_argument("--json")
    ap.add_argument("--label")
    a = ap.parse_args(argv)
    res = check(a.db)
    if a.label:
        res["label"] = a.label
    print(summary(res))
    if a.json:
        with open(a.json, "w", encoding="utf-8", newline="\n") as f:
            json.dump(res, f, ensure_ascii=False, indent=2)
            f.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
