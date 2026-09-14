$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'shared-ui-payload.ps1')

$artifactRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'))
$fixtureRoot = Join-Path $artifactRoot ('shared-ui-tests-' + [Guid]::NewGuid().ToString('N'))
$hostFixture = Join-Path $fixtureRoot 'host'
$pluginFixture = Join-Path $fixtureRoot 'plugin'
$checks = 0

function Assert-Rejected {
    param([string]$Expected)
    $failure = $null
    try { Assert-SharedUiPayload $hostFixture $pluginFixture } catch { $failure = $_.Exception.Message }
    if (-not $failure -or -not $failure.Contains($Expected)) { throw "Expected packaging rejection containing: $Expected" }
    $script:checks++
}

try {
    New-Item -ItemType Directory -Force $hostFixture, $pluginFixture | Out-Null
    $names = @('FruityLink.Core.dll', 'FruityLink.Plugins.Abstractions.dll', 'FruityLink.Ui.Avalonia.Hosting.dll')
    foreach ($name in $names) {
        [IO.File]::WriteAllText((Join-Path $hostFixture $name), "matching $name")
        Copy-Item -LiteralPath (Join-Path $hostFixture $name) -Destination $pluginFixture
    }
    Assert-SharedUiPayload $hostFixture $pluginFixture
    $checks++
    foreach ($name in $names) {
        [IO.File]::AppendAllText((Join-Path $pluginFixture $name), 'stale build with the same assembly version')
        Assert-Rejected "Shared UI contract mismatch for $name"
        Copy-Item -LiteralPath (Join-Path $hostFixture $name) -Destination $pluginFixture -Force
    }
    Remove-Item -LiteralPath (Join-Path $pluginFixture $names[2])
    Assert-Rejected 'Shared UI payload is incomplete'
    Copy-Item -LiteralPath (Join-Path $hostFixture $names[2]) -Destination $pluginFixture
    Remove-Item -LiteralPath (Join-Path $hostFixture $names[1])
    Assert-Rejected 'Shared UI payload is incomplete'
    Write-Host "Shared UI payload checks passed ($checks)."
}
finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture cleanup escapes installer artifacts.'
    }
    if (Test-Path -LiteralPath $resolvedFixture) { Remove-Item -LiteralPath $resolvedFixture -Recurse -Force }
}
