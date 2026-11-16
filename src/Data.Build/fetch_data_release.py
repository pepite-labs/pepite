#!/usr/bin/env python3
"""Fetch the data release into src/Data.Build/out/ (plan 13, A1). Stdlib only; run with ``python -I``.

    python -I src/Data.Build/fetch_data_release.py --cache <dir>        # download, check, unpack, verify
    python -I src/Data.Build/fetch_data_release.py --from <dir>         # the same from local copies of the assets
    python -I src/Data.Build/fetch_data_release.py --list               # print the plan only
      [--out <dir>] [--lock <data-release.lock.json>] [--only <asset name>]... [--replace]

Every asset of ``data-release.lock.json`` (GitHub Release ``<tag>`` of ``<repo>``) is downloaded into ``--cache`` (no
default: never inside the repository) or taken from ``--from``, checked against its pinned size and SHA-256, then
unpacked into ``<out>/<unpack_to>`` (``unpack_to: null`` = kept as a file, e.g. the espeak-ng source archive: its path is
printed). Unpacking refuses any member that would land outside its folder (zip-slip: absolute paths, drive letters,
``..``, links) and refuses to overwrite an existing file unless ``--replace``. Then ``verify_data_pack.py`` checks the
word base against ``data-pack.lock.json``.

Exit code 0 = everything fetched and verified, 1 = a check failed (nothing is unpacked when a check fails before
unpacking), 2 = bad arguments or an unpinned lock.
"""
import argparse
import hashlib
import json
import os
import posixpath
import shutil
import stat
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
LOCK = os.path.join(HERE, "data-release.lock.json")
sys.path.insert(0, HERE)
import verify_data_pack  # noqa: E402

USER_AGENT = "pepite-fetch-data-release"


class FetchError(Exception):
    """A check failed: the message says which asset and why."""


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def load_lock(path):
    with open(path, encoding="utf-8") as f:
        lock = json.load(f)
    names = set()
    for a in lock["assets"]:
        if a["name"] in names or "/" in a["name"] or "\\" in a["name"]:
            raise SystemExit("lock: bad or repeated asset name %r" % a["name"])
        names.add(a["name"])
        u = a.get("unpack_to")
        if u is not None and (u.startswith(("/", "\\")) or ":" in u or ".." in u.replace("\\", "/").split("/")):
            raise SystemExit("lock: unpack_to of %s must stay inside the out folder" % a["name"])
    return lock


def asset_url(lock, asset):
    return "https://github.com/%s/releases/download/%s/%s" % (lock["repo"], lock["tag"], asset["name"])


def check_file(path, asset):
    """Raise FetchError unless ``path`` has the pinned size and SHA-256."""
    if not os.path.isfile(path):
        raise FetchError("%s: missing (%s)" % (asset["name"], path))
    n = os.path.getsize(path)
    if n != asset["bytes"]:
        raise FetchError("%s: size %d, pinned %d" % (asset["name"], n, asset["bytes"]))
    if sha256_file(path) != asset["sha256"]:
        raise FetchError("%s: SHA-256 differs from the pin" % asset["name"])


def copy_limited(src, dst, limit, name):
    """Copy a stream, refusing it as soon as it is longer than ``limit`` bytes (the pinned size)."""
    n = 0
    for chunk in iter(lambda: src.read(1 << 20), b""):
        n += len(chunk)
        if limit is not None and n > limit:
            raise FetchError("%s: more than the pinned %d bytes, download stopped" % (name, limit))
        dst.write(chunk)
    return n


def download(url, dest, limit=None):
    """urllib download to ``dest`` through a .part file (https only), stopped past ``limit`` bytes."""
    if not url.startswith("https://"):
        raise FetchError("refusing a non-https URL: %s" % url)
    part = dest + ".part"
    req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    try:
        with urllib.request.urlopen(req, timeout=120) as r, open(part, "wb") as f:
            copy_limited(r, f, limit, os.path.basename(dest))
    except BaseException:
        if os.path.exists(part):
            os.remove(part)
        raise
    os.replace(part, dest)


def obtain(lock, asset, cache=None, src=None, fetch=download):
    """The local path of a checked copy of the asset (from ``src``, else the cache, downloading when needed)."""
    if src:
        p = os.path.join(src, asset["name"])
        check_file(p, asset)
        return p
    os.makedirs(cache, exist_ok=True)
    p = os.path.join(cache, asset["name"])
    if os.path.isfile(p):
        try:
            check_file(p, asset)
            return p
        except FetchError:
            os.remove(p)                       # a stale or partial cache entry: fetch it again once
    fetch(asset_url(lock, asset), p, asset["bytes"])
    check_file(p, asset)
    return p


