#Requires -Version 5.1
<#
.SYNOPSIS
Installs the bundled Python SDK in a per-user environment and registers FLMCP with Codex.
.DESCRIPTION
Run after installing FLMCP. The installer ships this file as
FruityLink/tools/fl-mcp/register-codex.ps1. Requires Python 3.11+, .NET 10, and Codex on PATH.
Use -WhatIf to inspect the planned paths without creating files or changing Codex settings.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$FlPath,
    [string]$Python = 'python',
    [string]$Name = 'flmcp',
    [string]$Workspace
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-SetupFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file is missing: $Path. Install the FLMCP component before running this script."
    }
}

function Invoke-SetupCommand {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE." }
}

if ([string]::IsNullOrWhiteSpace($FlPath)) {
    $FlPath = Join-Path $PSScriptRoot '../../..'
}
$FlPath = [IO.Path]::GetFullPath($FlPath)
$companion = Join-Path $FlPath 'FruityLink/tools/fl-mcp'
$flExe = Join-Path $FlPath 'FL64.exe'
$template = Join-Path $FlPath 'Data/Templates/Empty/Empty.flp'
$server = Join-Path $companion 'server/FlMcp.Server.exe'
$wheel = Join-Path $companion 'python/fruitylink_python-0.2.0-py3-none-any.whl'
foreach ($path in @($flExe, $template, $server, $wheel)) { Assert-SetupFile $path }

$localData = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrWhiteSpace($localData)) { throw 'LocalApplicationData is unavailable for this user.' }
$venv = Join-Path $localData 'FlMcp/python/0.2.0'
$venvPython = Join-Path $venv 'Scripts/python.exe'
if ([string]::IsNullOrWhiteSpace($Workspace)) { $Workspace = Join-Path $localData 'FlMcp/Projects' }
$Workspace = [IO.Path]::GetFullPath($Workspace)

Write-Host "FL Studio: $flExe"
Write-Host "MCP server: $server"
Write-Host "Python environment: $venv"
Write-Host "Project workspace: $Workspace"
if (-not $PSCmdlet.ShouldProcess("Codex MCP '$Name' and $venv", 'Install bundled Python SDK and register local server')) {
    return
}

# Preflight dependencies before creating the environment or modifying Codex configuration.
$codexCommand = (Get-Command codex -ErrorAction Stop).Source
$dotnetCommand = (Get-Command dotnet -ErrorAction Stop).Source
$runtimes = & $dotnetCommand --list-runtimes
if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft\.NETCore\.App 10\.')) {
    throw 'FLMCP requires the .NET 10 runtime. Install it, then run this script again.'
}
if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    $pythonCommand = (Get-Command $Python -ErrorAction Stop).Source
    Invoke-SetupCommand $pythonCommand @('-c', 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)')
    Invoke-SetupCommand $pythonCommand @('-m', 'venv', $venv)
}
Invoke-SetupCommand $venvPython @('-c', 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)')
Invoke-SetupCommand $venvPython @('-m', 'pip', '--disable-pip-version-check', 'install', '--no-index', '--no-deps', '--force-reinstall', $wheel)
Invoke-SetupCommand $venvPython @('-c', 'import fruitylink; import importlib.metadata; print("fruitylink-python " + importlib.metadata.version("fruitylink-python"))')

$codexArguments = @(
    'mcp', 'add', $Name,
    '--env', "FL_MCP_FL_EXE=$flExe",
    '--env', "FL_MCP_TEMPLATE=$template",
    '--env', "FL_MCP_PYTHON=$venvPython",
    '--env', "FL_MCP_WORKSPACE=$Workspace",
    '--', $server
)
Invoke-SetupCommand $codexCommand $codexArguments
Write-Host 'FLMCP is registered. Enable FL MCP once in FL Studio under Tools > FL Plugins, then close FL Studio.'
Write-Host 'Restart Codex to load the server. The MCP will launch its own FL Studio session when requested.'
