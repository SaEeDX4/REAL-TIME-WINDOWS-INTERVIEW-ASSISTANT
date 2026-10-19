# 2.1.0-rc.5 — security and privacy audit

Scope: everything rc.5 changed on the desktop side: account/auth flows (Forgot password, Switch account, Delete
account), the new exports (readable HTML, raw JSON/ZIP, `.iaprofile` backup and import), diagnostics, logging and the
update check. The backend, its migrations and the Paddle configuration are unchanged in rc.5
(`git diff a0bc946 -- src/InterviewAssistant.Backend* tests/InterviewAssistant.Backend.Tests railway.json` is empty).
Method: an independent read-only review with probe harnesses linked against the built Core library, then fixes with
regression tests (`SecurityAuditFixTests`, `SecurityAuditExportTests`, `Rc5DataExportTests`, `Rc5AccountTests`).

## Findings and outcome

| # | Severity | Finding | Outcome |
|---|---|---|---|
| H1 | High | A pending password-reset listener (up to 15 min) outlived the reset screen; a later link for another address could replace the signed-in session while the UI still showed the first account | **Fixed.** `PendingReset` holds one ticket per request; leaving the reset screens, signing in/out, switching account or closing Home cancels it, and only the current ticket may change the form. The recovery code is never exchanged while a different account is signed in (`signed_in_other_account`, checked before the token request and again before storing). A cancelled new-password prompt finishes as a normal sign-in instead of a half state |
| M1 | Medium | A malformed `.iaprofile` (null items, null ids, duplicate keys) threw unhandled exceptions | **Fixed.** Null fields rejected from the models' nullability, duplicate JSON properties refused, every validation exception becomes `ProfileBundleException(Invalid)`, a final catch shows the plain message. Every input of the review is a regression test |
| M2 | Medium | Resend after 30 s killed the only working link (the auth server sends one recovery e-mail per address per 60 s) | **Fixed.** Cooldown 60 s; a new request for the same address inside the window waits instead of silently dropping the e-mail |
| M3 | Medium | `SecretFilter` failed open past depth 96, truncated long key names, missed several key names and value shapes | **Fixed.** Fails closed at the depth limit, no truncation, new key markers (`…key`, `otp`, `recoverycode`, `signature`, `auth`) and value patterns (token assignments, `Basic …`, Google and Supabase key shapes). An export the filter cannot parse is not written. User ids, Paddle ids, e-mail and "keywords" are kept; résumé prose is not flagged |
| L1 | Low | Import did not re-run the prompt-injection guard | **Fixed.** Document, fact, interview and pack text are re-sanitised, flags recomputed, file names re-validated; the same warning as a résumé upload |
| L2 | Low | "changed after it was created" overstated an unkeyed hash | **Fixed** (copy): "damaged or incomplete" |
| L3 | Low | Backups/exports are not encrypted, the readable profile suggested sharing | **Fixed** (copy): "Not encrypted — store it safely" on the backup, readable profile and raw ZIP; "share" removed |
| L4 | Low | Value redaction rewrote résumé prose inside a backup | **Partly fixed.** The backup filters by key name and removes only unmistakable credential values (an API key pasted into notes is still removed — required by `NoSecretsInAnyProfileExport`); prose such as "Secret: active clearance" round-trips unchanged |
| L5 | Low | Local data could stay behind silently after account deletion | **Fixed.** Paths captured before the delete call; failures name the folder (`del_local_failed`), the log gets the exception type only |
| L6 | Low | Re-authentication was asked only to be refused while a subscription blocks deletion | **Fixed.** The dialog offers Manage billing / Keep account only |
| L7 | Low | An older auth server's 429 text could reveal that an account exists | **Fixed.** Any per-address "For security purposes" 429 counts as accepted |
| L8 | Low | Focus lost after Send reset link / a reset error | **Fixed** |
| L9 | Low | `$` in id/entry regexes accepted a trailing newline | **Fixed** (`\z`) |
| L10 | Low | Peak memory on hostile ZIPs | **Partly fixed.** `MaxTotalBytes` 64 MB, the declared entry count is checked before the central directory is parsed; entries are not hashed while streaming yet |
| L11 | Low | File-error log lines contained paths (Windows user name, candidate slug); control characters reached the log | **Fixed.** Type name only for IO/permission errors; control characters become spaces |
| L12 | Low | Unexpected exception types escaped the update check | **Fixed.** Final catch, type name only |

Also fixed from the error-state review in the same code: every export is written to a `.tmp` next to the target and
moved into place when complete (`ExportFile`), so a disk-full error never leaves a truncated file under the real name;
a parse or filter failure in the account export shows `exp_err_account_export` instead of escaping.

## Properties verified to hold

- **Enumeration-safe reset**: the same sentence for known and unknown addresses; rate limits per address treated as
  accepted; the general rate limit is still reported.
- **Re-authentication** before export, delete and password change; a different returned user id signs out.
- **Account isolation**: Switch account asks first, ends a running session, clears readiness; the workspace store is
  null unless it matches the signed-in account; device lists ignore responses for a previous user; readiness context
  is keyed by owner. rc.5 did not weaken account/device isolation.
- **HTML exports**: every value HTML-encoded; `lang`/`dir` from the registry; CSP `default-src 'none';
  style-src 'unsafe-inline'`; no scripts.
- **`.iaprofile` reader**: traversal rejected, strict entry allow-list, case-insensitive duplicates, manifest must list
  exactly the entries with matching size and SHA-256, newer versions refused; nothing extracted to disk; an import is
  always a new profile with fresh ids and is rolled back on failure.
- **No secrets**: the release gate's secret scan and `NoSecretsInRepository` pass; the provider key never lives in the
  desktop app in account mode; exports never contain passwords, sign-in tokens or keys; logs and Copy diagnostics carry
  no tokens, e-mail, transcripts or résumé text.
- **Billing**: Paddle stays **sandbox** (`appsettings.json` → `sandbox-api.paddle.com`; `production-smoke.yml` fails
  if `billingEnvironment` is not `sandbox`). `MinimumClientVersion` stays 2.0.0.
- **Signing**: rc.5 is an **Unsigned build**; unsigned builds never self-update (the update test uses a CI-only hook).

## Residual risks

- The bundle hash detects corruption, not tampering: a shared `.iaprofile` is untrusted input (handled by L1).
- L10 streaming hashes and a keyed bundle signature are not implemented.
- A machine-level attacker with the user's Windows session can read DPAPI-protected data — unchanged by design.
