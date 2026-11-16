"""Shared helpers of the plan 05 voice-lab harness (D20, D25): test items, CPU profile, timing, outputs.

Every engine script follows the same protocol so the numbers compare:

1. ``load_ms``: model load (process already started, imports done);
2. one untimed warm-up utterance (the app warms the voice up at start, plan 08 step 6);
3. each item is synthesised ``reps`` times (``--word-reps`` for the isolated words); per run
   ``first_audio_ms`` = time until the first playable audio chunk, ``total_ms`` = whole utterance,
   ``rtf`` = total / audio duration;
4. the first run of each item writes ``<id>.wav`` and its phonemes / word timings.

Profiles: ``normal`` (all cores, engine default threads) and ``2core`` (process affinity on two logical CPUs of two
different physical cores, engine threads = 2): the low-end stand-in of D25.
"""

import argparse
import csv
import json
import os
import re
import statistics
import time
import wave
from pathlib import Path

import numpy as np
import psutil

HERE = Path(__file__).resolve().parent
REPO_FREEWARE = HERE.parent.parent
DEFAULT_SENTENCES = REPO_FREEWARE / "development" / "voice-test" / "sentences.txt"
# Bulk-data folder (venvs, models, WAVs, outputs): never in the repository. From VOICE_LAB_ROOT or --root.
DEFAULT_ROOT = Path(os.environ["VOICE_LAB_ROOT"]) if os.environ.get("VOICE_LAB_ROOT") else None

# Logical CPUs 0 and 2 are on two different physical cores of a hyper-threaded host (0/1 share a core).
TWO_CORE_CPUS = [0, 2]


def load_items(path: Path = DEFAULT_SENTENCES) -> list[dict]:
    items = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        item_id, category, text = line.split("\t")
        items.append({"id": item_id, "category": category, "text": text})
    return items


def add_root(p: argparse.ArgumentParser) -> None:
    """--root, defaulting to VOICE_LAB_ROOT; required when that variable is not set."""
    p.add_argument("--root", type=Path, default=DEFAULT_ROOT, required=DEFAULT_ROOT is None,
                   help="voice-lab bulk-data folder (models/, out/); default: VOICE_LAB_ROOT")


def parse_args(engine_help: str, extra=None) -> argparse.Namespace:
    p = argparse.ArgumentParser(description=engine_help)
    add_root(p)
    p.add_argument("--sentences", type=Path, default=DEFAULT_SENTENCES)
    p.add_argument("--profile", choices=["normal", "2core"], default="normal")
    p.add_argument("--reps", type=int, default=3, help="timed runs per sentence")
    p.add_argument("--word-reps", type=int, default=10, help="timed runs per isolated word")
    p.add_argument("--only", default="", help="comma-separated item ids (debug)")
    if extra:
        extra(p)
    return p.parse_args()


def apply_profile(profile: str) -> int | None:
    """Pin the process for the 2-core profile; returns the thread count engines must use (None = default)."""
    proc = psutil.Process()
    if profile == "2core":
        proc.cpu_affinity(TWO_CORE_CPUS)
        os.environ["OMP_NUM_THREADS"] = "2"
        return 2
    return None


