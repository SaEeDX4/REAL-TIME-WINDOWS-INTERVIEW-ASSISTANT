<#
.SYNOPSIS  Launches the published EXE on Windows, verifies it starts, stays alive and logs no UI/XAML errors, then closes it.
#>
param([string]$Exe = "artifacts\InterviewAssistant\InterviewAssistant.exe", [int]$Seconds = 12)
$ErrorActionPreference = "Stop"
$logDir = Join-Path $env:LOCALAPPDATA "InterviewAssistant\logs"
if (Test-Path $logDir) { Remove-Item "$logDir\*" -Force -ErrorAction SilentlyContinue }
$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds $Seconds
$alive = -not $p.HasExited
Write-Host "Process alive after $Seconds s: $alive"
$log = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | Get-Content -ErrorAction SilentlyContinue
$log | ForEach-Object { Write-Host "  $_" }
if ($alive) { $p.CloseMainWindow() | Out-Null; Start-Sleep 3; if (-not $p.HasExited) { $p.Kill() } }
if (-not $alive) { throw "Application exited during startup (exit code $($p.ExitCode))" }
if ($log -match "UI exception|Fatal exception|XamlParse") { throw "Startup logged UI errors" }
if (-not ($log -match "Knowledge loaded")) { throw "Knowledge pack did not load" }
Write-Host "SMOKE TEST PASSED" -ForegroundColor Green
