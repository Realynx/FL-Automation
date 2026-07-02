# 21 — Playlist pattern-clip bugs (off-by-one FIXED; + CLIP-INSERT CRASH 2026-07-02)

## clip-insert-crash (2026-07-02) — native_add_pattern_clip CRASHED FL (AV)
Symptom: after LLM used `native_add_pattern_clip` (+ `native_add_notes`), FL crashed with an access
violation. Crash = `FUN_00ccc330` (playlist/pattern paint) @ ghidra `0xCCC47D`: `CMP [RAX+0x14],0`
where `RAX = *(patternArray[param_2] + 0x20)` = **-1** (the pattern's clip/score list, uninitialized).
patternArray = `*(0x14aa0c8)`, stride 0xC0. Live post-crash the patterns read `+0x20 == 0` (clean),
so the -1 was **transient during the insert** (a malformed clip pointed at an unrealized pattern slot).
**REAL ROOT CAUSE (confirmed, re/generated/clipcrash-rootcause.md):** the `-1` at `patternArray[N]+0x20`
is an **OUT-OF-RANGE read**. `param_2` to `FUN_00ccc330` = the pattern number DECODED from the clip's
sourceID (`(src-0x50000000)>>16`). FL's pattern array (`*(0x14aa0c8)` = `0x1803B68`, stride 0xC0) is only
~1000 slots (patterns 1..999); `+0x20` = the pattern's **PARAM recorder** (`+0x28` = note recorder). For
an in-range N, `+0x20` is 0 or a valid ptr (guard-safe). A clip whose decoded N > 999 reads adjacent
`.data` = `-1`, which defeats FL's `!= 0` guard → deref → AV. Our `ValidatePattern` capped at **9999**
(not 999), and a corrupted sourceID (garbage `+0x04`) could also decode OOB. The "realize the note
recorder" attempt FAILED because it inits `+0x28`, not the `+0x20` param recorder.
**FIX (applied, compiles; pending clean-project cliptest):** (1) `MaxPatternIndex` 9999→**999**; (2)
realize BOTH recorders — `FLpat_GetOrCreateNoteRecorder@0x11D4080(pat,1)` (+0x28) AND
`FLpat_GetOrCreateParamRecorder@0x11D4000(pat,1)` (+0x20); (3) atomic insert under `_scratchGate`;
(4) refresh via `RefreshPatternAsync` (11d4140→d49840). Do NOT adopt `FLpl_SendPatternToPlaylist`
wholesale (it auto-places, can't target track/tick, and doesn't init +0x20 either). Tool re-enabled.

---

# (original) Playlist pattern-clip references the WRONG pattern (off-by-one root cause + fix spec)

Status: **RESEARCH COMPLETE — root cause CONFIRMED in Ghidra + our code.** Static analysis only
(no live FL touched). Ghidra program `FLEngine_x64.dll`, image base `0x400000`; all engine
addresses below are Ghidra-absolute (live = liveFLEngineBase + (addr − 0x400000)).

---

## TL;DR

The FL engine encodes a playlist **pattern-clip source ID from the 1-based pattern NUMBER**:

```
sourceID = 0x50005000 + (patternNumber_1based << 16)     // engine: FLpl_SendPatternToPlaylist @0xCB0780
```

Our SDK builds the same constant but from a **0-based** index. `native_add_pattern_clip`
takes a 1-based `pattern`, does `pattern - 1`, and `AddPatternClipAsync` encodes that 0-based
value. Result: every clip references **pattern N−1** instead of N — and the very first authored
pattern (UI pattern 1 → index 0 → `sourceID = 0x50005000`) decodes to **pattern 0**, the reserved
slot. That is exactly the user's report: clicking the clip switches to the wrong / "pattern 0"
slot, shows the wrong name, and plays garbled/empty notes.

The magic constant `0x50005000` is **correct** (matches the engine). The defect is the
**0-based vs 1-based convention**: clips use 0-based while notes/name/recorder/select are all
1-based. `ListClips` is broken in the opposite direction (`+1`), which is why it "round-tripped"
in earlier testing and hid the bug.

Leading hypothesis in the task brief: **CONFIRMED**, with the precise mechanism nailed down
(it is a clean −1 on the pattern index at the clip-encode boundary, not a base-constant error).

---

## 1. The definitive pattern-index model (ground truth from Ghidra)

### Patterns are 1-based internally; index 0 is a reserved slot
- Pattern data array: `PTR_DAT_014AA0C8`, stride `0xC0`, indices **1..999** (length @ `+0x50`,
  color @ `+0x08`). Slot 0 = reserved header.
- Per-pattern note recorder: static array `DAT_01803B90 + idx*0xC0`
  (`FLpat_GetOrCreateNoteRecorder @0x11D4080`). The decompile indexes
  `(&DAT_01803b90)[param_1*0x18]` (0x18 qwords = 0xC0 bytes) and stores `rec+0x3C = param_1`.
  **`param_1` is the 1-based pattern number** (memory-verified live: `11D4080(99) ↔ static[99]`;
  current-pattern global `*(*(0x14AB580))` = 1 for pattern 1).
- Pattern name ptr: `*(DAT_01803B68 + idx*0xC0)` — same 0xC0 per-pattern block, 1-based.

### How a pattern clip stores which pattern it plays — `FLpl_SendPatternToPlaylist @0x00CB0780`
This is the engine's **canonical** "send pattern to playlist" primitive (called by
`TStepSeqForm.SendToPlaylistBtnClick @0xF464FD`). With `param_2 = patternNumber`:

