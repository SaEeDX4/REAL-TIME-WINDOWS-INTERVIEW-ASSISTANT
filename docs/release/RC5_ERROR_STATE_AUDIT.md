# 2.1.0-rc.5 — error-state and recovery audit

Scope: what happens when things go wrong in the rc.5 code paths: network loss, an expired sign-in that cannot be
renewed, captive Wi-Fi pages, cancellations, disk full, closed windows, concurrent runs. Method: an independent
read-only review, then fixes with tests. No path crashes the app (`App.xaml.cs` handles UI and background-task
exceptions); the review looked for states that get stuck and messages that are wrong.

## Findings and outcome

| # | Finding | Outcome |
|---|---|---|
| H1 | Pre-interview check stuck on "Checking…" when the sign-in renewal failed or the service returned an HTML login page; Account summary stuck on "Loading" | **Fixed.** `RunAllAsync` ends every pending check of its run with a reason; `CloudService.RefreshAsync` turns auth-server, JSON and transport errors into ServiceUnavailable |
| H2 | A cancelled or timed-out refresh left "Connecting…"; Help health check stuck on "Running…" | **Fixed.** Cancellation restores the previous state; the health check has a final status ("The check could not finish…"), and a refresh cut off by its 10 s limit counts as unreachable |
| M1 | A cancelled run could stop the next run's audio listener | **Fixed.** Generation guard after the local checks; the listener is linked to its own run |
| M2 | Update status stuck on "Checking for updates…" for unexpected exception types | **Fixed.** Final catch, type name logged |
| M3 | Closing Home during a pending password reset left the flow waiting on a closed window | **Fixed.** Closing Home cancels the reset; a dialog requested on a closed window answers Cancel instead of waiting forever |
| M4 | "Local data deleted" shown when deletion failed | **Fixed.** The folder that could not be removed is named |
| L1 | Readiness store not thread-safe | **Fixed.** Publish and report under one lock (`ConcurrentReports_AreNeverLost`) |
| L2 | "Settings saved" shown when the write failed | **Fixed.** `AppSettings.Save` returns false; Save and Restore defaults then say "Settings could not be saved" (they apply now but not after a restart) |
| L3 | Half-written export files kept the real name | **Fixed.** `.tmp` then move |
| L4 | Account export with a malformed response escaped | **Fixed.** `exp_err_account_export` |
| L5 | Checks and test tools could run during a live interview | **Fixed.** Blocked with a plain note |
| L6 | Start interview with unsaved edits | **Fixed.** Unsaved edits are asked about first |
| L7 | Raw English provider errors in developer mode | **Open** (developer builds only; the shipped account mode uses localized copy) |
| L8 | A Settings device preview marks readiness Out of date until Save/Discard | Kept: the previewed device is a different device; Discard restores the result |
| L9 | Leftover partial tool output after leaving the page | **Fixed.** "Test stopped because you left the page." |

## Verified fine

"Run all" cannot run twice; results for an old account, interview or device never reach the current result; signing
out mid-run clears it; store updates are marshalled to the UI thread; Home detaches all listeners on close; update
checks cannot overlap; Restore defaults keeps an unsaved device preview correct; closing Home with a dialog open is
refused; an unreadable settings file falls back to defaults; a failed backup import is rolled back.
