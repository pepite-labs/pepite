#!/usr/bin/env python3
"""Build the « Noms propres » pack (pepite-noms, plan 10C) from open databases, reproducibly.

Sources (study 10c, owner answers 2026-11-14): INSEE first names (Licence Ouverte 2.0), Statbel first names and REFNIS
(CC BY 4.0), Unicode CLDR 49 French country names (Unicode License v3), flag-icons 7.6.0 4:3 flags (MIT), Wikidata
(CC0: towns, French regions / departments, historical figures, French names of Belgian places) and Wikimedia Commons
flags of the Belgian regions and provinces (public domain / CC0 only), plus Pépite's own drawings (images/*.svg, CC0 1.0,
owner 2026-11-27: Saint-Nicolas). No name is typed by hand: the curated files hold only Wikidata identifiers
(selection/historiques.tsv) and the names the drawings go to (names.lock.json « dessins »). The pack itself is CC BY 4.0.

Sub-commands (run with ``python -I``; raw downloads under --raw, default $PEPITE_NOMS_RAW, else ../out/raw-noms):

    fetch [--pin-new]            download every input of names.lock.json (and the Commons flag files listed there),
                                 one at a time; a SHA-256 mismatch is an error; --pin-new records the hash of an input
                                 whose sha256 is still null (first edition, or a new input after review)
    snapshot <id>|all            run a Wikidata query (queries/*.rq) or the Commons licence request and save the answer
                                 as a snapshot file; its SHA-256 and date go into the lock (a deliberate refresh: a
                                 rebuild never queries a live service)
    build --out <dir> [--previous <noms.json>]
                                 transform → <dir>/noms/{noms.json, images/, LICENCE.txt, SOURCES.md, ATTRIBUTION-fr.txt},
                                 <dir>/noms-<edition>.zip and <dir>/diff-<edition>.md (against --previous)

Only the standard library and Pillow (build time, palette PNG) are used; SVG flags are drawn by the pinned resvg
binary (Apache-2.0 OR MIT, build-time tool, never shipped).
"""
import argparse
import datetime as _dt
import hashlib
import io
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unicodedata
import urllib.parse
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
LOCK = os.path.join(HERE, "names.lock.json")
USER_AGENT = "PepiteDataBuild/0.8 (names pack build; https://github.com/pepite-labs/pepite)"
FORMAT, VERSION = "pepite-noms", 1
SIZE_GATE = 10 * 1024 * 1024          # plan 10 step 15: the pack ≤ 10 MB
PREDICTED_FIRST_NAMES = 5000           # owner Q2: the top ≈ 5 000 predicted, the rest recognised only
MIN_COUNT = 20                         # ≥ 20 births since 1900 (France) or ≥ 20 persons (Belgium)
RECENT = range(2010, 2026)             # owner Q11: the predicted tier ranks by recent births (pupils' generation)
COMMON_FIRST_NAMES = 1000              # owner 2026-11-15: places rank after these first names (« avec Jo » → Joseph)
ALLTIME_PREDICTED = 1000               # owner 2026-11-17 (edition 2026.2): the all-time top 1 000 are predicted too (Roger, Martine)
PROMOTED_SCALE = 0.5                   # their rang: max(recent rang, tier-1 minimum, 0.5 × all-time rang), below the pupils' names
PLACE_CATEGORIES = {"continent", "pays", "region-be", "province-be", "commune-be", "region-fr", "departement-fr", "ville"}
HOME_COUNTRIES = {"BE", "FR", "LU", "CH", "NL", "DE", "CA"}   # ISO 3166 codes (Belgium, France and neighbours, Canada)
TOP_PLACES = {"continent", "pays", "region-be", "province-be", "region-fr"}   # 0.8.3: above the towns in the places band
IMAGE_LICENCES = {"MIT", "domaine public", "CC0 1.0"}   # the pack stays CC BY 4.0: no share-alike, never NC / ND
THUMB_W, THUMB_H = 128, 96             # 4:3, the bar's picture slot (bar height unchanged, Q18)
DRAWING_BOX = (128, 128)               # Pépite's own square drawings: the ARASAAC pictures' 128 px (1:1 for M at 200 %)
DRAWING_SOURCE = "pepite"              # the sources-table id of Pépite's own drawings

# CLDR codes that are not countries or territories a pupil writes (macro-regions, exceptional reservations)
CLDR_SKIP = {"EU", "EZ", "UN", "QO", "XA", "XB", "ZZ", "AC", "CP", "CQ", "DG", "EA", "IC", "TA"}
# UN M49 continents kept from the same CLDR file (no picture)
CLDR_CONTINENTS = {"002", "009", "019", "003", "005", "142", "150"}
REFNIS_REGIONS = {"02000", "03000", "04000"}
REFNIS_PROVINCES = {"10000", "20001", "20002", "30000", "40000", "50000", "60000", "70000", "80000", "90000"}
# category priority when two sources give the same name (« Luxembourg »: the country; « Liège »: the province)
PRIORITY = ["continent", "pays", "region-be", "province-be", "commune-be", "region-fr", "departement-fr", "ville",
            "personnage", "prenom"]

_NAME_RE = re.compile(r"^[^\W\d_]+(?:(?:[ '\-]|\. )[^\W\d_]+)*\.?$")   # a dot only after a letter and before a space


# ---------------------------------------------------------------- small helpers (pure, unit-tested)

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def clean(s):
    """NFC, typographic apostrophes → ', spaces collapsed."""
    s = unicodedata.normalize("NFC", s or "").replace("’", "'").replace("‘", "'").replace(" ", " ")
    return re.sub(r"\s+", " ", s).strip()


def valid_name(s):
    """A pack name: 2–40 characters starting with a capital, letters (any script, accents) with inner spaces, hyphens,
    apostrophes or dots after a letter (« J. R. R. Tolkien »); never digits, brackets or other signs, never all
    capitals. The app applies the same rule (Predict.Core NamesFile.CheckName)."""
    if not 2 <= len(s) <= 40 or not _NAME_RE.match(s) or not s[0].isupper():
        return False
    letters = [c for c in s if c.isalpha()]
    return not (len(letters) >= 2 and all(c.isupper() for c in letters))   # « UNESCO », « RDC »: not a name here


def title_case(upper):
    """INSEE writes names in capitals with accents (« JEAN-PIERRE », « N'GOLO »): each part after a space, hyphen or
    apostrophe gets one capital."""
    out, cap = [], True
    for ch in upper:
        out.append(ch.upper() if cap else ch.lower())
        cap = ch in " -'"
    return "".join(out)


def strip_paren(s):
    """« Forest (Bruxelles-Capitale) » → « Forest »."""
    return re.sub(r"\s*\([^)]*\)\s*", " ", s).strip()


