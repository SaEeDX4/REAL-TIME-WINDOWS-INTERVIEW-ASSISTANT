# 2.1.0-rc.2 — localization audit

## Architecture

* One string table per UI language: `src/InterviewAssistant.Core/Languages/i18n/{code}.json`, embedded in the Core
  assembly. English (`en.json`) is the reference; keys are `snake_case`.
* `Loc` (Core) — `T(key)`, `F(key, args)` (culture-aware formatting), `Language`, `LanguageChanged`, `IsRtl`,
  `MissingKeys(lang)`; English fallback per key (never a blank label).
* WPF: `{v:Loc key}` binds through `LocSource` (indexer binding) so **every open window updates immediately** when
  the UI language changes; `{v:LocFlow}` binds `FlowDirection` (Persian and Arabic are right-to-left).
* UI language is independent of the **interview language** (recognition) and the **answer language** (bullets);
  both are chosen per interview. Reports use the report language setting.
* Interviewer text and answers follow their own script direction (an Arabic question reads RTL in an English UI).

## Coverage (734 keys in English)

| Language | Keys | Missing | Identical to English | Of which reviewed cognates | Placeholder mismatches | RTL |
|---|---|---|---|---|---|---|
| Spanish (es) | 734 | 0 | 8 | 3 (Personal, Beta, Normal) | 0 | — |
| French (fr) | 734 | 0 | 20 | 14 (Question, Pause, Structure, Documents, Version, Installation, Service, Transcription, …) | 0 | — |
| German (de) | 734 | 0 | 11 | 6 (Upgrade, Interviews, Interview, Beta, Version, Normal) | 0 | — |
| Portuguese (pt) | 734 | 0 | 8 | 2 (Beta, Normal) | 0 | — |
| Italian (it) | 734 | 0 | 8 | 3 (Account, Password, Beta) | 0 | — |
| Arabic (ar) | 734 | 0 | 3 | 0 | 0 | yes |
| Persian (fa) | 734 | 0 | 3 | 0 | 0 | yes |
| Chinese (zh) | 734 | 0 | 4 | 0 | 0 | — |
| Hindi (hi) | 734 | 0 | 4 | 0 | 0 | — |

"Identical to English" values that are not cognates are format-only strings (`{0} / {1}`, `{0} · {1}`, `{0}`,
`COACH`, `Q{0}.`). **Fallback keys in use: 0** for every language.

## Gates (fail CI)

* `LocalizationCoverageTests.EverySupportedLanguageIsComplete` — per language: no missing keys, identical
  placeholders, no value identical to English unless format-only or a reviewed cognate, no extra keys.
* `XamlHasNoHardCodedUserText` / `DesktopCodeHasNoHardCodedUserText` — source scans of the desktop app.
* `EveryKeyUsedInTheAppExistsInEnglish` — including keys built dynamically (fact kinds, personal categories, audio
  states, font sizes).
* `LiveLanguageSwitchRaisesAnEventAndChangesText`, `UiLanguageNeverChangesTheAnswerLanguage`,
  `RightToLeftLanguagesAreRegisteredAsRtl`.
* **Runtime (Windows self-test)**: for Persian and Hindi the live window, all six Home pages, Settings and the
  pre-interview check are opened and their logical/visual trees scanned for any English UI string that has a
  translation; Persian must be right-to-left. Runs in `windows-release` CI on every push.

## Persian (RTL)

Real RTL: `FlowDirection` bound on every window and dialog; message boxes use RTL reading order; e-mail, password,
API key, model names, hotkeys, version and technical details are forced left-to-right; Persian orthography uses ی/ک
(no Arabic ي/ك — checked) and ZWNJ. Navigation arrows in text use "←".

## Hindi

Complete Devanagari table (734/734), common software loanwords in Devanagari (प्रोफ़ाइल, अकाउंट, रिपोर्ट).

## Findings

| # | Severity | Finding | Fix | Test | Remaining risk |
|---|---|---|---|---|---|
| L1 | High | RC1: only 44 keys translated; the product was half-English in every non-English language | All strings moved to tables, 9 complete translations | coverage tests | Translations were produced for this release and reviewed for consistency (shared labels, placeholders, terminology) but **not by professional native linguists**; a native review before Stable is recommended (Persian and Hindi first). |
| L2 | Medium | Old status strings ALL-CAPS in some languages | Sentence case like English | coverage tests | — |
| L3 | Medium | Help text named a non-existent "Save & prepare" button | Names the real "Prepare" button in all languages | review | — |
| L4 | Medium | Hotkey errors, password-reset cancel, key-test results, initial status, report preview prefix, file-dialog names, pre-interview AI test question were English | Localized | source scans | — |
| L5 | Low | Diagnostics panel (developer-oriented counters) and log lines remain English | By design (support data) | — | Customers can open Diagnostics from the header; it is labelled technical. |
| L6 | Low | Tooltips mention the default hotkeys even after the user changes them | — | — | Open: tooltips are static strings. |
| L7 | Low | Long German/French labels | Trimming/wrapping and wider default window (UI audit) | self-test layout checks (English only) | Not visually verified in every language on every resolution. |
