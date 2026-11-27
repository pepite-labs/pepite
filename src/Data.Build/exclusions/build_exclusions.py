#!/usr/bin/env python3
"""The « mots exclus » list: reviewed lemmas -> excluded forms -> shipped file (plan offensive-words, 0.4–0.6).

**This script never prints a word**: the console gets counts and row ids (``C…`` lemma rows, ``F…`` form rows) only.
The words live in the committed ``lemmas.tsv`` / ``forms.tsv`` next to this script and in the shipped file.

Sub-commands (run with ``python -I``; standard library only):

    decisions --candidates <work>/exclusions/candidates.tsv --decisions FILE --out lemmas.tsv --reviewed DATE
        merges the owner's review (the JSON or TSV exported by review.html) into ``lemmas.tsv``
        (``id lemma cat decision reason source``); every candidate id must have a decision.
    decisions-from-text --candidates <work>/exclusions/candidates.tsv --text "garder: C0055 ; exclure: C1234" --out FILE.json --reviewed DATE
        the owner's answer to the text batches (a phone review): the proposed decisions with these ids changed, written
        as the same decisions JSON as the review page's export (``--text-file`` reads the answer from files).
    forms --lemmas lemmas.tsv --db out/prediction.sqlite --grammar out/grammar.sqlite --out forms.tsv
        expands the excluded lemmas to their forms and writes ``forms.tsv`` (``id form lemma_id cat``), sorted and
        byte-reproducible, with the forms of the prediction lexicon only (Lexique 3.83, CC BY-SA 4.0): a form found only
        in grammar.sqlite (French.lex, MPL-2.0) is left to the engine, which expands the excluded lemmas from
        grammar.sqlite at load time. Homograph guard: a form is excluded only when **every** lemma it has in
        prediction.sqlite and grammar.sqlite is an excluded lemma (an excluded headword whose own spelling the guard
        keeps stays offered). Fold twins (accents, case, « œ ») of an excluded form are added only when every lemma
        they have is excluded or their only lemma folds like the excluded lemma (``--no-twins`` turns this off). Rows
        ``form+`` / ``form-`` of ``lemmas.tsv`` add or remove one form (its ``lemma`` column holds the form).
        Reports by id only: (a) excluded lemmas left with no form, (b) « keep » rows whose spelling is excluded,
        (c) excluded lemmas whose own spelling the guard keeps — each split into open rows and rows the owner has
        answered (``form-answers``) — and the excluded forms with a not-excluded twin.
    ship --forms forms.tsv --lemmas lemmas.tsv --out-dir out/exclusions --reviewed DATE [--db … --grammar …]
        writes ``fr.txt`` (header line, the counts per category, then one form per line, each base64 of its UTF-8 — owner answer Q4: so that a
        child browsing the install folder does not read a list of slurs; this is not a secret, the plain list is
        ``forms.tsv`` in the source repository) and ``SOURCE.txt`` (credits, review date, SHA-256 of ``forms.tsv`` and
        of the lexicon databases it was expanded from, counts per category).
    questions --lemmas lemmas.tsv --db … --grammar … --candidates <work>/exclusions/candidates.tsv --out FILE
        the owner's questions on the forms (a), (b), (c) and the rows to confirm, with the words: the file must stay in
        the work folder; the console gets counts only.
    form-answers --questions FILE --answers FILE --reviewed DATE
        applies the owner's answers (« Q03 exclure la forme ») to ``lemmas.tsv`` as ``form+`` / ``form-`` rows (id
        ``Qnn``) or decision changes, with the reason « owner DATE Qnn »; idempotent; then run ``forms`` again.
"""
import argparse
import base64
import csv
import hashlib
import json
import os
import re
import sqlite3
import sys
import unicodedata
from collections import Counter

CATS = "RHSDXVIG"
DECISIONS = ("exclude", "keep", "form+", "form-", "name-ok")   # name-ok: owner 2026-11-27, see ship
LEMMA_HEAD = ["id", "lemma", "cat", "decision", "reason", "source"]
FORM_HEAD = ["id", "form", "lemma_id", "cat"]
HEADER = "#pepite-exclusions/1 base64-utf8"
HERE = os.path.dirname(os.path.abspath(__file__))


def norm(s):
    return unicodedata.normalize("NFC", s).strip()


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def fold(s):
    """Accents, case and the « œ » / « æ » ligatures folded: a twin spelling the review may want to list too."""
    s = s.lower().replace("œ", "oe").replace("æ", "ae")
    return "".join(c for c in unicodedata.normalize("NFD", s) if not unicodedata.combining(c))


def read_tsv(path):
    with open(path, encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f, delimiter="\t", quoting=csv.QUOTE_NONE))


def write_tsv(path, head, rows):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\t".join(head) + "\n")
        for r in rows:
            vals = [str(r[k]) for k in head]
            if any("\t" in v or "\n" in v for v in vals):
                raise ValueError("tab or newline in row %s" % r.get("id"))
            f.write("\t".join(vals) + "\n")


# --- decisions -------------------------------------------------------------------------------------------------------

