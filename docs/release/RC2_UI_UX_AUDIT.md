# 2.1.0-rc.2 — UI/UX and accessibility audit

Method: static review of every window, dialog, template and theme token (XAML and code; WPF cannot run on the CI
Linux runner), WCAG 2.x contrast computed from the theme colours, then fixes. Runtime coverage comes from the
Windows CI self-test (all windows opened, English plus Persian and Hindi visual-tree scans, auto-fit checks) and the
real-EXE smoke test. **Not done:** a screen-reader session with NVDA/Narrator and visual checks at every
resolution/DPI on real hardware — listed in the manual checklist.

## Fixed in RC2

| Area | Finding (severity) | Fix |
|---|---|---|
| Contrast | Labels/hints colour 3.0–3.96:1 (High) | `TextFaint` #8B92A0 → 4.6–6.0:1 on every surface |
| Contrast | White on accent buttons 3.23:1 (Medium) | Primary buttons on #3567D0 (5.25:1) |
| Contrast | Input borders 1.33:1 (Medium) | #5C6577 input border (≥ 3:1) |
| States | Inputs had no disabled/hover state; multi-line text vertically centred (Medium) | Disabled opacity, hover border, `VerticalContentAlignment` honoured |
| Focus | Default dotted focus rectangle invisible on the dark theme (Medium) | Accent focus ring for check boxes, sliders, list items, expanders |
| Keyboard | Coach/Answer toggle, Adaptive badge and service banner were mouse-only borders (High) | Real buttons with names |
| Keyboard | Settings: no Enter/Escape; Pre-interview check and first-run intro: no Escape (Medium) | Default/cancel buttons, Escape closes |
| Screen readers | Toasts not announced (High) | UI Automation notification per toast, polite live region on the status line |
| Screen readers | Unnamed settings controls; no headings (Medium) | Names on every settings control; heading levels on page titles and sections |
| Layout | Live window header overflowed at the default width; history navigation vanished on narrow windows (High/Medium) | Wider default (720), trimming on status/Start label, Grid layout for the action bar |
| Layout | Settings and Pre-interview check taller than the work area at 1366×768 or 150 % scaling (High) | Fitted to the work area of the monitor they open on, scrollable, tools wrap |
| Layout | Wide screens: centred content with a gap after the navigation (Low) | Content left-aligned with sane max widths |
| Layout | Updates row and account badge clipped in long languages (Medium/Low) | Flexible widths, trimming |
| RTL | System message boxes not RTL in Persian/Arabic (Medium) | RTL reading order when the UI language is RTL |
| RTL | Interviewer question followed the UI direction (Medium) | Question and live transcript follow their own script |
| Selection | Export/Delete/Open/Copy/Remove enabled with nothing selected (Medium) | Enabled only with a selection |
| Empty states | No hint without profiles or interviews (Medium) | Empty-state hints |
| Data safety | Unsaved profile edits discarded silently (Medium) | Confirmation before discarding |
| Copy | Preflight ended with "[Disconnected]" after a successful test | "Test complete · transcription test passed"; PASS / WARNING / FAIL badges with remediation |
| Copy | Settings transcript wording | "Save the interview transcript to Documents when the session ends" |
| Toasts | Product-wide notifications (title + detail, kinds, de-duplication, max 3) incl. "Interview started", connection lost/restored (persistent banner while offline), device lost/back, report saved, profile saved, preparation complete | — |
| Account | Quota shown as remaining this period / available today with reset time; device list shows the full version incl. `-rc.2`; build channel vs update preference separated; Installed vs Portable detected from the install layout | — |
| Help & About | Product, version, channel, build, installed/portable, signing state, service environment; copy build info | — |

## Open (not fixed in RC2)

| Finding | Severity | Note |
|---|---|---|
| Icon glyphs (‹ › send) do not mirror in RTL | Low | Layout mirrors; glyph direction does not |
| Toasts can cover the typing box for a few seconds in the compact live window | Low | Dismiss by click |
| Tooltips show default hotkeys after the user changes them | Low | Static strings |
| First run shows the live introduction and Home onboarding together | Low | Both dismissible |
| Window restore on a secondary monitor with a different DPI | Low | Dialogs are fitted per monitor; the main windows use the primary work area for restore |
| Diagnostics panel is English | Low | Support data |
| Screen-reader walkthrough and per-resolution visual checks | — | Manual checklist (not automatable on CI) |

## rc.3

| Surface | Change | Check |
|---|---|---|
| Account page | New billing state "revoked" with its own label and notice; the upgrade banner appears for it like for an ended subscription | build + localization tests |
| Delete account | Dialog lists what is deleted including the sign-in; result message distinguishes "deleted" from "sign-in removal finishing automatically" | localization tests, manual checklist |
| Live window | Pause/Resume reconnect; a refused live connection (too many connections, update required) pauses listening with a readable note instead of a silent failure | `RelayEndToEndTests`, manual checklist X/AA |
