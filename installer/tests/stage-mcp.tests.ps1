[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DistributionPath,
    [Parameter(Mandatory)][string]$HostDirectory
)
$ErrorActionPreference = 'Stop'
$testInstallerRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $testInstallerRoot 'stage-mcp.ps1') -DistributionPath $DistributionPath -HostDirectory $HostDirectory
$testArtifactRoot = Join-Path $testInstallerRoot 'artifacts'
$testRoot = Join-Path $testArtifactRoot ('stage-mcp-tests-' + [Guid]::NewGuid().ToString('N'))
$checks = 0

function Assert-Packaging {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:checks++
}

function Assert-PackagingFailure {
    param([scriptblock]$Action, [string]$Message)
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert-Packaging $failed $Message
}

try {
    Assert-McpDistribution $DistributionPath $HostDirectory
    $checks++
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    $fixture = Join-Path $testRoot 'distribution'
    Copy-Item -LiteralPath $DistributionPath -Destination $fixture -Recurse

    $readme = Join-Path $fixture 'README.md'
    $originalReadme = [IO.File]::ReadAllBytes($readme)
    Add-Content -LiteralPath $readme -Value 'corrupted fixture'
    Assert-PackagingFailure { Assert-McpDistribution $fixture $HostDirectory } 'Modified bytes were accepted.'
    [IO.File]::WriteAllBytes($readme, $originalReadme)

    $extra = Join-Path $fixture 'unlisted.txt'
    Set-Content -LiteralPath $extra -Value 'not listed'
    Assert-PackagingFailure { Assert-McpDistribution $fixture $HostDirectory } 'Unlisted file was accepted.'
    Remove-Item -LiteralPath $extra

    $manifestPath = Join-Path $fixture 'SHA256SUMS.json'
    $originalManifest = [IO.File]::ReadAllBytes($manifestPath)
    $entries = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $entries[0].File = '../outside.txt'
    ConvertTo-Json -InputObject $entries | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Assert-PackagingFailure { Assert-McpDistribution $fixture $HostDirectory } 'Traversal manifest was accepted.'
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)

    $library = Join-Path $fixture 'server/ModelContextProtocol.dll'
    $originalLibrary = [IO.File]::ReadAllBytes($library)
    Remove-Item -LiteralPath $library
    Write-McpChecksums $fixture
    Assert-PackagingFailure { Assert-McpDistribution $fixture $HostDirectory } 'Incomplete runtime closure was accepted.'
    [IO.File]::WriteAllBytes($library, $originalLibrary)
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)

    $license = Join-Path $fixture 'LICENSE'
    $originalLicense = [IO.File]::ReadAllBytes($license)
    Set-Content -LiteralPath $license -Value 'wrong license'
    Write-McpChecksums $fixture
    Assert-PackagingFailure { Assert-McpDistribution $fixture $HostDirectory } 'Altered license with matching manifest was accepted.'
    [IO.File]::WriteAllBytes($license, $originalLicense)
    [IO.File]::WriteAllBytes($manifestPath, $originalManifest)

    Assert-PackagingFailure { Assert-McpDistribution $fixture (Join-Path $testRoot 'missing-host') } 'Missing host contracts were accepted.'
    $wrongHost = Join-Path $testRoot 'wrong-host'
    New-Item -ItemType Directory -Path $wrongHost | Out-Null
    Copy-Item -LiteralPath (Join-Path $fixture 'plugin/fl-mcp/FlMcp.Protocol.dll') -Destination (Join-Path $wrongHost 'FruityLink.Core.dll')
    Copy-Item -LiteralPath (Join-Path $HostDirectory 'FruityLink.Plugins.Abstractions.dll') -Destination $wrongHost
    Assert-PackagingFailure { Assert-McpDistribution $fixture $wrongHost } 'Mismatched host assembly version was accepted.'
    $payload = Join-Path $testRoot 'payload'
    $staged = Copy-McpDistribution $fixture $payload
    Assert-McpChecksums $staged
    Assert-McpChecksums (Join-Path $staged 'companion')
    $checks += 2
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $staged 'plugin/FlMcp.Plugin.dll')) 'Plugin was not mapped to plugin/.'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $staged 'companion/server/FlMcp.Server.dll')) 'Server was not mapped to companion/server/.'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $staged 'companion/register-codex.ps1')) 'Registration helper missing.'
    Assert-Packaging (-not (Test-Path -LiteralPath (Join-Path $staged 'companion/plugin'))) 'Companion contains duplicate plugin directory.'
    Assert-Packaging (@(Get-McpFiles $staged | Where-Object Extension -eq '.pdb').Count -eq 0) 'Staged checksum manifest contains files that packaging strips.'
    $stagedAgain = Copy-McpDistribution $fixture $payload
    Assert-Packaging ($stagedAgain -eq $staged) 'Repeated staging changed destination.'
    Assert-McpChecksums $stagedAgain
    $checks++
    Assert-PackagingFailure { Remove-McpStage $payload $testRoot } 'Cleanup accepted a target outside its owned staging root.'
    Write-Host "Passed $checks MCP packaging checks. Production payload and FL installations were not changed."
} finally {
    Remove-McpStage $testArtifactRoot $testRoot
}
