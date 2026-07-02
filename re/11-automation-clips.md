# Automation Clips — RE recipe (FLEngine_x64.dll)

Static Ghidra RE of FL Studio automation clips: read / add / update / delete points, curve &
tension, target binding, and clip creation. Addresses are **Ghidra** addresses (image base
0x400000). Live address = `liveBase + (ghidraAddr - 0x400000)` (same convention as the rest of the
catalog). Program = `FLEngine_x64.dll`.

> Constraint honored: this is an operation/control layer only — no DRM touched. Ghidra functions
> were renamed (FLac_/FLenv_/FLcr_ prefixes) for human readability. No inject / no save_program done
> here (lead saves + integrates + live-tests).

---

## 1. Object model — how to reach a clip's points

An automation clip is just a **channel** whose generator is the internal "Automation Clip"
(preset file `AutoTrack.fst`). Its curve lives in an envelope object hanging off the channel:

```
channel            = FLcr_ChannelListGetItem(*(0x14A98D8), idx)   // existing catalog accessor @0xF00F80
container          = *(channel + 0x390)        // automation/envelope holder (local_48[0x72])
env                = *(container + 0x10)        // the envelope object that owns the point list
pointsArr          = *(env + 0x28)             // Delphi dynamic array, element stride 0x20
count              = *(int*)(pointsArr - 8)     // Delphi dynarray length is 8 bytes before data
totalLength        = *(double*)(env + 0x68)     // clip length (max abs time); AddPoint clamps to this
recompute()        = (*(*env + 0x40))(env)      // env vtbl[8] — MUST call after any edit
```

`channel + 0x9c` = the clip's own (source) event id. `channel + 0x7a4` = channel index.
(These match the existing channel catalog: +0x9c recEventId, +0x7a4 index.)

### Point record (0x20 = 32 bytes) at `pointsArr + i*0x20`
| off | type  | meaning |
|-----|-------|---------|
| +0x00 | float | **delta-time** from the previous point. Absolute time of point i = Σ point[0..i].+0x00. point[0].+0x00 = absolute offset of the first point from clip start. |
| +0x04 | float | **value**, normalized 0.0–1.0 |
| +0x08 | float | **tension** (0 = linear; ~ -1..+1) |
| +0x0C | byte  | **curve type** (shape enum: single/double curve, hold, stairs, smooth, pulse, etc.) |
| +0x0D..+0x1F | — | internal/cached coefficients; recompute() refills them. Zero on write. |

**Time axis:** delta/abs times are in clip-local units; a fresh clip's default 2nd point is at
double **4.0** (one 4/4 bar). `totalLength = *(env+0x68)` is the clamp bound. Confirm the exact
unit (beats vs bars) live by reading an existing clip.

**Read a clip = pure peeks:** walk `pointsArr`, accumulate +0x00 for absolute time, read +0x04/
+0x08/+0x0C. `FLac_GetPointAbsTime(env, idx) @0xB327E0` returns the cumulative time of point idx if
you'd rather call it; `FLac_EvalValueAtTime(env, time) @0xB34C30` samples the curve (return reg is
likely XMM0 — peeking the point fields directly is the reliable read path).

---

## 2. Core engine functions (renamed in Ghidra)