def load_decisions(path):
    """id -> {decision, cat?, note} from the review page's export (JSON) or a TSV ``id decision [cat] [note]``."""
    if path.lower().endswith(".json"):
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        if data.get("format") != "pepite-exclusions-review":
            raise ValueError("not a review export (format field)")
        items = data.get("decisions", [])
        meta = {k: data.get(k) for k in ("candidates_sha256", "exported", "reviewer")}
    else:
        items = read_tsv(path)
        meta = {}
    out = {}
    for it in items:
        i = (it.get("id") or "").strip()
        d = (it.get("decision") or "").strip()
        if not i:
            continue
        if d not in ("exclude", "keep"):
            raise ValueError("row %s: decision must be exclude or keep" % i)
        out[i] = {"decision": d, "cat": (it.get("cat") or "").strip(), "note": norm(it.get("note") or "")}
    return out, meta


def cmd_decisions(a):
    cands = read_tsv(a.candidates)
    dec, meta = load_decisions(a.decisions)
    if meta.get("candidates_sha256") and meta["candidates_sha256"] != sha256_file(a.candidates):
        raise SystemExit("the review was made on another candidates.tsv (SHA-256 differs)")
    ids = {c["id"] for c in cands}
    missing = [c["id"] for c in cands if c["id"] not in dec]
    unknown = sorted(set(dec) - ids)
    if missing or unknown:
        raise SystemExit("decisions: %d candidate ids without a decision, %d unknown ids (first: %s)" % (
            len(missing), len(unknown), ", ".join((missing + unknown)[:10])))
    rows, changed = [], 0
    for c in cands:
        d = dec[c["id"]]
        cat = d["cat"] or c["cat"]
        if cat not in CATS:
            raise SystemExit("row %s: unknown category" % c["id"])
        if d["decision"] == c["decision"] and cat == c["cat"]:
            reason = c["reason"] + ("; owner: " + d["note"] if d["note"] else "")
        else:
            changed += 1
            reason = "owner: %s (proposed %s %s)" % (d["note"] or "changed", c["cat"], c["decision"])
        rows.append({"id": c["id"], "lemma": c["lemma"], "cat": cat, "decision": d["decision"],
                     "reason": reason.replace("\t", " "), "source": "%s; reviewed %s" % (c["source"], a.reviewed)})
    # form+ / form- rows already in an existing lemmas.tsv are kept (they are edited by hand after the review).
    if os.path.exists(a.out):
        rows += [r for r in read_tsv(a.out) if r["decision"] in ("form+", "form-", "name-ok")]
    write_tsv(a.out, LEMMA_HEAD, rows)
    n = Counter((r["cat"], r["decision"]) for r in rows)
    print("lemmas.tsv: %d rows, %d changed by the owner" % (len(rows), changed))
    for c in CATS:
        print("  %s  exclude %4d  keep %4d" % (c, n[(c, "exclude")], n[(c, "keep")]))
    return 0


TEXT_KEYS = {"garder": "keep", "garde": "keep", "keep": "keep", "exclure": "exclude", "exclus": "exclude",
             "exclude": "exclude"}


def parse_overrides(text):
    """« garder: C0055, C0089 ; exclure: C1234 » -> {id: decision}. Each id follows the last keyword seen; an id given
    two different decisions is refused."""
    out, current = {}, None
    for tok in re.findall(r"\bC\d{4,}\b|[A-Za-zÀ-ÿ]+", text):
        low = tok.lower()
        if low in TEXT_KEYS:
            current = TEXT_KEYS[low]
        elif re.fullmatch(r"C\d{4,}", tok):
            if current is None:
                raise ValueError("id %s before any « garder: » / « exclure: »" % tok)
            if out.get(tok, current) != current:
                raise ValueError("id %s is both kept and excluded" % tok)
            out[tok] = current
    return out


def cmd_decisions_from_text(a):
    cands = read_tsv(a.candidates)
    text = a.text if a.text is not None else ""
    for p in a.text_file or []:
        with open(p, encoding="utf-8") as f:
            text += "\n" + f.read()
    over = parse_overrides(text)
    ids = {c["id"] for c in cands}
    unknown = sorted(set(over) - ids)
    # a « garder » id that is no longer a candidate (the policy changed) is moot: the word is not excluded anyway
    moot = [] if a.strict else [i for i in unknown if over[i] == "keep"]
    unknown = [i for i in unknown if i not in moot]
    if unknown:
        raise SystemExit("decisions-from-text: %d unknown ids (%s)" % (len(unknown), ", ".join(unknown[:10])))
    if moot:
        print("« garder » ids no longer candidates (nothing to do): %d (%s)" % (len(moot), ", ".join(moot)))
    note = a.note or "owner review %s (text answer)" % a.reviewed
    decisions, changed = [], 0
    for c in cands:
        d = over.get(c["id"], c["decision"])
        changed += d != c["decision"]
        decisions.append({"id": c["id"], "decision": d, "proposed": c["decision"], "cat": c["cat"],
                          "note": note if c["id"] in over and d != c["decision"] else ""})
    data = {"format": "pepite-exclusions-review", "version": 1, "candidates_sha256": sha256_file(a.candidates),
            "exported": a.reviewed, "source": "decisions-from-text", "decisions": decisions}
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, indent=1)
        f.write("\n")
    n = Counter((c["cat"], d["decision"]) for c, d in zip(cands, decisions))
    print("decisions JSON: %d rows, %d overrides given, %d differ from the proposal" % (len(decisions), len(over),
                                                                                        changed))
    for c in CATS:
        print("  %s  exclude %4d  keep %4d" % (c, n[(c, "exclude")], n[(c, "keep")]))
    return 0


