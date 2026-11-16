"""Unit tests of fetch_fonts.py (stdlib only, no network): python -I -m unittest discover -s . -p test_fetch_fonts.py"""
import hashlib
import io
import os
import shutil
import tempfile
import unittest

import fetch_fonts as ff


class FakeResponse(io.BytesIO):
    status = 200

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def opener_for(data):
    return lambda req, timeout=None: FakeResponse(data)


class FetchFontsTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="fetchfonts-")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_pin_mismatch_aborts_and_leaves_nothing(self):
        dest = os.path.join(self.tmp, "x.ttf")
        good = b"font bytes"
        with self.assertRaises(ValueError):
            ff.download(["https://example.invalid/x.ttf"], dest, hashlib.sha256(good).hexdigest(), opener=opener_for(b"tampered"))
        self.assertFalse(os.path.exists(dest))
        self.assertFalse(os.path.exists(dest + ".part"))

    def test_pinned_download_is_kept(self):
        dest = os.path.join(self.tmp, "x.ttf")
        good = b"font bytes"
        url = ff.download(["https://example.invalid/x.ttf"], dest, hashlib.sha256(good).hexdigest(), opener=opener_for(good))
        self.assertEqual(url, "https://example.invalid/x.ttf")
        with open(dest, "rb") as f:
            self.assertEqual(f.read(), good)

    def test_pack_refuses_to_delete_a_foreign_folder(self):
        out = os.path.join(self.tmp, "out")
        os.makedirs(out)
        with open(os.path.join(out, "notes.txt"), "w") as f:
            f.write("mine")
        self.assertFalse(ff.is_previous_output(out))

        class A:
            raw = self.tmp
        A.out = out
        with self.assertRaises(SystemExit):
            ff.cmd_pack(A)
        self.assertTrue(os.path.exists(os.path.join(out, "notes.txt")))

    def test_previous_output_is_recognised(self):
        out = os.path.join(self.tmp, "fonts")
        self.assertTrue(ff.is_previous_output(out))                    # missing
        os.makedirs(os.path.join(out, "Lexend"))
        self.assertFalse(ff.is_previous_output(out))                   # no SOURCE.txt yet
        open(os.path.join(out, "Lexend", "SOURCE.txt"), "w").close()
        self.assertTrue(ff.is_previous_output(out))

    def test_every_packed_file_is_pinned(self):
        for folder, (_, files) in ff.PACK.items():
            names = [f[0] for f in files]
            self.assertIn("OFL.txt", names, folder)
            for packed, raw, member, sha in files:
                self.assertIn(raw, ff.RAW)
                self.assertRegex(sha, r"^[0-9a-f]{64}$")


if __name__ == "__main__":
    unittest.main()
