# Gap G2 — TQuickTree provider node-emit contract + node allocator

Target: `FLEngine_x64.dll` (image base `0x400000`). All addresses below are virtual (rebased).
Status: **RESOLVED** (the node allocator, the create+link primitive, and the full provider→node emit contract are all identified and decompiled). Minor open item noted at the end.

FL's browser tree is a virtual / data-source tree. There is **no public `AddNode`**. Two mechanisms put nodes on screen:

1. **Top-level provider emit** — `FLbrz_PopulateTreeFromTab` iterates the provider array and calls each `provider->vtbl[0x70]`. A provider either (a) repopulates an internal content model and lets the tree realize nodes lazily, or (b) directly creates+links nodes via the generic tree create primitive.
2. **Lazy expansion** — when an expandable node is opened, the tree pulls a child *count* and *binds* each child via instance callbacks, allocating real node structs on demand.

Both paths bottom out in the **same node allocator** and the **same relative-insert link helper**.

---

## 1. Function map (renamed + tagged `UI_win_browser`, saved)

### Generic tree (`FLui_Ctl_Tree_*`)
| Addr | New name | Role |
|------|----------|------|
| `0x00948fe0` | `FLui_Ctl_Tree_AllocNode` | **THE node allocator.** `node = AllocNode(tree)`. |
| `0x0095b050` | `FLui_Ctl_Tree_LinkNode` | **Link helper** = tree `vtbl[0x718]`. Relative insert, 4 modes. |
| `0x00964010` | `FLui_Ctl_Tree_CreateNode` | **Alloc + link wrapper.** `node = CreateNode(tree, refNode, mode, optExtra)`. The cleanest single-node primitive. |
| `0x0094a0f0` | `FLui_Ctl_Tree_RealizeChildren` | Bulk-realize N blank child nodes under a parent (inline alloc+link loop). |
| `0x0095a7d0` | `FLui_Ctl_Tree_ProvideChildren` | tree `vtbl[0x6f0]`. Queries child count, calls RealizeChildren. |
| `0x0095a850` | `FLui_Ctl_Tree_InitNode` | tree `vtbl[0x6f8]`. Sets inited flag, binds node, derives expandable flag. |
| `0x00954f50` | `FLui_Ctl_Tree_GetChildCount` | tree `vtbl[0x538]`. Thin dispatch → instance callback `tree+0x8cc` (ctx `tree+0x8d4`). |
| `0x00954f90` | `FLui_Ctl_Tree_BindNode` | tree `vtbl[0x540]`. Thin dispatch → instance callback `tree+0x8dc` (ctx `tree+0x8e4`). |
| `0x00960620` | `FLui_Ctl_Tree_ClearChildren` | Unlink+free all children of a node, reset `+0x04`/`+0x30`/`+0x38`. |
| `0x004093c0` | *(left as-is — global)* | Delphi zeroing `AllocMem` (via `PTR_FUN_01297ca8`). Used engine-wide; not renamed. |

Already-RE'd (referenced, not re-done): `FLui_Ctl_Tree_ExpandCollapseImpl@0x968b10`, `FLui_Ctl_Tree_BaseInit@0x9460a0`, browser tree ctor `FUN_0098a130@0x98a130`.

### Browser provider path (`FLui_Browser_Provider_*` / `FLui_Browser_*`)
| Addr | New name | Role |
|------|----------|------|
| `0x009a8760` | `FLui_Browser_AddProvider` | Registers a provider: appends it to `browser+0x2ec` dynarray. |
| `0x009b8e10` | `FLbrz_PopulateTreeFromTab` *(pre-existing)* | Driver: iterates `browser+0x2ec`, calls each `provider->vtbl[0x70]`. |
| `0x00fc19b0` | `FLui_Browser_Provider_EmitNode` | **Core emit:** content-item → node + payload, linked under parent. |
| `0x00fc1910` | `FLui_Browser_Provider_EmitNodeFields` | Builds a content-item from fields, then calls EmitNode. |
| `0x00fc2160` | `FLui_Browser_Provider_PopulateWebProjects` | Direct-emit example (FL Studio Web projects): clear → per-item EmitNode. |
| `0x00fc3400` | `FLui_Browser_Provider_WebProjectsEmit` | A provider `vtbl[0x70]` (kind 0x1c). |
| `0x00f9dd00` | `FLui_Browser_Provider_StaticTabsEmit` | A provider `vtbl[0x70]` (model-based: Support/Settings/Artwork). |

