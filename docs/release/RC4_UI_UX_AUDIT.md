# 2.1.0-rc.4 — UI/UX audit

Scope: the desktop app (WPF): Home (Get started, Profiles, Interviews, Reports, Settings, Help, Account), the Live
Assistant window, the pre-interview check and all dialogs. Baseline rc.3 (`dc67e80`).

## Design system

- **Tokens** (`Themes/Theme.xaml`): control height 36, button heights, corner radii, focus-ring radius, input and
  multi-line padding, page/section/field spacing, column gap, maximum page width; colours for placeholder,
  error-soft, error-strong and the dialog backdrop. Pages use the tokens instead of ad-hoc margins.
- **One form-control family**: TextBox, multi-line TextBox, PasswordBox and ComboBox (editable too) share height,
  radius, border, padding, fill, caret and selection colour, and the same states: normal, hover, focus ring, error,
  disabled, read-only, high contrast. They are the implicit defaults, so no page can drift.
- **Buttons**: `Btn.FormPrimary` (one per area), `Btn.Form` (secondary), `Btn.FormDanger`/`Btn.Danger`
  (destructive, visually separated), `Btn.Link`.
- **Placeholder/caret**: `Watermark.Text` is drawn inside the template's content area with the text's own inset,
  ignores clicks and hides on focus or content. PasswordBox emptiness is tracked without reading the password.
- **Feedback**: one in-app dialog system (`AppDialog`), one inline message control (`InlineMessage`: icon, text,
  detail, optional "Try again"), one toast host. No native message boxes remain (system file pickers stay native).

## Pages

| Area | rc.3 | rc.4 |
|---|---|---|
| Settings | separate window | Home page with sections (Audio, Answers, Listening, Window, Hotkeys, Privacy), sticky "Unsaved changes · Save / Discard" footer; the Live gear deep-links to the right section; hotkey conflicts (also with fixed hotkeys) rejected |
| Profiles | two small tabs; flat list of actions incl. Delete | full-width 50/50 segmented control (keyboard + RTL); left pane: **Import resume…** (primary), Add document… / New empty profile / Export… (secondary), divider, **Delete profile** (destructive, confirmed, Cancel default); header fields on one grid |
| Reports | list and preview stacked, first 8 questions only | two panes (~32 % list, min 260 px / ~68 % preview), rows with date/time, interview, question count; toolbar Open / Copy as text / Save as HTML… and a separated Delete; Up/Down, Enter opens, Delete asks; empty state and "Select a report" state; newest report auto-selected after Stop |
| Interviews | ad-hoc spacing | same grid and tokens as Profiles, Save button + unsaved indicator, destructive delete separated |
| Live window | input and send button misaligned; "0 / 1" counter when signed out | one row, same height token; counter hidden when no history is shown; signed-out history empty |
| Profile gap | guidance looked like an answer | amber "Not in your profile yet" card with speaking structure and "Add to profile" |
| Account | one page-level error | three independent sections with loading, specific failure, stale data note, retry |
| Pre-interview check | warnings = NOT READY | only failures block; "READY — WITH WARNINGS" lists optional improvements |
| Network | silent | debounced "Connection lost — reconnecting…" / "Connection restored" toasts |
| Sign-in form | inconsistent heights | shared controls, fixed tab order, Enter moves e-mail → password → submit, busy state blocks double submission, inline errors outline the field |

## Unsaved changes

One `NavigationGuard` covers profile, interview and Settings edits. It asks (Save / Discard changes / Cancel) only
before an action that would lose edits: leaving the page, switching item, importing into the open profile, closing
Home, closing the app, signing out. A language switch, resize or refresh never asks; it keeps the draft, the dirty
flag, the selection and the scroll position. Control construction is not an edit (found by the Windows CI smoke
test during rc.4 development: a slider's value coercion had marked Settings dirty at start-up).

## Verification

- Linux CI: App builds with warnings as errors; `UiFoundationTests`, `UiPagesTests`, `ErrorStateTests` (state
  logic, counters, toast debounce, copy rules, source rules such as "no MessageBox", "no SettingsWindow", "no overlay
  placeholders").
- Windows CI self-test (real WPF): every Home page renders; Settings deep-link, hotkey conflict, Discard; dialog
  overlay, destructive default Cancel, Escape; language switch keeps edits without a question; placeholder hidden
  when focused/filled; password box height equals e-mail box; Live counter hidden without history; question bar
  aligned; Reports shows two panes or the empty state with all four actions; segmented control arrow keys; fresh Home
  has no unsaved edits; Persian/Hindi string scan.
- Smoke test: real EXE starts, stays alive, exits cleanly on WM_CLOSE.

## Not verified by automation (manual, RC4_MANUAL_REGRESSION)

Visual quality at 1366×768 through 4K at 125–200 % scaling, RTL layouts of the new pages, high-contrast themes,
screen-reader announcements, long German/French labels, real network drops for the toasts.
