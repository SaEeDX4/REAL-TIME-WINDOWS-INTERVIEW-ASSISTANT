# Operations runbook

## Observability
- Every response carries `X-Correlation-Id` (generated or accepted from the client). Errors return `{code, message, correlationId}`; the desktop shows the id in "Copy diagnostics".
- Logs: structured, request bodies are never logged; secrets/tokens are redacted (`Middleware.Redact`). Résumé/document content is never logged.
- Recommended: ship container stdout to the host's log service; alert on 5xx rate, `/health/ready` failures and OpenAI/Paddle error codes.

## Admin API (`/api/v1/admin/*`, restricted to `Auth:AdminUserIds`, every call audited in `audit_log`)
| Endpoint | Purpose |
|---|---|
| `GET /admin/users?email=` | Find users by exact email (latest 50 otherwise) |
| `GET /admin/users/{id}` | Account, subscription, usage, device/session/document **counts** — never document content |
| `POST /admin/users/{id}/disable` / `enable` | Abuse response; disabling ends active leases |
| `GET /admin/sessions/active` | Live sessions |
| `GET/PUT /admin/config/{key}` | Remote config (allow-listed keys only; secrets cannot be set here) |
| `GET /admin/health` | DB + config summary |

## Incident switches (no redeploy)
| Situation | Action |
|---|---|
| A model is broken/deprecated | `PUT /admin/config/disabled_models ["gpt-4o-transcribe"]` → clients get the next fallback via `/config` |
| Change default models | `answer_model`, `transcription_model`, `*_fallbacks` |
| Outage / maintenance | `maintenance=true` (+ `maintenance_message`) → new sessions 503, account/billing still work |
| Vulnerable client build | raise `minimum_client_version` → older clients get 426 and are told to update |
| Cost spike | lower `Safety` caps (env) and redeploy, or disable affected users |

## Routine jobs (in-process `SessionSweeper`)
Every 30 s: expire leases without heartbeat for 90 s (billing the tail). Every 5 min: retry queued sign-in identity
deletions. Hourly: purge reports past retention. Live relays re-check their lease every 2 s on their own.

## Alert rules (configure on the hosting/log platform)
| Signal (log message / endpoint) | Severity | Meaning / first action |
|---|---|---|
| `/health/ready` ≠ 200 for 2 min | page | Database unreachable, auth keys missing or migrations pending — check the deploy and the database. |
| 5xx rate > 2 % over 5 min | page | Look up the correlation ids in the logs. |
| "Rejected Paddle webhook" (any) | warn | Wrong `Paddle__WebhookSecret` or a forged request; compare with Paddle's delivery log. |
| "is not mapped to any plan" | page | A Paddle price is not in `PlanCatalog`; Paddle retries until it is mapped. |
| "Paddle API unreachable" / Paddle delivery failures in the Paddle dashboard | warn | Provider incident or network. |
| "Relay for session … upstream unavailable" more than 10 / 5 min | page | OpenAI realtime outage or key problem — consider `maintenance=true`. |
| "Sign-in identity deletion … failed" repeating, or admin `/health` `identityDeletionsPending` > 0 for 1 h | warn | Supabase admin API down or `Auth__ServiceRoleKey` wrong/missing. |
| "Expired {Count} stale sessions" unusually high | info | Client crashes or network trouble; check the client version mix. |

## Database migrations and rollback
* Migrations are applied at startup (`Database__MigrateOnStart`) under EF's migration lock; every migration since
  2.0 is **additive** (new tables/columns with defaults), so the previous backend version keeps working against the
  newer schema. Roll back by redeploying the previous backend image — do **not** run down-migrations in production.
* If a schema change must be undone: take a database backup first, then run
  `dotnet ef database update <PreviousMigrationName> --project src/InterviewAssistant.Backend` against the database
  from a trusted machine (each migration's `Down` drops only what it added — data in those columns/tables is lost).
* rc.3 migrations: `Rc3RealtimeRelay` (column `interview_sessions.AllowanceAtHeartbeatSeconds`),
  `Rc3IdentityDeletionAndAdjustments` (identity-deletion columns on `users`, revocation columns on `subscriptions`,
  table `billing_adjustments`) and `Rc3SelfReviewFixes` (relay connection id, trial-forfeit flag, tables
  `billing_transactions` and `deleted_account_markers`, drops the interim current-transaction columns, queues
  identity deletion for accounts deleted earlier). Applied migrations are never edited or squashed: the branch
  deploys to production automatically. Rolling the backend back to rc.2 is safe with these in place, but an
  rc.2 backend hands out client secrets again (no relay) — roll forward instead where possible.

## Secrets rotation
Order for every secret: create the new value at the provider → set it in the host secret store → redeploy/restart →
verify → revoke the old value at the provider.

| Secret | Where to create | Verify after restart |
|---|---|---|
| `OpenAI__ApiKey` | OpenAI dashboard → API keys | Start a live session (relay connects) and ask a typed question. |
| `Paddle__ApiKey` | Paddle → Developer tools → Authentication | `/api/v1/billing/plans` shows prices; open the billing portal. |
| `Paddle__WebhookSecret` | Paddle → Notifications → destination → secret | Use "Send test notification"; no "Rejected Paddle webhook" logs. Paddle signs with one secret per destination — rotate in a quiet period, deliveries signed with the old secret during the switch are retried by Paddle. |
| `Auth__ServiceRoleKey` | Supabase → Project settings → API keys (secret key) | Admin `/health`: `identityDeletionConfigured: true`, `identityDeletionsPending` drains. |
| Supabase JWT signing keys | Supabase → JWT keys (rotate with a standby key) | `/health/ready` (JWKS keys loaded); sign in from the desktop. |
| Database password | Hosting platform | `/health/ready`. |
OpenAI key, Paddle API key / webhook secret, DB password: rotate in the host secret store and restart. Ephemeral upstream secrets are minted by the relay per connection, never leave the server and expire on their own (≤10 min). `Auth__ServiceRoleKey` (Supabase secret key, used only to delete sign-in identities of deleted accounts) rotates the same way; while it is missing, identity deletions queue up (admin `/health`: `identityDeletionsPending`) and complete automatically once it is set.
