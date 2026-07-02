# UI gap G4 — popup-menu WINDOW paint, hint-bar paint, drag cursors

Binary: `FLEngine_x64.dll`, image base `0x400000`. Static RE (Ghidra). This file fills
gap **G4** (the un-RE'd UI *rendering* surfaces). re/16 + re/23 covered the menu MODEL
(action list / categories / `TQuickMenuItem`/`TQuickPopupMenu`/`TQuickMenuProp`); this
covers the popup *window paint*, the floating hint-bar paint + hint grammar, and the
drag cursor mechanism. Confidence: **HIGH** for all three areas (function bodies + the
VMT + a string-table cross-check all corroborate).

All renamed funcs tagged `UI_skin` (rendering) or `UI_forms` (lifecycle).

---

## 1. Popup-menu WINDOW (`TQuickPopupMenuWindow`)

### Class
- Class = **`TQuickPopupMenuWindow`**, **derives from `TBaseGlassForm`** (ctor calls
  `TBaseGlassForm_Create`). It is one skinned WP/glass window **per submenu level**
  (a list of them is held on the menu engine at `engine+0xe0`).
- **VMT base = `0x706688`** (triangulated: `Destroy`@vtbl+0x38, `NewInstance`@vtbl+0x28
  =0x40f960, `FreeInstance`@vtbl+0x30=0x40f990 all line up). The class-ref pointer Ghidra
  shows as `&PTR_FUN_007066e0`.

### Key methods (renamed)
| Addr | Name | Role |
|------|------|------|
| `0x7127b0` | `TQuickPopupMenuWindow.Create` | ctor; builds skin-metrics obj (`win+0x808` = `FUN_0070a120(&DAT_00705e58,1)`), anim helper (`win+0x7a4`), anim durations (750/300/200 ms scaled) |
| `0x7129d0` | `TQuickPopupMenuWindow.Destroy` | dtor (vtbl+0x38); frees `+0x7bc/+0x808/+0x7a4` |
| `0x70b000` | `FLui_Menu_CreateWindow` | creates+wires one window level, then calls layout |
| `0x70f070` | `FLui_Menu_LayoutItems` | MEASURE/LAYOUT pass: builds column array (`win+0x790`) + per-row rect array (`win+0x798`), multi-column wrap, scrollbar, then `vtbl+0x188` (SetBounds) to size/position |
| `0x7116f0` | `FLui_Menu_MeasureRow` | per-row measure (font select by type, shortcut/arrow glyph widths) |
| `0x710370` | `FLui_Menu_WindowPaint` | **the paint** — VMT slot `0x706880` = **vtbl+0x1f8** |
| `0x711bb0` | `FLui_Menu_PaintRow` | per-row paint: chooses per-state colors, fills row, dispatches content+glyph |
| `0x7108c0` | `FLui_Menu_DrawRowContent` | draws row label / header caption / separator / owner-draw |
| `0x712540` | `FLui_Menu_DrawRowGlyph` | draws check / radio / submenu-arrow / item-icon glyph |
| `0x712a60` | `FLui_Menu_GetDefaultSize` | default size override (min width 0x188) |

### Spawn path
`FLmenu_ShowPopup(engine,x,y,ctrl,root)` @ **`0x70ab80`** (re/16):
1. close any open popup, build child nodes / populate items.
2. `FLui_Menu_CreateWindow(engine, rootItemNode, x, y)` → `TQuickPopupMenuWindow.Create`
   → set per-item value/colors/font color → `FLui_Menu_LayoutItems` (measure+place).
3. `SetWindowsHookExW(WH_CALLWNDPROC=4,…)` + `SetWindowsHookExW(WH_MOUSE=7,…)` for modal
   tracking; `SetCapture` on the window.

### Window object layout (offsets used by paint)
- `+0x304` canvas/context holder → `+0xa0` = active render target (cached to `win+0x800`)
- `+0x760` fade alpha byte · `+0x768` scrollbar (`+0xa9` visible, `+0x9c` width)
- `+0x770` "hot"/highlighted item ptr · `+0x780` menu engine (owner)
- `+0x788` has-icon-column flag · `+0x789` has-submenu/shortcut-column flag
- `+0x790` column array (12-byte recs: `+4`=col width, `+8`=gap)
- `+0x798` **per-row rect array, 0x20-byte recs**: `+0..0xF` rect L,T,R,B · `+0x10` col idx ·
  `+0x14` float arrow-offset · `+0x18` float hover/anim phase · `+0x1c` u16 align/flags ·
  `+0x1e` selected flag · `+0x1f` row TYPE (0=item, 1=separator, 2=header/caption)
- `+0x7a0` v-scroll offset · `+0x7b8` press/swipe anim factor (float) · `+0x7bc` cached
  bitmap (if set, paint just blits it) · `+0x7e8/+0x7ec` scratch bg/fg color ·
  `+0x7f0` cur item · `+0x7f8` cur item idx · `+0x800` render target · `+0x808` skin metrics ·
  `+0x810` item list (`FUN_0081dda0`=count, `FUN_0081ddc0(list,i)`=item) · `+0x818` submenu depth

### Skin-metrics object (`win+0x808`, ctor `&DAT_00705e58`) — the menu skin recipe
Fonts: `+0x08+style*8` style-indexed LABEL fonts · `+0x18` header font · `+0x20` value/2nd-glyph
font · `+0x28` SYMBOL font (shortcut + check/radio/arrow) · `+0x30` item-icon font.
Colors: `+0x38` window bg · `+0x3c` content/row bg · `+0x40` text · `+0x44` selected bg ·
`+0x48` selected text · `+0x4c` disabled text · `+0x50` header bg / separator color ·
`+0x54` header text. Metrics: `+0x68` DPI unit (float) · `+0x6c/0x70/0x74/0x78` paddings ·
`+0x80` row height · `+0x84` h-text pad · `+0x88` border inset/corner radius ·
`+0x90/0x94` left inset (icon col present/absent) · `+0x98/0x9c` right inset (sub/shortcut
present/absent) · `+0xa0` icon-col width · `+0xa4` icon-text gap · `+0xac` separator-row
height · `+0xb0` separator line thickness · `+0xb4` separator v-margin · `+0xb8` value gap.

### Item-row DRAW recipe
`FLui_Menu_WindowPaint`: cache render target; if `win+0x7bc` cached bitmap set → blit it;
else full draw: `FUN_006ac570` rounded bg (color metrics+0x38, radius metrics+0x88) →
push clip (`FUN_006b8130/006b7fc0`) → fill content (metrics+0x3c) → loop items: when the
column index changes draw the column gap, then for each visible row
(`FUN_006b81a0` intersects clip) call `FLui_Menu_PaintRow` → composite at fade alpha
(`FUN_006b2940`, `win+0x760`) → pop clip.

`FLui_Menu_PaintRow` picks colors by state into `win+0x7e8` (bg)/`win+0x7ec` (fg):
default = metrics +0x3c/+0x40; selected (item==`win+0x770` & row.selected) = +0x44/+0x48
(hi-DPI blends bg by anim phase row+0x18); disabled (item+0x81==0) fg = +0x4c; header
(row.type>1) = +0x50/+0x54. Fills the row if bg ≠ default, computes text rect (insets per
icon/sub columns), then `FLui_Menu_DrawRowContent`; then if icon column → glyph index from
item state (checked→3, checkable box→1/2, radio→9, default→0xc) drawn via
`FLui_Menu_DrawRowGlyph`; if sub column & item has children → submenu arrow (glyph 0).

`FLui_Menu_DrawRowContent` (3 branches by row.type):
- **item (0)**: shortcut glyph (font metrics+0x28, right) → label text `item+0x78`
  (font metrics+0x8+`item.style(+0x82)`*8, flags 0x18, color `win+0x7ec`) → value icon
  `item+0xb8` (font metrics+0x20). Draw via `FUN_006b3600`.
- **header (2)**: caption (substr of `item+0x78` from idx 2) in header font metrics+0x18,
  alignment from row+0x1c bits; optional trailing icon in metrics+0x30. Via `FUN_006b3970`.
- **separator (1)**: thin fill (metrics +0xb0/+0xb4) in color metrics+0x50.
- **owner-draw** (item+0x110 or +0x120 ≠ 0): calls the item's own TMethod draw callback
  (`item+0x118`/`item+0x128`) with state flags (selected/checkable/disabled).

`FLui_Menu_DrawRowGlyph(win, rect, glyphIdx, alpha, ch)`: glyph codepoints from table
`&DAT_012dcf32[glyphIdx*2]` drawn in symbol font metrics+0x28; idx 1/2 = check box,
3 = check, 9 = radio (+ extra dot `DAT_012dcf48` if checked), 0xc = item icon `ch`
(font metrics+0x30), 0 = submenu arrow. Disabled rows dim alpha (`*0x48>>8`).

---

## 2. Floating hint bar (`TFLHintBarForm`)

Singleton `DAT_0157c980`. Paint = **`TFLHintBarForm.FormPaint` @ `0xb65730`** (vtbl[0x178],
called by `FLui_Hint_BarRefresh@0xb654a0`). Setter `FLui_SetStatusHintCore@0x10ec570` /
`FLui_SetStatusHintAndRefresh@0x10ec870` store the current hint UStr in `DAT_015817d0`
(auto-clear `DAT_015817e0`=0x5dc ticks). Current accent/value color comes from
`PTR_DAT_014abbb8` blended via `FLui_Skin_BlendColor`.

`FormPaint` parses the hint string inline and draws each segment; the markup STRIPPER
(used to get plain text + flags) = **`FLui_Hint_StripMarkup` @ `0x628f90`**. Section
delimiter char = `^` (`DAT_00b66624` / `DAT_0062907c` = `"^"`).

### Hint-string grammar (verified vs. real strings)
Top level: **`<tooltip>|<statusbar>`** — `|` (Delphi VCL short/long convention) splits the
short tooltip (left) from the status-bar text (right). A leading `|` = status text only.
(NB: `|` is reused elsewhere for file filters `"%s (*.%s)|*.%1:s"` and CSV — that is not a
hint.) The status-bar part uses `^` markup:

| Token | Effect (in FormPaint) |
|-------|-----------------------|
| `^^TEXT^` | **accent/value** segment — TEXT in the value font (`form+0x794`), accent color (e.g. shortcut keys) |
| `^?TEXT^` | highlighted state label; uses active/hover knob color + icon glyph 0xf015 |
| `^.X` | level/progress bar; `X`='a'..'u' → 0.0..1.0; draws a gradient meter |
| `^_TEXT^` | secondary segment captured (right/dim text) |
| `` ^`TEXT^ `` | text-override segment (replaces the main display text) |
| `^#` | marker, consumed (no glyph) |
| `^<c>`, c∈'b'..'m' (incl. `^d`) | inline ICON glyph `U+F00A..U+F015` (= c-'b'+0xF00A; `^d`→0xF00C) in icon font `form+0x7a4`, state-colored |
| `^a` | consumed; glyph 0xF009 NOT drawn (acts as spacer/no-op) |
| `%s %d %%` | printf args substituted before the string is parsed |

`StripMarkup` flags: `^.` sets bit 2 (has level), `^_`/`` ^` `` set bit 1 (has secondary),
`^^`/`^?` consume-only; everything else `^X` strips 2 chars.

Real examples (defined strings):
- `"|^^B^Paint"` → tooltip empty; status: accent **B** + "Paint".
- `"|^d^^F2 / F3^Select color (Shift + right click to copy color 2)"` → icon + accent
  "F2 / F3" + plain text.
- `"^^Shift+Click^Sample preview"` → accent "Shift+Click" + "Sample preview".
- `"^^%d%%^Point %d to %d tension"` → accent value + description.
- `"|^b^a^^(double-click) ^Target mixer track"` → icon(s) + accent + text.

---

## 3. Drag cursors

### WP cursor primitive
- **`FLwp_SetCursor(control, cursorId)` @ `0x5d0e10`** — writes the WP cursor id at
  **`control+0xd8`** (a `short`) and, on change, posts WP msg **`0xb00f`**
  (cursor-changed) via `FLui_Input_PerformControlMsg`. This is the WP set-cursor setter.
- Editors stage a tool cursor in **`form+0xa5c`** then push it down:
  `FLui_Editor_UpdateToolCursor @ 0xd4cf00` maps the current tool (`form+0xa00`) to a
  cursor id and calls `FLwp_SetCursor` on the panel controls.

### id → HCURSOR
- **`FLui_Drag_ResolveCursor(mgr, id)` @ `0x83dae0`** — linked-list lookup (`mgr+0x14c`)
  by id, returns the HCURSOR (or default arrow `mgr+0x154`); **id == -1 → no cursor**.
  Cursor manager singleton = **`PTR_DAT_014ac158`**.
- **`FLui_Drag_LoadCursors(mgr)` @ `0x83d470`** — `mgr+0x154` = `LoadCursorW(IDC_ARROW)`;
  loads **21 cursors at negative ids -22..-2** (32×32, `LoadImageW`) from resource-name
  table **`DAT_012eed90`** (`MAKEINTRESOURCE` values); ids -21,-7,-6 (and the 0x7ffa..0x7fff
  range) come from FL's own module (`DAT_014b3298`), the rest from system. Positive ids
  (FL editor tool cursors) are registered by sibling loaders `0x83d500`/`0x83dbf0`.

Negative cursor-id map (id : Win32 IDC / FL-custom):
`-22`=SIZEALL · **`-21`=HAND** (editor pan/drag, set as `form+0xa5c`=0xffffffeb) · `-20`=HELP ·
`-19`=APPSTARTING · **`-18`=NO** (no-drop) · `-17..-12`=FL-custom (0x7ffa..0x7fff) ·
`-11`=WAIT · `-10`=UPARROW · `-9`=SIZEWE · `-8`=SIZENWSE · `-7`=SIZENS · `-6`=SIZENESW ·
`-5`=SIZEALL · `-4`=IBEAM · `-3`=CROSS · `-2`=ARROW.

### Cursor DURING a drag
- **`FLui_Drag_UpdateCursor` @ `0x5ccdf0`** — runs on each mouse-move while a drag op
  (`DAT_014b9ce0`) is active: hit-tests target, calls the drag-op **`vtbl+8`**
  (DragOver/GetCursor) → returns a `short` cursor id, then `SetCursor(FLui_Drag_ResolveCursor(
  PTR_DAT_014ac158, id))`. **Valid drop** → shows the `ImageList` drag image
  (`ImageList_DragEnter`/`Move` via `FUN_005e2330`/`005e24a0`); **no-drop / move-only** →
  hides the image (`FUN_005e2540`) and just sets the op-returned cursor. So valid-vs-invalid
  cursor selection lives in each drag op's `vtbl+8`.
- **`FLui_Drag_BeginImage` @ `0x5e2230`** — `ImageList_BeginDrag` wrapper (internal drags);
  stores hotspot `+0xf4/+0xf8`, sets dragging flag `+0xea`.
- **`FLui_Drag_End` @ `0x5cd610`** — drag finish/drop; on cancel restores
  `SetCursor(DAT_014b9d00)` (saved pre-drag cursor) or hides the drag image; fires the
  drop (vtbl[0]) + drop msgs (0xb03a). OLE drags use `DoDragDrop@0x468be0` (unchanged here).

---

## REUSE notes (for our own skinned UI)
- **Render our own skinned popup menu:** mirror `FLui_Menu_WindowPaint`/`PaintRow`/
  `DrawRowContent`/`DrawRowGlyph` — one rounded bg, clip, per-row rect array, per-state
  color set (normal/selected/disabled/header), columns for icon|label|shortcut|arrow,
  glyph font for check/radio/submenu, separator = thin fill. The whole skin recipe is the
  metrics block (`win+0x808`) field map above. To spawn FL's real menu instead, call
  `FLmenu_ShowPopup(engine,x,y,ctrl,root)@0x70ab80`.
- **Surface a message in FL's hint bar:** call `FLui_SetStatusHintAndRefresh@0x10ec870`
  with a Delphi UStr; use the `tooltip|status` split and `^^…^` for accent text,
  `^b..^m`/`^d` for inline icons, `^.X` for a level meter.
- **Set a drag cursor:** put a cursor id in `control+0xd8` via `FLwp_SetCursor@0x5d0e10`
  (negative = FL/system cursor table, -21=hand, -18=no-drop, -1=none); during a custom
  drag, return the id from the drag-op `vtbl+8` and let `FLui_Drag_UpdateCursor` apply it.

## Open / lower-confidence
- Positive FL editor-tool cursor ids (draw/paint/slice/zoom etc.) are registered by
  `0x83d500`/`0x83dbf0` — not enumerated here (that's ui-05 editor-tool territory, not the
  drag gap).
- The exact `^?` / `` ^` `` / `^_` runtime use is inferred from FormPaint branch behavior;
  the common tokens (`|`, `^^`, `^d`, `^b..^m`, `%s/%d`) are confirmed against real strings.
- VMT slot names other than paint/ctor/dtor/default-size/SetBounds(+0x188)/ClientRect(+0xe8)
  were not individually verified (not needed for this gap).
