# Entitlements, usage metering and safety caps

Status: **IMPLEMENTED + TESTED** (`tests/InterviewAssistant.Backend.Tests`, real PostgreSQL). Prices/plan names are development seeds — **final pricing is a business decision, not set here.**

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
| `CoachMode`, `AdvancedReports`, `CustomAnswerModes`, `ModelTier`, `MaxLanguages` | Feature flags |

## Live-minute metering (session leases)
- `POST /sessions` → lease + ephemeral OpenAI Realtime client secret (TTL 600 s). Idempotency key makes retries return the same lease.
- Client heartbeats every 30 s with a strictly increasing `seq` (replays → 409).
- Billed seconds per heartbeat = elapsed since last heartbeat, **capped at 2× the interval** (no over-billing after sleep/offline).
- No heartbeat for 90 s → lease expired by the sweeper; credential renewal then fails.
- Lease ends automatically at: allowance exhausted, per-account daily cap, `MaxSessionMinutes` (120), device revoked, account disabled.

## Safety caps (operator-level, `Safety` section)
Per-account daily live minutes (240), global daily live minutes, answers/minute per user (rate limiter), answers/day per account and globally, max answer tokens (600), max document size (2 MB), credential renewals per session.

## Usage ledger
Append-only `usage_ledger` rows (`live_seconds`, `answer_request`, `prep_job`). Summaries are computed from the ledger, never from client counters.
