#!/usr/bin/env python3
"""Package the data release assets (plan 13, A2). Stdlib only; run with ``python -I``. Maintainers only.

    python -I src/Data.Build/package_data_release.py --dest <work>\\data-release\\data-2026.11
        [--src src/Data.Build/out] --espeak-source <espeak-ng-<commit>.tar.gz> [--replace] [--write-lock]

Reads the pinned outputs of ``src/Data.Build/out/`` (the word base checked by ``verify_data_pack``, the pictures, the
« Noms propres » pack, the fonts, the espeak-ng build, the patched voices) and writes one asset per component, named as
in ``data-release.lock.json``:

- **deterministic zips**: members in sorted order, a fixed date (the first day of the tag's month), fixed permissions,
  deflate level 9, no directory entries — two runs on the same files and the same Python give the same bytes;
- the espeak-ng source archive (``git archive`` of the pinned commit, the GPL-3 source offer) is copied as is, after
  checking its name and the commit recorded in its header;
- ``SHA256SUMS.txt`` (``sha256sum -c`` format) and ``BUILD-INFO.txt`` (the sources and the tool versions, no clock time;
  ``--note`` lines, e.g. the accepted ``--scan-data`` notes, named by term number only).

It checks before packing: the data pack against ``data-pack.lock.json``, ``libespeak-ng.dll`` against
``espeak-ng.lock.json``, each voice model against its ``patched_sha256`` in ``voices.json``. It prints the lock fragment
(``assets``); ``--write-lock`` writes the new SHA-256 and sizes into ``data-release.lock.json`` (same asset names only).
An existing ``--dest`` is refused unless ``--replace``, and even then when it holds other files than this tool's outputs. Exit code 0 = written, 1 = a check failed.
"""
import argparse
import hashlib
import json
import os
import platform
import re
import shutil
import sys
import tarfile
import zipfile
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import verify_data_pack  # noqa: E402

RELEASE_LOCK = os.path.join(HERE, "data-release.lock.json")
ESPEAK_LOCK = os.path.join(HERE, "espeak-ng.lock.json")
VOICES = os.path.join(HERE, "voices.json")
NAMES_LOCK = os.path.join(HERE, "pepite-noms", "names.lock.json")
TOOL = "src/Data.Build/package_data_release.py"


