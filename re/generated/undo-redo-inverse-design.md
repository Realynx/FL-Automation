# Undo/Redo Redesign — Inverse-Operation Command Journal

Status: design only (no code changes). Author: analysis pass 2026-07-02.
Scope: replace the "restore whole `.flp`" undo model with a per-operation **inverse
journal** persisted to disk, while keeping the `.flp` per commit for crash recovery +
fallback.

---

## 1. Problem & directive

Today (`JsonProjectVersionControl`) every commit saves a full-state `.flp`
(`SaveCopyAsync`) and Undo/Redo/Restore **reopen a `.flp`** via `OpenProjectAsync`.
That is correct but heavy: it reloads the entire project, disrupts FL, and can't be
granular.

Directive:

1. **Inverse-operation model.** Undo must call FL's *setter* functions to put back the
   specific values each AI operation changed — not reopen a project. So we must capture
   the **BEFORE state of exactly what changed**, per operation.
2. **Persist the journal to the file system**, loaded **on demand** for undo/redo, and
   **not held in RAM** (FL is already a memory hog). Layout under
   `%APPDATA%\FLAutomate\versions\project\{session}\`.
3. **Still grouped by AI work-unit** — one commit per mutating agent turn (unchanged
   granularity), not per tool call.

---

## 2. What exists today (verified in code)

| Concern | Where | Note |
|---|---|---|
| Commit hot path | `JsonProjectVersionControl.CommitAsync` | 1 native `SaveCopyAsync` → `{commitId}.flp` + append row to `index.json` |
| Undo/Redo/Restore | `RestoreCoreAsync` | safety-backup, then `OpenProjectAsync(target.flp)` — full reopen |
| Commit DAG | `ProjectCommit` (`ProjectVersion.cs`) | `Id`, `ParentId`, `FlpBackupPath`, `StateJsonPath`, `Operations` (strings), `Trigger` |
| Turn boundary → 1 commit | `ProjectVersionCoordinator.OnTurnCompletedAsync` | gated on a **mutating** tool having fired (`IsMutating`) |
| Tool-fired signal | `FlAgent.ToolInvoked` → `ToolCallFilter` | fires per tool with `ToolCallInfo(Function, Arguments)` — **name only, no before-state** |
| Op audit sink | `IOperationAuditSink` / `OperationAuditSink` (`FlOperation`) | in-memory queue, **drained only for the commit label**; today only `MusicTheoryPlugin` records to it; `NativeControlPlugin` does **not** |
| The mutating tools | `NativeControlPlugin` (`INativeFlControl`) | full surface enumerated in §4 |
| Paths | `StoragePaths` | `ProjectVersionDir`, `ProjectFlpBackup`, `ProjectVersionIndexFile`, `ProjectStateFile` |
| UI | `VersionControlViewModel` + `AgentVersionControlGateway` | Undo/Redo/Jump/Restore-backup all funnel to backend `UndoAsync`/`RedoAsync`/`RestoreAsync` |

### 2.1 The decisive enabler — a symmetric GET already exists

Every value-set setter in `FlInjectBridge` routes through **one** primitive:

```
SetParamAsync(cmdId, value)  → bus SET (flags 0x11)
GetParamAsync(cmdId)         → bus GET (flags 0x2)   // live-verified
```

`SetTempo`, `SetMaster*`, `SetShuffle`, `SetMixerVolume/Pan/FxParam`, `SetChannelVolume/
Pan/Pitch/Muted/FxRoute`, and plugin params are all `SetParamAsync(cmdId,…)` calls.
Therefore the **BEFORE value of every command-bus scalar is already readable today**
with `GetParamAsync(sameCmdId)` — *no new RE*. The bridge even already exposes typed
`GetMixerVolumeAsync` / `GetChannelVolumeAsync` (just not on `INativeFlControl`).

Consequence: the whole **(A) value-set** class is Phase-1 reachable by surfacing
symmetric getters (thin wrappers over `GetParamAsync`) on the interface. New RE is only
needed for a handful of **create/delete inverse primitives** (§4 class B gaps).

---

## 3. Invertibility classes

- **(A) VALUE-SET invertible** — a getter reads the OLD scalar; a setter restores it.
  Undo = `set(old)`, Redo = `set(new)`.
- **(B) CREATE↔DELETE invertible** — inverse of add is delete (and vice versa). Undo of a
  *create* = delete-by-identity; undo of a *delete* = re-create from a captured spec.
- **(C) NO CLEAN INVERSE** — no getter and/or no inverse (plugin internal state, effect
  param-state on reload, whole-project ops). These commits keep the `.flp` fallback.

---

## 4. Coverage table (every mutating tool)

Legend — **Read-before** = how the BEFORE-state is captured; **Restore** = the inverse
call. "GetParam ✓" = already works via bus GET, no RE. "list ✓" = value parseable from an
existing `List*`/`Get*State` string. "**RE**" = needs a new native primitive.

### (A) Value-set — Phase 1 (all read-before via GetParam or existing lists)

| Tool | Class | Read-before (OLD) | Restore (setter) | New RE? |
|---|---|---|---|---|
| `native_set_tempo` | A | `GetTempoAsync` ✓ | `SetTempoAsync` | no |
| `native_set_master_pitch` | A | `GetParam(CmdMasterPitch)` ✓ | `SetMasterPitchAsync` | no (surface getter) |
| `native_set_shuffle` | A | `GetParam(CmdShuffle)` ✓ | `SetShuffleAsync` | no |
| `SetMasterVolume` (UI/probe) | A | `GetParam(CmdMasterVolume)` ✓ | `SetMasterVolumeAsync` | no |
| `native_set_mixer_volume` | A | `GetMixerVolumeAsync` ✓ | `SetMixerVolumeAsync` | no |
| `native_set_mixer_pan` | A | `GetParam(mixerPanId)` ✓ | `SetMixerPanAsync` | no (surface getter) |
| `native_set_mixer_send` | A | `GetParam(sendId)` ✓* | `SetMixerSendAsync` | verify send cmd is GET-able |
| `native_set_mixer_eq_gain` | A | `GetParam(eqBandId)` ✓* | `SetMixerEqGainAsync` | verify EQ cmd is GET-able |
| `native_set_channel_volume` | A | `GetChannelVolumeAsync` ✓ | `SetChannelVolumeAsync` | no |
| `native_set_channel_pan` | A | `GetParam(chanPanId)` ✓ | `SetChannelPanAsync` | no |
| `native_set_channel_pitch` | A | `GetParam(chanPitchId)` ✓ | `SetChannelPitchAsync` | no |
| `native_set_channel_muted` | A | `GetParam(chanMuteId)` ✓ | `SetChannelMutedAsync` | no |
| `native_route_channel_to_mixer` | A | `GetParam(chanFxRouteId)` ✓ | `SetChannelFxRouteAsync` | no |
| `native_set_channel_plugin_params` | A | `ListPluginParamsAsync` (`index: name = value`) list ✓, or `GetParam(chanParamId)` | `SetPluginParamAsync` | no |
| `native_set_mixer_plugin_params` | A | `ListPluginParamsAsync(track,slot)` list ✓ | `SetPluginParamAsync` | no |
| `native_set_track_name` | A | `ListPlaylistTracksAsync` list ✓ (default→empty if unlisted) | `SetTrackNameAsync` | no |
| `native_set_track_color` | A | `ListPlaylistTracksAsync` list ✓ | `SetTrackColorAsync` | no |
| `native_set_track_mute` | A | `ListPlaylistTracksAsync` list ✓ | `SetTrackMuteAsync` | no |
| `native_set_track_collapsed` | A | `ListPlaylistTracksAsync` list ✓ | `SetTrackCollapsedAsync` | no |
| `native_set_song_mode` | A | `GetSongStateAsync` list ✓ | `SetSongModeAsync` | no |
| `native_rename_arrangement` | A | `ListArrangementsAsync` list ✓ | `RenameArrangementAsync` | no |
| `native_move_clips` | A (multi) | `ListClipsAsync` → per-clip `start`,`track` ✓ | `MoveClipsAsync(old)` | no (address by identity, §7) |
| `native_resize_clips` | A (multi) | `ListClipsAsync` → per-clip `length` ✓ | `ResizeClipsAsync(old)` | no |

*`native_set_mixer_send` / `native_set_mixer_eq_gain` write via `SetParamAsync`, so GET
*should* mirror; flagged to live-verify the specific cmdIds are GET-readable before
relying on them (else read the raw fixed-point at the routing-matrix / EQ address, both
already RE'd in `controls-mixer.md`).

### (B) Create↔Delete

| Tool | Class | Inverse today? | Read-before / capture | Gap |
|---|---|---|---|---|
| `native_add_pattern_clips` | B create | **`DeleteClipsAsync` EXISTS** | record placed `(pattern,track,start)` identities | Phase 2: resolve identity→slot via `ListClips` at undo |
| `native_delete_clips` | B delete | re-add via **`AddPatternClipsAsync` EXISTS** (pattern clips only) | snapshot `ListClips` rows being deleted | audio/automation clips can't be re-added → (C) `.flp` |
| `native_duplicate_clip` | B create | `DeleteClipsAsync` | record new clip identity (dup goes after source) | Phase 2 identity resolve |
| `native_mute_clips` | A/B | unmute is inverse **but** needs per-clip prior state | `FLpl_GetClipMuted 0xF71A50` (RE'd, not surfaced) | Phase 2: surface `GetClipMuted` |
| `native_clear_pattern` | B delete-all | re-add via `AddNotesAsync` EXISTS | **snapshot `GetNotesAsync(pattern)` first** | Phase 2 (bounded read; big patterns costly) |
| `native_add_note` / `native_add_notes` | B create | ✗ no delete-note-by-identity | record note specs added | **Phase 3 RE: `delete_note(pattern,chan,key,startTick)`** |
| `native_create_pattern` | B create | ✗ no delete-pattern (Clear ≠ delete slot) | record new pattern index | **Phase 3 RE: `delete_pattern(index)`** |
| `native_add_channel` | B create | ✗ no delete-channel | record new channel index | **Phase 3 RE: `delete_channel(index)`** |
| `native_add_sample_channel` | B create | ✗ no delete-channel | record new channel index | **Phase 3 RE: `delete_channel(index)`** |
| `native_add_marker` | B create | ✗ no delete-marker | record `(tick,name)` | **Phase 3 RE: `delete_marker(tick/idx)`** |
| `native_add_automation_point` | B create | **`DeleteAutomationPointAsync` EXISTS** | resolve added point's index by `(time,value)` via `ListAutomationPoints` | Phase 2 identity resolve |
| `native_delete_automation_point` | B delete | re-add via **`AddAutomationPointAsync` EXISTS** | snapshot the point `(time,value,tension)` before delete | Phase 2 |
| `native_make_arrangement` | B create | **`DeleteArrangementAsync` EXISTS** | record returned index | Phase 2 |
| `native_clone_arrangement` | B create | **`DeleteArrangementAsync` EXISTS** | record returned index | Phase 2 |
| `native_delete_arrangement` | B delete | ✗ deep content lost | — | (C) `.flp` fallback |
| `native_replace_channel_sample` | A-ish | replace back with OLD path | read old sample path (`GetChannelPluginAsync` may carry it) | Phase 2: needs reliable sample-path read |
| `native_add_mixer_effect` | B create/replace | **`RemoveMixerEffectAsync` EXISTS** for empty-slot case | `ListMixerEffectsAsync` (was slot empty?) | replace-over-existing loses old param state → (C) `.flp` |
| `native_remove_mixer_effect` | B delete | re-add via `AddMixerEffectAsync` — **but param state lost** | `ListMixerEffectsAsync` (which effect) | (C) partial → `.flp` for exactness |
| `native_clone_mixer_effect` | B/C | restore dest slot prior content | `ListMixerEffectsAsync` | (C) partial → `.flp` |
| `native_slice_clip` | B composite | delete new half + resize original back | record clip geometry | Phase 3 (composite) |

### (C) No clean inverse (always `.flp` fallback for that commit)

- Effect **param-state** on reload (`add/remove/clone_mixer_effect` over a non-empty slot).
- `native_delete_arrangement` (deep tracks+clips).
- Whole-project lifecycle: `native_open_project`, `native_new_project`,
  `native_save_project_as`, `native_save_new_version`. (These aren't musical-state edits;
  they should not journal — a commit that contains one is `.flp`-only.)
- `native_render` / export (no state change; not journaled).

---

## 5. On-disk journal format

One file per commit, alongside its `.flp`, under `versions\project\{session}\`:

```
%APPDATA%\FLAutomate\versions\project\{session}\
  index.json              # commit DAG + HEAD (unchanged)
  {commitId}.flp          # full-state backup (KEPT — recovery + fallback)
  {commitId}.ops.json     # NEW — the inverse journal for THIS commit
  {commitId}.state.json   # existing optional readable snapshot (unchanged)
