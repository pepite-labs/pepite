#!/usr/bin/env python3
"""Unit tests of the corpus scripts (plan corpus-light 0.8), tiny fixtures only, no network.
Run: python -I -m unittest discover -s src/Data.Build/corpus/tests"""
import hashlib
import io
import json
import os
import random
import shutil
import sqlite3
import sys
import tempfile
import unittest

CORPUS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA_BUILD = os.path.dirname(CORPUS)
sys.path.insert(0, CORPUS)
sys.path.insert(0, DATA_BUILD)
import build_4grams  # noqa: E402
import build_ngrams  # noqa: E402
import compare_runs as cr  # noqa: E402
import db_checks  # noqa: E402
import fetch_corpus as fc  # noqa: E402
import prepare_corpus as pc  # noqa: E402
import write_sources as ws  # noqa: E402

try:
    import libzim  # noqa: F401
    HAVE_LIBZIM = True
except ImportError:
    HAVE_LIBZIM = False


class TmpDir(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="pepite-corpus-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write(self, name, text, mode="w"):
        p = os.path.join(self.tmp, name)
        os.makedirs(os.path.dirname(p), exist_ok=True)
        if "b" in mode:
            with open(p, mode) as f:
                f.write(text)
        else:
            with open(p, mode, encoding="utf-8", newline="\n") as f:
                f.write(text)
        return p


# ---------------------------------------------------------------- 0.1 fold

class Fold(unittest.TestCase):
    SAMPLES = ["cœur", "sœur", "œuvre", "nævus", "ex æquo", "coeur", "été", "ŒUF", "", "bœufs"]

    def test_all_copies_agree(self):
        for w in self.SAMPLES:
            ref = pc.fold(w)
            for mod in (build_ngrams, build_4grams, db_checks):
                self.assertEqual(mod.fold(w), ref, (mod.__name__, w))

    def test_values(self):
        self.assertEqual(pc.fold("cœur"), "coeur")
        self.assertEqual(pc.fold("nævus"), "naevus")
        self.assertEqual(pc.tokenize("Mon cœur, ma sœur et l'Œuvre"), ["mon", "coeur", "ma", "soeur", "et", "l", "oeuvre"])
        self.assertEqual(pc.tokenize("mon cœur"), pc.tokenize("mon coeur"))

    def test_builders_store_folded_ngrams(self):
        tmp = tempfile.mkdtemp(prefix="pepite-fold-")
        try:
            corpus = os.path.join(tmp, "c.txt")
            with open(corpus, "w", encoding="utf-8") as f:
                f.write("mon cœur bat fort\n" * 4)
            db = os.path.join(tmp, "p.sqlite")
            build_ngrams.build(db, [corpus], 3, 3)
            build_4grams.build(db, [corpus], 4)
            con = sqlite3.connect(db)
            self.assertEqual(con.execute("SELECT c FROM bigrams WHERE prev='mon' AND word='coeur'").fetchone(), (4,))
            self.assertEqual(con.execute("SELECT COUNT(*) FROM bigrams WHERE word LIKE '%œ%' OR prev LIKE '%œ%'").fetchone(), (0,))
            self.assertEqual(con.execute("SELECT word FROM fourgrams").fetchall(), [("fort",)])
            con.close()
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


# ---------------------------------------------------------------- 0.5 extraction

class Sentences(unittest.TestCase):
    def test_splitter(self):
        t = "Il pleut. Le chat dort ! « Viens ici », dit-il. M. Dupont arrive. J. Verne écrit… — Oui, dit Paul."
        self.assertEqual(pc.split_sentences(t), [
            "Il pleut.", "Le chat dort !", "« Viens ici », dit-il.", "M. Dupont arrive.", "J. Verne écrit…", "— Oui, dit Paul."])

    def test_splitter_keeps_lowercase_continuation(self):
        self.assertEqual(pc.split_sentences("Il dit: etc. et puis rien."), ["Il dit: etc. et puis rien."])

    def test_dialogue_dash(self):
        for s in ("— Oui, maman.", "-- Oui, maman.", "- Oui, maman.", "– Oui, maman."):
            self.assertEqual(pc.strip_dialogue(s), "Oui, maman.")

    def test_short_sentences_dropped(self):
        self.assertEqual(pc.keep(["Oui.", "Il fait très beau.", "Viens ici vite"]), ["Il fait très beau."])


GUTENBERG = """\ufeffThe Project Gutenberg EBook of Contes, by Someone

This eBook is for the use of anyone anywhere at no cost.

*** START OF THIS PROJECT GUTENBERG EBOOK CONTES ***




Produced by Some Volunteers and the Online Distributed
Proofreading Team.




TABLE DES MATIÈRES

I. Le petit chat ........ 5
II. La grande maison ..... 12
III. Le retour ........... 20


CHAPITRE I

LE PETIT CHAT

Il était une fois un petit chat qui vivait dans
une grande maison au bord de la rivière.

--Où vas-tu? lui demanda sa mère.

--Je vais voir le monde, répondit le petit chat.

[Illustration: Le chat]

End of the Project Gutenberg EBook of Contes, by Someone

*** END OF THIS PROJECT GUTENBERG EBOOK CONTES ***

***** This file should be named 12345-8.txt *****
Licence text that must not be read as a book.
"""


class Gutenberg(unittest.TestCase):
    def test_header_footer_toc_dashes(self):
        ss = pc.gutenberg_sentences(GUTENBERG.lstrip("\ufeff"))
        text = " ".join(ss)
        self.assertNotIn("Project Gutenberg", text)
        self.assertNotIn("Produced by", text)
        self.assertNotIn("Licence text", text)
        self.assertNotIn("....", text)
        self.assertNotIn("Illustration", text)
        self.assertIn("Il était une fois un petit chat qui vivait dans une grande maison au bord de la rivière.", ss)
        self.assertIn("Où vas-tu? lui demanda sa mère.", ss)
        self.assertIn("Je vais voir le monde, répondit le petit chat.", ss)
        self.assertFalse(any(s.startswith(("-", "—")) for s in ss))

    def test_read_decodes_latin1_and_bom(self):
        tmp = tempfile.mkdtemp(prefix="pepite-gb-")
        try:
            p = os.path.join(tmp, "b.txt")
            with open(p, "wb") as f:
                f.write("Ça été\r\n".encode("iso-8859-1"))
            self.assertEqual(pc.read_gutenberg(p, "iso-8859-1"), "Ça été\n")
            with open(p, "wb") as f:
                f.write("\ufeffÇa\r\n".encode("utf-8"))
            self.assertEqual(pc.read_gutenberg(p, "utf-8"), "Ça\n")
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


ARTICLE = """<html><head><title>Chat</title><style>p{}</style></head><body>
<table class="infobox"><tr><td>Nom : chat</td></tr></table>
<p>Le <b>chat</b> est un petit mammifère carnivore.<sup class="reference">[1]</sup> Il vit souvent avec les humains.</p>
<ul><li>Une liste qui ne compte pas comme du texte suivi.</li></ul>
<p>Les chats dorment beaucoup pendant la journée.</p>
<h2>Voir aussi</h2></body></html>"""

DISAMBIG = """<html><body><div><i>Sur cette page d'homonymie, tu trouveras différents sujets qui ont le même nom !</i></div>
<p><b>Mercure</b> désigne plusieurs choses.</p><ul><li>la planète ;</li><li>le dieu romain.</li></ul></body></html>"""

# an ordinary article that links to a disambiguation page (MediaWiki gives the link the class « mw-disambig »)
LINKS_TO_DISAMBIG = """<p>Le nombre <a class="mw-disambig" href="Dix">dix</a> suit le nombre neuf dans la suite des entiers.</p>"""


class Vikidia(unittest.TestCase):
    def test_p_extraction(self):
        paras, _ = pc.html_paragraphs(ARTICLE)
        self.assertEqual(paras, ["Le chat est un petit mammifère carnivore. Il vit souvent avec les humains.",
                                 "Les chats dorment beaucoup pendant la journée."])
        self.assertEqual(pc.article_sentences("Chat", ARTICLE), [
            "Le chat est un petit mammifère carnivore.", "Il vit souvent avec les humains.",
            "Les chats dorment beaucoup pendant la journée."])

    def test_disambiguation_dropped(self):
        self.assertEqual(pc.article_sentences("Mercure", DISAMBIG), [])
        self.assertEqual(pc.article_sentences("Mercure (homonymie)", ARTICLE), [])
        plain = "<p>Paris peut désigner la capitale.</p>"
        self.assertEqual(pc.article_sentences("Paris", plain), [])
        self.assertEqual(pc.article_sentences("CO", "<p><b>CO</b> peut être : </p><ul><li>le monoxyde</li></ul>"), [])
        self.assertEqual(pc.article_sentences("10 (nombre)", LINKS_TO_DISAMBIG),
                         ["Le nombre dix suit le nombre neuf dans la suite des entiers."])

    def test_non_article_pages_skipped(self):
        for t in ("Portail:Lyon/Aide", "Catégorie:Animal", "Vikidia:Accueil", "Modèle:Infobox", "Aide:Index"):
            self.assertFalse(pc.is_article_title(t), t)
        for t in ("Chat", "Andromède (galaxie)", "Star Wars : Épisode IV", "Kiss (musique)"):
            self.assertTrue(pc.is_article_title(t), t)

    @unittest.skipUnless(HAVE_LIBZIM, "libzim not installed (requirements-corpus.txt)")
    def test_three_article_zim(self):
        from libzim.writer import Creator, Hint, Item, StringProvider

        class Page(Item):
            def __init__(self, path, title, html):
                super().__init__()
                self.p, self.t, self.h = path, title, html

            def get_path(self):
                return self.p

            def get_title(self):
                return self.t

            def get_mimetype(self):
                return "text/html"

            def get_contentprovider(self):
                return StringProvider(self.h)

            def get_hints(self):
                return {Hint.FRONT_ARTICLE: True}

        tmp = tempfile.mkdtemp(prefix="pepite-zim-")
        try:
            zp = os.path.join(tmp, "t.zim")
            with Creator(zp).config_indexing(False, "fra") as c:
                c.add_item(Page("Chat", "Chat", ARTICLE))
                c.add_item(Page("Mercure", "Mercure", DISAMBIG))
                c.add_item(Page("Lune", "Lune", "<p>La Lune tourne autour de la Terre.</p>"))
                c.add_metadata("Title", "test")
                c.add_metadata("Language", "fra")
                c.add_item(Page("Portail:Animaux", "Portail:Animaux", "<p>Un portail qui ne compte pas.</p>"))
            arts = dict(pc.zim_articles(zp))
            self.assertEqual(set(arts), {"Chat", "Mercure", "Lune"})
            kept = {t: pc.article_sentences(t, h) for t, h in arts.items()}
            self.assertEqual(kept["Mercure"], [])
            self.assertEqual(kept["Lune"], ["La Lune tourne autour de la Terre."])
            self.assertEqual(len(kept["Chat"]), 3)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)


