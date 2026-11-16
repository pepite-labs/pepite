#!/usr/bin/env python3
"""Build the offline pictogram pack (pictos.sqlite) from the ARASAAC catalogue.

ARASAAC pictograms: author Sergio Palao, owner Gobierno de Aragón, licence CC BY-NC-SA 4.0
(https://arasaac.org). The pack is a *derivative* (resized, palette-reduced) and is shipped as a
separate file with its own notice (ARASAAC-LICENCE.txt) — the code of this repository is not
affected by its licence. The words come from prediction.sqlite / Lexique 3.83.

Sub-commands (run with ``python -I``; raw downloads stay under --raw, in <work>):

    fetch-index  [--seed <json>]                          raw/index/arasaac_all_fr_<YYYYMMDD>.json
    map          --lexique <tsv> --db <prediction.sqlite> [--overrides picto_overrides.tsv]
                                                          work/mapping.tsv + work/needed_ids.txt
    report       --db <prediction.sqlite> [--subs <txt>] [--wiki <txt>]   work/coverage.json (gate)
    fetch-images [--limit N] [--delay 0.3]                raw/png300/{id}.png (polite, resumable)
    pack         --out <pictos.sqlite> [--px 128] [--colors 128]          + ARASAAC-LICENCE.txt
    sheet        --forms top300|homographs --db <prediction.sqlite>       work/sheet_*.png

Pillow is needed only by ``pack`` and ``sheet`` (build-time dependency).
"""
import argparse
import datetime as _dt
import hashlib
import json
import os
import re
import sqlite3
import struct
import sys
import time
import unicodedata
import urllib.error
import urllib.request
import zlib
from collections import Counter, defaultdict, namedtuple

HERE = os.path.dirname(os.path.abspath(__file__))
INDEX_URL = "https://api.arasaac.org/v1/pictograms/all/fr"
IMAGE_URL = "https://static.arasaac.org/pictograms/{id}/{id}_300.png"
IMAGE_FALLBACK_URL = "https://api.arasaac.org/v1/pictograms/{id}?resolution=500&download=false"   # on a CDN 404
USER_AGENT = "PredictKeyboard-DataBuild/0.2 (offline pack build)"
LICENCE_URL = "https://creativecommons.org/licenses/by-nc-sa/4.0/"
CHANGES = "redimensionnés de 300 à {px} px, palette de {colors} couleurs"
PNG_SIG = b"\x89PNG\r\n\x1a\n"

# Coverage floors accepted by the owner in D4b (design/product-decisions.md, 2026-11-08: subtitles 53.4 %,
# wiki 38.9 %, lexicon 52.7 % after the auxiliary/article/abstract-symbol exclusions; they supersede plan 02's
# 75/65/70 %). A rule change that drops coverage below them fails the report.
GATES = {"subs": 53.0, "wiki": 38.0, "lexicon_weighted": 52.0}
# Plan 02 prototype (exact match, no type rules, no exclusions): printed for information only.
PROTOTYPE = {"subs": 83.1, "wiki": 74.0, "lexicon_weighted": 79.6}

# Homograph review list (plan 02, Tests): 40 forms whose dominant reading must win.
HOMOGRAPHS = ("été porte vers livre fils as est mine bois avocat sens tour son sol voile mousse glace louer "
              "ferme souris car mur pêche lit vol page poste moule cours temps plant marche palais mode "
              "verre ver vert mer mère maire").split()

KEY_RE = re.compile(r"^[a-zà-ÿœ]+$")
TOKEN_RE = re.compile(r"[a-zà-ÿœ]+")

# Lexique cgram → compatible ARASAAC keyword types (1 proper name, 2 common noun, 3 verb, 4 descriptive,
# 5 social, 6 misc). A keyword without a type is compatible with every cgram.
# Deviations from plan 02 rule 3, measured on the 2026-11-08 index: ARASAAC files its personal-pronoun
# pictograms (je, tu, il, nous, vous, moi…) under type 1 "proper name", so type 1 is accepted for PRO
# readings only (never for any other cgram); interrogatives/comparatives such as « où », « comme » are
# type 4, so PRE/CON/PRO also accept 4. Without this the most frequent words of speech had no picture.
_COMPAT = {"NOM": {2}, "VER": {3}, "AUX": {3}, "ADJ": {4, 2}, "ADV": {4, 6}, "PRE": {6, 4}, "CON": {6, 4},
           "ART": {6}, "PRO": {6, 4, 1}, "ONO": {5, 6}, "LIA": {6}}

Cand = namedtuple("Cand", "id type kw_rank from_plural aac schematic")
Reading = namedtuple("Reading", "lemma cgram freq")
Choice = namedtuple("Choice", "picto_id rule lemma cgram")


