#!/usr/bin/env python3
"""Candidate offensive words for the « mots exclus » list (plan offensive-words, Phase 0, steps 0.1–0.3).

Reads the French Wiktionnaire extraction of kaikki.org (raw wiktextract JSONL, CC BY-SA 4.0 + GFDL, pinned in
``exclusions.lock.json``) and the LDNOOBW ``fr`` file (CC BY 4.0, a cross-check seed only), and keeps the headwords of
``out/prediction.sqlite`` (a lemma, or a form of the lexicon) whose senses carry an offensive, vulgar, sexual or drug
label.

**This script never prints a word.** The console gets counts, label ids (a hash of the label name: a raw tag can be
free text) and row ids only; words go into ``candidates.tsv`` under the work folder, which is never committed.

Sub-commands (run with ``python -I``; standard library only):

    probe --wikt FILE [--out FILE]          label / tag / category counts of the French entries (0.1): label ids,
                                            counts and the signals each label triggers (names only in --out)
    candidates --wikt FILE --ldnoobw FILE --db out/prediction.sqlite --out <work>/exclusions/candidates.tsv
                                            the candidate rows with the proposed decision and reason (0.2, 0.3)

Categories: R ethnic / racial / religious slur, H homophobic / transphobic, S sexist slur, D disability slur,
X sexual / obscene, V vulgar not sexual (kept), I generic insult, G drugs. Policy (owner answers 2026-11-26, « exclude all »): R, H, S, D, X, I, G
excluded only when the dominant current sense is the flagged one (first sense flagged, or at least half the senses
flagged) — owner 2026-11-27: an R / H / S word is excluded only when its dominant
sense is the slur, every sense is flagged, or LDNOOBW lists it; correct anatomical terms are never X; a sense marked « péjoratif »
only (no insult label) is an I candidate proposed « keep » for review. Skipped: abbreviation senses, symbol / letter /
affix entries, inflection senses; an obsolete or archaic sense never makes a D / X / I / G word dominant; a one- or
two-letter headword is never proposed « exclude ».

How one sense is classified (labels = its tags, raw tags, topics and categories; the glosses are only read to tell
whom an insult targets, never printed):

    off   insult / offensive / pejorative label          vulg  « vulgaire » label
    sex   sexuality / pornography lexicon                drug  drug lexicon, or a slang drug gloss
    slur  a real insult / slur label: offensive, « Insulte », « Insultes en français », « injurieux », « insultant »,
          « Raciste », « Antisémite », « Xénophobe », « Homophobe », « Transphobe », « Misogyne », the sexism label
    R     slur + an ethnic or religious context (antiracism lexicon, ethnonym, gloss)
    H     slur + a gloss about sexual orientation or gender identity
    S     slur + a gloss about women
    D     off + a disability context (handicap lexicon, gloss)
    X     a sexual sense (sexuality / pornography lexicon, a gloss about sexual acts or genitals) with a mark
          (« vulgaire », slang, familiar, pejorative, pornography lexicon) — owner 2026-11-27: sexual / obscene only
    V     « vulgaire » and not sexual (swear words, scatology): kept, never excluded by default
    G     drug
    I     off (strong: insult label; weak: « péjoratif » only)
"""
import argparse
import base64
import gzip
import hashlib
import json
import os
import re
import sqlite3
import sys
import unicodedata
from collections import Counter

CATS = "RHSDXVIG"
CAT_NAMES = {
    "R": "injure ethnique / raciale / religieuse",
    "H": "injure homophobe / transphobe",
    "S": "injure contre les femmes (sexisme)",
    "D": "injure validiste (handicap)",
    "X": "sexuel / obscène",
    "V": "vulgaire non sexuel (gardé)",
    "I": "insulte générale",
    "G": "drogues",
}
ALWAYS = set("RHS")          # slurs: also excluded when every sense is flagged or LDNOOBW lists them
PRIORITY = "RHSDXGVI"        # a lemma flagged in several categories takes the first one here
DOMINANCE = "DXVIG"
# Senses and entries that are not words a pupil writes: abbreviations, symbols, letters (a one-letter headword has
# vulgar abbreviation senses); one- and two-letter headwords are never proposed « exclude ».
SKIP_TAGS = {"abbreviation"}
SKIP_POS = {"symbol", "abbrev", "letter", "character", "prefix", "suffix", "infix", "affix"}
SHORT = 2
OLD_TAGS = {"obsolete", "archaic"}

