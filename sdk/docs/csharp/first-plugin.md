# Your first C# plugin

Build a plugin that reads the project's tempo when enabled and writes it to the host
log. This verifies discovery, activation, and one live API call without changing music.

## Before you begin

You need the .NET 9 SDK and this repository checked out. To run the result, you also
need a matching FruityLink host installed in a compatible FL Studio build. See
[Installation](../installation.md). Run the following commands from the repository
root, beside `FruityLink.Sdk.slnx`.

## 1. Create the class library

```powershell
dotnet new classlib -n TempoLogger -o samples/TempoLogger -f net9.0
```

Replace `samples/TempoLogger/TempoLogger.csproj` with this complete project file.
The relative project reference assumes the folder location above:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\FruityLink.Plugins.Abstractions\FruityLink.Plugins.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

The reference supplies `IFlPlugin` and marks the assembly as a discovery candidate.
This uses the checkout's contracts rather than a different published package version.

## 2. Implement the plugin

Replace the generated `Class1.cs` with:

```csharp
using FruityLink.Plugins.Abstractions;

public sealed class TempoLogger : IFlPlugin
{
    private bool _enabled;

    public string Id => "tempo-logger";
    public string Name => "Tempo Logger";
    public string Description => "Reads the project tempo when enabled.";
    public string Version => "1.0.0";

    public async Task EnableAsync(IPluginContext context, CancellationToken ct = default)
    {
        if (_enabled) return;
        _enabled = true;

        try
        {
            if (!await context.Fl.IsAvailableAsync(ct))
            {
                context.Log("[tempo-logger] FL bridge is not ready.");
                return;
            }

            double bpm = await context.Fl.GetTempoAsync(ct);
            context.Log($"[tempo-logger] Current tempo: {bpm:0.###} BPM");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            context.Log("[tempo-logger] Tempo read cancelled.");
        }
        catch (Exception error)
        {
            context.Log($"[tempo-logger] Tempo read failed: {error.Message}");
        }
    }

    public Task DisableAsync(CancellationToken ct = default)
    {
        _enabled = false;
        return Task.CompletedTask;
    }
}
```

The class is public and has an implicit public parameterless constructor. Its `Id`
is a stable identifier used for enable/disable persistence. This example owns no
long-lived resources; a plugin that registers UI or starts services must clean up
those resources during disable.

## 3. Build and deploy

```powershell
dotnet build samples/TempoLogger/TempoLogger.csproj -c Release
```

Copy the output from `samples/TempoLogger/bin/Release/net9.0-windows/` to a separate
plugin directory. Set `$flHostDirectory` to the actual directory containing your
installed `FruityLink.Host.dll`:

```powershell
$flHostDirectory = 'C:\Program Files\Image-Line\FL Studio 2026\FruityLink'
$pluginDirectory = Join-Path $flHostDirectory 'plugins\TempoLogger'
New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
Copy-Item -Path 'samples/TempoLogger/bin/Release/net9.0-windows/*' -Destination $pluginDirectory -Recurse -Force
```

Use your installation's actual path; the example path is not auto-detected.
Writing under Program Files may require an elevated shell. The destination should
contain `TempoLogger.dll`, its build sidecars, and any private dependencies.

The host always supplies its shared contract assemblies. Plugin-local copies cannot
upgrade or override those contracts; use a matching host. See
[shared assemblies](../plugin-lifecycle.md#shared-unified-assemblies).

## 4. Enable and verify

Open **Tools → FL Plugins** in FL Studio and enable **Tempo Logger**. Read the daily
host log:

```powershell
$logPath = Join-Path $env:LOCALAPPDATA ('FruityLink\logs\plugin-host-' + (Get-Date -Format yyyyMMdd) + '.log')
Get-Content -LiteralPath $logPath -Tail 30
```

Look for `[tempo-logger] Current tempo: … BPM`. The value should match the open
project's tempo. If you see a readiness or error message, use
[Troubleshooting](../troubleshooting.md).

Disable and re-enable the plugin to read again. Enabled plugins are restored on the
next host startup.

## 5. Iterate

Rebuild and copy the output to the same deployed folder. Hot reload is enabled by
default and preserves the enabled state. The host loads a shadow copy so the original
DLL remains writable. Set `FRUITYLINK_PLUGIN_HOTRELOAD=0` before starting FL to disable
the watcher.

For a menu action and toolbar toggle, build the existing sample:

```powershell
dotnet build samples/HelloFl/HelloFl.csproj -c Release
```

Deploy that sample using the same folder pattern, enable **Hello FL**, and choose
**Tools → Hello FL: Log tempo**. Read [Menus and toolbar](../menus-and-toolbar.md)
before writing callbacks, then try the [C# recipes](recipes.md).
