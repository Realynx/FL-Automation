# Stage the offline FL MCP optional component. Requires PowerShell 5.1 or newer.
[CmdletBinding()]
param(
    [string]$DistributionPath,
    [string]$McpSourceRoot,
    [string]$SdkRoot,
    [string]$PythonWheel,
    [string]$PayloadRoot,
    [string]$HostDirectory,
    [switch]$ValidateOnly
)

$McpPackagingRoot = $PSScriptRoot
$McpSdkVersion = '0.2.0'
$McpLicenseHash = 'FFCCA38841ADB694B6F380647E15F17C446A4D1656FED51A1E2041D064C94CC8'

function Get-McpFullPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A nonempty filesystem path is required.' }
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}

function Assert-McpNoLinks {
    param([string]$Path)
    $current = Get-McpFullPath $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse points are not allowed in MCP packaging paths: $current"
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Get-McpFiles {
    param([string]$Root)
    Assert-McpNoLinks $Root
    $pending = New-Object 'Collections.Generic.Queue[string]'
    $pending.Enqueue((Get-McpFullPath $Root))
    while ($pending.Count -gt 0) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in MCP distribution: $($entry.FullName)" }
            if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
            else { $entry }
        }
    }
}

function Get-McpRelativePath {
    param([string]$Root, [string]$Path)
    $prefix = (Get-McpFullPath $Root) + [IO.Path]::DirectorySeparatorChar
    $full = Get-McpFullPath $Path
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes MCP package root: $Path" }
    return $full.Substring($prefix.Length).Replace('\', '/')
}

function Resolve-McpEntry {
    param([string]$Root, [string]$Relative)
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':')) { throw 'Invalid MCP manifest path.' }
    if (@($Relative -split '[\\/]' | Where-Object { $_ -eq '..' -or $_ -eq '.' -or $_ -eq '' }).Count -gt 0) { throw 'Invalid MCP manifest path component.' }
    $full = Get-McpFullPath (Join-Path $Root $Relative)
    $null = Get-McpRelativePath $Root $full
    return $full
}

function Assert-McpChecksums {
    param([string]$Root)
    $manifestPath = Join-Path $Root 'SHA256SUMS.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'FL MCP distribution is missing SHA256SUMS.json.' }
    $files = @(Get-McpFiles $Root)
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest) {
        $full = Resolve-McpEntry $Root $entry.File
        if (-not $seen.Add($full)) { throw "Duplicate MCP checksum entry: $($entry.File)" }
        if ($entry.Hash -notmatch '^[0-9A-Fa-f]{64}$') { throw "Invalid checksum for $($entry.File)" }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing MCP distribution file: $($entry.File)" }
        if ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -ne $entry.Hash) { throw "MCP checksum mismatch: $($entry.File)" }
    }
    foreach ($file in $files) {
        if ($file.FullName -ne $manifestPath -and -not $seen.Contains($file.FullName)) { throw "Unlisted MCP distribution file: $($file.Name)" }
    }
    if ($seen.Count -ne $files.Count - 1) { throw 'MCP manifest does not cover the exact distribution.' }
}

function Assert-McpRequiredFiles {
    param([string]$Root)
    $required = @('LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'docs/live-verification.md', 'examples/mcp-settings.json',
        'plugin/fl-mcp/FlMcp.Plugin.dll', 'plugin/fl-mcp/FlMcp.Plugin.deps.json', 'plugin/fl-mcp/FlMcp.Protocol.dll',
        'plugin/fl-mcp/FruityLink.Scripting.dll', 'server/FlMcp.Server.dll', 'server/FlMcp.Server.exe',
        'server/FlMcp.Server.deps.json', 'server/FlMcp.Server.runtimeconfig.json',
        'python/fruitylink_python-0.2.0-py3-none-any.whl', 'python/README.md',
        'licenses/fruitylink-python-LICENSE.txt', 'licenses/fruitylink-sdk-LICENSE.txt',
        'licenses/ModelContextProtocol-Apache-2.0.txt', 'licenses/ModelContextProtocol-NOTICES.txt',
        'licenses/Microsoft-DotNet-MIT.txt', 'licenses/Microsoft-DotNet-NOTICES.txt')
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Root $relative) -PathType Leaf)) { throw "Required MCP file missing: $relative" }
    }
    if ((Get-FileHash -LiteralPath (Join-Path $Root 'LICENSE') -Algorithm SHA256).Hash -ne $McpLicenseHash) { throw 'FL MCP must retain the verbatim PolyForm Noncommercial 1.0.0 license.' }
    foreach ($name in @('FruityLink.Core.dll', 'FruityLink.Plugins.Abstractions.dll')) {
        if (Test-Path -LiteralPath (Join-Path $Root "plugin/fl-mcp/$name")) { throw "MCP plugin must use the host shared contract, not bundle $name." }
    }
}

function Assert-McpAssemblyVersion {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required shared assembly missing: $Path" }
    $actual = [Reflection.AssemblyName]::GetAssemblyName($Path).Version.ToString()
    if ($actual -ne "$McpSdkVersion.0") { throw "MCP requires SDK $McpSdkVersion; assembly $Path has version $actual." }
}

