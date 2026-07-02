# 17 — DRM Guard: protect FL Studio's licensing from third-party plugins

**Status:** research + protection design. **Strictly DEFENSIVE.** We support Image-Line. This
document locates where FL's DRM/licensing lives **only so we can GUARD it** — detect and refuse
any attempt by a third-party plugin (or our own SDK) to tamper with it — and so we can **refuse
to run on an already-cracked FL** (anti-piracy). It deliberately contains **NO bypass, crack,
patch, or "flip this bit" content.** Where analysis revealed the exact edit that would defeat a
check, that edit is **not recorded** — only the region/role needed to protect it.

> Standing project rule (unchanged): pure interop, never touch FL's DRM/licensing. This doc
> extends that rule to **policing third-party plugins** and to **declining to operate on pirated FL.**

**Method:** static Ghidra only (`program="FLEngine_x64.dll"`, image base `0x400000`). No FL
runtime, no debugger, no edits to FL. Sibling agents own FL-runtime testing and `tools/bridge/*`
— this doc only *describes* the changes there.

**Address convention.** Ghidra addresses use image base `0x400000`. FLEngine rebases high under
ASLR. The runtime mapping (already used by the bridge) is:

```
runtime_addr = flEngineBase + (ghidra_addr - 0x400000)
RVA          = ghidra_addr - 0x400000        (module-relative; ASLR-independent)
```

All guarded regions below are expressed as **RVAs** so they are stable across runs and easy to
bake into a denylist; the runtime address is computed at check time from the live module base
(`bridge info` already reports `flEngineBase`).

---

## 0. TL;DR

- **DRM lives in plain `.text`, not the VMProtect VM.** FL's license-state logic, online
  activation, machine-ID/serial handling and trial gating are ordinary native functions in
  `.text` (RVA `0x1000–0xE967FF`). The VMProtect-virtualized sections `.avm0–.avm3`
  (RVA `0x175C000–0x344ADFF`) protect a *different* subset. So the licensing code is directly
  readable **and directly patchable** → it is exactly what an in-process plugin could target,
  and exactly what our guard must protect.
- **Two core units.** (a) the **license-state unit** (`il.LicenseIntf` / `TILLicenseIntf`):
  validity/renewal check + the refcounted license singleton; (b) the **registration/activation
  unit** (account / serial / machine-ID / online unlock). Plus the `gui.UnlockWindow` UI layer.
- **Authenticode is NOT in FLEngine.** FLEngine imports no WINTRUST/CRYPT32. The
  signature/self-verification path is in **`FL64.exe`** (which imports both) → **FL64.exe needs
  separate static analysis**; it is not loaded in Ghidra here.
- **Guard architecture** = three layers: (1) a **capability boundary** — untrusted plugins never
  receive raw memory-write / arbitrary-call primitives; (2) a **DRM-region denylist** — the
  bridge write/call dispatch refuses any op targeting a guarded region or installing a hook on a
  licensing function; (3) **integrity monitoring** — periodic checksums + hook/`VirtualProtect`
  detection on the DRM regions, fail-safe.
- **Anti-piracy add-on** — at startup (and periodically) we verify FL's DRM regions are intact;
  if FL is **already cracked**, our system **fails inert**: it loads nothing, unwinds cleanly,
  and leaves FL running normally.

---

## 1. Where DRM/licensing lives (guard-level map)

All addresses are Ghidra (base `0x400000`); RVA in parentheses. Names with `FUN_` are
Ghidra-default; `T*.Method` are Delphi-RTTI-recovered. **These are the regions to PROTECT.**

### 1a. License-state unit (`il.LicenseIntf` / `TILLicenseIntf`)

Class-name strings: `TILLicenseIntf` @ `0x00895EC7`, `il.LicenseIntf` @ `0x00895F03`.

| Function / datum | Ghidra | RVA | Role (guard-level) |
|---|---|---|---|
| License validity check | `FUN_0088F930` | `0x48F930` | Returns a status char ("needs renew" etc.); reads the license singleton. Body `0x0088F930–0x0088FB66`. The "is this license still good?" predicate. |
| License renewal / online re-validation poll | `FUN_00890040` | `0x490040` | Network-backed validation loop (Sleep+backoff, HTTP via `FUN_00613EF0`, checks HTTP 200/401), emits "License should be renewed". Body `0x00890040–0x00890294`. |
| Renewal result handlers | `FUN_0088FD60`, `FUN_0088FE30` | `0x48FD60`, `0x48FE30` | Apply the server's verdict to the license state. |
| License singleton refcount holder | `FUN_0088D260` | `0x48D260` | Manages the refcount of the global license object. |
| **License singleton (global)** | `DAT_014BDDC0` | `0x10BDDC0` | `.bss` pointer to the live license-interface object. **Refcount** at `DAT_014BDDC8` (`0x10BDDC8`). Read by the validity check + renewal poll. This is the **runtime root of "registered vs trial"**. |

