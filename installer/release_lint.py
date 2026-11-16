#!/usr/bin/env python3
"""Release lint (plan 12 step 5): what must never ship in an installer. Stdlib only; run with ``python -I``.

    python -I release_lint.py --stage <stage dir> --forbidden <list outside the repository> [--public] [--text <file> ...]
    python -I release_lint.py --stage <stage dir> --no-forbidden [--public] [--text <file> ...]

Checks, over every text file of the stage folder (``.txt .md .json .iss .in .xml .config .html .css .js .svg``, no
extension) and the extra
``--text`` files (the installer's own texts), and over the app's own binaries (``Pepite.exe``, ``Pepite.dll``,
``Predict.Core.dll``: build paths end up there):

- **forbidden words**: one word or phrase per line in ``--forbidden`` (case-insensitive, ``#`` comments). The list is kept
  outside the repository on purpose: it names what must not appear, so it cannot itself be published. A line
  ``own:<words>`` applies to our own files only (the ``--text`` files, ``LISEZ-MOI.txt``, ``THIRD-PARTY-NOTICES.txt`` and
  the app's binaries): generic words that third-party licence texts legitimately contain stay allowed in those
  licence texts. ``--no-forbidden`` (plan 13: the CI build, where the list is not available) skips this pass only and
  says so; every other check below still runs. A release built that way gets the full lint on the owner's local
  rebuild before it is published;
- **absolute drive paths** (``X:\\…``) except the documented install locations (``ALLOWED_PATHS``);
- **plain ``http://`` links** in our own texts (``--text`` files and ``LISEZ-MOI.txt``); the notices quote third-party
  licences verbatim (« http://mozilla.org/MPL/2.0/ ») and are not checked for this;
- with ``--public``: a voice pack whose ``voice.json`` says ``"public": false`` (D28) fails the release;
- leftover template placeholders (``{VERSION}``-style) in our own texts;
- **the embedded help** (plan « aide intégrée »): the files of ``aide\\`` (its fonts and their licence texts excepted) are
  our own texts, checked strictly like ``LISEZ-MOI.txt``, and ``help_lint.check`` runs on the folder (links to the app
  files and fonts must resolve); with ``--public``, ``aide\\index.html`` is required;
- **notice versions** (publication cleanup, S6): every runtime pack and package of ``Pepite.deps.json`` that ships files
  (runtime or native assets) must appear in ``THIRD-PARTY-NOTICES.txt`` with its version, in one paragraph that names
  the package (or its family: ``SQLitePCLRaw`` for ``SQLitePCLRaw.core``);
- **licence files** (plan 13, first CI installer run): a publish (``Pepite.deps.json`` present) must ship the non-empty
  licence texts of ``REQUIRED_LICENSES`` (.NET and Windows Desktop runtimes, System.Speech, the Apache-2.0 text): the
  build used to skip a missing one silently.

Exit code 0 = clean, 1 = problems (listed), 2 = bad arguments.
"""
import argparse
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import help_lint  # noqa: E402

TEXT_EXT = {".txt", ".md", ".json", ".iss", ".in", ".xml", ".config", ".html", ".css", ".js", ".svg", ""}
HELP_DIR = "aide"
OWN_BINARIES = ("Pepite.exe", "Pepite.dll", "Predict.Core.dll")
# install locations and examples the documents name on purpose (case-insensitive prefixes)
ALLOWED_PATHS = (
    "c:\\program files\\pepite-prediction",
    "c:\\program files\\",
    "c:\\windows\\temp\\",
    "d:\\programmes\\pepite",
    "c:\\windows\\",
)
# build paths inside third-party binaries we ship unchanged (the .NET apphost is built by Microsoft's CI)
ALLOWED_BINARY_PATHS = ("d:\\a\\_work\\",)
OWN_TEXTS = ("LISEZ-MOI.txt", "THIRD-PARTY-NOTICES.txt")
# the licence texts a publish must carry (Predict.Keyboard.csproj copies them; THIRD-PARTY-NOTICES.txt points to them)
REQUIRED_LICENSES = (
    r"licenses\dotnet\LICENSE.TXT",
    r"licenses\dotnet\THIRD-PARTY-NOTICES.TXT",
    r"licenses\windowsdesktop\LICENSE",
    r"licenses\system-speech\LICENSE.TXT",
    r"licenses\system-speech\THIRD-PARTY-NOTICES.TXT",
    r"licenses\apache-2.0\LICENSE.txt",
)
DRIVE_PATH = re.compile(r"(?<![A-Za-z0-9])[A-Za-z]:\\[^\s\"'<>|*?]*")
HTTP = re.compile(r"http://[^\s\"'<>)]+")
# XML namespace names (the help's SVG drawings: xmlns="http://www.w3.org/2000/svg") are identifiers, never fetched
XML_NAMESPACES = ("http://www.w3.org/",)
PLACEHOLDER = re.compile(r"\{[A-Z_]{3,}\}")


