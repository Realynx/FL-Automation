# Stage the offline FL MCP optional component. Requires PowerShell 5.1 or newer.
[CmdletBinding()]
param(
    [string]$DistributionPath,
    [string]$McpSourceRoot,
    [string]$SdkRoot,
    [string]$PythonWheel,
    [string]$PayloadRoot,
    [string]$HostDirectory,
    [string]$PythonRuntimeCacheDirectory,
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

function Assert-McpSharedHost {
    param([string]$HostDirectory, [string]$PluginDirectory)
    Assert-McpNoLinks $HostDirectory
    foreach ($name in @('FruityLink.Core.dll', 'FruityLink.Plugins.Abstractions.dll', 'FruityLink.Scripting.dll')) {
        Assert-McpAssemblyVersion (Join-Path $HostDirectory $name)
    }
    $pluginScripting = Join-Path $PluginDirectory 'FruityLink.Scripting.dll'
    Assert-McpAssemblyVersion $pluginScripting
    if ((Get-FileHash -LiteralPath (Join-Path $HostDirectory 'FruityLink.Scripting.dll') -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $pluginScripting -Algorithm SHA256).Hash) {
        throw 'The shared host and MCP plugin must contain the exact same FruityLink.Scripting assembly.'
    }
}

function Assert-McpDependencyClosure {
    param([string]$Directory, [string]$DepsFile, [string]$HostDirectory)
    $deps = Get-Content -LiteralPath (Join-Path $Directory $DepsFile) -Raw | ConvertFrom-Json
    foreach ($target in $deps.targets.PSObject.Properties) {
        foreach ($library in $target.Value.PSObject.Properties) {
            foreach ($asset in $library.Value.runtime.PSObject.Properties) {
                $fileName = [IO.Path]::GetFileName($asset.Name)
                $path = Join-Path $Directory $fileName
                if ($fileName -in @('FruityLink.Core.dll', 'FruityLink.Plugins.Abstractions.dll', 'FruityLink.Scripting.dll')) { $path = Join-Path $HostDirectory $fileName }
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
        foreach ($name in @('fruitylink/embedding.py', 'fruitylink/studio.py', 'fruitylink/operations.py', 'fruitylink/py.typed', 'fruitylink_python-0.2.0.dist-info/licenses/LICENSE')) {
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
    Assert-McpSharedHost $HostDirectory (Join-Path $Root 'plugin/fl-mcp')
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

function Get-McpPythonRuntimeSource {
    $source = Get-Content -LiteralPath (Join-Path $McpPackagingRoot 'python-runtime.json') -Raw | ConvertFrom-Json
    if ($source.version -notmatch '^\d+\.\d+\.\d+$' -or $source.architecture -ne 'windows-x64') { throw 'Invalid pinned Python runtime version or architecture.' }
    if ($source.archive.fileName -ne "python-$($source.version)-embed-amd64.zip") { throw 'Invalid pinned Python archive filename.' }
    if ($source.archive.url -ne "https://www.python.org/ftp/python/$($source.version)/$($source.archive.fileName)") { throw 'Python runtime must come from its pinned official HTTPS URL.' }
    if ($source.archive.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $source.archive.sizeBytes -le 0) { throw 'Python runtime requires a pinned SHA256 and byte length.' }
    return $source
}

function Assert-McpPythonArchive {
    param([string]$Path, [object]$Source)
    Assert-McpNoLinks $Path
    if ((Get-Item -LiteralPath $Path).Length -ne $Source.archive.sizeBytes -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Source.archive.sha256) {
        throw "Python runtime archive does not match the pinned official SHA256/size: $Path"
    }
}

function Get-McpPythonArchive {
    param([string]$CacheDirectory, [object]$Source)
    Assert-McpNoLinks $CacheDirectory
    New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
    $archive = Resolve-McpEntry $CacheDirectory $Source.archive.fileName
    if (Test-Path -LiteralPath $archive) {
        Assert-McpPythonArchive $archive $Source
        return $archive
    }
    $temporary = Resolve-McpEntry $CacheDirectory ($Source.archive.fileName + '.' + [Guid]::NewGuid().ToString('N') + '.partial')
    $protocol = [Net.ServicePointManager]::SecurityProtocol
    try {
        [Net.ServicePointManager]::SecurityProtocol = $protocol -bor [Net.SecurityProtocolType]::Tls12
        Write-Host "Downloading pinned Python $($Source.version) x64 runtime from python.org."
        Invoke-WebRequest -Uri $Source.archive.url -OutFile $temporary -UseBasicParsing -MaximumRedirection 0
        Assert-McpPythonArchive $temporary $Source
        Move-Item -LiteralPath $temporary -Destination $archive
    } finally {
        [Net.ServicePointManager]::SecurityProtocol = $protocol
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
    return $archive
}

function Expand-McpPythonArchive {
    param([string]$ArchivePath, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $path = Resolve-McpEntry $Destination $entry.FullName
            if (-not $seen.Add($path)) { throw "Duplicate Python archive entry: $($entry.FullName)" }
        }
    } finally { $archive.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $Destination)
}

function Copy-McpPythonRuntime {
    param([string]$Companion, [string]$CacheDirectory)
    $source = Get-McpPythonRuntimeSource
    $archive = Get-McpPythonArchive $CacheDirectory $source
    $python = Join-Path $Companion 'python'
    $runtime = Join-Path $python 'runtime'
    if (Test-Path -LiteralPath $runtime) { throw 'Source MCP distribution already contains a runtime; runtime provenance must be managed by the installer.' }
    Expand-McpPythonArchive $archive $runtime
    $wheelName = "fruitylink_python-$McpSdkVersion-py3-none-any.whl"
    $paths = @($source.standardLibrary, '.', "../$wheelName")
    [IO.File]::WriteAllLines((Resolve-McpEntry $runtime $source.pathConfiguration), [string[]]$paths, [Text.Encoding]::ASCII)
    $provenance = [ordered]@{
        source = $source
        sdkVersion = $McpSdkVersion
        sdkWheel = $wheelName
        sdkWheelSha256 = (Get-FileHash -LiteralPath (Join-Path $python $wheelName) -Algorithm SHA256).Hash.ToLowerInvariant()
        runtimeDirectory = 'runtime'
        runtimeLibrary = "runtime/$($source.library)"
        preflightExecutable = 'runtime/python.exe'
        executionMode = 'embedded-in-fl'
        importPaths = $paths
        modifications = @('Replaced python314._pth with the three explicit relative paths; site import remains disabled.')
    }
    $provenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $python 'RUNTIME-PROVENANCE.json') -Encoding UTF8
    Write-McpRuntimeDocumentation $Companion $source $wheelName
    Assert-McpPythonRuntime $Companion
}

function Write-McpRuntimeDocumentation {
    param([string]$Companion, [object]$Source, [string]$WheelName)
    $text = @"
# Installer-supplied Python runtime

This installer includes private CPython $($Source.version) for Windows x64. No user Python,
pip, virtual environment, PATH changes, or package download is required at installation.
The installer sets FL_MCP_PYTHON_RUNTIME to this companion's python/runtime directory and
FL_MCP_PYTHON_PATH to the bundled SDK wheel. FLMCP loads this CPython runtime inside the
connected FL Studio process; user scripts execute there. Python.exe is used only for the
installer's SDK import check, not to execute user scripts.

Official source: $($Source.archive.url)
Official release/checksum: $($Source.releaseUrl)
Archive SHA256: $($Source.archive.sha256)
Original byte length: $($Source.archive.sizeBytes)
Embedding documentation: $($Source.documentationUrl)

Every file from the official ZIP is retained, including runtime/LICENSE.txt with Python's
license and bundled third-party notices. Only runtime/$($Source.pathConfiguration) is changed:

    $($Source.standardLibrary)
    .
    ../$WheelName

These paths are relative to the runtime directory. The standard-library ZIP and native extension
modules stay beside python.exe; the pure-Python SDK wheel is imported directly from its ZIP.
Site initialization remains disabled. PYTHONPATH, user site-packages, and registry Python
configuration do not supply this runtime's imports. Scripts run with FL Studio's OS permissions,
inside its process; this is not a security sandbox. Do not replace these paths with a global
Python installation. Cancellation cannot safely force-stop arbitrary native Python extensions;
follow the embedded execution guidance in the MCP documentation.

RUNTIME-PROVENANCE.json records this pin and the exact SDK wheel hash. The companion's regenerated
SHA256SUMS.json covers installed files; SOURCE-SHA256SUMS.json preserves the original MCP
distribution manifest before the installer added Python. SOURCE-README.md describes the
standalone source deployment; this installer supplies its runtime without user setup.
"@
    [IO.File]::WriteAllText((Join-Path $Companion 'python/RUNTIME.md'), $text, [Text.UTF8Encoding]::new($false))
}

function Assert-McpPythonRuntime {
    param([string]$Companion)
    $source = Get-McpPythonRuntimeSource
    $runtime = Join-Path $Companion 'python/runtime'
    foreach ($name in @($source.executable, $source.library, $source.standardLibrary, $source.pathConfiguration,
        $source.license, '_ctypes.pyd', '_asyncio.pyd', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
        if (-not (Test-Path -LiteralPath (Resolve-McpEntry $runtime $name) -PathType Leaf)) { throw "Bundled Python runtime file missing: $name" }
    }
    $expected = @($source.standardLibrary, '.', "../fruitylink_python-$McpSdkVersion-py3-none-any.whl")
    $actual = @(Get-Content -LiteralPath (Join-Path $runtime $source.pathConfiguration))
    if (($actual -join "`n") -cne ($expected -join "`n")) { throw 'Bundled Python import paths differ from the isolated runtime contract.' }
    $provenance = Get-Content -LiteralPath (Join-Path $Companion 'python/RUNTIME-PROVENANCE.json') -Raw | ConvertFrom-Json
    if ($provenance.source.archive.sha256 -ne $source.archive.sha256 -or $provenance.sdkVersion -ne $McpSdkVersion) { throw 'Bundled Python provenance differs from the source pin.' }
    if ($provenance.executionMode -ne 'embedded-in-fl' -or $provenance.runtimeDirectory -ne 'runtime' -or
        $provenance.runtimeLibrary -ne "runtime/$($source.library)" -or $provenance.preflightExecutable -ne 'runtime/python.exe') {
        throw 'Bundled Python provenance does not describe the embedded FL runtime.'
    }
    $wheel = Resolve-McpEntry (Join-Path $Companion 'python') $provenance.sdkWheel
    if ((Get-FileHash -LiteralPath $wheel -Algorithm SHA256).Hash -ne $provenance.sdkWheelSha256) { throw 'Bundled Python SDK wheel differs from runtime provenance.' }
}

function Test-McpPythonRuntime {
    param([string]$Companion)
    Assert-McpPythonRuntime $Companion
    $source = Get-McpPythonRuntimeSource
    $code = @"
import asyncio, ctypes, importlib.metadata, json, pathlib, sqlite3, ssl, sys
import fruitylink.embedding
assert '.'.join(map(str, sys.version_info[:3])) == '$($source.version)'
assert ctypes.sizeof(ctypes.c_void_p) == 8
assert importlib.metadata.version('fruitylink-python') == '$McpSdkVersion'
assert sys.flags.isolated == 1 and sys.flags.ignore_environment == 1 and sys.flags.no_site == 1
assert 'site' not in sys.modules
assert len(sys.path) == 3, sys.path
assert 'fruitylink_python-$McpSdkVersion-py3-none-any.whl' in fruitylink.embedding.__file__
print(json.dumps({'python': sys.version.split()[0], 'sdk': importlib.metadata.version('fruitylink-python'), 'isolated': True}))
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($code))
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $Companion 'python/runtime/python.exe'
    $start.Arguments = '-c "import base64;exec(base64.b64decode(''' + $encoded + '''))"'
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['PYTHONPATH'] = Join-Path $Companion 'untrusted-external-python-path'
    $start.EnvironmentVariables['PYTHONHOME'] = Join-Path $Companion 'untrusted-external-python-home'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Bundled Python import smoke test timed out.' }
        if ($process.ExitCode -ne 0) { throw "Bundled Python import smoke test failed: $($errors.Result)" }
        Write-Host "Verified private Python imports: $($output.Result.Trim())"
    } finally { $process.Dispose() }
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
    param([string]$Distribution, [string]$PayloadRoot, [string]$PythonRuntimeCacheDirectory)
    $sourceRoot = Get-McpFullPath $Distribution
    $payload = Get-McpFullPath $PayloadRoot
    Assert-McpSeparateRoots $sourceRoot $payload
    $cache = Resolve-McpPythonCache $PythonRuntimeCacheDirectory $payload $sourceRoot
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
        Copy-McpPythonRuntime (Join-Path $staging 'companion') $cache
        Write-McpInstallerDocumentation (Join-Path $staging 'companion')
        Write-McpChecksums (Join-Path $staging 'companion')
        Write-McpChecksums $staging
        Assert-McpChecksums $staging
        Remove-McpStage $optional $target
        Move-Item -LiteralPath $staging -Destination $target
    } finally { Remove-McpStage $optional $staging }
    return $target
}

function Write-McpInstallerDocumentation {
    param([string]$Companion)
    $readme = Join-Path $Companion 'README.md'
    Copy-Item -LiteralPath $readme -Destination (Join-Path $Companion 'SOURCE-README.md')
    $intro = @'
# FLMCP installed by the FruityLink framework installer

Start with [INSTALLER-SETUP.md](INSTALLER-SETUP.md). The framework installer bundles a private
CPython runtime that executes inside FL Studio, plus the FruityLink SDK, and can connect the AI apps you select. You do not
need to install Python, create a virtual environment, or run a separate registration script.

- [Installer setup and client selection](INSTALLER-SETUP.md)
- [Bundled Python runtime and isolation](python/RUNTIME.md)
- [Exact runtime provenance and wheel hash](python/RUNTIME-PROVENANCE.json)
- [Live FL verification](docs/live-verification.md)
- [Original standalone MCP source README](SOURCE-README.md)

SOURCE-README.md is retained unchanged from the MCP distribution and describes standalone
source deployment. The framework installer supplies the private embedded runtime and enables
the plugin when you select clients. SOURCE-SHA256SUMS.json preserves that original
distribution's hashes/layout; SHA256SUMS.json verifies this installed companion's layout.
'@
    $setup = @'
# Configure FLMCP through the framework installer

The installer includes private CPython 3.14.6 x64 and fruitylink-python 0.2.0. FLMCP loads
the runtime into the connected FL Studio process, where user Python scripts execute with
the SDK available. No separate Python installation, pip/venv setup, or PATH change is needed.
Installation launches python/runtime/python.exe only to check SDK imports.

## Select clients during installation

1. Leave the FLMCP component checked. Its files are selected by default; client checkboxes
   start unchecked so only the AI apps you choose have their settings changed.
2. Check the clients to connect. Choices include Codex, Claude Desktop (standard and detected
   Microsoft Store installations), Claude Code, Cursor, VS Code's default profile, Gemini CLI,
   Windsurf, and OpenCode. Choose Other client / generic-json for a stdio JSON export.
3. Use the selected FL installation's saved Empty.flp template, or provide another absolute
   saved FLP path. The default workspace is the original user's local application data under
   FlMcp/Projects. The installer preserves this user context across elevation.
4. Install. Selected-client setup writes their FLMCP entries with backups of existing settings,
   preserves unrelated configuration, and enables fl-mcp in the user's persisted plugin state.
   If no clients are selected, the installer copies the component but does not enable it or
   change any client settings. Client tool approvals remain under your control.
5. Restart selected clients to load the server and restart FL after installing updated files.
   Choose an existing session with fl_instances and fl_attach, or close other FL instances
   before fl_project_start to create a new managed project.

## Connect an existing FL Studio session

Enable FLMCP in that running FL instance's Plugins menu. The installer's saved enabled-state
applies to subsequent launches; changing client settings does not enable a plugin in an
already running process. Use fl_instances to list sessions for the configured FL installation,
then fl_attach with the chosen process ID. Untitled projects are supported. If a project switch
is detected, reattach before continuing; project identity checks are best effort.

Use fl_detach to disconnect. Detaching or exiting the AI client leaves an attached FL process
open. In attached mode, saving creates a fresh workspace copy without replacing the current
project; fl_project_close and fl_project_render are unavailable. Use a managed project for that workflow.
Attaching itself does not require the starting template, but installer setup validates the
template because the same configuration also supports managed project creation.

The installed server is server/FlMcp.Server.exe. Its FL_MCP_PYTHON_RUNTIME setting points to
this companion's python/runtime directory, and FL_MCP_PYTHON_PATH points to
python/fruitylink_python-0.2.0-py3-none-any.whl. Keep the companion directory together.
The plugin and shared host must use matching FruityLink 0.2.0 contracts.

## Configure or retry later

Run the framework installer again to select clients, or use its own CLI:

    FruityLink.Installer.exe --list-mcp-clients
    FruityLink.Installer.exe --configure-mcp --fl-path "C:\Program Files\Image-Line\FL Studio 2026" --mcp-clients codex --dry-run

Remove --dry-run to apply the configuration. --mcp-template and --mcp-workspace override the
default saved project template and output workspace. --mcp-python-runtime can select another
compatible private CPython 3.14.6 x64 runtime directory. No client CLI or registration helper is
required. If setup reports malformed settings or missing files, resolve the reported error
and retry --configure-mcp; do not overwrite the user's configuration manually.

Uninstall removes matching connections only for selected clients and this FL installation.
Existing configuration backups are retained. Check the installer's per-client result before
closing it; copying the plugin files and connecting an AI app are separate reported steps.

FLMCP still requires a licensed FL Studio installation, a normal Windows desktop session,
and the companion's .NET 10 runtime. Python bundling does not prove live authoring or rendering.
Follow docs/live-verification.md using a disposable project. FLMCP retains its PolyForm
Noncommercial 1.0.0 license; SDK, Python, and dependency notices retain their own terms.
Python scripts run with FL Studio's permissions and share its process. This is not a security
sandbox; completed project edits are not automatically rolled back on errors or cancellation.
'@
    [IO.File]::WriteAllText($readme, $intro, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $Companion 'INSTALLER-SETUP.md'), $setup, [Text.UTF8Encoding]::new($false))
}

function Assert-McpSeparateRoots {
    param([string]$Source, [string]$Payload)
    if ($Source -eq $Payload -or
        $Payload.StartsWith($Source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $Source.StartsWith($Payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'MCP source distribution and payload roots must not overlap.'
    }
}

function Resolve-McpPythonCache {
    param([string]$Directory, [string]$Payload, [string]$Source)
    if (-not $Directory) { $Directory = Join-Path $McpPackagingRoot 'artifacts/python-runtime-cache' }
    $cache = Get-McpFullPath $Directory
    foreach ($root in @($Payload, $Source)) {
        if ($cache -eq $root -or $cache.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Python runtime cache must remain outside the installer payload and source distribution.'
        }
    }
    return $cache
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
    param([string]$DistributionPath, [string]$McpSourceRoot, [string]$SdkRoot, [string]$PythonWheel, [string]$PayloadRoot, [string]$HostDirectory, [string]$PythonRuntimeCacheDirectory, [switch]$ValidateOnly)
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
    $target = Copy-McpDistribution $DistributionPath (Get-McpFullPath $PayloadRoot) $PythonRuntimeCacheDirectory
    Test-McpPythonRuntime (Join-Path $target 'companion')
    Write-Host "Staged offline FL MCP component: $target"
}

if ($MyInvocation.InvocationName -ne '.') {
    $ErrorActionPreference = 'Stop'
    Invoke-McpStaging @PSBoundParameters
}