Note: the *state object* that `DAT_014BDDC0` points at is a **heap object** (its fields hold the
registered/trial/unlocked state). Because it is heap-allocated, its address is dynamic and cannot
go in a static denylist — so the guard protects the **code that computes/reads that state**
(above) and treats writes *to the singleton pointer itself* as tampering, rather than trying to
police the heap object's bytes. (Deliberately not recording which field value means "registered".)

### 1b. Registration / activation / machine-ID unit

| Function / string | Ghidra | RVA | Role (guard-level) |
|---|---|---|---|
| Online unlock request builder | `FUN_009E21A0` | `0x5E21A0` | Builds the `regfile_unlock.php?username&password&checksum&flags&id&v&dn` request. Body `0x009E21A0–0x009E30FD`. The account-unlock network path. |
| Serial → license URL builder | `FUN_009E8CC0` | `0x5E8CC0` | Builds `support.image-line.com/action/license/serial?serial=%s`. |
| License-state → analytics suffix | `FUN_009E4C70` | `0x5E4C70` | Reads the license-state object to emit `utm_flunlocked`/Trial/Unlocked; demonstrates the state fields exist (their semantics intentionally not recorded). |
| Trial-account creation | near `0x009E3504` (`CreateTrialAccount`) | `0x5E3504` | Trial provisioning. |
| Activation/machine-ID strings | `0x009E3538`, `0x009E8C8C` (`?machineid=%s&v=%s`), `0x009EA154` (`machineId`) | — | Machine-ID is sent to the activation server. |

Online endpoints referenced (for context, all `support.image-line.com`): `regfile_unlock.php`,
`action/license/serial`, `member/licensebeforeshop.php`, `?machineid=…`.

### 1c. Machine fingerprint / on-disk + registry license store

