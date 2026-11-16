"""Kokoro-82M v1.0, voice ``ff_siwis``, through kokoro-onnx (fp32 or int8 ONNX export): WAVs, latency, phonemes and
word timings from the model's duration output.

Run with the kokoro venv: ``python -I synth_kokoro.py --variant fp32|int8 [--profile 2core]``. Kokoro renders a
whole batch (up to 510 phonemes) at once, so ``first_audio_ms`` equals ``total_ms`` for every test item.
"""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # -I drops the script folder from sys.path

import onnxruntime  # noqa: E402
from kokoro_onnx import Kokoro  # noqa: E402

import common  # noqa: E402

MODELS = {"fp32": "kokoro-v1.0.onnx", "int8": "kokoro-v1.0.int8.onnx"}
VOICE = "ff_siwis"
LANG = "fr-fr"


def main() -> None:
    args = common.parse_args(__doc__, lambda p: p.add_argument("--variant", required=True, choices=list(MODELS)))
    threads = common.apply_profile(args.profile)
    run = common.Run(args, f"kokoro-{args.variant}")
    model = args.root / "models" / "kokoro" / MODELS[args.variant]
    voices = args.root / "models" / "kokoro" / "voices-v1.0.bin"

    t0 = common.now_ms()
    so = onnxruntime.SessionOptions()
    if threads:
        so.intra_op_num_threads = threads
        so.inter_op_num_threads = 1
    session = onnxruntime.InferenceSession(str(model), sess_options=so, providers=["CPUExecutionProvider"])
    kokoro = Kokoro.from_session(session, str(voices))
    run.loaded(common.now_ms() - t0)
    kokoro.create_timed("Bonjour.", voice=VOICE, lang=LANG)  # warm-up

    sr = None
    for item in run.items:
        for rep in range(run.reps_for(item)):
            t0 = common.now_ms()
            audio, sr, spoken = kokoro.create_timed(item["text"], voice=VOICE, lang=LANG)
            total = common.now_ms() - t0
            run.add(item, rep, total, total, len(audio) / sr)
            if rep:
                continue
            common.write_wav(run.out / f"{item['id']}.wav", audio, sr)
            run.phonemes[item["id"]] = kokoro.tokenizer.phonemize(item["text"], LANG)
            if spoken:
                spans = [(t.phoneme, t.start, t.end) for t in spoken]
                run.timings[item["id"]] = {"words": common.words_from_phoneme_spans(spans),
                                           "phonemes": [[p, round(s, 3), round(e, 3)] for p, s, e in spans]}
    run.finish({"sample_rate": sr, "threads": threads or "default", "model": model.name,
                "model_mb": common.mb(model.stat().st_size), "voices_mb": common.mb(voices.stat().st_size),
                "has_timings": kokoro.has_timings})


if __name__ == "__main__":
    main()