| Ghidra addr | Name | Signature / notes |
|------|------|------|
| 0xB30780 | `FLac_AddPoint` | `(env*, double absTime, float value, float tension, char doSnapshot) -> int idx`. Clamps absTime to [0,totalLength], converts to delta, fixes next point's delta, grows the array, calls recompute. **Float args ⇒ needs XMM1/XMM2/XMM3 (see §4).** |
| 0xB30AD0 | `FLac_DeletePoint` | `(env*, int idx)`. vtbl[6]@+0x30 guards min-point count; folds the deleted delta into the next point; shrinks array; recompute. **Integer-only — bridge-callable.** |
| 0xB327E0 | `FLac_GetPointAbsTime` | `(env*, uint idx) -> double` cumulative time. |
| 0xB34C30 | `FLac_EvalValueAtTime` | `(env*, time) -> value` (sample the curve). |
| 0x417FC0 | `_DynArraySetLength` (wrapper → 0x417B70) | `(arrFieldPtr=env+0x28, typeInfo, dimCount=1, newLen)`. Delphi SetLength. typeInfo = `&DAT_00B2C678` (the point element RTTI). **Integer/pointer-only — bridge-callable.** |
| 0xF00C50 | `FLac_FillDefaultLine` | `(container, value)` → writes the default flat 2-point line (t=0 and t=4.0, both = value). |
| 0xF00900 | `FLenv_BuildDualEnvelope` | builds the container's two envelopes (env at +0x10 and +0x18, mode byte +0x58). |
| 0xB2FE10 | `FLac_NotifyPointsChanged` | `(env, idx, ±1)` add/remove notify (called internally by add/delete). |
| 0x108A1A0 | `FLac_CreateAutomationClipForEvent` | `(uint targetEventId, char placeInPlaylist, void* targetDesc, char doPlace, char withUndo) -> channel*`. Full create (see §3). |
| 0xF22770 | `FLcr_CreateChannelFromFst` | `(idx=0xFFFFFFFE append, DelphiStr fstName, 1, 1) -> channel*`. Higher-level "create channel from .fst" than FLcr_InsertChannel. |
| 0xF53EF0 | (refresh) | post-create UI refresh used by the creator. |

