# 21 — Playlist clip ↔ pattern off-by-one: evidence

Diagnostic for the bug: in the PLAYLIST, clicking an AI-authored pattern-clip selects the
**wrong pattern** — its name changes, it shows "pattern 0", or it shows garbled/empty notes.
Hypothesis going in: a 0-based(clips) vs 1-based(patterns/notes) index mismatch.

**Verdict up front:** confirmed by code audit. The clip-authoring path is the *only* place in
the SDK that treats patterns as **0-based**; everything else (create / select / list / get-name /
get-notes / add-notes) is **1-based**, and FL's own pattern store is 1-based. The clip path writes
a pattern reference that is **one too low**, so a clip the agent means for pattern *N* is resolved
by FL to pattern *N-1* (and pattern 1 → the empty "pattern 0"). `native_list_clips` *hides* the bug
by adding 1 back when it prints, so the list looks right while clicking the clip lands wrong.

---

## Part 1 — Live read-only inspection: NOT POSSIBLE THIS SESSION

FL Studio is **not running**, so the bridge could not be read.

- `flprobe attach` → `FL64 running : no` / `pipe '\\.\pipe\FruityLinkBridge' not reachable: timed out`.
- `flprobe bridge ping` and `flprobe bridge fl_ready` → `pipe call ... timed out`.
- Process check: zero `FL64` / `FLEngine` / `FL*` processes (`exit-count: 0`).

No mutating commands were sent; nothing was injected; no project was opened. The live clip→pattern
corroboration (clip source-id vs actual pattern name/notes) could not be captured. The conclusion
below rests entirely on the Part 2 code audit, which is itself decisive. When FL is next up with the
buggy song, the one-line live confirmation is:

```
flprobe attach && flprobe bridge ping
flprobe bridge native_list_clips      # note the pattern each clip prints
flprobe bridge native_list_patterns   # the real pattern names
flprobe bridge native_get_notes <P> -1 # notes of the pattern a clip claims to use
```

Expected smoking gun once readable: `native_list_clips` prints "pattern N" (because its decode adds
+1), but the source id actually stored on the clip encodes N-1, so clicking the clip in FL selects
pattern N-1. For the first pattern this is pattern 0 (empty) → the reported "pattern 0 / garbled".

---

## Part 2 — Code-path audit (the proof)

### A. Everything EXCEPT clips is 1-based, and FL's pattern store is 1-based

| Operation | File:line | Index convention | How it indexes the pattern store |
|---|---|---|---|
| `CreatePatternAsync` | `FlInjectBridge.cs:406-411` | **returns 1-based** | loops `i = 1..999`, returns first empty `i` |
| `GetCurrentPatternAsync` | `FlInjectBridge.cs:384-389` | 1-based (doc says so) | reads `*(0x14ab580)` |
| `SelectPatternAsync` | `FlInjectBridge.cs:391-396` | 1-based | `ValidatePattern` then `cbb300(index)` |
| `ValidatePattern` | `FlInjectBridge.cs:1204-1208` | **rejects idx < 1** | enforces 1-based for all callers |
| `GetPatternNameAsync` | `FlInjectBridge.cs:428-433` | 1-based | name ptr at `0x1803B68 + index*0xC0` |
| `GetNotesAsync` | `FlInjectBridge.cs:876-908` | 1-based | recorder at `0x1803B90 + patIdx*0xC0` (`0x1803B90 = 0x1803B68 + 0x28`) |
| `AddNotesAsync` / `AddNoteAsync` | `FlInjectBridge.cs:322-372` | 1-based | `ValidatePattern`; `11d4080(patIdx,1)` |

The pattern store is a struct array, base `0x1803B68`, **stride `0xC0`**, indexed by the **1-based**
pattern number directly (slot *N* = pattern *N*; slot 0 is reserved/empty). Fields within the struct:
`+0x00` name ptr, `+0x28` note recorder, `+0x50` length.

