# Integration pending — FruityLink.Plugins.FlAgent ("FL Agent" plugin)

A new project `src/FruityLink.Plugins.FlAgent` packages the EXISTING FruityLink LLM agent behind the
host's `IFlPlugin` contract. No agent/LLM logic was rebuilt — it reuses `FruityLink.Agent`,
`FruityLink.Llm`, `FruityLink.Persistence`, `FruityLink.Core` and wires them into a small composition,
then surfaces the working chat experience in a self-contained WPF window. It builds clean (0/0) and
is verified to satisfy the host's discovery rules. Below is what you (human) must do to ship/discover
it, plus the one real gotcha (ALC type identity) and what I deliberately did NOT touch.

## What I built

`src/FruityLink.Plugins.FlAgent/` — a `net9.0-windows`, `UseWPF=true` class library (the plugin DLL):

- `FruityLink.Plugins.FlAgent.csproj`
  - `ManagePackageVersionsCentrally=false` (per contract). No app-level NuGet refs; everything comes
    transitively through ProjectReferences. One inline pin: `Microsoft.Extensions.AI.Abstractions`
    `10.7.0` to match the solution and silence MSB3277 (SK pulls 10.5.0 transitively otherwise).
  - ProjectReferences: `FruityLink.Plugins.Abstractions`, `FruityLink.Core`, `FruityLink.Llm`,
    `FruityLink.Agent`, `FruityLink.Persistence`. (Did NOT reference `FruityLink.FlStudio` — not
    needed; the plugin only touches FL via the `INativeFlControl` from the shared `context.Fl`. Did
    NOT reference `FruityLink.App` — its chat View/VM are coupled to the app theme dictionaries +
    `IDictationService`/Speech, so reusing them would drag in heavy, brittle deps; see UI choice.)
- `FlAgentPlugin.cs` — `public sealed class FlAgentPlugin : IFlPlugin`. Id=`fl-agent`, Name=`FL Agent`,
  Version=`1.0.0`. Cheap, side-effect-free ctor. `EnableAsync` builds the agent + opens the chat
  window (fully try/caught — never throws the host down; rolls back its enabled flag on failure).
  `DisableAsync` closes the window + releases the UI thread; both idempotent; FL left untouched.
- `Composition/AgentComposition.cs` — `ResolveOrBuild(context)`: prefers a host-provided
  `FruityLink.Agent.FlAgent` from `context.Services`, else composes one from the reused types
  (mirrors `tools/FruityLink.AgentRunner`) but feeds `context.Fl` into `NativeControlPlugin` so the
  plugin shares the host's single FL bridge. RAG retriever: reuses `context.Services`'
  `IKnowledgeRetriever` if present, else a private no-op (agent stays fully functional minus search).
- `Ui/UiHost.cs` — acquires a WPF `Dispatcher`: reuses the host app's UI thread when an
  `Application` is running, otherwise spins a private STA dispatcher thread (so the plugin also works
  in a non-WPF host, e.g. inside FL). `Stop()` only tears down a thread we own.
- `Ui/FlAgentChatWindow.cs` — a small pure-code (no XAML) dark chat window: transcript + composer,
  Enter-to-send/Shift+Enter newline, streams `FlAgent.StreamAsync` (text + thoughts) and shows each
  tool call via `FlAgent.ToolInvoked`. Unsubscribes from the (possibly shared) agent on close.

### Reused (NOT rebuilt) vs newly written

- REUSED as-is: `FlAgent`, `FlPluginSet`, `NativeControlPlugin`, `MusicTheoryPlugin`,
  `KnowledgePlugin`, `OrchestrationPlugin`, `SubAgentService`, `ToolCallFilter`, `OperationAuditSink`,
  `SystemPrompts` (via FlAgent), `AgentDelta`/`ToolCallInfo` (FruityLink.Agent); `ChatKernelFactory`,
  `LlmDiagnostics` (FruityLink.Llm); `JsonSettingsStore`, `DpapiSecretStore`, `StoragePaths`
  (FruityLink.Persistence). The plugin shares the user's `%APPDATA%\FruityLink\settings.json` +
  `secrets.json`, so it uses the SAME backend + API key configured in the WPF app — no separate setup.
- NEWLY written (thin glue only, no LLM logic): the `IFlPlugin` shell, the tiny composition, the UI
  thread helper, and the chat window.

### UI choice + why

