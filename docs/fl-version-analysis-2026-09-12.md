---
search:
  boost: 0.3
---

# FL Studio binary verification, 2026-09-12

This pass inspected the installed engines with Ghidra 12.1.2 and an independent PE/byte-signature
scanner. Inputs were read from disk; FL Studio was not started, injected, or changed. Ghidra used
new isolated projects under ignored `artifacts/reanalysis`. No license, activation, or security
features were modified.

## Exact input identity

Both engines have preferred image base `0x400000`. Addresses below are preferred virtual
addresses (VAs); subtract `0x400000` for RVAs. Runtime ASLR bases will differ.

| Engine file version | File bytes | SHA-256 |
| --- | ---: | --- |
| 25.2.5.5319 | 51,142,960 | `1b7e2381f2853ee590bff74429fb8aa5540d33a5b33c0c5e41ec01c03fe0c7ac` |
| 26.1.3.5570 | 22,207,792 | `bcba500563d5b3c78879e6c25cf872ed3674999c0d694087f2665268e8d1aeab` |

The installed directories were `C:\Program Files\Image-Line\FL Studio 2025` and
`C:\Program Files\Image-Line\FL Studio 2026`. The earlier reference build **26.1.0.5530 was
not installed** and was not independently verified in this pass. Results for 26.1.3 must not
be assigned to every FL Studio 2026 patch.

## Main finding: mixer offsets changed, not just the stride

Ghidra recovered FL's own Python mixer wrappers from their method-registration names, then
followed their reads into the mixer array. This provided semantic evidence independent of
the existing bridge's constants.

| Property | 25.2.5.5319 | 26.1.3.5570 | Evidence |
| --- | --- | --- | --- |
| Mixer array pointer slot | `0x14A7EB0` | `0x15DD808` | Existing unique `MixerTrackArray` signature; wrappers use the same global |
| Track stride | `0x1474` | `0x1478` | Old multiplication `0x51D * 4`; current `0x28F * 8` |
| Track type / custom name | `+8` / `+0xC` | `+8` / `+0xC` | Name cores `0x11C2450` / `0x12C03A0` |
| Enabled / solo state | `+0x18` / `+0x1A` | `+0x18` / `+0x1A` | `isTrackEnabled` and `isTrackSolo` wrappers |
| Send level table | `+0x2E4` | **`+0x394`** | `getRouteToLevel`: `0xE09220` / `0xD63990` |
| Send active table | `+0x2E8` | **`+0x398`** | `getRouteSendActive`: `0xE092C0` / `0xD63A30` |
| Effect-slot pointer array | `+0x1324` | **`+0x134C`** | `getSlotColor`: `0xE065C0` / `0xD60D30` |

Send records remain eight bytes: signed 32-bit level at `+0`, active byte at `+4`. Both
`getRouteToLevel` wrappers divide by the verified double constant **16000.0**. Effect-slot
pointers remain eight bytes with ten slots. The engine wrappers bound route destinations
to `0..501`; this is storage capacity, not a promise that every destination is usable in
the current project.

The 2026 send setter was followed through `setRouteTo` (`0xD63610`), its dispatched callback
(`0xD63590`), and `FLmx_SetRouteActiveCore` (`0x12A5460`). The core accepts the same five
arguments as 2025: routing manager, source, destination, mode, and notification flag. It
checks routing validity and updates the current `+0x394` record. It can invoke a routing
confirmation dialog for some changes; static analysis cannot guarantee unattended completion.

The previous unknown-version branch chose the 2025 mixer stride for the installed 2026
build. Even after fixing that stride, old send/effect offsets would still address unrelated
memory. The injected profile must therefore expose a **complete verified mixer layout**,
and a caller must reject an unavailable layout before reading or changing a track.

The fourteen fields supplied by `FlMixerLayout` are:

| Profile field | 25.2.5.5319 | 26.1.3.5570 |
| --- | ---: | ---: |
| `trackStride` | `0x1474` | `0x1478` |
| `nameOffset` | `0xC` | `0xC` |
| `typeOffset` | `8` | `8` |
| `enabledOffset` | `0x18` | `0x18` |
| `soloOffset` | `0x1A` | `0x1A` |
| `sendTableOffset` | `0x2E4` | `0x394` |
| `effectSlotsOffset` | `0x1324` | `0x134C` |
| `sendStride` | `8` | `8` |
| `sendLevelOffset` | `0` | `0` |
| `sendActiveOffset` | `4` | `4` |
| `effectSlotStride` | `8` | `8` |
| `effectIndexOffset` | `0x64` | `0x64` |
| `effectNameOffset` | `0x58` | `0x58` |
| `effectLoadVtableOffset` | `0xF0` | `0xF0` |

### Effect objects and loading

The current `plugins.getPluginName` wrapper at `0xD80DB0` independently confirms the mixer
effect pointer array `+0x134C` and the pointed-to object's fallback name at `+0x58`.
The occupied-plugin predicate at `0x1330B80` checks the signed integer at `object+0x64`.

| Item | 25.2.5.5319 | 26.1.3.5570 |
| --- | --- | --- |
| `TPluginRack` metaclass | `0x11B4960` | `0x12B2890` |
| Loader vtable slot | `+0xF0` | `+0xF0` |
| Mixer loader thunk | `0x11C3820` | `0x12C1770` |
| Shared preset/file loader | `0x11BB770` | `0x12B96A0` |

