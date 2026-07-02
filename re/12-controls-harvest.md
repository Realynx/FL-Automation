# Controls RE harvest (turn 8) — ready-to-integrate recipes

Static-RE results from a fan-out of sub-agents. Addresses are FLEngine_x64.dll, base 0x400000.
Automation clips are in `re/11-automation-clips.md`. Shipped+verified this turn: plugin params, sample→channel.

## Recurring bridge limitation: XMM args/returns
Several functions pass/return floats/doubles in XMM regs, which the bridge `call`/`callabs` (GP-register
ints + RAX return) can't handle. Affected: plugin-param VALUE read (getParamValue → float in XMM0),
automation `FLac_AddPoint` (double/float args), transport `FLtr_SeekToSongTick` (double XMM0).
**Recommended bridge enhancement:** add a `callf`/`callx` command to dllmain.cpp that loads up to N args into
XMM0..3 and returns XMM0 (double). Unlocks param read-back + value displays, direct automation point add, and seek.
Integer-only workarounds exist (poke + recompute) for automation; param read can be skipped (set-only) for now.

## Read piano-roll notes (PURE PEEKS — verified, no FL call) — task #27
- `noteRec = *(0x01803B90 + patIdx*0xC0)` (NULL = pattern empty). Current pattern idx = `*(int*)(*(0x14AB580))`.
- `count=*(int*)(noteRec+0x14)`, `data=*(noteRec+8)`, stride `0x18`. Notes are position-sorted.
- Note record (0x18): pos`+0`(int tick), realFlag`+4`(u16 0x4000), channel`+6`(u16), len`+8`(int),
  key`+0xC`(u16, 60=C5), finePitch`+0x10`(u8 c=0x78), pan`+0x12`(u8 c=0x40), flags`+0x13`(u8 bit0x20=muted),
  velocity`+0x15`(u8 0..128), modX`+0x16`/modY`+0x17`(u8 c=0x80).
- `FLpr_NoteBinarySearchByPos @0x11E0AB0` = first idx with pos>=X. No clean FLpy note-read accessor — walk the struct.
- → tool `native_get_notes(pattern, channel?)` returns pos/key/len/vel/muted list.

## Playlist TRACK state (verified field map + setters) — task #21
- `root = FLpl_GetCurrentArrangement() @0x11E32C0`; `track[i] = root + 0x24 + i*0x114` (i=1..500).
- Fields: name`+0x24`(UStr), color`+0x2c`(BGR 0x00BBGGRR), height`+0x34`(float, 1.0=default), enabled`+0x3c`(1=audible/0=mute),
  grouped`+0x3e`(1=grouped w/ track above), muteLock`+0xd5`, trackMode`+0xd8`(0 normal/1 audio/3 instrument),
  mixerTrack`+0xdc`, channel`+0xe0`, collapsed`+0xe4`, selected`+0xe5`. (Do NOT poke +0x60..0x8c — layout cache.)
- Setters (preferred, self-refresh): name+color `FLpl_SetTrackNameAndColor(root,i,uStr,bgr) @0x11E7940`;
  color `@0x11E7610`; selection `FLpl_SetTrackSelection(root,i,mode) @0x11E9C30`; solo `FLpl_SetTrackSolo @0x11E9810`;
  group selected `FLpl_GroupSelectedTracks(root,0/1) @0x11EA6B0`.
- mute/collapse/lock/height/grouped: poke field + repaint `FLpl_RepaintPlaylist(*0x14aab88) @0xDA40C0`.
- Arrangements: count `0x11FB1A0`, name `0x11FB160`, add `0x11FABC0`, delete `0x11FB1C0`, setCurrent `0x11FC880`.
- → tools native_list_playlist_tracks, native_set_track_name/color/mute/solo/collapse/group/height/select.

