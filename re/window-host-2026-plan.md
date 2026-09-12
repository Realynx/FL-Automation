# Window-host FL 2026 migration/fix plan (analysis only — no code edits)

Scope: make the FL Automate window-host (the AI window embedded inside FL) work in FL 2026 via the
runtime signature scanner (`tools/bridge/sigscan.{h,cpp}`, `SYM("Name")`). All call sites live in
`tools/bridge/dllmain.cpp`. This doc maps every window-host FUNCTION call site to a `SYM`, lists the
DATA globals to resolve RIP-relative, gives a struct/vtable-offset verification checklist (the fragile
cluster), walks the embed sequence (load-bearing vs cosmetic), and ranks what most likely broke in 2026.

Cross-refs: `re/version-fn-map.md` (2026 function addresses), `re/version-portability-design.md` (sym
strategy), memory `fl-version-portability` (inventory). Addresses below are Ghidra (image base 0x400000).

> Migration mechanic (from the design): a call site `rb(0xHEX)` → `SYM("LogicalName")`; the wire token
> `"11a5d20"` → `"sym:Name"`. `SYM` returns 0 when unresolved, so every migrated site must keep the
> existing `if (!fn) return/skip` fail-safe (most already null-check via `invokeGuarded`; direct pointer
> loads like `installShortcutHook`/`flCreateForm` already early-return on NULL).

---

## 1. Call-site → SYM mapping (window-host FUNCTIONS)

