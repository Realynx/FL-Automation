# UI Gap G3 — Docking internals (RepositionHostedWindow + L/R sub-sites + host vtbl contract)

Target: `FLEngine_x64.dll` (image base `0x400000`). Unblocks issue **#80** (embedding/docking our window into FL's workspace).
Static RE only. All addresses are runtime VAs.

Anchors: main form = `TFruityLoopsMainForm` (VMT `0x1060840`), singleton `DAT_01581200 == *PTR_DAT_014a8750`.
FormCreate `0x10bcf70`, FormResize `0x10ca8f0`.

---

## 0. TL;DR

- **`FLui_Dock_RepositionHostedWindow@0x114b810`** is the *toggle* that re-parents an editor's embeddable host-form between the **central dock host (`*(mainForm+0xB18)`)** and floating. If the form is currently un-parented it clamps it into the host client rect and `SetParent(dockHost)`; if currently parented it `SetParent(0)` to float. Final `SetBounds`.
- **`mainForm+0xB38` = LEFT work/dock sub-site, `mainForm+0xB40` = RIGHT work/dock sub-site** (the resizable side regions flanking the central workspace). CONFIRMED: the Work splitter sizes one of them based on the central-fill align byte (3=alLeft→`+0xB38`, 4=alRight→`+0xB40`), and the default-desktop-layout routine docks the Browser/SampleList form into `+0xB38`.
- The "dock host" is a **plain `TWinControl`** — docking uses generic VCL vtbl slots (`ClientOrigin`/`ClientRect`/`SetParent`/`SetBounds`), not a bespoke dock interface.

---

## 1. Main-form region field map (singleton fields, `idx*8 = offset`)

| field idx | offset | role |
|-----------|--------|------|
| `0x162` | `+0xB10` | TOP toolbar band (aliased to `param_1[0x670]`) |
| `0x163` | `+0xB18` | **CENTRAL DOCK HOST** — editor windows are `SetParent`-ed here |
| `0x164` | `+0xB20` | BOTTOM band (aliased to `param_1[0x671]`) |
| `0x166` | `+0xB30` | central workspace **FILL** panel; its align byte `+0xAD` = 3 (alLeft) or 4 (alRight) selects the left/right-handed workspace orientation |
| `0x167` | `+0xB38` | **LEFT work/dock sub-site** (hosts the Browser / SampleList panel) |
| `0x168` | `+0xB40` | **RIGHT work/dock sub-site** |

FormCreate inits ONLY `+0xB18` with the dock-host style flag (`FUN_007ffa50(param_1[0x163],1)` sets control style bits `0x11`). `+0xB38/+0xB40` are *not* dock-host-styled — they are VCL DockManager side sites, a different mechanism (see §6).

