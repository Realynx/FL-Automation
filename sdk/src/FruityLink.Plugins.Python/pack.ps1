[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$sdkRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$version = (Get-Content -LiteralPath (Join-Path $sdkRoot 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw 'SDK VERSION must contain a valid package version.'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $sdkRoot 'artifacts/python-plugin' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $output -Force

if (-not $SkipBuild) {
    & dotnet build (Join-Path $PSScriptRoot 'FruityLink.Plugins.Python.csproj') -c Release --nologo -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Plugin build failed with exit code $LASTEXITCODE." }
}

$build = Join-Path $PSScriptRoot 'bin/Release/net9.0-windows'
$expectedVersion = $version -replace '[-+].*$', ''
foreach ($assembly in @('FruityLink.Plugins.Python.dll', 'FruityLink.Scripting.dll')) {
    $actualVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $build $assembly)).Version.ToString(3)
    if ($actualVersion -ne $expectedVersion) {
        throw "$assembly is version $actualVersion, but SDK VERSION is $version. Rebuild before packaging."
    }
}
$staging = Join-Path $output ("package-" + [Guid]::NewGuid().ToString('N'))
$plugin = Join-Path $staging 'fl-python'
$null = New-Item -ItemType Directory -Path $plugin
$files = @(
    'FruityLink.Plugins.Python.dll',
    'FruityLink.Plugins.Python.deps.json',
    'FruityLink.Plugins.Python.xml',
    'FruityLink.Scripting.dll',
    'FruityLink.Scripting.xml'
)
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $build $file) -Destination (Join-Path $plugin $file)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $plugin 'README.md')
Copy-Item -LiteralPath (Join-Path $sdkRoot 'LICENSE') -Destination (Join-Path $plugin 'LICENSE')

$checksums = Get-ChildItem -LiteralPath $plugin -File | Sort-Object Name | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
}
[IO.File]::WriteAllLines((Join-Path $plugin 'SHA256SUMS'), [string[]] $checksums)
$archive = Join-Path $output "FruityLink.Plugins.Python-$version.zip"
Compress-Archive -LiteralPath $plugin -DestinationPath $archive -Force
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256", "$archiveHash  $([IO.Path]::GetFileName($archive))`n")

Write-Output "Package: $archive"
Write-Output "Staging: $staging"
Write-Output "SHA256: $archiveHash"
