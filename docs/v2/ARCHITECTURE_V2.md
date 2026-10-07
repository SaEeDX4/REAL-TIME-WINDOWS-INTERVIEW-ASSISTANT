# Architecture V2 — Commercial

Status legend: **DONE** = acceptance tests pass · **BUILT** = code exists, tested locally/with mocks, external validation pending · **PLANNED**.

## Baseline (protected)
- Prototype validated at commit `013fbc6` (Windows CI run #4 green, 88 tests). Local annotated tag `prototype-v1-shervin` (the git proxy for this environment refuses tag pushes; push it with `git push origin prototype-v1-shervin` from a normal clone).
- Development continues on `claude/clever-hopper-m8kdai` (the only branch this automation may push). Rename/branch to `commercial-v2` when merging is convenient.

## Runtime
.NET 10 LTS (supported to Nov 2028) for desktop, backend and tests. Migration from .NET 8: target framework change only; all 88 baseline tests green.

## Solution layout
```
src/InterviewAssistant.Core        engine (audio, turn detection, matching, memory, coach, reports, languages,
                                   profiles/targets/ingestion/preparation) — cross-platform, no UI
src/InterviewAssistant.Contracts   DTOs shared by desktop and backend (/api/v1)
src/InterviewAssistant.Backend     ASP.NET Core API: auth (Supabase JWT), Postgres (EF Core), entitlements,
                                   session leases, usage ledger, OpenAI proxy + realtime client secrets, Paddle
src/InterviewAssistant.App         WPF desktop (WASAPI loopback, overlay, onboarding, dashboard, billing)
tests/*                            unit, engine, backend integration, security, soak
```

## Key flows
**Sign-in**: desktop opens system browser → Supabase `/auth/v1/authorize` with PKCE (S256) + state → loopback `http://127.0.0.1:<port>/callback` → code exchange → access/refresh tokens stored with DPAPI. Backend validates Supabase JWT (issuer, audience, signature, lifetime).

**Live session lease**: `POST /api/v1/sessions` → entitlement + concurrency + device checks → server session id + OpenAI realtime **client secret** (`POST /v1/realtime/client_secrets`, short-lived `ek_…`) → desktop connects to OpenAI realtime directly with the ephemeral secret (lowest latency) → `POST /sessions/{id}/heartbeat` every 30 s meters seconds and renews the secret only while entitled → `POST /sessions/{id}/stop` or 90 s heartbeat timeout releases the lease.

**Answers**: desktop matches the selected target's local prepared bank first (no network). Otherwise `POST /api/v1/answers/stream` (SSE) → backend checks the lease + rate limits → OpenAI with the server key → streams deltas back. The permanent OpenAI key exists only on the server.

**Preparation**: résumé/JD/materials are parsed locally (PDF via PdfPig, DOCX via OpenXML zip parsing, TXT) → structured facts with provenance → user confirms facts → `PreparationPipeline` builds a per-target pack (match map, gaps, terminology, stories, 50–150 question bank, coach keywords, truth policy, questions to ask) with a deterministic local generator, optionally enriched by the backend LLM endpoint.

**Billing**: `POST /api/v1/billing/checkout` → Paddle transaction → hosted checkout URL opened in browser. Paddle webhooks (`ts:rawBody` HMAC-SHA256) → idempotent event store → subscription + entitlements updated. Customer portal via Paddle portal sessions. Server entitlements are authoritative.

## Developer mode (prototype path preserved)
The validated own-API-key path remains available under *Settings → Advanced → Developer mode* for internal testing and as a regression baseline. Commercial builds default to Cloud mode; customers never need an API key.

## Offline / degraded policy
App always launches. With a prepared target and a cached entitlement less than 72 h old, prepared answers and manual questions keep working offline; AI generation and live transcription require the backend. Service state is shown, never faked.