## Transport / song state — task #29
- Playhead READ: songTick `*(int*)0x14A92D0`; ticks/beat `*(int*)0x14A9E40`; ticks/bar `*(int*)0x14A8540`.
- Song-vs-pattern mode READ `*(int*)0x14A8670` (0 pat/1 song); TOGGLE `FLgl_GlobalCommandDispatch(15,1,2,0xf)`.
- Playing `*(int*)0x14A81C0==1`. Controls: PLAY(10,1,2,8) STOP(11,0,0,8) RECtoggle(12,1,0,0xf).
- Song arrangement obj `arr = *(0x14ABA80) ?: *(0x14AAB88)` (`FLpl_GetSongArrangementObj @0xE1EC70`).
- Loop sel: start `*(int*)(arr+0xd4c)`, end `+0xd50`. Song length(bars) `*(int*)(arr+0xb04)`.
- Markers: mgr `*(arr+0xd5c)`; arr `*mgr`; count `*(int*)(arr-8)`; entry `+i*0x34`: tick`+0`, type`+4`, name=UStr@`*(entry+8)`.
  Add `FLtr_AddTimelineMarkerCore(arr,tick,nameStr,0,4,4) @0xD523C0`; jump ops 5/6.
- Seek `FLtr_SeekToSongTick(double,byte) @0x10E3470` — XMM double (needs bridge enh) or poke 0x14A92D0 + refresh.
- → tools native_get_song_state, native_set_song_mode, native_set_loop, native_list/add_marker (+ seek when XMM lands).

## Project lifecycle (headless-capable) — task #26
- `projMgr = *(*(0x14abca8)+0x10)`; ctrl = `*(0x14a8750)`; song obj = `DAT_01581200`; cur path = `DAT_01581298`; FL install = `DAT_01580F40`.
- OPEN `FLproj_OpenProject(ctrl, wpath, 1) @0x10D50C0` (missing path → Empty.flp).
- SAVE `FLproj_SaveProjectToFlp(DAT_01581200, wpath, 1, &outPath) @0x10D6190`; collect-samples wrapper `@0x10D6830`.
- NEW: set `*(byte*)(projMgr+0x11)=1` then `projMgr->vtbl[0x30](projMgr,1,1,0)`.
- Metadata: title `@0xE11280`, author `@0xE11330`, genre `@0xE113E0`.
- → tools native_open_project, native_save_project, native_new_project. ⚠️ live-confirm ctrl/song globals.
- **RENDER/EXPORT is NOT cleanly headless**: goes through TRenderForm dialog. Best we can do = trigger
  `FLproj_FileExportFormat @0xE46EF0` → opens the export dialog for the user. True headless bounce = dedicated future pass.

## Browser tabs / WebView2 (verdict) — tasks #23/#24/#25
- Tabs: browserView `*0x157ffb8`; tabs `*(view+0x158)`; arr `*(tabs+0x10)`, count `*(arr-8)`; tab fields: root`+0x64`(UStr),
  name`+0x6c`(UStr), filter`+0x74`(UStr), id`+0xf4`. Select `FLbrz_SelectTabById @0x9AC590`; clone-tab `@0x9AC910`;
  browse-to-path `FLbrz_BrowseToPathInTab @0xF8C2D0`. Can enumerate/select/add(clone)/rename/re-root from the bridge.
- BUT a tab's content is a DataBrowser tree, NOT a webview. FL's webview = stock Delphi `Vcl.Edge.TCustomEdgeBrowser`
  (WebView2). **Verdict: a real interactive AI chat tab INSIDE FL's browser is the heavy path** (proprietary Delphi
  component authoring). **Recommended for task #25: host WebView2 in OUR WPF app** (standard MS control) talking to the
  app for LLM calls — or a custom WPF chat. Driving FL's existing tabs (navigate the user to X) IS feasible now.

## Render / export to audio (semi-headless) — task #33
- Render core (synchronous, writes WAV): `eng = *(*0x14ABCA8 + 0x8c)`; `result = (*(*eng+0x18))(eng, outPathStr, &config24, 1)` → 2=success/1=fail/0=cancel. Intermediate is always WAV; MP3/FLAC/OGG = post-WAV convert per the format field.
- `config24` = 24 bytes from `TRenderForm+0xe14..+0xe2f` (range/format/samplerate/bitdepth), flags `[2]=1,[4]=2`; `+0xe14` low byte ==1 ⇒ full SONG else current pattern. ⚠️ Capture once: dump those bytes after a manual WAV export, then parameterize.
- Recommended (reuses FL logic, no clicks): create `TRenderForm` via `FLui_CreateFormFromClassRef@0x10C2AA0`; set out dir `@+0xdd8`, name `@+0xde0`, range `@+0xe14=1`, mastering off (`+0xdfc & 1 == 0`); call `TRenderForm.StartBtnClick@0xDD25C0`. `FLrender_ValidatePreconditions@0xDD59D0` is silent if song non-empty + dir writable + no mastering refs (the only dialog triggers).
- Async: StartBtnClick spawns worker threads; poll worker `+0x10==1` done / `+0x11==1` cancelled. Core (`FLrender_MixDownWorkerExecute@0xDE0F80`). ⚠️ live-confirm a freshly-created (unshown) form populates e14..e2f.

