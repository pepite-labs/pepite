"""Automatic transcription of every engine's WAVs with Whisper (faster-whisper, model ``medium``, int8 on CPU), for
the intelligibility measure of D25 (WER / CER) and as the reference clock of the word-timing check.

Run with the whisper venv: ``python -I transcribe.py [--root <bulk-data folder>] [--engines a,b] [--threads 4]``.
Writes ``out/normal/<engine>/transcripts.json``: {id: {text, words: [{w, start, end, p}]}}. Decoding is
deterministic (beam 5, temperature 0, no previous-text conditioning, no prompt) so no engine is favoured.
"""

import argparse
import json
import sys
import time
import wave
from math import gcd
from pathlib import Path

import numpy as np
from faster_whisper import WhisperModel
from scipy.signal import resample_poly

sys.path.insert(0, str(Path(__file__).resolve().parent))  # -I drops the script folder from sys.path

import common  # noqa: E402


def load_16k(path: Path) -> np.ndarray:
    """16-bit mono WAV -> float32 at 16 kHz (decoded here: the PyAV wheel faster-whisper wants is not usable)."""
    with wave.open(str(path), "rb") as w:
        sr = w.getframerate()
        pcm = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
    g = gcd(16000, sr)
    return resample_poly(pcm, 16000 // g, sr // g).astype(np.float32)


def main() -> int:
    p = argparse.ArgumentParser()
    common.add_root(p)
    p.add_argument("--engines", default="", help="comma-separated engine folders (default: all under out/normal)")
    p.add_argument("--threads", type=int, default=4, help="CPU threads (keep the host usable)")
    p.add_argument("--model", default="whisper-medium", help="model folder under models/")
    args = p.parse_args()

    base = args.root / "out" / "normal"
    engines = args.engines.split(",") if args.engines else sorted(d.name for d in base.iterdir() if d.is_dir())
    model = WhisperModel(str(args.root / "models" / args.model), device="cpu", compute_type="int8",
                         cpu_threads=args.threads)
    for engine in engines:
        t0 = time.time()
        out = {}
        for wav in sorted((base / engine).glob("*.wav")):
            segments, _info = model.transcribe(load_16k(wav), language="fr", beam_size=5, temperature=0.0,
                                               condition_on_previous_text=False, word_timestamps=True,
                                               vad_filter=False)
            words, text = [], []
            for seg in segments:
                text.append(seg.text)
                for w in seg.words or []:
                    words.append({"w": w.word, "start": round(w.start, 3), "end": round(w.end, 3),
                                  "p": round(w.probability, 3)})
            out[wav.stem] = {"text": "".join(text).strip(), "words": words}
        (base / engine / "transcripts.json").write_text(json.dumps(out, ensure_ascii=False, indent=1),
                                                       encoding="utf-8")
        print(f"{engine}: {len(out)} files in {time.time() - t0:.0f} s", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
