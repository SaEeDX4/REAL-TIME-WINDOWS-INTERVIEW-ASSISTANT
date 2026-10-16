# 2.1.0-rc.4 — localization audit

Architecture unchanged from rc.2 (see RC2_LOCALIZATION_AUDIT): one JSON table per UI language embedded in Core,
English is the reference, `{v:Loc key}` updates every open window on a language switch, `{v:LocFlow}` drives
right-to-left layout, the UI language never changes the interview or answer language. Reports use their own label
table (`ReportLabels`, 10 languages).

## Coverage (measured by `LocalizationCoverageTests`, rc.4 build)

| Language | Keys | Missing | Identical to English (not a reviewed cognate) | Placeholder mismatches | RTL | Latin words left inside the text |
|---|---|---|---|---|---|---|
| English (en) | 814 | — | — | — | — | — |
| Spanish (es) | 814 | 0 | 0 | 0 | — | (not applicable: Latin script, gated by the identical-value check) |
| French (fr) | 814 | 0 | 0 | 0 | — | " |
| German (de) | 814 | 0 | 0 | 0 | — | " |
| Portuguese (pt) | 814 | 0 | 0 | 0 | — | " |
| Italian (it) | 814 | 0 | 0 | 0 | — | " |
| Arabic (ar) | 814 | 0 | 0 | 0 | yes | 5, all technical: "Apple" (sign-in provider), "https", Windows device names ("Headphones", "Stereo", "Hands-Free"), the "Win" key, the "Delete" key |
| Persian (fa) | 814 | 0 | 0 | 0 | yes | 8, all technical: as Arabic plus "Setup" (installer file name), "sandbox" (Paddle environment), "Documents" (folder path) |
| Chinese (zh) | 814 | 0 | 0 | 0 | — | 7, technical: as above plus "token" (API term) |
| Hindi (hi) | 814 | 0 | 0 | 0 | — | 6, technical: as above |

rc.3 had 742 keys per language; rc.4 adds 72 (in-app dialogs `dlg_*`, Settings page `set_*`, account section
states `acct_section_*`, neutral password-reset copy, "Hearing audio", Reports empty/selection states, profile-gap
card, connection toasts, contextual error copy, split fact counts, the account-change notice). Every one is translated in all 10 languages.

## Surface mapping (what is actually on screen)

The rc.3 manual finding — Persian Help/About showing "Product", "Version", "Release channel", "Build",
"Installation", "Code signing", "Service" — came from About rows built in code with English labels. In rc.4:

- About rows are declared in XAML with `{v:Loc about_*}` labels; technical *values* (version, build hash, channel
  id, URLs) stay as they are and are forced left-to-right.
- Source scans fail CI when XAML or desktop code contains user-facing English (`XamlHasNoHardCodedUserText`,
  `DesktopCodeHasNoHardCodedUserText`), and when the App uses a key missing from English
  (`EveryKeyUsedInTheAppExistsInEnglish`, including keys built at run time).
- Windows self-test (CI, every push): in Persian and Hindi it opens the Live window, all Home pages including the new
  Settings page, an open in-app dialog and the pre-interview check, walks the logical and visual trees, and fails on
  any English string that has a translation; Persian must be right-to-left.
- `UiFoundationTests`: dialog buttons are localized keys (`dlg_*`), About labels are bound in XAML, reset copy is
  identical for known and unknown e-mail in every language, English has no "résumé" (the English UI says
  "resume"; other languages keep their own word: Lebenslauf, currículum, CV, رزومه, السيرة الذاتية, 简历, रिज़्यूमे).

## Right-to-left (Persian, Arabic)

- `FlowDirection` flows from every window; the new in-app dialog card, Settings page, segmented Profiles control and
  two-pane Reports page mirror with it. The segmented control's Left/Right arrow keys follow the visual direction in
  RTL (`SegmentedNavigation`, unit-tested).
- E-mail, password, hotkeys, version, build and model names are forced left-to-right inside RTL layouts.
- Report HTML sets `dir="rtl"` and isolates interviewer text with `<bdi dir="auto">`.
- Persian orthography: ی/ک (no Arabic ي/ك), ZWNJ in compounds.

## Hindi

Complete Devanagari table. Common software loanwords in Devanagari (प्रोफ़ाइल, रिपोर्ट, अकाउंट). Keyboard key names
and the Windows device names stay in Latin script because that is how Windows shows them.

## Findings

| # | Severity | Finding | Fix | Verification |
|---|---|---|---|---|
| L1 | High | Persian Help/About labels in English (rc.3 manual) | About labels localized in XAML; technical values unchanged | self-test Persian scan, `UiFoundationTests` |
| L2 | High | Native message boxes used Windows-language buttons (OK/Cancel in the OS language, not the app language) | In-app dialogs with localized buttons | source test: no `MessageBox` in the App |
| L3 | Medium | Password-reset copy differed for known/unknown e-mail (enumeration) | One neutral sentence in 10 languages | `CloudAuthTests` |
| L4 | Medium | "45 confirmed facts" vs "44" without explanation | Counts name experience facts and interview-profile answers separately (10 languages) | `FactCountSemanticsTests` |
| L5 | Low | Windows device names, key names, "https", "token" in non-Latin text | Kept on purpose (what Windows shows / technical) | this audit |
| L6 | Low | Diagnostics panel and log lines English | By design (support data) | — |

## Remaining risk (external)

Translations were written and reviewed for consistency (terminology, placeholders, button names inside sentences)
but not by professional native linguists. A native review of Persian, Arabic and Hindi before Stable is recommended,
together with the visual checks in RC4_MANUAL_REGRESSION (long German/French labels at 125–200 % scaling, RTL
layouts of the new pages).