def norm(s):
    """NFC, lower case, typographic apostrophe → ASCII."""
    return unicodedata.normalize("NFC", s or "").lower().replace("\u2019", "'").strip()


def compatible(cgram, kw_type):
    if kw_type is None:
        return True
    return kw_type in _COMPAT.get((cgram or "").split(":")[0], set())


# ---------------------------------------------------------------- index

def build_keyword_index(pictos):
    """key → [Cand]; flagged (sex/violence) pictograms skipped (D4b); only single-token keys kept."""
    idx = defaultdict(list)
    for p in pictos:
        if p.get("sex") or p.get("violence"):
            continue
        for rank, kw in enumerate(p.get("keywords") or []):
            for field, from_plural in (("keyword", False), ("plural", True)):
                key = norm(kw.get(field))
                if key and KEY_RE.match(key):
                    idx[key].append(Cand(int(p["_id"]), kw.get("type"), rank, from_plural,
                                         bool(p.get("aac")), bool(p.get("schematic"))))
    return idx


def _tiebreak(c):
    # keyword before plural (added after review: « fils » matched the plural of « fil », a spool of thread,
    # before the keyword « fils » = son), then plan 02's order: kw_rank 0, aac, not schematic, lowest id
    return (c.from_plural, c.kw_rank != 0, not c.aac, c.schematic, c.id)


# ---------------------------------------------------------------- readings

def load_readings(lexique_path, forms=None):
    """form → [Reading] by decreasing freq; one Reading per (lemma, cgram), freq = max over rows of
    max(freqfilms2, freqlivres)."""
    acc = defaultdict(dict)
    with open(lexique_path, encoding="utf-8") as f:
        header = f.readline().rstrip("\n").split("\t")
        col = {n: i for i, n in enumerate(header)}
        for n in ("ortho", "lemme", "cgram", "freqfilms2", "freqlivres"):
            if n not in col:
                raise SystemExit(f"column {n!r} missing in {lexique_path}")
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) <= col["freqlivres"]:
                continue
            form = norm(p[col["ortho"]])
            if forms is not None and form not in forms:
                continue
            try:
                freq = max(float(p[col["freqfilms2"]] or 0), float(p[col["freqlivres"]] or 0))
            except ValueError:
                freq = 0.0
            k = (norm(p[col["lemme"]]), p[col["cgram"]].strip())
            if freq > acc[form].get(k, -1.0):
                acc[form][k] = freq
    return {f: sorted((Reading(l, c, q) for (l, c), q in d.items()), key=lambda r: (-r.freq, r.cgram, r.lemma))
            for f, d in acc.items()}


def dominant(readings, ratio=0.2):
    if not readings:
        return []
    top = readings[0].freq
    return [r for r in readings if r.freq >= ratio * top]


# ---------------------------------------------------------------- mapping

# Owner decisions (2026-11-08, iteration 2): no picture for the auxiliaries and for articles — ARASAAC's
# pictures for them are abstract (two hands for « être », a triangle for « le ») and confuse a child.
AUX_NONE = {"être", "avoir"}
ARTICLE_FORMS = {"le", "la", "les", "l", "un", "une", "des", "du", "au", "aux"}


def excluded_rule(form, readings):
    """'aux-none' when the dominant lemma is être/avoir used as a verb or auxiliary — lemma frequencies summed
    over cgrams, because Lexique splits them (« suis »: suivre 1819 vs être AUX 1272 + VER 669);
    'art-none' when the dominant reading is an article (cgram ART*) or the form is one of ARTICLE_FORMS."""
    top = readings[0] if readings else None
    by_lemma = defaultdict(float)
    for r in readings:
        by_lemma[r.lemma] += r.freq
    if by_lemma:
        lemma = max(by_lemma, key=lambda k: (by_lemma[k], k))
        if lemma in AUX_NONE and any(r.lemma == lemma and r.cgram in ("VER", "AUX") for r in readings):
            return "aux-none"
    if (top and top.cgram.startswith("ART")) or norm(form) in ARTICLE_FORMS:
        return "art-none"
    if len(norm(form)) == 1:
        return "letter-none"   # d', l', j', s', n', m', t', c', à, y: never the letter of the alphabet pictured
    return None


