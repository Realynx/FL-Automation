# Developer packaging: the editor is optional; the existing interpreter is framework-owned.
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$PayloadRoot = (Join-Path $PSScriptRoot 'FruityLink.Installer/payload')
)
$ErrorActionPreference = 'Stop'
$idePayloadRoot = $PayloadRoot
. (Join-Path $PSScriptRoot 'stage-mcp.ps1')

$payload = Get-McpFullPath $idePayloadRoot
$sdk = Join-Path (Split-Path $PSScriptRoot -Parent) 'sdk'
$project = Join-Path $sdk 'src/FruityLink.Plugins.PythonIde/FruityLink.Plugins.PythonIde.csproj'
$plugin = Join-Path $payload 'optional-plugins/fl-python-ide'
$commonPython = Join-Path $payload 'FruityLink/python'
$companion = Join-Path $payload 'optional-plugins/fl-mcp/companion'

# The MCP staging step has already verified the official Python archive and SDK wheel.
# Keep those exact MIT/PSF bytes in the base framework too, so IDE-only installations
# have Python even when the user deselects MCP. Existing MCP configs retain their paths.
Assert-McpChecksums $companion
Assert-McpPythonRuntime $companion
Remove-McpStage $payload $commonPython
Copy-Item -LiteralPath (Join-Path $companion 'python') -Destination $commonPython -Recurse
Copy-Item -LiteralPath (Join-Path $sdk 'LICENSE') -Destination (Join-Path $commonPython 'FRUITYLINK-LICENSE.txt')
@'
# FruityLink embedded Python

This is the framework's private CPython runtime and MIT-licensed fruitylink Python library.
Plugins such as FL Python IDE execute through FruityLink.Scripting inside FL Studio.
Users do not need to install Python or run pip. This directory is installed even when
FLMCP is deselected. The optional MCP companion retains its legacy paths for compatibility;
both plugins share the same process-owned interpreter, never a second interpreter.

See runtime/LICENSE.txt, FRUITYLINK-LICENSE.txt and RUNTIME-PROVENANCE.json.
'@ | Set-Content -LiteralPath (Join-Path $commonPython 'README.md') -Encoding UTF8

Remove-McpStage $payload $plugin
& dotnet publish $project -c $Configuration --nologo -o $plugin -warnaserror
if ($LASTEXITCODE -ne 0) { throw "Python IDE publish failed (exit $LASTEXITCODE)." }
foreach ($name in @('README.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path (Split-Path $project -Parent) $name) -Destination $plugin
}
Copy-Item -LiteralPath (Join-Path $sdk 'LICENSE') -Destination (Join-Path $plugin 'LICENSE')
$pluginLicenses = Join-Path $plugin 'licenses'
if (-not (Test-Path -LiteralPath $pluginLicenses -PathType Container)) {
    Copy-Item -LiteralPath (Join-Path (Split-Path $project -Parent) 'licenses') -Destination $pluginLicenses -Recurse
}
$frameworkNotices = Join-Path $payload 'FruityLink/licenses/python-ide'
Remove-McpStage $payload $frameworkNotices
New-Item -ItemType Directory -Path $frameworkNotices -Force | Out-Null
Copy-Item -LiteralPath $pluginLicenses -Destination (Join-Path $frameworkNotices 'licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $plugin 'THIRD-PARTY-NOTICES.md') -Destination $frameworkNotices
Get-ChildItem -LiteralPath $plugin -Recurse -File -Filter *.pdb | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
foreach ($name in @('FruityLink.Plugins.PythonIde.dll', 'AvaloniaEdit.dll', 'FruityLink.Ui.Avalonia.Hosting.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $plugin $name) -PathType Leaf)) { throw "Missing Python IDE dependency: $name" }
}
Write-McpChecksums $plugin
Write-McpChecksums $commonPython
Assert-McpChecksums $plugin
Assert-McpChecksums $commonPython
Write-Host 'Staged FL Python IDE and the framework Python runtime.'
