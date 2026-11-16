"""Unit tests for build_grammar.py — run: python -I -m unittest discover -s src/Data.Build -p
test_build_grammar.py (from the repo root). The lexicon is tests/french_fixture.lex (≈ 50 lines in the French.lex
format, most taken from French.lex v7.7, MPL-2.0); the real file is only checked through its pin."""
import json
import os
import sqlite3
import struct
import sys
import tempfile
import unittest
from types import SimpleNamespace

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_grammar as G  # noqa: E402
import build_spelling_ref as SR  # noqa: E402

FIXTURE = os.path.join(HERE, "tests", "french_fixture.lex")
PRED_FORMS = ["maisons", "petite", "gris", "les", "mange", "mangent", "mangé", "manger", "est", "sont", "a", "allée",
              "coeur", "coefficient", "évènement", "événement", "aujourd'hui", "yeah", "aux", "vais", "semble"]


def pred_db(d):
    db = os.path.join(d, "p.sqlite")
    if os.path.exists(db):
        return db
    con = sqlite3.connect(db)
    con.execute("CREATE TABLE words (ortho TEXT, phon TEXT, freq REAL)")
    con.executemany("INSERT INTO words VALUES (?, '', 1)", [(w,) for w in PRED_FORMS])
    con.commit()
    con.close()
    return db


def index(path):
    """ortho -> [(lemma, bits)] of pred_index."""
    con = sqlite3.connect(path)
    try:
        lemmas = [r[0] for r in con.execute("SELECT lemma FROM lemma ORDER BY id")]
        out = {}
        for o, a in con.execute("SELECT ortho, a FROM pred_index"):
            out[o] = [(lemmas[l], b) for l, b in struct.iter_unpack("<II", a)]
        return out
    finally:
        con.close()


class DecodeTests(unittest.TestCase):
    def test_tag_table(self):
        self.assertEqual(G.decode(":N:m:s/*"), [G.POS_N | G.MASC | G.SING])
        self.assertEqual(G.decode(":A:e:p"), [G.POS_A | G.MASC | G.FEM | G.PLUR])
        self.assertEqual(G.decode(":G:D:e:p/*"), [G.POS_DET | G.MASC | G.FEM | G.PLUR])
        self.assertEqual(G.decode(":G:Os:O3:m:p/*"), [G.POS_SUBJ | G.MASC | G.PLUR])
        self.assertEqual(G.decode(":V1_it_x__a:Ip:3s/*"), [G.POS_FINITE | G.PERSON["3s"] | G.MOOD_I])
        self.assertEqual(G.decode(":V1_it_x__a:Ip:3p/*"), [G.POS_FINITE | G.PERSON["3p"] | G.MOOD_I])
        self.assertEqual(G.decode(":V1_it_x__a:If:3p!/*"), [G.POS_FINITE | G.PERSON["3p"] | G.MOOD_I])
        self.assertEqual(G.decode(":V1_i__e_e_:Q:f:s/*", "aller"), [G.POS_Q | G.FEM | G.SING | G.AUX_ETRE | G.MODAL])
        self.assertEqual(G.decode(":V1_it_x__a:Y/*"), [G.POS_Y])
        self.assertEqual(G.decode(":V0ei_____a:Ip:3p!/*", "être"), [G.POS_FINITE | G.PERSON["3p"] | G.MOOD_I | G.ETRE])
        self.assertEqual(G.decode(":V0ait____a:Ip:3s/*", "avoir"), [G.POS_FINITE | G.PERSON["3s"] | G.MOOD_I | G.AVOIR])
        # a copula of type « e » that is not être (« sembler ») gets no être bit
        self.assertEqual(G.decode(":V1ei_____a:Ip:3s/*", "sembler"), [G.POS_FINITE | G.PERSON["3s"] | G.MOOD_I])

    def test_two_parts_of_speech_and_marks(self):
        self.assertEqual(G.decode(":N:A:m:i;C/*"), [G.POS_N | G.MASC | G.SING | G.PLUR, G.POS_A | G.MASC | G.SING | G.PLUR])
        self.assertEqual(G.decode(":G:R:D:e:p/*"), [G.POS_DET | G.MASC | G.FEM | G.PLUR, G.POS_PREP])
        # « mangé-je »: the interrogative 1ŝ is not a finite analysis of a participle tag
        self.assertEqual(G.decode(":V1_it_x__a:Q:A:m:s:1ŝ/*"), [G.POS_A | G.MASC | G.SING, G.POS_Q | G.MASC | G.SING])
        self.assertEqual(G.decode(":V1_it_x__a:Ip:Sp:1s:3s/*"),
                         [G.POS_FINITE | G.PERSON["1s"] | G.PERSON["3s"] | G.MOOD_I | G.MOOD_S])
        self.assertEqual(G.decode(":MP:e:i/*"), [G.POS_OTHER])
        self.assertEqual(G.decode(":W/*"), [G.POS_ADV])


