# 2.1.0-rc.4 — performance audit

Question: did the rc.4 safety work (acoustic voice-likeness, plausibility, partial trust boundary, report
re-validation, personal grounding) make the live path slower? Requirement: no extra LLM round trips; measure
question end → turn final → first bullet → complete.

Method: the same tests run on rc.3 (`dc67e80`, separate worktree) and rc.4 (this branch) on the same 4-core Linux CI
container, one after the other, test classes run serially (`[Collection("Soak")]`). The soak tests drive the real
`InterviewEngine` through `InterviewSimulator` on a virtual clock with realistic server-VAD timing (onset 250 ms,
silence window 400 ms, transcript 300 ms after the stop) and a provider that streams its first token after 700 ms;
virtual-clock numbers therefore show the engine's *decision* delays, not network time. Wall-clock numbers measure
local CPU work.

## End-to-end latency (44-minute soak, 106 questions: 26 multi-part, 30 acknowledgements, a reconnect, provider errors)

| Stage | rc.3 p50 / p95 / max | rc.4 p50 / p95 / max |
|---|---|---|
| VAD `speech_stopped` → question finalized | 600 / 800 / 800 ms | 600 / 800 / 800 ms |
| Finalized → first bullet, prepared answer | 0 / 0 / 0 ms | 0 / 0 / 0 ms |
| Finalized → first token, AI answer (700 ms simulated model) | 1000 / 1020 / 2460 ms | 1000 / 1020 / 2460 ms |
| Finalized → first bullet, AI answer | 1000 / 1020 / 2460 ms | 1000 / 1020 / 2480 ms |
| Finalized → complete, AI answer | 1540 / 1580 / 2980 ms | 1560 / 1580 / 3020 ms |
| AI requests for 106 questions | 72 | 72 |

The question-end → finalized delay is the adaptive end-of-turn wait (600 ms after a question mark, longer after an
incomplete sentence) and is unchanged. rc.4 adds **no** model call: the gate, the plausibility check, the grounding
check and the report re-validation are local. The +20–40 ms at the maximum/complete end is the per-bullet grounding
and validation work and is within run-to-run noise of the simulated stream. For real use add the 400 ms VAD silence
window (silence → `speech_stopped`) and the network/model time of the relay.

## Local CPU cost

| Measurement | rc.3 | rc.4 |
|---|---|---|
| Decision pipeline per question (gate → extract → classify → match → retrieve → plan), 198 runs, p50 / p95 / max | 5.90 / 10.09 / 22.85 ms | 5.48 / 8.08 / 13.56 ms |
| Acoustic analysis per 100 ms capture frame (now including pitch pairs) | budget < 10 ms | 2.7 ms |
| Partial-transcript handler during a 5-minute monologue (3,000 deltas, now including the trust check), max | 26.25 ms (30 s of virtual audio) | 6.37 ms; p50 0.45 ms, p99 2.0 ms (300 s) |
| Live text updates delivered over 300 s | — | 302 (≈1 per second, throttled) |

The partial handler got cheaper even though it now checks trust: the live text is built only up to the length the
UI shows (`TurnDetector.DisplayTailChars`), so a minute-long partial no longer allocates the whole transcript on
every delta. The trust check re-runs on every delta only while the partial is short (≤240 characters), then every
40 characters. Pitch pairs reuse the period the analyser already computes per hop; no extra correlation search.

## Memory (managed heap after a full collection, soak tests alone)

| | rc.3 | rc.4 |
|---|---|---|
| 44-minute soak: start → end | 6,308 → 8,850 KB (+2,542) | 7,531 → 10,257 KB (+2,726) |
| following 62-minute soak: start → end | 8,592 → 8,939 KB (+347) | 9,934 → 10,349 KB (+415) |

Growth per session is the same order in both (history, memory and metrics of 105 questions, bounded). rc.4 keeps
≈1.4 MB more after the first session that does not grow further (static tables and caches of the new checks); the
per-question provenance record (`HeardEvidence`, ten integers) is negligible. No leak trend over 106 minutes.

## Latency budget checks in CI

- `LocalDecisionPipeline_…_IsWellUnderTheLatencyBudget`: p95 < 25 ms.
- `Analysis_IsCheapEnoughForTheCaptureThread`: < 10 ms per 100 ms frame.
- `Rc2EngineTimingTests.FiveMinuteMonologue_…`: partial handler max < 50 ms, p99 < 10 ms, ≤ 5 full GCs during 3,000
  deltas (a delta during which a blocking full GC ran is reported and not charged to the handler). This test now runs
  outside the parallel test pool: under full parallel load its wall-clock numbers measured the other test classes,
  not the engine.
- `RecordedStyleAudioTests.Engine_SpeechFixture_ShowsLiveWordsBeforeTheTurnEnds`: real speech shows its first live
  words 560–740 ms after it starts (before the speaker finishes), i.e. the trust boundary adds no visible lag.

## Not measured here

Real network and model latency through the relay (depends on region and load), WPF rendering cost of the new
dialogs and Settings page on low-end hardware (manual check W in RC4_MANUAL_REGRESSION), and real Windows audio
capture jitter.
