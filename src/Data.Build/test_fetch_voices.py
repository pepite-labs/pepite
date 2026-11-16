"""Tests of fetch_voices.py (plans 08a, 08b). Run: python -I -m unittest test_fetch_voices.py (stdlib; the patch test runs only
when the onnx of requirements-voices.txt is installed)."""
import hashlib
import io
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import fetch_voices as fv  # noqa: E402


def sha(b):
    return hashlib.sha256(b).hexdigest()


class FakeResp(io.BytesIO):
    def __enter__(self):
        return self

    def __exit__(self, *a):
        self.close()


def fake_pins(model=b"MODEL", cfg=b"{}", card=b"card", lic=b"licence", **extra):
    return {"t": {
        "name": "T", "gender": "female", "culture": "fr-FR", "sampleRate": 22050, "model": "t.onnx", "revision": "r",
        "patched_sha256": sha(model), "attribution": ["a"], "licence": "CC BY 4.0", **extra,
        "files": [
            {"name": "t.onnx", "url": "u/t.onnx", "sha256": sha(model)},
            {"name": "t.onnx.json", "url": "u/t.onnx.json", "sha256": sha(cfg)},
            {"name": "MODEL_CARD", "url": "u/MODEL_CARD", "sha256": sha(card)},
            {"name": "LICENSE.txt", "url": "u/LICENSE.txt", "sha256": sha(lic)},
        ]}}


class PinsTest(unittest.TestCase):
    def test_siwis_pins_present(self):
        v = fv.load_pins()["siwis"]
        self.assertEqual("d1120aceb629668ffc742c69e6e183b45ccb379e8de3dee78a9261451f9ed9ff", v["patched_sha256"])
        names = {f["name"]: f["sha256"] for f in v["files"]}
        self.assertEqual("b1d946fd68d0347155fa59bce0a018924abbef3c1561181db45ca463d83963ef", names["fr_FR-siwis-medium.onnx"])
        self.assertEqual("df814aa3dfedd00510a07144442cc0a220ff21e43d03c3b078444fc0be68fc37", names["fr_FR-siwis-medium.onnx.json"])
        self.assertEqual("f75d76ec577b5e7a49a7462883d31ef65383022387818a9a0b75bdaf90b9010c", names["MODEL_CARD"])
        self.assertIn("LICENSE.txt", names)
        for f in v["files"]:
            self.assertIn("cf910fb59f1d79ab3b625a63993a108751c6c25d", f["url"]) if "huggingface" in f["url"] else None
            self.assertEqual(64, len(f["sha256"]))

    def test_pierre_pins_present(self):
        v = fv.load_pins()["pierre"]
        self.assertEqual("7f763f70ddf9148ecb878346e03133fc9779e45b69c5a0ddad8768cf203b496a", v["patched_sha256"])
        names = {f["name"]: f["sha256"] for f in v["files"]}
        self.assertEqual("9abb3800c199148897a9ed64e100d224f3de83579f100044174ad19418f1786f", names["fr_FR-upmc-medium.onnx"])
        self.assertEqual("e8636ec15dfd5d72db37a02cb5320a20f2b8d339f2a0e4337da64c58a33a5868", names["fr_FR-upmc-medium.onnx.json"])
        self.assertEqual("cf1ac7309e0e04bb159b7e6ae80bc8f66bfce9bdfb3e62ae93a6f57e210ab98a", names["MODEL_CARD"])
        self.assertIn("LICENSE.txt", names)
        self.assertIn("DATASET-LICENSE.md", names)
        self.assertEqual("eeac4783f84f941c995a593c52750b586184ba022e90e1f7758ffee306861e8f", names["DATASET-README.md"])
        self.assertEqual(("pierre", "CC BY-SA 4.0", True, 2), (v["speaker"], v["licence"], v["public"], v["order"]))   # D28 changed 2026-11-16
        self.assertIn("research only", v["risk_note"])
        siwis = fv.load_pins()["siwis"]
        self.assertEqual((None, "CC BY 4.0", True, 1), (siwis["speaker"], siwis["licence"], siwis["public"], siwis["order"]))

    def test_siwis_word_carrier_with_exceptions(self):
        # owner's blind sheet of 2026-11-16 (voice-evaluation § 9e): « Le mot : {0}. », « eu » and « les » said alone
        siwis = fv.load_pins()["siwis"]
        self.assertEqual("Le mot : {0}.", siwis["word_carrier"])
        self.assertEqual(["eu", "les"], siwis["word_carrier_except"])
        self.assertNotIn("inference_word", siwis)
        self.assertNotIn("word_carrier_except", fv.load_pins()["pierre"])

    def test_voice_filter(self):
        pins = fv.load_pins()
        self.assertEqual(["pierre"], list(fv.select(pins, "pierre")))
        self.assertEqual(set(pins), set(fv.select(pins, None)))
        with self.assertRaises(SystemExit):
            fv.select(pins, "jessica")


class FetchTest(unittest.TestCase):
    def test_bad_hash_rejected_and_deleted(self):
        pins = fake_pins()
        with tempfile.TemporaryDirectory() as raw:
            with self.assertRaises(SystemExit):
                fv.fetch(raw, pins, opener=lambda url: FakeResp(b"tampered"))
            self.assertEqual([], os.listdir(raw))

    def test_good_fetch(self):
        data = {"u/t.onnx": b"MODEL", "u/t.onnx.json": b"{}", "u/MODEL_CARD": b"card", "u/LICENSE.txt": b"licence"}
        with tempfile.TemporaryDirectory() as raw:
            fv.fetch(raw, fake_pins(), opener=lambda url: FakeResp(data[url]))
            self.assertEqual(4, len(os.listdir(raw)))


