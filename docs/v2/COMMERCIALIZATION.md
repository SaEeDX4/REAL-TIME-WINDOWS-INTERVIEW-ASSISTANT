# Commercialization readiness

## What is ready in code
- Multi-user backend: auth, per-user isolation, entitlements, metering, leases, safety caps, admin, export/delete — tested against PostgreSQL.
- Paddle MoR integration: checkout, portal, verified/idempotent/ordered webhooks, grace period.
- Server-side OpenAI key; ephemeral Realtime secrets; answer streaming proxy; remote model routing + kill switch; minimum client version.
- CI: PR validation (tests, migration drift, vulnerable packages, secret scan, container build), Windows release, backend deploy with approval gate.

## External accounts and decisions required (the remaining boundary)
| Item | Owner action | Needed for |
|---|---|---|
| Paddle account (sandbox → live), products & prices, webhook destination, seller verification | Create in Paddle dashboard | Checkout |
| Supabase project, auth providers, redirect URL `http://127.0.0.1:<port>/callback` | Create project | Login |
| OpenAI organization/project key with monthly spend limit | Create, store in host secret store | All AI |
| Hosting (container + managed PostgreSQL), domain, TLS | Choose provider | Backend |
| Code-signing: Azure Artifact Signing account + identity validation | Azure portal | Signed installer (SmartScreen) |
| Legal: Terms, Privacy Policy (based on PRIVACY_IMPLEMENTATION.md), refund policy | Counsel | Paddle approval requires these |
| Pricing | Business | Plan catalog values |

## Pricing guidance (not decided)
Unit cost drivers: Realtime transcription per minute and answer tokens per question. Use the desktop's live cost estimate and `usage_ledger` to calibrate. Keep a trial small enough that abuse is bounded by the global daily caps.

## Product positioning constraints
Interview preparation and practice coach with a live assist mode for authorized use. No stealth/undetectability claims, no hiring-outcome predictions.