```c
lVar3 = FLpat_GetOrCreateNoteRecorder(local_c4, 0);                 // local_c4 == param_2 (1-based)
...
FLpat_GetPatternName(local_c4, &local_80, 1);                       // same index → name
...
local_74 = local_c4 * 0x10000 + 0x50005000;                        // <<< CLIP SOURCE ID >>>
local_78 = local_b8;                                               // tmp+0x00 startTick
local_70 = *(PTR_DAT_014aa0c8 + local_c4*0xc0 + 0x50);             // tmp+0x08 length = pattern length
local_6c = FLpl_EncodeClipTrackField(local_b4);                   // tmp+0x0c = 500 - trackNo
local_65 |= 0x80;                                                  // tmp+0x13 ACTIVE
local_60 = local_5c = 0xffffffff;                                 // tmp+0x18/+0x1c full source trim
insert(Cobj, &local_78);                                          // vtbl+8
```

The **same `local_c4`** drives the note recorder, the name, the length lookup, AND the source ID.
Since the recorder index is 1-based, the **source ID is built from the 1-based pattern number**:

```
sourceID = 0x50005000 + (patternNumber_1based << 16)
```

### How FL decodes a clip back to a pattern — `FLpl_DecodeClipSourceID @0x00F72D40`
```c
bool FLpl_DecodeClipSourceID(uint id) {
  if ((int)id < 0x50000000) {            // CHANNEL clip (audio / automation / step-seq channel)
     channelIdx = id >> 16;              // 0-based channel index; validated < channelCount
     ... ("Wrong IDToChanPat channel" if out of range)
  } else {                               // PATTERN clip
     val = (id + 0xB0000000) >> 16;      // == (id - 0x50000000) >> 16
  }
  return (int)val >= 0;
}
```
For `id = 0x50005000 + (N<<16)`: `(id − 0x50000000) >> 16 = (0x5000 + (N<<16)) >> 16 = N`
(because `0x5000 >> 16 = 0`). So the decode returns the **1-based pattern number N** — the same
index used to look up the recorder/name/array. Clean inverse.

**Source-ID ranges (engine truth):**
| sourceID range | clip kind | decoded reference |
|---|---|---|
| `< 0x50000000` | channel clip (audio/automation/step-seq) | channel index = `id >> 16` (0-based) |
| `>= 0x50000000` | pattern clip | pattern number = `(id − 0x50000000) >> 16` (**1-based**) |

> NOTE: `re/generated/controls-playlist.md` (clip struct, +0x04 row) has the two branch **labels
> swapped** ("<0x50000000 → pattern", ">= → audio/automation"). The decompile above is authoritative:
> `< 0x50000000` = channel, `>= 0x50000000` = pattern. The per-branch *formulas* in that doc are
> correct; only the labels are wrong. The memory note in `fl-control-catalog`
> ("pattern = 0x50005000+(pat<<16)") is correct about the constant but does not flag that `pat`
> must be the **1-based** number.