# --- forms -----------------------------------------------------------------------------------------------------------

def lexicon_maps(db, grammar):
    """(form -> lemmas, lemma -> forms) over prediction.sqlite ``words`` and grammar.sqlite ``morph``."""
    f2l, l2f = {}, {}

    def add(form, lemma):
        if form and lemma:
            f2l.setdefault(form, set()).add(lemma)
            l2f.setdefault(lemma, set()).add(form)

    c = sqlite3.connect("file:%s?mode=ro" % db, uri=True)
    for form, lemma in c.execute("SELECT ortho, lemma FROM words"):
        add(form, lemma)
    c.close()
    lex_forms = set(f2l)
    if grammar:
        g = sqlite3.connect("file:%s?mode=ro" % grammar, uri=True)
        for form, lemma in g.execute("SELECT m.form, l.lemma FROM morph m JOIN lemma l ON l.id = m.lemma_id"):
            add(form, lemma)
        g.close()
    return f2l, l2f, lex_forms


ROW_REF = re.compile(r"\(row (C\d{4,})\)")


def row_ref(r):
    """The lemma row an owner answer row (``Qnn``, « … (row C0123) » in its reason) is about; else the row's own id."""
    m = ROW_REF.search(r.get("reason") or "") if re.fullmatch(r"Q\d{2}", r.get("id", "")) else None
    return m.group(1) if m else r.get("id")


def expand(lemma_rows, f2l, l2f, lex_forms=None, twins=True):
    """forms.tsv rows (without ids) and the report.

    Homograph guard (plan 0.5): a form is excluded only when **every** lemma it has in both sources (prediction.sqlite
    and grammar.sqlite) is an excluded lemma; an excluded headword whose own spelling the guard keeps stays offered
    and is reported (``own_guarded``) for an owner ``form+`` decision. Only the forms of ``lex_forms`` (the prediction
    lexicon, Lexique 3.83, CC BY-SA 4.0) are written: a grammar-only form (French.lex, MPL-2.0) is left to the engine,
    which expands the excluded lemmas from grammar.sqlite at load time (plan A1/A2).

    Twin spellings (``twins``, reviewer 2026-11-27): a lexicon form with the same :func:`fold` key (accents, case,
    « œ ») as a form excluded by the expansion is excluded too only when every lemma it has is excluded, or when its
    only lemma folds like the excluded lemma (a true spelling variant). Forms added by ``form+`` never seed twins."""
    excl, kept, by_id = {}, set(), {}
    for r in lemma_rows:
        by_id[r["id"]] = r
        if r["decision"] == "exclude":
            excl.setdefault(r["lemma"], r)
        elif r["decision"] == "keep":
            kept.add(r["lemma"])
    lex = set(f2l) if lex_forms is None else set(lex_forms)
    out, own_guarded, guarded, dropped = {}, [], 0, set()
    for lemma, r in excl.items():
        for f in set(l2f.get(lemma, ())) | {lemma}:
            lemmas = f2l.get(f) or {lemma}
            if all(x in excl for x in lemmas):
                if f not in lex:
                    dropped.add(f)
                    continue
                owner = min((excl[x] for x in lemmas), key=lambda q: q["id"])   # the first listed lemma owns it
                out[f] = (owner["id"], owner["cat"])
            else:
                guarded += 1
                if f == lemma:
                    own_guarded.append(r["id"])
    twins_added, twins_kept = 0, 0
    if twins:
        by_key = {}
        for f in lex:
            if f not in out:
                by_key.setdefault(fold(f), []).append(f)
        for f, (oid, ocat) in sorted(out.items()):
            for t in sorted(by_key.get(fold(f), ())):
                if t in out:
                    continue
                lemmas = f2l.get(t) or set()
                if t in kept or any(x in kept for x in lemmas):
                    twins_kept += 1                     # a word kept in the review is never a twin
                elif lemmas and all(x in excl for x in lemmas):
                    owner = min((excl[x] for x in lemmas), key=lambda q: q["id"])
                    out[t] = (owner["id"], owner["cat"])
                    twins_added += 1
                elif len(lemmas) == 1 and fold(next(iter(lemmas))) == fold(by_id[oid]["lemma"]):
                    out[t] = (oid, ocat)
                    twins_added += 1
                else:
                    twins_kept += 1
    added = removed = 0
    for r in lemma_rows:
        if r["decision"] == "form+":
            out[r["lemma"]] = (r["id"], r["cat"])
            added += 1
        elif r["decision"] == "form-":
            if out.pop(r["lemma"], None) is not None:
                removed += 1
    rows = [{"form": f, "lemma_id": i, "cat": c} for f, (i, c) in out.items()]
    rows.sort(key=lambda x: (x["form"], x["lemma_id"]))
    for n, x in enumerate(rows, 1):
        x["id"] = "F%05d" % n
    # an owner « form+ » row (id ``Qnn``) answers for the lemma row named in its reason: that row has a form now, and
    # its spelling, once excluded by the answer, is no longer « guarded » nor a « keep » row to ask about
    with_forms = {row_ref(by_id.get(x["lemma_id"]) or {"id": x["lemma_id"]}) for x in rows}
    # a form excluded through another lemma that has it too (« the first listed lemma owns it ») is a form of each
    with_forms |= {r["id"] for r in excl.values() if any(f in out for f in set(l2f.get(r["lemma"], ())) | {r["lemma"]})}
    # rows the owner has answered about (``form-answers``: a ``Qnn`` row naming them, or a « owner DATE Qnn: » note)
    answered = {row_ref(r) for r in lemma_rows if re.fullmatch(r"Q\d{2}", r["id"])}
    answered |= {r["id"] for r in lemma_rows if re.search(r"owner \S+ Q\d{2}:", r.get("reason") or "")}
    # excluded lemmas whose forms are all grammar-only: the engine excludes them at load time, nothing to ask
    engine_only = {r["id"] for r in excl.values()
                   if any(f in dropped for f in set(l2f.get(r["lemma"], ())) | {r["lemma"]})}
    rep = {
        "excluded_lemmas": len(excl), "guarded": guarded,
        "own_guarded": sorted(i for i in set(own_guarded) if by_id[i]["lemma"] not in out),
        "added": added, "removed": removed, "twins_added": twins_added, "twins_kept": twins_kept,
        "grammar_only_dropped": len(dropped),
        # (a) excluded lemmas that produce no form at all (their spellings are all forms of other words)
        "no_form": sorted(r["id"] for r in excl.values() if r["id"] not in with_forms),
        # (b) reviewed « keep » rows whose own spelling is excluded through a form of an excluded lemma
        "kept_but_excluded": sorted(by_id_r["id"] for by_id_r in lemma_rows
                                    if by_id_r["decision"] == "keep" and by_id_r["lemma"] in out),
        "answered": answered, "engine_only": engine_only,
    }
    return rows, rep


