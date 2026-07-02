# 22 — FL's shared WINDOW-HOST container (task #22)

Goal: identify the common object/frame that gives EVERY FL window (Channel Rack, Mixer, Piano Roll,
Playlist, Browser, plugin windows, Settings/Project dialogs) the same border / title-bar / chrome, so a
sibling can host OUR own window (the FL Agent chat) inside one and have it look native.

RESEARCH ONLY — static Ghidra on `FLEngine_x64.dll`, image base `0x400000`. Rebase at runtime:
`runtime = ghidra - 0x400000 + flEngineBase` (get flEngineBase from `flprobe bridge info`). No source/live-FL
touched. A few helper bookmarks added in Ghidra (category "WindowHost"); no renames (siblings share the instance).

## TL;DR / verdict (HIGH confidence)
- **FL windows ARE Delphi VCL `TForm`s, sub-classed into a skinned "WP" frame.** The class chain (from the
  RTTI dump `re/generated/fl-classes.txt`, cross-checked by decompile) is:
  `TObject → … → TCustomForm (0x826f20) → TForm (classRef 0x828d20) → `**`TCustomWPForm (0x7de900)`**` →
  TUnpaintedWPForm (0x7df8d8) → TWPForm (0x7dfdb0) → TChildWPForm (0x7e0298) → `*concrete windows*.
- **The shared window-host (the thing that draws the common chrome) = `TCustomWPForm`** (VMT ghidra
  `0x7de900`, instance size `0x708`). `TForm`/`TCustomForm` above it are *stock VCL* (size `0x678`, NO skin);
  **TCustomWPForm is exactly where FL adds the skinned title-bar/border/buttons + the content area.** Every
  `forms.X:wpform` window is a TCustomWPForm descendant.
- The **form factory** `FLui_CreateFormCore@0x841EF0` enforces "must descend from **`TForm`**" via the
  is-a check against **`&PTR_FUN_00828d20`** (= the runtime classRef of VCL `TForm`).
- **`+0x2b0` is the Win32 HWND of the host, generally** — it sits inside `TCustomForm`'s `0x678` size, so it
  is the inherited VCL `TWinControl` handle field shared by ALL forms (confirms & generalizes re/14's
  "browser HWND @ form+0x2b0"). Standard `ShowWindow`/`SetWindowPos`/`DestroyWindow` work on it.
- No VMProtect / obfuscation on any of the form/chrome path — clean Delphi VCL + the WP layer. All functions
  below decompiled cleanly.

---

## 1. The host class + vtables + ctor

### 1.1 Class hierarchy (VMT addresses are Ghidra/image-base 0x400000)
| Class | VMT (fl-classes "RVA") | runtime classRef | size | role |
|-------|------|------|------|------|
| `TCustomForm` | `0x826f20` | `0x826f38` | `0x678` | stock VCL form base (window, HWND, bounds, caption) |
| **`TForm`** | `0x828d08` | **`0x828d20`** | `0x678` | stock VCL `TForm`; **the factory's "is-a" gate** (`&PTR_FUN_00828d20`) |
| **`TCustomWPForm`** | **`0x7de900`** | `0x7de918` | `0x708` | **the shared SKINNED host** (adds chrome + content area + skin descriptor) |
| `TUnpaintedWPForm` | `0x7df8d8` | `0x7df8f0` | `0x718` | WP form variant (no auto-paint) |
| `TWPForm` | `0x7dfdb0` | `0x7dfdc8` | `0x748` | the usual concrete WP form base |
| `TChildWPForm` | `0x7e0298` | `0x7e02b0` | `0x748` | dockable/child WP form base |

Note on the `+0x18`: the RTTI tool listed each VMT at its `vmtSelfPtr` slot; the **runtime classRef** (the
value used as the "class", and what the factory passes around) = that + `0x18`. So `TForm`'s classRef is
`0x828d08 + 0x18 = 0x828d20` — i.e. `&PTR_FUN_00828d20` IS `TForm`. (Verified: `*(0x826e70) → TCustomForm`
selfptr; `TForm.parent → TCustomForm`.)

The simple modal dialogs (`TMessageForm` 0x64a388, `TInputQueryForm` 0x64c4e0, `TZipDialogBox` 0x8d4f60)
derive **directly from TForm** (parent slot `0x828c58 → 0x828d20`) — they are *NOT* skinned WP forms; they're
plain VCL. So "every FL window shares the same chrome" is specifically true of the **TCustomWPForm subtree**,
not the bare-VCL dialogs.

