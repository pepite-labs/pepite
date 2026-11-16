#!/usr/bin/env python3
"""Fetch and pack the three bundled dyslexia-friendly fonts of Predict.Keyboard (D16).

OpenDyslexic (Abbie Gonzalez), Lexend (The Lexend Project Authors) and Andika (SIL Global) are all under the
SIL Open Font License 1.1, each with Reserved Font Names. The OFL lets us redistribute them with the app as
long as every font file is shipped **unmodified** together with its licence: this script therefore never
touches a font (no subsetting, no format conversion, no renaming) — every file is checked byte for byte
against the SHA-256 pinned below and copied as is, beside its upstream OFL.txt (+ FONTLOG / README).

Sub-commands (run with ``python -I``; stdlib only):

    fetch   download the pinned files to --raw (default $PREDICT_FONTS_RAW, else Data.Build/out/raw-fonts;
            resumable: <name>.part, then renamed once the hash matches)
    pack    --out <dir>  ->  <dir>/{OpenDyslexic,Lexend,Andika}/ with the fonts, licence files and a generated
            SOURCE.txt (URL, version/commit, SHA-256, date); fails above the 8 MB size gate
    all     fetch + pack (default --out: Data.Build/out/fonts, git-ignored, copied next to the exe by the csproj)

Sources (checked 2026-11-08): Lexend — github.com/googlefonts/lexend @ 7894f02 (2023-03-02), static TTFs;
OpenDyslexic — forge.hackers.town/antijingoist/opendyslexic tag v0.91.12 (2019-10-17; the GitHub repo is
archived, kept as the fallback URL); Andika — software.sil.org Andika-7.000.zip (2025-06-02).
"""
import argparse
import datetime as _dt
import hashlib
import os
import shutil
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
USER_AGENT = "PredictKeyboard-DataBuild/0.3 (font fetch)"
SIZE_GATE = 8 * 1024 * 1024

_LEX = "https://raw.githubusercontent.com/googlefonts/lexend/7894f02b2e7eabc48595f1d4eff3b17b48c6e651/"
_OD = "https://forge.hackers.town/antijingoist/opendyslexic/raw/tag/v0.91.12/"
_OD_GH = "https://raw.githubusercontent.com/antijingoist/opendyslexic/v0.91.12/"

# raw name -> (urls, sha256)
RAW = {
    "lexend_Lexend-Regular.ttf": ([_LEX + "fonts/lexend/ttf/Lexend-Regular.ttf"],
                                  "e2082c28389e9871d5d77ae9163d5c0c1c259fe39bdac91f73d56a600b61f60f"),
    "lexend_Lexend-SemiBold.ttf": ([_LEX + "fonts/lexend/ttf/Lexend-SemiBold.ttf"],
                                   "204875848cc7981290a75f8be0e64b335b6d8f6ec6cc3ea05d907a37adab8b7b"),
    "lexend_OFL.txt": ([_LEX + "OFL.txt"], "5da8505887d0fa7fe963445fd58852707fda34adfeb65af25c99d152bab285bd"),
    "od_OpenDyslexic-Regular.otf": ([_OD + "compiled/OpenDyslexic-Regular.otf", _OD_GH + "compiled/OpenDyslexic-Regular.otf"],
                                    "c1a0cbf814a75a61abdf0f18143d347222b85e51209f47f8b685a1e0ca594b88"),
    "od_OpenDyslexic-Bold.otf": ([_OD + "compiled/OpenDyslexic-Bold.otf", _OD_GH + "compiled/OpenDyslexic-Bold.otf"],
                                 "0901b984a2c3fdc67e3fb8e2d7e1d373bd6d54c3fab9c6b5017f736a25d8ff13"),
    "od_OFL.txt": ([_OD + "OFL.txt", _OD_GH + "OFL.txt"],
                   "caafcccfb70fc72458fcbda812ec8f0a06cb300cbabb87eabbb30b946124394b"),
    "od_FONTLOG.txt": ([_OD + "FONTLOG.txt", _OD_GH + "FONTLOG.txt"],
                       "322831f8d145f2b9a83bc4188e6e450a53d3bb72f8f9b732136d7800f91110ce"),
    "Andika-7.000.zip": (["https://software.sil.org/downloads/r/andika/Andika-7.000.zip"],
                         "88ba6ea41ef4a8e5214b090df8fa2983be1babe4843efaa99cdb6078b0e2c070"),
}

