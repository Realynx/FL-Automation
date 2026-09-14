# Read-only production-payload checks plus destructive checks in a disposable copy.
[CmdletBinding()]
param(
    [string]$PayloadRoot = (Join-Path $PSScriptRoot '../FruityLink.Installer/payload')
)
$ErrorActionPreference = 'Stop'
$testPayloadRoot = $PayloadRoot
. (Join-Path $PSScriptRoot '../stage-serum-support.ps1')

$production = Get-McpFullPath (Join-Path $testPayloadRoot 'optional-plugins/serum-support')
Assert-SerumSupportPayload $production
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('fruitylink-serum-stage-test-' + [Guid]::NewGuid().ToString('N'))
$tempRoot = Get-McpFullPath ([IO.Path]::GetTempPath())
try {
    Copy-Item -LiteralPath $production -Destination $scratch -Recurse
    Add-Content -LiteralPath (Join-Path $scratch 'LICENSE') -Value 'tampered'
    try { Assert-SerumSupportPayload $scratch; throw 'Modified license bytes were accepted.' }
    catch { if ($_.Exception.Message -eq 'Modified license bytes were accepted.') { throw } }

    Remove-McpStage $tempRoot $scratch
    Copy-Item -LiteralPath $production -Destination $scratch -Recurse
    Copy-Item -LiteralPath (Join-Path $scratch 'fruitylink_serum.whl') -Destination (Join-Path $scratch 'old-version.whl')
    Write-McpChecksums $scratch
    try { Assert-SerumSupportPayload $scratch; throw 'Multiple extension wheels were accepted.' }
    catch { if ($_.Exception.Message -eq 'Multiple extension wheels were accepted.') { throw } }

    Remove-McpStage $tempRoot $scratch
    Copy-Item -LiteralPath $production -Destination $scratch -Recurse
    [IO.File]::WriteAllBytes((Join-Path $scratch 'commercial.fxp'), [byte[]](1, 2, 3))
    Write-McpChecksums $scratch
    try { Assert-SerumSupportPayload $scratch; throw 'A commercial preset payload was accepted.' }
    catch { if ($_.Exception.Message -eq 'A commercial preset payload was accepted.') { throw } }

    Remove-McpStage $tempRoot $scratch
    Copy-Item -LiteralPath $production -Destination $scratch -Recurse
    $wheel = Join-Path $scratch 'fruitylink_serum.whl'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($wheel, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.CreateEntry('fruitylink_serum/factory-bank.SerumPreset')
        $stream = $entry.Open()
        try { $stream.WriteByte(1) } finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
    Write-McpChecksums $scratch
    try { Assert-SerumSupportPayload $scratch; throw 'A valid wheel with an extra preset asset was accepted.' }
    catch { if ($_.Exception.Message -eq 'A valid wheel with an extra preset asset was accepted.') { throw } }

    Write-Host 'Passed Serum support staging, integrity, stable-wheel, archive-allowlist, and commercial-payload checks.'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-McpStage $tempRoot $scratch }
}
