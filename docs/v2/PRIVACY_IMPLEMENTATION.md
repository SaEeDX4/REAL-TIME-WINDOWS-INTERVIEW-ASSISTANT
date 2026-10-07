# Privacy — what the code actually does

This document describes implemented behaviour only. A public privacy policy must not promise more than this.

## Data locations
| Data | Where | Protection |
|---|---|---|
| Résumés, job descriptions, profiles, facts, stories, prepared packs | Desktop `%LOCALAPPDATA%` workspace | DPAPI (current Windows user) |
| Same documents when cloud sync is used | `user_documents` (jsonb) | Per-user isolation on every query (`UsersCannotSeeOrModifyEachOthersDocuments`); TLS in transit; DB encryption at rest is a hosting setting |
| Interview transcripts & reports | Desktop by default; reports may be synced | Reports expire after the plan's retention and are purged hourly |
| Audio | **Not stored.** Streamed to OpenAI Realtime for transcription only | — |
| Account | Supabase Auth (email/OAuth identity); our `users` table holds id, email, status | — |
| Payment data | **Paddle only.** We store Paddle customer/subscription ids and status | No card data in app or backend |
| Usage | `usage_ledger` (seconds, answer counts) — no content | — |

## Third-party processors
OpenAI (transcription + answer generation; API data is not used for training by default per OpenAI's API terms — verify current terms before publishing), Paddle (payments, tax), Supabase (authentication), hosting/DB provider.

## User rights
| Right | Endpoint / UI | Status |
|---|---|---|
| Export | `GET /api/v1/me/export` (JSON of account, subscription, devices, sessions, usage, all documents); desktop `WorkspaceStore.ExportProfile` ZIP per profile (UI button: desktop phase) | Implemented + tested |
| Delete | `DELETE /api/v1/me` deletes documents, devices, sessions, usage, prep jobs, subscription row, clears email; account id tombstoned. Requires cancelling an active subscription first. Desktop `WorkspaceStore.DeleteProfile` cascade (UI: desktop phase) | Implemented + tested |
| Delete single items | `DELETE /profiles|targets|reports/{id}` (cascades) | Implemented + tested |

Not yet implemented (do not claim): deletion of the Supabase Auth identity itself (needs the Supabase service-role admin call — external credential), automatic backup expiry statements (depends on hosting provider).

## Logging
No document contents, prompts, transcripts or tokens in logs. Admin views show counts, never content (`AdminIsRestrictedAuditedAndNeverSeesProfileContent`).

## Authorized use
The product is an interview preparation and practice coach. Users must acknowledge they will use it only where permitted. The app does not hide itself, evade screen capture or detection.
