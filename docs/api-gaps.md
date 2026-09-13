---
search:
  boost: 0.3
---

# Practical editing and mixing API gaps

Audit snapshot: September 12, 2026. This review compared the shared C# contract, its
Python facade, the MCP adapter, and existing reverse-engineering notes. It did not
run FL, test a new native call, or change production code. Mixer-track creation is
being implemented separately; the entries below describe the inspected baseline,
not a claim that work in progress has already shipped.

Subsequent verification: installer 0.1.20 adds native mixer insertion and typed
`QueryMixerTracksAsync` / `fl.mixer.list()`. Active-project bounds now apply to
mixer edits, and Current is identified by native metadata at physical slot 501,
not `count - 1`. FL 2026 live append/insert, routing/effect preservation,
save/reopen and rendering passed. The baseline entries below retain the original
audit context; mixer creation, typed track listing and those bounds defects are
addressed. Typed sends/FX state and explicit send disconnection remain open.

Installer 0.1.21 also adds guarded automation creation/initial linking, placement
and point editing; see [automation verification](automation-clips.md). The native
helper rejects ordinary channels and invalid counts before edits and uses exact
2025/2026 layouts. The unselected-clip slice/duplicate rejection below is removed
and regression-tested. Additional destinations on an existing automation channel,
typed send state and faithful per-channel audio capture remain follow-up work.

`fl.ops` exposes the generated shared contract, and MCP's `fl_execute_python` can
use that contract. A missing dedicated MCP tool is therefore **not** automatically
a missing capability. Prefer implementing a capability once in the shared SDK,
adding typed Python access, and leaving MCP responsible for session ownership and
workspace policy.

Evidence levels used below:

- **Implemented:** a current code path exists. This alone does not prove every
  input, channel type, or FL build works live.
- **Existing profile:** the current native profile supplies verified layout fields
  consumed by the SDK. Extend that profile only with independently checked fields.
- **Legacy static recipe:** an older Ghidra note supplies a starting point, usually
  from the 2025 engine. Recheck signatures, arguments, units and side effects on
  each supported exact build before exposing a new write operation.

Paths beginning `parent/re/` refer to the maintenance workspace's older evidence
collection outside this SDK checkout. `Fl-MCP/` denotes the separate MCP repository.
Some old notes contain superseded assumptions; they are evidence leads, not an ABI.

## Correctness prerequisites

These deserve attention before broadening the menu coverage.

### Saved/live note-count reconciliation

At the end of the installer verification, Glass Satellites' live query reported
5,076 notes while both its original FLP and the safety snapshot serialized 5,341
notes across 144 patterns. The entire 128,184-byte note payload is byte-identical
between the two saved files (SHA256
`5f9076485070aa04163cf69f40efed434c9299eb28c05c5e0ae6c82684738a9c`).
The earlier captured score also contains 5,341 distinct note records, so simple
duplicate-onset removal does not explain the difference.

Saved note data is intact. Query/enumeration behavior, load-time interpretation,
and subsequent live editing have not been distinguished. Reproduce with a
disposable copy before changing the query or project data; the user's reopened
instance was playing and was left untouched. This is a follow-up observation,
not evidence that the installer removed notes.

| Priority | Concrete issue | Evidence and required outcome |
| --- | --- | --- |
| High | Automation point writes do not establish that the target is an Automation Clip. `AutomationEnvAsync` accepts any non-null channel envelope container. | [Channels implementation](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Channels.cs), `AutomationEnvAsync`, `AddAutomationPointAsync`, `DeleteAutomationPointAsync`; `parent/re/11-automation-clips.md` explicitly notes that ordinary channels also have envelope containers. Add verified channel-kind/layout checks before treating an envelope as a clip. A pointer being non-null is insufficient. |
| High | `AddAutomationPointAsync` converts a negative or greater-than-4000 point count to zero, then resizes and rewrites the array. It also silently skips recomputation if the vtable pointer is not in FLEngine. | Same implementation. Reject invalid or unsupported state before mutation; do not turn an unrecognized/large curve into a one-point curve. Preflight the complete operation, including recomputation. |
| High | Existing slice/duplicate methods reject valid unselected clips. | [Playlist implementation](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Playlist.cs): `SliceClipAsync`, `DuplicateClipAsync` and `FindClipAddrAsync` test flag `0x80`, while `ListClipsAsync` documents that this is selection-style state, not existence. `QueryClipsAsync` already includes such clips. Use one validated clip-identity rule and regressions for template/unselected clips. |
| High | Mixer bounds and the identity of the final track are inconsistent across surfaces. | See the bounds section below. Validate against the selected project's actual model; do not derive an insert's type from its ordinal position. |
| Medium | Channel event addressing assumes the displayed index equals the record tag. | [Params implementation](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Params.cs), `ChannelParamId` and channel volume/pan/pitch/mute/routing methods, versus `ResolvePluginAsync`, which reads the channel's stored record-event ID. The existing code itself limits the assumption to unreordered projects. Reordered/deleted-channel behavior remains an unverified follow-up; verify identity before changing these write paths. |