# ---------------------------------------------------------------- 0.5 split / held-out / sample

class SplitAndSample(unittest.TestCase):
    def test_hash_bucket_is_stable(self):
        # sha256("Chat") etc. computed independently: the bucket is int(hex, 16) mod 20
        for t in ("Chat", "Lune", "Mercure", "Révolution française"):
            want = int(hashlib.sha256(t.encode("utf-8")).hexdigest(), 16) % 20
            self.assertEqual(pc.title_bucket(t), want)
        self.assertEqual(pc.title_bucket("Chat"), pc.title_bucket("Chat"))
        held = [t for t in ("T%d" % i for i in range(2000)) if pc.is_heldout_title(t)]
        self.assertTrue(70 <= len(held) <= 130, len(held))   # ≈ 5 %

    def test_heldout_id_in_training_stops(self):
        pc.check_book_lists(["1", "2"], ["3", "4"], ["3", "4", "5"])
        with self.assertRaises(SystemExit):
            pc.check_book_lists(["1", "2"], ["3"], ["2", "3"])

    def test_leak_drop(self):
        seen = {pc.key_hash("Le chat dort."), pc.key_hash("Il pleut beaucoup aujourd'hui")}
        sents = ["le CHAT dort", "Il pleut beaucoup aujourd’hui !", "Le chien court vite dans le jardin.", "Mon cœur bat."]
        kept, dropped, words = pc.draw(sents, seen, random.Random(1), None, shuffle=False)
        self.assertEqual(kept, ["Le chien court vite dans le jardin.", "Mon cœur bat."])
        self.assertEqual(dropped, 2)
        self.assertEqual(words, 6 + 2)   # tokens after the first, 2+ letters

    def test_draw_target_and_lexicon(self):
        lex = {"chien", "court", "vite", "dans", "le", "jardin"}
        kept, _, words = pc.draw(["Le chien court vite.", "Le zorglub dans le jardin."] * 3, set(), random.Random(2), 5, lex)
        self.assertGreaterEqual(words, 5)
        self.assertLess(len(kept), 6)

    def test_same_seed_same_sample(self):
        lines = ["phrase numéro %d du corpus" % i for i in range(3000)]
        a = list(pc.sample_source(20261115, "news", lines, pc.RATE))
        b = list(pc.sample_source(20261115, "news", lines, pc.RATE))
        c = list(pc.sample_source(20261116, "news", lines, pc.RATE))
        self.assertEqual(a, b)
        self.assertNotEqual(a, c)
        self.assertTrue(350 <= len(a) <= 550, len(a))          # ≈ 15 %
        self.assertEqual(pc.picked(20261115, "news", 7, rate=pc.RATE), pc.line_hash(20261115, "news", 7) < pc.threshold(pc.RATE))

    def test_token_matching_solver(self):
        rng = random.Random(3)
        vik = [(pc.line_hash(1, "vikidia", i), rng.randint(5, 30)) for i in range(5000)]
        d = pc.plan_d(subs_tokens=40000, shared_tokens=50000, books=[("b1", 6000), ("b2", 5000), ("b3", 9000)],
                      vikidia_hash_tokens=vik)
        self.assertLessEqual(abs(d["vikidia_tokens"] + d["book_tokens"] - 40000), 0.05 * 40000)
        self.assertLessEqual(d["book_tokens"], 0.2 * d["total"])
        self.assertEqual(d["books"], ["b1", "b2"])              # b3 would push the books above 20 %
        # the solved limit selects exactly the lines that were counted
        got = sum(t for h, t in vik if h < d["limit"])
        self.assertEqual(got, d["vikidia_tokens"])
        with self.assertRaises(SystemExit):
            pc.plan_d(10 ** 9, 0, [], vik)

    def test_fourgram_overlap(self):
        share, n = pc.fourgram_overlap(["a b c d e"], ["x a b c d y"])
        self.assertEqual(n, 2)
        self.assertAlmostEqual(share, 0.5)