All 2025/2026 addresses from `re/version-fn-map.md`. "CONFIRMED" = auto-mapped unique; "REFINE" = one of
the 13 pending (multi/zero/non-unique) — safe to migrate the call site now (fallback column carries the
2025 addr; the sym just won't resolve on 2026 until refined, and the tool fails safe).

| SYM("...") | 2025 | 2026 | dllmain.cpp call sites | status |
|---|---|---|---|---|
| `FLwp_Render` | 0x77adb0 | 0x79ad70 | 254, 277, 641, 994 | CONFIRMED |
| `FLui_WP_SetAlign` | 0x5ceef0 | 0x6087d0 | 273, 640, 1492 | CONFIRMED |
| `FLwp_SetterA` | 0x5d0d90 | 0x60a670 | 274, 638 | CONFIRMED |
| `FLwp_SetterB` | 0x5d0c50 | 0x60a530 | 275, 637 | CONFIRMED |
| `FormShortCut` | 0x114de10 | 0x1248140 | 324 (hook target) | CONFIRMED |
| `FormKeyDown` | 0x10c9920 | *(2 hits)* | 365 (hook target) | REFINE (multi-match) |
| `FLbrz_AddTabClone` | 0x9ac910 | 0xa7cc20 | 476 | CONFIRMED |
| `Delphi_UStrAsg` | 0x4133f0 | 0x4133f0 | 483, 484 | RTL-STABLE (same addr) |
| `FLui_MarkKbCapture` | 0x802820 | *(non-unique)* | 495 | REFINE |
| `FLbrz_SelectTabById` | 0x9ac590 | *(0 hits)* | 514, 543 | REFINE (prologue changed) |
| `TQuickEdit_SetText` | 0x74c260 | *(0 hits)* | 598 | REFINE (string-anchor) |
| `TQuickEdit_Refresh` | 0x74bb70 | *(0 hits)* | 600 | REFINE (string-anchor) |
| `FLwp_CreateButtonControl` | 0xf0ddb0 | 0xff11e0 | 633, 1475 | CONFIRMED |
| `FLwp_SetButtonCaption` | 0x5d0ae0 | 0x60a3c0 | 639, 1484 | CONFIRMED |
| `FLmenu_CreateItem` | 0x70e1a0 | 0x72d670 | 927, 1043, 1057, 1151, 1299 | CONFIRMED |
| `FL_ChildCount` | 0x81dda0 | 0x83fdf0 | 965 | CONFIRMED |
| `FL_ChildAt` | 0x81ddc0 | 0x83fe10 | 966 | CONFIRMED |
| `FL_FreeObj` | 0x40faa0 | 0x40faa0 | 967 | RTL-STABLE |
| `FL_ListRemoveAt` | 0x64f4d0 | 0x663b10 | 971 | CONFIRMED |
| `FLwp_SetControlValue` | 0x5d0d10 | 0x60a5f0 | 1496 | CONFIRMED |
| `TQuickEdit_ctor` | 0x74c400 | *(non-unique)* | 267 | REFINE |
| `FLui_CreateFormFromClassRef` | 0x10c2aa0 | 0x11c2870 | 1630 | CONFIRMED |
| `FLui_DockLayout` | 0x7e6170 | 0x808160 | 1858 | CONFIRMED |
| `FLui_Focusable` | 0x5de3e0 | 0x617cc0 | 1863 | CONFIRMED |
| `FLwp_SetFormCaption` | 0x841690 | 0x869370 | 1923 | CONFIRMED |
| `FLwp_SetVisible` | 0x833ec0 | 0x85bba0 | 1928, 1963 | CONFIRMED |
| `FLui_ZOrderRefresh` | 0x5d0ea0 | 0x60a780 | 1929 | CONFIRMED |
| `FLui_WP_GetHandle` | 0x5ddf70 | 0x617850 | 1935 | CONFIRMED |
| `FLwp_SetWindowState` | 0x836600 | 0x85e2e0 | 2024, 2041 | CONFIRMED |
| `FLui_SetStatusHint` | 0x10ec870 | 0x11eb580 | 2235 | CONFIRMED |

Notes:
- `FormShortCut`/`FormKeyDown` are **hook targets**, not called — the addr is patched with a `mov rax,imm64;
  jmp rax` trampoline (lines 321-336, 362-377). SYM still applies (`g_shortcutFn = SYM("FormShortCut")`), but
  the hook installer writes 12 bytes over the prologue, so a wrong addr corrupts random code. These MUST
  resolve correctly (or `installShortcutHook`/`installKeyDownHook` must no-op when SYM==0 — the existing
  `if (!g_shortcutFn) return` already does this once migrated).
- RTL-stable pair (`Delphi_UStrAsg`, `FL_FreeObj`) can migrate for consistency but the fallback works
  unchanged on 2026 (same address, confirmed).
- The 5 REFINE rows that are chat-tab-only (TQuickEdit ctor/SetText/Refresh, SelectTabById, MarkKbCapture)
  gate the in-FL chat *tab*, not the embedded AI *window* — the window-host proper (deliverable 4) does
  not depend on them. They can stay VersionLocked on 2026 without blocking the AI window.

---

## 2. DATA-global list (resolve RIP-relative → produce these logical names)

Every hardcoded `rb(0xADDR)` the window-host reads/dereferences as DATA (not a call target). These need
RIP-relative resolution (`SK_DataRef`) or, for the classRefs, a string-/xref-anchor. Proposed logical
names for the `SymEntry` table:

| Logical name | 2025 addr | shape | dllmain.cpp | role / re-derive hint |
|---|---|---|---|---|
| `TScriptDialog_ClassRef` | 0xcf3888 | metaclass (VMT 0xcf3870 +0x18) | 1906 (via `flCreateForm`→`rb` @1626), 1582 | **host-form class passed to CreateFormFromClassRef. CRITICAL.** Anchor on descriptor string `forms.pianorollscriptform` or the `lea r?,[rip+classref]` in TScriptDialog's registration/FormCreate. |
| `TQuickEdit_ClassRef` | 0x7466b8 | metaclass | 266 | class passed to `TQuickEdit_ctor`. Anchor on the `lea` feeding the ctor call. Chat-tab only. |
| `PTR_MainBrowser` | 0x157ffb8 | `*DAT` → TVirtualDataBrowser | 454 | main browser (chat tab). Double-deref: `*(void**)addr`. |
| `PTR_MainForm` | 0x14a8750 | `PTR_DAT` → &mainForm | 869, 904, 2229, (2054 cmt) | main form; window-host reads it for the action list + hint + dock host. Double-deref. |
| `PTR_ToolbarForm` | 0x14aa4c8 | `PTR_DAT` → &toolbarForm | 878, 905 | toolbar form (toolbar buttons + readiness). Double-deref. |
| `PTR_SongObj` | 0x14a9f40 | `PTR_DAT` → song obj | 906 | readiness gate only. |
| `PTR_ChannelList` | 0x14a98d8 | `PTR_DAT` → chan list | 907 | readiness gate only. |
| `PTR_IsPlaying` | 0x14a81c0 | `*DAT` → int flag | 392 | chat `readPlaying`. |
| `DAT_StatusHintText` | 0x15817d0 | UStr ptr (`DAT`) | 2203 | hint-bar text read-back (also written by `FLui_SetStatusHint`). |
| `DAT_LoadingFlag` | 0x157f667 | byte | 2217 | plugin-host loading indicator. |
| `DAT_BusyFlag` | 0x14bdbac | int | 2217 | busy indicator. |

Load-bearing for the AI window: **`TScriptDialog_ClassRef` and `PTR_MainForm`** (the hint path + readiness).
The rest gate chat-tab/menu/status, not the embedded window.

> Pre-existing bug to fix during migration: `g_embedClassRef` is resolved from env/default at line 2577 but
> `DoWinHostEmbed` **ignores it** and hardcodes `flCreateForm(0xcf3888)` at line 1906. The migration should
> route line 1906 through the resolved classRef — `flCreateForm(g_embedClassRef)` with
> `EMBED_CLASSREF_DEFAULT` sourced from `SYM("TScriptDialog_ClassRef")` — so the sym-resolved 2026 classRef
> is actually used and the env override finally works.

---

## 3. STRUCT/VTABLE-OFFSET verification checklist (the fragile cluster)

Grouped by object. Risk = probability the offset shifted in the 25→26 major rebuild AND the blast radius if
wrong. Delphi/VCL *ABI conventions* (UStr len@ptr-4, dynarray len@ptr-8, TMethod Data=Code+8, UStr header)
are **version-stable** (consistent with `UStrAsg` being same-addr) — listed last, do not re-derive.

### 3a. TScriptDialog host form  (instance from `TScriptDialog_ClassRef`; size 0x7f8, TVectorForm-derived)
| Offset | Field | Lines | Risk | Re-derive / sanity-check in FL 2026 |
|---|---|---|---|---|
| **0x194,0x3e0,0x3f0,0x400,0x534,0x564,0x574,0x5a4,0x5b4,0x5d4,0x5f4** | **11 event TMethods** (Code@off, Data@off+8) nulled by `embedNullFormEvents` | 1647 | **CRITICAL** | These null OnShow/OnClose/OnKey*/OnResize/etc. so a bare form doesn't AV on show. If any shifted, either the AV-handler isn't nulled → **crash on `FLwp_SetVisible`**, or a live field is zeroed → corruption. Re-derive from TScriptDialog's published prop-info / DFM event bindings in 2026, or from the deref sites inside its `FormShow`/vtable overrides. Sanity: each slot's Code must be 0 or point into a code section. |
| **0x760,0x770,0x788,0x7a8,0x7b0,0x7b8** | **6 subctrl slots** redirected to a zeroed stub by `embedNullFormEvents` | 1656 | **HIGH** | Missing script-editor subcontrols; paint/region vtable overrides deref `*(*(form+off)+0x78)` with no null-guard on the pointer. If shifted → paint override AVs. Re-derive from the deref sites in TScriptDialog's paint/region overrides (2025 analog `FUN_00cf46f0@0xcf470f`). |
| 0x110 | FCaption (VCL UnicodeString) | 1919, 1921 | MED | `FLwp_SetFormCaption`'s ModRM disp on param_1. Code nulls it first (guards the UStrAsg release-old), so a wrong offset → wrong/garbage caption but not fatal. |
| 0x11c | content container ptr | 1668, 1593 | MED | Source of the content rect. Falls back to a titlebar-inset heuristic (2/24/-4/-26) if unreadable → **cosmetic** if wrong. Re-derive: the container the Realign lays out. |
| 0x45c | form's own top-level HWND | 1585/1931 cmt, 1936 fallback | LOW (encapsulated) | Read via `FLui_WP_GetHandle` (function handles the offset). Only the fallback `*(form+0x2b0)` path (line 1936) touches an offset directly. |
| 0x2b0 | dead VCL handle slot (always 0) | 1936 | LOW | Fallback only; primary is the function. |
| 0x4c2 | winstate byte (0 normal/1 min/2 max) | 1716, 1835, 2038 | MED | min/max glyph + maximize-toggle target. Wrong → min/max cosmetically wrong. Cross-check vs `FLwp_SetWindowState`'s written field. |
| 0x6d0 | dockable-child flag (u16 \|=2) | 1853 | MED | `embedPromoteForm`. Wrong → no dock/maximize affordance; window still shows. |
| 0x6d2 | dock/layout style bits (u32) | 1855, 1856 | MED | fed to `FLui_DockLayout`. Same blast radius as 0x6d0. |
| 0x78 | FL object-model parent | 1838 (diag), 252 (generic WP) | MED | diagnostic + generic reparent check. |
| 0x34 | gate34 (&0x10) | 1836 | LOW | diagnostic only. |
| 0x389 | gate389 (shown gate) | 1837 | LOW | diagnostic only. |

### 3b. Content container `*(form+0x11c)` — WP bounds
| Offset | Field | Lines | Risk | Note |
|---|---|---|---|---|
| 0x90/0x94/0x98/0x9c | x/y/w/h | 1670-1671 | MED | Same WP bounds layout as tree/panel/sibling (3d). Sanity-checked in code; falls back → cosmetic. |

### 3c. Generic WP-control **vtable slots** (`SK_VtableSlot`; shared by form/TQuickEdit/TQuickBtn/tree/panel/browser)
Calling through a shifted vtable slot = AV. Derive each from an accessor's ModRM (`call [rax+disp]`) in 2026.
| vtbl slot | Meaning | Lines | Risk |
|---|---|---|---|
| **0x138** | SetParent | 249, 271, 636, 1454, 1486 | **HIGH** |
| **0x188** | SetBounds | 259, 993, 1491 | **HIGH** |
| **0x200** | Show/Hide | 1453, 1499 | **HIGH** |
| **0xd0** | browser ContentSwitch (the hook slot) | 502 | **HIGH** (chat tab) |
| 0x178 | Repaint | 1500 | MED |
| 0x1e8 | set toggle kind (2-state) | 1480 | MED |
| *0x318, 0x358* | relayout / FormShow (comments only, not called) | 1848, 1884/1990 | N/A |

### 3d. WP bounds field family (read directly, not via vtable) — 0x90/0x94/0x98/0x9c
tree (403-405), sibling MetronomeBtn (1546-1547 read +0x9c/+0x94), panel width (+0x98, 1550). Same layout as
3b; MED. All have code fallbacks → cosmetic if wrong.

### 3e. TQuickEdit fields (chat tab)
| Offset | Field | Lines | Risk |
|---|---|---|---|
| 0x624 | Delphi UStr text ptr | 571 | MED-HIGH (SEH-guarded → empty on miss) |
| 0x62c | caret pos (int) | 599 | LOW (scroll only) |
| 0x682 | multiline flag (u16 \|=1) | 276 | LOW |

### 3f. TQuickBtn fields (Send button + toolbar buttons)
| Offset | Field | Lines | Risk |
|---|---|---|---|
| 0x1e4 | onClick/onChange TMethod **code** | 536, 643, 645, 1448, 1503, 1504 | **HIGH** (our handler wiring + teardown safety) |
| 0x1ec | onClick/onChange TMethod **data** | 644, 1448, 1503 | **HIGH** |
| 0x48a | toggle/latch flags (int \|=0x4001) | 1481 | MED |
| 0x492 | live toggle state byte | 1497 | LOW |
| 0x49c/0x4a4 | paint TMethod code/data (cleared defensively) | 1449, 1466 | LOW |

### 3g. Browser (TVirtualDataBrowser) fields (chat tab)
| Offset | Field | Lines | Risk |
|---|---|---|---|
| 0x158 | tabs container | 289, 437, 458, 471, 540 | HIGH |
| 0x34c | content panel | 402, 487 | HIGH |
| 0x190 | tree control | 403 | MED |
| tabs+0x18 | current index (int) | 290, 438 | MED |
| tabs+0x10 | array ptr | 291, 438, 464, 471, 477, 541 | HIGH |
| tab+0xf4 | tab id (int) | 474, 481, 542 | MED |
| tab+0x20 | caption UStr slot 1 | 483 | MED |
| tab+0x6c | caption UStr slot 2 | 484 | MED |

### 3h. Menu item fields (Tools/View contributions)
| Offset | Field | Lines | Risk |
|---|---|---|---|
| 0x100/0x108 | our onClick TMethod code/data | 987, 1107-1108, 1238, 1354 | HIGH (teardown safety) |
| 0x78 | caption UStr | 957, 1249 | MED |
| 0x18 | tag (i64) | 861, 937, 1301, 1312 | MED |
| 0xb0 | child TList | 981, 1100 | MED |
| child TList +0x10 / +0x8 | count / array | 1102-1103 | MED |
| actionList +0x7c | masterRoot (top-menu container) | 1139, 1326 | MED |
| 0xbc | parent backref | 987 | LOW |
| 0x86 | hide-from-bar byte | 1121 | LOW |

### 3i. Main/toolbar form fields
| Offset | Field | Lines | Risk |
|---|---|---|---|
| mainForm+0x760 | action list | 871 | MED |
| mainForm+0x4c5 | suppress-shortcuts flag | 297, 303 | MED (space fix; cosmetic) |
| toolbarForm+0x760 | fixed toolbar panel | 1534 | MED |
| toolbarForm+0x878 | NewMainMenu bar | 880 | LOW |
| toolbarForm+0x9f8 | MetronomeBtn sibling (size probe, has fallback) | 1543 | LOW |
| panel+0x98 | width (has fallback) | 1550 | LOW |
| mainForm+0xb18 | dock-host (comment only; dock deferred) | 2054 | N/A |

### 3j. Delphi/VCL ABI conventions — **version-STABLE, do NOT re-derive**
UStr length @ ptr-4 (571, 959, 1251, 2205); dynarray length @ ptr-8 (465, 472, 478); TMethod
{Code,Data} adjacency = Data at Code+8 (throughout); UStr header codepage/elemsize/refcount/len in
`makeUStr` (233-239). These are Delphi RTL layout, stable across the 25→26 build.

---

## 4. The embed sequence — what makes the window APPEAR

`DoWinHostEmbed` (lines 1887-1982) runs on FL's MAIN thread. It **only creates + shows the FL host form and
returns its HWND**; it does NOT reparent our WPF child — the managed side `SetParent`s the child into
`g_embedContentHwnd` on the child's own thread (to avoid a cross-thread `SetParent` deadlock; see the block
comment 1873-1886). So "the window appears" = the FL host form is created, doesn't AV on show, and yields a
valid HWND for the managed reparent.

