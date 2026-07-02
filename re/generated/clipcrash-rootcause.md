# clipcrash-rootcause — playlist paint AV after pattern-clip insert (ROOT CAUSE + FIX)

Ghidra `FLEngine_x64.dll`, image base `0x400000` (live = liveBase + (addr − 0x400000); crashing
session FLEngine base `0x5E560000`). Static RE + one `read_memory` of the live-initialized pattern
pointer. This supersedes the guesswork in `re/21` §clip-insert-crash / `re/25` root-cause #1
("patternArray[N]+0x20 = clip/score list left at -1 that the note recorder realizes"). **That was
wrong about WHICH field +0x20 is.** Corrected below.

---

## TL;DR (the three answers the task asked for)

1. **`param_2` passed to the crash func `FUN_00ccc330` is the 1-based PATTERN NUMBER decoded from the
   clip's own `sourceID`** — not a track, not an arrangement index. The paint caller
   `FUN_00d96950` does `FUN_01088f00(clip.sourceID, &obj, &patNo)` and forwards `patNo` (`local_5c`).
   `FUN_01088f00` = the inline `FLpl_DecodeClipSourceID`: for a pattern clip (`sourceID>=0x50000000`)
   it returns `obj=0` and `patNo=(sourceID−0x50000000)>>16`.

