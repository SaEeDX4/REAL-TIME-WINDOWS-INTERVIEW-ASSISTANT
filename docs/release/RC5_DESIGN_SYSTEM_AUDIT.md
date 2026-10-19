# 2.1.0-rc.5 — design system audit

Scope: `Themes/Theme.xaml` and how Home, Settings, the Pre-interview check, the export choices, dialogs and the Live
Assistant use it. rc.5 makes form alignment an architecture rule instead of per-page margins. Tests:
`Rc5DesignSystemTests` (they parse the real XAML, so a page that drifts fails CI), `Rc5AccessibilityTests`,
`UiPagesTests`.

## What rc.5 adds

- **One field layout**: `Views/FormField` (label, optional ⓘ InfoTip, optional accessory such as "Forgot password?",
  the field, hint). Its template lives once in the theme. It links the label to the input for screen readers
  (`LabeledBy`), is not a Tab stop, and has a short-field variant. Every TextBox, PasswordBox and ComboBox on the Home
  pages, Settings, the Pre-interview page and the export choices sits in a FormField; code-built dialogs use
  `DialogForm.Labelled`, which produces the same layout.
- **Layout tokens** defined once: `S.ReadingMaxWidth` 760, `S.PageMaxWidth` 820, `S.SplitMaxWidth` 940,
  `S.FormMaxWidth` 620, `S.ContentMaxWidth` 1120, `S.FieldShortWidth` 320, `S.MultilineTallHeight` 170, `S.FieldGap`,
  `S.CardPadding`, `S.ColumnGap`, `S.ControlHeight`. Inputs carry no literal sizes or margins.
- **Shared text styles**: `T.Hint`, `T.MetaKey`, `T.MetaValue`; page-local hint styles derive from them without setters.
- **Accessibility primitives** (rc.5 accessibility audit): `AccessibleBorder` (a card/row with an automation peer),
  `LevelMeter` (a read-only progress bar), `v:LiveRegion.Mode` (announcing live regions), `RovingRadios` (one Tab stop
  per radio group). A focusable or named Border/panel without a peer fails `Rc5AccessibilityTests`.
- **Buttons**: one primary per view is now true on every page (UX audit M2); `Btn.Link` keeps 4.5:1 contrast on hover.

## Rules enforced by tests

Every labelled input inside a FormField; no literal input sizes; no hand-made field labels; page widths from tokens;
tokens defined exactly once; hint/meta styles derived without overrides; FormField links its label and is not a Tab
stop; no inert `AutomationProperties.LiveSetting`; audio meters are LevelMeters; the Live readiness indicator is an
icon.

## Open (honest list, no visual regression)

- **Type ramp** (UX M4): `H1`, `Section` and `Hint` still live in HomeWindow resources and are repeated literally in the
  Settings and Pre-interview pages; heading sizes 14–18 px are not tokenized; the title-bar product name uses the H1
  style. Proposed: `T.PageTitle`/`T.Title`/`T.Subtitle` in the theme and a small PageHeader control.
- **Buttons** (UX M5): a compact form button and an outline danger style for page-level deletes are not defined yet;
  profile/interview/report deletes are filled red (separated and confirmed).
- **Badges and card padding** (UX L6): five badge variants and four card paddings remain.
- **Meter width** 200 px and the Live readable width are local values, not tokens.