# packed folder -> (source description, [(packed name, raw name, zip member or None, sha256)])
PACK = {
    "OpenDyslexic": ("OpenDyslexic v0.91.12 (2019-10-17), " + _OD, [
        ("OpenDyslexic-Regular.otf", "od_OpenDyslexic-Regular.otf", None, RAW["od_OpenDyslexic-Regular.otf"][1]),
        ("OpenDyslexic-Bold.otf", "od_OpenDyslexic-Bold.otf", None, RAW["od_OpenDyslexic-Bold.otf"][1]),
        ("OFL.txt", "od_OFL.txt", None, RAW["od_OFL.txt"][1]),
        ("FONTLOG.txt", "od_FONTLOG.txt", None, RAW["od_FONTLOG.txt"][1]),
    ]),
    "Lexend": ("Lexend, googlefonts/lexend commit 7894f02b2e7eabc48595f1d4eff3b17b48c6e651 (2023-03-02), " + _LEX, [
        ("Lexend-Regular.ttf", "lexend_Lexend-Regular.ttf", None, RAW["lexend_Lexend-Regular.ttf"][1]),
        ("Lexend-SemiBold.ttf", "lexend_Lexend-SemiBold.ttf", None, RAW["lexend_Lexend-SemiBold.ttf"][1]),
        ("OFL.txt", "lexend_OFL.txt", None, RAW["lexend_OFL.txt"][1]),
    ]),
    "Andika": ("Andika 7.000 (2025-06-02), " + RAW["Andika-7.000.zip"][0][0], [
        ("Andika-Regular.ttf", "Andika-7.000.zip", "Andika-7.000/Andika-Regular.ttf",
         "27484fdc98d0d63f90407f8266e28295f6fb16d2b13c5024df0214f17152919a"),
        ("Andika-SemiBold.ttf", "Andika-7.000.zip", "Andika-7.000/Andika-SemiBold.ttf",
         "73c4df767769af2e545bf66ca4caabd4294ce61eb7206a9908eedfd6a7cc965e"),
        ("OFL.txt", "Andika-7.000.zip", "Andika-7.000/OFL.txt",
         "fd0f044f061aa463fa1675a71fa0c229a067e2062c321c89e5f20965883f23b2"),
        ("FONTLOG.txt", "Andika-7.000.zip", "Andika-7.000/FONTLOG.txt",
         "2a789c40e44d9b29bfbad138fee19debbe3d91da0caf29fc3df8ce79d9c6f624"),
        ("README.txt", "Andika-7.000.zip", "Andika-7.000/README.txt",
         "de9ff1789f9512826658b76aec71ad98c1c8c522a2e527493966dc81e4ffd997"),
    ]),
}


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def download(urls, dest, sha, opener=urllib.request.urlopen):
    """Download to dest.part (appending to a partial file when the server honours Range), check the pin,
    then rename. Returns the URL used. Raises on a hash mismatch (the .part is deleted)."""
    part = dest + ".part"
    last = None
    for url in urls:
        try:
            have = os.path.getsize(part) if os.path.exists(part) else 0
            req = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            if have:
                req.add_header("Range", "bytes=%d-" % have)
            with opener(req, timeout=60) as r:
                mode = "ab" if have and getattr(r, "status", 200) == 206 else "wb"
                with open(part, mode) as f:
                    shutil.copyfileobj(r, f)
            got = sha256_file(part)
            if got != sha:
                os.remove(part)
                raise ValueError("SHA-256 mismatch for %s: %s (expected %s)" % (url, got, sha))
            os.replace(part, dest)
            return url
        except ValueError:
            raise
        except Exception as e:   # network error: try the next mirror
            last = e
            print("  %s: %s" % (url, e), file=sys.stderr)
    raise RuntimeError("all sources failed for %s: %s" % (os.path.basename(dest), last))


