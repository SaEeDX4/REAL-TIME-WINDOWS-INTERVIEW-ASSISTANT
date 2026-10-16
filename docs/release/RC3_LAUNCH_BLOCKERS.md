# 2.1.0-rc.3 — launch blockers fixed in the repository

RC2 (build `8d82b20`, artifact `InterviewAssistant-v2.1.0-rc.2`) stays as published. RC3 changes the packaged build
(live transcription protocol, account deletion, billing states, license files), so it is a new candidate,
**2.1.0-rc.3**, not a silent replacement of RC2. Paddle stays in **sandbox**; the build is **unsigned**; it is a
release candidate, not Stable.

Each blocker below lists the design, the code, the tests, and what is still outside this repository.

## 1. Account deletion deletes the sign-in identity

**Before:** service data was deleted; the Supabase auth identity (e-mail, password hash, linked OAuth identities)
stayed, and the dialog said "contact support".

**Now** (`AccountService.DeleteAsync`, `IdentityAdmin.cs`):
1. Unchanged: recent sign-in required server-side (10 min, `amr`), deletion blocked while a subscription can still
   charge, all service data deleted in one transaction under the per-user advisory lock, account id tombstoned.
2. New: in the same transaction the account is marked `IdentityDeletionPendingSinceUtc`. After the commit the
   service calls the Supabase Auth admin API `DELETE /auth/v1/admin/users/{id}` (hard delete) with the server-only
   secret key `Auth__ServiceRoleKey` (headers `apikey` and `Authorization: Bearer`; never logged, never returned).
   A `404` counts as already deleted only when the auth service says `user_not_found` (a 404 from a wrong URL or a
   proxy stays a failure and is retried).
3. On any provider error, timeout or unexpected exception the deletion stays queued and the request still succeeds
   (the data deletion is already committed); `SessionSweeper` retries every 5 minutes, least-tried first
   (`IdentityDeletionService.RetryPendingAsync`). The migration queues accounts deleted before rc.3 as well. The client is told `identity: "pending"` and
   says the sign-in will be removed automatically; otherwise `"deleted"`.
4. Admin `/health` reports `identityDeletionConfigured` and `identityDeletionsPending`; startup logs a warning when
   the key is missing.
5. Dialog copy (10 languages) states exactly what is deleted, including the sign-in; no "contact support".
6. Because the hard delete frees the e-mail address, a pseudonymous marker (SHA-256 of the normalised e-mail, nothing
   else, purged after 365 days) stops "delete and sign up again" from granting a second trial.

Tests: `Rc3AccountAndRefundTests.DeletingTheAccount_AlsoDeletesTheSignInIdentity` (request URL, headers, key never
echoed, old token cannot recreate the account), `ProviderOutage_QueuesTheIdentityDeletion_AndTheSweeperCompletesIt`,
`IdentityAlreadyGoneAtTheProvider_CountsAsDeleted`, `A404ThatIsNotTheAuthServicesUserNotFound_StaysQueued`,
`AnUnexpectedErrorAfterTheDataIsDeleted_IsNeverReportedAsAFailedDeletion`,
`WithoutTheServerKey_TheDeletionStaysQueued_NeverSilentlyDropped`, `DeletingAndSigningUpAgainWithTheSameEmail_GivesNoSecondTrial`;
existing deletion tests (transaction, rollback, recent sign-in) still pass.

**External (not a code limitation of the provider):** set `Auth__ServiceRoleKey` in the production environment
(Supabase dashboard → Project settings → API keys → secret key). Until it is set, deletions queue and complete as
soon as it is configured. Paddle keeps invoices and customer records as merchant of record (legal retention); that
is disclosed, not hidden.

## 2. Third-party notices and full license texts

`licenses/` now contains the full texts and notices for every component shipped in the desktop package: the .NET and
Windows Desktop runtimes (MIT plus their third-party notices), NAudio (MIT), PdfPig (Apache-2.0, including the
Adobe AFM/Glyph List terms and the Apache PDFBox NOTICE for embedded data), Velopack (MIT) and the 236 Rust crates
statically linked into Velopack's native binaries. `THIRD_PARTY_NOTICES.md` indexes them. `build-release.ps1` copies
both into the package and fails if any referenced file is missing; `package-rc.ps1` re-checks. No copyleft
component is shipped; where a crate offers a choice, the permissive option is taken. Details and the open verify
items (runtime patch version per build, cargo-about confirmation of the Velopack crate list): 
[RC2_DEPENDENCY_LICENSE_AUDIT.md](RC2_DEPENDENCY_LICENSE_AUDIT.md). Test: `LicenseComplianceTests`.

## 3. Live transcription can no longer outlive the billable lease

**Before:** the desktop received an ephemeral OpenAI secret and connected to OpenAI itself. A socket opened while the
lease was active could keep transcribing after the lease ended (pause, exhaustion, expiry) — unmetered.

