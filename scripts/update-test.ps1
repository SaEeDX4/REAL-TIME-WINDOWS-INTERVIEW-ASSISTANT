<#
.SYNOPSIS
  N -> N+1 update test on a real Windows machine (CI): installs version N with its Setup.exe, publishes N+1 to a local
  feed, lets the installed N update itself through Velopack, and verifies N+1 is installed and runs.
#>
param([string]$VersionA = "2.0.900", [string]$VersionB = "2.0.901")
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root
$work = Join-Path $root "artifacts\update-test"
$evidence = Join-Path $root "artifacts\evidence"
New-Item -ItemType Directory -Force $work, $evidence | Out-Null
function Fail($m) { Write-Host "UPDATE TEST FAILED: $m" -ForegroundColor Red; exit 1 }
function Version-Of($exe) { (Get-Item $exe).VersionInfo.FileVersion }

# 1) Build N and N+1 from the same source (only the version differs).
foreach ($v in @($VersionA, $VersionB)) {
    ./scripts/build-release.ps1 -SkipBuild -Version $v -OutDir "artifacts\update-test\pub-$v"
}
./scripts/package-installer.ps1 -Version $VersionA -PublishDir "artifacts\update-test\pub-$VersionA" -OutDir "artifacts\update-test\feedA"
Copy-Item "$work\feedA" "$work\feedB" -Recurse -Force
./scripts/package-installer.ps1 -Version $VersionB -PublishDir "artifacts\update-test\pub-$VersionB" -OutDir "artifacts\update-test\feedB"

# 2) Install N silently with its Setup.exe.
$setup = Get-ChildItem "$work\feedA" -Filter *Setup.exe | Select-Object -First 1
if (-not $setup) { Fail "Setup.exe not produced" }
Write-Host "Installer: $($setup.Name) ($([math]::Round($setup.Length / 1MB, 1)) MB)"
$p = Start-Process $setup.FullName -ArgumentList "--silent" -PassThru -Wait
if ($p.ExitCode -ne 0) { Fail "Setup exited with $($p.ExitCode)" }
Start-Sleep -Seconds 3
Get-Process InterviewAssistant -ErrorAction SilentlyContinue | Stop-Process -Force   # Setup may launch the app
$installDir = Join-Path $env:LOCALAPPDATA "InterviewAssistant.Desktop"
$exe = Join-Path $installDir "current\InterviewAssistant.exe"
if (-not (Test-Path $exe)) { Fail "installed exe not found at $exe" }
$installed = Version-Of $exe
Write-Host "Installed: $installed"
if (-not $installed.StartsWith($VersionA)) { Fail "expected $VersionA, found $installed" }

# 3) Installed N checks the N+1 feed, downloads, and schedules the update for after exit.
$result = Join-Path $work "update-result.txt"
$p = Start-Process $exe -ArgumentList "--update-test", "`"$work\feedB`"", "`"$result`"" -PassThru -Wait
$text = if (Test-Path $result) { Get-Content $result -Raw } else { "(no result)" }
Write-Host "Update check: $text (exit $($p.ExitCode))"
if ($p.ExitCode -ne 0) { Fail $text }

# 4) Velopack applies after the process exits.
$deadline = (Get-Date).AddSeconds(180)
do { Start-Sleep -Seconds 3; $now = if (Test-Path $exe) { Version-Of $exe } else { "" } } while (-not $now.StartsWith($VersionB) -and (Get-Date) -lt $deadline)
if (-not $now.StartsWith($VersionB)) { Fail "still $now after 180 s" }
Write-Host "Updated to: $now" -ForegroundColor Green

# 5) N+1 runs and sees itself as current.
$result2 = Join-Path $work "update-result-2.txt"
$p = Start-Process $exe -ArgumentList "--update-test", "`"$work\feedB`"", "`"$result2`"" -PassThru -Wait
$text2 = Get-Content $result2 -Raw
Write-Host "Post-update check: $text2"
if ($text2 -notmatch "no-update \(current $([regex]::Escape($VersionB))\)") { Fail "N+1 did not report itself current: $text2" }

@{ installed = $installed; updatedTo = $now; check = $text; postCheck = $text2; installer = $setup.Name; installerBytes = $setup.Length } |
    ConvertTo-Json | Set-Content (Join-Path $evidence "update-test.json")

# 6) Uninstall cleanly.
$update = Join-Path $installDir "Update.exe"
if (Test-Path $update) { Start-Process $update -ArgumentList "--uninstall", "--silent" -Wait -ErrorAction SilentlyContinue }
Write-Host "UPDATE TEST PASSED ($VersionA -> $VersionB)" -ForegroundColor Green
