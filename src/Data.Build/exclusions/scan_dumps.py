#!/usr/bin/env python3
"""Scans files for the « mots exclus » (plan offensive-words, A5) — **never prints a word**: counts, line numbers and the
``forms.tsv`` ids (``F00012``; a form added from grammar.sqlite: ``G0003``, its rank in ordinal order) only.

    python -I scan_dumps.py [--forms forms.tsv] [--grammar grammar.sqlite] <Predict.Eval .log / --dump files ...>
    python -I scan_dumps.py --text <docs, logs, commit messages ...>

Eval files: in a curated ``.log`` case line (« ✓1 typed → word | bar, words »), a listed form after the ``|`` is
**offered** (the bar or the ≈ list); before it, it is the case's own text (**expected**: typed / intended word). In a held-out
``--dump`` (``n<TAB>context<TAB>word<TAB>letters``) the third column is the **expected** word. ``--text``: every word of every
line counts (« text »). A word matches as the engine's ``ExcludedWords.IsExcluded`` does: the whole word (a hyphenated
compound too), case ignored, then the part after each apostrophe. A compound whose **part** is listed is counted apart as
« part » (the engine matches whole words only, so it would offer the compound): a hit to review, not an offered form. ``--grammar``: also the grammar-only forms of the excluded lemmas the engine adds at
load (every grammar lemma excluded, not a prediction-lexicon form; needs ``--db``).

Exit code 0 = no hit, 1 = at least one hit, 2 = bad arguments.
"""
import argparse
import os
import re
import sqlite3
import sys
import unicodedata

HERE = os.path.dirname(os.path.abspath(__file__))
TOKEN = re.compile(r"[A-Za-zÀ-ÖØ-öø-ÿœŒæÆ'’-]+")   # with « - »: a hyphenated form is listed whole (reviewer 2026-11-27)
CASE_LINE = re.compile(r"^\s*(?:✓\d+|✗|ok\d+)\s")


def norm(s):
    return unicodedata.normalize("NFC", s).lower()


def read_forms(path):
    """form (lowercase NFC) -> id, and the excluded lemmas (lemmas.tsv next to forms.tsv)."""
    ids = {}
    with open(path, encoding="utf-8") as f:
        next(f, None)
        for line in f:
            c = line.rstrip("\n").split("\t")
            if len(c) >= 2:
                ids.setdefault(norm(c[1]), c[0])
    return ids


def read_excluded_lemmas(path):
    out = []
    if not os.path.exists(path):
        return out
    with open(path, encoding="utf-8") as f:
        head = next(f, "").rstrip("\n").split("\t")
        for line in f:
            r = dict(zip(head, line.rstrip("\n").split("\t")))
            if r.get("decision") == "exclude":
                out.append(r["lemma"])
    return out


def grammar_forms(grammar, db, lemmas):
    """The engine's load-time expansion (ExcludedWords.WithGrammar), as a set of lowercase forms."""
    excl = set(lemmas)
    lex = set()
    c = sqlite3.connect("file:%s?mode=ro" % db, uri=True)
    for (o,) in c.execute("SELECT ortho FROM words"):
        lex.add(o.lower())
    c.close()
    g = sqlite3.connect("file:%s?mode=ro" % grammar, uri=True)
    lemma_of = {}
    q = ",".join("?" * len(lemmas))
    ids = [r[0] for r in g.execute("SELECT id FROM lemma WHERE lemma IN (%s)" % q, lemmas)] if lemmas else []
    if ids:
        for form, lem in g.execute("SELECT m2.form, l.lemma FROM morph m JOIN morph m2 ON m2.form = m.form "
                                   "JOIN lemma l ON l.id = m2.lemma_id WHERE m.lemma_id IN (%s)" % ",".join(map(str, ids))):
            lemma_of.setdefault(form, set()).add(lem)
    if lemmas:
        for form, lem in g.execute("SELECT m.form, l.lemma FROM morph m JOIN lemma l ON l.id = m.lemma_id "
                                   "WHERE m.form IN (%s)" % q, lemmas):
            lemma_of.setdefault(form, set()).add(lem)
    g.close()
    for l in lemmas:
        lemma_of.setdefault(l, {l})
    return {norm(f) for f, ls in lemma_of.items() if ls and f.lower() not in lex and all(x in excl for x in ls)}


def read_names_ok(path):
    """The listed spellings a capitalised « Noms propres » pack name keeps (``name-ok`` rows of lemmas.tsv, owner
    2026-11-27)."""
    out = set()
    if not os.path.exists(path):
        return out
    with open(path, encoding="utf-8") as f:
        head = next(f, "").rstrip("\n").split("\t")
        for line in f:
            r = dict(zip(head, line.rstrip("\n").split("\t")))
            if r.get("decision") == "name-ok":
                out.add(norm(r["lemma"]))
    return out


def matcher(ids, allowed=None, names_ok=()):
    """word -> id or None: the whole token, then the part after each apostrophe; for a hyphenated token, then each part
    the same way (« x-y »). ``allowed``: ids not counted (``--allow``). A token written with a capital whose spelling is
    a ``name-ok`` one (an exempt pack name) gives « ~id », counted as allowed."""
    def one(w):
        if w in ids:
            return ids[w]
        for i, ch in enumerate(w[:-1]):
            if ch in "'’" and w[i + 1:] in ids:
                return ids[w[i + 1:]]
        return None

    def hit(word):
        w = norm(word).strip("-")
        if word[:1].isupper() and w in names_ok and w in ids:
            return "~" + ids[w]
        i = one(w)
        if i is None and "-" in w:
            for part in w.split("-"):
                i = one(part) if part else None
                if i:
                    i = "^" + i            # a listed part of a compound: reported apart (the engine matches whole words)
                    break
        return None if i is None or (allowed is not None and i.lstrip("^") in allowed) else i
    return hit


