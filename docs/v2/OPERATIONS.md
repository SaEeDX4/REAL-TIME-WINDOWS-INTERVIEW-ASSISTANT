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
| A model is broken/deprecated | `PUT /admin/config/disabled_models ["gpt-live-transcribe"]` → clients get the next fallback via `/config` |
| Change default models | `answer_model`, `transcription_model`, `*_fallbacks` |
| Outage / maintenance | `maintenance=true` (+ `maintenance_message`) → new sessions 503, account/billing still work |
| Vulnerable client build | raise `minimum_client_version` → older clients get 426 and are told to update |
| Cost spike | lower `Safety` caps (env) and redeploy, or disable affected users |

## Routine jobs (in-process `SessionSweeper`)
Every 30 s: expire leases without heartbeat for 90 s. Hourly: purge reports past retention.

## Secrets rotation
OpenAI key, Paddle API key / webhook secret, DB password: rotate in the host secret store and restart. Ephemeral client secrets expire on their own (≤10 min).
