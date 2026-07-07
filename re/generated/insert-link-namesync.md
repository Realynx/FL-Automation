# Insert/Channel ↔ Mixer ↔ Playlist LINK + NAME-SYNC (FLEngine_x64.dll)

Static RE for the "rename a track group once, propagate everywhere" feature. Goal: an AI
`organize the project` prompt renames a channel and its linked **mixer track** + **playlist
track(s)** to one consistent name.

Addresses are Ghidra absolute (image base `0x400000`). RVA = abs − 0x400000.
Live rebase this session: `live = 0x5E560000 + (abs − 0x400000)` (recompute per session).

---

## TL;DR

- **The link is stored as plain index fields**, resolved by scanning:
  - channel → mixer track: `TChannel + 0x288` (target FX track). Already exposed as
    `channels.getTargetFxTrack`/`SetChannelFxRouteAsync`.
  - playlist track → mixer track: `plTrack + 0xdc` (linked mixer track index).
  - playlist track → channel:    `plTrack + 0xe0` (linked channel index).
  - There is **no** channel-side back-pointer to its playlist track; you find it by scanning
    playlist tracks for `+0xe0 == channelIdx` (or `+0xdc == mixerTrack`).
- **FL auto-syncs names ONE WAY only: from the MIXER track outward.** Setting a mixer track's
  name (`mixer.setTrackName`, core `FUN_011c2810 @0x11C2810`) automatically writes the same
  name **and color** into the linked channel and every linked playlist track (all arrangements).
  Renaming the **channel** or the **playlist track** does **not** propagate.
- ⚠️ **Doc correction:** `re/generated/controls-playlist.md` lists `+0xdc = linked channel,
  +0xe0 = linked mixer`. That is **swapped**. The actual sync resolver `FUN_011aac20` compares
  `+0xdc` against a *mixer* index, and `re/12-controls-harvest.md:27` independently has it right:
  **`+0xdc = mixerTrack`, `+0xe0 = channel`.** Use +0xdc=mixer / +0xe0=channel.

---

## 1. How the link is stored

### Playlist track struct (stride `0x114`, `track[i] = FLpl_GetCurrentArrangement()@0x11E32C0 + i*0x114`, i=1..500)
| off | field | notes |
|---|---|---|
| `+0x24` | name (Delphi UStr) | default "Track N" |
| `+0x2c` | color (BGR) | |
| `+0xd8` | **track mode** | 0 = normal, 1 = audio, 3 = instrument (per re/12; controls-playlist says 1=instr/3=audio — mode value is not needed for naming) |
| `+0xdc` | **linked MIXER track index** | audio track routes here; `-1`/0 = none |
| `+0xe0` | **linked CHANNEL index** | instrument track points at its channel |

### Channel (TChannel)
| off | field | notes |
|---|---|---|
| `+0x288` | **target mixer FX track** | the channel↔mixer link. Read = `channels.getTargetFxTrack @0xE035B0`. Set = `FLcr_ApplyChannelFxRoute @0xE03640` → `FL_DispatchCommand(recEvt+8, track, 0x3DD)` |
| `+0x9c`  | recEventId (`recTag<<16`) | used to build the FX-route cmdId |
| name via VMT `+0x70` (set) / `+0x68` (get); color via VMT `+0x78` | | `channels.setChannelName @0xE00AD0` / `FLcr_ApplyChannelName @0xE00A40` |

### The "link" natives are NOT namers
- `mixer.linkChannelToTrack(channel, track, select)` @`0xE0A050` → op `FUN_00e09ef0`:
  **just does `FL_DispatchCommand(channel.recEvt+8, track, 0x3DD)`** = set the channel's FX route
  (identical to our `SetChannelFxRouteAsync`), then optionally selects the mixer track UI. No rename.
- `mixer.linkTrackToChannel(mode)` @`0xE09DD0` → op `FUN_00e09d40` → `TFXForm_LinkMenuClick
  @0x11A01C0`: the mixer right-click "route selected channels to this track / consecutive tracks"
  action. It FX-routes each **selected** channel to sequential mixer tracks. No rename either.

So "linking" in FL == establishing the FX route (channel `+0x288`) plus, for playlist tracks, the
`+0xdc/+0xe0` index (set when you create an audio/instrument track). The name propagation is a
separate mechanism living entirely in the mixer name setter.

---

## 2. The NAME-SYNC mechanism (mixer track is the hub)

### `mixer.setTrackName` core — `FUN_011c2810 @0x11C2810`  (call it `FLmx_SetTrackNameAndSync`)
Wrapper chain: `FLpy_mixer_setTrackName @0xE061D0` → `FLmx_SetTrackName_op @0xE06160` →
`FUN_011c2810(int trackIdx, UStr name, char syncFlag=1)`.

