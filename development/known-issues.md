# Known issues — `Predict.Keyboard`

Open problems found in real use, kept until fixed. Newest first.

## KI-2 — The Piper voices (0.5.0 / 0.6.0 test releases, plans 08a / 08b): known limits until plan 08c

| | |
|---|---|
| Found | 2026-11-11, plan 08a (by design, from plan 05's evaluation) |
| Build | 0.5.0 test release (`publish-0.5.0`); 0.6.0 (`publish-0.6.0`, siwis + pierre) |
| Numbers and units | siwis reads what espeak-ng makes of the text: « 8 h 30 » → « huit h trente », « 15,50 € » → « quinze virgule cinquante euros ». Same with pierre (0.6.0). A text normaliser comes in plan 08c. |
| Voice change at start | For the first 2.5–4 s after the start (longer on a slow PC) the Piper voice is still loading: the first utterances use the Windows voice (Hortense), then siwis takes over. `speech: using SAPI until piper:siwis is ready` in the log. « Écouter » on Siwis waits for the load. |
| Early highlight | With siwis, the highlight of a word that starts with a stop or a fricative (p, t, k, s…) can appear a little before the word is heard (plan 05: 89 % of word starts within the window against an automatic transcription). The highlight follows the audio clock; the offset comes from the model's alignment. A per-sound correction is possible in 08b if the owner's check asks for it. |
| Word echo liaisons | **Closed in 0.6.0 (plan 08b)**: the word echo says « les » then « les amis » (liaison grouping, `Liaison.cs`); whole sentences have espeak's liaisons. Numbers (« 9 ans ») stay open until 08c. |
| Two voices (0.6.0) | « Écouter » on the voice that is not the configured one loads it (≈ 3–5 s, `waiting for the Piper voice to load`); it stays in memory 30 s after its last utterance, then `speech: piper:<id> unloaded (idle)`. Pierre is a test voice (`"public": false`, D28). **Fixed in the 0.6.0 rebuild of 2026-11-14**: Enregistrer with the other voice no longer hands the echoes to Hortense while it loads — the previous Piper voice speaks until the new one is ready (`using piper:siwis until piper:pierre is ready`); selecting a voice in the list starts its load. |
| Liaisons espeak gets wrong | « bon exemple » is said with a nasal « bon » (espeak `bˈɔ̃n ɛɡzˈɑ̃pl`, wanted [bɔn]); « trois enfants » has a weak liaison (`tʁwˈaz ɑ̃fˈɑ̃`: the /z/ stays on « trois »; « les amis » → `le-z amˈi` is right). Owner's blind sheet, 2026-11-14. **Open → plan 08c** (phoneme post-corrections). |
| VC++ runtime | `onnxruntime.dll` needs the Visual C++ 2015–2022 runtime. A clean Windows (the test VM) lacks it: the load failed with `TypeInitializationException` (`Microsoft.ML.OnnxRuntime.NativeMethods`) and the app spoke with SAPI. **Fixed in 0.5.0 (A13)**: the four runtime DLLs ship app-local (`Data.Build/stage_vcruntime.ps1`). |
| Non-ASCII install path | espeak-ng opens its data with the ANSI file API. A folder name with non-ASCII characters is passed as its 8.3 short name; on a volume where 8.3 names are disabled that fails: `piper:siwis unavailable (InvalidOperationException: espeak_Initialize failed …), using SAPI`. Install under an ASCII path. |
| Very long sentences | A sentence of more than ≈ 35 words is synthesised in pieces of ≤ 200 phonemes (cut at a comma, else between words): the intonation restarts at each cut. A 1000-word text without punctuation starts in 0.66 s but peaks at +347 MB of working set (the 250 MB gate holds for normal sentences). |
| Load time | The load checks the voice's SHA-256 (60 MB) at every start: 3.3–4.8 s on the dev PC, 9.4–10.3 s in the test VM (cold disk). SAPI covers that time. |
| Status | **Open → plan 08c** (normaliser). Liaison grouping and pierre done in 0.6.0 (plan 08b). |

## KI-1 — No visible feedback at start-up; a second start looks like "nothing happens" (0.2.0 test release)

| | |
|---|---|
| Found | 2026-11-08, owner, second PC (not the dev host, not the sandbox) |
| Build | `release/prediction-0.2.0-test` — self-contained publish of 080cdb4, folder on the user's Desktop |
| Symptom | Double-click on `PredictKeyboard.exe` seemed to do **nothing**: no window, no message. |
| Cause (corrected 2026-11-09) | **Not a rights problem.** The app **was already running** (started earlier), and the user did not notice: the only sign of life is the tray icon, and nothing tells the user it started. The "Exécuter en tant qu'administrateur" start that "worked" was simply a second instance. |
| Status | **Fixed in 0.6.1**: welcome window at a manual start, « Pépite est déjà démarré » on a second start (single instance), a notification at a start with Windows ([keyboard-design.md §6e](../design/keyboard-design.md)); VM run `rebrand-061` W1–W3. |

Earlier hypotheses (non-admin crash, Mark-of-the-Web, OneDrive placeholders) are **withdrawn**; the
"Exécuter en tant qu'administrateur" workaround must not be suggested to users.
