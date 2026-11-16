"""Download the pinned model files of models.json under <root>/models and check their SHA-256.

Plan 05 (D19, D25). Run with ``python -I fetch_models.py <root> [--pin] [group ...]``.

- A download goes to a ``.part`` file, is hashed there, and only replaces the target when the hash matches; a
  mismatching ``.part`` is deleted. An interrupted or tampered download never becomes a model file.
- A file already present is re-hashed; a mismatch fails the run (delete it to fetch it again).
- An entry with an empty SHA-256 fails the run, unless ``--pin`` is given: then its hash is printed (UNPINNED) so it
  can be written into models.json.

Exit code 0 only when every selected file matches its pinned SHA-256.
"""

import argparse
import hashlib
import json
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
TIMEOUT_S = 60  # per socket operation (connect / read), not for the whole download


def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def download(url: str, part: Path) -> None:
    req = urllib.request.Request(url, headers={"User-Agent": "voice-lab/plan05"})
    with urllib.request.urlopen(req, timeout=TIMEOUT_S) as resp, open(part, "wb") as out:
        while block := resp.read(1 << 20):
            out.write(block)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("root", type=Path, help="voice-lab root (bulk-data folder); models go to <root>/models")
    p.add_argument("groups", nargs="*", help="manifest groups to fetch (default: all)")
    p.add_argument("--pin", action="store_true", help="accept entries without a SHA-256 and print their hash")
    args = p.parse_args()

    root = args.root / "models"
    groups = set(args.groups)
    manifest = json.loads((HERE / "models.json").read_text(encoding="utf-8"))
    bad = 0
    for entry in manifest["files"]:
        if groups and entry["group"] not in groups:
            continue
        dest = root / entry["path"]
        dest.parent.mkdir(parents=True, exist_ok=True)
        expected = entry.get("sha256", "")
        if not expected and not args.pin:
            print(f"UNPINNED\t-\t-\t{entry['path']}  (no SHA-256 in models.json; run with --pin to obtain it)")
            bad += 1
            continue
        if dest.exists():
            digest = sha256(dest)
        else:
            part = dest.with_name(dest.name + ".part")
            try:
                download(entry["url"], part)
                digest = sha256(part)
            except Exception as error:  # network error, timeout, disk full: never leave a partial file behind
                part.unlink(missing_ok=True)
                print(f"FAILED\t-\t-\t{entry['path']}  ({error})")
                bad += 1
                continue
            if expected and digest != expected:
                part.unlink()
                print(f"MISMATCH\t-\t{digest}\t{entry['path']}  (download deleted)")
                bad += 1
                continue
            part.replace(dest)
        status = "ok" if digest == expected else ("UNPINNED" if not expected else "MISMATCH")
        bad += status == "MISMATCH"
        print(f"{status}\t{dest.stat().st_size}\t{digest}\t{entry['path']}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