```

New `StoragePaths` member:
```csharp
public string ProjectOpsFile(string sessionId, string commitId) =>
    Path.Combine(ProjectVersionDir(sessionId), commitId + ".ops.json");
```

`{commitId}.ops.json` shape:

```jsonc
{
  "schemaVersion": 1,
  "commitId": "5f0c…",
  "createdAt": "2026-07-02T18:03:11Z",
  "invertible": true,          // false ⇒ contains a (C) op ⇒ undo/redo MUST use .flp
  "ops": [
    {
      "seq": 0,
      "kind": "SetScalar",     // SetScalar | Create | Delete | Composite
      "op": "channel_volume",  // logical op id → InverseOpRegistry entry (getter/setter pair)
      "target": { "channel": 2 },
      "old": { "value": 10000 },
      "new": { "value": 6400 }
    },
    {
      "seq": 1,
      "kind": "Create",
      "op": "pattern_clip",
      "target": { "pattern": 3, "track": 1, "start": 0 },   // stable IDENTITY, not slot index
      "old": null,
      "new": { "length": 0 }
    },
    {
      "seq": 2,
      "kind": "Delete",
      "op": "automation_point",
      "target": { "channel": 5 },
      "old": { "timeBeats": 4.0, "value": 0.75, "tension": 0.0 },  // full spec to re-create
      "new": null
    }
  ]
}
```

Rules:
- **`old`** drives UNDO, **`new`** drives REDO. `SetScalar` carries both; `Create` has
  `old:null` (undo deletes by `target`); `Delete` has `new:null` (undo re-creates from
  `old`).
- **Address by stable identity, not by volatile slot index** (§7).
- `invertible:false` on the file (or a missing file) ⇒ the commit's undo/redo falls back
  to the `.flp`. A single (C) op in a turn taints the whole commit to `.flp`-only (keeps
  correctness simple; the common all-(A)/(B) turn stays granular).
- Written **once** at commit, read **only** during that commit's undo/redo. Never loaded
  for any other commit → satisfies "not resident in RAM."

Domain types (Core.Domain):
```csharp
public enum ChangeOpKind { SetScalar, Create, Delete, Composite }