The current thunk preserves the four register arguments and copies the two byte stack
arguments into the callee's argument area. The decompiled core has the expected contract:
`(object, mode, path, displayName, apply, noUndo)`. Negative modes select the file-loading
branch, with a special `-3` branch; the core passes the selected path and object to FL's
project/preset reader. This establishes the static calling contract. Actual loading, UI
readiness, third-party plugin behavior, and successful rendering still need live verification.

## TScriptDialog: class resolution is distinct from window-layout support

The Delphi short string `TScriptDialog` occurs more than once, so a raw string match alone
is insufficient. A valid VMT has a pointer to that string at `classRef-0x88`, a self pointer
at `classRef-0xC8`, instance size at `-0x80`, parent-metaclass pointer at `-0x78`, and matching
class RTTI at `-0xA8`. These relationships identify one structurally valid class in each file.

| Item | 25.2.5.5319 | 26.1.3.5570 |
| --- | --- | --- |
| Class-name short string | `0xCF3DFA` | `0xD9B01A` |
| Pointer to class-name string | `0xCF3800` | `0xD9AA30` |
| Metaclass VA | `0xCF3888` | `0xD9AAB8` |
| Metaclass RVA | `0x8F3888` | `0x99AAB8` |
| Instance size | `0x7F8` | **`0x7F0`** |
| Paint override, vtable `+0x1B8` | `0xCF46F0` | `0xD9B770` |
| Script editor pointer read by paint | `form+0x7B8` | **`form+0x7C0`** |

The current paint override dereferences the editor at `form+0x7C0` and then its `+0x78`
parent without a null check on the editor itself. The old stub list stops at `+0x7B8`.
Recovering the class address and then applying that old list would therefore be unsafe.

The new structural resolver additionally checks parent self identity, plausible instance
size, RTTI/class-name agreement, and multiple executable method addresses. It rejects
multiple valid candidates. Private PE buffers use the file's preferred pointer base;
live images use their relocated pointer base.

**Do not use this script-dialog class for generic plugin windows.** The later native-form pass
below replaces this path entirely with a separately verified `TFLBaseVectorForm`. Recovering
`HostClassRef` alone never authorizes the old script stubs or event-field writes.

## Six missing managed symbols

The initial table contained 96 symbols. Its 95 nonempty signatures each matched once in
both installed binaries; the remaining `HostClassRef` had no signature. This was not complete
API coverage: six active managed literal addresses had no table entries at all.

All six now have candidate signatures with one match in each installed image. The complete
patterns and per-build match addresses are in
[`verified-symbols-2026-09-12.json`](../native/bridge/analysis/verified-symbols-2026-09-12.json).

| Logical operation | 25.2.5.5319 VA | 26.1.3.5570 VA | Semantic cross-check |
| --- | --- | --- | --- |
| Set route active | `0x11A67F0` | `0x12A5460` | FL's named mixer scripting wrapper dispatches to this core |
| Repaint playlist | `0xDA40C0` | `0xEACB30` | Refreshes playlist controls and current arrangement; changed internal `+0xD04` becomes `+0xD2C` |
| Set clip muted | `0xF71A60` | `0x10622D0` | Sets/clears bit `0x20` of the clip's byte at `+0x13` |
| Save project wrapper | `0x10D6190` | `0x11D97E0` | Same `(songObject, path, flags, outPath)` contract; constructs output path, calls `WriteFlpFile`, and returns success byte |
| Arrangement count | `0x11FB1A0` | `0x12FB460` | Returns zero for null array, otherwise Delphi dynamic-array length at `array-8` |
| Legacy `SongArrangement` pointer | `0x14ABA80` | `0x15E16C8` | Unique corresponding RIP-relative use, at `0xB7FAE7` / `0xC438A7`; decode displacement at `+3`, instruction ends at `+7` |

The clip-muted body alone has two matches in the 2026 image. Its strengthened signature
includes the immediately adjacent `SetClipSourceRange` function prologue and has one match
in each image. Future compiler rearrangement can make this signature unavailable; it must
never select an arbitrary clone. Stack-local differences in the route and save wrappers
are wildcarded while instruction structure remains checked.

The expanded catalog also names five optional native addresses previously omitted from
its coverage count. These remain unresolved on the installed 2026 build:

- `FLui_MarkKbCapture` (2025 `0x802820`): the simple candidate signature has four current matches.
- `FLbrz_SelectTabById` (2025 `0x9AC590`): the simple candidate signature has no current match.
- `TQuickEdit_SetText` (2025 `0x74C260`): a current candidate was identified at `0x76BE40`, but
  the surrounding browser-tab layout is not verified for use.
- `TQuickEdit_Refresh` (2025 `0x74BB70`): the simple candidate signature has no current match.
- `LoadingFlag` (2025 `0x157F667`): no new current data anchor was established in this pass.

The resulting production inspector reported **107/107 resolved on 25.2.5.5319** and
**102/107 on 26.1.3.5570**, with the five gaps above explicit. These are address-resolution
counts, not an end-to-end feature certification.

The `TQuickEdit_SetText` check found another concrete layout change: current text, caret,
and flags occupy `+0x634`, `+0x63C`, and `+0x692`, compared with `+0x624`, `+0x62C`, and
`+0x682` in 2025. The current setter calls the same Unicode-string assignment routine and
uses the updated fields. A constructor or setter resolving successfully does not justify
the old browser-tab code's direct field accesses. Keep this UI path gated until its full
layout is verified.

