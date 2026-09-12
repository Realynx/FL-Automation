# FL 2025 → 2026 function address map (auto-generated 2026-07-09)

Byte-signature cross-version mapping. 63/76 auto-mapped unique; 13 need a refinement pass.
Method: generate an operand-wildcarded pattern from the 2025 fn (auto-extended to be unique in 2025),
`Memory.findBytes` it in the 2026 engine. Source scripts + raw output: scratchpad `sigs.txt` / `map.txt`.
These 2026 addresses feed the `SymEntry.ghidra[FLV_2026]` column AND (with the pattern) the runtime scanner.

## CONFIRMED (63) — name | 2025 | 2026
RTL (SAME addr, stable): Delphi_UStrAsg 4133f0|4133f0 · FL_FreeObj 40faa0|40faa0 · Delphi_DynArraySetLength 417fc0|417fc0

Window-host/UI: FLui_CreateFormFromClassRef 10c2aa0|11c2870 · FLwp_SetFormCaption 841690|869370 · FLwp_SetVisible 833ec0|85bba0 · FLui_ZOrderRefresh 5d0ea0|60a780 · FLui_WP_GetHandle 5ddf70|617850 · FLwp_SetWindowState 836600|85e2e0 · FLui_DockLayout 7e6170|808160 · FLui_Focusable 5de3e0|617cc0 · FLwp_Render 77adb0|79ad70 · FLui_WP_SetAlign 5ceef0|6087d0 · FLwp_SetterA 5d0d90|60a670 · FLwp_SetterB 5d0c50|60a530 · FLwp_CreateButtonControl f0ddb0|ff11e0 · FLwp_SetButtonCaption 5d0ae0|60a3c0 · FLwp_SetControlValue 5d0d10|60a5f0 · FLbrz_AddTabClone 9ac910|a7cc20 · FLmenu_CreateItem 70e1a0|72d670 · FL_ChildCount 81dda0|83fdf0 · FL_ChildAt 81ddc0|83fe10 · FL_ListRemoveAt 64f4d0|663b10 · FLui_SetStatusHint 10ec870|11eb580 · FormShortCut 114de10|1248140

Transport: FLgl_GlobalCommandDispatch ef7b20|fd8800 · FLtr_SeekToSongTick 10e3470|11e1860 · FL_DispatchCommand f53fe0|1041320

Patterns/notes: FLpat_RebuildPattern 11d4140|12cf620 · FLpat_NotifyChanged f53d30|1041070 · FLui_RefreshEditors d421c0|e43d70 · FLcr_RefreshRack 107ead0|11804a0 · FLpat_GetNoteRecorder 11d4080|12cf560 · FLpat_GetParamRecorder 11d4000|12cf4e0 · FLpat_RecordNoteOn f6d740|105abc0 · FLpat_RecordNoteOff f6d880|105ad00 · FLpat_SetCurrentPattern cbb300|dce120 · FLpat_IsPatternEmpty 11db510|12d6bf0 · FLpat_NoteArrayClear 11e0930|12dcc40 · FLpat_SetPatternName 11d3960|12cee40

Channels: FLcr_ChannelListGetItem f00f80|fe4090 · FLcr_SelectOneChannel 10e3eb0|11e2300 · FLcr_ApplyChannelSolo e012f0|d5a3c0 · FLcr_InsertChannel f215e0|fff2f0 · FLcr_GetEventIDName f5ca00|1049e60 · FLac_DeletePoint b30ad0|b97e30

Mixer: FLmx_RefreshRouting 11a5d20|12a0d50

Playlist/project/arr: FLpl_SetCurrentArrangement 11fc880|12f8bc0 · FLpl_SetTrackNameColor 11e7940|12e3c40 · FLpl_SetTrackSolo 11e9810|12e5b10 · FLpl_SetTrackSelection 11e9c30|12e5f30 · FLpl_RecountActiveClips f6e180|105b600 · FLpl_SetClipSourceRange f71a70|105ef70 · FLpr_OpenProject 10d50c0|11d52c0 · FLpr_SetProjectPath 10d2c90|f50ce0 · FLpr_WriteFlpFile 10d5a60|11d5c60 · FL_AutoIncrementFileName 7f7800|819880 · FLtr_AddTimelineMarkerCore d523c0|e54090 · FLar_AddArrangement 11fabc0|12f6f00 · FLar_SetName 11fb0d0|12f7410 · FLar_CopyInto 11fb420|12f7760 · FLar_GetName 11fb160|12f74a0 · FLar_Delete 11fb1c0|12f7500

## NEEDS REFINEMENT (13)
- **Multi-match (1)** — extend pattern: FormKeyDown 10c9920 (2 hits, first 00ecda80).
- **Zero-match (7)** — prologue changed → string-anchor: TQuickEdit_SetText 74c260 · TQuickEdit_Refresh 74bb70 · FLbrz_SelectTabById 9ac590 · FLmx_SetRouteActive 11a67f0 · FLpl_RepaintPlaylist da40c0 · FLpr_SaveProjectToFlp 10d6190 · FLpl_SetTimeSelection d41e60.
- **Non-unique in 2025 (5)** — need a more distinctive window: TQuickEdit_ctor 74c400 · FLui_MarkKbCapture 802820 · FLpl_GetCurrentArrangement 11e32c0 · FLpl_SetClipMuted f71a60 · FLar_GetCount 11fb1a0.

## DATA GLOBALS 2025→2026 (17/30 via RIP-relative; raw: scratchpad dataglobals_map.txt)
CONFIRMED: MainFormPtr 14a8750|15d8968 · ToolbarFormPtr 14aa4c8|15da830 · MainBrowserPtr 157ffb8|16b2968 · StatusHintStr 15817d0|16b41d8 · BusyCounter 14bdbac|15ee0c4 · PPQ 14a79f8|15d7b40 · CurPatternIdx 14ab580|15db998 · PatternArray 14aa0c8|15da418 · ChannelList 14a98d8|15d9b90 · AutoLinkRegistry 14a81b8|15d8368 · CurArrangementIdx 149e8b4|15ce02c · ProjectObject 1581200|16b3c08 · ProjectPath 1581298|16b3c98 · ProjectTitle 15812a0|16b3ca0 · SongPatternMode 14a8670|15d8860 · PatternNameArrayBase 1803b68|1936ce8 · NoteRecorderArrayBase 1803b90|1936d10

NEEDS REFINEMENT (13):
- pattern-not-found in 2026 (8) — try a different xref site: PlayStatePtr 14a81c0 · MixerTrackArray 14a7eb0 · MixerTrackCount 14a9850 · RoutingMgr 14a99a0 · SongObject 14aab88 · SongArrangement 14aba80 · ProjectController 14abca8 · LoadInProgress 14a8748.
- no clean single-instr RIP xref (5) — need lea/imm/data-ref handling: LoadingFlag 157f667 · **HostClassRef cf3888** · **HostVMT cf3870** (window-host embed — load-bearing) · QuickEditVMT 7466b8 · DynArrayTypeInfo b2c678.

## NEXT
Refine the 13; then data globals (40, RIP-relative); then struct/vtable offsets (verify vs 2026, window-host cluster);
populate `g_syms[]` in sigscan.cpp with pattern + ghidra[2025]+[2026]; wire window-host through `sym:`; live-test AI window in FL 2026.
