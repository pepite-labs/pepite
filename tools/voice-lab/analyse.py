"""Aggregate the plan 05 runs into the tables of development/voice-evaluation.md (D25 objective measures).

Run with the whisper venv (numpy, jiwer): ``python -I analyse.py [--root ...]``. Reads, per engine,
``out/<profile>/<engine>/{latency.csv, run.json}`` and ``out/normal/<engine>/{phonemes,timings,transcripts}.json``;
writes ``out/results.md`` and ``out/results.json``.

Measures:

- latency: ``first_audio_ms`` p50 / p95 for the isolated words (word echo) and the sentences, ``total_ms`` and RTF of
  the sentences, ``load_ms``; for both profiles;
- intelligibility: WER / CER of the Whisper transcript against the test text, number items excluded (Whisper writes
  digits, so they are listed apart); plus PER, the phoneme error rate after both texts go through espeak-ng, which
  forgives homophones that no listener could tell apart either (« leurs devoirs » / « leur devoir », « allé » /
  « allée »);
- liaisons: ``liaisons.check`` on the engine's phonemes;
- word timing, on the words both sides could be matched (engine words mapped 1:1 onto the text's words, then onto
  Whisper's words):
  - *heard = highlighted* (plan 05 threshold): the middle of the word as Whisper hears it falls inside the span the
    app would highlight for that word (from its engine start to the next word's engine start);
  - *start within ±100 ms* (plan 07 lag criterion): ``|engine.start - whisper.start| <= 100 ms``;
- footprint: model files on disk, RAM added by loading, peak working set.
"""

import argparse
import csv
import difflib
import json
import re
import statistics
import sys
import unicodedata
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # -I drops the script folder from sys.path

import jiwer  # noqa: E402

import common  # noqa: E402
import liaisons  # noqa: E402

ORDER = ["sapi-hortense", "piper-siwis", "piper-tom", "piper-upmc-jessica", "piper-upmc-pierre",
         "kokoro-fp32", "kokoro-int8", "melo-fr"]
LAG_S = 0.10  # a highlight may start at most 100 ms before or after the word is heard (plan 07 lag criterion)


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8")) if path.exists() else None


def norm_text(s: str) -> str:
    s = unicodedata.normalize("NFC", s.lower()).replace("’", "'")
    s = re.sub(r"['\-]", " ", s)
    s = re.sub(r"[^\w\s]", " ", s)
    return " ".join(s.split())


def key(word: str) -> str:
    return re.sub(r"[^\w]", "", unicodedata.normalize("NFC", word.lower()))


def merge_whisper_words(words: list[dict]) -> list[dict]:
    """faster-whisper splits « l'école » into « l » + « 'école »: a piece without a leading space joins the previous."""
    out = []
    for w in words:
        if out and not w["w"].startswith(" "):
            out[-1] = {"w": out[-1]["w"] + w["w"], "start": out[-1]["start"], "end": w["end"]}
        else:
            out.append(dict(w))
    return out


_ESPEAK = None


def phonemize(texts: list[str]) -> list[str]:
    """French IPA without stress or spaces (espeak-ng 1.52 via phonemizer + espeakng-loader)."""
    global _ESPEAK
    if _ESPEAK is None:
        import espeakng_loader
        from phonemizer.backend import EspeakBackend
        from phonemizer.backend.espeak.wrapper import EspeakWrapper

        EspeakWrapper.set_library(espeakng_loader.get_library_path())
        EspeakWrapper.set_data_path(espeakng_loader.get_data_path())
        _ESPEAK = EspeakBackend("fr-fr", preserve_punctuation=False, with_stress=False)
    return ["".join(p.split()) or "_" for p in _ESPEAK.phonemize(texts)]


def latency_stats(rows: list[dict]) -> dict:
    def f(cat_word: bool, col: str) -> list[float]:
        return [float(r[col]) for r in rows if (r["category"] == "word") == cat_word and r[col] not in ("", "NaN")]

    w, s, t, rtf = f(True, "first_audio_ms"), f(False, "first_audio_ms"), f(False, "total_ms"), f(False, "rtf")
    return {"word_p50": common.pct(w, 50), "word_p95": common.pct(w, 95),
            "sent_first_p50": common.pct(s, 50), "sent_first_p95": common.pct(s, 95),
            "sent_total_p50": common.pct(t, 50), "sent_total_p95": common.pct(t, 95),
            "rtf_p50": common.pct(rtf, 50), "rtf_p95": common.pct(rtf, 95), "n_word": len(w), "n_sent": len(s)}


def read_rows(path: Path) -> list[dict]:
    with open(path, encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))