# The label probe (0.1) keeps the labels whose name matches this pattern, so the console shows the relevant ones only.
PROBE = re.compile(r"injur|insult|vulga|péjor|pejor|offens|raci|xéno|xeno|ethn|antis|homoph|transph|sexis|misog|"
                   r"valid|handic|drog|drug|stupéf|sexu|sexe|érot|obsc|porn|grossi|argot|slang|derog|ableis|"
                   r"mépris|dénigr|religio|slur|familier|cannabis", re.I)

# --- label signals (lowercased label names) -------------------------------------------------------------------------
# A usage label (« Raciste », « Injurieux ») marks the register; a domain label (« Racisme », the antiracism lexicon) only
# says what the word is about, so the « -iste » labels match whole labels only.
STRONG_OFF = re.compile(r"^offensive$|^insulte|insultes en français|injurieu|^injure|^raciste$|^antisémite$|"
                        r"^xénophobe$|^homophobe$|^transphobe$|^sexist[e]$|^misogyne$|^validiste$|mépris|dénigrement|"
                        r"raillerie", re.I)
# Owner 2026-11-27: an R / H / S sense needs a real insult or slur label; « péjoratif » alone, « méprisant », « par
# dénigrement » or « par raillerie » are not enough (they made ordinary words look like slurs).
SLUR = re.compile(r"^offensive$|^insulte|insultant|insultes en français|injurieu|^injure|^raciste$|^antisémite$|"
                  r"^xénophobe$|^homophobe$|^transphobe$|^sexist[e]$|^misogyne$|parfois injurieux ou raciste", re.I)
WEAK_OFF = re.compile(r"^pejorative$|péjoratif|termes péjoratifs", re.I)
VULG = re.compile(r"^vulgar$|^vulgaire$|^très vulgaire$|termes vulgaires en français", re.I)
SEX = re.compile(r"^sexuality$|lexique en français de la sexualité|pornograph|fétichisme sexuel|positions sexuelles|"
                 r"^érotisme$|lexique en français de l’érotisme", re.I)
PORN = re.compile(r"pornograph|fétichisme sexuel|positions sexuelles", re.I)
REGISTER = re.compile(r"^slang$|^colloquial$|^familiar$|familier|argot|^very-familiar$|^pejorative$|péjoratif", re.I)
SLANG = re.compile(r"^slang$|^colloquial$|familier|argot", re.I)
DRUG = re.compile(r"^drogue$|lexique en français de la drogue|drogues en français|stupéfiant", re.I)
ETH_LABEL = re.compile(r"raciste|racisme|antisémit|xénophob|antiracisme|ethnonym|^ethnology$|ethnologie", re.I)
DIS_LABEL = re.compile(r"^handicap$|lexique en français du handicap|validis", re.I)
ANATOMY = re.compile(r"^anatomy$|anatomie|^medicine$|médecine|biologie|^biology$|physiologie|zoologie|botanique", re.I)

# --- gloss contexts (whom an insult targets; never printed) ---------------------------------------------------------
# The patterns name words that are themselves on the list, so they are stored base64-encoded (UTF-8), like the shipped
# file (owner answer Q4): not a secret, only kept out of sight in the source. Decode one with
#     python -I -c "import base64,sys; print(base64.b64decode(sys.argv[1]).decode())" <string>
# (to a file of the work folder, not to a shared console).


def _rx(b64):
    return re.compile(base64.b64decode(b64).decode("utf-8"), re.I)



