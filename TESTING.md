# Testing, Validation Evidence & Known Limitations

## Windows CI (real `windows-latest` runner, Windows NT 10.0.26100) — run #4, commit `e77fbeb`: ✅ all green
https://github.com/SaEeDX4/REAL-TIME-WINDOWS-INTERVIEW-ASSISTANT/actions/runs/37502533008

| Step | Result |
|---|---|
| Restore / build (warnings = errors) | ✅ |
| 88 automated tests | ✅ 88/88 |
| Publish win-x64 self-contained single-file | ✅ |
| **Smoke test** (`scripts/smoke-test.ps1`): real EXE launched, top-level window created (~1.3 s), alive 15 s (~228 MB working set), closed via WM_CLOSE, **exit code 0**, log shows *Knowledge loaded → Main window loaded → Close requested → Clean shutdown*, no UI/fatal exceptions | ✅ |
| **In-app self-test** (`InterviewAssistant.exe --selftest`, no API key) | ✅ 20/20 (below) |
| Artifact `InterviewAssistant-Windows-x64` (60.8 MB) + `validation-evidence` (logs, self-test JSON, TRX) | ✅ uploaded |

Self-test checks executed inside the real WPF process on Windows:
main window + XAML · topmost · knowledge pack · question bank (97) · fact validator on all prepared answers · manual question → exactly 3 distinct bullets in UI (≈0.4 s incl. UI) · spoken paraphrase match · missing key: Start shows guidance and does not listen · missing key: unknown question shows actionable message · previous/next navigation · Settings window XAML · Pre-interview window XAML + local checks · compact mode · diagnostics panel · DPAPI round-trip (wrong entropy rejected) · settings persistence · hotkey parsing · audio enumeration with **zero devices** (no crash, status `NO PLAYBACK DEVICE`) · turn detector + duplicate guard · stop → idle.

### Bugs found and fixed by this Windows validation
1. **App ignored WM_CLOSE while the first-run check was open** (modal dialog disabled the main window) → check is now non-modal, owned windows close first, cleanup capped at 5 s.
2. **Every prepared answer rendered twice** in the UI (6 bullets instead of 3) → idempotent append-only rendering from a thread-safe snapshot; self-test asserts exact count.
3. **`reasoning_effort: "minimal"` would have been rejected by gpt-5.x models** → per-family effort + automatic retry/fallback.

## Automated tests (any OS): 88/88
Knowledge integrity & answer-quality evaluation (all 97 answers: 3–4 bullets, ≤85 words, no unverified numbers, no unsupported past claims) · matcher 97/97 indexed, **25/30 held-out phrasings, 0 wrong high-confidence** · turn detection (multi-part, resume-cancel, revisions, pleasantries, missing transcript) · duplicate guard · audio conversion 16–96 kHz · providers (SSE, error mapping, reasoning-effort retry, model fallback, realtime session JSON, transcription model fallback) · engine end-to-end with fakes · **simulated 60-minute soak**: 139/139 answers, 0 duplicates, LLM calls ≤ questions, flat memory · repository secret scan · optional live LLM evaluation (runs when `OPENAI_API_KEY` is set).

## TESTED / NOT TESTED
| Area | Status |
|---|---|
| Windows startup, XAML/resources, all three windows, clean shutdown, exit code | **TESTED on Windows CI** |
| Manual question UI, prepared answers, navigation, compact, diagnostics | **TESTED on Windows CI** |
| Missing-API-key behaviour, error states, no-audio-device behaviour | **TESTED on Windows CI** |
| DPAPI credential protection, settings persistence, hotkey parsing | **TESTED on Windows CI** |
| Pipeline logic, turn detection, dedupe, matcher, validator, soak | **TESTED** (unit/sim) |
| WASAPI loopback with a real headset, Chrome/Meet audio, Bluetooth reconnect | **NOT TESTED** — CI runners have no audio endpoint. Your test: docs/PRE_INTERVIEW_CHECKLIST.md tests 4, 10, 13 (Test audio button) |
| Live OpenAI transcription (`gpt-live-transcribe`) and answers (`gpt-5.4-mini`), real latency | **NOT TESTED** — no API key in CI. Your test: tests 3, 5, 6, 9 (Test transcription / Test AI). Add repo secret `OPENAI_API_KEY` to also run the live evaluation in CI |
| Global hotkey delivery, always-on-top over Chrome, multi-monitor/DPI | **NOT TESTED** interactively — tests 11, 12 |

## Known limitations
- Physical audio + live API paths need the 10–15 min check on your PC.
- Unsigned EXE → SmartScreen "More info → Run anyway".
- Size ~64 MB EXE / ~58 MB ZIP. Measured alternatives: uncompressed single-file 140 MB; folder layout 146 MB; ReadyToRun +4 MB; framework-dependent 2 MB but requires installing the .NET 8 Desktop Runtime (extra failure point). Trimming not used (WPF/reflection unsafe). Current choice = smallest reliable.
- Model availability depends on your OpenAI account; fallbacks are automatic (`gpt-live-transcribe`→`gpt-4o-transcribe`→`gpt-4o-mini-transcribe`; `gpt-5.4-mini`→`gpt-4.1-mini`) and Settings can override.
- Company facts (Teroxx/Abloxx/whitepaper/job posting) came from search extracts because those sites were blocked for the build environment; they are labelled by source in `knowledge/` and should be re-read before the interview. Résumé facts are verbatim from the CV.