def choose(form, readings, idx):
    """Rules 3–5 of plan 02: dominant readings in order; form (keyword or plural) then lemma (keyword only),
    type-compatible; else single reading → any-type form match; else None. Auxiliaries and articles never
    get a picture (Choice with picto_id None and rule 'aux-none' / 'art-none')."""
    form = norm(form)
    ex = excluded_rule(form, readings)
    if ex:
        r = readings[0] if readings else Reading("", "", 0.0)
        return Choice(None, ex, r.lemma, r.cgram)
    for r in dominant(readings):
        cands = [c for c in idx.get(form, ()) if compatible(r.cgram, c.type)]
        rule = "form"
        if not cands and r.lemma and r.lemma != form:
            cands = [c for c in idx.get(r.lemma, ()) if not c.from_plural and compatible(r.cgram, c.type)]
            rule = "lemma"
        if cands:
            return Choice(min(cands, key=_tiebreak).id, rule, r.lemma, r.cgram)
    if len(readings) <= 1:
        cands = [c for c in idx.get(form, ()) if c.type != 1]
        if cands:
            r = readings[0] if readings else Reading("", "", 0.0)
            return Choice(min(cands, key=_tiebreak).id, "form-anytype", r.lemma, r.cgram)
    return None


def load_overrides(path):
    """form → id (int) or None ('-' = no picture). Lines: form<TAB>id|-[<TAB>comment]; '#' comments."""
    out = {}
    if not path or not os.path.exists(path):
        return out
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            p = line.split("\t")
            if len(p) < 2:
                raise SystemExit(f"bad override line: {line!r}")
            v = p[1].strip()
            out[norm(p[0])] = None if v == "-" else int(v)
    return out


def build_mapping(forms, readings, idx, overrides=None, missing=(), excluded=None):
    """form → Choice for every form that gets a picture. Forms refused by a rule ('aux-none', 'art-none')
    or by an override '-' go to ``excluded`` (form → rule) when a dict is given."""
    overrides = overrides or {}
    missing = set(missing)
    excluded = excluded if excluded is not None else {}
    out = {}
    for form in forms:
        if form in overrides:
            pid = overrides[form]
            if pid is None:
                excluded[form] = "override-none"
            elif pid not in missing:
                r = (readings.get(form) or [Reading("", "", 0.0)])[0]
                out[form] = Choice(pid, "override", r.lemma, r.cgram)
            continue
        c = choose(form, readings.get(form, []), idx)
        if c and c.picto_id is None:
            excluded[form] = c.rule
        elif c and c.picto_id not in missing:
            out[form] = c
    return out


# ---------------------------------------------------------------- helpers: paths, db, index

def paths(raw):
    return {"raw": raw, "index_dir": os.path.join(raw, "index"), "png": os.path.join(raw, "png300"),
            "manifest": os.path.join(raw, "manifest.tsv"), "missing": os.path.join(raw, "missing.tsv"),
            "work": os.path.join(raw, "work"), "mapping": os.path.join(raw, "work", "mapping.tsv"),
            "needed": os.path.join(raw, "work", "needed_ids.txt")}


def latest_index(raw):
    p = paths(raw)
    with open(os.path.join(p["index_dir"], "index_latest.txt"), encoding="utf-8") as f:
        name = f.read().strip()
    return os.path.join(p["index_dir"], name)