2. **`patternArray[N] + 0x20` is the pattern's PARAMETER / AUTOMATION event recorder** (FL's
   "param recorder"), NOT the note recorder. Proof: `read_memory(0x14aa0c8)` → the pattern-array
   base pointer `*(0x14aa0c8) == 0x0001803B68`. So `block[N] = 0x1803B68 + N*0xC0`, and:
   * `block+0x20` = `0x1803B88` = `&DAT_01803b88` = **param recorder**, created by
     **`FLpat_GetOrCreateParamRecorder @0x11D4000`** (class `PTR_FUN_00f60f40`).
   * `block+0x28` = `0x1803B90` = `&DAT_01803b90` = **note recorder**, created by
     `FLpat_GetOrCreateNoteRecorder @0x11D4080` (class `PTR_FUN_00f613e8`).
   Confirmed independently by `FLpat_IsPatternEmpty @0x11DB510`, which reads `(&DAT_01803b88)[N*0x18]`
   (param, +0x20) then `(&DAT_01803b90)[N*0x18]` (note, +0x28).
   The crash "kind 3" render loop treats `+0x20` as automation VALUE events
   (`FLgl_cmd_GetValueRange` per clip's `+0x04`); "kind 2" (`+0x28`) is the note preview.

3. **This is exactly why the previous fix never worked.** `FLpat_GetOrCreateNoteRecorder @0x11D4080`
   and `FLpat_RebuildPatternAndRefresh @0x11D4140` (native_add_notes) both only ever create/populate
   `+0x28` (the note recorder). Neither touches `+0x20` (the param recorder). So realizing the note
   recorder + adding a note left `+0x20` completely alone → the crash field was never addressed.

**But the deeper point:** for any *in-range* pattern (1..999), `+0x20` is ALWAYS `0` or a valid
pointer (never `-1`) — `FLpat_IsPatternEmpty`'s empty-scan over 1..999 dereferences `+0x20+0x14` and
would itself AV otherwise, yet it runs fine. So the `-1` is **an OUT-OF-RANGE read**: the clip's
decoded pattern number `N` is beyond the ~1000-slot static pattern array, so `block[N]` reads
adjacent `.data` which held `-1`. The `!= 0` guard passes `-1` → `CMP [RAX+0x14],0` derefs `-1` → AV.
Post-crash the in-range patterns read `+0x20 == 0` because they were always fine; the bad slot was
never a real pattern.

---

## 1. The crash instruction (disassembled, `FUN_00ccc330`)

```
00ccc3cd  MOV RAX, [0x014aa0c8]          ; RAX = *(0x14aa0c8) = pattern-array base = 0x1803B68
00ccc3d4  MOV ECX, [RBP+0x74]           ; ECX = param_2 = decoded pattern number N
00ccc3da..e2  RAX = base + N*0xC0        ; block[N]   (stride 0xC0)  -> [RBP+0x68]
...
00ccc44f  MOV RAX, [RBP+0x68]           ; RAX = block[N]
00ccc453  MOV RAX, [RAX+0x20]           ; RAX = block[N]+0x20  = PARAM RECORDER   (== -1 at crash)
00ccc457  MOV [RBP+0xa0], RAX           ; local_118
00ccc466  MOV RAX, [RBP+0xa0]
00ccc46d  TEST RAX, RAX
00ccc470  JZ  0x00cccc2f                ; guard: skip if +0x20 == 0   (but -1 is != 0 -> falls through)
00ccc476  MOV RAX, [RBP+0xa0]
00ccc47d  CMP [RAX+0x14], 0x0           ; <<< CRASH: deref (-1)+0x14  -> ACCESS VIOLATION
00ccc481  JLE 0x00cccc2f
```

The `+0x28` note recorder is read the same way later (`00cccf1e: [RBP+0x68]->+0x28`, "kind 2"), and
`+0x30` (automation marker array, stride 0x34) later still.

## 2. `param_2` provenance — the decode (caller `FUN_00d96950`, contains ret `0xD974B9`)

```c
// param_3 = per-clip PAINT DESCRIPTOR; param_3+0x58 = the clip's sourceID (EventID)
FUN_01088f00(*(uint*)(param_3+0x58), &local_58 /*obj*/, &local_5c /*patNo*/);
...
if (local_58 == 0) {                       // PATTERN clip branch (sourceID >= 0x50000000)
   if (*(char*)(param_3+0xfc) != '\0')     // clip big enough to render its contents
      FUN_00ccc330(param_3, local_5c, ...); // <<< local_5c = decoded pattern number = the crash param_2
   else { /* small clip: same +0x20/+0x28 !=0 && *(+0x14)>0 test to pick a kind badge — same AV risk */ }
}
```

`FUN_01088f00` (= `FLpl_DecodeClipSourceID`, inline copy):
```c
if ((int)sourceID < 0x50000000) {          // CHANNEL clip
   if (channelCount-1 < sourceID>>16) { warn "Wrong IDToChanPat channel"; *obj=0; *patNo=0xffffffff; }
   else { *obj = FLcr_ChannelListGetItem(...); *patNo=0xffffffff; }   // -> caller takes audio/auto branch
} else {                                    // PATTERN clip
   *obj = 0;  *patNo = (sourceID + 0xb0000000) >> 16;   // = (sourceID - 0x50000000) >> 16
}
```
So `param_2` (`local_5c`) = **1-based pattern number the clip references**. (Channel clips can't reach
the pattern paint: `obj!=0` routes to the audio/automation branch, and the out-of-range channel case
returns `obj=0, patNo=-1` but the caller early-returns on `sourceID<0x50000000 && obj==0` with
"IDToChanPat returned nil unexpectedly".)

## 3. `+0x20` identity + initializer (the field the task hunted for)

`read_memory(0x14aa0c8)` → bytes `68 3b 80 01 00 00 00 00` → `*(0x14aa0c8) = 0x1803B68`.
Unified per-pattern block, base `0x1803B68`, stride `0xC0`, 1-based (slot 0 reserved):

| block off | abs base | field | initializer |
|---|---|---|---|
| +0x00 | `0x1803B68` | pattern name (Delphi WideString ptr) | `FLpat_SetPatternNameAndNotify` |
| +0x08 | `0x1803B70` | color | |
| **+0x20** | `0x1803B88` | **param / automation recorder** (the CRASH field, "kind 3") | **`FLpat_GetOrCreateParamRecorder @0x11D4000(pat,create)`** |
| +0x28 | `0x1803B90` | note recorder ("kind 2" note preview) | `FLpat_GetOrCreateNoteRecorder @0x11D4080(pat,create)` |
| +0x30 | `0x1803B98` | automation-marker array (stride 0x34) | |
| +0x44 | | loop/auto length | |
| +0x50 | | pattern length | |
| +0x58 | | content length (clip source length) | |

`FLpat_GetOrCreateParamRecorder @0x11D4000` (recovered):
```c
undefined8 FLpat_GetOrCreateParamRecorder(int pat, char create) {
  if ((&DAT_01803b88)[pat*0x18] == 0) {                 // 0x18 qwords = 0xC0 bytes ; slot == block+0x20
     void* r = FUN_00f6c8c0(&PTR_FUN_00f60f40, 1, 1);   // alloc param-recorder object
     (&DAT_01803b88)[pat*0x18] = r;  *(int*)(r+0x3c) = pat;
     if (create && *(int*)PTR_DAT_014a81c0 != 0) FUN_010e2ef0(*(int*)PTR_DAT_014aab28 + 1, 0, 0); // notify
  }
  return (&DAT_01803b88)[pat*0x18];
}
```
It guards on `== 0` — a slot holding `-1` (out-of-range garbage) is NOT recreated; and calling it for
an out-of-range `pat` would do an **OOB write** into adjacent `.data`. So it is only safe/meaningful
for an in-range pattern.

## 4. `FLpl_SendPatternToPlaylist @0xCB0780` — full analysis (why we can't just call it)

Signature: `void FLpl_SendPatternToPlaylist(void* songEngine /*=*(0x14A8EA0)*/, int patternNo /*1-based*/)`.
Relevant facts from the full decompile:
- It does **NOT** create the param recorder either. It calls only
  `FLpat_GetOrCreateNoteRecorder(patternNo,0)` and places the pattern clip **only if that note
  recorder has events** (`rec+0x14 > 0`) or a global force flag is set. It relies on `patternNo`
  being an in-range/realized pattern for `+0x20`-safety — same as everything else.
- **It AUTO-PLACES; you cannot target (track, tick).**
  * track = `FUN_00cb0360()` = the first run of EMPTY playlist tracks; if none →
    error "There aren't enough empty playlist tracks available." and it aborts.
  * tick  = active time-selection start (`songArr+0xd4c`) else song-start clamp (`*(0x14a9068+8)`, ≥0).
- It is the whole heavyweight "Send to Playlist": lays out every in-pattern CHANNEL clip on
  auto-assigned tracks (`FLpl_LayoutChannelClipsInPattern`), sets track names/colors, rearranges the
  channel rack, and wraps it all in an undo transaction.
- The bare pattern-clip core it runs (unchanged from `re/25`): resolve `Cobj = *(arr+0x14)`;
  `init` via `vtbl+0x10`; set `+0x00 start`, `+0x04 = pat*0x10000+0x50005000`, `+0x08 = patLen`,
  `+0x0c = FLpl_EncodeClipTrackField(track)`, `+0x13 |= 0x80`, `+0x18=+0x1c=0xffffffff`;
  `insert` via `vtbl+0x08`.

**Verdict:** do NOT switch to it wholesale — it can't place at a chosen track/tick (would force a
place-then-move dance, needs empty tracks, and drags in channel-clip layout + undo). Keep the direct
insert; it already replicates the correct core. Fix the ADD path instead (§5).

