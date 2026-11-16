#!/usr/bin/env python3
"""Check the prebuilt data pack in src/Data.Build/out/ against data-pack.lock.json (owner 2026-11-16).

    python -I src/Data.Build/verify_data_pack.py [--out <dir>]      # exit 0 when every file has its pinned SHA-256 and size

Normal builds use the pack we publish (plan 13: a versioned GitHub Release asset) instead of rebuilding the word base
from the corpora; this script is the check after copying it into place. Stdlib only.
"""
import argparse
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
LOCK = os.path.join(HERE, "data-pack.lock.json")


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def verify(out, lock):
    """[(path, problem or None)] for every file of the pack."""
    res = []
    for f in lock["files"]:
        p = os.path.join(out, *f["path"].split("/"))
        if not os.path.isfile(p):
            res.append((f["path"], "missing"))
        elif os.path.getsize(p) != f["bytes"]:
            res.append((f["path"], "size %d, pinned %d" % (os.path.getsize(p), f["bytes"])))
        elif sha256_file(p) != f["sha256"]:
            res.append((f["path"], "SHA-256 differs from the pin"))
        else:
            res.append((f["path"], None))
    return res


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=os.path.join(HERE, "out"))
    ap.add_argument("--lock", default=LOCK)
    a = ap.parse_args(argv)
    with open(a.lock, encoding="utf-8") as f:
        lock = json.load(f)
    bad = 0
    for path, problem in verify(a.out, lock):
        print("%-8s %s%s" % ("ok" if problem is None else "PROBLEM", path, "" if problem is None else ": " + problem))
        bad += problem is not None
    print("data pack %s %s: %d problem(s)" % (lock["pack"]["id"], lock["pack"]["version"], bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