def safe_members(zf, dest):
    """[(member, target path)] of the files of a zip; raises FetchError on a member outside ``dest`` or a link."""
    root = os.path.realpath(dest)
    out = []
    for m in zf.infolist():
        name = m.filename
        if name.endswith("/"):
            continue
        parts = name.replace("\\", "/").split("/")
        if (name.startswith(("/", "\\")) or ":" in name or ".." in parts or "" in parts
                or posixpath.isabs(name)):
            raise FetchError("zip-slip: member %r leaves its folder" % name)
        if stat.S_ISLNK(m.external_attr >> 16):
            raise FetchError("zip member %r is a link" % name)
        target = os.path.realpath(os.path.join(root, *parts))
        if os.path.commonpath([root, target]) != root:
            raise FetchError("zip-slip: member %r leaves its folder" % name)
        out.append((m, target))
    return out


def plan_unpack(lock, paths, out, replace):
    """[(asset, zip path, [(member, target)])]; raises FetchError on zip-slip or an existing file without --replace."""
    plan, existing = [], []
    for a in lock["assets"]:
        if a["name"] not in paths or a.get("unpack_to") is None:
            continue
        dest = os.path.join(out, *a["unpack_to"].replace("\\", "/").split("/"))
        with zipfile.ZipFile(paths[a["name"]]) as zf:
            members = safe_members(zf, dest)
        existing += [t for _, t in members if os.path.exists(t)]
        plan.append((a, paths[a["name"]], members))
    if existing and not replace:
        raise FetchError("%d file(s) already in %s (e.g. %s): pass --replace to overwrite them"
                         % (len(existing), out, os.path.relpath(existing[0], out)))
    return plan


def unpack(plan):
    n = 0
    for _, path, members in plan:
        with zipfile.ZipFile(path) as zf:
            for m, target in members:
                os.makedirs(os.path.dirname(target), exist_ok=True)
                with zf.open(m) as r, open(target, "wb") as f:
                    shutil.copyfileobj(r, f, 1 << 20)
                n += 1
    return n


def select(lock, only):
    if not only:
        return list(lock["assets"])
    names = {a["name"] for a in lock["assets"]}
    bad = [o for o in only if o not in names]
    if bad:
        raise SystemExit("--only: not in the lock: %s" % ", ".join(bad))
    return [a for a in lock["assets"] if a["name"] in only]


def main(argv=None, fetch=download):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--cache", help="download folder (required unless --from or --list)")
    ap.add_argument("--from", dest="src", help="folder holding local copies of the assets")
    ap.add_argument("--out", default=os.path.join(HERE, "out"))
    ap.add_argument("--lock", default=LOCK)
    ap.add_argument("--only", action="append", default=[], help="fetch only this asset (repeatable)")
    ap.add_argument("--replace", action="store_true", help="overwrite files already in --out")
    ap.add_argument("--list", action="store_true", help="print the plan and stop")
    ap.add_argument("--pack-lock", default=verify_data_pack.LOCK, help=argparse.SUPPRESS)
    a = ap.parse_args(argv)
    lock = load_lock(a.lock)
    assets = select(lock, a.only)
    if a.list:
        print("data release %s %s -> %s" % (lock["repo"], lock["tag"], a.out))
        for x in assets:
            print("  %-58s %12s  %s  -> %s" % (x["name"], x["bytes"], x["sha256"],
                                                "(kept as a file)" if x.get("unpack_to") is None else x["unpack_to"]))
            print("      %s  [%s]" % (asset_url(lock, x), x["licence"]))
        return 0
    unpinned = [x["name"] for x in assets if not x.get("sha256") or not x.get("bytes")]
    if unpinned:
        print("not pinned yet (no sha256 / bytes): %s" % ", ".join(unpinned), file=sys.stderr)
        return 2
    if not (a.cache or a.src):
        print("give --cache <dir> (downloads) or --from <dir> (local copies)", file=sys.stderr)
        return 2
    try:
        paths = {}
        for x in assets:
            paths[x["name"]] = obtain(lock, x, a.cache, a.src, fetch)
            print("ok       %s" % x["name"])
        plan = plan_unpack(lock, paths, a.out, a.replace)
        print("unpacked %d file(s) into %s" % (unpack(plan), a.out))
    except (FetchError, OSError, zipfile.BadZipFile) as e:
        print("PROBLEM  %s" % e, file=sys.stderr)
        return 1
    for x in assets:
        if x.get("unpack_to") is None:
            print("file     %s" % paths[x["name"]])
    if any(x.get("unpack_to") == "." and x["name"].startswith("pepite-data-") for x in assets):
        with open(a.pack_lock, encoding="utf-8") as f:
            pack = json.load(f)
        bad = [(p, why) for p, why in verify_data_pack.verify(a.out, pack) if why]
        for p, why in bad:
            print("PROBLEM  %s: %s" % (p, why), file=sys.stderr)
        print("data pack %s %s: %d problem(s)" % (pack["pack"]["id"], pack["pack"]["version"], len(bad)))
        if bad:
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
