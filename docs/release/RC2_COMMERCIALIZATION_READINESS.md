# 2.1.0-rc.2 — commercialization readiness

Classification: **READY** (done and verified in this repository) · **NEEDS WORK** (can be done in this repository,
not done yet) · **EXTERNAL BLOCKER** (needs a certificate, business approval, legal decision or production secret
that this repository cannot provide) · **LAUNCH BLOCKER** (must be resolved before selling to the public).

Nothing below claims an external verification that has not happened. Paddle stays in **sandbox**; RC2 is a
release candidate, not Stable.

| Item | Status | Evidence / what is missing |
|---|---|---|
| Code-signing certificate | **EXTERNAL BLOCKER · LAUNCH BLOCKER** | No certificate exists. RC2 is **unsigned**; the pipeline (`production-release.yml`) signs when a certificate secret is configured. Never faked. |
| Windows SmartScreen | **EXTERNAL BLOCKER** | Unsigned installers trigger SmartScreen warnings; reputation needs a signed, distributed build. |
| Installer (per user, no admin, Start Menu + Desktop shortcut, uninstall, upgrade) | READY | Velopack; CI installs N, updates to N+1, verifies, uninstalls on every push. |
| Auto-update | READY (signed builds only) · depends on signing | Unsigned builds never self-update by design; update trust covers the signed EXE only (security S6). |
| Release channels | READY | Build channel (Release Candidate) separate from the user's update preference; RCs never promoted to Stable automatically. |
| Crash recovery | READY | Report saved before network cleanup on close; unexpected errors keep the app running with a localized message; damaged files quarantined. |
| Support workflow | NEEDS WORK | Copy build info + logs folder exist; support e-mail/URL are configuration values (`Product__Support*`) that must be set; no ticketing process defined here. |
| Privacy policy link | **EXTERNAL BLOCKER** | Link is configuration (`Product__PrivacyPolicyUrl`); the legal text must be written/approved outside this repository. Must disclose interviewer-speech processing and providers. |
| Terms of service link | **EXTERNAL BLOCKER** | `Product__TermsUrl`; legal text not in this repository. |
| Billing terms / refund policy | **EXTERNAL BLOCKER** | `Product__BillingTermsUrl`; refund policy is a business decision. |
| Paddle Live cutover | **EXTERNAL BLOCKER** | Requires Paddle business verification and domain approval. Checklist below. Code prerequisites done in RC2: period-end check, unmapped-price retry, stale-subscription guard. Refund/chargeback events (`adjustment.*`) are **not handled** → NEEDS WORK or a documented manual procedure. |
| Supabase production settings | **EXTERNAL BLOCKER** (verify) | Redirect allow-list, e-mail confirmation, secure password change, anonymous sign-ins off, JWT signing keys (JWKS). Not verifiable from code. |
| Auth identity deletion on account deletion | NEEDS WORK · LAUNCH BLOCKER (GDPR) | Service data is deleted; the auth identity (e-mail) needs a server-side admin call (service key) — currently "contact support" (disclosed in the dialog). |
| Railway production health | READY (code) / verify (ops) | `/health`, `/health/ready` (DB, JWKS, pending migrations); startup migration retry; production-smoke workflow runs on a schedule and passes. Draining/replica settings not verified. |
| DB backup / restore strategy | **EXTERNAL BLOCKER** (ops decision) | Not configured in this repository; Railway/Postgres backup policy and a restore drill must be defined. |
| Migration rollback | NEEDS WORK | Migrations are additive; no automated down-migration procedure documented. |
| Monitoring | NEEDS WORK | Structured logs with correlation ids; alert rules (rejected webhooks, unmapped price, provider unreachable, 5xx rate) not set up. |
| Alerting | NEEDS WORK | As above. |
| Secret rotation | NEEDS WORK | Secrets are environment variables; rotation runbook (OpenAI key, Paddle key/webhook secret, Supabase keys) not written. |
| Release provenance | READY | Artifacts built only from the commit that passed every CI step; `BUILD_INFO.txt` records version, commit, CI run, signing state. |
| Checksums | READY | SHA-256 files for the portable ZIP and Setup.exe. |
| Minimum client version | READY | SemVer-correct gate (pre-release < release), configurable at runtime. |
| Data retention | READY (reports) / NEEDS WORK (logs, server vocabulary) | Report retention per plan; desktop logs not rotated; per-interview vocabulary documents not deleted with the interview. |
| Account deletion | READY (service data) | Transactional, locked, blocked by a resumable subscription, server-side recent sign-in; optional local wipe. |
| Security review | READY for RC / NEEDS WORK for launch | RC2 audit done and High findings fixed; open: realtime-socket lifetime vs lease (S1 remainder), client-built prompts (S3). An independent penetration test is recommended before launch. |
| Dependency licenses | READY / NEEDS WORK | All permissive (MIT, Apache-2.0, PostgreSQL); no vulnerable packages reported by `dotnet list package --vulnerable`; THIRD_PARTY_NOTICES lacks full license texts and the .NET runtime notices required for a self-contained build → NEEDS WORK before public distribution. |
| Localization | READY (complete) / NEEDS WORK (native review) | 10 languages × 734 keys, CI-gated; professional native review recommended. |
| Accessibility | READY for RC / NEEDS WORK | Contrast, focus, names, announcements fixed; no screen-reader session on hardware yet. |
| Real-audio validation | NEEDS WORK · LAUNCH BLOCKER | The acoustic gate is validated on synthetic audio; a recorded-audio validation set (real beeps, voices, music, panels) must be run before Stable. |
| Clean-machine validation | NEEDS WORK | CI uses fresh Windows runners (install/update/uninstall); a manual run on a clean consumer PC is in the manual checklist and not yet performed. |
| Sample data in repository | NEEDS WORK | `samples/shervin-teroxx` is excluded from packages; confirm consent or replace with synthetic data. |

