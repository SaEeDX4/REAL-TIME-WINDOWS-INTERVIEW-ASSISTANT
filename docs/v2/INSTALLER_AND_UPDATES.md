# Installer and updates (Velopack)

## What ships
| File | Purpose |
|---|---|
| `InterviewAssistant.Desktop-<channel>-Setup.exe` (~69 MB) | Per-user installer (no admin) → `%LOCALAPPDATA%\InterviewAssistant.Desktop`, Start menu + desktop shortcuts, uninstall entry |
| `…-<version>-<channel>-full.nupkg` / `…-delta.nupkg` | Update packages (delta for a small change measured at **0.31 MB** vs 61 MB full) |
| `releases.<channel>.json`, `assets.<channel>.json` | Update feed metadata |
| `…-Portable.zip` | Portable copy (no auto-update) |

The installer contains only the app and the generic `knowledge/` assets — no sample candidates, no settings, no secrets (`package-installer.ps1` refuses `*.dpapi`, `settings.json`, `*.env`, `session.*`).

## Channels
`stable` (tags `vX.Y.Z`) and `beta` (tags `vX.Y.Z-beta.N`). Users switch in Home → Account → Updates; the change applies after restart. `UpdatePolicy.NormalizeChannel` accepts only these two.

## Update rules (`UpdatePolicy`, tested)
- No check/download/apply while an interview session is running or paused, and not in the first 45 s after launch.
- Checks at most every 6 h.
- A downloaded update installs **after the app exits** (`WaitExitThenApplyUpdates`), or immediately on "Restart to update" — refused while a session is active.
- Feed URL comes from `cloud.json` → `updateUrl` (a GitHub repo URL or an https static folder). Empty = updates disabled.

## Signing
`production-release.yml` signs through Azure Artifact Signing (Trusted Signing) **only** when `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_SIGNING_PROFILE`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_CLIENT_SECRET` are configured; it then fails the release if `Setup.exe` is not validly signed. Without them the release is published **unsigned** with a workflow warning. No self-signed certificate is ever used.

## CI evidence
| Test | Where | Status |
|---|---|---|
| `vpk pack` config (x64, icon, channel, Setup + portable + full) | local Linux cross-pack | PASSED |
| Delta N→N+1 generation | local Linux cross-pack | PASSED (0.31 MB) |
| Silent install of N with Setup.exe, installed-version check | `scripts/update-test.ps1` on windows-latest | see Windows CI run |
| Installed N updates itself from the N+1 feed, N+1 runs and reports current, uninstall | `scripts/update-test.ps1` | see Windows CI run |
| Azure Artifact Signing | `production-release.yml` | NOT YET VALIDATED (needs Azure account + identity validation) |
| GitHub Releases as public feed | `production-release.yml` | NOT YET VALIDATED (repository is private; clients need a public feed — public releases repo or static hosting) |
