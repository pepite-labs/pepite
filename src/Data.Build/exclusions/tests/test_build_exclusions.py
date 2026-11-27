#!/usr/bin/env python3
"""Unit tests of the « mots exclus » scripts (plan offensive-words, Phase 0) — invented words only, no real listed word,
no network. Run: python -I -m unittest discover -s src/Data.Build/exclusions/tests -p "test*.py"

Also checks that no script prints a word: every console output is captured and searched for the fixture's words."""
import contextlib
import gzip
import io
import json
import os
import re
import sqlite3
import sys
import tempfile
import unittest

EXCL = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, EXCL)
import build_exclusions as B  # noqa: E402
import find_candidates as F  # noqa: E402
import make_review as M  # noqa: E402

# Invented words (none is French, none is on any list).
WORDS = ["zorblat", "zorblats", "zorblate", "kroutir", "kroutis", "kroutissons", "glabe", "frenouille", "frenouilles",
         "plimouze", "trambule", "zörblat", "bastrol", "quiblon", "vamoche", "sprindel", "noufle", "drabule",
         "quarnide", "zv", "flumpor", "brimol", "frénouille", "zorblât", "pîmer"]


def run_quiet(fn, *args):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        rc = fn(*args)
    return rc, out.getvalue()


def make_db(path, rows):
    c = sqlite3.connect(path)
    c.execute("CREATE TABLE words (ortho TEXT, lemma TEXT, cgram TEXT, phon TEXT, freq REAL)")
    c.executemany("INSERT INTO words VALUES (?, ?, 'NOM', '', ?)", rows)
    c.commit()
    c.close()


def make_grammar(path, pairs):
    c = sqlite3.connect(path)
    c.execute("CREATE TABLE lemma(id INTEGER PRIMARY KEY, lemma TEXT)")
    c.execute("CREATE TABLE morph(form TEXT, lemma_id INTEGER, tag_id INTEGER, PRIMARY KEY(form, lemma_id, tag_id))")
    ids = {}
    for form, lemma in pairs:
        if lemma not in ids:
            ids[lemma] = len(ids) + 1
            c.execute("INSERT INTO lemma VALUES (?, ?)", (ids[lemma], lemma))
        c.execute("INSERT INTO morph VALUES (?, ?, 1)", (form, ids[lemma]))
    c.commit()
    c.close()


LEXICON = [("zorblat", "zorblat", 1.0), ("zorblats", "zorblat", 0.5), ("kroutir", "kroutir", 2.0),
           ("kroutis", "kroutir", 1.0), ("glabe", "kroutir", 0.1), ("glabe", "glabe", 3.0),
           ("frenouille", "frenouille", 20.0), ("frenouilles", "frenouille", 5.0), ("trambule", "trambule", 0.2),
           ("trambule", "quiblon", 4.0), ("zörblat", "zörblat", 0.1),
           ("frénouille", "frénouille", 0.1), ("drabule", "drabule", 0.3), ("drabule", "bastrol", 2.0),
           ("zorblât", "pîmer", 1.0)]
GRAMMAR = [("zorblat", "zorblat"), ("zorblate", "zorblat"), ("kroutissons", "kroutir"), ("glabe", "glabe")]
LEMMAS = [
    {"id": "C0001", "lemma": "zorblat", "cat": "R", "decision": "exclude", "reason": "r", "source": "wiktionnaire"},
    {"id": "C0002", "lemma": "kroutir", "cat": "X", "decision": "exclude", "reason": "r", "source": "wiktionnaire"},
    {"id": "C0003", "lemma": "frenouille", "cat": "I", "decision": "keep", "reason": "r", "source": "wiktionnaire"},
    {"id": "C0004", "lemma": "trambule", "cat": "I", "decision": "exclude", "reason": "r", "source": "wiktionnaire"},
    {"id": "C0005", "lemma": "plimouze", "cat": "S", "decision": "form+", "reason": "r", "source": "owner"},
    {"id": "C0006", "lemma": "kroutis", "cat": "X", "decision": "form-", "reason": "r", "source": "owner"},
    {"id": "C0007", "lemma": "frénouille", "cat": "G", "decision": "exclude", "reason": "r", "source": "wiktionnaire"},
    {"id": "C0008", "lemma": "drabule", "cat": "I", "decision": "exclude", "reason": "r", "source": "wiktionnaire"},
    {"id": "C0009", "lemma": "bastrol", "cat": "I", "decision": "keep", "reason": "r", "source": "wiktionnaire"},
    {"id": "C0010", "lemma": "zorblats", "cat": "I", "decision": "keep", "reason": "r", "source": "wiktionnaire"},
]


