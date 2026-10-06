<#
.SYNOPSIS
  Runs `InterviewAssistant.exe --selftest` (real WPF process, no API key, no credits) and fails on any failed check.
#>
param([string]$Exe = "artifacts\InterviewAssistant\InterviewAssistant.exe", [string]$Out = "artifacts\selftest-result.json")
$ErrorActionPreference = "Stop"
$Exe = Resolve-Path $Exe
$Out = [System.IO.Path]::GetFullPath($Out)
if (Test-Path $Out) { Remove-Item $Out -Force }
$p = Start-Process -FilePath $Exe -ArgumentList "--selftest", "`"$Out`"" -PassThru
$null = $p.Handle
if (-not $p.WaitForExit(180000)) { $p.Kill(); throw "self-test timed out" }
if (-not (Test-Path $Out)) { throw "self-test produced no result file (exit code $($p.ExitCode))" }
$r = Get-Content $Out -Raw | ConvertFrom-Json
foreach ($c in $r.checks) {
    $mark = if ($c.Ok) { "PASS" } else { "FAIL" }
    Write-Host ("[{0}] {1,-62} {2,6} ms  {3}" -f $mark, $c.Name, $c.Ms, $c.Detail)
}
if ($p.ExitCode -ne 0 -or -not $r.passed) { throw "SELF-TEST FAILED (exit code $($p.ExitCode))" }
Write-Host "SELF-TEST PASSED ($(@($r.checks).Count) checks) on $($r.os)" -ForegroundColor Green