def read_forbidden(path):
    """[(word, own_only)]"""
    words = []
    with open(path, encoding="utf-8-sig") as f:
        for line in f:
            w = line.strip()
            if w and not w.startswith("#"):
                words.append((w[4:].strip(), True) if w.lower().startswith("own:") else (w, False))
    if not words:
        raise SystemExit(f"{path}: the forbidden-word list is empty")
    return words


def _words(forbidden, own):
    """The words that apply to a file (a plain string is a word for every file)."""
    out = []
    for w in forbidden:
        word, own_only = (w, False) if isinstance(w, str) else w
        if own or not own_only:
            out.append(word)
    return out


def text_of(path):
    with open(path, "rb") as f:
        raw = f.read()
    for enc in ("utf-8-sig", "utf-16", "cp1252"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("latin-1")


def check_text(name, text, forbidden, own, strict=None):
    problems = []
    low = text.lower()
    for w in _words(forbidden, own):
        if w.lower() in low:
            problems.append(f"{name}: forbidden word ({w[:2]}…, {len(w)} chars)")
    for m in DRIVE_PATH.finditer(text):
        p = low[m.start():m.start() + 64]   # the allowed prefixes may hold a space (« Program Files »)
        if not any(p.startswith(a) for a in ALLOWED_PATHS):
            problems.append(f"{name}: absolute path {m.group(0)[:60]}")
    if own if strict is None else strict:
        for m in HTTP.finditer(text):
            if m.group(0).startswith(XML_NAMESPACES):
                continue
            problems.append(f"{name}: plain http link {m.group(0)[:60]}")
        for m in PLACEHOLDER.finditer(text):
            problems.append(f"{name}: placeholder left {m.group(0)}")
    return problems


def check_binary(name, data, forbidden):
    problems = []
    low = data.lower()
    for w in _words(forbidden, True):
        for enc in ("utf-8", "utf-16-le"):
            if w.lower().encode(enc) in low:
                problems.append(f"{name}: forbidden word in the binary ({w[:2]}…, {enc})")
                break
    # build paths (PDB path in the debug directory, source paths): any X:\ followed by a path in ASCII
    for m in re.finditer(rb"[A-Za-z]:\\[\x20-\x7e]{3,200}?\.(?:pdb|cs)\b", data):
        if m.group(0).decode("ascii", "replace").lower().startswith(ALLOWED_BINARY_PATHS):
            continue
        problems.append(f"{name}: build path {m.group(0)[:80].decode('ascii', 'replace')}")
    return problems


def check_voices(stage):
    problems = []
    vdir = os.path.join(stage, "voices")
    if os.path.isdir(vdir):
        for name in sorted(os.listdir(vdir)):
            card = os.path.join(vdir, name, "voice.json")
            if os.path.isfile(card):
                with open(card, encoding="utf-8-sig") as f:
                    if json.load(f).get("public", True) is False:
                        problems.append(f"voices\\{name}: \"public\": false in a public release (D28)")
    return problems


def shipped_packages(deps):
    """[(name, version)] of the runtime packs and packages of a deps.json that ship runtime or native files."""
    targets = deps.get("targets", {})
    target = targets.get(deps.get("runtimeTarget", {}).get("name", ""), {}) or (list(targets.values())[-1] if targets else {})
    out = []
    for key, lib in deps.get("libraries", {}).items():
        if lib.get("type") not in ("package", "runtimepack"):
            continue
        assets = target.get(key, {})
        if not ("runtime" in assets or "native" in assets):
            continue
        name, version = key.rsplit("/", 1)
        out.append((name[len("runtimepack."):] if name.startswith("runtimepack.") else name, version))
    return out


def _families(name):
    """The names a notice may use for a package: itself, then shorter dotted prefixes (at least two parts for the
    Microsoft.* / System.* names, so « Microsoft » alone never counts)."""
    parts = name.split(".")
    least = 2 if parts[0] in ("Microsoft", "System", "runtime") else 1
    return [".".join(parts[:n]) for n in range(len(parts), least - 1, -1)]


def check_versions(stage):
    deps_path, notices_path = os.path.join(stage, "Pepite.deps.json"), os.path.join(stage, "THIRD-PARTY-NOTICES.txt")
    if not os.path.isfile(deps_path):
        return []
    if not os.path.isfile(notices_path):
        return ["THIRD-PARTY-NOTICES.txt missing"]
    with open(deps_path, encoding="utf-8-sig") as f:
        deps = json.load(f)
    paragraphs = [p.lower() for p in re.split(r"\r?\n[ \t]*\r?\n", text_of(notices_path))]
    problems = []
    for name, version in shipped_packages(deps):
        fams = [x.lower() for x in _families(name)]
        ver = re.compile(r"(?<![0-9.])" + re.escape(version) + r"(?![0-9])")   # 6.0.3 never matches 6.0.32
        if not any(ver.search(p) and any(fm in p for fm in fams) for p in paragraphs):
            problems.append(f"THIRD-PARTY-NOTICES.txt: {name} {version} (Pepite.deps.json) has no entry with that version")
    return problems


def check_licenses(stage):
    """The required licence files of a publish (a stage with ``Pepite.deps.json``): present and not empty."""
    if not os.path.isfile(os.path.join(stage, "Pepite.deps.json")):
        return []
    problems = []
    for rel in REQUIRED_LICENSES:
        path = os.path.join(stage, *rel.split("\\"))
        if not os.path.isfile(path):
            problems.append(f"{rel} missing: a publish ships this licence text")
        elif os.path.getsize(path) == 0:
            problems.append(f"{rel} is empty")
    return problems


def lint(stage, forbidden, public=False, texts=()):
    problems = check_versions(stage) + check_licenses(stage)
    for root, _, files in os.walk(stage):
        for fn in files:
            path = os.path.join(root, fn)
            rel = os.path.relpath(path, stage)
            ext = os.path.splitext(fn)[1].lower()
            if fn in OWN_BINARIES:
                with open(path, "rb") as f:
                    problems += check_binary(rel, f.read(), forbidden)
            elif ext in TEXT_EXT:
                help_own = is_help_own(rel)
                problems += check_text(rel, text_of(path), forbidden, own=(rel in OWN_TEXTS or help_own),
                                       strict=(rel == "LISEZ-MOI.txt" or help_own))
    for t in texts:
        problems += check_text(os.path.basename(t), text_of(t), forbidden, own=True)
    help_dir = os.path.join(stage, HELP_DIR)
    if os.path.isdir(help_dir):
        problems += [HELP_DIR + "\\" + p for p in help_lint.check(help_dir, stage=True)]
    if public:
        problems += check_voices(stage)
        if not os.path.isfile(os.path.join(help_dir, "index.html")):
            problems.append(HELP_DIR + "\\index.html missing: a public release ships the embedded help")
    return problems


def is_help_own(rel):
    """A file of the embedded help we wrote (``aide\\…``), not its fonts and their licence texts (``aide\\fonts\\…``)."""
    parts = rel.replace("/", os.sep).split(os.sep)
    return len(parts) > 1 and parts[0] == HELP_DIR and parts[1] != "fonts"


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--stage", required=True)
    p.add_argument("--forbidden", help="word list kept outside the repository")
    p.add_argument("--no-forbidden", action="store_true", help="skip the forbidden-word pass only (CI build)")
    p.add_argument("--public", action="store_true")
    p.add_argument("--text", action="append", default=[])
    a = p.parse_args(argv)
    if not os.path.isdir(a.stage):
        print(f"no stage folder: {a.stage}", file=sys.stderr)
        return 2
    if bool(a.forbidden) == a.no_forbidden:
        print("give exactly one of --forbidden <list> and --no-forbidden", file=sys.stderr)
        return 2
    if a.no_forbidden:
        print("release lint: forbidden-word pass SKIPPED (--no-forbidden); the other checks run")
    problems = lint(a.stage, [] if a.no_forbidden else read_forbidden(a.forbidden), a.public, a.text)
    for pr in problems:
        print("LINT: " + pr)
    print(f"release lint: {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
