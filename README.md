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
1. Download `InterviewAssistant-win-x64.zip` (GitHub → Actions → latest **windows-release** run → Artifacts, or Releases), unzip anywhere.
2. Run `InterviewAssistant.exe`. Windows SmartScreen may warn because the EXE is unsigned → **More info → Run anyway**.
3. The **Pre-interview check** opens on first run:
   - **Settings** → paste your OpenAI API key → *Test* → choose the **headset** as the playback device → Save.
   - Play any video in Chrome: the green level bar must move.
   - **Run checks** → all green → **Start interview**.
4. During the call, keep the window beside the camera. Status shows `● LISTENING`.

See **docs/PRE_INTERVIEW_CHECKLIST.md** for the 5-minute routine before the call.

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
tests/InterviewAssistant.Tests  82 automated tests incl. 60-min simulated soak and answer-quality evaluation
scripts/                        build-release.ps1, run-tests.ps1, smoke-test.ps1, question bank generator
.github/workflows/              windows-latest: build → test → publish → launch smoke test → ZIP artifact
```
Docs: [BUILD.md](BUILD.md) · [TESTING.md](TESTING.md) · [SECURITY.md](SECURITY.md) · [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)

## Editing answers
Prepared answers live in `scripts/question_bank_source.py`. Edit, then run `python scripts/question_bank_source.py` to regenerate `knowledge/question_bank.json` and run the tests (they enforce bullet count, length and factuality). The published app reads `knowledge/` next to the EXE, so you can also edit those files directly after unzipping.

## Known limitations (honest)
- **Not yet run on real Windows + Google Meet by the developer.** Everything was compiled for win-x64 and tested headlessly on Linux; the CI workflow builds, tests and launch-smoke-tests on `windows-latest`. Run the Pre-interview check yourself before relying on it.
- Live latency depends on the OpenAI realtime service; partial words appear per speech segment (≈ each pause), not word-by-word, because the server transcribes each VAD segment after it ends.
- Research pages (abloxx.com, teroxx.com, job boards, the whitepaper PDF) were blocked by the build environment's network policy; facts come from search-engine extracts and are labelled by source. Re-read the whitepaper and live job posting before the interview.
- Model names (`gpt-4.1-mini`, `gpt-4o-transcribe`) are configurable in Settings in case your account uses different ones.