ETH_GLOSS = _rx(  # peoples, origins, nationalities, religions
    "XGIoanVpZnxqdWl2ZXxqdWlmc3xhcmFiZXxhcmFiZXN8bm9pcnxub2lyZXxub2lyc3xhZnJpY2Fpbnxhc2lhdGlxdWV8Y2hpbm9p"
    "c3xtYWdocsOpYmlufG11c3VsbWFufGdpdGFufHRzaWdhbmV8dHppZ2FuZXxyb21hbmljaGVsfMOpdHJhbmdlcnzDqXRyYW5nw6hy"
    "ZXxpbW1pZ3Jcdyp8cmFjZXxyYWNpYWxcdyp8ZXRobmlcdyp8b3JpZ2luZXxjb3VsZXVyIGRlIHBlYXV8bcOpdGlzXHcqfGl0YWxp"
    "ZW58YWxsZW1hbmR8YW5nbGFpc3xhbcOpcmljYWlufHBvcnR1Z2Fpc3xlc3BhZ25vbHxwb2xvbmFpc3xiZWxnZXxzdWlzc2V8cXXD"
    "qWLDqWNvaXN8dmlldG5hbWllbnxpbmRvY2hpbm9pc3xqYXBvbmFpc3xjb3LDqWVufHR1cmN8aW5kaWVufGFtw6lyaW5kaWVufGF1"
    "dG9jaHRvbmV8Y2F0aG9saXF1ZXxwcm90ZXN0YW50fGNocsOpdGllbnxyZWxpZ2lcdyp8bmF0aW9uYWxpdMOpfHBldXBsZXxibGFu"
    "Y3xibGFuY2hlfG5vcmQtYWZyaWNhaW58YW50aWxsYWlzfHLDqXVuaW9ubmFpc3xjb3JzZXxicmV0b258YXV2ZXJnbmF0fHByb3Zp"
    "bmNpYWwpXGI=")

HOM_GLOSS = _rx(  # sexual orientation, gender identity
    "aG9tb3NleHVcdyp8XGJnYXlzP1xifFxiZ2FpZT9zP1xifGxlc2JpZW5uXHcqfHRyYW5zZ2VucmVcdyp8XGJ0cmFuc1xifHRyYW5z"
    "c2V4dVx3Knx0cmF2ZXN0aVx3KnxlZmbDqW1pbsOpXHcqfGJpc2V4dVx3KnxcYmludmVydGl8cMOpZMOpcmFzdGVcdyp8c29kb21p"
    "dGU=")

WOM_GLOSS = _rx(  # women
    "XGJmZW1tZXM/XGJ8XGJmaWxsZXM/XGJ8XGLDqXBvdXNlfHByb3N0aXR1XHcqfFxibWHDrnRyZXNzZQ==")

DIS_GLOSS = _rx(  # disabilities, mental illness
    "aGFuZGljYXBcdyp8ZMOpZmljaWVuXHcqfG1lbnRhbGVtZW50fG1hbGFkaWUgbWVudGFsZXxhdHRhcmTDqXxyZXRhcmTDqXx0cmlz"
    "b21cdyp8aW5maXJtXHcqfGF2ZXVnbGV8XGJzb3VyZHM/XGJ8XGJtdWV0cz9cYnxib2l0ZXVcdyp8YXV0aXNcdyp8YWxpw6luw6l8"
    "XGJuYWlucz9cYnxwYXJhbHlzXHcqfGVzdHJvcGlcdyp8Ym9zc3V8aW52YWxpZGV8c2NoaXpvXHcqfHBzeWNoaWF0clx3Kg==")

DRUG_GLOSS = _rx(  # drugs and their trade
    "XGJkcm9ndWV8c3R1cMOpZmlhbnR8Y2FubmFiaXN8aGFzY2hpY2h8aGFzY2hpc2NofGNvY2HDr25lfGjDqXJvw69uZXxtYXJpanVh"
    "bmF8XGJqb2ludCBkZVxifFxiZGVhbGVyfGRlYWxldXJ8dG94aWNvbWFuXHcq")

SEX_GLOSS = _rx(  # sexual acts, genitals (not buttocks: scatology is « vulgaire »)
    "YWN0ZSBzZXh1ZWx8cmFwcG9ydCBzZXh1ZWx8cmVsYXRpb24gc2V4dWVsbGV8XGJjb8OvdHxtYXN0dXJiXHcqfG9yZ2FuZXM/IGfD"
    "qW5pdFx3KnxcYnDDqW5pc3xcYnZhZ2lufFxidnVsdmV8XGJ0ZXN0aWN1bFx3KnxcYnNwZXJtZXxcYmZlbGxhdGlvbnxcYnNvZG9t"
    "aVx3Knxwcm9zdGl0dVx3Knxjb3B1bFx3KnxcYnNlaW5zP1xifGZhaXJlIGzigJlhbW91cnxleGNpdGF0aW9uIHNleHVlbGxl")


