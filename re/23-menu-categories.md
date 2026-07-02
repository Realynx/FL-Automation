# FL menu CATEGORIES / sections — model + recipes (task #23)

Goal: (1) understand how FL groups the items inside a top-level dropdown into **categories / sections**,
(2) map the **View** menu's "Windows" category (the window show/hide toggles), and (3) write recipes to
**insert into** an existing category at a position and to **create a new category** — so the menu-
contribution SDK can later target categories. Companion to `re/16-toolbar-plugins-menu.md` (the menu
model + item struct) and `re/integration-pending-plugins-toolbar.md` (the materializer in
`tools/bridge/dllmain.cpp`). All findings STATIC (Ghidra decompile of `FLEngine_x64.dll`, image base
`0x400000`); no live FL poked. The Part-2 fix (View ▸ FL Agent → top of "Windows") is implemented in
`dllmain.cpp` — see §6.

## TL;DR / verdict
- **A "category" inside a dropdown is just a run of consecutive children in the item's `+0xb0` child
  list, delimited by SEPARATOR items.** There is no category object, no group container, and no
  per-item "category" field that draws dividers. Items render in **child-list order** (= the order you
  insert them; the `index` you pass to `FLmenu_InsertChild`). A category boundary is an explicit
  **separator item** whose caption is the single character `"-"`.
- **Three item kinds**, all the same class (`&PTR_FUN_00706308`), distinguished by data, not type:
  1. **Caption (leaf/clickable)** — normal item; onClick TMethod at `+0x100/+0x108`.
  2. **Submenu** — a caption item that *has children* (`+0xb0`); FL draws it with a ▸ and expands it.
     No onClick (FL expands it). (e.g. our "Tools ▸ FL Plugins".)
  3. **Separator** — caption starts with `"-"` (`*(u16*)(item+0x78) == 0x2d`). `FLmenu_IsSeparatorCaption
     @0x81fc30` + the item vtbl `isSeparator` slot (`vtbl+0xd8 → 0x70e100`) report it; FL draws a divider
     row and it is not selectable. This is the **only** divider mechanism — `+0x87`/`+0x88` do NOT draw
     dividers (see "myth-busting" below).
- **There is no built-in "category header" (grey non-clickable label) item kind.** FL's own dropdowns
  group with bare `"-"` separators, not text headers. A header *can* be faked with a disabled caption
  item (`+0x81 = 0`), but raw enable/checkable pokes are a known crasher in FL's popup renderer (see
  §4) — prefer separators. (Recipe for a named header in §5, with the caveat.)
- **View's "Windows" category = the window show/hide toggles at the TOP of the View dropdown**
  (Playlist / Piano roll / Channel rack / Mixer / Browser / …). It is the first group, so its first
  item (Playlist) is at/near child index 0; the group is closed by a `"-"` separator before View's next
  group. We can't enumerate the exact static order (the dropdown tree is built from streamed DFM/action
  data — the captions have **no code xrefs**), so we locate the category's top **at runtime** by
  scanning View's children for the first window-toggle caption (§3, §6).
