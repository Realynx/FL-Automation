# 20 — FL "fully initialized" readiness signal (for the in-process bootstrap)

**Goal.** Our managed bootstrap (proxy/CLR chain) runs **~1 s after FL's process starts** —
far too early. At that point the audio-engine *read* path is up but the **song/project
object and the UI forms are not yet allocated**, so any FL-state-dependent write faults.
This doc finds a **cheap, stable global** the bootstrap can **poll** to know FL has finished
initializing (main window created, default/empty project loaded, engine ready) before doing
FL-state-dependent work.

Method: **static Ghidra only** (program `FLEngine_x64.dll`, image base `0x00400000`). No FL
runtime was touched. All "is this NULL before init?" claims are verified with `read_memory`
against the on-disk image: a `.bss` address returns *"Unable to read bytes"* (zero-filled at
load = NULL pre-init); a `.data` address returns its initialized bytes.

> **Rebase.** Ghidra addresses below use image base `0x400000`. Runtime address =
> `flEngineBase + (ghidraAddr − 0x400000)` (the bridge's existing formula). None of the globals
> here are in the `.avm*` (VMProtect) range, which starts at `0x01b5c000` — they are plain
> `.data`/`.bss` and safe to read.

---

## 1. Why the tempo-WRITE faults (decompile of `FL_DispatchCommand` @ `0xF53FE0`)

The observed fault was `call f53fe0 40000005 226c8 11` → `FL_DispatchCommand(cmdId=0x40000005
(tempo), value=0x226c8, flags=0x11)`. The tempo case (lines from the decompile):

```c
else if (param_1 == 0x40000005) {                       // TEMPO
  if ((flags & 0x20) != 0) { ... }                      // 0x11: not taken (range query)
  if (((flags & 1) == 0) || (*PTR_DAT_014aaad0 != '\0')) {   // 0x11: SET set, gate byte==0  -> FALSE
    if ((flags & 2) != 0) local_res10[0] = *(uint*)PTR_DAT_014a9f00;   // GET
  }
  else if (*(char *)(*(longlong *)PTR_DAT_014a9f40 + 0x3c) == '\0') {  // <-- (A) FAULT
    *(uint *)PTR_DAT_014a9f00 = value;                  // write tempo value (static storage)
    ...
  }
  if ((flags & 0x10) != 0) {                            // 0x11: taken
    local_2f8 = *(longlong **)(*(longlong *)PTR_DAT_014aa4c8 + 0x778);   // <-- (B) FAULT
    (**(code **)(*local_2f8 + 0x1f0))(local_2f8, *(uint*)PTR_DAT_014a9f00, 0);  // vcall on tempo ctrl
  }
  if ((flags & 4) != 0) { ... status-bar text ... }     // 0x11: not taken
}
```

### Ghidra pointer-global convention (critical — this is the deref shape)
The `0x14Axxxx` symbols are a **`.data` indirection table**: each slot holds the address of the
*real* global, and the real global (`.bss`) holds the live object pointer. In the decompiler,
`PTR_DAT_014aXXXX` already means *"the pointer value loaded from the slot"*, so
`*(T*)PTR_DAT_014aXXXX` is the **second** dereference. In bridge terms (matching the existing
`mainForm = *(void**)(*(void**)0x14a8750)` notation):

```
slot (.data, file-backed)  --*-->  realGlobal (.bss, NULL pre-init)  --*-->  live object (heap)
```

Verified with `read_memory`:

| Decompiler symbol | slot (Ghidra) | slot content = real global | real global section | meaning |
|---|---|---|---|---|
| `PTR_DAT_014a9f00` | `0x14A9F00` | `0x014A0BC0` | **.data** (=`0x000222E0`=**140000**) | raw tempo, **milli-BPM** (static storage) |
| `PTR_DAT_014a9f40` | `0x14A9F40` | `0x01832B78` | **.bss → NULL** | **song/transport object** holder |
| `PTR_DAT_014aa4c8` | `0x14AA4C8` | `0x0157E850` | **.bss → NULL** | **toolbar form** (tempo/vol/pitch ctrls) |
| `PTR_DAT_014aaad0` | `0x14AAAD0` | `0x0141C5F6` | .rdata (`=0x00`) | "gate" config byte (allow-edit) |

So the tempo **READ** path (`GetTempoAsync→140`) works because it reads the **static** value at
`0x14A0BC0` (no object needed). The tempo **WRITE** needs allocated objects:

- **(A)** `*(char*)(*(void**)0x01832B78 + 0x3c)` — `*(void**)0x01832B78` is the **song/transport
  object** (tempo `double`@+0x10, **playing byte@+0x3c**, beat@+0x20). It is **`.bss` / NULL
  pre-init**, so the `+0x3c` read is the **primary access violation**. The gate byte
  `*0x0141C5F6 == 0`, so for `flags=0x11` (and also the live-tested `0x3dd`) control **enters this
  else-if** → faults. *(This is exactly the prompt's "project/song object isn't allocated yet".)*
- **(B)** had (A) passed, `flags&0x10` then derefs `*(void**)0x0157E850` (**toolbar form**, also
  NULL pre-init) `+0x778` → tempo-control vcall → second fault.

The flags don't matter; the difference between the failing call and the earlier **live-verified**
130→150 BPM write (`flags 0x3dd`) is purely **timing** — the song object + toolbar form were
allocated by then.

Helper functions confirming the song-object shape (renamed in Ghidra):
`FL_UpdateTransportStatusHint` (`0x12637D0`) and `FL_PushTransportTempoToToolbar` (`0x1263AC0`)
both read `*(void**)0x01832B78` (`+0x3c` playing, `+0x20` beat, `+0x10` tempo) and the toolbar
tempo ctrl `*(void**)0x0157E850 + 0x778`.

---

## 2. The project/song-loaded globals

All are **`.bss` / NULL pre-init** (verified) and become non-null when FL builds its UI and
loads the default project. Reached with the same **double-deref** shape from a `.data` slot.

| Signal | slot (Ghidra) | real global (Ghidra) | `*(void**)real` = | becomes non-null when |
|---|---|---|---|---|
| **mainForm** | `0x14A8750` | `0x01581200` | main window (`+0x760` menus/actionList, `+0x1F98` channel-rack mod) | main window created |
| **toolbarForm** | `0x14AA4C8` | `0x0157E850` | toolbar (tempo `+0x778`, vol `+0x9e0`, pitch `+0xb28`) | transport/toolbar UI built |
| **songObj** (transport) | `0x14A9F40` | `0x01832B78` | song/transport (tempo`+0x10`, playing`+0x3c`, beat`+0x20`) | **default project/song loaded** |
| **chanList (all)** | `0x14A98D8` | `0x0157F638` | all-channels list; **count = `*(int*)(list+0x10)`** (=`channels.channelCount(1)`) | channel rack / song doc loaded |
| chanList (filtered) | `0x14A7968` | `0x0157F640` | current/group channel list; count@`+0x10` | (view variant of above) |

`songObj` (#2) is the exact pointer the tempo-WRITE faults on; `chanList` (#4) is what
`ListChannels` reads (empty/guarded while NULL). Each maps to one observed failure.

**Not useful as a "fully ready" gate:**
- Host-context root: slot `0x14A7E40` → real `0x01496E38` (zero-init `.data`). Set by
  `CreateFruityInstance` in the **first moments** of load — flips far too early.
- Mixer count: slot `0x14A9850` → `0x012F5F0C` (`.data`, non-zero static) — not a load gate.
- Python context getter `*(void**)0x14A7DC0` (used by `FL_ResolveChannel`/Python layer) — **NULL
  at rest** unless scripting is active; do not use.

**End-of-startup "ready" indicator.** No single dedicated boolean was needed: the combination
**mainForm ∧ toolbarForm ∧ songObj (∧ chanList) all non-null** *is* the robust readiness
predicate. mainForm/toolbarForm cover "main window + transport UI created"; songObj covers
"engine song/transport allocated"; chanList covers "channel rack/song document present". This
matches the observed ordering: the menu install already succeeds ~1.5 s in (so the forms are a
**weaker/earlier** signal), whereas the song/channel objects are the **later, stronger** signal —
gating on them implies the forms are already up.

---

## 3. Recommended readiness check (poll, no hook)

Poll a few stable globals from the **bridge worker thread** (read-only aligned loads). At each
indirection level **check non-null before dereferencing the next**, so a half-built chain never
faults. Reads are 8-byte aligned → atomic on x64; wrap in the bridge's existing SEH for safety.

```c
// base = flEngineBase ; slotRVA = ghidraSlotAddr - 0x400000
static void* deref2(uintptr_t base, uintptr_t slotGhidra) {   // **(slot)
    void** real = *(void***)(base + (slotGhidra - 0x400000)); // .data slot -> real global (relocated)
    if (!real) return NULL;
    return *real;                                             // .bss real global -> live object (NULL pre-init)
}

bool FlIsReady(uintptr_t base) {
    void* mainForm    = deref2(base, 0x14A8750);   // main window
    void* toolbarForm = deref2(base, 0x14AA4C8);   // transport/tempo UI  (tempo-WRITE target B)
    void* songObj     = deref2(base, 0x14A9F40);   // song/transport obj  (tempo-WRITE target A)
    void* chanList    = deref2(base, 0x14A98D8);   // channel rack / song doc (ListChannels)
    if (!(mainForm && toolbarForm && songObj && chanList)) return false;
    // Optional: confirm the default template actually populated channels.
    // int n = *(int*)((char*)chanList + 0x10);  // tolerate 0 for an intentionally-empty template
    return true;
}

// bootstrap:
//   const int TIMEOUT_MS = 30000, INTERVAL_MS = 50;
//   for (t=0; t<TIMEOUT_MS; t+=INTERVAL_MS) { if (FlIsReady(base)) break; Sleep(INTERVAL_MS); }
//   if (FlIsReady(base)) RunFlStateDependentInit(); else LogReadinessTimeout();
```

Equivalent single-deref form (the real-global RVAs are fixed, so you can skip the slot):
`mainForm=*(void**)(base+0x1181200)`, `toolbarForm=*(void**)(base+0x117E850)`,
`songObj=*(void**)(base+0x1432B78)`, `chanList=*(void**)(base+0x117F638)`. Prefer the
slot/double-deref above — it matches the bridge's proven `mainForm` access and is robust to a
differently-based load.

- **Timeout:** `30 s` (generous: plugin scan + default-template load can take seconds; if not
  ready by then, log and proceed degraded rather than hang).
- **Interval:** `50 ms` (≤600 polls; negligible cost — a handful of pointer loads each).
- **Channels-present nuance:** `ListChannels` only returns real channels once
  `*(int*)(chanList+0x10) > 0` (default template loaded). If you need channels specifically, wait
  for `count >= 1` with a **short capped** extra wait (~2–3 s) **and tolerate a stable 0** (a user
  may set an empty default template) — do not hard-block on `count>0`.

**VMProtect / fallback.** All slots (`0x14A…`) and real globals (`0x158…/0x157…/0x1832…`) are
well below `.avm0` (`0x01b5c000`) → not virtualized, directly readable. If any one global were
ever obscured/moved, fall back to **mainForm** alone (slot `0x14A8750`, referenced by ~everything
and already proven by the working menu install) as the primary gate; the host-context root
(`0x14A7E40`) is a usable *earliest* liveness check but is too early for "fully ready".

---

## 4. Summary

- **Recommended readiness condition (poll, worker thread, 50 ms / 30 s):**
  all of `mainForm = **0x14A8750`, `toolbarForm = **0x14AA4C8`, `songObj = **0x14A9F40`,
  `chanList = **0x14A98D8` are **non-null** (each is `**slot`: `.data` slot → `.bss` real global →
  live object). Optionally also `*(int*)(chanList+0x10) >= 1` for channels-present (capped wait,
  tolerate 0).
- **Where it becomes true in startup:** after the main window + toolbar are constructed
  (~1.5 s in our session, when menu install already works) **and** the default project/song is
  loaded (allocates the transport object `*(0x1832B78)` and channel list `*(0x157F638)`), which is
  the later/stronger edge. The bootstrap's ~1 s wake is before all of this.
- **Why these:** `songObj` (`*0x01832B78`, `.bss`/NULL pre-init) is the **exact pointer the
  tempo-WRITE faults on** (`+0x3c`), and `toolbarForm` (`*0x0157E850`) is its second deref;
  `chanList` (`*0x0157F638`) is what `ListChannels` reads. The tempo-READ works early only because
  it hits the **static** tempo value at `0x14A0BC0` (=140000) — no object required.
- **Confidence:** **High** on the deref shapes and the NULL-pre-init facts (directly verified via
  `read_memory`: `.bss` reals return "unable to read"; `0x14A0BC0`=140000 matches the observed
  140 BPM; the decompiled fault path matches the logged faulting call).
- **Risks / caveats:**
  1. *Exact allocation ordering* of songObj vs chanList vs forms is inferred (no live RE this
     session) — requiring **all four** non-null neutralizes the ordering risk.
  2. *`count>0`* is template-dependent; treat as a soft, capped confirmation, not a hard gate.
  3. Reads must be **non-null-checked per level** (and ideally SEH-guarded) to avoid racing a
     half-constructed chain; pointer loads are atomic on x64 so the check itself is safe.
  4. RVAs are tied to this FL build (`FL Studio 2025`, `FLEngine_x64.dll`); re-verify slot→real
     mappings if the engine DLL changes.
