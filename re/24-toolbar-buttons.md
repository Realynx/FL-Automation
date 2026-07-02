# Custom toolbar TOGGLE buttons (metronome-style) + plugin SDK — RE synthesis (task #87)

Goal: add our OWN big square TOGGLE button to FL's main toolbar (like the metronome / typing‑keyboard
toggles), expose it via the plugin SDK so any plugin can add buttons, and ship a built‑in button that
toggles the FL Agent (LLM driver) window. 3 Opus agents RE'd this; full detail in
`re/generated/toolbar-{A-buttons,B-add,C-sdk}.md`. Core pointer chain **LIVE‑VERIFIED 2026‑07‑02**.

## LIVE‑VERIFIED chain (flprobe, running FL; flEngineBase 0x5E560000)
- **Toolbar form = `*(*(0x14aa4c8))`** — DOUBLE deref (`PTR_DAT_014aa4c8` → data slot ghidra 0x157e850 →
  heap form). Live form = TToolbarForm (VMT ghidra `0xcb3470`; near FormCreate 0xcb7f90). A single deref
  lands in‑module and its `+0x9f8` reads null — must double‑deref.
- **Toggle buttons are published fields of the form:** MetronomeBtn @ **form+0x9f8**, KbToMIDIBtn
  (typing‑kb) @ **form+0xaa8** (both live heap objects). Other fields (Agent A): LoopRecordBtn +0xa00,
  BlendRecordBtn +0xa08, PrecountBtn +0xa10, StartOnInputBtn(wait) +0xa18, AutoScrollBtn +0xab0,
  StepEditBtn +0xab8, View* +0xa20..+0xa60.
- **Widget class = TQuickBtn family** — both toggles' VMT = live `0x5e875520` = **ghidra 0x715520**
  (matches `FLwp_CreateButtonControl@0xF0DDB0`'s TQuickBtn, ~0x715508). ⇒ **a button we create with
  0xF0DDB0 is the SAME class as FL's toggles.** (Agent A called it `TQuickEditToolbarItem`/0xb50880 from
  the DFM field type — the LIVE VMT is the TQuickBtn create path; use that.)
- **Toggle state byte = `btn+0x492`** — read 0x00 on both (both off) ✓. This is the source of truth for
  the "lit" look; the class default paint swaps the glyph COLOR by this byte (no custom paint needed for
  the lit state). (Agent A's separate "global" addrs 0x14a8be8 etc. did NOT read as booleans — ignore;
  use btn+0x492.)

