"""Unit tests of verify_data_pack.py (stdlib only): python -I -m unittest discover -s src/Data.Build -p test_verify_data_pack.py"""
import hashlib
import json
import os
import shutil
import tempfile
import unittest

import verify_data_pack as v


class VerifyDataPack(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="datapack-")
        os.makedirs(os.path.join(self.tmp, "grammar"))
        self.files = {"prediction.sqlite": b"db", "grammar/SOURCE.txt": b"notice"}
        for p, data in self.files.items():
            with open(os.path.join(self.tmp, *p.split("/")), "wb") as f:
                f.write(data)
        self.lock = {"pack": {"id": "t", "version": "0"},
                     "files": [{"path": p, "sha256": hashlib.sha256(d).hexdigest(), "bytes": len(d)} for p, d in self.files.items()]}

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_ok_then_changed_then_missing(self):
        self.assertEqual([p for _, p in v.verify(self.tmp, self.lock)], [None, None])
        with open(os.path.join(self.tmp, "prediction.sqlite"), "wb") as f:
            f.write(b"DB")
        os.remove(os.path.join(self.tmp, "grammar", "SOURCE.txt"))
        self.assertEqual([p for _, p in v.verify(self.tmp, self.lock)], ["SHA-256 differs from the pin", "missing"])

    def test_committed_lock_url_and_pins(self):
        with open(v.LOCK, encoding="utf-8") as f:
            lock = json.load(f)
        # plan 13: the pack is an asset of the data release named in data-release.lock.json
        with open(os.path.join(os.path.dirname(v.LOCK), "data-release.lock.json"), encoding="utf-8") as f:
            rel = json.load(f)
        name = "pepite-data-%s.zip" % lock["pack"]["version"]
        self.assertEqual(lock["pack"]["url"], "https://github.com/%s/releases/download/%s/%s" % (rel["repo"], rel["tag"], name))
        self.assertIn(name, [a["name"] for a in rel["assets"]])
        for f in lock["files"]:
            self.assertRegex(f["sha256"], r"^[0-9a-f]{64}$")
            self.assertGreater(f["bytes"], 0)


if __name__ == "__main__":
    unittest.main()