## 5. THE FIX (direct-insert path — `FlInjectBridge.cs`)

Root cause = a clip can carry a `sourceID` that decodes to an **out-of-range pattern N** (`block[N]`
past the ~1000-slot static array → `+0x20` reads `-1`). Two holes let that happen:

- **`ValidatePattern` ceiling is `9999`, but FL's real max pattern is `999`** (`FLpat_SetCurrentPattern`:
  `patternCount = #{idx∈1..999 : !IsPatternEmpty(idx)}`; `CreatePatternAsync` scans 1..999). So
  `native_add_pattern_clip(pattern=1000..9999)` passes validation, encodes `0x50005000+(N<<16)`, and
  the paint decodes N=1000..9999 → OOB `+0x20` → `-1` → AV. (An in-range clip is provably safe.)
- (Historical, now closed) non-atomic SHARED scratch build could store a garbage `+0x04` → OOB decode.
  The current `_scratchGate` lease around init→writes→insert closes this; KEEP it.

### FIX 1 (MANDATORY) — never encode an out-of-range/nonexistent pattern
`FlInjectBridge.cs:1385` — tighten the bound to FL's real max, and prefer requiring the pattern to
exist:
```csharp
private const int MaxPatternIndex = 999;                 // was 9999; FL hard cap is 999 (block[N] past this = OOB -> the -1 AV)
```
Better still, in `AddPatternClipAsync` reject a not-yet-created pattern (so the clip can never point
at an empty/garbage slot):
```csharp
ValidatePattern(pattern);                                // now 1..999
if (await IsPatternEmptyAsync(pattern, ct))              // 11db510(pat,1,0,0)
    throw new InvalidOperationException($"Pattern {pattern} has no content yet; add notes before placing a clip.");
```
This alone kills the `-1`: every clip then references an in-range slot whose `+0x20` is `{0, valid}`,
which the engine's `!= 0` guard handles safely.

### FIX 2 (DEFENSIVE, optional) — realize the crash field itself
Alongside the existing note-recorder realize (`FlInjectBridge.cs:1193`), also create the **param
recorder** so the block the paint dereferences is fully materialized:
```csharp
await CallAsync("11d4080", new ulong[] { (uint)pattern, 1 }, ct);  // FLpat_GetOrCreateNoteRecorder -> +0x28 (existing)
await CallAsync("11d4000", new ulong[] { (uint)pattern, 1 }, ct);  // FLpat_GetOrCreateParamRecorder -> +0x20 (ADD THIS)
```
Effect for an in-range pattern: `+0x20` goes from `0` to an empty (count-0) recorder — the paint's
`0 < *(+0x14)` test still skips it, so behavior is unchanged, but the block now matches a fully
realized pattern. NOTE: this is only safe once FIX 1 guarantees `pattern` is in range (calling
`11d4000` on an OOB pattern would OOB-write). It does NOT substitute for FIX 1.

