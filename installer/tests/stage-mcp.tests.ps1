[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DistributionPath,
    [Parameter(Mandatory)][string]$HostDirectory,
    [string]$PythonRuntimeCacheDirectory
)
$ErrorActionPreference = 'Stop'
$testInstallerRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $testInstallerRoot 'stage-mcp.ps1') -DistributionPath $DistributionPath -HostDirectory $HostDirectory -PythonRuntimeCacheDirectory $PythonRuntimeCacheDirectory
$testArtifactRoot = Join-Path $testInstallerRoot 'artifacts'
$testRoot = Join-Path $testArtifactRoot ('stage-mcp-tests-' + [Guid]::NewGuid().ToString('N'))
$checks = 0
if (-not $PythonRuntimeCacheDirectory) { $PythonRuntimeCacheDirectory = Join-Path $testInstallerRoot 'artifacts/python-runtime-cache' }

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
    $differentScriptingHost = Join-Path $testRoot 'different-scripting-host'
    New-Item -ItemType Directory -Path $differentScriptingHost | Out-Null
    foreach ($name in @('FruityLink.Core.dll', 'FruityLink.Plugins.Abstractions.dll', 'FruityLink.Scripting.dll')) {
        Copy-Item -LiteralPath (Join-Path $HostDirectory $name) -Destination (Join-Path $differentScriptingHost $name)
    }
    [IO.File]::AppendAllText((Join-Path $differentScriptingHost 'FruityLink.Scripting.dll'), 'changed-scripting-build')
    Assert-PackagingFailure { Assert-McpSharedHost $differentScriptingHost (Join-Path $fixture 'plugin/fl-mcp') } 'Different scripting binaries with the same SDK version were accepted.'
    $payload = Join-Path $testRoot 'payload'
    $staged = Copy-McpDistribution $fixture $payload $PythonRuntimeCacheDirectory
    Assert-McpChecksums $staged
    Assert-McpChecksums (Join-Path $staged 'companion')
    $checks += 2
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $staged 'plugin/FlMcp.Plugin.dll')) 'Plugin was not mapped to plugin/.'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $staged 'companion/server/FlMcp.Server.dll')) 'Server was not mapped to companion/server/.'
    $companion = Join-Path $staged 'companion'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $companion 'python/runtime/python.exe')) 'Private interpreter missing.'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $companion 'python/runtime/python314.dll')) 'Embedded CPython DLL missing.'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $companion 'python/runtime/LICENSE.txt')) 'Official Python license/notices missing.'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $companion 'python/RUNTIME-PROVENANCE.json')) 'Runtime provenance missing.'
    Assert-Packaging (Test-Path -LiteralPath (Join-Path $companion 'INSTALLER-SETUP.md')) 'Installer client setup documentation missing.'
    Assert-Packaging ((Get-FileHash -LiteralPath (Join-Path $companion 'SOURCE-README.md')).Hash -eq (Get-FileHash -LiteralPath $readme).Hash) 'Original MCP README was not preserved.'
    Assert-Packaging ((Get-Content -LiteralPath (Join-Path $companion 'README.md') -Raw).Contains('INSTALLER-SETUP.md')) 'Installed README does not distinguish the bundled setup flow.'
    Assert-Packaging ((Get-Content -LiteralPath (Join-Path $companion 'INSTALLER-SETUP.md') -Raw).Contains('FL_MCP_PYTHON_RUNTIME')) 'Installer setup does not describe the runtime directory contract.'
    $provenancePath = Join-Path $companion 'python/RUNTIME-PROVENANCE.json'
    $provenanceBytes = [IO.File]::ReadAllBytes($provenancePath)
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    Assert-Packaging ($provenance.executionMode -eq 'embedded-in-fl') 'Runtime provenance does not identify embedded execution.'
    $provenance.executionMode = 'external-worker'
    $provenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $provenancePath -Encoding UTF8
    Assert-PackagingFailure { Assert-McpPythonRuntime $companion } 'Obsolete worker provenance was accepted.'
    [IO.File]::WriteAllBytes($provenancePath, $provenanceBytes)
    Test-McpPythonRuntime $companion
    Assert-McpChecksums $companion
    $checks += 2
    $pathConfig = Join-Path $companion 'python/runtime/python314._pth'
    $originalConfig = [IO.File]::ReadAllBytes($pathConfig)
    Add-Content -LiteralPath $pathConfig -Value 'import site'
    Assert-PackagingFailure { Assert-McpPythonRuntime $companion } 'Global site initialization was accepted.'
    [IO.File]::WriteAllBytes($pathConfig, $originalConfig)
    $runtimeSource = Get-McpPythonRuntimeSource
    $archivePath = Get-McpPythonArchive $PythonRuntimeCacheDirectory $runtimeSource
    $badCache = Join-Path $testRoot 'damaged-cache'
    New-Item -ItemType Directory -Path $badCache | Out-Null
    $badArchive = Join-Path $badCache $runtimeSource.archive.fileName
    Copy-Item -LiteralPath $archivePath -Destination $badArchive
    $archiveBytes = [IO.File]::ReadAllBytes($badArchive)
    $archiveBytes[0] = $archiveBytes[0] -bxor 1
    [IO.File]::WriteAllBytes($badArchive, $archiveBytes)
    Assert-PackagingFailure { Get-McpPythonArchive $badCache $runtimeSource } 'Tampered cached interpreter was accepted.'
    Assert-PackagingFailure { Copy-McpDistribution $fixture $payload (Join-Path $payload 'cache') } 'Runtime cache was permitted inside payload.'
    Assert-PackagingFailure { Copy-McpDistribution $fixture $payload (Join-Path $fixture 'cache') } 'Runtime cache was permitted to change the verified source.'
    Assert-PackagingFailure { Copy-McpDistribution $fixture $testRoot } 'Overlapping source/payload roots were accepted.'
    Assert-Packaging (-not (Test-Path -LiteralPath (Join-Path $staged 'companion/plugin'))) 'Companion contains duplicate plugin directory.'
    Assert-Packaging (@(Get-McpFiles $staged | Where-Object Extension -eq '.pdb').Count -eq 0) 'Staged checksum manifest contains files that packaging strips.'
    # A verified cache must permit a complete rerun with network access unavailable.
    function Invoke-WebRequest { throw 'Offline staging attempted a network download.' }
    $stagedAgain = Copy-McpDistribution $fixture $payload $PythonRuntimeCacheDirectory
    Assert-Packaging ($stagedAgain -eq $staged) 'Repeated staging changed destination.'
    Assert-McpChecksums $stagedAgain
    Test-McpPythonRuntime (Join-Path $stagedAgain 'companion')
    $checks++
    Assert-PackagingFailure { Remove-McpStage $payload $testRoot } 'Cleanup accepted a target outside its owned staging root.'
    Write-Host "Passed $checks MCP packaging checks. Production payload and FL installations were not changed."
} finally {
    Remove-McpStage $testArtifactRoot $testRoot
}
