<#
.SYNOPSIS
  Assembles the distributable release-candidate files from an already-validated build:
    InterviewAssistant-Windows-x64-v<ver>.zip   (portable, self-contained)
    InterviewAssistant-Setup-v<ver>.exe          (per-user installer, Start Menu + Desktop shortcut, uninstall)
    *.sha256, RELEASE_NOTES-v<ver>.md, BUILD_INFO.txt
.DESCRIPTION
  Runs only after the build, tests, smoke test, self-test, installer and update test have passed in the same job,
  so the artifacts always come from the commit that passed. Audits the package contents and the EXE version metadata.
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Commit = "",
    [string]$RunUrl = "",
    [string]$PublishDir = "artifacts\InterviewAssistant",
    [string]$InstallerDir = "artifacts\installer",
    [string]$OutDir = "artifacts\release"
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Force $OutDir | Out-Null

# 1) Content audit: runtime files + end-user docs only.
$exe = Join-Path $PublishDir "InterviewAssistant.exe"
if (-not (Test-Path $exe)) { throw "Publish output not found: $exe" }
$forbidden = Get-ChildItem $PublishDir -Recurse -File | Where-Object {
    $_.Extension -in ".pdb", ".cs", ".csproj", ".sln", ".dpapi", ".env", ".key", ".pfx", ".log" -or
    $_.Name -like "*Tests*.dll" -or $_.Name -like "appsettings*.json" -or $_.Name -eq "settings.json" -or
    $_.FullName -match "\\samples\\|\\tests\\|\\\.git\\"
}
if ($forbidden) { throw "Refusing to package: $($forbidden.FullName -join ', ')" }
$cloud = Get-Content (Join-Path $PublishDir "cloud.json") -Raw
if ($cloud -match "sk-|pdl_|service_role|password|secret") { throw "cloud.json contains secret-looking values" }

# 2) Version metadata must match the release (FileVersion is numeric; ProductVersion carries the pre-release tag).
$info = (Get-Item $exe).VersionInfo
$numeric = ($Version -split '[-+]')[0]
if (-not $info.FileVersion.StartsWith($numeric)) { throw "EXE FileVersion $($info.FileVersion) does not match $Version" }
if (-not $info.ProductVersion.StartsWith($Version)) { throw "EXE ProductVersion $($info.ProductVersion) does not match $Version" }
Write-Host "EXE: FileVersion $($info.FileVersion) · ProductVersion $($info.ProductVersion) · $($info.ProductName)"

# 3) Portable ZIP.
$zip = Join-Path $OutDir "InterviewAssistant-Windows-x64-v$Version.zip"
Compress-Archive -Path "$PublishDir\*" -DestinationPath $zip

# 4) Installer.
$setup = Get-ChildItem $InstallerDir -Filter "*Setup.exe" | Select-Object -First 1
if (-not $setup) { throw "Setup.exe not found in $InstallerDir" }
$setupOut = Join-Path $OutDir "InterviewAssistant-Setup-v$Version.exe"
Copy-Item $setup.FullName $setupOut

# 5) Checksums, notes, build info.
foreach ($f in @($zip, $setupOut)) {
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant()
    "$h  $(Split-Path $f -Leaf)" | Set-Content -Encoding ascii "$f.sha256"
    Write-Host "SHA256 $h  $(Split-Path $f -Leaf)"
}
$notes = "docs\release\RELEASE_NOTES-v$Version.md"
if (Test-Path $notes) { Copy-Item $notes (Join-Path $OutDir "RELEASE_NOTES-v$Version.md") } else { throw "Release notes missing: $notes" }
$sig = Get-AuthenticodeSignature $exe
@(
    "Product:      Interview Assistant",
    "Version:      $Version",
    "Channel:      $(if ($Version -match '-rc') { 'Release Candidate' } elseif ($Version -match '-') { 'Pre-release' } else { 'Stable' })",
    "Commit:       $Commit",
    "CI run:       $RunUrl",
    "Built (UTC):  $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm'))",
    "Code signing: $(if ($sig.Status -eq 'Valid') { 'Signed by ' + $sig.SignerCertificate.Subject } else { 'UNSIGNED (SmartScreen will warn; auto-update disabled)' })",
    "Portable:     $(Split-Path $zip -Leaf)",
    "Installer:    $(Split-Path $setupOut -Leaf) (per-user, no admin rights, Start Menu + Desktop shortcut, uninstall via Settings -> Apps)"
) | Set-Content (Join-Path $OutDir "BUILD_INFO.txt")
Get-ChildItem $OutDir | Format-Table Name, Length
