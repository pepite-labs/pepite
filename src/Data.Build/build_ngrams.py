#!/usr/bin/env python3
"""Add bigram + trigram context models to prediction.sqlite from open French corpora.

Trains P(word | prev) and P(word | prev2, prev) so the predictor can use up to two previous words
of context. Pruned by minimum count to stay small and fast (resource budget: about 240 MB disk +
memory for the whole app, voices excluded).

Inputs: one or more corpus files. Tatoeba `*.tsv` (text = last column) or Leipzig
`*-sentences.txt` ("id<TAB>sentence") or plain text (one sentence per line) are auto-detected.
N-gram counts are statistics derived from the corpora; the corpora themselves are not redistributed.

Usage:
    python -I build_ngrams.py <prediction.sqlite> <corpus1> [corpus2 ...] [--min2 3 --min3 3]
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
                yield line.rstrip("\n").split("\t")[-1]   # Tatoeba/Leipzig: text is last column
            else:
                yield line


def build(db_path, corpora, min2, min3):
    bi, tri = Counter(), Counter()
    n = 0
    for corpus in corpora:
        for s in sentences(corpus):
            t = [fold(x) for x in TOKEN.findall(s.lower().replace("'", " ").replace("’", " ").replace("-", " "))]
            for a, b in zip(t, t[1:]):
                bi[(a, b)] += 1
            for a, b, c in zip(t, t[1:], t[2:]):
                tri[(a, b, c)] += 1
            n += 1
            if n % 250000 == 0:
                print(f"  {n} sentences | {len(bi)} bi, {len(tri)} tri types")

    birows = [(a, b, c) for (a, b), c in bi.items() if c >= min2]
    trirows = [(a, b, c, d) for (a, b, c), d in tri.items() if d >= min3]

    con = sqlite3.connect(db_path)
    con.execute("DROP TABLE IF EXISTS bigrams")
    con.execute("DROP TABLE IF EXISTS trigrams")
    con.execute("CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER)")
    con.execute("CREATE TABLE trigrams (p2 TEXT, p1 TEXT, word TEXT, c INTEGER)")
    con.executemany("INSERT INTO bigrams VALUES (?,?,?)", birows)
    con.executemany("INSERT INTO trigrams VALUES (?,?,?,?)", trirows)
    con.execute("CREATE INDEX idx_bi_prev ON bigrams(prev)")
    con.execute("CREATE INDEX idx_tri_ctx ON trigrams(p2, p1)")
    con.commit()
    con.close()
    print(f"wrote {len(birows)} bigrams (>= {min2}) and {len(trirows)} trigrams (>= {min3}) "
          f"from {n} sentences -> {db_path}")


if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) < 2:
        raise SystemExit(__doc__)
    min2 = int(a[a.index("--min2") + 1]) if "--min2" in a else 3
    min3 = int(a[a.index("--min3") + 1]) if "--min3" in a else 3
    flags = {"--min2", "--min3"}
    pos = [x for i, x in enumerate(a) if x not in flags and (i == 0 or a[i - 1] not in flags)]
    build(pos[0], pos[1:], min2, min3)
