#!/usr/bin/env python3
"""Unit tests of scan_dumps.py (plan offensive-words A5) — invented words only; the console must never get a word."""
import contextlib
import io
import os
import sqlite3
import sys
import tempfile
import unittest

EXCL = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, EXCL)
import scan_dumps as S  # noqa: E402

WORDS = ["zorblat", "kroutir", "zorblate", "ouflor", "snorfle"]


def run(argv):
    out = io.StringIO()
    with contextlib.redirect_stdout(out):
        rc = S.main(argv)
    return rc, out.getvalue()


class ScanTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.d = self.tmp.name
        self.forms = self.write("forms.tsv", "id\tform\tlemma_id\tcat\nF00001\tkroutir\tC0002\tX\nF00002\touflor\tC0003\tR\n"
                                             "F00003\tzorblat\tC0001\tR\n")
        self.lemmas = self.write("lemmas.tsv", "id\tlemma\tcat\tdecision\treason\tsource\nC0001\tzorblat\tR\texclude\tr\tw\n"
                                               "C0002\tkroutir\tX\texclude\tr\tw\nC0003\touflor\tR\texclude\tr\tw\n"
                                               "C0009\tbastrol\tI\tkeep\tr\tw\n")

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, name, text):
        p = os.path.join(self.d, name)
        with open(p, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        return p

    def assertNoWord(self, txt):
        for w in WORDS:
            self.assertNotIn(w, txt.lower())

    def test_log_offered_and_expected(self):
        log = self.write("cases.log", "header | not a case\n"
                                      "  ✓1 kroutis      → kroutis | kroutis, Zorblat, bastrol\n"
                                      "  ✗  ouflor       → ouflor  | bastrol, l'ouflor\n"
                                      "  ✓1 bastr        → bastrol | bastrol\n")
        rc, txt = run(["--forms", self.forms, log])
        self.assertEqual(rc, 1)
        self.assertNoWord(txt)
        self.assertIn("cases.log: expected 2, offered 2", txt)
        self.assertIn("line 2: offered F00003", txt)                 # case ignored
        self.assertIn("line 3: expected F00002, expected F00002, offered F00002", txt)   # the apostrophe rule

    def test_dump_expected_column_and_clean_file(self):
        dump = self.write("E1-lc.dump", "0\tnon\tbastrol\t3\n1\tnon bastrol\tzorblat\t2\n")
        clean = self.write("clean.dump", "0\tnon\tbastrol\t3\n")
        rc, txt = run(["--forms", self.forms, dump, clean])
        self.assertEqual(rc, 1)
        self.assertIn("E1-lc.dump: expected 1", txt)
        self.assertIn("line 2: expected F00003", txt)
        self.assertIn("clean.dump: 0 hits", txt)
        rc, txt = run(["--forms", self.forms, clean])
        self.assertEqual(rc, 0)
        self.assertIn("total: 0 hits", txt)

    def test_text_mode_counts_every_word(self):
        doc = self.write("notes.md", "Une note sur Kroutir.\nRien ici.\n")
        rc, txt = run(["--forms", self.forms, "--text", doc])
        self.assertEqual(rc, 1)
        self.assertNoWord(txt)
        self.assertIn("notes.md: text 1", txt)
        self.assertIn("line 1: text F00001", txt)

    def test_grammar_forms_get_g_ids(self):
        db = os.path.join(self.d, "prediction.sqlite")
        c = sqlite3.connect(db)
        c.execute("CREATE TABLE words (ortho TEXT, lemma TEXT, cgram TEXT, phon TEXT, freq REAL)")
        c.executemany("INSERT INTO words VALUES (?, ?, 'NOM', '', 1)", [("zorblat", "zorblat"), ("kroutir", "kroutir")])
        c.commit()
        c.close()
        g = os.path.join(self.d, "grammar.sqlite")
        c = sqlite3.connect(g)
        c.execute("CREATE TABLE lemma(id INTEGER PRIMARY KEY, lemma TEXT)")
        c.execute("CREATE TABLE morph(form TEXT, lemma_id INTEGER, tag_id INTEGER)")
        c.executemany("INSERT INTO lemma VALUES (?, ?)", [(1, "zorblat"), (2, "bastrol")])
        c.executemany("INSERT INTO morph VALUES (?, ?, 1)", [("zorblate", 1), ("zorbli", 1), ("zorbli", 2)])
        c.commit()
        c.close()
        doc = self.write("n.txt", "zorblate zorbli\n")
        rc, txt = run(["--forms", self.forms, "--lemmas", self.lemmas, "--grammar", g, "--db", db, "--text", doc])
        self.assertEqual(rc, 1)
        self.assertNoWord(txt)
        self.assertIn("line 1: text G0001", txt)                    # « zorbli » is also an innocent lemma's form: not listed
        self.assertIn("n.txt: text 1", txt)

    def test_hyphenated_form_and_parts(self):
        forms = self.write("f2.tsv", "id\tform\tlemma_id\tcat\nF00001\tkrou-tir\tC0002\tX\nF00002\touflor\tC0003\tR\n")
        doc = self.write("h.txt", "un krou-tir ici\nun bastrol-ouflor là\nun krou seul\n")
        rc, txt = run(["--forms", forms, "--allow", "", "--text", doc])
        self.assertEqual(rc, 1)
        self.assertIn("line 1: text F00001", txt)                   # the whole hyphenated form
        self.assertIn("line 2: part F00002", txt)                   # a listed part of a compound: reported apart
        self.assertIn("h.txt: part 1, text 1", txt)

    def test_allow_list_by_file(self):
        doc = self.write("design.md", "about kroutir here\n")
        other = self.write("other.md", "about kroutir here\n")
        allow = self.write("allow.tsv", "# reviewed\nF00001\tdesign.md\tthe English word (test)\n")
        rc, txt = run(["--forms", self.forms, "--allow", allow, "--text", doc, other])
        self.assertEqual(rc, 1)                                     # other.md still counts
        self.assertIn("design.md: allowed 1", txt)
        self.assertIn("other.md: text 1", txt)
        rc, _ = run(["--forms", self.forms, "--allow", allow, "--text", doc])
        self.assertEqual(rc, 0)

    def test_exempt_pack_name_with_a_capital(self):
        lemmas = self.write("l2.tsv", "id\tlemma\tcat\tdecision\treason\tsource\nN01\tkroutir\tX\tname-ok\towner\towner\n")
        log = self.write("names.log", "  ✓1 Krou        → Kroutir | Kroutir, kroutir\n")
        rc, txt = run(["--forms", self.forms, "--lemmas", lemmas, log])
        self.assertEqual(rc, 1)                                     # the lowercase word still counts
        self.assertIn("names.log: allowed 2, offered 1", txt)       # the case text and the bar's capitalised name

    def test_missing_file_and_no_argument(self):
        rc, _ = run(["--forms", self.forms, os.path.join(self.d, "none.log")])
        self.assertEqual(rc, 1)
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(run(["--forms", self.forms])[0], 2)


if __name__ == "__main__":
    unittest.main()