# ---------------------------------------------------------------- 0.3 fetch / lock

class FakeResponse(io.BytesIO):
    def __init__(self, data, status=200):
        super().__init__(data)
        self.status = status

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


class NoWait(fc.Throttle):
    def __init__(self):
        super().__init__(pause=0)


class Lock(TmpDir):
    def lock_with(self, data, sha=None):
        return {"inputs": [{"id": "x", "file": "x/x.txt", "url": "https://example.invalid/x.txt",
                            "sha256": sha if sha is not None else hashlib.sha256(data).hexdigest(), "bytes": len(data),
                            "use": "train", "public": True}]}

    def run_cmd(self, cmd, lock, opener, *extra):
        lp = os.path.join(self.tmp, "lock.json")
        fc.save_lock(lock, lp)
        rc = fc.main(["--work", self.tmp, "--lock", lp, cmd] + list(extra), opener=opener, throttle=NoWait())
        return rc, fc.load_lock(lp)

    def test_mismatch_stops_and_leaves_nothing(self):
        lock = self.lock_with(b"good")
        rc, _ = self.run_cmd("fetch", lock, lambda req, timeout=None: FakeResponse(b"tampered"))
        self.assertEqual(rc, 2)
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "raw", "x", "x.txt")))
        self.assertFalse(os.path.exists(os.path.join(self.tmp, "raw", "x", "x.txt.part")))

    def test_fetch_then_verify_then_detect_change(self):
        lock = self.lock_with(b"good")
        rc, _ = self.run_cmd("fetch", lock, lambda req, timeout=None: FakeResponse(b"good"))
        self.assertEqual(rc, 0)
        self.assertEqual(self.run_cmd("verify", lock, None)[0], 0)
        with open(os.path.join(self.tmp, "raw", "x", "x.txt"), "wb") as f:
            f.write(b"evil")
        self.assertEqual(self.run_cmd("verify", lock, None)[0], 1)
        # never overwrites: fetch refuses the changed file instead of downloading again
        self.assertEqual(self.run_cmd("fetch", lock, lambda req, timeout=None: FakeResponse(b"good"))[0], 2)

    def test_resume_partial_download(self):
        data = b"0123456789"
        lock = self.lock_with(data)
        part = self.write("raw/x/x.txt.part", b"01234", "wb")
        seen = {}

        def opener(req, timeout=None):
            seen["range"] = req.get_header("Range")
            return FakeResponse(data[5:], status=206)
        self.assertEqual(self.run_cmd("fetch", lock, opener)[0], 0)
        self.assertEqual(seen["range"], "bytes=5-")
        with open(part[:-5], "rb") as f:
            self.assertEqual(f.read(), data)

    def test_pin_new_records_hash(self):
        lock = self.lock_with(b"", sha=None)
        lock["inputs"][0]["sha256"] = None
        lock["inputs"][0]["bytes"] = None
        rc, new = self.run_cmd("pin-new", lock, lambda req, timeout=None: FakeResponse(b"fresh"))
        self.assertEqual(rc, 0)
        self.assertEqual(new["inputs"][0]["sha256"], hashlib.sha256(b"fresh").hexdigest())
        self.assertEqual(new["inputs"][0]["bytes"], 5)

    def test_adopted_copy_is_used(self):
        src = self.write("elsewhere/corpus.txt", b"local", "wb")
        lock = self.lock_with(b"local")
        lp = os.path.join(self.tmp, "lock.json")
        fc.save_lock(lock, lp)
        fc.main(["--work", self.tmp, "--lock", lp, "adopt", "x", src], throttle=NoWait())
        self.assertEqual(self.run_cmd("verify", lock, None)[0], 0)

    def test_work_folder_has_no_default(self):
        old = os.environ.pop("PEPITE_CORPUS_WORK", None)
        try:
            with self.assertRaises(SystemExit):
                fc.work_dir(None)
        finally:
            if old is not None:
                os.environ["PEPITE_CORPUS_WORK"] = old

    def test_throttle_spacing(self):
        t = {"now": 0.0}
        waits = []
        th = fc.Throttle(pause=2.0, clock=lambda: t["now"], sleep=lambda s: (waits.append(s), t.__setitem__("now", t["now"] + s)))
        th.wait()
        t["now"] += 0.5
        th.wait()
        self.assertEqual(waits, [1.5])

    def archive_bytes(self, kind, member, data):
        import bz2
        import tarfile
        import zipfile
        p = os.path.join(self.tmp, "src." + kind)
        if kind == "zip":
            with zipfile.ZipFile(p, "w") as z:
                z.writestr("README.txt", b"other member")
                z.writestr(member, data)
        elif kind == "tar.gz":
            with tarfile.open(p, "w:gz") as t:
                info = tarfile.TarInfo(member)
                info.size = len(data)
                t.addfile(info, io.BytesIO(data))
        else:
            with bz2.open(p, "wb") as f:
                f.write(data)
        with open(p, "rb") as f:
            return f.read()

    def test_fetch_through_an_archive(self):
        data = b"id\tune phrase\n"
        for kind, member in (("zip", "corpus/sentences.txt"), ("tar.gz", "corpus/sentences.txt"), ("bz2", None)):
            with self.subTest(kind=kind):
                arch = self.archive_bytes(kind, member, data)
                lock = {"inputs": [{"id": "x", "file": "x/sentences.txt", "url": "https://example.invalid/x." + kind,
                                    "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data),
                                    "archive": {"file": "x/x." + kind, "member": member,
                                                "sha256": hashlib.sha256(arch).hexdigest(), "bytes": len(arch)},
                                    "use": "train", "public": True}]}
                shutil.rmtree(os.path.join(self.tmp, "raw"), ignore_errors=True)
                rc, _ = self.run_cmd("fetch", lock, lambda req, timeout=None: FakeResponse(arch))
                self.assertEqual(rc, 0)
                with open(os.path.join(self.tmp, "raw", "x", "sentences.txt"), "rb") as f:
                    self.assertEqual(f.read(), data)                       # the member, not the archive
                with open(os.path.join(self.tmp, "raw", "x", "x." + kind), "rb") as f:
                    self.assertEqual(f.read(), arch)                       # the archive at its own path
                # a tampered archive stops before anything is extracted
                shutil.rmtree(os.path.join(self.tmp, "raw"), ignore_errors=True)
                rc, _ = self.run_cmd("fetch", lock, lambda req, timeout=None: FakeResponse(arch + b"x"))
                self.assertEqual(rc, 2)
                self.assertFalse(os.path.exists(os.path.join(self.tmp, "raw", "x", "sentences.txt")))

    def test_unsafe_members_refused(self):
        for bad in ("/etc/passwd", "../x.txt", "a/../../x", "C:/x", "\\x"):
            self.assertFalse(fc.safe_member(bad), bad)
        self.assertTrue(fc.safe_member("fra_news_2023_1M/fra_news_2023_1M-sentences.txt"))
        import tarfile
        p = os.path.join(self.tmp, "link.tar.gz")
        with tarfile.open(p, "w:gz") as t:
            info = tarfile.TarInfo("corpus/s.txt")
            info.type = tarfile.SYMTYPE
            info.linkname = "/etc/passwd"
            t.addfile(info)
        with self.assertRaises(fc.LockMismatch):
            fc.extract_member(p, "corpus/s.txt", os.path.join(self.tmp, "out.txt"))
        with self.assertRaises(fc.LockMismatch):
            fc.extract_member(p, "../s.txt", os.path.join(self.tmp, "out.txt"))

    def test_committed_lock_is_well_formed(self):
        lock = fc.load_lock()
        ids = [e["id"] for e in lock["inputs"]]
        self.assertEqual(len(ids), len(set(ids)))
        for e in lock["inputs"]:
            for k in ("id", "url", "sha256", "licence", "use"):
                self.assertIn(k, e, e["id"])
            self.assertIn(e["use"], ("train", "heldout", "test-only", "private", "selection", "excluded"), e["id"])
            self.assertIn("public", e, e["id"])
            blob = json.dumps(e, ensure_ascii=False)
            self.assertNotRegex(blob, r"[A-Za-z]:\\\\", e["id"])   # no local paths
        self.assertFalse(fc.entry(lock, "opensubtitles-fr-4m")["public"])


