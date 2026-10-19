# 2.1.0-rc.5 — performance audit

Question: did the rc.5 changes (pitch-run tracking, follow-up intent, the readiness model, the redesigned pages) make
the live path slower or heavier? Requirement: no extra LLM round trip.

This document was revised after an independent engine audit. That audit could not reproduce the first version's
"identical" latency figures to the millisecond, measured a higher analyser cost than stated, and counted the run
tracker's state wrongly. The numbers below were re-measured after the audit fixes (short held-tone fallback,
follow-up grammar constraints, split timing, voiced-stretch counting).

Method: the soak and timing tests drive the real `InterviewEngine` through `InterviewSimulator` on a virtual clock
(server-VAD timing: onset 250 ms, silence 400 ms, transcript 300 ms after the stop; the provider's first token after
700 ms), so latency numbers show the engine's decision delays, not network time. The virtual clock advances in 20 ms
steps while the background answer tasks run in real time, so **the latency figures are virtual-clock results that
vary by about 20 ms from run to run** (one tick); question, answer and request counts are deterministic. Wall-clock
numbers measure local CPU work on a shared 4-core Linux container and vary with load. The rc.4 column is the
`a0bc946` build as measured for the first version of this document and by the independent audit.

## End-to-end latency (44-minute soak, 106 questions)

| Stage | rc.4 p50 / p95 / max | rc.5 p50 / p95 / max (this branch, 2 runs) |
|---|---|---|
| VAD `speech_stopped` → question finalized | 600 / 800 / 800 ms | 600 / 800 / 800 ms |
| Finalized → first bullet, prepared answer | 0 / 0 / 0 ms | 0 / 0 / 0 ms |
| Finalized → first token, AI answer (700 ms simulated model) | 1000 / 1020 / 2460–2480 ms | 1000 / 1020 / 2480 ms |
| Finalized → first bullet, AI answer | 1000 / 1020 / 2460–2480 ms | 1000 / 1020 / 2480 ms |
| Finalized → complete, AI answer | 1540–1560 / 1580 / 2980–3000 ms | 1540–1560 / 1580 / 3020 ms |
| AI requests / records (memory, cache, model) for 106 questions | 72 / 105, 29, 70 | 72 / 105, 29, 70 |
| RC2 soak (beeps, panel, variants, outages, pauses): records / AI requests | 115 / 121 | 115 / 121 |

Equivalent within the one-tick (20 ms) resolution of the virtual clock; the counts reproduce exactly. The follow-up
rules add no wait and no model call per question: the end-of-turn wait after a complete question is unchanged
(600 ms), and the split rule only decides *merge vs. new turn* at finalization.

Extra answers are a separate matter. A follow-up that becomes its own turn gets its own answer and therefore its own
model request (or prepared answer) — that is the point of the change. Since the audit fixes, this happens only after
the 1.8 s reply allowance, so a quick multi-part question ("…? Give me an example." 1.2 s later) is one question and
one answer again, as in rc.4. Instructions that the first rc.5 build answered ("Continue to the next slide.", "Show me
your screen.") no longer cause a request.

## Local CPU cost

| Measurement | rc.4 | rc.5 |
|---|---|---|
| Decision pipeline per question (gate → extract → classify → match → retrieve → plan), 198 runs, p50 / p95 / max | 3.89 / 8.41 / 29.85 ms | 3.90–4.01 / 6.35–7.17 / 56–62 ms (run alone, two runs) |
| Acoustic analysis per 100 ms frame, without / with pitch-run and stretch tracking | 1.3–2.3 ms (no tracking) | 2.0–2.3 / 2.5–2.6 ms (run alone, two runs); 4.0 / 5.7 ms with the soak running in parallel |
| Partial handler, 5-minute monologue (3,000 deltas): max / p50 / p99 | 8.40 / 0.302 / 0.689 ms | 6.34 / 0.332 / 0.703 ms |
| Live text updates over 300 s | 302 | 302 |

The analyser figures are environment-dependent: the audit measured 2.61 / 2.73 ms, the first version of this document
1.3–1.7 ms. All are well under the 10 ms budget asserted in CI for both paths; run tracking adds about 0.2–0.5 ms per
frame. The pipeline maximum is a single outlier on a shared container (the p95 stays under the 25 ms CI budget). The
follow-up classifier is a few compiled regular expressions per sentence; the run tracker reuses the period the
analyser already computes per window and keeps nine fields of state (last pitch, run minimum and maximum, run length,
stretch length, and the tracked / run / flat / stretch counters since the last frame) behind a private lock.

## Memory (managed heap after a full collection)

| | rc.4 | rc.5 |
|---|---|---|
| 44-minute soak: start → end | 9,488 → 11,318 KB (+1,830) | 16,476 → 16,830 KB (+354) and 13,228 → 16,834 KB (+3,606) in two runs |
| following RC2 soak: start → end | 11,008 → 11,182 KB (+174) | 16,518 → 16,817 KB (+299), 16,589 → 16,770 KB (+181) |
| long-context monologue delta | 580 KB | 578 KB |

Heap figures depend on what the test process loaded before the soak (more test classes and fixtures are loaded on
this branch, and the start value depends on test ordering), so only the bounded growth is comparable: the soak keeps a
bounded history and the long-context delta is unchanged. `HeardEvidence` gains four integers per heard question
(three run counts and the voiced-stretch count).

## UI

The readiness store publishes immutable snapshots; surfaces re-render only on `Changed` (one event per check result,
at most 8 per run plus the begin). The pre-interview checks run asynchronously and are cancelled when the page is
left. WPF rendering cost of the redesigned pages on low-end hardware and at 4K / 200 % is a manual check
(RC5_MANUAL_REGRESSION).

## Budget checks in CI

`Analysis_IsCheapEnoughForTheCaptureThread` (< 10 ms per frame, with and without run tracking),
`LocalDecisionPipeline_…_IsWellUnderTheLatencyBudget` (p95 < 25 ms), `Rc2EngineTimingTests` (partial handler max
< 50 ms, p99 < 10 ms), `Engine_SpeechFixture_ShowsLiveWordsBeforeTheTurnEnds` (live words before the turn ends for
the speech fixture it plays). Live text for one- and two-word imperative follow-ups now appears when their segment is
final rather than mid-sound (see RC5_ENGINE_AUDIT, section 4).
