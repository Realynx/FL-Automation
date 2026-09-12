# Builds the whole in-process bootstrap chain and assembles bootstrap\dist\ in the exact layout
# that must be dropped into FL Studio's program directory.
#
#   pwsh bootstrap\build-and-stage.ps1               # DEV stage: bridge debug pipe ON (flprobe works)
#   pwsh bootstrap\build-and-stage.ps1 -Production   # locked-down stage: NO debug pipe
#
# Output: bootstrap\dist\  ->  copy its contents into "...\Image-Line\FL Studio 2025\"
#   version.dll
#   FruityLink\FlClrHost.dll
#   FruityLink\FlBridge.dll
#   FruityLink\FruityLink.Host.dll (+ .runtimeconfig.json, .deps.json)
#   FruityLink\FruityLink.FlStudio.dll
#   FruityLink\FruityLink.Core.dll
#   FruityLink\FruityLink.Plugins.Abstractions.dll
#   FruityLink\FruityLink.Plugins.Host.dll
#   FruityLink\plugins\fl-agent\*           (the FL Automate plugin publish closure)
param(
    # When set, build the bridge WITHOUT the external debug named-pipe server (production / lockdown).
    # Default (dev) builds the bridge with -DFRUITYLINK_DEBUG=ON so flprobe can drive it over the pipe.
    [switch]$Production
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Write-Host "repo = $repo"
$debugPipe = -not $Production
Write-Host ("bridge debug pipe : {0}" -f ($(if ($debugPipe) { 'ON (dev)' } else { 'OFF (production)' })))

# 1) native: version.dll proxy + FlClrHost.dll CoreCLR host
cmake -S "$repo\bootstrap" -B "$repo\bootstrap\build" -A x64 | Out-Null
cmake --build "$repo\bootstrap\build" --config Release | Out-Null

# 2) native: FlBridge.dll (in-proc C++ SDK; same logic that used to be injected + pipe-driven).
#    Re-configure each run so the FRUITYLINK_DEBUG option is applied (the external debug pipe is gated
#    out of production builds — see sdk\native\bridge\CMakeLists.txt + the FRUITYLINK_DEBUG_PIPE macro).
$flDebug = if ($debugPipe) { "ON" } else { "OFF" }
cmake -S "$repo\sdk\native\bridge" -B "$repo\sdk\native\bridge\build" -A x64 "-DFRUITYLINK_DEBUG=$flDebug" | Out-Null
cmake --build "$repo\sdk\native\bridge\build" --config Release
if ($LASTEXITCODE -ne 0) { throw "FlBridge.dll (C++ bridge) build FAILED — see errors above; refusing to stage a stale DLL." }

# 3) managed: FruityLink.Host (+ runtimeconfig/deps + FlStudio/Core/Plugins.Abstractions/Plugins.Host)
dotnet build "$repo\sdk\src\FruityLink.Host\FruityLink.Host.csproj" -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw "FruityLink.Host (C#) build FAILED — see 'dotnet build' output; refusing to stage." }

# 4) assemble dist\
$dist = "$repo\bootstrap\dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path "$dist\FruityLink" -Force | Out-Null

Copy-Item "$repo\bootstrap\build\VersionProxy\Release\version.dll" "$dist\version.dll" -Force
Copy-Item "$repo\bootstrap\build\CLRHost\Release\FlClrHost.dll"     "$dist\FruityLink\" -Force
Copy-Item "$repo\sdk\native\bridge\build\Release\FlBridge.dll"           "$dist\FruityLink\" -Force

$ho = "$repo\sdk\src\FruityLink.Host\bin\Release\net9.0-windows"
foreach ($f in @(
    "FruityLink.Host.dll",
    "FruityLink.Host.runtimeconfig.json",
    "FruityLink.Host.deps.json",
    "FruityLink.FlStudio.dll",
    "FruityLink.Core.dll",
    "FruityLink.Plugins.Abstractions.dll",
    "FruityLink.Plugins.Host.dll")) {
    Copy-Item "$ho\$f" "$dist\FruityLink\" -Force
}

# 5) deploy the FL Automate plugin: publish its FULL closure into plugins\fl-agent\ so the host discovers
#    + hot-reloads it. The host's default plugins dir is <host-dir>\plugins (AppContext.BaseDirectory,
#    anchored to ...\FruityLink\ by the CLR host), which is exactly this folder once dist is installed.
$pluginDst = "$dist\FruityLink\plugins\fl-agent"
New-Item -ItemType Directory -Path $pluginDst -Force | Out-Null
Write-Host "`nPublishing FL Automate plugin (-c Debug) -> $pluginDst"
dotnet publish "$repo\src\FruityLink.Plugins.FlAgent\FruityLink.Plugins.FlAgent.csproj" -c Debug -o $pluginDst | Out-Null
if ($LASTEXITCODE -ne 0) { throw "FL Automate plugin publish FAILED — refusing to stage." }

# 5b) The flagship UI now runs Avalonia IN-PROCESS inside FL, so the plugin closure MUST carry the full
#     Avalonia native/runtime set (Skia / HarfBuzz / ANGLE) + managed assemblies + the bundled fonts
#     (embedded as avares:// resources in FruityLink.Ui.Avalonia.dll). A missing libSkiaSharp.dll throws
#     on Avalonia init INSIDE FL — the single most likely deploy failure — so fail the stage loudly if any
#     required piece is absent rather than shipping a plugin that crashes on enable.
$avRequired = @(
    "FruityLink.Ui.Avalonia.dll",                       # the UI lib (App/Views/Theme/ViewModels + fonts)
    "Avalonia.Base.dll", "Avalonia.Controls.dll", "Avalonia.Win32.dll", "Avalonia.Skia.dll",
    "SkiaSharp.dll", "HarfBuzzSharp.dll",
    "runtimes\win-x64\native\libSkiaSharp.dll",         # native Skia (Avalonia's Win32 software renderer)
    "runtimes\win-x64\native\libHarfBuzzSharp.dll",     # native text shaping
    "runtimes\win-x64\native\av_libglesv2.dll"          # ANGLE (present even in software mode)
)
$avMissing = $avRequired | Where-Object { -not (Test-Path (Join-Path $pluginDst $_)) }
if ($avMissing) { throw "Avalonia UI closure INCOMPLETE in $pluginDst — missing: $($avMissing -join ', '). FL would crash on plugin enable." }
Write-Host ("Avalonia UI closure OK ({0} required assets present, incl. native libSkiaSharp)." -f $avRequired.Count)

# 6) sync the staged payload into the GUI installer's payload dir so FruityLink.Installer ships the
#    exact same files (its csproj copies payload\** to the build output; manifest = version.dll + FruityLink\).
$instPayload = "$repo\installer\FruityLink.Installer\payload"
New-Item -ItemType Directory -Path $instPayload -Force | Out-Null
Get-ChildItem $instPayload -Force | Where-Object { $_.Name -ne "PLACEHOLDER.txt" } | Remove-Item -Recurse -Force
Copy-Item "$dist\*" $instPayload -Recurse -Force
Write-Host "Synced GUI installer payload -> $instPayload"

Write-Host "`nStaged dist layout:"
Get-ChildItem $dist -Recurse -File | ForEach-Object { "  " + $_.FullName.Substring($dist.Length+1) }
Write-Host "`nNext: run bootstrap\install.ps1 (elevated) to copy dist\* into FL Studio's directory."