def province_name(fr):
    """« Province du Hainaut » → « Hainaut », « Province de Liège » → « Liège »."""
    return re.sub(r"^Province (?:de la |de l'|du |de |d')", "", clean(fr))


def subset_alias(alias, label):
    """An alias is kept as a recognised variant of a historical figure only when every one of its words is a word of
    the label (« Napoléon » for « Napoléon Ier », « Jacques Cousteau » for « Jacques-Yves Cousteau »): aliases also hold
    pseudonyms, misspellings and accentless copies, which must never become « correct » spellings."""
    words = set(re.split(r"[ \-]", label))
    parts = [p for p in re.split(r"[ \-]", alias) if p]
    return bool(parts) and alias != label and all(p in words for p in parts)


def rang(value, top, floor=0.01):
    """Popularity on 0–1: the square root of the share of the top value (a long tail stays visible), 3 decimals."""
    if top <= 0 or value <= 0:
        return floor
    return round(max(floor, min(1.0, math.sqrt(value / top))), 3)


def load_lock(path=LOCK):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save_lock(lock, path=LOCK):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(lock, f, ensure_ascii=False, indent=2)
        f.write("\n")


def inp(lock, ident):
    for i in lock["inputs"]:
        if i["id"] == ident:
            return i
    raise KeyError(ident)


def snap(lock, ident):
    for s in lock["snapshots"]:
        if s["id"] == ident:
            return s
    raise KeyError(ident)


# ---------------------------------------------------------------- fetch

def download(url, dest):
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    part = dest + ".part"
    with urllib.request.urlopen(req, timeout=120) as r, open(part, "wb") as f:
        shutil.copyfileobj(r, f)
    os.replace(part, dest)


def cmd_fetch(a):
    lock = load_lock()
    os.makedirs(a.raw, exist_ok=True)
    changed = False
    items = [(i["file"], i["url"], i) for i in lock["inputs"]]
    items += [(os.path.join("commons", c["file"]), c["url"], c) for c in lock.get("commons", [])]
    for name, url, entry in items:   # one download at a time (the lab disk is exFAT: no parallel writes)
        dest = os.path.join(a.raw, name)
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        want = entry.get("sha256")
        if os.path.exists(dest) and want and sha256_file(dest) == want:
            print("ok (cached) %s" % name)
            continue
        if not os.path.exists(dest) or want:
            print("fetch %s <- %s" % (name, url))
            download(url, dest)
            time.sleep(1.0)   # polite to the servers
        got = sha256_file(dest)
        if want is None:
            if not a.pin_new:
                raise SystemExit("%s has no pinned SHA-256: review it, then run fetch --pin-new" % name)
            entry["sha256"] = got
            changed = True
            print("pinned %s %s" % (name, got))
        elif got != want:
            os.remove(dest)
            raise SystemExit("SHA-256 mismatch for %s: %s (expected %s)" % (name, got, want))
        else:
            print("ok %s" % name)
    if changed:
        save_lock(lock)
    return 0


# ---------------------------------------------------------------- snapshots (deliberate refresh)

def sparql(endpoint, query):
    req = urllib.request.Request(endpoint, data=urllib.parse.urlencode({"query": query, "format": "json"}).encode(),
                                 headers={"User-Agent": USER_AGENT, "Accept": "application/sparql-results+json"})
    with urllib.request.urlopen(req, timeout=300) as r:
        return json.load(r)


def selection_restricted(path=os.path.join(HERE, "selection", "historiques.tsv")):
    """The QIDs whose note column says « sensible » (owner 2026-11-15: kept, recognised, never promoted, never pictured)."""
    out = set()
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            cols = line.rstrip("\n").split("\t")
            if len(cols) >= 4 and cols[3].strip() == "sensible":
                out.add(cols[0].strip())
    return out


