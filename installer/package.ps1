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
# <version> comes from the repo-root VERSION file — the single product-version
# source shared with Directory.Build.props (assembly stamping) and
# deploy\install-all.sh, which AUTO-INCREMENTS it on every publish, runs this
# during the site step, and uploads the newest artifact to Server B, where the
# marketing API serves it as /opt/fl-automate/downloads/fl-automate-installer.zip.
#
# Idempotent: re-running wipes the publish staging dir and replaces the zip for
# the same version. Compatible with Windows PowerShell 5.1 and PowerShell 7+.
# =============================================================================
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$McpDistributionPath,
    [string]$McpSourceRoot,
    [string]$McpPythonWheel,
    [string]$McpPythonRuntimeCacheDirectory,
    [string]$SerumSupportWheel
)

$ErrorActionPreference = 'Stop'

$ProjectDir  = Join-Path $PSScriptRoot 'FruityLink.Installer'
$ProjectFile = Join-Path $ProjectDir 'FruityLink.Installer.csproj'
$ArtifactDir = Join-Path $PSScriptRoot 'artifacts'
$PublishDir  = Join-Path $ArtifactDir 'publish-win-x64'

if (-not (Test-Path $ProjectFile)) {
    throw "Project not found: $ProjectFile"
}

# --- Version from the repo-root VERSION file ----------------------------------
# (Single source of truth: Directory.Build.props stamps the same value on every
# assembly, so the installed plugin's version always matches the artifact name.)
$VersionFile = Join-Path (Split-Path $PSScriptRoot -Parent) 'VERSION'
if (-not (Test-Path $VersionFile)) { throw "No VERSION file at $VersionFile" }
$Version = (Get-Content $VersionFile -Raw).Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION file content '$Version' is not <major>.<minor>.<patch>" }
Write-Host "==> Packaging FL Automate installer v$Version ($Configuration, win-x64, self-contained single-file)"

