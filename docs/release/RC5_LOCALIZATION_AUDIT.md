# 2.1.0-rc.5 — localization and RTL audit

Languages: English, German, French, Spanish, Italian, Portuguese, Persian (RTL), Arabic (RTL), Hindi, Chinese — 10
tables in `src/InterviewAssistant.Core/Languages/i18n`. rc.5 added about 340 keys (readiness, Settings categories,
Account, exports, Live chrome, Quick help, Interview Focus, accessibility). Tests: `LocalizationCoverageTests`
(complete tables, placeholders, reviewed cognates), `NoTableDefinesAKeyTwice`, `L10nDateAndBidiTests`, the self-test's
Persian/Hindi scans on Windows.

## Baseline checks (all pass)

Every table has the same key set; every `{n}` placeholder is preserved; no duplicate keys; no unused keys (the Settings
category descriptions are built dynamically); no hard-coded English in new XAML or code-built UI; Persian uses Persian
ی/ک and ZWNJ, Arabic its own letters; register matches each table (de Sie, fr vous, es tú, pt você, it tu, zh 您).

## Findings fixed in rc.5

- **Terminology drift in new keys** (about 190 values): ES "en directo"→"en vivo", "la app"→"la aplicación"; FR "séance"
  → "session"; DE "Bewerbungsgespräche"→"Interviews", "Angaben"→"Fakten", Sie in new keys; IT "dal vivo"→"live",
  "Pagina iniziale"→"Home", "Utente"→"Account"; AR "ملفات التعريف"→"الملفات الشخصية", count phrasing "الفحوص الناجحة: n من m";
  FA "گذرواژه"→"رمز عبور", "جلسه"→"نشست", "نمایه"→"پروفایل"; ZH 您 throughout new keys, 个人资料, 电子邮箱, full-width ——;
  HI "खाता"→"अकाउंट", "डिलीट"→"हटाएँ", "इंपोर्ट"→"इम्पोर्ट", and three meaning errors; EN "1 interviews".
- **Dates**: export dates no longer contain the weekday ("Si rinnova il 18 ottobre 2026" instead of "il domenica…").
- **Arabic calendar**: ar-SA defaults to Umm al-Qura (Hijri); all Arabic number/date formatting (exports, Account,
  billing lines, `Loc.Format`) now uses a Gregorian clone so dates match Paddle receipts. Persian keeps its calendar.
- **Bidi**: versions and e-mail addresses inside Persian/Arabic Account sentences are isolated left-to-right.
- **Language switch**: Pre-interview results formatted with values re-format in the new language (they used to stay
  in the old one); separators ("Label: value", list commas) come from the tables (French " : ", Persian/Arabic "، ",
  Chinese full-width).
- **Length**: the Pre-interview footer trims with a tool tip instead of clipping; More-menu rows wrap.
- **RTL mirroring**: the Live history chevrons follow the reading direction; string tool tips follow their control.
- **Copy pass**: meeting-app neutral wording instead of "Chrome", plain badges instead of PASS/WARNING/FAIL, German
  "Hinweis" consistent with "Bereit mit Hinweisen", no "Home → Account" paths in fix hints.

## Known limitations

- Follow-up recognition ("Tell me more", "Go on") is English grammar; other languages are recognised when the
  interviewer asks with a question mark (see `RC5_ENGINE_AUDIT.md`).
- A few older keys keep older terms (ZH 你 in some rc.4 strings, IT "dal vivo" in one service message, HI खाता in one
  toast); they are correct but not uniform.
- Translations were reviewed by the audit, not by native-speaker testers; manual row U covers a visual pass.