What it does, in order:
1. Writes the name into the **mixer** track: `Delphi_UStrAsg(mixerTrack + 0x0c, name)`
   (`mixerTrack = *g_MixerTrackArrayPtr[0x14A7EB0] + trackIdx*0x1474`).
2. Refreshes the 10 FX-slot plugin display names.
3. **Channel sync:** `iCh = FUN_011aaa30(trackIdx, 0)` → the first channel whose
   `+0x288 == trackIdx` and that is a generator (`FUN_00f12b30`). If found:
   - `channel + 0x7c = mixerTrack + 0x14` (secondary color),
   - `channel.VMT[0x70](channel, mixerTrack+0x0c)`  → **set channel name**,
   - `channel.VMT[0x78](channel, mixerTrack+0x04)`  → **set channel color**.
4. **Playlist sync:** for every arrangement (`FLpl_GetArrangementCount/ByIndex`):
   `iTrk = FUN_011aac20(arrObj, trackIdx)` → the playlist track with `+0xdc == trackIdx`. If `>0`:
   - `Delphi_UStrAsg(track+0x24, mixerTrack+0x0c)` → **set playlist track name**,
   - copies color (`track+0x2c/+0x30`, custom-color flag `track+0x70`) from the mixer track.
5. If `syncFlag`: broadcasts UI refresh + dirties the project.

`FUN_011c2810` is the shared "apply name+color to a mixer track and cascade" routine — its 20+
callers are all mixer/group ops (`TFXForm.CreateGroupMenuClick`, `AutoColorGroupMenuClick`,
`RouteSelToThis1MenuClick`, color menus, etc.), never a channel-rack or playlist rename path.

### The two resolvers (how the cascade finds the linked items)
- `FUN_011aaa30(mixerTrackIdx, mode)` — scans the all-channels list `*0x14A98D8`; returns the
  first channel index with `channel+0x288 == mixerTrackIdx` (& is-generator). **Note: only the
  FIRST channel routed to that track is synced** — multiple channels on one mixer track → only one
  renamed by the cascade.
- `FUN_011aac20(arrObj, mixerTrackIdx)` — walks a curated track-index list at `arrObj+0x21c7c`;
  returns the playlist track index with `+0xdc == mixerTrackIdx`.

### The other two name setters do NOT propagate
- **Channel** `FLcr_ApplyChannelName @0xE00A40`: resolves channel, calls `VMT[0x70]` (writes the
  channel's own name + notifies its plugin host + UI refresh). Touches no mixer/playlist struct.
- **Playlist** `FLpl_SetTrackNameAndColor @0x11E7940` → `FLpl_SetTrackName_inner @0x11E75F0`:
  `Delphi_UStrAsg(track+0x24, name)` only. No mixer/channel write.

**Conclusion:** FL does NOT sync "any → all". It syncs **mixer → (channel + playlist)** only.
The user's "rename any one and it propagates" is really "rename the *mixer track* and it
propagates." Our tool must therefore drive the rename **through the mixer track** (when one
exists) and additionally set the channel/playlist directly for the cases the cascade misses.

---

## 3. What exists TODAY vs needs new RE

| Piece | Status |
|---|---|
| Set channel name | ✅ EXISTS — `channels.setChannelName`/`FLcr_ApplyChannelName @0xE00A40` (bridge: channel name control) |
| Set playlist track name | ✅ EXISTS — `SetTrackNameAsync` → `FLpl_SetTrackNameAndColor @0x11E7940` |
| Read channel→mixer link | ✅ EXISTS — `TChannel+0x288` (`getTargetFxTrack @0xE035B0`) |
| Set channel→mixer route | ✅ EXISTS — `SetChannelFxRouteAsync` → `FLcr_ApplyChannelFxRoute @0xE03640` |
| Set mixer track name (+ auto-cascade) | 🟡 native fully RE'd (`FUN_011c2810 @0x11C2810` / `FLpy_mixer_setTrackName @0xE061D0`) but **no bridge wrapper yet** — thin new method |
| Resolve channel→playlist track | 🟡 NEW (trivial): scan `plTrack+0xe0 == channelIdx` (pure peek, no FL call) |
| Resolve mixer→playlist track | 🟡 NEW (trivial): scan `plTrack+0xdc == mixerTrack` (pure peek) or reuse `FUN_011aac20` |
| Field offsets +0xdc/+0xe0 | 🟡 confirmed via sync code; **1 live peek recommended** to settle the doc conflict |

No new *structural* RE is required — every link field and every setter is already mapped.

---

## 4. PLAN — bridge method + AI tool

