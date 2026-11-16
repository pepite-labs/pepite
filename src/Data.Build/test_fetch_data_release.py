"""Unit tests of fetch_data_release.py (plan 13, A1; stdlib only, no network):
python -I -m unittest discover -s src/Data.Build -p test_fetch_data_release.py"""
import hashlib
import io
import json
import os
import shutil
import tempfile
import unittest
import zipfile
from contextlib import redirect_stderr, redirect_stdout

import fetch_data_release as fd


def zip_bytes(files):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, data in files.items():
            z.writestr(name, data)
    return buf.getvalue()


class FetchDataRelease(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="fetchrel-")
        self.src = os.path.join(self.tmp, "assets")
        self.out = os.path.join(self.tmp, "out")
        os.makedirs(self.src)
        self.pack_files = {"prediction.sqlite": b"db", "grammar/SOURCE.txt": b"notice"}
        self.assets = {
            "pepite-data-1.zip": (zip_bytes(self.pack_files), "."),
            "voice-x.zip": (zip_bytes({"voice.json": b"{}", "m.onnx": b"model"}), "voices/x"),
            "src.tar.gz": (b"source archive", None),
        }
        self.write_lock()
        pack = {"pack": {"id": "t", "version": "1"},
                "files": [{"path": p, "sha256": hashlib.sha256(d).hexdigest(), "bytes": len(d)} for p, d in self.pack_files.items()]}
        self.pack_lock = os.path.join(self.tmp, "pack.lock.json")
        with open(self.pack_lock, "w", encoding="utf-8") as f:
            json.dump(pack, f)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write_lock(self, tamper=None):
        entries = []
        for name, (data, unpack_to) in self.assets.items():
            with open(os.path.join(self.src, name), "wb") as f:
                f.write(data)
            e = {"name": name, "sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data), "licence": "t",
                 "unpack_to": unpack_to}
            if tamper:
                tamper(e)
            entries.append(e)
        self.lock = os.path.join(self.tmp, "lock.json")
        with open(self.lock, "w", encoding="utf-8") as f:
            json.dump({"repo": "o/r", "tag": "data-t", "assets": entries}, f)

    def run_main(self, *args, fetch=None):
        out, err = io.StringIO(), io.StringIO()
        argv = ["--lock", self.lock, "--out", self.out, "--pack-lock", self.pack_lock] + list(args)
        with redirect_stdout(out), redirect_stderr(err):
            rc = fd.main(argv, fetch=fetch) if fetch else fd.main(argv)
        return rc, out.getvalue() + err.getvalue()

    def test_from_unpacks_and_verifies(self):
        rc, text = self.run_main("--from", self.src)
        self.assertEqual(rc, 0, text)
        self.assertTrue(os.path.isfile(os.path.join(self.out, "grammar", "SOURCE.txt")))
        self.assertTrue(os.path.isfile(os.path.join(self.out, "voices", "x", "m.onnx")))
        self.assertFalse(os.path.exists(os.path.join(self.out, "src.tar.gz")))   # kept as a file, not unpacked
        self.assertIn("file     " + os.path.join(self.src, "src.tar.gz"), text)
        self.assertIn("data pack t 1: 0 problem(s)", text)

    def test_bad_hash_is_refused_before_unpacking(self):
        self.write_lock(lambda e: e.update(sha256="0" * 64) if e["name"] == "voice-x.zip" else None)
        rc, text = self.run_main("--from", self.src)
        self.assertEqual(rc, 1)
        self.assertIn("voice-x.zip: SHA-256 differs from the pin", text)
        self.assertFalse(os.path.exists(self.out))

    def test_wrong_size_is_refused(self):
        self.write_lock(lambda e: e.update(bytes=e["bytes"] + 1) if e["name"] == "src.tar.gz" else None)
        rc, text = self.run_main("--from", self.src)
        self.assertEqual(rc, 1)
        self.assertIn("src.tar.gz: size", text)

    def test_zip_slip_is_refused(self):
        for bad in ("../evil.txt", "a/../../evil.txt", "/abs.txt", "C:/drive.txt"):
            self.assets["voice-x.zip"] = (zip_bytes({"ok.txt": b"x", bad: b"x"}), "voices/x")
            self.write_lock()
            rc, text = self.run_main("--from", self.src)
            self.assertEqual(rc, 1, bad)
            self.assertIn("zip-slip", text)
            self.assertFalse(os.path.exists(os.path.join(self.tmp, "evil.txt")))
            self.assertFalse(os.path.exists(os.path.join(self.out, "voices", "x", "ok.txt")))   # nothing unpacked

    def test_refuses_to_overwrite_without_replace(self):
        self.assertEqual(self.run_main("--from", self.src)[0], 0)
        rc, text = self.run_main("--from", self.src)
        self.assertEqual(rc, 1)
        self.assertIn("--replace", text)
        self.assertEqual(self.run_main("--from", self.src, "--replace")[0], 0)

    def test_changed_pack_file_fails_verification(self):
        self.pack_files["prediction.sqlite"] = b"DB"
        self.assets["pepite-data-1.zip"] = (zip_bytes(self.pack_files), ".")
        self.write_lock()
        rc, text = self.run_main("--from", self.src)
        self.assertEqual(rc, 1)
        self.assertIn("prediction.sqlite: SHA-256 differs from the pin", text)

    def test_download_uses_the_release_url_and_the_cache(self):
        seen = []

        def fake(url, dest, limit):
            seen.append(url)
            self.assertEqual(limit, len(self.assets[url.rsplit("/", 1)[1]][0]))
            shutil.copyfile(os.path.join(self.src, url.rsplit("/", 1)[1]), dest)

        cache = os.path.join(self.tmp, "cache")
        rc, text = self.run_main("--cache", cache, fetch=fake)
        self.assertEqual(rc, 0, text)
        self.assertIn("https://github.com/o/r/releases/download/data-t/voice-x.zip", seen)
        self.assertEqual(len(seen), 3)
        rc, _ = self.run_main("--cache", cache, "--replace", fetch=fake)
        self.assertEqual((rc, len(seen)), (0, 3))       # checked cache entries are not downloaded again

    def test_download_stops_past_the_pinned_size(self):
        out = io.BytesIO()
        self.assertEqual(fd.copy_limited(io.BytesIO(b"x" * 10), out, 10, "a"), 10)
        with self.assertRaises(fd.FetchError) as e:
            fd.copy_limited(io.BytesIO(b"x" * (3 << 20)), io.BytesIO(), 1 << 20, "big.zip")
        self.assertIn("big.zip: more than the pinned", str(e.exception))
        with self.assertRaises(fd.FetchError):
            fd.download("http://example.invalid/a.zip", os.path.join(self.tmp, "a.zip"), 1)   # https only

    def test_unpinned_lock_and_missing_source_are_argument_errors(self):
        self.assertEqual(self.run_main()[0], 2)          # neither --cache nor --from
        self.write_lock(lambda e: e.update(sha256=None))
        self.assertEqual(self.run_main("--from", self.src)[0], 2)

    def test_only_and_list(self):
        rc, text = self.run_main("--from", self.src, "--only", "voice-x.zip")
        self.assertEqual(rc, 0, text)
        self.assertNotIn("data pack", text)              # the word base was not fetched, so not verified
        rc, text = self.run_main("--list")
        self.assertEqual(rc, 0)
        self.assertIn("(kept as a file)", text)

    def test_committed_lock_is_well_formed(self):
        lock = fd.load_lock(fd.LOCK)
        self.assertEqual(lock["repo"], "pepite-labs/pepite")
        self.assertRegex(lock["tag"], r"^data-\d{4}\.\d{2}(\.\d+)?$")
        for a in lock["assets"]:
            self.assertTrue(a["licence"])
            if a["sha256"] is not None:
                self.assertRegex(a["sha256"], r"^[0-9a-f]{64}$")
                self.assertGreater(a["bytes"], 0)
        self.assertEqual(sum(a["unpack_to"] is None for a in lock["assets"]), 1)   # the espeak-ng source archive


if __name__ == "__main__":
    unittest.main()