def norm(s):
    return unicodedata.normalize("NFC", s).strip()


def entries(path):
    """French-language entries of the wiktextract JSONL (plain or .gz)."""
    opener = gzip.open if path.endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8") as f:
        for line in f:
            if '"lang_code": "fr"' not in line and '"lang_code":"fr"' not in line:
                continue
            o = json.loads(line)
            if o.get("lang_code") == "fr" and o.get("word"):
                yield o


def _labels(obj, keys):
    out = []
    for k in keys:
        for v in obj.get(k, []) or []:
            if isinstance(v, dict):
                v = v.get("name", "")
            if isinstance(v, str) and v:
                out.append(v.strip())
    return out


def sense_labels(s):
    return _labels(s, ("tags", "raw_tags", "topics", "categories"))


def entry_labels(o):
    return _labels(o, ("tags", "raw_tags", "categories"))


def signals(labels, glosses):
    """The signal set of one sense (see the module doc)."""
    low = [l.lower() for l in labels]
    text = " ".join(glosses)

    def any_(rx):
        return any(rx.search(l) for l in low)

    sig = set()
    if any_(SLUR):
        sig.add("slur")
    if any_(STRONG_OFF):
        sig.add("off")
    elif any_(WEAK_OFF):
        sig.add("pej")
    if any_(VULG):
        sig.add("vulg")
    if any_(SEX):
        sig.add("sex")
    if any_(PORN):
        sig.add("porn")
    if any_(REGISTER):
        sig.add("reg")
    if any_(DRUG) or (any_(SLANG) and DRUG_GLOSS.search(text)):
        sig.add("drug")
    if SEX_GLOSS.search(text):
        sig.add("sexg")
    if any_(ANATOMY):
        sig.add("anat")
    if any_(ETH_LABEL) or ETH_GLOSS.search(text):
        sig.add("eth")
    if HOM_GLOSS.search(text):
        sig.add("hom")
    if WOM_GLOSS.search(text):
        sig.add("wom")
    if any_(DIS_LABEL) or DIS_GLOSS.search(text):
        sig.add("dis")
    return sig


def classify(sig):
    """(category, strong) of one sense; ('', False) when not flagged."""
    off = "off" in sig or "pej" in sig
    if "slur" in sig and "eth" in sig:
        return "R", True
    if "slur" in sig and "hom" in sig:
        return "H", True
    if "slur" in sig and "wom" in sig:
        return "S", True
    if off and "dis" in sig:
        return "D", True
    # owner 2026-11-27: X is sexual / obscene only (sexual acts, genitals, pornography); a vulgar word that is not
    # sexual is V « vulgaire » (swear words, scatology), kept; a familiar register is never a reason to exclude
    sexual = "sex" in sig or "porn" in sig or "sexg" in sig
    marked = "vulg" in sig or "reg" in sig or "porn" in sig
    if sexual and marked and not ("anat" in sig and "vulg" not in sig and "reg" not in sig):
        return "X", True
    if "drug" in sig:
        return "G", True
    if "vulg" in sig:
        return "V", True
    if "off" in sig:
        return "I", True
    if "pej" in sig:
        return "I", False
    return "", False


