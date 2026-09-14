# Build and stage the separately selectable, MIT-licensed Serum helper wheel.
[CmdletBinding()]
param(
    [string]$PayloadRoot = (Join-Path $PSScriptRoot 'FruityLink.Installer/payload'),
    [string]$SerumWheel
)
$ErrorActionPreference = 'Stop'
$serumPayloadRoot = $PayloadRoot
$serumWheelInput = $SerumWheel
. (Join-Path $PSScriptRoot 'stage-mcp.ps1')

$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'sdk/extensions/serum-support'
$builtWheelName = 'fruitylink_serum-0.1.0-py3-none-any.whl'
$installedWheelName = 'fruitylink_serum.whl'

function Assert-SerumSupportPayload([string]$Root) {
    $Root = Get-McpFullPath $Root
    $files = @(Get-McpFiles $Root)
    $allowedPayloadFiles = @($installedWheelName, 'LICENSE', 'README.md', 'SHA256SUMS.json')
    foreach ($required in $allowedPayloadFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $required) -PathType Leaf)) {
            throw "Serum support payload is missing $required."
        }
    }
    $unexpectedPayload = @($files | Where-Object { $allowedPayloadFiles -cnotcontains $_.Name })
    if ($unexpectedPayload.Count -gt 0 -or $files.Count -ne $allowedPayloadFiles.Count) {
        throw 'Serum support payload contains files outside its wheel, README, license, and checksum allowlist.'
    }
    $wheels = @($files | Where-Object Extension -eq '.whl')
    if ($wheels.Count -ne 1 -or $wheels[0].Name -cne $installedWheelName) {
        throw "Serum support must contain exactly the stable installed wheel $installedWheelName."
    }
    $forbidden = @($files | Where-Object {
        $_.Extension -in @('.fxp', '.fxb', '.vstpreset', '.dll', '.vst3') -or
        $_.Name -match '(?i)serum(_x64)?\.exe'
    })
    if ($forbidden.Count -gt 0) { throw 'Commercial presets or plugin binaries cannot be bundled with Serum support.' }
    Assert-McpChecksums $Root
    $stagedLicense = Join-Path $Root 'LICENSE'
    if ((Get-FileHash -LiteralPath $stagedLicense -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $source 'LICENSE') -Algorithm SHA256).Hash) {
        throw 'Staged Serum support license differs from the reviewed source license.'
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($wheels[0].FullName)
    try {
        $allowedWheelEntries = @(
            'fruitylink_serum/__init__.py',
            'fruitylink_serum/analysis.py',
            'fruitylink_serum/builder.py',
            'fruitylink_serum/cbor.py',
            'fruitylink_serum/inventory.py',
            'fruitylink_serum/loading.py',
            'fruitylink_serum/schema.py',
            'fruitylink_serum/state_reader.py',
            'fruitylink_serum/vstpreset.py',
            'fruitylink_serum/xfer.py',
            'fruitylink_serum/data/filters.json',
            'fruitylink_serum/data/fx-schema.json',
            'fruitylink_serum/data/parameter-map.json',
            'fruitylink_serum/data/wavetables.json',
            'fruitylink_serum-0.1.0.dist-info/METADATA',
            'fruitylink_serum-0.1.0.dist-info/WHEEL',
            'fruitylink_serum-0.1.0.dist-info/licenses/LICENSE',
            'fruitylink_serum-0.1.0.dist-info/RECORD'
        )
        $wheelNames = @($zip.Entries | ForEach-Object FullName)
        if ($wheelNames.Count -ne $allowedWheelEntries.Count -or
            @($wheelNames | Where-Object { $allowedWheelEntries -cnotcontains $_ }).Count -gt 0 -or
            @($wheelNames | Select-Object -Unique).Count -ne $wheelNames.Count) {
            throw 'Serum support wheel entries do not match the reviewed eighteen-file allowlist.'
        }
        $metadata = @($zip.Entries | Where-Object FullName -eq 'fruitylink_serum-0.1.0.dist-info/METADATA')
        $license = @($zip.Entries | Where-Object FullName -eq 'fruitylink_serum-0.1.0.dist-info/licenses/LICENSE')
        if ($metadata.Count -ne 1 -or $license.Count -ne 1) { throw 'Serum support wheel lacks expected package metadata or embedded license.' }
        $reader = [IO.StreamReader]::new($metadata[0].Open())
        try { $metadataText = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($metadataText -notmatch '(?m)^Name: fruitylink-serum\r?$' -or $metadataText -notmatch '(?m)^Version: 0\.1\.0\r?$' -or $metadataText -notmatch '(?m)^License-Expression: MIT\r?$') {
            throw 'Serum support wheel identity or license metadata is unexpected.'
        }
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $licenseStream = $license[0].Open()
            try { $embeddedLicenseHash = [Convert]::ToHexString($hasher.ComputeHash($licenseStream)) }
            finally { $licenseStream.Dispose() }
        } finally { $hasher.Dispose() }
        if ($embeddedLicenseHash -ne (Get-FileHash -LiteralPath $stagedLicense -Algorithm SHA256).Hash) {
            throw 'Embedded wheel license differs from the staged reviewed license.'
        }
    } finally { $zip.Dispose() }
}

function Invoke-SerumSupportStaging([string]$PayloadRoot, [string]$SerumWheel) {
    $target = Join-Path (Get-McpFullPath $PayloadRoot) 'optional-plugins/serum-support'
    Remove-McpStage (Get-McpFullPath $PayloadRoot) $target
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    if ([string]::IsNullOrWhiteSpace($SerumWheel)) {
        $buildRoot = Join-Path ([IO.Path]::GetTempPath()) ('fruitylink-serum-wheel-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $buildRoot | Out-Null
        try {
            & uv run --project (Join-Path $repo 'sdk/python') python -m build --wheel --outdir $buildRoot $source
            if ($LASTEXITCODE -ne 0) { throw "Serum support wheel build failed (exit $LASTEXITCODE)." }
            $SerumWheel = Join-Path $buildRoot $builtWheelName
            if (-not (Test-Path -LiteralPath $SerumWheel -PathType Leaf)) { throw "Build did not produce $builtWheelName." }
            Copy-Item -LiteralPath $SerumWheel -Destination (Join-Path $target $installedWheelName)
        } finally {
            if (Test-Path -LiteralPath $buildRoot) { Remove-McpStage ([IO.Path]::GetTempPath()) $buildRoot }
        }
    } else {
        $SerumWheel = Get-McpFullPath $SerumWheel
        if ((Split-Path $SerumWheel -Leaf) -cne $builtWheelName) { throw "Expected release wheel filename $builtWheelName." }
        Copy-Item -LiteralPath $SerumWheel -Destination (Join-Path $target $installedWheelName)
    }
    Copy-Item -LiteralPath (Join-Path $source 'LICENSE') -Destination $target
    Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination $target
    Write-McpChecksums $target
    Assert-SerumSupportPayload $target
    Write-Host "Staged Serum support at $target"
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-SerumSupportStaging -PayloadRoot $serumPayloadRoot -SerumWheel $serumWheelInput
}
