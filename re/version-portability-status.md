# FL version portability — overnight status (2026-07-09)

## ★ 2026-07-09 (day) — LIVE CRASH ON FL 2026 FIXED (table-aware legacy hex path)
The readiness-gate fix let the managed side run native ops on 2026, but the C# surface still hardcodes
**2025** Ghidra addresses and the bridge rebased them flat (`base + (a-0x400000)`) — correct only on 2025.
On 2026 those landed on garbage → AV in the clip-collection indexer (FLEngine RVA 0xEDD0E0 / ghidra
0x12DD0E0). FIX: `resolveAddr()` in dllmain.cpp is now **table-aware** — it calls `sig_resolveAll()` then
`sig_addrByGhidra2025(a)` (new, in sigscan.cpp/.h: reverse-lookup a 2025 ghidra addr → its RESOLVED
runtime addr). A table hit returns the version-correct address for the WHOLE un-migrated hex surface at
once (no per-call-site migration needed). On a CONFIRMED 2026 engine, an UNMAPPED address now REFUSES
(`*ok=false` → caller gets a clean err) instead of rebasing to a wrong pointer — so any remaining gap
degrades to a failed op, never a crash. 2025/unknown keep the old linear rebase (byte-identical, no
regression). Built clean; deployed to the live FL 2026 install (elevated copy over
`...\FL Studio 2026\FruityLink\FlBridge.dll`) + staged to all 8 installer payload copies. Live-test pending.

## ★ 2026-07-09 (day) — HostClassRef RESOLVED for 2026 → window unblocked + window UI changes
- **HostClassRef 2026 = 0xd99378** (was the last window-embed gap). Recovered by VMT-slot disambiguation
  (Ghidra subagent): the TScriptDialog-unique virtual override `paint` (2025 0xcf46f0 → 2026 0xd9a030, VMT
  slot 0x1b8) appears in exactly ONE 2026 VMT slot (0xd99530) → VMT_2026 0xd99360 → classRef = VMT+0x18 =
  0xd99378. Verified at 6 sibling offsets + the classRef self-check (*(0xcf3888)=0x5df850 ⟷ *(0xd99378)=
  0x619130, same method cross-version). HIGH confidence. Wired into `g_syms[]` HostClassRef row
  (ghidra[FLV_2026]=0xD99378) — fallback-only row, so on 2026 it now resolves and DoWinHostEmbed can create
  the host form. `HostClassRef` is no longer in the gap list.
- **Window UI (user request):** removed our custom on-caption minimize + maximize/restore glyph buttons
  (draw + hit-test in embedHostSubProc) — the caption keeps only FL's native close (X). The programmatic
  DoWinHostMinimize/DoWinHostMaximizeToggle + winhost_min/winhost_max commands are kept (not caption buttons).
