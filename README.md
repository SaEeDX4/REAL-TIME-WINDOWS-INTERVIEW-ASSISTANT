# Interview Assistant — Shervin Fallahdoust × Teroxx Product Owner

A Windows desktop assistant that listens to the **interviewer's audio** from your headset (Chrome / Google Meet stays completely normal), detects when a question is finished, and shows **3 short, speakable bullets** you can read and say in your own voice.

> The interview allows AI assistance. This app is a normal, visible, always-on-top window. It does not hide, record the meeting, or touch your microphone.

```
Interviewer speaks in Meet
 → WASAPI loopback capture of your headset (no browser extension, no tab sharing)
 → 24 kHz mono PCM16 → OpenAI Realtime transcription (server VAD segments)
 → Adaptive end-of-question detector (merges multi-part questions, cancels if interviewer resumes)
 → Duplicate guard → classify (Verified / Hypothetical / Bridge) → local semantic match (97 prepared answers)
 → HIGH match: prepared answer instantly (≈5 ms, no API call)
   otherwise: compact context retrieval → streaming LLM → fact validation → bullets appear one at a time
 → back to LISTENING
```

## Quick start (Windows 10/11 x64)
1. Download **`InterviewAssistant-Windows-x64`** from GitHub → *Actions* → latest green **windows-release** run → *Artifacts* (GitHub wraps it in a zip; inside is `InterviewAssistant-Windows-x64.zip` → unzip again). Contents: `InterviewAssistant.exe` (self-contained, no .NET install) + `knowledge\` + docs.
2. Run `InterviewAssistant.exe`. Windows SmartScreen may warn because the EXE is unsigned → **More info → Run anyway**.
3. The **Pre-interview check** opens on first run:
   - **Settings** → paste your OpenAI API key → *Test* → choose the **headset** as the playback device → Save.
   - Play any video in Chrome: the green level bar must move.
   - **Test audio / Test transcription / Test AI**, then **Run all checks** → **READY FOR INTERVIEW** → **Start interview**.
4. During the call, keep the window beside the camera. Status shows `● LISTENING`.

See **docs/PRE_INTERVIEW_CHECKLIST.md** for the 10–15 minute hardware test and the 2-minute interview-day routine.

### Build locally on Windows
```powershell
git clone https://github.com/SaEeDX4/REAL-TIME-WINDOWS-INTERVIEW-ASSISTANT.git
cd REAL-TIME-WINDOWS-INTERVIEW-ASSISTANT
git checkout claude/clever-hopper-m8kdai
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1      # needs .NET 8 SDK
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1         # launch/close check
powershell -ExecutionPolicy Bypass -File scripts\selftest.ps1           # in-app self-test (no API key)
# -> artifacts\InterviewAssistant-Windows-x64.zip, artifacts\InterviewAssistant\InterviewAssistant.exe
```

### Models (verified against OpenAI announcements, Oct 2026)
| Use | Default | Why | Automatic fallback |
|---|---|---|---|
| Transcription | `gpt-live-transcribe` | streaming model: words appear *while* the interviewer speaks (~$0.017/min) | `gpt-4o-transcribe` → `gpt-4o-mini-transcribe` |
| Answers | `gpt-5.4-mini`, reasoning `none` | ~0.7 s first token, ~175 tok/s, better quality than 4.1-mini | `gpt-4.1-mini` |
All changeable in Settings. Rough cost for a 60-min interview: ≈ $1 transcription + a few cents for answers.

## Using it live
| Action | How |
|---|---|
| Start / pause listening | **Start** button or `Ctrl+Alt+L` |
| Show / hide window | `Ctrl+Alt+Space` |
| Type or paste a question (works even if audio fails) | box at the bottom, or `Ctrl+Alt+Q`, then Enter |
| Shorter / Technical / Example / Full answer / Regenerate | chips under the answer (AI) |
| Previous / next answer | ◀ ▶ |
| Compact mode (question + bullets only) | ⧉ icon in header |
| Diagnostics: latency, CPU, memory, reconnects, est. cost | pulse icon in header |
| Pre-interview check + Test audio / transcription / AI | ✓ icon in header |
| Offline self-test (no API key) | `InterviewAssistant.exe --selftest result.json` |

Answers never rewrite under your eyes: bullets are **appended** once complete; prepared answers stay stable.

## Answer rules (built into prompt + validator)
- **Verified** (your history): only résumé facts and numbers (Arzif 60,000 traders, +60% revenue, Binance/CoinEx/KuCoin, −20% downtime, +25% satisfaction, LG GTM, …).
- **Hypothetical** ("how would you…"): the strongest Product Owner approach, phrased "I'd…", no unnecessary disclaimers.
- **Bridge** ("have you personally built a ledger?"): one honest sentence, then the approach. Never invents past work.
- A validator flags unverified numbers and softens unsupported "I built / I led" claims in AI output before display.

## Repository
```
src/InterviewAssistant.Core     cross-platform engine: audio conversion, turn detection, matching, retrieval,
                                prompts, validation, OpenAI realtime + streaming clients, orchestration, metrics
src/InterviewAssistant.App      WPF UI, WASAPI loopback (NAudio), DPAPI key store, hotkeys, settings
knowledge/                      candidate profile, stories, role/company/product/whitepaper research,
                                PO + crypto playbooks, answer policy, runtime prompt, 97-question bank, tests
tests/InterviewAssistant.Tests  88 automated tests incl. 60-min simulated soak and answer-quality evaluation
scripts/                        build-release.ps1, run-tests.ps1, smoke-test.ps1, question bank generator
.github/workflows/              windows-latest: build → test → publish → launch smoke test → ZIP artifact
```
Docs: [BUILD.md](BUILD.md) · [TESTING.md](TESTING.md) · [SECURITY.md](SECURITY.md) · [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)

## Editing answers
Prepared answers live in `scripts/question_bank_source.py`. Edit, then run `python scripts/question_bank_source.py` to regenerate `knowledge/question_bank.json` and run the tests (they enforce bullet count, length and factuality). The published app reads `knowledge/` next to the EXE, so you can also edit those files directly after unzipping.

## Known limitations
See [TESTING.md](TESTING.md#known-limitations). In short: Windows startup, XAML, UI flows and error paths are validated automatically on a real Windows runner; physical audio devices, Google Meet and live OpenAI calls must be checked once on your PC (docs/PRE_INTERVIEW_CHECKLIST.md). Company facts that could not be read from official pages are labelled in `knowledge/`.
