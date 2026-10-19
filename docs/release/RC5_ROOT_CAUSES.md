# 2.1.0-rc.5 — root causes

Every rc.4 finding addressed in rc.5 — from the rc.4 manual test and from the thirteen independent rc.5 audits — with
the cause found in the code, the fix and the guard. rc.4 (`a0bc946`) artifacts are unchanged; rc.5 is a new version.

## P0 — a real "Tell me more." was not answered (rc.4 regression)

| Symptom | Root cause | rc.5 fix | Guard |
|---|---|---|---|
| A flat, falling "Tell me more." / "Go on." / "Elaborate." after an answer produced nothing | (1) rc.4's held-tone test (≥ 10 pitch pairs, ≥ 65 % adjacent-hop steadiness) also matched level declarative speech; short rising asks escaped it, which is why the rc.4 fixtures passed. (2) Speech resuming within 2.5 s reopened and merged into the previous turn. (3) One-word imperatives were dropped as non-questions; "Go on." matched no rule | Pitch runs (`PitchRunTracker`): speech drifts over a run, a tone stays inside 2 %; a follow-up **grammar** (`FollowUpIntent`: an elicitation verb in imperative position) instead of a phrase list; a standalone follow-up after a completed answer is its own turn (`UtteranceKind.FollowUp`) | `Rc5FollowUpTests` (flat-intonation fixtures, quick follow-ups, panel cases, the same phrases over non-speech) |
| The independent engine audit then found regressions of the new rules (short buzz/piano clips heard as voice, one-word follow-ups over a knock or a sung melody, statements such as "Continue to the next slide." answered, a panel hand-off plus "Go on." answered, quick multi-part questions split, a repeated follow-up suppressed) | The new acoustic rule dropped the rc.4 fallback when fewer than 10 run windows existed; the follow-up grammar had no complement constraint and treated its own verbs as anaphora; the split ignored timing | Fixed in rc.5: below 10 run windows the rc.4 held-tone rule decides again; very short imperatives need strong acoustic support and a real voiced stretch; complement rules for go on/continue/carry on; a hand-off makes the next bare follow-up other-directed; a follow-up splits off only after about 2 s of silence; a repeated follow-up after its answer is answered again. Before/after table and residual risks in `RC5_ENGINE_AUDIT.md` | `Rc5EngineAuditTests`, `Rc5FollowUpTests.SamePhrasesOverShortNonSpeech_CreateNothing`, `…ShortAndPulsingHeldTones_AreNotVoiceLike` |

## Readiness — "Ready" meant different things in different places

| Symptom | Root cause | Fix | Guard |
|---|---|---|---|
| Get started, Interviews, the pre-interview window and the Live window could disagree | Each surface computed readiness from its own inputs; the check ran in a separate window | One `ReadinessStore`; the check is an in-app page; every surface renders the same snapshot | `Rc5ReadinessTests`, self-test |
| Account mode never reached "Ready" | Transcription could only be proven with real meeting audio and was reported as a WARNING | Outcome **Not tested** (optional, neutral) | `CloudTranscriptionNotTestedYet_IsStillReady…` |
| "Ready" from the morning still showed in the afternoon / after settings changes | The context was only account, interview and device | Revision (settings, profile, preparation, knowledge) + 30-minute limit | `AnOldResult_IsOutOfDate`, `SavedSettingsOrProfile_MakeTheResultOutOfDate…` |
| "Checking…" forever after a network problem | Only cancellation was caught; auth-server, HTML and JSON errors escaped `RunAllAsync` / `RefreshAsync` | Every failure ends the pending checks with a reason; refresh failures become ServiceUnavailable | error-state tests |

## Found by the Windows self-test during rc.5

| Symptom | Root cause | Fix | Guard |
|---|---|---|---|
| The in-app self-test hung after the first typed answer (every push from run 76) | Live auto-fit with an Auto vertical scroll bar: an answer near the fit boundary toggled the scroll bar, the width change raised SizeChanged, which scheduled another fit — an endless Loaded-priority loop that kept the CPU busy and starved all background and idle work. A real product bug, not only a test problem | `FitAnswer` remembers the inputs of the last fit (viewport height, the scroll viewer's outer width, the visible answer content, font preset) and does nothing for the same inputs | `AutoFit_NeverRefitsTheSameInputs`; the self-test now has a per-check limit and a watchdog that names a starved step |
| Starting while signed out could wait on the acknowledgement dialog | The acknowledgement was asked before the sign-in precondition | Asked only when listening can start | `AuthorizedUse_IsAskedOnlyWhenListeningCanStart` |

## Navigation, first run, account

| Symptom | Root cause | Fix |
|---|---|---|
| The Live gear flashed Interviews/Get started before Settings | Home showed its default page, then navigated | Routes resolved before the window shows (`HomeRoute`) |
| The authorized-use acknowledgement could be skipped | Only Get started's button checked it; first run opened on Account | Home opens on Get started; every start path asks once, after the sign-in precondition |
| A password reset could replace the signed-in account | The reset listener outlived the reset screen | `PendingReset` tickets; no code exchange while another account is signed in |
| Updates under Account, no reset of preferences | Settings had grown page by page | Settings categories, Updates inside Settings, Reset section / Restore all defaults |

## Exports and data

| Symptom | Root cause | Fix |
|---|---|---|
| "Export my data" was a raw JSON dump | One export path for machines and people | Readable HTML (recommended) + raw JSON; profile backup `.iaprofile` with preview and import |
| Malformed backups crashed the import; the secret filter failed open on deep input | Validation trusted typed deserialisation; depth limit returned the input | Strict validation, fail-closed filter, atomic `.tmp` writes (`RC5_SECURITY_PRIVACY_AUDIT.md`) |

## Accessibility and localization

| Symptom | Root cause | Fix |
|---|---|---|
| Narrator silent on cards, rows and meters; live regions never announced | WPF panels/Borders have no automation peer; LiveSetting needs a LiveRegionChanged event | `AccessibleBorder`, `LevelMeter`, `v:LiveRegion` + XAML rule tests |
| Arabic billing dates on the Hijri calendar; "il domenica" in Italian exports | ar-SA defaults to Umm al-Qura; long date pattern includes the weekday | Gregorian formatting culture for Arabic; weekday-free long dates |
| Mixed-language check details after a language switch | Results stored as finished strings | Key + arguments remembered and re-formatted |
