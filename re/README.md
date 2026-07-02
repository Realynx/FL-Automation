# FL Studio Reverse-Engineering Notes

This folder documents the reverse-engineering work behind the **native DLL-injection
bridge** for FruityLink — the path to *total* programmatic control of FL Studio,
beyond what the sandboxed controller-script (MIDI/SysEx) bridge can reach.

> Scope & intent: FL Studio is licensed software running on the developer's own
> machine. This work builds an **interop control bridge** for an LLM agent. It does
> **not** modify, patch, or redistribute any Image-Line binary, and it does **not**
> touch the licensing / DRM path. See `02-control-strategy.md` ("Coexisting with FL's
> protections") for the rules we hold ourselves to.

## Documents

| File | Contents |
|------|----------|
| `01-module-architecture.md` | What FL Studio is made of: the FL64.exe shell, the FLEngine_x64.dll engine, the managed + Python layers, the startup/loader chain, and the protections (VMProtect, Authenticode). |
| `02-control-strategy.md` | How we get "total control": the three control surfaces (native engine calls, embedded-Python C-API, raw structures), the recommended hybrid, the requirement→API map, and how it plugs into the existing C# `IFlRpc`/`IFlBridge`. |
| `03-roadmap.md` | Phased plan with concrete next RE steps, risks, and the live log of Ghidra symbols we've named. |

## Reproducing the analysis environment

- **Binary:** `C:\Program Files\Image-Line\FL Studio 2025\FL64.exe` (v25.2.5.5319, x86-64).
- **Ghidra project:** `C:\Users\poofi\Documents\Ghidra\FlStudio` (program `FL64.exe`, image base `0x140000000`).
- **Access:** Ghidra MCP bridge (TCP `http://127.0.0.1:8089`). Renames/comments made via MCP are saved into the Ghidra project.

## Naming conventions used in Ghidra

So the disassembly reads cleanly for a human:

- `FL_*` — confirmed FL-specific logic (e.g. `FL_ResolveEngineDllPath_FromRegistry`).
- `cppinit_*` — C++ global constructors / static initializers.
- `crt_*` — MSVC C-runtime glue.
- `wstr_*`, `mem_*` — identified libc++/STL string & memory helpers.
- `g_p*` / `g_*` — global variables (Hungarian prefix per the bridge's linter).
- Uncertain identifications are marked **(inferred)** in the function's plate comment.

A running list of everything renamed lives at the bottom of `03-roadmap.md`.
