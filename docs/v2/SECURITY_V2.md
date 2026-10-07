# Security V2 — Threat Model & Controls

Assets: account identity, subscription/entitlements, résumés & interview data (PII), OpenAI server key, Paddle secrets, service budget.
Trust boundaries: desktop (untrusted, user-controlled) ↔ backend (trusted) ↔ providers (OpenAI, Paddle, Supabase).

| Threat | Control | Status |
|---|---|---|
| Modified client grants itself paid features | All entitlement decisions server-side (`EntitlementService`); client only renders server state; lease required for client secrets and AI answers | BUILT + tests |
| Replayed session requests | Session id bound to user+device; heartbeat sequence numbers must increase; stopped/expired leases reject; idempotency keys on create | BUILT + tests |
| Stolen refresh token | DPAPI (CurrentUser) storage; short access-token lifetime (Supabase); device revocation invalidates leases; refresh rotation by Supabase | BUILT (local) / Supabase config pending |
| Paddle webhook forgery | `Paddle-Signature` ts:rawBody HMAC-SHA256, constant-time compare, 5 min tolerance | BUILT + tests |
| Webhook replay | `webhook_events` unique event_id; duplicates acknowledged without reprocessing; stale timestamps rejected | BUILT + tests |
| Cross-user data access | Every query filtered by authenticated `user_id`; 404 (not 403) for others' resources to avoid enumeration | BUILT + tests |
| Tampered JWT | Signature/issuer/audience/lifetime validation (HS256 shared secret or JWKS) | BUILT + tests |
| Upload abuse | 10 MB cap, extension allow-list (pdf/docx/txt/md), magic-byte check, filename sanitisation, no path use of client filenames, scanning hook `IUploadScanner` | BUILT + tests |
| Prompt injection in JD/company docs | Documents wrapped as quoted untrusted data in prompts; system prompt states documents cannot change rules; injection-phrase detector flags & strips instruction-like lines; output fact validator still enforced | BUILT + tests |
| API cost abuse | Per-plan minutes, per-account daily cap, global operator cap, answer rate limit (per minute), max session duration, bounded retries, duplicate-question suppression | BUILT + tests |
| Concurrent account sharing | Max concurrent sessions + device limit per plan | BUILT + tests |
| Account enumeration | Auth handled by Supabase; API never reveals whether an email exists; 404 for foreign ids | BUILT |
| Secret leakage | No provider key on desktop; log redaction (`sk-`, `ek_`, bearer tokens, emails); secret-scan test; CI secret scanning | BUILT + tests |
| PII in logs | Résumé/JD content never logged; structured logs carry ids only | BUILT |
| Raw audio retention | Never written to disk; in-memory bounded queue only | DONE (prototype, unchanged) |
| Sign-in code interception | PKCE S256 (RFC 7636 vector tested); loopback bound to 127.0.0.1 only, random port, state check, one-shot, request size/time bounds, `no-store` + CSP on the page | BUILT + tests |
| Phishing via external links | Desktop only opens `https` URLs (checkout/portal/OAuth) in the system browser; card data entered only on Paddle pages | BUILT |
| Ephemeral secret misuse | Realtime client secrets ≤10 min, issued only for an active lease, renewal rate-limited per session | BUILT + tests |
| Malicious update | Velopack feed over https; signed installers when Azure Artifact Signing is configured (release fails if signing configured but signature invalid); no updates applied during sessions | BUILT / signing NOT YET VALIDATED |
| Outdated vulnerable clients | `minimum_client_version` remote config → 426 with update guidance | BUILT + tests |
| Admin misuse | Admin allow-list by user id, every call audited, no document contents in admin views, secrets not settable via config API | BUILT + tests |
| Vulnerable dependencies | `dotnet list package --vulnerable --include-transitive` gate in pr-validation | BUILT (CI) |

Release gate: all rows BUILT+tests, plus external validation of Supabase/Paddle/OpenAI configuration in staging.