## Add our own square toggle — recipe (Agent B, reconciled)
Own‑control TQuickBtn (safest first ship — no FL DFM/model mutation, clean eject):
1. `btn = FLwp_CreateButtonControl()@0xF0DDB0` (TQuickBtn).
2. Make it a 2‑state toggle: `vtbl[0x1e8](btn,2)`; toggle flags `btn+0x48a |= 0x4001`; state via
   `FLwp_SetControlValue@0x5D0D10(btn, 0|1)` (writes state + repaints lit). NB live state field is
   **+0x492** (use this; cross‑checks vs Agent B's +0xc4 guess — +0x492 is the live‑confirmed one).
3. Square + placement: `vtbl[0x188](btn,x,y,s,s)` (WP rect at +0x90/+0x94/+0x98/+0x9c); anchor fix‑right
   `FLui_WP_SetAlign(btn,4)@0x5ceef0`. Parent onto the **fixed** top panel `*(form+0x760)` via
   **`vtbl[0x138]`=SetParent** (NOT the customizable `*(form+0x9c0)` .tpr panel — it serializes/rebuilds).
   Read exact square size/y/free‑x live from a sibling toggle (e.g. metronome btn rect) at impl.
4. Show `vtbl[0x200](btn,1)` → repaint `vtbl[0x178](btn)`.
5. Icon: v1 = P_ILGlyphs glyph char or text caption (`FUN_005d0ae0@0x5d0ae0`); v2 = custom bitmap via an
   OnPaint TMethod `btn+0x49c(code)/+0x4a4(data)` blitting with `FLui_Paint_DrawBitmap@0x58eb80` onto the
   canvas HDC (`*(HDC*)(*(*(btn+0x304)+0xa0)+0x58)`). No skin‑index route gives custom art.
6. Click: OnChange TMethod `btn+0x1e4(code)/+0x1ec(data)` → RWX thunk reads `*(int*)(btn+0x492)`, flips our
   flag, posts a bridge event. Reflect host‑driven state back via `FLwp_SetControlValue(btn,0|1)`.
   Programmatic toggle primitive (Agent A): `FLbtn_SetToggleStateAndClick@0x717e10(btn,newState,fire)`.
7. Persistence: toolbar built once in `TToolbarForm.FormCreate@0xcb7f90`; re‑assert if FL recreates the
   form (EditToolbars `TShortcutsModule.EditToolbarsActionExecute@0xe477d0` / skin / UI‑scale) by polling
   `*(*(0x14aa4c8))` (or `*(btn+0x78)` vs `*(form+0x760)`).
8. Teardown (MAIN thread, before FreeLibrary, ordered): zero TMethods (+0x49c/+0x4a4, +0x1e4/+0x1ec) →
   hide `vtbl[0x200](btn,0)` → unparent `vtbl[0x138](btn,0)` → destroy (or hide‑and‑leave inert) →
   invalidate panel → free thunk/bitmap.

## SDK design (Agent C) — ~90% clone of the existing menu‑contribution pipeline
- **Plugins.Abstractions:** `IFlToolbarRegistrar` (`AddToggle(spec, Func<bool> isActive, Action onToggled)`,
  `AddButton(spec, onClick)`, `Refresh()`), `IFlToolbarButton : IDisposable` (`SetTooltip/SetIcon/Invalidate`),
  `FlToolbarButtonSpec` (Id, Tooltip, Icon, Order, Kind), `FlToolbarIcon` (Png | GlyphName | Text). Mirror
  `IFlMenuRegistrar`/`IMenuContribution`. Add `IPluginContext.Toolbar` beside `.Menu`.
- **Host↔bridge:** reconcile‑from‑managed‑list (like `menu_contrib_*`): new bridge cmds
  `toolbar_button_add/refresh/remove/list` + `toolbar_button_icon <id>` (out‑of‑band PNG via buffer‑resize
  protocol). Managed `ToolbarGlue` (4 `[UnmanagedCallersOnly]`) + CLR‑host export `FlClr_GetToolbarFns`
  (clone of `FlClr_GetMenuFns`). Clicks = event‑driven thunk → `Invoke(id)`; lit state = push‑on‑Changed →
  native re‑queries `Active(id)` + repaints (optional low‑freq WM_TIMER backstop).
- **FL Agent button (dogfood):** register a toggle in `FlAgentPlugin.EnableAsync` beside the existing menu
  toggle; `isActive = EmbeddedChatHost.IsHostVisible()`, `onToggled = ToggleChatVisible` (already exist).
  Sync at the `_chatVisible` flip sites; dispose in `DisableAsync`.
- **What already exists:** `plugins_button_*` builds the Tools▸submenu (not a square btn) — reuse its
  deferred‑install/retry + SEH/graceful‑degrade scaffolding, swap the menu‑item creator for the TQuickBtn recipe.

## Phased build plan
- **P1 managed SDK** (Abstractions + registry + ToolbarGlue + host/context wiring) — unit‑testable, ships
  inert (native‑missing ⇒ no buttons).
- **P2 native** (`DoToolbarInstall`: create/place/toggle/icon/onClick/teardown + the 5 bridge cmds), verify
  via flprobe (add one real square button) before FlAgent.
- **P3 FL Agent toggle button** (small — reuses ToggleChatVisible).
- **P4 polish** (custom‑bitmap icon overlay, icon cache/scaling, granular update, toolbar‑recreate re‑assert).

## Remaining live‑confirm at impl (not blockers)
Exact glyph codepoint / custom‑paint overlay‑vs‑replace, live square size/free‑x/y from a sibling toggle,
the onClick thunk (needs native bridge code — can't be done from the external harness), anchor 4‑vs‑6 on
compact/resize, button destructor for the fullest eject.