def cmd_probe(a):
    by_kind = {k: Counter() for k in ("sense.tags", "sense.raw_tags", "sense.topics", "sense.categories",
                                      "entry.tags", "entry.raw_tags", "entry.categories")}
    n_entries = n_senses = 0
    for o in entries(a.wikt):
        n_entries += 1
        for k in ("tags", "raw_tags", "categories"):
            for v in o.get(k, []) or []:
                if isinstance(v, str):
                    by_kind["entry." + k][v] += 1
        for s in o.get("senses", []) or []:
            n_senses += 1
            for k in ("tags", "raw_tags", "topics", "categories"):
                for v in s.get(k, []) or []:
                    if isinstance(v, str):
                        by_kind["sense." + k][v] += 1
    print("french entries: %d, senses: %d" % (n_entries, n_senses))
    for kind, c in by_kind.items():
        rel = [(v, n) for v, n in c.items() if PROBE.search(v) or a.all]
        rel.sort(key=lambda x: -x[1])
        print("== %s: %d distinct labels, %d relevant" % (kind, len(c), len(rel)))
        for v, n in rel[: a.top]:
            # a raw tag can be free text (an example, a note) holding a listed word: the console gets a label id and
            # the signals the label triggers; the names go to --out, a file of the work folder
            sig = ",".join(sorted(signals([v], []))) or "-"
            print("  %7d  L%s  %s" % (n, label_id(v), sig))
    if a.out:
        with open(a.out, "w", encoding="utf-8", newline="\n") as f:
            f.write("kind\tlabel_id\tcount\tlabel\n")
            for kind, c in by_kind.items():
                for v, n in sorted(c.items(), key=lambda x: -x[1]):
                    if PROBE.search(v) or a.all:
                        f.write("%s\t%s\t%d\t%s\n" % (kind, label_id(v), n, " ".join(v.split())))
        print("label names written to the --out file")


def label_id(v):
    return hashlib.sha256(v.encode("utf-8")).hexdigest()[:8]


def load_lexicon(db):
    """lemma -> summed freq of its forms; form -> its freq; for the ``words`` of prediction.sqlite."""
    c = sqlite3.connect("file:%s?mode=ro" % db, uri=True)
    lemmas, forms = {}, {}
    for ortho, lemma, freq in c.execute("SELECT ortho, lemma, freq FROM words"):
        f = freq or 0.0
        if lemma:
            lemmas[lemma] = lemmas.get(lemma, 0.0) + f
        if ortho:
            forms[ortho] = forms.get(ortho, 0.0) + f
    c.close()
    return lemmas, forms


def load_ldnoobw(path):
    out = set()
    with open(path, encoding="utf-8") as f:
        for line in f:
            w = norm(line).lower()
            if w and " " not in w:
                out.add(w)
    return out


def decide(cat, strong, total, flagged, first_flagged, seed, freq=0.0):
    """The proposed decision and reason of one candidate (policy 0.3 with the owner's « exclude all »)."""
    dominant = first_flagged or (total > 0 and 2 * flagged >= total)
    share = "%d/%d senses flagged%s" % (flagged, total, ", first flagged" if first_flagged else "")
    if cat == "V":
        return "keep", "V: vulgar, not sexual — kept (owner 2026-11-27; %s)" % share
    if cat in ALWAYS:
        # owner, 2026-11-27: « whatever the other senses » gave about half false positives on the first 50 R rows
        if total > 0 and flagged >= total:
            return "exclude", "%s: every sense flagged (%s)" % (cat, share)
        if dominant:
            return "exclude", "%s: dominant sense is the slur (%s)" % (cat, share)
        if seed:
            return "exclude", "%s: LDNOOBW lists it (%s)" % (cat, share)
        return "keep", "%s: innocent sense dominates (%s)" % (cat, share)
    if cat == "I" and not strong:
        if dominant and seed:
            return "exclude", "I: pejorative sense dominates and LDNOOBW lists it (%s)" % share
        return "keep", "I: pejorative label only, no insult label (%s; review)" % share
    if dominant:
        return "exclude", "%s: dominant sense flagged (%s)" % (cat, share)
    return "keep", "%s: innocent sense dominates (%s)" % (cat, share)