def cmd_fetch(a):
    os.makedirs(a.raw, exist_ok=True)
    for name, (urls, sha) in RAW.items():
        dest = os.path.join(a.raw, name)
        if os.path.exists(dest) and sha256_file(dest) == sha:
            print("ok (cached) %s" % name)
            continue
        url = download(urls, dest, sha)
        print("ok %s <- %s" % (name, url))
    return 0


def is_previous_output(out):
    """True when --out is empty or holds only family folders written by an earlier pack (each with SOURCE.txt):
    the only kind of folder pack may delete and rebuild."""
    if not os.path.isdir(out):
        return True
    for name in os.listdir(out):
        p = os.path.join(out, name)
        if name not in PACK or not os.path.isdir(p) or not os.path.isfile(os.path.join(p, "SOURCE.txt")):
            return False
    return True


def cmd_pack(a):
    total = 0
    today = _dt.date.today().isoformat()
    if not is_previous_output(a.out):
        raise SystemExit("refusing to replace %s: it is not an earlier fonts output (no SOURCE.txt / other files)" % a.out)
    if os.path.isdir(a.out):
        shutil.rmtree(a.out)
    for folder, (source, files) in PACK.items():
        d = os.path.join(a.out, folder)
        os.makedirs(d)
        lines = ["%s — SIL Open Font License 1.1 (see OFL.txt)." % folder, "Source: " + source,
                 "Files are byte-identical to upstream (never modified: Reserved Font Name).",
                 "Packed %s by Data.Build/fetch_fonts.py." % today, ""]
        for packed, raw, member, sha in files:
            src = os.path.join(a.raw, raw)
            if not os.path.exists(src):
                raise SystemExit("missing raw file %s: run fetch first" % src)
            dest = os.path.join(d, packed)
            if member is None:
                shutil.copyfile(src, dest)
            else:
                with zipfile.ZipFile(src) as z, z.open(member) as m, open(dest, "wb") as f:
                    shutil.copyfileobj(m, f)
            got = sha256_file(dest)
            if got != sha:
                raise SystemExit("SHA-256 mismatch for %s/%s: %s" % (folder, packed, got))
            size = os.path.getsize(dest)
            total += size
            lines.append("%s  %d bytes  sha256 %s" % (packed, size, sha))
        with open(os.path.join(d, "SOURCE.txt"), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(lines) + "\n")
        total += os.path.getsize(os.path.join(d, "SOURCE.txt"))
        print("packed %s (%d files)" % (folder, len(files)))
    print("fonts: %d bytes (%.2f MB) in %s" % (total, total / 1048576, a.out))
    if total > SIZE_GATE:
        raise SystemExit("size gate: %d bytes > %d" % (total, SIZE_GATE))
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    # raw downloads: --raw, else $PREDICT_FONTS_RAW, else out/raw-fonts (git-ignored)
    ap.add_argument("--raw", default=os.environ.get("PREDICT_FONTS_RAW") or os.path.join(HERE, "out", "raw-fonts"))
    ap.add_argument("--out", default=os.path.join(HERE, "out", "fonts"))
    ap.add_argument("cmd", choices=["fetch", "pack", "all"], nargs="?", default="all")
    a = ap.parse_args(argv)
    if a.cmd in ("fetch", "all"):
        cmd_fetch(a)
    if a.cmd in ("pack", "all"):
        cmd_pack(a)
    return 0


if __name__ == "__main__":
    sys.exit(main())
