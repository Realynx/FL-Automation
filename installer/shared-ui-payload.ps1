# First-party UI plugins and the process-owned hosting API ship as one build.
function Assert-SharedUiPayload {
    param(
        [Parameter(Mandatory)][string]$HostDirectory,
        [Parameter(Mandatory)][string]$PluginDirectory
    )

    foreach ($name in @('FruityLink.Core.dll', 'FruityLink.Plugins.Abstractions.dll', 'FruityLink.Ui.Avalonia.Hosting.dll')) {
        $hostFile = Join-Path $HostDirectory $name
        $pluginFile = Join-Path $PluginDirectory $name
        foreach ($file in @($hostFile, $pluginFile)) {
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                throw "Shared UI payload is incomplete: $file"
            }
        }
        if ((Get-FileHash -LiteralPath $hostFile -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $pluginFile -Algorithm SHA256).Hash) {
            throw "Shared UI contract mismatch for $name in $PluginDirectory. Rebuild the host and UI plugins together."
        }
    }
}