### FIX 3 (already present, KEEP) — atomic private-scratch build
`InsertClipRawAsync` (`:1258`) holds `_scratchGate` across zero→init→7 pokes→insert. Keep it so no
parallel scratch user can corrupt `+0x04` into an OOB sourceID mid-build.

### Crash-free recipe (calls + order + args)
```
1. ValidatePattern(pattern)                                  // 1..999
2. require !IsPatternEmptyAsync(pattern)                      // pattern must exist / have content
3. CallAsync("11d4080", {pattern, 1})                         // note recorder  (+0x28)   [existing]
   CallAsync("11d4000", {pattern, 1})                         // param recorder (+0x20)   [FIX 2, optional]
4. InsertClipRawAsync(startTick,
        sourceID = 0x50005000 + ((uint)pattern << 16),        // 1-based encode (re/21)
        len, track, srcStart=-1, srcEnd=-1)                   // under _scratchGate; init vtbl+0x10, insert vtbl+0x08
   -> RecountActiveClips f6e180(Cobj)                          // [inside InsertClipRaw]
5. RefreshPatternClipsAsync(pattern)                          // d49840(songArr,pat,3)+repaint (re/25, "broken until clicked")
```

## 6. Live-verify (scratch/empty project only — never the user's work)
1. **Confirm the OOB trigger:** breakpoint at ghidra `0xCCC47D` (live `0x5E560000+0x8CC47D`). On hit,
   read `RAX` (should be `-1`) and back-compute `param_2` = `[RBP+0x74]`; assert `param_2 > 999`
   (or an otherwise-not-created slot). Then read the clip's `+0x04` and confirm
   `(src−0x50000000)>>16 == param_2`.
2. **Confirm the field identity:** at the same session read `*(0x14aa0c8)` (== `0x1803B68`) and, for a
   known automation-bearing pattern K, assert `*(0x1803B68 + K*0xC0 + 0x20) == FLpat_GetOrCreateParamRecorder(K,0)`
   and `... + 0x28 == FLpat_GetOrCreateNoteRecorder(K,0)`.
3. **Confirm the fix:** with `MaxPatternIndex=999` + the exists-check, run the `re/25` verify flow
   (create pattern → add note → `native_add_pattern_clip(pat,1,0,0)` → list → move → list); FL must
   not crash and clips must render without a click. Then attempt `native_add_pattern_clip(1500,…)`
   and assert it now THROWS (instead of AV'ing).
4. Confirm `FLpat_GetOrCreateParamRecorder(pat,1)` on an already-empty in-range pattern is a safe
   no-op-ish (creates an empty recorder, count 0, doesn't disturb notes) if FIX 2 is adopted.

## 7. Address table (ghidra, base 0x400000)
| addr | name / role |
|---|---|
| 0xCCC330 | `FUN_00ccc330` playlist pattern-contents paint; crash at `0xCCC47D` deref `block[param_2]+0x20 +0x14` |
| 0xD96950 | `FUN_00d96950` per-clip paint dispatch; decodes sourceID (`0xD974C3`), calls crash func (ret `0xD974B9`) |
| 0x1088F00 | `FUN_01088f00` = inline `FLpl_DecodeClipSourceID`: pattern `patNo=(id−0x50000000)>>16`; channel `obj,idx=-1` |
| 0x11D4000 | **`FLpat_GetOrCreateParamRecorder(pat,create)`** → `&DAT_01803b88[pat*0x18]` = block+0x20 (**the +0x20 initializer**) |
| 0x11D4080 | `FLpat_GetOrCreateNoteRecorder(pat,create)` → `&DAT_01803b90[pat*0x18]` = block+0x28 |
| 0x11DB510 | `FLpat_IsPatternEmpty(pat,chkName,chkColor,chkAuto)` reads +0x20 then +0x28 (proves in-range slots are safe) |
| 0xCBB300 | `FLpat_SetCurrentPattern`; comment confirms patterns are 1..999 |
| 0xCB0780 | `FLpl_SendPatternToPlaylist(songEngine,patNo)` canonical add — auto-places (no track/tick target), doesn't init +0x20 |
| 0xCB0000 | `FLpl_LayoutChannelClipsInPattern` (channel-clip expander into the note recorder) |
| pattern array | `*(0x14aa0c8) = 0x1803B68`, stride 0xC0, 1-based; +0x20 param rec, +0x28 note rec, +0x50 len, +0x58 content len |