def collect(path, lemmas, forms):
    """headword -> aggregated sense statistics, for the headwords that are a lemma or a form of the lexicon.

    Inflection senses (« pluriel de … », ``form_of``) are skipped: the lemma carries the labels. Variant senses
    (« variante de … », ``alt_of``) are kept, since a variant spelling carries its own labels; a flagged variant takes
    the R / H / S / D category of its target when the target has one (its gloss names only the target)."""
    agg, target_cats = {}, {}
    for o in entries(path):
        w = norm(o["word"])
        if o.get("pos") in SKIP_POS:
            continue
        senses = [s for s in (o.get("senses") or []) if not s.get("form_of") and s.get("glosses")
                  and not SKIP_TAGS.intersection(s.get("tags") or ())]
        if not senses:
            continue
        inlex = w in lemmas or w in forms
        el = entry_labels(o)
        rec = agg.setdefault(w, {"total": 0, "cats": Counter(), "strong": Counter(), "first": "", "nflag": 0,
                                 "sig": Counter(), "entry_only": "", "alt": set()}) if inlex else None
        if len(senses) > 1:
            # A page-level label of a multi-sense entry belongs to no sense in particular: a hint only.
            c, _ = classify(signals(el, []))
            if rec is not None and c and not rec["entry_only"]:
                rec["entry_only"] = c
            el = []
        for i, s in enumerate(senses):
            sig = signals(sense_labels(s) + el, [g for g in s.get("glosses") or [] if isinstance(g, str)])
            c, strong = classify(sig)
            if c in DOMINANCE and OLD_TAGS.intersection(s.get("tags") or ()):
                c, strong = "", False        # an obsolete obscene / insult / drug sense is not a current sense
            if c in "RHSD" and c:
                target_cats.setdefault(w, set()).add(c)
            if rec is None:
                continue
            if rec["total"] == 0 and i == 0:
                rec["first"] = c
            rec["total"] += 1
            if c:
                rec["cats"][c] += 1
                rec["nflag"] += 1
                if strong:
                    rec["strong"][c] += 1
                rec["sig"].update(sig)
                for t in (s.get("alt_of") or []) if "slur" in sig else []:
                    tw = t.get("word") if isinstance(t, dict) else t
                    if isinstance(tw, str) and tw:
                        rec["alt"].add((norm(tw), c))
    upgraded = 0
    for w, rec in agg.items():
        for tw, c in rec["alt"]:
            tc = [x for x in PRIORITY if x in target_cats.get(tw, ())]
            if tc and PRIORITY.index(tc[0]) < PRIORITY.index(c):
                rec["cats"][c] -= 1
                rec["strong"][c] -= 1 if rec["strong"][c] > 0 else 0
                rec["cats"][tc[0]] += 1
                rec["strong"][tc[0]] += 1
                rec["sig"]["alt"] += 1
                upgraded += 1
                if rec["first"] == c:
                    rec["first"] = tc[0]
    collect.upgraded = upgraded
    return agg


