# 2.1.0-rc.5 — export and data audit

Scope: the rc.5 exports and the profile backup/import. Code: `HomeWindow.DataExport.cs`, `Views/ExportDialog.cs`,
`Views/ExportChoiceList.xaml`, `Core/Persistence/ProfileBundle.cs`, `SecretFilter.cs`, `ExportFile.cs`,
`Core/Reports/AccountExportRenderer.cs`, `ProfileExportRenderer.cs`, `ExportLabels.cs`. Tests: `Rc5DataExportTests`,
`SecurityAuditExportTests`, `L10nDateAndBidiTests`.

## What rc.5 delivers

| Export | Format | Contents | For |
|---|---|---|---|
| Export my data → **Readable** (recommended) | HTML, no scripts, print-friendly | account, plan and usage, devices, live-session table, saved documents; technical details collapsed | people (open in a browser, print to PDF) |
| Export my data → **Raw data** | JSON | the service export after the secret filter | advanced use, data-protection requests |
| Profiles → Export → **Readable profile** | HTML | confirmed experience, interview-profile answers, facts still to review (marked) | reading and printing |
| Profiles → Export → **Backup & transfer** | `.iaprofile` (ZIP with a SHA-256 manifest) | the profile with its interviews, prepared answers and reports | restore, move to another PC or account |
| Profiles → Export → **Raw data** | ZIP of JSON | the same data as separate files | other tools (cannot be imported) |

**Import backup** shows a preview (profile name, counts of facts, interviews, reports, creation date) before it imports,
always as a **new** profile with fresh ids; nothing on the PC is replaced, and a failed import is rolled back.

## Guarantees and how they are enforced

- **No credentials**: passwords, sign-in tokens, API/provider keys and payment secrets never appear in any export.
  `SecretFilter` removes secret-looking keys and values, fails closed at its depth limit and refuses an input it cannot
  parse (the export is then not written). The backup filters by key name and removes only unmistakable credential
  values so résumé prose round-trips unchanged (security audit L4). Tests run every export path against planted
  secrets (`NoSecretsInAnyProfileExport`, `EveryLeakCaseOfTheReview_IsFiltered`).
- **Readable**: every value HTML-encoded, `lang`/`dir` set for right-to-left languages, dates without the weekday
  ("Si rinnova il 18 ottobre 2026"), Arabic on the Gregorian calendar so billing dates match Paddle receipts.
- **Atomic files**: written to `<name>.tmp`, moved into place on success, deleted on failure (error-state L3).
- **Honest labels**: the backup, readable profile and raw ZIP say "Not encrypted — store it safely"; an invalid
  backup is "damaged or incomplete" (the unkeyed manifest detects corruption, not tampering).
- **Untrusted import**: traversal and unexpected entries rejected; manifest must match exactly; size limits (64 MB total,
  entry count checked before parsing); null fields, duplicate keys and malformed JSON are a plain "isn't a valid
  profile backup"; prompt-injection guard re-run on imported text with the résumé-upload warning.

## Findings fixed in rc.5 (from the security and error-state reviews)

Malformed backups crashing the import (M1), fail-open secret filter (M3), injection guard not re-run (L1), lossy backup
redaction (L4, partly), trailing-newline ids (L9), memory on hostile ZIPs (L10, partly), half-written files (L3),
account export parse errors escaping (L4 error-state). Details: `RC5_SECURITY_PRIVACY_AUDIT.md`.

## Open

Streaming hashes for very large backups and a keyed bundle signature are not implemented; a backup is as private as
the place the user stores it.