def fold_twins(rows, lex_forms):
    excluded = {r["form"] for r in rows}
    by_key = {}
    for f in lex_forms:
        if f not in excluded:
            by_key.setdefault(fold(f), 0)
            by_key[fold(f)] += 1
    return [r["id"] for r in rows if by_key.get(fold(r["form"]))]


def cmd_forms(a):
    lemma_rows = read_tsv(a.lemmas)
    bad = [r["id"] for r in lemma_rows if r["decision"] not in DECISIONS or r["cat"] not in CATS]
    if bad:
        raise SystemExit("lemmas.tsv: %d rows with an unknown decision or category (first: %s)" % (
            len(bad), ", ".join(bad[:10])))
    f2l, l2f, lex_forms = lexicon_maps(a.db, a.grammar)
    rows, rep = expand(lemma_rows, f2l, l2f, lex_forms, not a.no_twins)
    write_tsv(a.out, FORM_HEAD, rows)
    twins = fold_twins(rows, lex_forms)
    n = Counter(r["cat"] for r in rows)

    def ids(xs):
        return (" (" + ", ".join(xs[:60]) + ("…" if len(xs) > 60 else "") + ")") if xs else ""

    print("forms.tsv: %d forms from %d excluded lemmas (form+ %d, form- %d); sha256 %s" % (
        len(rows), rep["excluded_lemmas"], rep["added"], rep["removed"], sha256_file(a.out)))
    print("  per category: " + ", ".join("%s %d" % (c, n[c]) for c in CATS))
    print("  inputs: prediction.sqlite sha256 %s%s" % (sha256_file(a.db), (
        "; grammar.sqlite sha256 %s" % sha256_file(a.grammar)) if a.grammar else ""))
    print("grammar-only forms left to the engine (not written): %d" % rep["grammar_only_dropped"])
    print("fold twins excluded: %d; twins kept (another, unexcluded word): %d" % (rep["twins_added"], rep["twins_kept"]))
    print("homograph guard kept %d forms" % rep["guarded"])
    def split(xs, also=()):
        """« N — open n (ids); answered by the owner n (ids)[; …] »: an answered row is no longer a question."""
        op = [x for x in xs if x not in rep["answered"] and x not in also]
        an = [x for x in xs if x in rep["answered"]]
        txt = "%d — open %d%s; answered by the owner %d%s" % (len(xs), len(op), ids(op), len(an), ids(an))
        if also:
            en = [x for x in xs if x in also and x not in rep["answered"]]
            txt += "; grammar-only forms (excluded by the engine at load time) %d%s" % (len(en), ids(en))
        return txt

    print("(a) excluded lemmas with no form left: " + split(rep["no_form"], rep["engine_only"]))
    print("(b) « keep » rows whose spelling is excluded: " + split(rep["kept_but_excluded"]))
    print("(c) excluded lemmas whose own spelling the guard keeps: " + split(rep["own_guarded"]))
    print("excluded forms with a not-excluded fold twin in the lexicon: %d%s" % (len(twins), ids(twins)))
    return 0


