# 2.1.0-rc.2 / rc.3 / rc.4 — privacy audit

## What is stored where (after RC2)

| Data | Where | Protection | Retention / deletion |
|---|---|---|---|
| Raw interview audio | **Never stored** — since rc.3 streamed through the service's relay to transcription (held in memory per message only, never written or logged); the microphone is never captured | TLS on both legs | — |
| Candidate profiles, résumé text, facts, personal interview-profile answers | Desktop: `%APPDATA%\InterviewAssistant\workspace\accounts\<hash of account id>` | DPAPI (Windows user) | Until the user deletes them; optional wipe after account deletion |
| Interviews, prepared answers, reports | Same account workspace | DPAPI | Report retention per plan; delete per item |
| Report viewer copies (HTML, for the browser) | `%LOCALAPPDATA%\InterviewAssistant\view\<account hash>` | Windows user folder, not synced | Removed when the account's workspace closes/opens and when the report is deleted |
| Interview transcript | Only if the user enables "Save the interview transcript to Documents…" (off by default) | Plain Markdown in Documents (user's explicit choice) | User-managed |
| Service account | Backend Postgres: account row, devices (random installation id, hashed), usage ledger (seconds, counts), sessions (timestamps), documents the app uploads (transcription vocabulary per interview), subscription state | Database access controls; no content in logs | Deleted on account deletion (see below) |
| Answer requests | Sent per request via the backend to the model provider (confirmed facts, personal answers, earlier questions in the session, the interviewer's question) | TLS; `store:false`; nothing persisted by the backend | Not stored by the backend |
| Logs | Desktop `%LOCALAPPDATA%\InterviewAssistant\logs`; backend platform logs | No résumé, profile, transcript, token or request-body content | Desktop logs are not rotated (finding P10) |
| Speaker data | **None** — no embeddings, voiceprints or biometrics | — | — |
| Telemetry | **None** (no third-party analytics or crash reporting) | — | — |

## Findings and status

| # | Sev | Finding | Fix | Test | Remaining risk |
|---|---|---|---|---|---|
| P1 | **High** | Every session wrote a plain HTML report (candidate name, questions, suggestions) to the shared, often cloud-synced Documents folder; deletions never removed it | Viewer copies only in the account's private local folder, cleaned up (888c56a, aabd8c9) | self-test | — |
| P2 | Medium | Opt-in transcript in Documents, overwritten within the same minute | Unique per session; kept as the user's explicit choice | — | Plain text by design; disclosed in Settings. |
| P3 | Medium | A different account signing in during an interview kept the previous account's knowledge on screen | The interview is stopped (report saved to the first account) before switching (888c56a) | — | — |
| P4 | Medium | Pre-RC2 shared workspace adopted by whichever account signs in first | Unchanged; marker prevents a second adoption | `AccountWorkspaceIsolationTests` | Pre-RC2 data was already visible to every account of that Windows user; a confirmation dialog would be better (NEEDS WORK, low). |
| P5 | Medium | Deletion dialog promised "all data stored on the service"; the sign-in identity was not deleted | Dialog states exactly what is deleted; **rc.3:** the sign-in identity (e-mail, password, linked OAuth identities) is deleted through the Supabase admin API after the data deletion commits, queued and retried automatically on failure; optional wipe of this PC's copy (aabd8c9, 0aaabc4) | `Rc3AccountAndRefundTests` (4 identity tests) | Requires `Auth__ServiceRoleKey` in production (EXTERNAL); invoices and customer records stay with Paddle (merchant of record, legal retention). |
| P6 | Medium | Account deletion allowed with a paused subscription; partial deletion possible | Paused blocks deletion; one transaction under the user lock (cba6cb8) | `PausedSubscription_BlocksAccountDeletion_AndNothingIsDeleted` | — |
| P7 | Low | Transcription vocabulary (employers, confirmed skills) stored server-side per interview; not deleted with the interview | **rc.3:** deleting an interview or a profile deletes the server copy (acca68f) | — | Best-effort: if offline or signed out at that moment, it stays until the account is deleted. |
| P8 | Info | Interviewer speech (third-party) is processed by the transcription and answer providers | `store:false` set explicitly | — | Disclose in the privacy policy (**legal text is outside this repository**). |
| P9 | Low | Workspace under `%APPDATA%` (Roaming) | Documented | — | Roams with domain profiles; consider Local. |
| P10 | Low | Desktop logs not rotated; device names logged | **rc.3:** daily log files older than 14 days are deleted at startup (acca68f) | — | Device names remain in logs (local only). |
| P11 | Low | `samples/shervin-teroxx` contains a real-looking profile | Excluded from packages by the packaging guard | `ShippedAssetsTests` | Confirm consent or replace with synthetic data before public release. |
| P12 | Low | Server export lacks prep jobs and per-entry usage | — | — | Document or extend. |

## Verified

Raw audio never written; transcript off by default; no content in logs; DPAPI at rest; account-scoped workspaces by
hashed id; signed-out state shows and persists nothing; no third-party telemetry; no voiceprints; unconfirmed facts
never presented as experience (truth boundary in `ProfileService.ToEngineProfile` / preparation pipeline).

## rc.3 additions

* **Relay:** interview audio and transcripts now pass through the backend. The relay keeps nothing: messages are held
  in memory only while being forwarded, and the relay logs only counts (messages forwarded/dropped) and the close
  reason — never audio, transcript text or tokens. The privacy policy must name the service as a processor of
  interviewer audio in transit (legal text is outside this repository).
* **Refund/chargeback records** (`billing_adjustments`) store Paddle ids, action, type and status only (no amounts,
  no personal data).
* **Account deletion** now also removes the sign-in identity; the account row keeps only the tombstone (id,
  timestamps, identity-deletion status) needed to stop the old token from recreating the account.
* **Trial-abuse marker:** after a deletion the service keeps a SHA-256 hash of the normalised e-mail address (no
  other data, not linked to the deleted account id) for 365 days, so deleting and signing up again with the same
  address gives no second free trial. This is pseudonymous personal data kept for fraud prevention (legitimate
  interest) and must be disclosed in the privacy policy (legal text outside this repository).
* **Payment records** (`billing_transactions`) keep Paddle transaction/subscription ids, origin, billing period and
  a paid/unpaid flag — no amounts, card or address data. They are retained for refund/chargeback handling.

## rc.4

Re-reviewed for 2.1.0-rc.4: no new data leaves the PC, no new server-side storage, no new third party.

- **Acoustic provenance per heard question** (`HeardEvidence`): frame counts, durations in milliseconds, pitch-pair
  counts and the support label. It is not audio, cannot reproduce speech and contains no voice characteristics
  usable to identify a person (no spectra, no pitch values, no embeddings). It stays in the local DPAPI workspace
  with the session and the report. **No biometric voiceprints** are computed or stored; panel handling relies on
  names in the text, not on speaker recognition.
- Logs: the engine logs verdict kinds, reasons and sizes (never transcript text); account errors log codes and
  correlation ids (never request bodies, tokens or profile content).
- Password reset no longer reveals whether an e-mail address has an account.