## Ranked capability gaps

### 1. Explicit routing state and typed mixer inspection

**Available:** track volume/pan/mute/name; channel-to-mixer routing; `SetMixerSendAsync`
enables a send and sets its level. `ListMixerEffectsAsync` includes human-readable
send descriptions.

**Missing:** explicit enable/disable, exclusive routing to a bus, a clear sidechain
contract, and typed track/send/FX state. `IFlStructuredQuery` has no mixer query.
`fl.mixer` iterates by count and reads names individually; effects and sends are
primarily text. `send_to(destination, 0)` still activates the connection because
the implementation always calls the routing core with enable `1`.

**Impact:** a script cannot reliably replace a direct master route with a subgroup
route, distinguish a zero-level connection from a disconnected route, or verify a
complete mix graph without parsing display text.

**Starting point:** existing profile fields already describe send records, active
flags, levels, track names/types, and FX slot objects. Typed readback can reuse
those readers. The existing `FLmx_SetRouteActiveCore` binding is the route-write
lead, but disabling can invoke a sidechain confirmation according to the legacy
recipe; inspect that behavior and its flags before promising unattended disconnect.
Return actual active/level state after a change.

Evidence: [Mixer implementation](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Mixer.cs),
`SetMixerSendAsync`/`DescribeSendsAsync`; [structured queries](../src/FruityLink.Core/Abstractions/IFlStructuredQuery.cs);
[Python mixer](../python/src/fruitylink/mixer.py); `parent/re/generated/controls-mixer.md`,
“Routing / sends” (`FLmx_SetRouteActiveCore`, legacy address `0x11A67F0`).

### 2. Mixer FX bypass, wet level, and complete built-in EQ controls

**Available:** load/replace/clear an effect, enumerate hosted-plugin parameters,
set a parameter, and set one of three mixer EQ gains. `clone_type_to` correctly
states that it copies the effect type only.

**Missing:** per-slot enabled/bypass and wet mix with getters; built-in EQ gain
readback, frequency and bandwidth; stereo separation; mixer solo with explicit
state. These are mixer/wrapper controls, so enumerating a plugin's own parameters
does not substitute for them.

**Impact:** effect A/B comparisons, parallel processing, and repeatable mixer EQ
edits require UI actions or an effect-specific workaround.

**Starting point:** the legacy mixer map documents command IDs for slot enable
(`base(track,slot)+0x70001f00`) and wet level (`+0x70001f01`), EQ gain/frequency/
bandwidth (`+0x70001fd0`, `+0x70001fd8`, `+0x70001fe0` plus band), and stereo
separation (`+0x70001fc2`). These are promising shared command-bus additions, not
new raw struct writes. Confirm the getter/setter scale and flags on both builds;
the normalized wrapper scale must not be confused with the raw native scale.

Evidence: [shared contract](../src/FruityLink.Core/Abstractions/INativeFlControl.cs),
[Python mixer](../python/src/fruitylink/mixer.py); `parent/re/generated/controls-mixer.md`,
“Operation map” and “Track built-in param map”.

### 3. Mixer insert creation, deletion and reordering

**Available:** operate on existing tracks. Add-track support is active work in this
revision and should be documented from its final contract and tests.

**Missing in the inspected baseline:** insert before/after a chosen track, delete
or trim unused inserts, reorder tracks/groups, and preserve or return updated
routing identities after structural changes. Renaming an existing track does not
create an insert.

**Impact:** small templates cannot grow into a full mix without UI assistance;
large projects cannot be reorganized predictably. Reordering changes the meaning
of retained index references and needs explicit remapping/requery semantics.

