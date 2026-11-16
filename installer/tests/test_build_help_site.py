"""Unit tests of build_help_site.py (plan 13, A10; stdlib only), on the real help and texts of the repository:
python -I -m unittest discover -s installer/tests -p test_build_help_site.py"""
import io
import os
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, ".."))
import build_help_site as bs  # noqa: E402
import help_lint  # noqa: E402

REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
AIDE = os.path.join(REPO, "aide")
FONTS = {"Lexend": ["Lexend-Regular.ttf", "Lexend-SemiBold.ttf"], "Andika": ["Andika-Regular.ttf", "Andika-SemiBold.ttf"],
         "OpenDyslexic": ["OpenDyslexic-Regular.otf", "OpenDyslexic-Bold.otf"]}
SOURCE_FILES = ("THIRD-PARTY-NOTICES.txt", "COPYING", "installer/DEPLOIEMENT.txt", "installer/LISEZ-MOI.txt.in",
                "installer/notes/credit-pierre.txt", "src/Data.Build/voices.json")


class BuildHelpSiteTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="helpsite-")
        self.fonts = os.path.join(self.tmp, "fonts")
        for fam, files in FONTS.items():
            os.makedirs(os.path.join(self.fonts, fam))
            for f in files + ["OFL.txt", "SOURCE.txt"]:
                with open(os.path.join(self.fonts, fam, f), "wb") as h:
                    h.write(b"font")
        self.minor = ".".join(bs.app_version(REPO).split(".")[:2])     # the repository itself is a source tree

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def source_tree(self, name, version):
        """A copy of the files the site reads from a source tree, with another csproj Version."""
        src = os.path.join(self.tmp, name)
        shutil.copytree(AIDE, os.path.join(src, "aide"), ignore=shutil.ignore_patterns("fonts"))
        for rel in SOURCE_FILES:
            target = os.path.join(src, *rel.split("/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copyfile(os.path.join(REPO, *rel.split("/")), target)
        with open(os.path.join(src, "installer", "notes", version + ".txt"), "w", encoding="utf-8") as h:
            h.write("NOUVEAU DANS LA %s\n" % version)
        os.makedirs(os.path.join(src, "src", "Predict.Keyboard"))
        with open(os.path.join(src, "src", "Predict.Keyboard", "Predict.Keyboard.csproj"), "w", encoding="utf-8") as h:
            h.write("<Project><PropertyGroup><Version>%s</Version></PropertyGroup></Project>" % version)
        return src

    def run_main(self, *args):
        buf = io.StringIO()
        with redirect_stdout(buf), redirect_stderr(buf):
            rc = bs.main(list(args))
        return rc, buf.getvalue()

    def read(self, *parts):
        with open(os.path.join(*parts), encoding="utf-8", newline="") as h:
            return h.read()

    def test_site_layout_banner_privacy_and_lint(self):
        out = os.path.join(self.tmp, "site")
        old = self.source_tree("old", "0.9.3")
        rc, text = self.run_main("--out", out, "--fonts", self.fonts, "--latest", self.minor,
                                 "--version", "0.9=" + old, "--version", "%s=%s" % (self.minor, REPO))
        self.assertEqual(rc, 0, text)
        self.assertIn("0 problem(s)", text)
        self.assertIn("url=%s/aide/index.html" % self.minor, self.read(out, "index.html"))
        self.assertTrue(os.path.isfile(os.path.join(out, ".nojekyll")))
        for minor in ("0.9", self.minor):
            self.assertTrue(os.path.isfile(os.path.join(out, minor, "index.html")))
            for t in bs.TEXTS:
                self.assertTrue(os.path.isfile(os.path.join(out, minor, t)), t)
            self.assertTrue(os.path.isfile(os.path.join(out, minor, "aide", "fonts", "Lexend", "OFL.txt")))
            self.assertFalse(os.path.exists(os.path.join(out, minor, "aide", "fonts", "Lexend", "SOURCE.txt")))
            self.assertFalse(os.path.exists(os.path.join(out, minor, "aide", "README.md")))
            self.assertEqual(help_lint.check(os.path.join(out, minor, "aide"), stage=True), [])
            lisez = self.read(out, minor, "LISEZ-MOI.txt")
            self.assertNotIn("{", lisez)                                   # every placeholder rendered
            self.assertNotIn("\r\r", lisez)
            self.assertIn("\r\n", lisez)
        self.assertIn("NOUVEAU DANS LA 0.9.3\r\n", self.read(out, "0.9", "LISEZ-MOI.txt"))
        page = self.read(out, "0.9", "aide", "index.html")
        self.assertIn("Aide en ligne de Pépite 0.9 ", page)
        self.assertIn(help_lint.CSP, page)                                 # the CSP is untouched
        self.assertIn("GitHub Pages", self.read(out, self.minor, "aide", "vie-privee.html"))
        self.assertNotIn("GitHub Pages", self.read(out, self.minor, "aide", "orthographe.html"))
        self.assertIn(".en-ligne", self.read(out, self.minor, "aide", "style.css"))
        self.assertNotIn("en-ligne", self.read(out, self.minor, "aide", "aller", "reglages-voix.html"))   # stubs as they are

    def test_refusals(self):
        out = os.path.join(self.tmp, "site")
        self.assertEqual(self.run_main("--out", out, "--fonts", self.fonts, "--latest", "0.10", "--version", "0.10")[0], 2)
        src = self.source_tree("s", "0.10.7")
        rc, _ = self.run_main("--out", out, "--fonts", self.fonts, "--latest", "0.11", "--version", "0.10=" + src)
        self.assertEqual(rc, 1)                                            # --latest is not built
        rc, text = self.run_main("--out", out, "--fonts", self.fonts, "--latest", "0.11", "--version", "0.11=" + src,
                                 "--replace")
        self.assertEqual(rc, 1)
        self.assertIn("is not in the minor 0.11", text)
        os.remove(os.path.join(src, "COPYING"))
        rc, text = self.run_main("--out", out, "--fonts", self.fonts, "--latest", "0.10", "--version", "0.10=" + src,
                                 "--replace")
        self.assertEqual(rc, 1)
        self.assertIn("COPYING missing", text)

    def test_transform_needs_its_markers(self):
        with self.assertRaises(bs.SiteError):
            bs.transform_page("<html><body><p>x</p></body></html>", "0.10")
        page = bs.transform_page('<body>\n<header class="haut"></header><h1>T</h1>', "0.10", privacy=True)
        self.assertLess(page.index("en-ligne"), page.index('<header class="haut">'))
        self.assertLess(page.index("</h1>"), page.index("GitHub Pages"))


if __name__ == "__main__":
    unittest.main()
