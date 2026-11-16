# Voice evaluation — neural French voices (plan 05, D20, D25)

Run 2026-11-10 on the dev PC, **without the owner** (D25). The recommendation is **provisional**: it rests on
objective measures only. **Update 2026-11-11:** the owner's blind listening (§ 9b) confirms siwis and **replaces tom
by upmc-pierre** as the male voice; D20 changed accordingly.

Plan 05. Harness: [../tools/voice-lab/](../tools/voice-lab/).
Test text: [voice-test/sentences.txt](voice-test/sentences.txt). Raw outputs (WAVs, CSV, JSON, page):
`<work>\voice-lab\blind-results\` (not in the repository).

## 1. Summary

- **Final choice (blind listening 2026-11-11, § 9b): Piper `siwis` (female, default) + `upmc` speaker `pierre`
  (male).** The provisional recommendation (siwis + tom) was changed for the male voice after the owner's
  blind listening. Piper is the only neural engine that meets every latency threshold, even on 2 cores, with the
  smallest footprint; siwis is also the most intelligible engine of the test (on a par with Hortense).
- **Liaisons are not a voice problem.** On whole sentences Hortense, Piper and Kokoro all make the six liaisons of the
  TODO list (Hortense 17/19 expected, Piper and Kokoro 18/19, all three respect the 4 forbidden ones). The
  owner's « no liaison » comes from speaking **word by word** (TODO causes 1–2): plan 08 step 7 (speak
  « déterminant + nom » together) is the fix, whatever the voice.
- **Kokoro `ff_siwis`** is as intelligible as Piper, with word timings as good as the best Piper voice, but ~10× slower
  (word p95 547–674 ms, RTF ~0.5) and 5× bigger; **int8 is 6–11× slower than fp32** on this CPU. Not for the word
  echo.
- **MeloTTS-French is rejected**: its front-end phonemises each word on its own, so it makes 2/19 liaisons and
  pronounces latent consonants (« ils ont » /ilz ɔ̃t/, « arrivent » /aʁivt/); slower than real time on 2 cores,
  1.4 GB RAM.
- **Numbers and units need a text normaliser** before any espeak-based voice: « 8 h 30 » is read « huit H
  trente », « 15,50 euros » « quinze virgule cinquante euros »; « 9 ans » misses the /v/ of « neuf‿ans » in every engine.
- **Word timing**: Piper's alignment output sums exactly to the audio and its word starts sit within ~50 ms
  (median) of Whisper's; the plan's 95 % « heard = highlighted » threshold is reached by tom (96.9 %) but not by
  siwis (89.2 %) against a reference (Whisper) that is itself ±50–100 ms on short words — **not conclusive; to be
  checked by eye in plan 07/08**. SAPI word events are only right at the voice's native 16 kHz format.

## 2. Set-up

- **Host**: Intel Core i7-8750H (6 cores / 12 threads, 2.2 GHz, turbo 4.1 GHz), 32 GB RAM, Windows 11 Pro
  10.0.26200, no GPU used.
- **Profiles**:
  - `normal`: all cores, each engine's default thread count;
  - `2core`: process affinity on logical CPUs 0 and 2 (two different physical cores) and engine threads = 2.
    This is the D25 stand-in for a low-end PC. It limits parallelism, **not per-core speed**: a 4.1 GHz Coffee Lake
    core is about 2–2.5× faster than the core of a Celeron-class school laptop (estimate, not measured). Scale the
    `2core` figures by ~2.5 for a worst case.
- **Protocol** (same for every engine, [common.py](../tools/voice-lab/common.py)):
  1. `load_ms` = model load, in a process whose imports are done;
  2. one untimed warm-up utterance (plan 08 warms the voice up at start);
  3. each item is synthesised several times: sentences × 3 (`2core`: × 2), isolated words × 10; the engines
     slower than real time (Kokoro int8, MeloTTS) × 1 and × 5;
  4. `first_audio_ms` = time from the call to the first playable audio chunk; `total_ms`; `rtf` = total / audio
     duration. Piper and MeloTTS yield one chunk per sentence, Kokoro one per batch of ≤ 510 phonemes, SAPI streams
     (first PCM bytes). No audio device is involved: the times are synthesis only, for every engine.
- **Test text**: 40 items — 12 liaison sentences (the TODO list plus other obligatory, facultative and forbidden
  liaisons), 15 school-level sentences (questions, exclamations, elisions), 5 homophone sentences, 3 number/date
  sentences, 5 isolated words.
- **Liaisons** ([liaisons.py](../tools/voice-lab/liaisons.py)): 25 checks on the phonemes each engine actually
  synthesised (Piper / Kokoro / MeloTTS front-end output; SAPI `PhonemeReached` events mapped from the SAPI French
  phone set to IPA). When the engine reports phoneme timings, the duration of the liaison consonant in the audio is
  read too — it is never zero, so the consonant is really spoken, not only written in the phoneme string.
- **Intelligibility** ([transcribe.py](../tools/voice-lab/transcribe.py)): Whisper `medium` (faster-whisper,
  int8, CPU, beam 5, temperature 0, no prompt) transcribes every WAV; WER and CER against the test text (number
  items excluded: Whisper writes digits), and **PER**, the phoneme error rate after both texts go through
  espeak-ng — it forgives homophones that a listener cannot tell apart either (« leurs devoirs » / « leur
  devoir »). Whisper is a strong listener: it fixes many small slips, so the scores rank engines but are kinder
  than a child would be.
- **Word timing** ([analyse.py](../tools/voice-lab/analyse.py)): Piper from the alignment output
  (`piper.patch_voice_with_alignment`), Kokoro from the model's duration output, MeloTTS from its monotonic
  alignment, SAPI from `SpeakProgress`. Engine words are mapped 1:1 onto the text's words (items where the counts
  differ are listed, not compared), then matched to Whisper's words (Whisper word timestamps, 20 ms steps, DTW on
  cross-attention: ±50–100 ms on short words, and the first word of a clip is snapped to 0 s). Two scores:
  - **heard = highlighted** (plan 05 threshold): the middle of the word as Whisper hears it falls inside the span the
    app would highlight for that word (from its start to the next word's start); also given without each item's
    first word (Whisper's 0 s snap);
  - **start within ±100 ms** (plan 07 lag criterion): `|engine.start − whisper.start| ≤ 100 ms`. Whisper's own
    ±50–100 ms error counts against every engine here, so this score is a floor.
- **SAPI output format**: Hortense is recorded at 16 kHz, its native rate. System.Speech computes `SpeakProgress` /
  `PhonemeReached` positions in the voice's native format: at 22.05 kHz every event time came out ×1.378, at
  44.1 kHz ×2.756 (measured on L01 and S11).

## 3. Results

Generated by [analyse.py](../tools/voice-lab/analyse.py) (copy of `out\results.md`). Reproduce everything with
`powershell -NoProfile -ExecutionPolicy Bypass -File tools\voice-lab\run_all.ps1 -Root <work>\voice-lab`
(from the repository root, or set `VOICE_LAB_ROOT`; about
2 h on this host, one CPU-heavy process at a time).

### 3.1 Latency (ms; first audio = first playable chunk)

| Engine | Profile | Load | Word first p50 / p95 | Sentence first p50 / p95 | Sentence total p50 / p95 | RTF p50 / p95 |
|---|---|---:|---:|---:|---:|---:|
| sapi-hortense | normal | 123 | 1 / 1 | 2 / 5 | 4 / 7 | 0.002 / 0.002 |
| sapi-hortense | 2core | 109 | 1 / 1 | 2 / 5 | 4 / 8 | 0.002 / 0.003 |
| piper-siwis | normal | 2499 | 35 / 50 | 80 / 132 | 80 / 132 | 0.042 / 0.058 |
| piper-siwis | 2core | 2518 | 43 / 46 | 87 / 129 | 87 / 129 | 0.049 / 0.052 |
| piper-tom | normal | 3547 | 63 / 104 | 183 / 265 | 183 / 265 | 0.084 / 0.107 |
| piper-tom | 2core | 3512 | 68 / 91 | 194 / 289 | 194 / 289 | 0.092 / 0.105 |
| piper-upmc-jessica | normal | 3442 | 26 / 45 | 85 / 136 | 85 / 136 | 0.045 / 0.063 |
| piper-upmc-jessica | 2core | 3399 | 29 / 36 | 98 / 140 | 98 / 140 | 0.050 / 0.054 |
| piper-upmc-pierre | normal | 3236 | 30 / 37 | 87 / 147 | 87 / 147 | 0.048 / 0.076 |
| piper-upmc-pierre | 2core | 3308 | 32 / 40 | 91 / 154 | 91 / 154 | 0.051 / 0.059 |
| kokoro-fp32 | normal | 2282 | 452 / 674 | 829 / 1219 | 829 / 1219 | 0.430 / 0.607 |
| kokoro-fp32 | 2core | 2269 | 478 / 547 | 920 / 1323 | 920 / 1323 | 0.508 / 0.575 |
| kokoro-int8 | normal | 2077 | 3150 / 3723 | 5305 / 7788 | 5305 / 7788 | 3.025 / 3.193 |
| kokoro-int8 | 2core | 1940 | 5446 / 6114 | 10197 / 14808 | 10197 / 14808 | 5.653 / 6.003 |
| melo-fr | normal | 4119 | 396 / 994 | 1402 / 3432 | 1402 / 3432 | 0.627 / 1.349 |
| melo-fr | 2core | 5017 | 1519 / 1952 | 4991 / 7453 | 4991 / 7453 | 2.728 / 2.909 |

### 3.2 Intelligibility (Whisper medium; number items excluded)

| Engine | WER | CER | PER (phonemes) | Exact items | WER liaison / school / homophone / word |
|---|---:|---:|---:|---:|---|
| sapi-hortense | 4.6 % | 1.8 % | 0.6 % | 31/37 | 3 % / 0 % / 12 % / 80 % |
| piper-siwis | 3.7 % | 1.0 % | 0.4 % | 31/37 | 3 % / 2 % / 9 % / 20 % |
| piper-tom | 6.9 % | 2.9 % | 1.6 % | 30/37 | 11 % / 0 % / 12 % / 80 % |
| piper-upmc-jessica | 13.0 % | 5.9 % | 4.7 % | 23/37 | 14 % / 6 % / 18 % / 120 % |
| piper-upmc-pierre | 7.4 % | 3.6 % | 2.8 % | 25/37 | 6 % / 4 % / 12 % / 80 % |
| kokoro-fp32 | 5.1 % | 2.1 % | 1.0 % | 29/37 | 5 % / 3 % / 12 % / 20 % |
| kokoro-int8 | 6.9 % | 3.4 % | 3.2 % | 30/37 | 5 % / 2 % / 21 % / 60 % |
| melo-fr | 6.0 % | 2.1 % | 0.9 % | 28/37 | 11 % / 1 % / 12 % / 20 % |

### 3.3 Liaisons (from the synthesised phonemes)

| Engine | TODO list | All expected | Forbidden respected | Facultative made | Missed / wrong |
|---|---:|---:|---:|---:|---|
| sapi-hortense | 6/6 | 17/19 | 4/4 | 1/2 | bon exemple, neuf ans |
| piper-siwis | 6/6 | 18/19 | 4/4 | 2/2 | neuf ans |
| piper-tom | 6/6 | 18/19 | 4/4 | 2/2 | neuf ans |
| piper-upmc-jessica | 6/6 | 18/19 | 4/4 | 2/2 | neuf ans |
| piper-upmc-pierre | 6/6 | 18/19 | 4/4 | 2/2 | neuf ans |
| kokoro-fp32 | 6/6 | 18/19 | 4/4 | 2/2 | neuf ans |
| kokoro-int8 | 6/6 | 18/19 | 4/4 | 2/2 | neuf ans |
| melo-fr | 1/6 | 2/19 | 2/4 | 1/2 | les amis, ils ont, deux heures, petit ami, c'est un, bon exemple, nous avons, des idées, mon ami, trois enfants, grand homme, nous allons, les enfants, nous irons, on est, ils ont (gagné), neuf ans, WRONG ils (sont) |

Detail (✓ made, with the consonant's duration in ms when the engine gives phoneme timings; ✗ not made; ? other pronunciation):

| Item | Pair | Kind | sapi-hortense | piper-siwis | piper-tom | piper-upmc-jessica | piper-upmc-pierre | kokoro-fp32 | kokoro-int8 | melo-fr |
|---|---|---|---|---|---|---|---|---|---|---|
| L01 | les amis | todo | ✓ 75 | ✓ 46 | ✓ 104 | ✓ 34 | ✓ 58 | ✓ 50 | ✓ 50 | ✗ |
| L02 | un enfant | todo | ✓ 60 | ✓ 34 | ✓ 35 | ✓ 23 | ✓ 70 | ✓ 25 | ✓ 25 | ✓ 127 |
| L03 | ils ont | todo | ✓ 85 | ✓ 59 | ✓ 116 | ✓ 58 | ✓ 70 | ✓ 25 | ✓ 25 | ? |
| L04 | deux heures | todo | ✓ 110 | ✓ 81 | ✓ 87 | ✓ 23 | ✓ 46 | ✓ 50 | ✓ 50 | ✗ |
| L05 | petit ami | todo | ✓ 90 | ✓ 58 | ✓ 93 | ✓ 92 | ✓ 70 | ✓ 50 | ✓ 50 | ✗ |
| L06 | c'est un | todo | ✓ 80 | ✓ 58 | ✓ 111 | ✓ 47 | ✓ 93 | ✓ 50 | ✓ 50 | ? |
| L06 | bon exemple | obligatory | ✗ | ✓ 34 | ✓ 52 | ✓ 35 | ✓ 23 | ✓ 25 | ✓ 25 | ✗ |
| L07 | nous avons | obligatory | ✓ 70 | ✓ 47 | ✓ 75 | ✓ 70 | ✓ 58 | ✓ 50 | ✓ 50 | ✗ |
| L07 | des idées | obligatory | ✓ 85 | ✓ 58 | ✓ 47 | ✓ 70 | ✓ 35 | ✓ 50 | ✓ 50 | ✗ |
| L08 | mon ami | obligatory | ✓ 60 | ✓ 47 | ✓ 58 | ✓ 46 | ✓ 70 | ✓ 50 | ✓ 25 | ✗ |
| L09 | trois enfants | obligatory | ✓ 65 | ✓ 46 | ✓ 40 | ✓ 47 | ✓ 34 | ✓ 25 | ✓ 25 | ✗ |
| L10 | grand homme | obligatory | ✓ 120 | ✓ 81 | ✓ 87 | ✓ 70 | ✓ 46 | ✓ 50 | ✓ 50 | ✗ |
| S05 | aux élèves | obligatory | ✓ 80 | ✓ 70 | ✓ 69 | ✓ 59 | ✓ 70 | ✓ 50 | ✓ 50 | ✓ 174 |
| S07 | nous allons | obligatory | ✓ 70 | ✓ 59 | ✓ 110 | ✓ 70 | ✓ 70 | ✓ 50 | ✓ 50 | ✗ |
| S11 | les enfants | obligatory | ✓ 70 | ✓ 46 | ✓ 75 | ✓ 46 | ✓ 46 | ✓ 50 | ✓ 50 | ✗ |
| S15 | nous irons | obligatory | ✓ 85 | ✓ 58 | ✓ 69 | ✓ 47 | ✓ 47 | ✓ 50 | ✓ 50 | ✗ |
| H03 | on est | obligatory | ✓ 50 | ✓ 46 | ✓ 47 | ✓ 58 | ✓ 46 | ✓ 25 | ✓ 25 | ? |
| H03 | ils ont (gagné) | obligatory | ✓ 85 | ✓ 59 | ✓ 87 | ✓ 58 | ✓ 58 | ✓ 25 | ✓ 25 | ? |
| N03 | neuf ans | obligatory | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |
| H02 | sont allés | facultative | ✓ 60 | ✓ 59 | ✓ 87 | ✓ 58 | ✓ 47 | ✓ 50 | ✓ 50 | ✗ |
| S07 | allons à | facultative | ✗ | ✓ 23 | ✓ 46 | ✓ 47 | ✓ 23 | ✓ 25 | ✓ 25 | ✓ 314 |
| L11 | les héros | forbidden | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |
| L12 | (chat) et un | forbidden | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ? |
| S09 | les (mains) | forbidden | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ |
| H02 | ils (sont) | forbidden | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | ✓ 128 |

### 3.4 Word timing vs Whisper word times

| Engine | Items mapped 1:1 | Words compared | Heard = highlighted | idem, first words excluded | Start within ±100 ms | Median abs. start delta | Not mapped |
|---|---:|---:|---:|---:|---:|---:|---|
| sapi-hortense | 38 | 202 | 72.8 % | 66.9 % | 73.8 % | 90 ms | N01 (8 engine words / 6 text words); N02 (7 engine words / 5 text words) |
| piper-siwis | 38 | 203 | 86.7 % | 89.2 % | 74.9 % | 49 ms | N01 (8 engine words / 6 text words); N02 (7 engine words / 5 text words) |
| piper-tom | 38 | 195 | 94.9 % | 96.9 % | 79.0 % | 52 ms | N01 (8 engine words / 6 text words); N02 (7 engine words / 5 text words) |
| piper-upmc-jessica | 38 | 189 | 94.2 % | 92.9 % | 88.4 % | 46 ms | N01 (8 engine words / 6 text words); N02 (7 engine words / 5 text words) |
| piper-upmc-pierre | 38 | 197 | 93.4 % | 92.1 % | 86.8 % | 46 ms | N01 (8 engine words / 6 text words); N02 (7 engine words / 5 text words) |
| kokoro-fp32 | 38 | 204 | 93.6 % | 96.4 % | 82.8 % | 48 ms | N01 (8 engine words / 6 text words); N02 (7 engine words / 5 text words) |
| kokoro-int8 | 38 | 201 | 92.0 % | 94.5 % | 80.6 % | 49 ms | N01 (8 engine words / 6 text words); N02 (7 engine words / 5 text words) |
| melo-fr | 40 | 209 | 82.8 % | 80.6 % | 70.3 % | 65 ms | – |

### 3.5 Footprint

| Engine | Model on disk (MB) | RAM added by load (MB) | Peak working set (MB) |
|---|---:|---:|---:|
| sapi-hortense | 9.4 | 19 | 150 |
| piper-siwis | 60.3 | 85 | 242 |
| piper-tom | 60.6 | 91 | 396 |
| piper-upmc-jessica | 73.2 | 98 | 260 |
| piper-upmc-pierre | 73.2 | 98 | 276 |
| kokoro-fp32 | 337.3 | 338 | 535 |
| kokoro-int8 | 135.7 | 146 | 337 |
| melo-fr | 622.8 | 1372 | 2356 |

### 3.6 Number items (Whisper transcript)

| Engine | N01 | N02 | N03 |
|---|---|---|---|
| sapi-hortense | Nous sommes le 10 novembre 2026. | Le livre coûte 15,50 €. | Il est 8h30 et j'ai 9 ans. |
| piper-siwis | Nous sommes le 10 novembre 2026. | Le livre coûte 15,50 €. | Il est 8 âge 30 et g 9 ans. |
| piper-tom | Nous sommes le 10 novembre 2026. | Le livre coûte 15,50 euros. | Il est 8, H30 et G9 en. |
| piper-upmc-jessica | Nous sommes le 10 novembre 2026. | Le livre coûte 15,5 euros. | Il est 8H30 et j'ai 9 ans. |
| piper-upmc-pierre | Nous sommes le 10 novembre 2026. | Le livre coûte 15,5 euros. | Il est 8 âge, 30 et j'ai 9 ans. |
| kokoro-fp32 | Nous sommes le 10 novembre 2026. | Le livre coûte 15,50 €. | Il est 8 h 30 et g 9 ans. |
| kokoro-int8 | Nous sommes le 10 novembre 2026. | Le livre coûte 15,50 €. | Il est 8 h 30 et g 9 ans. |
| melo-fr | Nous sommes le 10 novembre 2026. | Le livre coûte 15,50 €. | Il est 8h30 et j'ai 9 ans. |

## 4. Findings

### 4.1 Latency

- **Piper** is fast enough everywhere: word echo p95 ≤ 104 ms, sentence first audio p95 ≤ 289 ms, RTF ≤ 0.11 in
  both profiles. **tom costs twice siwis** (44.1 kHz model vs 22.05 kHz): on a Celeron-class CPU (≈ ×2.5) its
  sentence p95 would be ≈ 720 ms, above the 500 ms target, while siwis would stay ≈ 320 ms.
- **2 cores barely change Piper** (the model is small; two threads are enough): per-core speed is what matters on a
  low-end PC, which this profile cannot show (§ 10).
- **Load time**: 2.5–3.7 s per Piper voice, for the ONNX Runtime session alone (also 3.3–4.8 s with graph
  optimisation off or basic: not an optimisation cost; cause not investigated). Kokoro 2 s, MeloTTS 4–5 s plus
  7 s of imports, Hortense 0.1 s.
- **Kokoro fp32**: 452 / 674 ms (word p50 / p95), RTF 0.43 — 10× Piper. **Kokoro int8** (kokoro-onnx export, ONNX
  Runtime 1.31.1) is **6–11× slower than fp32** on this CPU (RTF 3–5.7), not faster: quantisation does not help
  here.
- **MeloTTS**: RTF 0.63 on all cores, **2.7 on 2 cores** (slower than real time), word p95 1–2 s.
- **Hortense**: first audio in 1–5 ms (it streams), the latency reference.

### 4.2 Intelligibility

- **siwis ≈ Hortense** are the most intelligible (PER 0.4 % / 0.6 %), then MeloTTS 0.9 %, Kokoro fp32 1.0 %,
  tom 1.6 %, upmc-pierre 2.8 %, Kokoro int8 3.2 %, upmc-jessica 4.7 %.
- Most WER « errors » are shared by every engine and are homophones a listener cannot resolve either (« leurs
  devoirs » / « leur devoir », « allé » / « allée », « vers » / « verres », « content » / « contents »): PER removes
  them.
- Engine-specific errors: **tom** garbles L12 « Il a un chat et un chien » (« Il arracha et rachia »);
  **upmc-jessica** 14 items wrong (« quart table », « char »…); **MeloTTS** turns « arrivent » into « il arrive »
  (wrong /t/), « ami est » into « ami, il est »; **Kokoro**: kokoro-onnx 0.6.1 leaves espeak-ng's language-switch flags
  « (en)fˈʊtbɔːl(fr) » in the phoneme string and the letters of « en » / « fr » get spoken (Whisper: « ain football
  fait »); Piper strips these flags.
- The 5 isolated words are poorly transcribed for every engine (Whisper has no context: « école » → « Écoute »,
  « éléphant » → « éléphants »): not significant with n = 5.

### 4.3 Liaisons

- Piper, Kokoro (both espeak-ng 1.52 front-ends) and Hortense make **all six TODO liaisons**; Piper and Kokoro make
  18/19 obligatory ones, Hortense 17/19 (no « bon‿exemple », no facultative « allons‿à »). None of them makes a
  forbidden one (« les / héros », « et / un », and a latent consonant before a consonant: « les / mains »,
  « ils / sont »).
- The liaison consonant is really in the audio: its duration is 23–120 ms in Piper, Kokoro and Hortense, the engines
  that report phoneme timings (detail table, § 3.3).
- **« neuf‿ans » /nœvɑ̃/ is missed by every engine** (they say /nœf ɑ̃/), Hortense too. It was only tested as
  « 9 ans » (digits): the miss may be an artefact of number expansion (the digit is expanded on its own, then no
  liaison rule applies) rather than of the word « neuf »; not tested with the word spelled out.
- **MeloTTS** makes 2/19: it phonemises each BERT word alone with gruut, so no cross-word liaison is possible; its
  two « hits » are accidents of the isolated pronunciations (« un » is always /œ̃n/, even in « un chat »), and the
  same habit makes the forbidden « ils‿sont » /ilz sɔ̃t/.

### 4.4 Word timing

- **Word boundaries**: espeak-ng's words map 1:1 onto the app's words for all 38 non-number items, including
  elisions (« l'école » /lekɔl/, « qu'il », « j'ai ») and hyphenated words (« est-ce », « après-midi »,
  « grand-mère », « est-il »). The liaison consonant is attached to the **left** word (« le-z amˈi »): the
  highlight of « amis » starts after its /z/.
- **Numbers expand** into several espeak words (« 2026 » → 3, « 15,50 » → 3): N01 and N02 are not 1:1; plan 08
  must merge them into one span (its unit test list already has `2026`).
- **Piper alignments** (`/Ceil_output_0`, piper1-gpl 1.8.0): the per-phoneme sample counts sum exactly to the audio
  length on all 40 items. Word starts are within 46–52 ms (median absolute offset) of Whisper's; 75–88 % of
  them within ±100 ms. « Heard = highlighted »: tom
  96.9 %, upmc 92–93 %, siwis 89.2 % (first words excluded). siwis' misses are highlights that start 110–300 ms
  **early** on words beginning with a stop or fricative (the closure is counted in the word) and very short
  function words (« à », « a », « un », « et », 50–100 ms long) where Whisper's own error is as large as the word.
- **Kokoro** durations: 96.4 % heard = highlighted, 82.8 % within ±100 ms — on a par with tom (96.9 % / 79.0 %).
  **MeloTTS**: 80.6 %. **Hortense** (`SpeakProgress`): 66.9 %, median absolute offset ≈ 90 ms from Whisper's word
  starts — worth knowing for plan 07's SAPI highlight.

### 4.5 Footprint

All candidates are far below D20's 500 MB per voice: Piper 60–73 MB on disk and +85–98 MB RAM once loaded;
Kokoro fp32 337 MB / +338 MB; MeloTTS 623 MB (with its French BERT) / +1.4 GB.

### 4.6 Text normalisation

espeak-ng (Piper, Kokoro) and gruut (MeloTTS) read « 8 h 30 » as « huit H trente » (Whisper: « 8 âge 30 »,
« 8, H30 ») and « 15,50 euros » as « quinze virgule cinquante euros »; Hortense says « huit heures trente ».
Dates and plain numbers (« le 10 novembre 2026 ») are fine everywhere.

## 5. Acceptance thresholds (plan 05)

Figures of the `2core` profile (the low-end stand-in), model loaded and warmed up. ✓ met, ✗ not met, ~ not
conclusive.

| Threshold | Hortense | siwis | tom | upmc jessica | upmc pierre | Kokoro fp32 | Kokoro int8 | MeloTTS |
|---|---|---|---|---|---|---|---|---|
| Word echo: first audio p95 ≤ 300 ms | ✓ 1 | ✓ 46 | ✓ 91 | ✓ 36 | ✓ 40 | ✗ 547 | ✗ 6114 | ✗ 1952 |
| Sentence: first audio p95 ≤ 500 ms | ✓ 5 | ✓ 129 | ✓ 289 ¹ | ✓ 140 | ✓ 154 | ✗ 1323 | ✗ 14808 | ✗ 7453 |
| Sentence: RTF < 0.3 (p50) | ✓ 0.002 | ✓ 0.049 | ✓ 0.092 | ✓ 0.050 | ✓ 0.051 | ✗ 0.51 | ✗ 5.65 | ✗ 2.73 |
| Word timings: heard = highlighted ≥ 95 % ² | ✗ 66.9 % | ~ 89.2 % | ✓ 96.9 % | ~ 92.9 % | ~ 92.1 % | ✓ 96.4 % | ~ 94.5 % | ✗ 80.6 % |
| Quality: owner's ranking beats Hortense | — | pending § 9 | pending | pending | pending | pending | — | pending |

¹ ≈ 720 ms expected on a Celeron-class CPU (×2.5, estimate). ² Against Whisper word times, first word of each item
excluded; the reference is ±50–100 ms on short words, so ~ means « below 95 % against a coarse reference »,
not « wrong words ». The plan's spot check of 10 sentences by eye was not possible without the owner.

## 6. Provisional recommendation

> Superseded for the male voice by the blind listening (§ 9b): **upmc-pierre replaces tom**. Kept below as the
> pre-listening reasoning.

**Keep D20 as it is — Piper `fr_FR-siwis-medium` (female, default) + `fr_FR-tom-medium` (male), SAPI as fallback —
provisionally, until the owner's blind listening (§ 9).**

Reasoning, on the objective measures only:

1. **siwis** wins on latency, footprint and intelligibility (word timing not conclusive: 89.2 %, § 5): most
   intelligible (PER 0.4 %, like Hortense), fastest neural voice
   (word p95 46 ms, sentence p95 129 ms on 2 cores), smallest (60 MB, +85 MB RAM), 18/19 liaisons, alignment
   output that sums exactly to the audio. Its licence (SIWIS data, CC BY 4.0) is GPL-3-compatible.
2. **tom** stays the male voice, with two reservations to check in the blind test: intelligibility a notch below
   siwis (PER 1.6 %, one garbled sentence) and twice the CPU cost (44.1 kHz). The only other male candidate,
   **upmc-pierre**, is faster but less intelligible (PER 2.8 %) and CC BY-SA; it is the fallback if the owner
   rejects tom. **upmc-jessica** is the least intelligible voice tested (PER 4.7 %): not recommended.
3. **Kokoro `ff_siwis`** is not a replacement: as intelligible as Piper with timings as good as tom's, but ~10× slower
   (fails the word-echo and sentence thresholds even on all 6 cores), 5× larger, and the same espeak-ng front-end,
   so no gain on liaisons. Reconsider only if the blind test finds it clearly more natural, and then only for
   reading whole sentences on fast PCs. Never the int8 export.
4. **MeloTTS** is rejected: no cross-word liaisons by design, wrong latent consonants, slower than real time on 2
   cores, 1.4 GB RAM, PyTorch-only (no ONNX path for the .NET app).
5. **Hortense** is not the cause of the missing liaisons (6/6 on sentences); whether the neural voices sound more
   natural than Hortense is exactly what the blind test must answer.

What would change the recommendation: the owner ranks tom below upmc-pierre (→ swap the male voice), ranks Kokoro
far above Piper (→ a sentence-only Kokoro option, still not for the echo), or ranks Hortense above both Piper
voices (→ neural voices become optional, plan 08 shrinks).

## 7. Impact on plan 08

1. **Voices**: siwis + upmc (speaker pierre, `speaker_id_map["pierre"]`; the 73 MB model also holds jessica) —
   decided by the blind listening (§ 9b). Ship only the voice files plus the alignment-patched `.onnx`
   (the patch only marks one more graph output, `/Ceil_output_0`: +18 bytes).
2. **Path A (libpiper) vs B (ONNX Runtime + espeak-ng)**: the alignments are a **model output**, not a library
   feature: any runtime that runs the patched model gets samples per phoneme id. Path B therefore gets alignments
   for free; path A still has to prove that the libpiper C API passes them through (not verified here: no native
   build in this iteration). Keep the plan's rule (A if verified, else B); B's text → ids step is piper1-gpl's
   `PiperVoice.phonemize` + `phonemes_to_ids` (GPL-3, portable to C#), including the removal of espeak's
   language-switch flags `(en)…(fr)` that kokoro-onnx forgets (§ 4.2).
3. **fp32 vs int8**: stay fp32. Piper medium fp32 is already 20× faster than real time; the only int8 model
   measured (Kokoro) was 6–11× *slower* with ONNX Runtime 1.31.1 on CPU.
4. **Warm-up** (step 6) is needed: 2.5–3.7 s to create the session. Load only the selected voice, on the speech
   thread, at start; the first word must not wait for it (SAPI until ready).
5. **Liaison grouping** (step 7) is the real fix for the TODO: every engine makes the liaisons inside one
   utterance, none across two calls.
6. **Text normaliser** (new, small): before espeak-ng, expand « 8 h 30 » → « 8 heures 30 », « 15,50 € /
   euros » → « 15 euros 50 », and similar unit abbreviations; Hortense does it itself, espeak-ng does not.
7. **Word spans** (step 4): espeak words = app words except numbers; merge the phoneme words of one number into one
   span; the liaison consonant belongs to the left word.
8. **Latency on real low-end PCs**: tom may exceed 500 ms to the first audio on long sentences on a Celeron-class
   CPU (estimate). Measure N3 on such a PC if one is available; if needed, split long sentences at commas
   (Piper already yields one chunk per sentence).

## 8. Models and tools (for THIRD-PARTY-NOTICES)

Only Piper and its voices are candidates for shipping; the other rows are the measuring tools and the challengers.
The model files listed in [models.json](../tools/voice-lab/models.json) are fetched by
[fetch_models.py](../tools/voice-lab/fetch_models.py) from pinned URLs and **enforced**: a file whose SHA-256 differs
is rejected. The other hashes are **recorded only**, not checked by any script: the binaries inside pip wheels
(`espeakbridge.pyd`, `espeak-ng.dll`), the alignment-patched `.onnx` files made locally, and the dbmdz BERT that
MeloTTS downloads implicitly from the Hugging Face Hub.

Licence compatibility: the tom voice data is AGPL-3.0; D19 accepts it because GPL-3 §13 allows combining with
AGPL-3 work (the AGPL terms then apply to that part).

| Component | Version / revision | Licence | File | SHA-256 |
|---|---|---|---|---|
| piper-tts (piper1-gpl) | 1.8.0 (PyPI) | GPL-3.0-or-later | wheel; bundles espeak-ng **1.52.0.1** (`espeakbridge.pyd`) | `29631a68cf69727df76071561c9d839dfd05ae326e7f4ed01ae4a69d03f63429` (`espeakbridge.pyd`) |
| Piper voice siwis medium | rhasspy/piper-voices @ `cf910fb5` | model card: dataset SIWIS **CC BY 4.0**; fine-tuned from the Piper `hifi_tts_92` base model, trained on Hi-Fi TTS (OpenSLR 109, **CC BY 4.0**; web, 2026-11-10) | `fr_FR-siwis-medium.onnx` | `b1d946fd68d0347155fa59bce0a018924abbef3c1561181db45ca463d83963ef` |
|  |  |  | `fr_FR-siwis-medium.onnx.json` | `df814aa3dfedd00510a07144442cc0a220ff21e43d03c3b078444fc0be68fc37` |
|  |  |  | `MODEL_CARD` | `f75d76ec577b5e7a49a7462883d31ef65383022387818a9a0b75bdaf90b9010c` |
|  |  |  | alignment-patched `.onnx` (local, `piper.patch_voice_with_alignment`) | `d1120aceb629668ffc742c69e6e183b45ccb379e8de3dee78a9261451f9ed9ff` |
| Piper voice tom medium | same revision | model card: dataset **AGPL-3.0**; model licence = dataset licence per piper-voices (unverified) | `fr_FR-tom-medium.onnx` | `bf65074ccdeeeeaa832e75edb1c0a513c01c9a972bdf085ff8a6e71ea234fd41` |
|  |  |  | `fr_FR-tom-medium.onnx.json` | `2f7f885ad5a0aad802e3cc24e4f57239febdcb142b4876de5d238094674361cc` |
|  |  |  | `MODEL_CARD` | `ece4132df4a1ae2f90e22c9f01539aec14acb7f8dc8ded64edd2051945d0f1e9` |
|  |  |  | alignment-patched `.onnx` | `b1c6e23a5948fd21c290133ec6156994a4ef1071218043229ac0d3fd46493323` |
| Piper voice upmc medium (speakers jessica, pierre) | same revision | model card: upmc-pierre-data **CC BY-SA 4.0** | `fr_FR-upmc-medium.onnx` | `9abb3800c199148897a9ed64e100d224f3de83579f100044174ad19418f1786f` |
|  |  |  | `fr_FR-upmc-medium.onnx.json` | `e8636ec15dfd5d72db37a02cb5320a20f2b8d339f2a0e4337da64c58a33a5868` |
|  |  |  | `MODEL_CARD` | `cf1ac7309e0e04bb159b7e6ae80bc8f66bfce9bdfb3e62ae93a6f57e210ab98a` |
|  |  |  | alignment-patched `.onnx` | `7f763f70ddf9148ecb878346e03133fc9779e45b69c5a0ddad8768cf203b496a` |
| ONNX Runtime | 1.31.1 (PyPI) | MIT | — | — |
| onnx (alignment patch) | 1.23.2 | Apache-2.0 | — | — |
| Kokoro-82M v1.0 (voice `ff_siwis`) | ONNX export, kokoro-onnx release `model-files-v1.1` | Apache-2.0 (weights); `ff_siwis` trained on SIWIS (CC BY 4.0) | `kokoro-v1.0.onnx` (fp32) | `beb0d1848dee9a49da392cc3df26958d46cfa35d321edf434f52949153f0df3a` |
|  |  |  | `kokoro-v1.0.int8.onnx` | `ae315a79b623f244700e4afb9246c46a26066782e049ba174bf3ba433970ee9c` |
|  |  |  | `voices-v1.0.bin` | `bca610b8308e8d99f32e6fe4197e7ec01679264efed0cac9140fe9c29f1fbf7d` |
| kokoro-onnx | 0.6.1 | MIT | — | — |
| phonemizer | 3.6.0 | GPL-3.0 | — | — |
| espeakng-loader | 0.2.4 (bundles espeak-ng **1.52.0**, GPL-3.0-or-later) | no licence in the wheel metadata (espeak-ng itself GPL-3); not shipped — plan 08 links espeak-ng directly | `espeak-ng.dll` | `646d387acbc7ac2aa45e3625aa00a6835ae5d446ff8b0748298c3900b4dde258` |
| MeloTTS | git `209145371cff` (myshell-ai/MeloTTS) | MIT | — | — |
| MeloTTS-French | HF myshell-ai/MeloTTS-French @ `1e9bf590` | MIT | `checkpoint.pth` | `fdf967d514f91582e451c482cab655e5d736821c3ba87ede8bb0625709642b29` |
|  |  |  | `config.json` | `361e84109451acb2f0331c2c9f3c5437e9a502380bbf8741b02545e41b062139` |
| dbmdz/bert-base-french-europeana-cased (MeloTTS prosody) | HF @ `b895c3cf` | MIT | `pytorch_model.bin` | `ff5df6abf065e94b474d58176a72f89a977320ddd06448a258797e74a4257a89` |
| gruut (MeloTTS French G2P) | 2.2.3 | MIT | — | — |
| PyTorch (CPU) | 2.5.1 | BSD-3-Clause | — | — |
| Whisper medium (measuring tool) | HF Systran/faster-whisper-medium @ `08e178d4` | MIT | `model.bin` | `9b45e1009dcc4ab601eff815b61d80e60ce3fd8c74c1a14f4a282258286b51ae` |
| faster-whisper / CTranslate2 | 1.2.1 / 4.8.5 | MIT / MIT | — | — |
| jiwer / SciPy | 4.0.0 / 1.18.1 | Apache-2.0 / BSD-3-Clause | — | — |
| Microsoft Hortense Desktop (baseline) | Windows component | OS licence | `M1036Hortense.*` + `MSTTSLocFrFR.dat` | — |

Not used: **Supertonic** (OpenRAIL-M adds use restrictions, not GPL-3-compatible; excluded by D25).

## 9. Blind listening (owner, later)

The page is ready: `<work>\voice-lab\blind\index.html` (16 sentences ×
7 voices = 112 clips, 196 ratings). It is generated by
[make_blind_page.py](../tools/voice-lab/make_blind_page.py) from the template
[blind_test_template.html](../tools/voice-lab/blind_test_template.html) and the WAVs of this run; the audio stays on
the lab disk next to the page (`blind\audio\`).

- **Voices** (default): Hortense, Piper siwis, tom, upmc-jessica, upmc-pierre, Kokoro fp32, MeloTTS. Kokoro int8 is
  left out (same phonemes and voice as fp32, and far too slow, § 4); add it with `--engines`.
- **Sentences** (default): the 12 liaison sentences plus a question, an exclamation, an elision-heavy question and a
  date (`L01–L12, S03, S04, S13, N01`).
- **Blind**: every clip is copied under a random file name; the order of the voices is shuffled per sentence and
  shown as letters A–G; the code → voice key is stored base64-encoded and only decoded by « Exporter » / « Révéler ».

**How to run it**

1. Open `index.html` in Edge or Firefox (double-click; no server, no network). Use headphones, at a normal volume.
2. For each sentence, play each letter (any order, as often as needed) and give:
   - **Naturel** 1–5 (1 = robotic, tiring; 3 = acceptable; 5 = sounds like a person);
   - **Liaisons** 1–5 on the sentences that list « Liaisons à écouter » (1 = missing or wrong liaisons;
     5 = all right, and none where the list says « sans liaison »).
3. Ratings are kept in the browser if the page is closed; « Effacer mes notes » starts again.
4. At the end, click **« Exporter (JSON) »**: it downloads `blind-results-<run>.json` (each rating with its voice,
   plus the mean per voice). « Révéler les voix » shows the means on the page.

**How to record the result**

1. Save the JSON in `<work>\voice-lab\blind-results\` (created by the
   generator, never touched by it).
2. Add a « Blind listening » table to this file (voice, mean naturalness, mean liaisons, n) and compare it with the
   provisional recommendation (§ 6).
3. If the owner's ranking confirms siwis + tom, mark D20 as confirmed (« confirmed by blind listening, <date> »);
   if not, change D20 in [../design/product-decisions.md](../design/product-decisions.md) (voice names) and the
   voice list of plan 08 before plan 08 starts.
4. Plan 05's quality threshold is met when siwis and tom beat Hortense on both naturalness and liaisons.

Every clip is resampled to 22.05 kHz and levelled to the same loudness (RMS −20 dBFS), so neither the sample rate nor
the volume tells the engines apart.

**Do not regenerate the page once rating has started**: a new page has new codes and a new run id, so the ratings
kept in the browser no longer apply. Regenerating replaces only `blind\index.html` and `blind\audio\`. If needed,
from the repository root:
`<work>\voice-lab\venvs\whisper\Scripts\python.exe -I tools\voice-lab\make_blind_page.py --root <work>\voice-lab`.

## 9b. Blind listening — results (owner, 2026-11-11)

Export `blind-results-20261110-011832.json` (196/196 ratings, one listener), archived in
`<work>\voice-lab\blind-results\`.

| Rank | Voice | Naturel (n = 16) | Liaisons (n = 12) |
|---|---|---|---|
| 1 | **Piper siwis** | **4.88** | **5.00** |
| 1 | Kokoro fp32 | 4.88 | 5.00 |
| 3 | **Piper upmc-pierre** | **4.88** | 4.42 |
| 4 | MeloTTS | 4.63 | 4.50 |
| 5 | Piper upmc-jessica | 4.63 | 4.25 |
| 6 | Piper tom | 4.25 | 4.08 |
| 7 | SAPI Hortense | 2.06 | 4.92 |

Findings:

- All neural voices sound far more natural than Hortense (2.06): neural voices are worth plan 08.
- **siwis** confirmed as the default voice (top on both scores). Kokoro ties it but is ~10× slower (§ 4): not retained.
- **tom** is the least natural neural voice (L12 garbled, as in § 4.2): **replaced by upmc-pierre** (natural 4.88,
  2× faster than tom, CC BY-SA 4.0 instead of AGPL-3). D20 updated.
- **Gap between phonemes and hearing:** « trois‿enfants » (L09) rated 1 for pierre, jessica and tom although the
  phoneme check found the /z/ (§ 3.3, 25–47 ms); same for pierre on « un‿enfant » (L02, rated 2). The upmc/tom /z/ and
  /n/ are too short or too weak to be heard. Plan 08: listen again to L02/L09 with pierre (e.g. a slightly lower
  `length_scale` or a pause-free phrase) and keep « liaison audible » in N-tests.
- Plan 05 quality threshold: siwis beats Hortense on both scores; pierre beats it on naturalness but not on liaisons
  (4.42 vs 4.92) — accepted by the owner.
- MeloTTS liaisons were rated 4.50 by ear, despite 2/19 in the phoneme check: a single listener and 12 sentences
  are a small sample.

## 9c. Pierre's inference variants (plan 08b step 7, 2026-11-13)

Pierre's liaisons were rated lower than siwis' (§ 9b: L02 = 2, L09 = 1). Five sets of Piper inference scales were
synthesised by the app itself (`--speech-bench --wav … --variant`, null audio, pierre = speaker 1 of
`fr_FR-upmc-medium`) on L01, L02, L06, L09, S11, S03, S13 (35 clips), and put on a new blind page (`make_blind_page.py
--root <work>\test-runs\08b-pierre\listening --engines pierre-v1,…,pierre-v5`; the 2026-11-11 page is untouched).

Duration of the liaison consonant in the model's own alignment (ms; hop = 256 samples = 11.6 ms, so values are
multiples of it):

| Variant | noise_scale / length_scale / noise_w | L01 /z/ | L02 /n/ | L06 /t/ | L09 /z/ | S11 /z/ |
|---|---|---|---|---|---|---|
| V1 (upstream) | 0.667 / 1.0 / 0.8 | 46 | 35 | 93 | 35 | 35 |
| V2 | 0.5 / 1.0 / 0.333 | 58 | 58 | 81 | 23 | 58 |
| V3 | 0.667 / 1.1 / 0.8 | 46 | 58 | 93 | 23 | 46 |
| V4 | 0.5 / 1.1 / 0.333 | 46 | 58 | 93 | 35 | 58 |
| V5 | 0.667 / 0.9 / 0.8 | 35 | 58 | 81 | 35 | 35 |

The noise makes each run different (one rendering per variant): the durations say the consonant is there in every
variant, not which one is heard best. For comparison, siwis (device bench, same items): 46 / 23 / 58 / 58 / 46 ms.

**Choice:** winner = highest mean liaison score on the blind sheet, ties by naturalness; it becomes the `inference`
block of pierre's card (voices.json → voice.json, B4). ~~Until the owner rates the page, pierre keeps V1 (upstream
scales, D28 Q4): release 0.6.0 ships V1.~~ *Superseded 2026-11-14: the owner rated the page, pierre uses V4 (below).*

**Owner's blind sheet (2026-11-14, 60/60 rated; results file `blind-results-20261113-214238.json` in the listening
folder):**

| Variant | Liaison (mean of 5) | Naturel (mean of 7) |
|---|---|---|
| **V4** | **4.0** | **5.0** |
| V3 | 4.0 | 4.71 |
| V1 (upstream) | 3.6 | 5.0 |
| V2 | 3.8 | 4.71 |
| V5 | 3.8 | 4.57 |

Winner **V4** (noise_scale 0.5, length_scale 1.1, noise_w 0.333; tie with V3 on liaisons, broken by naturalness): it is
now pierre's card `inference` (re-packed with `fetch_voices.py --voice pierre`; the patched model's SHA-256 `7f763f70…`
is unchanged, only `voice.json` differs). Rebuilt 0.6.0 (`publish-0.6.0-new`).

Owner's remarks on the sheet:

- « les amis »: the liaison is very good in all five variants (espeak gives `le-z amˈi`, the /z/ attached).
- « bon exemple » is said with a nasal « bon » instead of [bɔn]: espeak gives `bˈɔ̃n ɛɡzˈɑ̃pl` (nasal vowel **and** the
  /n/): to fix in plan 08c as a phoneme post-correction (« bon » before a vowel: `bɔ̃n` → `bɔn`; the other
  liaison words in `ɔ̃n` / `ɛ̃n` to be checked with the 08c probe).
- « trois enfants »: the liaison is weak: espeak gives `tʁwˈaz ɑ̃fˈɑ̃` — the /z/ ends the first word instead of starting
  the next (`le-z amˈi` sounds right); plan 08c: move a final liaison consonant to the next word (`az ɑ̃` → `a zɑ̃`) when
  the next word starts with a vowel.

**Isolated words (owner, 2026-11-14):** with pierre a word said alone (« très ») sounded different from one echo to the
next, whole sentences did not. espeak is not the cause: « très », « Très », « TRÈS », « très, », « très. », « très »
with spaces all give `tʁˈɛ` (bench `--word`, phonemes of every variant). It is VITS' sampling noise on a very short
input: the length of « très » over 10 renderings (cache off, null audio):

| Scales (noise_scale / noise_w) | Lengths, ms (10 takes) | Range |
|---|---|---|
| V1 0.667 / 0.8 | 336.7 – 476.0 | 139 ms |
| V4 0.5 / 0.333 | 359.9 – 452.8 | 93 ms |
| word 0.333 / 0.2 | 383.1 – 429.6 | 46 ms |

So isolated-word utterances (word echo, inserted word, a tile / right-click) use the card's `inference_word`
(pierre: noise_scale 0.333, noise_w 0.2; length_scale stays the voice's), and a **word audio cache** (voice, text,
scales and rate; ≤ 200 words / 20 MB) replays the first rendering: five echoes of « très » are identical sample for
sample (bench `--word très --word-cache on`, both voices), and come out in 0–1 ms instead of ≈ 170 ms. Siwis has no
`inference_word` (its scales were not judged unstable); it gets the cache.

**Siwis' pace (owner, 2026-11-14):** a little too fast by default: its card gets `inference` = its own scales with
`length_scale` 1.1 (0.5 / 1.1 / 0.333), like pierre. The speed setting (−10..10) multiplies that base:
`length_scale = base × 1.5^(−rate/10)` (rate 0 = the voice's pace, −10 = 1.5× slower, +10 = 1.5× faster); the old
absolute clamp [0.667, 1.5] would have made −8…−10 equal with a base of 1.1, so the clamp now follows the base. The
default rate stays 0.

## 9d. Pierre's isolated words and « il y a » (owner listening of 0.6.0, 2026-11-14)

Owner: pierre is « very bad » on words said alone (word echo: « la », « par », « est », « très », « même »), its
sentences are fine; « il y a » has a too long « y ». Measured with the app's own pipeline (`--speech-bench --wav <dir>
--word-items <file> [--word-suffix .] [--carrier "Le mot : {0}."] [--phoneme-fix a=b]`, null audio, 5 takes per word and
variant, word cache off) on 12 words (la par est très même maison enfant chat il et petit voiture); WAVs, tables and a
blind page in `<work>\test-runs\08b-pierre\words-listening\`.

| Variant | Scales (ns / ls / nw), input | Sound at the last sample (of 60) | Vowel share | Span, median (spread) | ORT run, median |
|---|---|---|---|---|---|
| W1 (0.6.0) | 0.333 / 1.1 / 0.2, `mot` | **20** | 0.54 | 379 ms (49) | 48 ms |
| W2 | 0.5 / 1.1 / 0.333, `mot` | 18 | 0.56 | 372 ms (75) | – |
| W3 | W1 + `mot.` | 4 | 0.51 | 375 ms (49) | 52 ms |
| W4 | W2 + `mot.` | 2 | 0.55 | 372 ms (56) | – |
| W5 | V4 in « Le mot : mot. », word cut out | 0 | **0.71** | 326 ms (49) | 90 ms |
| W7 | 0.5 / 1.35 / 0.333, `mot` | 8 | 0.58 | 428 ms (91) | – |
| siwis (0.6.0) | 0.5 / 1.1 / 0.333, `mot` | 5 | 0.64 | 394 ms (32) | 63 ms |

« Sound at the last sample » = trailing silence < 5 ms (the waveform stops mid-sound); vowel share = vowel nucleus
(stress mark + vowel) / (nucleus + consonants) in the model's alignment, median of the words; ORT run = the synthesis
call alone (`_model.Run`, null audio, 60 takes; « – » = not timed).

Findings (hypotheses of the owner's request):

- **Pipeline (trim / fade / cache / first chunk): nothing is cut.** Peak normalisation, then the queue, gain, resampler
  and WASAPI; the word cache replays the full rendering. But pierre's leading silence is ≈ 45 ms (siwis ≈ 200 ms) and
  the device is closed after 5 s idle: if an endpoint drops or ramps the first tens of ms of a new stream, pierre's
  first consonant is hit and siwis' is not. **Not verified** (null audio); check on the device.
- **No final « . » (b): supported.** A third of pierre's word takes end mid-sound without punctuation (est, et, chat,
  il, petit, voiture, la, maison); with « . » 2–4/60, in a carrier 0/60. The « . » also moves the final lengthening
  into its own frames (très /ɛ/ 221 → 93 ms) and adds ≈ 80 ms of silence.
- **Duration (c): words are short and consonant-heavy, not too long.** « même » vowel 81 ms / consonants 371 ms
  (siwis 140 / 290), « par » 93 / 127 (siwis 279 / 197). length_scale 1.35 lengthens all but keeps the imbalance; the
  carrier balances it best.
- **Lower word noise (a): not the cause by the measures** (same durations and balance as V4; only a narrower spread,
  49 vs 75 ms). The ear decides.
- **« la » ends with espeak's clitic marker « - »** (`lˈa-`, as every clitic: le, les, de…): alone, pierre gives it
  ≈ 260 ms, part of it sounding; in a sentence « - » is always followed by a word. W6 (« - » removed) gives `lˈa` with a
  209 ms /a/.
- **Carrier (e): feasible** — cut at the quietest 5 ms within 150 ms before the word's aligned start (the alignment's
  11.6 ms hop is too coarse for voiced onsets: up to 44 % of peak at the aligned start of même / voiture), 10 ms fade-in;
  costs ≈ +40 ms of synthesis (90 vs 48 ms median) on the first echo of a word (the cache replays it).
- **« il y a »:** espeak `il iʲ a`; pierre gives i+ʲ 139 ms in « Il y a un enfant dans la cour. » (siwis 46 ms) and
  186 ms alone (siwis 81 ms): the palatalisation mark ʲ is a phoneme of its own and pierre lengthens it. Candidate
  post-corrections: P2 `iʲ → i` (93 / 58 ms), P3 `iʲ → j` (104 / 70 ms, same espeak word count), P4 `il ja` (46 / 70 ms;
  merges two espeak words: the word map must then treat « y a » as one).

**Blind sheet:** 14 cards, 95 clips — W1–W5, W7 and siwis on the 12 words (W6 on « la » only), P1–P4 and siwis on
the two « il y a » items; scales « Naturel » and « Articulation » (`make_blind_page.py --aspect`).

**Owner's ratings (2026-11-14, 190/190; results file `blind-results-20261114-095445.json` in the words-listening
folder), mean of the 12 words (1–5):**

| Variant | Naturel | Articulation | Notes |
|---|---|---|---|
| **W5 carrier** | **4.83** | **4.83** | 5 / 5 on 11 words; « même » 3 / 3 |
| siwis (0.6.0) | 4.58 | 4.58 | « est » 1 / 1, « et » 4 / 4 |
| W3 (W1 + « . ») | 2.75 | 2.5 | |
| W1 (0.6.0) | 2.42 | 2.33 | |
| W2, W4, W7 | 2.08 | 2.0–2.17 | |
| W6 (« la » without « - ») | 1 | 1 | one word |

| « il y a » | I01 sentence (N / A) | I02 alone (N / A) |
|---|---|---|
| P1 (0.6.0, `iʲ`) | 4 / 3 | 1 / 1 |
| **P2 `iʲ → i`** | 4 / 3 | **5 / 5** |
| P3 `iʲ → j` | 4 / 3 | 4 / 3 |
| P4 `il ja` | 5 / 5 | 1 / 1 |
| siwis | 5 / 5 | 5 / 5 |

**Decision (0.7.2):**

- **Isolated words of pierre = W5.** Pierre's card has `"word_carrier": "Le mot : {0}."`: an isolated word (word echo,
  inserted word, right-click, a reading tile, a pupil's name — any word utterance without word progress, ≤ 48
  characters, no sentence end of its own) is synthesised inside the carrier with the voice's scales (V4 0.5 / 1.1 /
  0.333) and cut out by `PiperCarrier` — the same code the bench uses since 0.7.2 (`--carrier` only overrides the
  format), so the app plays what was rated. The cut: after the format's clause boundary (« : » + space) in the
  alignment, at the quietest 5 ms of the 150 ms before it, 10 ms fade-in, re-normalised. If the cut is not possible
  (no alignment because an unknown phoneme was skipped, boundary not found, nothing after it) or the text has its own
  sentence end, the word is said alone as in 0.6.0 (`inference_word` 0.333 / 0.2) and the log says
  `piper: word carrier fallback (<reason>)`. The cut clip (or the fallback's) goes to the word cache under a key of
  its own: a repeated word costs nothing. Siwis has no carrier (rated 4.58 alone) and is unchanged.
- **« il y a » = P2.** Pierre's card has `"phoneme_fix": [["iʲ", "i"]]` (`PiperPhonemeFix`), applied after espeak to
  every utterance of pierre (words, carrier, sentences, reading). P4 was best in the sentence but fails alone (1 / 1);
  P2 is the only one good in both. A rule may not contain a space and both sides must sound, so the espeak word count
  — hence the word map, the highlight and the liaison grouping (text-level) — is unchanged (unit test on the 40 golden
  items). espeak also writes `iʲ` for an /i/ before a vowel (« amis‿arrivent », « ami‿est », « lit‿une »,
  « mercredi‿après-midi »: 7 of the 40 golden items); the rule changes those too. That case was not rated: it is on
  the confirmation page below.
- The golden fixtures are unchanged: they check the port of Piper's phonemizer against piper-tts 1.8.0; the
  correction is a layer after it, tested on its own.

**Cost (time to first audio, cold word, null audio, word cache off; 12 words × 10 takes, self-contained 0.7.2):**

| Where | Carrier (0.7.2) p50 / p95 | Word alone (0.6.0 path, `--carrier off`) p50 / p95 | Δ p95 |
|---|---|---|---|
| Host (i7, 4.1 GHz) | 61.4 / 79.3 ms | 35.0 / 45.8 ms | +34 ms |
| Test VM (3 vCPU) | 69.2 / 84.7 ms | 35.9 / 49.3 ms | +35 ms |

(siwis on the host: 45.0 / 63.4 ms.) Well below the +150 ms p95 budget: no pre-synthesis of the suggestions and no
shorter carrier needed. A repeated word is a cache hit (0–1 ms).

**Confirmation page:** `<work>\test-runs\08c-pierre-words\page\blind\index.html` — the 12 words and the two « il y
a » items rendered by the final 0.7.2 pipeline (`--speech-bench --wav --word-items`, card defaults, null audio)
against the rated bench clips (W5, P2) and siwis, plus two /i/-before-a-vowel sentences (0.7.2, without the
correction, siwis). Commands in `run-08c.sh`.

**Owner's confirmation (2026-11-14, blind, 96 / 96 ratings, `blind-results-20261114-151041.json`):**

| Engine | Naturel | Articulation | n |
|---|---|---|---|
| pierre 0.7.2 (final pipeline) | 5.00 | 4.88 | 16 |
| pierre bench clips (W5 / P2) | 5.00 | 4.86 | 14 |
| siwis | 5.00 | 4.88 | 16 |
| pierre without `iʲ → i` | 4.50 | 5.00 | 2 |

- The shipped pipeline matches the rated bench clips: same notes item by item, with « par » and « même » at 5 / 4 in
  both (the 0.45 cut level of « même » is not heard). « il y a » 5 / 5.
- /i/ before a vowel: « Les amis arrivent ce soir. » 5 / 5 with and without the rule; « Mon ami est très gentil. »
  5 / 5 with it, 4 / 5 without. **The rule stays on every `iʲ`**; no need to limit it to the standalone « y ».
- The only low note is siwis « et » (articulation 3). Siwis is unchanged in 0.7.2.

## 9e. Siwis' isolated words (owner listening of 0.8.3, 2026-11-16)

The owner heard « papa » and « et » said wrongly by siwis alone (TODO « Speech »). Blind sheet of 18 short words
(papa, maman, et, est, a, à, un, eu, oui, le, la, mais, les, des, nez, lit, pa, mi) × 4 variants, one take each,
rendered with piper-tts 1.8.0 and the aligned siwis model (`synth_siwis_words.py`, a port of `PiperCarrier`'s cut):
A the word alone with the card's scales (0.5 / 1.1 / 0.333, today), B in « Le mot : {0}. » cut on the alignment
(pierre's carrier), C alone with word scales 0.333 / 0.2, D in « Je dis : {0}. » cut the same way. Files:
`<work>\test-runs\siwis-words\` (page, `latency.json`, `blind-results-20261116-190332.json`, 144 / 144 ratings).

| Variant | Naturel | Articulation | Low notes | Synthesis p50 / p95 (piper-tts, 10 cold takes × 18) |
|---|---|---|---|---|
| **B « Le mot : {0}. »** | **4.67** | **4.72** | « eu » 2 / 2, « les » 3 / 3 | 98.1 / 127.2 ms |
| D « Je dis : {0}. » | 4.61 | 4.67 | « eu » 2 / 2, « un » 4 / 3 | 96.6 / 117.6 ms |
| A alone (0.8.3) | 3.72 | 3.72 | « papa », « mais » 1 / 1; « à », « un », « pa » 3 | 51.2 / 73.8 ms |
| C alone, word scales | 3.61 | 3.61 | « papa », « mais » 1 / 1; « à », « les », « pa », « mi » 3 | 51.9 / 68.9 ms |

- The carrier lifts « papa » and « mais » from 1 to 5 and most words to 5; « et » 4 → 5.
- **« eu »** is 5 / 5 alone and 2 / 2 in both carriers. espeak gives `y` in every variant (alone too: « eu » the
  participle), so the phonemes do not explain it; the bare vowel after the « : » pause is what sounds wrong.
- **« les »** is 5 alone (A), 5 in D, 3 in B (and 3 alone with the word scales, C). Check of B's cut (no
  listening): the rated clip starts at −65 dB and the /l/ rises over 20–60 ms as in « la » (not clipped); on 20
  re-synthesised takes of « Le mot : les. » the cut is 23–135 ms before the aligned start, at 0.001 of the peak
  (silence), exactly like « le », « la », « lit ». The cut is not the cause, so the cut code is unchanged (pierre
  untouched); one take per variant was rated and VITS noise varies per take.
- Cost: the carrier synthesises ≈ 4× the phonemes, +53 ms p95 for the synthesis call here (pierre in the app: +34 ms
  first audio, § 9d); below the +150 ms budget, and a repeated word is a cache hit.

**Decision (owner, 2026-11-16):** siwis' card gets pierre's carrier, `"word_carrier": "Le mot : {0}."`, with
`"word_carrier_except": ["eu", "les"]`: a word on that list is always said alone (A, the voice's scales). The list is
compared after the carrier's own normalisation (trimmed), NFC and lower case, whole text only (« Les » matches,
« les amis » does not). Siwis has no `inference_word`, so a failed cut also falls back to A. Same mechanism as
§ 9d otherwise (`PiperCarrier`, word cache under the carrier key; an excepted word is cached under the plain key).
Not yet heard in the app: the first owner test of a build with the new card closes it.

## 10. Not verified / limits

- **No listening at all** (D25): naturalness, prosody and pleasantness are unmeasured; the blind test (§ 9) is
  the missing half of the decision.
- **Low-end profile** = 2 cores of a 4.1 GHz i7: it caps parallelism, not per-core speed; the ×2.5 factor for a
  Celeron-class CPU is an estimate. No real school laptop and no Windows Sandbox were used (D25).
- **Liaisons are read from the phonemes the engine synthesised**, plus the consonant's duration in the audio; no
  acoustic phone recogniser checked them by ear.
- **Whisper as listener**: a strong model that repairs small slips; its word timestamps are ±50–100 ms on short
  words and snap the first word to 0 s. WER/PER rank the engines; they do not measure what a child understands.
  Only one run per engine was transcribed (Piper and Kokoro are not deterministic: another run would differ
  slightly).
- **Word timing**: the plan's spot check of 10 sentences by eye/ear was not done; the 95 % threshold is not
  conclusive for siwis and upmc (§ 5).
- **libpiper (C API)** was not built: whether it exposes the alignments is unverified (plan 08 step 1).
- **Load times** were measured once per process (cold start of each run, files in the OS cache after the first).
- **Kokoro word timings via KokoroSharp** (plan 05 risk) were not tried: kokoro-onnx's duration output was used.
- **SAPI first-audio** is measured on a memory stream; the app plays through the audio device, which adds its own
  buffer latency (same for every engine in plan 08 with NAudio, not measured here).
- The blind page was checked for rendering (headless Edge: 112 clips, 196 rating groups, all audio files present,
  every voice 16 times, order shuffled); its buttons (export, reveal, reset) were not clicked.