class Gutenberg_Selection(unittest.TestCase):
    def book(self, i, authors, translators=(), langs=("fr",)):
        return {"id": i, "title": "Livre %d" % i, "languages": list(langs),
                "authors": [{"name": n, "birth_year": 1800, "death_year": d} for n, d in authors],
                "translators": [{"name": n, "birth_year": 1800, "death_year": d} for n, d in translators]}

    def test_eligibility_rule(self):
        self.assertTrue(fc.eligibility(self.book(1, [("Ségur, Sophie de", 1874)]))[0])
        self.assertFalse(fc.eligibility(self.book(2, [("Pergaud, Louis", 1915), ("Late, Ann", 1960)]))[0])
        self.assertFalse(fc.eligibility(self.book(3, [("Nobody, Known", None)]))[0])
        self.assertFalse(fc.eligibility(self.book(4, [("Andersen, H. C. (Hans Christian)", 1875)]))[0])   # no translator
        self.assertTrue(fc.eligibility(self.book(5, [("Andersen, H. C. (Hans Christian)", 1875)], [("Soldi, D.", 1900)]))[0])
        self.assertFalse(fc.eligibility(self.book(6, [("Andersen, H. C. (Hans Christian)", 1875)], [("Recent, T.", 1980)]))[0])
        self.assertFalse(fc.eligibility(self.book(7, [("Ségur, Sophie de", 1874)], langs=("fr", "en")))[0])
        ok, tr, ev, _ = fc.eligibility(self.book(8, [("Dupont, Jean", 1900)]), "Translator: Someone\nTitle: x")
        self.assertFalse(ok)
        self.assertTrue(tr)

    def test_picks(self):
        books = [self.book(i, [("Auteur %d" % (i % 7), 1900)]) for i in range(1, 41)]
        tokens = {i: 60000 + 1000 * i for i in range(1, 41)}
        r1 = fc.pick_books(books, tokens)
        self.assertEqual(r1, fc.pick_books(books, tokens))       # seeded
        held = [i for i, r in r1.items() if r == "heldout-E1"]
        self.assertEqual(len(held), 3)
        held_authors = {books[i - 1]["authors"][0]["name"] for i in held}
        self.assertEqual(len(held_authors), 3)
        for i, r in r1.items():
            if r.startswith("train"):
                self.assertNotIn(books[i - 1]["authors"][0]["name"], held_authors)
        light = sum(tokens[i] for i, r in r1.items() if r == "train-light")
        self.assertGreaterEqual(light, fc.LIGHT_TOKENS)
        self.assertEqual(set(r1), set(range(1, 41)))

    def test_curation_after_picks(self):
        self.assertGreater(fc.old_spelling_rate(["Il étoit une fois un enfant qui avoit peur."]), 100)
        self.assertEqual(fc.old_spelling_rate(["Il était une fois un enfant qui avait peur."]), 0.0)
        roles = {18112: "train-light", 1: "train-full", 2: "train-light", 3: "heldout-E1"}
        why = fc.curate(roles, {2: 5.0, 3: 9.0})
        self.assertEqual(roles, {18112: "excluded-content", 1: "train-full", 2: "excluded-content", 3: "heldout-E1"})
        self.assertIn("adult fiction", why[18112])
        self.assertIn("pre-1835", why[2])

    def test_catalogue_strings_are_cleaned(self):
        b = fc.clean_book({"id": 1, "title": "Contes\n de Noël", "authors": [{"name": "La Ville de Mirmont\u200f, Madame"}],
                           "translators": [{"name": "\u202aX\u202c"}]})
        self.assertEqual(b["title"], "Contes de Noël")
        self.assertEqual(b["authors"][0]["name"], "La Ville de Mirmont, Madame")
        self.assertEqual(b["translators"][0]["name"], "X")

    def test_second_edition_is_a_duplicate(self):
        books = [self.book(i, [("Auteur %d" % i, 1900)]) for i in range(1, 12)]
        books.append(dict(self.book(99, [("Auteur 3", 1900)]), title="Livre 3: autre édition"))
        roles = fc.pick_books(books, {b["id"]: 200000 for b in books})
        self.assertEqual(roles[99], "duplicate")
        self.assertNotEqual(roles[3], "duplicate")

    def test_gutenberg_paths(self):
        self.assertEqual(fc.gutenberg_dir(15058), "1/5/0/5/15058/")
        self.assertEqual(fc.gutenberg_dir(7), "0/7/")
        listing = '<a href="15058-8.txt">15058-8.txt</a> <a href="15058-h/">h</a> <a href="15058-8.zip">z</a>'
        self.assertEqual(fc.pick_gutenberg_file(15058, listing), ("15058-8.txt", "iso-8859-1"))
        self.assertEqual(fc.pick_gutenberg_file(15058, listing + '<a href="15058-0.txt">u</a>'), ("15058-0.txt", "utf-8"))
        self.assertIsNone(fc.pick_gutenberg_file(15058, '<a href="15058-h.htm">x</a>'))