# --- Publish (clean staging dir first — idempotent) ---------------------------
$ArtifactDir = [IO.Path]::GetFullPath($ArtifactDir)
$PublishDir = [IO.Path]::GetFullPath($PublishDir)
if (-not $PublishDir.StartsWith($ArtifactDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish staging directory escapes installer artifacts.' }
& {
    param($helper, $root, $target)
    . $helper
    Remove-McpStage $root $target
} (Join-Path $PSScriptRoot 'stage-mcp.ps1') $ArtifactDir $PublishDir
New-Item -ItemType Directory -Force $ArtifactDir | Out-Null

# Step 1: plain build — runs the csproj's StagePayloadFromSource target, which
# refreshes payload\ from the source tree (host + FL Agent plugin publish +
# cmake natives when present).
Write-Host '==> dotnet build (refreshes payload\ from source)'
& dotnet build $ProjectFile -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

# FL MCP is an offline optional component in both editions. Source builds use
# the sibling Fl-MCP checkout and this SDK; a verified prebuilt distribution can
# be supplied explicitly. Stage after the host refresh so SDK versions match.
Write-Host '==> Stage and verify bundled FL MCP plugin + companion'
& (Join-Path $PSScriptRoot 'stage-mcp.ps1') `
    -DistributionPath $McpDistributionPath `
    -McpSourceRoot $McpSourceRoot `
    -PythonWheel $McpPythonWheel `
    -PythonRuntimeCacheDirectory $McpPythonRuntimeCacheDirectory `
    -SdkRoot (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk') `
    -PayloadRoot (Join-Path $ProjectDir 'payload')
if (-not $?) { throw 'FL MCP staging failed.' }

Write-Host '==> Stage the Python editor plugin and framework-owned interpreter'
& (Join-Path $PSScriptRoot 'stage-python-ide.ps1') -Configuration $Configuration -PayloadRoot (Join-Path $ProjectDir 'payload')
if (-not $?) { throw 'Python IDE staging failed.' }

Write-Host '==> Stage and verify the Serum support extension wheel'
& (Join-Path $PSScriptRoot 'stage-serum-support.ps1') -PayloadRoot (Join-Path $ProjectDir 'payload') -SerumWheel $SerumSupportWheel
if (-not $?) { throw 'Serum support staging failed.' }

Write-Host '==> Stage the SDK session host for standalone Python builds'
& (Join-Path $PSScriptRoot 'stage-session-host.ps1') -Configuration $Configuration -PayloadRoot (Join-Path $ProjectDir 'payload')
if (-not $?) { throw 'SDK session host staging failed.' }

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
    Copy-Item -LiteralPath $PayloadSrc -Destination $PublishDir -Recurse -Force
    Get-ChildItem -LiteralPath $PayloadDst -Recurse -Filter *.pdb -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
}
if (-not (Test-Path (Join-Path $PayloadDst 'version.dll'))) {
    throw "payload\version.dll still missing after copy — refusing to package a broken installer"
}

# Debug symbols are dead weight in a shipped installer.
Get-ChildItem -LiteralPath $PublishDir -Recurse -Filter *.pdb -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

# Validate the final optional component again after publish and symbol removal.
& {
    param($helper, $bundle, $sharedHostRoot, $ideBundle)
    . $helper
    Assert-McpChecksums $bundle
    Assert-McpChecksums (Join-Path $bundle 'companion')
    Assert-McpPythonRuntime (Join-Path $bundle 'companion')
    Assert-McpSharedHost $sharedHostRoot (Join-Path $bundle 'plugin')
    Assert-McpChecksums $ideBundle
    Assert-McpChecksums (Join-Path $sharedHostRoot 'python')
    Assert-McpDependencyClosure $ideBundle 'FruityLink.Plugins.PythonIde.deps.json' $sharedHostRoot
    Assert-McpDependencyClosure $sharedHostRoot 'FruityLink.Host.deps.json' $sharedHostRoot
} (Join-Path $PSScriptRoot 'stage-mcp.ps1') (Join-Path $PayloadDst 'optional-plugins/fl-mcp') (Join-Path $PayloadDst 'FruityLink') (Join-Path $PayloadDst 'optional-plugins/fl-python-ide')

& {
    param($helper, $bundle)
    . $helper
    Assert-SerumSupportPayload $bundle
} (Join-Path $PSScriptRoot 'stage-serum-support.ps1') (Join-Path $PayloadDst 'optional-plugins/serum-support')

. (Join-Path $PSScriptRoot 'shared-ui-payload.ps1')
$sharedUiHost = Join-Path $PayloadDst 'FruityLink'
Assert-SharedUiPayload $sharedUiHost (Join-Path $PayloadDst 'optional-plugins/fl-python-ide')
$productUiPlugin = Join-Path $sharedUiHost 'plugins/fl-agent'
if (Test-Path -LiteralPath $productUiPlugin -PathType Container) {
    Assert-SharedUiPayload $sharedUiHost $productUiPlugin
}

# --- Zip: ONE publish, TWO editions ---------------------------------------------
# Entry-by-entry with normalized forward-slash names: .NET Framework's
# ZipFile.CreateFromDirectory (PS 5.1) writes backslash entry names, PS7's
# doesn't — this keeps the artifact byte-layout identical under both editions.
# No root folder inside the zip: exe + payload/ at the top level.
#
#   * PACKAGED  fl-automate-installer-v<ver>.zip
#       The sold product: full payload including the FL Automate plugin
#       (payload/FruityLink/plugins/fl-agent/). Uploaded to the marketing site.
#   * COMMUNITY fruitylink-installer-v<ver>.zip
#       The FruityLink plugin system, Python IDE, and optional FL MCP — SAME exe,
#       fl-agent payload stripped. FL MCP keeps its PolyForm Noncommercial license.
#       The installer detects the edition at runtime from
#       payload presence (InstallerInfo.IsPackagedEdition). Published as a
#       GitHub release artifact on the open-source repo.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-InstallerZip {
    param(
        [string]$ZipPath,
        [string[]]$ExcludePrefixes = @()
    )
    $ZipPath = [IO.Path]::GetFullPath($ZipPath)
    if (-not $ZipPath.StartsWith($ArtifactDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP destination escapes installer artifacts.' }
    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, 'Create')
    try {
        $trim = (Get-Item $PublishDir).FullName.Length + 1
        Get-ChildItem -LiteralPath $PublishDir -Recurse -File | ForEach-Object {
            $rel = $_.FullName.Substring($trim).Replace('\', '/')
            foreach ($prefix in $ExcludePrefixes) {
                if ($rel.StartsWith($prefix)) { return }
            }
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $_.FullName, $rel,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }

    $SizeMB = [math]::Round((Get-Item $ZipPath).Length / 1MB, 1)
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        foreach ($required in @('payload/optional-plugins/fl-mcp/plugin/FlMcp.Plugin.dll',
            'payload/FruityLink/FruityLink.Scripting.dll',
            'payload/FruityLink/python/runtime/python314.dll',
            'payload/FruityLink/python/FRUITYLINK-LICENSE.txt',
            'payload/optional-plugins/fl-python-ide/FruityLink.Plugins.PythonIde.dll',
            'payload/optional-plugins/fl-python-ide/AvaloniaEdit.dll',
            'payload/optional-plugins/fl-python-ide/LICENSE',
            'payload/optional-plugins/serum-support/fruitylink_serum.whl',
            'payload/optional-plugins/serum-support/LICENSE',
            'payload/optional-plugins/serum-support/SHA256SUMS.json',
            'payload/optional-plugins/fl-mcp/companion/server/FlMcp.Server.dll',
            'payload/optional-plugins/fl-mcp/companion/python/fruitylink_python-0.2.0-py3-none-any.whl',
            'payload/optional-plugins/fl-mcp/companion/python/runtime/python.exe',
            'payload/optional-plugins/fl-mcp/companion/python/runtime/python314.dll',
            'payload/optional-plugins/fl-mcp/companion/python/runtime/python314._pth',
            'payload/optional-plugins/fl-mcp/companion/python/runtime/LICENSE.txt',
            'payload/optional-plugins/fl-mcp/companion/python/RUNTIME-PROVENANCE.json',
            'payload/optional-plugins/fl-mcp/companion/INSTALLER-SETUP.md',
            'payload/optional-plugins/fl-mcp/companion/SOURCE-README.md',
            'licenses/Tomlyn-2.10.1-LICENSE.txt',
            'THIRD-PARTY-NOTICES.md',
            'payload/optional-plugins/fl-mcp/companion/LICENSE')) {
            if ($null -eq $zip.GetEntry($required)) { throw "Installer ZIP is missing bundled FL MCP file: $required" }
        }
        foreach ($prefix in $ExcludePrefixes) {
            if (@($zip.Entries | Where-Object { $_.FullName.StartsWith($prefix) }).Count -gt 0) { throw "Installer ZIP retained excluded payload: $prefix" }
        }
        $top = $zip.Entries.FullName |
            ForEach-Object { ($_ -split '/')[0] } |
            Sort-Object -Unique
        Write-Host "==> Top-level zip entries: $($top -join ', ')"
        Write-Host "==> Zip entry count: $($zip.Entries.Count)"
    } finally { $zip.Dispose() }
    Write-Host "==> Artifact: $ZipPath ($SizeMB MB)"
}

Write-Host '==> Packaged edition (sold plugin included)'
New-InstallerZip -ZipPath (Join-Path $ArtifactDir "fl-automate-installer-v$Version.zip")

Write-Host '==> Community edition (FruityLink + Python IDE + optional FL MCP; FL Agent excluded)'
New-InstallerZip -ZipPath (Join-Path $ArtifactDir "fruitylink-installer-v$Version.zip") `
    -ExcludePrefixes @('payload/FruityLink/plugins/fl-agent/')
