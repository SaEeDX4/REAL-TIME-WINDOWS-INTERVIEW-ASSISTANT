# Production hardening plan (audit → work streams)

Context: Railway auto-deploys `claude/clever-hopper-m8kdai`; every push must start cleanly with the variables already set in Railway.

## Audit findings (blocking)
| # | Area | Finding | Fix |
|---|---|---|---|
| 1 | Plans | `Trial (development seed)`, `pro-dev` served by `/billing/plans` | Production catalog `trial` / `pro`; legacy `pro-dev` alias + data migration |
| 2 | OpenAI | Default transcription model `gpt-live-transcribe` is not an OpenAI model id → every session start fails at secret minting | `gpt-4o-transcribe` primary, `gpt-4o-mini-transcribe` fallback; server retries fallbacks; server-configurable |
| 3 | Billing | Webhook event row committed before processing → a processing failure turns Paddle's retry into a "duplicate" (lost payment) | Event insert + state change in one transaction |
| 4 | Billing | Non-string JSON fields throw → 500; unknown price silently grants trial | Safe field access → 400/ignored; unknown price logged as error |
| 5 | Billing | Paddle `checkout.url` requires a default payment link page that loads Paddle.js | Backend serves `/pay` (Paddle.js + client-side token from config) |
| 6 | Billing | Checkout allowed while already subscribed (double subscription) | 409 `already_subscribed` → portal |
| 7 | Metering | Start/heartbeat not serialised per user → concurrent-limit bypass / double-billed heartbeat | Per-user Postgres advisory lock in a transaction |
| 8 | Metering | Crashed app's lease blocks restart for 90 s | Starting on the same device ends that device's stale lease |
| 9 | Infra | No forwarded-header handling behind Railway → all clients share one rate-limit bucket; no HSTS | ForwardedHeaders (XFF/proto, 1 hop) |
| 10 | Infra | No request body limit; config not validated at startup | Kestrel body limit; validated options, production fail-fast for required secrets |
| 11 | Entitlements | `AdvancedReports`, `CustomAnswerModes`, `ModelTier`, `MaxLanguages` never enforced (false claims); prep quota not consumed by desktop preparation | Remove unenforced flags; desktop preparation consumes a server prep job; retention applied locally from plan |
| 12 | Account | Billing state derived in UI | Server computes `billingState` (trial, trial_exhausted, active, canceling, past_due, paused, expired) |
| 13 | Auth | Desktop needs Supabase URL + publishable key; no email/password sign-up/sign-in; no password reset | Public client config from `/api/v1/config` (`Auth__PublishableKey` in Railway); email/password sign-up, sign-in, reset (PKCE), optional OAuth |
| 14 | Desktop | Release builds expose own-API-key developer mode, model pickers, price fields | Developer features compiled only into Debug/`DeveloperBuild` builds |
| 15 | Desktop | Ephemeral secret reused across reconnects; desktop resends session config | Fresh secret per connection; server-preconfigured session (no client session.update) |
| 16 | Desktop | Sleep/wake ends lease → user must press Resume | Automatic lease restart while the user is listening |
| 17 | Updates | Auto-update would apply unsigned packages | Updates only for Authenticode-signed builds, downloaded package signer must match; CI update hook compiled only into test builds |
| 18 | CI | No verification of what Railway deployed | Post-push production smoke workflow (health, ready, plans naming) |
| 19 | Self-test | Would depend on production when cloud is configured | Hermetic `--offline-cloud` mode exercising the service-unavailable UI |

## Work streams (each committed separately, tests green before push)
1. Backend: plans, config/options, forwarded headers, limits, webhook atomicity, metering locks, models, billing state, checkout page, public client config, logging.
2. Client library: email auth, server-provided auth config, async per-connection secrets, checkout URL validation.
3. Desktop: account UI states, upgrade/manage billing flows, developer-mode gating, readiness, offline/service-unavailable, auto lease recovery, prep metering, retention.
4. Updater hardening.
5. CI: production smoke; Windows + Linux gates.
6. Tests for every listed scenario.
7. Docs: manual E2E checklist, release-readiness report, Railway deployment/runbook.