def intelligibility(items: list[dict], tr: dict) -> dict:
    refs, hyps, per_cat, wrong = [], [], {}, []
    for it in items:
        if it["category"] == "number" or it["id"] not in tr:
            continue
        r, h = norm_text(it["text"]), norm_text(tr[it["id"]]["text"])
        refs.append(r)
        hyps.append(h if h else "<vide>")
        per_cat.setdefault(it["category"], ([], []))
        per_cat[it["category"]][0].append(r)
        per_cat[it["category"]][1].append(hyps[-1])
        if r != h:
            wrong.append({"id": it["id"], "ref": it["text"], "hyp": tr[it["id"]]["text"]})
    if not refs:
        return {}
    per = jiwer.cer(phonemize(refs), phonemize([h if h != "<vide>" else "" for h in hyps]))
    return {"wer": jiwer.wer(refs, hyps), "cer": jiwer.cer(refs, hyps), "per": per, "n": len(refs),
            "exact": len(refs) - len(wrong),
            "per_cat": {c: jiwer.wer(r, h) for c, (r, h) in per_cat.items()}, "wrong": wrong,
            "numbers": {it["id"]: tr[it["id"]]["text"] for it in items if it["category"] == "number" and it["id"] in tr}}


def word_timing(items: list[dict], tim: dict, tr: dict) -> dict:
    mapped_items, unmapped, hits, heard, total, deltas = 0, [], 0, 0, 0, []
    heard_inner, total_inner = 0, 0  # without each item's first word: Whisper snaps it to 0 s, before the silence
    for it in items:
        if it["id"] not in tim or it["id"] not in tr:
            continue
        toks = common.text_tokens(it["text"])
        ew = tim[it["id"]]["words"]
        if len(ew) != len(toks):
            unmapped.append(f"{it['id']} ({len(ew)} engine words / {len(toks)} text words)")
            continue
        mapped_items += 1
        ww = merge_whisper_words(tr[it["id"]]["words"])
        sm = difflib.SequenceMatcher(a=[key(t) for t in toks], b=[key(w["w"]) for w in ww], autojunk=False)
        for blk in sm.get_matching_blocks():
            for k in range(blk.size):
                i = blk.a + k
                e, w = ew[i], ww[blk.b + k]
                span_end = ew[i + 1]["start"] if i + 1 < len(ew) else max(e.get("end") or e["start"], w["end"]) + 0.3
                total += 1
                hits += abs(e["start"] - w["start"]) <= LAG_S
                ok = e["start"] <= (w["start"] + w["end"]) / 2 < span_end
                heard += ok
                if i:
                    heard_inner += ok
                    total_inner += 1
                deltas.append(abs(e["start"] - w["start"]) * 1000)
    return {"items_mapped": mapped_items, "unmapped": unmapped, "words_compared": total,
            "hit_rate": hits / total if total else None, "heard_rate": heard / total if total else None,
            "heard_rate_inner": heard_inner / total_inner if total_inner else None,
            "median_abs_delta_ms": statistics.median(deltas) if deltas else None}


def main() -> int:
    p = argparse.ArgumentParser()
    common.add_root(p)
    p.add_argument("--sentences", type=Path, default=common.DEFAULT_SENTENCES)
    args = p.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")  # the tables hold IPA and « »
    items = common.load_items(args.sentences)
    base = args.root / "out"
    found = {d.name for d in (base / "normal").iterdir() if d.is_dir()}
    engines = [e for e in ORDER if e in found] + sorted(found - set(ORDER))

    res = {}
    for e in engines:
        n = base / "normal" / e
        r = {"run": load_json(n / "run.json"), "lat": {}}
        for prof in ("normal", "2core"):
            f = base / prof / e / "latency.csv"
            if f.exists():
                r["lat"][prof] = latency_stats(read_rows(f))
                r.setdefault("run_" + prof, load_json(base / prof / e / "run.json"))
        ph, tim, tr = load_json(n / "phonemes.json"), load_json(n / "timings.json"), load_json(n / "transcripts.json")
        if ph:
            timed = {k: v["phonemes"] for k, v in (tim or {}).items() if v.get("phonemes")}
            r["liaisons"] = liaisons.check(ph, timed)
            r["liaison_score"] = liaisons.score(r["liaisons"])
        if tr:
            r["intel"] = intelligibility(items, tr)
        if tim and tr:
            r["timing"] = word_timing(items, tim, tr)
        res[e] = r
    (base / "results.json").write_text(json.dumps(res, ensure_ascii=False, indent=1, default=str), encoding="utf-8")
    (base / "results.md").write_text(render(res), encoding="utf-8")
    print(render(res))
    return 0


def fmt(v, nd=0):
    if v is None or (isinstance(v, float) and v != v):
        return "–"
    return f"{v:.{nd}f}"


