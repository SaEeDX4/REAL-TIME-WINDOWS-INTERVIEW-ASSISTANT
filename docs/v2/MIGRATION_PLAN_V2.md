# Migration Plan V2 — phases, gates, rollback

| Phase | Deliverable | Gate (must pass) | Rollback |
|---|---|---|---|
| A | .NET 10, docs, baseline commit `013fbc6` | 88 baseline tests on net10 | revert TFM commit |
| B | Profiles/targets, ingestion, preparation, Shervin → fixture, brand neutral | profile/target/ingest/prep tests; old engine tests unchanged | prototype knowledge loader kept as fixture loader |
| E | Coach mode, memory, anti-repetition, interruption, languages | live-behaviour tests, soak | feature flags (`coach`, `adaptive`) |
| F | Reports + HTML export | report tests | report is additive |
| C/D | Backend, auth, leases, metering, Paddle | backend integration tests vs Postgres, webhook signature/idempotency tests | backend is new service; desktop Developer mode still works |
| G | Desktop cloud mode + UX redesign + localization | Windows CI self-test covers new windows | Developer mode + legacy overlay path |
| H/I | Velopack Setup.exe, update N→N+1 test, signing (gated) | Windows CI install/update test | ZIP artifact still produced |

Risks: external accounts (Paddle, Supabase, OpenAI server key, hosting, signing) — implemented to the boundary, validated with contract tests/mocks, marked NOT YET VALIDATED until credentials exist.