class PackError(Exception):
    pass


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def read_json(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def tree(root, sub):
    """Sorted posix paths (relative to ``root``) of every file under ``root/sub``."""
    base = os.path.join(root, *sub.split("/"))
    if not os.path.isdir(base):
        raise PackError("missing folder: %s" % base)
    out = []
    for d, _, fs in os.walk(base):
        for f in fs:
            out.append(os.path.relpath(os.path.join(d, f), root).replace("\\", "/"))
    return sorted(out)


def zip_date(tag):
    m = re.match(r"^data-(\d{4})\.(\d{2})", tag)
    if not m:
        raise PackError("tag %r: expected data-YYYY.MM" % tag)
    return (int(m.group(1)), int(m.group(2)), 1, 0, 0, 0)


def write_zip(path, root, members, date):
    """A deterministic zip of ``root/<member>`` stored as ``<member>`` (posix paths, sorted); ZipFile.open(zi, "w")
    takes the archive's compresslevel, so the archive is opened at level 9."""
    part = path + ".part"
    with zipfile.ZipFile(part, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for m in sorted(members):
            zi = zipfile.ZipInfo(m, date_time=date)
            zi.compress_type = zipfile.ZIP_DEFLATED
            zi.create_system = 3                       # the same header on every platform
            zi.external_attr = 0o100644 << 16
            with open(os.path.join(root, *m.split("/")), "rb") as src, z.open(zi, "w") as dst:
                shutil.copyfileobj(src, dst, 1 << 20)
    os.replace(part, path)


def tar_commit(path):
    """The commit ``git archive`` recorded in the pax global header of a .tar.gz (None if absent)."""
    with tarfile.open(path, "r:gz") as t:
        t.next()
        return t.pax_headers.get("comment")


def plan_assets(src, tag, espeak_source):
    """[(asset name, kind, root, members or source file)] and the source notes for BUILD-INFO."""
    notes, assets = [], []
    pack = read_json(verify_data_pack.LOCK)
    bad = [(p, why) for p, why in verify_data_pack.verify(src, pack) if why]
    if bad:
        raise PackError("data pack: " + "; ".join("%s %s" % b for b in bad))
    ver = pack["pack"]["version"]
    assets.append(("pepite-data-%s.zip" % ver, "zip", src, sorted(f["path"] for f in pack["files"])))
    notes.append("pepite-data %s (data-pack.lock.json)" % ver)

    lic = os.path.join(src, "ARASAAC-LICENCE.txt")
    if not os.path.isfile(lic) or not os.path.isfile(os.path.join(src, "pictos.sqlite")):
        raise PackError("pictos.sqlite / ARASAAC-LICENCE.txt missing")
    with open(lic, encoding="utf-8") as f:
        m = re.search(r"Index du catalogue du (\d{4}-\d{2}-\d{2})", f.read())
    if not m:
        raise PackError("ARASAAC-LICENCE.txt: no catalogue date")
    assets.append(("pictos-arasaac-%s.zip" % m.group(1), "zip", src, ["ARASAAC-LICENCE.txt", "pictos.sqlite"]))
    notes.append("pictures: ARASAAC catalogue index of %s (build_pictos.py)" % m.group(1))

    edition = read_json(NAMES_LOCK)["pack"]["edition"]
    root = os.path.join(src, "noms-pack")
    assets.append(("noms-%s.zip" % edition, "zip", root, tree(root, "noms")))
    notes.append("pepite-noms edition %s (pepite-noms/names.lock.json)" % edition)

    month = tag[len("data-"):][:7]
    root = os.path.join(src, "fonts")
    assets.append(("fonts-%s.zip" % month, "zip", root, tree(root, ".")))
    notes.append("fonts: fetch_fonts.py pins (one SOURCE.txt per family)")

    el = read_json(ESPEAK_LOCK)
    commit = el["espeak"]["commit"]
    root = os.path.join(src, "espeak")
    dll = os.path.join(root, "libespeak-ng.dll")
    if not os.path.isfile(dll) or sha256_file(dll) != el["dll_sha256"]:
        raise PackError("espeak/libespeak-ng.dll missing or not the pinned build (espeak-ng.lock.json dll_sha256)")
    assets.append(("espeak-ng-win-x64-%s.zip" % commit[:8], "zip", root, tree(root, ".")))
    name = "espeak-ng-%s.tar.gz" % commit
    if not espeak_source or os.path.basename(espeak_source) != name:
        raise PackError("--espeak-source must be %s (git archive of the pinned commit)" % name)
    if tar_commit(espeak_source) != commit:
        raise PackError("%s: its header does not record commit %s" % (name, commit))
    assets.append((name, "file", None, espeak_source))
    notes.append("espeak-ng commit %s (espeak-ng.lock.json; build_espeak.ps1)" % commit)

    for vname, v in sorted(read_json(VOICES)["voices"].items(), key=lambda kv: kv[1]["order"]):
        root = os.path.join(src, "voices")
        model = os.path.join(root, vname, v["model"])
        if not os.path.isfile(model) or sha256_file(model) != v["patched_sha256"]:
            raise PackError("voices/%s/%s missing or not the pinned patched model" % (vname, v["model"]))
        vroot = os.path.join(root, vname)
        assets.append(("voice-%s-%s.zip" % (vname, v["revision"][:8]), "zip", vroot, tree(vroot, ".")))
        notes.append("voice %s: piper-voices revision %s, patched model %s (voices.json)" % (vname, v["revision"], v["patched_sha256"]))
    for i, (n, kind, root, members) in enumerate(assets):
        if kind == "zip":
            assets[i] = (n, kind, root, [m[2:] if m.startswith("./") else m for m in members])
    return assets, notes


OUTPUTS = ("SHA256SUMS.txt", "BUILD-INFO.txt")


def clear_dest(dest, names, replace):
    """Empty ``dest`` for a new run: refused unless --replace, and even then when it holds anything but the outputs of
    this tool (the asset names, SHA256SUMS.txt, BUILD-INFO.txt, leftover .part files): never delete foreign files."""
    if not os.path.exists(dest) or not os.listdir(dest):
        return
    if not replace:
        raise PackError("%s is not empty (pass --replace)" % dest)
    ours = set(names) | set(OUTPUTS)
    ours |= {n + ".part" for n in ours}
    foreign = sorted(set(os.listdir(dest)) - ours)
    if foreign:
        raise PackError("%s holds %d file(s) that are not outputs of this tool (e.g. %s): refusing --replace"
                        % (dest, len(foreign), foreign[0]))
    for f in os.listdir(dest):
        os.remove(os.path.join(dest, f))


def package(src, dest, tag, espeak_source, replace=False, extra_notes=()):
    """Write the assets, SHA256SUMS.txt and BUILD-INFO.txt; returns [(name, bytes, sha256)]."""
    assets, notes = plan_assets(src, tag, espeak_source)
    clear_dest(dest, [a[0] for a in assets], replace)
    os.makedirs(dest, exist_ok=True)
    date = zip_date(tag)
    rows = []
    for name, kind, root, members in assets:
        path = os.path.join(dest, name)
        if kind == "zip":
            write_zip(path, root, members, date)
        else:
            shutil.copyfile(members, path)
        rows.append((name, os.path.getsize(path), sha256_file(path), len(members) if kind == "zip" else 1))
        print("  %-58s %12d  %s" % rows[-1][:3])
    with open(os.path.join(dest, "SHA256SUMS.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.writelines("%s *%s\n" % (h, n) for n, _, h, _ in rows)
    info = ["Pepite data release %s" % tag, "made by %s (deterministic zips: sorted members, date %04d-%02d-%02d 00:00, "
            "mode 644, deflate level 9)" % ((TOOL,) + date[:3]),
            "Python %s, zlib %s" % (platform.python_version(), zlib.ZLIB_RUNTIME_VERSION), "", "sources:"]
    info += ["  " + n for n in notes]
    if extra_notes:
        info += ["", "notes:"] + ["  " + n for n in extra_notes]
    info += ["", "assets (bytes, sha256, files):"]
    info += ["  %s %d %s %d" % r for r in rows]
    with open(os.path.join(dest, "BUILD-INFO.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(info) + "\n")
    return [(n, b, h) for n, b, h, _ in rows]


def update_lock(lock_path, rows):
    lock = read_json(lock_path)
    names = [a["name"] for a in lock["assets"]]
    if sorted(names) != sorted(n for n, _, _ in rows):
        raise PackError("the lock names other assets: %s" % ", ".join(sorted(set(names) ^ {n for n, _, _ in rows})))
    by = {n: (b, h) for n, b, h in rows}
    for a in lock["assets"]:
        a["bytes"], a["sha256"] = by[a["name"]]
    lines = ["{", '  "comment": %s,' % json.dumps(lock["comment"], ensure_ascii=False),
             '  "repo": %s,' % json.dumps(lock["repo"]), '  "tag": %s,' % json.dumps(lock["tag"]), '  "assets": [']
    lines += ["    %s%s" % (json.dumps({k: a[k] for k in ("name", "sha256", "bytes", "licence", "unpack_to")}, ensure_ascii=False),
                           "," if i < len(lock["assets"]) - 1 else "") for i, a in enumerate(lock["assets"])]
    lines += ["  ]", "}"]
    with open(lock_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--dest", required=True)
    ap.add_argument("--src", default=os.path.join(HERE, "out"))
    ap.add_argument("--espeak-source", required=True, help="espeak-ng-<commit>.tar.gz (git archive of the pinned commit)")
    ap.add_argument("--lock", default=RELEASE_LOCK)
    ap.add_argument("--replace", action="store_true")
    ap.add_argument("--write-lock", action="store_true", help="write the SHA-256 and sizes into the lock")
    ap.add_argument("--note", action="append", default=[],
                    help="a line for BUILD-INFO.txt (e.g. the accepted --scan-data notes, by term number only)")
    a = ap.parse_args(argv)
    tag = read_json(a.lock)["tag"]
    try:
        print("data release %s -> %s" % (tag, a.dest))
        rows = package(os.path.abspath(a.src), os.path.abspath(a.dest), tag, a.espeak_source, a.replace, a.note)
        print("lock fragment (assets):")
        for n, b, h in rows:
            print('  {"name": "%s", "sha256": "%s", "bytes": %d}' % (n, h, b))
        if a.write_lock:
            update_lock(a.lock, rows)
            print("wrote %s" % a.lock)
    except PackError as e:
        print("PROBLEM  %s" % e, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