Step-by-step (fresh-create branch, line 1904 guard):

1. **Create host form** — `flCreateForm(0xcf3888)` → `FLui_CreateFormFromClassRef(classRef,&slot)` (1906,
   1630). **LOAD-BEARING.** Needs both `SYM("FLui_CreateFormFromClassRef")` and the resolved
   `TScriptDialog_ClassRef`. Wrong fn or classRef → AV or bogus form → nothing appears.
2. **Null event TMethods + redirect subctrls** — `embedNullFormEvents(form)` (1910 → offsets 1647/1656).
   **LOAD-BEARING (crash-prevention).** If the OnShow slot (0x5d4) isn't nulled because the offset shifted,
   step 4 (`FLwp_SetVisible`) runs FormShow which derefs the missing script-editor subcontrol → AV. This is
   the single most likely 2026 crash point after the classRef.
3. **Caption** — null `form+0x110`, then `FLwp_SetFormCaption(form,"FL Automate")` (1917-1924).
   **COSMETIC** (guarded; wrong offset → wrong caption, not fatal).
4. **Show with chrome** — `FLwp_SetVisible(form,1)` (1928) then `FLui_ZOrderRefresh(form)` (1929).
   **LOAD-BEARING.** SetVisible → SetShowing → Realign lays out the titlebar + content. Depends on step 2
   having neutralized the AV handlers.
