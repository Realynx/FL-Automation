# FL UI: layout / positioning / docking / scrolling / resize (Wave-1, re/ui-06)

How FL's "WP" widget framework (custom layer over Delphi VCL) **positions/sizes/anchors** controls, arranges
a container's children, **scrolls** a viewport, **docks/floats** windows, and **relayouts on resize** — with the
exact field offsets + functions so we can (a) lay out OUR controls in a container, (b) make a scrollable area,
(c) make OUR injected host **dockable** like an FL window (ties to #80 window-embed).

RESEARCH ONLY (static Ghidra decompile of `FLEngine_x64.dll`, image base 0x400000). Ghidra renames done in this
area (tag `UI_layout`); NO source edits. Rebase at runtime: `runtime = ghidra - 0x400000 + flEngineBase`.
Builds on re/13 (WP control create/parent), re/14 (forms/HWND@+0x2b0), re/22 (window manager / dock-float).

## TL;DR / verdict
- **FL layout is VCL-style, not a constraint/flow engine.** Child positions are set **explicitly** by builder code
  (compute x/y/w/h → `SetBounds`), plus three lightweight automatic mechanisms: **anchors** (a byte at
  `ctrl+0xb3`), **size constraints / preferred size** (`ctrl+0x474/+0x476` u16), and a **scroll offset** applied to
  the whole client area. There is no auto flow/grid layout; "how a container arranges children" = *each child keeps
  the bounds you gave it, the container only re-anchors + invalidates on resize.*
- **Every WP control carries its own layout state** (one flat struct):
  - **bounds rect** `x@+0x90, y@+0x94, w@+0x98, h@+0x9c` (i32) — set by `FLui_Layout_SetBounds` (vtbl[0x188]).
  - **preferred/default size** `w@+0x474, h@+0x476` (u16) — what builders poke for skin-driven sizing.
  - **parent control ptr** `@+0x78` (read by SetBounds/origin/relayout; 0 ⇒ top of a tree).
  - **child/ownership node** `@+0x11c` (children `TList` @node+0x24) — the thing you call to **parent** a control.
  - **scroll/metrics object** `@+0xd0` (scroll offset + extents + the anchor reference rect).
  - **client/content rect** `@+0x330..+0x33c` (l,t,r,b) — the scroll-adjusted area children live in.
  - **anchor flags** `@+0xb3`; **style flags** `@+0x34`; **misc layout flags** `@+0xa0/+0xa4`; **scroll flag** =
    bit `0x2000` of `@+0x320`.
- **Parent a control** (the clean primitive): `FLui_WP_SetParent@0x5d0850(ctrl, parent)` (control `vtbl[0x138]`)
  — sets `ctrl+0x78`=parent, removes from old / adds to new parent's child list (`FLui_Layout_AddChildToParent` /
  `RemoveChildFromParent`), recalcs anchors. **Docking reuses this verbatim**: `SetParent(win, dockHost)` = dock,
  `SetParent(win, 0)` = float. Lower-level equivalent (the +0x11c node link, used by some builders):
  `(*(ctrl+0x11c))->vtbl[0x10]( ctrl+0x11c , *(parent+0x11c) )` (`FLui_Layout_ChildNode_SetParent@0x50adb0`).
  **NB:** re/13 called `vtbl[0x138]` "apply skin/theme" — that was wrong; it is SetParent (the `parent` arg also
  supplies the skin context, hence the confusion).
- **Dock/float a window** (re/22): `WindowSetAttached@0x1228CD0(window, 1|0)` — docked state = bit `0x4` of
  `window+0xd4` (attached editors) **or** host (`window+0xb8`) having a non-null parent `@host+0x78` (hosted
  editors). Dock host/workspace = `mainForm+0xb18`; dock origin via host `vtbl[0xe0]` (cumulative parent-chain
  origin). See §5 for the deepened dock map + the "dock our window" recipe.
- **Confidence HIGH** on the bounds/anchor/relayout core + parenting recipe + client-rect/scroll math + dock
  field model (re/22 re-confirmed). **MEDIUM** on the exact Delphi internals of the `+0x11c` node insert
  (TComponent-style; the *usage* recipe is proven even though the insert virtual is a component protocol).

---

## 1. The control layout struct (offsets on every TWPControl)

Established from `FLui_Layout_SetBounds@0x802ef0`, the base init `FUN_005ce910` (TWPControl deep ctor),
`FLui_Layout_ComputeClientRect@0x801170`, `FLui_Layout_UpdateAnchorPivot@0x5cf670`, and the channel-rack builder
`FLwp_BuildChannelRackControls@0xF0E330` (re/13).