### Plugin-menu bootstrap fields

The legacy browser-tab findings do not imply that the plugin menu's field positions changed.
Those were independently checked against Delphi's published field metadata:

| Form field | 25.2.5.5319 | 26.1.3.5570 |
| --- | --- | --- |
| `TFruityLoopsMainForm.MainMenu` | `+0x760` | `+0x760` |
| `TToolbarForm.TopToolbar` | `+0x760` | `+0x760` |
| `TToolbarForm.NewMainMenu` | `+0x878` | `+0x878` |

The current main-form metaclass is `0x1165380`, with its published field table at `0x116571B`.
The current toolbar-form metaclass is `0xDC8478`, with its field table at `0xDC87E8`.
`NewMainMenu` is the published field name at `0xDC8A53`; its preceding field entry records
offset `0x878`. The corresponding old metaclasses are `0x1060858` and `0xCB3470`.
The Python inspector reproduces these class/field relationships in its `menuForms` report.
This verifies the three named field positions used during menu discovery, not the complete
menu object's layout or every UI callback.

## MCP authoring and persistence paths checked

Focused Ghidra decompilation also inspected the uniquely matched project-open, project-path,
FLP-write, note-recorder, note-on/off, pattern-rebuild, current-arrangement, active-clip-count,
clip-source-range, and mixer-refresh functions in both binaries.

- `GetNoteRecorder` still indexes the pattern store by `patternIndex * 0xC0`, with the recorder
  pointer at `+0x28`. The current store starts at `0x193CCF0`; its recorder field base is
  `0x193CD18`. Note-on prepares a record and calls recorder initialization/insertion methods.
  Note-off scans the recorder count at `+0x14` and updates the matching note's duration.
- `GetCurrentArrangement` still returns the pointer selected by the current index from the
  arrangement array. Current playlist name core `0x12E7930` confirms track stride `0x114`
  and name offset `+0x24`.
- `SetClipSourceRange` still interprets clip type and updates source-range fields, while
  `RecountActiveClips` walks the clip collection. These are semantic spot checks, not proof
  of every object offset used by every clip operation.
- `WriteFlpFile` still accepts a path and flags, calls the project writer, and returns
  success/failure. Its surrounding code can report errors through UI paths. A matching
  function or successfully returned IPC message does not prove a valid FLP was written.

This pass found and addressed concrete version-dependent memory-layout and catalog problems.
It does **not** certify all FL internals, all future 25/26 patches, all third-party effects,
or an end-to-end unattended authoring/rendering session.

## Live startup validation

On 2026-09-12, a hidden FLMCP launch exposed a separate bootstrap race on 26.1.3.5570.
Windows recorded exception `c00000fd` (stack overflow); the crash dump repeatedly alternated
`FlBridge`'s window callback and `user32!CallWindowProcW`. Concurrent hook installation could
store the bridge callback as its own original procedure. The old visible-window filter also
reported `mainTid:0` during a hidden launch although the object-only readiness gate returned true.

The fix serializes hook installation, refuses self-chaining, carries call state per synchronous
message, handles main-window destruction, and locates `TFruityLoopsMainForm` even when hidden.
Readiness now requires that HWND; ordinary startup no longer runs diagnostic engine probes.
A synthetic Win32 test verifies 3,200 concurrent calls plus window recreation and teardown.

The follow-up baseline passed using the same FL executable/engine in an isolated writable test
installation, leaving the Program Files installation unchanged. FLMCP started process 47204,
reported available status, and embedded CPython 3.14.6 returned `os.getpid() == 47204`, tempo
140 and structured channel data. Saving and closing the owned session also succeeded. The local
companion-repository artifact is `Fl-MCP/artifacts/live-launch-20260912-170149.json`.
This is evidence for startup, direct reads and save/close; render and broader authoring remain
separate validation scenarios.

## Song-length layout correction after live authoring

The installed 26.1.3.5570 demo contained two 1536-tick playlist clips at PPQ 96 and
rendered four bars successfully. Its legacy status reader first reported zero bars;
adding the missing object-pointer dereference then exposed an integer word of
`0x40000000` instead of the expected length. Pointer indirection alone was not a
sufficient fix: the underlying object layout had changed.

A fresh focused Ghidra pass inspected `RecomputeSongLength` and
`SetCurrentArrangement` in both installed engine binaries. The generated evidence is
`artifacts/reanalysis/songlength-ghidra2025.txt` and
`artifacts/reanalysis/songlength-ghidra2026.txt`, with corresponding target TSVs and
logs. These are offline decompilations; no FL process was controlled during analysis.

| Native field | 25.2.5.5319 | 26.1.3.5570 |
| --- | --- | --- |
| Recompute function, preferred VA | `0xD37450` | `0xE3ADE0` |
| Mode/scope, 32-bit integer | `+0xB00` | `+0xB18` |
| Cached song length, 32-bit integer ticks | `+0xB04` | `+0xB1C` |
| Playlist canvas pointer | `+0xD04` | `+0xD2C` |
| Timeline-marker manager pointer | `+0xD5C` | `+0xD84` |