5. **Get the HWND** — `FLui_WP_GetHandle(form)` → `*(form+0x45c)`, fallback `*(form+0x2b0)` (1935-1937).
   **LOAD-BEARING.** No valid HWND (`IsWindow` fails) → `FAIL step=no-form-hwnd` → external fallback, window
   never embeds. `g_embedContentHwnd = formHwnd` (1938) is the reparent target handed to the managed side.
6. **Position + frame paint** — center over `g_mainWnd`, `SetWindowPos` + `RedrawWindow` (1943-1952).
   Cosmetic (pure Win32).
7. **Subclass the form HWND** — `SetWindowLongPtrW(formHwnd, GWLP_WNDPROC, embedHostSubProc)` (1955). Needed
   for resize-glue + caption buttons; not for first appearance. Pure Win32.
8. **Promote to dockable/maximizable** — `embedPromoteForm` (1960 → `form+0x6d0`, `FLui_DockLayout`,
   `FLui_Focusable`). **COSMETIC/affordance** — max button etc.; window shows without it.
9. **Read content rect** — `embedReadFormContentRect` via `form+0x11c` (1968), fallback heuristic.
   Cosmetic (child inset).

Reuse branch (1961-1965): re-`FLwp_SetVisible` + Win32 `ShowWindow`. Managed side re-attaches the child.