---

## 2. Provider array + populate driver

- Providers live in a dynarray at **`browser+0x2ec`** (`+0x08` = data ptr to array of provider-object pointers, `+0x10` = count). `browser` is the `TVirtualDataBrowser` control, focused instance `*0x157ffb8`.
- Register with `FLui_Browser_AddProvider(browser, providerObj)` → pushes into `browser+0x2ec`.
- `FLbrz_PopulateTreeFromTab(browser, tree, selNode, dataFolders, kind, p3)`:
  ```c
  arr = *(browser + 0x2ec);
  for (i = 0; i < *(int*)(arr + 0x10); i++) {
      provider = *(void**)(*(arr + 8) + i*8);
      (*(code**)(*provider + 0x70))(provider, tree, kind, dataFolders); // <-- vtbl[0x70]
  }
  ```
- Provider objects are FL "object-system" instances: first qword = vtbl; `vtbl[0x70]` is the emit slot. Providers seen in `FLbrz_MainBrowserCtor@0xf8d82b` (created with vtbls `PTR_FUN_00f99d98`, `PTR_FUN_00f984b8`, `LAB_00f77168`, `PTR_FUN_00faea18`, `PTR_FUN_00fafa40`, `PTR_FUN_00fb0460`). Resolve a slot with `*(vtbl + 0x70)`. (Provider #1's slot 0x70 = `0xf9b6b0` is a no-op stub; not all providers emit.)

`provider->vtbl[0x70]` signature: `void emit(provider, TTree* tree, int kind, void* dataFolders [, byte p5, char p6])`. `kind` selects which tab/category this provider should populate (each provider gates on a `kind` bitmask, e.g. WebProjects gates `kind==0x1c`).

---

## 3. THE node allocator — `FLui_Ctl_Tree_AllocNode @ 0x00948fe0`

```c
node* AllocNode(tree) {
    size = 0x40;                               // base node struct
    if ((*(u16*)(tree+0x34) & 0x10) == 0) {    // tree wants per-node extra
        if (*(int*)(tree+0x52c) == -1)
            tree->vtbl[0x828](tree, &tree->[0x52c]);   // compute extra size, cache
        size = *(int*)(tree+0x52c) + 0x40;
    }
    node = AllocMem(size + *(u32*)(tree+0x7c8));   // FUN_004093c0 -> zeroing AllocMem
    *(u32*)(node+0x10) = 1;                     // display-row span = 1
    *(u32*)(node+0x14) = *(u32*)(tree+0x514);   // row height (int)
    *(u16*)(node+0x08) = *(u16*)(tree+0x514);   // row height (u16, used for height accumulation)
    *(u16*)(node+0x0a) = 0x80;                  // flags = VISIBLE
    *(u8 *)(node+0x0c) = 0x32;                  // default indent/metric (=50)
    return node;
}
```

Key points:
- Raw memory comes from **`FUN_004093c0` → `PTR_FUN_01297ca8`** (Delphi `AllocMem`, **zero-filled**). This is why `next-sibling(+0x28)`, `first-child(+0x30)`, `last-child(+0x38)`, `parent(+0x18)`, `payload(+0x48)` are all valid-zero until the caller sets them.
- The allocator sets ONLY metrics+flags; it does **not** link the node or set the payload. Linking + payload are the caller's job (see §4/§5).
- Node total size = `0x40 + tree[0x52c] + tree[0x7c8]`. The payload slot `+0x48` lives inside the tree-reserved per-node extra; the browser content tree reserves enough.

### Node struct layout (confirmed)
| Off | Type | Meaning |
|-----|------|---------|
| `+0x00` | int | sibling index (ordinal among siblings) |
| `+0x04` | int | realized child count (0 + flag 0x40 ⇒ children pulled lazily) |
| `+0x08` | u16 | row-height copy (height accumulation) |
| `+0x0a` | u16 | **flags**: 0x1 inited · 0x20 expanded · 0x40 expandable · 0x80 visible · 0x100 expand-target · 0x200 has-extdata · 0x400 last-of-single · 0x1000 ? · 0x4000 busy |
| `+0x0c` | u8 | indent/metric (def 0x32) |
| `+0x10` | int | display-row span (def 1) |
| `+0x14` | int | row height (px) |
| `+0x18` | ptr | **parent** (root sentinel = `tree+0x50c`) |
| `+0x20` | ptr | **prev sibling** |
| `+0x28` | ptr | **next sibling** |
| `+0x30` | ptr | **first child** |
| `+0x38` | ptr | **last child** (O(1) append) |
| `+0x40..` | — | tree-reserved extra; optExtra stored at `+0x40 + tree[0x7c8]*8` (flag 0x200) |
| `+0x48` | ptr | **payload** (caption = `payload->vtbl[0x80](payload,&out)`; `payload+0x10`=owning tree; `payload+0x18`=content item) |

---

## 4. Link helper — `FLui_Ctl_Tree_LinkNode @ 0x0095b050` (tree `vtbl[0x718]`)

`LinkNode(_, newNode, refNode, tree, mode)` — relative insert, 4 modes:

| mode | meaning | core links |
|------|---------|------------|
| 1 | insert **before** sibling `refNode` | new.prev=ref.prev; ref.prev=new; new.next=ref; new.parent=ref.parent; new.index=ref.index; if new.prev==0 → parent.firstChild=new else prev.next=new |
| 2 | insert **after** sibling `refNode` | new.next=ref.next; ref.next=new; new.prev=ref; new.parent=ref.parent; if new.next==0 → parent.lastChild=new else next.prev=new; new.index=ref.index |
| 3 | **prepend child** of `refNode` | if no firstChild → first=last=new, new.next=0 else oldFirst.prev=new, new.next=oldFirst, ref.firstChild=new; new.prev=0; new.parent=ref; new.index=0 |
| 4 | **append child** of `refNode` (most common) | if no lastChild → first=last=new, new.prev=0 else oldLast.next=new, new.prev=oldLast, ref.lastChild=new; new.next=0; new.parent=ref; new.index=prev?prev.index+1:0 |

After linking (all modes): walk affected nodes incrementing `+0x00` index; `parent.childCount(+0x04)++`; `parent.flags(+0x0a) |= 0x40` (expandable); update visible-row bookkeeping via `FUN_009467a0`/`FUN_009467d0`.

---

## 5. THE emit contract (the answer to G2)

### 5a. Generic single node: `FLui_Ctl_Tree_CreateNode @ 0x00964010`
```c
node* CreateNode(tree, refNode, mode, optExtra) {
    if (mode == 0) return 0;
    BeginInsert(tree);                                  // FUN_0095ffd0
    if (refNode == 0) refNode = *(tree+0x50c);          // root sentinel
    node = FLui_Ctl_Tree_AllocNode(tree);               // §3
    // when target is the root sentinel: mode 1 -> 3, mode 2 -> 4 (become child-of-root)
    if (mode in {3,4} && !(refNode.flags & 1)) tree->vtbl[0x6f8](tree, refNode);  // InitNode parent
    tree->vtbl[0x718](tree, node, refNode, tree, mode); // LinkNode  §4
    if (optExtra) { *(void**)(node + 0x40 + tree[0x7c8]*8) = optExtra; node.flags |= 0x200; }
    // redraw/scroll bookkeeping, vtbl[0x900]/FUN_00964380...
    return node;
}
```
This is the cleanest reusable primitive: **allocate + link in one call**. It does NOT set the payload (`+0x48`) — caller sets it.

### 5b. Browser provider emit: `FLui_Browser_Provider_EmitNode @ 0x00fc19b0`
The exact sequence a browser provider runs to add ONE child node:
```c
node* EmitNode(ctx, tree, parentNode, contentItem) {
    node = FLui_Ctl_Tree_CreateNode(tree, parentNode, 3 /*prepend child*/, 0);  // §5a
    // choose payload class by content-item type byte (contentItem+0x54):
    switch (*(u8*)(contentItem+0x54)) {
      case 0x86: payload = new(PTR_FUN_00fb0630, 1, browser=*(ctx+0x14), tree, node); break; // folder/group
      case '%' : payload = new(PTR_FUN_00fb0838, 1, *(ctx+0x14), tree, node); break;          // special
      default  : payload = new(PTR_FUN_00fb0a40, 1, *(ctx+0x14), tree, node); break;          // leaf/item
    }
    *(void**)(node + 0x48) = payload;                 // attach payload
    FUN_00988080(*(payload+0x18-slot)... , contentItem); // bind: payload+0x18 = contentItem
    (*(code**)(*(ctx+0x14) + 0x128))(*(ctx+0x14), node); // browser.vtbl[0x128]: register/index node
    return node;
}
```
- The **content item** object (class `&DAT_00984000`) is the data record: `+0x08`=caption(UStr), `+0x30`=path/subtitle(UStr), `+0x54`=type byte, `+0x24`=icon id, `+0x2c`=u16, `+0xc4`=u32. Build it with `FLui_Browser_Provider_EmitNodeFields@0xfc1910` (which calls `FUN_00987fd0(&DAT_00984000,1)` then sets the fields and calls EmitNode).
- The **payload** object (node+0x48) wraps the content item (`payload+0x18 = contentItem`, `payload+0x10 = owning tree`) and exposes the caption via `payload->vtbl[0x80](payload,&out)`.
- A folder-type item (type 0x86) yields a payload whose later child-binding makes the node expandable; leaf items don't.

### 5c. Full provider populate (direct style) — `FLui_Browser_Provider_PopulateWebProjects@0xfc2160`
```c
tree->vtbl[0x850](tree);                 // BeginUpdate
FLui_Ctl_Tree_ClearChildren(tree, parentNode, 0);
for (item in items) {
    node = EmitNodeFields(ctx, tree, parentNode, caption, subtitle, typeByte, icon, ...); // §5b
    // optionally set fields on *(node+0x48)->contentItem (path at item+0xb8, id at item+0x50)
    // optionally add inline buttons via FUN_00988a10(contentItem, label, glyph, cb)
}
tree->vtbl[0x888](tree);                 // EndUpdate (recompute layout, repaint)
```

### 5d. Lazy / model-driven style (providers #2, #4)
Instead of emitting nodes directly, these providers repopulate an internal **content model** (held at `provider+0x14`/`provider+0x20`) inside `BeginUpdate(vtbl[0x850]) … EndUpdate(vtbl[0x888])`. The tree then realizes nodes on demand:
- **Expand** (`FLui_Ctl_Tree_ExpandCollapseImpl@0x968b10`): if `!(node.flags & 1)` → `vtbl[0x6f8]` **InitNode**; if `(node.flags & 0x40) && node.childCount==0` → `vtbl[0x6f0]` **ProvideChildren**.
- **ProvideChildren** (`0x95a7d0`): `vtbl[0x538]` **GetChildCount**(tree,node,&n); then `FLui_Ctl_Tree_RealizeChildren(tree,node,n)`.
- **RealizeChildren** (`0x94a0f0`): loop n times — `child = AllocNode(tree)`; inline-link as append (`child.index`, `child.prev=parent.lastTracker`, `prev.next=child`, `child.parent=parent`, set `parent.firstChild`/tail), finally `parent.childCount = n`. Children are **blank**; payload bound later.
- **InitNode** (`0x95a850`): sets flag 0x1, calls `vtbl[0x540]` **BindNode**(tree, parentNode, node, &outFlags); outFlags bits → set node flags (0x2 expanded-want, 0x4→0x40 expandable, 0x10 expand-target, 0x8→0x1000).
- **GetChildCount**/**BindNode** are thin dispatchers to **instance callbacks**: count at `tree+0x8cc` (ctx `tree+0x8d4`), bind at `tree+0x8dc` (ctx `tree+0x8e4`). Wiring these two callbacks is the "register a data source" mechanism for the lazy path. (Where they get assigned for the browser content tree is still open — see below.)

---

## 6. REUSE recipe — populate FL's OWN browser tree natively

Two viable strategies. **Strategy A (direct, recommended)** avoids the lazy-callback plumbing entirely.

### Strategy A — register a provider, emit nodes directly
1. Build a provider object whose vtbl slot `0x70` points at your emit function with signature
   `void emit(provider, TTree* tree, int kind, void* dataFolders)`.
2. `FLui_Browser_AddProvider(browser, provider)` (browser = `*0x157ffb8`) — appends to `browser+0x2ec`. Your `vtbl[0x70]` is then invoked by `FLbrz_PopulateTreeFromTab` for every tab refresh; gate on `kind`.
3. Inside `emit`:
   ```c
   tree->vtbl[0x850](tree);                       // BeginUpdate
   parent = *(tree+0x50c);                         // root sentinel  (or an existing node)
   // Per item — pick ONE:
   //  (a) high level:
   item = FUN_00987fd0(&DAT_00984000, 1);
   UStrAsg(item+0x08, caption); UStrAsg(item+0x30, subtitle);
   *(u8*)(item+0x54) = typeByte; *(u32*)(item+0x24) = iconId;
   node = FLui_Browser_Provider_EmitNode(ctx, tree, parent, item);   // ctx+0x14 must hold browser
   ReleaseRef(item);
   //  (b) low level (no content-item/payload, e.g. plain caption tree):
   node = FLui_Ctl_Tree_CreateNode(tree, parent, 4 /*append child*/, 0);
   *(void**)(node+0x48) = myPayload;   // payload must answer vtbl[0x80](payload,&out)=caption
   tree->vtbl[0x888](tree);                        // EndUpdate -> relayout + repaint
   ```
4. To make a node expandable on demand: set `node.flags(+0x0a) |= 0x40` and leave `node.childCount(+0x04)==0`; supply children when expanded (Strategy B callbacks) OR pre-emit children eagerly with `CreateNode(tree, node, 4, …)`.

### Strategy B — lazy data source (for huge/virtual trees)
Set the two instance callbacks before population:
- `*(tree+0x8d4) = ctxForCount; *(tree+0x8cc) = &GetChildCount_cb;` where `cb(ctx, tree, node, int* outCount)`.
- `*(tree+0x8e4) = ctxForBind;  *(tree+0x8dc) = &BindNode_cb;`  where `cb(ctx, tree, parentNode, node, byte* outFlags)` — set `node+0x48` payload here and OR `*outFlags` with 0x4 to mark expandable.
Then create top-level nodes with `CreateNode(tree, root, 4, 0)`, marking parents expandable; the tree calls your callbacks through `ProvideChildren`/`InitNode` on expand. (Avoids materializing the whole tree.)

### Allocator-only escape hatch
If you must hand-roll: `node = FLui_Ctl_Tree_AllocNode(tree)` then `FLui_Ctl_Tree_LinkNode(0, node, parent, tree, 4)` replicates `CreateNode`'s append exactly (set `node+0x48` afterward). Memory is zero-filled, so unset sibling/child/payload pointers are safe.

---

## 7. Confidence + open items
- **High confidence:** node allocator (`0x948fe0`), link helper modes (`0x95b050`), create+link wrapper (`0x964010`), bulk-realize (`0x94a0f0`), provider iteration + `vtbl[0x70]` dispatch (`0x9b8e10`), provider register (`0x9a8760`), and the EmitNode payload/content-item binding (`0xfc19b0`). All decompiled and cross-checked; node-struct offsets match the verified anchors.
- **High confidence:** `vtbl[0x538]`/`vtbl[0x540]`/`vtbl[0x6f0]`/`vtbl[0x6f8]`/`vtbl[0x718]`/`vtbl[0x850]`/`vtbl[0x888]` slot meanings (resolved from the base tree vtbl `0x9897a8` and confirmed by call sites).
- **Open:** exactly where the browser content tree assigns its lazy data-source callbacks `tree+0x8cc`/`tree+0x8dc` (and their ctx `+0x8d4`/`+0x8e4`) was not pinned down — not needed for Strategy A. If Strategy B is pursued, find the writer of `tree+0x8dc` (likely in the browser content-bind path off `FLbrz_MainBrowserCtor`/`FUN_0098a130`).
- **Note (cross-area):** content-item class = `&DAT_00984000` (record builder `FUN_00987fd0`); browser-node payload classes = `PTR_FUN_00fb0630` (folder/0x86), `PTR_FUN_00fb0838` (0x25), `PTR_FUN_00fb0a40` (leaf). `FUN_00988080` = bind content-item into payload; `FUN_00988a10` = add inline action button to a content item. These belong to the browser content/payload subsystem (not retitled here).
