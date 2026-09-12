# FL version portability — signature-scanning design + plan

Goal: ONE bridge binary that supports multiple FL Studio versions (25.2.5 and 26.1.0 today) by resolving
FL addresses at RUNTIME via byte-signature scanning, instead of the hardcoded `base + (ghidra - 0x400000)`
rebase (`dllmain.cpp` `GHIDRA_BASE`:46, `rb()`:221, `resolveAddr()`:2163). A wrong address = uncatchable AV
inside FL, so the whole design fails SAFE (refuse, never guess).

## Ground truth (verified 2026-07-09, both engines in Ghidra project "FlStudio")
- Versions: **2025 = 25.2.5.5319** (51MB, `/FLEngine_x64.dll`) · **2026 = 26.1.0.5530** (22MB, `/FL2026/FLEngine_x64.dll`). Disambiguate Ghidra queries by PATH. `run_script_inline` enabled.
- **FL code MOVED**: `0xd523c0` = FLtr_AddTimelineMarkerCore in 2025, unrelated in 2026.
- **Delphi RTL is STABLE-placed**: `Delphi_UStrAsg@0x4133f0` identical in both → low RTL addrs (link-order-first) likely need NO signatures; verify per-fn.
- **~267 hardcoded deps** (see inventory in memory `fl-version-portability`): ~76 functions (sig-scannable), ~40 data globals (RIP-relative), ~21 vtable offsets, ~130 struct offsets (class-layout — least portable). Window-host is the biggest/most fragile cluster (broke in 2026).

## Architecture (from the design pass)

**1. Signature format** — author IDA-style strings (`"48 8B 05 ? ? ? ? E8"`), compile once to `{bytes,mask}` + an anchor index (a RARE fixed byte, not 0x00/0x48/0xCC/0xFF) for memchr acceleration.

**2. Scanner** — parse the loaded module's PE headers (HMODULE == image base) for every `MEM_EXECUTE|CNT_CODE` section; memchr-anchored masked scan. **Uniqueness is mandatory**: 0 matches = NotFound, >1 = Ambiguous — both FAIL loudly; never pick one. Early-out at the 2nd hit. Full 51MB pass ≈ a few ms; ~150 symbols resolve <1s, once, at init. (FLEngine `.text` is NOT runtime-packed — today's linear rebase working proves in-memory==on-disk; VMProtect wraps the FL64 shell, not the engine.)

**3. Resolution table** — `SymEntry{ name, pattern, kind(Function|DataRef|VtableCallSite), dispOff, instrEnd, dispSize, ghidra[FLV_COUNT] fallback, addr, status }`. `resolveAllSymbols()` runs ONCE at BridgeStart / behind `fl_ready`; caches; logs a one-line gap report. `SYM("name")`/`findSym` lookup.

**4. Data globals (hard case)** — resolve via a function that references the global + RIP-relative decode:
`target = (matchStart + instrEnd) + (int32)disp32`, where `disp32` is at `matchStart+dispOff`. Note `mov dword[rip+d],imm32` (`C7 05 dd dd dd dd ii ii ii ii`) has `instrEnd = dispOff+4+4` (trailing imm), while `lea/mov reg,[rip+d]` has `instrEnd = dispOff+4`. Self-correcting across versions (disp differs, math still yields the right addr).

**5. Vtable/struct offsets** — NOT recovered by scanning; class-layout constants that a major version can shift. Strategy: (a) prefer CALLING an FL function over poking a field (deletes the offset dep); (b) DERIVE an offset from an accessor's ModRM displacement (same decode as §4) — `mov reg,[self+disp]` / `call [rax+disp]`; (c) otherwise keep as version-keyed constants + MANDATORY runtime sanity checks (vtable slot → points into a code section; pointer field → in-module/heap; Delphi string → len∈[0,1<<20] & elemsize==2, reusing the C# `EnsureInModule`/`FlEngineRange` guards). Browser/`TQuickEdit`/button field offsets are highest-churn.

**6. Migration (incremental, additive)** — new wire token `sym:NAME` tried first, else legacy hex (still valid on the current version) → **zero C# transport change**; migrating a call site is a one-token edit (`"11a5d20"` → `"sym:FLmx_RefreshRouting"`; internal `rb(0x114de10)` → `SYM("FormShortCut")`). Per entry: sig and/or per-version hardcoded fallback + self-check + drift-detect (`sigAddr != fbAddr` on a version where both are known = the sig is wrong, logged before use). Version detect = FileVersion resource (authoritative) + image-size sanity (do NOT assume newer=smaller without confirming). Un-migrated subsystem on a NEW version = `VersionLocked` → tool refuses (safe), never crashes.

**7. Signature generation (per symbol, validated in BOTH versions)** — take opening bytes from the entry; wildcard operands that relocate (E8/E9 rel32, RIP disp32, `mov r64,imm64` abs); keep opcodes/REX/ModRM; trim to the SHORTEST prefix that matches EXACTLY ONCE in 2025 AND EXACTLY ONCE in 2026. For data globals, pick a specific `lea/mov reg,[rip+DAT]` xref, record dispOff/instrEnd, verify `m_ghidra+instrEnd+disp == knownGlobal` in 2025. Automatable via `run_script_inline` (walk instructions, mask relocatable operand bytes, enforce the both-versions-unique gate).

**8. Fail-safe invariants** — resolve-all-at-init before serving; refuse (`err:unresolved:NAME`) don't guess; never pick among multiple matches; never trust a fallback on an unknown version; self-check bytes; sanity-check derived offsets/pointers; expose the gap list via a `syms` command JSON → the C# tool registry marks tools whose symbol didn't resolve as UNAVAILABLE (the LLM is never offered a tool that would AV).

## Plan (phased; multi-session)
- **P0 DONE**: terrain + inventory + design + methodology proof.
- **P1**: build the scanner infra in `dllmain.cpp` (scanner + table + `resolveWireAddr` + `syms` command + fail-safe) — ADDITIVE, no behavior change until call sites migrate. (subagent, C++, design complete)
- **P2**: Ghidra sig-generation + cross-version map for the 76 functions → emit the `SymEntry` table rows. (me, `run_script_inline`, serial)
- **P3**: data globals via RIP-relative (~40). (me, scripted)
- **P4**: struct/vtable offsets — verify vs 2026, re-derive the window-host cluster.
- **P5**: version detect + per-version fallback columns + `compatibility.json` update.
- **P6 (PRIORITY, front-runnable vertical)**: WINDOW-HOST fix — re-RE the 2026 host-form classRef + creation fn + event-TMethod offsets + embed offsets so the AI window shows in FL 2026. Proves the whole approach end-to-end.

## Parallelization
- **Me (Ghidra, serial/scripted)**: the cross-version diff/sig-generation (P2/P3) + window-host 2026 RE (P6) — stateful Ghidra can't be shared across subagents.
- **Subagents (parallel, no Ghidra)**: scanner infra C++ (P1), version plumbing + compatibility.json (P5), C# call-site migration, window-host fix implementation once addresses are known.
