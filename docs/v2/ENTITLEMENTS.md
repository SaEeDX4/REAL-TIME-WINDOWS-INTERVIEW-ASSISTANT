# Entitlements, usage metering and safety caps

Status: **IMPLEMENTED + TESTED** (`tests/InterviewAssistant.Backend.Tests`, real PostgreSQL). Plans are `trial` (Trial) and `pro` (Pro, Paddle sandbox price `USD 19.99 / month`). The legacy code `pro-dev` is mapped to `pro` (catalog `LegacyCodes` + data migration `ProductionHardening`).

## Current numbers (unchanged from the previous catalog — only codes/names were fixed)
| | Trial | Pro |
|---|---|---|
| Live minutes | 30 lifetime | 600 per billing period |
| Candidate profiles | 1 | 3 |
| Preparation runs / month | 2 | 20 |
| Concurrent sessions | 1 | 1 |
| Devices | 1 | 2 |
| Report retention | 14 days | 365 days |
| Coach mode | yes | yes |

Unit economics to review before live launch (estimates — verify against current OpenAI and Paddle price lists):
a fully used Pro month is 600 min of `gpt-4o-transcribe` realtime audio (≈ USD 0.006/min → ≈ USD 3.60) plus answer
generation with `gpt-5.4-mini` (short, capped at 600 output tokens each) plus Paddle's fee (≈ 5 % + USD 0.50). That
leaves a positive margin at USD 19.99, but the 240 min/day per-account cap and the global daily caps below are the
backstop against abuse. Changing numbers is a configuration change (`PlanCatalog`), not a code change.

## Billing state (server-computed, `GET /api/v1/me` → `billingState`)
`trial`, `trial_exhausted`, `active`, `canceling` (paid until period end), `past_due` (paid features kept for
`Paddle:PastDueGraceDays`), `paused`, `expired`. The app shows plain-language text for each and never decides access itself.

## Principle
The server is the single source of truth. The desktop only *displays* entitlements from `GET /api/v1/me`; every limit is enforced on the server. A modified client cannot grant itself features (`ClientCannotGrantItselfPaidFeatures`).

## Plan catalog (`PlanCatalog` in configuration — data-driven, no code change to re-price)
| Field | Meaning |
|---|---|
| `MaxProfiles` | Candidate profiles stored server-side (402 `profile_limit`) |
| `PrepJobsPerMonth` | Preparation runs per calendar month (402 `prep_quota`) |
| `LiveMinutesPerPeriod` / `TrialMinutes` | Live interview minutes per billing period / lifetime trial |
| `MaxConcurrentSessions` | Active leases (409 `concurrent_limit`) |
| `MaxDevices` | Registered installations (403 `device_limit`) |
| `ReportRetentionDays` | Reports hidden after expiry, purged hourly |
| `CoachMode` | Feature flag |

## Live-minute metering (session leases)
- `POST /sessions` → lease + ephemeral OpenAI Realtime client secret (TTL 600 s). Idempotency key makes retries return the same lease.
- One lease per device: restarting on the same device replaces its stale lease; leases on other devices count against `MaxConcurrentSessions`. Start/heartbeat/renew/stop are serialized per user with a PostgreSQL advisory lock (race-tested).
- Client heartbeats every 30 s with a strictly increasing `seq` (replays → 409).
- Billed seconds per heartbeat = elapsed since last heartbeat, **capped at 2× the interval** (no over-billing after sleep/offline).
- No heartbeat for 90 s → lease expired by the sweeper; credential renewal then fails.
- Lease ends automatically at: allowance exhausted, per-account daily cap, `MaxSessionMinutes` (120), device revoked, account disabled.

## Safety caps (operator-level, `Safety` section)
Per-account daily live minutes (240), global daily live minutes, answers/minute per user (rate limiter), answers/day per account and globally, max answer tokens (600), max document size (2 MB), credential renewals per session.

## Usage ledger
Append-only `usage_ledger` rows (`live_seconds`, `answer_request`, `prep_job`). Summaries are computed from the ledger, never from client counters.
