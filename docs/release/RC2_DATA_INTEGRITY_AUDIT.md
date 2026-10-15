# 2.1.0-rc.2 — data-integrity audit

| # | Area | Sev | Finding | Root cause | Fix | Test | Remaining risk |
|---|---|---|---|---|---|---|---|
| D1 | Workspace store | Medium | One corrupt or undecryptable file hid every profile/report and blocked deletion | `Read<T>` had no error handling | Damaged file renamed to `*.corrupt` (kept for support), skipped, user notified (888c56a) | `WorkspaceStoreTests.DamagedFile_IsQuarantinedAndEverythingElseStaysVisible` | — |
| D2 | Workspace store | Low | Power loss could leave an empty file after the atomic rename | No flush before `File.Move` | Write-through + `Flush(true)` before rename (888c56a) | — | `active.json` (ids only) is not atomic; corruption is tolerated. |
| D3 | Reports | Medium | Closing the app during an interview could lose the report (network cleanup ran first) | Ordering in `DisposeAsync` | Report and opt-in transcript saved locally first (888c56a) | — | — |
| D4 | Reports | Low | File-name collisions | Minute-resolution names | Session-unique names; encrypted store uses GUIDs | — | — |
| D5 | Reports | Low | Wrong start time after an empty session | Early return | Start time reset (888c56a) | — | — |
| D6 | Profile facts | Medium (RC1) | Header "4 awaiting review" vs footer "0 to review" | Two counts | One `FactStats` (RC2 earlier commit) | `FactStats_HeaderListAndFiltersAgreeAfterEveryChange` | Personal/boundary answers are counted in the profile list but shown in their own tab (wording, not truth). |
| D7 | Migration | Low | Duplicate document ids made a profile unloadable | `ToDictionary` | Grouped lookup (888c56a) | `MigrationTolerates_DuplicateDocumentIds` | — |
| D8 | Account deletion | Medium | Six separate deletes, no transaction, outside the user lock | — | One transaction under the per-user advisory lock (cba6cb8) | `PausedSubscription_BlocksAccountDeletion_AndNothingIsDeleted` (rollback) | — |
| D9 | Quotas | Low | Daily answer/typed/prep caps are check-then-insert | No serialisation | Unchanged | — | Concurrent requests can exceed a daily cap by a few units (bounded by the per-minute limiter). |
| D10 | Lease sweeper | Low | Raced heartbeats without the lock | — | Sweeper takes the user lock and re-checks (cba6cb8) | `HeartbeatJustAfterTheSweepCutoff_WinsAgainstTheSweeper` | — |
| D11 | Webhooks | Low | Missing `occurred_at` defeated ordering | Fallback to "now" | Rejected (400) (cba6cb8) | `WebhookWithoutOccurredAt_IsRejected` | — |
| D12 | Session continuity | High (RC1) | Pause/resume reset history | Engine rebuilt by Settings | No rebuild during a session | `PauseResumeTwice_OneContinuousSession_SevenOfSeven` | — |
| D13 | Variants | High (RC1) | New history entries / wrong item | — | In-place replacement of the displayed item; question record reused (`VariantCount`) | `VariantOnOlderItem_…`, soak | — |
| D14 | Suppressed turns | High (RC1) | Fake turns in memory/analytics | — | Never recorded; report re-filters and discloses a count | soak, `Rc2ReportTests` | — |
| D15 | Account switching | Medium | Switch during a session | — | Stop first, then switch (888c56a) | `AccountWorkspaceIsolationTests` | — |
| D16 | Unsaved edits | Medium | Switching profile, refreshing or closing Home discarded profile edits silently | Reload on selection | Confirmation before discarding; refreshes keep edits on screen | — | Interview fields are saved by Prepare only. |
| D17 | Truth boundary | — | Verified intact: only `UserConfirmed` facts reach the engine and preparation; editing a fact drops confirmation unless re-confirmed; migration never changes confirmation | — | — | `ProfileIngestionTests`, `AnswerQualityEvaluationTests` | — |
| D18 | Cloud/local sync | Info | Only per-interview transcription vocabulary is uploaded; it is not deleted with the interview | — | Documented (privacy P7) | — | — |
| D19 | Migrations | Info | EF model vs migrations | — | `dotnet ef migrations has-pending-model-changes` in `pr-validation` CI | CI | — |
| D20 | Variants | High | A cancelled variant wiped the original answer (history and report) | Original restored, record untouched (796de96) | `VariantCancelledByTheNextQuestion_…` | — |
| D21 | Account switching | Medium | A failure while opening a workspace could leave the previous account's knowledge/workspace in place; during a deferred switch Home could show or change the previous account's data (incl. retention) | Previous workspace closed first, new one published only after loading, failures show no account's data; Home treats the workspace as unavailable while it does not belong to the signed-in account (796de96) | `AccountWorkspaceIsolationTests` | — |
| D22 | Account page | Medium | Plan, usage and devices of the previous account could stay on screen | Cleared whenever no account data is loaded; late device responses for another account ignored (796de96) | — | — |
| D23 | Preparation | Low | Unexpected errors left an interview "preparing" | Any failure ends as failed with a readable reason (796de96) | — | — |
| D24 | Legacy adoption | Low | A partially failed pre-RC2 adoption could later be completed by another account | Unchanged | — | Rare (requires a file-move failure); NEEDS WORK: write the owner marker before moving. |

