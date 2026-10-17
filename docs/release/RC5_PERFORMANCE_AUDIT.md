# 2.1.0-rc.5 — performance audit

Question: did the rc.5 changes (pitch-run tracking, follow-up intent, the readiness model, the redesigned pages) make
the live path slower or heavier? Requirement: no extra LLM round trip.

Method: the soak and timing tests ran on rc.4 (`a0bc946`, separate worktree) and rc.5 (this branch) on the same
4-core Linux container, one after the other, with the soak classes serialised (`[Collection("Soak")]`). The soak drives
the real `InterviewEngine` through `InterviewSimulator` on a virtual clock (server-VAD timing: onset 250 ms, silence
400 ms, transcript 300 ms after the stop; the provider's first token after 700 ms), so latency numbers show the
engine's decision delays, not network time. Wall-clock numbers measure local CPU work.

## End-to-end latency (44-minute soak, 106 questions)

| Stage | rc.4 p50 / p95 / max | rc.5 p50 / p95 / max |
|---|---|---|
| VAD `speech_stopped` → question finalized | 600 / 800 / 800 ms | 600 / 800 / 800 ms |
| Finalized → first bullet, prepared answer | 0 / 0 / 0 ms | 0 / 0 / 0 ms |
| Finalized → first token, AI answer (700 ms simulated model) | 1000 / 1020 / 2480 ms | 1000 / 1020 / 2480 ms |
| Finalized → first bullet, AI answer | 1000 / 1020 / 2480 ms | 1000 / 1020 / 2480 ms |
| Finalized → complete, AI answer | 1540 / 1580 / 3000 ms | 1540 / 1580 / 3000 ms |
| AI requests for 106 questions / answers (memory, cache, model) | 72 / 105, 29, 70 | 72 / 105, 29, 70 |
| RC2 soak (beeps, panel, variants, outages, pauses): records / AI requests | 115 / 121 | 115 / 121 |

Identical: the follow-up rules add no wait and no model call. The new-turn rule for a quick follow-up does not change
the end-of-turn wait (600 ms after a complete question); it only decides *merge vs. new turn* at finalization.

## Local CPU cost

| Measurement | rc.4 | rc.5 |
|---|---|---|
| Decision pipeline per question (gate → extract → classify → match → retrieve → plan), 198 runs, p50 / p95 / max | 3.89 / 8.41 / 29.85 ms | 3.46 / 5.07 / 18.95 ms |
| Acoustic analysis per 100 ms frame | 1.3–2.3 ms | 1.3–1.7 ms with pitch-run tracking (three runs; budget 10 ms, asserted in CI for both paths) |
| Partial handler, 5-minute monologue (3,000 deltas): max / p50 / p99 | 8.40 / 0.302 / 0.689 ms | 6.79 / 0.284 / 0.571 ms |
| Live text updates over 300 s | 302 | 302 |

Pipeline differences are within run-to-run noise of a shared container (the rc.5 run happened to be quieter). The
follow-up classifier is one compiled regex per sentence; the run tracker reuses the period the analyser already
computes per window and keeps five numbers of state.

## Memory (managed heap after a full collection)

| | rc.4 | rc.5 |
|---|---|---|
| 44-minute soak: start → end | 9,488 → 11,318 KB (+1,830) | 10,938 → 12,767 KB (+1,829) |
| following RC2 soak: start → end | 11,008 → 11,182 KB (+174) | 12,457 → 12,602 KB (+145) |
| long-context monologue delta | 580 KB | 580 KB |

Per-session growth is the same; rc.5 starts ≈ 1.4 MB higher because the test process loads more fixtures and test
classes (the 14 new audio fixtures, readiness tests). `HeardEvidence` gains three integers per heard question.

## UI

The readiness store publishes immutable snapshots; surfaces re-render only on `Changed` (one event per check result,
at most 8 per run plus the begin). The pre-interview checks run asynchronously and are cancelled when the page is
left. WPF rendering cost of the redesigned pages on low-end hardware and at 4K / 200 % is a manual check
(RC5_MANUAL_REGRESSION).

## Budget checks in CI

`Analysis_IsCheapEnoughForTheCaptureThread` (< 10 ms per frame, with and without run tracking),
`LocalDecisionPipeline_…_IsWellUnderTheLatencyBudget` (p95 < 25 ms), `Rc2EngineTimingTests` (partial handler max
< 50 ms, p99 < 10 ms), `Engine_SpeechFixture_ShowsLiveWordsBeforeTheTurnEnds` (no visible lag from the trust boundary).