The actual instructions establish the types and offsets, independently of the old
source comments. In 2025, `0xD374AB` executes
`89 B3 04 0B 00 00` (`mov dword ptr [rbx+0xB04], esi`). The equivalent 2026 store at
`0xE3AE3B` is `89 B3 1C 0B 00 00` (`mov dword ptr [rbx+0xB1C], esi`). The 2026
marker load at `0xE3AE22` is `48 8B 8B 84 0D 00 00`, reading `+0xD84`.

Critically, 2026 `SetCurrentArrangement` at `0x12FCB40` uses **`+0xB04` as part of
a double-precision viewport calculation**. Its instruction at `0x12FCCC2` is
`F2 0F 59 82 04 0B 00 00` (`mulsd xmm0, qword ptr [rdx+0xB04]`). Therefore the old
`SetSongLengthToContentAsync` four-byte write to this offset corrupts half of a
floating-point field. Its marker scan also reads the obsolete `+0xD5C` offset.
The reported word matches that writer's `0x40000000` clamp; this is consistent with
the fallback accepting an implausible marker/span, but is not a trace proving which
input supplied the value. The incorrect write target itself is directly verified.

The bounded correction removes that direct cached-length/marker fallback and keeps
FL's native arrangement refresh. The normal path through `SetCurrentArrangement`
calls `0xE3ADE0` even when selecting the current arrangement; the same-index check
does not skip that recomputation. Diagnostic write hooks must also refuse the old
unprofiled field rather than expose it as a repair mechanism. Displayed content
length can instead use validated playlist spans and 64-bit arithmetic. It must not
claim to include marker-only extensions or an active playback selection.

One related inconsistency was identified: `MaxClipEndAsync` treated clip flag
`+0x13 & 0x80` as existence, while structured/legacy clip readers use the physical
collection count. The inspected `RecountActiveClips` routines (`0xF6E180` in 2025,
`0x105E970` in 2026) count that flag into a separate `+0x48` field without changing
the collection's `+0x14` count. Span reporting must include valid stored clips whose
flag is clear, including template clips, and valid channel-zero audio sources.

These findings do not enable a new raw transport-layout profile. The installed
0.1.12 framework passed the native-refresh regression on 26.1.3.5570: an appended,
resized, and deleted clip changed content length through 4 → 8 → 6 → 4 bars and
actual playback-range ends through 1535 → 3071 → 2303 → 1535 ticks at 96 PPQ.
The restored 72-note arrangement saved and rendered through MCP to a 7.5-second,
48 kHz stereo float WAV without clipping. Reopening the editor preserved both
four-bar clips and the correct textual length. This does not establish equivalent
live behavior for FL 2025 or marker-only export tails. Local MCP evidence is
`artifacts/neon-render-20260912-180733.json` in the separate Fl-MCP checkout.

## Native plugin forms: second offline pass

The exact engines above were re-opened in the disposable Ghidra projects. No running FL process,
installation, or foreground window was changed. This pass enables the generic factory only on
**25.2.5.5319 and 26.1.3.5570**; the older uninstalled 26.1.0.5530 does not inherit this layout.

| Verified item | 25.2.5.5319 VA | 26.1.3.5570 VA |
| --- | --- | --- |
| `TFLBaseVectorForm` metaclass | `0x721BA0` | `0x741740` |
| Parent `TVectorForm` | `0x7E0780` | `0x802970` |
| Native base constructor, VMT `+0x78` | `0x722040` | `0x741BE0` |
| Constructor's fixed-size initialization | `0x7E46B0` | `0x806840` |
| Native width/height constraints, VMT `+0xA8` | `0x7E60A0` | `0x808230` |
| Generic close | `0x83ACF0` | `0x862F00` |
| Native maximize toggle | `0x7E68C0` | `0x808A50` |
| `TControl.Caption` setter | `0x5D0AE0` | `0x60A490` |
| `TControl.Visible` setter | `0x5D08C0` | `0x60A270` |

Both base-form VMTs describe **0x798-byte** instances. Their published fields agree: `NewCaption`
`+0x760`, `MaxBtn +0x768`, `MinBtn +0x770`, `MenuBtn +0x778`, `CaptionizeBtn +0x780`, and
`CloseBtn +0x788`. `NewCaption` is a genuine `TNewCaption`, with FL's theme, native glyph buttons,
drag behavior and alignment helpers streamed from the base form's DFM. The resource has no
script editor, plugin-specific content, `OnCreate`, `OnClose`, `OnDestroy`, or `OnShow` handler.
Only generic `FormAlignPosition` and `FormAlignInsertBefore` events are present. Constructor
arguments follow FL's existing `Application.CreateForm` wrapper: class reference and out-instance
slot; the application supplies the native owner and allocation convention.

The constructor initially makes minimum and maximum equal to the resource's size. The constructor,
constraint override and resize hit test independently identify min width/height at `+0x6AC/+0x6B0`,
max width/height at `+0x6B4/+0x6B8`, and effective minimums at `+0x6C8/+0x6CC`. The factory sets
these verified limits for resizable plugin content. The native inner-client rectangle is obtained
through VMT `+0x310` (`0x7E62B0` / `0x808440`), intersected with the actual Win32 client rectangle.
Caption HWND bounds are mapped into that client, so FL's actual caption height and DPI layout
determine the content area. There is no fixed 24-pixel caption fallback. A separate Win32 container
supplies a content parent with origin `(0,0)`. **`form+0x11C` is Touch settings, not a content object;**
the live-failure correction below supersedes the earlier interpretation.

