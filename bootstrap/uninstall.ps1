# Removes the FruityLink in-process proxy chain from FL Studio's directory (restores stock FL).
# Run elevated. Closes FL first (version.dll is locked while FL runs).
param(
    [string]$FlDir = "C:\Program Files\Image-Line\FL Studio 2025"
)
$ErrorActionPreference = "Stop"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $isAdmin) {
    Start-Process pwsh -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -FlDir `"$FlDir`""
    return
}

$fl = Get-Process FL64 -ErrorAction SilentlyContinue
if ($fl) { Write-Host "Closing FL Studio..."; $fl | Stop-Process -Force; Start-Sleep 2 }

Remove-Item "$FlDir\FruityLink" -Recurse -Force -ErrorAction SilentlyContinue
if (Test-Path "$FlDir\version_backup.dll") {
    Move-Item "$FlDir\version_backup.dll" "$FlDir\version.dll" -Force
    Write-Host "Restored original version.dll from backup."
} else {
    Remove-Item "$FlDir\version.dll" -Force -ErrorAction SilentlyContinue
    Write-Host "Removed proxy version.dll (there was no original — FL uses System32's)."
}
Write-Host "Uninstalled. FL Studio is back to stock."
