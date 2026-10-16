# 2.1.0-rc.4 — data integrity audit

Question: can rc.4 lose, corrupt, invent or mis-attribute user data, and can it coexist with rc.3 (installed
clients, stored files, the production service)?

## Backend and production database

- **No backend, contract or migration change** between rc.3 (`dc67e80`) and rc.4: `src/InterviewAssistant.Backend`,
  `src/InterviewAssistant.Contracts` and `railway.json` are unchanged. The deployed migrations (Rc3RealtimeRelay,
  Rc3IdentityDeletionAndAdjustments, Rc3SelfReviewFixes) are untouched; no new migration exists, so there is no
  production upgrade path to test beyond the one verified for rc.3.
- The branch auto-deploys to production; the rc.4 commits only rebuild the same backend code (Core changes affect
  desktop-only types; the backend uses Core's provider types only). rc.3 clients keep working: same API, same relay
  protocol, same minimum client version (not raised).
- rc.4 reports `ClientVersion` "2.1.0-rc.4" on device registration, as rc.3 did for itself.

## Local data (DPAPI-encrypted workspace)

| Data | rc.4 change | Compatibility |
|---|---|---|
| Session memory / question records | New optional `Heard` (acoustic provenance: ten integers and a support label) on heard questions | Additive JSON property: rc.3 records load with `Heard = null` (never re-classified); an rc.3 build reading rc.4 files ignores the unknown property (System.Text.Json default) |
| Reports | New `UncertainQuestions` list and `ReportQuestion.UncertainReason` | Additive; older saved reports render as before |
| Profiles | No schema change; counts computed differently (`FactStats.ConfirmedProfileAnswers`) | Display only |
| Settings | No new stored fields beyond rc.3 | — |

## Report contents

- Non-speech never becomes a question in the first place (engine); 0 history entries, 0 SessionMemory entries, 0
  report questions for every non-voice fixture (`RecordedStyleAudioTests`).
- Defence in depth: a heard question whose recorded evidence fails the acoustic checks is **moved** to "Possibly not
  interviewer speech (not counted)" with its live number and reason — not deleted, not counted in category counts,
  latency, preparation statistics, stories or metrics (`Rc4ReportDefenseTests`).
- Typed questions and records without evidence (rc.3 history) are never reclassified; evidence lost in a reconnect
  abstains rather than flags.
- Exact prompt echoes (the recognizer repeating its vocabulary prompt) remain excluded and counted under "not
  counted", as in rc.3.
- Variants rework their own item and add no history; typed questions stay labelled; the newest report is selected
  after Stop (unchanged behaviour, re-verified).

## User edits

- One unsaved-changes guard for profiles, interviews and Settings. It asks before leaving a page, switching item,
  importing into the open profile, closing Home or the app, or signing out — never on a language switch, resize or
  refresh, and never because controls were being built (CI-found defect, fixed: Settings had been dirty at start-up,
  which also meant stored settings were not shown on that page).
- A "Save" answer that fails validation keeps the user on the page with the problem; nothing is saved
  automatically.
- A failed profile delete no longer reloads the profile (which would have dropped unsaved edits).

## Accounts and sign-out

- Sign-out clears the account's history from the Live window (counter hidden, no "0 / 1"); if the session ends during
  an interview, Stop saves the report to that account before the history is cleared.
- Sign-in restores the user's workspace (unchanged path).
- Password reset never reveals whether an account exists.

## Profile facts

- Only confirmed facts reach answers (unchanged). Personal answers are now limited to the confirmed words (English:
  checked per bullet before display); nothing is invented when a fact is missing.
- Counts state what they count: "44 confirmed experience facts + 1 interview-profile answers".

## Tests

`Rc4ReportDefenseTests` (including JSON round trip of `HeardEvidence`), `RecordedStyleAudioTests`,
`PersonalGroundingTests`, `FactCountSemanticsTests`, `UiFoundationTests` (guard behaviour), `UiPagesTests` (counter,
sign-out history), existing report, workspace, migration and soak tests. Windows self-test: fresh Home has no unsaved
edits; DPAPI workspace round trip.