def cmd_candidates(a):
    lemmas, forms = load_lexicon(a.db)
    seed = load_ldnoobw(a.ldnoobw) if a.ldnoobw else set()
    agg = collect(a.wikt, lemmas, forms)
    rows = []
    for w, rec in agg.items():
        if not rec["nflag"]:
            if rec["entry_only"]:
                rows.append([w, rec["entry_only"], rec["total"], 0, 0, _freq(w, lemmas, forms), _inlex(w, lemmas),
                             int(w.lower() in seed), "keep",
                             "%s: page-level label only, no sense labelled (review)" % rec["entry_only"],
                             "wiktionnaire", "entry"])
            continue
        cat = next(c for c in PRIORITY if rec["cats"][c] > 0)
        strong = rec["strong"][cat] > 0 or cat != "I"
        insw = int(w.lower() in seed)
        dec, reason = decide(cat, strong, rec["total"], rec["nflag"], bool(rec["first"]), insw, _freq(w, lemmas, forms))
        if len(w) <= SHORT and dec == "exclude":
            dec, reason = "keep", "%s: letter or two-letter word, flagged sense is a symbol or abbreviation (review)" % cat
        sig = ",".join(k for k in ("slur", "off", "pej", "vulg", "sex", "porn", "reg", "drug", "sexg", "eth", "hom", "wom", "dis",
                                   "anat", "alt") if rec["sig"][k])
        rows.append([w, cat, rec["total"], rec["nflag"], int(bool(rec["first"])), _freq(w, lemmas, forms),
                     _inlex(w, lemmas), insw, dec, reason, "wiktionnaire", sig])
    have = {r[0].lower() for r in rows}
    seed_only = 0
    for w in sorted(seed):
        if w in have or (w not in lemmas and w not in forms):
            continue
        seed_only += 1
        rows.append([w, "I", agg.get(w, {}).get("total", 0), 0, 0, _freq(w, lemmas, forms), _inlex(w, lemmas), 1,
                     "keep", "I: LDNOOBW seed only, no Wiktionnaire label (review)", "ldnoobw", ""])
    rows.sort(key=lambda r: (CATS.index(r[1]), r[8], r[0]))
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    head = ["id", "lemma", "cat", "senses_total", "senses_flagged", "first_sense_flagged", "lexique_freq",
            "in_lexicon", "ldnoobw", "decision", "reason", "source", "signals"]
    ids = stable_ids(rows, a.previous if a.previous is not None else a.out)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write("\t".join(head) + "\n")
        for i, r in zip(ids, rows):
            f.write("\t".join([i] + [str(x) for x in r]) + "\n")
    print("ids: %d kept from the previous candidates file, %d new" % (stable_ids.kept, len(rows) - stable_ids.kept))
    # Counts only.
    print("lexicon: %d lemmas, %d forms; wiktionnaire headwords matched: %d; candidates: %d" % (
        len(lemmas), len(forms), len(agg), len(rows)))
    print("variant senses given their target's category: %d" % getattr(collect, "upgraded", 0))
    print("in_lexicon: lemma %d, form only %d" % (sum(r[6] == "lemma" for r in rows), sum(r[6] == "form" for r in rows)))
    print("ldnoobw: %d words, in lexicon %d, among labelled candidates %d, seed-only rows %d" % (
        len(seed), sum(1 for w in seed if w in lemmas or w in forms), sum(1 for r in rows if r[7] and r[10] ==
                                                                          "wiktionnaire"), seed_only))
    print("cat  candidates  exclude  keep")
    for c in CATS:
        rs = [r for r in rows if r[1] == c]
        print("  %s  %10d  %7d  %4d" % (c, len(rs), sum(r[8] == "exclude" for r in rs),
                                       sum(r[8] == "keep" for r in rs)))
    print("total %9d  %7d  %4d" % (len(rows), sum(r[8] == "exclude" for r in rows), sum(r[8] == "keep" for r in rows)))


def stable_ids(rows, previous):
    """Row ids: a headword keeps the id it had in the previous candidates file (the owner's review cites ids); a new
    headword gets the next free number. Without a previous file, ids follow the row order."""
    old = {}
    if previous and os.path.exists(previous):
        with open(previous, encoding="utf-8") as f:
            next(f, None)
            for line in f:
                parts = line.rstrip("\n").split("\t")
                if len(parts) > 1:
                    old[parts[1]] = parts[0]
    top = max([int(i[1:]) for i in old.values() if i[1:].isdigit()] + [0])
    out, kept = [], 0
    for r in rows:
        if r[0] in old:
            out.append(old[r[0]])
            kept += 1
        else:
            top += 1
            out.append("C%04d" % top)
    stable_ids.kept = kept
    return out


def _freq(w, lemmas, forms):
    return round(lemmas.get(w, forms.get(w, 0.0)), 2)


def _inlex(w, lemmas):
    return "lemma" if w in lemmas else "form"


def main(argv=None):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = p.add_subparsers(dest="cmd", required=True)
    pr = sub.add_parser("probe")
    pr.add_argument("--wikt", required=True)
    pr.add_argument("--top", type=int, default=60)
    pr.add_argument("--all", action="store_true", help="every label, not only the relevant ones")
    pr.add_argument("--out", help="a work-folder file for the label names (never printed)")
    ca = sub.add_parser("candidates")
    ca.add_argument("--wikt", required=True)
    ca.add_argument("--ldnoobw")
    ca.add_argument("--db", required=True)
    ca.add_argument("--out", required=True)
    ca.add_argument("--previous", help="an earlier candidates.tsv whose ids are kept (default: the --out file; '' = none)")
    a = p.parse_args(argv)
    {"probe": cmd_probe, "candidates": cmd_candidates}[a.cmd](a)
    return 0


if __name__ == "__main__":
    sys.exit(main())
