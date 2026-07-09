# FlBridge — injected dev bridge (native)

A small native DLL injected into the live FL Studio (`FL64.exe`) process so the dev loop
can verify reverse-engineered addresses/structs against real memory, and to host the
eventual control orchestrator. **v1 is read-only** (named-pipe server + SEH-guarded memory
reads); it installs no hooks, so load/unload stays clean.

## Build (x64, MSVC via CMake)
```sh
cmake -S tools/bridge -B tools/bridge/build -A x64
cmake --build tools/bridge/build --config Release
# -> tools/bridge/build/Release/FlBridge.dll
```

## Use (via flprobe)
```sh
flprobe inject            # copies FlBridge.dll to a temp name, injects into FL64
flprobe bridge ping       # -> pong
flprobe bridge info       # -> {pid, bridgeBase, flEngineBase, flEngineSize}
flprobe bridge peek e02d30 16   # read 16 bytes at FLEngine + (0xE02D30-0x400000) (Ghidra addr)
flprobe eject             # BridgeStop() then FreeLibrary — clean unload
flprobe reload            # eject + rebuild-copy + inject (the iterate loop)
```

## Clean-unload contract
One worker thread joined by `BridgeStop()` before `FreeLibrary`; overlapped
`ConnectNamedPipe` waits on the stop event; no hooks/timers/callbacks registered with FL;
module never pinned. See [../../docs/native-bridge.md](../../docs/native-bridge.md) for the
full architecture + protocol overview.
