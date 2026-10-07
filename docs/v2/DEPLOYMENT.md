# Deployment

Status: container build, migration bundle and CI/CD workflow **IMPLEMENTED**; staging/production deploy **NOT YET VALIDATED** — requires a hosting account, a PostgreSQL instance, a domain and the secrets below.

## Components
| Component | Choice | Notes |
|---|---|---|
| API | ASP.NET Core (.NET 10 LTS) container, `src/InterviewAssistant.Backend/Dockerfile` | Stateless, non-root, port 8080 |
| Database | PostgreSQL 16+ (managed: Azure Database for PostgreSQL, Neon, Supabase Postgres, RDS…) | TLS required |
| Auth | Supabase Auth (email + OAuth) — JWT verified via JWKS | Desktop uses PKCE through the system browser |
| Billing | Paddle (MoR) | See BILLING.md |
| AI | OpenAI — server key only | Desktop receives ephemeral Realtime secrets only |
| Images | GHCR `ghcr.io/<owner>/<repo>/backend:<sha>` | Pushed by `backend-deploy.yml` |

Any container host works (Azure Container Apps, Fly.io, Render, Railway, ECS). The workflow deploys through a host-provided **deploy hook** URL so the pipeline is not tied to one vendor.

## Configuration (environment variables; double underscore = section separator)
| Variable | Required | Example |
|---|---|---|
| `ConnectionStrings__Db` | yes | `Host=…;Database=ia;Username=…;Password=…;SSL Mode=Require` |
| `Auth__Issuer` | yes | `https://<project>.supabase.co/auth/v1` |
| `Auth__JwksUrl` | yes (or legacy `Auth__JwtSecret`) | `https://<project>.supabase.co/auth/v1/.well-known/jwks.json` |
| `Auth__AdminUserIds__0` | for admin API | Supabase user id |
| `OpenAI__ApiKey` | yes | server-only project key with spend limit |
| `Paddle__ApiBaseUrl`, `Paddle__ApiKey`, `Paddle__WebhookSecret` | yes | |
| `PlanCatalog__Plans__1__PaddlePriceIds__0` | yes | `pri_…` |
| `Database__MigrateOnStartup` | no (default false) | Prefer the migration bundle |

## Pipeline (`.github/workflows/backend-deploy.yml`)
1. Trigger: push to `main` touching backend code, or manual dispatch. **Never from arbitrary branches.**
2. Integration tests on a PostgreSQL service container; migration drift check; `dotnet ef migrations bundle`.
3. Container build → GHCR.
4. **staging** environment: apply migrations (`STAGING_DATABASE_URL`), call `STAGING_DEPLOY_HOOK`, smoke tests against `vars.STAGING_BASE_URL` (`/health/ready`, `/api/v1/config`, auth 401, unsigned webhook 401).
5. **production** environment — protected by required reviewers (the approval gate; configure under GitHub → Settings → Environments → production). Same steps with `PRODUCTION_*` secrets.

Each step is skipped with a notice when its secret is missing, so the workflow is safe to merge before accounts exist.

## Health
`/health` (liveness), `/health/ready` (DB reachable + no pending migrations → 200, else 503).

## Rollback
Redeploy the previous image tag via the hook. Migrations are additive by policy; destructive changes ship in two releases (expand → contract).