### 1.2 Construction path (what builds a host)
```
FLui_CreateFormFromClassRef@0x10C2AA0(classRef, &slot)
  → FLui_CreateFormCore@0x841EF0(formMgr=*0x14AA6E8, classRef, &slot)
       inst = (**(code**)(classRef - 0x30))(classRef)      // Delphi NewInstance  = FUN_0040f960
                                                            //   allocs *(int*)(classRef-0x80) bytes
       *slot = inst
       (**(code**)(*inst + 0x78))(inst, 0xFF, formMgr)      // the form INIT (vtbl[+0x78])
       // if this is the first/main form: set Application.MainForm, OR WS_EX_APPWINDOW onto inst+0x2b0,
       //   then FLwp_FormSetAppWindowVisible@0x82FBB0(inst+0x2b0, vis, vis)
       // gate: FUN_00410010(classRef, &PTR_FUN_00828d20)   // "descends from TForm?"
```
- **Metaclass NewInstance** `FUN_0040f960`: `inst = alloc(*(int*)(classRef-0x80)); FUN_0040fb40(classRef,inst)`
  — instance size lives at **classRef-0x80**.
- **The form INIT (vtbl[+0x78])** for TCustomWPForm = **`FUN_007e3d60`** (= `TCustomWPForm.Create`). It:
  - inits the WP fields (see §3): `+0x6b4/+0x6b8 = 0x2000` (default w/h), `+0x6bc/+0x6c0 = 1`, `+0x6d0 = 0x801`,
    `+0x6d2 = 0x200`, `+0x6a9 = 1`, `+0x690..+0x69c = 0` (a rect), `+0x6fc = 0xf`, `+0x640 = 8`;
  - calls **`FUN_008323d0`** (= `TCustomForm.Create`, the VCL ctor: registers the form, DFM/MainForm logic,
    sets `+0x64c` from the "is-main-form" global `DAT_014bdc90+0x179`, ORs component flag `0x400000` @+0x14;
    its inner `FUN_00831460` is the deeper `TWinControl`/`TScrollingWinControl` ctor that **creates the HWND**);
  - calls **`FUN_007fde80`** → creates the WP skin/painter helper at **`+0x304`**
    (`FUN_007fe750(&PTR_FUN_007fc980, 1)`), then `*(+0x304)+0xb0 = +0x6d2`;
  - sets the content container (`+0x11c`) padding `+0x20/+0x21 = 0x10`.
- **is-a checks** (custom, parent link at classRef-0x78): `FUN_00410010(cls, target)` walks
  `cls = **(cls-0x78)`; `FUN_0040fe90(inst, target)` = `FUN_00410010(*inst, target)`.

---

## 2. The chrome (border / title-bar / caption / window buttons)

FL does NOT use the OS non-client frame for the visible chrome — it paints its own **skinned** title-bar from
**WP child controls**, so anything hosted in a TCustomWPForm inherits that look for free.

### 2.1 WP chrome control types (string table @ `0x761d00`, UTF-16 Delphi UStrings)
| addr | type name | role |
|------|-----------|------|
| `0x761d24` | `titlebar` | the title-bar container control |
| `0x761d44` | `tnewcaption` | the **caption / title-text** control (renders the window title) |
| `0x761d68` | `tnewmenu` | the menu bar (`NewMainMenu`, re/16 — on TToolbarForm) |
| `0x761d84` | `toolbutton` | title-bar buttons (close / min / dock) |
(These are matched by string-compare during descriptor parsing → no direct code xref. Same table holds
`tquickedit` @0x761e70, `tquickbtn`, etc. used in re/13/14.)

### 2.2 Title-bar build / paint
- **`FUN_007dd6a0`** (a TCustomWPForm vtable method = "build title bar"): loads the skin part
  **`"P_TitleBar"`** into the form's **skin/font object @ `+0x49c`** (`FUN_00653690(*(form+0x49c),"P_TitleBar")`),
  then `FUN_007dd1a0(form)` arranges/colours it and lays out via the content container `+0x11c`.
- **`FUN_007dd1a0`** (title-bar colour/state): branches on `*(u8*)(form+0x512)`; uses titlebar colour
  `*(u32*)(form+0x508)` and the skin obj `+0x49c`.
- Skin parts: **`P_TitleBar`** (@0x6582ec / 0x7dd714 / 0xcebeb4 / 0x1050c00), **`P_LargeTitleBar`** (@0x658304).
- Published-property hints on the WP form RTTI: `CaptionSize` (@0x7def2b), `ShowCaption` (@0x807087),
  `OnPaintCaption` (@0x7a157e) — i.e. caption height + visibility + a paint hook are all form-level.
