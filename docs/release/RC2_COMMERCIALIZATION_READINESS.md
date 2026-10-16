# 2.1.0-rc.3 — commercialization readiness

Updated for **2.1.0-rc.3** (the file keeps its RC2 name as the living audit set). The five in-repository launch
blockers left by RC2 are fixed — see [RC3_LAUNCH_BLOCKERS.md](RC3_LAUNCH_BLOCKERS.md).

Classification: **READY** (done and verified in this repository) · **NEEDS WORK** (can be done in this repository,
not done yet) · **EXTERNAL BLOCKER** (needs a certificate, business approval, legal decision or production secret
that this repository cannot provide) · **LAUNCH BLOCKER** (must be resolved before selling to the public).

Nothing below claims an external verification that has not happened. Paddle stays in **sandbox**; rc.3 is a
release candidate, not Stable.

| Item | Status | Evidence / what is missing |
|---|---|---|
| Code-signing certificate | **EXTERNAL BLOCKER · LAUNCH BLOCKER** | No certificate exists. rc.3 is **unsigned**; the pipeline (`production-release.yml`) signs when a certificate secret is configured. Never faked. |
| Windows SmartScreen | **EXTERNAL BLOCKER** | Unsigned installers trigger SmartScreen warnings; reputation needs a signed, distributed build. |
| Installer (per user, no admin, Start Menu + Desktop shortcut, uninstall, upgrade) | READY | Velopack; CI installs N, updates to N+1, verifies, uninstalls on every push. |
| Auto-update | READY (signed builds only) · depends on signing | Unsigned builds never self-update by design; update trust covers the signed EXE only (security S6). |
| Release channels | READY | Build channel (Release Candidate) separate from the user's update preference; RCs never promoted to Stable automatically. |
| Crash recovery | READY | Report saved before network cleanup on close; unexpected errors keep the app running with a localized message; damaged files quarantined. |
| Support workflow | **EXTERNAL** (business process) | In-app: copy build info, logs folder, correlation ids on every error; support e-mail/URL are configuration values (`Product__Support*`) to set; the ticketing process and staffing are a business decision. |
| Privacy policy link | **EXTERNAL BLOCKER** | Link is configuration (`Product__PrivacyPolicyUrl`); the legal text must be written/approved outside this repository. Must disclose interviewer-speech processing and providers. |
| Terms of service link | **EXTERNAL BLOCKER** | `Product__TermsUrl`; legal text not in this repository. |
| Billing terms / refund policy | **EXTERNAL BLOCKER** | `Product__BillingTermsUrl`; refund policy is a business decision. |
| Paddle Live cutover | **EXTERNAL BLOCKER** | Requires Paddle business verification and domain approval. Checklist below. Code prerequisites done: period-end check, unmapped-price retry, stale-subscription guard (RC2); **refunds, chargebacks and reversals** (`adjustment.*`) revoke/restore paid access, idempotent and order-safe (rc.3). |
| Supabase production settings | **EXTERNAL BLOCKER** (verify) | Redirect allow-list, e-mail confirmation, secure password change, anonymous sign-ins off, JWT signing keys (JWKS). Not verifiable from code. |
| Auth identity deletion on account deletion | READY (code) · **EXTERNAL** secret | rc.3 deletes the Supabase identity automatically (admin API, queued retry, no "contact support"). Production must set `Auth__ServiceRoleKey`; until then deletions queue and complete once it is set (startup warning, admin health counter). |
| Railway production health | READY (code) / verify (ops) | `/health`, `/health/ready` (DB, JWKS, pending migrations); startup migration retry; production-smoke workflow runs on a schedule and passes. Draining/replica settings not verified. |
| DB backup / restore strategy | **EXTERNAL BLOCKER** (ops decision) | Not configured in this repository; Railway/Postgres backup policy and a restore drill must be defined. |
| Migration rollback | READY (documented) | Additive migrations; roll back by redeploying the previous image; down-migration procedure and rc.3 specifics in `docs/v2/OPERATIONS.md`. |
| Monitoring / alerting | READY (rules defined) · **EXTERNAL** (configure) | Structured logs with correlation ids; the alert rule set (health, 5xx, rejected webhooks, unmapped price, relay upstream failures, pending identity deletions) is in `docs/v2/OPERATIONS.md`; it must be configured on the hosting/log platform. |
| Secret rotation | READY (runbook) | Per-secret rotation steps and verification in `docs/v2/OPERATIONS.md`. |
| Release provenance | READY | Artifacts built only from the commit that passed every CI step; `BUILD_INFO.txt` records version, commit, CI run, signing state. |
| Checksums | READY | SHA-256 files for the portable ZIP and Setup.exe. |
| Minimum client version | READY | SemVer-correct gate (pre-release < release), configurable at runtime. |
| Data retention | READY | Report retention per plan; desktop logs deleted after 14 days; interview vocabulary deleted with the interview (rc.3); deleted-account e-mail markers purged after 365 days. |
| Account deletion | READY | Transactional, locked, blocked by a resumable subscription, server-side recent sign-in, sign-in identity deleted (rc.3); optional local wipe. |
| Live metering | READY (rc.3) | Server-side relay: transcription never outlives billed time; every lease end billed (incl. device revocation). |
| Security review | READY for RC · **EXTERNAL** pen test | RC2 audit + rc.3 adversarial review, all High/Medium findings fixed. Open by design: client-built prompts (S3, bounded by caps — moving templates server-side is a hardening step, not a launch blocker). An independent penetration test is recommended before launch (external). |
| Dependency licenses | READY (verify items open) | All permissive (MIT, Apache-2.0, BSD, PostgreSQL; no copyleft shipped); no vulnerable packages reported by `dotnet list package --vulnerable`. Full license texts, the .NET / Windows Desktop runtime notices and the Velopack native-binary notices now ship in `licenses\` next to THIRD_PARTY_NOTICES.md (build fails without them; `LicenseComplianceTests`). Open verify items: see `RC2_DEPENDENCY_LICENSE_AUDIT.md`. |
| Localization | READY (complete) · **EXTERNAL** native review | 10 languages × 742 keys, CI-gated; a professional native-speaker review is recommended (external). |
| Accessibility | READY for RC / NEEDS WORK | Contrast, focus, names, announcements fixed; no screen-reader session on hardware yet. |
| Real-audio validation | READY (automated, rc.3) · **MANUAL** real-PC run pending | 44 recording-style CC0 fixtures through simulated call paths (`RecordedStyleAudioTests`); analyser fixes; residual misclassifications documented. Checklist G on a real PC (real Windows/Teams sounds) is still required before Stable. |
| Clean-machine validation | **MANUAL** | CI uses fresh Windows runners (install/update/uninstall); a manual run on a clean consumer PC is in the manual checklist. |
| Sample data in repository | **EXTERNAL** (consent) | `samples/shervin-teroxx` is excluded from packages (packaging guard, `ShippedAssetsTests`); the owner must confirm consent or remove it. |

## Paddle Live cutover checklist (do not execute until business verification is complete)

1. **Paddle Live account**: business/KYC and payout verification complete; domain approved (use a custom domain you
   own for `Product__PublicBaseUrl`); products and prices re-created in Live; default payment link set to
   `<PublicBaseUrl>/pay`; customer portal enabled.
2. **Notification destination** at `<PublicBaseUrl>/api/v1/webhooks/paddle` for `subscription.created`, `activated`,
   `updated`, `trialing`, `past_due`, `paused`, `resumed`, `canceled`, `transaction.completed`,
   `adjustment.created`, `adjustment.updated`; copy its secret. (Do the same on the sandbox destination now.)
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
   cancel → Canceling; full refund from the dashboard → `/me` billing state `revoked` once Paddle approves it.
7. **Monitoring**: alerts on "Rejected Paddle webhook", "is not mapped to any plan", "Paddle API unreachable", and
   Paddle delivery failures.
8. **Rollback**: reverting the variables to sandbox removes live customers' entitlements — plan a maintenance window.