**Starting point:** the legacy UI catalog names add/insert/delete handlers at
`0x11ABFC0`, `0x11AC6A0`, `0x119FF60`, `0x119FF50` and trim at `0x11AF6D0`.
These are menu handlers, not verified headless function contracts. Follow their
model operations and inspect confirmation, active-selection and routing effects;
do not invoke a guessed menu object or mutate the track array manually.

Evidence: `parent/re/ui-win-mixer.md`, section 9; the shared contract and Python
mixer module above. Gate any new operation on its native profile capability.

### 4. Automation creation, linking, placement and curve editing

**Available:** list/query points, add a point and delete a point on an existing
automation channel. Text descriptions attempt to identify targets. Adding an
“Automation Clip” generator alone does not establish a parameter link.

**Missing:** create a clip from a typed parameter target, link/unlink/reassign
targets, typed target readback, place that channel clip in the playlist, update
existing point time/value/tension, and select a curve shape. Current point creation
always writes curve type zero.

**Impact:** a filter sweep, send throw or ducking envelope cannot be authored from
scratch through a coherent public operation, despite the presence of point APIs.

**Starting point:** `parent/re/11-automation-clips.md` records creation via
`FLac_CreateAutomationClipForEvent` (`0x108A1A0`) and linking via
`FLac_AddTargetLink` (`0xE8DEB0`). Their object/argument contracts, target descriptor,
playback restrictions and version mappings need verification. The current target
registry reader explicitly treats its root as ambiguous and probes alternatives;
that uncertainty must not be reused for writes. Resolve the correctness
prerequisites above before expanding this surface.

Evidence: [Channels implementation](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Channels.cs),
`AutomationLinkRegistryAsync` through `DeleteAutomationPointAsync`;
[Python automation](../python/src/fruitylink/automation.py).

### 5. Audio/channel clips and playlist organization

**Available:** place pattern clips; read, move, resize, mute, delete, slice and
duplicate clips; bulk edits; track naming/color/mute/collapse/selection; full
arrangement add/clone/rename/delete/select. Slice/duplicate still have the
selection-state defect noted above.

**Missing:** explicit audio/automation-channel placement, source trim/in-out,
stretching and fades, “make unique”, and playlist track reorder/group/link modes.
Loading a WAV into a Sampler channel plus writing a note is not the same operation
as placing an audio clip with a source window. Typed clip records do not expose
source trim or a persistent identity for later structural edits.

**Impact:** arranging vocals, long risers, stems and repeated edited audio regions
still requires workarounds; organizing linked channel/mixer/playlist tracks is
not expressible as one verified operation.

**Starting point:** the existing private `InsertClipRawAsync` is shared groundwork.
`parent/re/generated/playlist-A-primitives.md`, section 6, describes channel
source IDs and source-range conversion through `FLpl_SetClipSourceRange`; verify
audio units and preservation of existing trims before adding public operations.
`parent/re/generated/insert-link-namesync.md` describes link fields and corrects
swapped fields in an older playlist note, so use a verified profile rather than
copying either offset table into another consumer.

Evidence: [Playlist implementation](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Playlist.cs),
[Python playlist](../python/src/fruitylink/playlist.py).

### 6. Complete pattern/channel management and plugin state

**Available:** select/create/name/clear a pattern, edit/delete notes, and clone
its complete note records. Add/name/route channels and replace samples. Load and
clear mixer effects.

**Missing:** pattern deletion/reorder/merge and full pattern cloning including
automation/name/color; channel delete/reorder/clone; state-preserving FX reorder,
cross-track copy and explicit preset load/save. `ClonePatternAsync` explicitly
copies notes only. `CloneMixerEffectAsync` loads another instance by name and
therefore does not preserve its current settings.

**Impact:** variations can lose automation or sound design, mistakes cannot be
cleanly removed, and project cleanup/organization requires the UI. Also,
`CreatePatternAsync` selects the first empty slot; it does not reserve multiple
empty objects. Do not retain several successive empty creations as if they were
guaranteed distinct; populate and requery, or add an explicit reservation contract.

**Starting point:** pattern slots and materialization are described in
`parent/re/generated/controls-patterns.md`; channel structural operations are only
catalogued as UI paths in `controls-channelrack.md`. The legacy
`preset-C-files.md` describes instance dispatcher opcode `0x12` with UTF-8 paths
for state loading. These are leads requiring renewed version/ABI validation,
especially when plugin state, audio threads and undo are involved.

