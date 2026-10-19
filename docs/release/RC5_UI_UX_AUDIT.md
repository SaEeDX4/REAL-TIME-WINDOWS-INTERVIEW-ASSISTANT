# 2.1.0-rc.5 — UI/UX audit

Scope: a screen-by-screen review of the rc.5 desktop app (Get started, Pre-interview check, Profiles, Interviews,
Reports, Settings, Account and sign-in, Help & about, the Live Assistant and its popovers, dialogs). Method: an
independent read-only review of the XAML and code (WPF cannot run on the review host, so layout figures assume Segoe UI
metrics), then fixes; Windows CI runs the in-app self-test on a real desktop.

## Per-screen verdict after the fixes

| Screen | Purpose clear in 3 s | One primary action | Notes |
|---|---|---|---|
| Get started | yes | yes (the next step) | now the landing page for first and later visits; acknowledgement on top on first run |
| Pre-interview check | yes | yes (Start interview) | reaches Ready in account mode; never stuck on Checking; bar aligned with the page |
| Interviews | yes | yes | Prepare is primary until prepared, then Start interview; New interview is secondary |
| Profiles | yes | yes (Save profile) | Import resume is secondary in the list pane |
| Account | yes | yes | two columns at the default window size |
| Settings | yes | yes (Save) | Restart to update is secondary next to Save |
| Help & about | yes | n/a | standard 36 px buttons; health check renamed "Check app health" |
| Reports | yes | n/a | unchanged from rc.4 (empty and select states fine) |
| Live Assistant | yes | yes (Start) | calm toolbar, no diagnostics, Quick help, Maximize, Interview Focus |

## Findings and outcome

High: H1 (readiness never Ready), H2 (acknowledgement skippable), H3 (stale Ready) — **fixed**, see
`RC5_READINESS_UX_AUDIT.md`.

| # | Finding | Outcome |
|---|---|---|
| M1 | Sticky bars not aligned with the page | Fixed |
| M2 | Two primary buttons per view (Profiles, Interviews, Updates) | Fixed |
| M3 | Account one column at the default size | Fixed (breakpoint 720) |
| M4 | No type ramp; heading sizes drift; two Level-1 headings per page | **Open** — documented in `RC5_DESIGN_SYSTEM_AUDIT.md`; no visual regression, a refactor for after rc.5 |
| M5 | Mixed button heights and destructive styles | Partly fixed: Help uses the form height. Outline-danger style for page-level deletes and a compact form button are **open** |
| M6 | Technical/Chrome-only copy | Fixed by the rc.5 copy pass |
| M7 | Leaving the check page discarded the run | Fixed |
| M8 | Live entry points looked dead with a minimized Home; double click opened two Homes | Fixed |
| M9 | Returning users landed on Interviews | Fixed |
| M10 | Get started stuck on "Complete profile" | Fixed |
| M11 | Disabled Start without a reason | Fixed |
| M12 | Help health check competed with the pre-interview check | Fixed (renamed, different wording) |
| L1 | RTL chevrons | Fixed (history chevrons follow the reading direction) |
| L2 | String tool tips not right-to-left | Fixed (implicit ToolTip follows its target) |
| L3 | Settings footer always visible | Kept: it shows "All changes saved" and costs one row |
| L4 | Title-bar halves overlap on narrow windows | Fixed |
| L5 | MinHeight above the work area at 200 % on 1080p | **Open** (manual row V) |
| L6 | Card padding and badge variants drift | **Open** (design-system follow-up) |
| L7 | Maximized answer indented from the question card | Fixed (answer column aligns left) |
| L8 | Interview Focus on 1366×768 leaves little answer space | **Open**; compact view is one click away in More |
| L9 | Fixed heights could clip Devanagari | Fixed (input MinHeight, larger context line) |
| L10 | Extra Tab stops on cards | Kept deliberately: the cards are now named UIA groups for screen readers |
| L11 | Two count code paths | Fixed |
| L12 | Home opened at every launch for Live-first users | Fixed |
| L13 | German "Hinweise" vs "Warnung"; "Home → Account" copy | Fixed by the copy pass |
| L14 | ScrollBar style ignores orientation | **Open** (no horizontal scrolling exists) |
| L15 | Start with unsaved edits started behind a dialog | Fixed |

## Product coherence checks that hold

One readiness store; deep links resolve before a page shows (no flash); Settings categories with descriptions, Updates
inside Settings, Reset section / Restore all defaults with a confirmation; Live window without diagnostics, Quick help
contextual and compact; every labelled input uses `FormField`; one dialog system, one inline message, one toast host.