def cmd_form_answers(a):
    """Applies the owner's answers to ``questions`` (« Q03 exclure la forme ») to ``lemmas.tsv``, idempotently:
    (b) « garder la forme » -> a ``form-`` row; (a/c) « exclure la forme » -> a ``form+`` row over the homograph guard;
    (t) « exclure la forme » -> a ``form+`` row for the fold twin of an excluded form that is a reviewed « keep »
    spelling (a question added by hand to the questions file: ``questions`` does not ask it); (d) « garder » -> the row
    becomes « keep »; every other answer is recorded in the row's reason. Rows added here take the question number as id
    (``Q03``) and name the row they answer for (« (row C0123) »), which the reports of ``forms`` use."""
    qre = re.compile(r"^(Q\d{2}) \| \(([a-z/]+)\) \| (C\d{4,}) \| ([A-Z]) \| ([^|]+?) \|")
    questions = {}
    with open(a.questions, encoding="utf-8") as f:
        for line in f:
            m = qre.match(line)
            if m:
                questions[m.group(1)] = (m.group(2), m.group(3), m.group(4), m.group(5).strip())
    answers = {}
    with open(a.answers, encoding="utf-8") as f:
        for line in f:
            if line.lstrip().startswith("#"):
                continue
            m = re.match(r"\s*(Q\d{2})\s*[:|]?\s*(exclure la forme|garder la forme|exclure|garder)\b", line)
            if m:
                answers[m.group(1)] = m.group(2)
    missing = sorted(set(questions) - set(answers))
    unknown = sorted(set(answers) - set(questions))
    if missing or unknown:
        raise SystemExit("form-answers: %d questions without an answer, %d unknown (%s)" % (
            len(missing), len(unknown), ", ".join((missing + unknown)[:10])))
    tag = "owner %s" % a.reviewed
    rows = [r for r in read_tsv(a.lemmas) if not re.fullmatch(r"Q\d{2}", r["id"])]
    for r in rows:                                    # idempotent: drop the notes of an earlier run
        r["reason"] = re.sub(r"; %s Q\d{2}: [^;]*" % re.escape(tag), "", r["reason"])
    by_id = {r["id"]: r for r in rows}
    added, changed, noted, skipped = [], 0, 0, []
    turned_keep = {cid for q, (k, cid, _, _) in questions.items() if k == "d" and answers[q] == "garder"}
    for q in sorted(questions):
        kind, cid, cat, word = questions[q]
        ans = answers[q]
        r = by_id.get(cid)
        if r is None:
            raise SystemExit("form-answers: %s refers to %s, not in lemmas.tsv" % (q, cid))
        note = "; %s %s: %s" % (tag, q, ans)
        if kind == "b" and ans == "garder la forme":
            added.append({"id": q, "lemma": word, "cat": cat, "decision": "form-",
                          "reason": "%s %s: garder la forme (a reviewed « keep » word, row %s)" % (tag, q, cid),
                          "source": "owner"})
        elif kind == "a/c" and ans == "exclure la forme":
            if cid in turned_keep:
                skipped.append(q)                     # the word itself is kept by a (d) answer: no form+
                r["reason"] += note + " (not applied: the row is kept by its (d) answer)"
                continue
            added.append({"id": q, "lemma": word, "cat": cat, "decision": "form+",
                          "reason": "%s %s: exclure la forme, over the homograph guard (row %s)" % (tag, q, cid),
                          "source": "owner"})
        elif kind == "t" and ans == "exclure la forme":
            added.append({"id": q, "lemma": word, "cat": cat, "decision": "form+",
                          "reason": "%s %s: exclure la forme, the fold twin of an excluded form (row %s)" % (tag, q, cid),
                          "source": "owner"})
        elif kind == "d" and ans == "garder":
            r["decision"] = "keep"
            r["reason"] += note
            changed += 1
        else:
            r["reason"] += note
            noted += 1
    write_tsv(a.lemmas, LEMMA_HEAD, rows + added)
    n = Counter(x["decision"] for x in added)
    print("form-answers: %d answers; form+ %d, form- %d, rows turned « keep » %d, notes %d; skipped %d%s" % (
        len(answers), n["form+"], n["form-"], changed, noted, len(skipped),
        (" (" + ", ".join(skipped) + ")") if skipped else ""))
    return 0


CONFIRM_IDS = ("C0103", "C0115", "C0122")   # reviewer 2026-11-27: excluded only through LDNOOBW, few senses flagged