The native close routine chooses hide for a normal nonmodal form without an `OnClose` override.
The 2026 system-button dispatcher `0x741010` calls that routine directly; the registry additionally
consumes `WM_CLOSE`/`SC_CLOSE` as hide. Native maximize calls `SetWindowState`, preserving the form
HWND. The DFM's initially hidden minimize button is deliberately left hidden: its native action
minimizes the owning application, rather than just this plugin. Menu/captionize controls also keep
their native hidden defaults; the bridge does not invent menus or draw substitute caption buttons.

### Corrected caption identity and ABI

The historical symbol `FLwp_SetFormCaption` was misidentified. Its 2026 RTTI property entry at
`0x8577B9` names **`TApplication.Title`**, setter `0x8698A0`; the 2025 setter is `0x841690`.
Its `self+0x110` storage belongs to an application, not a form. The previous form call and raw
`form+0x110` clearing workaround have been removed. The catalogue now calls it `FLapp_SetTitle`;
the old name remains a deprecated lookup alias solely for compatibility with diagnostic clients.

The real `TControl.Caption` setter is published on `TCustomForm`, `TNewCaption`, and the toolbar
controls. It accepts a **Delphi UnicodeString**, not a raw `wchar_t*`. Both form/native-caption calls
and toolbar/Send button captions now use a scoped UTF-16 buffer with the correct 12-byte header.
Ghidra's `_UStrAsg` at `0x4133F0` confirms that a negative reference count copies the input into
Delphi-owned storage. This permits safe scope-bound buffers and Unicode captions without allocator
mixing. No active native or managed source calls the application-title setter on a form.

### Registry, tests and limits

New keyed create/bind/show/close/status commands and legacy slot zero share this factory. Each
main-thread command carries independent request/response storage. A newly created host is detached;
the registry sizes the foreign child only after the child's own UI thread reparents it and bind
verifies the parent. Close of a missing ID succeeds. Close of a still-parented child fails. Confirmed
destruction releases only that record; failed/ambiguous destruction remains visible in diagnostics,
and `WM_NCDESTROY` invalidates the matching record. Borrowed, duplicate, cross-process, and wrong-thread
surface handles are rejected. Native child resizing uses asynchronous cross-thread positioning.