| off | type | meaning |
|----:|------|---------|
| +0x34 | u16 | **style flags** (bit0 = skip-invalidate-on-resize; bit1 = skip save-bounds; bit3 = no-dock per re/22) |
| +0x78 | ptr | **parent control** (the layout parent — read by SetBounds/origin/relayout; set via the +0x11c node) |
| +0x80 | ptr | default/owner (set to `vtbl[0x150]` result in ctor) |
| +0x88 | ptr | self back-pointer |
| +0x90 / +0x94 | i32 | **x / y** (bounds, parent-relative) |
| +0x98 / +0x9c | i32 | **w / h** (bounds) |
| +0xa0 | u32 | layout flags (bit 0x40 set in ctor) |
| +0xa4 | u32 | relayout flags (bit 0x10 = "relayout pending/deferred"; bit 0x4000 = skip save-bounds) |
| +0xa9 | u8 | **visible** (also the window ✓ source for forms, re/22) |
| +0xaa/+0xab/+0xac | u8 | enabled / mounted flags |
| +0xb3 | u8 | **anchor flags** (bit0/bit1 = anchor left/right(X); bit2/bit3 = anchor top/bottom(Y); 3 = centered) |
| +0xc4 | i32 | general value (re/13) |
| +0xd0 | ptr | **scroll/metrics object** (scroll offset + extents + anchor reference rect; class VMT 0x5bd558) |
| +0x11c | ptr | **child/ownership node** (children `TList` @node+0x24, owner ctrl @node+0x8; class VMT 0x5bfad0) |
| +0x2cc | i32 | (init 0x60) |
| +0x2d4 / +0x2d8 | i32 | **anchor pivot X / Y** (computed by UpdateAnchorPivot from +0xb3) |
| +0x2dc / +0x2e0 | i32 | anchor center X / Y |
| +0x2e4 / +0x2e8 | i32 | anchor delta (passed to parent vtbl[0x250] on bounds change) |
| +0x2ec..+0x2f8 | i32×4 | **saved bounds** (x,y,w,h) for anchoring (written by `FLui_Layout_SaveBoundsForAnchor`) |
| +0x304 | ptr | render/paint context object (skin area — `forms.*` paint helper) |
| +0x318 | i16 | **relayout-suspend counter** (BeginUpdate/EndUpdate; >0 ⇒ defer realign, set +0xa4 bit0x10) |
| +0x320 | u32 | flags; **bit 0x2000 = scrollable** (content rect uses scroll offset) |
| +0x330..+0x33c | i32×4 | **client/content rect** (l,t,r,b) — scroll-adjusted; what children are arranged within |
| +0x340 | ptr | **client region / clip object** (TWPRegion-ish; rect stored via `FUN_00653150`) |
| +0x45c / +0x2b0 | HWND | window handle (control HWND @+0x45c; top form HWND @+0x2b0, re/14) |
| +0x474 / +0x476 | u16 | **preferred / default width / height** (skin-driven; builders poke these) |
| +0x484..+0x487 | u8×4 | per-side margins/padding (l,t,r,b — seen poked to 0x15 in channelrack) |
| +0x48a | u32 | button/control behavior bitfield |
| +0x496 | u8 | "no auto-relayout" guard (checked by vtbl[0x1c0]) |

## 2. SetBounds → invalidate → relayout chain (the per-control pipeline)

`FLui_Layout_SetBounds@0x802ef0` (control **vtbl[0x188]**), signature `(ctrl, x, y, w, h)`:
```
if (x,y,w,h) differ from +0x90/+0x94/+0x98/+0x9c:
  if visible(+0xa9): FLui_Layout_InvalidateBounds(ctrl)            // 0x7fe470 RedrawWindow(old rect)
  rect = (x, y, x+w, y+h)
  FLui_Layout_SetRectCore(ctrl, &rect)                            // 0x5d29d0 (below)
  FUN_005d2870(ctrl, 0x47,0,0)                                    // notify "bounds changed" message 0x47
  ctrl->vtbl[0x110](ctrl)                                         // FLui_Layout_RelayoutHook (below)
  if (+0x78 parent) { FLui_Layout_ResetHostPaintCache(ctrl); if visible InvalidateBounds(ctrl) }
  if ((+0x34 & 1)==0) dyn 0xffcf (ctrl)                           // **OnResize** (Delphi dynamic method)
```
`FLui_Layout_SetRectCore@0x5d29d0(ctrl, rect[l,t,r,b])`: writes `+0x90=l, +0x94=t, +0x98=r-l, +0x9c=b-t`, then
`FLui_Layout_UpdateAnchorPivot@0x5cf670(ctrl)` + `FLui_Layout_SaveBoundsForAnchor@0x5d56b0(ctrl)`.

`FLui_Layout_RelayoutHook@0x801200` (control **vtbl[0x110]**):
```
FLui_Layout_NotifyParentRealign(ctrl)   // 0x5cf1a0 — if +0x78, ask parent to realign this child
ctrl->vtbl[0x1c0](ctrl)                  // 0x71c9b0 — recompute own client rect if +0x496==0 -> ComputeClientRect
```
`FLui_Layout_NotifyParentRealign@0x5cf1a0`: `if (ctrl+0x78) FLui_Layout_RealignChild(parent, ctrl)`.
`FLui_Layout_RealignChild@0x5d71d0(parent, child)`: if not deferred (parent+0x318 word == 0):
`vtbl[0xe8](parent,&rect)` get parent client rect → `parent->vtbl[0x1c8](parent, child, rect)`
(`FLui_Layout_SetClientRegion@0x7ffdf0`: ensure +0x340 region, store rect into it, then `vtbl[0x178]`
invalidate) → clear parent+0xa4 bit0x10. Else set parent+0xa4 bit0x10 (defer).

**Net effect:** moving/resizing a control writes its rect, fixes its anchor pivot, tells the parent to re-clip +
repaint, and recomputes its own scroll-adjusted client rect. No child re-flow — children keep their explicit
bounds (re-anchored on parent resize, see §3).