def cmd_questions(a):
    """The owner's questions on the forms, with the words (the output file stays in the work folder)."""
    lemma_rows = read_tsv(a.lemmas)
    by_lemma = {r["lemma"]: r for r in lemma_rows if r["decision"] in ("exclude", "keep")}
    cands = {r["id"]: r for r in read_tsv(a.candidates)} if a.candidates else {}
    f2l, l2f, lex_forms = lexicon_maps(a.db, a.grammar)
    rows, rep = expand(lemma_rows, f2l, l2f, lex_forms, not a.no_twins)
    freq = {}
    c = sqlite3.connect("file:%s?mode=ro" % a.db, uri=True)
    for form, lemma, fq in c.execute("SELECT ortho, lemma, freq FROM words"):
        freq[(form, lemma)] = freq.get((form, lemma), 0.0) + (fq or 0.0)
    c.close()
    ids = {r["id"]: r for r in lemma_rows}

    def others(form, own):
        out = []
        for x in sorted(f2l.get(form, ()) - {own}):
            st = by_lemma.get(x)
            state = ("gardé à la revue" if st["decision"] == "keep" else "exclu") if st else "non revu"
            out.append((x, state, freq.get((form, x), 0.0)))
        return out

    def fmt(os_):
        return "; ".join("%s (%s, fréq. %.2f)" % (x, s, f) for x, s, f in os_) or "aucun autre mot"

    qs, info = [], []
    guarded = set(rep["own_guarded"])
    for i in (x for x in rep["no_form"] if x not in rep["answered"]):   # an answered row is asked no more
        if i in guarded:
            continue                                   # asked once, as (a/c) below
        r = ids[i]
        # every form is grammar-only (French.lex): nothing to ask, the engine excludes them at load time
        info.append("%s | %s | %s | formes seulement dans grammar.sqlite : exclues par le moteur au chargement"
                    % (i, r["cat"], r["lemma"]))
    for i in (x for x in rep["kept_but_excluded"] if x not in rep["answered"]):   # an answered row is asked no more
        r = ids[i]
        o = [x for x in f2l.get(r["lemma"], ()) if x != r["lemma"]]
        qs.append(("b", i, r["cat"], r["lemma"], "gardé à la revue, mais exclu comme forme de : " + (
            ", ".join(sorted(o)) or "?"), "garder la forme"))
    for i in (x for x in rep["own_guarded"] if x not in rep["answered"]):   # an answered row is asked no more
        r = ids[i]
        o = others(r["lemma"], r["lemma"])
        own = freq.get((r["lemma"], r["lemma"]), 0.0)
        if any(s == "gardé à la revue" for _, s, _ in o):
            rec = "garder la forme"
        else:
            rec = "exclure la forme" if own >= max([f for _, _, f in o] + [0.0]) else "garder la forme"
        kind = "a/c" if i in rep["no_form"] else "c"
        qs.append((kind, i, r["cat"], r["lemma"], "%sle mot exclu (fréq. %.2f) a la même orthographe que : %s" % (
            "aucune forme exclue ; " if kind == "a/c" else "", own, fmt(o)), rec))
    for i in CONFIRM_IDS:
        r = ids.get(i)
        if not r or r["decision"] != "exclude" or i in rep["answered"]:
            continue
        cd = cands.get(i, {})
        first = cd.get("first_sense_flagged") == "1"
        qs.append(("d", i, r["cat"], r["lemma"], "exclu seulement par LDNOOBW : %s/%s sens signalés%s" % (
            cd.get("senses_flagged", "?"), cd.get("senses_total", "?"), ", le premier" if first else
            ", pas le premier"), "exclure" if first else "garder"))
    head = ("Questions sur les formes des mots exclus (%s). Réponse : par numéro, par exemple\n"
            "« Q01 exclure la forme, Q02 garder la forme » ; sans réponse, la recommandation s'applique.\n"
            "(a/c) mot exclu dont l'orthographe est aussi un autre mot, et qui ne laisse donc aucune forme exclue\n"
            "(b) mot gardé dont l'orthographe est exclue (forme d'un mot exclu)\n"
            "(c) mot exclu dont l'orthographe est aussi un autre mot  (d) exclusion à confirmer (exclure / garder)\n\n"
            % a.reviewed)
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(head)
        for n, (k, i, cat, w, what, rec) in enumerate(qs, 1):
            f.write("Q%02d | (%s) | %s | %s | %s | %s | recommandé : %s\n" % (n, k, i, cat, w, what, rec))
        if info:
            f.write("\nPour information (aucune réponse attendue) :\n" + "".join(l + "\n" for l in info))
    n = Counter(q[0] for q in qs)
    print("questions: %d — (a/c) %d, (b) %d, (c) %d, (d) %d; for information (grammar-only forms): %d" % (
        len(qs), n["a/c"], n["b"], n["c"], n["d"], len(info)))
    return 0


# --- ship ------------------------------------------------------------------------------------------------------------

LEMMAS_MARK = "#lemmas"
NAMES_MARK = "#names"
CATS_MARK = "#categories"


def _b64(w):
    return base64.b64encode(w.encode("utf-8")).decode("ascii") + "\n"


def encode_lines(forms, lemmas=(), cats=None, names=()):
    """Header, (when given) a ``#categories R 38 H 8 …`` line with the forms per category (the app's load log: counts
    only), one base64 form per line, then (when given) a ``#lemmas`` line and one base64 excluded lemma per line: the
    engine expands those lemmas with grammar.sqlite at load time (a grammar-only form is not in the file)."""
    body = HEADER + "\n"
    if cats is not None:
        body += CATS_MARK + " " + " ".join("%s %d" % (c, cats.get(c, 0)) for c in CATS) + "\n"
    body += "".join(_b64(f) for f in forms)
    if lemmas:
        body += LEMMAS_MARK + "\n" + "".join(_b64(l) for l in lemmas)
    if names:
        body += NAMES_MARK + "\n" + "".join(_b64(n) for n in names)
    return body


