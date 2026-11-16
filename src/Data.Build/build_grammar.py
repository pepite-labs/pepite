#!/usr/bin/env python3
"""The grammar morphology table ``grammar.sqlite`` (plan 11, 10D-0): every analysis of every form of Grammalecte's
« Lexique simplifié » v7.7 (``lexicons/French.lex``, MPL-2.0), with its lemma and its tag decoded into our own feature
bits (``MorphBits``), plus an index of the prediction lexicon's forms (``pred_index``) for the engine.

Lexique 3.83 (``prediction.sqlite``) keeps one row per form and no gender, number or person; grammatical prediction
(10D-1) needs all the analyses of a form (« est » = être 3s and the noun « est »). The table is a separate file: MPL-2.0
data derived from French.lex (Covered Software: a Modification of French.lex, distributed in Executable Form, MPL § 3.2),
indexed by the prediction lexicon's spellings (the ``pred_index`` keys come from Lexique 3.83, CC BY-SA 4.0: « coeur »,
« aujourd'hui »). Pépite (GPL-3.0-or-later) is the Larger Work (MPL § 3.3). Its Source Code Form is French.lex at the
pinned commit plus this converter.

Sub-commands (run with ``python -I``; standard library only):

    fetch [--raw DIR] [--from-dir DIR | --from-archive TAR]
                                         the pinned files of grammar.lock.json -> DIR, SHA-256 checked (--from-dir: an
                                         extracted copy of the pinned archive; --from-archive: the pinned tarball itself,
                                         its SHA-256 checked, members read by name, nothing extracted to disk)
    fetch-archive --dest DIR             the pinned tarball -> DIR (once; kept, SHA-256 checked), so that the upstream
                                         mirror can disappear
    check-official [--raw DIR]           optional O1 check: does grammalecte.net serve the 2.3.0 archive, and is its
                                         French.lex identical? The answer goes to DIR/official-check.txt (never fails)
    build [--raw DIR] [--db out/prediction.sqlite] [--out out/grammar.sqlite]
                                         writes grammar.sqlite (byte-reproducible), out/grammar/SOURCE.txt and
                                         out/grammar/MPL-2.0.txt; prints the file's SHA-256
    mapping-report --lexique Lexique383.tsv [--grammar out/grammar.sqlite] [--report FILE]
                                         compares POS, gender, number and person with Lexique (report only, never
                                         shipped); exit 1 below the gate (0.95 for number on N/A, person on finite verbs)

DIR defaults to $PREDICT_GRAMMALECTE_RAW, else Data.Build/out/raw-grammalecte (git-ignored), as build_spelling_ref.py.
Downloaded files are data: they are read, never executed.
"""
import argparse
import datetime
import hashlib
import json
import os
import platform
import re
import shutil
import sqlite3
import struct
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_spelling_ref as SR  # noqa: E402

LOCK = os.path.join(HERE, "grammar.lock.json")
# no personal data: the project's provisional public URL (D18) only
USER_AGENT = "Pepite-Data.Build (+https://github.com/pepite-labs/pepite)"
OFFICIAL = "https://grammalecte.net/zip/Grammalecte-fr-v2.3.0.zip"
MPL_TEXT = os.path.join(HERE, "..", "..", "licenses", "mpl-2.0", "LICENSE.txt")
SCHEMA = 1

# ---------------------------------------------------------------- MorphBits (our own layout; C#: Predict.Core.MorphBits)
POS_N, POS_A, POS_DET, POS_SUBJ, POS_PRON, POS_FINITE, POS_Q, POS_Y, POS_P, POS_PREP, POS_CONJ, POS_ADV, POS_OTHER = \
    1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 15
