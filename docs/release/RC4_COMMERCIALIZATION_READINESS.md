# 2.1.0-rc.4 — commercialization readiness

Legend as in rc.3: **READY** (done and verified in this repository) · **OPEN** (work in this repository not done) ·
**EXTERNAL BLOCKER** (needs a certificate, business approval, legal text or production secret this repository cannot
provide) · **LAUNCH BLOCKER** (must be resolved before selling to the public).

rc.4 is a release candidate for testing, not Stable. Billing stays in the Paddle **sandbox**.

## What rc.4 changes in readiness

| Area | Status | Notes |
|---|---|---|
| Non-speech never becomes a question (rc.3 P0) | READY (code + automated real-audio suite) · manual check C pending | 0 answered / 0 history / 0 report entries for every non-voice fixture; residual risks (singing, a quiet knock with short phrases) documented in RC4_ENGINE_AUDIT |
| Short real questions | READY | "Why?", "Why.", "How so?", "Really?", "And then?", "Tell me more.", "What happened?", "Metrics?", "Anything else?" answered from real (synthesized) speech |
| No fabrication in personal answers | READY (English verified per bullet; other languages by prompt rule) | see RC4_ENGINE_AUDIT §5 |
| Report trustworthiness | READY | doubtful questions listed separately and excluded from statistics |
| Desktop UI consistency | READY (code + Windows self-test) · visual manual pass pending | design tokens, one form-control family, in-app dialogs, Settings page, Profiles/Reports redesign |
| Error states | READY | independent account sections, contextual copy, tested matrix |
| Localization | READY (complete, 813 keys × 10) · **EXTERNAL** native review | |
| Accessibility | READY for RC · manual screen-reader pass pending | |
| Backend compatibility | READY | no server change; rc.3 clients unaffected; minimum version not raised |

## Unchanged external items (from rc.3)

| Item | Status |
|---|---|
| Code-signing certificate | **EXTERNAL BLOCKER · LAUNCH BLOCKER** — rc.4 is **unsigned**; the pipeline signs when a certificate secret exists. Never faked. Automatic updates stay disabled for unsigned builds. |
| Windows SmartScreen reputation | **EXTERNAL BLOCKER** — needs signed, distributed builds. |
| Privacy policy, terms of service, billing terms / refund policy | **EXTERNAL BLOCKER** — legal texts outside this repository; URLs are configuration. |
| Paddle Live cutover | **EXTERNAL BLOCKER** — business verification and domain approval; code prerequisites done (rc.2/rc.3). Not switched. |
| Supabase production settings | **EXTERNAL BLOCKER** (verify) — redirect allow-list, e-mail confirmation, JWKS, anonymous sign-ins off. |
| `Auth__ServiceRoleKey` in production | **EXTERNAL** — identity deletions queue until it is set. |
| Database backup/restore policy and drill | **EXTERNAL BLOCKER** (ops). |
| Monitoring/alerting configuration | **EXTERNAL** (rules defined in `docs/v2/OPERATIONS.md`). |
| Independent penetration test | **EXTERNAL** (recommended before launch). |
| Native-speaker translation review (fa, ar, hi first) | **EXTERNAL** (recommended). |
| Sample candidate data consent (`samples/shervin-teroxx`, not shipped) | **EXTERNAL** — owner to confirm consent or remove. |

## Open in this repository

| Item | Status |
|---|---|
| Speaker diarization for panel interviews | OPEN by design — not claimed; name-based addressing only. No voiceprints. |
| Personal-answer grounding check for non-English answers | OPEN — prompt rule only; a language-aware check would need per-language stemming. |
| Manual regression A–X on real hardware | OPEN — checklist in RC4_MANUAL_REGRESSION; must be run by a person on Windows before promoting rc.4. |

## Verdict

rc.4 closes the rc.3 P0 in code with automated real-audio evidence and closes the listed P1/P2 UX findings. It is
suitable for **release-candidate testing**. It is not sellable to the public until the external launch blockers
above (code signing, legal texts, Paddle Live, production verification) are resolved and the manual regression has
passed.