## 3. Anchors & "arranging children"

- **Anchors** live in `ctrl+0xb3` (computed into pivots by `FLui_Layout_UpdateAnchorPivot@0x5cf670` using the
  `+0xd0` metrics object's `vtbl[0x20](idx)` GetEdge, idx 0=left/1=top/2=right/3=bottom). Bits: 0/1 = anchor
  X to left/right, 2/3 = anchor Y to top/bottom, value 3 = centered (zeroes the delta). On bounds change the
  delta `+0x2e4/+0x2e8` is pushed to the parent via `parent->vtbl[0x250]`. The default node anchor bytes
  (node+0x20/+0x21) are 0x10; channel-rack clears bits of node+0x21 to change anchoring.
- **Preferred size**: `+0x474/+0x476` (u16). Builders set these (e.g. channelrack select-button 0xb0×0xb0) and the
  skin/auto-size path uses them; the *actual* laid-out size is the i32 `+0x98/+0x9c` from SetBounds.
- **There is no flow/stack layout.** The "arrange a container's children" path = `FLui_Layout_SetClientRegion`
  (sets the parent's clip rect to its client rect) + per-child `RelayoutHook` (re-anchor + repaint). Child
  positions are whatever the builder assigned. To lay out our own children we therefore just call `SetBounds`
  on each (see §6 recipe).
- **Get rects:** `FLui_Layout_GetLocalRect@0x5d3a90` (vtbl[0x1b0]) → (0,0,w,h); `FLui_Layout_GetClientRect@0x801140`
  (vtbl[0xe8]) → the +0x330 scroll-adjusted rect; `FLui_Layout_GetAbsoluteOrigin@0x5cfef0` (vtbl[0xe0]) → walks
  `+0x78` parent chain summing each `+0x90/+0x94` = absolute/host origin (this is the "dock origin" re/22 reads).

## 4. Scrolling / viewport (FLui_Layout_ComputeClientRect)

`FLui_Layout_ComputeClientRect@0x801170` is the heart of scrolling. It writes the client/content rect at
`ctrl+0x330..+0x33c`:
```
if ((ctrl+0x320 & 0x2000) == 0)            // NOT scrollable
    clientRect = (0, 0, w@+0x98, h@+0x9c)
else                                       // scrollable
    s = *(ctrl+0xd0)                       // scroll/metrics object
    clientRect = ( -s->scrollX(+0x10), -s->scrollY(+0x14),
                    w + s->extentW(+0x18),  h + s->extentH(+0x1c) )
```
Because every child is laid out relative to `GetClientRect` (the +0x330 rect), **a non-zero scroll offset shifts
all children together** — that *is* the scroll. `vtbl[0xe8]` (`GetClientRect`) returns this rect to children.
- **Scrollable flag** = bit `0x2000` at `ctrl+0x320`.
- **Scroll state** lives on the `+0xd0` metrics object (class VMT 0x5bd558; shares the link-base with the +0x11c
  node — `vtbl[0x10]=SetParent@0x50adb0`). Offsets: scrollX `+0x10`, scrollY `+0x14`, extentW `+0x18`,
  extentH `+0x1c`; `vtbl[0x20](idx)` = GetEdge (also feeds anchors). Its change-callback
  `FUN_005d55b0` simply fires the owner control's `vtbl[0x110]` (RelayoutHook) → recompute client rect → all
  children re-positioned.
- *Scrollbar-widget wiring, scroll-offset setters/clamping, and viewport clipping are detailed in §7 (sub-agent
  scroll map).* 

## 5. Docking (deepens re/22)

Baseline from re/22: dock/float ONE window `WindowSetAttached@0x1228CD0(window, attach)`; ALL editors
`SwitchEditorsAttached@0x110C320`; dock host/workspace `mainForm+0xb18` (`*0x14A8750+0xb18`); dock origin from
host `vtbl[0xe0]` (= `FLui_Layout_GetAbsoluteOrigin@0x5cfef0`, §3); docked-state fields: bit `0x4` of
`window+0xd4` (attached editors) or host (`window+0xb8`) parent `@host+0x78 != 0` (hosted editors).

- **Attached-style editor** (`window+0xb8 == 0`): toggling compares desired vs current bit `0x4` @`+0xd4`; on
  **dock** sets `+0xd4 |= 4` and **adds** the dock origin to `+0xc0/+0xc4`; on **float** clears `+0xd4 &= ~4`
  and **subtracts** it. ⇒ docked = bit 0x4 of `+0xd4`, plus position offset by the host origin.
- **Hosted editor** (`window+0xb8 != 0`): if host parent (`*(host+0x78)`) disagrees with `attach`, re-dock via
  `FormHide(host)` + `FUN_0114b810(host)` + `FormShow(host)`. ⇒ docked = host has a non-null `+0x78` parent
  (same parent convention as WP controls, §1).

*(Dock-host object class @mainForm+0xb18, dock zones/splitters, ArrangeWorkspace@0x10F5130 reposition logic, and
the full dock function map are detailed in §6-dock below from the docking sub-agent.)*

## 6. REUSE RECIPES

### A. Lay out OUR controls in a container
1. Create the controls (re/13): `ctrl = FLwp_CreateControl(&VMT, 1, 0)` (or button/wheel wrappers); base-init
   `FUN_005d08c0(ctrl,0)`; set skin descriptor `Delphi_UStrAsg(ctrl+0x328, L"...:quickbutton")`.
   *(Note: `ctrl->vtbl[0x138](ctrl, container)` that channel-rack calls is NOT skin — it is SetParent, see step 3.)*
2. **Position/size each**: `ctrl->vtbl[0x188](ctrl, x, y, w, h)` (= `FLui_Layout_SetBounds`). (Or poke preferred
   size `+0x474/+0x476` u16 for skin auto-size.) Coordinates are **parent-relative** (parent's client/+0x330
   origin).
3. **Parent it** — clean primitive: `FLui_WP_SetParent@0x5d0850(ctrl, container)` (= `vtbl[0x138]`): sets
   `ctrl+0x78`=container, adds to its child list, recalcs anchors. (Raw node form:
   `(*(ctrl+0x11c))->vtbl[0x10]( ctrl+0x11c , *(container+0x11c) )`.) The container is any WP control (channel-rack
   content panel `form+0x7d4`, or our own host control).
4. **Anchor** (optional): set `ctrl+0xb3` bits (0/1 X-left/right, 2/3 Y-top/bottom) so the control follows the
   container edges when the container resizes.
5. Show + finalize: `ctrl->vtbl[0x200](ctrl, 1)` then `FUN_0076aef0(ctrl, 1)`. All main-thread.
- The container re-clips + repaints children automatically on its own SetBounds (§2/§3); you do **not** get
  auto-flow — set each child's bounds yourself (or on the container's OnResize dyn 0xffcf, re-issue SetBounds).
6. **Batch many SetBounds** to avoid relayout/repaint thrash: `FLui_Layout_BeginUpdate(container)@0x5d72b0`
   (increments the +0x318 suspend counter) → issue all child `SetBounds` → `FLui_Layout_EndUpdate(container)@0x5d72c0`
   (decrements; when it reaches 0 and a relayout is pending (+0xa4 bit 0x10) it flushes once via
   `FLui_Layout_FlushDeferredRelayout@0x5d72f0`). Mirrors VCL BeginUpdate/EndUpdate.

### B. Make a scrollable area
1. Use a WP control as the viewport; set its scrollable flag: `ctrl+0x320 |= 0x2000`.
2. Put content controls as children (recipe A) sized to the full content extent.
3. Drive scroll by writing the `+0xd0` metrics object's `scrollX@+0x10 / scrollY@+0x14` (clamp to
   `extentW@+0x18 / extentH@+0x1c`) — then fire the control's `vtbl[0x110]` (RelayoutHook) (or call the metrics
   change-callback `FUN_005d55b0`) so `ComputeClientRect` re-runs and shifts the children. `GetClientRect`
   (vtbl[0xe8]) will then report the scrolled `+0x330` rect. *(Exact setter / scrollbar-widget wiring: §7.)*

