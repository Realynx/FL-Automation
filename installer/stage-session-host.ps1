[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$SdkRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk'),
    [Parameter(Mandatory)][string]$PayloadRoot
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $SdkRoot 'src/FruityLink.SessionHost/FruityLink.SessionHost.csproj'
$target = Join-Path $PayloadRoot 'FruityLink/tools/session-host'
& dotnet publish $project -c $Configuration --no-self-contained -o $target
if ($LASTEXITCODE -ne 0) { throw 'SDK session host publish failed.' }
foreach ($file in @('FruityLink.SessionHost.exe', 'FruityLink.SessionHost.dll', 'FruityLink.Core.dll', 'FruityLink.SessionHost.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $target $file))) { throw "SDK session host is incomplete: $file" }
}
Write-Host 'Staged the framework session host for standalone Python builds.'
