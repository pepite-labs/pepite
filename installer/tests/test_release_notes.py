"""Unit tests of release_notes.py (plan 13, A5; stdlib only):
python -I -m unittest discover -s installer/tests -p test_release_notes.py"""
import io
import os
import shutil
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import release_notes as rn  # noqa: E402

EXE_SHA = "a" * 64
SRC = "espeak-ng-" + "1" * 40 + ".tar.gz"


class ReleaseNotesTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="relnotes-")
        self.sums = os.path.join(self.tmp, "SHA256SUMS.txt")
        with open(self.sums, "w", encoding="utf-8", newline="\n") as f:
            f.write("%s *Pepite-0.10.1-installation.exe\n%s *%s\n%s  LISEZ-MOI.txt\n" % (EXE_SHA, "b" * 64, SRC, "c" * 64))

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def run_main(self, *args):
        out = os.path.join(self.tmp, "notes.md")
        buf = io.StringIO()
        with redirect_stdout(buf), redirect_stderr(buf):
            rc = rn.main(["--version", "0.10.1", "--sums", self.sums, "--repo", "o/r", "--out", out] + list(args))
        if rc:
            return rc, buf.getvalue()
        with open(out, encoding="utf-8") as f:
            return rc, f.read()

    def test_read_sums_both_formats(self):
        sums = rn.read_sums(self.sums)
        self.assertEqual(sums["Pepite-0.10.1-installation.exe"], EXE_SHA)
        self.assertEqual(sums["LISEZ-MOI.txt"], "c" * 64)

    def test_unsigned_notes_hold_every_required_part(self):
        rc, text = self.run_main()                          # the committed installer/notes/0.10.1.txt
        self.assertEqual(rc, 0, text)
        for part in ("Version de test, non signée — pour essai, pas encore pour les élèves",
                     "Informations complémentaires", "Exécuter quand même", "Smart App Control",
                     "Ordinateurs gérés", "%s  Pepite-0.10.1-installation.exe" % EXE_SHA, "Get-FileHash",
                     "gh attestation verify Pepite-0.10.1-installation.exe --repo o/r "
                     "--signer-workflow o/r/.github/workflows/app.yml", "gh auth login",
                     "https://github.com/pepite-labs/pepite/releases/tag/%s" % rn.read_data_release()["tag"],
                     "GPL-3.0-or-later", "https://github.com/o/r/tree/v0.10.1", SRC,
                     "usage non commercial uniquement", "Voix Pierre", "réservées à la recherche", rn.SITE,
                     "UNE AIDE INTÉGRÉE"):
            self.assertIn(part, text)
        self.assertIsNone(rn.PLACEHOLDER.search(text))

    def test_signed_notes_drop_the_unsigned_wording(self):
        notes = os.path.join(self.tmp, "notes.txt")             # the 0.10.1 notes themselves say « non signée »
        with open(notes, "w", encoding="utf-8") as f:
            f.write("NOUVEAU DANS LA 0.10.2 : LA SIGNATURE\n")
        rc, text = self.run_main("--signed", "--notes", notes)
        self.assertEqual(rc, 0, text)
        self.assertNotIn("non signée", text)
        self.assertNotIn("Smart App Control", text)
        self.assertIn(EXE_SHA, text)

    def test_missing_installer_or_source_offer_is_refused(self):
        with open(self.sums, "w", encoding="utf-8") as f:
            f.write("%s *%s\n" % ("b" * 64, SRC))
        rc, text = self.run_main()
        self.assertEqual(rc, 1)
        self.assertIn("Pepite-0.10.1-installation.exe", text)
        with open(self.sums, "w", encoding="utf-8") as f:
            f.write("%s *Pepite-0.10.1-installation.exe\n" % EXE_SHA)
        rc, text = self.run_main()
        self.assertEqual(rc, 1)
        self.assertIn("source offer", text)

    def test_data_release_comes_from_the_lock(self):
        lock = os.path.join(self.tmp, "data-release.lock.json")
        with open(lock, "w", encoding="utf-8") as f:
            f.write('{"repo": "x/y", "tag": "data-2099.01.2", "assets": []}')
        rc, text = self.run_main("--data-lock", lock)
        self.assertEqual(rc, 0, text)
        self.assertIn("[`data-2099.01.2`](https://github.com/x/y/releases/tag/data-2099.01.2)", text)
        with open(lock, "w", encoding="utf-8") as f:
            f.write('{"repo": "x/y", "tag": "v1.0.0"}')
        self.assertEqual(self.run_main("--data-lock", lock)[0], 1)   # not a data-YYYY.MM tag

    def test_missing_notes_file_and_bad_arguments(self):
        rc, _ = self.run_main("--notes", os.path.join(self.tmp, "absent.txt"))
        self.assertEqual(rc, 1)
        buf = io.StringIO()
        with redirect_stdout(buf), redirect_stderr(buf):
            self.assertEqual(rn.main(["--version", "v0.10", "--sums", self.sums, "--repo", "o/r", "--out", "x"]), 2)


if __name__ == "__main__":
    unittest.main()