### Evidence that +0xB38/+0xB40 are LEFT/RIGHT sub-sites
- `TFruityLoopsMainForm.FormResize@0x10ca8f0` realigns exactly three things each resize: the form, `param_1[0x167]`(+0xB38), `param_1[0x168]`(+0xB40), via `FLui_Layout_RealignChildRegion`.
- `TFruityLoopsMainForm.WorkSplitterChanging@0x1104bb0`: reads central-fill align `*(param_1[0x166]+0xAD)`; if `==3` → `SetWidth(param_1[0x167]/+0xB38, newSize)`, else (`==4`) → `SetWidth(param_1[0x168]/+0xB40, newSize)`. The splitter literally resizes one side region.
- `FLui_Dock_ApplyDefaultDesktopLayout@0x10f5540` (called by `TShortcutsModule.SetDefaultDesktopLayoutActionExecute`): `FUN_005d2130(browserForm /*PTR_DAT_014abff8*/, param_1[0x167]/*+0xB38*/, 0, 5)` + `TSampleListForm.FormEndDock(...,+0xB38,0)` + `SetWidth(+0xB38, width)`; then `FLui_WP_SetShowing(+0xB38,1)`/`(+0xB40,1)`. The Browser docks into `+0xB38`.
- `FLui_Dock_GetLeftSiteWidth@0x10f4ee0` (renamed) returns `*(mainForm+0xB38)+0x98` (the left site's Width field).

Confidence: **HIGH**. Caveat: "left/right" can swap with the workspace orientation (central-fill align is user-configurable). Structurally `+0xB38` pairs with align 3, `+0xB40` with align 4.

---

## 2. Hosted-window / control field offsets

These are on the **embeddable host-form** object that `RepositionHostedWindow` operates on (`= editorObj+0xB8`):

| offset | meaning |
|--------|---------|
| `+0x78`  | **Parent pointer** (idx `0xf`). `0` = floating/un-parented; set = parented. Reposition toggles on this. |
| `+0x90`  | Left / BoundsRect.x (idx `0x12`) |
| `+0x94`  | Top / BoundsRect.y |
| `+0x98`  | **Width** (idx `0x13`) |
| `+0x9c`  | **Height** |
| `+0xC0/+0xC4` | "docked-position" pair adjusted by `WindowSetAttached` (attached-style only) by ± dock origin |
| `+0xD4`  | style flags; bit `0x4` = "pos currently offset by dock origin" (attached-style) |
| `+0xE4`  | guard byte — `WindowSetAttached` skips the toggle if nonzero |
| `+0x4C2` | window-state byte (0 normal, 2 maximized); drives `ShowWindow(SW_* = DAT_012ee920[state])` via `FUN_00836600` |
| `+0xA9`  | "has handle / can arrange" flag (checked by `FLui_Dock_ArrangeWindow`) |

On the **editor object** itself: `+0xB8` = pointer to its embeddable host-form. `+0xB8==0` ⇒ "attached-style" (no separate host-form); `+0xB8!=0` ⇒ "hosted-style" (has a host-form that re-parents into `+0xB18`).

---

## 3. Dock-host vtbl contract

The dock host is `*(mainForm+0xB18)`, a generic `TWinControl`. Slots used by docking:

| vtbl slot | meaning |
|-----------|---------|
| `[0xE0]` | **ClientOrigin** → packed point (x in low 32, y in high 32). Origin of the host client area. |
| `[0xE8]` | **GetClientRect(TRect\*)** → `{left, top, right, bottom}` (16 bytes). |

These are *generic* TControl slots, also used everywhere via the helpers in §5 — the host is not special.

The **hosted window/control** vtbl (the form being docked) uses:

| vtbl slot | meaning |
|-----------|---------|
| `[0x138]` | **SetParent(parent)** — `parent=dockHost` to attach, `0` to float. THIS is the "register/unregister into host" call. |
| `[0x188]` | **SetBounds(x, y, w, h)** |
| `[0x1C8]` | **AlignControls(arg, TRect\*)** — realign children (used by `FLui_Layout_RealignChildRegion`) |
| `[0x308]` | set region/inset rect (used by band setup `FUN_007e7a70`) |
| `[0x310]` | get client size into out-buf (used by FormResize / WorkSplitterChanging) |
| `[0x340]` | set "updating"/lock(bool) (BeginUpdate-style, used by ApplyDefaultDesktopLayout) |
| `[0x190]` (`400`) | realign/invalidate (called at end of WorkSplitterChanging) |

There is **no dedicated FL "add hosted child" function** — registration is just the VCL `SetParent` slot `[0x138]`.

---

## 4. `FLui_Dock_RepositionHostedWindow@0x114b810` — exact math

Param = the embeddable **host-form** `F` (`= editorObj+0xB8`). Globals: `dockHost = *(DAT_01581200+0xB18)`.

```
1. if F != 0 && F[+0x4C2]==2 (maximized):  FUN_00836600(F,0)  // restore;  remember wasMax=true
2. cur = { GetLeft=FUN_008337c0(F), GetTop=FUN_008337f0(F) }   // local_30 = {x,y}

3. if (F[+0x78] == 0)        // currently UNPARENTED -> ATTACH into dockHost
   a. cur = cur - dockHost.ClientOrigin            // FUN_005d0170(dockHost,&cur)  (screen->host-client)
   b. marginX=marginY=0
      formRect  = BoundsRect(F)        // FUN_005cfd60: {x,y,x+w,y+h} from +0x90/+0x94/+0x98/+0x9c
      mainRect  = BoundsRect(mainForm)
      if formRect sticks out of mainRect on ANY edge:
          scale = *(*(mainForm+0x304)+0xB4) + 0x0C  (float)
          marginX = round(scale*12);  marginY = round(scale*96)
   c. hostClient = dockHost.vtbl[0xE8]()            // {left,top,right(=R),bottom(=B)}
      maxX = R - F.Width(+0x98) - marginX
      x = clamp(cur.x, marginX, maxX)               // min then max
      maxY = B - F.Height(+0x9c) - marginY
      y = clamp(cur.y, marginY, maxY)
   d. F.vtbl[0x138](F, dockHost)                    // SetParent(dockHost)  == REGISTER as hosted child

4. else                       // currently PARENTED -> DETACH / FLOAT
   a. pos = cur + dockHost.ClientOrigin             // FUN_005cffc0  (host-client->screen)
   b. F.vtbl[0x138](F, 0)                           // SetParent(0) == float
   c. LockWindowUpdate(0)

5. F.vtbl[0x188](F, x, y, F.Width(+0x98), F.Height(+0x9c))   // SetBounds
6. if wasMax:  FUN_00836600(F,2)                    // re-maximize
```

Key points:
- It is a **toggle**: attaches if floating, detaches if parented. The "Detach" menu handlers (`EE/SS/FX/PDetachMenuClick`) call it directly to flip state.
- Clamp keeps the attached form inside the host client area with a `(12,96)*DPIscale` margin only when it would otherwise spill outside the main form.
- Pos values move through host `ClientOrigin` (`-` on attach, `+` on detach), i.e. screen↔host-client conversion.

---

## 5. Supporting helpers (generic VCL — NOT renamed; cross-area)

| addr | role |
|------|------|
| `FUN_005d0170` | `pt - host.ClientOrigin(vtbl[0xE0])` (screen→client) — hundreds of callers |
| `FUN_005cffc0` | `pt + host.ClientOrigin` (client→screen) |
| `FUN_005cfd60` | BoundsRect getter `{ +0x90, +0x94, +0x90+0x98, +0x94+0x9c }` |
| `FUN_005cf940` | **SetWidth(w)** = `vtbl[0x188](Left,Top,w,Height)` + dirty bit (left as-is) |
| `FUN_005d2130` | VCL **DockManager manual-dock** (sends WM_ `0xffbb`/`0xffb3` to the site's dock manager via `FUN_0040ffe0`) — the heavy side-site path |
| `FUN_00836600` | window-state set: writes `+0x4C2`, `ShowWindow(SW_* = DAT_012ee920[state])` |
| `FUN_008337c0`/`008337f0` | GetLeft / GetTop (DPI-aware) |
| `FUN_0083af10`/`FUN_007ea5d0` | FormHide / FormShow (used around Reposition in WindowSetAttached) |

Window-manager link `*PTR_DAT_014aa6e8`: FormCreate wires `mgr+0x228=mainForm`, `mgr+0x220=FLui_Shell_HintBarDefaultText`. It is the hint-bar/app controller — **does NOT participate** in the reposition math.

---

## 6. The two docking mechanisms

**A. Editor windows ↔ central dock host (`+0xB18`)** — the primary embed path.
- Attach/detach a single editor's host-form: `FLui_Dock_RepositionHostedWindow(hostForm)` (toggle) — or `FLui_Dock_WindowSetAttached(editor, attach)` which, for hosted-style editors, does `FormHide + RepositionHostedWindow + FormShow`.
- Bulk dock/undock all editors: `FLui_Dock_SwitchAllEditors@0x110c320` iterates every channel editor (`PTR_DAT_014a98d8` list) and every mixer-track editor (`g_MixerTrackArray + i*0x1474 + 0x1324`), calling `WindowSetAttached(win, attach)`.
- Per-window keep-on-screen clamp: `FLui_Dock_ArrangeWindow@0x10f4fb0` (clamps a window's BoundsRect into its parent's client rect, or the monitor work-area if floating).

**B. Side panels ↔ left/right sub-sites (`+0xB38`/`+0xB40`)** — VCL DockManager path.
- `FLui_Dock_ApplyDefaultDesktopLayout@0x10f5540` docks the Browser into `+0xB38` via `FUN_005d2130(browser, +0xB38, 0, 5)` + `FormEndDock`, then `SetWidth` both sites and shows them. Requires a fully VCL-dockable `TForm`.

---

## 7. #80 dock recipe (embed OUR window)

`mainForm = *PTR_DAT_014a8750` (== `DAT_01581200`).

### Recommended: attach into the central dock host (`+0xB18`), exactly like FL's editors
Our control must be a child window/`TWinControl`-compatible control with the field/vtbl layout in §2/§3 (a real VCL control if we go through the engine; a Win32 child HWND if we drive it natively).

Attached-style flow (mirrors RepositionHostedWindow ATTACH branch):
```
dockHost = *(mainForm + 0xB18)
ourForm.Parent (+0x78)        // currently 0 (floating)
hostClient = dockHost.vtbl[0xE8]()                 // client rect
origin     = dockHost.vtbl[0xE0]()                 // client origin
pos        = desiredScreenPos - origin             // -> host-client coords
x = clamp(pos.x, 0, hostClient.right  - ourWidth)
y = clamp(pos.y, 0, hostClient.bottom - ourHeight)
ourForm.vtbl[0x138](ourForm, dockHost)             // SetParent(dockHost)  -> registers as hosted child
ourForm.vtbl[0x188](ourForm, x, y, ourWidth, ourHeight)   // SetBounds
```
Shortcut: set `ourForm+0x78 = 0` then just call `FLui_Dock_RepositionHostedWindow(ourForm)` — it performs the attach + clamp + SetParent + SetBounds for us (it attaches because `+0x78==0`).

Field writes to make before/around it:
- `ourForm+0x4C2 = 0` (not maximized) unless you want it maximized in-host.
- After attach, `ourForm+0x78` will equal `dockHost`.

Detach / float (teardown step 1):
```
FLui_Dock_RepositionHostedWindow(ourForm)   // toggles: since +0x78 now set, it SetParent(0) + floats
// or manually: ourForm.vtbl[0x138](ourForm, 0); LockWindowUpdate(0);
//              ourForm.vtbl[0x188](ourForm, screenX, screenY, w, h)
```

Full teardown (before freeing our window):
1. If `+0x4C2==2` (maximized): `FUN_00836600(ourForm,0)` to restore.
2. `ourForm.vtbl[0x138](ourForm, 0)` — un-parent (VCL removes us from the host child list).
3. Free our control. Do NOT free while still parented into `+0xB18`.

### Alternative: dock into left/right side sites (`+0xB38`/`+0xB40`)
Only if our window is a fully VCL-dockable `TForm`:
```
site = *(mainForm + 0xB38)   // or +0xB40
FUN_005d2130(ourForm, site, dockType, align)   // VCL ManualDock (align byte ~5 seen for browser)
FUN_005cf940(site, desiredWidth)               // SetWidth
FLui_WP_SetShowing(site, 1)
```
Heavier and needs VCL dock participation; for an injected native window, prefer the `+0xB18` `SetParent` path.

---

## 8. Renames / tags applied (this gap)

Renamed `FUN_*` → `FLui_Dock_*` and tagged `UI_layout`:
- `0x10f5540` → **`FLui_Dock_ApplyDefaultDesktopLayout`** (Set-Default-Desktop-Layout routine; docks browser into `+0xB38`)
- `0x10f4ee0` → **`FLui_Dock_GetLeftSiteWidth`** (returns `*(mainForm+0xB38)+0x98`)

Tagged `UI_layout` (kept existing names):
- `0x114b810` `FLui_Dock_RepositionHostedWindow`
- `0x5d71d0`  `FLui_Layout_RealignChildRegion`
- `0x10ca8f0` `TFruityLoopsMainForm.FormResize`
- `0x1104bb0` `TFruityLoopsMainForm.WorkSplitterChanging`
- `0x10f4fb0` `FLui_Dock_ArrangeWindow`
- `0x1228cd0` `FLui_Dock_WindowSetAttached`
- `0x110c320` `FLui_Dock_SwitchAllEditors`

Generic VCL helpers intentionally **not** renamed (cross-area, 100s of callers): `FUN_005d0170`, `FUN_005cffc0`, `FUN_005cfd60`, `FUN_005cf940` (SetWidth), `FUN_005d2130` (DockManager dock), `FUN_00836600` (window-state).

Program saved.

---

## 9. Confidence & open items

- RepositionHostedWindow math: **HIGH** (full decomp).
- Host/window vtbl contract `[0xE0]/[0xE8]/[0x138]/[0x188]/[0x1C8]`: **HIGH**.
- `+0xB38`=LEFT, `+0xB40`=RIGHT sub-sites: **HIGH** (splitter align 3/4 + browser `FormEndDock` into `+0xB38` + `GetLeftSiteWidth`). Note L/R can swap with workspace orientation (central-fill align is user-set).

Open:
- Exact `dockType`/`align` arg semantics of `FUN_005d2130` (VCL DockManager) — only partially traced (align `5` observed for the browser).
- Whether `+0xB18` hosts multiple simultaneous children with auto-tiling, or windows overlap unless arranged — Reposition clamps each independently; `ApplyDefaultDesktopLayout` positions specific windows (mixer at `g_MixerManagerPtr`, etc.) by hand.
- DPI scaling internals of `FUN_008337c0/f0` (GetLeft/GetTop) not fully traced.

**G3 status: RESOLVED.** All three sub-goals (reposition math, `+0xB38/+0xB40` identity = L/R dock sub-sites, dock-host vtbl contract) are nailed, with a concrete #80 recipe for both the central-host and side-site paths. Only minor DockManager arg semantics remain open.