### "Pattern 0"
Index 0 is the reserved pattern slot (the 1-based array's header). A clip whose source decodes to
0 references the recorder/name/array at slot 0: blank name, no real notes → shows as "pattern 0"
with garbled/empty content. This is what the user sees for the first clip.

---

## 2. What our code actually does (the bug, line by line)

### Writer — `src/FruityLink.Agent/Plugins/NativeControlPlugin.cs`
`native_add_pattern_clip` (lines 557–567), param `pattern` documented **1-based**:
```csharp
await fl.AddPatternClipAsync(pattern - 1, track, startTick, lengthTick, ct);   // line 565  <-- BUG: -1
```

### Writer — `src/FruityLink.FlStudio/Inject/FlInjectBridge.cs`
`AddPatternClipAsync(int patternIdx /* documented 0-based */, …)` (lines 1022–1036):
```csharp
if (patternIdx < 0 || patternIdx > MaxPatternIndex) throw ...;                              // 1025 (allows 0)
len = await AI32Async(patArr + (ulong)patternIdx * 0xC0 + 0x50, ct);                        // 1030 wrong slot
await InsertClipRawAsync(startTick, 0x50005000u + ((uint)patternIdx << 16), len, track,...);// 1034 0-based encode
```
With the plugin's `pattern - 1`, the stored source for UI pattern P is
`0x50005000 + ((P−1) << 16)`, which the engine decodes to **pattern P−1** (and **0** for P = 1).
The auto-length read at line 1030 also reads pattern **P−1**'s length (latent length bug; same −1).

### Reader — `ListClipsAsync` in `FlInjectBridge.cs` (lines 999–1020)
```csharp
string srcDesc = src >= 0x50000000
    ? $"pattern {(int)((src - 0x50000000) >> 16) + 1}"     // line 1015  <-- BUG: extra +1
    : $"channel {(int)(src >> 16)}";
```
The engine decode is `(src − 0x50000000) >> 16` (already 1-based). The extra `+1` makes ListClips
report **N+1**. Combined with the writer's −1, our writer↔ListClips round-trips to P (self-
consistent), which is exactly why the bug passed the earlier "verified" check (memory
`overnight-loop-fixes`: "raw add bumped count + FL survived" + ListClips round-trip — never
compared against the engine's own decode or a click/playback). Against any engine-authored clip
(user dragged a pattern), ListClips reports the pattern one too high.

### Everything else is already 1-based (so the clip path is the lone outlier)
- `AddNotesAsync`/`AddNoteAsync` (line 329, recorder call line 343): `pattern` 1-based → `11D4080(N)`.
- `CreatePatternAsync` (406–411): loops `i = 1..`, returns **i (1-based)**.
- `SelectPatternAsync` (391–396), `GetNotesAsync` (877–884, `0x1803B90 + patIdx*0xC0`),
  `GetPatternNameAsync` (428–433, `0x1803B68 + index*0xC0`), `ListPatternsAsync`,
  `IsPatternEmptyAsync`, `ClearPatternAsync`: all 1-based.
- `ValidatePattern` (1203–1208): enforces **1..9999** — the correct guard, NOT used by the clip path.

---

## 3. Root cause of each symptom

A clip authored for UI pattern **P** is stored with `sourceID = 0x50005000 + ((P−1)<<16)`, which FL
resolves (via the inline equivalent of `FLpl_DecodeClipSourceID`) to **pattern P−1**:

- **(a) Clicking the clip changes the pattern NAME** — FL resolves the clip's `+0x04` source to
  pattern P−1 and makes that the active pattern / derives the clip-track label from it; you see
  P−1's name (or slot 0's blank name), not P's. Root: 0-based encode at FlInjectBridge.cs:1034
  (+ plugin `−1` at NativeControlPlugin.cs:565). Engine encode reference: `0x00CB0780`
  (`local_74 = local_c4*0x10000 + 0x50005000`).