- Confidence HIGH on the item kinds + offsets (all decompiled) and on the runtime location strategy
  (uses FL's own child-count/child-at + caption read, already proven by the Tools-submenu work).

---

## 1. The item struct fields that matter for categories
Class VMT `&PTR_FUN_00706308`; created by `FLmenu_CreateItem_CaptionClick@0x70e1a0` →
`FLmenu_NewItemObj@0x70e040` → `FLmenu_ItemBaseInit@0x81a100`. (Caption/child/onClick offsets are from
re/16; the ones below are added/corrected here.)

| off | type | meaning | source (decompiled) |
|----:|------|---------|---------------------|
| +0x18 | i64 | tag / value (we store our contribution index here) | dynamic items |
| +0x78 | UStr | **caption** (Delphi UnicodeString, len at ptr-4). `"-"` ⇒ separator | `FLmenu_SetItemCaption@0x81daf0` |
| +0x80 | u8 | **checked state** (the ✓). Set by `FUN_0081dbb0`, which also calls Win32 `CheckMenuItem(+0xa0,…)` | `FUN_0081dbb0@0x81dbb0` |
| +0x81 | u8 | **enabled** (default 1). `FUN_0081e260` (execute) returns early if 0 ⇒ a disabled item is a non-clickable label | ItemBaseInit; `0x81e260` |
| +0x85 | u8 | internal "live/realized in a menu" flag (gates the radio-group propagation) | `0x81dd00`/`0x81db30` |
| +0x86 | u8 | **show-in-bar / visible** (default 1; read by BuildBar) | ItemBaseInit |
| +0x87 | u8 | **radio-group id** (NOT a sort key — see myth-busting). Checking an item un-checks same-`+0x87` siblings | `FUN_0081dd00@0x81dd00`, `FUN_0081db30@0x81db30` |
| +0x88 | i32 | **group id** (default `-1`); generic grouping handle, not a divider | ItemBaseInit |
| +0x8c | ptr | bound action/owner object (`+0x18` of it = the action's tag) | `FUN_0081dd70`, `0x81e260` |
| +0xa0 | u16 | auto Win32 menu id (used by `CheckMenuItem`) | ItemBaseInit `FUN_008199c0` |
| +0xb0 | ptr | **child list** (TList: count @+0x10, array @+0x8). Presence ⇒ submenu. Order = display order | InsertChild lazily creates |
| +0xbc | ptr | parent back-pointer | InsertChild |
| +0x100/+0x108 | ptr | **onClick TMethod** (code / data=Self) | CreateItem_CaptionClick |
| +0x140 | u8 | checkable/radio flag (drives the auto-toggle in `0x81e260`) | dynamic items |
| +0x151 | u16 | flags; **bit0 set ⇒ force "not a separator"** even if caption is `"-"` | `0x70e100` |
| vtbl+0xd8 | fn | **isSeparator** → `0x70e100`: `((+0x151 & 1)==0) && caption[0]=='-'` | |
| vtbl+0xe0 | fn | setChecked/setVisible | re/16 |

### Myth-busting (corrects re/16's "sort/group order" guess for +0x87)
`FLmenu_InsertChild@0x81e090` does **not** sort the list. With `index>0` it only *clamps* the new item's
`+0x87` up to the predecessor's `+0x87` (`if (new+0x87 < prev+0x87) new+0x87 = prev+0x87`), then inserts
at the **literal `index`** via `FUN_00508d30(list, index, child)`. So **display order = the index you
pass = child-list order.** `+0x87` is a **radio-group id**: `FUN_0081dd00` sets it, and when an item
becomes checked, `FUN_0081db30` walks the siblings and un-checks every one with the **same** `+0x87`
(`FUN_0081dbb0(sib, 0)`). Neither `+0x87` nor `+0x88` causes FL to draw a divider. **Dividers come only
from `"-"` separator items.**

---

## 2. How a category is rendered (the separator path)
- A child with caption `"-"` is reported as a separator by `FLmenu_IsSeparatorCaption@0x81fc30`
  (`(*(longlong*)(item+0x78)==0 || **(short**)(item+0x78)!=0x2d) ? 0 : 1`) and by the item's
  `isSeparator` vtbl slot (`vtbl+0xd8 → 0x70e100`, which also honors the `+0x151 bit0` override). FL's
  popup/bar layout (`FLmenu_LayoutBar@0x7049f0` and the popup window builder reached from
  `FLmenu_ShowPopup@0x70ab80`) draws a divider row for these instead of a normal entry.
- Everything between two separators (or between the top/bottom of the list and a separator) is one
  **category**. Proven in our own code: `populatePluginChildren` inserts `addPluginMenuItem(item, L"-",
  …)` to split "Settings ▸" from the live plugin list, and it renders + works live (see
  re/integration-pending-plugins-toolbar.md §5).

---

## 3. The View menu's "Windows" category — where it begins
- The FL **View** dropdown opens with the **window show/hide toggles** as its first group — what we call
  the **"Windows" category**: **Playlist, Piano roll, Channel rack, Mixer, Browser** (plus Plugin
  picker, etc.). These bind to `TShortcutsModule.ShowPlaylistActionExecute@0xe43d00 /
  ShowPianoRollActionExecute@0xe43bf0 / ShowChannelRackActionExecute@0xe43ac0 /
  ShowMixerActionExecute@0xe43b50` (the live captions carry an `&` accelerator, e.g. `&Playlist`,
  `&Channel rack` — pool entries at `0x133fef7` etc.).
- **Why we locate it at runtime, not statically:** the dropdown tree (masterRoot →
  `*(actionList+0x7c)`) is materialized from FL's **streamed DFM / action-list data**, not built by
  code that references the caption constants — those caption strings have **no code xrefs**
  (`get_xrefs_to 0x133fef7` → none), and the on-disk pool is alphabetical, not menu order. So the exact
  static index of Playlist isn't recoverable from Ghidra alone.
- **Runtime location (what the fix does):** enumerate View's children in order
  (`FUN_0081dda0`=count / `FUN_0081ddc0`=child[i], already wrapped as `flChildCount`/`flChildAt`), read
  each caption (`item+0x78`), strip the `&` accelerator + lower-case, and return the index of the
  **first** child whose caption is one of `{playlist, piano roll, channel rack, mixer, browser}`. That
  index is the **top of the "Windows" category**; inserting there puts our item as the new first row of
  the group (immediately above Playlist). The set is order-independent (any window name found first is
  the group top), and if none match we fall back to append (no regression).

---

## 4. Recipe (a): insert into an existing category at a position
Use FL's own creator — it already takes an index:
```
FLmenu_CreateItem_CaptionClick(parentList, index, captionUStr, &TMethod{code,data}) -> item
    index < 0  → append (== child count)
    index == 0 → first child
    index == N → before the child currently at N  (FUN_00508d30 inserts at the literal index)
```
So "insert at the top of category C" = find C's first child index `i` (the item right after C's opening
separator, or 0 if C is the first group) and pass `index = i`. "Insert at the bottom of C" = pass the
index of C's closing separator (so we land just before it). Our items use only the safe fields
(`+0x18` tag + ✓ glyph in the caption); we do **not** poke `+0x80/+0x81/+0x140` — FL's popup builds a
per-item render control and reading those half-initialized fields faults (`+0x45c` null-deref; learned
in the toolbar work). The `+0x87` clamp in InsertChild is harmless for us (our items aren't radio).

## 5. Recipe (b): create a NEW category
- **Preferred (proven): a separator boundary.** Insert a `"-"` caption item at the boundary index to
  open a new section, then add your items after it (and optionally a trailing `"-"` to close it):
  ```
  sep = FLmenu_CreateItem_CaptionClick(parent, boundaryIdx, makeUStr(L"-"), {0,0});   // divider
  it  = FLmenu_CreateItem_CaptionClick(parent, boundaryIdx+1, makeUStr(L"My item"), {code,data});
  ```
  This is exactly how FL's own dropdowns are grouped and how our Tools ▸ FL Plugins submenu splits
  Settings from the plugin list (rendered + verified live).
- **Named header (grey label) — possible but unproven, use with care.** FL has no header *kind*; a
  header is a **disabled caption** item (`+0x81 = 0` ⇒ `FUN_0081e260` no-ops on click). To do it
  SDK-cleanly, create the item normally then disable it via FL's own enable API **after** it is fully
  realized — do **not** raw-write `+0x81`/`+0x140` before the popup builds (that path is the known
  `+0x45c` crash). Because FL itself uses bare separators (no text headers) between View groups, the
  separator recipe above is the recommended way to "create a category" for the SDK; a future
  `category` SDK param can map to {separator boundary} + optional {disabled header} once the disable-
  after-realize path is live-verified.

## 6. The Part-2 fix (implemented in `tools/bridge/dllmain.cpp`)
The menu-contribution materializer used to **append** every entry (`index = 0xFFFFFFFF`), so the FL
Agent View toggle landed at the **bottom** of View. Changed so a **View** contribution is inserted at
the **top of the Windows category**:

- New POD helper `readItemCaptionDeaccel(item, out, cap)` — reads `item+0x78` (Delphi UStr), strips
  `&`, lower-cases (SEH-guarded, no C++ objects in the `__try` frame).
- New helper `viewWindowsTopIndex(viewMenu)` — scans View's children
  (`flChildCount`/`flChildAt`) and returns the index of the first window-toggle
  (`{playlist, piano roll, channel rack, mixer, browser}`), or `-1` if none.
- `addMenuContribItem(topMenu, c, tag, index)` — gained an `index` param, passed straight to
  `FLmenu_CreateItem_CaptionClick` (`index<0` = append, `>=0` = insert-at). Was hard-coded to append.
- `DoMenuContribInstall` — for `menu == "View"` it computes `idx = viewWindowsTopIndex(top)` and inserts
  there (falls back to append if not found); all other menus keep appending.

Eject-safety is unchanged: every created item is still tracked in `g_menuItems` and torn down by
`removeMenuContribItems` / `clearMenuContribThunksMem` by pointer (position-independent), so the new
insert position doesn't affect removal. Tools ▸ FL Plugins / Settings are untouched (different code
path). Build: `cmake --build tools\bridge\build --config Release` (and Debug) — both green.

## Key addresses (ghidra / image base 0x400000)
| what | addr |
|------|------|
| isSeparator? (caption[0]=='-') | `FLmenu_IsSeparatorCaption` `0x81fc30` |
| item vtbl isSeparator slot | `vtbl+0xd8 → 0x70e100` |
| set radio-group (+0x87) | `0x81dd00` |
| un-check same-radio-group siblings | `0x81db30` |
| set checked (+0x80) + CheckMenuItem | `0x81dbb0` |
| item execute/click (honors +0x81 enabled) | `0x81e260` |
| insert child at index (no sort) | `FLmenu_InsertChild 0x81e090` (FUN_00508d30 = list insert) |
| create item (caption+click, index arg) | `FLmenu_CreateItem_CaptionClick 0x70e1a0` |
| child count / child[i] | `0x81dda0` / `0x81ddc0` |
| View toggle actions | `ShowPlaylist 0xe43d00`, `ShowPianoRoll 0xe43bf0`, `ShowChannelRack 0xe43ac0`, `ShowMixer 0xe43b50` |
| master menu root | `*( *(mainForm+0x760) + 0x7c )` |
</content>
</invoke>