POS_MASK = 0xF
MASC, FEM = 1 << 4, 1 << 5
SING, PLUR = 1 << 6, 1 << 7
PERSON = {"1s": 1 << 8, "2s": 1 << 9, "3s": 1 << 10, "1p": 1 << 11, "2p": 1 << 12, "3p": 1 << 13}
MOOD_I, MOOD_S, MOOD_E, MOOD_K = 1 << 14, 1 << 15, 1 << 16, 1 << 17
ETRE, AVOIR, AUX_ETRE, MODAL = 1 << 18, 1 << 19, 1 << 20, 1 << 21
MODALS = {"pouvoir", "vouloir", "devoir", "aller", "savoir", "falloir"}
MOODS = {"Ip": MOOD_I, "Iq": MOOD_I, "Is": MOOD_I, "If": MOOD_I, "Sp": MOOD_S, "Sq": MOOD_S, "E": MOOD_E, "K": MOOD_K}
DETS = {"D", "Dp", "Dd", "Di", "Dn", "B"}          # « B » cardinal number: « deux maisons » agrees like a determiner
OTHER_PRON = {"Oo", "Ov", "Od", "Oi", "Oj", "Or", "On", "Ow", "Ot"}
NOMINAL = {POS_N, POS_A, POS_DET, POS_SUBJ, POS_PRON, POS_Q}   # the analyses that carry gender and number
VERBAL = {POS_FINITE, POS_Q, POS_Y, POS_P}


def fields(tag):
    """The ``:`` fields of a tag without its dictionary part (``/*``), its other fields (``;LEX ~SEM =FQ``) and the
    ``#`` sub-marks: « :N:A:m:i;C/* » → ['N', 'A', 'm', 'i']."""
    tag = tag.split("/", 1)[0]
    out = []
    for m in re.finditer(r"([:;~=])([^:;~=]*)", tag):
        if m.group(1) == ":":
            out.append(m.group(2).split("#", 1)[0])
    return out


def decode(tag, lemma=""):
    """The analyses of one French.lex tag as MorphBits values, one per part of speech (a tag can carry two: « :N:A »
    noun and adjective, « :Q:A » participle and adjective, « :G:R:D » « aux » = preposition + determiner), sorted by POS.
    Gender / number go to the nominal analyses (N, A, Det, pronouns, Q), persons and moods to the finite one. The
    interrogative first person (« 1ś », « 1ŝ »: « aimé-je ») counts as 1s, the « ! » mark of « 3p! » is dropped."""
    fs = fields(tag)
    pos = set()
    verb = None
    for f in fs:
        if re.match(r"^V[0-3]", f):
            verb = f
        elif f == "N":
            pos.add(POS_N)
        elif f == "A":
            pos.add(POS_A)
        elif f == "Q":
            pos.add(POS_Q)
        elif f == "Y":
            pos.add(POS_Y)
        elif f == "P":
            pos.add(POS_P)
        elif f in MOODS:
            pos.add(POS_FINITE)
        elif f in DETS:
            pos.add(POS_DET)
        elif f == "Os":
            pos.add(POS_SUBJ)
        elif f in OTHER_PRON:
            pos.add(POS_PRON)
        elif f in ("R", "Rv"):
            pos.add(POS_PREP)
        elif f in ("C", "Cc", "Cs"):
            pos.add(POS_CONJ)
        elif f in ("W", "X", "U"):
            pos.add(POS_ADV)
    if not pos:
        pos.add(POS_OTHER)
    gn = 0
    for f in fs:
        gn |= {"m": MASC, "f": FEM, "e": MASC | FEM, "s": SING, "p": PLUR, "i": SING | PLUR}.get(f, 0)
    persons = moods = 0
    for f in fs:
        p = f.rstrip("!").replace("ś", "s").replace("ŝ", "s")
        persons |= PERSON.get(p, 0)
        moods |= MOODS.get(f, 0)
    vb = 0
    if verb:
        if verb.startswith("V0e"):                     # group 0 + type e: être (type e alone also marks « sembler »)
            vb |= ETRE
        if verb.startswith("V0a"):
            vb |= AVOIR
        if len(verb) > 8 and verb[8] == "e":           # V + 9 columns; the 8th is « auxiliaire être » (« aller »)
            vb |= AUX_ETRE
        if lemma in MODALS:
            vb |= MODAL
    out = []
    for p in sorted(pos):
        b = p
        if p in NOMINAL:
            b |= gn
        if p == POS_FINITE:
            b |= persons | moods
        if p in VERBAL:
            b |= vb
        out.append(b)
    return out