- **(b) Becomes "pattern 0"** — for the FIRST authored pattern (UI P = 1): index 0 →
  `sourceID = 0x50005000` → decode `(0x50005000 − 0x50000000)>>16 = 0` → the reserved slot 0.
  `ValidatePattern` (which would reject 0) is bypassed because the clip path validates
  `patternIdx >= 0` (line 1025) instead.
- **(c) Notes render garbled/wrong** — the clip plays pattern P−1's note recorder
  (`0x1803B90 + (P−1)*0xC0`), or slot 0's empty/garbage recorder for P = 1, instead of pattern P's
  notes (`0x1803B90 + P*0xC0`). The notes the agent authored are intact in pattern P; the clip
  simply points one slot too low.

The auto-length default (line 1030) is wrong by the same −1 (reads P−1's length) — fixed by the
same convention change.

---

## 4. FIX SPEC (make the SDK uniformly 1-based; convert nowhere — FL patterns ARE 1-based)

The whole SDK is already 1-based except the clip path. Bring the clip path into line. **No
"convert to FL-internal" step is needed** because the FL-internal pattern convention for the clip
source is itself 1-based.

### 4.1 `NativeControlPlugin.AddPatternClipAsync` (NativeControlPlugin.cs ~line 565) — drop the −1
```csharp
// BEFORE
await fl.AddPatternClipAsync(pattern - 1, track, startTick, lengthTick, ct);
// AFTER
await fl.AddPatternClipAsync(pattern, track, startTick, lengthTick, ct);     // pass 1-based straight through
```
Keep the `[Description("Pattern 1-based")]` on the param (already correct).

### 4.2 `FlInjectBridge.AddPatternClipAsync` (FlInjectBridge.cs lines 1022–1036) — make it 1-based
```csharp
/// <summary>Adds a pattern clip to the playlist timeline. pattern is the 1-based pattern number.
/// lengthTick<=0 = pattern's own length.</summary>
public async Task AddPatternClipAsync(int pattern, int track, int startTick, int lengthTick, CancellationToken ct = default)
{
    ValidatePattern(pattern);                                            // 1..9999 (rejects 0/neg — kills the "pattern 0" case)
    int len = lengthTick;
    if (len <= 0)
    {
        ulong patArr = await GPtrAsync("14aa0c8", ct);
        if (patArr != 0) len = await AI32Async(patArr + (ulong)pattern * 0xC0 + 0x50, ct);   // 1-based slot
        if (len <= 0) len = await GetPpqAsync(ct) * 4;
    }
    // pattern-clip sourceID = 0x50005000 + (1-based pattern << 16)  — matches FLpl_SendPatternToPlaylist @0xCB0780
    await InsertClipRawAsync(startTick, 0x50005000u + ((uint)pattern << 16), len, track, -1, -1, ct);
    await RepaintPlaylistAsync(ct);
}
```
Index math change: `(uint)patternIdx << 16` (0-based) → `(uint)pattern << 16` (1-based); length
lookup `patternIdx*0xC0` → `pattern*0xC0`; validation `patternIdx<0||>Max` → `ValidatePattern(pattern)`.
(`MaxPatternIndex` may be deleted if no longer referenced; `ValidatePattern` already uses 9999.)

### 4.3 `FlInjectBridge.ListClipsAsync` (FlInjectBridge.cs line 1015) — drop the +1
```csharp
// BEFORE
string srcDesc = src >= 0x50000000 ? $"pattern {(int)((src - 0x50000000) >> 16) + 1}" : $"channel {(int)(src >> 16)}";
// AFTER  (engine-true: (src - 0x50000000) >> 16 is already the 1-based pattern number)
string srcDesc = src >= 0x50000000 ? $"pattern {(int)((src - 0x50000000) >> 16)}" : $"channel {(int)(src >> 16)}";
```
Channel branch is correct as-is (`src >> 16` = 0-based channel index). Branch direction
(`>= 0x50000000` = pattern) is correct.

### 4.4 No other call sites
The only engine encode is `FLpl_SendPatternToPlaylist @0xCB0780`; the only SDK encode is
`AddPatternClipAsync`; the only SDK pattern-source decode is `ListClipsAsync`. `MoveClipAsync`,
`ResizeClipAsync`, `DeleteClipAsync`, `SetClipMutedAsync`, `SliceClipAsync`, `DuplicateClipAsync`
operate on existing clip structs by slot index and do not touch the source ID — no change needed.

### 4.5 Names/rename — NOT implicated
The wrong "name on click" is purely a side effect of the clip pointing at the wrong pattern; once
the source ID is correct, the clip resolves to pattern P and shows P's name. No change to the name
path (`GetPatternNameAsync`, the `Delphi_UStrAsg` rename persistence) is required for this bug.

---

## 5. Read-back self-check the fix agent can run

After the fix, in a project with at least 3 patterns (so an off-by-one is unambiguous):

1. `K = CreatePatternAsync()`  (returns 1-based; pick K ≥ 2 so K and K−1 differ from 0).
2. `AddNotesAsync(K, [ (ch0, key=60, start=0, len=480, vel=100) ])`.
3. `AddPatternClipAsync(K, track=1, startTick=0, lengthTick=0)`.
4. `ListClipsAsync()` → assert the new clip line reads **`pattern K`** (NOT K−1, NOT K+1, NOT 0).
5. `GetNotesAsync(K)` → assert it returns the note authored in step 2.
6. **Raw-bridge cross-check (no LLM):** read the clip's source field and assert it equals the
   engine encoding and decodes to K:
   - clip base = `data + i*stride` from `ClipCollectionAsync`; `peekabs clip+0x04 4`.
   - assert `src == 0x50005000 + (K << 16)`.
   - assert engine decode `(int)((src - 0x50000000) >> 16) == K`.
7. **Regression guard for K = 1:** `AddPatternClipAsync(1, …)` then ListClips → must read `pattern 1`
   (pre-fix this produced `pattern 0`); and `AddPatternClipAsync(0, …)` must now THROW
   (ValidatePattern rejects 0) rather than silently create a pattern-0 clip.
8. **Live behavior (manual, optional):** clicking the clip selects pattern K (current-pattern
   global `*(*(0x14AB580))` == K), shows K's name, and plays K's notes.

Use a scratch C# harness referencing `FlInjectBridge` for steps 1–7 (the minimax test backend
mis-reports tool results — see memory `minimax-llm-flaky-toolcalls`); do NOT trust the LLM's
self-report of ListClips.

