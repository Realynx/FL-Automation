# ui-04 — WP skinning / rendering / theming pipeline (Wave 1)

How FL paints its UI: the WP painter/canvas, the per-control & form paint path, the **`P_*` font
catalog**, color/blend math, the skin **descriptor** system (`forms.X:wpform`, `controls.…:role`), and the
theme system — plus the recipe to make OUR injected controls/windows paint with FL's native skin.

RESEARCH ONLY — static Ghidra on `FLEngine_x64.dll`, image base `0x400000`. Rebase at runtime:
`runtime = ghidra - 0x400000 + flEngineBase` (flEngineBase from `flprobe bridge info`). No source / live FL.
Builds on re/13 (WP controls), re/22{,-window-host} (TCustomWPForm host, chrome, `+0x49c`/`+0x304`/`+0x6e8`).

> **Big reframe vs the task brief:** the `P_*` resources (`P_TitleBar`, `P_Small`, `P_Normal`, …) are **NOT
> bitmap skin parts — they are the predefined FONT roles** (typeface + size + metrics). The skin-part loader
> `FUN_007dd6a0` loads the `P_TitleBar` *font* into the form's font object `+0x49c`. The actual bitmap/skin
> chrome is painted by the WP painter object `+0x304` over the VCL canvas. See §2 (paint) and §3 (fonts).

---

## TL;DR / verdict
- **Two cooperating objects do all WP drawing**, both hung off every WP widget (control *and* form — a form IS
  a control):
  1. **The painter / canvas** at **`widget+0x304`** = **`TQuickControlCanvasEx`** (VMT 0x7fc980) — a GDI
     software canvas (HDC + offscreen ARGB DIB) holding the GDI font/pen/brush + the shared render ctx ptr
     (`+0xb4`). Created by `FLui_Paint_CreateFormCanvas@0x7fde80`. FL paints into the DIB then blits. (§2)
  2. **The font object** at **`widget+0x49c`** (class `&PTR_FUN_006528e0`) — the typeface/size/color used for
     this widget's text. Set to a named role via `FLui_Skin_LoadNamedFont(widget+0x49c, "P_Normal")`.
- **The `P_*` catalog = the font catalog.** A global array `DAT_014bb1a8` of ~15 font defs, built once at
  startup by **`FLui_Skin_BuildFontCatalog@0x654670`** (lazy, via `FLui_Skin_EnsureFontsInit@0x658570` which
  also loads **`QuickFontCache.dll`** — FL's glyph cache). Each entry = {name `P_*`, TTF/OTF face, size,
  metrics, color}. Fonts live in `…\Artwork\Fonts` (`FLui_Skin_GetFontDir@0x654490`).
- **Color is plain 32-bit ARGB ints**, not palette indices, at the WP layer. The reusable primitive is
  **`FLui_Skin_BlendColor@0x626f90(argbA, argbB, t)`** (saturating per-channel lerp, `t` 0..256). Text color =
  `fontObj+0x78` (`-1` ⇒ inherit), set via **`FLui_Skin_SetFontColor@0x653c30`**.
- **Skin/behaviour selection is by DESCRIPTOR string:** forms store `"forms.<name>:wpform"` @ `form+0x6e8`;
  controls store `"controls.<…>;…:<role>"` @ `ctrl+0x328`. The `:<role>` suffix (`wpform`/`quickbutton`/
  `wheel`/`digiwheel`/`quickedit`/`titlebar`/`tnewcaption`/`toolbutton`/…) picks the skin part-set + behaviour.
  *(parser/role-resolution + theme palette + bitmap parts = §6, skin-descriptor/theme lane.)*
- **Reuse:** to look native, host inside a TCustomWPForm (re/22 — free skinned chrome) and (a) set each
  text-bearing widget's font role via `FLui_Skin_LoadNamedFont(widget+0x49c, "P_Normal")`, (b) take colors from
  the live theme and blend with `FLui_Skin_BlendColor`, (c) set the control descriptor `:role` so FL paints it
  with the matching skin parts. Full recipe §7.
- **No VMProtect** anywhere on the font/color/paint/descriptor path — all clean Delphi, fully decompiled.

---

## 2. The paint pipeline (painter / canvas object)  — *paint lane (complete)*