### C. Make OUR injected host dockable (ties to #80)
Give our host the FL window/control dock fields so `WindowSetAttached@0x1228CD0` works on it:
- Simplest (attached-editor path): ensure `host+0xb8 == 0`, maintain `host+0xd4` (we own bit 0x4 = docked),
  `host+0xc0/+0xc4` position, and a working `vtbl[0xe0]` origin. Call `WindowSetAttached(host, 1)` to dock /
  `(host, 0)` to float; it adds/subtracts the dock-host origin (`mainForm+0xb18` `vtbl[0xe0]`).
- Hosted-editor path: set `host+0xb8` = a child-wrapper whose `+0x78` parent we point at the dock host, then
  `WindowSetAttached` re-parents via `FormHide`+`FUN_0114b810`+`FormShow`.
- Either way our window must already be a registered FL form (re/22 §6: create through the form factory so it
  joins `formMgr+0x20`, has `+0xa9` visible byte + HWND@+0x2b0 + CanClose vtbl). Then a View toggle + ✓ per
  re/22 §6 C. *(Dock-host internals / whether a custom dock zone is needed: §6-dock.)*

---

## FUNCTION MAP (Ghidra addrs, image base 0x400000) — renamed `FLui_Layout_*`, tag `UI_layout`

### Bounds / anchor / relayout (this doc's core)
| addr | name | role |
|------|------|------|
| 0x802ef0 | FLui_Layout_SetBounds | vtbl[0x188]; set rect +0x90/+0x94/+0x98/+0x9c, invalidate, relayout, OnResize |
| 0x5d29d0 | FLui_Layout_SetRectCore | write rect fields + UpdateAnchorPivot + SaveBoundsForAnchor |
| 0x801200 | FLui_Layout_RelayoutHook | vtbl[0x110]; NotifyParentRealign + recompute own client rect (vtbl[0x1c0]) |
| 0x5cf1a0 | FLui_Layout_NotifyParentRealign | if +0x78 parent, RealignChild(parent,self) |
| 0x5d71d0 | FLui_Layout_RealignChild | parent: GetClientRect (vtbl[0xe8]) + SetClientRegion (vtbl[0x1c8]) for child |
| 0x7ffdf0 | FLui_Layout_SetClientRegion | vtbl[0x1c8]; ensure +0x340 region, store client rect, invalidate (vtbl[0x178]) |
| 0x7ffdb0 | FLui_Layout_EnsureClientRegion | lazily create the +0x340 client/clip region object |
| 0x8016a0 | FLui_Layout_InvalidateInParent | vtbl[0x178]; invalidate whole control in parent (max rect) |
| 0x8015f0 | FLui_Layout_InvalidateRectInParent | invalidate a (clamped) sub-rect in parent → RedrawWindow |
| 0x7fe620 | FLui_Layout_RedrawChildRegion | RedrawWindow of a child's region on the host HWND |
| 0x7fe470 | FLui_Layout_InvalidateBounds | RedrawWindow(control bounds) before/after a move |
| 0x7fee50 | FLui_Layout_ResetHostPaintCache | reset host paint-cache region offset after bounds change |
| 0x801170 | FLui_Layout_ComputeClientRect | compute +0x330 client/content rect (scroll-adjusted) |
| 0x801140 | FLui_Layout_GetClientRect | vtbl[0xe8]; return +0x330 rect |
| 0x5d3a90 | FLui_Layout_GetLocalRect | vtbl[0x1b0]; return (0,0,w,h) |
| 0x5cfef0 | FLui_Layout_GetAbsoluteOrigin | vtbl[0xe0]; cumulative parent-chain origin (= dock origin) |
| 0x5cf670 | FLui_Layout_UpdateAnchorPivot | compute anchor pivot/center from +0xb3 + metrics GetEdge |
| 0x5d56b0 | FLui_Layout_SaveBoundsForAnchor | snapshot bounds to +0x2ec..+0x2f8 for anchoring |
| 0x5d4760 | FLui_Layout_CreateChildNode | vtbl[0xc0]; create the +0x11c child/ownership node (VMT 0x5bfad0) |
| 0x5e9900 | FLui_Layout_ChildNode_Ctor | node ctor: owner @+0x8, children TList @+0x24 |
| 0x50adb0 | FLui_Layout_ChildNode_SetParent | node vtbl[0x10]; **the parenting call** (dispatch to parent node) |
| 0x5e9a60 | FLui_Layout_ChildNode_Insert | node vtbl[0]; insert/assign child (Delphi component protocol) |
| 0x50aee0 | FLui_Layout_ChildNode_RejectIncompatible | reparent type-mismatch → error |
| 0x50ade0 | FLui_Layout_RaiseReparentError | raise EComponentError-style reparent exception |
| 0x5d72b0 | FLui_Layout_BeginUpdate | inc +0x318 suspend counter (defer relayout) |
| 0x5d72c0 | FLui_Layout_EndUpdate | dec +0x318; flush deferred relayout at 0 |
| 0x5d72f0 | FLui_Layout_FlushDeferredRelayout | run the pending realign (→ RealignChild) |