**Design — server-authoritative relay** (`RealtimeRelay.cs`, `SessionService`, `/api/v1/sessions/{id}/realtime`):
* `POST /sessions` returns the lease and a relay path, **no secret**. Clients that do not declare relay support get
  `426 client_update_required` (pre-rc.3 builds can no longer start live sessions).
* The desktop opens a WebSocket to the relay with its own access token. Before accepting, the service checks that the
  lease belongs to the user, is active, recently heartbeaten, on an active device, and that allowance remains; it then
  mints the upstream ephemeral secret itself (counted against the per-lease cap) and connects to OpenAI. The secret
  never leaves the server.
* Client → provider: only `input_audio_buffer.append|commit|clear` text messages up to 256 KB, parsed strictly
  (duplicate keys, trailing content and invalid UTF-8 rejected) and **rebuilt** from `type` and base64 `audio` — the
  client's own bytes never reach the provider, so it cannot change the model, prompt or VAD. The upstream secret is
  minted outside the per-user lock. Provider → client: forwarded unchanged.
* The relay closes with `1008 lease_ended`:
  - immediately when the lease ends in this process — stop, replacement, heartbeat expiry, allowance/daily/global cap,
    maximum duration, device revoked, account disabled, account deleted (`RealtimeRelayRegistry.Close` after commit);
  - within the check interval (2 s) by re-reading the lease from the database (covers another replica);
  - as soon as the time since the last heartbeat reaches the smaller of the **remaining allowance** and the
    **heartbeat timeout**, minus a 5 s margin (more than twice the ≤ 2 s check interval) — so audio is never carried
    outside the window that heartbeats and the expiry sweeper bill (`SessionService.RelayMayRun`); the first check
    runs right after the relay registers.
* One relay per lease, across replicas: the lease row records the current relay connection; a newer connection
  replaces the previous one immediately in-process and within one check on any other replica.
* Every way a lease ends is billed: stop, replacement and expiry bill the time since the last heartbeat (bounded by
  the timeout) — and since the self-review also **device revocation and admin suspension**, which previously ended
  leases unbilled (revoke-and-re-add would have been a metering bypass).
* Desktop: `CloudSession.RelayEndpoint`, the user's token as bearer, `X-Client-Version`; Pause closes transcription
  explicitly; Resume and automatic lease replacement reconnect (this also fixed a pre-existing defect where a resumed
  session never reconnected the real transcriber). A 401 refreshes the token once; a refusal because the lease
  ended triggers an immediate heartbeat (normal lease-ended handling); "too many connections" or "update required"
  pause listening and end the lease instead of retrying while billed.

Tests: `RealtimeRelayTests` (no secret in any response; only rebuilt audio messages forwarded, bypass attempts
dropped; transcripts relayed; stop, device revocation (billed), admin suspension (billed), allowance exhaustion
between heartbeats and heartbeat timeout all close the relay; a newer connection on another replica stops the older
one; refused for another user's lease, an ended lease and plain HTTP; upstream outage), `RelayEndToEndTests` (the
real desktop `CloudSession` + `OpenAiRealtimeTranscriber` against the relay on Kestrel: transcripts, pause/resume
through a new lease, ended lease stops transcription), updated session/billing tests.

**Inherent costs:** one extra network hop (backend region ↔ OpenAI) on transcription latency, not yet measured on a
consumer connection; backend bandwidth ≈ 64 KB/s of base64 PCM per live session; the host must allow
WebSocket upgrades and long-lived connections (Railway supports both; the relay sends 15 s keep-alives).

## 4. Refunds, reversals and chargebacks

`BillingWebhookProcessor` (Paddle Billing, sandbox):
* `adjustment.created` / `adjustment.updated` are stored per adjustment id (`billing_adjustments`) with a
  **per-adjustment ordering guard** — a late "pending_approval" never overwrites "approved", a late "approved" never
  overwrites "reversed". Duplicate deliveries are absorbed by the event-id primary key (unchanged).
* `transaction.completed` records every payment (`billing_transactions`): subscription, origin, the billing period it
  paid for, and whether money was collected (zero totals and payment-method changes are not payments). Stored even
  when the user is not known yet.
* **Revocation is derived, not toggled:** paid access is revoked while **every** recorded payment of the subscription
  whose billing period covers the current time has an approved **full refund** or an approved **chargeback** that no
  later approved **chargeback_reverse** undid. A refund of an earlier period, a partial refund, a credit, a chargeback
  warning, a rejected/reversed refund, a one-off or zero-value transaction never changes access; a new payment for the
  current period restores it. A refund of a payment that is not on record (e.g. made before rc.3) is logged for
  manual review instead of guessed. Adjustments that arrive before the subscription or user is known are applied
  when they arrive.
* Billing state `revoked` with a localized label and notice (10 languages); entitlement falls back to the free plan.