# ---------------------------------------------------------------- 0.6 other scripts

class WriteSources(unittest.TestCase):
    def lock(self):
        return {"inputs": [
            {"id": "lexique-3.83", "use": "train", "public": True, "sha256": "a" * 64, "bytes": 1, "licence": "CC BY-SA 4.0",
             "credit": "Lexique 3.83", "version": "3.83", "date": "2019", "url": "http://www.lexique.org/x.zip"},
            {"id": "subs", "use": "private", "public": False, "sha256": "b" * 64, "licence": "none", "credit": "subs"},
            {"id": "gutenberg-1", "group": "gutenberg", "gutenberg_id": 1, "use": "train", "public": True,
             "sha256": "c" * 64, "licence": "PD", "credit": "« Livre », Auteur — Project Gutenberg eBook #1"},
            {"id": "gutenberg-2", "group": "gutenberg", "gutenberg_id": 2, "use": "train", "public": True,
             "sha256": "d" * 64, "licence": "PD", "credit": "« Autre », Auteur — Project Gutenberg eBook #2"},
            {"id": "wiki", "use": "test-only", "public": True, "sha256": "e" * 64, "licence": "CC BY", "credit": "W"}]}

    def test_output(self):
        text = ws.render(self.lock(), roles={1: "train-light", 2: "train-full"}, keep_roles=("train-light",))
        self.assertIn("CC BY-SA 4.0", text)
        self.assertIn("Lexique 3.83", text)
        self.assertIn("a" * 64, text)
        self.assertIn("eBook #1", text)
        self.assertNotIn("eBook #2", text)       # role not kept
        self.assertNotIn("subs", text)           # private input never listed
        self.assertNotIn("\nW\n", text)          # test-only input not listed
        self.assertIn("the words table", text)

    def test_public_false_training_input_refused(self):
        lock = self.lock()
        lock["inputs"][1]["use"] = "train"
        with self.assertRaises(SystemExit):
            ws.render(lock)
        self.assertIn("subs", ws.render(lock, private=True))

    def test_unpinned_refused(self):
        lock = self.lock()
        lock["inputs"][0]["sha256"] = None
        with self.assertRaises(SystemExit):
            ws.render(lock)


