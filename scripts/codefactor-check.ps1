[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sdkRoot = Split-Path -Parent $PSScriptRoot

Push-Location $sdkRoot
try {
    # Rebuild prevents an incremental build from concealing existing compiler warnings.
    dotnet build FruityLink.Sdk.slnx --configuration Release --no-incremental -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'SDK build or code-quality analysis failed.' }
    dotnet test FruityLink.Sdk.slnx --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'SDK regression tests failed.' }
}
finally { Pop-Location }
