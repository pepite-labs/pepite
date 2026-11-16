#!/usr/bin/env python3
"""The spelling indicator's reference list (0.8.3, D12): which lexicon forms may be marked ✓.

Lexique 3.83 counts its words in film subtitles and books, so it also holds English and slang forms (« yeah », « team »,
« mam », « till ») that the spelling indicator must not approve. The reference is Grammalecte's « Lexique simplifié pour
Grammalecte v7.7 » (``lexicons/French.lex``, MPL-2.0), pinned at the Grammalecte 2.3.0 release state (GitHub mirror
Pofilo/grammalecte, commit 08511c2…, study 10d § 1.2). A form of ``words`` is **trusted** when it is a form of French.lex,
or becomes one once « oe » is written « œ » (Lexique writes « coeur », « oeil », French.lex « cœur », « œil »). The forms
that are not trusted (words of letters only: the indicator never judges an elision or a compound as one word) go into
the table ``spelling_unlisted(ortho)`` of ``spelling.sqlite`` (a few thousand rows); the
engine checks them like unknown words (≈ or ?), never ✓. Prediction does not read the table.

Licence (publication cleanup, 2026-11-16): the table is derived from French.lex, so it is MPL-2.0 data (file-level
copyleft) and lives in its own file, ``spelling.sqlite``, next to ``prediction.sqlite`` (CC BY-SA 4.0), with
``spelling/SOURCE.txt`` and ``spelling/MPL-2.0.txt``. Up to data pack 2026.11 the tables were inside ``prediction.sqlite``;
``build`` drops them from a database that still has them (and VACUUMs it, which changes its SHA-256).

Sub-commands (run with ``python -I``; standard library only):

    fetch [--raw DIR] [--from FILE]   French.lex at the pinned commit -> DIR/French.lex, SHA-256 checked (--from: copy a
                                      local file instead of downloading; it must have the same SHA-256)
    build [--db out/prediction.sqlite] [--out out/spelling.sqlite] [--raw DIR]
                                      reads the ``words`` of the database, writes ``spelling.sqlite`` (tables
                                      ``spelling_unlisted`` and ``spelling_source``; byte-reproducible with the
                                      same Python / SQLite versions, recorded in SOURCE.txt),
                                      ``spelling/SOURCE.txt`` and ``spelling/MPL-2.0.txt`` next to it

DIR defaults to $PREDICT_GRAMMALECTE_RAW, else Data.Build/out/raw-grammalecte (git-ignored). The plan 10D-0 morphology
table reads the same pinned file through :func:`read_lex`.
"""
import argparse
import hashlib
import os
import platform
import shutil
import sqlite3
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
USER_AGENT = "PredictKeyboard-DataBuild/0.8 (spelling reference fetch)"
COMMIT = "08511c222029b3be20bf547563315d10fb48d44b"
URL = "https://raw.githubusercontent.com/Pofilo/grammalecte/%s/lexicons/French.lex" % COMMIT
SHA256 = "5938d8180b64e4f5320c358dab64e2eea1298c6c5967b147d5506f0ac4562c4a"
SOURCE = ("Grammalecte « Lexique simplifié » v7.7 (lexicons/French.lex), MPL-2.0, "
          "github.com/Pofilo/grammalecte commit %s (Grammalecte 2.3.0)" % COMMIT)
NAME = "French.lex"
MPL_TEXT = os.path.join(HERE, "..", "..", "licenses", "mpl-2.0", "LICENSE.txt")
TABLES = ("spelling_unlisted", "spelling_source")


def default_raw():
    return os.environ.get("PREDICT_GRAMMALECTE_RAW") or os.path.join(HERE, "out", "raw-grammalecte")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def checked(path):
    got = sha256(path)
    if got != SHA256:
        raise SystemExit("%s: SHA-256 %s, expected %s" % (path, got, SHA256))
    return path