| String | Ghidra | Role |
|---|---|---|
| `ILRegKey` | `0x008453C0`, `0x00847BF4`, `0x00DEEF30`, `0x00FCD21C`, … (many) | The license **registry/file key** name; read in many places to fetch the reg key. |
| `"ilregkey"` | `0x00DEC608` | Used by the load-license-file handler. |
| `DiskSerial` | `0x008DF31C` | Volume serial → machine fingerprint. |
| `System\Hardware specific\`, `Hardware` | `0x009203D0`, `0x009203B0` | Registry path for the hardware-specific machine fingerprint. |
| `licensevalidation.txt` | `0x00DED630` | Offline validation file (written by the unlock window). |
| `License\`, `CopyLicenseToAllUsers.exe`, `RemoveLicenses\`, `RemoveLicenses.exe` | `0x00DC35C4`… | License install/remove helper tooling. |

Registry APIs are imported (`RegOpenKeyExW`/`RegQueryValueExW`/`RegSetValueExW`/…), and
`GetVolumeInformationW` + `GetComputerNameExW` provide the fingerprint inputs.

### 1d. UI layer — `gui.UnlockWindow` (lower guard priority — UI, not the verdict)

`TUnlockForm.*` (~`0x00DEB000–0x00DF3000`) and `TWelcomeUnlockForm.*` (~`0x01040000–0x01041800`):
`FileUnlockLoadLicenseBtnClick`, `UnlockWithAccountBtnClick`, `RegisterSerialBtnClick`,
`SendVerificationCodeBtnClick`, `ContinueTrialBtnClick`, `UpdateLicenseBtnClick`, etc. These call
into the units above. They are the human entry points; guard them lightly (they are not the
trust verdict) but include the click-handlers' *targets* (1a/1b) in the denylist.

### 1e. Trial-plugin gating (separate from app licensing)

Strings around `0x00B74A84–0x00B790E0` (`RegOfTrial`, `DemoPluginCannotBeSaved`,
"…is in |trial| mode", "…will be |locked|…") gate **demo plugins inside projects**. Same defensive
treatment: our SDK must never be usable to flip a plugin's trial/locked state.

### 1f. Anti-tamper layer

- **VMProtect** virtualizes `.avm0–.avm3` (RVA `0x175C000–0x344ADFF`, ~30 MB of VM bytecode).
  The licensing logic in §1a/§1b is **outside** these sections (plain `.text`). VMProtect's
  exception-based machinery is visible via imports `AddVectoredExceptionHandler` (`0x13F`) and
  `UnhandledExceptionFilter` (`0x15C`).
- **Anti-debug:** `IsDebuggerPresent` (import `0x119`).
- **Memory-permission / introspection APIs FL itself uses** (and that a plugin would abuse):
  `VirtualProtect` (`0x114`), `VirtualQuery`/`VirtualQueryEx` (`0x15E`/`0x160`), `VirtualAlloc`,
  `IsBadWritePtr`.
- **Authenticode:** **not in FLEngine** (no WINTRUST/CRYPT32 imports). Per `re/15`, FL64.exe
  imports WINTRUST + CRYPT32 and verifies FLEngine. **→ FL64.exe must be analyzed separately**
  to locate the signature self-check; treat that as a TODO, not a guess.

---

## 2. Threat model — how a plugin could tamper

The SDK/bridge today exposes raw primitives (over the pipe **and**, in the future in-process,
via the `FlBridge_Command` export): `peek/peekabs`, **`poke/pokeabs`** (SEH + `VirtualProtect`
write), **`call/callabs/callhere`** and **`callf/callfabs`** (arbitrary native call on FL's main
thread, GP + XMM args). The bridge itself already demonstrates every dangerous technique a
malicious plugin would use (it installs inline hooks and patches vtbl slots for the chat tab) —
so we know exactly what to police:

| # | Technique | Concretely (what to detect) |
|---|---|---|
| T1 | **Patch/NOP a licensing function** | `VirtualProtect`→write over `FUN_0088F930` / `FUN_00890040` / activation funcs → the verdict is forced. Detect: writes landing in a guarded RVA range; post-hoc checksum drift. |
| T2 | **Inline hook / detour** | Overwrite a licensing func's prologue with `mov rax,imm64; jmp rax` (the exact pattern the bridge uses for FormKeyDown/FormShortCut) or a `jmp rel32`, redirecting to plugin code that returns a forged result. Detect: first-bytes changed vs baseline; prologue is a jump into non-FLEngine memory. |
| T2b | **Trampoline / mid-function patch** | Same idea away from the prologue. Detect: any write into a guarded code range; whole-region checksum. |
| T3 | **IAT / vtable hook** | Redirect the license object's vtable slots, or hook imports the DRM uses (`Reg*`, `GetVolumeInformationW`, the HTTP path) to feed fake activation/registry/file data. Detect: license-object vtable pointers leaving FLEngine's `.rdata`/`.text`; IAT entries pointing outside their owning module. |
| T4 | **Redirect the singleton** | Overwrite `DAT_014BDDC0` to point at a plugin-built fake license object. Detect: the singleton pointer leaving FLEngine's heap/expected module, or changing outside the legitimate init path. |
| T5 | **Forge activation responses** | Hook/MITM the network or the result handlers (`FUN_0088FD60/FE30`) so "renew/unlock" always succeeds. Detect: hooks on those handlers (T2/T3). (Network MITM is out of our process scope — note only.) |
| T6 | **Edit license files / registry** | Write fake `ILRegKey` / `licensevalidation.txt` / hardware-fingerprint registry values. Our SDK must offer **no** file/registry primitive that can reach these; detect attempts via the file/registry path allowlist (§3). |
| T7 | **Neuter the anti-tamper itself** | Patch VMProtect integrity routines / the `IsDebuggerPresent` check / the Authenticode path. Detect (in-process, partial): checksum drift on the anti-tamper code that lives in FLEngine; the FL64.exe Authenticode path needs the separate analysis (§1f). |
| T8 | **Use OUR primitives as the weapon** | The single biggest risk: a plugin simply asks the bridge to `poke`/`callabs` the licensing region. **Mitigation is the capability boundary (§3.1) — untrusted plugins never get these verbs.** |

**In-process is the worst case.** Once plugins run in-process under the CLR host
(`re/clr-loader-prebuilt`), .NET code shares FL's address space and can call
`VirtualProtect`/`memcpy`/`Marshal.*` directly **without** the bridge. AppDomains are gone in
.NET Core and `AssemblyLoadContext` provides **no memory isolation** — it only isolates assembly
resolution. So in-process the guard cannot rely on "don't expose the verb"; it must add
**integrity monitoring** (§3.3) and load-time **plugin vetting** (§4.3) as the real backstops.

---

## 3. Guard + detection design

### 3.1 Capability model (trust boundary)

Two tiers:

- **Trusted core** (our code: bridge, C# SDK internals, MCP server host). May use raw primitives
  internally to implement safe high-level ops.
- **Untrusted third-party plugins.** Receive **only** the high-level, safe music-control SDK
  (`INativeFlControl` + the MCP tools) — never raw memory/call primitives.

**Rules:**

1. `poke`, `pokeabs`, `call`, `callabs`, `callhere`, `callf`, `callfabs`, `peek`, `peekabs`,
   `scratch`, and the raw `FlBridge_Command` export are **internal-only**. They are *not*
   surfaced on any interface a plugin can reach. Plugins see `INativeFlControl` (typed ops:
   tempo, notes, mixer, …) and nothing that takes an address.
2. The safe SDK ops are implemented in trusted code that builds the bridge commands itself; the
   plugin supplies only musical parameters (BPM, key, track index), never addresses or bytes.
3. **No "escape hatch."** No `ExecuteRaw(string)`, no generic `Call(addr,args)`, no
   `ReadMemory/WriteMemory` on any plugin-facing surface. (Audit: today `FlInjectBridge` exposes
   `CallAsync`/`PeekAsync`/raw `RawAsync` as *public* — see §4.2; these must become non-plugin-
   reachable.)
4. The capability check is **enforced at the boundary the plugin actually crosses** (the C# SDK
   surface and, in-process, the CLR host's exposed object graph), not merely documented.

This alone defeats T8 for the **cross-process** bridge model. In-process it is **necessary but
not sufficient** (a plugin can P/Invoke `VirtualProtect` itself) → §3.3/§4.3.

### 3.2 DRM-region denylist (defense-in-depth at the dispatch layer)

Even though trusted code is the only caller of the raw verbs, the bridge's `poke`/`call`
dispatch should **refuse** ops that target DRM — so a bug in our own higher layer, or a future
mis-wiring, can never be turned into a DRM write. This is a cheap, central, fail-safe check.

**Guarded set (RVA ranges + singletons), version-keyed:**

```
# Code regions to refuse writes-to and hooks-on (RVAs; pad each function with a margin):
LICENSE_VALIDITY      0x48F930 .. 0x48FB66   # FUN_0088F930
LICENSE_RENEW_POLL    0x490040 .. 0x490294   # FUN_00890040
LICENSE_RESULT_A/B    0x48FD60 .. 0x48FE..   # FUN_0088FD60 / FUN_0088FE30
LICENSE_REFCOUNT      0x48D260 .. 0x48D2..   # FUN_0088D260
ACTIVATION_UNLOCK     0x5E21A0 .. 0x5E30FD   # FUN_009E21A0
ACTIVATION_SERIAL     0x5E8CC0 .. 0x5E8D..   # FUN_009E8CC0
ACTIVATION_STATE      0x5E4C70 .. 0x5E4D..   # FUN_009E4C70
UNLOCK_UI             0x9EB000 .. 0x9F3000   # TUnlockForm.* (handler targets)
# Data:
LICENSE_SINGLETON     0x10BDDC0 (+8 refcount) # DAT_014BDDC0 / DAT_014BDDC8 — refuse writes
```

**How ranges are expressed & checked:**

- Stored as **module-relative RVAs**. At check time the dispatch computes the guarded runtime
  span `flEngineBase + rva` and tests the target against it.
- **Write guard (`poke/pokeabs`):** if `[dst, dst+len)` intersects any guarded code range **or**
  the singleton datum → refuse (return an error; do not write). Covers T1/T2b/T4.
- **Call guard (`call*`):** refuse a *call target* inside a guarded range only if it would be
  abused as a write primitive; more important is to refuse calls to generic memory-mutators
  (`VirtualProtect`, `WriteProcessMemory`, `memcpy`-like) **with a guarded destination argument**.
  Since the bridge can't fully model every callee's semantics, the high-value, tractable check is:
  **refuse `callabs`/`callfabs` to `VirtualProtect`/`VirtualProtectEx`/`VirtualAlloc`(RWX) when an
  argument points into a guarded range.**
- **Hook guard:** before/after any op, verify the first N bytes (e.g. 16) of each guarded
  function match the baseline (§3.3). A changed prologue = an inline hook (T2). Also verify each
  guarded function's prologue is **not** a `jmp`/`mov rax,imm64;jmp rax` into non-FLEngine memory.
- The denylist is **conservative/superset**: pad each function range and include the whole
  license/activation clusters. Over-refusing a DRM write is always correct (we never write there).

This is *defense in depth*, not the primary control — §3.1 is. But it makes "accidentally write
to DRM" structurally impossible at the chokepoint.

### 3.3 Integrity monitoring (detect post-hoc tampering, incl. in-process)

A background monitor (trusted, in the bridge or CLR host) periodically verifies the DRM regions
are pristine. This is the backstop that survives the in-process model (where a plugin can write
memory without the bridge).

**What is checked:**

1. **Region checksums.** Hash (e.g. SHA-256 or a fast CRC + occasional SHA) the guarded **code**
   ranges (§3.2) and compare to the **version-keyed baseline** (§5). Drift ⇒ the code was
   patched (T1/T2b/T7).
2. **Prologue/hook check.** For each guarded function, compare the first 16 bytes to baseline and
   confirm the entry isn't a detour jump into a foreign module (T2). Confirm the function still
   resides in FLEngine's executable range.
3. **Vtable/singleton check.** Confirm `DAT_014BDDC0` points into FLEngine's expected heap/region
   and that the license object's vtable slots point into FLEngine `.text`/`.rdata` (T3/T4).
4. **`VirtualProtect`-on-DRM watch.** Periodically `VirtualQuery` the guarded code pages; if any
   guarded page is **writable** (`PAGE_*WRITE*`/`*EXECUTE_*WRITE*`) when it should be
   `EXECUTE_READ`, that's a strong tamper signal (someone made it writable to patch it — T1/T7).
5. **Import/IAT spot-check.** Confirm the DRM-relevant imports (`Reg*`, `GetVolumeInformationW`,
   the HTTP path) resolve to their owning system modules, not to plugin code (T3).

**Cadence:** once at startup (gate, §6), then on a low-frequency timer (e.g. every few seconds)
and opportunistically before each *trusted* DRM-adjacent operation. Keep it cheap: CRC the bulk,
SHA only on mismatch to confirm.

**Scope honesty:** in-process, a determined plugin running before/with equal privilege can race
or disable an in-process monitor — this is anti-piracy **hygiene**, not unbreakable DRM (§7,
§9.4). The monitor raises the bar and catches the common cases (lazy patches, off-the-shelf
loaders, our own bugs); it is not a guarantee.

### 3.4 Response (fail-safe)

On any positive detection from §3.2/§3.3:

1. **Refuse the operation** (for a denylist hit) — return an error, write nothing, call nothing.
2. **Log** to the bridge log + an audit sink (`IOperationAuditSink` already exists in Core) with
   the region, op, and offending plugin id.
3. **Flag/disable the offending plugin** — the CLR host marks it untrusted and stops dispatching
   to it; the plugin manager surfaces it as disabled.
4. **Fail safe, never fail open.** If the guard can't determine safety (can't read a region, base
   unknown, baseline missing for this FL version) → **refuse**, don't allow. Better to decline a
   legitimate op than to permit a DRM write.

---

## 4. Implementation plan (where each guard physically lives)

> Describes changes; does **not** edit `tools/bridge/*` (owned by another agent) or any source.

### 4.1 Bridge `poke`/`call` dispatch — `tools/bridge/dllmain.cpp`

The chokepoint. In `handleCmd`, the `poke/pokeabs` and `call/callabs/callfabs` branches
(currently ~L776 and ~L743) should consult a guard before acting:

- Add a static, version-keyed table of guarded **RVA ranges** + the singleton RVA (§3.2). Compute
  guarded runtime spans from `GetModuleHandleA("FLEngine_x64.dll")` (the bridge already rebases
  via `rb()`/`resolveAddr()`).
- **`poke`/`pokeabs`:** before `safeWrite`, reject if `[real, real+len)` intersects any guarded
  span (code ranges + singleton). Return e.g. `{"ok":0,"err":"guarded-region"}`.
- **`call`/`callabs`/`callfabs`:** if the resolved target is `VirtualProtect`/`VirtualProtectEx`/
  `VirtualAlloc` (resolve their addresses once) and any arg points into a guarded span → reject.
  (General callee modeling is infeasible; this catches the make-writable step.)
- Add a `guard_status` command: returns the current checksum/verify result for monitoring/tests.
- Keep this **internal** — see 4.2 for why the verbs must not be plugin-reachable regardless.
- The bridge already uses `VirtualProtect`+prologue patching for its chat-tab hooks (`installKey
  DownHook`/`installShortcutHook`/vtbl slot at `+0xd0`). The integrity monitor must **whitelist
  the bridge's own known hooks** (FormKeyDown `0x10c9920`, FormShortCut `0x114de10`, browser
  vtbl[0xd0]) so they aren't flagged — and those targets are **not** in the DRM set, so no
  conflict. (Going forward, prefer to keep our hooks far from DRM and document them.)

### 4.2 C# SDK boundary

- **`INativeFlControl`** (`src/FruityLink.Core/Abstractions/INativeFlControl.cs`) is the correct
  plugin-facing surface — it is **all typed, no addresses**. Keep it as the only thing plugins
  see. Good as-is.
- **`FlInjectBridge`** (`src/FruityLink.FlStudio/Inject/FlInjectBridge.cs`) currently exposes
  **public** `RawAsync(string)`, `CallAsync(ghidraHexAddr,…)`, `PeekAsync(addr,…)` (and the class
  holds the command map). **Action:** these raw members must not be reachable from plugin code.
  Options: make them `internal` (+ `InternalsVisibleTo` only our trusted assemblies), or move the
  raw transport into an internal type and have plugins depend solely on `INativeFlControl`. The
  goal: **no plugin assembly can bind to a method that takes an address or raw command string.**
- **`InProcBridge`** (`.../Inject/InProcBridge.cs`) P/Invokes `FlBridge_Command` directly — this
  is the in-process raw door. Mark `internal`; never hand it to plugins.
- **MCP server** (`src/FruityLink.Mcp`, per `business-model-plugin-system`): re-expose **only**
  the safe `INativeFlControl`/`NativeControlPlugin` tools. Do **not** add an MCP tool that takes a
  memory address or raw command — that would re-open T8 to any MCP client.

### 4.3 CLR host / plugin loader (the in-process, higher-risk surface)

This is where in-process plugins are loaded (`re/clr-loader-prebuilt`,
`parallel-build-jobs-2026-06-30`). Add capability **gating + vetting** here:

- **Capability gating:** construct each plugin with only the safe SDK services in its DI graph;
  never inject `FlInjectBridge`'s raw type, `InProcBridge`, or anything address-taking.
- **No raw P/Invoke smuggling (best-effort):** plugins are open-source C# we curate, so the
  primary control is **review + signing of curated plugins**. For defense in depth, consider
  loading third-party plugins into a dedicated `AssemblyLoadContext` and (optionally) a separate
  low-privilege helper process for *fully* untrusted code — acknowledging ALC gives **no** memory
  isolation, so a malicious in-proc plugin can still P/Invoke. Treat ALC as resolution hygiene,
  not a security boundary; the real backstops are curation + the integrity monitor (§3.3) + the
  fail-inert gate (§6).
- **Start the integrity monitor** (§3.3) from the host as a trusted background service, before any
  third-party plugin is loaded.
- **Honor responses (§3.4):** the host owns "disable offending plugin."

### 4.4 Region-table maintenance

Keep the guarded RVA table + baselines (§5) in a single version-keyed data file the bridge and
host both read. Update per FL release (§5). One source of truth; no addresses hard-coded in logic.

---

## 5. Known-good baselines (version-keyed, no cracker aid)

Baselines are about **integrity only** — region hashes — never about "where the yes/no bit is."
Shipping a SHA-256 of a code region does **not** tell a cracker what to change; it only lets us
notice that *something* changed.

- **Per-FL-version manifest.** Key by FL's build string (e.g. `25.2.5.5319`). For each version
  store: FLEngine size/timestamp, the guarded RVA ranges, and a hash per range (+ first-16-byte
  prologue hashes for the hook check). FL build is discoverable from the `version.dll`
  `GetFileVersionInfo*` data FLEngine already imports, or FL64.exe's version resource.
- **How baselines are generated.** On a **known-good, legitimately-licensed** FL install, our
  trusted tooling reads the on-disk PE (or the in-memory clean image at first load before any
  plugin runs) and records the hashes. No bypass data is produced or stored — only digests.
- **Cross-release updates.** New FL version ⇒ regenerate the manifest from a clean install and
  ship it. Until a version is baselined, the guard **fails safe**: it won't *write* to DRM
  regardless (denylist is structural), and the anti-piracy gate (§6) treats "unknown version" as
  *don't-know* → it may run inert or run in a reduced "no baseline" mode (policy choice in §6.4),
  but never asserts "cracked" on an unbaselined build (false-positive control).
- **False-positive sources to respect:** legit FL updates/hotfixes change bytes (→ version-aware
  baselines), ASLR (→ always RVA-relative, never absolute), relocations applied at load (→ hash
  the on-disk-equivalent or mask reloc'd bytes), and FL's **own** legitimate self-modification if
  any (→ exclude such regions from the baseline). The DRM functions in §1 are ordinary `.text`
  and are not expected to self-modify, which is what makes them baseline-able.

---

## 6. Pre-existing tamper detection → fail-inert shutdown (anti-piracy)

**Goal:** if FL's DRM has **already** been patched/cracked *before* we load, our system does
**nothing** — it refuses to start, unwinds cleanly, and leaves FL running exactly as it was. We do
not operate on pirated FL. This is separate from §2–§4 (which stop *our* plugins from tampering);
here we *decline to run at all* on an already-tampered host.

### 6.1 Detection (startup + periodic) — guard-level, no crack content

Run the integrity checks of §3.3 against the §5 baseline **before loading anything**, and infer
"already cracked" from these signals (each a *defensive observation*, none a how-to):

1. **Code integrity mismatch** on the licensing/anti-tamper regions (§1a/§1b/§1f) vs the
   version-keyed baseline — bytes differ from a clean install of this exact FL build.
2. **Inline hooks/detours already present** on licensing funcs (prologue is a jump into foreign
   memory; first-bytes differ from baseline) at our load time.
3. **Guarded code pages already writable** (`VirtualQuery` shows RWX/`*WRITE*` on §3.2 code) — a
   patcher made them writable.
4. **Forced/constant verdicts:** the validity/result functions (§1a) have been replaced by stubs
   (e.g. prologue is an immediate `ret`/`mov al,imm;ret` shape, or the function body hash differs
   to a trivial size) — detect as an integrity/shape mismatch, **without** recording the specific
   patch. (We check "does it match known-good," not "what does a crack look like.")
5. **Neutered anti-tamper:** VMProtect entry/exception machinery or the FLEngine-side anti-debug
   differs from baseline. (The Authenticode self-check lives in **FL64.exe** — once that module is
   analyzed (§1f), add: FL64's verify path patched / its import to WINTRUST redirected.)
6. **Tampered license artifacts:** `ILRegKey` registry value or `licensevalidation.txt` present
   in shapes inconsistent with a genuine activation (checked at the **integrity** level — presence
   of known patcher fingerprints / structurally invalid blobs — never "what a valid one contains").
7. **Generic loader/emulator present:** a well-known crack loader module mapped in-process, an
   unexpected module shadowing FLEngine, or a foreign module owning the DRM IAT entries.

**False-positive discipline (critical — we must not break legit users):**

- Only assert "cracked" on a **baselined** FL version (§5). Unknown version ⇒ never assert cracked
  (§6.4).
- Require **corroboration**: prefer ≥2 independent signals (e.g. checksum drift *and* a writable
  DRM page, or a detour *and* a loader module) before declaring tamper, to avoid acting on a
  single anomaly (AV instrumentation, legit Image-Line hotfix, our own whitelisted hooks).
- **Whitelist our own hooks** (§4.1) and known-benign instrumentation.
- Genuine activation differences (online vs file unlock, machine-ID variance) must **not** read as
  tamper — we check code/region integrity and known-bad fingerprints, not the user's license value.

### 6.2 Fail-inert shutdown — "zero effect"

On a corroborated positive (§6.1), our software **does nothing**:

- The proxy / CLR host **does not load** the plugin system, the bridge, the CLR app, or the MCP
  server. Nothing of ours initializes.
- It **unwinds cleanly**: any partial init is rolled back; no thread is left running; no hook is
  installed; no window subclass; the bridge's named pipe is never created; `FlBridge_Command` is
  never wired in-process.
- **FL keeps running normally and untouched.** We must never crash, hang, or degrade a user's FL
  even when we refuse to run — including if the "crack" signal is a false positive on their
  machine. Refusing = *we* are absent, *FL* is unaffected.
- **No revealing nag.** Fail **silent/inert**. Do not pop a "piracy detected" dialog or log a
  message that tells a cracker which check fired (that just teaches them what to neutralize).
  Optionally a single, generic, non-specific log line behind a debug flag for our own support.

### 6.3 Where the gate lives in the bootstrap chain

- **Earliest: the proxy DLL** (`re/15`, `version.dll`). Its **worker thread** (off the loader
  lock — never in `DllMain`) runs the §6.1 gate **before** it `LoadLibrary`s our bridge or boots
  the CLR. If the gate says "tampered," the worker simply **returns without loading anything** —
  the proxy still forwards all 15 `version.dll` exports normally, so FL is fully functional and
  none the wiser. This is the ideal choke point: it precedes every piece of our stack.
- **Second: the CLR host**, immediately after the runtime starts and **before** any plugin/app
  assembly loads — re-run the gate; on positive, host exits inert (no app, no plugins).
- **Periodic re-check:** the integrity monitor (§3.3) keeps running; if FL is cracked *after* our
  inert-safe start (e.g. a patch applied live), the monitor triggers the same fail-inert path —
  unload our plugin system / stop dispatching, cleanly, leaving FL running.
- **Ordering:** gate → (clean) load bridge → start monitor → (clean) load CLR app/plugins. Any
  stage's failure ⇒ inert, FL untouched.

### 6.4 Unknown / unbaselined FL version

If FL's build isn't in our manifest (§5) we **cannot** distinguish cracked from "just newer."
Policy: **do not assert cracked**; instead either (a) run in a degraded mode that still refuses
DRM writes structurally (denylist is version-independent for the *ranges we do know*, but ranges
shift across versions → conservative) or (b) **run inert until a baseline ships** — the safer,
recommended default for anti-piracy until the new version is baselined. Choose per release; never
let "unknown" become "assert cracked" (false-positive) **or** "assume clean and write to DRM."

### 6.5 Tamper-resistance of the check itself (proportionate)

So a cracked-FL user can't trivially knock out just our detector — kept proportionate (hygiene,
not an arms race):

- **Don't centralize the verdict in one flag.** Recompute integrity at multiple sites (proxy
  worker, CLR host start, periodic monitor) so removing one site doesn't blind the others.
- **Compare to hashes, not to a single boolean.** There's no "isCracked = false" global to flip;
  each site independently derives the result from region digests.
- **Keep baselines as digests** (§5) — even reading our manifest tells a cracker nothing about
  where to patch FL.
- **Self-consistency:** the monitor can verify *its own* code region hasn't been patched (the same
  technique we apply to FL), and run the bulk hash in trusted code, not in a plugin-reachable path.
- **Accept the ceiling:** an attacker with full local control of an already-cracked machine can
  eventually disable any in-process check. Our objective is to **not knowingly run on pirated FL**
  and to make casual circumvention non-trivial — not to ship unbreakable DRM (we explicitly do not
  weaken or attack FL's; §7).

---

## 7. Explicit non-goals / safety boundary

- **No bypass content anywhere.** This doc records regions/roles to **protect**, never the
  edits/bytes/values that would defeat a check. Where analysis surfaced such detail, it was
  withheld by design (e.g. the exact license-state field semantics in §1a).
- We **never** disable, weaken, NOP, or patch any FL DRM/anti-tamper routine. The guard only
  **reads** (hashes/queries) DRM regions and **refuses** our own ops against them.
- We do not attack, MITM, or spoof Image-Line's activation servers.
- We do not store or transmit anything that helps crack FL.

---

## 8. FL64.exe follow-up (separate analysis required)

FLEngine has **no WINTRUST/CRYPT32** imports → the **Authenticode/signature self-verification is
in FL64.exe**, which is not loaded in Ghidra here. To complete §1f/§6.1(5), a separate static
pass on `FL64.exe` should locate (guard-level only): the WINTRUST/CRYPT32 call sites that verify
FLEngine, the loader's signature-check result handling, and any "verify every module" sweep
(risk #3 in `re/15`). Until then, treat FL64's anti-tamper as *known to exist, not yet mapped* —
do not guess its addresses.

---

## 9. Confidence & risks

1. **DRM location (high).** The license-state and activation units are unambiguous from strings +
   xrefs (`il.LicenseIntf`/`TILLicenseIntf`, `regfile_unlock.php`, `/license/serial`,
   `licensevalidation.txt`, `ILRegKey`, `?machineid=`). Exact function **boundaries** for a few
   helpers are approximate (padded in the denylist → safe).
2. **"Plain .text, not VM'd" (high).** Confirmed by segment map: all located licensing funcs are
   in `.text` (`0x1000–0xE967FF`), outside `.avm0–.avm3`. This is *why* the guard is needed and
   feasible (baseline-able).
3. **Authenticode in FL64, not FLEngine (high).** No crypto imports in FLEngine; FL64 imports
   WINTRUST+CRYPT32 (`re/15`). FL64 itself is unanalyzed here (§8).
4. **In-process enforceability (medium — the main risk).** The capability boundary fully governs
   the cross-process bridge, but in-process CLR plugins can P/Invoke memory APIs directly; ALC
   gives no memory isolation. Mitigation = curation + integrity monitor + fail-inert gate, which
   is hygiene, not a hard guarantee. Set expectations accordingly.
5. **False positives in anti-piracy (medium).** Mitigated by version-keyed baselines, multi-signal
   corroboration, our-hook whitelist, and "unknown version ⇒ never assert cracked." The hard
   requirement "never break a legit user's FL" is met by fail-**inert** (we just don't load).
6. **Static-only blind spots (low/medium).** No runtime confirmation here (sibling owns runtime).
   Baseline generation and the `VirtualProtect`/page-permission checks need runtime validation
   before shipping.
7. **Maintenance (low, ongoing).** Baselines must be regenerated each FL release; the guarded RVA
   table shifts across versions. One version-keyed manifest keeps this tractable.
