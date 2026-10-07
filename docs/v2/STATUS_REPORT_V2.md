# Commercial V2 — Status report

Format per spec §46. "Validated" means an automated test or CI step exercised it; nothing below is claimed from code existence alone.

## IMPLEMENTED
- **Generic multi-user product** (no candidate hard-coding): profiles with résumé/JD ingestion (PDF/DOCX/TXT, upload security, prompt-injection guard), fact provenance + review (only confirmed facts reach answers), multiple interview targets, 15-stage preparation → 50–150 question bank per target, DPAPI-encrypted local workspace, export ZIP, cascade delete.
- **Live intelligence**: Answer + Coach modes (Ctrl+Alt+C: 3 keywords + structure), bounded full-interview memory with story anti-repetition, adaptive Concise/Rapid on interruptions (Ctrl+Alt+R reset), 10-language detection with answer-language rules and RTL rendering, post-interview report (honesty labels, no scores, 10 languages, safe HTML export).
- **Backend** (ASP.NET Core .NET 10 + PostgreSQL + EF migrations): Supabase JWT auth, per-user isolation, server-side entitlements from a data-driven plan catalog, heartbeat-metered session leases, per-account/global caps, rate limits, OpenAI key server-side only (ephemeral Realtime secrets + SSE answer proxy), remote model routing/kill switch/maintenance/min client version, devices, admin API (audited), export/delete, readiness probe, correlation ids, Dockerfile.
- **Billing**: Paddle MoR hosted checkout + portal; verified, idempotent, ordered webhooks; past-due grace; cancel at period end. No card data in app/backend.
- **Desktop cloud client**: Supabase PKCE via system browser (OAuth + magic link) with a 127.0.0.1 loopback receiver, DPAPI token store, typed API client with refresh/retry, leases with heartbeats/renewal, cloud answer provider; developer (own key) mode preserved.
- **UX**: Home dashboard (onboarding checklist with authorized-use acknowledgement, profiles + fact review, interviews + preparation progress, reports, account/billing/devices/privacy, language, updates); live overlay with Coach card, adaptive badge, minutes left, auto-fit so all bullets are visible without scrolling, localized labels (10 languages), RTL UI/answers.
- **Installer/updates**: Velopack Setup.exe (per-user), stable/beta channels, delta updates, update policy that never acts during a session.
- **CI/CD**: pr-validation (core + backend integration tests on PostgreSQL, migration drift, vulnerable packages, secret scan, container build); windows-release (tests, publish, real-EXE smoke + self-test, installer, N→N+1 update test); production-release (tag → channel, signing gated on secrets, GitHub Release feed); backend-deploy (staging + approval-gated production).
- **Docs**: all 18 required documents plus DESKTOP_CLIENT, STATUS_REPORT_V2.

## TESTED
| What | How | Result |
|---|---|---|
| Core/engine/ingestion/preparation/languages/reports/client auth/update policy | `dotnet test tests/InterviewAssistant.Tests` — Linux + windows-latest | 201/201 |
| Backend + desktop client end-to-end | `dotnet test tests/InterviewAssistant.Backend.Tests` against PostgreSQL 16 (local + CI service) | 44/44 |
| Migration drift, vulnerable packages, secret scan, backend container image | pr-validation on ubuntu-latest | green |
| Real EXE launch/close, in-app self-test (XAML, Coach, auto-fit, RTL, Home, local prepare, report) | windows-latest smoke + `--selftest` | see CI run for this commit |
| Installer + N→N+1 self-update + uninstall | `scripts/update-test.ps1` on windows-latest | see CI run for this commit |
| Velopack packaging config and delta | local cross-pack | Setup 68.6 MB; delta 0.31 MB |

## NOT YET TESTED (external accounts / hardware / human)
- Live Supabase sign-in, Paddle sandbox purchase/webhooks/portal, server-issued OpenAI Realtime session used by the desktop, live multilingual answer quality (gated test needs `OPENAI_API_KEY`).
- Azure Artifact Signing and SmartScreen; GitHub Releases as a *public* update feed.
- Staging/production deployment + smoke against a real host.
- Visual review of the redesigned screens on real Windows displays (100–200 % scaling), real-interview latency P50/P95 with live audio.
- Native-speaker review of UI strings and report labels.

## KNOWN LIMITATIONS
- Company research from URLs is not fetched automatically (pasted text/documents only).
- Synced cloud documents are opaque JSON; desktop sync of profiles/targets to the server is available via the API client but the UI keeps data local by default.
- Deleting the Supabase identity itself needs the service-role admin call (not implemented); `DELETE /me` deletes all app data and tombstones the account.
- UI localisation covers the critical screens (40 strings); some secondary dialogs (Settings, Readiness) remain English.
- Repository is private: auto-update from GitHub Releases needs a public feed location.

## NEXT
Blocked on external accounts (checklist below). With them: run the sandbox billing test, live sign-in, live transcription through the server, deploy staging, sign the installer, and repeat the Windows end-to-end on a real PC.

## EXTERNAL ACCOUNT CHECKLIST (create these; never paste secrets into chat — store them as GitHub/hosting secrets)
1. **Supabase** project: enable e-mail magic link + Google/Microsoft; add redirect `http://127.0.0.1:*/callback`; note project URL + anon key (public) → `cloud.json`; set `Auth__Issuer`, `Auth__JwksUrl` on the backend.
2. **OpenAI** project key with a monthly spend limit → backend secret `OpenAI__ApiKey` only.
3. **Paddle** sandbox: products/prices → `PlanCatalog__Plans__N__PaddlePriceIds__0`; API key + notification secret → `Paddle__ApiKey`, `Paddle__WebhookSecret`; webhook URL `https://<staging>/api/v1/webhooks/paddle`.
4. **Hosting** (container + managed PostgreSQL) + domain/TLS → GitHub environment secrets `STAGING_DATABASE_URL`, `STAGING_DEPLOY_HOOK`, variable `STAGING_BASE_URL` (and `PRODUCTION_*`); add required reviewers to the `production` environment.
5. **Azure Artifact Signing** account + identity validation → secrets `AZURE_SIGNING_ENDPOINT/ACCOUNT/PROFILE`, `AZURE_CLIENT_ID/TENANT_ID/CLIENT_SECRET` in the `release` environment.
6. **Public update feed**: a public releases repository or static https folder → `updateUrl` in `cloud.json`.
