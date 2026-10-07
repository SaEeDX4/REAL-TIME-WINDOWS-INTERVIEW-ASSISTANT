<#
.SYNOPSIS
  Packs a published build into a Velopack installer + update feed (Setup.exe, full/delta nupkg, releases.<channel>.json).
.DESCRIPTION
  Signing: pass -SignMetadata <metadata.json> (Azure Artifact Signing / Trusted Signing) when credentials are available.
  Without it the installer is UNSIGNED (Windows SmartScreen will warn) — never a self-signed "production" certificate.
.EXAMPLE
  ./scripts/package-installer.ps1 -Version 2.0.0 -Channel stable
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet("stable", "beta")][string]$Channel = "stable",
    [string]$PublishDir = "artifacts\InterviewAssistant",
    [string]$OutDir = "artifacts\installer",
    [string]$SignMetadata = "",
    [string]$ReleaseNotes = ""
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root
$env:DOTNET_ROLL_FORWARD = "Major"   # vpk is a .NET tool; allow it to run on the installed runtime

$tools = Join-Path $env:USERPROFILE ".dotnet\tools"
if (-not ($env:PATH -split ';' -contains $tools)) { $env:PATH += ";$tools" }
if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    dotnet tool install -g vpk --version 1.2.161
    if ($LASTEXITCODE) { throw "vpk install failed" }
}
if (-not (Test-Path (Join-Path $PublishDir "InterviewAssistant.exe"))) { throw "Publish output not found in $PublishDir" }

$bad = Get-ChildItem $PublishDir -Recurse -Include *.dpapi, settings.json, *.env, session.* -ErrorAction SilentlyContinue
if ($bad) { throw "Refusing to package secret/settings files: $($bad.FullName -join ', ')" }

$vpkArgs = @("pack", "--packId", "InterviewAssistant.Desktop", "--packVersion", $Version, "--packDir", $PublishDir,
          "--mainExe", "InterviewAssistant.exe", "--packTitle", "Interview Assistant", "--packAuthors", "Interview Assistant",
          "--icon", "src\InterviewAssistant.App\Assets\app.ico", "--channel", $Channel, "--runtime", "win-x64", "--outputDir", $OutDir)
if ($ReleaseNotes -and (Test-Path $ReleaseNotes)) { $vpkArgs += @("--releaseNotes", $ReleaseNotes) }
if ($SignMetadata) {
    if (-not (Test-Path $SignMetadata)) { throw "Signing metadata not found: $SignMetadata" }
    $vpkArgs += @("--azureTrustedSignFile", $SignMetadata)
    Write-Host "Signing with Azure Artifact Signing ($SignMetadata)" -ForegroundColor Cyan
} else {
    Write-Warning "Packaging UNSIGNED installer (no signing credentials configured)."
}
vpk @vpkArgs
if ($LASTEXITCODE) { throw "vpk pack failed" }
Get-ChildItem $OutDir | Format-Table Name, Length
