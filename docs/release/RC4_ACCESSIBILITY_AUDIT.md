# 2.1.0-rc.4 — accessibility audit

Target: keyboard-only use, screen readers (UI Automation: Narrator/NVDA), Windows high contrast, scaling to 200 %,
right-to-left languages. Build: 2.1.0-rc.4.

## Findings and state

| Area | rc.4 state | Evidence |
|---|---|---|
| Dialogs | In-app dialogs are UIA dialogs (`IsDialog`), named by their title with the message as help text, title is a level-1 heading; focus moves into the dialog and is trapped (Tab/Ctrl+Tab cycle, arrow keys contained); Enter = default action, Escape = cancel; destructive confirmations default to Cancel; buttons disabled while a validating action runs (no double submission) | `Views/AppDialog.cs`, self-test "dialog overlay / Escape / default Cancel" |
| Native message boxes | None left in the App (they ignored the app language and RTL) | `UiFoundationTests` source rule |
| Accessible names | Every button has an accessible name (text content, a `{v:Loc}` content, content set in code, or `AutomationProperties.Name` for icon buttons); all inputs on Settings, Profiles, Account and the Live window carry `AutomationProperties.Name` | XAML scan done for this audit (one button gets its content in code: Upgrade) |
| Segmented Profiles control | One tab stop (the selected segment), Left/Right switch and follow visual direction in RTL, both segments named | `SegmentedNavigation` unit test, self-test |
| Reports list | Up/Down select, Enter opens, Delete asks (destructive dialog) | self-test, `UiPagesTests` |
| Sign-in form | Fixed tab order, Enter advances/submits, inline errors on the field (error state of the template), busy state | `UiFoundationTests`, self-test |
| Focus visibility | Shared focus ring on all form controls and buttons (token-based radius) | `Theme.xaml` |
| High contrast | Form controls switch to system colours under Windows high contrast (`HighContrast` source with live updates) | `Theme.xaml`, manual check |
| Announcements | UIA live regions: the Live status line (polite), inline section messages (errors assertive, others polite), toasts (errors assertive), unsaved-changes indicators, account status and the pre-interview verdict (polite). The status is text, not colour only; "Hearing audio" is neutral (no false "question" cue) | `MainWindow.xaml`, `InlineMessage`, `ToastHost`, `ReadinessWindow.xaml` |
| Colour as the only cue | Profile-gap card has a title and icon besides the amber accent; errors have icon + text | `UiPagesTests` |
| Scaling | Layout uses star sizing and tokens; Reports panes have a minimum width; text wraps | manual check at 125–200 % |
| RTL | FlowDirection from every window; dialogs, Settings, segmented control and Reports mirror; LTR forced for e-mail, password, hotkeys, versions | self-test Persian scan, manual check |

## Known gaps

- Not tested with a real screen reader in CI (UIA properties are set and asserted where possible; announcement
  quality needs a manual pass with Narrator and NVDA).
- Tooltips still mention default hotkeys even after the user changes them (static strings, carried from rc.2).
- The live answer area updates frequently while streaming; it is not a live region on purpose (it would flood screen
  readers). Users of screen readers get the complete answer after streaming ends.