class PackTest(unittest.TestCase):
    def _raw(self, raw, model=b"MODEL"):
        for n, b in (("t.onnx", b"orig"), ("t.onnx.json", b"{}"), ("MODEL_CARD", b"card"), ("LICENSE.txt", b"licence")):
            with open(os.path.join(raw, fv.raw_name("t", n)), "wb") as f:
                f.write(b)
        with open(fv.patched_path(raw, "t", {"model": "t.onnx"}), "wb") as f:
            f.write(model)

    def test_pack_writes_card_and_refuses_foreign_folder(self):
        with tempfile.TemporaryDirectory() as raw, tempfile.TemporaryDirectory() as out:
            self._raw(raw)
            fv.pack(raw, out, fake_pins(), today="2026-11-11")
            card = json.load(open(os.path.join(out, "t", "voice.json"), encoding="utf-8"))
            self.assertEqual(sha(b"MODEL"), card["sha256"]["t.onnx"])
            self.assertIn("SOURCE.txt", card["sizes"])
            self.assertEqual((None, 100, "CC BY 4.0", True), (card["speaker"], card["order"], card["licence"], card["public"]))
            self.assertNotIn("ShareAlike", open(os.path.join(out, "t", "SOURCE.txt"), encoding="utf-8").read())
            fv.pack(raw, out, fake_pins(), today="2026-11-11")    # its own folder: replaced
            os.remove(os.path.join(out, "t", fv.MARKER))
            with self.assertRaises(SystemExit):
                fv.pack(raw, out, fake_pins())

    def test_pack_card_carries_speaker_order_licence_public_and_the_sharealike_line(self):
        pins = fake_pins(speaker="pierre", order=2, licence="CC BY-SA 4.0", public=False,
                         inference={"noise_scale": 0.5, "length_scale": 1.0, "noise_w": 0.333},
                         inference_word={"noise_scale": 0.333, "noise_w": 0.2},
                         word_carrier="Le mot : {0}.", word_carrier_except=["eu", "les"], phoneme_fix=[["iʲ", "i"]])
        with tempfile.TemporaryDirectory() as raw, tempfile.TemporaryDirectory() as out:
            self._raw(raw)
            fv.pack(raw, out, pins, today="2026-11-13")
            card = json.load(open(os.path.join(out, "t", "voice.json"), encoding="utf-8"))
            self.assertEqual(("pierre", 2, "CC BY-SA 4.0", False), (card["speaker"], card["order"], card["licence"], card["public"]))
            self.assertEqual(0.333, card["inference"]["noise_w"])
            self.assertEqual({"noise_scale": 0.333, "noise_w": 0.2}, card["inference_word"])
            self.assertEqual("Le mot : {0}.", card["word_carrier"])
            self.assertEqual(["eu", "les"], card["word_carrier_except"])
            self.assertEqual([["iʲ", "i"]], card["phoneme_fix"])
            src = open(os.path.join(out, "t", "SOURCE.txt"), encoding="utf-8").read()
            self.assertIn("Licence: CC BY-SA 4.0 (LICENSE.txt)", src)
            self.assertIn("ShareAlike : le modèle modifié reste sous CC BY-SA 4.0", src)

    def test_main_voice_option_limits_the_command(self):
        with tempfile.TemporaryDirectory() as raw:
            seen = []
            orig = fv.fetch
            fv.fetch = lambda r, pins, opener=None: seen.append(sorted(pins))
            try:
                fv.main(["fetch", "--raw", raw, "--voice", "pierre"])
            finally:
                fv.fetch = orig
            self.assertEqual([["pierre"]], seen)

    def test_pack_enforces_the_patched_hash(self):
        with tempfile.TemporaryDirectory() as raw, tempfile.TemporaryDirectory() as out:
            self._raw(raw, model=b"NOT PATCHED")
            with self.assertRaises(SystemExit):
                fv.pack(raw, out, fake_pins())


@unittest.skipUnless(__import__("importlib").util.find_spec("onnx"), "onnx not installed (requirements-voices.txt)")
class PatchTest(unittest.TestCase):
    def test_patch_enforces_the_pinned_hash(self):
        import onnx
        from onnx import TensorProto, helper
        g = helper.make_graph([helper.make_node("Ceil", ["x"], ["c"]), helper.make_node("Identity", ["c"], ["y"])], "g",
                              [helper.make_tensor_value_info("x", TensorProto.FLOAT, [1])],
                              [helper.make_tensor_value_info("y", TensorProto.FLOAT, [1])])
        with tempfile.TemporaryDirectory() as raw:
            onnx.save(helper.make_model(g), os.path.join(raw, fv.raw_name("t", "t.onnx")))
            with self.assertRaises(SystemExit):   # the fake pin is not the hash of the patched graph
                fv.patch(raw, fake_pins())
            self.assertFalse(os.path.exists(fv.patched_path(raw, "t", {"model": "t.onnx"})))


if __name__ == "__main__":
    unittest.main()