Tests (`Rc3AccountAndRefundTests`): full refund revokes and a newer payment restores; partial refund/credit/warning/
rejected keep access; chargeback revokes and reversal restores; out-of-order deliveries; duplicate deliveries;
refund of an older period keeps access; a payment-method change does not hide a chargeback; refund of a payment not
on record leaves access alone; adjustment before the subscription; revoked account only gets the trial allowance.

**External:** subscribe the Paddle notification destination to `adjustment.created` and `adjustment.updated`
(checklist in [RC2_COMMERCIALIZATION_READINESS.md](RC2_COMMERCIALIZATION_READINESS.md)); the refund policy itself is
a business decision.

## 5. Real-audio validation beyond sine waves

`scripts/generate-audio-fixtures.py` builds 44 deterministic, CC0 fixtures (no Windows or other third-party audio):
modelled notification chimes (glockenspiel, marimba), critical-stop and buzz error tones, a shutter, keyboard/mouse
clicks, a door knock, instrumental and sung music fragments, and short human-like speech (eSpeak NG, 7 phrases incl.
German and Spanish). Each sound exists in three call paths: direct, a quiet call (pink noise, 100 Hz–7 kHz band
limit, Opus 16 kHz round trip, compression) and a noisy call (fan noise, heavier compression, clipping).
`tests/fixtures/audio/MANIFEST.md` documents provenance, checksums (`--check` regenerates bit-identically) and the
per-file classification.

They exposed three analyser defects, fixed in `AcousticAnalyzer`: a truncated last analysis window per frame,
harmonic bell partials accepted as voice pitch (now checked against sub-multiple periods), and sustained ringing notes
counted as voiced (now "sustained" when the spectrum is unchanged across neighbouring windows).

Result, stated plainly: all 14 speech fixtures are detected as speech (lowest: "Why?"). Of 30 non-speech fixtures, 12
are now rejected acoustically; 18 (two-note chimes, low buzz and critical-stop tones, knocks, music, sung melody)
still contain enough periodic energy to pass as speech. For all 30, typical recogniser output (prompt echo,
"Thank you.", empty, "♪") creates nothing; only if the recogniser turns one of those 18 into a question-like
sentence would it be answered. `RecordedStyleAudioTests` asserts both outcomes per fixture, so improvements and
regressions are visible. The manual real-PC check (checklist G) stays required.

## Additional fixes found while doing this

* Pause/Resume with the real transcriber never reconnected (the loop ended when Resume cancelled the old run token and
  `StartAsync` refused to restart) — fixed, with a regression test (see 3).
* Deleting an interview/profile now deletes its transcription vocabulary on the service (privacy P7).
* Desktop logs are deleted after 14 days (privacy P10).

## rc.3 self-review

An independent adversarial review of the rc.3 changes (relay, identity deletion, refunds) reported eleven findings;
all were fixed in 9f35da4 with tests:

| # | Severity | Finding | Fix |
|---|---|---|---|
| R1 | High | Revoking one's own device (and admin suspension) ended leases without billing — revoke/re-add loop gave unmetered transcription | Billed like a stop under the user lock |
| R2 | High (multi-replica) | One lease could drive relays on two replicas | Relay connection id on the lease row |
| R3 | Medium | "Current transaction" rule: refunds of old payments could revoke; payment-method changes could mask a chargeback | Billing-period-based rule over recorded payments |
| R4 | Medium | Any 404 counted as identity deleted | Only `user_not_found` |
| R5 | Medium | Accounts deleted before rc.3 never queued | Migration backfill |
| R6 | Medium | Exception after the data deletion committed returned 500 | Always "pending", never an error |
| R7 | Medium | Delete + re-signup granted a new trial | Pseudonymous e-mail marker (365 days) |
| R8 | Low–Medium | Duplicate-key / trailing-content messages passed the relay filter with the original bytes forwarded | Strict parse + rebuilt message |
| R9 | Low | Small unbilled windows (allowance margin, check before registration) | Margin on both limits, immediate first check |
| R10 | Low | Desktop retried relay refusals forever while billed; expired token fatal | Refresh once; heartbeat or pause on refusal |
| R11 | Low | Retry batch starvation; secret minted under the user lock | Least-tried first; minted outside the lock |

The same review confirmed: no cross-user relay access, no provider secret in any response, no secrets or e-mail
addresses in new log lines, correct WebSocket send/close handling and clean-up, and correct Paddle field names.

## Remaining blockers outside this repository

Code-signing certificate; Paddle Live business verification and domain approval; legal texts (privacy policy, terms,
billing/refund terms); production secrets `Auth__ServiceRoleKey` and the Paddle `adjustment.*` subscription; Supabase
production settings; database backup policy and restore drill; monitoring/alert rules on the hosting platform; the
manual real-PC checklist ([RC2_MANUAL_REGRESSION.md](RC2_MANUAL_REGRESSION.md)).
