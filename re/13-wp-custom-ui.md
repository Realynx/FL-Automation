# Building custom UI inside FL with the WP widget framework (task #34)

FL's UI = a custom "WP" framework over Delphi VCL. RE'd by 3 agents (form / controls / events).
Verdict: an interactive in-FL custom panel is **feasible but heavy** — controls + events are fully mapped; the
remaining blocker is a **host form** (FL has no generic "blank form" factory). For most needs the pragmatic path
stays "render custom UI in our WPF app." Build the in-FL panel only when FL-native look/placement is required.

## Form creation / lifecycle
- `FLui_CreateFormFromClassRef(classRef, &outSlot)@0x10C2AA0` → `FLui_CreateFormCore(formMgr=*0x14AA6E8, classRef, &outSlot)@0x841EF0`:
  `inst = (*(classRef-0x30))(classRef)` (metaclass create) → `*outSlot=inst` → `(*(*inst+0x78))(inst, 0xFF, formMgr)` (init).
- Form manager (owns lifetime) = `*0x14AA6E8`. **Form's real Win32 HWND = `inst+0x2b0`** → standard `ShowWindow`/`SetWindowPos`/`MoveWindow`/`DestroyWindow` work (no FL-specific show needed for a basic floating window). `FLwp_FormSetAppWindowVisible@0x82FBB0` toggles WS_EX_APPWINDOW on it.
- ⚠️ NO stock empty-form classRef. Each panel (channelrack/mixer/browser/picker) is its own class+VMT+purpose-built Show (e.g. picker classRef `&PTR_FUN_00dae368`@0xDAE2A0, shown by picker-specific FUN_00DB4770). Our-own-panel = repurpose an existing simple form OR author a custom WP class (Delphi metaclass with create-slot@-0x30 + init@vtbl+0x78) from the injected DLL — the heavy part. LIVE-CONFIRM the metaclass create offset by calling the factory on the picker classRef and checking slot+0x2b0 is an HWND.

## Control creation + layout
- Generic ctor: `FLwp_CreateControl(classVMT, 1, 0)@0x717A90` (=TWPControl.Create). Wrappers: button `FLwp_CreateButtonControl@0xF0DDB0` (VMT LAB_00715520), wheel/knob `FLwp_CreateWheelWithSkin(buf, descrWStr)@0xF0DAF0` (VMT PTR_FUN_007a0ca0); digiwheel etc. FUN_00f0de00/FUN_00f0e160. Type = VMT; descriptor `:suffix` (:quickbutton/:wheel/:digiwheel) picks skin+behavior. No isolated label class — reuse a non-interactive button.
- Create→show sequence (from `FLwp_BuildChannelRackControls@0xF0E330`): create → `FUN_005d08c0(ctrl,0)` base init → `vtbl[0x138](ctrl, *(*0x14A8BF8+0x7a0))` skin/theme → `FUN_004133f0(ctrl+0x328, L"controls.button;...:quickbutton")` descriptor → `vtbl[0x188](ctrl,x,y,w,h)` SetBounds (or poke x@+0x90/y@+0x94/w@+0x474 u16/h@+0x476 u16) → hint `FUN_004133f0(ctrl+0xe4, "|^^hint")` → **parent**: `(*(ctrl+0x11c))->vtbl[0x10](ctrl+0x11c, *(parent+0x11c))` (parent's container child; channel rack uses form+0x7d4) → show `vtbl[0x200](ctrl,1)` → finalize `FUN_0076aef0(ctrl,1)`. All main-thread.

## Events + values (interactivity)
- Events = Delphi TMethod {code@+OFF, data(Self)@+OFF+8} slots — settable, so we can install our own handler: poke `ctrl+OFF+8=ourCtx`, `ctrl+OFF=thunkAddr` (bridge allocs an RWX x64 thunk in FL; FL calls `thunk(ctrl,ctx,args)` on the UI thread → record + return fast). Slots: onClick/MouseDown +0x144/+0x14c; button change +0x1e4/+0x1ec; wheel OnChange +0x398/+0x3a0; DblClick +0x1f4/+0x1fc; MouseActivate +0x214/+0x21c; Hint +0x2bc/+0x2c4; GetText +0x4ac/+0x4b4.
- Value: general `int@ctrl+0xc4`, set via `FLwp_SetControlValue@0x5D0D10(ctrl,val)` (writes +0xc4, marks dirty, redraws, notifies); read = peek +0xc4. Wheel/knob value `int@ctrl+0x450`, range `vtbl+0x1e0(idx 0=min/1=max, val)`. Press state byte @+0x4cd. Poll alt: diff +0xc4/+0x450/+0x4cd.
- Input routing: a control becomes live by registering into the form's layout via `(*(ctrl+0x11c))->vtbl[0x10](ctrl+0x11c, formLayout)`; the wpform message loop hit-tests + dispatches to the child's TMethods. So controls created via the standard path are automatically interactive.

## Risks / next step
RWX thunk must be valid x64 + fast + SEH-safe (bad one crashes FL); `data` ptr must outlive the control. Value field varies by subtype (+0xc4 vs +0x450) — prefer the setter. Proof path: instantiate/repurpose a host form → create a button + knob → set onClick/onChange to bridge thunks → verify click/turn calls back. That's the MVP for in-FL custom UI.