**Crucial corroboration — this exact off-by-one was already found and fixed once, in `GetNotes`.**
`FlInjectBridge.cs:879-882`:

> "The note-recorder static array 0x1803B90 is indexed by the **1-based** pattern number directly
> (verified: `11d4080(N) <-> static[N]`) ... **(No -1: that read `static[pattern-1]` and reported
> 'no notes'.)**"

So it is *proven live* that indexing this `0xC0`-stride pattern array with a 0-based value lands on
the wrong (empty) slot and yields "no notes". The clip path repeats that mistake.

### B. The clip path is 0-based — the break

`AddPatternClipAsync` (`FlInjectBridge.cs:1022-1036`):

- `FlInjectBridge.cs:1022` doc + `INativeFlControl.cs:128` contract: **"patternIdx is 0-based."**
- `FlInjectBridge.cs:1025` range check is `patternIdx < 0 ...` (allows 0) — it does **NOT** call
  `ValidatePattern` (which would reject 0). Deliberately 0-based.
- `FlInjectBridge.cs:1030` auto-length read: `patArr + patternIdx*0xC0 + 0x50`, where
  `patArr = *(0x14aa0c8)`. Same `0xC0` stride / `+0x50` length offset as the pattern struct above,
  but indexed **0-based** → reads the length of the *wrong* slot (for pattern 1 it reads slot 0).
- `FlInjectBridge.cs:1034` source id: `0x50005000u + ((uint)patternIdx << 16)` — the pattern field
  in the clip source id is set from the **0-based** `patternIdx`.

So for the agent's pattern *N*: stored source-id pattern field = `N-1`, and auto-length is read from
array slot `N-1`. Both are one slot too low versus the 1-based store that `GetNotes`/`GetPatternName`
use.

### C. Why the bug is invisible in `native_list_clips` (the masking)

`ListClipsAsync` (`FlInjectBridge.cs:1015`) decodes the source id with a **+1**:

```csharp
string srcDesc = src >= 0x50000000 ? $"pattern {(int)((src - 0x50000000) >> 16) + 1}" : ...
```

Round-trip inside our own code: store `N` as `(N-1)<<16` (line 1034) → decode `((... )>>16)+1` = `N`.
Self-consistent, so the list view prints the agent's intended N. But FL's *engine*, when it actually
uses the source id (clicking the clip / rendering), extracts the pattern field **without** that +1 —
i.e. it reads `N-1` directly as the pattern number. Hence list says N, click selects N-1.

### D. What the LLM is told (tool-description layer) — NOT the cause, but note the contradiction

- `native_add_pattern_clip` description says **"pattern 1-based"** — correct intent.
  (`NativeControlPlugin.cs:558`.)
- The wrapper then does the 1→0 conversion itself: `fl.AddPatternClipAsync(pattern - 1, ...)`
  (`NativeControlPlugin.cs:565`; identical in the MCP surface `PlaylistTools.cs:93`).
- `native_add_notes` / `native_get_notes` descriptions say **"pattern 1-based"** and pass the value
  straight through (`NativeControlPlugin.cs:146,161,474`).

So the LLM is consistently told **1-based everywhere** and passes a 1-based pattern number. The bug
is **not** the LLM confusing two conventions — it is the SDK/wrapper silently subtracting 1 on the
clip path only, against a FL pattern store that is 1-based. (The `INativeFlControl.cs:128` "0-based"
contract is the one piece of internal documentation that matches the buggy behaviour; it is also
wrong relative to FL.)

### E. Native layer (`tools/bridge/dllmain.cpp`) — not involved

The bridge is a generic executor (`call` / `callabs` / `peek` / `poke` / `pokeabs`). The whole clip
struct is built field-by-field on the C# side in `InsertClipRawAsync` (`FlInjectBridge.cs:1084-1105`)
and handed to FL's own collection `init`/`insert` vtbl fns; the source-id pattern field is poked at
`tmp+0x04` (`:1096`) from the value computed in C#. A grep of `dllmain.cpp` for
pattern/clip/source/`0xC0`/`0x50005000` finds **no index arithmetic** — the native side never
touches the pattern number. So this is **not** a native-layer fix.

