"""Unit tests for build_spelling_ref.py — run: python -I -m unittest discover -s src/Data.Build -p
test_build_spelling_ref.py (from the repo root). A hand-made French.lex excerpt; the SHA-256 check is exercised with a
wrong file."""
import os
import sqlite3
import sys
import tempfile
import unittest
from types import SimpleNamespace

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_spelling_ref as B  # noqa: E402

LEX = """# This Source Code Form is subject to the terms of the Mozilla Public
# Lexique simplifié pour Grammalecte v7.7

# :POS ;LEX ~SEM =FQ /DIC
maison\tmaison\t:N:f:s/*
cœur\tcœur\t:N:m:s/*
œil\tœil\t:N:m:s/*
poète\tpoète\t:N:e:s/*
OK\tOK\t:J/*
"""


class SpellingRefTests(unittest.TestCase):
    def test_reads_forms_and_skips_comments(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "French.lex")
            with open(p, "w", encoding="utf-8") as f:
                f.write(LEX)
            rows = list(B.read_lex(p))
            self.assertEqual(rows[0], ("maison", "maison", ":N:f:s/*"))
            self.assertEqual(len(rows), 5)

    def test_trusted_with_oe_written_oe_ligature(self):
        ref = {"maison", "cœur", "œil", "poète", "OK"}
        self.assertTrue(B.trusted("coeur", ref))       # Lexique writes « coeur »
        self.assertTrue(B.trusted("oeil", ref))
        self.assertTrue(B.trusted("poète", ref))
        self.assertFalse(B.trusted("yeah", ref))
        self.assertFalse(B.trusted("ok", ref))          # case significant: the reference has « OK »
        self.assertEqual(B.unlisted(["maison", "yeah", "mam", "coeur", "l'", "aujourd'hui", "peut-être"], ref), ["mam", "yeah"])

    def _db(self, d, legacy=False):
        db = os.path.join(d, "p.sqlite")
        con = sqlite3.connect(db)
        con.execute("CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL)")
        con.executemany("INSERT INTO words VALUES (?, '', 1)", [("maison",), ("yeah",), ("coeur",)])
        if legacy:                                           # pack 2026.11 and older: the tables inside the database
            con.execute("CREATE TABLE spelling_unlisted (ortho TEXT PRIMARY KEY)")
            con.execute("INSERT INTO spelling_unlisted VALUES ('yeah')")
            con.execute("CREATE TABLE spelling_source (source TEXT, sha256 TEXT, reference_forms INTEGER, unlisted INTEGER)")
        con.commit()
        con.close()
        return db

    def _build(self, d, db, out):
        old = B.SHA256
        lex = os.path.join(d, "French.lex")
        B.SHA256 = B.sha256(lex)
        try:
            B.cmd_build(SimpleNamespace(raw=d, db=db, out=out))
        finally:
            B.SHA256 = old

    def _lex(self, d):
        with open(os.path.join(d, "French.lex"), "w", encoding="utf-8") as f:
            f.write(LEX)

    def test_build_writes_spelling_sqlite_with_its_notices_and_refuses_a_wrong_file(self):
        with tempfile.TemporaryDirectory() as d:
            self._lex(d)
            db = self._db(d)
            out = os.path.join(d, "spelling.sqlite")
            with self.assertRaises(SystemExit):                  # not the pinned file
                B.cmd_build(SimpleNamespace(raw=d, db=db, out=out))
            before = B.sha256(db)
            self._build(d, db, out)
            first = B.sha256(out)
            self._build(d, db, out)                              # twice: replaced, byte for byte the same
            self.assertEqual(first, B.sha256(out))
            self.assertEqual(before, B.sha256(db))               # the prediction database is only read
            con = sqlite3.connect(out)
            self.assertEqual([r[0] for r in con.execute("SELECT ortho FROM spelling_unlisted")], ["yeah"])
            self.assertEqual(con.execute("SELECT unlisted FROM spelling_source").fetchone()[0], 1)
            con.close()
            src = open(os.path.join(d, "spelling", "SOURCE.txt"), encoding="utf-8").read()
            self.assertIn("MPL-2.0", src)
            self.assertIn("spelling.sqlite SHA-256: " + first, src)
            self.assertTrue(os.path.exists(os.path.join(d, "spelling", "MPL-2.0.txt")))

    def test_build_drops_the_legacy_tables_from_the_prediction_database(self):
        with tempfile.TemporaryDirectory() as d:
            self._lex(d)
            db = self._db(d, legacy=True)
            self._build(d, db, os.path.join(d, "spelling.sqlite"))
            con = sqlite3.connect(db)
            names = {n for (n,) in con.execute("SELECT name FROM sqlite_master WHERE type = 'table'")}
            con.close()
            self.assertEqual(names, {"words"})
            self.assertEqual(B.unlisted_count(os.path.join(d, "spelling.sqlite")), 1)

    def test_release_check_counts_the_table(self):
        with tempfile.TemporaryDirectory() as d:
            db = os.path.join(d, "s.sqlite")
            self.assertIsNone(B.unlisted_count(db))            # no file
            con = sqlite3.connect(db)
            con.execute("CREATE TABLE words (ortho TEXT)")
            con.commit()
            con.close()
            self.assertIsNone(B.unlisted_count(db))            # no table: refused by size_report.py
            con = sqlite3.connect(db)
            con.execute("CREATE TABLE spelling_unlisted (ortho TEXT PRIMARY KEY)")
            con.execute("INSERT INTO spelling_unlisted VALUES ('yeah')")
            con.commit()
            con.close()
            self.assertEqual(B.unlisted_count(db), 1)

    def test_fetch_from_a_local_copy_checks_the_hash(self):
        with tempfile.TemporaryDirectory() as d:
            src = os.path.join(d, "x.lex")
            with open(src, "w", encoding="utf-8") as f:
                f.write(LEX)
            raw = os.path.join(d, "raw")
            with self.assertRaises(SystemExit):
                B.cmd_fetch(SimpleNamespace(raw=raw, source=src))
            self.assertFalse(os.path.exists(os.path.join(raw, "French.lex")))


if __name__ == "__main__":
    unittest.main()