Cross-area (referenced, not renamed by this doc): `FLui_Input_PerformControlMsg@0x5d2870` (input agent) — the
per-control WindowProc dispatch; SetBounds sends it msg `0x47` (bounds-changed). Skin/render owns the `+0x340`
client-region/`+0x304` paint-context objects. `vtbl[0x138]`=`FLui_WP_SetParent@0x5d0850` (shared wp-core
primitive; the parent arg also carries the skin context — re/13's "skin-apply" label was a misread).

### Key vtable slots on TWPControl (resolved from VMT @0xefca08)
| slot | fn | meaning |
|------|----|---------|
| vtbl[0xc0] | 0x5d4760 | create +0x11c child node |
| vtbl[0xe0] | 0x5cfef0 | GetAbsoluteOrigin (dock origin) |
| vtbl[0xe8] | 0x801140 | GetClientRect (+0x330) |
| vtbl[0x110] | 0x801200 | RelayoutHook |
| vtbl[0x138] | 0x5d0850 | **FLui_WP_SetParent** — set parent @+0x78 (the contain/dock primitive; re/13 "skin" was wrong) |
| vtbl[0x178] | 0x8016a0 | InvalidateInParent |
| vtbl[0x188] | 0x802ef0 | **SetBounds(x,y,w,h)** |
| vtbl[0x1b0] | 0x5d3a90 | GetLocalRect |
| vtbl[0x1c0] | 0x71c9b0 | recompute client rect (→ ComputeClientRect) |
| vtbl[0x1c8] | 0x7ffdf0 | SetClientRegion |
| vtbl[0x1e0] | 0x7a6950 | set value range (min/max) (re/13) |
| vtbl[0x200] | 0x71d300 | show / set-visible |
| vtbl[0x250] | (per-class) | child-bounds-changed notify (anchor delta) |

### Docking → full map in **§6-dock**; Scroll/resize → full map in **§7**.
Quick index: dock `FLui_Dock_WindowSetAttached@0x1228CD0`, `RepositionHostedWindow@0x114B810`,
`SwitchAllEditors@0x110C320`, `ArrangeWorkspace@0x10F5130`, `ArrangeWindow@0x10F4FB0`, dock host `*(mainForm+0xb18)`.
Scroll `FLui_Layout_ScrollModel_Ctor@0x5E7990`, `ScrollGetEdge@0x5E7D60`, `ScrollOnChange@0x5D55B0`,
`ScrollContentRect@0x803430`; scrollbar widget `TQuickScroller`. Resize via `SetBounds`+OnResize(dyn 0xffcf).

---

## §6-dock — the docking system (deep map)

**Key insight: docking is just parenting into the workspace container.** The dock host `*(mainForm+0xb18)`
(mainForm=`*0x14A8750`; also aliased `DAT_01581200`) is an ordinary WP container control — it uses the SAME
generic layout vtable as everything in §1-§4 (`vtbl[0xe0]`=GetAbsoluteOrigin gives the dock origin,
`vtbl[0xe8]`=GetClientRect gives the dock-area rect, `vtbl[0x178]`/`vtbl[0x1c8]` arrange its children). Docked
windows are children of this container and are clipped/arranged by the ordinary cascade. So there are **no
special dock-zone/splitter objects** in the engine — a window is "docked" iff it is parented into the host
(hosted editors) or carries the dock bit + host-relative position (attached editors).

### FLui_Dock_WindowSetAttached@0x1228CD0 (window, attach) — dock(1)/float(0) ONE window
Two paths by whether the window has a host-wrapper at `+0xb8`:
- **Attached-style editor (`window+0xb8 == 0`):** acts only if requested ≠ current dock bit (`window+0xd4 & 4`).
  `dockOrigin = host.vtbl[0xe0]()` (host = `*(mainForm+0xb18)`).
  - **dock** (attach=1): `window+0xd4 |= 4`; `window+0xc0 += dockOrigin.x`, `window+0xc4 += dockOrigin.y`
    (screen → host-relative).
  - **float** (attach=0): `window+0xd4 &= ~4`; get host client rect (`host.vtbl[0xe8]`); compute the window's
    screen point (`FUN_005d0170(host, &window+0xc0)`); if it falls **outside** the host rect
    (`FUN_00420570`==0) snap to a default pos (x=0xC, y=6); else `window+0xc0/+0xc4 -= dockOrigin`
    (host-relative → screen). ⇒ **docked state = bit 0x4 of `window+0xd4`.**
- **Hosted editor (`window+0xb8 != 0`):** if the wrapper's parent state (`*(host+0x78) != 0`) disagrees with
  `attach` (and `window+0xe4==0` guard), re-dock the wrapper `host = window+0xb8`:
  `FormHide(host)` → `FLui_Dock_RepositionHostedWindow(host)` → `FormShow(host)`.
  ⇒ **docked state = wrapper has a non-null `+0x78` parent** (same parent convention as every WP control, §1).

### FLui_Dock_RepositionHostedWindow@0x114B810 (form) — reposition a wrapper when toggling docked/floating
1. If `form+0x4c2==2` (maximized) un-maximize first (`FUN_00836600(form,0)`), remember to restore.
2. Read current pos (`FUN_008337C0`=left, `FUN_008337F0`=top).
3. **floating → dock** (`form+0x78==0`): convert pos to host-relative (`FUN_005d0170(host,&pos)`); compute
   DPI-scaled padding (12px / 96px × the DPI factor `*(form+0x304)+0xb4 → +0xc`); **clamp** the docked
   position inside the host client rect (`host.vtbl[0xe8]`) so the window can't dock off-screen; **re-parent to
   docked** `form.vtbl[0x138](form, host)` = `FLui_WP_SetParent(form, host)` (host becomes the layout parent +0x78).
4. **dock → float** (`form+0x78!=0`): convert host-relative → screen (`FUN_005cffc0(host,&pos)`); **re-parent to
   floating** `form.vtbl[0x138](form, 0)` = `FLui_WP_SetParent(form, 0)`; `LockWindowUpdate(0)`.
5. Apply the new geometry with `form.vtbl[0x188]` (= `FLui_Layout_SetBounds`) keeping current w/h; restore
   maximize if it was set. **SetBounds is the universal positioner even for docking.**
   Coordinate transforms: `FUN_005d0170` = screen→host-relative, `FUN_005cffc0` = host-relative→screen.

### FLui_Dock_SwitchAllEditors@0x110C320 (mainForm, attached) — dock/float ALL editors
Iterate every channel plugin editor (`channelList=*0x14A98D8`, count @+0x10, item via `FLcr_ChannelListGetItem`)
+ every mixer effect editor (`g_MixerTrackArrayPtr`, track stride 0x1474, 10 slots @+0x1324+i*8) and call
`FLui_Dock_WindowSetAttached(editor, !attached)` on each. Wired from View → "Switch editors to
Attached/Detached" (re/22 §5).

### FLui_Dock_ArrangeWorkspace@0x10F5130 () — fit editor windows into the workspace rect
- Iterate the workspace/desktop manager `*0x14AC158`: count `FUN_0083D2D0`, item `FUN_0083D2B0`; for each item
  of class `&PTR_FUN_007de918` call `FUN_010F4FB0(item)` (arrange that window).
- Build the workspace rect from the manager geometry (`FUN_0083EDF0`=left, `FUN_0083EE40`=top,
  `FUN_0083EE60`=width, `FUN_0083EDD0`=height).
- For each channel editor + each mixer effect editor, issue window-command **`0x27` ("fit to rect")** via
  `FUN_0122ACF0(editor, 0x27, 0, &rect)`. (`FUN_0122ACF0` = the window command dispatch — window-mgr area.)
- Wired from View → "Arrange windows into workspace" (re/22 §5).

### Dock function map (FLui_Dock_*, tag UI_layout)
| addr | name | role |
|------|------|------|
| 0x1228CD0 | FLui_Dock_WindowSetAttached | dock(1)/float(0) one window; bit0x4@+0xd4; pos+0xc0/+0xc4 ±dockOrigin |
| 0x114B810 | FLui_Dock_RepositionHostedWindow | re-dock/float a wrapper: clamp into host, re-parent (SetParent vtbl[0x138]), SetBounds |
| 0x110C320 | FLui_Dock_SwitchAllEditors | dock/float ALL channel + mixer editors |
| 0x10F5130 | FLui_Dock_ArrangeWorkspace | fit editor windows into the workspace rect (cmd 0x27) |
| 0x10F4FB0 | FLui_Dock_ArrangeWindow | arrange one workspace window (called per-item by ArrangeWorkspace) |
| `*(mainForm+0xb18)` | (dock host container) | WP container; docked windows = children; vtbl[0xe0]/[0xe8] = dock origin/rect |
| 0x005d0170 | (xform) screen→host-relative | used when docking |
| 0x005cffc0 | (xform) host-relative→screen | used when floating |

### Dock-our-window recipe (deepens §6.C; for #80 window-embed)
Two routes:
- **Embed route (cleanest — native arrange):** `host=*(*0x14A8750+0xb18); FLui_WP_SetParent(ourCtrl, host)`
  (= `ourCtrl->vtbl[0x138](ourCtrl, host)`). Our control becomes a workspace child, arranged/clipped/sized by the
  host exactly like a docked editor; position it with `SetBounds` in host-client coords. To **float** later,
  `FLui_WP_SetParent(ourCtrl, topLevelForm)` (or `…, 0`). (No dock bit / wrapper needed — you ride the ordinary
  §3 parenting. Raw node form: `(*(*(ourCtrl+0x11c))->vtbl[0x10])(*(ourCtrl+0x11c), *(host+0x11c))`.)
- **Window route (behave like an FL editor):** give our form the standard window struct (re/22: `+0xa9`
  visible, HWND@+0x2b0, pos `+0xc0/+0xc4`, flags `+0xd4`, optional wrapper `+0xb8`). Then
  `FLui_Dock_WindowSetAttached(ourForm, 1|0)`. If we set `ourForm+0xb8` = a wrapper whose `+0x78` parent points
  at the dock host, `WindowSetAttached` re-docks it through `RepositionHostedWindow` (clamp + re-parent +
  SetBounds), and it then responds to "Switch editors to Attached/Detached" + "Arrange into workspace".
- **Teardown** (main thread, before FreeLibrary, cf. re/22): detach our child
  (`ChildNode_SetParent(ourNode, 0)`) and/or float+close our form first, so the host's children list / dock
  state hold no dangling pointer.

## §7 — scroll / viewport + resize (deep map)

### The scroll model object `ctrl+0xd0`
Created in base init via `FLui_Layout_ScrollModel_Ctor@0x5E7990` (class VMT `&PTR_FUN_005bd558`; shares the
link-base with the `+0x11c` node — `vtbl[0x10]`=`ChildNode_SetParent@0x50adb0`). Fields:
`+0x08 owner ctrl`, **`+0x10 scrollX`, `+0x14 scrollY`, `+0x18 extentW`, `+0x1c extentH`** (content beyond the
control's own w/h). Change-callback `FLui_Layout_ScrollOnChange@0x5D55B0` simply fires the owner's
`vtbl[0x110]` (RelayoutHook) → `ComputeClientRect` re-runs → all children re-positioned (§4). **So a relayout
is sufficient to apply a new scroll offset.**

`FLui_Layout_ScrollGetEdge@0x5E7D60 (scrollObj, idx)` = the scroll model's `vtbl[0x20]` edge accessor (feeds
both `ComputeClientRect`-style math and the anchor pivots in `UpdateAnchorPivot`). When
`owner+0xa0 & 0x100000` (scroll-adjust enabled) and the owner has a parent, it returns scroll-adjusted edges;
otherwise raw. Index map: `0=left(+0x90)-sX`, `1=top(+0x94)-sY`, `2=right(+0x98)+sX+extW`,
`3=bottom(+0x9c)+sY+extH`; `4..7` = same against the **reference/saved bounds** `+0x2ec..+0x2f8` (the anchor
baseline from `SaveBoundsForAnchor`). So **scrollability is gated by two flags**: `+0x320 bit 0x2000` (client
rect uses scroll offset, §4) and `+0xa0 bit 0x100000` (edge accessor applies offset).

### Pixel scroll — FLui_Layout_ScrollContentRect@0x803430 (ctrl, &rect, dx, dy, flags)
The actual visual scroll: Win32 `ScrollWindowEx` on the control's HWND `+0x45c`, blitting existing pixels by
(dx,dy) within `rect` and handling the update region (combine + invalidate). `flags`: bit0 = combine with
current update region, bit1 = invalidate-only (no blit; for `&PTR_FUN_007fc270` controls calls
`FUN_00802fe0` instead), bit2 = skip the pre-step (`FUN_007fe500`). This is the cheap "scroll already-painted
content" used by the scroller wiring below (logical offset lives on `+0xd0`; this just moves the pixels).

### Scrollbar widget — TQuickScroller (`:tquickscroller`)
The WP scrollbar control class is **`TQuickScroller`** (sub-parts `TQuickScrollerBtn`, `TQuickScrollerHandle`;
box variant `TQuickScrollBoxScroller`; adjust hook `TOnAdjustScroller`). Skin descriptor suffix
`:tquickscroller`, e.g. `forms.eventeditform.controls.scrollbar:tquickscroller`,
`*.userview.scrollbar`. It is a value control (like a wheel) — create it like any WP control (re/13) with that
descriptor.
- **Thumb size** = `*ScrollerSetKnobWidth` (e.g. `TEventEditForm.EventScrollerSetKnobWidth@0xD41870` just
  clears bit0 of the width) — set proportional to viewport/content.
- **OnChange wiring** (canonical, from `TQuickPopupMenuWindow.ScrollBarChange@0x711640`):
  ```
  oldOfs = win+0x7a0
  newOfs = FUN_006d4dc0(scroller)              // read scroller value
  win+0x7a0 = newOfs
  rect = viewportRect(win)                     // vtbl[0xe8] + chrome insets, cf. FUN_007115b0
  FLui_Layout_ScrollContentRect(win, &rect, oldOfs-newOfs, 0, 1)   // X-axis; swap args 3/4 for Y
  ```
  Other scrollers (`TEventEditForm.*ScrollerChange`) post a message then call the content panel's
  `vtbl[0x178]` to re-arrange. Auto-scroll on drag/wheel + `MSH_SCROLL_LINES_MSG` / `WheelScrollLines` also
  exist.

### Resize handling (form/control resize → relayout)
There is **no separate per-control WM_SIZE relayout pass** — resize flows through `FLui_Layout_SetBounds` → the §2
cascade (→ parent `RealignChild`/`SetClientRegion` re-fits children; scrollable children recompute via §4).
At the **top-level form** the Win32 WM_SIZE drives the VCL-style `OnResize` handler (concrete-window area; e.g.
`TStepSeqForm.FormResize@0xf47ec0` — a thin thunk into the relayout), which `SetBounds`-es the root content
control and lets the cascade flow down. Per control, `SetBounds` also fires Delphi **dynamic method 0xffcf
(OnResize)** when `(+0x34 & 1)==0` — the hook a form/panel overrides for custom relayout (reposition a
`TQuickScroller`, recompute extents on `+0xd0`). `FUN_007fe620` (tail of `InvalidateRectInParent`) does the
`RedrawWindow`/region invalidation on the owning HWND `+0x45c`. **Reuse:** to relayout on resize, override the
control's `0xffcf` dynamic (or the form's `OnResize`) and re-issue child `SetBounds` there (batch via
Begin/EndUpdate).

