<#
.SYNOPSIS
  In-place upgrade test from the previous release candidate (CI, real Windows): installs the PUBLISHED installer of the
  previous candidate (downloaded from its CI run and verified against its recorded SHA-256), plants user data the way a
  tester's PC holds it (settings, workspace, saved reports), runs the NEW candidate's Setup.exe over it and verifies:
  the new version is installed, the user data is untouched, the new build starts, shows its version in the log, runs
  stably and exits cleanly, and the uninstaller removes the app (program files, shortcuts, uninstall entry) while
  leaving the user's own data in place.

  Unsigned builds never self-update, so testers upgrade exactly this way: by running the next Setup.exe.
#>
param(
    [Parameter(Mandatory)][string]$ToVersion,
    [string]$FromVersion = "2.1.0-rc.4",
    [string]$FromRunId = "38932251103",
    [string]$FromSetupSha256 = "03b34330d470fadd538955e2c5c1a353d0a18e9aa367254b1d75219bcf66260e",
    [string]$InstallerDir = "artifacts\installer"
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root
$work = Join-Path $root "artifacts\upgrade-test"
$evidence = Join-Path $root "artifacts\evidence"
New-Item -ItemType Directory -Force $work, $evidence | Out-Null
function Fail($m) { Write-Host "UPGRADE TEST FAILED: $m" -ForegroundColor Red; exit 1 }
function Product-Version($exe) { (Get-Item $exe).VersionInfo.ProductVersion }
function Stop-App { Get-Process InterviewAssistant -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2 }

$installDir = Join-Path $env:LOCALAPPDATA "InterviewAssistant.Desktop"
$exe = Join-Path $installDir "current\InterviewAssistant.exe"
$roaming = Join-Path $env:APPDATA "InterviewAssistant"
$local = Join-Path $env:LOCALAPPDATA "InterviewAssistant"
$settingsPath = Join-Path $roaming "settings.json"

# 1) The previous candidate exactly as published: its CI artifact, checked against the SHA-256 in its release report.
$from = Join-Path $work "from"
New-Item -ItemType Directory -Force $from | Out-Null
gh run download $FromRunId --repo $env:GITHUB_REPOSITORY --name "InterviewAssistant-v$FromVersion" --dir $from
if ($LASTEXITCODE) { Fail "could not download the published $FromVersion artifact (run $FromRunId)" }
$oldSetup = Get-ChildItem $from -Recurse -Filter "InterviewAssistant-Setup-v$FromVersion.exe" | Select-Object -First 1
if (-not $oldSetup) { Fail "InterviewAssistant-Setup-v$FromVersion.exe not in the downloaded artifact" }
$hash = (Get-FileHash $oldSetup.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $FromSetupSha256) { Fail "$($oldSetup.Name) SHA-256 $hash does not match the published $FromSetupSha256" }
Write-Host "Previous installer: $($oldSetup.Name) (SHA-256 verified)"

$newSetup = Get-ChildItem $InstallerDir -Filter "*Setup.exe" | Select-Object -First 1
if (-not $newSetup) { Fail "new Setup.exe not found in $InstallerDir" }

# 2) Install the previous candidate silently.
Stop-App
$p = Start-Process $oldSetup.FullName -ArgumentList "--silent" -PassThru -Wait
if ($p.ExitCode -ne 0) { Fail "$FromVersion Setup exited with $($p.ExitCode)" }
Start-Sleep -Seconds 3; Stop-App
if (-not (Test-Path $exe)) { Fail "$FromVersion exe not found at $exe" }
$before = Product-Version $exe
if (-not $before.StartsWith($FromVersion)) { Fail "expected $FromVersion installed, found $before" }
Write-Host "Installed: $before"

# 3) User data as a tester's PC holds it after using the previous candidate.
New-Item -ItemType Directory -Force $roaming, (Join-Path $local "workspaces"), (Join-Path $local "reports") | Out-Null
$settings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
$settings["UiLanguage"] = "de"
$settings["HotkeyToggleWindow"] = "Ctrl+Alt+F9"
$settings["AuthorizedUseAcknowledged"] = $true
$settings["FirstRunCompleted"] = $true
$settings | ConvertTo-Json -Depth 10 | Set-Content $settingsPath -Encoding utf8
$marker = "upgrade-test " + [guid]::NewGuid().ToString("n")
$workspaceMarker = Join-Path $local "workspaces\upgrade-test-marker.bin"
$reportMarker = Join-Path $local "reports\upgrade-test-report.html"
Set-Content $workspaceMarker $marker
Set-Content $reportMarker "<html><body>$marker</body></html>"

# 4) Run the new candidate's Setup.exe over the installed one.
$p = Start-Process $newSetup.FullName -ArgumentList "--silent" -PassThru -Wait
if ($p.ExitCode -ne 0) { Fail "$ToVersion Setup over $FromVersion exited with $($p.ExitCode)" }
Start-Sleep -Seconds 3; Stop-App
$after = Product-Version $exe
if (-not $after.StartsWith($ToVersion)) { Fail "after the upgrade the installed version is $after, expected $ToVersion" }
Write-Host "Upgraded to: $after" -ForegroundColor Green

# 5) The user's data is exactly as it was.
$kept = Get-Content $settingsPath -Raw | ConvertFrom-Json
if ($kept.UiLanguage -ne "de" -or $kept.HotkeyToggleWindow -ne "Ctrl+Alt+F9" -or -not $kept.AuthorizedUseAcknowledged) { Fail "settings changed by the upgrade: $(Get-Content $settingsPath -Raw)" }
if ((Get-Content $workspaceMarker -Raw).Trim() -ne $marker) { Fail "workspace data changed or removed by the upgrade" }
if (-not (Test-Path $reportMarker)) { Fail "saved report removed by the upgrade" }

# 6) The upgraded build starts on the old user's settings, logs its version, stays up, and exits cleanly.
$logToday = Join-Path $local "logs\app-$(Get-Date -Format yyyyMMdd).log"
# count LINES already in today's log (a byte length cannot index the UTF-8 text: "·" and other characters are multi-byte)
$linesBefore = if (Test-Path $logToday) { @(Get-Content $logToday -Encoding utf8).Count } else { 0 }
$app = Start-Process $exe -PassThru
Start-Sleep -Seconds 15
if ($app.HasExited) { Fail "$ToVersion exited on its own after the upgrade (exit $($app.ExitCode))" }
$null = $app.CloseMainWindow()
if (-not $app.WaitForExit(20000)) { $app.Kill(); Fail "$ToVersion did not close within 20 s" }
$log = if (Test-Path $logToday) { (Get-Content $logToday -Encoding utf8 | Select-Object -Skip $linesBefore) -join "`n" } else { "" }
if ($log -notmatch [regex]::Escape("Version $ToVersion")) { Write-Host "---- log since launch ----"; Write-Host $log }
if ($log -notmatch [regex]::Escape("Version $ToVersion")) { Fail "the upgraded app did not log 'Version $ToVersion' at startup" }
if ($log -notmatch "Clean shutdown") { Fail "the upgraded app did not shut down cleanly" }
$kept = Get-Content $settingsPath -Raw | ConvertFrom-Json
if ($kept.UiLanguage -ne "de" -or $kept.HotkeyToggleWindow -ne "Ctrl+Alt+F9") { Fail "the upgraded app lost the user's settings when it saved them" }

# 7) Uninstall: the app goes (program folder, shortcuts, uninstall entry); the user's own data stays.
$update = Join-Path $installDir "Update.exe"
if (-not (Test-Path $update)) { Fail "Update.exe (uninstaller) not found" }
$p = Start-Process $update -ArgumentList "--uninstall", "--silent" -PassThru -Wait
if ($p.ExitCode -ne 0) { Fail "uninstall exited with $($p.ExitCode)" }
Start-Sleep -Seconds 5
if (Test-Path (Join-Path $installDir "current")) { Fail "program files still present after uninstall" }
$shortcuts = @(
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Interview Assistant.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "Interview Assistant.lnk"))
foreach ($s in $shortcuts) { if (Test-Path $s) { Fail "shortcut still present after uninstall: $s" } }
if (Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\InterviewAssistant.Desktop") { Fail "uninstall entry still registered" }
if (-not (Test-Path $settingsPath) -or -not (Test-Path $workspaceMarker)) { Fail "uninstall removed the user's own data (settings/workspace)" }

Remove-Item $workspaceMarker, $reportMarker -Force -ErrorAction SilentlyContinue
@{ from = $before; fromSetup = $oldSetup.Name; fromSetupSha256 = $hash; to = $after; toSetup = $newSetup.Name
   dataKept = $true; launched = $true; uninstalled = $true } | ConvertTo-Json | Set-Content (Join-Path $evidence "upgrade-test.json")
Write-Host "UPGRADE TEST PASSED ($FromVersion -> $ToVersion in place, data kept, clean uninstall)" -ForegroundColor Green
