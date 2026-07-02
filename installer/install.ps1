<#
.SYNOPSIS
  One-line installer/uninstaller bootstrap for FruityLink.

.DESCRIPTION
  Locates (or downloads) FruityLink.Installer.exe and runs it headless, elevating via UAC when the
  FL Studio directory needs administrator rights. Designed for a GitHub one-liner:

    # Install (PowerShell, run from an elevated-or-not prompt):
    irm https://raw.githubusercontent.com/OWNER/REPO/main/installer/install.ps1 | iex

    # To pass options through the one-liner, fetch then invoke the scriptblock:
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/OWNER/REPO/main/installer/install.ps1))) -DryRun

  Run locally next to a build of the installer:

    .\install.ps1                 # install (auto-detect FL Studio)
    .\install.ps1 -DryRun         # preview only, write nothing
    .\install.ps1 -Uninstall      # remove FruityLink, restore the original version.dll
    .\install.ps1 -FlPath "D:\FL Studio 2025"

.NOTES
  Set $InstallerUrl / the $Repo placeholder below to your published release before sharing the
  one-liner. When the installer EXE is found next to this script, no download happens.
#>
[CmdletBinding()]
param(
    [string] $FlPath,
    [switch] $Uninstall,
    [switch] $DryRun,
    [Alias('Headless')][switch] $Silent = $true,
    [string] $InstallerExe,
    [string] $InstallerUrl = '',
    [switch] $Help
)

$ErrorActionPreference = 'Stop'

# ---- EDIT ME: point these at your published release -------------------------------------------
$Repo         = 'OWNER/REPO'   # e.g. 'fox/FruityLink'
$DefaultAsset = "https://github.com/$Repo/releases/latest/download/FruityLink.Installer.exe"
# ----------------------------------------------------------------------------------------------

function Write-Info { param($m) Write-Host "[FruityLink] $m" -ForegroundColor Cyan }
function Write-Err  { param($m) Write-Host "[FruityLink] $m" -ForegroundColor Red }

if ($Help) {
    Write-Host @"
FruityLink install bootstrap
  -Install (default)     Install FruityLink into FL Studio.
  -Uninstall             Remove FruityLink and restore the original version.dll.
  -DryRun                Preview every action without writing anything (no elevation needed).
  -FlPath <dir>          FL Studio directory (default: auto-detect).
  -InstallerExe <path>   Use a specific installer EXE.
  -InstallerUrl <url>    Download the installer EXE from this URL if not found locally.
"@
    return
}

function Resolve-InstallerExe {
    # 1. Explicit path.
    if ($InstallerExe -and (Test-Path $InstallerExe)) { return (Resolve-Path $InstallerExe).Path }

    # 2. Next to this script (release layout, or a dev build copied here).
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
    foreach ($candidate in @(
        (Join-Path $here 'FruityLink.Installer.exe'),
        (Join-Path $here 'FruityLink.Installer\bin\Release\net9.0-windows\FruityLink.Installer.exe'),
        (Join-Path $here 'FruityLink.Installer\bin\Debug\net9.0-windows\FruityLink.Installer.exe')
    )) {
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    }

    # 3. Download.
    $url = if ($InstallerUrl) { $InstallerUrl } else { $DefaultAsset }
    if ($url -match 'OWNER/REPO') {
        throw "No installer EXE found locally and no download URL configured. Edit `$Repo/`$InstallerUrl in install.ps1, or pass -InstallerExe."
    }
    $dest = Join-Path ([System.IO.Path]::GetTempPath()) ("FruityLink.Installer-" + [guid]::NewGuid().ToString('N') + ".exe")
    Write-Info "Downloading installer from $url"
    Invoke-WebRequest -Uri $url -OutFile $dest -UseBasicParsing
    return $dest
}

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Build the installer argument list.
$verb = if ($Uninstall) { '--uninstall' } else { '--install' }
$exeArgs = @($verb)
if ($Silent)  { $exeArgs += '--silent' }
if ($DryRun)  { $exeArgs += '--dry-run' }
if ($FlPath)  { $exeArgs += @('--fl-path', $FlPath) }

try {
    $exe = Resolve-InstallerExe
} catch {
    Write-Err $_.Exception.Message
    exit 2
}

Write-Info ("Running: {0} {1}" -f $exe, ($exeArgs -join ' '))

# Real install/uninstall into Program Files needs admin; dry-run does not.
$needsAdmin = (-not $DryRun) -and (-not (Test-IsAdmin))

if ($needsAdmin) {
    Write-Info "Elevating (UAC) to modify the FL Studio directory..."
    $p = Start-Process -FilePath $exe -ArgumentList $exeArgs -Verb RunAs -Wait -PassThru
} else {
    $p = Start-Process -FilePath $exe -ArgumentList $exeArgs -NoNewWindow -Wait -PassThru
}

$code = $p.ExitCode
if ($code -eq 0) {
    Write-Info ("Done ({0}). Log: %LocalAppData%\FruityLink\installer-log.txt" -f $verb.TrimStart('-'))
} else {
    Write-Err  ("Installer exited with code {0}. See %LocalAppData%\FruityLink\installer-log.txt" -f $code)
}
exit $code
