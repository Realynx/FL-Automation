# 09 — Status & Capabilities

Snapshot after the autonomous build-out. FL Studio control via the injected `FlBridge.dll`.

## Working & LIVE-VERIFIED
- **Dev harness** (`flprobe` + `FlBridge.dll`): `inject`/`eject`/`reload` (clean unload, no VMProtect
  block), `ping`/`info`, `peek`/`peekabs`, `poke`/`pokeabs` (SEH+VirtualProtect), `scratch`
  (out-param buffer), `call`/`callabs` (8 int args, **on FL's main thread** via window-subclass),
  `key` (posts a keystroke — note: did NOT trigger FL transport, FL routes input itself).
- **Command bus** `FL_DispatchCommand(cmdId, value, flags)` @ `0xF53FE0`: GET `flags 0x2`, SET `0x11`.
- **Global:** tempo, master volume, master pitch, shuffle — set/get verified (UI-confirmed).
- **Mixer:** track volume (track 0 & 1), pan, FX-param formula `(track*0x40+slot)<<16` — verified,
  multi-track confirmed. Track ctrl offsets: vol `+0x70001FC0`, pan `+0x70001FC1`. **FX plugin params
  use IEEE float-bit values** (read `0x3F000000` = 0.5), range 0.0..1.0.
- **Channels:** volume/pan/pitch/mute by index (`(channel<<16)+param`) — verified (ch0-3).

## Available to the LLM NOW (compile-verified; full solution builds)
`INativeFlControl` (Core) → `FlInjectBridge` (FlStudio) → **`NativeControlPlugin`** (Agent, 14
`[KernelFunction]`s) registered in `ServiceConfiguration` + `FlPluginSet`. The agent can call:
`native_set_tempo/get_tempo`, `native_set_master_volume/pitch`, `native_set_shuffle`,
`native_set_mixer_volume/pan`, `native_set_mixer_fx_param`,
`native_set_channel_volume/pan/pitch/muted`, `native_route_channel_to_mixer`, `native_is_available`.

## How to run the native path
1. `cmake -S tools/bridge -B tools/bridge/build -A x64 && cmake --build tools/bridge/build --config Release`
2. Launch FL Studio, then `flprobe inject` (or the app calls the bridge once injection is wired into the UI).
3. The LLM's `native_*` tools now drive FL directly.

## Deferred RE (next targets, with the specific blocker)
- **Transport** (play/stop/record/loop): all callers are Python wrappers needing ctx `(*0x14A7DC0)()`
  which is **null at rest**; a zeroed fake ctx **crashes FL**; `PostMessage` key is a no-op. Next:
  find the toolbar Play handler's real song/transport singleton (or init the scripting host).
- **Pattern notes** (`addNote` @0xB7E210): needs a `flpianoroll.Note` object or building the internal
  24-byte NoteRec and `TList.Add` to the active pattern's score. Struct in `controls-patterns.md`.
- **Playlist clips**: direct struct writes (`poke`) + playlist refresh (vtbl+0x178). Struct in
  `controls-playlist.md`.
- **Channel enumeration / names / add-delete**: channel TList live layout unresolved (param control
  works by index without it); names need string writes.

## Key files
- Native bridge: `tools/bridge/` · C# client: `src/FruityLink.FlStudio/Inject/FlInjectBridge.cs`
- LLM plugin: `src/FruityLink.Agent/Plugins/NativeControlPlugin.cs` · interface: `src/FruityLink.Core/Abstractions/INativeFlControl.cs`
- RE maps: `re/generated/controls-*.md`, `re/06`/`08`; memory `fl-control-catalog`.
