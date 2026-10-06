<#
.SYNOPSIS
  Real Windows startup/shutdown smoke test for the published EXE (no API key needed).
  PASS requires: process starts, a top-level WPF window appears, process stays alive, main window + knowledge
  load are logged, the app closes cleanly via WM_CLOSE with exit code 0, and no UI/fatal exceptions are logged.
#>
param([string]$Exe = "artifacts\InterviewAssistant\InterviewAssistant.exe", [int]$AliveSeconds = 15)
$ErrorActionPreference = "Stop"
$evidence = Join-Path (Get-Location) "artifacts\evidence"; New-Item -ItemType Directory -Force $evidence | Out-Null
trap { Get-ChildItem (Join-Path $env:LOCALAPPDATA "InterviewAssistant\logs") -Filter *.log -ErrorAction SilentlyContinue | Copy-Item -Destination $evidence -Force; break }
$Exe = Resolve-Path $Exe
$logDir = Join-Path $env:LOCALAPPDATA "InterviewAssistant\logs"
if (Test-Path $logDir) { Remove-Item "$logDir\*" -Force -ErrorAction SilentlyContinue }
$started = Get-Date

function Show-Diagnostics {
    Write-Host "---- application log ----"
    Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | Get-Content | ForEach-Object { Write-Host "  $_" }
    Write-Host "---- Windows Application event log (crashes since start) ----"
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in '.NET Runtime', 'Application Error', 'Windows Error Reporting' } |
        ForEach-Object { Write-Host "  [$($_.ProviderName)] $($_.Message)" }
}
function Fail($msg) { Show-Diagnostics; throw "SMOKE TEST FAILED: $msg" }

$p = Start-Process -FilePath $Exe -PassThru
$null = $p.Handle   # cache handle so ExitCode is available later

# 1) A real top-level window must appear.
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    $p.Refresh()
    if ($p.HasExited) { Fail "process exited during startup (exit code $($p.ExitCode))" }
    if ($p.MainWindowHandle -ne 0) { break }
    Start-Sleep -Milliseconds 250
}
if ($p.MainWindowHandle -eq 0) { Fail "no window appeared within 30 s" }
$startupMs = [int]((Get-Date) - $started).TotalMilliseconds
Write-Host "Window created after $startupMs ms: '$($p.MainWindowTitle)' (handle $($p.MainWindowHandle))"

# 2) Must stay alive.
Start-Sleep -Seconds $AliveSeconds
$p.Refresh()
if ($p.HasExited) { Fail "process died after startup (exit code $($p.ExitCode))" }
Write-Host ("Alive after {0} s · working set {1:N0} MB · threads {2}" -f $AliveSeconds, ($p.WorkingSet64 / 1MB), $p.Threads.Count)

# 3) Clean close: WM_CLOSE (first may close the first-run dialog, then the main window).
for ($i = 0; $i -lt 5 -and -not $p.HasExited; $i++) {
    $p.Refresh()
    $null = $p.CloseMainWindow()
    $null = $p.WaitForExit(6000)
}
if (-not $p.HasExited) { $p.Kill(); Fail "application did not close on WM_CLOSE (killed)" }
if ($p.ExitCode -ne 0) { Fail "non-zero exit code $($p.ExitCode)" }

# 4) Log assertions.
$log = (Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | Get-Content) -join "`n"
if ($log -notmatch "Knowledge loaded") { Fail "knowledge pack did not load" }
if ($log -notmatch "Main window loaded") { Fail "main window Loaded event not reached" }
if ($log -notmatch "Close requested") { Fail "WM_CLOSE not handled" }
if ($log -notmatch "Clean shutdown") { Fail "clean shutdown not logged" }
if ($log -match "UI exception|Fatal exception|XamlParse") { Fail "UI/fatal exception logged" }
Show-Diagnostics
Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object { Copy-Item $_.FullName (Join-Path $evidence "smoke-$($_.Name)") -Force }
Write-Host "SMOKE TEST PASSED (startup $startupMs ms, clean exit 0)" -ForegroundColor Green
