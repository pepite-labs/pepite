# voice-lab — neural French voice test harness (plan 05)

Measures the candidate voices of D20 / D25 on objective criteria: latency, liaisons (from the synthesised
phonemes), intelligibility (Whisper), word timing, footprint; and builds the blind listening page. Results and
how to read them: [../../development/voice-evaluation.md](../../development/voice-evaluation.md).

Nothing here ships with the app. Venvs, models, WAVs and outputs live in your bulk-data folder, given as
`-Root` / `--root` or by the `VOICE_LAB_ROOT` environment variable, never in the repository. Python always runs
with `-I`.

| File | Role |
|---|---|
| `run_all.ps1` | Runs the steps in order: `setup`, `fetch`, `patch`, `normal`, `2core`, `transcribe`, `analyse`, `blind` |
| `requirements-*.txt` | Pinned packages of the four venvs (piper, kokoro, whisper: Python 3.12; melo: Python 3.10) |
| `models.json`, `fetch_models.py` | Pinned model URLs + SHA-256; download and check |
| `common.py` | Test items, CPU profiles, timing protocol, output layout |
| `synth_sapi.ps1` | Baseline: Microsoft Hortense (System.Speech), 16 kHz native format |
| `synth_piper.py` | Piper (piper1-gpl 1.8.0), alignment-patched voices |
| `synth_kokoro.py` | Kokoro-82M `ff_siwis` via kokoro-onnx (fp32 / int8) |
| `synth_melo.py` | MeloTTS-French (PyTorch CPU) |
| `liaisons.py` | The 23 liaison checks and their scoring |
| `transcribe.py` | Whisper medium (faster-whisper) transcripts with word times |
| `analyse.py` | Tables → `out/results.md`, `out/results.json` |
| `make_blind_page.py`, `blind_test_template.html` | Blind listening page → `blind/index.html` + `blind/audio/` |

Quick start (from the repository root): `powershell -NoProfile -ExecutionPolicy Bypass -File tools\voice-lab\run_all.ps1 -Root <bulk-data folder>`
(about 2 h on an i7-8750H, one CPU-heavy process at a time). One step only: `-Steps analyse`; one engine:
`-Steps normal -Engines piper`.
