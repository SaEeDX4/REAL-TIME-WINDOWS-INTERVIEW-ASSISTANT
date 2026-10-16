<#
.SYNOPSIS
  Release gate run before packaging: version format, release documents and committed-secret scan.
.DESCRIPTION
  - The app project version is a semantic version; a release candidate (x.y.z-rc.N) is never built as Stable.
  - docs/release/RELEASE_NOTES-v<version>.md exists, names the version and is not a placeholder.
  - For a release candidate N = 2..3, every mandatory audit document of the RC2 audit set exists and is substantive
    (for rc.3 updated for it), plus RC3_LAUNCH_BLOCKERS.md. From N >= 4 the candidate's own audit set RC<N>_*.md (root
    causes, engine, UI/UX, localization, data integrity, error states, accessibility, performance, manual regression,
    commercialization readiness) plus the standing RC2 security, privacy and dependency audits re-reviewed for rc.N.
  - No provider keys, webhook secrets, signed JWTs (e.g. a service-role key), database URLs with passwords or private keys are committed (test fixtures are marked "fake-credential: test fixture").
#>
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root
$failures = @()

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { $failures += "Not a semantic version: $Version" }
$csproj = ([xml](Get-Content src/InterviewAssistant.App/InterviewAssistant.App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($csproj -ne $Version) { $failures += "App project version $csproj does not match $Version" }

function Test-Doc([string]$path, [int]$minChars) {
    if (-not (Test-Path $path)) { return "Missing release document: $path" }
    $text = Get-Content $path -Raw
    if ($text.Length -lt $minChars) { return "Release document too short ($($text.Length) chars): $path" }
    if ($text -match '(?i)\b(TODO|TBD|lorem ipsum)\b') { return "Release document contains a placeholder: $path" }
    return $null
}

$notes = "docs/release/RELEASE_NOTES-v$Version.md"
$r = Test-Doc $notes 400; if ($r) { $failures += $r }
elseif (-not ((Get-Content $notes -Raw).Contains($Version))) { $failures += "$notes does not name version $Version" }

if ($Version -match '-rc\.(\d+)$' -and [int]$Matches[1] -ge 2) {
    $n = [int]$Matches[1]
    if ($n -le 3) {
        # rc.2 / rc.3: the RC2 audit set, kept current for rc.3, plus rc.3's launch-blocker report.
        foreach ($d in "ROOT_CAUSES", "ENGINE_AUDIT", "PERFORMANCE_AUDIT", "SECURITY_AUDIT", "PRIVACY_AUDIT", "LOCALIZATION_AUDIT",
                       "UI_UX_AUDIT", "DATA_INTEGRITY_AUDIT", "COMMERCIALIZATION_READINESS", "MANUAL_REGRESSION", "DEPENDENCY_LICENSE_AUDIT") {
            $r = Test-Doc "docs/release/RC2_$d.md" 800; if ($r) { $failures += $r }
            elseif ($n -ge 3 -and -not ((Get-Content "docs/release/RC2_$d.md" -Raw).Contains("rc.$n"))) { $failures += "docs/release/RC2_$d.md was not updated for rc.$n" }
        }
        if ($n -ge 3) { $r = Test-Doc "docs/release/RC${n}_LAUNCH_BLOCKERS.md" 800; if ($r) { $failures += $r } }
    } else {
        # rc.4 and later: the candidate's own audit set ...
        foreach ($d in "ROOT_CAUSES", "ENGINE_AUDIT", "UI_UX_AUDIT", "LOCALIZATION_AUDIT", "DATA_INTEGRITY_AUDIT", "ERROR_STATE_AUDIT",
                       "ACCESSIBILITY_AUDIT", "PERFORMANCE_AUDIT", "MANUAL_REGRESSION", "COMMERCIALIZATION_READINESS") {
            $r = Test-Doc "docs/release/RC${n}_$d.md" 800; if ($r) { $failures += $r }
            elseif (-not ((Get-Content "docs/release/RC${n}_$d.md" -Raw).Contains("rc.$n"))) { $failures += "docs/release/RC${n}_$d.md does not name rc.$n" }
        }
        # ... and the standing security, privacy and dependency audits, re-reviewed for this candidate.
        foreach ($d in "SECURITY_AUDIT", "PRIVACY_AUDIT", "DEPENDENCY_LICENSE_AUDIT") {
            $r = Test-Doc "docs/release/RC2_$d.md" 800; if ($r) { $failures += $r }
            elseif (-not ((Get-Content "docs/release/RC2_$d.md" -Raw).Contains("rc.$n"))) { $failures += "docs/release/RC2_$d.md was not re-reviewed for rc.$n" }
        }
    }
}

$secrets = git grep -nE "sk-(proj-)?[A-Za-z0-9_-]{32,}|pdl_(live|sdbx)_apikey_[A-Za-z0-9]{20,}|pdl_ntfset_[A-Za-z0-9]{20,}|-----BEGIN (RSA |EC )?PRIVATE KEY-----|eyJ[A-Za-z0-9_-]{20,}\.eyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{20,}|postgres(ql)?://[^:/ ]+:[^@ ]+@|sb_secret_[A-Za-z0-9_-]{20,}" |
    Where-Object { $_ -notmatch "fake-credential: test fixture" }
if ($secrets) { $failures += "Possible committed secrets:`n$($secrets -join "`n")" }

if ($failures) { $failures | ForEach-Object { Write-Host "::error::$_" }; throw "Release gate failed ($($failures.Count) problem(s))." }
Write-Host "Release gate passed for ${Version}: version, release notes, RC documents, secret scan."
# git grep exits 1 when nothing matched (the expected, clean result): never let that leak out as a failure.
exit 0