---

## Conclusion — root cause + where to fix

**Root cause:** `AddPatternClipAsync` and its tool/MCP wrappers use a **0-based** pattern index for
the clip source id and the auto-length lookup, while FL's pattern store and every other SDK pattern
op are **1-based**. The clip therefore stores a pattern reference one too low. FL resolves the clip
to pattern *N-1* (pattern 1 → empty slot 0 = "pattern 0"; pattern 2 → shows pattern 1's name/notes;
etc.), producing exactly the reported "wrong pattern / pattern 0 / garbled notes". `native_list_clips`
masks it with a compensating `+1` in its decode, which is why the list looks correct.

This is the *same* off-by-one already discovered and fixed in `GetNotes` (the `0xC0`-stride pattern
array is 1-based; a 0-based index hits the wrong/empty slot) — left unfixed on the clip path.

**Layer to fix: the SDK (and its tool/MCP wrappers) — make the clip path 1-based like the rest.**
Native `dllmain.cpp` needs no change. Concretely:

1. `NativeControlPlugin.cs:565` and `PlaylistTools.cs:93` — drop the `- 1`; pass `pattern` directly.
2. `FlInjectBridge.cs:1023-1034` — treat the arg as 1-based: use `ValidatePattern`, index the length
   array at `... + pattern*0xC0 + 0x50` (1-based, matching `GetPatternName`/`GetNotes`), and build the
   source id as `0x50005000u + ((uint)pattern << 16)` (store the 1-based number FL expects).
3. `FlInjectBridge.cs:1015` (`ListClipsAsync`) — drop the compensating `+1` so a stored 1-based N
   decodes back to N (otherwise the fix would push the list display off by one the other way).
4. Update the doc/contract that currently says 0-based: `FlInjectBridge.cs:1022` and
   `INativeFlControl.cs:128`.

(Steps 1–3 must land together — store and decode are a matched pair. The decisive RE check that the
sibling agent can confirm: drag a pattern-1 clip in FL's native UI and read its stored source-id
pattern field — it should be 1, not 0; if so, our `(N-1)<<16` store is the off-by-one.)

---

### Report summary

- **Live clip→pattern corruption observed:** could **not** be read — FL Studio was not running
  (bridge pipe timed out, 0 FL processes). No mutation performed.
- **Code-path index breaks (file:line):**
  - `FlInjectBridge.cs:1034` source id from **0-based** `patternIdx` (`0x50005000 + (patternIdx<<16)`).
  - `FlInjectBridge.cs:1030` auto-length from **0-based** slot (`patArr + patternIdx*0xC0 + 0x50`),
    vs 1-based `GetPatternName` `FlInjectBridge.cs:430` and `GetNotes` `FlInjectBridge.cs:884`.
  - `FlInjectBridge.cs:1025` allows `patternIdx == 0` (skips `ValidatePattern`, which requires ≥1,
    `FlInjectBridge.cs:1206`).
  - `FlInjectBridge.cs:1015` `ListClips` decode `+1` masks the bug in the list view.
  - Wrappers convert 1→0: `NativeControlPlugin.cs:565`, `PlaylistTools.cs:93`; contract calls it
    0-based at `INativeFlControl.cs:128` — while the LLM-facing text says 1-based
    (`NativeControlPlugin.cs:558`).
  - Native `tools/bridge/dllmain.cpp`: no pattern/clip index math — not the fix site.
- **Best root-cause call:** clip authoring is 0-based against a 1-based FL pattern store → clip
  references pattern N-1 (pattern 1 → "pattern 0"/empty). Fix in the **SDK + wrappers** (make the
  clip path 1-based, drop `ListClips`'s `+1`). Same class of bug previously fixed in `GetNotes`.
