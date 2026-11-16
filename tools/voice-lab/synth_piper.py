"""Piper (piper1-gpl 1.8.0) voices: WAVs, latency, espeak-ng phonemes and word timings from the alignment output.

Run with the piper venv: ``python -I synth_piper.py --voice siwis [--profile 2core]``. The voice must have been
patched with ``python -m piper.patch_voice_with_alignment`` into ``models/piper-aligned/`` (plan 05 step 3); the
latency is measured on that patched model, the one plan 08 would ship.
"""

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # -I drops the script folder from sys.path

import numpy as np  # noqa: E402
import onnxruntime  # noqa: E402
from piper import PiperVoice, SynthesisConfig  # noqa: E402
from piper.config import PiperConfig  # noqa: E402
from piper.phonemize_espeak import ESPEAK_DATA_DIR  # noqa: E402

import common  # noqa: E402


def load(model: Path, threads: int | None) -> PiperVoice:
    so = onnxruntime.SessionOptions()
    if threads:
        so.intra_op_num_threads = threads
        so.inter_op_num_threads = 1
    config = PiperConfig.from_dict(json.loads(Path(str(model) + ".json").read_text(encoding="utf-8")))
    session = onnxruntime.InferenceSession(str(model), sess_options=so, providers=["CPUExecutionProvider"])
    return PiperVoice(config=config, session=session, espeak_data_dir=Path(ESPEAK_DATA_DIR), download_dir=model.parent)


def main() -> None:
    def extra(p):
        p.add_argument("--voice", required=True, choices=["siwis", "tom", "upmc"])
        p.add_argument("--speaker", default="", help="speaker name of a multi-speaker voice (upmc: jessica, pierre)")

    args = common.parse_args(__doc__, extra)
    threads = common.apply_profile(args.profile)
    run = common.Run(args, f"piper-{args.voice}" + (f"-{args.speaker}" if args.speaker else ""))
    model = args.root / "models" / "piper-aligned" / f"fr_FR-{args.voice}-medium.onnx"

    t0 = common.now_ms()
    voice = load(model, threads)
    run.loaded(common.now_ms() - t0)
    syn = SynthesisConfig(speaker_id=voice.config.speaker_id_map[args.speaker]) if args.speaker else None
    list(voice.synthesize("Bonjour.", syn_config=syn, include_alignments=True))  # warm-up

    sr = voice.config.sample_rate
    for item in run.items:
        for rep in range(run.reps_for(item)):
            chunks = []
            t0 = common.now_ms()
            first = None
            for chunk in voice.synthesize(item["text"], syn_config=syn, include_alignments=True):
                if first is None:
                    first = common.now_ms() - t0
                chunks.append(chunk)
            total = common.now_ms() - t0
            n = sum(len(c.audio_float_array) for c in chunks)
            run.add(item, rep, first, total, n / sr)
            if rep:
                continue
            common.write_wav(run.out / f"{item['id']}.wav", np.concatenate([c.audio_float_array for c in chunks]), sr)
            run.phonemes[item["id"]] = " | ".join("".join(c.phonemes) for c in chunks)
            spans, offset, ok = [], 0, True
            for c in chunks:
                if not c.phoneme_alignments:
                    ok = False
                    break
                pos = offset
                for a in c.phoneme_alignments:
                    spans.append((a.phoneme, pos / sr, (pos + a.num_samples) / sr))
                    pos += a.num_samples
                spans.append((" ", pos / sr, pos / sr))
                offset += len(c.audio_float_array)
            if ok:
                run.timings[item["id"]] = {"words": common.words_from_phoneme_spans(spans),
                                           "phonemes": [[p, round(s, 3), round(e, 3)] for p, s, e in spans]}
    run.finish({"sample_rate": sr, "threads": threads or "default", "model": model.name,
                "model_mb": common.mb(model.stat().st_size)})


if __name__ == "__main__":
    main()
