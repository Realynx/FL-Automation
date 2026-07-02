# FruityLink MCP Server

A [Model Context Protocol](https://modelcontextprotocol.io/) (MCP) server that exposes the **entire
FruityLink FL Studio control SDK** as MCP tools, so any MCP-capable AI client or host
(Claude Desktop, etc.) can drive FL Studio over the standard protocol.

This is the **open-source** side of FruityLink and is independent of any particular AI backend. It
re-exposes the existing C# FL-control SDK (`INativeFlControl`, implemented by the injected-bridge
client `FlInjectBridge`) as **88 MCP tools** with clean names, JSON-schema parameters, and tuned
descriptions (units, ranges) mirroring the in-app Semantic-Kernel tool surface.

## How it works

```
MCP host (Claude Desktop, …)
        │  JSON-RPC over stdio (or HTTP/SSE)
        ▼
FruityLink.Mcp  ── INativeFlControl ──▶  FlInjectBridge
        │                                     │  named pipe  \\.\pipe\FruityLinkBridge
        │                                     ▼
        └──────────────────────────▶  FlBridge.dll (injected into FL64.exe)
                                              ▼
                                        FL Studio engine
```

- Tools call the SDK, which talks to the injected `FlBridge.dll` over the named pipe.
- **Listing tools requires nothing.** **Invoking** a tool requires FL Studio running with the
  FruityLink bridge injected. If the bridge isn't reachable, tools return a clear message asking the
  user to start FL and inject the bridge (connectivity errors are distinguished from logic errors).

## Prerequisites

- .NET SDK 9.0+ (the project targets `net9.0-windows`).
- Windows + FL Studio (FL64.exe) with the FruityLink `FlBridge.dll` injected — required only to
  *invoke* tools, not to list them.

## Build

```sh
dotnet build src/FruityLink.Mcp/FruityLink.Mcp.csproj -c Release
```

The executable lands at:

```
src/FruityLink.Mcp/bin/Release/net9.0-windows/FruityLink.Mcp.exe
```

(For a self-contained folder, `dotnet publish -c Release -r win-x64 --self-contained false`.)

## Run

### stdio (default — for local MCP hosts)

```sh
dotnet run --project src/FruityLink.Mcp/FruityLink.Mcp.csproj
# or run the built exe directly:
src/FruityLink.Mcp/bin/Release/net9.0-windows/FruityLink.Mcp.exe
```

stdio uses **stdout for the JSON-RPC channel**; all logging is routed to stderr so it can't corrupt
the protocol.

### HTTP / SSE (optional — for remote/networked hosts)

```sh
FruityLink.Mcp.exe --http                      # binds http://127.0.0.1:3001 (MCP endpoint at /)
FruityLink.Mcp.exe --http http://0.0.0.0:8080  # custom bind URL
```

A plain `GET /` returns HTTP 406 — that's expected; the streamable-HTTP endpoint requires the
`Accept: text/event-stream` header used by MCP clients.

## Sample MCP client config (Claude Desktop)

Add to `claude_desktop_config.json` (Settings → Developer → Edit Config):

```json
{
  "mcpServers": {
    "fruitylink": {
      "command": "C:\\Users\\poofi\\source\\repos\\FruityLoopsLLMLink\\src\\FruityLink.Mcp\\bin\\Release\\net9.0-windows\\FruityLink.Mcp.exe",
      "args": []
    }
  }
}
```

Or run via the SDK without a pre-built exe:

```json
{
  "mcpServers": {
    "fruitylink": {
      "command": "dotnet",
      "args": [
        "run", "--project",
        "C:\\Users\\poofi\\source\\repos\\FruityLoopsLLMLink\\src\\FruityLink.Mcp\\FruityLink.Mcp.csproj",
        "-c", "Release"
      ]
    }
  }
}
```

Restart the host; the tools (all prefixed `native_`) then appear.

## Tool coverage

The server exposes **every** operation in `INativeFlControl` (the canonical SDK interface) — a strict
superset of `NativeControlPlugin` (the in-app Semantic-Kernel tools). Categories:

| Category | Tools |
|---|---|
| Status | `native_is_available` |
| Global / master | `native_set_tempo`, `native_get_tempo`, `native_set_master_volume`, `native_set_master_pitch`, `native_set_shuffle` |
| Transport / song | `native_transport_play`, `native_transport_stop`, `native_transport_toggle_record`, `native_get_song_state`, `native_set_song_mode`, `native_seek`, `native_list_markers`, `native_add_marker` |
| Mixer | `native_set_mixer_volume`, `native_set_mixer_pan`, `native_set_mixer_fx_param`, `native_set_mixer_send`, `native_set_mixer_eq_gain` |
| Channel rack | `native_set_channel_volume`, `native_set_channel_pan`, `native_set_channel_pitch`, `native_set_channel_muted`, `native_route_channel_to_mixer`, `native_list_channels`, `native_get_channel_count`, `native_get_channel_name`, `native_select_channel` |
| Patterns | `native_list_patterns`, `native_get_current_pattern`, `native_get_pattern_name`, `native_select_pattern`, `native_create_pattern`, `native_clear_pattern`, `native_get_ppq` |
| Piano roll | `native_add_note`, `native_add_notes`, `native_get_notes` |
| Plugins / inserts | `native_list_available_plugins`, `native_get_channel_plugin`, `native_add_channel`, `native_list_mixer_effects`, `native_add_mixer_effect`, `native_remove_mixer_effect`, `native_clone_mixer_effect` |
| Plugin params | `native_list_channel_plugin_params`, `native_set_channel_plugin_param`, `native_list_mixer_plugin_params`, `native_set_mixer_plugin_param` |
| Samples | `native_list_samples`, `native_add_sample_channel`, `native_replace_channel_sample` |
| Playlist tracks | `native_list_playlist_tracks`, `native_set_track_name`, `native_set_track_color`, `native_set_track_mute`, `native_set_track_collapsed`, `native_select_track` |
| Playlist clips | `native_list_clips`, `native_add_pattern_clip`, `native_move_clip`, `native_resize_clip`, `native_delete_clip`, `native_mute_clip`, `native_slice_clip`, `native_duplicate_clip` |
| Project lifecycle | `native_save_project`, `native_open_project`, `native_new_project`, `native_get_project_info`, `native_save_project_as`, `native_save_copy`, `native_save_new_version`, `native_list_recent_projects` |
| Arrangements | `native_list_arrangements`, `native_make_arrangement`, `native_clone_arrangement`, `native_rename_arrangement`, `native_delete_arrangement`, `native_select_arrangement` |
| Automation clips | `native_list_automation_points`, `native_add_automation_point`, `native_delete_automation_point` |
| Render | `native_render` |
| In-FL chat tab | `native_open_chat_tab`, `native_close_chat_tab`, `native_chat_poll`, `native_chat_say` |

### Units / scales (as documented in each tool)

- Volumes: `0–12800` (~7624 = 0 dB for master; 10000 ≈ channel default).
- Pan: `0–12800` (6400 = center).
- MIDI keys: `0–131` (60 = middle C).
- Note positions/lengths and clip ticks: **PPQ ticks** (call `native_get_ppq`, often 960/quarter).
- Automation point time: **beats** (4 beats = 1 bar in 4/4).
- Normalized plugin params: `0.0–1.0`.

## Notes

- Built on the official **`ModelContextProtocol`** C# SDK (1.4.0) with stdio + DI hosting, plus
  **`ModelContextProtocol.AspNetCore`** for the optional HTTP/SSE transport.
- Package versions are pinned inline; the project sets
  `ManagePackageVersionsCentrally=false` so it builds without the repo's shared package props.
