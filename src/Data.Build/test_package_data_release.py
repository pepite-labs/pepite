"""Unit tests of package_data_release.py (plan 13, A2; stdlib only):
python -I -m unittest discover -s src/Data.Build -p test_package_data_release.py"""
import hashlib
import io
import json
import os
import shutil
import tarfile
import tempfile
import time
import unittest
import zipfile

import package_data_release as pk


class PackageDataRelease(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="pkgrel-")
        self.root = os.path.join(self.tmp, "root")
        for rel, data in (("b/two.txt", b"2" * 1000), ("a.txt", b"1"), ("b/c/three.bin", bytes(range(256)) * 10)):
            p = os.path.join(self.root, *rel.split("/"))
            os.makedirs(os.path.dirname(p), exist_ok=True)
            with open(p, "wb") as f:
                f.write(data)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_zip_is_deterministic(self):
        members = pk.tree(self.root, ".")
        members = [m[2:] if m.startswith("./") else m for m in members]
        self.assertEqual(members, ["a.txt", "b/c/three.bin", "b/two.txt"])
        z1, z2 = os.path.join(self.tmp, "1.zip"), os.path.join(self.tmp, "2.zip")
        pk.write_zip(z1, self.root, members, (2026, 11, 1, 0, 0, 0))
        time.sleep(1.1)                                   # a later clock time must not change the bytes
        os.utime(os.path.join(self.root, "a.txt"))        # nor a newer file date
        pk.write_zip(z2, self.root, list(reversed(members)), (2026, 11, 1, 0, 0, 0))
        self.assertEqual(pk.sha256_file(z1), pk.sha256_file(z2))
        with zipfile.ZipFile(z1) as z:
            self.assertEqual(z.namelist(), members)
            self.assertEqual({i.date_time for i in z.infolist()}, {(2026, 11, 1, 0, 0, 0)})
            self.assertEqual(z.read("b/two.txt"), b"2" * 1000)
            self.assertTrue(all(i.compress_type == zipfile.ZIP_DEFLATED for i in z.infolist()))

    def test_zip_date_comes_from_the_tag(self):
        self.assertEqual(pk.zip_date("data-2026.11"), (2026, 11, 1, 0, 0, 0))
        self.assertEqual(pk.zip_date("data-2026.11.2"), (2026, 11, 1, 0, 0, 0))
        with self.assertRaises(pk.PackError):
            pk.zip_date("v1.0.0")

    def test_tar_commit_reads_the_git_archive_header(self):
        path = os.path.join(self.tmp, "s.tar.gz")
        with tarfile.open(path, "w:gz", format=tarfile.PAX_FORMAT, pax_headers={"comment": "abc123"}) as t:
            info = tarfile.TarInfo("s/x.txt")
            info.size = 1
            t.addfile(info, io.BytesIO(b"x"))
        self.assertEqual(pk.tar_commit(path), "abc123")

    def test_replace_never_deletes_foreign_files(self):
        dest = os.path.join(self.tmp, "dest")
        os.makedirs(dest)
        pk.clear_dest(dest, ["a.zip"], replace=False)          # empty: nothing to do
        for n in ("a.zip", "SHA256SUMS.txt", "a.zip.part"):
            open(os.path.join(dest, n), "w").close()
        with self.assertRaises(pk.PackError):
            pk.clear_dest(dest, ["a.zip"], replace=False)
        open(os.path.join(dest, "notes.txt"), "w").close()
        with self.assertRaises(pk.PackError) as e:
            pk.clear_dest(dest, ["a.zip"], replace=True)
        self.assertIn("notes.txt", str(e.exception))
        self.assertTrue(os.path.isfile(os.path.join(dest, "a.zip")))     # nothing deleted
        os.remove(os.path.join(dest, "notes.txt"))
        pk.clear_dest(dest, ["a.zip"], replace=True)
        self.assertEqual(os.listdir(dest), [])

    def test_update_lock_keeps_names_and_licences(self):
        lock = os.path.join(self.tmp, "lock.json")
        with open(lock, "w", encoding="utf-8") as f:
            json.dump({"comment": "c", "repo": "o/r", "tag": "data-2026.11", "assets": [
                {"name": "a.zip", "sha256": None, "bytes": None, "licence": "L1", "unpack_to": "."},
                {"name": "s.tar.gz", "sha256": None, "bytes": None, "licence": "L2", "unpack_to": None}]}, f)
        h = hashlib.sha256(b"x").hexdigest()
        pk.update_lock(lock, [("s.tar.gz", 5, h), ("a.zip", 7, h)])
        with open(lock, encoding="utf-8") as f:
            got = json.load(f)
        self.assertEqual([(a["name"], a["bytes"], a["licence"], a["unpack_to"]) for a in got["assets"]],
                         [("a.zip", 7, "L1", "."), ("s.tar.gz", 5, "L2", None)])
        with self.assertRaises(pk.PackError):
            pk.update_lock(lock, [("other.zip", 1, h)])


if __name__ == "__main__":
    unittest.main()
