# 2.1.0-rc.5 — readiness UX audit

Scope: the one readiness model and every surface that shows it: the in-app **Pre-interview check** page, **Get
started** (readiness dashboard), the **Interviews** readiness card, the Home title-bar pill and the Live Assistant
indicator. Code: `UiState/Readiness.cs`, `ReadinessVerdict.cs`, `ReadinessPresentation.cs`, `Views/PreInterviewPage`,
`HomeWindow.Readiness.cs`, `HomeWindow.Start.cs`, `MainWindow.Chrome.cs`. Tests: `Rc5ReadinessTests`,
`Rc5ReadinessPageTests`, the in-app self-test (pre-interview and Get started checks).

## The model

- **One store** (`ReadinessStore`, `vm.Readiness`): every surface renders the same `ReadinessSnapshot`; none computes
  readiness on its own. Changes are published under a lock and marshalled to the UI thread.
- **Eight checks**: playback device, audio signal, account and plan, transcription, answer engine, interview prepared,
  prepared answers, window on top. Outcomes: Pass, Warning, Fail, Pending and (rc.5) **Not tested** — an optional test
  only the user can run with real meeting audio, shown neutral, never as a warning.
- **States**: Not checked, Checking, Ready, Ready with warnings, Not ready, Out of date. Only failures block Start.
- **Context**: account | interview | playback device | revision. Saving settings, saving the profile, finishing a
  preparation and choosing another knowledge set bump the revision; a result older than 30 minutes is Out of date too.
  Out of date and Not checked start the checks automatically when the page opens.

## Audit findings and outcome

| # | Finding | Outcome |
|---|---|---|
| H1 | In account mode transcription was always a WARNING, so a working setup could never reach "Ready" — amber everywhere, warnings meant nothing | **Fixed.** Cloud transcription is reported as Not tested; the summary reads "7 of 7 checks passed · 1 optional test to run" and the state is Ready |
| H3 | "Ready" went stale silently (profile save, re-prepare, settings, time) | **Fixed.** Revision in the context + 30-minute limit; every surface shows Out of date |
| M7 | Leaving the page (often through a row's own fix button) threw the run away | **Fixed.** The bounded run (about 15 s) finishes into the store; only closing Home cancels it; a running test tool stops with a "Test stopped" note |
| H1/M1 (error-state) | Offline, an expired sign-in, a captive Wi-Fi page or a cancelled refresh left checks on "Checking…" forever; a cancelled run could stop the next run's signal listener | **Fixed.** Every failure ends the pending checks with a reason; a stale run never touches its replacement; the signal listener is linked to its run |
| L5 (error-state) | Checks could run during a live interview (second lease, typed AI request) | **Fixed.** No auto-run and no run while a session is active ("An interview is in progress…") |
| H2 | The authorized-use acknowledgement could be skipped; Get started was skipped on first run | **Fixed.** Home opens on Get started; every start path (Live Start, hotkey, Get started, Pre-interview Start) asks for the acknowledgement once |
| M9 | Returning users landed on Interviews | **Fixed.** Home opens on Get started |
| M10 | The primary button could stick on "Complete profile"; step 4 could be Done before step 3 | **Fixed.** A knowing prepare without confirmed facts moves on (the step keeps the reminder); the check step is Done only with sign-in and preparation done |
| M11 | Disabled Start gave no reason | **Fixed.** Tool tip: "Fix the failed checks first" / "Run the pre-interview check first" / checks running |
| M1 | Sticky action bar far from the content when maximized | **Fixed.** The bar is as wide as the page and starts where it starts; the status text trims with a tool tip in long languages |
| L11 | Two code paths for the pass count (Live used raw counts) | **Fixed.** One `ReadinessPresentation.Summary` helper for the page, Home and Live |
| M6 | Check copy assumed Chrome, used dBFS and PASS/WARNING | Copy pass in rc.5: meeting-app neutral wording, plain badges; Hz/dB values stay under Technical details |

## Accessibility of the readiness surfaces

The summary card, the check rows and the interview card are UIA groups/list items with their full sentence as name;
the summary is a live region that is actually announced; the Live indicator shows the state icon (check, warning,
cross, clock), not a colour-only dot. Details: `RC5_ACCESSIBILITY_AUDIT.md`.

## Not automated

Real meeting audio, real devices and the network failure paths on a real PC are rows H, I and S of
`RC5_MANUAL_REGRESSION.md`.