def decode_all(text):
    """(forms, lemmas, names): the reader the engine mirrors (``ExcludedWords.FromFile``); other ``#`` lines are skipped."""
    lines = text.splitlines()
    if not lines or lines[0] != HEADER:
        raise ValueError("not a Pépite exclusions file")
    forms, lemmas, names = [], [], []
    cur = forms
    for l in lines[1:]:
        if l == LEMMAS_MARK:
            cur = lemmas
        elif l == NAMES_MARK:
            cur = names
        elif l.startswith("#"):
            continue
        elif l.strip():
            cur.append(base64.b64decode(l, validate=True).decode("utf-8"))
    return forms, lemmas, names


def decode_sections(text):
    """(forms, lemmas) of an exclusions file."""
    return decode_all(text)[:2]


def decode_lines(text):
    """The forms of an exclusions file."""
    return decode_sections(text)[0]


def source_text(forms_rows, lemma_rows, forms_sha, reviewed, lock, pins=()):
    n_forms = Counter(r["cat"] for r in forms_rows)
    n_lemmas = Counter(r["cat"] for r in lemma_rows if r["decision"] == "exclude")
    n_keep = sum(1 for r in lemma_rows if r["decision"] == "keep")
    inputs = (lock or {}).get("inputs", [])
    lines = [
        "Pépite — mots exclus (fr.txt)",
        "",
        "Words Pépite never offers in the prediction bar, the next-word prediction or the ≈ list; a pupil who types one",
        "writes it normally (plan offensive-words, product decision « Mots exclus »).",
        "",
        "Format: the first line is « %s »; the line « %s … » gives the number of forms per" % (HEADER, CATS_MARK),
        "category; each next line is one word form, encoded in base64 of",
        "its UTF-8 spelling; after the line « %s » come the excluded lemmas, encoded the same way, which Pépite" % LEMMAS_MARK,
        "expands with its grammar lexicon when it loads the list (a form is then excluded only when every lemma it has",
        "is excluded); after the line « %s » come the listed spellings a « Noms propres » pack name keeps when it" % NAMES_MARK,
        "is written with its capital (owner decision; the lowercase word stays excluded),",
        "and the encoding only keeps the list out of sight of a child browsing the install folder: it",
        "is NOT a secret. The plain, reviewed list is src/Data.Build/exclusions/forms.tsv (forms) and lemmas.tsv",
        "(decisions with reasons, including the words kept) in Pépite's source repository.",
        "",
        "Reviewed: %s" % reviewed,
        "forms.tsv SHA-256: %s" % forms_sha,
        "Forms: %d (%s)" % (len(forms_rows), ", ".join("%s %d" % (c, n_forms[c]) for c in CATS)),
        "Excluded lemmas: %d (%s); reviewed and kept: %d" % (sum(n_lemmas.values()),
                                                              ", ".join("%s %d" % (c, n_lemmas[c]) for c in CATS),
                                                              n_keep),
        "Categories: R ethnic / racial / religious slur, H homophobic / transphobic, S sexist slur, D disability slur,",
        "X sexual / obscene, V vulgar (kept), I generic insult, G drugs.",
        "",
        "Sources and licences:",
    ]
    for i in inputs:
        lines.append("- %s — %s; %s; SHA-256 %s" % (i.get("credit", i.get("id")), i.get("version", ""),
                                                    i.get("url", ""), i.get("sha256", "")))
    lines += [
        "- Word forms: Lexique 3.83 (B. New, C. Pallier et al., https://www.lexique.org, CC BY-SA 4.0), the word list of",
        "  Pépite's prediction.sqlite. No form is taken from Grammalecte's French.lex (MPL-2.0): grammar.sqlite only",
        "  takes part in the homograph check here, and Pépite adds the inflections it knows of the excluded lemmas when",
        "  it loads the list.",
    ]
    for name, sha in pins:
        lines.append("  %s SHA-256: %s" % (name, sha))
    lines += [
        "",
        "This list is derived from Wiktionnaire and is distributed under CC BY-SA 4.0, as a separate data file of",
        "Pépite (GPL-3.0-or-later). CC BY-SA 4.0 is one-way compatible with GPLv3 (Creative Commons, 2015-10-08).",
        "The candidates were selected by Wiktionnaire's own usage labels (LDNOOBW as a cross-check); every proposed",
        "exclusion was then reviewed by the maintainer.",
        "",
    ]
    return "\n".join(lines)