class LookupTests(unittest.TestCase):
    known = {"cœur", "coefficient", "aujourd’hui", "OK", "maison"}

    def test_oe_only_when_the_result_exists(self):
        self.assertEqual(G.lookup("coeur", self.known), "cœur")
        self.assertEqual(G.lookup("coefficient", self.known), "coefficient")
        self.assertIsNone(G.lookup("moelle", self.known))

    def test_apostrophe_and_case(self):
        self.assertEqual(G.lookup("aujourd'hui", self.known), "aujourd’hui")
        self.assertEqual(G.lookup("Maison", self.known), "maison")
        self.assertIsNone(G.lookup("ok", self.known))     # lowercase is tried, never the other way round


class BuildTests(unittest.TestCase):
    def build(self, d, name="g.sqlite"):
        out = os.path.join(d, name)
        sha, counts = G.build(FIXTURE, pred_db(d), out, SR.sha256(FIXTURE))
        return out, sha, counts

    def test_or_merged_per_lemma_and_pos(self):
        with tempfile.TemporaryDirectory() as d:
            out, _, counts = self.build(d)
            ix = index(out)
            # « mange »: Ip:Sp:1s:3s and E:2s are one finite analysis of manger
            self.assertEqual(ix["mange"], [("manger", G.POS_FINITE | G.PERSON["1s"] | G.PERSON["2s"] | G.PERSON["3s"]
                                            | G.MOOD_I | G.MOOD_S | G.MOOD_E)])
            self.assertIn(("être", G.POS_FINITE | G.PERSON["3s"] | G.MOOD_I | G.ETRE), ix["est"])
            self.assertIn(("est", G.POS_N | G.MASC | G.SING), ix["est"])
            self.assertIn(("aller", G.POS_Q | G.FEM | G.SING | G.AUX_ETRE | G.MODAL), ix["allée"])
            self.assertEqual(ix["vais"], [("aller", G.POS_FINITE | G.PERSON["1s"] | G.MOOD_I | G.AUX_ETRE | G.MODAL)])
            self.assertEqual(ix["coeur"], [("cœur", G.POS_N | G.MASC | G.SING)])
            self.assertIn("aujourd'hui", ix)
            self.assertNotIn("yeah", ix)
            self.assertEqual(counts["pred_index"], len(PRED_FORMS) - 1)
            # the two « événement » lines (dictionaries /M and /*) are one analysis
            self.assertEqual(counts["lex_lines"] - counts["morph"], 1)

    def test_both_1990_spellings_in_morph(self):
        with tempfile.TemporaryDirectory() as d:
            out, _, _ = self.build(d)
            con = sqlite3.connect(out)
            forms = {r[0] for r in con.execute("SELECT form FROM morph WHERE form IN ('évènement', 'événement')")}
            con.close()
            self.assertEqual(forms, {"évènement", "événement"})

    def test_deterministic_bytes(self):
        with tempfile.TemporaryDirectory() as d:
            _, a, _ = self.build(d, "a.sqlite")
            _, b, _ = self.build(d, "b.sqlite")
            self.assertEqual(a, b)
            with open(os.path.join(d, "a.sqlite"), "rb") as f1, open(os.path.join(d, "b.sqlite"), "rb") as f2:
                self.assertEqual(f1.read(), f2.read())

    def test_sha_mismatch_refuses_and_source_written(self):
        with tempfile.TemporaryDirectory() as d:
            raw = os.path.join(d, "raw")
            os.makedirs(raw)
            with open(FIXTURE, "rb") as s, open(os.path.join(raw, "French.lex"), "wb") as t:
                t.write(s.read())
            db = pred_db(d)
            outdir = os.path.join(d, "out")
            os.makedirs(outdir)
            out = os.path.join(outdir, "grammar.sqlite")
            a = SimpleNamespace(lock=G.LOCK, raw=raw, db=db, out=out)
            with self.assertRaises(SystemExit):          # not the pinned French.lex
                G.cmd_build(a)
            self.assertFalse(os.path.exists(out))
            # a lock pinning the fixture (and the spelling reference patched alike) builds
            with open(G.LOCK, encoding="utf-8") as f:
                lock = json.load(f)
            sha = SR.sha256(FIXTURE)
            lock["grammalecte"]["files"]["lexicons/French.lex"]["sha256"] = sha
            lp = os.path.join(d, "lock.json")
            with open(lp, "w", encoding="utf-8") as f:
                json.dump(lock, f)
            old = SR.SHA256
            SR.SHA256 = sha
            try:
                G.cmd_build(SimpleNamespace(lock=lp, raw=raw, db=db, out=out))
            finally:
                SR.SHA256 = old
            src = os.path.join(outdir, "grammar", "SOURCE.txt")
            self.assertTrue(os.path.exists(os.path.join(outdir, "grammar", "MPL-2.0.txt")))
            with open(src, encoding="utf-8") as f:
                text = f.read()
            self.assertIn("Larger Work", text)
            self.assertIn(SR.sha256(out), text)
            self.assertIn(lock["grammalecte"]["commit"], text)