class Fixture(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.d = self.tmp.name
        self.db = os.path.join(self.d, "prediction.sqlite")
        self.gr = os.path.join(self.d, "grammar.sqlite")
        make_db(self.db, LEXICON)
        make_grammar(self.gr, GRAMMAR)
        self.lemmas = os.path.join(self.d, "lemmas.tsv")
        B.write_tsv(self.lemmas, B.LEMMA_HEAD, LEMMAS)

    def tearDown(self):
        self.tmp.cleanup()

    def assertNoWord(self, text):
        for w in WORDS:
            self.assertNotIn(w, text, "a word reached the console")

    def forms(self, out="forms.tsv", *extra):
        p = os.path.join(self.d, out)
        rc, txt = run_quiet(B.main, ["forms", "--lemmas", self.lemmas, "--db", self.db, "--grammar", self.gr,
                                     "--out", p] + list(extra))
        self.assertEqual(rc, 0)
        self.assertNoWord(txt)
        return p, B.read_tsv(p), txt


class FormsTests(Fixture):
    def test_expansion_guard_twins_and_overrides(self):
        _, rows, txt = self.forms()
        got = {r["form"]: (r["lemma_id"], r["cat"]) for r in rows}
        # lexicon forms only (« zorblate », « kroutissons » are grammar-only: left to the engine); « glabe » is also a
        # form of an innocent lemma (guard); « kroutis » removed by form-; « plimouze » added by form+; the guarded own
        # spellings « trambule » / « drabule » stay offered; twin « zörblat » (its only lemma folds like the excluded
        # lemma) is excluded with its source's id; twin « zorblât » (another, unreviewed word) stays offered; twin
        # « frenouille » is a reviewed « keep » word and stays offered
        self.assertEqual(sorted(got), ["frénouille", "kroutir", "plimouze", "zorblat", "zorblats", "zörblat"])
        self.assertEqual(got["zörblat"], ("C0001", "R"))
        self.assertEqual(got["plimouze"], ("C0005", "S"))
        self.assertIn("grammar-only forms left to the engine (not written): 2", txt)
        self.assertIn("fold twins excluded: 1; twins kept (another, unexcluded word): 2", txt)
        self.assertIn("(a) excluded lemmas with no form left: 2 — open 2 (C0004, C0008); answered by the owner 0", txt)
        self.assertIn("(b) « keep » rows whose spelling is excluded: 1 — open 1 (C0010); answered by the owner 0", txt)
        self.assertIn("(c) excluded lemmas whose own spelling the guard keeps: 2 — open 2 (C0004, C0008); answered", txt)

    def test_form_answers_apply_idempotently(self):
        qf = os.path.join(self.d, "questions.txt")
        run_quiet(B.main, ["questions", "--lemmas", self.lemmas, "--db", self.db, "--grammar", self.gr, "--out", qf])
        with open(qf, encoding="utf-8") as f:
            order = [re.match(r"(Q\d{2}) \| \(([a-z/]+)\) \| (C\d{4})", l).groups() for l in f if re.match(r"Q\d", l)]
        self.assertEqual(order, [("Q01", "b", "C0010"), ("Q02", "a/c", "C0004"), ("Q03", "a/c", "C0008")])
        ans = os.path.join(self.d, "answers.txt")
        with open(ans, "w", encoding="utf-8") as f:
            f.write("# owner\nQ01 garder la forme\nQ02 exclure la forme\nQ03 garder la forme\n")
        args = ["form-answers", "--lemmas", self.lemmas, "--questions", qf, "--answers", ans, "--reviewed", "2026-11-27"]
        rc, txt = run_quiet(B.main, args)
        self.assertEqual(rc, 0)
        self.assertNoWord(txt)
        with open(self.lemmas, "rb") as f:
            first = f.read()
        run_quiet(B.main, args)                                    # a second run changes nothing
        with open(self.lemmas, "rb") as f:
            self.assertEqual(f.read(), first)
        _, rows, _ = self.forms()
        got = {r["form"]: r["lemma_id"] for r in rows}
        self.assertEqual(got.get("trambule"), "Q02")               # form+ over the guard
        self.assertNotIn("zorblats", got)                          # form-: the reviewed « keep » spelling
        self.assertNotIn("drabule", got)                           # guard kept
        self.assertIn("owner 2026-11-27 Q03: garder la forme",
                      {r["id"]: r for r in B.read_tsv(self.lemmas)}["C0008"]["reason"])
        # the reports map the answer rows back to the rows they answer for: nothing is left open
        _, _, txt = self.forms()
        self.assertIn("(a) excluded lemmas with no form left: 1 — open 0; answered by the owner 1 (C0008)", txt)
        self.assertIn("(b) « keep » rows whose spelling is excluded: 0 — open 0; answered by the owner 0", txt)
        self.assertIn("(c) excluded lemmas whose own spelling the guard keeps: 1 — open 0; answered by the owner 1 "
                      "(C0008)", txt)
        # a (t) question added by hand: the fold twin of an excluded form, a reviewed « keep » spelling -> form+
        with open(qf, "a", encoding="utf-8") as f:
            f.write("Q04 | (t) | C0003 | R | frenouille | jumeau | recommandé : exclure la forme\n")
        with open(ans, "w", encoding="utf-8") as f:
            f.write("Q01 garder la forme\nQ02 exclure la forme\nQ03 garder la forme\nQ04 exclure la forme\n")
        rc, txt = run_quiet(B.main, args)
        self.assertEqual(rc, 0)
        self.assertNoWord(txt)
        _, rows, txt = self.forms()
        self.assertEqual({r["form"]: r["lemma_id"] for r in rows}.get("frenouille"), "Q04")
        self.assertIn("(b) « keep » rows whose spelling is excluded: 1 — open 0; answered by the owner 1 (C0003)", txt)
        with open(ans, "w", encoding="utf-8") as f:
            f.write("Q01 garder la forme\n")
        with self.assertRaises(SystemExit):                        # every question needs an answer
            run_quiet(B.main, args)

    def test_no_twins_option(self):
        _, rows, _ = self.forms("forms.tsv", "--no-twins")
        self.assertNotIn("zörblat", {r["form"] for r in rows})

    def test_questions_file_and_quiet_console(self):
        out = os.path.join(self.d, "questions.txt")
        rc, txt = run_quiet(B.main, ["questions", "--lemmas", self.lemmas, "--db", self.db, "--grammar", self.gr,
                                     "--out", out, "--reviewed", "2026-11-27"])
        self.assertEqual(rc, 0)
        self.assertNoWord(txt)
        self.assertIn("questions: 3 — (a/c) 2, (b) 1, (c) 0, (d) 0", txt)
        with open(out, encoding="utf-8") as f:
            lines = [l for l in f if re.match(r"Q[0-9]+ ", l)]
        self.assertEqual(len(lines), 3)
        # « drabule » shares its spelling with a reviewed « keep » word: keep the form; « trambule » with an unreviewed,
        # more frequent word: keep the form too
        drab = [l for l in lines if "| C0008 |" in l and "(a/c)" in l][0]
        self.assertIn("bastrol (gardé à la revue", drab)
        self.assertTrue(drab.rstrip().endswith("recommandé : garder la forme"))
        self.assertIn("recommandé : garder la forme", [l for l in lines if "(b)" in l][0])

    def test_sorted_ids_and_byte_reproducible(self):
        p1, rows, _ = self.forms("a.tsv")
        p2, _, _ = self.forms("b.tsv")
        with open(p1, "rb") as f1, open(p2, "rb") as f2:
            self.assertEqual(f1.read(), f2.read())
        self.assertEqual([r["form"] for r in rows], sorted(r["form"] for r in rows))
        self.assertEqual([r["id"] for r in rows], ["F%05d" % i for i in range(1, len(rows) + 1)])

    def test_unknown_decision_is_refused(self):
        B.write_tsv(self.lemmas, B.LEMMA_HEAD, LEMMAS + [dict(LEMMAS[0], id="C0099", decision="maybe")])
        with self.assertRaises(SystemExit):
            run_quiet(B.main, ["forms", "--lemmas", self.lemmas, "--db", self.db, "--out",
                               os.path.join(self.d, "x.tsv")])


class ShipTests(Fixture):
    def test_base64_file_and_source(self):
        p, rows, _ = self.forms()
        out = os.path.join(self.d, "out")
        lock = os.path.join(EXCL, "exclusions.lock.json")
        rc, txt = run_quiet(B.main, ["ship", "--forms", p, "--lemmas", self.lemmas, "--out-dir", out,
                                     "--reviewed", "2026-11-27", "--lock", lock])
        self.assertEqual(rc, 0)
        self.assertNoWord(txt)
        with open(os.path.join(out, "fr.txt"), encoding="ascii") as f:
            body = f.read()
        self.assertTrue(body.startswith(B.HEADER + "\n"))
        self.assertEqual(body.splitlines()[1], "#categories R 3 H 0 S 1 D 0 X 1 V 0 I 0 G 1")   # counts for the app's log
        self.assertNoWord(body)                                   # encoded: no word in clear
        self.assertEqual(B.decode_sections(body), ([r["form"] for r in rows],
                                                   sorted(r["lemma"] for r in LEMMAS if r["decision"] == "exclude")))
        with open(os.path.join(out, "SOURCE.txt"), encoding="utf-8") as f:
            src = f.read()
        self.assertNoWord(src)
        self.assertIn(B.sha256_file(p), src)
        self.assertIn("2026-11-27", src)
        self.assertIn("CC BY-SA 4.0", src)
        self.assertIn("NOT a secret", src)
        self.assertNotRegex(src, r"[A-Za-z]:\\")                 # no drive path in a public file

    def test_review_date_and_inputs_pinned_in_the_lock(self):
        p, _, _ = self.forms()
        out = os.path.join(self.d, "out2")
        lock = os.path.join(self.d, "lock.json")
        with open(lock, "w", encoding="utf-8") as f:
            json.dump({"inputs": [], "forms_inputs": {"prediction.sqlite": "0" * 64, "reviewed": "2026-11-27"}}, f)
        args = ["ship", "--forms", p, "--lemmas", self.lemmas, "--out-dir", out, "--lock", lock, "--db", self.db]
        with self.assertRaises(SystemExit):                       # another database than forms.tsv was expanded from
            run_quiet(B.main, args)
        rc, _ = run_quiet(B.main, args + ["--other-inputs"])
        self.assertEqual(rc, 0)
        with open(os.path.join(out, "SOURCE.txt"), encoding="utf-8") as f:
            self.assertIn("Reviewed: 2026-11-27", f.read())        # the date from the lock

    def test_exempt_pack_names_section(self):
        B.write_tsv(self.lemmas, B.LEMMA_HEAD, LEMMAS + [{"id": "N01", "lemma": "zorblat", "cat": "R", "decision": "name-ok",
                                                         "reason": "owner names", "source": "owner"}])
        p, rows, txt = self.forms()
        self.assertIn("zorblat", {r["form"] for r in rows})        # still excluded as a word
        out = os.path.join(self.d, "out3")
        rc, txt = run_quiet(B.main, ["ship", "--forms", p, "--lemmas", self.lemmas, "--out-dir", out, "--reviewed", "2026-11-27"])
        self.assertEqual(rc, 0)
        self.assertNoWord(txt)
        with open(os.path.join(out, "fr.txt"), encoding="ascii") as f:
            body = f.read()
        self.assertEqual(B.decode_all(body)[2], ["zorblat"])
        self.assertNoWord(body)

    def test_decode_rejects_other_files(self):
        with self.assertRaises(ValueError):
            B.decode_lines("plain\nlist\n")

    def test_non_ascii_round_trip(self):
        forms = ["drabulé", "œnoufle", "sprindel-noufle"]
        self.assertEqual(B.decode_lines(B.encode_lines(forms)), forms)


class DecisionTests(Fixture):
    def cands(self):
        p = os.path.join(self.d, "candidates.tsv")
        rows = [{"id": "C0001", "lemma": "bastrol", "cat": "R", "senses_total": 1, "senses_flagged": 1,
                 "first_sense_flagged": 1, "lexique_freq": 0.1, "in_lexicon": "lemma", "ldnoobw": 0,
                 "decision": "exclude", "reason": "R: proposed", "source": "wiktionnaire", "signals": "off,eth"},
                {"id": "C0002", "lemma": "vamoche", "cat": "I", "senses_total": 4, "senses_flagged": 1,
                 "first_sense_flagged": 0, "lexique_freq": 12.0, "in_lexicon": "lemma", "ldnoobw": 0,
                 "decision": "keep", "reason": "I: proposed", "source": "wiktionnaire", "signals": "pej"}]
        B.write_tsv(p, M.FIELDS, rows)
        return p

    def export(self, cands, decisions, sha=True):
        p = os.path.join(self.d, "dec.json")
        data = {"format": "pepite-exclusions-review", "version": 1, "exported": "2026-11-27T10:00:00Z",
                "decisions": decisions}
        if sha:
            data["candidates_sha256"] = B.sha256_file(cands)
        with open(p, "w", encoding="utf-8") as f:
            json.dump(data, f)
        return p

    def test_merge_owner_changes(self):
        c = self.cands()
        d = self.export(c, [{"id": "C0001", "decision": "exclude", "cat": "R", "note": ""},
                            {"id": "C0002", "decision": "exclude", "cat": "I", "note": "insulte courante"}])
        out = os.path.join(self.d, "lemmas-out.tsv")
        rc, txt = run_quiet(B.main, ["decisions", "--candidates", c, "--decisions", d, "--out", out,
                                     "--reviewed", "2026-11-27"])
        self.assertEqual(rc, 0)
        self.assertNoWord(txt)
        rows = {r["id"]: r for r in B.read_tsv(out)}
        self.assertEqual(rows["C0002"]["decision"], "exclude")
        self.assertIn("owner: insulte courante", rows["C0002"]["reason"])
        self.assertEqual(rows["C0001"]["reason"], "R: proposed")
        self.assertIn("reviewed 2026-11-27", rows["C0001"]["source"])
        self.assertIn("changed by the owner", txt)

    def test_missing_decision_and_other_candidates_file(self):
        c = self.cands()
        out = os.path.join(self.d, "lemmas-out.tsv")
        d = self.export(c, [{"id": "C0001", "decision": "exclude"}])
        with self.assertRaises(SystemExit):
            run_quiet(B.main, ["decisions", "--candidates", c, "--decisions", d, "--out", out, "--reviewed", "x"])
        d = self.export(c, [{"id": "C0001", "decision": "exclude"}, {"id": "C0002", "decision": "keep"}])
        with open(c, "a", encoding="utf-8") as f:
            f.write("")                                           # same bytes: still accepted
        run_quiet(B.main, ["decisions", "--candidates", c, "--decisions", d, "--out", out, "--reviewed", "x"])
        with open(d, encoding="utf-8") as f:
            data = json.load(f)
        data["candidates_sha256"] = "0" * 64
        with open(d, "w", encoding="utf-8") as f:
            json.dump(data, f)
        with self.assertRaises(SystemExit):
            run_quiet(B.main, ["decisions", "--candidates", c, "--decisions", d, "--out", out, "--reviewed", "x"])

    def test_tsv_export_and_bad_decision(self):
        c = self.cands()
        p = os.path.join(self.d, "dec.tsv")
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write("id\tdecision\tcat\tnote\nC0001\texclude\tR\t\nC0002\tkeep\tI\t\n")
        out = os.path.join(self.d, "lemmas-out.tsv")
        rc, _ = run_quiet(B.main, ["decisions", "--candidates", c, "--decisions", p, "--out", out, "--reviewed", "x"])
        self.assertEqual(rc, 0)
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write("id\tdecision\nC0001\tperhaps\n")
        with self.assertRaises(ValueError):
            B.load_decisions(p)


class TextAnswerTests(Fixture):
    cands = DecisionTests.cands
    def test_overrides_on_top_of_proposals(self):
        c = self.cands()
        out = os.path.join(self.d, "from-text.json")
        rc, txt = run_quiet(B.main, ["decisions-from-text", "--candidates", c, "--text",
                                     "garder: C0001 ; exclure: C0002", "--out", out, "--reviewed", "2026-11-27"])
        self.assertEqual(rc, 0)
        self.assertNoWord(txt)
        with open(out, encoding="utf-8") as f:
            raw = f.read()
        self.assertNoWord(raw)                                    # ids only, like the page's export
        got = {d["id"]: d["decision"] for d in json.loads(raw)["decisions"]}
        self.assertEqual(got, {"C0001": "keep", "C0002": "exclude"})
        lem = os.path.join(self.d, "lemmas-out.tsv")              # feeds « decisions » like the page's export
        rc, _ = run_quiet(B.main, ["decisions", "--candidates", c, "--decisions", out, "--out", lem,
                                   "--reviewed", "2026-11-27"])
        self.assertEqual(rc, 0)
        self.assertEqual({r["id"]: r["decision"] for r in B.read_tsv(lem)}, got)

    def test_answers_shown_in_page_and_moot_keep_ids(self):
        c = self.cands()
        ans = os.path.join(self.d, "answer.txt")
        with open(ans, "w", encoding="utf-8") as f:
            f.write("# lot 1\ngarder: C0001, C0777\n")           # C0777: no longer a candidate -> moot
        rc, txt = run_quiet(B.main, ["decisions-from-text", "--candidates", c, "--text-file", ans, "--out",
                                     os.path.join(self.d, "d.json"), "--reviewed", "x"])
        self.assertEqual(rc, 0)
        self.assertIn("C0777", txt)
        html = os.path.join(self.d, "r.html")
        lots = os.path.join(self.d, "lots")
        rc, txt = run_quiet(M.main, ["--candidates", c, "--out", html, "--batches", lots, "--answers", ans])
        self.assertIn("owner answers applied: 1 rows changed", txt)
        with open(os.path.join(lots, "lot-001.txt"), encoding="utf-8") as f:
            line = [l for l in f if l.startswith("C0001")][0]
        self.assertIn(" | garder | ", line)
        self.assertIn("owner: garder (proposed exclure)", line)

    def test_seen_rows_go_after_unseen_ones(self):
        rows = [{"id": "C0001", "cat": "R", "decision": "exclude", "lexique_freq": "9"},
                {"id": "C0002", "cat": "X", "decision": "exclude", "lexique_freq": "1"},
                {"id": "C0003", "cat": "R", "decision": "keep", "lexique_freq": "5"}]
        self.assertEqual([r["id"] for r in M.batch_order(rows)], ["C0001", "C0002", "C0003"])
        self.assertEqual([r["id"] for r in M.batch_order(rows, {"C0001"})], ["C0002", "C0001", "C0003"])
        p = os.path.join(self.d, "seen.txt")
        with open(p, "w", encoding="utf-8") as f:
            f.write("# ids\nC0001\nC0003, C12345\n")
        self.assertEqual(M.read_ids([p]), {"C0001", "C0003", "C12345"})

    def test_parse_and_refusals(self):
        self.assertEqual(B.parse_overrides("Garder : C0055, C0089 ;\nexclure: C1234"),
                         {"C0055": "keep", "C0089": "keep", "C1234": "exclude"})
        self.assertEqual(B.parse_overrides(""), {})
        with self.assertRaises(ValueError):
            B.parse_overrides("C0055")
        with self.assertRaises(ValueError):
            B.parse_overrides("garder: C0055 exclure: C0055")
        c = self.cands()
        with self.assertRaises(SystemExit):
            run_quiet(B.main, ["decisions-from-text", "--candidates", c, "--text", "exclure: C9999", "--out",
                               os.path.join(self.d, "x.json"), "--reviewed", "x"])


def sense(gloss, tags=(), raw=(), cats=(), topics=(), alt=None):
    s = {"glosses": [gloss], "tags": list(tags), "raw_tags": list(raw), "categories": list(cats),
         "topics": list(topics)}
    if alt:
        s["alt_of"] = [{"word": alt}]
    return s


def entry(word, *senses, lang="fr", form_of=False):
    o = {"word": word, "lang_code": lang, "lang": "Français", "pos": "noun", "senses": list(senses)}
    if form_of:
        for s in o["senses"]:
            s["form_of"] = [{"word": "x"}]
    return o


class CandidateTests(unittest.TestCase):
    def test_classify_rules(self):
        eth = F.signals(["offensive", "Insultes en français"], ["Personne d’origine étrangère."])
        self.assertEqual(F.classify(eth), ("R", True))
        self.assertEqual(F.classify(F.signals(["offensive"], ["Homme homosexuel."])), ("H", True))
        self.assertEqual(F.classify(F.signals(["Injurieux"], ["Femme bavarde."])), ("S", True))
        # owner 2026-11-27: « péjoratif » / « méprisant » alone never make an R / H / S sense
        self.assertEqual(F.classify(F.signals(["pejorative"], ["Homme homosexuel."])), ("I", False))
        self.assertEqual(F.classify(F.signals(["Méprisant"], ["Personne d’origine étrangère."])), ("I", True))
        self.assertEqual(F.classify(F.signals(["Termes péjoratifs en français"], ["Femme bavarde."])), ("I", False))
        self.assertEqual(F.classify(F.signals(["pejorative", "Handicap"], ["Personne."])), ("D", True))
        # owner 2026-11-27: X = sexual / obscene; vulgar and not sexual = V (kept); familiar alone never flags
        self.assertEqual(F.classify(F.signals(["vulgar"], ["Acte sexuel."])), ("X", True))
        self.assertEqual(F.classify(F.signals(["Termes familiers en français"], ["Rapport sexuel."])), ("X", True))
        self.assertEqual(F.classify(F.signals(["Lexique en français de la pornographie"], ["Film."])), ("X", True))
        self.assertEqual(F.classify(F.signals(["vulgar"], ["Chose."])), ("V", True))
        self.assertEqual(F.classify(F.signals(["Termes vulgaires en français"], ["Les fesses."])), ("V", True))
        self.assertEqual(F.classify(F.signals(["Termes familiers en français"], ["Chose."])), ("", False))
        self.assertEqual(F.decide("V", True, 1, 1, True, 1)[0], "keep")
        self.assertEqual(F.classify(F.signals(["Lexique en français de la drogue"], ["Plante."])), ("G", True))
        self.assertEqual(F.classify(F.signals(["Insultes en français"], ["Personne sotte."])), ("I", True))
        self.assertEqual(F.classify(F.signals(["pejorative"], ["Personne sotte."])), ("I", False))
        # a domain label (the antiracism lexicon) is context, not an insult label
        self.assertEqual(F.classify(F.signals(["Lexique en français de l’antiracisme"], ["Doctrine."])), ("", False))
        # correct anatomical term: sexuality lexicon + anatomy, no register mark -> never X
        self.assertEqual(F.classify(F.signals(["sexuality", "anatomy"], ["Organe."])), ("", False))
        # « nom vulgaire » (common name of a plant) is not a vulgarity
        self.assertEqual(F.classify(F.signals(["Nom vulgaire"], ["Plante."])), ("", False))

    def test_decide_policy(self):
        # owner 2026-11-27: R / H / S only when dominant, every sense flagged, or listed by LDNOOBW
        self.assertEqual(F.decide("R", True, 5, 1, False, 0, 0.5)[0], "keep")        # minority, even a rare word
        self.assertEqual(F.decide("R", True, 5, 1, False, 1, 0.5)[0], "exclude")     # ... unless LDNOOBW lists it
        self.assertEqual(F.decide("R", True, 5, 1, True, 0, 50.0)[0], "exclude")     # first sense is the slur
        self.assertEqual(F.decide("S", True, 1, 1, False, 0, 50.0)[0], "exclude")    # every sense flagged
        self.assertEqual(F.decide("S", True, 5, 2, False, 0, 0.1)[0], "keep")
        self.assertEqual(F.decide("H", True, 2, 1, False, 0, 50.0)[0], "exclude")    # half the senses: dominant
        self.assertEqual(F.decide("X", True, 4, 1, True, 0)[0], "exclude")           # first sense flagged
        self.assertEqual(F.decide("X", True, 4, 1, False, 0)[0], "keep")
        self.assertEqual(F.decide("G", True, 3, 2, False, 0)[0], "exclude")
        self.assertEqual(F.decide("I", False, 1, 1, True, 0)[0], "keep")             # pejorative only
        self.assertEqual(F.decide("I", False, 1, 1, True, 1)[0], "exclude")          # ... but LDNOOBW lists it
        self.assertEqual(F.decide("I", True, 1, 1, True, 0)[0], "exclude")

    def test_candidates_end_to_end_prints_no_word(self):
        with tempfile.TemporaryDirectory() as d:
            wikt = os.path.join(d, "raw.jsonl.gz")
            ents = [
                entry("bastrol", sense("Personne d’origine étrangère.", tags=["offensive"])),
                entry("quiblon", sense("Objet rond."), sense("Outil."), sense("Acte sexuel.", tags=["vulgar"])),  # X minor
                entry("vamoche", sense("Acte sexuel.", tags=["vulgar"]), sense("Objet.")),          # X first
                entry("sprindel", sense("Personne sotte.", tags=["pejorative"])),                   # I weak
                entry("noufle", sense("Variante de bastrol.", tags=["offensive"], alt="bastrol")),  # alt -> R
                entry("drabule", sense("Plante.", topics=["sexuality", "anatomy"])),                # not flagged
                entry("zorblats", sense("Pluriel de zorblat.", tags=["vulgar"]), form_of=True),     # form_of skipped
                entry("glabe", sense("Chose.", tags=["vulgar"]), lang="en"),                        # not French
                entry("trambule", sense("Personne.", tags=["offensive"])),                          # not in lexicon
                entry("quarnide", sense("Chose.", tags=["vulgar", "obsolete"]), sense("Outil.")),   # obsolete X
                entry("zv", sense("Acte sexuel.", tags=["vulgar"])),                                 # two letters
                entry("brimol", sense("Chose.", tags=["vulgar"])),                                   # V, kept
                entry("flumpor", sense("Chose.", tags=["vulgar", "abbreviation"]), sense("Outil.")),  # abbreviation
            ]
            with gzip.open(wikt, "wt", encoding="utf-8") as f:
                for o in ents:
                    f.write(json.dumps(o, ensure_ascii=False) + "\n")
            db = os.path.join(d, "prediction.sqlite")
            make_db(db, [(w, w, 1.0) for w in ("bastrol", "quiblon", "vamoche", "sprindel", "noufle", "drabule",
                                                 "glabe", "frenouille", "quarnide", "zv", "flumpor", "brimol")]
                    + [("zorblats", "zorblat", 1.0)])
            seed = os.path.join(d, "fr.txt")
            with open(seed, "w", encoding="utf-8") as f:
                f.write("frenouille\nsprindel\nnot in lexicon\n")
            out = os.path.join(d, "candidates.tsv")
            # the probe prints label ids and counts, never a label name (a raw tag can hold a word)
            labels = os.path.join(d, "labels.tsv")
            rc, txt = run_quiet(F.main, ["probe", "--wikt", wikt, "--all", "--out", labels])
            self.assertEqual(rc, 0)
            self.assertNotIn("offensive", txt)
            self.assertRegex(txt, r"L[0-9a-f]{8}")
            with open(labels, encoding="utf-8") as f:
                self.assertIn("offensive", f.read())
            rc, txt = run_quiet(F.main, ["candidates", "--wikt", wikt, "--ldnoobw", seed, "--db", db, "--out", out])
            self.assertEqual(rc, 0)
            for w in WORDS:
                self.assertNotIn(w, txt)
            rows = {r["lemma"]: r for r in B.read_tsv(out)}
            self.assertEqual(sorted(rows), ["bastrol", "brimol", "frenouille", "noufle", "quiblon", "sprindel",
                                            "vamoche", "zv"])
            self.assertEqual((rows["brimol"]["cat"], rows["brimol"]["decision"]), ("V", "keep"))
            self.assertEqual((rows["zv"]["cat"], rows["zv"]["decision"]), ("X", "keep"))
            self.assertEqual((rows["bastrol"]["cat"], rows["bastrol"]["decision"]), ("R", "exclude"))
            self.assertEqual((rows["noufle"]["cat"], rows["noufle"]["decision"]), ("R", "exclude"))
            self.assertEqual((rows["quiblon"]["cat"], rows["quiblon"]["decision"]), ("X", "keep"))
            self.assertEqual((rows["vamoche"]["cat"], rows["vamoche"]["decision"]), ("X", "exclude"))
            self.assertEqual((rows["sprindel"]["cat"], rows["sprindel"]["ldnoobw"]), ("I", "1"))
            self.assertEqual(rows["sprindel"]["decision"], "exclude")       # weak + dominant + seed
            self.assertEqual((rows["frenouille"]["source"], rows["frenouille"]["decision"]), ("ldnoobw", "keep"))

            # the review page: every row, the words inside one escaped JSON block, never a raw « </ »
            html = os.path.join(d, "review.html")
            rc, txt = run_quiet(M.main, ["--candidates", out, "--out", html])
            self.assertEqual(rc, 0)
            for w in WORDS:
                self.assertNotIn(w, txt)
            with open(html, encoding="utf-8") as f:
                page = f.read()
            self.assertIn("candidates_sha256", page)
            self.assertIn(B.sha256_file(out), page)
            self.assertEqual(page.count("</script>"), 1)
            # no network request: the only link is the per-row Wiktionnaire link the owner may open
            self.assertIn("https://fr.wiktionary.org/wiki/${encodeURIComponent(r.lemma)}", page)
            self.assertEqual(set(re.findall(r"https?://[^/]+/", page)), {"https://fr.wiktionary.org/"})
            self.assertNotIn("fetch(", page)
            data = json.loads(page[page.index("const DATA = ") + 13:page.index(";\nconst META")])
            self.assertEqual(len(data), 8)

            # text batches for a phone review: proposed « exclure » rows first, category R first
            batches = os.path.join(d, "lots")
            rc, txt = run_quiet(M.main, ["--candidates", out, "--out", html, "--batches", batches,
                                         "--batch-size", "3"])
            for w in WORDS:
                self.assertNotIn(w, txt)
            self.assertEqual(sorted(os.listdir(batches)), ["lot-001.txt", "lot-002.txt", "lot-003.txt"])
            with open(os.path.join(batches, "lot-001.txt"), encoding="utf-8") as f:
                first = [l for l in f.read().splitlines() if l.startswith("C0")]
            self.assertEqual(len(first), 3)
            self.assertTrue(all(" | exclure | " in l for l in first))
            self.assertEqual(first[0].split(" | ")[1], "R")

            # ids stay the same across a rerun (the review cites them), even when a new row appears
            before = {r["lemma"]: r["id"] for r in B.read_tsv(out)}
            with open(seed, "a", encoding="utf-8") as f:
                f.write("drabule\n")
            run_quiet(F.main, ["candidates", "--wikt", wikt, "--ldnoobw", seed, "--db", db, "--out", out])
            after = {r["lemma"]: r["id"] for r in B.read_tsv(out)}
            self.assertEqual({k: after[k] for k in before}, before)
            self.assertEqual(len(after), len(before) + 1)
            self.assertNotIn(after["drabule"], before.values())


if __name__ == "__main__":
    unittest.main()
