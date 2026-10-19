# 2.1.0-rc.5 — commercialization readiness

Legend as before: **READY** (done and verified in this repository) · **OPEN** (work in this repository not done) ·
**EXTERNAL BLOCKER** (needs a certificate, business approval, legal text or production secret this repository cannot
provide) · **LAUNCH BLOCKER** (must be resolved before selling to the public).

rc.5 is a **release candidate** for testing, not Stable. Billing stays in the Paddle **sandbox**. The build is
**unsigned**.

## What rc.5 changes in readiness

| Area | Status | Notes |
|---|---|---|
| Imperative follow-ups ("Tell me more.", "Go on.", "Walk me through that.") | READY (code + synthetic-speech fixtures) · manual row D pending | English grammar; other languages when asked with "?" (RC5_ENGINE_AUDIT) |
| Non-speech never becomes a question | READY (code + real-audio suite incl. short clips) · manual row C pending | residual look-alike risks documented in RC5_ENGINE_AUDIT |
| Interview readiness | READY | one store, in-app check, Get started dashboard, indicators in Home and Live; account mode reaches Ready |
| Navigation and first run | READY | deep links without flash; Home opens on Get started; acknowledgement on every start path |
| Settings | READY | categories, Updates, reset section / restore defaults; save failures reported |
| Account and auth | READY · manual rows L–N pending | dashboard, header menu, switch account, forgot password, delete dialog |
| Data portability | READY | readable and raw exports, `.iaprofile` backup/import with preview; no credentials in any export |
| Live Assistant | READY · visual manual row P pending | calm toolbar, Quick help, Maximize, Interview Focus |
| Accessibility | READY for RC · Narrator/NVDA pass (row W) pending | UIA peers, live regions, meters, roving radio groups |
| Localization | READY (10 languages, complete) · **EXTERNAL** native-speaker review | terminology pass, RTL, Gregorian Arabic dates |
| Design system | READY for RC · type ramp, button and badge tokens OPEN | no visual regression; see RC5_DESIGN_SYSTEM_AUDIT |
| Backend compatibility | READY | no server change in rc.5; rc.4 clients unaffected; minimum client version stays 2.0.0 |
| Install / update / uninstall | READY (CI: install, N→N+1 update, in-place upgrade from the published rc.4 Setup, uninstall) | |

## Unchanged external items

| Item | Status |
|---|---|
| Authenticode code-signing certificate | **EXTERNAL BLOCKER / LAUNCH BLOCKER** — rc.5 ships unsigned; SmartScreen warns; unsigned builds never self-update |
| Paddle live account approval, live keys, live webhook secret | **EXTERNAL BLOCKER / LAUNCH BLOCKER** — billing stays sandbox |
| Final legal texts (terms, privacy policy, refund policy) reviewed by counsel | **EXTERNAL BLOCKER / LAUNCH BLOCKER** |
| Native-speaker review of the nine non-English languages | **EXTERNAL** |
| Manual regression A–W on real hardware (`RC5_MANUAL_REGRESSION.md`) | **OPEN** — not yet run by a person for rc.5 |

## Operational recommendations

- The production backend on Railway deploys on every push of this branch. Point production at a protected release
  branch or tag before launch so a candidate push cannot change the live service (rc.5 contains no backend change).
- Keep `production-smoke.yml`'s sandbox assertion until the Paddle live switch is a deliberate, reviewed change.