### UI-level editors (reference; mirror the data ops above with undo + repaint)
- `FLac_UISetPointValue @0xCC8010` — sets selected point (`editor+0x18`) value with a value-entry dialog.
- `FLac_UIChangePointCurve @0xCC7CB0` — writes curve byte (+0x0C) from a menu item.
- `FLac_UIPointEditDispatch @0xCC6930` — the mouse dispatcher: add point, delete point, default
  tension, move point, move tension. Useful as a reference for the exact field writes & undo labels
  ("automation add point", "automation delete point", "automation set point value", "automation
  move tension", "automation change point curve").

Undo transaction begin = `FUN_00F382C0(*(0x14A9360), DelphiStr label, groupId=*(owner+0x7a4))`.
Build the Delphi UnicodeString label with `FUN_0054C5E0(&slot, L"...")` (already in the catalog's
Delphi-string notes). Undo is optional for headless edits.

---

## 3. Target links — what a clip controls

Target links live in a **global registry**: `reg = *(0x14A81B8 + 8)` (a TList: items ptr `reg+0x08`,
count `reg+0x10`, capacity `reg+0x14`). Each entry (alloc `FUN_00EA2DB0(8)`):

```
entry + 0x00 : target descriptor object (optional VST/param link; 0 if plain event)
entry + 0x08 : SOURCE event id  = clip channel's (channel+0x9c)
entry + 0x10 : TARGET event id  = the param the clip drives
```

- **Read a clip's targets:** scan `reg`; for entries where `entry+0x08 == channel+0x9c`, collect
  `entry+0x10`. Resolve a human name with `FLgl_cmd_GetEventIDName(&out, targetEventId, ..)`.
- **Add a target link:** `FLac_AddTargetLink @0xE8DEB0 (clipObj, int targetEventId)` — dedups, appends
  an entry, posts WM 0x551 to refresh. (clipObj+0x12b4 = channel; uses channel+0x9c as source.)
  Both args integer/pointer ⇒ bridge-callable. NOTE: FL refuses adding links during playback
  ("You must stop playback before adding new target links").
- One param may have multiple clips ("Choose a clip… multiple automation clips linked to that
  parameter"); links are many-to-many through this registry.

**Identifying which channels are automation clips:** robust = a channel whose `channel+0x9c`
appears as a SOURCE id (`entry+0x08`) in `reg`. (A non-null `channel+0x390` alone is not unique —
regular channels also have envelope containers.) Confirm a cheaper discriminator (generator id at
`channel+0x64`, or a clip flag) live.

---

## 4. Bridge integration plan (works with the CURRENT bridge — no XMM support needed)

The bridge passes **integer registers only** and `callabs` returns RAX only (per catalog). So:

**Callable directly:** `FLac_DeletePoint(env, idx)`, `_DynArraySetLength(env+0x28, typeInfoLive, 1,
N)`, env `recompute` via vtbl[8], `FLac_CreateAutomationClipForEvent(eventId, 1, 0, 1, 1)`,
`FLac_AddTargetLink(clip, targetId)`, `FL_DispatchCommand`.
**NOT directly callable:** `FLac_AddPoint` (needs `double`/`float` in XMM1/2/3).

### Read clip points  — peeks only
```
container = PeekAbs(channel + 0x390); env = PeekAbs(container + 0x10)
arr = PeekAbs(env + 0x28); n = PeekAbs(arr - 8) as int32
abs = 0
for i in 0..n-1:
    p = arr + i*0x20
    abs += f32(PeekAbs(p + 0x00))         // delta -> running absolute
    value   = f32(PeekAbs(p + 0x04))
    tension = f32(PeekAbs(p + 0x08))
    curve   = u8 (PeekAbs(p + 0x0C))
```

### Update an existing point  — poke + recompute (no realloc)
```
p = PeekAbs(env+0x28) + idx*0x20
PokeAbs(p + 0x04, f32bits(value))      // and/or +0x08 tension, +0x0C curve byte
vt = PeekAbs(env); CallAbs(PeekAbs(vt + 0x40), env)   // recompute
// then refresh (see below)
```

### Author / rebuild a clip's curve  — integer-only, avoids XMM AddPoint
For a target set of points sorted by ascending absolute time `t[i]` (values/tension/curve):
```
env = PeekAbs(PeekAbs(channel+0x390) + 0x10)
CallAbs(FLcr_…SetLength=0x417FC0live, env+0x28, typeInfoLive=0xB2C678live, 1, N)  // grow/shrink to N
arr = PeekAbs(env+0x28)
prev = 0
for i in 0..N-1:
    p = arr + i*0x20
    PokeAbs(p+0x00, f32bits(t[i]-prev)); prev = t[i]     // delta encoding
    PokeAbs(p+0x04, f32bits(value[i]))
    PokeAbs(p+0x08, f32bits(tension[i]))
    PokeAbs(p+0x0C, u8(curve[i]))                          // (+0x0D..0x1F left as zero/garbage; recompute fixes caches)
CallAbs(PeekAbs(PeekAbs(env)+0x40), env)                  // recompute
```
(Appending a single in-order point = SetLength(count+1) then write the last slot. Inserting in the
middle is easier via full-rebuild than manual memmove.)

### Delete a point
```
CallAbs(FLac_DeletePoint=0xB30AD0live, env, idx)   // handles delta-fixup + shrink + recompute
```

### Create a clip bound to a parameter
```
CallAbs(FLac_CreateAutomationClipForEvent=0x108A1A0live, targetEventId, 1, 0, 1, 1)
```
`targetEventId` = the FL event id of the param to automate (same id space used by plugin-param
dispatch / FL_DispatchCommand). Returns the new channel*. It auto-creates the channel from
AutoTrack.fst, names/colors it, registers the target link, and fills the default line. After create,
read its env via §1 and author points as above. (`targetDesc`=0 for a plain event target.)

### Refresh after edits
Reuse the catalog's safe refresh chain (the one used for notes): `FUN_011D4140(patIdx,1)` +
`FUN_00F53D30` + `FUN_00D421C0` + `FUN_0107EAD0` — and/or `FUN_00F53EF0()` which the creator uses.
Do NOT use FUN_0107EB90 (kills audio). All edits run on FL's main thread (the bridge already
marshals there).

---

## 5. Confirm-live checklist for the lead
1. Time unit of point delta/abs and `env+0x68` totalLength (beats vs bars; default 2nd point = 4.0).
2. Discriminator for "channel is an automation clip" (registry source-id scan is robust; check
   `channel+0x64` generator id for a cheaper test).
3. `env+0x390 → +0x10 → +0x28` chain on a real clip (peek and verify 0x20-stride floats look sane).
4. `_DynArraySetLength` element typeInfo live addr (`0xB2C678`) and that SetLength on the point
   array doesn't disturb other channel state.
5. Whether a lighter refresh suffices after point pokes (recompute alone may update playback; the UI
   block in the playlist may need the note-refresh chain).

## 6. Optional bridge enhancement
Adding XMM/float argument support to the bridge ("callf") would let you call `FLac_AddPoint`
directly and also unlocks other float-arg engine funcs. Not required — §4 is integer-only — but a
clean general upgrade (same gap as the deferred XMM0 return-read).
