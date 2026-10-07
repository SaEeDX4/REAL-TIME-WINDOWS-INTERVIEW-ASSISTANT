# Testing — Commercial V2

Every claim below is backed by an automated test or a CI step. Items not exercised are listed as NOT YET VALIDATED.

## Suites
| Suite | Runs on | Count | What it proves |
|---|---|---|---|
| `tests/InterviewAssistant.Tests` | Linux (pr-validation) + Windows (windows-release) | 201 | Engine, ingestion & upload security, preparation pipeline, truth boundary, multilingual detection, Coach/memory/interruption, reports (+ round-trip), workspace encryption, cloud client auth (PKCE, loopback, token store), UI strings, update policy, 60-min simulated soak, repository secret scan |
| `tests/InterviewAssistant.Backend.Tests` | Linux + PostgreSQL 16 service | 44 | Real ASP.NET pipeline + real migrations: auth (6 forged-token kinds), isolation, entitlements, leases/heartbeats/metering, caps, answer proxy (server key + server model), rate limits, Paddle webhooks (signature, replay, idempotency, ordering, grace, cancellation), checkout/portal, kill switch, maintenance, min version, admin, export/delete, readiness; desktop client end-to-end against this backend |
| In-app `--selftest` | Windows, real EXE | ~30 checks | XAML of all windows, manual-question UI, Coach toggle, auto-fit without scrolling, Persian RTL switch, Home pages, local profile → prepare → load in an isolated DPAPI workspace, report on Stop, DPAPI, settings, hotkeys, audio enumeration |
| `scripts/smoke-test.ps1` | Windows | — | EXE launches, window appears, stays alive, WM_CLOSE → exit 0, no fatal errors |
| `scripts/update-test.ps1` | Windows | — | Setup.exe silent install of N, self-update to N+1 from a feed, N+1 runs, uninstall |
| pr-validation extras | Linux | — | Migration drift, vulnerable packages, committed-secret scan, backend container build |

## Run locally
```
dotnet test tests/InterviewAssistant.Tests
IA_TEST_PG="Host=localhost;Username=ia_test;Password=ia_test" dotnet test tests/InterviewAssistant.Backend.Tests   # needs PostgreSQL
pwsh scripts/build-release.ps1; pwsh scripts/smoke-test.ps1; pwsh scripts/selftest.ps1          # Windows
pwsh scripts/update-test.ps1                                                                      # Windows
```

## NOT YET VALIDATED (needs external accounts or a human on Windows)
- Real Supabase sign-in (OAuth + magic link) and JWKS validation against a live project.
- Real Paddle sandbox checkout, portal, and webhook delivery.
- Real OpenAI ephemeral transcription session created by the server and used by the desktop.
- Live answer quality in all 10 languages with a real model (gated test exists; needs `OPENAI_API_KEY` secret).
- Code signing; SmartScreen reputation.
- Staging/production deployment and smoke tests (workflow ready, needs hosting + secrets).
- Visual review of the new screens and real-interview latency on a user PC.
- Native-speaker review of UI strings and report labels.
