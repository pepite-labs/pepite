"""Unit tests for build_pictos.py — run: python -I -m unittest src/Data.Build/test_build_pictos.py
(from the repo root) or ``python -I test_build_pictos.py`` from this folder. Fixtures: tests/ (hand-made)."""
import contextlib
import io
import json
import re
import os
import struct
import sys
import tempfile
import unittest
import urllib.error
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import build_pictos as B  # noqa: E402

FIX = os.path.join(HERE, "tests")


def load_fixture():
    with open(os.path.join(FIX, "index_fixture.json"), encoding="utf-8") as f:
        idx = B.build_keyword_index(json.load(f))
    readings = B.load_readings(os.path.join(FIX, "lexique_fixture.tsv"))
    return idx, readings


def tiny_png(w=2, h=2):
    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    raw = b"".join(b"\x00" + b"\xff" * (w * 3) for _ in range(h))
    return (B.PNG_SIG + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


class MappingTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.idx, cls.readings = load_fixture()

    def pick(self, form):
        return B.choose(form, self.readings.get(form, []), self.idx)

    def test_ete_gets_no_picture_aux_none(self):
        # dominant reading = être (verb), not summer; and être/avoir never get a picture (owner decision)
        c = self.pick("été")
        self.assertEqual((c.picto_id, c.rule, c.lemma, c.cgram), (None, "aux-none", "être", "VER"))

    def test_auxiliary_forms_get_no_picture(self):
        R = B.Reading
        cases = {"est": [R("être", "VER", 19418), R("est", "NOM", 82)], "sont": [R("être", "VER", 1449)],
                 "ai": [R("avoir", "AUX", 4902), R("avoir", "VER", 2476)], "a": [R("avoir", "AUX", 6351), R("a", "NOM", 81)],
                 "avait": [R("avoir", "AUX", 3116)], "eu": [R("avoir", "VER", 668)],
                 "avoir": [R("avoir", "AUX", 674), R("avoir", "NOM", 3)],
                 # Lexique splits être over AUX/VER: summed, it beats suivre
                 "suis": [R("suivre", "VER", 1819), R("être", "AUX", 1272), R("être", "VER", 669)]}
        for form, r in cases.items():
            self.assertEqual(B.choose(form, r, self.idx).rule, "aux-none", form)
            self.assertIsNone(B.choose(form, r, self.idx).picto_id, form)
        # a noun whose lemma merely looks like être is untouched
        self.assertIsNone(B.excluded_rule("lune", [R("lune", "NOM", 50)]))

    def test_articles_get_no_picture_art_none(self):
        R = B.Reading
        cases = {"le": [R("le", "ART:def", 18311), R("le", "PRO:per", 3301)], "la": [R("la", "ART:def", 23634)],
                 "les": [R("les", "ART:def", 14662)], "l": [R("l", "ART:def", 78)], "un": [R("un", "ART:ind", 13551)],
                 "une": [R("une", "ART:ind", 9588)], "des": [R("des", "ART:ind", 10625)], "du": [R("du", "ART:def", 6882)],
                 "au": [R("au", "ART:def", 5322)], "aux": [R("aux", "ART:def", 1908)]}
        for form, r in cases.items():
            self.assertEqual(B.choose(form, r, self.idx).rule, "art-none", form)
        self.assertEqual(B.excluded_rule("Des", []), "art-none")       # listed form even without readings
        # elided forms are single letters: never the alphabet picture (« d' » showed a letter D)
        for f in ("d", "j", "s", "n", "y"):
            self.assertEqual(B.excluded_rule(f, [R(f, "PRE", 55)]), "letter-none", f)
        self.assertEqual(self.pick("je")[:2], (70, "form"))              # pronouns keep their picture
        m, ex = {}, {}
        m = B.build_mapping(["été", "le", "maisons"], self.readings, self.idx, {}, (), ex)
        self.assertEqual(sorted(m), ["maisons"])
        self.assertEqual(ex, {"été": "aux-none", "le": "art-none"})

    def test_porte_noun_dominant_kw_rank_0(self):
        c = self.pick("porte")
        self.assertEqual((c.picto_id, c.rule, c.cgram), (20, "form", "NOM"))   # 19 has porte at rank 1

    def test_plural_and_feminine_and_verb_via_lemma(self):
        self.assertEqual(self.pick("maisons")[:2], (30, "lemma"))
        self.assertEqual(self.pick("grande")[:2], (40, "lemma"))   # 41 has "grand" only as a plural
        self.assertEqual(self.pick("mange")[:2], (60, "lemma"))    # keyword "Manger" normalised

    def test_plural_keyword_is_a_form_match(self):
        self.assertEqual(self.pick("portes")[:2], (20, "form-anytype"))   # no Lexique reading → any-type rule
        r = [B.Reading("porte", "NOM", 10.0)]
        self.assertEqual(B.choose("portes", r, self.idx)[:2], (20, "form"))

    def test_tie_break_order(self):
        # 9 flagged, 71 type 1, 99 kw_rank 1, 100 not aac, 102 schematic, 101 < 103
        self.assertEqual(self.pick("lune").picto_id, 101)
        cands = [B.Cand(5, 2, 1, False, True, False), B.Cand(6, 2, 0, False, False, False),
                 B.Cand(7, 2, 0, False, True, True), B.Cand(8, 2, 0, False, True, False),
                 B.Cand(3, 2, 0, False, True, False)]
        self.assertEqual(min(cands, key=B._tiebreak).id, 3)
        # an exact keyword beats a plural (« fils » = son, not the plural of « fil »)
        fils = [B.Cand(2978, 2, 0, True, False, False), B.Cand(9887, 2, 0, False, False, False)]
        self.assertEqual(min(fils, key=B._tiebreak).id, 9887)

    def test_type_rules(self):
        self.assertIsNone(self.pick("pas"))                        # ADV dominant; only a noun picture
        self.assertEqual(self.pick("je")[:2], (70, "form"))        # pronoun filed as type 1 (deviation)
        self.assertFalse(B.compatible("NOM", 1))
        self.assertFalse(B.compatible("VER", 2))
        self.assertTrue(B.compatible("ADJ:num", 2))
        self.assertTrue(B.compatible("NOM", None))
        self.assertEqual(self.pick("zorg")[:2], (90, "form-anytype"))   # single reading, any type

    def test_dominance_threshold(self):
        r = [B.Reading("a", "PRE", 100.0), B.Reading("b", "NOM", 20.0), B.Reading("c", "NOM", 19.99)]
        self.assertEqual([x.lemma for x in B.dominant(r)], ["a", "b"])
        self.assertIsNone(self.pick("vers"))                       # NOM « vers » (60) < 0.2 × 350
        r2 = [B.Reading("vers", "PRE", 350.0), B.Reading("vers", "NOM", 70.0)]
        self.assertEqual(B.choose("vers", r2, self.idx)[:2], (50, "form"))

    def test_flagged_excluded_and_bad_keys_dropped(self):
        self.assertNotIn(9, [c.id for c in self.idx["lune"]])
        self.assertEqual([c.id for c in self.idx["être"]], [11])  # 12 is flagged violence
        for k in ("grand-père", "pomme de terre", "pomme", "aujourd'hui"):
            self.assertNotIn(k, self.idx)

    def test_overrides(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "o.tsv")
            with open(p, "w", encoding="utf-8") as f:
                f.write("# comment\nLune\t100\tcurated\nporte\t-\tno picture\n")
            ov = B.load_overrides(p)
        self.assertEqual(ov, {"lune": 100, "porte": None})
        ex = {}
        m = B.build_mapping(["lune", "porte", "mange"], self.readings, self.idx, ov, (), ex)
        self.assertEqual(m["lune"][:2], (100, "override"))
        self.assertNotIn("porte", m)
        self.assertEqual(ex, {"porte": "override-none"})
        self.assertEqual(m["mange"].picto_id, 60)
        m2 = B.build_mapping(["mange"], self.readings, self.idx, {}, missing={60})
        self.assertNotIn("mange", m2)

    def test_norm(self):
        self.assertEqual(B.norm("Aujourd’Hui"), "aujourd'hui")
        self.assertEqual(B.norm("été"), "été")


class FakeResp(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


class FakeOpener:
    def __init__(self, script):
        self.script = script   # id → list of responses (bytes or int HTTP code)
        self.calls = []
        self.urls = []

    def open(self, req, timeout=None):
        pid = int(re.search(r"/pictograms/(\d+)", req.full_url).group(1))
        self.calls.append(pid)
        self.urls.append(req.full_url)
        r = self.script[pid].pop(0)
        if isinstance(r, int):
            hdr = {"Retry-After": "7"} if r == 429 else {}
            raise urllib.error.HTTPError(req.full_url, r, "x", hdr, None)
        return FakeResp(r)


class FetchTests(unittest.TestCase):
    def test_resume_part_ignored_corrupt_refetched_404_and_retry(self):
        good = tiny_png()
        with tempfile.TemporaryDirectory() as d:
            png = os.path.join(d, "png300")
            os.makedirs(png)
            with open(os.path.join(png, "1.png"), "wb") as f:
                f.write(good)                                  # valid → skipped
            with open(os.path.join(png, "2.png.part"), "wb") as f:
                f.write(good[:20])                             # stale .part → ignored, 2 fetched
            with open(os.path.join(png, "3.png"), "wb") as f:
                f.write(b"GIF89a....")                         # corrupt → refetched
            with open(os.path.join(png, "5.png"), "wb") as f:
                f.write(good[:-12])                            # truncated (no IEND) → refetched
            op = FakeOpener({2: [good], 3: [good], 4: [404, 404], 5: [429, good], 6: [404, good]})
            slept = []
            got, skip, miss = B.fetch_images([1, 2, 3, 4, 5, 6], png, os.path.join(d, "m.tsv"),
                                             os.path.join(d, "missing.tsv"), opener=op, delay=0.3,
                                             sleep=slept.append, log=lambda *_: None)
            self.assertEqual((got, skip, miss), (4, 1, [4]))
            self.assertEqual(op.calls, [2, 3, 4, 4, 5, 5, 6, 6])      # 404 on the CDN → one API retry
            self.assertIn("api.arasaac.org", op.urls[-1])
            self.assertIn(7, slept)                             # Retry-After honoured
            self.assertTrue(all(s >= 0.3 for s in slept))
            for i in (2, 3, 5, 6):
                self.assertTrue(B.png_ok(os.path.join(png, f"{i}.png")))
            with open(os.path.join(d, "m.tsv"), encoding="utf-8") as f:
                self.assertEqual(len(f.read().splitlines()), 4)
            with open(os.path.join(d, "missing.tsv"), encoding="utf-8") as f:
                self.assertTrue(f.read().startswith("4\t404"))

    def test_abort_after_consecutive_failures(self):
        with tempfile.TemporaryDirectory() as d:
            op = FakeOpener({1: [500] * 30})
            with self.assertRaises(SystemExit):
                B.fetch_images([1], d, os.path.join(d, "m"), os.path.join(d, "x"), opener=op,
                               sleep=lambda s: None, log=lambda *_: None, max_fail=5)
            self.assertEqual(len(op.calls), 5)

    def test_png_ok(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "a.png")
            b = bytearray(tiny_png())
            with open(p, "wb") as f:
                f.write(b)
            self.assertTrue(B.png_ok(p))
            b[20] ^= 0xFF                                       # IHDR CRC mismatch
            with open(p, "wb") as f:
                f.write(b)
            self.assertFalse(B.png_ok(p))
            self.assertFalse(B.png_ok(os.path.join(d, "none.png")))


class AttributionTests(unittest.TestCase):
    def test_attribution_fr(self):
        a = B.attribution_fr()
        self.assertIn("Sergio Palao", a)
        self.assertIn("Gouvernement d'Aragon", a)
        self.assertNotIn("{", a)



class DataPathTest(unittest.TestCase):
    """D18: no bulk-data default under the source tree; a clear error instead."""

    def setUp(self):
        self._env = {k: os.environ.pop(k, None) for k in ("PREDICT_PICTOS_RAW", "PREDICT_CORPUS_DIR")}

    def tearDown(self):
        for k, v in self._env.items():
            if v is not None:
                os.environ[k] = v

    def test_raw_is_required(self):
        err = io.StringIO()
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(err):
            B.main(["fetch-index"])
        self.assertIn("PREDICT_PICTOS_RAW", err.getvalue())

    def test_report_needs_the_held_out_texts(self):
        err = io.StringIO()
        with self.assertRaises(SystemExit), contextlib.redirect_stderr(err):
            B.main(["--raw", "x", "report", "--db", "y"])
        self.assertIn("PREDICT_CORPUS_DIR", err.getvalue())


if __name__ == "__main__":
    unittest.main()
