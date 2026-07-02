# ui-gap-editor-detail — G6: Editor gesture body + Playlist track-strip paint + toolbar slots

Wave-4 residual-polish RE closing **GAP G6** (re/ui-gaps.md §G6). Static Ghidra on `FLEngine_x64.dll`,
image base `0x400000` (all addrs absolute/Ghidra; runtime = `ghidra - 0x400000 + flEngineBase`). Builds on
**re/ui-win-eventedit.md** (the `TEventEditForm` one-class/three-modes catalog; §3a coord model, §3b unified
mouse dispatch, §3c draw+hit path, §8 toolbar). This doc traces the three pieces that were previously
"mapped by call-graph, not line-by-line":

1. `PianoPanelMouseMove@0xd6b8d0` + `PianoPanelMouseUp@0xd64bb0` — the unified PR+PL time-grid gesture body
   (create / drag / resize / slice / select / pan), **line-by-line**.
2. `AUTracksPanelPaint@0xd7cec0` — the Playlist **track-strip** render (~11 KB).
3. The per-mode **toolbar child slot** identities (byte offsets + per-mode visibility + L→R placement), from
   `FLui_Editor_LayoutToolbar@0xd3d150`.

Tag for the whole window: **`UI_win_eventedit`**. All renames this pass are in-lane editor-canvas helpers.

> **Key field recap (from ui-win-eventedit).** Handlers take `(form, canvas, …)`. `form` = `TEventEditForm`
> (`param_1`); `canvas` = the active grid control (`param_2`, the PianoPanel `form+0xc40` in PR or the
> AUTracksPanel `form+0xd04` in PL). Decompiler indexes on a `longlong*` are byte = idx×8.
> **form:** mode `+0xb00` · tool `+0xa00` · h-zoom sec/px `+0xaec` · time origin `+0xa64` · timebase/ppq for
> formatting `+0xb08` · snap mode `+0xb2c` · PR line-draw pending count `+0xb90` · last-draw time anchor
> `+0xd84` · last value/pitch anchor `+0xd88` · drag accumulator (double) `+0xd8c` · marquee helper `+0x828`.
> **canvas:** kind `+0x3f0` (**1**=PR note grid, **3**=PL clip grid) · scroll-x px `+0x3fc` · target/pattern id
> `+0x3f4` · data model `+0x57c` · hit item id `+0x588` (`[0xb1]`, `<0` = none) · hit-zone `+0x5b4` ·
> **gesture id `+0x400`** (`[0x80]`) · prev-gesture `+0x404` · marquee anchor rect `+0x540/+0x548`
> (`[0xa8]/[0xa9]`) · resize edge state `+0x550/+0x554/+0x558` (`[0xaa]/[0xab]`) · scratch clip-index list
> `+0x3e0` · auto-scroll delta `+0x3c8/+0x3cc` · kb/automation helper obj `+0x654` · PL "select-channel
> pending" bool `+0x53b` · vertical scroller `+0x40c`.

---

## 0. Renames + tags made this pass (12, all tagged `UI_win_eventedit`, program saved)

| addr | new name | role |
|---|---|---|
| 0xd69fe0 | `FLui_EditorCanvas_ApplyDragEdit` | **the drag-apply core** — move/resize/draw/slice an item; wraps the edit in an undo step named `L"move"`/`L"resize"` |
| 0xd66170 | `FLui_EditorCanvas_UpdateMarquee` | area-select rectangle update (gesture 4/5): recompute rect `canvas[0xa8/a9]`, invalidate, range-select, build `"%s to %s"` hint |
| 0xd66850 | `FLui_EditorCanvas_UpdateResizeEdge` | edge/range-resize preview (gesture 7/8): snap time, compute edge, update `canvas[0xaa/ab]`, redraw preview |
| 0xd669e0 | `FLui_Editor_PanDrag` | hand/middle-drag pan (gesture 3): drive H time-scroller (`form+0x820`) + canvas V-scroller (`canvas+0x40c`) from mouse delta |
| 0xd6caa0 | `FLui_EditorCanvas_UpdateAutoScroll` | edge auto-scroll: set `canvas+0x3c8/+0x3cc` scroll deltas when the mouse nears a canvas edge, arm the scroll timer |
| 0xd6cbe0 | `FLui_EditorCanvas_StopAutoScroll` | remove the auto-scroll timer callback (`FUN_00d6c8f0`) from the global timer list |
| 0xd6af90 | `FLui_Editor_BuildMoveHint` | hint text while dragging an existing item: clip/pattern name + pitch/"Slide to"/"%s for %s" |
| 0xd6b740 | `FLui_Editor_BuildSelectHint` | hint text while dragging empty space (new-item / marquee): item-name-at + bar:beat:tick |
| 0xd801f0 | `FLui_Editor_PR_HighlightLineKeys` | PR line/ramp draw: highlight the swept key range (`FUN_00d955e0`) + show a bar:beat:tick hint |
| 0xd49980 | `FLui_Editor_ShowHint` | 1-liner → `FLui_SetStatusHintCore(mainForm, str)` (posts to the hint bar, re/ui-gap-popup-hint-render) |
| 0xd499a0 | `FLui_Editor_FormatBarBeatTick` | format `(kind, tick, ppq)` → `"bar:beat:tick"` UStr via `FUN_01213180` (kind 0 = pattern-relative, 1 = arrangement/PL) |
| 0xd64650 | `FLui_EditorCanvas_PL_CommitClipEdits` | PL post-edit: resolve overlapping clips' group ids, recount, refresh chanrack, clear scratch list `canvas+0x3e0` |

