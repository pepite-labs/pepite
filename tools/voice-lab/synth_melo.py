"""MeloTTS-French (myshell-ai, PyTorch on CPU): WAVs, latency, phonemes and word timings from the duration path.

Run with the melo venv (Python 3.10): ``python -I synth_melo.py [--profile 2core]``.

Harness notes:

- MeloTTS imports every language front-end at start (Japanese needs a 500 MB MeCab dictionary, Chinese / English /
  Korean / Spanish fetch their own BERT tokenizers). Only French is used here, so those modules are replaced by empty
  stubs before import; the French path (gruut + espeak-style phonemes, ``dbmdz/bert-base-french-europeana-cased``)
  is the unmodified upstream code.
- The synthesis loop is upstream's ``TTS.tts_to_file`` unrolled, so the first sentence's audio can be timed and the
  monotonic alignment (``attn``) kept: frames per phoneme -> word start / end.
- MeloTTS phonemises **each BERT word on its own** (``french.g2p``), which matters for liaisons.
"""

import os
import sys
import types
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))  # -I drops the script folder from sys.path

import common  # noqa: E402

STUBS = ["chinese", "japanese", "english", "chinese_mix", "korean", "spanish",
         "chinese_bert", "english_bert", "japanese_bert", "spanish_bert"]


def install_stubs() -> None:
    def unused(*_a, **_k):
        raise RuntimeError("language front-end stubbed out by the voice-lab harness")

    for name in STUBS:
        m = types.ModuleType(f"melo.text.{name}")
        m.text_normalize = m.g2p = m.get_bert_feature = unused
        sys.modules[m.__name__] = m


def main() -> None:
    args = common.parse_args(__doc__)
    threads = common.apply_profile(args.profile)
    os.environ.setdefault("HF_HOME", str(args.root / "cache" / "hf"))
    if (Path(os.environ["HF_HOME"]) / "hub" / "models--dbmdz--bert-base-french-europeana-cased").is_dir():
        os.environ["HF_HUB_OFFLINE"] = "1"  # cached: no update check against the Hub inside the timed load
    os.environ.setdefault("TOKENIZERS_PARALLELISM", "false")
    run = common.Run(args, "melo-fr")

    t0 = common.now_ms()
    install_stubs()
    import numpy as np
    import torch
    from melo import utils
    from melo.api import TTS
    from melo.text import cleaner

    if threads:
        torch.set_num_threads(threads)
    import_ms = common.now_ms() - t0
    run.rss_start = run.proc.memory_info().rss  # after the heavy imports: RAM below is the model's own

    t0 = common.now_ms()
    mdir = args.root / "models" / "melo"
    tts = TTS(language="FR", device="cpu", config_path=str(mdir / "config.json"), ckpt_path=str(mdir / "checkpoint.pth"))
    speaker = tts.hps.data.spk2id["FR"]
    sr = tts.hps.data.sampling_rate
    hop = tts.hps.data.hop_length

    def synth_piece(text: str):
        bert, ja_bert, phones, tones, lang_ids = utils.get_text_for_tts_infer(text, "FR", tts.hps, "cpu", tts.symbol_to_id)
        with torch.no_grad():
            o, attn, *_ = tts.model.infer(
                phones.unsqueeze(0), torch.LongTensor([phones.size(0)]), torch.LongTensor([speaker]),
                tones.unsqueeze(0), lang_ids.unsqueeze(0), bert.unsqueeze(0), ja_bert.unsqueeze(0),
                sdp_ratio=0.2, noise_scale=0.6, noise_scale_w=0.8, length_scale=1.0)
        frames = attn[0, 0].sum(0).cpu().numpy()  # frames per (blank-interspersed) phoneme id
        return o[0, 0].cpu().float().numpy(), frames

    synth_piece("Bonjour.")  # loads the French BERT (lazy in upstream) and warms up
    run.loaded(common.now_ms() - t0)
    synth_piece("Bonjour.")

    gap = int(sr * 0.05)  # upstream audio_numpy_concat adds 50 ms after every piece
    for item in run.items:
        for rep in range(run.reps_for(item)):
            t0 = common.now_ms()
            first = None
            parts = []
            for piece in TTS.split_sentences_into_pieces(item["text"], "FR", quiet=True):
                audio, frames = synth_piece(piece)
                if first is None:
                    first = common.now_ms() - t0
                parts.append((piece, audio, frames))
            total = common.now_ms() - t0
            wav = np.concatenate([np.concatenate([a, np.zeros(gap, np.float32)]) for _, a, _ in parts])
            run.add(item, rep, first, total, len(wav) / sr)
            if rep:
                continue
            common.write_wav(run.out / f"{item['id']}.wav", wav, sr)
            ph_all, words, spans, offset = [], [], [], 0.0
            for piece, audio, frames in parts:
                _norm, phones, _tones, word2ph = cleaner.clean_text(piece, "FR")
                # add_blank: phone k (pads included) is id 2k+1; the blanks between carry frames of their own
                t = np.concatenate([[0], np.cumsum(frames)]) * hop / sr + offset
                piece_ph = []
                for g, (a, b) in group_ranges(piece, word2ph):
                    if a == b:
                        continue
                    ph = "".join(phones[a:b])
                    piece_ph.append(ph)
                    s, e = float(t[2 * a + 1]), float(t[2 * (b - 1) + 2])
                    spans.append((ph, s, e))
                    spans.append((" ", e, e))
                    words.append({"tok": g, "ph": ph, "start": round(s, 3), "end": round(e, 3)})
                ph_all.append(" ".join(piece_ph))
                offset += (len(audio) + gap) / sr
            run.phonemes[item["id"]] = " | ".join(ph_all)
            run.timings[item["id"]] = {"words": merge_to_text_words(item["text"], words),
                                       "phonemes": [[p, round(s, 3), round(e, 3)] for p, s, e in spans]}
    bert_mb = hf_cache_mb(Path(os.environ["HF_HOME"]))
    run.finish({"sample_rate": sr, "threads": threads or "default", "model": "checkpoint.pth",
                "model_mb": common.mb((mdir / "checkpoint.pth").stat().st_size), "extra_mb": bert_mb,
                "import_ms": round(import_ms), "bert": "dbmdz/bert-base-french-europeana-cased"})


