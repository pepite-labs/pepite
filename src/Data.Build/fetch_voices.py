#!/usr/bin/env python3
"""Fetch, patch and pack the Piper voices of Predict.Keyboard (plans 08a, 08b; D7, D19, D20, D28).

The voice model is used as published (piper-voices, pinned revision) except for one change Piper itself documents
(``piper.patch_voice_with_alignment``): the model's ``Ceil`` tensor (frames per phoneme id) is marked as a graph
output, so the app knows where each word starts in the audio. That change is re-written here (about 15 lines), done
with the pinned ``onnx`` package, and must give the pinned SHA-256 byte for byte.

Sub-commands (run with ``python -I``):

    fetch   download the pinned files to --raw (env PREDICT_VOICES_RAW; **no default**, about 140 MB):
            <name>.part, hash-checked, then renamed; a mismatch deletes the file (stdlib only)
    patch   add the alignment output to the model in --raw (needs ``onnx``: the venv of
            requirements-voices.txt); fails unless the result has the pinned SHA-256
    pack    --out <dir> (default Data.Build/out/voices, git-ignored) -> <dir>/<id>/ with the patched model, its
            .onnx.json, MODEL_CARD, LICENSE.txt (the voice's licence), any other pinned file (pierre: DATASET-LICENSE.md),
            SOURCE.txt (the change notice) and voice.json (the app's card: sizes and SHA-256 of every file, speaker,
            order, licence, public); refuses to replace a folder it did not write
    all     fetch + patch + pack

    --voice <id> limits any command to one voice of voices.json (default: all of them).

Licences (``licence`` in voices.json): siwis is CC BY 4.0 (SIWIS dataset; base model hifi_tts_92 on Hi-Fi TTS, CC BY
4.0); pierre (upmc) is CC BY-SA 4.0 (UPMC Pierre Voice Data), so the modified model stays under CC BY-SA 4.0. The
modification is stated in SOURCE.txt and in the app's « À propos ». ``"public": false`` is copied to the card: such a
pack ships in private test releases only (D28). pierre is public since 2026-11-16 (D28 changed): its base model was
fine-tuned from lessac, a research-only corpus, so its ``risk_note`` is written to SOURCE.txt (withdrawal path: set
``"public": false`` again and publish a new release).
"""
import argparse
import datetime as _dt
import hashlib
import json
import os
import shutil
import sys
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
PINS = os.path.join(HERE, "voices.json")
DEFAULT_OUT = os.path.join(HERE, "out", "voices")
USER_AGENT = "PredictKeyboard-DataBuild/0.5 (voice fetch)"
MARKER = ".packed-by-fetch_voices"
ONNX_VERSION = "1.23.2"


def load_pins(path=PINS):
    with open(path, encoding="utf-8") as f:
        return json.load(f)["voices"]


def select(pins, voice):
    """All pins, or only ``voice`` (unknown id: exit)."""
    if voice is None:
        return pins
    if voice not in pins:
        raise SystemExit(f"unknown voice {voice!r}: {', '.join(sorted(pins))}")
    return {voice: pins[voice]}


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def raw_name(voice_id, name):
    return f"{voice_id}__{name}"