def cmd_fetch(a):
    os.makedirs(a.raw, exist_ok=True)
    dest = os.path.join(a.raw, NAME)
    if os.path.exists(dest):
        checked(dest)
        print("%s already there (SHA-256 ok)" % dest)
        return
    part = dest + ".part"
    if a.source:
        shutil.copyfile(a.source, part)
    else:
        req = urllib.request.Request(URL, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(req, timeout=120) as r, open(part, "wb") as f:
            shutil.copyfileobj(r, f)
    if sha256(part) != SHA256:
        os.remove(part)
        raise SystemExit("downloaded %s: SHA-256 mismatch (expected %s)" % (NAME, SHA256))
    os.replace(part, dest)
    print("%s: SHA-256 ok" % dest)


def read_lex(path):
    """(form, lemma, tags) for each entry of French.lex; comment and blank lines skipped."""
    with open(path, encoding="utf-8") as f:
        for line in f:
            if not line.strip() or line.startswith("#"):
                continue
            parts = line.rstrip("\n").split("\t")
            if len(parts) >= 3:
                yield parts[0], parts[1], parts[2]


def reference_forms(path):
    return {form for form, _, _ in read_lex(path)}


def trusted(form, ref):
    """A lexicon form is trusted when French.lex has it, or has it with « oe » written « œ » (« coeur » → « cœur »)."""
    return form in ref or ("oe" in form and form.replace("oe", "œ") in ref)


def judged(form):
    """Only a word of letters is ever judged by the indicator: an elision (« l' »), a compound (« aujourd'hui »,
    « peut-être ») or an abbreviation (« p. ») never reaches it as one word, so it is left out of the table."""
    return form.isalpha()


def unlisted(forms, ref):
    return sorted({f for f in forms if judged(f) and not trusted(f, ref)})


def write_spelling_db(path, out, ref_count):
    """spelling.sqlite from scratch (temporary file, VACUUM, rename): the same inputs give the same bytes."""
    tmp = path + ".tmp"
    for f in (path, tmp):
        if os.path.exists(f):
            os.remove(f)
    con = sqlite3.connect(tmp)
    try:
        con.execute("PRAGMA page_size=4096")
        con.execute("PRAGMA journal_mode=DELETE")
        con.execute("CREATE TABLE spelling_unlisted (ortho TEXT PRIMARY KEY)")
        con.executemany("INSERT INTO spelling_unlisted VALUES (?)", ((f,) for f in out))
        con.execute("CREATE TABLE spelling_source (source TEXT, sha256 TEXT, reference_forms INTEGER, unlisted INTEGER)")
        con.execute("INSERT INTO spelling_source VALUES (?, ?, ?, ?)", (SOURCE, SHA256, ref_count, len(out)))
        con.commit()
        con.execute("VACUUM")
    finally:
        con.close()
    os.replace(tmp, path)


def drop_legacy_tables(db):
    """Up to pack 2026.11 the tables were inside prediction.sqlite: drop them there (and VACUUM). True when dropped."""
    con = sqlite3.connect(db)
    try:
        have = [n for (n,) in con.execute("SELECT name FROM sqlite_master WHERE type = 'table'") if n in TABLES]
        if not have:
            return False
        for n in have:
            con.execute("DROP TABLE %s" % n)
        con.commit()
        con.execute("VACUUM")
        return True
    finally:
        con.close()


def write_source(dir_, out_path, ref_count, unlisted_n, forms_n):
    os.makedirs(dir_, exist_ok=True)
    lines = [
        "spelling.sqlite — reference list of the spelling indicator (Pépite, D12)",
        "",
        "Licence: Mozilla Public License 2.0 (MPL-2.0), the text in MPL-2.0.txt next to this file.",
        "spelling.sqlite is Covered Software: a Modification of French.lex (Grammalecte « Lexique simplifié » v7.7),",
        "distributed in Executable Form (MPL-2.0 § 3.2). Pépite (GPL-3.0-or-later) is the Larger Work (MPL-2.0 § 3.3).",
        "Source Code Form: French.lex at https://github.com/Pofilo/grammalecte/blob/%s/lexicons/French.lex" % COMMIT,
        "plus the converter src/Data.Build/build_spelling_ref.py of Pépite's source.",
        "Content: the spellings of the prediction lexicon (Lexique 3.83, CC BY-SA 4.0, http://www.lexique.org) made of",
        "letters only that French.lex does not have, also once « oe » is written « œ » (table spelling_unlisted), and the",
        "source of the comparison (table spelling_source). French.lex itself is not included.",
        "",
        "Source: %s" % SOURCE,
        "French.lex SHA-256: %s" % SHA256,
        "French.lex notice: « This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy",
        "of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/. »",
        "",
        "Built with Python %s, SQLite %s" % (platform.python_version(), sqlite3.sqlite_version),
        "Counts: %d prediction forms, %d French.lex forms, %d unlisted" % (forms_n, ref_count, unlisted_n),
        "spelling.sqlite SHA-256: %s" % sha256(out_path),
        "",
    ]
    with open(os.path.join(dir_, "SOURCE.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    mpl = os.path.normpath(MPL_TEXT)
    if not os.path.exists(mpl):
        raise SystemExit("MPL-2.0 text missing: %s" % mpl)
    shutil.copyfile(mpl, os.path.join(dir_, "MPL-2.0.txt"))


def cmd_build(a):
    lex = checked(os.path.join(a.raw, NAME))
    ref = reference_forms(lex)
    con = sqlite3.connect("file:%s?mode=ro" % a.db.replace("\\", "/"), uri=True)
    try:
        forms = [r[0] for r in con.execute("SELECT ortho FROM words")]
    finally:
        con.close()
    out = unlisted(forms, ref)
    write_spelling_db(a.out, out, len(ref))
    write_source(os.path.join(os.path.dirname(os.path.abspath(a.out)), "spelling"), a.out, len(ref), len(out), len(forms))
    if drop_legacy_tables(a.db):
        print("%s: the old spelling tables were dropped (now in %s); the database was VACUUMed, new SHA-256 %s"
              % (a.db, os.path.basename(a.out), sha256(a.db)))
    print("%d lexicon forms, %d reference forms, %d unlisted (%.1f %%)" % (len(forms), len(ref), len(out), 100.0 * len(out) / max(1, len(forms))))
    print("%s SHA-256 %s" % (a.out, sha256(a.out)))


def unlisted_count(db):
    """The rows of ``spelling_unlisted`` in ``spelling.sqlite`` (or an older prediction.sqlite), None when the file or the
    table is missing (a release check: the app then treats every lexicon form as trusted, the 0.8.2 behaviour)."""
    if not os.path.exists(db):
        return None
    con = sqlite3.connect("file:%s?mode=ro" % db.replace("\\", "/"), uri=True)
    try:
        if not con.execute("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'spelling_unlisted'").fetchone():
            return None
        return con.execute("SELECT COUNT(*) FROM spelling_unlisted").fetchone()[0]
    finally:
        con.close()


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    f = sub.add_parser("fetch")
    f.add_argument("--raw", default=default_raw())
    f.add_argument("--from", dest="source")
    b = sub.add_parser("build")
    b.add_argument("--raw", default=default_raw())
    b.add_argument("--db", default=os.path.join(HERE, "out", "prediction.sqlite"))
    b.add_argument("--out", default=os.path.join(HERE, "out", "spelling.sqlite"))
    a = p.parse_args(argv)
    {"fetch": cmd_fetch, "build": cmd_build}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
