# 2.1.0-rc.4 — error-state audit

Question: when something fails, does the user see what failed, whether older data is still shown, and what to do —
without technical noise, and without one failure blanking unrelated information?

## Account page (the rc.3 "That took too long" report)

Root cause: the devices request reached the 30 s client timeout (`BackendException(504, "timeout")`) and its message
became the page-level status, although plan and quota had loaded.

rc.4 (`UiState/SectionState.cs`, `UiState/ErrorCopy.cs`, `HomeWindow` account code):

- Three independently loaded sections: **Plan & usage**, **Devices**, **Billing**. Each has its own state:
  not loaded, loading (shown only while there is no data), ready, failed.
- A failure shows the section's own title ("Couldn't load your devices."), the reason in plain words, a "Try again"
  button, and — if older data is still on screen — "Showing saved data from 14:05."
- Responses that arrive after a newer request started, or after sign-out/account switch, are ignored.
- A successful retry clears the error and the stale note by itself.
- Correlation ids are written to the log only; dialogs and toasts keep a short "(ref 1a2b3c4d)" for support.

### Matrix (`ErrorStateTests`, `UiFoundationTests`)

| Case | Expected | Result |
|---|---|---|
| Slow endpoint | "Loading…" only until data exists; a slow refresh keeps the data on screen | pass |
| Offline | "Cannot reach the Interview Assistant service. Check your internet connection." | pass |
| Reconnect | stale notes shown while offline, cleared by the next successful loads | pass |
| One endpoint times out | only that section fails; others stay ready; no correlation id inline | pass |
| HTTP 500 | "temporarily unavailable" — never a raw exception message | pass |
| Cancelled by the user/page | not a failure (nothing shown) | pass |
| Retry success | clears only its own section | pass |
| Section titles | distinct and not the generic timeout text, in all 10 languages | pass |

## Elsewhere

| Surface | Behaviour |
|---|---|
| Live answers | Provider errors: a safe prepared answer (Medium+ match) with a note, otherwise a specific message; transient errors (rate limit, timeout, quota) do not turn the whole status red |
| Network during a session | Debounced "Connection lost — reconnecting…" after 3 s, "Connection restored" only after a shown "lost", ≤ 1 pair per 10 s; transcription reconnects automatically (rc.3 behaviour, unchanged) |
| Profiles / Interviews / Reports | Contextual copy for save, export, delete, document reading, report open/copy/save/delete failures (e.g. "Couldn't save the profile. Check disk space and try again."), in-app dialogs, no MessageBox |
| Password reset | One neutral sentence for any address; rate limits and network errors still reported |
| Pre-interview check | Only FAIL blocks; warnings are listed as optional improvements; the audio check waits 3 s for capture and listens for 2 minutes after a "nothing heard" warning |
| Settings save | Validation failure (e.g. duplicate hotkey) keeps the page open on the failing section |

## Logging

Logs carry error codes, HTTP status, exception type and correlation id — never resume content, profile facts,
transcripts, tokens or secrets (unchanged rule; the new code follows it: section failures log code + reference only,
the engine logs verdict kinds and sizes).