def fetch(raw, pins, opener=None):
    os.makedirs(raw, exist_ok=True)
    opener = opener or (lambda url: urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": USER_AGENT}), timeout=60))
    for vid, v in pins.items():
        for f in v["files"]:
            dest = os.path.join(raw, raw_name(vid, f["name"]))
            if os.path.exists(dest) and sha256_file(dest) == f["sha256"]:
                print(f"  ok     {f['name']}")
                continue
            part = dest + ".part"
            with opener(f["url"]) as r, open(part, "wb") as o:
                shutil.copyfileobj(r, o)
            got = sha256_file(part)
            if got != f["sha256"]:
                os.remove(part)
                raise SystemExit(f"SHA-256 mismatch for {f['name']}: got {got}, expected {f['sha256']}")
            os.replace(part, dest)
            print(f"  fetched {f['name']}")


def add_alignment_output(model):
    """Mark the single Ceil node's output as a graph output (what piper.patch_voice_with_alignment does)."""
    import onnx  # noqa: PLC0415 (only the patch step needs it)

    names = {o for node in model.graph.node if node.op_type == "Ceil" for o in node.output}
    if len(names) != 1:
        raise SystemExit(f"expected one Ceil tensor, found {sorted(names)}")
    name = next(iter(names))
    if any(o.name == name for o in model.graph.output):
        raise SystemExit(f"{name} is already an output")
    vi = onnx.ValueInfoProto()
    vi.name = name
    model.graph.output.append(vi)
    return name


def patched_path(raw, vid, v):
    return os.path.join(raw, raw_name(vid, v["model"]) + ".patched")


def patch(raw, pins):
    import onnx  # noqa: PLC0415

    if onnx.__version__ != ONNX_VERSION:
        raise SystemExit(f"onnx {onnx.__version__}: the pinned patch needs onnx {ONNX_VERSION} (requirements-voices.txt)")
    for vid, v in pins.items():
        out = patched_path(raw, vid, v)
        if os.path.exists(out) and sha256_file(out) == v["patched_sha256"]:
            print(f"  ok     {vid} (patched)")
            continue
        model = onnx.load(os.path.join(raw, raw_name(vid, v["model"])))
        name = add_alignment_output(model)
        onnx.save(model, out + ".part")
        got = sha256_file(out + ".part")
        if got != v["patched_sha256"]:
            os.remove(out + ".part")
            raise SystemExit(f"patched {vid}: SHA-256 {got}, expected {v['patched_sha256']}")
        os.replace(out + ".part", out)
        print(f"  patched {vid}: {name} added")


def pack(raw, out, pins, today=None):
    today = today or _dt.date.today().isoformat()
    for vid, v in pins.items():
        dest = os.path.join(out, vid)
        if os.path.exists(dest):
            if not os.path.exists(os.path.join(dest, MARKER)):
                raise SystemExit(f"{dest} exists and was not written by fetch_voices.py: refusing to replace it")
            shutil.rmtree(dest)
        os.makedirs(dest)
        files = {}
        for f in v["files"]:
            src = patched_path(raw, vid, v) if f["name"] == v["model"] else os.path.join(raw, raw_name(vid, f["name"]))
            shutil.copyfile(src, os.path.join(dest, f["name"]))
        if sha256_file(os.path.join(dest, v["model"])) != v["patched_sha256"]:
            raise SystemExit(f"{vid}: packed model is not the patched one")
        upstream = {f["name"]: f["sha256"] for f in v["files"]}
        lines = [
            f"Piper voice {v['model'][:-5]} for Predict.Keyboard, packed by Data.Build/fetch_voices.py on {today}.",
            f"Source: https://huggingface.co/rhasspy/piper-voices (revision {v['revision']})",
        ]
        lines += [f"  {f['url']}  sha256 {f['sha256']}" for f in v["files"]]
        lines += [
            f"modifié : sortie /Ceil_output_0 ajoutée (onnx {ONNX_VERSION}) — the Ceil tensor is marked as a graph output,",
            f"  as piper.patch_voice_with_alignment does; upstream sha256 {upstream[v['model']]},",
            f"  modified sha256 {v['patched_sha256']}.",
            f"Licence: {v['licence']} (LICENSE.txt). Attribution:",
        ] + [f"  {a}" for a in v["attribution"]]
        if "BY-SA" in v["licence"]:
            lines.append(f"ShareAlike : le modèle modifié reste sous {v['licence']}.")
        if not v.get("public", True):
            lines.append("Not public: private test releases only (D28).")
        if v.get("risk_note"):
            lines.append(f"Note: {v['risk_note']}")
        with open(os.path.join(dest, "SOURCE.txt"), "w", encoding="utf-8", newline="\n") as fo:
            fo.write("\n".join(lines) + "\n")
        names = [f["name"] for f in v["files"]] + ["SOURCE.txt"]
        card = {
            "id": vid, "name": v["name"], "gender": v["gender"], "culture": v["culture"], "sampleRate": v["sampleRate"],
            "model": v["model"], "config": v["model"] + ".json",
            "sizes": {n: os.path.getsize(os.path.join(dest, n)) for n in names},
            "sha256": {n: sha256_file(os.path.join(dest, n)) for n in names},
            "speaker": v.get("speaker"), "order": v.get("order", 100), "licence": v["licence"],
            "public": v.get("public", True), "attribution": v["attribution"],
        }
        if "inference" in v:   # optional override of the .onnx.json scales (the .onnx.json itself is never edited)
            card["inference"] = v["inference"]
        if "inference_word" in v:   # optional lower noise for isolated-word utterances (noise_scale, noise_w)
            card["inference_word"] = v["inference_word"]
        if "word_carrier" in v:   # optional carrier sentence for isolated words (« Le mot : {0}. », the word cut out)
            card["word_carrier"] = v["word_carrier"]
        if "word_carrier_except" in v:   # optional words always said alone (siwis: « eu », « les »; compared trimmed, lower case)
            card["word_carrier_except"] = v["word_carrier_except"]
        if "phoneme_fix" in v:   # optional phoneme post-corrections ([from, to] pairs, after espeak)
            card["phoneme_fix"] = v["phoneme_fix"]
        with open(os.path.join(dest, "voice.json"), "w", encoding="utf-8", newline="\n") as fo:
            json.dump(card, fo, ensure_ascii=False, indent=2)
            fo.write("\n")
        open(os.path.join(dest, MARKER), "w").close()
        total = sum(card["sizes"].values())
        print(f"  packed {dest} ({total / 1e6:.1f} MB)")


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("command", choices=["fetch", "patch", "pack", "all"])
    p.add_argument("--raw", default=os.environ.get("PREDICT_VOICES_RAW"), help="download folder (env PREDICT_VOICES_RAW)")
    p.add_argument("--out", default=DEFAULT_OUT)
    p.add_argument("--voice", help="only this voice id of voices.json")
    a = p.parse_args(argv)
    if not a.raw:
        sys.exit("no download folder: pass --raw <dir> or set PREDICT_VOICES_RAW (about 140 MB; keep it off C:)")
    pins = select(load_pins(), a.voice)
    if a.command in ("fetch", "all"):
        fetch(a.raw, pins)
    if a.command in ("patch", "all"):
        patch(a.raw, pins)
    if a.command in ("pack", "all"):
        pack(a.raw, a.out, pins)


if __name__ == "__main__":
    main()