class DbChecks(TmpDir):
    def test_shares(self):
        db = os.path.join(self.tmp, "p.sqlite")
        con = sqlite3.connect(db)
        con.execute("CREATE TABLE bigrams (prev TEXT, word TEXT, c INTEGER)")
        con.executemany("INSERT INTO bigrams VALUES (?,?,?)",
                        [("ii", "est", 10), ("ca", "va", 5), ("il", "est", 65), ("il", "fut", 10), ("il", "aimèrent", 5), ("il", "plaît", 5)])
        con.commit()
        con.close()
        r = db_checks.check(db)["tables"]["bigrams"]
        self.assertEqual(r["rows"], 6)
        self.assertAlmostEqual(r["ocr_share"], 0.15)
        self.assertAlmostEqual(r["ocr_strict_share"], 0.05)
        self.assertAlmostEqual(r["passe_simple_share"], 0.15)


class CompareRuns(TmpDir):
    def make(self, label, used, extra=None):
        d = os.path.join(self.tmp, "runs", label)
        os.makedirs(d)
        words = ["maison", "chat", "jardin", "école", "porte"]
        for s in ("E1", "E2", "E3", "W", "S-unseen"):
            with open(os.path.join(d, "%s.dump" % s), "w", encoding="utf-8") as f:
                for i in range(200):
                    w = words[i % 5]
                    f.write("%d\tle petit\t%s\t%d\n" % (i, w, used(s, i, w)))
        logs = {"cases.log": "overall          60    0,80    1,00    0,85   => A\n",   # culture-formatted (French)
                "personal.log": "personal words: names / lesson words top-1 within 2 letters 0.474; spelling pairs (« Mes fautes ») top-1 1.000; sentence-start words top-1 0.9\n",
                "spelling.log": "gates: wrong reds 0 (target 0); x\n",
                "ginvented.log": "class errors\nall           525      0.700       0.900     0.80        0.10\n"}
        logs.update(extra or {})
        for n, t in logs.items():
            with open(os.path.join(d, n), "w", encoding="utf-8") as f:
                f.write(t)
        with open(os.path.join(d, "db_checks.json"), "w", encoding="utf-8") as f:
            json.dump({"bytes": 1, "tables": {"bigrams": {"rows": 1, "mass": 100, "ocr_share": 0.0, "ocr_strict_share": 0.0, "passe_simple_share": 0.001}}}, f)

    def test_go_d(self):
        self.make("A1", lambda s, i, w: len(w) // 2)
        self.make("D1", lambda s, i, w: len(w) // 2 - (1 if i % 2 else 0))
        text, v = cr.report(os.path.join(self.tmp, "runs"), "A1", "D1", None)
        self.assertEqual(v, "GO-D")
        self.assertIn("KSS(E)", text)

    def test_tie_and_loses(self):
        self.make("A1", lambda s, i, w: 2)
        self.make("D1", lambda s, i, w: 2)
        self.assertEqual(cr.report(os.path.join(self.tmp, "runs"), "A1", "D1", None)[1], "TIE→D")
        shutil.rmtree(os.path.join(self.tmp, "runs", "D1"))
        self.make("D1", lambda s, i, w: 2 + (1 if s == "E3" and i % 3 == 0 else 0))   # E3 worse by > 1 pt
        self.assertEqual(cr.report(os.path.join(self.tmp, "runs"), "A1", "D1", None)[1], "D-LOSES")

    def test_failed_gate_loses(self):
        self.make("A1", lambda s, i, w: 3)
        self.make("D1", lambda s, i, w: 1, {"spelling.log": "gates: wrong reds 2 (target 0)\n"})
        self.assertEqual(cr.report(os.path.join(self.tmp, "runs"), "A1", "D1", None)[1], "D-LOSES")

    def test_unpaired_words_stop(self):
        self.make("A1", lambda s, i, w: 2)
        self.make("D1", lambda s, i, w: 2)
        with open(os.path.join(self.tmp, "runs", "D1", "E1.dump"), "a", encoding="utf-8") as f:
            f.write("200\tx\tmaison\t1\n")
        with self.assertRaises(SystemExit):
            cr.report(os.path.join(self.tmp, "runs"), "A1", "D1", None)

    def test_sentence_blocks(self):
        p = os.path.join(self.tmp, "d.dump")
        # line 1 « le chat mange une souris » (all evaluated); line 2 « il dort » ; line 3 « la petite x maison » (x skipped)
        rows = [("le", "chat"), ("le chat", "mange"), ("le chat mange", "une"), ("chat mange une", "souris"),
                ("il", "dort"), ("la", "petite"), ("la petite x", "maison")]
        with open(p, "w", encoding="utf-8") as f:
            for i, (c, w) in enumerate(rows):
                f.write("%d\t%s\t%s\t1\n" % (i, c, w))
        self.assertEqual([r[3] for r in cr.read_dump(p)], [0, 0, 0, 0, 1, 2, 2])

    def test_bootstrap_is_seeded(self):
        sets = {s: [(5, 3, 2 + (i % 2)) for i in range(100)] for s in cr.DECISION}
        self.assertEqual(cr.bootstrap(sets), cr.bootstrap(sets))


if __name__ == "__main__":
    unittest.main()