def group_ranges(text: str, word2ph: list[int]) -> list[tuple[str, tuple[int, int]]]:
    """The BERT word groups french.g2p phonemises one by one (WordPiece pieces joined), with the [start, end) range
    of their phones in g2p's list (index 0 is the start pad). word2ph has one entry per WordPiece token."""
    from melo.text import french

    groups: list[list[str]] = []
    for t in french.tokenizer.tokenize(french.text_normalize(text)):
        if t.startswith("#") and groups:
            groups[-1].append(t.replace("#", ""))
        else:
            groups.append([t])
    out, k, pos = [], 1, word2ph[0]
    for g in groups:
        n = sum(word2ph[k:k + len(g)])
        out.append(("".join(g), (pos, pos + n)))
        k += len(g)
        pos += n
    return out


def merge_to_text_words(text: str, groups: list[dict]) -> list[dict]:
    """Merge BERT groups (« l », « ' », « école ») into the app's words (« l'école »); punctuation groups are skipped."""
    out, gi = [], 0
    for tok in common.text_tokens(text):
        want = "".join(ch for ch in tok.lower() if ch.isalnum())
        got, start, end, ph = "", None, None, ""
        while gi < len(groups) and len(got) < len(want):
            g = groups[gi]
            gi += 1
            k = "".join(ch for ch in g["tok"].lower() if ch.isalnum())
            if not k:
                continue
            got += k
            start = g["start"] if start is None else start
            end, ph = g["end"], ph + g["ph"]
        if start is not None:
            out.append({"ph": ph, "start": start, "end": end})
    return out


def hf_cache_mb(hf_home: Path) -> float:
    total = sum(f.stat().st_size for f in hf_home.rglob("*") if f.is_file() and "bert-base-french" in str(f))
    return common.mb(total)


if __name__ == "__main__":
    main()