- Plugin windows specifically: `forms.pluginform.newcaption.keyboardfocusbtn.color2` (@0xe8a4e4) → a plugin
  host window uses the same `tnewcaption` title bar.

**What we inherit by hosting in a TCustomWPForm:** the skinned border, the `titlebar`+`tnewcaption` caption,
the `toolbutton` close/min/dock buttons, drag-move/resize, dock behaviour, and the FL skin — all automatic.

---

## 3. Field layout on the host object (offsets from the form instance)
Confidence: HIGH = decompile + an independent source (re/13/14 live); MED = single decompile; LOW = single
init write, meaning inferred. All within the TCustomWPForm `0x708` unless noted.

| offset | type | field | conf | source |
|------:|------|-------|------|--------|
| `+0x11c` | ptr | **content / layout container child** — parent target for child controls (`(*(form+0x11c))->vtbl[0x10]`); padding `+0x20/+0x21` | HIGH | re/13 + init 0x7e3d60 + 0x7dd6a0 |
| `+0x14` | u32 | VCL component flags (ctor ORs `0x400000`) | MED | 0x8323d0 |
| `+0x34` | u16 | form-style flags (ctor tests `&0x10`) | MED | 0x8323d0 |
| **`+0x2b0`** | HWND | **Win32 window handle (VCL TWinControl) — SHARED by ALL forms** | HIGH | re/14 live + within TCustomForm 0x678 |
| `+0x304` | ptr | WP **skin/painter helper** object (class `&PTR_FUN_007fc980`) | MED | 0x7fde80 + init |
| `+0x49c` | ptr | **skin / font object** (`P_TitleBar` loaded here; also the menu-bar font, re/16) | HIGH | 0x7dd6a0 + re/16 |
| `+0x508` | u32 | title-bar colour | MED | 0x7dd1a0 |
| `+0x512` | u8 | title-bar mode/state (`==2` = custom path) | MED | 0x7dd1a0 |
| `+0x64c` | u8 | **"is main form" flag** (from `DAT_014bdc90+0x179`) | HIGH | 0x8323d0 / CreateFormCore |
| `+0x659` | u8 | flag → if set, ctor calls `vtbl[+0x290]` | LOW | 0x8323d0 |
| `+0x66c` | u8 | bit-flags (bit0 toggled during ctor) | LOW | 0x8323d0 |
| `+0x690..+0x69c` | 4×i32 | a **rect (x,y,w,h)** (init 0) — saved/restore window bounds | MED | init 0x7e3d60 |
| `+0x6a9` | u8 | =1 (init) | LOW | init |
| `+0x6b4 / +0x6b8` | u32 | default/max size `0x2000` each | MED | init |
| `+0x6d0` | u16 | flags = `0x801` | MED | init |
| `+0x6d2` | u32 | = `0x200` (mirrored to skin helper `+0x304`+0xb0) | MED | init |
| **`+0x6e8`** | UStr | **WP skin DESCRIPTOR** `"forms.<name>:wpform"` (selects the skin/chrome) | HIGH | re/13/14 |
| `+0x6fc` | u8 | = `0xf` (init) | LOW | init |

### Caption text, dock/float, bounds — specifics
- **Caption (title text):** rendered by the `tnewcaption` child control; the *source* string is the VCL
  `TCustomForm` caption (`TControl.Text` / `FCaption`). The exact UStr field offset on the instance was **not
  pinned statically** (no direct setter on the chrome path) — the one needs-live item: set a known title and
  scan the instance for that UStr, or read the `tnewcaption` control's own text model. `+0x6e8` is the *skin*
  descriptor, NOT the caption.
- **Dockable / floating:** this is VCL-based — `TChildWPForm` is the dockable base, and FL has sibling
  dock classes `TCustomDockForm` (0x82ad58) and `TDragDockObjectForm` (0x90f570). The float/dock/MDI state is
  the VCL `FormStyle`/`HostDockSite` machinery (see `TFruityLoopsMainForm.SB{Left,Bottom}DockSiteGetSiteInfo`,
  which reference `&PTR_FUN_00828d20`); the `+0x34` flags participate. (MED — not a single byte flag.)
- **Window bounds (x/y/w/h):** the live window rect is the HWND rect (VCL `Left/Top/Width/Height`); the
  `+0x690` rect is a saved/restore copy. NOTE: the `+0x90/+0x94/+0x98/+0x9c` rect from re/13 is for **WP
  controls**, not the form host — don't conflate.

---