# ---------------------------------------------------------------- lock, fetch
def load_lock(path=LOCK):
    with open(path, encoding="utf-8") as f:
        return json.load(f)["grammalecte"]


def raw_name(path):
    return os.path.basename(path)


def lex_entry(lock):
    return lock["files"]["lexicons/French.lex"]


def checked(path, want):
    got = SR.sha256(path)
    if got != want:
        raise SystemExit("%s: SHA-256 %s, expected %s" % (path, got, want))
    return path


def archive_member(tar_path, lock, rel):
    """The bytes of one pinned file read from the pinned tarball (« grammalecte-<commit>/<rel> »), by name: the archive
    is untrusted data, nothing of it is extracted to disk."""
    import tarfile
    checked(tar_path, lock["archive"]["sha256"])
    with tarfile.open(tar_path, "r:gz") as t:
        name = "grammalecte-%s/%s" % (lock["commit"], rel)
        m = t.getmember(name)
        if not m.isfile():
            raise SystemExit("%s: not a file in the archive" % name)
        return t.extractfile(m).read()


def cmd_fetch_archive(a):
    lock = load_lock(a.lock)
    os.makedirs(a.dest, exist_ok=True)
    dest = os.path.join(a.dest, "grammalecte-%s.tar.gz" % lock["commit"][:7])
    if os.path.exists(dest):
        checked(dest, lock["archive"]["sha256"])
        print("%s already there (SHA-256 ok)" % dest)
        return
    req = urllib.request.Request(lock["archive"]["url"], headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(req, timeout=300) as r, open(dest + ".part", "wb") as f:
        shutil.copyfileobj(r, f)
    if SR.sha256(dest + ".part") != lock["archive"]["sha256"]:
        os.remove(dest + ".part")
        raise SystemExit("archive: SHA-256 mismatch (expected %s)" % lock["archive"]["sha256"])
    os.replace(dest + ".part", dest)
    print("%s: SHA-256 ok" % dest)


def cmd_fetch(a):
    lock = load_lock(a.lock)
    os.makedirs(a.raw, exist_ok=True)
    if getattr(a, "from_archive", None):
        for rel, info in sorted(lock["files"].items()):
            dest = os.path.join(a.raw, raw_name(rel))
            if os.path.exists(dest):
                checked(dest, info["sha256"])
                continue
            data = archive_member(a.from_archive, lock, rel)
            if hashlib.sha256(data).hexdigest() != info["sha256"]:
                raise SystemExit("%s: SHA-256 mismatch in the archive" % rel)
            with open(dest + ".part", "wb") as f:
                f.write(data)
            os.replace(dest + ".part", dest)
            print("%s: SHA-256 ok (from the archive)" % dest)
        return
    src_dir = a.from_dir
    # French.lex through the spelling reference's own fetch (same pin, same folder)
    SR.cmd_fetch(argparse.Namespace(raw=a.raw, source=os.path.join(src_dir, "lexicons", "French.lex") if src_dir else None))
    for rel, info in sorted(lock["files"].items()):
        if rel == "lexicons/French.lex":
            continue
        dest = os.path.join(a.raw, raw_name(rel))
        if os.path.exists(dest):
            checked(dest, info["sha256"])
            print("%s already there (SHA-256 ok)" % dest)
            continue
        part = dest + ".part"
        if src_dir:
            shutil.copyfile(os.path.join(src_dir, *rel.split("/")), part)
        else:
            url = "https://raw.githubusercontent.com/Pofilo/grammalecte/%s/%s" % (lock["commit"], rel)
            req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=120) as r, open(part, "wb") as f:
                shutil.copyfileobj(r, f)
        if SR.sha256(part) != info["sha256"]:
            os.remove(part)
            raise SystemExit("%s: SHA-256 mismatch (expected %s)" % (rel, info["sha256"]))
        os.replace(part, dest)
        print("%s: SHA-256 ok" % dest)


