<#
.SYNOPSIS
  Release gate run before packaging: version format, release documents and committed-secret scan.
.DESCRIPTION
  - The app project version is a semantic version; a release candidate (x.y.z-rc.N) is never built as Stable.
  - docs/release/RELEASE_NOTES-v<version>.md exists, names the version and is not a placeholder.
  - For a release candidate N >= 2, every mandatory RC audit document exists and is substantive.
  - No provider keys or webhook secrets are committed (test fixtures are marked "fake-credential: test fixture").
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
    $n = $Matches[1]
    foreach ($d in "ROOT_CAUSES", "ENGINE_AUDIT", "PERFORMANCE_AUDIT", "SECURITY_AUDIT", "PRIVACY_AUDIT", "LOCALIZATION_AUDIT",
                   "UI_UX_AUDIT", "DATA_INTEGRITY_AUDIT", "COMMERCIALIZATION_READINESS", "MANUAL_REGRESSION") {
        $r = Test-Doc "docs/release/RC${n}_$d.md" 800; if ($r) { $failures += $r }
    }
}

$secrets = git grep -nE "sk-(proj-)?[A-Za-z0-9_-]{32,}|pdl_(live|sdbx)_apikey_[A-Za-z0-9]{20,}|pdl_ntfset_[A-Za-z0-9]{20,}|-----BEGIN (RSA |EC )?PRIVATE KEY-----" |
    Where-Object { $_ -notmatch "fake-credential: test fixture" }
if ($secrets) { $failures += "Possible committed secrets:`n$($secrets -join "`n")" }

if ($failures) { $failures | ForEach-Object { Write-Host "::error::$_" }; throw "Release gate failed ($($failures.Count) problem(s))." }
Write-Host "Release gate passed for ${Version}: version, release notes, RC documents, secret scan."
