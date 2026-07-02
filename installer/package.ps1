# =============================================================================
# FL Automate — Windows installer packaging.
#
#   pwsh -NoProfile -File installer\package.ps1          (PowerShell 7)
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File installer\package.ps1
#
# Publishes installer\FruityLink.Installer self-contained for win-x64 as a
# SINGLE-FILE exe (IncludeNativeLibrariesForSelfExtract=true — verified to work
# with Avalonia 11.3; the payload\ tree stays loose next to the exe, which is
# exactly how the exe resolves it at runtime), then zips the publish folder to:
#
#   installer\artifacts\fl-automate-installer-v<version>.zip
#
# <version> comes from the csproj <Version>. deploy\install-all.sh runs this
# during the site step and uploads the newest artifact to Server B, where the
# marketing API serves it as /opt/fl-automate/downloads/fl-automate-installer.zip.
#
# Idempotent: re-running wipes the publish staging dir and replaces the zip for
# the same version. Compatible with Windows PowerShell 5.1 and PowerShell 7+.
# =============================================================================
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$ProjectDir  = Join-Path $PSScriptRoot 'FruityLink.Installer'
$ProjectFile = Join-Path $ProjectDir 'FruityLink.Installer.csproj'
$ArtifactDir = Join-Path $PSScriptRoot 'artifacts'
$PublishDir  = Join-Path $ArtifactDir 'publish-win-x64'

if (-not (Test-Path $ProjectFile)) {
    throw "Project not found: $ProjectFile"
}

# --- Version from the csproj <Version> ---------------------------------------
$csproj  = Get-Content $ProjectFile -Raw
$m = [regex]::Match($csproj, '<Version>\s*([^<\s]+)\s*</Version>')
if (-not $m.Success) { throw "No <Version> found in $ProjectFile" }
$Version = $m.Groups[1].Value
Write-Host "==> Packaging FL Automate installer v$Version ($Configuration, win-x64, self-contained single-file)"

# --- Publish (clean staging dir first — idempotent) ---------------------------
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
New-Item -ItemType Directory -Force $ArtifactDir | Out-Null

# Step 1: plain build — runs the csproj's StagePayloadFromSource target, which
# refreshes payload\ from the source tree (host + FL Agent plugin publish +
# cmake natives when present).
Write-Host '==> dotnet build (refreshes payload\ from source)'
& dotnet build $ProjectFile -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

# Step 2: the actual publish. HasSourceTree=false skips the staging target and
# its ProjectReferences on purpose: the single-file/RID global properties would
# otherwise leak into the nested FL Agent plugin publish (a library —
# NETSDK1099) and bloat the payload with a second runtime. Step 1 just
# refreshed the payload\ snapshot, so this publishes exactly those bits.
Write-Host '==> dotnet publish (win-x64, self-contained, single-file)'
& dotnet publish $ProjectFile `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:HasSourceTree=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# --- Sanity: exe + payload must both be in the publish output -----------------
$Exe = Join-Path $PublishDir 'FruityLink.Installer.exe'
if (-not (Test-Path $Exe)) { throw "Publish output has no FruityLink.Installer.exe in $PublishDir" }

# The csproj's None(CopyToOutputDirectory) items normally publish payload\ as
# loose files next to the single-file exe. If an SDK change ever stops that,
# fall back to copying the source payload snapshot explicitly — the exe resolves
# its payload from .\payload next to itself, so the zip MUST contain it.
$PayloadDst = Join-Path $PublishDir 'payload'
if (-not (Test-Path (Join-Path $PayloadDst 'version.dll'))) {
    Write-Host '  payload\ missing from publish output — copying from project source'
    $PayloadSrc = Join-Path $ProjectDir 'payload'
    if (-not (Test-Path $PayloadSrc)) { throw "No payload source at $PayloadSrc" }
    Copy-Item $PayloadSrc $PublishDir -Recurse -Force
    Get-ChildItem $PayloadDst -Recurse -Filter *.pdb | Remove-Item -Force
}
if (-not (Test-Path (Join-Path $PayloadDst 'version.dll'))) {
    throw "payload\version.dll still missing after copy — refusing to package a broken installer"
}

# Debug symbols are dead weight in a shipped installer.
Get-ChildItem $PublishDir -Recurse -Filter *.pdb | Remove-Item -Force

# --- Zip -----------------------------------------------------------------------
# Entry-by-entry with normalized forward-slash names: .NET Framework's
# ZipFile.CreateFromDirectory (PS 5.1) writes backslash entry names, PS7's
# doesn't — this keeps the artifact byte-layout identical under both editions.
# No root folder inside the zip: exe + payload/ at the top level.
$ZipPath = Join-Path $ArtifactDir "fl-automate-installer-v$Version.zip"
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($ZipPath, 'Create')
try {
    $trim = (Get-Item $PublishDir).FullName.Length + 1
    Get-ChildItem $PublishDir -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($trim).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $_.FullName, $rel,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }

# --- Report --------------------------------------------------------------------
$SizeMB = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
$zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
try {
    $top = $zip.Entries.FullName |
        ForEach-Object { ($_ -split '/')[0] } |
        Sort-Object -Unique
    Write-Host "==> Top-level zip entries: $($top -join ', ')"
    Write-Host "==> Zip entry count: $($zip.Entries.Count)"
} finally { $zip.Dispose() }
Write-Host "==> Artifact: $ZipPath ($SizeMB MB)"
