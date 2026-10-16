# 2.1.0-rc.3 — manual regression checklist (A–V, W–AB)

Run on a real Windows 10/11 PC with speakers or a headset, signed in to a **disposable test account** (never delete
the real manual-test account). Mark each line Pass/Fail with a note. Automated coverage that already exercises the
same behaviour is listed so failures can be cross-checked; automated tests do **not** replace this run (they use
simulated audio and cannot judge real voices, real Windows sounds or visual quality).

| # | Steps | Expected | Automated cross-check | Result |
|---|---|---|---|---|
| A | Install `InterviewAssistant-Setup-v2.1.0-rc.3.exe`; open Help & about | Version 2.1.0-rc.3 · Release Candidate · Build &lt;commit&gt; · Installed · Unsigned | CI install/update/uninstall, `InstallLayoutTests`, self-test build info | |
| B | Sign in | Signed in; account badge; only this account's data | `AccountWorkspaceIsolationTests`, `CloudAuthTests` | |
| C | Switch UI to Persian; check Home, Account, Help, Live Assistant, Pre-interview check, Settings | No major English leftovers; real RTL; e-mail/version/hotkeys stay LTR | `LocalizationCoverageTests`, self-test "UI localisation (fa)" | |
| D | Switch UI to Hindi; same surfaces | No major English leftovers | self-test "UI localisation (hi)" | |
| E | Return to English | All windows update live without restart | `LiveLanguageSwitch…` | |
| F | Profile → Interview profile tab: add a hobby, Save, restart the app; ask "What are your hobbies?"; then clear/unconfirm the hobby, Save, ask again | 1st: answer uses the confirmed hobby. 2nd: honest prompt to add one — nothing invented, no model call | `AnswerQualityEvaluationTests.PersonalQuestions…`, `Hobbies_UseOnlyConfirmedProfileFacts` | |
| G | While listening, play Windows notification, error and screenshot sounds (several times) | Zero questions, zero answers; history unchanged | engine noise scenarios, RC2 soak | |
| H | Play 60–120 s of interviewer context ending with one question | Window stays responsive; the core question is shown with a short context line; one answer | `FiveMinuteMonologue…`, `LongContextEndingInTwoPartQuestion…` | |
| I | Ask a two-part question with a 0.5–1 s pause | One question, one answer | `TwoPartQuestionWithNaturalPause…` | |
| J | Panel recording: one interviewer asks another ("John, what do you think?") | No candidate answer | `Panel_InterviewerAsksAnotherInterviewer_NoAnswer` | |
| K | Panel: a candidate-directed question | One correct answer | `Panel_CandidateAddressedByName_IsAnswered` | |
| L | "Have you personally built X?" where the profile has no evidence | Honest bridge first, then verified adjacent experience in the past tense | `DirectExperienceGap…` | |
| M | Hypothetical version ("How would you build X?") | Confident "I'd…" answer | engine tests | |
| N | Navigate to an older question (‹), click Technical | Same position (e.g. 5/8 stays 5/8), same question, answer replaced, count unchanged | `VariantOnOlderItem…`, self-test | |
| O | Pause; type a question | Answer works; live minutes do not decrease | backend `PausedAfterLiveSession…PauseBillsNothing` | |
| P | Resume; ask a new spoken question | History continues (e.g. 4/4 → 5/5), not reset | `PauseResumeTwice…SevenOfSeven` | |
| Q | Stop | Toast "Report saved"; new report appears immediately in Reports with every question | `Rc2ReportTests`, self-test | |
| R | Turn Wi-Fi off during a session, then on | "Connection lost" toast and persistent banner; then "Connection restored"; no restart needed | client reconnect tests | |
| S | Pre-interview check: Test audio, Test transcription | PASS badges, "Test complete · transcription test passed", READY FOR INTERVIEW; never ends with "Disconnected" | self-test preflight window | |
| T | Account page | Remaining this period, available today (with reset), device list with full version `2.1.0-rc.3`, build channel vs update preference | backend `ReleaseCandidateClientsPassTheGate…` | |
| U | Restart the app | Still signed in; same profile and interview; no other user's data | `AccountWorkspaceIsolationTests` | |
| V | Sign out; then sign in again | Signed out: no profile/interview/report visible. Signed in: data restored | self-test signed-out gate | |

## Additional checks for RC2

| Steps | Expected | Result |
|---|---|---|
| Uninstall via Settings → Apps | Clean removal; reinstall works | |
| 1366×768 at 100 % and 1920×1080 at 150 %: open Settings and Pre-interview check | Fully on screen, Save/Start reachable | |
| Narrator: Tab through the live window; trigger a toast | Every control named; toast announced | |
| Delete the disposable account (re-enter password) | Service data deleted; optional local wipe works; "Your account and your sign-in were deleted"; signing in again with the same e-mail and password fails (sign-in removed); signing up again with it gives no new trial | |

## Additional checks for rc.3 (live relay, billing, deletion)

| # | Steps | Expected | Automated cross-check | Result |
|---|---|---|---|---|
| W | Start listening; ask three spoken questions; compare how fast the first suggestion appears with rc.2 on the same PC/network | Transcription works; added delay small (note the seconds) | `RelayEndToEndTests` (functional only; latency not measured on consumer networks) | |
| X | Pause for 2 minutes; check Account minutes; Resume and ask a question | Minutes do not decrease while paused; transcription resumes without restarting the app | `RelayEndToEndTests`, `PauseResume…` | |
| Y | While listening, remove this PC on the Account page from a second device (or revoke via the web) | Listening stops within seconds with "device removed"; minutes used up to that moment are counted | `RevokingYourOwnDevice_EndsTheLeaseBilledLikeAStop` | |
| Z | With a sandbox Pro test account: refund the subscription payment in full from the Paddle sandbox dashboard | Account shows "Pro ended — payment refunded"; live minutes fall back to the trial | `ApprovedFullRefund_RevokesPaidAccess…` | |
| AA | Install rc.2 on a second PC (or keep it), try to start listening against the rc.3 service | Clear "update required" message; typed questions still work | `OldClientsThatWouldHoldAProviderSecret_MustUpdate` | |
| AB | Disconnect Wi-Fi for 2 minutes during listening, then reconnect | Listening resumes by itself (new live session) or shows a clear note; no transcription while disconnected is billed beyond the timeout | `RelayClosesAtTheHeartbeatTimeout…` | |
