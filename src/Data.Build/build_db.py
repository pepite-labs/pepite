#!/usr/bin/env python3
"""Build the prediction lexicon DB (prediction.sqlite) from Lexique 3.83.

Input:  Lexique383.tsv  (CC BY-SA 4.0 — http://www.lexique.org, download separately)
Output: prediction.sqlite  with one table `words(ortho, lemma, cgram, phon, freq)`.

The runtime library (Predict.Core, C#) computes the phonetic fold key itself at load time,
so this step only normalises Lexique into a small, deduplicated SQLite file: everything comes
from Lexique.

Usage:
    python -I build_db.py <Lexique383.tsv> <prediction.sqlite>
"""
import sqlite3
import sys


def build(tsv_path, db_path):
    rows = {}
    with open(tsv_path, encoding="utf-8") as f:
        header = f.readline().rstrip("\n").split("\t")
        col = {name: i for i, name in enumerate(header)}
        need = ["ortho", "phon", "lemme", "cgram", "freqfilms2", "freqlivres"]
        for n in need:
            if n not in col:
                raise SystemExit(f"column {n!r} missing — is this Lexique 3.83? header={header[:6]}")
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) <= col["freqlivres"]:
                continue
            ortho = p[col["ortho"]].strip()
            if not ortho or " " in ortho:            # drop multi-word entries for v1
                continue
            try:
                freq = max(float(p[col["freqfilms2"]] or 0), float(p[col["freqlivres"]] or 0))
            except ValueError:
                freq = 0.0
            key = ortho.lower()
            # keep the most frequent homograph row per surface form
            if key not in rows or freq > rows[key][4]:
                rows[key] = (ortho, p[col["lemme"]], p[col["cgram"]], p[col["phon"]], freq)

    con = sqlite3.connect(db_path)
    con.execute("DROP TABLE IF EXISTS words")
    con.execute("CREATE TABLE words (ortho TEXT, lemma TEXT, cgram TEXT, phon TEXT, freq REAL)")
    con.executemany("INSERT INTO words VALUES (?,?,?,?,?)", rows.values())
    con.execute("CREATE INDEX idx_ortho ON words(ortho)")
    con.commit()
    n = con.execute("SELECT COUNT(*) FROM words").fetchone()[0]
    con.close()
    print(f"wrote {n} words -> {db_path}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    build(sys.argv[1], sys.argv[2])