Evidence: [Patterns implementation](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Patterns.cs),
`CreatePatternAsync`/`ClonePatternAsync`; [Mixer implementation](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Mixer.cs),
`CloneMixerEffectAsync`; [Python patterns](../python/src/fruitylink/patterns.py).

### 7. Save/export settings and reversible editing

**Available:** shared SDK open/new/save/save-as/save-copy/new-version operations;
an interactive export-dialog opener. MCP adds verified snapshot save and managed
CLI WAV rendering, with cancellation and owned-process handling.

**Missing:** typed render settings/readback (sample rate, bit depth, tail mode,
selection/song range, stems, other formats), native render completion as a shared
SDK capability, sample-collection/project packaging, and a grouped native undo
contract for script edits. MCP rendering intentionally refuses attached sessions
and closes its disposable editor; this is ownership policy, not a missing
forwarder to fix by calling the shared lifecycle methods behind its back.

**Impact:** an attached user cannot request a verified in-session bounce with
explicit settings, and ordinary batches cannot be treated as one undoable edit.
Current MCP renders use saved FL export settings. A snapshot is recovery evidence,
not an automatic rollback or a guarantee that external samples are bundled.

**Starting point:** `parent/re/12-controls-harvest.md`, “Render / export”, records
a render config and worker path but explicitly leaves initialization/live checks
open. Do not ship those offsets as a ready recipe. `parent/re/generated/controls-global.md`
lists native undo/history entry points; `undo-redo-inverse-design.md` is a design,
not an implemented transaction system.

Evidence: [Project implementation](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Project.cs),
[Python project](../python/src/fruitylink/project.py);
`Fl-MCP/src/FlMcp.Server/ManagedSession.cs` (`RenderAsync`),
`Fl-MCP/src/FlMcp.Plugin/ScriptingSessionPolicy.cs`.

## Bounds, identity and capability reporting

The current interface/generated docs still describe routing as `0..125` and a
127-track mixer. MCP repeats that in `FlTools.cs` and **enforces** `0..125` in
`CommandDispatcher.RegisterMixing`/`ValidateParameterTarget`. Conversely, shared
`SetChannelFxRouteAsync` clamps to `0..500`, while command-bus mixer volume/pan/EQ
methods do not first validate the project's current track count. These are
inconsistent contracts, not interchangeable descriptions of FL's dynamic capacity.

`ListMixerTracksAsync` formats `count-1` as Current and `count-2` as the last insert.
`GetMixerTrackNameAsync` instead uses the profile's actual type/name fields. The
reported live case returned **Insert 17** from effect inspection when the count
was 18; this review did not independently operate that FL process. Do **not** call
that slot Current, infer insert capacity from this count alone, or treat a fixture
that labels its last track Current as proof of live semantics. Return typed model
roles and verified bounds, and requery after insert/delete/reorder.

Evidence: [shared interface](../src/FruityLink.Core/Abstractions/INativeFlControl.cs),
[Params](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Params.cs),
[Mixer](../src/FruityLink.FlStudio/Inject/FlInjectBridge.Mixer.cs),
[generated Python operations](../python/src/fruitylink/operations.py), and the MCP
files above. Pattern `1..999` and playlist `1..500` limits have separate model
evidence; changing mixer capacity is not a reason to remove those bounds blindly.

`fl.capabilities()` reports known layout availability and unavailable operations.
It does not establish that every channel is a hosted plugin or Automation Clip,
nor does it currently describe every operation's individual symbol/layout needs.
New operations should carry the appropriate native capability requirements and
fail before mutation on unsupported builds. The built-in Sampler parameter
diagnostic is deliberate: no verified Sampler envelope interface was established
by finding that 3xOsc exposes hosted-plugin parameters.

Evidence: [availability policy](../src/FruityLink.Scripting/OperationAvailability.cs),
[dispatcher](../src/FruityLink.Scripting/FlScriptingDispatcher.cs), and
[Sampler regression cases](../tests/FruityLink.FlStudio.Tests/SamplerParameterTests.cs).

## Suggested implementation order

Finish the active add-track work and align bounds/readback first. Then add typed
mixer state and explicit route state, followed by FX bypass/wet controls. Address
automation identity/count validation and clip selection handling before exposing
automation creation or audio placement. Full state cloning, structural reordering
and in-session rendering require separate version-verified work rather than a
larger list of thin MCP tools over incomplete native behavior.