def selection_qids(path=os.path.join(HERE, "selection", "historiques.tsv")):
    out = []
    with open(path, encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            q = line.split("\t")[0].strip()
            if not re.match(r"^Q\d+$", q):
                raise SystemExit("bad QID line in %s: %r" % (path, line))
            out.append(q)
    if len(set(out)) != len(out):
        raise SystemExit("duplicate QIDs in %s" % path)
    return out


def query_text(s):
    with open(os.path.join(HERE, s["query"]), encoding="utf-8") as f:
        q = f.read()
    if "{{QIDS}}" in q:
        q = q.replace("{{QIDS}}", " ".join("wd:" + x for x in selection_qids()))
    return q


def commons_flag_titles(raw, lock):
    """The flag files (P41) of the Belgian regions and provinces in the wd-belgique snapshot."""
    data = read_snapshot(raw, lock, "wd-belgique")
    titles = set()
    for b in data["results"]["bindings"]:
        nis = b["nis"]["value"]
        if nis in REFNIS_REGIONS | REFNIS_PROVINCES and "flagFile" in b:
            titles.add(urllib.parse.unquote(b["flagFile"]["value"].rsplit("/", 1)[1]))
    return sorted(titles)


def cmd_snapshot(a):
    lock = load_lock()
    os.makedirs(os.path.join(a.raw, "snapshots"), exist_ok=True)
    ids = [s["id"] for s in lock["snapshots"]] if a.id == "all" else [a.id]
    for ident in ids:
        s = snap(lock, ident)
        if s.get("kind") == "commons":
            titles = commons_flag_titles(a.raw, lock)
            q = {"action": "query", "format": "json", "prop": "imageinfo", "iiprop": "extmetadata|url|sha1",
                 "titles": "|".join("File:" + t for t in titles)}
            req = urllib.request.Request(s["endpoint"] + "?" + urllib.parse.urlencode(q), headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(req, timeout=120) as r:
                data = json.load(r)
            files = []
            for page in data["query"]["pages"].values():
                ii = (page.get("imageinfo") or [{}])[0]
                meta = ii.get("extmetadata", {})
                files.append({"file": page["title"][5:], "page": ii.get("descriptionurl"), "url": (ii.get("url") or "").split("?")[0],
                              "licence": (meta.get("LicenseShortName") or {}).get("value"),
                              "auteur": re.sub(r"<[^>]+>", "", (meta.get("Artist") or {}).get("value", "")).strip()})
            data = {"files": sorted(files, key=lambda x: x["file"])}
            # the SVGs themselves become pinned inputs (fetched one at a time by « fetch »), allowed licences only
            known = {c["file"]: c for c in lock.get("commons", [])}
            lock["commons"] = [dict(known.get(f["file"], {}), file=f["file"], url=f["url"], licence=f["licence"],
                                    sha256=known.get(f["file"], {}).get("sha256"))
                               for f in data["files"] if commons_licence(f["licence"])]
        else:
            data = sparql(s["endpoint"], query_text(s))
        dest = os.path.join(a.raw, "snapshots", s["file"])
        with open(dest, "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, ensure_ascii=False, sort_keys=True)
        s["sha256"] = sha256_file(dest)
        s["date"] = _dt.date.today().isoformat()
        print("snapshot %s: %s (%s)" % (ident, dest, s["sha256"]))
        save_lock(lock)
        time.sleep(2.0)
    return 0


def commons_licence(short):
    """The Commons licence names the pack accepts (study Q4 / D19: public domain and CC0 only for these flags)."""
    s = (short or "").strip().lower()
    if s in ("public domain", "pd", "domaine public") or s.startswith("pd-") or s.startswith("public domain"):
        return "domaine public"
    if s in ("cc0", "cc0 1.0", "cc-zero"):
        return "CC0 1.0"
    return None


def read_snapshot(raw, lock, ident):
    s = snap(lock, ident)
    path = os.path.join(raw, "snapshots", s["file"])
    if not os.path.exists(path):
        raise SystemExit("snapshot %s missing (%s): run « snapshot %s »" % (ident, path, ident))
    if s.get("sha256") and sha256_file(path) != s["sha256"]:
        raise SystemExit("snapshot %s does not match its pinned SHA-256" % ident)
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def bindings(data):
    """SPARQL JSON → list of {var: value} (entity URIs reduced to their QID)."""
    out = []
    for b in data["results"]["bindings"]:
        out.append({k: v["value"].replace("http://www.wikidata.org/entity/", "") for k, v in b.items()})
    return out


def label_of(row):
    return clean(row.get("fr") or row.get("mul") or "")


# ---------------------------------------------------------------- transforms (pure, unit-tested)

def first_names(insee_rows, statbel_rows, statbel_births=None):
    """insee_rows: (sexe, prenom, periode, valeur); statbel_rows: (name, frequency) of the whole population;
    statbel_births: {name: births 2016–2025} (Statbel newborns, owner 2026-11-15: Belgian names weighted by recent
    births like the French ones; None falls back to the population shares). → [entry] with two tiers.

    Edition 2026.2 (owner 2026-11-17): the ALLTIME_PREDICTED names most given since 1900 are predicted too, so that the
    grown-ups' names (Roger, Martine, Rodolphe) come at a sentence start and typed in lowercase; such a promoted name ranks
    by max(its recent rang, the lowest rang of the recent tier, PROMOTED_SCALE × its all-time rang), so it comes below the
    ≈ 480 most given pupils' names (Roger 481st, measured). It carries a transient « _promu » key (its recognised rang of
    2026.1): the places' cap and the figures' scale leave it out, a place or figure of the same name keeps its 2026.1
    values (merge), and the key is never written (public_entries)."""
    fr_total, fr_recent, be = {}, {}, {}
    be_births = {clean(n): c for n, c in (statbel_births or {}).items()}
    for _, prenom, periode, valeur in insee_rows:
        if prenom.startswith("_"):
            continue   # « _PRENOMS_RARES »
        name = clean(title_case(prenom))
        try:
            n = int(valeur)
        except ValueError:
            continue
        fr_total[name] = fr_total.get(name, 0) + n
        if periode.isdigit() and int(periode) in RECENT:
            fr_recent[name] = fr_recent.get(name, 0) + n
    for name, freq in statbel_rows:
        name = clean(name)
        be[name] = be.get(name, 0) + int(freq)
    tot_fr = sum(fr_total.values()) or 1
    tot_rec = sum(fr_recent.values()) or 1
    tot_be = sum(be.values()) or 1
    tot_bb = sum(be_births.values()) or 1
    names = {n for n in set(fr_total) | set(be) | set(be_births)
             if (fr_total.get(n, 0) >= MIN_COUNT or be.get(n, 0) >= MIN_COUNT or be_births.get(n, 0) >= MIN_COUNT) and valid_name(n)}
    if statbel_births is None:
        recent = {n: max(fr_recent.get(n, 0) / tot_rec, be.get(n, 0) / tot_be) for n in names}
    else:
        recent = {n: max(fr_recent.get(n, 0) / tot_rec, be_births.get(n, 0) / tot_bb) for n in names}
    alltime = {n: max(fr_total.get(n, 0) / tot_fr, be.get(n, 0) / tot_be) for n in names}
    ranked = sorted(names, key=lambda n: (-recent[n], -alltime[n], n))
    predicted = set(ranked[:PREDICTED_FIRST_NAMES])
    promoted = set(sorted(names, key=lambda n: (-alltime[n], -recent[n], n))[:ALLTIME_PREDICTED]) - predicted
    top_r = max(recent.values(), default=1)
    top_a = max(alltime.values(), default=1)
    low = min((rang(recent[n], top_r) for n in predicted), default=0.01)
    out = []
    for n in ranked:
        src = "insee" if fr_total.get(n, 0) >= be.get(n, 0) * (tot_fr / tot_be) else "statbel"
        if n in predicted:
            out.append({"nom": n, "categorie": "prenom", "rang": rang(recent[n], top_r), "source": src})
        elif n in promoted:
            ra = rang(alltime[n], top_a)
            out.append({"nom": n, "categorie": "prenom", "rang": max(rang(recent[n], top_r), low, round(PROMOTED_SCALE * ra, 3)),
                        "source": src, "_promu": ra})
        else:
            out.append({"nom": n, "categorie": "prenom", "rang": rang(alltime[n], top_a), "niveau": 2, "source": src})
    return out


def countries(territories, flag_codes):
    """CLDR fr territories → countries (alpha-2, with a flag-icons 4:3 picture when one exists) and continents."""
    out = []
    for code, name in territories.items():
        base = code.split("-alt-")[0]
        if "-alt-" in code:
            continue
        if base in CLDR_CONTINENTS:
            n = clean(name)
            if valid_name(n):
                out.append({"nom": n, "categorie": "continent", "rang": 0.8, "source": "cldr"})
            continue
        if not re.match(r"^[A-Z]{2}$", base) or base in CLDR_SKIP:
            continue
        n = clean(name)
        if not valid_name(n):   # « R.A.S. chinoise de Hong Kong »: the short form (« Hong Kong ») when it is a name
            n = clean(territories.get(base + "-alt-short", ""))
            if not valid_name(n):
                continue
        # 0.8.3: the pupils' own countries and their neighbours first among the countries (« Bel » → Belgique before Belize)
        e = {"nom": n, "categorie": "pays", "rang": 0.9 if base in HOME_COUNTRIES else 0.6, "source": "cldr"}
        alt = territories.get(base + "-alt-variant")
        if alt and valid_name(clean(alt)) and clean(alt) != n:
            e["variantes"] = [clean(alt)]
        if base.lower() in flag_codes:
            e.update(image="images/pays/%s.png" % base.lower(), image_source="flag-icons", svg=("flag-icons", base.lower()))
        out.append(e)
    return out


def belgium(refnis_rows, wd_rows, flags):
    """REFNIS current lines (level, code, de, fr, nl) + the wd-belgique snapshot + {nis: (png path, commons file)}
    → regions, provinces, municipalities: French name shown, Dutch / German names and the Wikidata French label as
    variants (owner Q9), population from Wikidata for the ranking."""
    wd = {}
    for r in wd_rows:
        wd.setdefault(r["nis"], []).append(r)
    pops = {}
    for nis, rows in wd.items():
        p = [int(float(r["population"])) for r in rows if r.get("population")]
        if p:
            pops[nis] = max(p)
    top = max((pops.get(code, 0) for lvl, code, *_ in refnis_rows if lvl == "4"), default=1)
    out = []
    for lvl, code, de, fr, nl in refnis_rows:
        if lvl == "1":
            cat, shown = "region-be", clean(fr)
        elif lvl == "2":
            cat, shown = "province-be", province_name(fr)
        elif lvl == "4":
            cat, shown = "commune-be", clean(strip_paren(fr))
        else:
            continue   # arrondissements: the same names as their towns
        variants = []
        for v in [nl, de] + [label_of(r) for r in wd.get(code, [])]:
            v = clean(strip_paren(v)) if v else ""
            if lvl == "2":
                v = re.sub(r"^(Provincie |Provinz )", "", v)
            if v and v != shown and v not in variants and valid_name(v):
                variants.append(v)
        if lvl in ("1", "2"):
            for r in wd.get(code, []):
                for al in (r.get("aliases") or "").split("|"):
                    al = clean(al)
                    if al and al != shown and al not in variants and valid_name(al) and len(al.split()) <= 3 and not al.isupper():
                        variants.append(al)
        e = {"nom": shown, "categorie": cat, "source": "statbel-refnis",
             "rang": 0.7 if lvl in ("1", "2") else rang(pops.get(code, 0), top, 0.05)}
        if variants:
            e["variantes"] = variants
        if code in flags:
            png, commons_file = flags[code]
            e.update(image=png, image_source="commons:" + commons_file, svg=("commons", commons_file))
        out.append(e)
    return out


def towns(rows):
    """wd-villes snapshot rows → towns ranked by population (capitals at least 0.3)."""
    out, top = [], 2_200_000
    for r in rows:
        n = label_of(r)
        if not valid_name(n):
            continue
        pop = int(float(r["population"])) if r.get("population") else 0
        rg = rang(pop, top, 0.05)
        if "capitale" in (r.get("reasons") or ""):
            rg = max(rg, 0.3)
        out.append({"nom": n, "categorie": "ville", "rang": rg, "source": "wikidata"})
    return out


def france_admin(rows):
    out = []
    for r in rows:
        n = label_of(r)
        if valid_name(n):
            out.append({"nom": n, "categorie": r.get("kind", "region-fr"), "rang": 0.5, "source": "wikidata"})
    return out


def historical(rows, qids, restricted=frozenset()):
    """wd-historiques rows → figures (deceased only), aliases that are parts of the label as variants (subset_alias)."""
    want = set(qids)
    out, seen = [], set()
    for r in rows:
        q = r["item"]
        if q not in want or q in seen:
            continue
        seen.add(q)
        n = label_of(r)
        born = int(r["born"]) if r.get("born") else None
        if not r.get("died") and not (born is None or born < 1800):
            raise SystemExit("historical figure %s (%s) has no death date: the pack holds deceased people only" % (q, n))
        if not valid_name(n):
            continue
        links = int(r.get("links") or 0)
        e = {"nom": n, "categorie": "personnage", "rang": rang(links, 250, 0.2), "source": "wikidata", "wikidata": q}
        if q in restricted:
            # owner 2026-11-15: recognised, offered only after a capital and 3 letters, after every first name, no picture
            e.update(niveau=2, restreint=True)
        variants = sorted({clean(a) for a in (r.get("aliases") or "").split("|") if a and subset_alias(clean(a), n) and valid_name(clean(a))})
        if variants:
            e["variantes"] = variants
        out.append(e)
    missing = want - seen
    if missing:
        raise SystemExit("snapshot wd-historiques lacks %d selected QIDs (%s…): refresh it" % (len(missing), sorted(missing)[:5]))
    return out


def below_common_first_names(places, firsts, n=None):
    """Owner 2026-11-15: towns, countries and regions rank after the common first names (« avec Jo » → Joseph before
    Johannesbourg, « je vois Ca » → first names before Cali): a place's rang is scaled below the rang of the n-th
    predicted first name — countries, continents, Belgian regions / provinces and French regions into (0.80 … 0.95) ×
    that rang, every other place (municipalities, departments, towns) into (0.05 … 0.80) ×, each keeping its own order. Never below 0.0105, so a place stays out of the
    « rare recognised name » floor the spelling indicator treats as a possible misspelling (D2)."""
    n = n or COMMON_FIRST_NAMES
    ranks = sorted((e["rang"] for e in firsts if e.get("niveau") != 2 and "_promu" not in e), reverse=True)   # 2026.2: as before
    if not ranks:
        return places
    cap = ranks[min(n, len(ranks)) - 1]
    for e in places:
        if e["categorie"] in TOP_PLACES:
            # 0.8.3 (10C review): countries, continents and regions head the band (« né en Bel » → Belgique before Belo
            # Horizonte, Belgrade): 0.80 … 0.95 × cap
            e["rang"] = round(max(0.0105, cap * (0.80 + 0.15 * e["rang"])), 4)
        elif e["categorie"] in PLACE_CATEGORIES:
            e["rang"] = round(max(0.0105, cap * (0.05 + 0.75 * e["rang"])), 4)
    return places


def merge(entries):
    """One entry per shown name: the highest-priority category wins, variants are united, the highest rang and tier 1
    win, the first picture is kept. Variants equal to another entry's name are dropped (the other entry holds it)."""
    by = {}
    for e in entries:
        k = e["nom"]
        if k not in by:
            by[k] = dict(e)
            continue
        cur = by[k]
        a, b = (cur, e) if PRIORITY.index(cur["categorie"]) <= PRIORITY.index(e["categorie"]) else (e, cur)
        if "_promu" in b and a["categorie"] != "prenom":
            # 2026.2: a place or a figure that is also a promoted first name keeps its 2026.1 rang and tier
            b = dict(b, rang=b["_promu"], niveau=2)
        m = dict(a)
        m["rang"] = max(a["rang"], b["rang"])
        if a.get("niveau") == 2 and b.get("niveau") != 2:
            m.pop("niveau", None)
        vs = list(a.get("variantes", [])) + [v for v in b.get("variantes", []) if v not in a.get("variantes", [])]
        if vs:
            m["variantes"] = vs
        if "image" not in m and "image" in b:
            for key in ("image", "image_source", "svg"):
                m[key] = b[key]
        by[k] = m
    names = set(by)
    for e in by.values():
        if "variantes" in e:
            e["variantes"] = [v for v in e["variantes"] if v not in names and v != e["nom"]]
            if not e["variantes"]:
                del e["variantes"]
    return sorted(by.values(), key=lambda e: (PRIORITY.index(e["categorie"]), -e["rang"], e["nom"]))


def public_entries(entries):
    """The entries as written to noms.json: the transient keys (« _promu ») removed."""
    return [{k: v for k, v in e.items() if not k.startswith("_")} for e in entries]


def apply_drawings(entries, lock, here=HERE):
    """Pépite's own drawings (lock « dessins », owner 2026-11-27): each goes to the entry of its « nom », which must exist,
    must not be restricted and must not already have a picture; the committed SVG must match its pinned SHA-256 and its
    licence be allowed. With « variantes »: false the entry gets « image_variantes »: false (the picture stays off its
    variants: « Saint-Nicolas » is pictured, « Sint-Niklaas » is not). The entry's source, licence and author become the
    drawing's, as for a flag. Returns [(entry, svg path)] to render."""
    by = {e["nom"]: e for e in entries}
    out = []
    for d in lock.get("dessins", {}).get("files", []):
        e = by.get(d["nom"])
        if e is None:
            raise SystemExit("drawing %s: no entry « %s » in the pack" % (d["file"], d["nom"]))
        if e.get("restreint"):
            raise SystemExit("drawing %s: « %s » is a restricted figure (never a picture)" % (d["file"], d["nom"]))
        if "image" in e or "svg" in e:
            raise SystemExit("drawing %s: « %s » already has a picture" % (d["file"], d["nom"]))
        if d.get("licence") not in IMAGE_LICENCES:
            raise SystemExit("drawing %s: licence %r not allowed" % (d["file"], d.get("licence")))
        if not re.match(r"^images/pepite/[a-z0-9-]+\.png$", d.get("image", "")):
            raise SystemExit("drawing %s: bad image path %r" % (d["file"], d.get("image")))
        svg = checked(os.path.join(here, *d["file"].split("/")), d.get("sha256"))
        e.update(image=d["image"], source=DRAWING_SOURCE, licence=d["licence"], auteur=d["auteur"])
        if d.get("variantes") is False and e.get("variantes"):
            e["image_variantes"] = False
        out.append((e, svg))
    return out


def diff_report(prev, cur, edition):
    """Markdown: names added / removed per category against the previous edition (owner Q10: reviewed before shipping);
    since 2026.2 also the names that became predicted and the names whose rang changed."""
    def key(e):
        return (e["categorie"], e["nom"])
    p = {key(e) for e in prev}
    c = {key(e) for e in cur}
    lines = ["# Noms propres — édition %s : différences avec l'édition précédente" % edition, ""]
    cats = sorted({k[0] for k in p | c}, key=lambda x: PRIORITY.index(x) if x in PRIORITY else 99)
    lines.append("| catégorie | avant | après | ajoutés | retirés |")
    lines.append("|---|---|---|---|---|")
    for cat in cats:
        pc = {k for k in p if k[0] == cat}
        cc = {k for k in c if k[0] == cat}
        lines.append("| %s | %d | %d | %d | %d |" % (cat, len(pc), len(cc), len(cc - pc), len(pc - cc)))
    for cat in cats:
        added = sorted(k[1] for k in c - p if k[0] == cat)
        removed = sorted(k[1] for k in p - c if k[0] == cat)
        if not added and not removed:
            continue
        lines += ["", "## %s" % cat]
        if added:
            lines.append("Ajoutés (%d) : %s%s" % (len(added), ", ".join(added[:300]), " …" if len(added) > 300 else ""))
        if removed:
            lines.append("Retirés (%d) : %s%s" % (len(removed), ", ".join(removed[:300]), " …" if len(removed) > 300 else ""))
    pm = {key(e): e for e in prev}
    both = [(pm[key(e)], e) for e in cur if key(e) in pm]
    promoted = sorted(e["nom"] for a, e in both if a.get("niveau") == 2 and e.get("niveau") != 2)
    demoted = sorted(e["nom"] for a, e in both if a.get("niveau") != 2 and e.get("niveau") == 2)
    if promoted or demoted:
        lines += ["", "## Passés au niveau prédit"]
        lines.append("Passés au niveau prédit (%d) : %s%s" % (len(promoted), ", ".join(promoted[:300]), " …" if len(promoted) > 300 else ""))
        if demoted:
            lines.append("Passés au niveau reconnu (%d) : %s%s" % (len(demoted), ", ".join(demoted[:300]), " …" if len(demoted) > 300 else ""))
    pic_added = sorted((e for a, e in both if "image" in e and "image" not in a), key=lambda x: x["nom"])
    pic_removed = sorted(e["nom"] for a, e in both if "image" in a and "image" not in e)
    if pic_added or pic_removed:
        lines += ["", "## Images"]
        if pic_added:
            lines.append("Image ajoutée (%d) : %s" % (len(pic_added), ", ".join(
                e["nom"] + (" (pas sur ses variantes : %s)" % ", ".join(e.get("variantes", [])) if e.get("image_variantes") is False else "")
                for e in pic_added)))
        if pic_removed:
            lines.append("Image retirée (%d) : %s" % (len(pic_removed), ", ".join(pic_removed)))
    changed = [(a, e) for a, e in both if a.get("rang") != e.get("rang")]
    if changed:
        lines += ["", "## Rang changé", "", "| catégorie | noms |", "|---|---|"]
        for cat in cats:
            n = sum(1 for _, e in changed if e["categorie"] == cat)
            if n:
                lines.append("| %s | %d |" % (cat, n))
        changed.sort(key=lambda x: (PRIORITY.index(x[1]["categorie"]) if x[1]["categorie"] in PRIORITY else 99, x[1]["nom"]))
        shown = ["%s %s → %s" % (e["nom"], a.get("rang"), e.get("rang")) for a, e in changed[:300]]
        lines += ["", "Rang changé (%d) : %s%s" % (len(changed), ", ".join(shown), " …" if len(changed) > 300 else "")]
    return "\n".join(lines) + "\n"


def sources_table(lock):
    """The noms.json « sources » table: id → titre, licence, url (each entry's « source » points here)."""
    t = {}
    for i in lock["inputs"]:
        if i.get("role") == "tool" or i["id"] in ("cldr-licence", "cc-by-4.0"):
            continue
        key = {"statbel-prenoms-h": "statbel", "statbel-prenoms-f": "statbel", "statbel-naissances-f": "statbel",
               "statbel-naissances-h": "statbel", "cldr-territoires": "cldr"}.get(i["id"], i["id"])
        t.setdefault(key, {"titre": i["version"] if key != "statbel" else "Statbel, Prénoms de la population totale par commune, 2026 ; Prénoms des nouveau-nés par commune, 2016-2025",
                           "licence": i["licence"], "url": i["page"]})
    t["wikidata"] = {"titre": "Wikidata", "licence": lock["wikidata"]["licence"], "url": "https://www.wikidata.org"}
    t["commons"] = {"titre": "Wikimedia Commons", "licence": "domaine public ou CC0 1.0 (voir SOURCES.md)",
                    "url": "https://commons.wikimedia.org"}
    if lock.get("dessins", {}).get("files"):
        t[DRAWING_SOURCE] = {"titre": "Pépite, dessins originaux", "licence": "CC0 1.0",
                             "url": "https://github.com/pepite-labs/pepite"}
    return t


def attribution(lock):
    p = lock["pack"]
    lines = ["Noms propres — paquet de données ouvertes « %s », édition %s, licence %s" % (p["titre"], p["edition"], p["licence"]),
             "(%s). Données modifiées : sélection, mise en forme et classement par Pépite." % p["licence_url"], ""]
    for i in lock["inputs"]:
        if i.get("attribution_fr"):
            lines.append("• " + i["attribution_fr"])
    lines.append("• " + lock["wikidata"]["attribution_fr"])
    lines.append("• " + lock["commons_attribution_fr"])
    if lock.get("dessins", {}).get("files"):
        lines.append("• " + lock["dessins"]["attribution_fr"])
    lines.append("Statbel et l'Insee ne cautionnent pas Pépite ni l'usage fait de leurs données.")
    return "\n".join(lines) + "\n"


# ---------------------------------------------------------------- build

def read_zip_text(path, encoding, suffix):
    with zipfile.ZipFile(path) as z:
        names = [n for n in z.namelist() if n.lower().endswith(suffix)]
        if len(names) != 1:
            raise SystemExit("%s: expected one %s member, found %s" % (path, suffix, names))
        return z.read(names[0]).decode(encoding)


def checked(p, sha):
    """A raw file that exists and matches its pinned SHA-256 (a build never reads an unpinned or changed input)."""
    if not os.path.exists(p):
        raise SystemExit("missing raw file %s: run fetch first" % p)
    if not sha:
        raise SystemExit("%s has no pinned SHA-256 in names.lock.json" % p)
    if sha256_file(p) != sha:
        raise SystemExit("SHA-256 mismatch for %s" % p)
    return p


def input_path(raw, lock, ident):
    i = inp(lock, ident)
    return checked(os.path.join(raw, i["file"]), i.get("sha256"))


def commons_path(raw, lock, fname):
    c = next((c for c in lock.get("commons", []) if c["file"] == fname), None)
    if c is None:
        raise SystemExit("Commons file %s is not pinned in names.lock.json" % fname)
    return checked(os.path.join(raw, "commons", fname), c.get("sha256"))


def load_inputs(raw, lock):
    def path(ident):
        return input_path(raw, lock, ident)
    txt = read_zip_text(path("insee"), "utf-8-sig", ".csv")
    insee = [tuple(l.split(";")[:4]) for l in txt.splitlines()[1:] if l.count(";") >= 3]
    stat = []
    for ident in ("statbel-prenoms-h", "statbel-prenoms-f"):
        for l in read_zip_text(path(ident), "cp1252", ".txt").splitlines()[1:]:
            f = l.split("|")
            if len(f) == 5 and f[4].strip().isdigit():
                stat.append((f[3], f[4].strip()))
    births = {}   # Statbel newborns 2016–2025 by municipality (≥ 5 per municipality), summed per name
    for ident in ("statbel-naissances-f", "statbel-naissances-h"):
        for l in read_zip_text(path(ident), "cp1252", ".txt").splitlines()[1:]:
            f = l.split("|")
            if len(f) == 5 and f[4].strip().isdigit():
                births[f[3]] = births.get(f[3], 0) + int(f[4].strip())
    ref = []
    for l in read_zip_text(path("statbel-refnis"), "cp1252", ".txt").splitlines()[1:]:
        f = l.split("|")
        if len(f) >= 8 and f[7].strip() == "31/12/9999":
            ref.append((f[0], f[1], f[3], f[4], f[5]))
    ref = list(dict.fromkeys(ref))   # the file repeats some lines
    with open(path("cldr-territoires"), encoding="utf-8") as f:
        terr = json.load(f)["main"]["fr"]["localeDisplayNames"]["territories"]
    return insee, stat, births, ref, terr


def flag_icons_codes(zpath):
    with zipfile.ZipFile(zpath) as z:
        return {os.path.basename(n)[:-4] for n in z.namelist() if "/flags/4x3/" in n and n.endswith(".svg")}


def render(svg_bytes, resvg, out_png, box=(THUMB_W, THUMB_H)):
    """SVG → PNG at twice the box width (resvg), fitted into the box (flags: THUMB_W × THUMB_H; drawings: DRAWING_BOX),
    palette PNG (Pillow)."""
    from PIL import Image   # build-time only
    with tempfile.TemporaryDirectory() as td:
        src, dst = os.path.join(td, "in.svg"), os.path.join(td, "out.png")
        with open(src, "wb") as f:
            f.write(svg_bytes)
        subprocess.run([resvg, "--width", str(box[0] * 2), src, dst], check=True, capture_output=True)
        im = Image.open(dst).convert("RGBA")
    im.thumbnail(box, Image.LANCZOS)
    pal = im.quantize(colors=128, method=Image.FASTOCTREE)
    os.makedirs(os.path.dirname(out_png), exist_ok=True)
    pal.save(out_png, optimize=True)


def resvg_exe(raw, lock):
    """The pinned resvg.exe: extracted from the pinned zip, and its own SHA-256 checked before every run (« exe_sha256 »)."""
    i = inp(lock, "resvg")
    z = input_path(raw, lock, "resvg")
    d = os.path.join(raw, "tools", "resvg-0.50.0")
    exe = os.path.join(d, "resvg.exe")
    if not os.path.exists(exe):
        os.makedirs(d, exist_ok=True)
        with zipfile.ZipFile(z) as zz:
            member = [n for n in zz.namelist() if n.lower().endswith("resvg.exe")][0]
            with zz.open(member) as m, open(exe, "wb") as f:
                shutil.copyfileobj(m, f)
    return checked(exe, i.get("exe_sha256"))


def cmd_build(a):
    lock = load_lock()
    raw = a.raw
    insee, stat, births, ref, terr = load_inputs(raw, lock)
    flagzip = input_path(raw, lock, "flag-icons")
    codes = flag_icons_codes(flagzip)

    # Belgian region / province flags: Commons files whose licence the commons-drapeaux snapshot says is PD / CC0
    cm = {f["file"]: f for f in read_snapshot(raw, lock, "commons-drapeaux")["files"]}
    pinned = {c["file"]: c for c in lock.get("commons", [])}
    wd_be = bindings(read_snapshot(raw, lock, "wd-belgique"))
    flags = {}
    for r in wd_be:
        nis = r["nis"]
        if nis not in REFNIS_REGIONS | REFNIS_PROVINCES or "flagFile" not in r:
            continue
        fname = urllib.parse.unquote(r["flagFile"].rsplit("/", 1)[1])
        lic = commons_licence((cm.get(fname) or {}).get("licence"))
        if lic and fname in pinned and nis not in flags:
            flags[nis] = ("images/belgique/%s.png" % nis, fname)

    firsts = first_names(insee, stat, births)
    places = []
    places += countries(terr, codes)
    places += belgium(ref, wd_be, flags)
    places += france_admin(bindings(read_snapshot(raw, lock, "wd-france")))
    places += towns(bindings(read_snapshot(raw, lock, "wd-villes")))
    entries = below_common_first_names(places, firsts)
    entries += below_first_names(historical(bindings(read_snapshot(raw, lock, "wd-historiques")), selection_qids(), selection_restricted()), firsts)
    entries += firsts
    entries = public_entries(merge(entries))

    out = os.path.abspath(a.out)
    pack = os.path.join(out, "noms")
    if os.path.isdir(pack):
        if not os.path.exists(os.path.join(pack, "noms.json")):
            raise SystemExit("refusing to replace %s: not an earlier pack" % pack)
        shutil.rmtree(pack)
    os.makedirs(pack)

    # pictures
    resvg = resvg_exe(raw, lock)
    image_rows = []
    with zipfile.ZipFile(flagzip) as fz:
        fl = {os.path.basename(n)[:-4]: n for n in fz.namelist() if "/flags/4x3/" in n and n.endswith(".svg")}
        for e in entries:
            if "svg" not in e:
                continue
            kind, ref_ = e.pop("svg")
            if kind == "flag-icons":
                svg = fz.read(fl[ref_])
                e["source"], e["licence"], e["auteur"] = "flag-icons", "MIT", "flag-icons (Panayiotis Lipiridis et contributeurs)"
            else:
                with open(commons_path(raw, lock, ref_), "rb") as f:
                    svg = f.read()
                meta = cm[ref_]
                e["source"] = meta["page"]
                e["licence"] = commons_licence(meta["licence"])
                e["auteur"] = meta["auteur"] or "Wikimedia Commons"
                image_rows.append((e["nom"], ref_, meta["page"], e["licence"], e["auteur"]))
            e.pop("image_source", None)
            render(svg, resvg, os.path.join(pack, e["image"].replace("/", os.sep)))
    for e in entries:
        e.pop("image_source", None)
    drawings = apply_drawings(entries, lock)
    for e, svg_path in drawings:
        with open(svg_path, "rb") as f:
            render(f.read(), resvg, os.path.join(pack, e["image"].replace("/", os.sep)), DRAWING_BOX)

    # checks
    names = [e["nom"] for e in entries]
    assert len(names) == len(set(names)), "duplicate names"
    srcs = sources_table(lock)
    for e in entries:
        if not valid_name(e["nom"]) or not 0 < e["rang"] <= 1:
            raise SystemExit("bad entry %r" % e)
        if "image" in e and e.get("licence") not in IMAGE_LICENCES:
            raise SystemExit("picture of %s: licence %r not allowed" % (e["nom"], e.get("licence")))
        if "licence" not in e and e["source"] not in srcs:
            raise SystemExit("entry %s has no licence" % e["nom"])

    doc = {"format": FORMAT, "version": VERSION, "titre": lock["pack"]["titre"], "edition": lock["pack"]["edition"],
           "licence": lock["pack"]["licence"], "licence_url": lock["pack"]["licence_url"],
           "attribution": attribution(lock), "sources": srcs, "noms": entries}
    body = json.dumps({k: v for k, v in doc.items() if k != "noms"}, ensure_ascii=False, indent=1)[:-2]
    with open(os.path.join(pack, "noms.json"), "w", encoding="utf-8", newline="\n") as f:
        f.write(body + ',\n "noms": [\n')
        f.write(",\n".join(json.dumps(e, ensure_ascii=False, separators=(",", ":")) for e in entries))
        f.write("\n ]\n}\n")
    with open(os.path.join(pack, "ATTRIBUTION-fr.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(attribution(lock))
    write_licence(pack, raw, lock)
    write_sources(pack, lock, image_rows, entries, drawings)

    # diff report against the previous edition
    prev = []
    if a.previous:
        with open(a.previous, encoding="utf-8") as f:
            prev = json.load(f)["noms"]
    edition = lock["pack"]["edition"]
    with open(os.path.join(out, "diff-%s.md" % edition), "w", encoding="utf-8", newline="\n") as f:
        f.write(diff_report(prev, entries, edition))

    zpath = os.path.join(out, "noms-%s.zip" % edition)
    write_zip(zpath, pack, out)
    size = sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(pack) for f in fs)
    counts = {}
    for e in entries:
        k = e["categorie"] + (" (reconnu)" if e.get("niveau") == 2 else "")
        counts[k] = counts.get(k, 0) + 1
    report = {"edition": edition, "names": len(entries), "counts": counts,
              "images": sum(1 for e in entries if "image" in e), "folder_bytes": size, "zip_bytes": os.path.getsize(zpath),
              "zip_sha256": sha256_file(zpath), "noms_json_sha256": sha256_file(os.path.join(pack, "noms.json")),
              "tools": {"python": sys.version.split()[0], "pillow": pillow_version(), "resvg": inp(lock, "resvg")["version"]}}
    with open(os.path.join(out, "build-report.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(report, f, ensure_ascii=False, indent=1)
    print(json.dumps(report, ensure_ascii=False, indent=1))
    if size > SIZE_GATE:
        raise SystemExit("size gate: %d bytes > %d" % (size, SIZE_GATE))
    return 0


def pillow_version():
    try:
        import PIL
        return PIL.__version__
    except ImportError:
        return None


ZIP_DATE = (2026, 1, 1, 0, 0, 0)   # a fixed date in every zip entry: two builds of the same inputs give the same zip


def write_zip(zpath, pack, out):
    """Deterministic zip: sorted relative paths, a fixed date and fixed permissions on every entry."""
    files = []
    for root, _, fs in os.walk(pack):
        for fn in fs:
            p = os.path.join(root, fn)
            files.append((os.path.relpath(p, out).replace(os.sep, "/"), p))
    with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
        for rel, p in sorted(files):
            zi = zipfile.ZipInfo(rel, date_time=ZIP_DATE)
            zi.compress_type = zipfile.ZIP_DEFLATED
            zi.external_attr = 0o644 << 16
            with open(p, "rb") as f:
                z.writestr(zi, f.read())


def below_first_names(figures, firsts):
    """Plan 10C review: a historical figure ranks below every predicted first name (« Ad » gives « Adam » before
    « Adolf »): its rang is scaled into (0.5 … 0.95) × the lowest rang of the predicted first names, by its own share."""
    low = min((e["rang"] for e in firsts if e.get("niveau") != 2 and "_promu" not in e), default=0.01)   # 2026.2: as before
    for e in figures:
        e["rang"] = 0.001 if e.get("restreint") else round(max(0.001, low * (0.5 + 0.45 * e["rang"])), 4)
    return figures


def write_licence(pack, raw, lock):
    """LICENCE.txt: the pack's CC BY 4.0 notice, then the notices its inputs require (Unicode License v3 of CLDR, the
    MIT notice of flag-icons verbatim), then the CC BY 4.0 legal code."""
    with zipfile.ZipFile(input_path(raw, lock, "flag-icons")) as z:
        mit = z.read([n for n in z.namelist() if n.count("/") == 1 and n.endswith("/LICENSE")][0]).decode("utf-8")
    with open(input_path(raw, lock, "cldr-licence"), encoding="utf-8") as f:
        unicode_lic = f.read()
    with open(input_path(raw, lock, "cc-by-4.0"), encoding="utf-8") as f:
        ccby = f.read()
    p = lock["pack"]
    text = ["« %s » (édition %s) — © 2026 Les auteurs de Pépite, sous licence Creative Commons Attribution 4.0" % (p["titre"], p["edition"]),
            "International (CC BY 4.0), %s. Les contributions sont acceptées sous la même licence." % p["licence_url"], "",
            "Ce paquet est construit à partir de données ouvertes ; leurs auteurs et licences sont dans ATTRIBUTION-fr.txt et",
            "SOURCES.md. Les avis ci-dessous accompagnent les données reprises.", "",
            "=" * 78, "Noms des pays : Unicode CLDR 49 — Unicode License v3", "=" * 78, unicode_lic.strip(), "",
            "=" * 78, "Drapeaux des pays : flag-icons 7.6.0 — licence MIT", "=" * 78, mit.strip(), ""] + drawing_notice(lock) + [
            "=" * 78, "Creative Commons Attribution 4.0 International — texte juridique", "=" * 78, ccby.strip(), ""]
    with open(os.path.join(pack, "LICENCE.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(text))


def drawing_notice(lock):
    """The LICENCE.txt section of Pépite's own drawings (CC0 1.0 requires no notice: the dedication is stated)."""
    files = lock.get("dessins", {}).get("files", [])
    if not files:
        return []
    out = ["=" * 78, "Dessins de Pépite — CC0 1.0 Universel (domaine public)", "=" * 78]
    for d in files:
        out.append("%s (%s) : %s." % (d["titre"], d["image"], d["auteur"]))
    out += ["Dans la mesure permise par la loi, leurs auteurs renoncent à tous leurs droits d'auteur et droits voisins sur ces",
            "dessins (CC0 1.0 Universel, https://creativecommons.org/publicdomain/zero/1.0/legalcode.fr) : ils peuvent être",
            "copiés, modifiés et diffusés, même à des fins commerciales, sans autorisation ni mention.", ""]
    return out


def write_sources(pack, lock, image_rows, entries, drawings=()):
    lines = ["# Sources du paquet « %s », édition %s" % (lock["pack"]["titre"], lock["pack"]["edition"]), "",
             "| entrée | version | licence | URL | SHA-256 |", "|---|---|---|---|---|"]
    for i in lock["inputs"]:
        if i.get("role") == "tool":
            continue
        lines.append("| %s | %s | %s | %s | `%s` |" % (i["id"], i["version"], i["licence"], i["url"], i["sha256"]))
    lines += ["", "Instantanés Wikidata (CC0 1.0) et Commons, refaits seulement par l'étape « snapshot » :", "",
              "| instantané | date | requête | SHA-256 |", "|---|---|---|---|"]
    for s in lock["snapshots"]:
        lines.append("| %s | %s | %s | `%s` |" % (s["id"], s["date"], s.get("query", s.get("endpoint")), s["sha256"]))
    lines += ["", "Drapeaux des régions et provinces de Belgique (Wikimedia Commons) :", "",
              "| nom | fichier | page | licence | auteur |", "|---|---|---|---|---|"]
    for nom, f, page, lic, aut in sorted(image_rows):
        lines.append("| %s | %s | %s | %s | %s |" % (nom, f, page, lic, aut.replace("|", "/")))
    tool = inp(lock, "resvg")
    lines += ["", "Outil de construction (jamais livré) : %s, %s, %s, SHA-256 `%s`." % (tool["version"], tool["licence"], tool["url"], tool["sha256"]),
              "", "Drapeaux des pays : flag-icons 7.6.0, dossier `flags/4x3`, licence MIT (voir LICENCE.txt) ; %d images." %
              sum(1 for e in entries if e.get("source") == "flag-icons")]
    if drawings:
        files = {d["nom"]: d for d in lock["dessins"]["files"]}
        lines += ["", "Dessins de Pépite (dessins originaux, aucune œuvre de tiers ; source SVG dans `pepite-noms/images/`, rendu",
                  "par resvg en PNG 128 × 128) :", "",
                  "| nom | dessin | fichier | licence | auteur | sur les variantes | SHA-256 du SVG |", "|---|---|---|---|---|---|---|"]
        for e, _ in sorted(drawings, key=lambda x: x[0]["nom"]):
            d = files[e["nom"]]
            lines.append("| %s | %s | %s | %s | %s | %s | `%s` |" % (
                e["nom"], d["titre"], d["file"], d["licence"], d["auteur"],
                "non (%s)" % ", ".join(e.get("variantes", [])) if e.get("image_variantes") is False else "oui", d["sha256"]))
    with open(os.path.join(pack, "SOURCES.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--raw", default=os.environ.get("PEPITE_NOMS_RAW") or os.path.join(HERE, "..", "out", "raw-noms"))
    sub = ap.add_subparsers(dest="cmd", required=True)
    f = sub.add_parser("fetch")
    f.add_argument("--pin-new", action="store_true")
    s = sub.add_parser("snapshot")
    s.add_argument("id")
    b = sub.add_parser("build")
    b.add_argument("--out", default=os.path.join(HERE, "..", "out", "noms-pack"))
    b.add_argument("--previous")
    a = ap.parse_args(argv)
    return {"fetch": cmd_fetch, "snapshot": cmd_snapshot, "build": cmd_build}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