class ArchiveTests(unittest.TestCase):
    def test_fetch_from_the_pinned_archive_checks_every_hash(self):
        import io
        import tarfile
        with tempfile.TemporaryDirectory() as d:
            lock = G.load_lock()
            commit = lock["commit"]
            fixture = open(FIXTURE, "rb").read()
            tar = os.path.join(d, "a.tar.gz")
            with tarfile.open(tar, "w:gz") as t:
                for rel in lock["files"]:
                    data = fixture if rel.endswith("French.lex") else rel.encode("utf-8")
                    info = tarfile.TarInfo("grammalecte-%s/%s" % (commit, rel))
                    info.size = len(data)
                    t.addfile(info, io.BytesIO(data))
            # the real lock pins another archive: refused before anything is read
            raw = os.path.join(d, "raw")
            with self.assertRaises(SystemExit):
                G.cmd_fetch(SimpleNamespace(lock=G.LOCK, raw=raw, from_archive=tar, from_dir=None))
            # a lock pinning this archive and its members: the four files are written, SHA-256 checked
            import hashlib
            lock["archive"]["sha256"] = SR.sha256(tar)
            for rel, info in lock["files"].items():
                data = fixture if rel.endswith("French.lex") else rel.encode("utf-8")
                info["sha256"] = hashlib.sha256(data).hexdigest()
            lp = os.path.join(d, "lock.json")
            with open(lp, "w", encoding="utf-8") as f:
                json.dump({"grammalecte": lock}, f)
            G.cmd_fetch(SimpleNamespace(lock=lp, raw=raw, from_archive=tar, from_dir=None))
            self.assertEqual(sorted(os.listdir(raw)), ["French.lex", "French.tagset.txt", "phonet_simil.txt", "rules.grx"])


class LockTests(unittest.TestCase):
    def test_french_lex_pin_equals_the_spelling_reference(self):
        lock = G.load_lock()
        self.assertEqual(lock["commit"], SR.COMMIT)
        self.assertEqual(lock["files"]["lexicons/French.lex"]["sha256"], SR.SHA256)
        for info in lock["files"].values():
            self.assertRegex(info["sha256"], "^[0-9a-f]{64}$")


if __name__ == "__main__":
    unittest.main()
