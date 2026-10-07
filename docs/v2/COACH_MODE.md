# Coach Mode

Contract (`CoachOutput`): exactly **3 keywords** joined by ` · `, **one structure** containing `→` (≤ 90 chars), optional one-line reminder. Displayed as 2 lines.

Sources, fastest first:
1. Prepared bank (same language) → instant, no network (test: first bullet ≤ 5 ms, 0 requests).
2. AI (≈60 output tokens) with a language rule; output parsed and validated against the contract (any script: Latin, Arabic/Persian, Han, Devanagari).
3. Deterministic fallback (prepared keywords or question keywords + mode structure) if the AI output violates the contract or the service is down — flagged `COACH_FORMAT` in diagnostics.

Toggle Answer/Coach live: hotkey **Ctrl+Alt+C** (engine `TogglePresentation`). Coach mode also feeds memory (keywords recorded per question).