---

## 6. Engine references (Ghidra, base 0x400000)
- `FLpl_SendPatternToPlaylist @0x00CB0780` — canonical clip encode: `sourceID = pat1based*0x10000 + 0x50005000`.
- `FLpl_DecodeClipSourceID @0x00F72D40` — `>=0x50000000` → pattern `(id-0x50000000)>>16` (1-based); `<` → channel `id>>16` (0-based).
- `FLpl_IsClipDeleted @0x00F719A0` → calls the decoder on `clip+0x04`.
- `FLpat_GetOrCreateNoteRecorder @0x11D4080` — recorders `DAT_01803B90 + idx*0xC0`, **1-based**.
- Pattern array `PTR_DAT_014AA0C8`, stride `0xC0`, idx 1..; length `+0x50`, color `+0x08`.
- Clip struct: start `+0x00`, sourceID `+0x04`, length `+0x08`, track `+0x0C (=500-trackNo)`,
  active/mute flags `+0x13 (0x80 active / 0x20 muted)`, source trim `+0x18/+0x1C`.
- Helpers: `FLpl_GetClipByIndex 0x11E0DD0`, `FLpl_RecountActiveClips 0xF6E180`,
  `FLpl_RepaintPlaylist 0xDA40C0`, `FLpl_GetCurrentArrangement 0x11E32C0` (Cobj = `*(arr+0x14)`).

## 7. Our code references
- `src/FruityLink.Agent/Plugins/NativeControlPlugin.cs:565` — `pattern - 1` (remove).
- `src/FruityLink.FlStudio/Inject/FlInjectBridge.cs:1022-1036` — `AddPatternClipAsync` (→ 1-based).
- `src/FruityLink.FlStudio/Inject/FlInjectBridge.cs:1015` — `ListClipsAsync` decode (remove `+1`).
- `src/FruityLink.FlStudio/Inject/FlInjectBridge.cs:1203-1208` — `ValidatePattern` (reuse for the clip path).