def render(res: dict) -> str:
    L = ["## Latency (ms; first audio = first playable chunk)", "",
         "| Engine | Profile | Load | Word first p50 / p95 | Sentence first p50 / p95 | Sentence total p50 / p95 | RTF p50 / p95 |",
         "|---|---|---:|---:|---:|---:|---:|"]
    for e, r in res.items():
        for prof, s in r["lat"].items():
            run = r.get("run_" + prof) or {}
            L.append(f"| {e} | {prof} | {fmt(run.get('load_ms'))} | {fmt(s['word_p50'])} / {fmt(s['word_p95'])} | "
                     f"{fmt(s['sent_first_p50'])} / {fmt(s['sent_first_p95'])} | {fmt(s['sent_total_p50'])} / "
                     f"{fmt(s['sent_total_p95'])} | {fmt(s['rtf_p50'], 3)} / {fmt(s['rtf_p95'], 3)} |")
    L += ["", "## Intelligibility (Whisper medium; number items excluded)", "",
          "| Engine | WER | CER | PER (phonemes) | Exact items | WER liaison / school / homophone / word |",
          "|---|---:|---:|---:|---:|---|"]
    for e, r in res.items():
        i = r.get("intel")
        if i:
            pc = i["per_cat"]
            L.append(f"| {e} | {i['wer']*100:.1f} % | {i['cer']*100:.1f} % | {i['per']*100:.1f} % | {i['exact']}/{i['n']} | "
                     + " / ".join(f"{pc.get(c, float('nan'))*100:.0f} %" for c in ("liaison", "school", "homophone", "word"))
                     + " |")
    L += ["", "## Liaisons (from the synthesised phonemes)", "",
          "| Engine | TODO list | All expected | Forbidden respected | Facultative made | Missed / wrong |",
          "|---|---:|---:|---:|---:|---|"]
    for e, r in res.items():
        s = r.get("liaison_score")
        if s:
            L.append(f"| {e} | {s['todo'][0]}/{s['todo'][1]} | {s['expected'][0]}/{s['expected'][1]} | "
                     f"{s['forbidden_respected'][0]}/{s['forbidden_respected'][1]} | {s['facultative'][0]}/{s['facultative'][1]} | "
                     f"{', '.join(s['missed'] + ['WRONG ' + w for w in s['wrong']]) or '–'} |")
    names = [e for e in res if res[e].get("liaisons")]
    if names:
        L += ["", "Detail (✓ made, with the consonant's duration in ms when the engine gives phoneme timings; "
              "✗ not made; ? other pronunciation):", "",
              "| Item | Pair | Kind | " + " | ".join(names) + " |", "|---|---|---|" + "---|" * len(names)]
        for k, c in enumerate(liaisons.CHECKS):
            cells = []
            for e in names:
                x = res[e]["liaisons"][k]
                sym = {"made": "✓", "not made": "✗", "n/a": "?", "missing": "–"}[x["status"]]
                cells.append(sym + (f" {x['dur_ms']:.0f}" if x["dur_ms"] else ""))
            L.append(f"| {c[0]} | {c[1]} | {c[2]} | " + " | ".join(cells) + " |")
    L += ["", "## Word timing vs Whisper word times", "",
          "| Engine | Items mapped 1:1 | Words compared | Heard = highlighted | idem, first words excluded | Start within ±100 ms | Median abs. start delta | Not mapped |",
          "|---|---:|---:|---:|---:|---:|---:|---|"]
    for e, r in res.items():
        t = r.get("timing")
        if t:
            L.append(f"| {e} | {t['items_mapped']} | {t['words_compared']} | "
                     f"{fmt(t['heard_rate'] * 100 if t['heard_rate'] is not None else None, 1)} % | "
                     f"{fmt(t['heard_rate_inner'] * 100 if t['heard_rate_inner'] is not None else None, 1)} % | "
                     f"{fmt(t['hit_rate'] * 100 if t['hit_rate'] is not None else None, 1)} % | "
                     f"{fmt(t['median_abs_delta_ms'])} ms | {'; '.join(t['unmapped']) or '–'} |")
    L += ["", "## Footprint", "", "| Engine | Model on disk (MB) | RAM added by load (MB) | Peak working set (MB) |",
          "|---|---:|---:|---:|"]
    for e, r in res.items():
        run = r.get("run") or {}
        disk = (run.get("model_mb") or 0) + (run.get("voices_mb") or 0) + (run.get("extra_mb") or 0)
        L.append(f"| {e} | {fmt(disk, 1)} | {fmt((run.get('rss_loaded_mb') or 0) - (run.get('rss_start_mb') or 0), 0)} | "
                 f"{fmt(run.get('peak_wset_mb'), 0)} |")
    L += ["", "## Number items (Whisper transcript)", "", "| Engine | " + " | ".join(("N01", "N02", "N03")) + " |",
          "|---|---|---|---|"]
    for e, r in res.items():
        i = r.get("intel")
        if i:
            L.append(f"| {e} | " + " | ".join(i["numbers"].get(k, "–") for k in ("N01", "N02", "N03")) + " |")
    return "\n".join(L) + "\n"


if __name__ == "__main__":
    sys.exit(main())