(Not renamed — out of lane / already named: the two published handlers `PianoPanelMouseMove/Up` keep their
RTTI names; the `FLpl_*`/`FLpat_*`/`FUN_00f6*` data ops and `FLui_Editor_SnapTime`/`RecountGrids`/
`ArmMarquee`/`InvalidateCanvases`/`ResolveHoverTarget` were renamed in the Wave-2 pass.)

---

## 1. The gesture-id enum  (`canvas+0x400`, decompiler `canvas[0x80]`)  — HIGH

Every drag on the PR note grid **and** the PL clip grid runs through the same `PianoPanelMouse*` trio, which
stashes a **gesture id** at `canvas+0x400` on MouseDown and branches on it in Move/Up. Confirmed values
(supersedes/refines ui-win-eventedit §3c's approximate `{0xb move, 0xc/0xd resize, 0xe select, 0x32 lasso}`):

| id | meaning | evidence |
|---:|---|---|
| 0 | none / pure hover | Move → `ResolveHoverTarget` only |
| 2 | tool preview drag (chord/strum stamp) | Move `FUN_00d67250`; Up `FUN_00d594b0`+`FUN_00cc7330` (`PTR_DAT_014a9358` kb/preview) |
| 3 | **pan** (hand) | Move → `FLui_Editor_PanDrag`; Up resets cursor + both scrollers |
| 4, 5 | **marquee** area-select | Move mask `0x1b0`→`FLui_EditorCanvas_UpdateMarquee`; Up 4 → invalidate / PL recount+ctx-menu |
| 6 | **line/ramp draw** (PR) | Move: tool 6 + `canvas+0x539` → time-sel; `form+0xb90` line path → `FLui_Editor_PR_HighlightLineKeys` |
| 7, 8 | **edge / range resize preview** | Move mask `0x180`→`FLui_EditorCanvas_UpdateResizeEdge` |
| 9 | paint/erase brush | ApplyDragEdit mask `0x42`; Up cursor `FUN_0083db10(...,7)` |
| 0xb | **move** item(s) | ApplyDragEdit undo step `L"move"` |
| 0xc, 0xd | **resize** item L/R edge | ApplyDragEdit undo step `L"resize"` (`iVar6-0xc < 2`) |
| 0x13 | resize w/ snap-doubling | ApplyDragEdit sets snap `\|0x300`, step ×2 |
| 0x14 | hover (PL variant) | Move → `ResolveHoverTarget` |
| 0x28 | **on-canvas MIDI-kb / automation** audition | Up `FUN_00c361e0(canvas+0x654,…)`; ApplyDragEdit `FUN_00c35e10` |
| 0x32 | **lasso** free-select | Move short-circuits to cleanup |

Draw/move/resize commit gestures are the set `{9,0xb,0xc,0xd,0x13,0x15}` (mask tests `0x38` on `gesture-8`,
`0x403c04`, `0x10e` recur through the code). Hit-**zone** at `canvas+0x5b4` selects *which* gesture MouseDown
starts: `7`=move-body, `0xc9`=resize edge, `0x15`=PL variant, `1000`(0x3e8)/`0x2000`=empty area, `2000`(0x7d0)=
special.

---

## 2. `PianoPanelMouseMove@0xd6b8d0`  (line-by-line)  — HIGH

Signature `(form, canvas, shift:ushort, x:uint, y:int)`. Skips entirely if `canvas+0x415 != 0` (drag
suppressed). Otherwise:

```
1.  time_px  = canvas->vtbl[0x238](canvas, y_arg, 0)      // pixel→row/track index (local_bc)
    time     = round((canvas+0xafc scrollX + x) * form+0xaec)   // pixel→ticks   (§3a; local_3c/local_48)
    FUN_00d86170(canvas,0)                                 // begin hover/clear
    canvas->vtbl[0x1f8](canvas, time, &list, 1)            // gather item(s) at time  → local_30
    FUN_00414da0(&hint,3,...,list)                          // seed hint string
2.  switch (canvas+0x400 gesture):
    ── gesture == 3 ──────────────────────────────────────  PAN
        FLui_Editor_PanDrag(ctx, canvas)                    // scroll both axes from delta
    ── (1<<gesture) & 0x1b0  (gesture 4,5,7,8) ───────────  MARQUEE / EDGE-RESIZE
        FLui_EditorCanvas_UpdateAutoScroll(canvas,x,y)      // arm edge auto-scroll
        if (1<<gesture)&0x180 (7,8):                        //   edge/range resize
            FLui_EditorCanvas_UpdateResizeEdge(canvas,time)
        else (4,5):                                         //   area select
            if PL (canvas+0x3f0==3) & !global DAT_0157ec09: // scan clips overlapping the marquee
                for each clip: rect-test vs marquee(anchor..cur); if hit &
                    (track locked | FUN_00d8e700==0) → cache mute state DAT_0157ec08, set DAT_0157ec09
            FLui_EditorCanvas_UpdateMarquee(canvas,time_px,time)
    ── else ─────────────────────────────────────────────  DRAW / MOVE / RESIZE
        if tool∈{4,5} (mask 0x30) OR (tool==6 & canvas+0x539):   // TIME-SELECTION draw
            build a time-selection range record (kind = PL?1:0):
              FLui_Editor_FormatBarBeatTick + FUN_00414c70 → FLui_Editor_ShowHint
            (mode 2 path also calls FUN_00d8c650; falls through to FUN_00412d60 cleanup)
        else if gesture == 0x32:  goto cleanup              // lasso handled elsewhere
        else if form+0xb90 != 0:                            // PR LINE/RAMP draw in progress
            if form+0xb90>0 & canvas+0x7e==3: UpdateAutoScroll + ResolveHoverTarget
            FLui_Editor_PR_HighlightLineKeys(form, form+0xd84, time)   // highlight swept keys
            form+0xd84 = time; goto cleanup
        // promote slice→? on shift while dragging a PR note (gesture 6, tool 10, PR): tool = 9 (temp)
        if gesture ∈ {0,0x14}:                              // pure hover
            FLui_EditorCanvas_ResolveHoverTarget(canvas,x,y,canvas[0xc2])   // update hit id/zone/cursor
        else:                                               // an active edit
            FLui_EditorCanvas_ApplyDragEdit(ctx, canvas, &hint, time, &timeInt, &timeDbl)  // §3
        restore tool
        // build + post the drag hint (unless Alt/right combo shift&0x280==0x80):
        if canvas+0x53a == 0:
            if gesture==0x28 || (hit-zone==1000 & hint!=0):  FLui_Editor_ShowHint(kb/menu text)
            elif hint!=0:
                if canvas[0xb1] (hit id) < 0:  FLui_Editor_BuildSelectHint(...)   // new/marquee
                else:                          FLui_Editor_BuildMoveHint(canvas,...) // move existing
                FLui_Editor_ShowHint(form, hint)
        else:   // canvas+0x53a set = "select channel on drag" (PL)
            if FUN_00d594b0(canvas): FUN_00cc5b40(PTR_DAT_014a9358, {x,y})   // route to kb/preview
3.  cleanup: free hint UStrs.
```

**Reuse takeaways.** (a) The *only* geometry is §3a: `time = round((canvas+0xafc + x) · form+0xaec)` and
`row = canvas->vtbl[0x238](y)`. (b) The actual data mutation for a drag is entirely inside
`FLui_EditorCanvas_ApplyDragEdit` (§3) — Move itself just classifies the gesture, updates hover, and paints a
hint. (c) `form+0xb90` is the PR "I am dragging out a line of notes" latch; while set, Move only sweeps the key
highlight and records `form+0xd84`, deferring the note creation.

### 2a. Inside `FLui_EditorCanvas_ApplyDragEdit@0xd69fe0`  — HIGH (the item edit)
`ctx` is the stack gesture-context (`ctx+0x100`=form, `+0x110`=shift, `+0x118`=x, `+0x120`=y). Flow:

- **Auto-scroll follow** (`FUN_00d6d2e0` + `UpdateAutoScroll`); if gesture 9 → set brush cursor.
- If `gesture & 0x42` (2,6) **and PL** (`canvas+0x7e==3`): resolve the clip under cursor via the kb/menu
  helper (`FUN_00c342f0`/`FUN_00c3ce40`/`FUN_00c3c5a0` on `canvas+0x654`) — the "paint clips" brush path.
- **No item hit** (`canvas[0xb1]==-1`) and gesture ∈ `0x10..0x17` (draw range):
  - Step-repeat draw: while dragging horizontally, walk the grid X (`form+0x100... a04` cursor), snap each
    step (`FLui_Editor_SnapTime` → `FLui_Editor_CalcSnapStep`), and `FUN_00d91530`/`FUN_00d910a0` **add a new
    item per step** (brush/pencil). Recount (`FLui_Editor_RecountGrids`) after.
- **Item hit** (`canvas[0xb1] >= 0`, id != -3):
  - gesture 2 → `FUN_00d67250` (chord/strum stamp preview).
  - gesture 0x28 → stop auto-scroll, drive the on-canvas keyboard (`FUN_00c35e10` on `canvas+0x654`).
  - else the **move/resize** core:
    - compute delta-value (`local_1c`) and, for resize on hit-zone 7, a length delta (`FUN_00d8c3d0`).
    - **open the undo step once** (guarded by `form+0xc60`): gesture 0xb → `FUN_00f37ec0(…, L"move")`;
      gesture 0xc/0xd → `…, L"resize"`. Marquee is disarmed (`FLui_Editor_ArmMarquee(0,0)`) once moving.
    - snap the move (`FUN_00f6fcc0`/`fc60`/`fc90` timebase + `FLui_Editor_CalcSnapStep`) → `local_64` step.
    - dispatch the actual data op on `canvas+0x57c` (the model): selected-set move/resize
      (`FUN_00d67880`/`FUN_00d68830`/`FUN_00d691d0`/`FUN_00d672f0`, and PL group ops `FUN_00d68160/81e0`),
      setting `canvas+0x53b = (PL)` so MouseUp knows to do the channel-select/jump.
    - PR: mirror the moved note onto the on-screen kb (`param_2[0xc1]+8`), vtbl[0x230] redraw.
    - accumulate `form+0xd8c += delta`; if length changed, update `form+0xd88`; `RecountGrids`.

So: **one function = the entire "drag an existing note/clip (move or resize) OR pencil-draw new ones".** To
drive it from our bridge, prefer calling the underlying model ops directly (ui-win-eventedit §10) rather than
synthesizing mouse gestures.

---

## 3. `PianoPanelMouseUp@0xd64bb0`  (line-by-line)  — HIGH

Signature `(form, canvas, x:uint, shift:ushort, y:int, wparam:uint)`. Guarded by the same Alt/right combo
(`canvas[0x14]&0x1000000 && shift&0x280==0x80` → skip). Then, in order:

```
form+0xd78 = 0
if gesture ∉ {3,5,0xe} (mask 0x4028):  FUN_010cefa0(1)     // (re-enable something; non-scroll gestures)
FLui_Editor_ArmMarquee(form,0,0)                            // disarm marquee
FLui_EditorCanvas_StopAutoScroll(canvas)                    // kill edge scroll timer
FUN_0083db10(PTR_DAT_014ac158, 0)                           // reset cursor
if DAT_0157eb74: clear the "swap"/reorder latch (canvas+0x43c = -1; FUN_00d90da0)

── COMMIT RESIZE (gesture in bit-table DAT_00d6594a) ───────
    if canvas[0xab] (resize edge state) < 0:                // simple resize end
        FUN_00d8e6d0(canvas); canvas[0xaa]=-1
        gesture 5 → FUN_00d7c920(form,canvas);  gesture 4 → InvalidateCanvases
        FUN_00d487d0(form,1,0)
    else:                                                    // range/group resize end
        FUN_00d8f380 + FUN_00d8f7d0(canvas, canvas[0xac])    // apply
        canvas[0xab]=-1; FUN_011d5610(7); FUN_00d37800(form)
    canvas[0xaa]=canvas[0xa9]=-1; DAT_0157ec08=DAT_0157ec09=0

FUN_00d90a00(canvas)                                         // finalize gesture state

── IF an item is under the mouse (canvas[0xb1] >= 0) ───────
    FUN_0107b290()
    gesture 2  → FUN_00d594b0 + FUN_00cc7330(kb) ; FUN_00d90cb0
    gesture 0x28 → FUN_00c361e0(canvas+0x654,…) ; gesture = 0     // kb release
    if tool∈{0,1,8,10} (0x503) & gesture∈{2,0xa,0xd,0x12,0x16} (0x403c04):
        if PR: read the clicked note's props (FUN_00f6c7c0) → push to the toolbar
               color/porta/slide widgets (form+0x870/+0x878, FUN_00d4dde0);   FUN_00d86de0(canvas)
    FUN_00d8e590(canvas)

── PL "select channel / jump to pattern" (canvas+0x53b set, tool!=8, DAT_013fa5d5) ──
    canvas+0x53b = 0;  clear every channel's temp field (+0x72c = -1)
    EnterCriticalSection(mixer lock)
      if no single hit: for each clip HitTest → FUN_00d64960 (collect)
      else:            FUN_00d64960(the hit clip) ; FUN_00d86de0
    LeaveCriticalSection
    for each channel with +0x72c >= 0: select it (FUN_00f35110) & set its value (vtbl[0x130])
    FUN_011d5610(1); FUN_00d37800(form)

── DESELECT / COLOR-GROUP bookkeeping ─────────────────────
    FLui_EditorCanvas_PL_CommitClipEdits(form,canvas,0)      // §merge overlapping clips, recount
    if FUN_00f53f90(canvas[0xb6])==0:                        // no pending "clicked" item
        if color-transform changed (FUN_00808...): DeselectAll; canvas[0x85]=0x80000001
        if tool!=7: FUN_00d801a0(form,-1)
    else:                                                    // a clip was single-clicked (PL)
        // double-click-to-open: jump to the clip's pattern OR select its channel,
        // sync the pattern picker (FLpat_JumpToPatternCore / TToolbarForm_PatSelectChange),
        // and the picker mini-editor (DAT_0157e998 PR ghost).   canvas[0xb6] = -1

── redraw / finalize ──────────────────────────────────────
    if (arr model +0x28 & 1): FUN_00d37450(form, resize?5:1)      // mark dirty
    PR: if canvas[0xc9] >= 0: FUN_00d58bf0(canvas,…)              // clear ghost
    FUN_00d595d0(canvas,…); FUN_00d80310(form); FUN_00d86340(canvas)
    PL: FUN_00d235b0()
    FUN_00d90ab0(canvas,0); canvas+0x594 = 0x7fffffff
    if gesture != 0:
        lasso (0x32) + a valid drop track → FUN_011210b0 (finalize lasso paint)
        gesture 3 (pan) → UpdateToolCursor + reset scroller knobs (FUN_006ce8c0 ×2)
        PL + gesture 10 + key 'V' held → make PL selection unique from clone
                (FLpl_RecountActiveClips; TFruityLoopsMainForm_MakePLSelectionUniqueFromClone)
        canvas+0x400 = 0 (gesture cleared); canvas[0xb1] = -3
        PL: FUN_00d4d0e0(form) + push both scrollers' vtbl[0x178]
    draw-gesture (8,9,10) → canvas->vtbl[0x230](canvas)          // full repaint
    // RIGHT-BUTTON on a marquee (gesture 4, shift&0x80) over ≥2 clips → clip context menu:
    if gesture==4 & rmb & clipcount>1:  FLmenu_ShowPopup(mainForm+0xa00 menu, x,y, canvas)
    FUN_007fd7b0(canvas)
```

**Reuse takeaways.** MouseUp is the **commit + side-effects** stage: it finalizes the undo step, does the PL
clip-group merge (`FLui_EditorCanvas_PL_CommitClipEdits`), performs *click* semantics (PR → load the clicked
note's color/slide into the toolbar; PL → jump-to-pattern / select-channel), repaints, and — for a
right-drag-marquee over multiple clips — spawns the clip context menu. Everything data-relevant it does is
reachable directly through the `FLpl_*`/`FLpat_*`/channel-select ops, so a bridge never needs to replay it.

---

## 4. `AUTracksPanelPaint@0xd7cec0` — the Playlist **track-strip** render  — HIGH/MED

**Crucial reuse fact: this paints the TRACK LANES + HEADERS + chrome, NOT the clips.** The loop iterates
**tracks** (`local_1e8`, a track index 0..500, capped `< 0x1f5`), never clips — there is no per-clip time
loop here. The individual clip rectangles are drawn by the shared `FLui_EditorCanvas_DrawItems@0xd86700`
(ui-win-eventedit §3c) from the item cache. So AUTracksPanelPaint = the *background/strip* layer:
lane fills, track headers (name / mute / solo / lock / color / activity meter), group headers, separators,
and per-track overlays. To render a faithful playlist ourselves we need **both** this (strips) and DrawItems
(clips).

**Setup.**
```
panel = form+0x830                     // the paint target WP control (the AUTracks panel)
skin  = *(*(panel+0x304)+0xb4)         // skin/font/metrics object   (lVar1)
gfx   = *(*(panel+0x304)+0xa0)         // graphics surface           (lVar2)
grid  = form+0xd04                     // the clip-grid canvas (scroll geometry provider, plVar3)
clip region saved (gfx+0xd0/+0xd8 = local_c0/local_b8)
bVar5 = "panel scrolled fully past bottom" early-out test
```

**Per-track arrangement record** = `FLpl_GetCurrentArrangement() + 0x24 + track*0x114` (`lVar19`, **stride
0x114**). Fields read (offsets are record-relative; the arrangement-absolute equivalent = `+0x24+` these,
cross-checked against MouseUp which used `arr+0xb4`=Y, `arr+0xb8`=H, `arr+0xd8`=content-count):

| rec off | field | use in paint |
|---:|---|---|
| +0x08 | track color (argb) | header/lane tint (byte-swapped `\|0xff000000`) |
| +0x18 | **active / un-dimmed** bool | if 0 → whole strip drawn dimmed (blend toward bg) |
| +0x1a | group-collapse / indent level | left indent = `+0x1a * 6px`; if != 0 draws a **group header** row |
| +0x3c | activity/peak float | ×512 clamp 0x100 → bottom-strip color blend (meter flash) |
| +0x40/+0x44 | custom fg/bg colors | used when +0x4c set (else defaults `PTR_DAT_014a8e50`/`014abe80`) |
| +0x48 | bottom-strip base color | the thin colored footer under each lane |
| +0x4c | "has custom color" bool | selects custom vs default palette |
| +0x50..+0x5f | **lane rect** (x0,y0,x1,y1) | computed + stored; the track's hit rect |
| +0x60..+0x6f | **mute/name button rect** | stored for hit-testing (Up reads these) |
| +0x70..+0x7f | **solo button rect** | stored for hit-testing |
| +0x80..+0x8f | **lock icon rect** | stored for hit-testing |
| +0x90 | track Y (px, `arr+0xb4`) | lane top − `grid+0x3fc` scrollX |
| +0x94 | track height (`arr+0xb8`) | lane height |
| +0xb1 | solo? bool | header state |
| +0xb4 | **content type** (1 = pattern/automation, 3 = audio-channel) | picks the header render + meter |
| +0xbc | linked channel / mixer index (when +0xb4==3) | routes color/meter |
| +0xc1 | **selected** bool | draws the selection border (`FUN_006ac570` inset) |
| +0xc4 | custom-render object (when +0xb4==1 & `PTR_DAT_014ac3a0`) | delegated draw |
| +0xdc | color/mixer group (-1 = none) | header shading (`DAT_013fa5e1`) |
| +0xe8/+0xec | progress num/den | the render/streaming progress overlay bar |
| +0xf0 | per-track icon scale | icon/name sizing |

**Draw primitives** (skin-canvas vtbl family — reusable render API): `FUN_004f0ae0` make-RECT ·
`FUN_006b1250` fill-rect · `FUN_006b1d30`/`FUN_006b1ad0` vertical-gradient fill · `FUN_006b0e20` rect outline
· `FUN_006aba40`/`FUN_006ac570`/`FUN_006ad340` rounded/inset/bordered fill · `FUN_006b3970`/`FUN_006b3600`
text blit `(gfx,font,str,rect,flags,color,alpha)` · `FUN_006b89c0` measure glyph → size · `FUN_006a9de0`
glyph/icon blit · `FUN_01199a40`/`FUN_01199af0` name-plate / mute-icon · `FUN_006b0880` line/level bar ·
`FUN_006b7f20`/`FUN_006b8030` push/pop clip-rect · `FUN_006a5c50` set blend mode · color math
`FUN_00626d50`(shade) / `FUN_00626bd0` / `FUN_006255c0`(mix) / `FUN_00625430` / `FLui_Skin_BlendColor` ·
`FUN_0040c660` round(double)→int. Skin globals: `PTR_DAT_014a9b80` (header text color), `DAT_0157ebf4`
(separator/shadow), `DAT_0157ebf8`, `PTR_DAT_014a9d70`/`014aba58` (icon atlas), `PTR_DAT_014aabd8` (font set),
`PTR_DAT_014a7b50` (scaled meter font).

**Per-track sequence** (when the lane is on-screen and not collapsed-hidden `bVar5`):
1. compute lane rect (`+0x50`), fetch the track's **group range** (`FLpl_GetTrackGroupRange`) and whether the
   next track starts a new group (`local_1c4`, draws a divider).
2. resolve **lane color** (custom vs default), dim if `+0x18==0`, byte-swap to argb.
3. draw the **header block** on the left: background fill + gradient, group indent, **track name**
   (`FLpl_GetTrackName` → text blit; falls back to auto-name `FUN_011f0560`), and — if room — the small
   3-dot **activity meter**.
4. draw **mute / solo / lock** icons, caching each icon's rect into `+0x60/+0x70/+0x80` for MouseDown
   hit-testing; mute uses `forms.playlist.mutelock.color` when locked.
5. draw the **render-progress overlay** (`+0xe8/+0xec` → `FUN_006b0880`) when a track is streaming/rendering.
6. draw the **bottom color strip** (`+0x48` blended by the `+0x3c` activity float) + the **lane separator**
   line (`DAT_0157ebf4`).
7. advance to the next track; break when past the visible bottom (`local_54`), and after the loop draw the
   **empty-area filler** below the last track (`PTR_DAT_014aa968` bg + a `DAT_0157ebf4` top line).

Confidence: the layer identity (strips-not-clips), the track-record stride/offsets, and the draw-primitive
roster are HIGH; a few of the deeper conditional overlays (the `local_1ac` icon-flag math, the exact meaning
of `+0xe8/+0xec`) are MED (characterized, not every pixel traced).

---

## 5. Toolbar child slot identities  (`FLui_Editor_LayoutToolbar@0xd3d150`)  — HIGH (visibility) / MED (roles)

`FLui_Editor_LayoutToolbar` relays out the whole editor toolbar on resize/mode-switch. It (a) sets **per-mode
visibility** of each child via `FLui_WP_SetShowing`, then (b) places the visible children **left→right** by
threading them through `FUN_00d3d0e0(cursor, child)` (which advances an x-cursor). This byte-confirms the
per-mode slot map that ui-win-eventedit §8/§11 had left "individually un-pinned".

**Origin.** Layout starts at the right edge of the **options button** `form+0x850` (`[0x10a]`,
"Piano roll/Playlist options") plus a margin; the **play button** `form+0x848` (`[0x109]`) is placed last, at
the running cursor, then the toolbar total width is set (`FUN_007dd270`).

### 5a. Per-mode visibility (from the explicit `SetShowing` calls)  — HIGH

| slot (form+) | shown when | ⇒ EE(0) | PR(1) | PL(2) | likely role |
|---|---|:---:|:---:|:---:|---|
| +0x778 | value-lane target valid (`form+0xc24` has a target) | ✓* | ✓* | — | **event/velocity-lane toggle** (companion `+0x768`) |
| +0x7d8 | `mode > 0` | — | ✓ | ✓ | grid-only control (snap/loop group) |
| +0x810 | `mode > 0` | — | ✓ | ✓ | grid-only control |
| +0x7f0 | `mode < 2` | ✓ | ✓ | — | EE/PR-only (tool/select cluster) |
| +0x840 | `mode == 1` | — | ✓ | — | PR-only |
| +0x838 | `mode == 1` | — | ✓ | — | PR-only |
| +0x8e8 | `mode == 1` | — | ✓ | — | PR-only |
| +0x9c8 | `mode == 1` | — | ✓ | — | PR-only |
| +0x808 | `mode == 2` | — | — | ✓ | PL-only |

`*` = subject to the value-lane target being present. Slots without a `SetShowing` here are **always shown**
(placed unconditionally): `+0x7e8`, `+0x768`, `+0x7b8`, `+0x770`, `+0x7d0`, `+0x7a8`, `+0x7b0`, `+0x848`,
`+0x850`.

### 5b. L→R placement order (the exact `FUN_00d3d0e0` sequence)  — HIGH

```
[origin = right edge of options btn +0x850]
+0x7f0 → +0x7e8 → +0x9c8 → +0x838 → +0x8e8   [gap]
+0x768 → +0x7b8 → +0x840 → +0x770 → +0x810 → +0x808 → +0x778 → +0x7d0 → +0x7a8 → +0x7b0 → +0x7d8
[gap if +0x768.checked (+0xa9)]
+0x848 (play, via FUN_005cf880)
then FUN_00d3cdc0(form) always, FUN_00d3cf60(form) if mode<2   // secondary row / value-lane strip
```

### 5c. Slots cross-referenced to §8 handlers  — MED

Pinned from the ui-win-eventedit field map + §8 roles:

| slot | idx | handler / role (ui-win-eventedit §8) | conf |
|---|---|---|---|
| +0x850 | [0x10a] | **Options** menu (`MenuBtn2BeforePopup@0xd7c320`) — layout origin | HIGH |
| +0x848 | [0x109] | **Play/pause** (`EEPlayBtnClick@0xd49e30`) — placed last | HIGH |
| +0x7e8 | [0xfd] | **Snap to grid** (`SnapBtnBeforePopup@0xda1860`/`MouseUp`) | HIGH |
| +0x7b8 | [0xf7] | **Paint/draw tool** ("Paint") | HIGH |
| +0x7f0 | — | EE/PR **tool selector** cluster (`SelectToolBtnClick@0xd4d290`) | MED |
| +0x778/+0x768 | [0xed] | **event/velocity-lane** toggle + type panel (EE/PR) | MED |
| +0x9c8/+0x838/+0x8e8/+0x840 | — | PR-only extras (scale-snap / stamp / ghost / note-size cluster) | MED |
| +0x808 | — | PL-only extra (clip source / tools) | MED |

The four PR-only slots (`+0x838/+0x840/+0x8e8/+0x9c8`) and the PL-only `+0x808` are byte-confirmed as
mode-gated (5a); their exact handler binding is set in `FormCreate`/the sub-builders and is MED until the
`.dfm` OnClick TMethod pokes are traced — not needed for the reuse recipe (visibility + order suffice).

**Reuse.** To emulate the editor toolbar per mode: show the 5a set for the mode, place them in the 5b order
starting after the options button, and gate the value-lane toggle on whether a value lane exists. Active tool
lives at `form+0xa00`; snap at `form+0xb2c` (ui-win-eventedit §8).

---

## 6. Confidence + residual

- **HIGH:** the gesture-id enum at `canvas+0x400` (§1); the Move/Up control flow and the fact that all data
  mutation funnels through `FLui_EditorCanvas_ApplyDragEdit` + the `FLpl_*`/`FLpat_*` ops (§2–3); MouseUp's
  commit/click side-effects incl. PL jump-to-pattern & the right-drag clip context menu; **AUTracksPanelPaint
  paints strips not clips** and the track-record stride 0x114 + header/mute/solo/lock rect offsets (§4); the
  per-mode toolbar visibility + L→R order (§5a/5b).
- **MED:** a few gesture sub-values (2 vs 6 vs 9 brush semantics) inferred from static dispatch, not
  runtime-confirmed (no live FL); the deeper AUTracksPanelPaint overlays (`+0xe8/+0xec` progress, `local_1ac`
  icon-flag math); the exact handler bound to each PR-only / PL-only toolbar slot (visibility is HIGH, the
  OnClick binding is MED).
- **Not chased (out of lane / lower value):** the item-add op family called by ApplyDragEdit
  (`FUN_00d67880/68830/691d0/672f0` etc. — the NoteRecorder/clip add-move-resize primitives; a data-side
  patterns wave) and the sub-builders `FUN_00d3cdc0`/`FUN_00d3cf60` that lay out the secondary/value-lane
  toolbar row. None block the documented reuse recipes; **G6 is closed** for the #80 native-UI goal.
```