def cmd_check_official(a):
    """O1: the GitHub mirror is the pinned source; when grammalecte.net serves the official 2.3.0 archive, its French.lex
    (if it has one) is compared. Never blocks the build: the result is one line in SOURCE.txt."""
    lock = load_lock(a.lock)
    today = datetime.date.today().isoformat()
    os.makedirs(a.raw, exist_ok=True)
    d = os.path.join(a.raw, "official")
    os.makedirs(d, exist_ok=True)
    z = os.path.join(d, os.path.basename(OFFICIAL))
    try:
        if not os.path.exists(z):
            req = urllib.request.Request(OFFICIAL, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=120) as r, open(z + ".part", "wb") as f:
                shutil.copyfileobj(r, f)
            os.replace(z + ".part", z)
        with zipfile.ZipFile(z) as zf:
            lex = [n for n in zf.namelist() if n.endswith("/French.lex") or n == "French.lex"]
            if not lex:
                line = ("official archive %s (SHA-256 %s): reachable, holds no French.lex (compiled dictionaries only), "
                        "not comparable (%s)" % (OFFICIAL, SR.sha256(z), today))
            else:
                h = hashlib.sha256(zf.read(lex[0])).hexdigest()
                same = h == lex_entry(lock)["sha256"]
                line = "official archive %s: French.lex %s (%s)" % (OFFICIAL, "identical" if same else "DIFFERENT " + h, today)
    except Exception as e:  # noqa: BLE001 — any failure is only reported
        line = "official archive %s: unreachable (%s; %s)" % (OFFICIAL, type(e).__name__, today)
    with open(os.path.join(a.raw, "official-check.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(line + "\n")
    print(line)


# ---------------------------------------------------------------- normalisation (prediction form -> French.lex form)
def lookup(form, known):
    """The French.lex form for a prediction-lexicon form, else None: as it is, lowercase, « oe » → « œ » (only when
    French.lex has the result: « coefficient » stays), then each of these with ' written ’ (« aujourd'hui »)."""
    cands = [form, form.lower()]
    for c in list(cands):
        if "oe" in c:
            cands.append(c.replace("oe", "œ"))
    for c in list(cands):
        if "'" in c:
            cands.append(c.replace("'", "’"))
    for c in cands:
        if c in known:
            return c
    return None


# ---------------------------------------------------------------- build
def read_rows(lex_path):
    """Distinct (form, lemma, tag) of French.lex, the tag without its dictionary part: two lines that differ only by it
    (the same form in two spelling dictionaries, « /M » and « /* ») are one analysis — every variant is accepted (O11)."""
    rows = set()
    n = 0
    for form, lemma, tag in SR.read_lex(lex_path):
        rows.add((form, lemma, tag.split("/", 1)[0]))
        n += 1
    return rows, n


def build(lex_path, db_path, out_path, lex_sha, source_lines=None):
    rows, lines = read_rows(lex_path)
    lemmas = sorted({r[1] for r in rows})
    lid = {l: i for i, l in enumerate(lemmas)}
    tags = sorted({r[2] for r in rows})
    tid = {t: i for i, t in enumerate(tags)}
    # per tag: the decoded analyses; only the modal bit depends on the lemma (our list), so the tag table stores the
    # lemma-independent bits and pred_index the full ones
    tag_bits = {t: decode(t) for t in tags}
    morph = sorted((f, lid[l], tid[t]) for f, l, t in rows)
    by_form = {}
    for f, l, t in rows:
        by_form.setdefault(f, []).append((l, t))

    con = sqlite3.connect(db_path) if db_path else None
    try:
        words = sorted({r[0] for r in con.execute("SELECT ortho FROM words")}) if con else []
    finally:
        if con:
            con.close()
    # the prediction lexicon's spellings are the pred_index keys: their list is recorded (the database file itself also
    # holds the n-grams, rebuilt independently)
    words_sha = hashlib.sha256("\n".join(words).encode("utf-8")).hexdigest()
    pred = []
    for w in words:
        lf = lookup(w, by_form)
        if lf is None:
            continue
        merged = {}
        for l, t in by_form[lf]:
            for b in decode(t, l):
                k = (lid[l], b & POS_MASK)
                merged[k] = merged.get(k, 0) | b
        blob = b"".join(struct.pack("<II", k[0], merged[k]) for k in sorted(merged))
        pred.append((w, blob))

    if os.path.exists(out_path):
        os.remove(out_path)
    tmp = out_path + ".tmp"
    if os.path.exists(tmp):
        os.remove(tmp)
    con = sqlite3.connect(tmp)
    try:
        con.execute("PRAGMA page_size=4096")
        con.execute("PRAGMA journal_mode=DELETE")
        con.execute("CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)")
        con.execute("CREATE TABLE lemma(id INTEGER PRIMARY KEY, lemma TEXT)")
        con.execute("CREATE TABLE tag(id INTEGER PRIMARY KEY, tag TEXT UNIQUE, bits INTEGER, bits2 INTEGER NOT NULL DEFAULT 0)")
        con.execute("CREATE TABLE morph(form TEXT, lemma_id INTEGER, tag_id INTEGER, PRIMARY KEY(form, lemma_id, tag_id)) WITHOUT ROWID")
        con.execute("CREATE TABLE pred_index(ortho TEXT PRIMARY KEY, a BLOB) WITHOUT ROWID")
        con.executemany("INSERT INTO lemma VALUES (?, ?)", enumerate(lemmas))
        multi = 0
        for t in tags:
            b = tag_bits[t]
            if len(b) > 2:
                raise SystemExit("tag %r decodes into %d analyses (the tag table holds two)" % (t, len(b)))
            multi += len(b) == 2
            con.execute("INSERT INTO tag VALUES (?, ?, ?, ?)", (tid[t], t, b[0], b[1] if len(b) > 1 else 0))
        con.executemany("INSERT INTO morph VALUES (?, ?, ?)", morph)
        con.execute("CREATE INDEX morph_lemma ON morph(lemma_id)")
        con.executemany("INSERT INTO pred_index VALUES (?, ?)", pred)
        meta = {
            "schema": str(SCHEMA),
            "source": SR.SOURCE,
            "commit": SR.COMMIT,
            "french_lex_sha256": lex_sha,
            "licence": "MPL-2.0 (data derived from French.lex, indexed by the prediction lexicon's spellings from Lexique "
                       "3.83, CC BY-SA 4.0; converter build_grammar.py, GPL-3.0-or-later)",
            "lex_lines": str(lines),
            "morph_rows": str(len(morph)),
            "lemmas": str(len(lemmas)),
            "tags": str(len(tags)),
            "tags_two_pos": str(multi),
            "pred_index_rows": str(len(pred)),
            "prediction_forms": str(len(words)),
            "prediction_words_sha256": words_sha,
            "bits": "MorphBits v1: 0-3 POS (1 N, 2 A, 3 Det, 4 subject pronoun, 5 other pronoun, 6 finite verb, 7 Q, "
                    "8 Y, 9 P, 10 prep, 11 conj, 12 adv, 15 other); 4 m, 5 f; 6 s, 7 p; 8-13 persons 1s 2s 3s 1p 2p 3p; "
                    "14 I, 15 S, 16 E, 17 K; 18 être, 19 avoir, 20 aux être, 21 modal",
        }
        con.executemany("INSERT INTO meta VALUES (?, ?)", sorted(meta.items()))
        con.commit()
        con.execute("VACUUM")
    finally:
        con.close()
    os.replace(tmp, out_path)
    counts = {"lex_lines": lines, "morph": len(morph), "lemma": len(lemmas), "tag": len(tags), "tag_two_pos": multi,
              "pred_index": len(pred), "prediction_forms": len(words), "prediction_words_sha256": words_sha}
    return SR.sha256(out_path), counts


def write_source(dir_, lock, counts, sha, official=None):
    os.makedirs(dir_, exist_ok=True)
    lex = lex_entry(lock)
    lines = [
        "grammar.sqlite — morphology table for grammatical prediction (Pépite, plan 11 / 10D-0)",
        "",
        "Licence: Mozilla Public License 2.0 (MPL-2.0), the text in MPL-2.0.txt next to this file.",
        "grammar.sqlite is Covered Software: a Modification of French.lex (Grammalecte « Lexique simplifié » v7.7),",
        "distributed in Executable Form (MPL-2.0 § 3.2). Pépite (GPL-3.0-or-later) is the Larger Work (MPL-2.0 § 3.3).",
        "Source Code Form: French.lex at %s/blob/%s/lexicons/French.lex" % (lock["mirror"], lock["commit"]),
        "plus the converter src/Data.Build/build_grammar.py of Pépite's source.",
        "Content: MPL-2.0 data derived from French.lex, indexed by the prediction lexicon's spellings (the pred_index keys,",
        "from Lexique 3.83, CC BY-SA 4.0, http://www.lexique.org: « coeur », « aujourd'hui »).",
        "",
        "Source: Grammalecte %s, GitHub mirror %s, commit %s" % (lock["version"], lock["mirror"], lock["commit"]),
        "Archive: %s" % lock["archive"]["url"],
        "Archive SHA-256: %s" % lock["archive"]["sha256"],
        "French.lex SHA-256: %s" % lex["sha256"],
        "French.lex notice: « This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0. If a copy",
        "of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/. »",
        "O1 check: %s" % (official or "not run"),
        "",
        "Built with Python %s, SQLite %s" % (platform.python_version(), sqlite3.sqlite_version),
        "Counts: %d French.lex lines, %d analyses (morph), %d lemmas, %d tags (%d with two parts of speech), %d prediction"
        " forms indexed of %d" % (counts["lex_lines"], counts["morph"], counts["lemma"], counts["tag"], counts["tag_two_pos"],
                                  counts["pred_index"], counts["prediction_forms"]),
        "Prediction lexicon spellings (sorted list of prediction.sqlite words.ortho, joined by LF) SHA-256: %s"
        % counts["prediction_words_sha256"],
        "grammar.sqlite SHA-256: %s" % sha,
        "",
    ]
    with open(os.path.join(dir_, "SOURCE.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    shutil.copyfile(a_mpl(), os.path.join(dir_, "MPL-2.0.txt"))


def a_mpl():
    p = os.path.normpath(MPL_TEXT)
    if not os.path.exists(p):
        raise SystemExit("MPL-2.0 text missing: %s" % p)
    return p


def cmd_build(a):
    lock = load_lock(a.lock)
    sha_want = lex_entry(lock)["sha256"]
    if sha_want != SR.SHA256 or lock["commit"] != SR.COMMIT:
        raise SystemExit("grammar.lock.json and build_spelling_ref.py pin different French.lex files")
    lex = checked(os.path.join(a.raw, SR.NAME), sha_want)
    sha, counts = build(lex, a.db, a.out, sha_want)
    official = None
    oc = os.path.join(a.raw, "official-check.txt")
    if os.path.exists(oc):
        with open(oc, encoding="utf-8") as f:
            official = f.read().strip()
    write_source(os.path.join(os.path.dirname(os.path.abspath(a.out)), "grammar"), lock, counts, sha, official)
    print("morph %(morph)d, lemma %(lemma)d, tag %(tag)d (%(tag_two_pos)d with two POS), pred_index %(pred_index)d of "
          "%(prediction_forms)d prediction forms" % counts)
    print("%s SHA-256 %s (%.1f MB)" % (a.out, sha, os.path.getsize(a.out) / 1048576))


# ---------------------------------------------------------------- mapping report (vs Lexique 3.83)
LEX_POS = {"NOM": {POS_N}, "ADJ": {POS_A}, "ADJ:num": {POS_DET, POS_A}, "ADJ:dem": {POS_DET}, "ADJ:pos": {POS_DET},
           "ADJ:ind": {POS_DET, POS_A, POS_PRON}, "ADJ:int": {POS_DET, POS_PRON}, "ART:def": {POS_DET},
           "ART:ind": {POS_DET}, "PRE": {POS_PREP}, "CON": {POS_CONJ}, "ADV": {POS_ADV},
           "PRO:per": {POS_SUBJ, POS_PRON}, "PRO:dem": {POS_PRON}, "PRO:ind": {POS_PRON, POS_DET},
           "PRO:int": {POS_PRON}, "PRO:pos": {POS_PRON}, "PRO:rel": {POS_PRON}, "ONO": {POS_OTHER},
           "AUX": VERBAL, "VER": VERBAL}
LEX_MOOD = {"ind", "sub", "imp", "cnd"}


def analyses(con):
    """form -> list of bits of every analysis (morph joined with the per-lemma decoding)."""
    lemmas = [r[0] for r in con.execute("SELECT lemma FROM lemma ORDER BY id")]
    tags = [r[0] for r in con.execute("SELECT tag FROM tag ORDER BY id")]
    out = {}
    for form, l, t in con.execute("SELECT form, lemma_id, tag_id FROM morph"):
        out.setdefault(form, []).extend(decode(tags[t], lemmas[l]))
    return out


def mapping_report(lexique, grammar):
    con = sqlite3.connect("file:%s?mode=ro" % grammar.replace("\\", "/"), uri=True)
    try:
        an = analyses(con)
    finally:
        con.close()
    stats = {k: [0, 0, 0] for k in ("pos", "gender N/A", "number N/A", "person finite")}   # agree, compared, vacuous
    forms_both, forms_lex = set(), set()
    misses = {k: [] for k in stats}

    def bump(k, ok, ex, vacuous=False):
        """vacuous: an agreement that our bits give whatever Lexique says (epicene, invariable, every person)."""
        stats[k][1] += 1
        if ok:
            stats[k][0] += 1
            if vacuous:
                stats[k][2] += 1
        elif len(misses[k]) < 25:
            misses[k].append(ex)

    with open(lexique, encoding="utf-8") as f:
        header = f.readline().rstrip("\n").split("\t")
        col = {n: i for i, n in enumerate(header)}
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) <= col["infover"]:
                continue
            ortho, cgram = p[col["ortho"]], p[col["cgram"]]
            if not ortho or " " in ortho:
                continue
            forms_lex.add(ortho)
            lf = lookup(ortho, an)
            if lf is None:
                continue
            forms_both.add(ortho)
            bits = an[lf]
            ours = {b & POS_MASK for b in bits}
            want = LEX_POS.get(cgram)
            if want is not None:
                bump("pos", bool(ours & want), "%s %s ours=%s" % (ortho, cgram, sorted(ours)))
            genre, nombre = p[col["genre"]], p[col["nombre"]]
            if cgram in ("NOM", "ADJ"):
                pos = POS_N if cgram == "NOM" else POS_A
                same = [b for b in bits if b & POS_MASK == pos] or [b for b in bits if b & POS_MASK in (POS_N, POS_A, POS_Q)]
                if same:
                    if genre in ("m", "f"):
                        g = MASC if genre == "m" else FEM
                        bump("gender N/A", any(b & g for b in same), "%s %s %s" % (ortho, cgram, genre),
                             all(b & (MASC | FEM) == MASC | FEM for b in same if b & g))
                    if nombre in ("s", "p"):
                        n = SING if nombre == "s" else PLUR
                        bump("number N/A", any(b & n for b in same), "%s %s %s" % (ortho, cgram, nombre),
                             all(b & (SING | PLUR) == SING | PLUR for b in same if b & n))
            if cgram in ("VER", "AUX"):
                fin = 0
                for b in bits:
                    if b & POS_MASK == POS_FINITE:
                        fin |= b
                for inf in p[col["infover"]].split(";"):
                    parts = inf.split(":")
                    if len(parts) == 3 and parts[0] in LEX_MOOD and parts[2] in PERSON:
                        bump("person finite", bool(fin & PERSON[parts[2]]), "%s %s" % (ortho, inf),
                             fin & 0x3F00 == 0x3F00)
    return stats, len(forms_both), len(forms_lex), misses


GATES = ("number N/A", "person finite")


def cmd_mapping_report(a):
    stats, both, nlex, misses = mapping_report(a.lexique, a.grammar)
    lines = ["Mapping report — MorphBits (from French.lex tags) against Lexique 3.83 (cgram, genre, nombre, infover)",
             "Lexique SHA-256 %s; grammar.sqlite SHA-256 %s; %s" % (SR.sha256(a.lexique), SR.sha256(a.grammar),
                                                                   datetime.date.today().isoformat()),
             "%d of %d Lexique forms are in French.lex (after the normalisation of build_grammar.lookup)" % (both, nlex),
             "", "field            agree / compared   rate   vacuous  gate",
             "(vacuous = share of the compared cases that agree only because our bits allow every value: epicene,",
             " invariable, every person; not computed for POS)", ""]
    fail = False
    for k, (ok, n, vac) in stats.items():
        rate = ok / n if n else 0.0
        gate = ">= 0.95" if k in GATES else "-"
        if k in GATES and rate < 0.95:
            fail = True
        lines.append("%-16s %8d / %-8d %6.4f  %7s  %s" % (k, ok, n, rate, "-" if k == "pos" else "%.4f" % (vac / n if n else 0), gate))
    lines.append("")
    lines.append("gate: %s" % ("FAILED" if fail else "passed"))
    for k, ex in misses.items():
        if ex:
            lines.append("")
            lines.append("first disagreements, %s:" % k)
            lines.extend("  " + e for e in ex)
    text = "\n".join(lines) + "\n"
    if a.report:
        os.makedirs(os.path.dirname(os.path.abspath(a.report)), exist_ok=True)
        with open(a.report, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
    sys.stdout.buffer.write(text.encode("utf-8"))
    return 1 if fail else 0


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--lock", default=LOCK)
    sub = p.add_subparsers(dest="cmd", required=True)
    f = sub.add_parser("fetch")
    f.add_argument("--raw", default=SR.default_raw())
    g = f.add_mutually_exclusive_group()
    g.add_argument("--from-dir", dest="from_dir")
    g.add_argument("--from-archive", dest="from_archive")
    fa = sub.add_parser("fetch-archive")
    fa.add_argument("--dest", required=True)
    c = sub.add_parser("check-official")
    c.add_argument("--raw", default=SR.default_raw())
    b = sub.add_parser("build")
    b.add_argument("--raw", default=SR.default_raw())
    b.add_argument("--db", default=os.path.join(HERE, "out", "prediction.sqlite"))
    b.add_argument("--out", default=os.path.join(HERE, "out", "grammar.sqlite"))
    m = sub.add_parser("mapping-report")
    m.add_argument("--lexique", required=True)
    m.add_argument("--grammar", default=os.path.join(HERE, "out", "grammar.sqlite"))
    m.add_argument("--report")
    a = p.parse_args(argv)
    return {"fetch": cmd_fetch, "fetch-archive": cmd_fetch_archive, "check-official": cmd_check_official, "build": cmd_build,
            "mapping-report": cmd_mapping_report}[a.cmd](a) or 0


if __name__ == "__main__":
    sys.exit(main())
