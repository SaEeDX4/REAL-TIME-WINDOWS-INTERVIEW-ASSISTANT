<#
.SYNOPSIS
  Builds, tests and packages Interview Assistant for Windows x64.
.DESCRIPTION
  Produces artifacts\InterviewAssistant-Windows-x64.zip containing a self-contained single-file
  InterviewAssistant.exe (no .NET install needed) plus the knowledge\ folder and docs.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$FolderLayout,  # publish as EXE + DLLs instead of single-file (fallback if single-file causes issues)
    [string]$Version = "",  # e.g. 2.1.0 (default: the version in the .csproj)
    [string]$OutDir = "artifacts\InterviewAssistant",
    [switch]$SkipBuild      # publish only (solution already built and tested in this run)
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0 and re-run."
}
Write-Host "dotnet $(dotnet --version)" -ForegroundColor Cyan

if (-not $SkipBuild) {
    dotnet restore InterviewAssistant.sln
    if ($LASTEXITCODE) { throw "restore failed" }
    dotnet build InterviewAssistant.sln -c $Configuration --no-restore -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE) { throw "build failed" }
}

if (-not $SkipTests -and -not $SkipBuild) {
    dotnet test tests/InterviewAssistant.Tests -c $Configuration --no-build --logger "console;verbosity=normal"
    if ($LASTEXITCODE) { throw "tests failed" }
}

$out = Join-Path $root $OutDir
$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$single = if ($FolderLayout) { "false" } else { "true" }
dotnet publish src/InterviewAssistant.App -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=$single -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none @versionArgs -o $out
if ($LASTEXITCODE) { throw "publish failed" }

Copy-Item README.md, TESTING.md, docs\PRE_INTERVIEW_CHECKLIST.md, docs\TROUBLESHOOTING.md -Destination $out -ErrorAction SilentlyContinue

# Safety: never ship secrets or local settings.
$bad = Get-ChildItem $out -Recurse -Include *.dpapi, settings.json, *.env -ErrorAction SilentlyContinue
if ($bad) { throw "Refusing to package secret/settings files: $($bad.FullName -join ', ')" }

if ($OutDir -ne "artifacts\InterviewAssistant") { Write-Host "Published to $out"; return }
$zip = Join-Path $root "artifacts\InterviewAssistant-Windows-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host "`nPackage: $zip" -ForegroundColor Green
Write-Host "SHA256:  $hash"
Write-Host "Run:     $out\InterviewAssistant.exe"