def load_index(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def db_forms(db):
    """[(form, freq)] lower-case, by decreasing freq (prediction.sqlite.words)."""
    con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        best = {}
        for ortho, freq in con.execute("SELECT ortho, freq FROM words"):
            f = norm(ortho)
            if f and freq is not None and freq >= best.get(f, -1.0):
                best[f] = float(freq)
            elif f and f not in best:
                best[f] = 0.0
    finally:
        con.close()
    return sorted(best.items(), key=lambda x: (-x[1], x[0]))


def read_missing(raw):
    p = paths(raw)["missing"]
    if not os.path.exists(p):
        return set()
    with open(p, encoding="utf-8") as f:
        return {int(l.split("\t")[0]) for l in f if l.strip() and l[0].isdigit()}


def read_excluded(raw):
    path = os.path.join(paths(raw)["work"], "excluded.tsv")
    if not os.path.exists(path):
        return {}
    with open(path, encoding="utf-8") as f:
        f.readline()
        return dict(line.rstrip("\n").split("\t") for line in f if line.strip())


def read_mapping(raw):
    out = {}
    with open(paths(raw)["mapping"], encoding="utf-8") as f:
        f.readline()
        for line in f:
            form, pid, rule, lemma, cgram = line.rstrip("\n").split("\t")
            out[form] = Choice(int(pid), rule, lemma, cgram)
    return out


# ---------------------------------------------------------------- commands

def cmd_fetch_index(a):
    p = paths(a.raw)
    os.makedirs(p["index_dir"], exist_ok=True)
    if a.seed:
        with open(a.seed, "rb") as f:
            data = f.read()
        stamp = _dt.datetime.fromtimestamp(os.path.getmtime(a.seed)).strftime("%Y%m%d")
    else:
        req = urllib.request.Request(INDEX_URL, headers={"User-Agent": USER_AGENT})
        with urllib.request.urlopen(req, timeout=120) as r:
            data = r.read()
        stamp = _dt.date.today().strftime("%Y%m%d")
    n = len(json.loads(data.decode("utf-8")))
    name = f"arasaac_all_fr_{stamp}.json"
    with open(os.path.join(p["index_dir"], name), "wb") as f:
        f.write(data)
    with open(os.path.join(p["index_dir"], "index_latest.txt"), "w", encoding="utf-8") as f:
        f.write(name + "\n")
    print(f"index: {n} pictograms -> {name} ({len(data)} bytes)")


def cmd_map(a):
    p = paths(a.raw)
    os.makedirs(p["work"], exist_ok=True)
    pictos = load_index(a.index or latest_index(a.raw))
    idx = build_keyword_index(pictos)
    ranked = db_forms(a.db)
    forms = [f for f, _ in ranked]
    readings = load_readings(a.lexique, set(forms))
    overrides = load_overrides(a.overrides)
    known = {int(x["_id"]) for x in pictos}
    for f, pid in overrides.items():
        if pid is not None and pid not in known:
            raise SystemExit(f"override {f!r} -> {pid}: id not in the index")
    excluded = {}
    mapping = build_mapping(forms, readings, idx, overrides, read_missing(a.raw), excluded)
    with open(os.path.join(p["work"], "excluded.tsv"), "w", encoding="utf-8", newline="\n") as f:
        f.write("form\trule\n")
        f.writelines(f"{form}\t{rule}\n" for form, rule in sorted(excluded.items()))
    with open(p["mapping"], "w", encoding="utf-8", newline="\n") as f:
        f.write("form\tpicto_id\trule\treading_lemma\treading_cgram\n")
        for form in sorted(mapping):
            c = mapping[form]
            f.write(f"{form}\t{c.picto_id}\t{c.rule}\t{c.lemma}\t{c.cgram}\n")
    # most frequent forms first, so an interrupted fetch (or a partial pack) covers the common words
    ids, seen = [], set()
    for f in forms:
        c = mapping.get(f)
        if c and c.picto_id not in seen:
            seen.add(c.picto_id)
            ids.append(c.picto_id)
    with open(p["needed"], "w", encoding="utf-8", newline="\n") as f:
        f.writelines(f"{i}\n" for i in ids)
    rules = Counter(c.rule for c in mapping.values())
    print(f"map: {len(mapping)}/{len(forms)} forms, {len(ids)} pictograms, rules {dict(rules)}, "
          f"{len(overrides)} overrides, excluded {dict(Counter(excluded.values()))}")


def token_coverage(path, covered, limit=None, skip=frozenset()):
    """% of the corpus tokens that have a picture; tokens in ``skip`` are left out of the count."""
    tot = hit = 0
    with open(path, encoding="utf-8") as f:
        for i, line in enumerate(f):
            if limit and i >= limit:
                break
            for t in TOKEN_RE.findall(norm(line)):
                if t in skip:
                    continue
                tot += 1
                hit += t in covered
    return 100.0 * hit / tot if tot else 0.0, tot


def cmd_report(a):
    p = paths(a.raw)
    mapping = read_mapping(a.raw)
    forms = db_forms(a.db)
    cgram = {}
    con = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    for ortho, cg in con.execute("SELECT ortho, cgram FROM words"):
        cgram.setdefault(norm(ortho), (cg or "?").split(":")[0])
    con.close()
    covered = set(mapping)
    # forms refused on purpose by a rule (auxiliaries, articles: owner decision) are not a coverage gap:
    # the gate is measured on the other ("eligible") forms; raw numbers over all tokens are reported too
    ruled_out = frozenset(f for f, r in read_excluded(a.raw).items() if r in ("aux-none", "art-none", "letter-none"))
    tot_w = sum(q for _, q in forms) or 1.0
    elig_w = sum(q for f, q in forms if f not in ruled_out) or 1.0
    rep = {"forms": len(forms), "forms_covered": len(covered),
           "distinct_pictograms": len({c.picto_id for c in mapping.values()}),
           "by_rule": dict(Counter(c.rule for c in mapping.values())),
           "excluded_by_rule": dict(Counter(read_excluded(a.raw).values())),
           "lexicon_weighted": round(100.0 * sum(q for f, q in forms if f in covered) / elig_w, 2),
           "lexicon_weighted_raw": round(100.0 * sum(q for f, q in forms if f in covered) / tot_w, 2)}
    for n in (1000, 5000, 20000):
        top = forms[:n]
        rep[f"top{n // 1000}k_forms"] = round(100.0 * sum(f in covered for f, _ in top) / len(top), 2)
        w = sum(q for _, q in top) or 1.0
        rep[f"top{n // 1000}k_weighted"] = round(100.0 * sum(q for f, q in top if f in covered) / w, 2)
    by = defaultdict(lambda: [0, 0])
    for f, _ in forms:
        k = cgram.get(f, "?")
        by[k][1] += 1
        by[k][0] += f in covered
    rep["by_cgram"] = {k: f"{v[0]}/{v[1]}" for k, v in sorted(by.items(), key=lambda x: -x[1][1])}
    for key, path in (("subs", a.subs), ("wiki", a.wiki)):
        if path and os.path.exists(path):
            pct, n = token_coverage(path, covered, skip=ruled_out)
            raw, n_all = token_coverage(path, covered)
            rep[key] = round(pct, 2)
            rep[key + "_tokens"] = n
            rep[key + "_raw"] = round(raw, 2)
            rep[key + "_tokens_raw"] = n_all
    gate = {k: (rep.get(k, 0.0) >= v) for k, v in GATES.items()}
    drop = {k: round(PROTOTYPE[k] - rep.get(k, 0.0), 2) for k in PROTOTYPE}
    rep["gate"] = gate
    rep["drop_vs_prototype"] = drop
    rep["index"] = os.path.basename(latest_index(a.raw))
    with open(os.path.join(p["work"], "coverage.json"), "w", encoding="utf-8") as f:
        json.dump(rep, f, ensure_ascii=False, indent=1)
    print(json.dumps(rep, ensure_ascii=False, indent=1))
    print(f"(info) points below the plan-02 prototype, which had no exclusions: {drop}")
    if not all(gate.values()):
        print(f"GATE FAILED: {gate}")
        return 1
    print("gate: OK")
    return 0


# ---------------------------------------------------------------- fetch-images

def png_ok(path):
    """PNG signature + a parsable IHDR (13 bytes, width/height > 0, CRC ok) + IEND at the end."""
    try:
        with open(path, "rb") as f:
            head = f.read(33)
            f.seek(-12, os.SEEK_END)
            tail = f.read(12)
    except OSError:
        return False
    if len(head) < 33 or head[:8] != PNG_SIG:
        return False
    length, ctype = struct.unpack(">I4s", head[8:16])
    if length != 13 or ctype != b"IHDR":
        return False
    w, h = struct.unpack(">II", head[16:24])
    crc = struct.unpack(">I", head[29:33])[0]
    if w == 0 or h == 0 or zlib.crc32(head[12:29]) & 0xFFFFFFFF != crc:
        return False
    return tail[4:8] == b"IEND"


def fetch_images(ids, png_dir, manifest, missing_path, opener=None, delay=0.3, sleep=time.sleep,
                 log=print, max_fail=20):
    """Download {id}_300.png for each id not already valid on disk. Sequential, ``delay`` s between
    requests, Retry-After honoured on 429/503, exponential backoff ≤ 60 s, abort after ``max_fail``
    consecutive failures; a 404 on the CDN is retried once on the API (``IMAGE_FALLBACK_URL``, 500 px — the
    pack resizes anyway), a 404 there too → missing.tsv. Returns (fetched, skipped, missing)."""
    opener = opener or urllib.request.build_opener()
    os.makedirs(png_dir, exist_ok=True)
    fetched = skipped = 0
    missing = []
    fails = 0
    first = True
    for pid in ids:
        dst = os.path.join(png_dir, f"{pid}.png")
        if png_ok(dst):
            skipped += 1
            continue
        attempt = 0
        source = IMAGE_URL
        while True:
            if not first:
                sleep(delay)
            first = False
            url = source.format(id=pid)
            try:
                req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
                with opener.open(req, timeout=60) as r:
                    data = r.read()
                if not data.startswith(PNG_SIG):
                    raise ValueError("not a PNG")
                part = dst + ".part"
                with open(part, "wb") as f:
                    f.write(data)
                if not png_ok(part):
                    os.remove(part)
                    raise ValueError("truncated PNG")
                os.replace(part, dst)
                with open(manifest, "a", encoding="utf-8", newline="\n") as f:
                    f.write(f"{pid}\t{url}\t{len(data)}\t{hashlib.sha256(data).hexdigest()}\t"
                            f"{_dt.datetime.now().isoformat(timespec='seconds')}\n")
                fetched += 1
                fails = 0
                if fetched % 250 == 0:
                    log(f"fetched {fetched} (skipped {skipped}, missing {len(missing)}) last id {pid}")
                break
            except urllib.error.HTTPError as e:
                if e.code == 404 and source == IMAGE_URL:
                    source = IMAGE_FALLBACK_URL
                    continue
                if e.code == 404:
                    missing.append(pid)
                    with open(missing_path, "a", encoding="utf-8", newline="\n") as f:
                        f.write(f"{pid}\t404\t{url}\n")
                    break
                wait = None
                if e.code in (429, 503):
                    ra = e.headers.get("Retry-After") if e.headers else None
                    if ra and ra.strip().isdigit():
                        wait = int(ra.strip())
                err = f"HTTP {e.code}"
            except (urllib.error.URLError, OSError, ValueError) as e:
                wait = None
                err = repr(e)
            fails += 1
            attempt += 1
            if fails >= max_fail:
                raise SystemExit(f"aborting: {fails} consecutive failures (last id {pid}: {err})")
            backoff = wait if wait is not None else min(60, 2 ** attempt)
            log(f"id {pid}: {err}, retry in {backoff} s")
            sleep(backoff)
    return fetched, skipped, missing


def cmd_fetch_images(a):
    p = paths(a.raw)
    with open(p["needed"], encoding="utf-8") as f:
        ids = [int(x) for x in f if x.strip()]
    already = read_missing(a.raw)
    ids = [i for i in ids if i not in already]
    if a.limit:
        ids = ids[:a.limit]
    t = time.time()
    got, skip, miss = fetch_images(ids, p["png"], p["manifest"], p["missing"], delay=a.delay)
    print(f"fetch-images: {got} fetched, {skip} already there, {len(miss)} missing (404), "
          f"{time.time() - t:.0f} s")


# ---------------------------------------------------------------- pack / sheet

def attribution_fr():
    """Verbatim (fr) from arasaac.org/terms-of-use, see arasaac_attribution.txt."""
    with open(os.path.join(HERE, "arasaac_attribution.txt"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("fr: "):
                return line[4:].strip()
    raise SystemExit("arasaac_attribution.txt has no 'fr: ' line")


def shrink(img, px, colors):
    """RGBA → LANCZOS fit into px×px (centred) → ``colors``-colour palette with alpha → PNG bytes."""
    import io
    from PIL import Image
    im = img.convert("RGBA")
    if im.getbbox() is None:
        im = Image.new("RGBA", (px, px), (0, 0, 0, 0))
    s = px / max(im.size)
    im = im.resize((max(1, round(im.width * s)), max(1, round(im.height * s))), Image.LANCZOS)
    if im.size != (px, px):
        canvas = Image.new("RGBA", (px, px), (0, 0, 0, 0))
        canvas.paste(im, ((px - im.width) // 2, (px - im.height) // 2))
        im = canvas
    out = im
    if colors > 0:
        out = im.quantize(colors=colors, method=Image.Quantize.FASTOCTREE)
        # the octree merges "transparent" with near-transparent edge colours (alpha 0 → 1–5), which shows as
        # a faint grey square on the white slot: snap nearly-transparent palette entries to fully transparent
        pal = out.getpalette("RGBA")
        for k in range(3, len(pal), 4):
            if pal[k] < 16:
                pal[k - 3:k + 1] = [0, 0, 0, 0]
        out.putpalette(pal, "RGBA")
    buf = io.BytesIO()
    out.save(buf, "PNG", optimize=True)
    return buf.getvalue(), im, out


def psnr(a, b):
    """PSNR (dB) between two RGBA images composited on white (what the bar shows)."""
    from PIL import Image, ImageChops, ImageStat
    def flat(x):
        bg = Image.new("RGBA", x.size, (255, 255, 255, 255))
        return Image.alpha_composite(bg, x.convert("RGBA")).convert("RGB")
    st = ImageStat.Stat(ImageChops.difference(flat(a), flat(b)))
    mse = sum(v * v for v in st.rms) / 3
    return 99.0 if mse == 0 else 10 * __import__("math").log10(255 * 255 / mse)


def cmd_pack(a):
    from PIL import Image
    p = paths(a.raw)
    mapping = read_mapping(a.raw)
    index_name = os.path.basename(latest_index(a.raw))
    known = {int(x["_id"]) for x in load_index(latest_index(a.raw))}
    missing = read_missing(a.raw)
    ids = sorted({c.picto_id for c in mapping.values()} - missing)
    absent = [i for i in ids if not png_ok(os.path.join(p["png"], f"{i}.png"))]
    wanted = len(ids)
    if absent and not a.partial:
        raise SystemExit(f"{len(absent)} pictograms not downloaded yet (e.g. {absent[:5]}) — run fetch-images "
                         "(or pack --partial: words whose picture is not fetched yet get none)")
    if absent:
        print(f"pack --partial: {len(absent)} of {wanted} pictograms not fetched yet, their words are left out")
        missing = missing | set(absent)
        ids = [i for i in ids if i not in missing]
    bad = [i for i in ids if i not in known]
    if bad:
        raise SystemExit(f"ids not in the index: {bad[:5]}")
    out = os.path.abspath(a.out)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    tmp = out + ".tmp"
    if os.path.exists(tmp):
        os.remove(tmp)
    con = sqlite3.connect(tmp)
    con.executescript("""
        CREATE TABLE picto(id INTEGER PRIMARY KEY, png BLOB NOT NULL);
        CREATE TABLE word_picto(form TEXT PRIMARY KEY, picto_id INTEGER NOT NULL) WITHOUT ROWID;
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);""")
    t = time.time()
    scores = []
    total = 0
    for n, pid in enumerate(ids, 1):
        with Image.open(os.path.join(p["png"], f"{pid}.png")) as src:
            data, ref, q = shrink(src, a.px, a.colors)
        if n % 10 == 0:                                        # banding check on a 10 % sample
            scores.append((psnr(ref, q), pid))
        total += len(data)
        con.execute("INSERT INTO picto VALUES (?,?)", (pid, data))
        if n % 1000 == 0:
            print(f"pack: {n}/{len(ids)} ({time.time() - t:.0f} s)")
    con.executemany("INSERT INTO word_picto VALUES (?,?)",
                    sorted((f, c.picto_id) for f, c in mapping.items() if c.picto_id not in missing))
    changes = CHANGES.format(px=a.px, colors=a.colors) if a.colors > 0 else f"redimensionnés de 300 à {a.px} px"
    idx_date = re.search(r"(\d{8})", index_name).group(1)
    meta = {"source": "ARASAAC https://arasaac.org (" + IMAGE_URL + ")", "licence": "CC BY-NC-SA 4.0",
            "licence_url": LICENCE_URL, "attribution_fr": attribution_fr(), "author": "Sergio Palao",
            "owner": "Gobierno de Aragón", "index_date": f"{idx_date[:4]}-{idx_date[4:6]}-{idx_date[6:]}",
            "build_date": _dt.date.today().isoformat(), "px": str(a.px), "count": str(len(ids)),
            "changes": changes, "schema": "1"}
    if absent:
        meta["partial"] = f"{len(ids)} of {wanted} pictures fetched; words of the others left out"
    con.executemany("INSERT INTO meta VALUES (?,?)", sorted(meta.items()))
    con.commit()
    con.execute("VACUUM")
    con.close()
    os.replace(tmp, out)
    lic = os.path.join(os.path.dirname(out), "ARASAAC-LICENCE.txt")
    with open(lic, "w", encoding="utf-8", newline="\r\n") as f:
        f.write(f"""Pictogrammes ARASAAC — pictos.sqlite
====================================

{meta['attribution_fr']}

Auteur : Sergio Palao. Propriétaire : Gobierno de Aragón (Espagne). Origine : ARASAAC (https://arasaac.org).
Licence : Creative Commons Attribution - Pas d'Utilisation Commerciale - Partage dans les Mêmes
Conditions 4.0 International (CC BY-NC-SA 4.0) — {LICENCE_URL}

Modifications : {changes}. Index du catalogue du {meta['index_date']}, pack généré le {meta['build_date']}.
Ce fichier (pictos.sqlite) est une œuvre dérivée distribuée sous la même licence CC BY-NC-SA 4.0.
Il est distinct du logiciel « Pépite », dont le code n'est pas concerné par cette licence.
""")
    size = os.path.getsize(out)
    scores.sort()
    mean = sum(s for s, _ in scores) / len(scores) if scores else 0
    p1 = scores[max(0, len(scores) // 100 - 1)][0] if scores else 0
    words = sum(1 for c in mapping.values() if c.picto_id not in missing)
    print(f"pack: {len(ids)} pictures, {words} words, {size / 1e6:.1f} MB "
          f"(png {total / 1e6:.1f} MB) in {time.time() - t:.0f} s -> {out}")
    print(f"banding (PSNR on white, 10 % sample): mean {mean:.1f} dB, 1st pct {p1:.1f} dB, "
          f"worst {[f'{s:.1f}@{i}' for s, i in scores[:5]]}")
    if size > 30e6:
        print("WARNING: pack > 30 MB")
        return 1
    return 0


def _font(size):
    from PIL import ImageFont
    for name in ("segoeui.ttf", "arial.ttf", "DejaVuSans.ttf"):
        for d in (os.path.join(os.environ.get("WINDIR", "C:/Windows"), "Fonts"), ""):
            try:
                return ImageFont.truetype(os.path.join(d, name) if d else name, size)
            except OSError:
                pass
    return ImageFont.load_default()


def cmd_sheet(a):
    from PIL import Image, ImageDraw
    p = paths(a.raw)
    mapping = read_mapping(a.raw)
    excluded = read_excluded(a.raw)
    if a.forms == "homographs":
        forms = list(HOMOGRAPHS)
    else:
        n = int(a.forms[3:]) if a.forms.startswith("top") else 300
        forms = [f for f, _ in db_forms(a.db)[:n]]
    cols, cw, ch, per = 10, 150, 112, 100
    font, small = _font(16), _font(11)
    outs = []
    for page in range(0, len(forms), per):
        chunk = forms[page:page + per]
        rows = (len(chunk) + cols - 1) // cols
        sheet = Image.new("RGB", (cols * cw, rows * ch), "white")
        d = ImageDraw.Draw(sheet)
        for k, form in enumerate(chunk):
            x, y = (k % cols) * cw, (k // cols) * ch
            d.rectangle([x, y, x + cw - 1, y + ch - 1], outline=(200, 200, 200))
            c = mapping.get(form)
            if c and not png_ok(os.path.join(p["png"], f"{c.picto_id}.png")):
                d.text((x + 4, y + 24), f"({c.picto_id} pas encore)", fill=(200, 0, 0), font=small)
                d.text((x + 4, y + 72), f"{page + k + 1}. {form}", fill="black", font=font)
            elif c:
                with Image.open(os.path.join(p["png"], f"{c.picto_id}.png")) as src:
                    im = src.convert("RGBA")
                    im.thumbnail((64, 64), Image.LANCZOS)
                    sheet.paste(im, (x + (cw - im.width) // 2, y + 4), im)
                d.text((x + 4, y + 72), f"{page + k + 1}. {form}", fill="black", font=font)
                d.text((x + 4, y + 94), f"{c.picto_id} {c.rule[:6]} {c.cgram}", fill=(90, 90, 90), font=small)
            else:
                d.text((x + cw // 2 - 8, y + 24), "—", fill=(180, 180, 180), font=font)
                d.text((x + 4, y + 72), f"{page + k + 1}. {form}", fill=(140, 140, 140), font=font)
                if form in excluded:
                    d.text((x + 4, y + 94), excluded[form], fill=(160, 120, 0), font=small)
        name = f"sheet_{a.forms}_{page // per + 1}.png" if len(forms) > per else f"sheet_{a.forms}.png"
        path = os.path.join(p["work"], name)
        sheet.save(path)
        outs.append(path)
    print("sheet:", ", ".join(outs))


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    # bulk downloads (~5 k PNGs + index): --raw, else $PREDICT_PICTOS_RAW; no default under the source tree
    ap.add_argument("--raw", default=os.environ.get("PREDICT_PICTOS_RAW"))
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("fetch-index"); s.add_argument("--seed")
    s = sub.add_parser("map"); s.add_argument("--lexique", required=True); s.add_argument("--db", required=True)
    s.add_argument("--overrides", default=os.path.join(HERE, "picto_overrides.tsv")); s.add_argument("--index")
    s = sub.add_parser("report"); s.add_argument("--db", required=True)
    corpus = os.environ.get("PREDICT_CORPUS_DIR")
    s.add_argument("--subs", default=os.path.join(corpus, "heldout_subs_40k.txt") if corpus else None)
    s.add_argument("--wiki", default=os.path.join(corpus, "heldout_wiki_8k.txt") if corpus else None)
    s = sub.add_parser("fetch-images"); s.add_argument("--limit", type=int); s.add_argument("--delay", type=float, default=0.3)
    s = sub.add_parser("pack"); s.add_argument("--out", default=os.path.join(HERE, "out", "pictos.sqlite"))
    s.add_argument("--px", type=int, default=128); s.add_argument("--colors", type=int, default=128)
    s.add_argument("--partial", action="store_true", help="pack what is fetched so far (fetch still running)")
    s = sub.add_parser("sheet"); s.add_argument("--forms", default="top300"); s.add_argument("--db", required=True)
    a = ap.parse_args(argv)
    if not a.raw:
        ap.error("set --raw or PREDICT_PICTOS_RAW: the folder for the raw downloads (index, ~5 k PNGs; keep it on a "
                 "bulk-data disk, not under the source tree)")
    if a.cmd == "report" and not (a.subs and a.wiki):
        ap.error("set --subs and --wiki, or PREDICT_CORPUS_DIR (folder with heldout_subs_40k.txt and heldout_wiki_8k.txt): "
                 "the coverage gate needs both held-out texts")
    if a.cmd == "fetch-images" and a.delay < 0.3:
        ap.error("--delay must be >= 0.3 s (polite fetch)")
    fn = {"fetch-index": cmd_fetch_index, "map": cmd_map, "report": cmd_report, "fetch-images": cmd_fetch_images,
          "pack": cmd_pack, "sheet": cmd_sheet}[a.cmd]
    return fn(a) or 0


if __name__ == "__main__":
    sys.exit(main())