### Scroll/resize function map (tag UI_layout)
| addr | name | role |
|------|------|------|
| 0x5E7990 | FLui_Layout_ScrollModel_Ctor | ctor of the +0xd0 scroll/inset object (VMT 0x5bd558) |
| 0x5E7D60 | FLui_Layout_ScrollGetEdge | +0xd0 vtbl[0x20] edge accessor (idx 0-7, scroll/ref adjusted) |
| 0x5D55B0 | FLui_Layout_ScrollOnChange | scroll-changed callback → vtbl[0x110] relayout |
| 0x803430 | FLui_Layout_ScrollContentRect | ScrollWindowEx pixel blit on ctrl HWND +0x45c |
| 0x801170 | FLui_Layout_ComputeClientRect | (owned in §4) scroll-adjusted client rect +0x330 |
| (string) | TQuickScroller / :tquickscroller | the WP scrollbar widget |
| (dyn 0xffcf) | OnResize | Delphi dynamic method fired by SetBounds (custom relayout hook) |

### Scrollable-area recipe (deepens §6.B)
1. Viewport = a WP control; set `viewport+0x320 |= 0x2000` (client rect uses scroll) and, for the edge
   accessor, `viewport+0xa0 |= 0x100000`.
2. Set content extent on the scroll model: `*(i32*)(scroll+0x18)=extraW`, `*(i32*)(scroll+0x1c)=extraH`
   (scroll = `*(viewport+0xd0)`). Parent children into the viewport (§3) — they now live in scrolled coords.
