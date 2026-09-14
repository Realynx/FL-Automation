[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)

# Exercise the published native installer against disposable profiles. This never installs
# into FL Studio, invokes an FL executable, or updates the current user's MCP settings.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'This integration check requires PowerShell 7.' }
$publish = [IO.Path]::GetFullPath($PublishDirectory)
$installer = Join-Path $publish 'FruityLink.Installer.exe'
$sourceCompanion = Join-Path $publish 'payload/optional-plugins/fl-mcp/companion'
if (-not (Test-Path -LiteralPath $installer)) { throw 'Published installer is missing.' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('fruitylink-package-' + [Guid]::NewGuid().ToString('N'))
$fl = Join-Path $scratch 'FL Studio scratch'
$profile = Join-Path $scratch 'Profile with spaces'
$checks = [Collections.Generic.List[string]]::new()

function Assert-Check([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    $checks.Add($Description)
}

function Write-Scratch([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Invoke-Installer([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $info = [Diagnostics.ProcessStartInfo]::new($installer)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = 'Hidden'
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.ArgumentList.Add('--silent')
    # Deliberately invalid external Python paths must not affect the private runtime probe.
    $info.Environment['PYTHONHOME'] = Join-Path $scratch 'not-python'
    $info.Environment['PYTHONPATH'] = Join-Path $scratch 'not-the-sdk'
    $process = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'Scratch installer CLI invocation timed out.'
        }
        if ($process.ExitCode -ne $ExpectedExit) {
            throw "Expected exit $ExpectedExit, received $($process.ExitCode): $($stdout.Result) $($stderr.Result)"
        }
        return $stdout.Result
    }
    finally { $process.Dispose() }
}

[IO.Directory]::CreateDirectory($fl) | Out-Null
Write-Scratch (Join-Path $fl 'FL64.exe') 'Never execute this placeholder.'
Write-Scratch (Join-Path $fl 'Data/Templates/Empty/Empty.flp') 'Never open this placeholder.'
$manifest = Invoke-Installer @('--print-manifest') | ConvertFrom-Json
Assert-Check ($manifest.Items.Count -eq 6) 'Published default manifest includes FLMCP, Python IDE, and Serum support.'
Assert-Check (@($manifest.Items | Where-Object Destination -eq 'FruityLink/python/extensions/serum-support').Count -eq 1) 'Published default manifest installs Serum support in the trusted extension directory.'
$without = Invoke-Installer @('--print-manifest', '--without-mcp') | ConvertFrom-Json
Assert-Check ($without.Items.Count -eq 4 -and @($without.Items | Where-Object Destination -eq 'FruityLink/plugins/fl-python-ide').Count -eq 1) 'Published manifest can exclude FLMCP and keep Python IDE and Serum support.'
$withoutIde = Invoke-Installer @('--print-manifest', '--without-python-ide') | ConvertFrom-Json
Assert-Check ($withoutIde.Items.Count -eq 5 -and @($withoutIde.Items | Where-Object Destination -eq 'FruityLink/plugins/fl-python-ide').Count -eq 0) 'Published manifest can exclude Python IDE and keep FLMCP and Serum support.'
$withoutSerum = Invoke-Installer @('--print-manifest', '--without-serum-support') | ConvertFrom-Json
Assert-Check ($withoutSerum.Items.Count -eq 5 -and @($withoutSerum.Items | Where-Object Destination -eq 'FruityLink/python/extensions/serum-support').Count -eq 0) 'Published manifest can exclude Serum support independently.'
$frameworkOnly = Invoke-Installer @('--print-manifest', '--without-mcp', '--without-python-ide', '--without-serum-support') | ConvertFrom-Json
Assert-Check ($frameworkOnly.Items.Count -eq 2) 'All separately selectable components can be deselected.'
Assert-Check (Test-Path -LiteralPath (Join-Path $publish 'payload/FruityLink/python/runtime/python314.dll')) 'Framework includes embedded Python independently of optional plugins.'
Assert-Check (Test-Path -LiteralPath (Join-Path $publish 'payload/optional-plugins/serum-support/fruitylink_serum.whl')) 'Published payload includes the Serum support wheel under its upgrade-stable installed name.'
$listed = Invoke-Installer @('--list-mcp-clients', '--mcp-profile', $profile)
Assert-Check ($listed.Contains('claude-code:') -and $listed.Contains('codex:')) 'Native client discovery exposes expected clients.'
Assert-Check (-not (Test-Path -LiteralPath $profile)) 'Listing clients leaves the scratch profile absent.'

$clients = 'codex,claude-desktop,claude-code,cursor,vscode,gemini-cli,windsurf,opencode,generic-json'
$setup = @('--configure-mcp', '--fl-path', $fl, '--mcp-profile', $profile, '--mcp-clients', $clients)
$null = Invoke-Installer ($setup + '--dry-run')
Assert-Check (-not (Test-Path -LiteralPath $profile)) 'Dry run succeeds without runtime files and writes no client configuration.'

$companion = Join-Path $fl 'FruityLink/tools/fl-mcp'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($companion)) | Out-Null
Copy-Item -LiteralPath $sourceCompanion -Destination $companion -Recurse
$codex = Join-Path $profile '.codex/config.toml'
$claude = Join-Path $profile '.claude.json'
$state = Join-Path $profile 'AppData/Local/FruityLink/plugins.json'
$codexBefore = "model = 'keep-my-model'`n[mcp_servers.unrelated]`ncommand = 'keep-my-server'`n"
$claudeBefore = '{"theme":"keep-theme","mcpServers":{"unrelated":{"command":"keep-server"}}}'
Write-Scratch $codex $codexBefore
Write-Scratch $claude $claudeBefore
Write-Scratch $state '{"Enabled":["other-plugin"],"extra":"keep-state"}'
$null = Invoke-Installer $setup

$relativeConfigs = @(
    '.codex/config.toml', 'AppData/Roaming/Claude/claude_desktop_config.json', '.claude.json',
    '.cursor/mcp.json', 'AppData/Roaming/Code/User/mcp.json', '.gemini/settings.json',
    '.codeium/windsurf/mcp_config.json', '.config/opencode/opencode.jsonc',
    'AppData/Local/FlMcp/mcp-settings.json'
)
foreach ($relative in $relativeConfigs) {
    $config = Join-Path $profile $relative
    Assert-Check (Test-Path -LiteralPath $config) "Created $relative in the scratch profile."
    $content = [IO.File]::ReadAllText($config)
    Assert-Check ($content.Contains('FL_MCP_PYTHON_RUNTIME') -and -not $content.Contains('FL_MCP_PYTHON"')) "Embedded runtime configured in $relative."
}
$codexAfter = [IO.File]::ReadAllText($codex)
Assert-Check ($codexAfter.Contains('keep-my-model') -and $codexAfter.Contains('keep-my-server')) 'Codex settings and unrelated server preserved.'
$claudeAfter = Get-Content -LiteralPath $claude -Raw | ConvertFrom-Json
Assert-Check ($claudeAfter.theme -eq 'keep-theme' -and $claudeAfter.mcpServers.unrelated.command -eq 'keep-server') 'Claude settings and unrelated server preserved.'
$envSettings = $claudeAfter.mcpServers.flmcp.env
Assert-Check ($envSettings.FL_MCP_PYTHON_RUNTIME -eq (Join-Path $companion 'python/runtime')) 'Runtime directory points to installed private Python.'
Assert-Check ($envSettings.FL_MCP_FL_EXE -eq (Join-Path $fl 'FL64.exe')) 'Client connection targets only the scratch FL path.'
$enabled = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
Assert-Check ($enabled.Enabled -contains 'fl-mcp' -and $enabled.Enabled -contains 'other-plugin' -and $enabled.extra -eq 'keep-state') 'Plugin enablement preserves other plugins and settings.'
$codexBackup = @(Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($codex)) -Filter '*.bak' -File)
Assert-Check ($codexBackup.Count -eq 1 -and [IO.File]::ReadAllText($codexBackup[0].FullName) -ceq $codexBefore) 'Existing Codex file has an exact original-content backup.'

$beforeRepeat = @{}
Get-ChildItem -LiteralPath $profile -File -Recurse | ForEach-Object { $beforeRepeat[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
$null = Invoke-Installer $setup
$afterRepeat = @(Get-ChildItem -LiteralPath $profile -File -Recurse)
Assert-Check ($beforeRepeat.Count -eq $afterRepeat.Count) 'Repeated setup creates no extra backups or files.'
foreach ($file in $afterRepeat) {
    if ($beforeRepeat[$file.FullName] -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'Repeated setup changed an already-matching configuration.' }
}
$checks.Add('Repeated setup preserves every configuration byte.')

$invalid = Join-Path $scratch 'Invalid profile'
$invalidCodex = Join-Path $invalid '.codex/config.toml'
Write-Scratch $invalidCodex $codexBefore
Write-Scratch (Join-Path $invalid '.claude.json') '{invalid'
$null = Invoke-Installer @('--configure-mcp', '--fl-path', $fl, '--mcp-profile', $invalid, '--mcp-clients', 'codex,claude-code') 1
Assert-Check ([IO.File]::ReadAllText($invalidCodex) -ceq $codexBefore) 'Malformed selected client prevents earlier valid client writes.'
Assert-Check (-not (Test-Path -LiteralPath (Join-Path $invalid 'AppData/Local/FruityLink/plugins.json'))) 'Malformed selected client prevents plugin-state writes.'

$report = [ordered]@{ Passed = $checks.Count; Checks = $checks; ScratchDirectory = $scratch; PublishDirectory = $publish }
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $scratch 'report.json') -Encoding utf8
Write-Output ("Passed {0} published-package checks. Scratch report: {1}" -f $checks.Count, (Join-Path $scratch 'report.json'))