function Assert-McpDependencyClosure {
    param([string]$Directory, [string]$DepsFile, [string]$HostDirectory)
    $deps = Get-Content -LiteralPath (Join-Path $Directory $DepsFile) -Raw | ConvertFrom-Json
    foreach ($target in $deps.targets.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            foreach ($asset in $library.Value.runtime.PSObject.Properties) {
                $fileName = [IO.Path]::GetFileName($asset.Name)
                $path = Join-Path $Directory $fileName
                if ($fileName -in @('FruityLink.Core.dll', 'FruityLink.Plugins.Abstractions.dll')) { $path = Join-Path $HostDirectory $fileName }
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Incomplete MCP runtime closure: $fileName" }
            }
            foreach ($asset in $library.Value.runtimeTargets.PSObject.Properties) {
                if (-not (Test-Path -LiteralPath (Resolve-McpEntry $Directory $asset.Name) -PathType Leaf)) { throw "Missing MCP runtime asset: $($asset.Name)" }
            }
        }
    }
}

function Assert-McpWheel {
    param([string]$Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($name in @('fruitylink/worker.py', 'fruitylink/studio.py', 'fruitylink/operations.py', 'fruitylink/py.typed', 'fruitylink_python-0.2.0.dist-info/licenses/LICENSE')) {
            if ($null -eq $archive.GetEntry($name)) { throw "Incomplete Python wheel: $name" }
        }
        $entry = $archive.GetEntry('fruitylink_python-0.2.0.dist-info/METADATA')
        if ($null -eq $entry) { throw 'Python wheel metadata missing.' }
        $reader = New-Object IO.StreamReader ($entry.Open())
        try { $metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($metadata -notmatch '(?m)^Name: fruitylink-python\r?$' -or $metadata -notmatch '(?m)^Version: 0\.2\.0\r?$') { throw 'MCP Python wheel must match SDK 0.2.0.' }
    } finally { $archive.Dispose() }
}

function Assert-McpDistribution {
    param([string]$Root, [string]$HostDirectory)
    $Root = Get-McpFullPath $Root
    Assert-McpChecksums $Root
    Assert-McpRequiredFiles $Root
    Assert-McpNoLinks $HostDirectory
    foreach ($name in @('FruityLink.Core.dll', 'FruityLink.Plugins.Abstractions.dll')) { Assert-McpAssemblyVersion (Join-Path $HostDirectory $name) }
    Assert-McpAssemblyVersion (Join-Path $Root 'plugin/fl-mcp/FruityLink.Scripting.dll')
    $deps = Get-Content -LiteralPath (Join-Path $Root 'plugin/fl-mcp/FlMcp.Plugin.deps.json') -Raw | ConvertFrom-Json
    foreach ($name in @('FruityLink.Core', 'FruityLink.Plugins.Abstractions', 'FruityLink.Scripting')) {
        if ($null -eq $deps.libraries.PSObject.Properties["$name/$McpSdkVersion"]) { throw "MCP dependency $name does not match SDK $McpSdkVersion." }
    }
    Assert-McpDependencyClosure (Join-Path $Root 'plugin/fl-mcp') 'FlMcp.Plugin.deps.json' $HostDirectory
    Assert-McpDependencyClosure (Join-Path $Root 'server') 'FlMcp.Server.deps.json' $HostDirectory
    Assert-McpWheel (Join-Path $Root 'python/fruitylink_python-0.2.0-py3-none-any.whl')
}

function Write-McpChecksums {
    param([string]$Root)
    $entries = @(Get-McpFiles $Root | Sort-Object FullName | ForEach-Object {
        $relative = Get-McpRelativePath $Root $_.FullName
        if ($relative -ne 'SHA256SUMS.json') {
            [pscustomobject]@{ File = $relative; Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        }
    })
    ConvertTo-Json -InputObject $entries | Set-Content -LiteralPath (Join-Path $Root 'SHA256SUMS.json') -Encoding UTF8
}

function Remove-McpStage {
    param([string]$Root, [string]$Target)
    $null = Get-McpRelativePath $Root $Target
    Assert-McpNoLinks $Target
    if (Test-Path -LiteralPath $Target) {
        $null = @(Get-McpFiles $Target)
        Remove-Item -LiteralPath (Get-McpFullPath $Target) -Recurse -Force
    }
}

function Copy-McpDistribution {
    param([string]$Distribution, [string]$PayloadRoot)
    $sourceRoot = Get-McpFullPath $Distribution
    $payload = Get-McpFullPath $PayloadRoot
    if ($payload -eq $sourceRoot -or $payload.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The MCP payload destination must not be inside its source distribution.'
    }
    $sourceHashes = @{}
    foreach ($entry in (Get-Content -LiteralPath (Join-Path $sourceRoot 'SHA256SUMS.json') -Raw | ConvertFrom-Json)) {
        $sourceHashes[$entry.File.Replace('\', '/')] = $entry.Hash
    }
    $optional = Join-Path $PayloadRoot 'optional-plugins'
    $target = Join-Path $optional 'fl-mcp'
    $staging = Join-Path $optional ('.fl-mcp-' + [Guid]::NewGuid().ToString('N'))
    Assert-McpNoLinks $staging
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        foreach ($file in Get-McpFiles $Distribution) {
            $relative = Get-McpRelativePath $Distribution $file.FullName
            if ($relative -ne 'SHA256SUMS.json' -and -not $sourceHashes.ContainsKey($relative)) { throw "MCP source gained an unlisted file during staging: $relative" }
            $expectedHash = $sourceHashes[$relative]
            if ($file.Extension -eq '.pdb') { continue }
            if ($relative.StartsWith('plugin/fl-mcp/', [StringComparison]::Ordinal)) { $relative = 'plugin/' + $relative.Substring(14) }
            elseif ($relative.StartsWith('plugin/', [StringComparison]::Ordinal)) { throw "Unexpected plugin directory in MCP distribution: $relative" }
            elseif ($relative -eq 'SHA256SUMS.json') { $relative = 'companion/SOURCE-SHA256SUMS.json' }
            else { $relative = 'companion/' + $relative }
            $destination = Resolve-McpEntry $staging $relative
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination
            if ($expectedHash -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expectedHash) { throw "MCP source changed during staging: $($file.Name)" }
        }
        $registration = Join-Path $McpPackagingRoot 'register-flmcp-codex.ps1'
        if (-not (Test-Path -LiteralPath $registration -PathType Leaf)) { throw 'Installer MCP registration helper is missing.' }
        Copy-Item -LiteralPath $registration -Destination (Join-Path $staging 'companion/register-codex.ps1')
        Write-McpChecksums (Join-Path $staging 'companion')
        Write-McpChecksums $staging
        Assert-McpChecksums $staging
        Remove-McpStage $optional $target
        Move-Item -LiteralPath $staging -Destination $target
    } finally { Remove-McpStage $optional $staging }
    return $target
}

function Build-McpDistribution {
    param([string]$SourceRoot, [string]$SdkRoot, [string]$Wheel)
    if ((Get-Content -LiteralPath (Join-Path $SdkRoot 'VERSION') -Raw).Trim() -ne $McpSdkVersion) { throw 'MCP bundle requires the matching SDK 0.2.0 source.' }
    $pack = Join-Path $SourceRoot 'scripts/package.ps1'
    if (-not (Test-Path -LiteralPath $pack -PathType Leaf)) { throw "MCP source checkout missing: $SourceRoot. Supply -McpDistributionPath to installer/package.ps1 for a prebuilt bundle." }
    if (-not (Test-Path -LiteralPath $Wheel -PathType Leaf)) {
        & uv run --directory (Join-Path $SdkRoot 'python') --locked python -m build
        if ($LASTEXITCODE -ne 0) { throw 'SDK Python wheel build failed.' }
    }
    $output = Join-Path $McpPackagingRoot ('artifacts/mcp-distribution-' + [Guid]::NewGuid().ToString('N'))
    & pwsh -NoProfile -File $pack -FruityLinkSdkRoot $SdkRoot -PythonWheel $Wheel -OutputDirectory $output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'FL MCP source distribution build failed.' }
    return $output
}

function Invoke-McpStaging {
    param([string]$DistributionPath, [string]$McpSourceRoot, [string]$SdkRoot, [string]$PythonWheel, [string]$PayloadRoot, [string]$HostDirectory, [switch]$ValidateOnly)
    $repoRoot = Split-Path $McpPackagingRoot -Parent
    if (-not $SdkRoot) { $SdkRoot = Join-Path $repoRoot 'sdk' }
    if (-not $McpSourceRoot) { $McpSourceRoot = Join-Path (Split-Path $repoRoot -Parent) 'Fl-MCP' }
    if (-not $PayloadRoot) { $PayloadRoot = Join-Path $McpPackagingRoot 'FruityLink.Installer/payload' }
    if (-not $HostDirectory) { $HostDirectory = Join-Path $PayloadRoot 'FruityLink' }
    if (-not $PythonWheel) { $PythonWheel = Join-Path $SdkRoot "python/dist/fruitylink_python-$McpSdkVersion-py3-none-any.whl" }
    if (-not $DistributionPath) {
        if ($ValidateOnly) { throw 'ValidateOnly requires an explicit existing DistributionPath; it never builds or stages files.' }
        $DistributionPath = Build-McpDistribution $McpSourceRoot $SdkRoot $PythonWheel
    }
    $DistributionPath = Get-McpFullPath $DistributionPath
    Assert-McpDistribution $DistributionPath (Get-McpFullPath $HostDirectory)
    if ($ValidateOnly) { Write-Host "Validated FL MCP distribution and SDK $McpSdkVersion host: $DistributionPath"; return }
    $target = Copy-McpDistribution $DistributionPath (Get-McpFullPath $PayloadRoot)
    Write-Host "Staged offline FL MCP component: $target"
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    Invoke-McpStaging @PSBoundParameters
}
