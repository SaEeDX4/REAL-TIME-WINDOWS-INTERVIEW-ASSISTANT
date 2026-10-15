# 2.1.0-rc.2 — privacy audit

## What is stored where (after RC2)

| Data | Where | Protection | Retention / deletion |
|---|---|---|---|
| Raw interview audio | **Never stored** — streamed to transcription only; the microphone is never captured | — | — |
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
| P5 | Medium | Deletion dialog promised "all data stored on the service" | Dialog states exactly what is deleted, that invoices stay with Paddle and that removing the sign-in e-mail needs support; optional wipe of this PC's copy (aabd8c9) | — | The auth identity (e-mail) is not deleted automatically — **NEEDS WORK** (backend call to the auth provider's admin API with a server-side service key). |
| P6 | Medium | Account deletion allowed with a paused subscription; partial deletion possible | Paused blocks deletion; one transaction under the user lock (cba6cb8) | `PausedSubscription_BlocksAccountDeletion_AndNothingIsDeleted` | — |
| P7 | Low | Transcription vocabulary (employers, confirmed skills) stored server-side per interview; not deleted with the interview | Disclosed here | — | Delete the server document with the interview (NEEDS WORK). |
| P8 | Info | Interviewer speech (third-party) is processed by the transcription and answer providers | `store:false` set explicitly | — | Disclose in the privacy policy (**legal text is outside this repository**). |
| P9 | Low | Workspace under `%APPDATA%` (Roaming) | Documented | — | Roams with domain profiles; consider Local. |
| P10 | Low | Desktop logs not rotated; device names logged | — | — | NEEDS WORK (rotation/retention). |
| P11 | Low | `samples/shervin-teroxx` contains a real-looking profile | Excluded from packages by the packaging guard | `ShippedAssetsTests` | Confirm consent or replace with synthetic data before public release. |
| P12 | Low | Server export lacks prep jobs and per-entry usage | — | — | Document or extend. |

## Verified

Raw audio never written; transcript off by default; no content in logs; DPAPI at rest; account-scoped workspaces by
hashed id; signed-out state shows and persists nothing; no third-party telemetry; no voiceprints; unconfirmed facts
never presented as experience (truth boundary in `ProfileService.ToEngineProfile` / preparation pipeline).