public sealed record ChangeRecord(
    int Seq,
    ChangeOpKind Kind,
    string Op,                              // registry key
    IReadOnlyDictionary<string,object?> Target,
    IReadOnlyDictionary<string,object?>? Old,
    IReadOnlyDictionary<string,object?>? New);

public sealed record CommitJournal(
    int SchemaVersion, string CommitId, DateTimeOffset CreatedAt,
    bool Invertible, IReadOnlyList<ChangeRecord> Ops);
```

---

## 6. Capture seam

The read-before-write happens **at the tool layer** (`NativeControlPlugin`), which is the
only place that both knows the semantic op and holds the `INativeFlControl` handle. Two
new pieces:

### 6.1 `IChangeJournal` (Core.Abstractions) — the per-turn recorder

```csharp
public interface IChangeJournal
{
    // Read OLD via the registry, perform nothing else — just capture + buffer one record.
    void Append(ChangeRecord record);

    // Drained by the coordinator at the turn/commit boundary (like IOperationAuditSink).
    IReadOnlyList<ChangeRecord> Drain();
}
```
Implementation mirrors `OperationAuditSink`: a thread-safe **in-memory queue that only
ever holds the CURRENT turn's records** (a handful), drained and written to disk at
commit, then cleared. Past commits never occupy RAM.

### 6.2 `InverseOpRegistry` — one place that maps op-id → (readOld, apply)

To avoid bolting read-before/apply onto each of ~40 tools ad hoc, centralize each op's
two directions in a registry keyed by the `op` string:

```csharp
public interface IInverseOp
{
    // capture BEFORE-state for the given target (returns the "old" token dict)
    Task<IReadOnlyDictionary<string,object?>> ReadAsync(
        INativeFlControl fl, IReadOnlyDictionary<string,object?> target, CancellationToken ct);

