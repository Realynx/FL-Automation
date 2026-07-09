<div align="center">

<img src="assets/logo.svg" width="104" alt="FL Automate" />

<h1>FruityLink SDK</h1>

<p><strong>Write C# plugins that run inside FL Studio.</strong><br/>
The open-source plugin system and FL Studio control surface behind
<a href="https://fl-automate.com">FL Automate</a>.</p>

<p>
<img src="https://img.shields.io/badge/license-MIT-8b5cf6?style=flat-square" alt="MIT license" />
<img src="https://img.shields.io/badge/.NET-9.0-7c3aed?style=flat-square" alt=".NET 9" />
<img src="https://img.shields.io/badge/FL%20Studio-2025%20%2F%202026-d946ef?style=flat-square" alt="FL Studio 2025/2026" />
</p>

<p>
<a href="https://fl-automate.com">Website</a> ·
<a href="https://fl-automate.com/#pricing">Pricing</a> ·
<a href="docs/getting-started.md">Getting started</a> ·
<a href="#documentation">Docs</a>
</p>

<p>🌱 <strong>1.5% of FL Automate AI usage goes to carbon credits</strong> —
<a href="https://fl-automate.com/#pricing">fl-automate.com/#pricing</a></p>

</div>

---

FruityLink lets a plain C# class library run inside FL Studio and drive it programmatically —
tempo, piano-roll notes, patterns, playlist clips, mixer, plugin parameters, native menus and
toolbar buttons, even your own UI embedded in FL's window chrome.

The FL Automate AI assistant is a separate, closed-source plugin **built on this SDK**. Everything
it can do in FL Studio, your plugin can do too.

## Quickstart

Reference `FruityLink.Plugins.Abstractions`, implement one interface:

```csharp
public sealed class MyPlugin : IFlPlugin
{
    public string Id => "my-plugin";
    public string Name => "My Plugin";
    public string Description => "Logs the current tempo.";
    public string Version => "1.0.0";

    private IDisposable? _cmd;

    public Task EnableAsync(IPluginContext ctx, CancellationToken ct = default)
    {
        _cmd = ctx.Menu.AddCommand(FlNativeMenu.Tools, "Log tempo",
            onInvoke: () => _ = Task.Run(async () =>
                ctx.Log($"tempo = {await ctx.Fl.GetTempoAsync()} BPM")));
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken ct = default)
    {
        _cmd?.Dispose();
        return Task.CompletedTask;
    }
}
```

Drop the build output into `<host-dir>\plugins\MyPlugin\` and enable it from **Tools ▸ FL Plugins**.
Rebuild and it hot-reloads. Full example: [`samples/HelloFl`](samples/HelloFl).

## What you get

- **`INativeFlControl`** — a typed, safe control surface: channels, patterns, notes, mixer,
  playlist, transport, markers, projects, arrangements, plugin params, automation.
- **Native menu + toolbar integration** — commands and toggles in FL's own UI.
- **UI embedding** — host Avalonia, WPF, or any `HWND` inside a real FL editor form.
- **Hot reload + isolation** — each plugin loads in its own collectible `AssemblyLoadContext`
  from a shadow copy.
- **Fail-safe by design** — FL functions are resolved by signature scan and refused rather than
  guessed; plugins never see raw memory primitives.

## Documentation

- [Getting started](docs/getting-started.md) — first plugin, deploy, debug.
- [Plugin lifecycle](docs/plugin-lifecycle.md) — discovery, isolation, pre-warm, hot reload.
- [FL control API](docs/fl-control-api.md) — the `INativeFlControl` surface and FL's value conventions.
- [Menus and toolbar](docs/menus-and-toolbar.md) — contributing to FL's native UI.
- [Window embedding](docs/window-embedding.md) — your window inside FL, incl. the WPF helpers.
- [Avalonia UI](docs/avalonia-ui.md) — Avalonia inside FL, and the gotchas that matter.
- [Native bridge](docs/native-bridge.md) — architecture, protocol, build, FL version policy.

## Building

```sh
dotnet build FruityLink.Sdk.slnx                                  # managed SDK + sample
cmake -S native/bridge -B native/bridge/build -A x64              # native bridge (MSVC, x64)
cmake --build native/bridge/build --config Release
```

## License

[MIT](LICENSE) © FL Automate

<div align="center"><sub>
Made for producers who script. <a href="https://fl-automate.com/#pricing">FL Automate pricing</a> —
🌱 1.5% of AI usage funds carbon credits.
</sub></div>
