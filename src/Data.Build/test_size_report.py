"""Unit tests of size_report.py's grammar check (plan 11; stdlib only): python -I -m unittest discover -s . -p test_size_report.py"""
import os
import shutil
import tempfile
import unittest

import size_report as sr


class GrammarCheckTest(unittest.TestCase):
    def setUp(self):
        self.new = tempfile.mkdtemp(prefix="sizereport-new-")
        self.old = tempfile.mkdtemp(prefix="sizereport-old-")

    def tearDown(self):
        shutil.rmtree(self.new, ignore_errors=True)
        shutil.rmtree(self.old, ignore_errors=True)

    def _write(self, rel, data=b"x" * 10):
        path = os.path.join(self.new, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data)

    def test_missing_grammar_is_warned(self):
        self.assertEqual(len(sr.grammar_missing(self.new)), 3)
        text = sr.report(self.new, self.old)
        self.assertIn("**absent**", text)
        self.assertIn("**Warning:** missing `grammar.sqlite`", text)

    def test_grammar_without_its_notices_is_warned(self):
        self._write("grammar.sqlite")
        self.assertEqual(sr.grammar_missing(self.new), [os.path.join("grammar", "SOURCE.txt"), os.path.join("grammar", "MPL-2.0.txt")])
        self.assertIn("**Warning:**", sr.report(self.new, self.old))

    def test_complete_grammar_is_listed(self):
        for rel in sr.GRAMMAR_FILES:
            self._write(rel)
        self.assertEqual(sr.grammar_missing(self.new), [])
        text = sr.report(self.new, self.old)
        self.assertIn("| `grammar.sqlite` (MPL-2.0) | — |", text)
        self.assertNotIn("**Warning:**", text)

    def test_report_without_an_old_release(self):
        """Plan 13: a CI build has no earlier release folder; the report shows the new folder alone."""
        for rel in sr.GRAMMAR_FILES:
            self._write(rel)
        self._write("voices/siwis/voice.json", b'{"licence": "CC BY 4.0", "public": true}')
        text = sr.report(self.new)
        self.assertIn("no earlier release to compare with", text)
        self.assertNotIn(" against ", text)
        self.assertIn("`voices\\siwis\\` (CC BY 4.0)", text)
        self.assertIn("**App without voices**", text)
        self.assertIn("## Installer (plan 12)", text)


class InstallerRowsTest(unittest.TestCase):
    """Plan 12: installer components and installed sizes."""

    def setUp(self):
        self.rel = tempfile.mkdtemp(prefix="sizereport-inst-")
        for rel, n in [("Pepite.exe", 100), ("Pepite.pdb", 7000), ("pictos.sqlite", 40), ("ARASAAC-LICENCE.txt", 2),
                       ("noms/noms.json", 8), ("voices/siwis/m.onnx", 300), ("voices/pierre/m.onnx", 500)]:
            path = os.path.join(self.rel, rel)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as f:
                f.write(b"x" * n)
        for name, public in (("siwis", True), ("pierre", False)):
            with open(os.path.join(self.rel, "voices", name, "voice.json"), "w", encoding="utf-8") as f:
                f.write('{"licence": "CC", "public": %s}' % ("true" if public else "false"))
        self.inst = os.path.join(self.rel, "..", os.path.basename(self.rel) + "-setup.exe")
        with open(self.inst, "wb") as f:
            f.write(b"y" * 1234)

    def tearDown(self):
        shutil.rmtree(self.rel, ignore_errors=True)
        os.remove(self.inst)

    def test_components_skip_pdbs_and_split_voices(self):
        c = sr.components(self.rel)
        cards = [len('{"licence": "CC", "public": true}'), len('{"licence": "CC", "public": false}')]
        self.assertEqual(c["app"], 110)              # ARASAAC-LICENCE.txt with app (0.10.2)
        self.assertEqual(c["images"], 40)
        self.assertEqual(c["voix\\siwis"], 300 + cards[0])
        self.assertEqual(c["voix\\pierre"], 500 + cards[1])

    def test_rows_name_the_installer_and_both_installs(self):
        text = "\n".join(sr.install_rows(self.rel, self.inst))
        self.assertIn("-setup.exe` |", text)
        self.assertIn("Installed, public build", text)
        self.assertIn("Installed, private test build", text)
        self.assertIn("component `voix\\pierre`", text)

    def test_public_folder_has_no_private_total(self):
        shutil.rmtree(os.path.join(self.rel, "voices", "pierre"))
        text = "\n".join(sr.install_rows(self.rel))
        self.assertIn("no non-public pack in this folder", text)


if __name__ == "__main__":
    unittest.main()
