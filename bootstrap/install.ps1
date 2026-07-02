# Installs the FruityLink in-process proxy chain into FL Studio's program directory.
# MUST run elevated (writes to Program Files). Self-elevates via UAC if needed.
#
#   pwsh bootstrap\install.ps1 [-FlDir "C:\Program Files\Image-Line\FL Studio 2025"]
#
# Closes FL first if it is running (version.dll is loaded for the whole session, so it can only be
# placed/replaced while FL is closed). Backs up any existing version.dll. Idempotent.
param(
    [string]$FlDir = "C:\Program Files\Image-Line\FL Studio 2025"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dist = "$repo\bootstrap\dist"

if (-not (Test-Path "$dist\version.dll")) {
    Write-Host "dist not found — building first..."
    & "$repo\bootstrap\build-and-stage.ps1"
}

# self-elevate
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Re-launching elevated..."
    Start-Process pwsh -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -FlDir `"$FlDir`""
    return
}

if (-not (Test-Path "$FlDir\FL64.exe")) { throw "FL64.exe not found in $FlDir" }

# FL must be closed to place version.dll (it is loaded for the whole session).
$fl = Get-Process FL64 -ErrorAction SilentlyContinue
if ($fl) { Write-Host "Closing FL Studio..."; $fl | Stop-Process -Force; Start-Sleep 2 }

# Back up any pre-existing version.dll (unlikely; not an FL-shipped file).
if (Test-Path "$FlDir\version.dll") {
    if (-not (Test-Path "$FlDir\version_backup.dll")) {
        Copy-Item "$FlDir\version.dll" "$FlDir\version_backup.dll" -Force
        Write-Host "Backed up existing version.dll -> version_backup.dll"
    }
}

Copy-Item "$dist\version.dll" "$FlDir\version.dll" -Force
New-Item -ItemType Directory -Path "$FlDir\FruityLink" -Force | Out-Null
Copy-Item "$dist\FruityLink\*" "$FlDir\FruityLink\" -Recurse -Force

Write-Host "`nInstalled into $FlDir :"
Write-Host "  version.dll"
Get-ChildItem "$FlDir\FruityLink" -File | ForEach-Object { "  FruityLink\" + $_.Name }
Write-Host "`nLaunch FL Studio. Watch %TEMP%\fruitylink-proxy.log for the boot chain."
Write-Host "Disable temporarily without uninstalling: set env var FRUITYLINK_DISABLE=1."
