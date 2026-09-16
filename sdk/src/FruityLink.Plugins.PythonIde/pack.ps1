[CmdletBinding()]
param([string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$sdkRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $sdkRoot 'artifacts/python-ide' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $outputRoot "fl-python-ide-$stamp"
$plugin = Join-Path $stage 'fl-python-ide'
New-Item -ItemType Directory -Path $plugin -Force | Out-Null
& dotnet publish (Join-Path $PSScriptRoot 'FruityLink.Plugins.PythonIde.csproj') -c Release -r win-x64 --self-contained false -o $plugin -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Python IDE publish failed with exit code $LASTEXITCODE." }
$required = @('FruityLink.Plugins.PythonIde.dll', 'FruityLink.Plugins.PythonIde.deps.json', 'AvaloniaEdit.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/AvaloniaEdit-11.3.0-LICENSE.txt')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $plugin $relative) -PathType Leaf)) { throw "Incomplete plugin output: $relative" }
}
$files = Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName
$manifest = foreach ($file in $files) {
    [pscustomobject]@{ path = $file.FullName.Substring($stage.Length + 1).Replace('\', '/'); bytes = $file.Length; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8
$archive = "$stage.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive
[pscustomobject]@{
    Archive = $archive
    Bytes = (Get-Item -LiteralPath $archive).Length
    Sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
} | Format-List