    // apply a value token (old on undo, new on redo) — for Create/Delete this creates or
    // deletes by identity instead of setting a scalar
    Task ApplyAsync(
        INativeFlControl fl, IReadOnlyDictionary<string,object?> target,
        IReadOnlyDictionary<string,object?>? value, CancellationToken ct);
}
```

The tool becomes uniform. E.g. `native_set_channel_volume`:
```csharp
var target = Dict("channel", channel);
var old    = await journal.CaptureAsync(fl, "channel_volume", target, ct); // registry.ReadAsync
await fl.SetChannelVolumeAsync(channel, v, ct);
journal.Append(SetScalar("channel_volume", target, old, Dict("value", v)));
```
`CaptureAsync` is a thin helper on the journal that calls
`registry["channel_volume"].ReadAsync`. The **same** registry entry's `ApplyAsync` is used
by the undo executor — one code path for both directions, no divergence.

Wiring changes:
- Inject `IChangeJournal` (+ the registry) into `NativeControlPlugin`
  (`AgentComposition` line 81 constructs it as `new NativeControlPlugin(context.Fl)` →
  becomes `new NativeControlPlugin(context.Fl, journal)`).
- `ProjectVersionCoordinator` already drains the audit sink for the label; it additionally
  **drains `IChangeJournal`** and passes the records to `CommitAsync`.
- `IProjectVersionControl.CommitAsync` gains an optional
  `IReadOnlyList<ChangeRecord>? changes = null` parameter; `JsonProjectVersionControl`
  writes them to `{commitId}.ops.json` (atomic) right after `SaveCopyAsync`, computing
  `invertible = changes.All(registry.CanInvert)`.

Cost: one extra bridge GET per mutating tool call (bulk ops = one list read for the whole
batch), incurred **during the turn**, not at commit. Commit itself stays 1 native save +
a small JSON write.

---

## 7. Undo / Redo execution (from disk)

Undo reverses the **current HEAD commit**'s ops (to land on its parent). Redo re-applies a
**child commit**'s ops.

```
UndoAsync():
  head = Head; parent = commit(head.ParentId)          // as today
  journal = read {head.Id}.ops.json  (DISK, on demand)
  if journal == null || !journal.Invertible:
      => FALLBACK: SafetyBackup + OpenProjectAsync(parent.flp)   // current behavior
  else:
      for op in journal.Ops REVERSED:                  // reverse order
          registry[op.Op].ApplyAsync(fl, op.Target, op.Old)   // SetScalar→set(old);
                                                              // Create→delete(target);
                                                              // Delete→create(op.Old)
      HEAD = parent; SaveIndex()