## 4. Concrete windows confirmed on this host (all TCustomWPForm descendants)
From `fl-classes.txt` (parent slot in parens) + the `:wpform` string sweep:
- **Browser:** `TSampleListForm` (0xf8a4f0, `forms.browser:wpform`, re/14 — HWND@+0x2b0 confirmed live) and
  `TBrowserForm` (0x9e0320). (parent `0x7e06b8`)
- **Channel Rack / Step Sequencer:** `TStepSeqForm` (0xf41a00, `forms.channelrack:wpform`,
  `controls.forms;forms.channelrack:wpform`@0xf4614c). (parent `0x7e0c30`)
- **Toolbar / top menu:** `TToolbarForm` (0xcb3458, `forms.toolbarform:wpform`, re/16). (parent `0x7e06b8`)
- **Main window:** `TFruityLoopsMainForm` (0x1060840, 532 published methods). (parent `0x7e06b8`)
- **Plugin window / list:** `TPluginListForm` (0xfe3488); plugin GUI host uses `forms.pluginform.newcaption.*`.
- **Many tool dialogs (all `:wpform` → same host):** `forms.nameeditform`, `forms.paletteeditorform`,
  `forms.msgform`, `forms.renderform`/`wavrenderform`/`dwprenderform`, `forms.pr*` (Piano-Roll tools:
  arp/strum/flam/legato/quantize/chord/randomize/flip/…), `forms.pl*` (Playlist: `plclippropform`,
  `plmergearrangementform`), `forms.eventeditform`, `forms.newprojform`, `forms.pythonform`,
  `forms.shortcutform`, `forms.cloudaccountsform`, `forms.pluginmonitorform`, etc. (65 `:wpform` strings).
- **Mixer / Piano-Roll / Playlist MAIN editors:** they are `:wpform` windows too (so same host); their exact
  class names weren't matched by the name grep (likely unit-prefixed, e.g. score/arrangement units) — if a
  sibling needs the precise VMT, grep `fl-classes.txt` for the unit names or capture the live form pointer.

---

## 5. How this feeds "host OUR window" (for the sibling's plan)
- **Cleanest "native look" = create a TCustomWPForm-descendant via the factory** (`FLui_CreateFormFromClassRef`)
  — you get the skinned title-bar (`titlebar`+`tnewcaption`+`P_TitleBar`), `toolbutton` close/dock buttons,
  the HWND@+0x2b0, and the content container @+0x11c for free. Caveat from re/13/14: **there is NO blank-form
  classRef** — you must repurpose an existing simple form class (e.g. `nameeditform`/`paletteeditorform`/the
  picker `&PTR_FUN_00dae368`) or author a Delphi WP class (heavy). Set the skin via the descriptor `+0x6e8`.
- Drop your content by parenting WP controls into the form's content container `+0x11c` (the re/13/14 Stage-B1
  recipe: `FUN_0074c400` create → `vtbl[0x138]` SetParent → `vtbl[0x188]` SetBounds → `FUN_005ceef0` realize →
  `FUN_0077adb0` render). OR drop standard Win32 children on the HWND@+0x2b0 (re/14 v0 fallback; loses skin).
- The chat-tab work (re/14 Stage B2–C2d) already hosts inside the **browser content panel** rather than a new
  host form; this doc is the alternative "own skinned window" route if a standalone FL-native window is wanted.

## 6. Confidence + open items
- **HIGH:** the class chain & that TCustomWPForm (0x7de900) is the shared skinned host; TForm (0x828d20) is the
  factory gate; HWND@+0x2b0 is the shared VCL handle; content container @+0x11c; skin descriptor @+0x6e8; skin
  obj @+0x49c; chrome = `titlebar`/`tnewcaption`/`toolbutton` + `P_TitleBar`; the ctor chain
  (`0x7e3d60 → 0x8323d0/0x00831460 + 0x7fde80`).
- **MED:** the exact semantics of the WP init fields (`+0x508/+0x512/+0x6d0/+0x6d2/+0x690` etc.); dock/float
  being VCL FormStyle rather than one flag.
- **NEEDS LIVE (one item):** the precise caption-text UStr offset on the instance (rendered by `tnewcaption`;
  source is the VCL Caption). Find by set-known-title-then-scan, or read the `tnewcaption` control's text model.
- **No VMProtect** anywhere on the form-host / chrome path.

## Ghidra annotations (this pass)
Bookmarks only (category "WindowHost"), no renames (shared instance): `0x7de900` TCustomWPForm VMT (shared host),
`0x828d20` TForm classRef (factory gate), `0x7e3d60` TCustomWPForm.Create init, `0x8323d0` TCustomForm.Create,
`0x7dd6a0` build-title-bar (P_TitleBar chrome), `0x841ef0` FLui_CreateFormCore (already named).