**Minimal set for the window to APPEAR:** `FLui_CreateFormFromClassRef` + `TScriptDialog_ClassRef` (step 1),
the 11 event-TMethod + 6 subctrl offsets (step 2), `FLwp_SetVisible` (step 4), `FLui_WP_GetHandle` + the
`+0x45c/+0x2b0` HWND offsets (step 5). Everything else degrades to cosmetic or is pure Win32.

---

## 5. Risk ranking — fix highest-probability cause first

**#1 (certain) — every hardcoded FUNCTION address is wrong on 2026.** The bridge rebases `base + (ghidra -
0x400000)`; FL 2026 moved all FL code (fn-map confirms, e.g. CreateFormFromClassRef 0x10c2aa0→0x11c2870). So
the very first embed call already lands in unrelated code → AV/garbage. **This is the primary breakage.**
Fix: migrate the deliverable-1 call sites to `SYM` (2026 addresses already mapped for the whole window-host
core — all CONFIRMED). Biggest win, lowest effort.

**#2 (certain) — the `TScriptDialog_ClassRef` (0xcf3888) data global moved.** It is just an address in
`.rdata` that relocates like everything else; the rebased 0xcf3888 on 2026 points at the wrong bytes →
`FLui_CreateFormFromClassRef` gets a bogus metaclass → AV at step 1. Fix: resolve RIP-relative /
string-anchor (deliverable 2). Also fix the `g_embedClassRef`-ignored bug so the resolved value is used.

**#3 (probable) — the TScriptDialog event-TMethod + subctrl offsets shifted.** A 22MB tighter recompiled
build can add/reorder VCL/FL class fields. If the OnShow slot (0x5d4) or a subctrl slot (0x7b8) moved,
`embedNullFormEvents` nulls the wrong field → **crash on show** (step 2/4), the classic "window-host broke"
symptom even after functions resolve. Fix: re-verify the 11+6 offsets in 2026 Ghidra (deliverable 3a). This
is the fragile cluster — verify before trusting the 2025 constants.

**#4 (possible) — WP vtable slot indices shifted** (0x138 SetParent / 0x188 SetBounds / 0x200 Show). Wrong
slot = AV when showing/laying out the form or widgets. Verify via `SK_VtableSlot` derivation (3c).

**#5 (lower) — form-layout offsets** (+0x45c HWND, +0x11c content, +0x4c2 winstate). +0x45c is encapsulated
in `FLui_WP_GetHandle` (low direct risk); +0x11c/+0x4c2 have code fallbacks → degrade to cosmetic, and their
failure is *caught* (IsWindow / sanity checks) so they fail-safe rather than crash.

**Recommended order:** (1) migrate all CONFIRMED window-host functions to `SYM` → (2) resolve + wire
`TScriptDialog_ClassRef` and fix the ignored-classRef bug → (3) re-verify the 11 event-TMethod + 6 subctrl
offsets in 2026 → (4) verify the 6 WP vtable slots → then live-test the AI window in FL 2026. Steps 1-2
alone should get the form *created*; step 3 is what stops it *crashing on show*.

### Handoff checklist for the address data you're producing in parallel
- **Functions (deliverable 1):** already have 2026 addrs for all CONFIRMED window-host rows. Only the 6
  chat-tab REFINE rows remain (not required for the AI window itself).
- **Data globals (deliverable 2):** produce RIP-relative resolutions for the 11 listed; `TScriptDialog_ClassRef`
  + `PTR_MainForm` are the load-bearing pair for the window.
- **Struct offsets (deliverable 3):** the 11 event-TMethods + 6 subctrls (3a) are the must-verify set;
  then the 6 WP vtable slots (3c); the rest have fallbacks/are ABI-stable.
