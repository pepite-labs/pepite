"""Build the blind listening page (plan 05 step 5, D25) from the normal-profile WAVs.

``python -I make_blind_page.py [--root ...] [--engines a,b,...] [--items L01,...] [--aspect key|Title|help]`` writes
``<root>/blind/index.html`` and ``<root>/blind/audio/<code>.wav``: every clip gets a random code, so neither the file
name nor the order (shuffled per sentence) tells the engine; the code -> engine key is embedded base64-encoded and only
decoded by the page's export / reveal. Every copy is resampled to 22.05 kHz (the engines' native rates would tell
them apart) and levelled to the same loudness (RMS -20 dBFS, peak <= -0.2 dBFS), so a louder engine does not win on
volume. Open ``index.html`` in a browser (local file, no server needed). The second scale is « Liaisons » on the
sentences with a liaison check; ``--aspect`` replaces it with another scale on every item (e.g. the owner's isolated-word
sheet of 0.6.0: ``articulation|Articulation|1 = ... ; 5 = ...``).

Regenerating draws new codes and a new run id: ratings already given on the old page (kept in that browser) no
longer apply, so do not regenerate once the owner has started rating. Only ``blind/index.html`` and ``blind/audio/``
are replaced; exported results belong in ``<root>/blind-results/`` and are never touched.
"""

import argparse
import base64
import json
import secrets
import shutil
import sys
import wave
from datetime import datetime, timezone
from math import gcd
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # -I drops the script folder from sys.path

import numpy as np  # noqa: E402
from scipy.signal import resample_poly  # noqa: E402

import common  # noqa: E402
import liaisons  # noqa: E402

DEFAULT_ENGINES = "sapi-hortense,piper-siwis,piper-tom,piper-upmc-jessica,piper-upmc-pierre,kokoro-fp32,melo-fr"
DEFAULT_ITEMS = "L01,L02,L03,L04,L05,L06,L07,L08,L09,L10,L11,L12,S03,S04,S13,N01"


RATE = 22050


def copy_levelled(src: Path, dst: Path, rms_db: float = -20.0, peak: float = 0.977) -> None:
    """Copy a 16-bit mono WAV at ``RATE``, scaled to the target RMS (and never above ``peak``)."""
    with wave.open(str(src), "rb") as w:
        sr = w.getframerate()
        x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float64) / 32768.0
    if sr != RATE:
        g = gcd(RATE, sr)
        x = resample_poly(x, RATE // g, sr // g)
    rms = float(np.sqrt(np.mean(x * x))) if x.size else 0.0
    if rms > 0:
        x = x * min(10 ** (rms_db / 20) / rms, peak / float(np.max(np.abs(x))))
    with wave.open(str(dst), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes((np.clip(x, -1, 1) * 32767).astype("<i2").tobytes())


def main() -> int:
    p = argparse.ArgumentParser()
    common.add_root(p)
    p.add_argument("--sentences", type=Path, default=common.DEFAULT_SENTENCES)
    p.add_argument("--engines", default=DEFAULT_ENGINES)
    p.add_argument("--items", default=DEFAULT_ITEMS)
    p.add_argument("--aspect", help="second scale on every item instead of the liaisons: key|Title|help text")
    args = p.parse_args()

    src = args.root / "out" / "normal"
    engines = [e for e in args.engines.split(",") if (src / e).is_dir()]
    texts = {i["id"]: i["text"] for i in common.load_items(args.sentences)}
    out = args.root / "blind"
    # Replace only what this script writes: anything else in blind/ (e.g. a results JSON saved there) stays.
    if (out / "audio").exists():
        shutil.rmtree(out / "audio")
    (out / "index.html").unlink(missing_ok=True)
    (out / "audio").mkdir(parents=True)
    (args.root / "blind-results").mkdir(exist_ok=True)

    rng = secrets.SystemRandom()
    key, items = {}, []
    for item_id in args.items.split(","):
        clips = []
        for e in engines:
            wav = src / e / f"{item_id}.wav"
            if not wav.exists():
                continue
            code = secrets.token_hex(4)
            copy_levelled(wav, out / "audio" / f"{code}.wav")
            key[code] = e
            clips.append({"code": code})
        rng.shuffle(clips)
        pairs = [c[1] + (" (sans liaison)" if c[2] == "forbidden" else "")
                 for c in liaisons.CHECKS if c[0] == item_id and c[2] != "facultative"]
        items.append({"id": item_id, "text": texts[item_id], "pairs": pairs, "clips": clips})

    run = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
    data = {"run": run, "generated": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "engines": len(engines), "items": items,
            "key": base64.b64encode(json.dumps(key).encode("ascii")).decode("ascii")}
    if args.aspect:
        a_key, a_title, a_help = args.aspect.split("|", 2)
        data["aspect"] = {"key": a_key, "title": a_title, "help": a_help}
    template = (Path(__file__).resolve().parent / "blind_test_template.html").read_text(encoding="utf-8")
    page = template.replace("/*__DATA__*/", json.dumps(data, ensure_ascii=False).replace("</", "<\\/"))
    (out / "index.html").write_text(page, encoding="utf-8")
    print(f"{out / 'index.html'}: {len(items)} sentences x {len(engines)} voices = {len(key)} clips")
    return 0


if __name__ == "__main__":
    sys.exit(main())