### 2.1 The painter = `TQuickControlCanvasEx` (a GDI software canvas)
Every WP widget owns a **canvas at `widget+0x304`** — class **`TQuickControlCanvasEx`** (VMT `0x7fc980`, unit
`QuickControls`; base `TQuickControlCanvas`). It wraps a Win32 **HDC + an offscreen ARGB DIB** — FL's UI is
**GDI-rendered into a DIB then blitted** (software, not GPU).
- **Create:** `FLui_Paint_CreateFormCanvas@0x7fde80(widget)` (TCustomWPForm) / `FLui_Paint_CreateWindowCanvas@
  0x804700` (other WP windows): frees old `+0x304`, ctor `FLui_Paint_CanvasExCtor@0x7fe750(&VMT,1)` (sets
  `canvas+0xb4 = *PTR_DAT_014a9930` = the shared skin ctx), then `FLui_Paint_CanvasExBind@0x7fe860(canvas,
  owner)` binds canvas→owner and copies the owner's **render-mode word** into `canvas+0xb0` (owner WP control
  +0x320 / WP form +0x490 / TCustomWPForm +0x6d2). **NB:** `+0xb0` is a render-mode/style flags word (bit
  `0x10`=double-buffered), **not** a UI scale (re/22's "scale" read was wrong); the real global UI scale is
  `*PTR_DAT_014a9878+0xc` (§3).
- **Canvas fields:** `+0x2c` CRITICAL_SECTION (canvas is thread-safe); **`+0x58` HDC** (set via
  `FLui_Paint_CanvasSetDC@0x590220`); `+0x64/+0x6c/+0x74` GDI **Font / Pen / Brush** sub-objects
  (`FLui_Paint_CanvasGetBrush@0x58e3d0`; text color @font+0x28, bk color @brush+…); `+0x84` default ROP
  `0xCC0020` (SRCCOPY); `+0xa0` active DIB buffer (set during WM_PAINT); `+0xa8` update/clip-region mgr;
  `+0xb4` the shared skin ctx ptr.

### 2.2 Shared render/skin context + the DRAW PRIMITIVES (the reuse calls)
- **Global skin/metrics ctx = `0x14bda18`** (`*PTR_DAT_014a9930`); accessor **`FLui_Paint_GetSkinCtx@0x7fdac0`**
  `= *(widget+0x304)+0xb4`. Fields: `+0x1c` border/spacing, `+0x20` base line/text metric, `+0x24` default bk
  color, `+0x28` default text color (also `+0x34` padding/inset, used by control font auto-fit `FUN_0077ef20`).
- **Each primitive is `(canvas, args…)`**, internally wrapped by Changing/RequiredState(mode: 1=font 2=pen
  4=brush)/Changed. The full set (renamed `FLui_Paint_*`):
  - **Fills/shapes:** `FillRect@0x58f530(canvas,RECT*)`, `FrameRect@0x58f620`, `Rectangle@0x58f9e0`,
    `RoundRect@0x58fa70`, `Ellipse@0x58f4b0`, `Arc/ArcTo/AngleArc/Chord/Pie` (0x58e990/ea40/eaf0/f0d0/f7b0),
    `FloodFill@0x58f590`, `DrawFocusRect@0x58f460`.
  - **Lines:** `MoveTo@0x58f760`, `LineTo@0x58f700`, `Polygon@0x58f860`, `Polyline@0x58f8c0`,
    `PolyBezier@0x58f920`, `PolyBezierTo@0x58f980`.
  - **Bitmap / skin-sprite blit:** **`DrawBitmap@0x58eb80(canvas, destRect*, bmpObj, srcRect*, frame)`** —
    StretchBlt with mask support (this is what blits theme bitmap parts).
  - **Glyph/icon:** `DrawGlyph@0x58f260(canvas,x,y,glyphObj)` / `DrawGlyphEx@0x58f360` (uses ctx colors);
    generic `DrawDrawable@0x58fb10`.
  - **Text:** `TextOut@0x58fbb0(canvas,x,y,UStr)`, `TextOutClipped@0x58fcc0(canvas,RECT*,x,y,UStr)`,
    `DrawTextRect@0x58fde0(canvas,RECT*,&UStr,fmtFlags)` (DrawTextExW, multiline/align),
    `TextExtent@0x58ff20(canvas,UStr)→SIZE`.
- DIB/blit infra used by WM_PAINT (not renamed — bitmap-parts territory): `FUN_006b8d20` create DIB,
  `FUN_006b54d0` DIB mem-HDC, `FUN_006a4df0`/`006a9de0` (alpha-)blit DIB→DC, `FUN_006b8330` set DIB origin.

### 2.3 Invalidate + WM_PAINT pipeline + the paint vtable slots
- **Invalidate:** control **`vtbl+0x178`** = invalidate-whole-control (`0x8016a0` — Layout fork) →
  `0x7fe620` → **`RedrawWindow(HWND@widget+0x45c, rgn, 0x41)`** ⇒ WM_PAINT. (`FLui_Paint_InvalidateChildRect@
  0x7fe500` = child-rect variant.) **WP host HWND = `widget+0x45c`** (distinct from the VCL TWinControl handle
  `+0x2b0`, re/14/22).
- **WM_PAINT = `FLui_Paint_WindowWMPaint@0x8038b0`:** `BeginPaint` → alloc offscreen DIB+memDC →
  `canvas+0xa0=buffer` → paint own surface via **`vtbl+0x228(self,hdc)`** → walk children (`widget+0x368`
  list), clip each, dispatch **WP WM_PAINT msg `0xf`** (`FUN_005d2870`, re/13) recursing nested canvases via
  **`FLui_Paint_PaintControlTree@0x7fdec0`** → each control's **`vtbl+0x1a0`** (paint-self) → blit DIB to
  screen → `EndPaint`.
- **⇒ Two paint slots:** **`vtbl+0x1a0`** = WP control "paint self into the current canvas/buffer";
  **`vtbl+0x228`** = window/form Paint (background + non-WP content).
- Font-driven auto-size: `FUN_0077adb0` (= `FLui_WP_RecalcContentWidth`, WP-core fork): width/height =
  `PTR_DAT_014abba0[widget+0x5c3]·(ctx+0x20)`; apply-font `FLui_Paint_ApplyControlFont@0x802650`,
  `FLui_Paint_ApplyFontAndRelayout@0x77ad70`.

### 2.4 Form chrome paint
TCustomWPForm's `vtbl+0x228` paints the form background; the **title-bar is a child control** built by
`FLui_Skin_BuildTitleBar@0x7dd6a0` (§4) and painted during the child WM_PAINT(0xf) walk — it draws the caption
with the **P_TitleBar font (`form+0x49c`)** + titlebar color (`form+0x508`) via the canvas `TextOut`. Published
`OnPaintCaption` hook @`0x7a157e` overrides caption painting.

---

## 3. The `P_*` FONT catalog (this lane — complete)

`FLui_Skin_BuildFontCatalog@0x654670` constructs every predefined font once and registers it in the global
catalog `DAT_014bb1a8` (Delphi dyn-array of font-obj ptrs; count at `DAT_014bb1a8-8`). Each font:
`f = FLui_Skin_CreateFont(&PTR_FUN_006528e0,1)` → set name `f+0x14="P_X"` → set face/size/metrics →
`FLui_Skin_RegisterFont(f)` (→ `FLui_Skin_CatalogAddFont`, add/replace by name in `DAT_014bb1a8`).

| `P_*` name | size (pt) | face (segoe build / opensans build) | notes |
|---|---|---|---|
| `P_SmallBold` | 9 | segoeui / OpenSans-Regular | weight via metric `+0x30=0.65` |
| `P_Small` | 9 | segoeui / OpenSans-Regular | |
| `P_Tiny` | 7 | segoeui / tahoma | `+0x3c=1.5`, `+0x2c=0.8` |
| `P_Tiny2` | 7 | (clone of `P_Tiny`) | `+0x2c=0.9` |
| `P_Normal` | 11 | segoeui / OpenSans-Regular | the default body font |
| `P_NormalLight` | 11 | segoeuisl (semilight) | hi-DPI default (scale ≥ 2.0) |
| `P_NormalBold` | 11 | segoeui / OpenSans-Regular | bold metric `+0x30=0.5` |
| `P_Large` | 13 | (clone of `P_Normal`, size 13) | |
| `P_LargeBold` | 13 | (clone of `P_NormalBold`, size 13) | |
| `P_TitleBar` | 11 | segoeui / OpenSans-Regular | window caption font (→ form+0x49c) |
| `P_LargeTitleBar` | 13 | (clone of `P_TitleBar`, size 13) | |
| `P_DigitWheel` | 18 | OpenSans-CondLight | the numeric wheel/value readout font |
| `P_Monospaced` | 11 | lucon (Lucida Console) / consola | code/value monospace |
| `P_ILGlyphs` | 16 | **ILGlyphsEx.ilfont** (icon font) | FL's vector UI icons; flags `+0x44\|=0x20`, `+0x40=2`, `+0x42=3` |
| `P_WebSymbols` | 16 | **WebSymbols-Regular.otf** (icon font) | web/symbol glyphs; flags `+0x44\|=0x120` |

Notes:
- Sizes are double constants in the builder (e.g. `0x4026…`=11.0, `0x402a…`=13.0, `0x4032…`=18.0,
  `0x401c…`=7.0, `0x4030…`=16.0), each clamped to `1..1000`.
- Two BUILD VARIANTS chosen at runtime by whether `segoeui.ttf` exists (`FUN_007f6c70` file-exists):
  Windows-Segoe build vs bundled-OpenSans build. Don't assume a face — read it off the font obj.
- `P_ILGlyphs` / `P_WebSymbols` are **icon fonts**: FL draws toolbar/window-button glyphs as text in these
  fonts. To reuse FL's native icons in our buttons, set the widget font to `P_ILGlyphs` and emit the glyph
  codepoint (codepoint catalog not yet dumped — TODO).

### Font object structure (class `&PTR_FUN_006528e0`)
| off | type | field |
|----:|------|-------|
| `+0x10` | u8 | dirty flag (set by `FLui_Skin_FontChanged`) |
| `+0x14` | UStr | **role/name** (`"P_Normal"` …) |
| `+0x1c` | UStr | **face / family** (resolved TTF/OTF file stem) |
| `+0x28` | f32 | **point size** |
| `+0x2c` | f32 | metric (≈ horizontal/condense factor) |
| `+0x30` | f32 | metric (weight/baseline-ish; bold uses 0.5) |
| `+0x38` | f32 | metric (e.g. 0.75) |
| `+0x3c` | f32 | metric (line factor; `P_Tiny`=1.5) |
| `+0x40` | i16 | style (`P_ILGlyphs`/`P_WebSymbols`=2) |
| `+0x42` | i16 | style2 (icon fonts =3) |
| `+0x44` | u32 | **render flags** (icon-font hints: `\|0x20`, `\|0x120`) |
| `+0x48` | f64 | mirror of a metric |
| `+0x68/+0x70` | TMethod | onChange {code,data} (fired by `FLui_Skin_FontChanged`) |
| `+0x78` | i32 | **ARGB color** (`-1` = inherit/auto) |

### Font API (this lane)
| fn | addr | sig | what |
|----|------|-----|------|
| `FLui_Skin_LoadNamedFont` | 0x653690 | (fontObj, char* name) | **resolve a `P_*` role** into fontObj (else treat name as a TTF path under `Artwork\Fonts`) — THE call to set a widget's font role |
| `FLui_Skin_FindFontByName` | 0x653580 | (UStr name)→int | catalog index of a `P_*` (or −1) |
| `FLui_Skin_CopyFontDef` | 0x6530e0 | (dst, srcEntry) | copy face/size/metrics (not color) + notify |
| `FLui_Skin_CloneFont` | 0x653150 | (dst, src) | copy color (+0x78) **and** def — full clone |
| `FLui_Skin_SetFontColor` | 0x653c30 | (fontObj, argb) | set `+0x78`, notify on change |
| `FLui_Skin_ApplyDefaultFontColor` | 0x653550 | (fontObj, ctx) | if color==−1, set from `ctx+0x28` (default text color) |
| `FLui_Skin_FontChanged` | 0x653ee0 | (fontObj) | mark dirty + fire onChange TMethod |
| `FLui_Skin_CreateFont` | 0x652cd0 | (&PTR_FUN_006528e0,1)→font | ctor a font obj |
| `FLui_Skin_RegisterFont` / `…CatalogAddFont` | 0x6542b0 / 0x6543a0 | (font) | add/replace in catalog by name |
| `FLui_Skin_BuildFontCatalog` | 0x654670 | () | build all `P_*` |
| `FLui_Skin_EnsureFontsInit` | 0x658570 | () | lazy one-time init (+ QuickFontCache.dll) |
| `FLui_Skin_FreeFontCatalog` | 0x658460 | () | release all catalog fonts |
| `FLui_Skin_GetFontDir` | 0x654490 | (&out)→UStr | locate `…\Artwork\Fonts` |

Globals: `DAT_014bb1a8` = font catalog array; `DAT_014bb1a0` = font search-dir list (idx −1 = count);
`PTR_DAT_014abba0` = font-size-class → row-height multiplier table; `*PTR_DAT_014a9930` = shared render ctx
(`+0x20` base text unit, `+0x28` default text color, `+0x34` padding); `*PTR_DAT_014a9878 +0xc` = global UI
scale. Per-widget: font obj `+0x49c`, font-size class `+0x5c3`, auto-fit secondary font `+0x5d0`.

---

## 4. Color / caption-color (this lane)
- **`FLui_Skin_BlendColor@0x626f90(argbA, argbB, t)`** — the core color primitive: per-channel saturating
  interpolation (SIMD), `t` in 0..256 (≈ `B + (A−B)·t/256`, clamped). Reuse this for hover/press/disabled tints.
- **Caption / title-bar color** `FLui_Skin_ApplyTitleBarColor@0x7dd1a0` → if `form+0x512==2` set font color
  directly from `form+0x508`; else `FLui_Skin_ComputeCaptionColor@0x7dd0a0(form, factor)`:
  - `colorBg  = BlendColor(form+0x500, form+0x504, factor·256)` → background (`FLwp_SetControlValue`);
  - `colorTxt = BlendColor(form+0x508, form+0x50c, factor·256)` → `SetFontColor(form+0x49c, colorTxt)`.
  ⇒ FL keeps **active/inactive color pairs** (`+0x500/+0x504` bg, `+0x508/+0x50c` caption) and blends by a
  focus/brightness factor. Same pattern is reusable for our own skinned caption.

### Title-bar build (chrome font path, this lane)
`FLui_Skin_BuildTitleBar@0x7dd6a0(form)` (a TCustomWPForm vtable method): base `FUN_00802650(form)` (which,
if `form+0xab==0`, applies the widget font via `FLui_Skin_ApplyDefaultFontColor(form+0x49c, form+0xb4)`), then
if `*(form+0x49c)+0x14==0` loads the **`P_TitleBar`** font into `form+0x49c`, then `ApplyTitleBarColor`, then
sets content container `+0x11c` padding and re-lays out. So the caption uses the `P_TitleBar`/`P_LargeTitleBar`
font + the blended caption color — automatic for any TCustomWPForm.

---

## 6. Skin descriptor + theme + bitmap parts  — *skin-descriptor/theme lane (complete)*

### 6.1 Descriptor system — how a control/form picks its skin
- **Format & storage.** A WP descriptor is a Delphi `UnicodeString` `"<path>;<fallback-path>:<role>"`. Forms
  store it at **`form+0x6e8`** (`"forms.<name>:wpform"`, 61 of them — `forms.main`, `forms.mixer`,
  `forms.channelrack`, `forms.browser`, `forms.pluginform`, `forms.options`, `forms.themeeditorform`, …);
  controls store it at **`ctrl+0x328`** (`"controls.button;…:quickbutton"`). Written via `Delphi_UStrAsg@0x4133f0`
  (re/13's `FUN_004133f0` = `System._UStrAsg`; heap-copies the const string, so our pokes persist).
- **`:role` → WP control class.** The substring after `:` is matched against the **WP class registry** (sorted
  UString table @ **`0x761c00`**: `quickbutton, quickwincontrol, slider, tcustomvectorpanel, tcustomwavscope,
  themewheel, thqwavscope, titlebar, tnewcaption, tnewmenu, toolbutton, tpaintboxselector, tquickbittable,
  tquickedit, …`) → selects the control CLASS (widget-core lane owns the registry; not renamed).
- **class name → skin element type → color roles.** The same names are **skin element TYPES** in
  **`Simple_ThemeElements.ini`**. `FLui_Skin_BuildElementCatalog@0x1033060` parses that ini; each element's
  name → a **type id** via `FLui_Skin_LookupElementTypeId@0x76b5a0` (binary search over a name→id table
  @`0x1487598`, stride 0xc, 40 entries; names @`0x1031e88`: `checkbox=13, color=23, digiwheel=10,
  focusbutton=15, gauge=8, label, quickbutton, slider, titlebar, …`). The type id dictates the **named color
  roles** the element exposes (added by `FLui_Skin_AddElementColorRole@0x10328c0`):
  - knob/wheel (5): `background, cappressedcolor, fillcolor, fillpressedcolor, gearcolor, railcolor, railringcolor`
  - slider (6): `fillcolor, fillpressedcolor, handlecolor, handlepressedcolor, railcolor, railringcolor`
  - button (0x13/0x14): `background, buttoncolor, buttonpressedcolor, textcolor, textpressedcolor`
  - checkbox (0xd): `background, textcolor, checkglosscolor, checkoncolor, checkoffcolor, outlinecolor`
  - grid/list (0x15/0x16): `gradientcolor, background, scrollercolor, textcolor, fixedcolor, selectedcolor`
  - icon/glyph (0x11): `glyphcolor, textcolor`; label/memo (7/9/10): `background, textcolor`; `color`
    (0x17) = a plain named palette swatch.
  ⇒ **descriptor → element type → role set**, each `(element, role)` → an ARGB via the color core (§6.2).

### 6.2 Theme / color system (the high-value reuse part)
Delphi units **`ThemeSupport`** + **`QuickThemeHandler`**. The live theme is a parsed XML tree (global
**`DAT_012e2980`**; `==0` ⇒ no theme / passthrough). Theme files: **`.flstheme`** (also `.xml`/`.zip`) in
**`Artwork\Themes\`** (folder id 0x413); key members `theme.inc`, `colors.inc`, `Simple_ThemeElements.ini`;
current = `__current.flstheme`; ini `General\Theme`→`ThemeFilename`/`LastThemeFilename`. Loose skins under
`Artwork\Skins\` (folder id 0x409). Magic `.FLSTHEME`.
- **Global theme object = `*(*0x14ABCA8 + 0x6c)`** (the app/engine controller's +0x6c; cf. re/22 +0x40=window
  mgr). `vtbl[0x30](file,1)` loads a `.flstheme`; `vtbl[0x20]/[0x28]` = select/serialize. (concrete vtable is
  dynamic — resolve live.)
- **Theme load:** `FLui_Skin_LoadTheme@0x1038600` (.xml → `FLui_Skin_ExportThemeXML@0x1038720` builder;
  .flstheme → theme obj). The XML schema: root `flstudio`/`themeversion`, a **`tweaks`** node (global
  Hue/Saturation/Lightness/Contrast/Text), then the named-color list.
- **★ Public color getter — USE THIS for native colors:**
  - **`FLui_Skin_GetThemeColor(defaultARGB, roleKey, flags)@0x76b6f0`** — splits `roleKey` (`"element.role"`)
    via `FLui_Skin_SplitColorKey@0x7687d0`, resolves it (`FLui_Skin_ResolveRoleColor@0x7677a0`), and if
    `flags!=0` applies the global recolor transform. **Controls call it with `flags=0xf`.**
    e.g. `argb = FLui_Skin_GetThemeColor(0xFF808080, L"quickbutton.background", 0xf)`.
  - `FLui_Skin_GetThemeColorRaw(default, roleKey)@0x76b630` — same lookup, **no** transform.
- **Resolution chain:** `FLui_Skin_ResolveColorValueStr@0x765100` walks the theme XML for the element's role
  (follows `class` inheritance, falls back to the global `colors` section by name) → a value string;
  `FLui_Skin_ParseColorSpec@0x766360` parses it → ARGB, supporting `$RRGGBB`, named refs (against
  `DAT_01580e00`, the named-color table) and `~`/`%` blend-and-percent operators.
- **★ Global recolor engine:** `FLui_Skin_ApplyColorTransform(argb, flags)@0x7636f0` — re-tints the whole UI
  from the theme tweaks. Flags: `1`=contrast, `2`=saturation, `4`=lightness, `8`=hue (`0x10`=negate), `0x40`=
  luminance-map into a 256-entry ramp (`PTR_DAT_014a73e0`, base/tint `PTR_DAT_014ab4a8`), `0x80`=dark-mode
  min-saturation (`PTR_DAT_014a9860`). Amounts: `DAT_014bd4f0`(hue)/`14bd4f4`(sat)/`14bd4f8`(contrast)/
  `14bd4fc`(light).
- **Global tweak knobs** (set from the Theme-settings wheels by `FLui_Skin_ReadTweakWheels@0x10316d0`):
  `PTR_DAT_014a8430`(Hue), `PTR_DAT_014abcc8`(Sat), `PTR_DAT_014a7e88`(Light), `PTR_DAT_014a85e8`(Contrast),
  `PTR_DAT_014a86b8`(Text). `FLui_Skin_ApplyContrast@0x7619a0` applies the Text tweak alone (used by browser).
- **Editor-side palette:** global list **`DAT_01580df8`** of color records (`+0x60` role name, `+0x58`
  full key, `+0x70` default spec, `+0x50` value, **`+0x88` = resolved ARGB**, `+0x8c` order). Built by
  `FLui_Skin_BuildElementCatalog`; append `FLui_Skin_PaletteAddRecord@0x1032840`; lookup-by-key
  `FLui_Skin_PaletteFindByKey@0x103a680` (record → ARGB @+0x88). Drives **`TThemeEditorForm`**
  (`forms.themeeditorform:wpform`@0x1030b70; RTTI methods `FormCreate@0x1030900`,
  `PalettePaintboxPaint@0x1036330`, `PartSelectClick@0x1036d40`, `ContrastWheelChange@0x103a550`,
  `SaveAsBtnClick@0x103ba10`). Menu entry: `TShortcutsModule.OptionsShowThemeSettingsActionExecute@0xe46180`.

### 6.3 Bitmap skin parts
RTTI confirms `QuickThemeHandler.TThemeBitmapData` + `TDictionary<string,TThemeBitmapData>` (name→bitmap
cache) and `ThemeSupport.TThemePaintInfo`/`TThemeGradient` + `TDictionary<string,TThemePaintInfo>`
(name→paint-info: gradient+colors, consumed by the painter — **paint lane**). Element type `themewheel` is the
image-backed wheel variant. The bitmap cache is built/keyed when the theme object loads; the concrete loader is
behind the dynamic theme-object vtable (not statically pinned — **weak spot**). NOTE: most FL theming is the
**color/gradient path (§6.2)**; bitmaps are a thin cache on top. ARGB byte order is Delphi `TColor`
(`0x00BBGGRR`), with the alpha/high byte handled separately by the painter.

---

## 7. Reuse recipe — make OUR UI paint with FL's native skin
1. **Host in a TCustomWPForm** (re/22 §5): create via `FLui_CreateFormFromClassRef` → free skinned chrome
   (border, `P_TitleBar` caption via `titlebar`+`tnewcaption`, `toolbutton` window buttons), content container
   `+0x11c`, HWND `+0x2b0`. Set the form skin via the descriptor `form+0x6e8 = "forms.<x>:wpform"`.
2. **Create controls the WP way** (re/13): set each control's descriptor `ctrl+0x328 = "controls.…:<role>"`
   so FL paints it with the matching native skin parts + behaviour (`:quickbutton`, `:wheel`, …).
3. **Text that matches FL:** each text-bearing widget has a font obj at `+0x49c`; call
   `FLui_Skin_LoadNamedFont(widget+0x49c, "P_Normal")` (or `P_Small`/`P_NormalBold`/`P_TitleBar`/`P_DigitWheel`/
   `P_Monospaced`). For DPI-correctness mirror FL: if `*PTR_DAT_014a9878+0xc >= 2.0` use the `…Light` variant.
   Leave color `−1` to inherit the theme text color, or set explicitly with `FLui_Skin_SetFontColor`.
4. **Native icons:** set a widget's font to `P_ILGlyphs` (or `P_WebSymbols`) and emit the icon glyph codepoint
   to draw FL's own vector UI icons (codepoint table = TODO).
5. **Colors / states:** get native theme colors with
   `argb = FLui_Skin_GetThemeColor(fallbackARGB, L"<elem>.<role>", 0xf)@0x76b6f0` (e.g.
   `quickbutton.background`, `slider.fillcolor`, `titlebar.background`, `label.textcolor` — element types &
   roles per §6.1; main thread). It already applies the user's Hue/Sat/Light/Contrast so it tracks the live
   theme. For an ARGB you computed yourself, run `FLui_Skin_ApplyColorTransform(argb, 0xf)@0x7636f0` so it
   re-tints with the theme. Derive hover/press/disabled tints with `FLui_Skin_BlendColor(base, accent, t)`;
   keep active/inactive caption color pairs like FL (§4). ARGB byte order is Delphi `TColor` (`0x00BBGGRR`).
6. **Custom drawing** (instead of stock controls): implement a WP control's `vtbl+0x1a0` (paint-self) +
   `vtbl+0x178` (invalidate → RedrawWindow → folds you into the DIB). Inside paint, use `canvas =
   *(widget+0x304)` and the `FLui_Paint_*` primitives — `FillRect@0x58f530`, `RoundRect@0x58fa70`,
   `DrawBitmap@0x58eb80` (skin sprites), `TextOut@0x58fbb0`/`DrawTextRect@0x58fde0` — pulling default
   colors/metrics from `FLui_Paint_GetSkinCtx@0x7fdac0` (`ctx+0x24` bk, `+0x28` text, `+0x20` base metric).
   These are the exact calls FL uses ⇒ pixel-identical output.

---

## 8. Ghidra renames / tags / bookmarks (this pass)
All tagged **`UI_skin`**; bookmarks category **`UI_skin`** (font/color lane) — paint & theme forks use
`UI_skin_paint` / `UI_skin_theme`.

**Font/color lane (this agent):**
| new name | addr | old |
|----------|------|-----|
| `FLui_Skin_BuildFontCatalog` | 0x654670 | FUN_00654670 |
| `FLui_Skin_CreateFont` | 0x652cd0 | FUN_00652cd0 |
| `FLui_Skin_RegisterFont` | 0x6542b0 | FUN_006542b0 |
| `FLui_Skin_CatalogAddFont` | 0x6543a0 | FUN_006543a0 |
| `FLui_Skin_LoadNamedFont` | 0x653690 | FUN_00653690 |
| `FLui_Skin_FindFontByName` | 0x653580 | FUN_00653580 |
| `FLui_Skin_CopyFontDef` | 0x6530e0 | FUN_006530e0 |
| `FLui_Skin_CloneFont` | 0x653150 | FUN_00653150 |
| `FLui_Skin_SetFontColor` | 0x653c30 | FUN_00653c30 |
| `FLui_Skin_ApplyDefaultFontColor` | 0x653550 | FUN_00653550 |
| `FLui_Skin_FontChanged` | 0x653ee0 | FUN_00653ee0 |
| `FLui_Skin_EnsureFontsInit` | 0x658570 | FUN_00658570 |
| `FLui_Skin_FreeFontCatalog` | 0x658460 | FUN_00658460 |
| `FLui_Skin_GetFontDir` | 0x654490 | FUN_00654490 |
| `FLui_Skin_BlendColor` | 0x626f90 | FUN_00626f90 |
| `FLui_Skin_BuildTitleBar` | 0x7dd6a0 | FUN_007dd6a0 |
| `FLui_Skin_ApplyTitleBarColor` | 0x7dd1a0 | FUN_007dd1a0 |
| `FLui_Skin_ComputeCaptionColor` | 0x7dd0a0 | FUN_007dd0a0 |

**§8b — Skin-descriptor / theme / color lane** (tagged `UI_skin`; bookmarks category `UI_skin_theme`):
| new name | addr | role |
|----------|------|------|
| `FLui_Skin_GetThemeColor` | 0x76b6f0 | **(default, "elem.role", flags=0xf) → themed ARGB** ★ |
| `FLui_Skin_GetThemeColorRaw` | 0x76b630 | same, no transform |
| `FLui_Skin_ResolveRoleColor` | 0x7677a0 | role → value (class inheritance) |
| `FLui_Skin_ResolveColorValueStr` | 0x765100 | walk theme XML → value string |
| `FLui_Skin_ParseColorSpec` | 0x766360 | value string → ARGB ($RRGGBB/named/`~`/`%`) |
| `FLui_Skin_ApplyColorTransform` | 0x7636f0 | **global recolor (hue/sat/light/contrast)** ★ |
| `FLui_Skin_ApplyContrast` | 0x7619a0 | apply Text tweak to ARGB |
| `FLui_Skin_SplitColorKey` | 0x7687d0 | split `"elem.role"` |
| `FLui_Skin_LookupElementTypeId` | 0x76b5a0 | element name → type id |
| `FLui_Skin_LoadTheme` | 0x1038600 | load `.flstheme`/`.xml` |
| `FLui_Skin_ExportThemeXML` | 0x1038720 | serialize theme XML (schema) |
| `FLui_Skin_BuildElementCatalog` | 0x1033060 | parse `Simple_ThemeElements.ini` → palette |
| `FLui_Skin_AddElementColorRole` | 0x10328c0 | add role to an element |
| `FLui_Skin_PaletteAddRecord` | 0x1032840 | append palette record |
| `FLui_Skin_PaletteFindByKey` | 0x103a680 | palette record by key (ARGB @+0x88) |
| `FLui_Skin_ReadTweakWheels` | 0x10316d0 | read Theme-settings wheels → tweak globals |

**§8a — Paint-pipeline lane** (tagged `UI_skin`; bookmarks category `UI_skin_paint`): 45 funcs renamed
`FLui_Paint_*`. Canvas object/pipeline: `CreateFormCanvas@0x7fde80`, `CreateWindowCanvas@0x804700`,
`CanvasExCtor@0x7fe750`, `CanvasExBind@0x7fe860`, `GetSkinCtx@0x7fdac0`, `WindowWMPaint@0x8038b0`,
`PaintControlTree@0x7fdec0`, `InvalidateChildRect@0x7fe500`, `IsCanvasBuffered@0x7ffab0`,
`WindowHasUpdateRgn@0x7fef90`, `ApplyControlFont@0x802650`, `ApplyFontAndRelayout@0x77ad70`; canvas/state
`CanvasCtor@0x58e790`, `CanvasChanging@0x58e5f0`, `CanvasChanged@0x58e5d0`, `CanvasRequiredState@0x590280`,
`CanvasDeselectGDI@0x590190`, `CanvasSetDC@0x590220`, `CanvasGetBrush@0x58e3d0`; **draw primitives** `FillRect
@0x58f530`, `FrameRect@0x58f620`, `Rectangle@0x58f9e0`, `RoundRect@0x58fa70`, `Ellipse@0x58f4b0`,
`Arc@0x58e990`, `ArcTo@0x58ea40`, `AngleArc@0x58eaf0`, `Chord@0x58f0d0`, `Pie@0x58f7b0`, `FloodFill@0x58f590`,
`DrawFocusRect@0x58f460`, `MoveTo@0x58f760`, `LineTo@0x58f700`, `Polygon@0x58f860`, `Polyline@0x58f8c0`,
`PolyBezier@0x58f920`, `PolyBezierTo@0x58f980`, `DrawBitmap@0x58eb80`, `DrawGlyph@0x58f260`,
`DrawGlyphEx@0x58f360`, `DrawDrawable@0x58fb10`, `TextOut@0x58fbb0`, `TextOutClipped@0x58fcc0`,
`DrawTextRect@0x58fde0`, `TextExtent@0x58ff20`. Bookmarks: 0x8038b0, 0x7fc980 (VMT), 0x7fde80, 0x7fdac0,
0x58f530. Canvas VMT `0x7fc980` = `TQuickControlCanvasEx`.

> **Cross-lane boundary notes** (renamed by sibling forks, referenced here): `0x77adb0` =
> `FLui_WP_RecalcContentWidth` (WP-core), `0x7fee50` = `FLui_Layout_ResetHostPaintCache`, `0x7fe620` =
> `FLui_Layout_RedrawChildRegion`, `0x8016a0` = `FLui_Layout_ArrangeChildren` (= the `vtbl+0x178` invalidate).

---

## 9. Confidence + VMProtect
- **HIGH:** `P_*` are fonts; the font catalog/object/API; `BlendColor`; the per-widget font obj `+0x49c` +
  `LoadNamedFont` role-set; painter obj `+0x304` + shared ctx `*PTR_DAT_014a9930` + UI-scale `*PTR_DAT_014a9878`;
  the caption active/inactive color-pair blend; descriptor format/offsets (`form+0x6e8`, `ctrl+0x328`);
  element-type→role-set table; the color core (`GetThemeColor`→`ResolveColorValueStr`→`ParseColorSpec`→
  `ApplyColorTransform`) + tweak/transform globals; theme files/folders; global theme obj `*(*0x14ABCA8+0x6c)`;
  theme XML tree `DAT_012e2980`; palette `DAT_01580df8`; `TThemeEditorForm`.
- **HIGH (paint):** `TQuickControlCanvasEx` class/VMT (`0x7fc980`); the full `FLui_Paint_*` primitive set +
  signatures; the WM_PAINT→DIB→blit pipeline (`0x8038b0`); invalidate→`RedrawWindow(HWND@widget+0x45c)`; the
  `vtbl+0x1a0` (control paint-self) / `vtbl+0x228` (form paint) slots; WP msg `0xf` dispatch; shared ctx
  `0x14bda18`.
- **MED:** theme-object concrete vtable slots (dynamic — resolve live); named-color table `DAT_01580e00`;
  exact Pen/Brush/Font sub-object offset assignment among canvas `+0x64/+0x6c/+0x74`; canvas `+0xb0`
  flags-vs-scale interpretation (now read as render-mode flags).
- **LOW / open / TODO:** the `QuickThemeHandler.TThemeBitmapData` concrete loader (behind the dynamic
  theme-object vtable); the `P_ILGlyphs`/`P_WebSymbols` icon-glyph codepoint table.
- **No VMProtect / obfuscation** on any function in this whole area (font/color/paint/descriptor/theme) —
  all clean Delphi + GDI, fully decompiled.