## Playlist CLIP placement (arrangement) — task #28
- ✅ CORRECT (from decompiling the engine's own FLpl_SendPatternToPlaylist@0xCB0780): the clip collection is
  `Cobj = *(FLpl_GetCurrentArrangement@0x11E32C0 + 0x14)` — a DEREFERENCE of arr+0x14 (the prior bug used the address
  inline / the wrong e1ec70 object). Cobj: vtbl@0, data@8, stride@0x10, count@0x14. vtbl=*(Cobj); initFn=*(vtbl+0x10),
  insertFn=*(vtbl+0x8) (offsets were right; object identity was wrong). ADD: zero tmp[0x48]; callabs initFn(Cobj,tmp);
  poke fields (start@0,sourceID@4,len@8,track@0xc=500-trk,+0x13|=0x80,+0x18=+0x1c=0xffffffff); callabs insertFn(Cobj,tmp);
  FLpl_RecountActiveClips(Cobj)@0xF6E180; repaint. DELETE: GetClipByIndex(Cobj,i) → clear +0x13&~0x80 → Recount → repaint.
  Audio/automation clip: same, sourceID=channelIdx<<16. ALL clip methods must use this same Cobj.
- Clip collection (orig note) `C = FLpl_GetCurrentArrangement()@0x11E32C0 + 0x14` (= `*(*0x14AAB88+0xd04)+0x57c`). Object:
  `C+0x08`=data, `C+0x10`=stride, `C+0x14`=count, `C+0x48`=active count. `FLpl_GetClipByIndex(C,i)@0x11E0DD0`.
- Clip struct: startTick`+0`, sourceID`+4`(int), lengthTicks`+8`, track`+0xc`(i16 = 500−trackNo, enc `FLpl_EncodeClipTrackField@0xB47000`),
  flags`+0x13`(0x80=ACTIVE required, 0x20=muted), srcTrim`+0x18`/`+0x1c`(-1=full), UID`+0x20`, stretch`+0x40`(double).
- **sourceID:** pattern clip = `0x50005000 + (patIdx<<16)` (≥0x50000000); audio/automation clip = `channelIdx<<16` (<0x50000000,
  references a channel-rack channel). Decoder `FLpl_DecodeClipSourceID@0xF72D40`. To place a sample: add it as a channel
  (native_add_sample_channel), then add a clip with sourceID = thatChannel<<16 (live-confirm).
- ADD (from `FLpl_SendPatternToPlaylist@0xCB0780`): zero tmp[0x48]; `add=*(*C+8)`, `init=*(*C+0x10)` (resolve live);
  `init(C,&tmp)`; set tmp +0(start) +4(sourceID) +8(len = pattern *0x14AA0C8+patIdx*0xC0+0x50) +0xc(500−trackNo) +0x13|=0x80
  +0x18=+0x1c=0xffffffff; `add(C,&tmp)`; `FLpl_RecountActiveClips(C)@0xF6E180`; `FLpl_RepaintPlaylist(*0x14AAB88)@0xDA40C0`.
- MOVE: poke +0(start)/+0xc(track)+repaint. RESIZE: poke +8(len) / audio trim `FLpl_SetClipSourceRange@0xF71A70`.
  MUTE `FLpl_SetClipMuted@0xF71A60`. DELETE: clear +0x13 bit0x80 → RecountActiveClips + repaint (LIVE-CONFIRM; a remove
  vtbl slot may exist). Overlap test `FLpl_TrackHasClipInRange@0xF6F560`.
- → tools native_add_pattern_clip, native_add_audio_clip, native_move_clip, native_resize_clip, native_delete_clip, native_list_clips.