RedoAsync():
  child = newest child of head
  journal = read {child.Id}.ops.json
  if journal == null || !journal.Invertible: OpenProjectAsync(child.flp)
  else:
      for op in journal.Ops FORWARD:
          registry[op.Op].ApplyAsync(fl, op.Target, op.New)    // Create→create(op.New);
                                                              // Delete→delete(target)
      HEAD = child; SaveIndex()
```

Multi-value (bulk) ops: each affected element is its **own** `ChangeRecord` (one per clip
/ point / param), so reverse-order replay handles them element-by-element correctly. Bulk
setters (`MoveClipsAsync(list)`, `ResizeClipsAsync(list)`) may be used to batch the
restore for one repaint, but the journal stays per-element for correctness.

**Identity, not slot index (critical).** Clip slot indices, automation-point indices, and
"first empty pattern" all shift as objects are added/removed. Records therefore store a
**stable identity** and resolve it to the live index at apply time:
- clips → `(sourcePattern, track, startTick)` resolved via `ListClips` (the same identity
  `AddPatternClips`' dedup already uses);
- automation points → `(channel, timeBeats)` resolved via `ListAutomationPoints`;
- notes → `(pattern, channel, key, startTick)` (Phase 3, needs delete-note).

**Arbitrary Jump / Restore(commitId).** A multi-step jump across the DAG stays on the
**`.flp` path** (authoritative, simplest, already correct). Granular replay is used only
for single-step Undo/Redo in Phase 1–3. (Phase 4 optional: chain journals along the
head→target path when every commit on it is `invertible`.)

---

## 8. The `.flp`'s remaining role

Keep `SaveCopyAsync` on the commit hot path. The `.flp` is now three things:

1. **Crash recovery** — unchanged. `DetectRecoveryAsync` still compares the newest
   backup against FL's on-disk project on session open.
2. **Fallback** for undo/redo of any commit whose journal is missing, unreadable, or
   `invertible:false` (contains a (C) op or a not-yet-implemented (B) inverse).
3. **Authoritative multi-step Jump/Restore** (§7) and the pre-restore safety backup.

So the journal is the *granular* path; the `.flp` is *recovery + fallback + jump*. Nothing
about recovery regresses; the change is additive.

---

## 9. Interfaces & touch list

New:
- `Core.Domain`: `ChangeOpKind`, `ChangeRecord`, `CommitJournal`.
- `Core.Abstractions`: `IChangeJournal`.
- `Agent`: `IInverseOp`, `InverseOpRegistry` (op-id → read/apply), `ChangeJournal`
  (queue impl, mirrors `OperationAuditSink`), plus a `CaptureAsync` helper.
- `Persistence`: `StoragePaths.ProjectOpsFile`; journal read/write in
  `JsonProjectVersionControl` (atomic write via existing `AtomicFile`).
- `Core.Abstractions.INativeFlControl`: surface the symmetric getters that the bridge
  already implements or can trivially add over `GetParamAsync`:
  `GetMasterVolume/Pitch`, `GetShuffle`, `GetMixerVolume`(exists)/`GetMixerPan`,
  `GetChannelVolume`(exists)/`GetChannelPan/Pitch/Muted/FxRoute`, `GetClipMuted`
  (`0xF71A50`, RE'd). (Plugin-param, track, clip-geometry, song-mode, arrangement,
  automation OLD values are already readable via existing `List*`/`Get*State`.)

Changed:
- `IProjectVersionControl.CommitAsync` + impl: optional `IReadOnlyList<ChangeRecord>?`.
- `JsonProjectVersionControl.UndoAsync/RedoAsync`: journal-first, `.flp`-fallback (above).
  `RestoreAsync` unchanged (`.flp`).
- `NativeControlPlugin` ctor: take `IChangeJournal` + registry; wrap each (A)/(B) tool
  with capture (mechanical, one line pre + one line post).
- `ProjectVersionCoordinator.OnTurnCompletedAsync`: drain the journal, pass to
  `CommitAsync`.
- `AgentComposition`: construct `ChangeJournal` + `InverseOpRegistry`, thread into
  `NativeControlPlugin` and `ProjectVersionCoordinator`.

UI (no signature changes required):
- `VersionControlViewModel` Undo/Redo already call backend `UndoAsync`/`RedoAsync` →
  become granular automatically.
- Per-row Jump/Restore-backup already call `RestoreAsync` → stay `.flp`.
- Optional polish: add `bool Granular` to `VersionCommit` DTO (from journal
  `invertible`) so a row can badge "granular" vs "snapshot-only". `AgentVersionControlGateway.Map`
  fills it.

---

## 10. Phased build plan

**Phase 0 — plumbing (no behavior change).**
`ChangeRecord`/`CommitJournal`/`IChangeJournal`/`ChangeJournal`; `ProjectOpsFile`;
`CommitAsync` writes ops.json; `UndoAsync`/`RedoAsync` read journal with `.flp` fallback;
coordinator drains journal; inject journal into `NativeControlPlugin`. With an empty
registry every commit is `invertible:false` ⇒ identical to today (pure `.flp`). Ship this
first; it's inert until ops are registered.

**Phase 1 — value-set (A), highest AI-edit frequency, NO new RE.**
Register: tempo, master vol/pitch, shuffle, mixer vol/pan (+send/eq after GET-verify),
channel vol/pan/pitch/mute/route, channel & mixer plugin params, track name/color/mute/
collapse, song mode, rename arrangement, clip move, clip resize. Read-before via
`GetParam`/existing lists. Surface the symmetric scalar getters on `INativeFlControl`.
This alone makes the common "tweak params / move-resize clips / rename" turns fully
granular.

**Phase 2 — create/delete with EXISTING inverses + identity resolution.**
`add_pattern_clips`↔`delete_clips` (identity→slot resolver), `delete_clips` re-add
(snapshot; pattern clips only), `duplicate_clip`, `mute_clips` (surface `GetClipMuted`),
`clear_pattern` (note snapshot + `AddNotes`), automation add/delete (index resolve /
snapshot), `make`/`clone_arrangement`↔`delete_arrangement`, `replace_channel_sample`
(needs sample-path read). Build the identity-resolution helpers here.

**Phase 3 — new RE inverse primitives.**
`delete_note(pattern,chan,key,startTick)`, `delete_channel(index)`,
`delete_pattern(index)`, `delete_marker(tick/idx)`, `slice_clip` composite inverse,
exact effect-state restore. Until each lands, its op stays unregistered ⇒ its commits are
`invertible:false` ⇒ `.flp` fallback (safe). Each RE unlock flips one more op to granular.

**Phase 4 — optional.**
Multi-step Jump via journal chaining when every commit on the head→target path is
`invertible`; otherwise keep `.flp`. Per-row "granular" badge in the UI.

---

## 11. Risks / notes

- **Channel addressing caveat.** `ChannelParamId` assumes `recTag == channel index` (true
  for unreordered projects). Reordered/deleted-channel projects need recEventId resolution
  — this affects BOTH set and get equally, so it's not new, but the resolver should be
  shared so undo hits the same channel the edit did.
- **GET-verify sends/EQ.** `SetMixerSend`/`SetMixerEqGain` write via the bus; confirm the
  matching cmdId is GET-readable, else read the raw fixed-point at the RE'd matrix/EQ
  address. Flagged in §4.
- **Big `clear_pattern`.** Snapshotting all notes before a clear is bounded but can be
  large; acceptable (it's disk, drained per turn) but the note-snapshot op should page
  `GetNotes` and, for very large patterns, may opt the commit to `.flp`-only.
- **Taint rule keeps correctness cheap.** Any single (C)/unregistered op in a turn sets
  the commit `invertible:false` (whole-commit `.flp` fallback), so we never partially
  invert a turn and leave FL in a half-state. Granularity is gained per *turn*, monotonic
  as phases land.
- **FL's own Ctrl+Z untouched.** This journal is the *agent's* undo; it does not interact
  with FL's internal undo stack (which the AI's command-bus writes may or may not push).
```
