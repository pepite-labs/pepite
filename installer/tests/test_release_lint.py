"""Unit tests of release_lint.py (plan 12; stdlib only): python -I -m unittest discover -s installer/tests -p test_release_lint.py
The forbidden words used here are made up: the real list lives outside the repository."""
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import release_lint as rl  # noqa: E402

WORDS = ["Zorglub", "zorg@example.invalid"]

HELP_CSP = ("default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; "
            "form-action 'none'; base-uri 'none'")


def help_index(body="<p>Bonjour.</p>"):
    """The smallest help page help_lint accepts (one page, its own navigation)."""
    return ('<!doctype html><html lang="fr"><head><meta charset="utf-8">'
            f'<meta http-equiv="Content-Security-Policy" content="{HELP_CSP}">'
            '<meta name="pepite-public" content="enfant"><title>Aide de Pépite</title>'
            '<link rel="stylesheet" href="style.css"></head><body>'
            '<nav class="menu"><ul><li><a href="index.html">Sommaire</a></li></ul></nav>'
            f'<main><h1>Aide</h1>{body}</main></body></html>')


class ReleaseLintTest(unittest.TestCase):
    def setUp(self):
        self.stage = tempfile.mkdtemp(prefix="lint-stage-")

    def tearDown(self):
        shutil.rmtree(self.stage, ignore_errors=True)

    def write(self, rel, data):
        path = os.path.join(self.stage, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data if isinstance(data, bytes) else data.encode("utf-8"))
        return path

    def write_help(self, body="<p>Bonjour.</p>"):
        self.write("aide/index.html", help_index(body))
        self.write("aide/style.css", "body { font-size: 1.25rem; }\n")

    def test_clean_stage_passes(self):
        self.write_help()
        self.write("LISEZ-MOI.txt", "Pépite 0.10.0\r\nC:\\Program Files\\Pepite-Prediction\\Pepite.exe --log\r\nhttps://github.com/x\r\n")
        self.write("voices/siwis/voice.json", json.dumps({"public": True}))
        self.write("Pepite.dll", b"MZ\x00\x00/_/Predict.Keyboard/obj/Pepite.pdb\x00")
        self.assertEqual(rl.lint(self.stage, WORDS, public=True), [])

    DEPS = {"runtimeTarget": {"name": ".NETCoreApp,Version=v6.0/win-x64"},
            "targets": {".NETCoreApp,Version=v6.0/win-x64": {
                "Pepite/0.10.0": {"runtime": {"Pepite.dll": {}}},
                "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/6.0.32": {"runtime": {"x.dll": {}}},
                "SQLitePCLRaw.core/2.1.2": {"runtime": {"y.dll": {}}},
                "System.Memory/4.5.5": {}}},
            "libraries": {"Pepite/0.10.0": {"type": "project"},
                          "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/6.0.32": {"type": "runtimepack"},
                          "SQLitePCLRaw.core/2.1.2": {"type": "package"},
                          "System.Memory/4.5.5": {"type": "package"}}}

    def write_licenses(self):
        for rel in rl.REQUIRED_LICENSES:
            self.write(rel.replace("\\", "/"), "MIT License\n")
        self.write_exclusions()

    def write_exclusions(self):
        # an invented word, base64 of « zorblat »
        self.write("exclusions/fr.txt", rl.EXCLUSIONS_HEADER + "\nem9yYmxhdA==\n#lemmas\nem9yYmxhdA==\n")
        self.write("exclusions/SOURCE.txt", "Pépite — mots exclus (fr.txt)\n")

    def test_publish_requires_the_exclusions(self):
        self.write("Pepite.deps.json", json.dumps(self.DEPS))
        self.write("THIRD-PARTY-NOTICES.txt", ".NET runtime 6.0.32\n  package Microsoft.NETCore.App.Runtime.win-x64 6.0.32\n\n"
                                              "SQLitePCLRaw 2.1.2 — Apache-2.0\n")
        self.write_licenses()
        self.assertEqual(rl.lint(self.stage, WORDS), [])
        os.remove(os.path.join(self.stage, "exclusions", "fr.txt"))
        self.assertEqual(len(probs := rl.lint(self.stage, WORDS)), 1, probs)
        self.assertIn("exclusions\\fr.txt missing", probs[0])
        self.write("exclusions/fr.txt", "zorblat\n")                                    # a plain list: not the format
        self.assertEqual(rl.lint(self.stage, WORDS), ["exclusions\\fr.txt is not a Pépite exclusions file (header)"])
        self.write_exclusions()
        self.write("exclusions/SOURCE.txt", b"")
        self.assertEqual(rl.lint(self.stage, WORDS), ["exclusions\\SOURCE.txt is empty"])
        os.remove(os.path.join(self.stage, "Pepite.deps.json"))                          # not a publish: not required
        self.assertEqual(rl.check_exclusions(self.stage), [])

    def test_publish_requires_the_licence_files(self):
        self.write("Pepite.deps.json", json.dumps(self.DEPS))
        self.write("THIRD-PARTY-NOTICES.txt", ".NET runtime 6.0.32\n  package Microsoft.NETCore.App.Runtime.win-x64 6.0.32\n\n"
                                              "SQLitePCLRaw 2.1.2 — Apache-2.0\n")
        self.write_exclusions()
        probs = rl.lint(self.stage, WORDS)
        self.assertEqual(len(probs), len(rl.REQUIRED_LICENSES), probs)
        for sub in ("dotnet", "windowsdesktop", "system-speech", "apache-2.0"):
            self.assertTrue(any(f"licenses\\{sub}\\" in p and "missing" in p for p in probs), (sub, probs))
        self.write_licenses()
        self.assertEqual(rl.lint(self.stage, WORDS), [])
        os.remove(os.path.join(self.stage, "licenses", "windowsdesktop", "LICENSE"))     # one missing file is enough
        probs = rl.lint(self.stage, WORDS)
        self.assertEqual(len(probs), 1, probs)
        self.assertIn("licenses\\windowsdesktop\\LICENSE missing", probs[0])
        self.write("licenses/windowsdesktop/LICENSE", b"")                                  # an empty one too
        self.assertEqual(rl.lint(self.stage, WORDS), ["licenses\\windowsdesktop\\LICENSE is empty"])

    def test_no_licence_check_without_a_publish(self):
        self.write("licenses/x/LICENSE", "MIT")                       # no Pepite.deps.json: not a publish stage
        self.assertEqual(rl.check_licenses(self.stage), [])

    def test_notice_versions_follow_the_deps_json(self):
        self.write_licenses()
        self.write("Pepite.deps.json", json.dumps(self.DEPS))
        self.write("THIRD-PARTY-NOTICES.txt", ".NET runtime 6.0.32\n  package Microsoft.NETCore.App.Runtime.win-x64 6.0.32\n\n"
                                              "SQLitePCLRaw 2.1.2 — Apache-2.0\n")
        self.assertEqual(rl.lint(self.stage, WORDS), [])          # System.Memory ships nothing: not required
        self.write("THIRD-PARTY-NOTICES.txt", ".NET runtime 6.0.30\n  package Microsoft.NETCore.App.Runtime.win-x64 6.0.30\n\n"
                                              "SQLitePCLRaw 2.1.2 — Apache-2.0\n\nunrelated 6.0.32\n")
        probs = rl.lint(self.stage, WORDS)
        self.assertEqual(len(probs), 1, probs)
        self.assertIn("Microsoft.NETCore.App.Runtime.win-x64 6.0.32", probs[0])
        self.write("THIRD-PARTY-NOTICES.txt", "package Microsoft.NETCore.App.Runtime.win-x64 6.0.320\n\n"   # no prefix match
                                              "SQLitePCLRaw 2.1.2 — Apache-2.0\n")
        self.assertEqual(len(rl.lint(self.stage, WORDS)), 1)

    def test_forbidden_word_in_text_and_binary(self):
        self.write("THIRD-PARTY-NOTICES.txt", "made by ZORGLUB")
        self.write("Predict.Core.dll", "zorglub".encode("utf-16-le"))
        probs = rl.lint(self.stage, WORDS)
        self.assertEqual(len(probs), 2, probs)
        self.assertTrue(all("forbidden word" in p for p in probs))
        self.assertFalse(any("Zorglub" in p or "ZORGLUB" in p for p in probs), "the report must not repeat the word")

    def test_drive_paths(self):
        self.write("notes.md", "built in F:\\work\\stage and D:\\Programmes\\Pepite is an example")
        probs = rl.lint(self.stage, WORDS)
        self.assertEqual(len(probs), 1, probs)
        self.assertIn("F:\\work\\stage", probs[0])

    def test_build_path_in_binary(self):
        self.write("Pepite.dll", b"MZ\x00C:\\src\\proj\\obj\\Release\\Pepite.pdb\x00")
        probs = rl.lint(self.stage, WORDS)
        self.assertTrue(any("build path" in p for p in probs), probs)

    def test_http_and_placeholders_only_in_own_texts(self):
        self.write("licenses/x/LICENSE", "see http://example.org/licence")
        self.assertEqual(rl.lint(self.stage, WORDS), [])
        own = self.write("../lint-own-" + os.path.basename(self.stage) + ".txt", "voir http://example.org et {VERSION}")
        try:
            probs = rl.lint(self.stage, WORDS, texts=[own])
            self.assertEqual(len(probs), 2, probs)
        finally:
            os.remove(own)

    def test_public_release_requires_the_help(self):
        self.assertEqual(rl.lint(self.stage, WORDS, public=False), [])
        probs = rl.lint(self.stage, WORDS, public=True)
        self.assertEqual(len(probs), 1, probs)
        self.assertIn("aide\\index.html missing", probs[0])

    def test_help_files_are_strict_own_texts(self):
        words = [("Zorglub", False), ("frobnicate", True)]
        self.write_help('<p>Voir http://example.org, {VERSION} et frobnicate.</p>')
        self.write("aide/fonts/Andika/OFL.txt", "http://scripts.sil.org/OFL — you may frobnicate")   # third-party text
        probs = rl.lint(self.stage, words)
        self.assertEqual(len(probs), 3, probs)
        self.assertTrue(all(p.startswith("aide") and "OFL" not in p for p in probs), probs)
        # an SVG namespace name is not a link
        self.write("aide/img/x.svg", '<svg xmlns="http://www.w3.org/2000/svg"/>')
        self.assertEqual(len(rl.lint(self.stage, words)), 3)

    def test_help_lint_runs_on_the_stage(self):
        self.write_help('<p><a href="nulle-part.html">x</a> <a href="../LISEZ-MOI.txt">lisez-moi</a></p>')
        probs = rl.lint(self.stage, WORDS)
        self.assertEqual(len(probs), 2, probs)
        self.assertTrue(any("nulle-part.html not found" in p for p in probs), probs)
        self.assertTrue(any("LISEZ-MOI.txt missing next to the help" in p for p in probs), probs)
        self.write("LISEZ-MOI.txt", "Pépite")
        self.assertEqual(len(rl.lint(self.stage, WORDS)), 1)

    def test_non_public_voice_fails_a_public_release_only(self):
        self.write_help()
        self.write("voices/pierre/voice.json", json.dumps({"public": False}))
        self.assertEqual(rl.lint(self.stage, WORDS, public=False), [])
        probs = rl.lint(self.stage, WORDS, public=True)
        self.assertEqual(len(probs), 1)
        self.assertIn("pierre", probs[0])

    def test_own_words_apply_to_our_files_only(self):
        words = [("Zorglub", False), ("frobnicate", True)]
        self.write("licenses/x/LICENSE", "You may not frobnicate this library.")
        self.assertEqual(rl.lint(self.stage, words), [])
        self.write("THIRD-PARTY-NOTICES.txt", "we never frobnicate here")
        self.write("Pepite.dll", b"MZ frobnicate")
        self.assertEqual(len(rl.lint(self.stage, words)), 2)

    def test_microsoft_apphost_build_path_is_allowed(self):
        self.write("Pepite.exe", b"MZ D:\\a\\_work\\1\\s\\artifacts\\obj\\apphost.pdb\x00")
        self.assertEqual(rl.lint(self.stage, WORDS), [])

    def test_read_forbidden_scopes(self):
        path = self.write("../forb-" + os.path.basename(self.stage) + ".txt", "# c\nZorglub\nown: frobnicate\n")
        try:
            self.assertEqual(rl.read_forbidden(path), [("Zorglub", False), ("frobnicate", True)])
        finally:
            os.remove(path)

    def test_no_forbidden_skips_only_the_word_pass(self):
        """Plan 13 (CI build): --no-forbidden drops the word list, every other check still runs."""
        self.write("THIRD-PARTY-NOTICES.txt", "made by ZORGLUB")
        self.write("notes.md", "built in F:\\work\\stage")
        self.write("voices/pierre/voice.json", json.dumps({"public": False}))
        self.write("Pepite.dll", b"MZ\x00C:\\src\\proj\\obj\\Release\\Pepite.pdb\x00")
        probs = rl.lint(self.stage, [], public=True)
        self.assertFalse(any("forbidden word" in p for p in probs), probs)
        for expected in ("F:\\work\\stage", "pierre", "build path", "aide\\index.html missing"):
            self.assertTrue(any(expected in p for p in probs), (expected, probs))

    def test_command_line_needs_exactly_one_word_source(self):
        self.write_help()
        self.write("voices/pierre/voice.json", json.dumps({"public": False}))   # a public:false pack
        with open(os.devnull, "w") as null:
            stdout, sys.stdout = sys.stdout, null
            try:
                self.assertEqual(rl.main(["--stage", self.stage]), 2)
                self.assertEqual(rl.main(["--stage", self.stage, "--no-forbidden"]), 0)
                self.assertEqual(rl.main(["--stage", self.stage, "--no-forbidden", "--public"]), 1)
            finally:
                sys.stdout = stdout

    def test_empty_forbidden_list_is_refused(self):
        path = self.write("../empty-" + os.path.basename(self.stage) + ".txt", "# nothing\n")
        try:
            with self.assertRaises(SystemExit):
                rl.read_forbidden(path)
        finally:
            os.remove(path)


if __name__ == "__main__":
    unittest.main()