class Run:
    """Output folder, latency rows and memory figures of one engine/voice/profile run."""

    def __init__(self, args: argparse.Namespace, engine: str):
        self.args = args
        self.engine = engine
        self.out = args.root / "out" / args.profile / engine
        self.out.mkdir(parents=True, exist_ok=True)
        self.rows: list[dict] = []
        self.phonemes: dict[str, str] = {}
        self.timings: dict[str, list[dict]] = {}
        self.proc = psutil.Process()
        self.rss_start = self.proc.memory_info().rss
        self.load_ms = None
        self.rss_loaded = None
        self.items = load_items(args.sentences)
        if args.only:
            keep = set(args.only.split(","))
            self.items = [i for i in self.items if i["id"] in keep]

    def loaded(self, load_ms: float) -> None:
        self.load_ms = load_ms
        self.rss_loaded = self.proc.memory_info().rss

    def reps_for(self, item: dict) -> int:
        return self.args.word_reps if item["category"] == "word" else self.args.reps

    def add(self, item: dict, rep: int, first_ms: float, total_ms: float, audio_s: float) -> None:
        self.rows.append({
            "engine": self.engine, "profile": self.args.profile, "id": item["id"], "category": item["category"],
            "rep": rep, "load_ms": round(self.load_ms or 0, 1), "first_audio_ms": round(first_ms, 1),
            "total_ms": round(total_ms, 1), "audio_s": round(audio_s, 3),
            "rtf": round(total_ms / 1000 / audio_s, 4) if audio_s > 0 else "",
        })

    def finish(self, extra: dict | None = None) -> None:
        with open(self.out / "latency.csv", "w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=list(self.rows[0].keys()))
            w.writeheader()
            w.writerows(self.rows)
        if self.phonemes:
            dump_json(self.out / "phonemes.json", self.phonemes)
        if self.timings:
            dump_json(self.out / "timings.json", self.timings)
        mi = self.proc.memory_info()
        mem = {
            "rss_start_mb": mb(self.rss_start), "rss_loaded_mb": mb(self.rss_loaded or 0),
            "rss_end_mb": mb(mi.rss), "peak_wset_mb": mb(getattr(mi, "peak_wset", 0)),
            "load_ms": self.load_ms,
        }
        mem.update(extra or {})
        dump_json(self.out / "run.json", mem)
        print(summary(self.rows), json.dumps(mem))


def mb(n: int) -> float:
    return round(n / (1 << 20), 1)


def dump_json(path: Path, data) -> None:
    Path(path).write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")


def write_wav(path: Path, audio: np.ndarray, sample_rate: int) -> None:
    pcm = (np.clip(audio, -1.0, 1.0) * 32767).astype("<i2")
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(sample_rate)
        w.writeframes(pcm.tobytes())


def now_ms() -> float:
    return time.perf_counter() * 1000.0


def pct(values: list[float], q: float) -> float:
    """Nearest-rank percentile (q in 0..100)."""
    v = sorted(values)
    if not v:
        return float("nan")
    k = max(0, min(len(v) - 1, int(np.ceil(q / 100 * len(v))) - 1))
    return v[k]


def summary(rows: list[dict]) -> str:
    words = [r["first_audio_ms"] for r in rows if r["category"] == "word"]
    sents = [r["first_audio_ms"] for r in rows if r["category"] != "word"]
    rtf = [r["rtf"] for r in rows if r["category"] != "word" and r["rtf"] != ""]
    return (f"word first p50={pct(words, 50):.0f} p95={pct(words, 95):.0f} ms; "
            f"sentence first p50={pct(sents, 50):.0f} p95={pct(sents, 95):.0f} ms; "
            f"rtf median={statistics.median(rtf) if rtf else float('nan'):.3f}")


_TOKEN_STRIP = re.compile(r"^[^\w]+|[^\w]+$")


def text_tokens(text: str) -> list[str]:
    """Words as highlighted by the app: whitespace-separated, punctuation stripped; « l'école » and « grand-mère »
    stay one word (elision and hyphen), lone punctuation (« ! », « ? ») is dropped."""
    out = []
    for raw in text.split():
        tok = _TOKEN_STRIP.sub("", raw)
        if tok:
            out.append(tok)
    return out


def words_from_phoneme_spans(spans: list[tuple[str, float, float]]) -> list[dict]:
    """Group (phoneme, start_s, end_s) into words at spaces; punctuation-only groups are dropped."""
    words, cur = [], []
    for ph, s, e in spans:
        if ph == " ":
            if cur:
                words.append(cur)
            cur = []
        else:
            cur.append((ph, s, e))
    if cur:
        words.append(cur)
    out = []
    for w in words:
        sym = "".join(p for p, _, _ in w)
        core = [x for x in w if x[0] not in ".,;:!?¡¿—…\"«»" and x[0] not in ("^", "$", "_")]
        if not core:
            continue
        out.append({"ph": sym, "start": round(core[0][1], 3), "end": round(core[-1][2], 3)})
    return out
