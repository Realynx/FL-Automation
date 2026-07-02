# Integration pending — FruityLink.Mcp (MCP server)

A new project `src/FruityLink.Mcp` exposes the entire FL-control SDK (`INativeFlControl`) as MCP
tools over **stdio** (default) and optional **HTTP/SSE**. It is standalone and self-wires its own DI,
so it runs without any changes to the rest of the solution. Below is everything you (the human) may
want to do, plus what I deliberately did NOT touch.

## What I built

- `src/FruityLink.Mcp/` — a `net9.0-windows` console app:
  - `FruityLink.Mcp.csproj` — `ManagePackageVersionsCentrally=false`; versions pinned inline:
    - `ModelContextProtocol` 1.4.0 (stdio + DI hosting)
    - `ModelContextProtocol.AspNetCore` 1.4.0 (optional HTTP/SSE)
    - `Microsoft.Extensions.Hosting` 10.0.7
    - `FrameworkReference Microsoft.AspNetCore.App` (for the HTTP transport; runtime already present)
    - `ProjectReference` → `FruityLink.Core`, `FruityLink.FlStudio`
  - `Program.cs` — host + transport selection (`--http [url]` → HTTP/SSE, else stdio). stdio logging
    is forced to **stderr** so it never corrupts the stdout JSON-RPC channel.
  - `McpSupport.cs` — error classification (ported from `Agent/Plugins/PluginSupport.cs`) +
    `ParseNotes` (ported from `NativeControlPlugin.ParseNotes`).
  - `Tools/*.cs` — 17 `[McpServerToolType]` classes, **88 tools** total, each a thin wrapper over an
    `INativeFlControl` method (constructor-injected), mirroring `NativeControlPlugin` names/units and
    adding the few ops the plugin omitted (`native_get_pattern_name`, `native_get_channel_count`,
    `native_get_channel_name`, and the 4 `native_*_chat*` tools).
  - `README.md` — build/run + Claude Desktop config sample.

## DI wiring — none required from you

The server registers `INativeFlControl → FlInjectBridge` as a singleton itself and discovers tools via
`WithToolsFromAssembly()`. Nothing to add elsewhere. By default `FlInjectBridge` uses the named pipe
`\\.\pipe\FruityLinkBridge` (the standard out-of-process bridge), which is exactly right for a
separate MCP-server process talking to FL.

> If you ever host this *inside* FL's process (version.dll/CLR-host route), call
> `FlInjectBridge.UseInProcessTransport()` at startup to route commands through the in-process
> P/Invoke instead of the pipe. Not needed for the normal standalone MCP server.

## Build & verify

```sh
dotnet build src/FruityLink.Mcp/FruityLink.Mcp.csproj         # 0 warnings, 0 errors
```

Protocol smoke test (no FL needed) — pipe an `initialize` + `tools/list` handshake to the exe over
stdio and confirm 88 tools are listed. (I verified this; see the run report.)

## Optional: add it to the solution

I did **not** edit `FruityLink.slnx` (out of scope / parallel agents). If you want it in the solution:

```sh
dotnet sln FruityLink.slnx add src/FruityLink.Mcp/FruityLink.Mcp.csproj
```

It builds fine on its own regardless.

## Run

- stdio (local hosts): `FruityLink.Mcp.exe`
- HTTP/SSE (remote): `FruityLink.Mcp.exe --http http://127.0.0.1:3001`

Claude Desktop `mcpServers` config sample is in `src/FruityLink.Mcp/README.md`.

## Strictly NOT touched (per scope)

`FruityLink.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `marketing/`, `installer/`,
`bootstrap/`, `tools/`, `src/FruityLink.Host/`, and all existing `src/*` project files. No git ops.
Everything new lives under `src/FruityLink.Mcp/` (plus this notes file).

## Notes / caveats

- `Directory.Build.props` still applies to my project (it sets `NoWarn` incl. `CA1416`, `LangVersion`,
  nullable, implicit usings) — that's fine and intended; it does **not** force central package
  management. `Directory.Packages.props` is overridden locally via
  `ManagePackageVersionsCentrally=false`, so the shared version list isn't required.
- I did not reference `FruityLink.Agent` (would pull SemanticKernel + Llm). The two small helpers it
  has (`ParseNotes`, `BridgeError`) are ported verbatim into `McpSupport.cs` instead, keeping the MCP
  server lean and dependency-light.
- Tools depend on the sibling-stable `INativeFlControl` contract only — not on any in-progress
  in-process bridge work.