A small dedicated chat window IN the plugin (pure C#, own dark palette), not the app's
`ChatPanel`/`ChatViewModel`. Reusing the app View/VM would require referencing `FruityLink.App`
(a WinExe) and merging its theme `ResourceDictionary`s (the panel uses `StaticResource AccentButton`
/ `SelectableText` / `DisclosureToggle` — these throw at load if absent) plus providing an
`IDictationService` (Speech stack). That is heavy and fragile for a hot-loaded plugin DLL. The
self-contained window reuses the real agent (all LLM behaviour intact) with zero theme/Speech
coupling. Per memory `in-fl-chat-tab-parked`, I did NOT use the in-FL native tab (its input can't
capture space) — this real OS window has proper keyboard focus.

## Build & verify

```sh
dotnet build src/FruityLink.Plugins.FlAgent/FruityLink.Plugins.FlAgent.csproj -c Release
# -> Build succeeded. 0 Warning(s) 0 Error(s)
```

Contract smoke (done, no FL needed): loaded the built DLL via reflection — type is public, implements
`IFlPlugin`, has a public parameterless ctor, `Activator.CreateInstance` succeeds, and
Id/Name/Description/Version read back correctly. This matches the host's `IsLoadablePluginType` filter.

## Deploy for discovery (what you must do)

The host (`FruityLink.Plugins.Host`) scans `<host-dir>\plugins\` — either flat
(`plugins\FruityLink.Plugins.FlAgent.dll`) or, recommended, a per-plugin folder
`plugins\fl-agent\` with the DLL + its private deps beside it. Each plugin loads in its OWN
collectible `AssemblyLoadContext` whose `AssemblyDependencyResolver` reads the plugin's `.deps.json`,
so you must ship the FULL dependency closure, not just the one DLL. Use publish:

```sh
dotnet publish src/FruityLink.Plugins.FlAgent/FruityLink.Plugins.FlAgent.csproj -c Release -o <stage>
# copy <stage>\* into  <host-dir>\plugins\fl-agent\
```

Publish emits ~40 DLLs (the DLL + `FruityLink.Agent/Llm/Persistence/Core`, Semantic Kernel,
Microsoft.Extensions.AI, the OpenAI connector, `.deps.json`, etc.). Notes:
- `FruityLink.Core.dll` and `FruityLink.Plugins.Abstractions.dll` will be in the folder; that's
  harmless — the host's `PluginLoadContext` deliberately SHARES those two from its default context, so
  the on-disk copies are ignored (keeps contract types identity-equal across the boundary).
- WPF + `System.Security.Cryptography.ProtectedData` (used by `DpapiSecretStore`) are provided by the
  Windows Desktop shared framework (the plugin is `net9.0-windows`/`UseWPF`), so they are correctly
  NOT in the publish folder — the runtime resolves them from the host's shared framework.

The host activates it through `PluginManager` (the toolbar "Plugins" dropdown / persisted
`plugins.json`); enabling calls `FlAgentPlugin.EnableAsync(context)` and the chat window appears.

## The one real gotcha — ALC type identity (affects `context.Services`)

Only `FruityLink.Core` + `FruityLink.Plugins.Abstractions` unify with the host; `FruityLink.Agent`
loads PRIVATELY in the plugin's ALC. So `context.Services.GetService(typeof(FlAgent))` will normally
MISS — the plugin-context `FlAgent` type is a different identity than the host-context one — and the
plugin will (correctly) fall back to composing its own agent. That is the intended, fully-functional
path; you don't need to do anything. Implications:

- To make the host-provided `FlAgent` path actually hit, the host would have to share
  `FruityLink.Agent` (add it to `PluginLoadContext.Shared`) AND register `FlAgent` in the
  `IServiceProvider` it passes to `PluginManager`. Not recommended (couples the agent assembly across
  the boundary). Leaving it as-is = the plugin self-composes; behaviour is identical to the WPF app.
- `IKnowledgeRetriever` DOES cross cleanly (it lives in the shared `FruityLink.Core`). If you want the
  plugin's `search_knowledge` to use the real RAG index, register an `IKnowledgeRetriever` in the
  `services` you hand `PluginHost.Initialize(fl, services, ...)`. Otherwise it no-ops gracefully.
- `context.Services` may be empty (the contract allows it) — the plugin handles null/empty fine.

## Strictly NOT touched (per scope)

`src/FruityLink.Plugins.Abstractions/`, `src/FruityLink.Plugins.Host/`, every existing `src/*` and
`tools/*` project, `bootstrap/`, `installer/`, `marketing/`, `FruityLink.slnx`,
`Directory.Build.props`, `Directory.Packages.props`. Not a git repo — no git ops. Everything new lives
under `src/FruityLink.Plugins.FlAgent/` (plus this notes file).

## Optional: add to the solution

I did not edit `FruityLink.slnx`. If you want it in the solution:
`dotnet sln FruityLink.slnx add src/FruityLink.Plugins.FlAgent/FruityLink.Plugins.FlAgent.csproj`
(it builds standalone regardless).