Initial geometry is clamped/cascaded within the FL main window's monitor work area. Requested minima
scale from the child's original DPI to the current host DPI. Quiet shows veto activation during FL's
native visibility setter using a temporary thread-local [Windows CBT activation hook](https://learn.microsoft.com/en-us/windows/win32/winmsg/cbtproc).
Explicit user reopening can activate the form. Process termination never waits for a UI/worker thread
from `DllMain`; explicit `BridgeStop` performs orderly cleanup. Creating a native form pins the bridge
for process lifetime, so forced unload cannot strand subclass callbacks pointing at unloaded code.

The production build uses `FRUITYLINK_DEBUG=OFF`. Five native CTest groups pass, including **122 hidden
Win32 registry checks** covering multiple windows, bind isolation, close/hide, minimums, malformed
requests, legacy compatibility, failed destruction, borrowed handles, and UnicodeString headers.
Static production inspection resolves **109/109** entries on 2025 and **104/109** on current 2026;
the same five optional legacy browser/loading entries remain unavailable. Both exact builds now
report `windowEmbedding:true` for the verified generic layout.

**This pass initially provided binary and fixture verification, not live chrome validation.** The user's active FL sessions
were left untouched during analysis. Live tests of both versions, two simultaneous plugin windows, caption buttons,
focus, cross-monitor scaling, theme changes, and disable/re-enable remain pending. Docking is still
refused because FL can recreate the form HWND; no dock/rebind protocol is implemented.

Focused decompiler artifacts include `nativebase-ghidra2025.txt`, `nativebase-ghidra2026.txt`,
`verifiedold.txt`, `close2025.txt`, `close2026.txt`, `max2026.txt`, `string2026.txt`, and
`basehelpers-ghidra2026.txt` under ignored `artifacts/reanalysis`. The source inspector
`inspect_window_classes.py` reproduces VMT/field/method evidence and selected Ghidra targets.

### First live candidate: Touch object misidentified as content

The user-installed 0.1.14 candidate loaded the expected native DLL on 26.1.3.5570 and reported
`windowEmbedding:true`, `104/109` resolved symbols. At 19:38:33 the FL Agent plugin recorded
`native-content-bounds-unavailable` and correctly used its external window. Creation had passed
version/class/constructor/caption checks; failure occurred before managed bind.

The remaining error was in the factory's rectangle calculation: it still interpreted the old
script-host's `form+0x11C` pointer as a control. Exact published-property records identify this
field as **`TControl.Touch`** on both engines: getter entries `0x5C3A73` (2025) and `0x5FD423`
(2026) encode field `0x11C` and property name `Touch`. Reading that helper's `+0x90` as a
rectangle was invalid. This is a correction to the earlier analysis, not a new version offset.

The corrected profile removes `contentOffset` entirely. Its verified VMT `+0x310` getter accepts
`(form, RECT*)`; both decompilations return the native in-client border rectangle using FL's own
`+0x690/+0x694/+0x698/+0x69C` values. `TNewCaption` independently inherits through
`TQuickCustomControl → TQuickWinControl → TWinControl`, so its HWND getter and parent verification
are valid. The factory intersects the native rectangle with `GetClientRect`, then excludes the
mapped native caption. It never reads Touch settings or invents border/caption offsets.

The actual `FlWindowFactory`, rather than only its registry abstraction, now has **104 regression
checks** with injected FL calls and genuine hidden Windows host/caption HWNDs. The fixture puts a
`PAGE_NOACCESS` sentinel at the Touch pointer, verifies exact requested content dimensions, Unicode
captions and minimum sizes, and rejects missing/wrong-parent captions or invalid inner rectangles.
All **six native CTest groups** pass with the production debug pipe disabled. Factory logs include
process ID, stage-specific failure, native/Win32/caption rectangles and successful host/content
handles without logging the user's caption. Live verification of this correction follows separately.

### Live native frames: repaint correction

The installed 0.1.15 build created two genuine `TFLBaseVectorForm` windows on 26.1.3.5570 at
19:50:37–19:50:38, with content dimensions 540×800 and 1120×780. The user confirmed the native
chrome worked, then reported flickering/gray content during dragging that partially recovered
on hover. A read-only query of those logged HWNDs found host style `0x94000000` and content
style `0x56000000`: the native hosts lacked `WS_CLIPCHILDREN`, while their containers already
clipped children and siblings. Parent painting over the embedded surface is a plausible cause
of the observed gray areas; the style omission itself was directly verified.

The 0.1.16 candidate preserved existing host styles while adding `WS_CLIPCHILDREN | WS_CLIPSIBLINGS`.
The registry retained these flags when FL changed styles. It also skipped child positioning for
pure host moves and compares actual rectangles before resizing the container or foreign child.
This removes the previous unconditional asynchronous child resize on every move notification.
Successful factory logs now include host/content style bits alongside class, HWNDs and bounds.
The separate managed hosting correction coalesces normal non-erasing invalidation on Avalonia's
dispatcher instead of forcing synchronous erase/repaint within FL's resize call stack.

All six native CTest groups pass with the production debug pipe disabled: **135 registry checks**
and **106 actual-factory checks**, including ten hidden host moves with no child/container
position calls, one real resize per changed rectangle, redundant size notification suppression,
and clipping retention without removing unrelated style bits. These are automated geometry and
style regressions, not proof of rendered-pixel behavior during a real FL drag. The next live test
exposed the caption-paint regression below; the blanket host clipping change is superseded.

### Native caption regression: protect only the foreign content region

The user-installed 0.1.16 candidate made the plugin's entire native title strip and buttons
invisible while their hit regions remained active. Offline inspection explains why: FL paints
its windowed caption controls into a shared parent surface, so `WS_CLIPCHILDREN` excludes the
very areas those controls must draw. Host-wide child clipping is now removed, including the
registry's style-change override. FL's original styles remain unchanged; the separate foreign
container retains its own child/sibling clipping.

Both exact binaries use the same paint dispatch and buffering contract (addresses are preferred VAs):

| Method | FL 25.2.5.5319 | FL 26.1.3.5570 |
| --- | --- | --- |
| `TCustomWPForm` dynamic `WM_PAINT` | `0x7E4760` | `0x8068F0` |
| Delegated QuickPaint routine | `0x8038B0` | `0x825E30` |
| `TCustomWPForm` dynamic `WM_ERASEBKGND` | `0x7E4750` | `0x8068E0` |
| Imported `GetUpdateRgn` thunk | `0x424DE0` | `0x424F10` |
| Imported `BeginPaint` thunk | `0x424650` | `0x424780` |
| Imported `EndPaint` thunk | `0x424980` | `0x424AB0` |

QuickPaint reads `TWMPaint.DC` at message `+8`. Without a supplied DC, its buffered branch
captures `GetUpdateRgn` **before** `BeginPaint`, then paints its native child list into that
surface. It propagates the captured region to the buffer and preserves the destination clip
when presenting. With a supplied DC it uses that DC and omits its own BeginPaint/EndPaint pair,
but still captures the update region for buffering. The erase handler simply returns result 1.
These are direct decompiler/import-table observations, not assumptions about generic VCL behavior.

The registry now removes only the actual bound, locally visible foreign container rectangle
from the parent's update region immediately before forwarding a normal `WM_PAINT` once to FL.
This preserves the caption/border region and FL's own buffering and BeginPaint/EndPaint lifecycle.
For an already supplied paint/erase DC, scoped SaveDC/ExcludeClipRect/RestoreDC excludes only
that same foreign rectangle and restores caller state. It does not manufacture a paint DC:
[Microsoft documents](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getupdatergn)
that BeginPaint clears the update region, which would break FL's later buffered-region capture.
[ValidateRect](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-validaterect)
permits removing just this owned rectangle. No host-wide invalidation, polling, synthetic input,
paint-skipping, or synchronous child repaint is introduced. The idempotent sizing and managed
non-erasing invalidation improvements remain in place.

Focused artifacts are `paint2025.txt`, `paint2026.txt`, `paint-focus2025.txt`, and
`paint-focus2026.txt` under ignored `artifacts/reanalysis`. The checked-in class inspector now
emits dynamic paint-handler addresses alongside its other metadata.

The final production Release build passes **all six native CTest groups**, with **222 registry
checks** and **106 actual-factory checks**. The new paint fixture uses a dedicated thread on a
private Windows desktop that is never switched into view, plus a memory DIB. It captures the
update region before BeginPaint, models native caption/button child HWNDs painted into the parent,
and verifies title/button pixels remain drawn while foreign-body pixels remain untouched. It also
checks update-region consumption, exactly-once forwarding, empty internal paints, supplied-DC
clip restoration and unbound/hidden/detached pass-through. Other geometry fixtures remain hidden.
The private desktop/thread/window resources are released after both success and failure. No FL
instance or user desktop is manipulated. This is a Win32 paint-contract model, not live FL pixel
verification. Live caption/drag validation of this narrower correction remains pending;
cross-monitor DPI and FL 2025 live behavior also remain unverified.

## Marker and loop correction after the production music session

The music authoring session hit guarded native faults while adding its first marker and
clearing a loop. Both requests passed the same global indirection-table slot as the playlist
object. Offline inspection of FL's own arrangement resolver proves that each global must be
dereferenced twice; a nonzero active slot can contain a null object, in which case FL uses the
main playlist object through the fallback slot. This is independent of signature resolution:
the existing marker and selection signatures still match uniquely on both inspected binaries.

| Evidence (preferred VA unless stated otherwise) | 25.2.5.5319 | 26.1.3.5570 |
| --- | --- | --- |
| Active arrangement global | `0x14ABA80` | `0x15E16C8` |
| Fallback playlist global | `0x14AAB88` | `0x15E0730` |
| Native arrangement resolver | `0xE1EC70` | `0xD6AAD0` / `0xD79C10` |
| Set time selection | `0xD41E60` | `0xE45950` |
| Native UI selection call site | `0xB7FAE7` | `0xC438A7` |
| Add timeline marker | `0xD523C0` | `0xE55F90` |
| Marker insertion | `0xF70BA0` | `0x10613D0` |
| Marker manager offset from playlist object | `0xD5C` | `0xD84` |
| Selection start / exclusive end offsets | `0xD4C` / `0xD50` | `0xD74` / `0xD78` |

The actual selection ABI takes five arguments: playlist object, start tick, exclusive end
tick, refresh-controls byte, and notify-transport byte. The old SDK supplied only three.
Both native UI callers supply `1, 1` for the last two arguments, which the SDK now matches.
Clearing passes `-1, -1`; a normal four-bar selection at 96 PPQ is `[0, 1536)`. The native
selection stores the supplied end directly. Toolbar text can display an inclusive final tick;
that presentation should not be substituted into the exclusive-end API. No direct selection
or cached song-length writes are needed.

Marker insertion still takes six arguments and copies a Delphi Unicode string into its own
storage. The marker manager's first pointer is a Delphi dynamic array, whose signed 64-bit
length lives at data minus eight. Records are `0x34` bytes with the tick at `+0` and name
pointer at `+8` on both versions. The old marker reader incorrectly reused the 2025 manager
offset on 2026. The new exact-build `timelineLayout` capability supplies these fields through
the scanner abstraction. Unknown builds report `null`, and both direct managed calls and
scripting capabilities refuse these operations before accessing native state.

Focused decompilation artifacts under ignored `artifacts/reanalysis` are `timeline2025.txt`,
`timeline2026.txt`, `markerdata2026.txt`, `timeline-more2026.txt`, and
`selection-range2026.txt`. The last includes the 2026 range application at `0xE50550` and
transport publication at `0x11D1790`; selection notification at `0x11D1AA0` invokes this path.
The engine hashes at the start of this report remain unchanged.

The required SDK quality gate passes **313 tests**, including **154 FlStudio tests**, with
zero compiler/analyzer warnings and real embedded Python integration enabled. New tests
exercise both timeline layouts, Unicode string ownership, double-pointer fallback, five-argument
set/clear calls, idempotent clear, 64-bit count validation, unavailable capabilities, and no
automatic retry after a native fault. The production native Release build has debug transport
disabled and passes all **six CTest groups**. Live marker/loop validation of this correction
remains a separate installation check; the completed user composition and render were preserved.

### Live loop validation exposed a separate status-reporting defect

The installed 0.1.18 fixture successfully created and read back both markers, including the
Unicode name. Setting a loop to beats 4..12 at 96 PPQ populated both the playlist selection
and actual native range with `384, 1152`. The active and main playlist pointers agreed.
Seeking to tick 1104 and playing for 0.65 seconds at 120 BPM then reached tick 459, confirming
that playback wrapped into the requested loop. Clearing also completed without a native fault.

The old `GetSongStateAsync` still printed `playRange=[0..1535]` because it read the toolbar
slider's full seek bounds. This was a reporting error, not a failure of the corrected setter.
The slider's `AdjustParams` routine at 2025 `0xCBBE50` / 2026 `0xDD0630` independently reads
the actual transport range and maps it into its broader slider domain to draw the indicator.
Decompiler evidence is in `song-slider2025.txt` and `song-slider2026.txt` under ignored
`artifacts/reanalysis`; the narrow live probe is recorded in the 0.1.18 regression artifacts.

Two new `SK_DataRef` symbols share a unique selection-refresh signature, matched once on each
binary at 2025 `0x10CE270` / 2026 `0x11D1AA0`:

| Symbol | Displacement / instruction-end offsets | 2025 global | 2026 global |
| --- | --- | --- | --- |
| `TransportRangeStart` | `10 / 14` | `0x14A95F8` | `0x15DF0A0` |
| `TransportRangeEnd` | `19 / 23` | `0x14ABB38` | `0x15E1798` |

Each resolved global holds a pointer to a signed 32-bit bound. Both status readers now use
these globals, validate a nonnegative start and a larger exclusive end, and display the final
included tick (`end - 1`) for compatibility. The toolbar continues to supply only the playhead.
Unknown or invalid range data displays `[-1..-1]`, without a misleading slider-bound fallback.
The functioning marker and loop setters are unchanged. Regression cases cover selected/full-song
ranges, a single tick, the largest valid end, null/unresolved/invalid data, and cancellation.
Native tests also decode the production data-reference recipes against a controlled image.
The debug-disabled Release build passes all six native CTest groups; actual binary inspection
now resolves **111/111 symbols on 2025** and **106/111 on 2026**, with the same five optional gaps.

## Ordinary mixer insertion and physical Current identity

Read-only examination of the live project found native count 18, while physical slot 17 had
ordinary type 1 and the default name `Insert 17`. The old list header incorrectly labeled this
slot Current. Both inspected binaries instead special-case Current at **physical index 501**.
The native count is a cardinality containing Master, ordinary inserts, and Current; its
ordinary physical range is `1..count-2`. Higher preallocated ordinary slots can contain default
names and routing fields without being exposed as active inserts. The `+0x19` in-use byte is
not a reliable visibility test, so enumeration follows the native count/range, not that flag.
The root's read-only probe confirmed type 0 at Master, type 1 at 16 and dormant 17, and type 2
at 501. The saved music project was not changed by this probe.

| Native method / data | 25.2.5.5319 | 26.1.3.5570 |
| --- | --- | --- |
| Published AddOneMixerTrackMenuClick | `0x11ABFC0` | `0x12AAC50` |
| Published AddTrackAfterSelectedBtnClick | `0x11AC6A0` | `0x12AB330` |
| Add-after helper | `0x11AB9F0` | `0x12AA680` |
| Scalar insert routine | `0x11A7B30` | `0x12A6790` |
| Internal count setter | `0x11805F0` | `0x127EEE0` |
| Track array global | `0x14A7EB0` | `0x15DD808` |
| Cardinality global | `0x14A9850` | `0x15DF300` |

The scalar insertion ABI is `(insertIndex, numberOfTracks, flags)`. The Add-after UI converts
`after` to `insertIndex = after + 1`; append uses `count - 1`. It passes `0x0B` for append and
`0x0F` for explicit placement, adding dock inheritance in the latter case. These flags retain
FL's undo and selection behavior. Insertion after Master is valid: the native dock-source branch
explicitly handles a predecessor below 1. The internal count setter clamps to **3..502**, so
the supported maximum is 500 ordinary inserts and at least one ordinary insert remains.

The native routine grows through FL's own count setter, builds a permutation across all 502
physical entries, and applies FL's remapping of track/FX state, routing, channel routes, and
playlist links. It also performs native notifications and UI refresh. The SDK never writes the
count or copies mixer records itself. A unique 66-byte `FLmx_InsertTracks` signature resolves
the scalar routine on each exact binary. Focused published-metadata and decompiler artifacts
are `add-mixer2025/2026.txt`, `add-mixer-core2025/2026.txt`, and `mixer-count2025/2026.txt`
under ignored `artifacts/reanalysis`.

`mixer_add` executes validation, insertion, and postvalidation in one existing synchronous
FL-main-thread dispatch. This avoids a managed read/call gap when computing append placement.
The command requires the exact verified mixer profile, resolved native symbols, plausible count,
capacity, ordinary/Master predecessor type, and Current's physical type before mutation. It returns
the actual created index only after the count increases by one and that index has ordinary type.
Faulted or unconfirmed calls explicitly report possible mutation without retry or synthetic rollback.
The managed wrapper preserves that guidance even for malformed or truncated responses, and existing
scripting cancellation continues to drain the underlying native command.

`QueryMixerTracksAsync` returns Master and ordinary inserts with physical indices and kinds;
Python enumeration uses this query. Bounds on mixer fields, scalar controls, effects, sends,
and channel destinations exclude Current and dormant slots. Native cardinality remains available
separately. As documented for other structured queries, enumeration is read-only but not an atomic
snapshot; clients must requery after insertion because later indices can change.

Verification: **349 SDK tests** pass with zero compiler/analyzer warnings and real embedded Python
tests enabled. All **seven native CTest groups** pass with production debug transport disabled,
including **110 insertion checks** using the actual native backend with injected memory/symbol/call
dependencies. These exercise both strides, append/after-Master/middle insertion, exact flags,
capacity, invalid topology/pointers, and ambiguous outcomes without retry. Actual binary inspection
resolves **112/112 symbols on 2025** and **107/112 on 2026**, with the same five optional gaps.
Live insertion, preservation, save/reopen and render validation remains a separate installer check.

## Reproduction

The read-only standard-library Python inspector and focused Ghidra script live in
[`native/bridge/analysis`](../native/bridge/analysis/README.md). Full generated JSON,
disassembly/decompilation, Ghidra projects, and logs stay under ignored `artifacts/reanalysis`.
Only the analysis tooling, small signature evidence file, and this report belong in source.