### Tool: `native_rename_track_group(target, newName)`
`target` = channel index (default: the selected channel). Renames the channel + its linked mixer
track + its linked playlist track(s) to `newName`.

### Bridge implementation (native, main thread) — recommended composite (robust, no reliance on cascade)
1. Resolve `TChannel` from index (`FLcr_ChannelListGetItem(*0x14A98D8, idx)`).
2. **Channel:** set name via existing channel-name setter (`FLcr_ApplyChannelName` path). Always.
3. `mixerTrack = *(int*)(TChannel+0x288)`.
   - If `mixerTrack > 0` (a dedicated insert, not Master): set the mixer track name. Two options:
     - (a) **preferred one-call:** `FUN_011c2810(mixerTrack, uStr(newName), 1)` — renames the mixer
       track *and* cascades to the linked channel + playlist track(s) + color, exactly as FL does.
     - (b) portable: call `FLpy_mixer_setTrackName` op path (needs scripting ctx).
   - If `mixerTrack == 0` (Master): skip — never rename Master.
4. **Playlist:** iterate `track[i]` (i=1..500) of the current arrangement (optionally all
   arrangements). For each with `*(int*)(track+0xe0) == channelIdx` **OR**
   (`mixerTrack>0 && *(int*)(track+0xdc) == mixerTrack`): call
   `FLpl_SetTrackNameAndColor(root, i, uStr(newName), keepColor)`.
   (This covers instrument tracks linked directly to the channel and audio tracks linked to the
   mixer — including cases where step 3's cascade didn't fire, e.g. Master-routed, or a
   not-first channel on the track.)

Steps 2 and 4 make the result correct even without the cascade; step 3(a) additionally gives the
native color-sync and matches FL's atomic behavior. UStr builder for names already exists (used by
`FLpl_SetTrackNameAndColor`).

### Optional read tool: `native_get_track_links(channel)`
Return `{ mixerTrack: chan+0x288, playlistTracks: [i where +0xe0==idx or +0xdc==mixerTrack] }` so
the AI can preview/verify links before renaming and report what it changed.

### Phasing
- **Phase 1 (ship now, minimal new code):** composite of existing setters (steps 2 + 4) + read
  `+0x288` + the trivial playlist link-scan. Zero new core RE. This alone satisfies "rename channel
  + linked mixer + linked playlist together."
- **Phase 2 (polish):** wire `FUN_011c2810` (3(a)) for the single-call mixer cascade incl. color;
  needs only a thin wrapper + confirm the arg convention `(int, UStr, char)`. Low risk.
- **Phase 3 (nice-to-have):** `native_get_track_links` + a batch `organize` that walks all channels
  and normalizes names/colors across the three domains.

### One live check before shipping Phase 1
Peek a loaded project's `plTrack+0xdc` / `+0xe0` on an instrument track vs an audio track to
confirm the +0xdc=mixer / +0xe0=channel mapping (two internal docs disagreed; sync code says
+0xdc=mixer). Then patch `controls-playlist.md`.

---

## Key addresses
| abs (base 0x400000) | symbol / meaning |
|---|---|
| `0x11C2810` | `FUN_011c2810` mixer setTrackName **core + cascade** (rename me `FLmx_SetTrackNameAndSync`) |
| `0xE061D0` / `0xE06160` | `FLpy_mixer_setTrackName` / `FLmx_SetTrackName_op` |
| `0x011aaa30` | resolve mixer track → linked channel index (scan `+0x288`) |
| `0x011aac20` | resolve mixer track → linked playlist track index (scan `+0xdc`) |
| `0xE00A40` / `0xE00AD0` | `FLcr_ApplyChannelName` / `channels.setChannelName` (no propagate) |
| `0x11E7940` / `0x11E75F0` | `FLpl_SetTrackNameAndColor` / `FLpl_SetTrackName_inner` (no propagate) |
| `0xE035B0` / `0xE03640` | channel `getTargetFxTrack` (`+0x288`) / `FLcr_ApplyChannelFxRoute` |
| `0xE0A050` / `0xE09DD0` | `mixer.linkChannelToTrack` (=set FX route) / `linkTrackToChannel` (=TFXForm link menu) |
| `0x11A01C0` | `TFXForm_LinkMenuClick` (route selected channels → mixer tracks; no rename) |
| `0x11E32C0` | `FLpl_GetCurrentArrangement` (playlist track root) |
| `0x14A7EB0` | `g_MixerTrackArrayPtr` (mixer track N = `*ptr + N*0x1474`) |
| `0x14A98D8` | all-channels list (count `+0x10`, item via `FLcr_ChannelListGetItem @0xF00F80`) |
