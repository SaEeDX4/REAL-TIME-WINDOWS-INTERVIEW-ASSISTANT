# 2.1.0-rc.5 — accessibility audit

Scope: keyboard, screen reader (UI Automation), contrast and high contrast for everything new or changed in rc.5.
Method: an independent read-only review, then fixes guarded by `Rc5AccessibilityTests` (XAML rules) and the in-app
self-test on Windows. A real Narrator/NVDA pass is manual row W of `RC5_MANUAL_REGRESSION.md` (not yet run by a person).

## Root problem found

WPF's Border, StackPanel and DockPanel have **no automation peer**: a name, help text or live setting on them never
reaches UI Automation, and a focusable Border is a Tab stop Narrator cannot announce. The rc.5 self-test read the
dependency property and therefore gave false confidence. Second, `AutomationProperties.LiveSetting` alone announces
nothing: a live region speaks only when the element raises LiveRegionChanged.

## Findings and outcome

| Severity | Finding | Outcome |
|---|---|---|
| High | Focusable/named Borders and panels without a peer: pre-interview summary card and check rows, Get started step cards, interview readiness card, health rows, popover cards (More, Quick help, account menu), export choice group, report toolbar | **Fixed.** `AccessibleBorder` (UIA Group or ListItem with the name). `Rc5AccessibilityTests` fails on any focusable or named Border/panel |
| High | Audio meters invisible to screen readers | **Fixed.** `LevelMeter`: a read-only UIA progress bar (0–100) in Live, Settings and the Pre-interview page |
| High | Inert live regions (13 places) | **Fixed.** `v:LiveRegion.Mode="Polite"` raises LiveRegionChanged when the text or name changes; the same value is never re-announced, so the 2 s Help refresh stays silent |
| Medium | Focus lost after More-menu actions | **Fixed.** In-place actions (Compact, Restore size, after the Focus dialog) return focus to More |
| Medium | Toggle state shown only by a check glyph | **Fixed.** Compact view and Coach keywords expose On/Off as item status |
| Medium | Live readiness indicator colour-only | **Fixed.** The state icon (check, warning, cross, clock) in its colour |
| Medium | Contrast: link hover/pressed 4.04/3.21:1; Adaptive badge 4.44:1 | **Fixed.** Link text turns to the body colour on hover/pressed; badge text uses the body colour |
| Medium | Interview Focus presets: each a Tab stop, arrows moved focus without selecting | **Fixed.** `RovingRadios`: one Tab stop, arrows select (shared with the export choices, mirrored in RTL) |
| Low | Every InfoTip named "More information" | **Fixed.** "More information: <field>" |
| Low | Menu buttons expose no expanded state; menu rows are plain buttons | Open |
| Low | Diagnostics key/value pairs without LabeledBy; decorative glyphs exposed as text | Open |
| Low | Quick help opens itself on first use and takes focus | Kept (it is the first-use explanation); announced through its heading |

## Verified fine

Heading levels on the new pages (H1 + Level2 sections and cards); custom 2 px focus rings wherever the default focus
visual is removed; Escape and focus return in popovers, the account menu and dialogs; Enter/Escape defaults in the
export and delete dialogs (Keep is the default); category list Up/Down/Home/End; FormField labels linked to inputs;
Windows high contrast uses system colours; states are never shown by colour alone.