## Paddle Live cutover checklist (do not execute until business verification is complete)

1. **Paddle Live account**: business/KYC and payout verification complete; domain approved (use a custom domain you
   own for `Product__PublicBaseUrl`); products and prices re-created in Live; default payment link set to
   `<PublicBaseUrl>/pay`; customer portal enabled.
2. **Notification destination** at `<PublicBaseUrl>/api/v1/webhooks/paddle` for `subscription.created`, `activated`,
   `updated`, `trialing`, `past_due`, `paused`, `resumed`, `canceled`, `transaction.completed` (add
   `adjustment.created/updated` once handled); copy its secret.
3. **Railway environment** (never committed): `Paddle__ApiBaseUrl=https://api.paddle.com`, `Paddle__ApiKey`
   (`pdl_live_…`), `Paddle__ClientToken` (`live_…`), `Paddle__WebhookSecret`,
   `PlanCatalog__Plans__1__PaddlePriceIds__0` = live `pri_…` (remove sandbox ids), review
   `Paddle__PastDueGraceDays`; legal URLs `Product__TermsUrl`, `Product__PrivacyPolicyUrl`,
   `Product__BillingTermsUrl`, `Product__SupportUrl`, `Product__SupportEmail`. Startup validation refuses a sandbox
   key with a live URL.
4. **Database**: archive or delete sandbox `subscriptions` rows (sandbox customer/subscription ids would fail against
   the Live API); disable the sandbox notification destination for production.
5. **Deploy and verify**: startup log "billing live (checkout page configured)"; `/api/v1/config` →
   `billingEnvironment: live`; `/api/v1/billing/plans` shows the live price.
6. **Test purchase** with a real card (or 100 % discount): webhook processed, `/me` = Pro/Active, portal opens;
   cancel → Canceling; refund from the dashboard → observe events; cancel manually until refunds are handled.
7. **Monitoring**: alerts on "Rejected Paddle webhook", "is not mapped to any plan", "Paddle API unreachable", and
   Paddle delivery failures.
8. **Rollback**: reverting the variables to sandbox removes live customers' entitlements — plan a maintenance window.