- **Caption fix:** the embed already set caption "FL Automate" pre-show, but FL's Realign can stamp the
  class-default "configure scripts" over it during the show → added a POST-show FLwp_SetFormCaption("FL
  Automate") re-set (safe: +0x110 holds a valid UStr by then) so the titlebar reliably reads "FL Automate".
- All built + deployed (live FL 2026 install @14:09 + 8 payload copies). Live-test pending.

## ★ 2026-07-09 (day) — 2026 PAINT CRASH FIXED (one struct-field offset) → dock re-enabled on 2026
First 2026 embed with HostClassRef resolved CREATED + showed the form, then AV'd in WM_PAINT: fault
0xD9A04F = 2026 paint 0xd9a030 + 0x1F, read [NULL+0x78]. Cause: the bridge stubs TScriptDialog's
script-editor subcontrol pointer fields (so the bare form's paint/region overrides don't deref widgets it
lacks) using 2025 field offsets, and the instance layout shifted on 2026. Ghidra cross-version RE (resumed
subagent) proved the TScriptDialog layout is **byte-identical 25→26 EXCEPT ONE field**: the script-editor
subcontrol pointer moved **0x7b8 (2025) → 0x7c0 (2026)** (an 8-byte field inserted before it; instance size
0x7f8→0x7f0 as the tail also compacted). The 2026 paint derefs EXACTLY [form+0x7c0]→[+0x78]. All other
stubbed/read fields are unchanged: the other 5 subctrl slots (0x760/0x770/0x788/0x7a8/0x7b0), all 11 event
TMethod slots (0x194..0x5f4), and 0x110/0x11c/0x45c/0x2b0/0x4c2. FIX (dllmain.cpp): embedNullFormEvents
version-keys the one slot (`scriptEdOff = ver==2026 ? 0x7c0 : 0x7b8`); the DoWinHostEmbed version-guard now
allows **2025 AND 2026** (unknown versions still fail-safe → external window). Built + deployed @14:38 (live
+ 8 payloads). With this, 2026 gets the full docked window + "FL Automate" caption + native-X-only chrome.
Live-test pending. Reference 2026 addrs: paint 0xd9a030, FormShow 0xd99e30, FormCreate 0xd99c30, region
0xd9a390, classRef 0xd99378, instance size 0x7f0.


Goal: one bridge that runs across FL versions via runtime signature scanning instead of hardcoded
addresses. Built + validated against FL 2025 (25.2.5) and FL 2026 (26.1.0), both in the Ghidra project.

## DONE (built + compiling)
- **Scanner infrastructure** — `tools/bridge/sigscan.{h,cpp}`: PE-section scanner, uniqueness-enforced
  resolution (0 or >1 match = fail loudly), RIP-relative data-global resolver with **anchor+`dataDelta`**
  support (a global reachable only via a nearby anchor + fixed struct offset), version detection
  (FileVersion), the `sym:` wire token, and the fail-safe `syms` gap-report command. Additive — the
  legacy `rb()`/hex path is byte-for-byte untouched. `FlBridge.dll` builds clean (Release + Debug).
- **Symbol table populated: 92 symbols** in `g_syms[]` — **66 functions + 26 data globals** — each with a
  cross-version byte signature (or RIP-relative spec) + confirmed 2025 & 2026 addresses. Full map in
  `re/version-fn-map.md`; the machine-readable dataset is `re/version-symbols.tsv`.
- **Method: proven end-to-end.** A run_script_inline generator builds operand-wildcarded patterns from
  2025 functions (auto-extended to unique), `Memory.findBytes` matches them in 2026. RTL functions
  (UStrAsg/FreeObj/DynArraySetLength) confirmed at IDENTICAL addresses across versions (no signature
  needed). Data globals resolved by signaturing a referencing instruction + decoding the RIP-relative
  disp (self-correcting across versions); anchor+delta handles VMT/classRef/typeinfo globals.

## ALSO DONE overnight (all built + tested — 411 tests green; C++ bridge + full .NET solution both compile)
- **Version-plumbing + graceful tool availability** (C#): `IFlSymbolResolution` + `FlInjectBridge.Symbols.cs`
  query the bridge's `syms` (cached, fail-OPEN). `NativeSymbolGate` maps high-risk tools → their symbols;
  `AgentKernelBuilder.ApplyVersionGateAsync` PRUNES tools whose symbol is unresolved from the kernel (both
  subset + full-surface paths) → on any version, unmapped tools quietly disappear instead of firing a wrong
  address. Logs the version + gap set at boot. `compatibility.json` now lists 25.2.5 AND 26.1.0 (2026 hashes
  pending a verified install before re-arming FlIntegrity).
- **Window-host migration** (dllmain.cpp): 46 call sites → `symAddr("Name")`; classRef bug fixed
  (flCreateForm takes a resolved classRef, DoWinHostEmbed fails cleanly on a null/unmapped one); a fail-safe
  `HostClassRef` fallback row keeps 2025 working. Struct offsets left as version-keyed + SEH/sanity-guarded.
- **★ Readiness gate migrated** (the last blocker for 2026): `sig_resolveAll()` now runs at the TOP of
  `handleCmd` (independent of the gate — breaks the chicken-and-egg), and `flIsReady()`/`derefReady()`
  resolve their 4 globals via `symAddr` (MainFormPtr/ToolbarFormPtr/ReadySongObj/ChannelList — ReadySongObj
  0x14a9f40→0x15da278 resolved + added). On 2025 byte-identical to the old gate; logs a missing symbol
  instead of hanging. Without this the whole bridge would have stayed gated-off on 2026.

## FINAL STATE: ~94 table rows (66 functions + 27 data globals cross-version-resolved + a HostClassRef fallback).
Everything builds; core test suites all green. NOT YET live-verified in a running FL (bridge not loadable
tonight) — that's step 1 below.

## REMAINING GAPS (fail-safe: these tools go UNAVAILABLE on a version where their symbol is unresolved — never crash)
Function stragglers (~9, mostly chat-tab widgets + a few setters whose opening code changed 25→26):
TQuickEdit_SetText/Refresh, FLbrz_SelectTabById, FLmx_SetRouteActive, FLpl_RepaintPlaylist,
FLpr_SaveProjectToFlp, FLui_MarkKbCapture, FLar_GetCount, FLpl_SetClipMuted. Data globals: **HostClassRef**
(TScriptDialog metaclass — gates the window EMBED), SongArrangement, LoadingFlag.

### Note on the 9 straggler functions (verified 2026-07-09, ~02:08)
All 9 resist STATIC cross-version signatures: byte-sig fails (opening code changed 25→26), no C-string
anchors (FL strings are Delphi length-prefixed — ASCII findBytes misses them), and their pointer slots
sit in relocation-style tables (odd alignment, no already-mapped neighbors to anchor on). They are the
correct set to leave fail-safe (chat-tab widgets + a few setters; none gate the window). To finish them,
the practical route is RUNTIME discovery: with the bridge live in FL 2026, resolve each by
decompiling/inspecting the 2026 engine directly (find the function by behavior) and add a per-version
fallback (ghidra[2026] hardcoded), OR handle Delphi-string anchoring (match the length-prefixed bytes).

### Recovery recipes (to hit 100% later — the tooling is all in place)
- **VMT-slot recovery** (best for the TQuickEdit methods + any class method): the class VMT is mapped
  (QuickEditVMT 0x7466b8→0x7661e8). Find the method's slot offset in the 2025 VMT, read the same offset
  in the 2026 VMT → 2026 address. Add as a per-version fallback row (ghidra[2025]+[2026], no pattern).
- **HostClassRef**: the TObject VMT prefix is shared (8 classes), so disambiguate via a TScriptDialog-
  UNIQUE override (FormCreate 0xcf42c0 / FormShow 0xcf44c0 / paint 0xcf46f0): map one, find its unique
  pointer slot in 2026, subtract the slot offset → 2026 VMT → +0x18 = classRef. OR resolve at runtime
  once FL 2026 is live (debugger). classRef 2025 = 0xcf3888; *(0xcf3888)=method@0x5df850.
- **String-anchor**: for functions referencing UI text — note FL strings are Delphi (length-prefixed /
  sometimes UTF-16), so match on the RAW bytes not a C string (my ASCII findBytes missed "untitled.flp").
- **Per-version fallback**: for any function whose code changed too much for ONE cross-version pattern,
  give it BOTH hardcoded addresses (ghidra[2025], ghidra[2026]) and no pattern — the scanner uses the
  detected-version fallback. Works on both known versions (just not future-portable).

## HOW TO VALIDATE / CONTINUE
1. **Live-verify** (the real proof): with the bridge loaded in FL 2025 AND FL 2026, run the `syms`
   command — it reports resolved/unresolved per version. All 92 should resolve; unresolved = a signature
   whose runtime bytes differ from the static image (shouldn't happen — the engine isn't packed).
2. **Window in 2026**: after the migration subagent + resolving HostClassRef, test the AI window embeds.
3. **Migrate the rest of the call sites** (transport/patterns/channels/mixer/playlist `CallAsync("hex")`
   in `src/FruityLink.FlStudio/Inject/*.cs` + `dllmain.cpp`) to `sym:` incrementally — each is a one-token
   edit, fail-safe if unresolved. The C# transport already forwards `sym:` tokens unchanged.
4. **Add a version**: import the engine into Ghidra, re-run the generator scripts (they're the batch
   engine), add the ghidra[] column. Architecture doc: `re/version-portability-design.md`.

## KEY ARTIFACTS
`re/version-fn-map.md` (human map) · `re/version-symbols.tsv` (machine dataset) ·
`re/version-portability-design.md` (architecture) · `re/window-host-2026-plan.md` (window fix plan) ·
`tools/bridge/sigscan.{h,cpp}` (the scanner) · Ghidra: project "FlStudio" pid 1832, programs
`/FLEngine_x64.dll` (2025) + `/FL2026/FLEngine_x64.dll` (2026), `run_script_inline` enabled.
