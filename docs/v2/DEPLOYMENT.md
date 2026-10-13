# Deployment (Railway)

Status: **production deploy running on Railway** — `https://real-time-windows-interview-assistant-production.up.railway.app`.
Verified continuously by `.github/workflows/production-smoke.yml` (on every push and every 6 h).

## Components
| Component | Choice | Notes |
|---|---|---|
| API | ASP.NET Core (.NET 10) container, `src/InterviewAssistant.Backend/Dockerfile` (`RAILWAY_DOCKERFILE_PATH`) | Stateless, non-root, listens on 8080 and `$PORT` |
| Database | Railway PostgreSQL | Migrations applied at start (`Database__MigrateOnStart=true`), additive only |
| Auth | Supabase Auth — RS256/ES256 JWT verified via JWKS | Desktop: e-mail/password and optional OAuth (PKCE via loopback) |
| Billing | Paddle Billing (sandbox now; merchant of record) | See BILLING.md |
| AI | OpenAI — server key only | Desktop receives short-lived realtime client secrets only |

`railway.json` makes Railway wait for `/health/ready` before switching traffic (180 s) and restart on failure.
Railway auto-deploys the connected branch on push. `backend-deploy.yml` (GHCR image + deploy hook, `main` only) is an
optional alternative for other hosts and skips itself when its secrets are absent.

## Configuration (Railway variables; double underscore = section separator)
Production startup **fails fast** with a list of problems when required values are missing or inconsistent
(`ConfigValidation`); nothing secret is ever logged.

| Variable | Required | Notes |
|---|---|---|
| `ConnectionStrings__Db` | yes | Railway Postgres reference |
| `Database__MigrateOnStart` | yes (`true`) | Applies pending migrations before serving |
| `Auth__Issuer` | yes | `https://<project>.supabase.co/auth/v1` |
| `Auth__JwksUrl` | yes | `https://<project>.supabase.co/auth/v1/.well-known/jwks.json` |
| `Auth__Audience` | yes | `authenticated` |
| `Auth__PublishableKey` | **yes for desktop sign-in** | Supabase *publishable* (anon) key — public by design, served to the app via `/api/v1/config` |
| `Auth__OAuthProviders__0` | optional | e.g. `google` (enable it in Supabase first) |
| `Auth__AdminUserIds__0` | optional | Supabase user id for `/api/v1/admin/*` |
| `OpenAI__ApiKey` | yes | server-only project key with a monthly spend limit |
| `Paddle__ApiBaseUrl` | no | default `https://sandbox-api.paddle.com`; `https://api.paddle.com` for live |
| `Paddle__ApiKey`, `Paddle__WebhookSecret` | yes | server-only; live/sandbox mismatch is rejected at startup |
| `Paddle__ClientToken` | **yes for checkout** | Paddle client-side token (`test_…` sandbox / `live_…`) used by the `/pay` page |
| `PlanCatalog__Plans__1__PaddlePriceIds__0` | yes | the Pro price `pri_…` |
| `Product__PublicBaseUrl` | yes for sign-up confirmation | the backend's public URL (used for `/auth/confirmed`) |
| `Product__PrivacyPolicyUrl`, `Product__TermsUrl`, `Product__BillingTermsUrl`, `Product__SupportUrl`, `Product__SupportEmail` | before public launch | https links shown in the app; omitted links are hidden, never faked |

### External settings that must match
- **Paddle → Checkout → Checkout settings → Default payment link:** `<PublicBaseUrl>/pay`. Without it Paddle returns no checkout URL (the API logs the exact fix).
- **Paddle → Notifications:** destination `<PublicBaseUrl>/api/v1/webhooks/paddle`, the events listed in BILLING.md.
- **Supabase → Authentication → URL configuration → Redirect URLs:** `http://127.0.0.1:*/**` (desktop loopback for OAuth and password reset) and `<PublicBaseUrl>/auth/confirmed`.
- **Supabase → JWT keys:** asymmetric signing keys (the default for new projects). The legacy HS256 secret is refused in production.

## Health
| Endpoint | Meaning |
|---|---|
| `/health` | liveness + `version` (first 12 chars of `RAILWAY_GIT_COMMIT_SHA`) |
| `/health/ready` | 200 `ready` only when the DB answers, JWKS keys are loaded and no migration is pending; otherwise 503 with `reason` (`database_unreachable`, `auth_keys_unavailable`, `migrations_pending`) |

## Logs
JSON console logs in production (Railway log explorer): startup summary (auth mode, sign-in configured, billing
environment, checkout page configured, plan codes), session lifecycle, webhook outcomes per event, Paddle error codes.
Tokens, keys, e-mail bodies, transcripts and answers are never logged.

## Rollback
Railway → Deployments → previous deployment → Redeploy. Migrations are additive (expand → contract over two
releases), so the previous build runs against the newer schema. Production data is never wiped automatically.
