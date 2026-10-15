# 2.1.0-rc.2 — performance audit

Measurements come from the engine simulations (virtual clock, deterministic) and wall-clock micro-benchmarks run in
the test suite on Linux and on the Windows CI runner. WPF rendering, startup and idle CPU on real consumer hardware
are covered only by the Windows smoke test (startup time, working set) and are listed as open below.

## Latency — before (RC1, 5ce6b95) and after (RC2), same 106-question simulation

| Path | RC1 p50 / p95 / max | RC2 p50 / p95 / max | Change |
|---|---|---|---|
| `speech_stopped` event → question finalized (+400 ms server VAD) | 600 / 800 / 800 ms | 600 / 800 / 800 ms | none |
| Prepared answer: finalized → first bullet | 0 / 0 / 0 ms | 0 / 0 / 0 ms | none (still immediate) |
| Generated answer (700 ms simulated first token): finalized → first bullet | 1 020 / 1 020 / 2 480 ms | 1 020 / 1 020 / 2 460 ms | none |
| Generated answer: finalized → complete | 1 580 / 1 580 / 3 040 ms | 1 560 / 1 580 / 3 000 ms | none |
| New local layers per question (gate + extract + classify + match + retrieve + plan), wall clock, 198 runs | — | p50 4.7–5.0 ms · p95 7.8–10.2 ms · max 20–48 ms | +≈5 ms vs ≈1 000 ms first token |
| Acoustic analysis per 100 ms audio frame | — | well below 1 ms (5-minute monologue: max handler 1.3–4.2 ms incl. turn handling) | — |

Design constraints honoured: no second model call per answer (planner and quality gate are local); no added waiting
after turns (end-of-question timing unchanged); the prepared/high-confidence path stays immediate.

## Long sessions and memory

| Scenario | Result |
|---|---|
| RC2 soak — 61.7 virtual minutes, 112 heard + 3 typed questions, 33 beeps/shutters, 10 panel remarks, 11 variants, AI outage, reconnect, 3 pause/resume cycles | 115 records = 115 real questions, 0 noise leaked; managed memory +290 KB (7 660 → 7 950 KB) |
| Long soak — 43.7 virtual minutes, 106 questions (26 multi-part, 30 acknowledgements, reconnect, provider failures) | 0 duplicates; +219 KB |
| 5-minute monologue | max partial handler 1.3–4.2 ms; 252 live-text UI updates in 300 s (throttled to ≤ 1 per 120 ms) |
| 10 minutes of fragmented speech | memory +398 KB; turn text bounded (8 000 chars / 400 segments) |
| History / metrics | capped (500 history items, 200 latency samples) |

## Findings

| # | Sev | Finding | Fix | Evidence | Remaining risk |
|---|---|---|---|---|---|
| PF1 | High | Long speech: O(n²) segment re-joins, unbounded partials, one UI update per partial | Cached text, bounded buffers, 120 ms throttle, tail-only live text | monologue/fragment tests | — |
| PF2 | Medium | Answer stream could hang indefinitely after the first token (connection held) | 20 s idle / 90 s total cap | `StalledStreamAfterTheFirstToken_TimesOutInsteadOfHanging` | — |
| PF3 | Low | Local decision layers add work on every turn | Measured ≈ 5 ms p50; regexes compiled once | latency test (budget p95 < 25 ms) | CI runner noise produces occasional 20–48 ms maxima. |
| PF4 | Low | HttpClient usage | Long-lived clients with pooled handlers (`PooledConnectionLifetime` 10 min); the key probe disposes its client | code review | — |
| PF5 | Low | Timers / cancellation sources | View-model timers owned and stopped on dispose; per-operation cancellation sources are disposed or replaced on the UI thread | code review | See self-review notes in the final report. |
| PF6 | Info | Backend heartbeat runs three SUM queries incl. a global daily SUM | Unchanged | backend audit | Add a daily aggregate table when traffic grows. |

## Not measured (open)

* Cold start, idle CPU, UI frame times and language-switch time on real consumer PCs (CI smoke test records startup
  time ≈ 1.1 s to first window and ≈ 240 MB working set on the Windows runner, self-contained single-file build).
* Real-network latency (the provider's first-token time dominates generated answers).
