"""Golden phonemes / ids of the 40 plan 05 test items from piper1-gpl 1.8.0, for the C# port (plans 08a, 08b step 2).

Run with the voice-lab piper venv (the reference implementation, GPL-3.0-or-later, and its bundled espeak-ng):

    <root>\\venvs\\piper\\Scripts\\python.exe -I -X utf8 tools\\voice-lab\\golden_ids.py --root <root> \\
        --out src\\Predict.Keyboard.Tests\\Fixtures\\piper-golden-siwis.json
    ... golden_ids.py --root <root> --model fr_FR-upmc-medium --speaker pierre \\
        --out src\\Predict.Keyboard.Tests\\Fixtures\\piper-golden-pierre.json

The defaults (``--model fr_FR-siwis-medium``, no speaker) reproduce the siwis file unchanged. With ``--speaker`` the
voice must be multi-speaker: the alignments are synthesised with ``SynthesisConfig(speaker_id=speaker_id_map[...])``
and ``meta`` adds the speaker, its id, ``num_speakers`` and the three inference scales.

Per item: ``clauses`` (raw ``espeakbridge.get_phonemes`` tuples after ``set_voice("fr")``), ``sentences``
(``PiperVoice.phonemize``), ``ids`` (``phonemes_to_ids`` per sentence) and ``words`` (each sentence's phonemes joined
and split at spaces). For a few items (liaisons, elisions, numbers, an isolated word) one synthesis with alignments
gives ``align``: ``[phoneme, samples]`` per sentence. The model output is not deterministic (noise), so that block is
recorded once and a re-run changes nothing else. ``meta`` carries the versions, the voice's id map and vowel clusters,
so the C# tests run without the voice installed.

Compact JSON, one item per line; refuses to write more than 64 KB. Only our own test sentences go in.
"""

import argparse
import hashlib
import json
import sys
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # -I drops the script folder from sys.path

import onnxruntime  # noqa: E402
import piper  # noqa: E402
from piper import PiperVoice  # noqa: E402
from piper import espeakbridge  # noqa: E402
from piper.config import PiperConfig, SynthesisConfig  # noqa: E402
from piper.phonemize_espeak import ESPEAK_DATA_DIR  # noqa: E402

import common  # noqa: E402

ALIGN_ITEMS = ["L01", "L02", "S06", "S13", "N01", "N02", "W01"]
MAX_BYTES = 64 * 1024


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    common.add_root(p)
    p.add_argument("--sentences", type=Path, default=common.DEFAULT_SENTENCES)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--model", default="fr_FR-siwis-medium", help="voice under <root>/models/piper-aligned/ (patched)")
    p.add_argument("--speaker", default=None, help="speaker name of a multi-speaker voice (speaker_id_map)")
    p.add_argument("--keep-align", action="store_true", default=True,
                   help="reuse the alignment block of an existing --out (default: on; the model output is noisy)")
    p.add_argument("--new-align", dest="keep_align", action="store_false", help="re-record the alignments")
    args = p.parse_args()

    model = args.root / "models" / "piper-aligned" / f"{args.model}.onnx"
    cfg_path = Path(str(model) + ".json")
    config_dict = json.loads(cfg_path.read_text(encoding="utf-8"))
    config = PiperConfig.from_dict(config_dict)
    syn_config = None
    if args.speaker is not None:
        if config.num_speakers <= 1 or args.speaker not in config.speaker_id_map:
            sys.exit(f"{args.model}: no speaker {args.speaker!r} ({config.num_speakers} speakers)")
        syn_config = SynthesisConfig(speaker_id=config.speaker_id_map[args.speaker])

    old_align: dict[str, list] = {}
    if args.keep_align and args.out.exists():
        for line in args.out.read_text(encoding="utf-8").splitlines():
            if not line.startswith('{"id"'):
                continue
            row = json.loads(line.rstrip(","))
            if "align" in row:
                old_align[row["id"]] = row["align"]

    voice = None
    items = common.load_items(args.sentences)
    rows = []
    espeakbridge.initialize(str(ESPEAK_DATA_DIR))
    for item in items:
        text = item["text"]
        espeakbridge.set_voice(config.espeak_voice)
        clauses = [[ph, term, bool(eos)] for ph, term, eos in espeakbridge.get_phonemes(text)]
        if voice is None:
            session = onnxruntime.InferenceSession(str(model), providers=["CPUExecutionProvider"])
            voice = PiperVoice(config=config, session=session, espeak_data_dir=Path(ESPEAK_DATA_DIR))
        sentences = voice.phonemize(text)
        ids = [voice.phonemes_to_ids(s) for s in sentences]
        words = ["".join(s).split(" ") for s in sentences]
        row = {"id": item["id"], "text": text, "clauses": clauses, "sentences": sentences, "ids": ids, "words": words}
        if item["id"] in ALIGN_ITEMS:
            if item["id"] in old_align:
                row["align"] = old_align[item["id"]]
            else:
                row["align"] = [[[a.phoneme, int(a.num_samples)] for a in (c.phoneme_alignments or [])]
                                for c in voice.synthesize(text, syn_config=syn_config, include_alignments=True)]
        rows.append(row)

    bridge = Path(piper.__file__).parent / "espeakbridge.pyd"
    meta = {
        "meta": {
            "piper_tts": "1.8.0",
            "espeak_ng": "1.52.0.1",
            "espeakbridge_sha256": sha256(bridge),
            "voice": args.model,
            "voice_json_sha256": sha256(cfg_path),
            "espeak_voice": config.espeak_voice,
            "hop_length": config_dict.get("hop_length", 256),
            "sample_rate": config.sample_rate,
            "vowel_clusters": config_dict.get("vowel_clusters", []),
            "id_map": config_dict["phoneme_id_map"],
            "date": date.today().isoformat(),
        }
    }
    if args.speaker is not None:
        meta["meta"].update({
            "speaker": args.speaker,
            "speaker_id": config.speaker_id_map[args.speaker],
            "num_speakers": config.num_speakers,
            "noise_scale": config.noise_scale,
            "length_scale": config.length_scale,
            "noise_w": config.noise_w_scale,
        })
    lines = [json.dumps(meta, ensure_ascii=False, separators=(",", ":"))]
    lines += [json.dumps(r, ensure_ascii=False, separators=(",", ":")) for r in rows]
    body = "[\n" + ",\n".join(lines) + "\n]\n"
    size = len(body.encode("utf-8"))
    if size > MAX_BYTES:
        sys.exit(f"refusing to write {size} bytes (> {MAX_BYTES})")
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(body, encoding="utf-8", newline="\n")
    print(f"{args.out}: {len(rows)} items, {size} bytes, align for {sum('align' in r for r in rows)} items")


if __name__ == "__main__":
    main()