def cmd_ship(a):
    forms_rows = read_tsv(a.forms)
    lemma_rows = read_tsv(a.lemmas)
    forms = [r["form"] for r in forms_rows]
    if forms != sorted(set(forms)):
        raise SystemExit("forms.tsv is not sorted or has duplicates: rebuild it with « forms »")
    lock = None
    if a.lock and os.path.exists(a.lock):
        with open(a.lock, encoding="utf-8") as f:
            lock = json.load(f)
    # the review date and the lexicon databases forms.tsv was expanded from are pinned in the lock, so that a CI build
    # writes the same SOURCE.txt as the maintainer's (reviewer 2026-11-27)
    pins_lock = (lock or {}).get("forms_inputs", {})
    reviewed = a.reviewed or pins_lock.get("reviewed")
    if not reviewed:
        raise SystemExit("ship: no review date (--reviewed, or forms_inputs.reviewed in the lock)")
    for x in (a.db, a.grammar):
        want = pins_lock.get(os.path.basename(x)) if x else None
        if want and sha256_file(x) != want and not a.other_inputs:
            raise SystemExit("ship: %s differs from the one forms.tsv was expanded from (lock forms_inputs): rebuild "
                             "forms.tsv with « forms », or pass --other-inputs" % os.path.basename(x))
    os.makedirs(a.out_dir, exist_ok=True)
    lemmas = sorted({r["lemma"] for r in lemma_rows if r["decision"] == "exclude"})
    # owner 2026-11-27: listed spellings that a « Noms propres » pack name, written with its capital, keeps (« name-ok »
    # rows, the form in the lemma column); the lowercase lexicon word stays excluded
    names = sorted({r["lemma"] for r in lemma_rows if r["decision"] == "name-ok"})
    body = encode_lines(forms, lemmas, Counter(r["cat"] for r in forms_rows), names)
    assert decode_all(body) == (forms, lemmas, names)
    p = os.path.join(a.out_dir, "fr.txt")
    with open(p, "w", encoding="ascii", newline="\n") as f:
        f.write(body)
    s = os.path.join(a.out_dir, "SOURCE.txt")
    with open(s, "w", encoding="utf-8", newline="\n") as f:
        f.write(source_text(forms_rows, lemma_rows, sha256_file(a.forms), reviewed, lock,
                            [(os.path.basename(x), sha256_file(x)) for x in (a.db, a.grammar) if x]))
    print("fr.txt: %d forms + %d lemmas + %d exempt pack names, %d bytes, sha256 %s" % (len(forms), len(lemmas), len(names),
                                                                                       os.path.getsize(p),
                                                                   sha256_file(p)))
    print("SOURCE.txt: sha256 %s" % sha256_file(s))
    return 0


def main(argv=None):
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    p = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = p.add_subparsers(dest="cmd", required=True)
    d = sub.add_parser("decisions")
    d.add_argument("--candidates", required=True)
    d.add_argument("--decisions", required=True)
    d.add_argument("--out", default=os.path.join(HERE, "lemmas.tsv"))
    d.add_argument("--reviewed", required=True, help="review date, YYYY-MM-DD")
    t = sub.add_parser("decisions-from-text")
    t.add_argument("--candidates", required=True)
    t.add_argument("--text", help="the owner's answer, e.g. « garder: C0055, C0089 ; exclure: C1234 »")
    t.add_argument("--text-file", action="append", help="a file holding (part of) the answer; repeatable")
    t.add_argument("--out", required=True, help="decisions JSON, the same format as the review page's export")
    t.add_argument("--reviewed", required=True, help="review date, YYYY-MM-DD")
    t.add_argument("--note")
    t.add_argument("--strict", action="store_true", help="refuse every unknown id, « garder » ones too")
    fo = sub.add_parser("forms")
    fo.add_argument("--lemmas", default=os.path.join(HERE, "lemmas.tsv"))
    fo.add_argument("--db", required=True)
    fo.add_argument("--grammar")
    fo.add_argument("--out", default=os.path.join(HERE, "forms.tsv"))
    fo.add_argument("--no-twins", action="store_true", help="do not add the fold twins of the excluded forms")
    q = sub.add_parser("questions")
    q.add_argument("--lemmas", default=os.path.join(HERE, "lemmas.tsv"))
    q.add_argument("--db", required=True)
    q.add_argument("--grammar")
    q.add_argument("--candidates")
    q.add_argument("--out", required=True, help="a work-folder file: it holds the words")
    q.add_argument("--reviewed", default="")
    q.add_argument("--no-twins", action="store_true")
    fa = sub.add_parser("form-answers")
    fa.add_argument("--lemmas", default=os.path.join(HERE, "lemmas.tsv"))
    fa.add_argument("--questions", required=True)
    fa.add_argument("--answers", required=True)
    fa.add_argument("--reviewed", required=True)
    sh = sub.add_parser("ship")
    sh.add_argument("--forms", default=os.path.join(HERE, "forms.tsv"))
    sh.add_argument("--lemmas", default=os.path.join(HERE, "lemmas.tsv"))
    sh.add_argument("--out-dir", required=True)
    sh.add_argument("--reviewed", help="review date (default: forms_inputs.reviewed of the lock)")
    sh.add_argument("--other-inputs", action="store_true", help="accept databases other than the lock's forms_inputs")
    sh.add_argument("--lock", default=os.path.join(HERE, "exclusions.lock.json"))
    sh.add_argument("--db", help="prediction.sqlite the forms were expanded from (its SHA-256 goes into SOURCE.txt)")
    sh.add_argument("--grammar", help="grammar.sqlite used by the homograph check (SHA-256 into SOURCE.txt)")
    a = p.parse_args(argv)
    return {"decisions": cmd_decisions, "decisions-from-text": cmd_decisions_from_text, "forms": cmd_forms,
            "questions": cmd_questions, "form-answers": cmd_form_answers, "ship": cmd_ship}[a.cmd](a)


if __name__ == "__main__":
    sys.exit(main())
