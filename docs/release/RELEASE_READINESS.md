# Release readiness — paid Windows SaaS (Paddle sandbox)

Date: 2026-10-13 · Branch: `claude/clever-hopper-m8kdai` · Backend: Railway production service (sandbox billing)

## Verdict
**Ready for the sandbox end-to-end test** once the three Railway variables in "Blocking configuration" are set.
**Not ready for a live (paid, public) launch** until the items under "Before live launch" are done.

## Verified
| Area | Evidence |
|---|---|
| Backend deploy | `production-smoke` green on the deployed commit: `/health` reports the pushed commit, `/health/ready` = `ready` (DB + JWKS keys + no pending migrations) |
| Plan catalog | `/api/v1/billing/plans` → `trial` "Trial", `pro` "Pro" with live Paddle price "USD 19.99 / month"; no seed/dev naming (checked by the smoke workflow) |
| Public config | `/api/v1/config` contains no secret-shaped values (checked by the smoke workflow on every deploy) |
| Auth boundary | `/me` without token, with a forged token, unsigned webhook, `/admin` → 401 in production |
| Security headers | `nosniff`, `X-Frame-Options: DENY`, HSTS, `no-store`, strict CSP on API responses |
| Backend tests | 78 integration tests on real PostgreSQL + real migrations: JWKS/RS256 auth, every Paddle event → state, idempotent/out-of-order webhooks, wrong-typed payloads, checkout custom data from the authenticated user only, already-subscribed, portal, metering races (advisory lock), same-device lease replacement, trial exhaustion, transcription model fallback, config validation |
| Client/core tests | 215 tests: auth (sign-up, password sign-in, reset via PKCE loopback, readable errors, expiry), per-connection realtime secrets, server-configured transcription, engine, answer evaluation, soak |
| Windows build | `windows-release`: warnings-as-errors build, tests, publish, real-EXE smoke, in-app self-test (service-only customer build, offline/signed-out states, no secrets in `cloud.json`, unsigned build never self-updates), Velopack installer, N→N+1 update test |
| Secrets | Windows binary/config ship only the API base URL. OpenAI, Paddle API key, webhook secret and DB credentials exist only in Railway. Developer own-key mode is compiled only into Debug/`DeveloperBuild` builds |
| Microphone | Never opened; capture is WASAPI loopback of the playback device only |

## Blocking configuration (sandbox E2E) — needs the account owner
1. Railway `Auth__PublishableKey` (Supabase publishable key) — without it the app cannot sign in (smoke warns).
2. Railway `Paddle__ClientToken` (sandbox `test_…` client-side token) and Paddle default payment link = `<backend>/pay` — without them checkout cannot open.
3. Railway `Product__PublicBaseUrl` + Supabase redirect URLs (`http://127.0.0.1:*/**`, `<backend>/auth/confirmed`) — e-mail confirmation and password reset.

## Before live launch (not code)
| Item | Why |
|---|---|
| Code signing (Azure Artifact Signing or an OV/EV certificate) → set the `AZURE_*` secrets | Unsigned installer triggers SmartScreen; auto-update stays **disabled** until releases are signed |
| Legal pages: privacy policy, terms, billing/refund terms, support contact (`Product__*`) | Required by Paddle and consumers; the app hides links that are not configured rather than inventing them |
| Paddle live account approval, live price, live API key/webhook secret/client token, `Paddle__ApiBaseUrl=https://api.paddle.com` | Startup rejects live/sandbox mismatches |
| OpenAI project spend limit + monitoring | Global/day caps exist in config (`Safety`), the provider limit is the last line |
| Review Pro limits (600 min/period, 240 min/day) against current OpenAI pricing | See ENTITLEMENTS.md unit economics |
| Railway: backups for Postgres, alerting on `/health/ready` | Ops |
| Run MANUAL_E2E_CHECKLIST.md on a clean PC and keep the evidence | Real audio, real checkout, real webhooks |

## Known limitations
- The answer path and transcription run through OpenAI; an OpenAI outage shows a readable "service unavailable" state, there is no second AI vendor.
- Offline: live help needs the service; local profiles, interviews and reports remain available offline.
- OAuth providers are optional and appear only when listed in `Auth__OAuthProviders` and enabled in Supabase.