def scan_line(line, hit, mode):
    """[(kind, id)] of one line: kind offered / expected / text."""
    out = []
    if mode == "text":
        return [("text", i) for i in (hit(t) for t in TOKEN.findall(line)) if i]
    if mode == "dump":
        cols = line.rstrip("\r\n").split("\t")
        if len(cols) >= 3:
            out += [("expected", i) for i in (hit(t) for t in TOKEN.findall(cols[2])) if i]
            out += [("context", i) for i in (hit(t) for t in TOKEN.findall(cols[1])) if i]
        return out
    if "|" in line and CASE_LINE.match(line):
        left, right = line.split("|", 1)
        out += [("expected", i) for i in (hit(t) for t in TOKEN.findall(left)) if i]
        out += [("offered", i) for i in (hit(t) for t in TOKEN.findall(right)) if i]
    elif "|" in line:
        left, right = line.split("|", 1)
        out += [("offered", i) for i in (hit(t) for t in TOKEN.findall(right)) if i]
    return out


def read_allow(path):
    """[(path suffix, id)] of the reviewed exceptions: ``id<TAB>path<TAB>reason`` lines, ``#`` comments."""
    out = []
    if path and os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            for line in f:
                c = line.rstrip("\n").split("\t")
                if line.startswith("#") or len(c) < 2 or not c[0].strip():
                    continue
                out.append((c[1].strip().replace("\\", "/").lower(), c[0].strip()))
    return out


def allowed_for(path, allow):
    p = os.path.abspath(path).replace("\\", "/").lower()
    return {i for suffix, i in allow if p.endswith("/" + suffix) or p == suffix}


def scan_file(path, hit, text_mode, allowed=()):
    mode = "text" if text_mode else ("dump" if path.lower().endswith(".dump") else "log")
    counts, lines = {}, []
    with open(path, encoding="utf-8", errors="replace") as f:
        for n, line in enumerate(f, 1):
            hs = [("allowed", i[1:]) if i.startswith("~") else ("part", i[1:]) if i.startswith("^")
                  else ("allowed" if i in allowed else k, i) for k, i in scan_line(line, hit, mode)]
            for kind, i in hs:
                counts[kind] = counts.get(kind, 0) + 1
            if hs:
                lines.append((n, hs))
    return counts, lines


def main(argv=None):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    p.add_argument("files", nargs="*")
    p.add_argument("--forms", default=os.path.join(HERE, "forms.tsv"))
    p.add_argument("--lemmas", default=os.path.join(HERE, "lemmas.tsv"))
    p.add_argument("--grammar", help="grammar.sqlite: also the forms the engine adds at load")
    p.add_argument("--db", help="prediction.sqlite (with --grammar)")
    p.add_argument("--text", nargs="+", help="plain files: every word counts")
    p.add_argument("--max-lines", type=int, default=30, help="hit lines listed per file")
    p.add_argument("--allow", default=os.path.join(HERE, "scan-allow.tsv"),
                   help="reviewed exceptions (id, file, reason): counted as « allowed », not as hits")
    a = p.parse_args(argv)
    files = list(a.files) + list(a.text or [])
    if not files:
        p.print_usage(sys.stderr)
        return 2
    ids = read_forms(a.forms)
    n_listed = len(ids)
    if a.grammar:
        if not a.db:
            print("--grammar needs --db", file=sys.stderr)
            return 2
        extra = sorted(grammar_forms(a.grammar, a.db, read_excluded_lemmas(a.lemmas)) - set(ids))
        for k, f in enumerate(extra, 1):
            ids[f] = "G%04d" % k
    print("scan: %d forms (%d from forms.tsv, %d grammar forms)" % (len(ids), n_listed, len(ids) - n_listed))
    hit = matcher(ids, names_ok=read_names_ok(a.lemmas))
    allow = read_allow(a.allow)
    total = {}
    for path in files:
        if not os.path.isfile(path):
            print("%s: missing" % os.path.basename(path))
            total["missing"] = total.get("missing", 0) + 1
            continue
        counts, lines = scan_file(path, hit, path in (a.text or []), allowed_for(path, allow))
        for k, v in counts.items():
            total[k] = total.get(k, 0) + v
        print("%s: %s" % (os.path.basename(path), ", ".join("%s %d" % (k, v) for k, v in sorted(counts.items())) or "0 hits"))
        for n, hs in lines[:a.max_lines]:
            print("  line %d: %s" % (n, ", ".join("%s %s" % (k, i) for k, i in hs)))
        if len(lines) > a.max_lines:
            print("  … %d more lines" % (len(lines) - a.max_lines))
    hits = sum(v for k, v in total.items() if k not in ("missing", "allowed"))
    print("total: %d hits (%s)" % (hits, ", ".join("%s %d" % (k, v) for k, v in sorted(total.items())) or "none"))
    return 1 if hits or total.get("missing") else 0


if __name__ == "__main__":
    sys.exit(main())
