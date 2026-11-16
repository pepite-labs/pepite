#!/usr/bin/env python3
"""Add a 4-gram table to prediction.sqlite (3 previous words of context).

Appends table `fourgrams(p3, p2, p1, word, c)` without touching the existing bigram/trigram
tables. Pruned by minimum count to stay small. Same corpora/tokeniser as build_ngrams.py.

Usage:
    python -I build_4grams.py <prediction.sqlite> <corpus1> [corpus2 ...] [--min 4]
"""
import re
import sqlite3
import sys
from collections import Counter

TOKEN = re.compile(r"[a-zà-ÿœ]+")


def fold(token):
    """« œ » -> « oe », « æ » -> « ae »: Lexique spells « coeur », so an unfolded « cœur » n-gram is never proposed.
    Applied to each token after the match, never to the whole line (plan corpus-light 0.1). Same function in
    build_ngrams.py, build_4grams.py, corpus/prepare_corpus.py and corpus/db_checks.py (a unit test checks they agree)."""
    return token.replace("œ", "oe").replace("æ", "ae")


def sentences(path):
    with open(path, encoding="utf-8", errors="ignore") as f:
        for line in f:
            if "\t" in line and (path.endswith(".tsv") or path.endswith("sentences.txt")):
                yield line.rstrip("\n").split("\t")[-1]
            else:
                yield line


def build(db_path, corpora, mincount):
    four = Counter()
    n = 0
    for corpus in corpora:
        for s in sentences(corpus):
            t = [fold(x) for x in TOKEN.findall(s.lower().replace("'", " ").replace("’", " ").replace("-", " "))]
            for a, b, c, d in zip(t, t[1:], t[2:], t[3:]):
                four[(a, b, c, d)] += 1
            n += 1
            if n % 500000 == 0:
                print(f"  {n} sentences | {len(four)} 4-gram types")
    rows = [(a, b, c, d, e) for (a, b, c, d), e in four.items() if e >= mincount]
    con = sqlite3.connect(db_path)
    con.execute("DROP TABLE IF EXISTS fourgrams")
    con.execute("CREATE TABLE fourgrams (p3 TEXT, p2 TEXT, p1 TEXT, word TEXT, c INTEGER)")
    con.executemany("INSERT INTO fourgrams VALUES (?,?,?,?,?)", rows)
    con.execute("CREATE INDEX idx_four_ctx ON fourgrams(p3, p2, p1)")
    con.commit()
    con.close()
    print(f"wrote {len(rows)} 4-grams (>= {mincount}) from {n} sentences -> {db_path}")


if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) < 2:
        raise SystemExit(__doc__)
    mn = int(a[a.index("--min") + 1]) if "--min" in a else 4
    pos = [x for i, x in enumerate(a) if x != "--min" and (i == 0 or a[i - 1] != "--min")]
    build(pos[0], pos[1:], mn)