3. Add a `TQuickScroller` (descriptor `…scrollbar:tquickscroller`), set its knob width = viewport/content.
4. On its OnChange: write `scroll+0x10`(X)/`scroll+0x14`(Y) = new offset, then either
   `FLui_Layout_ScrollContentRect(viewport,&rect, dx, dy, 1)` + `viewport.vtbl[0x178]`, or the minimal path —
   just call `viewport.vtbl[0x110]` (RelayoutHook) which `ScrollOnChange` proves re-fits all children.

## Concurrency note (Ghidra renames)
This area was worked by multiple agents in parallel on one shared Ghidra DB; layout-core names converged
(SetBounds/SetRectCore/RelayoutHook/RealignChild/GetClientRect/GetAbsoluteOrigin/CreateChildNode/ChildNode_*/
ComputeClientRect) and the dock + scroll/resize funcs above were named `FLui_Dock_*` / `FLui_Layout_Scroll*`
and tagged `UI_layout`. **Addresses are authoritative** if any live name has drifted. Cross-area finds noted,
not renamed: `FUN_0122ACF0` (window-cmd dispatch — window-mgr), `TQuickScroller`/`TEventEditForm.*ScrollerChange`
(concrete-window area), `FUN_007fe620`/`+0x340`/`+0x304` (skin/render paint).
