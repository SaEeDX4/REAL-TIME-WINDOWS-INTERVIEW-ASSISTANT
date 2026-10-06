# Testing & Test Report

Run: `powershell -File scripts\run-tests.ps1` (Windows) or `dotnet test tests/InterviewAssistant.Tests -c Release` (any OS).
Live LLM evaluation: set `OPENAI_API_KEY` first (otherwise that one test reports *SKIPPED* and passes).

## Results (2026-10-06, Linux x64 build host, .NET 8)
**82 tests passed, 0 failed** (the live-LLM test self-skips without a key). The whole solution (including the WPF app) compiles for win-x64 with warnings-as-errors.

| Area | Result |
|---|---|
| Knowledge integrity | 97 prepared questions (≥80 required), 107 test utterances, all evidence ids resolve to verified stories |
| Prepared-answer evaluation (automated) | All 97 answers: 3–4 bullets, 6–30 words per bullet, ≤85 words total, **no unverified numbers, no unsupported past claims**; average 47.4 words (~20 s spoken). Bridge answers all open with an honest bridge. |
| Matcher — indexed paraphrases | 97/97 top-1, 0 wrong high-confidence |
| Matcher — **held-out novel phrasings** | **25/30 (83%) top-1; 0 wrong high-confidence matches** → misses go to the LLM, never show a wrong prepared answer |
| Unexpected questions | ≤1 of 10 served from cache (goes to LLM) |
| Matching speed | ~3.7 ms per question |
| Turn detection | clear question finalizes 300–700 ms after speech-stopped; multi-part with pause → **one** question; interviewer resumes during settle → finalization cancelled; revised transcript → no duplicate; pleasantries ("Okay, great, thank you") ignored; missing transcript → no hang |
| Duplicate protection | punctuation/word revisions suppressed; re-ask after 30 s answered again |
| Audio conversion | 48k/44.1k/96k/16k stereo float → 24k mono PCM16: correct length (±0.4%) and amplitude; odd chunk sizes and split frames are continuous (±2 LSB) |
| Providers | SSE streaming parse; 401/429/500/400/network/missing-key mapped to typed errors; realtime `session.update` JSON matches the documented GA shape (and legacy beta shape); realtime events dispatched; malformed JSON ignored |
| Engine end-to-end (fake transcriber + fake LLM) | cache hit → 0 LLM calls; unknown → 1 streaming call, bullets appended once in order; LLM failure → prepared fallback; invalid key → actionable message + `API ERROR`; manual question works with no audio and no provider; variants use LLM with follow-up context; fabricated "I built … 43%" softened + flagged; new question cancels in-flight generation; reconnect surfaced and recovered; pause blocks audio and answers; no-audio warning appears/clears |
| **60-minute soak (simulated clock)** | 36,153 audio frames, 139 questions (30% multi-part), 28 small-talk turns, 1 reconnect: **139 answers for 139 questions, 0 duplicates, LLM requests 54 ≤ questions, history capped at 30, metrics capped, managed memory flat (6.6 MB → 6.4 MB)** |

### Latency
| Segment | Measured / estimated |
|---|---|
| Speech-stopped → question finalized (settle logic) | **measured in simulation: 460 ms** for a clear question (450 ms rule + tick); 1.7 s when the sentence trails off ("…and") |
| Question → prepared answer on screen | **measured: <5 ms** (match + render) |
| Interviewer stops talking → question finalized (real) | *estimated* 0.85–1.3 s = server VAD silence 400 ms + max(transcription ≈0.3–0.8 s, settle 450 ms). **Not measured — needs live Windows run.** |
| Question → first AI bullet (real) | *estimated* 0.8–2 s (first-token + first full sentence). Probe in Pre-interview check measures first-token latency live. |
Diagnostics (pulse icon) shows real per-question numbers and p50/p95 during use.

## TESTED / NOT TESTED
| Item | Status | Why / how to close |
|---|---|---|
| Core pipeline logic, matching, validation, turn detection, soak | **TESTED** (automated, above) | — |
| WPF app compiles for win-x64; self-contained single-file EXE produced | **TESTED** (PE32+ GUI x86-64, 66 MB) | — |
| XAML resource keys resolve | **TESTED** (static check script) | — |
| App launches on Windows | **NOT TESTED by me** | Build host is Linux. CI `smoke-test.ps1` launches the EXE on `windows-latest` and fails on startup/XAML errors. |
| WASAPI loopback from Chrome/Meet, device change, Bluetooth reconnect | **NOT TESTED** | Needs real Windows audio hardware. Use Pre-interview check → Audio. |
| Live OpenAI realtime transcription & live LLM quality/latency | **NOT TESTED** | No API key in the build environment. `LiveEvaluationTests` runs automatically when `OPENAI_API_KEY` is set (locally or as CI secret). |
| Always-on-top, hotkeys, multi-monitor placement, DPI | **NOT TESTED** | Needs Windows desktop; implemented with standard Win32/WPF APIs. |
| Real 60-minute session | **NOT TESTED** (simulated soak passed) | Run a 60-min YouTube interview video through the app with Diagnostics on; watch memory/CPU. |

### Manual Windows test script (15 min)
1. Pre-interview check all green. 2. Play a YouTube "product owner interview questions" video → questions appear, bullets follow. 3. Pause the video mid-question for 1 s, resume → still one question. 4. Unplug/replug the headset → status recovers. 5. Disable Wi-Fi 10 s → `RECONNECTING`, type a question → prepared answer; re-enable → `LISTENING`. 6. Press Shorter / Technical / Full. 7. Stop, close → reopen at same position.
