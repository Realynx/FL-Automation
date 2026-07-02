# Project-State Version Control + Backup — Backend Design

Undo/redo of the AI's edits to the live FL project, grouped by **AI work-unit (agent turn)**, with
full-state backups stored in our app data so we can recover even if FL Studio crashes without saving.

Grounded in the real code:
- `src/FruityLink.Core/Abstractions/INativeFlControl.cs` — SDK read/write surface + project lifecycle.
- `src/FruityLink.FlStudio/Inject/FlInjectBridge.cs` — native impls of save/open (already headless).
- `src/FruityLink.Persistence/StoragePaths.cs` — app-data layout (`%APPDATA%\FLAutomate`).
- `src/FruityLink.Core/Domain/VersionTree.cs` + `Abstractions/IPersistence.cs` + `Persistence/JsonVersionTree.cs` — the existing (unwired) chat version DAG.
- `src/FruityLink.Agent/{AgentTurnRunner,FlAgent,OperationAuditSink}.cs` + `Plugins.FlAgent/Ui/AvaloniaChatPresenter.cs` — the turn boundary + mutation signal.

---

## 0. TL;DR / key findings

1. **No RE needed for headless save-as / open-path.** `FlInjectBridge` already implements, verified:
   - `SaveCopyAsync(path)` → `FLproj_SaveProjectToFlp` (`0x10d6190`): writes a `.flp` to an **arbitrary
     path without changing** the current project path/title. **This is our backup primitive.** Headless
     (no dialog).
   - `OpenProjectAsync(path)` → `FLproj_OpenProject` (`0x10d50c0`): opens a `.flp` from an arbitrary
     path. **This is our restore primitive.** Headless.
   - Plus `SaveProjectAsAsync`, `SaveProjectAsync`, `GetProjectInfoAsync` (title/path/saved-state).
2. **The `.flp` is the authoritative artifact.** Almost every SDK *getter* returns a human-readable
   **string**, not a round-trippable structured model, and many read-only facts have **no matching
   setter** (see §1 gap table). So restore = **open the commit's `.flp`**; the JSON state is
   **inspection + last-resort partial replay**, not the primary restore path.
3. **The existing chat version tree already models a tree of `{chat snapshot + FlOperation[]}` per
   session but is unwired** (nobody calls `CreateCheckpointAsync`; it stores **no** project state / no
   `.flp`). We **align** with it: a project commit references the chat node id rather than duplicating
   the chat DAG.
4. **Turn boundary = `FlAgent.StreamAsync` completion.** Mutation gate = the per-turn set of invoked
   tools (`FlAgent.ToolInvoked : ToolCallInfo(Function, Arguments)`), backed up by draining
   `OperationAuditSink`. Commit **only when the turn actually mutated persistent project state.**
5. **Latency/stability caveat:** the bridge is a single serialized pipe on FL's main thread, and heavy
   synchronous native traffic has frozen FL before (MEMORY: mixer-freeze / bridge-timeout, `native_render`
   removed). A `.flp` save is **one** native call (cheap); a full JSON state read is **many** (per
   channel/pattern/clip). So: `.flp` backup always; JSON capture is bounded/best-effort/off the hot path.

---

## 1. State-capture inventory (what we can READ vs WRITE-BACK)

### Readable from `INativeFlControl` (candidates for the JSON snapshot)
| Domain | Getter(s) | Shape |
|---|---|---|
| Transport/tempo | `GetTempoAsync`→double, `GetPpqAsync`→int, `GetSongStateAsync`→**string** (mode/playing/tick/loop/bar-beat) | mixed |
| Project | `GetProjectInfoAsync`→**string** (title/path/saved) | string |
| Patterns | `GetCurrentPatternAsync`, `ListPatternsAsync`→**string**, `GetPatternNameAsync` | mixed |
| Channels | `GetChannelCountAsync`, `ListChannelsAsync`→**string**, `GetChannelNameAsync`, `GetChannelPluginAsync`→**string** | mixed |
| Notes | `GetNotesAsync(pattern,channel,offset)`→**string** (paged) | string |
| Mixer | `ListMixerEffectsAsync(track)`→**string**, `ListPluginParamsAsync(...)`→**string** | string |
| Playlist | `ListPlaylistTracksAsync`→**string**, `ListClipsAsync(offset,track)`→**string** | string |
| Arrangements | `ListArrangementsAsync`→**string** | string |
| Markers | `ListMarkersAsync`→**string** | string |
| Automation | `ListAutomationPointsAsync(channel)`→**string** | string |
| Plugin catalog | `ListAvailablePluginsAsync`, `ListSamplesAsync` | catalog, **not** project state — do **not** snapshot |

Transient / never snapshot: `GetStatusAsync` (hint bar), `IsAvailableAsync`.

### Writable back (setters usable for a JSON→FL replay)
Tempo/master (`SetTempo`, `SetMasterVolume/Pitch`, `SetShuffle`), mixer (`SetMixerVolume/Pan/FxParam/Send/EqGain`),
channels (`SetChannelVolume/Pan/Pitch/Muted/FxRoute`), notes (`AddNote(s)` additive; `ClearPatternAsync` to wipe),
patterns (`Create/Select/Clear`), plugin params (`SetPluginParam`), mixer FX (`Add/Remove/Clone MixerEffect`),
channels/samples (`AddChannel`, `AddSampleChannel`, `ReplaceChannelSample`), playlist track props
(`SetTrackName/Color/Mute/Collapsed`), clips (`AddPatternClip/Move/Resize/Delete/SetMuted/Slice/Duplicate`),
markers (`AddMarker`), arrangements (`Add/Clone/Rename/Delete/Select`), automation (`Add/DeleteAutomationPoint`).

### Round-trip GAPS (read but cannot faithfully write back → why `.flp` is authoritative)
- **Plugin/generator internal state**: `GetChannelPluginAsync`/`ListMixerEffectsAsync` return *descriptions*;
  there is **no preset-chunk get/set**. Only exposed params via `SetPluginParam` are writable → arbitrary
  synth/effect internal state is **not** JSON-restorable.
- **Notes are additive-only**: no per-note delete. Restoring a pattern means `ClearPatternAsync` + re-`AddNotes`,
  and `GetNotesAsync` returns a **string** that must be parsed to reconstruct `NoteSpec[]`.
- **No deleters** for channels or patterns; **no `SetPatternName`** (the enum has `RenamePattern` but the
  interface exposes no setter) → can't remove/rename what a restore should remove/rename.
- **Index-based addressing** (clips, FX slots) **shifts** as items are added/removed → fragile for replay.
- **Getters are strings**, not structured/versioned models → parsing is brittle and lossy.

**Conclusion:** JSON snapshot = human-readable inspection + *partial* emergency replay. The `.flp` is the
authoritative, lossless recovery artifact and the primary restore mechanism.

---

## 2. Storage layout (under `%APPDATA%\FLAutomate`)

Distinct subdir per the brief, keyed by chat session, linked to the existing chat tree by node id:

```
%APPDATA%\FLAutomate\
  versions\
    {sessionId}.json                 # EXISTING chat version DAG (JsonVersionTree / VersionNode)
    project\                          # NEW — this system
      {sessionId}\
        index.json                   # commit DAG + HEAD + recovery metadata (ProjectVersionIndex)
        {commitId}.flp               # authoritative backup (SaveCopyAsync target)
        {commitId}.state.json        # FlProjectState snapshot (inspection + fallback replay); optional
```

`index.json` (one small file, atomically rewritten via existing `AtomicFile`) holds the commit list, the
HEAD pointer, the redo cursor, and crash-recovery fields — so history + undo/redo state survive restarts.

### `StoragePaths` additions
```csharp
public string ProjectVersionsDirectory { get; }                 // versions/project
public string ProjectVersionDir(string sessionId);              // versions/project/{sessionId}
public string ProjectVersionIndexFile(string sessionId);        // .../index.json
public string ProjectFlpBackup(string sessionId, string commitId);   // .../{commitId}.flp
public string ProjectStateFile(string sessionId, string commitId);   // .../{commitId}.state.json
// create ProjectVersionsDirectory in EnsureDirectories(); create the per-session dir lazily on first commit.
```

---

## 3. Core types (the contract the UI agent builds against)

Place in `FruityLink.Core` (`Domain/ProjectVersion.cs`, `Abstractions/IProjectVersionControl.cs`).

```csharp
namespace FruityLink.Core.Domain;

/// <summary>One AI work-unit committed to project history. A commit = a full-state backup
/// (.flp) + optional readable snapshot (state.json) + a link to the chat version node.</summary>
public sealed record ProjectCommit(
    string Id,                       // GUID "N"
    string? ParentId,                // previous commit (null = root); DAG to mirror the chat tree
    string Label,                    // "Added 4-bar drum loop" (auto from ops/user msg, or manual)
    DateTimeOffset CreatedAt,
    string SessionId,                // owning chat session
    string? ChatNodeId,              // linked VersionNode.Id in the chat tree (align, don't duplicate)
    string FlpBackupPath,            // absolute path to {commitId}.flp (authoritative restore)
    string? StateJsonPath,           // absolute path to {commitId}.state.json, or null
    string? OriginalProjectPath,     // FL's own project path at commit time (for crash detection)
    IReadOnlyList<string> Operations,// human-readable op summaries (from OperationAuditSink drain)
    CommitTrigger Trigger);          // Auto | Manual | PreRestoreSafety | Initial

public enum CommitTrigger { Initial, Auto, Manual, PreRestoreSafety }
```

```csharp
namespace FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

/// <summary>Version control + backup of the live FL project, grouped by AI work-unit.
/// Restore = open the target commit's .flp (authoritative); JSON state is inspection + fallback.</summary>
public interface IProjectVersionControl
{
    /// <summary>Current session this VC tracks. Set when a chat session becomes active.</summary>
    string SessionId { get; }

    /// <summary>Commits in creation order (oldest→newest) for the session. Bind the UI to this.</summary>
    IReadOnlyList<ProjectCommit> History { get; }

    /// <summary>The commit the live project currently reflects (HEAD), or null before the first commit.</summary>
    ProjectCommit? Head { get; }

    bool CanUndo { get; }
    bool CanRedo { get; }

    /// <summary>Point the VC at a session (loads its index.json). Call on session open/switch.</summary>
    Task OpenSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Capture a commit NOW: save the .flp backup (+ optional state.json), append to history,
    /// advance HEAD. <paramref name="chatNodeId"/> links the chat checkpoint. <paramref name="operations"/>
    /// are the drained op summaries (also used to auto-label when <paramref name="label"/> is null).
    /// Returns null when there was nothing to capture (e.g. bridge unavailable).</summary>
    Task<ProjectCommit?> CommitAsync(
        string? label = null,
        string? chatNodeId = null,
        IReadOnlyList<string>? operations = null,
        CommitTrigger trigger = CommitTrigger.Manual,
        CancellationToken ct = default);

    /// <summary>Move HEAD to its parent and restore that commit (open its .flp).</summary>
    Task<ProjectCommit?> UndoAsync(CancellationToken ct = default);

    /// <summary>Re-apply the commit undone last (move HEAD forward along the last-departed branch).</summary>
    Task<ProjectCommit?> RedoAsync(CancellationToken ct = default);

    /// <summary>Restore an arbitrary commit: (1) safety-backup the live project, (2) open the commit's
    /// .flp, (3) set HEAD. Does NOT create a new commit (HEAD just moves). Committing AFTER a restore
    /// from a non-tip commit branches the DAG (mirrors the chat tree's branch-on-edit).</summary>
    Task<ProjectCommit?> RestoreAsync(string commitId, CancellationToken ct = default);

    /// <summary>Deserialized readable snapshot for a commit (UI diff/inspect), or null if none.</summary>
    Task<FlProjectState?> GetStateAsync(string commitId, CancellationToken ct = default);

    /// <summary>Raised (any thread) after History/Head/Can* change. UI marshals to its thread.</summary>
    event EventHandler<ProjectVersionChanged>? Changed;
}

public sealed record ProjectVersionChanged(
    ProjectVersionChangeKind Kind, ProjectCommit? Commit);
public enum ProjectVersionChangeKind { Committed, Restored, Undone, Redone, SessionOpened, Pruned }
```

`FlProjectState` (Phase 3) is a plain serializable bag — typed scalars where we have them, string blobs
otherwise (honest about §1's limits):

```csharp
public sealed record FlProjectState(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    double Tempo, int Ppq,
    string ProjectInfo, string SongState,
    string Patterns, string Channels, string PlaylistTracks,
    string Clips, string Arrangements, string Markers,
    IReadOnlyList<FlChannelState> ChannelDetail,   // name + plugin desc + (optional) notes-per-pattern
    IReadOnlyList<string> MixerTracks);            // per-track effect list strings
public sealed record FlChannelState(int Index, string Name, string PluginDescription, string? Notes);
```

---

## 4. Commit trigger — one commit per AI work-unit

**Boundary:** completion of `FlAgent.StreamAsync(userInput)` for a turn (see `AvaloniaChatPresenter.RunTurnAsync`).
One turn = one AI work-unit, regardless of how many tool calls it made.

**Gate (only commit on real, persistent mutation):** collect the turn's invoked tools via
`FlAgent.ToolInvoked` (`ToolCallInfo.Function`). Classify:
- **Read-only** (never commit): names starting `get_`/`list_`, plus `is_available`, `get_status`.
- **Transient** (never commit — no persistent project change): `transport_play/stop/record`, `seek`,
  `select_*` (pattern/channel/track/arrangement selection is view state).
- **Mutating** (commit): everything else (add/set/create/clear/delete/move/resize/rename/route/
  add_note(s)/add_*_effect/set_*_param/…).

Also **drain `OperationAuditSink`** at turn end for the op summaries / auto-label (note: today only
`MusicTheoryPlugin` records — see §8 "gaps to close" to make the audit complete; the ToolInvoked set is
the reliable gate meanwhile). If any mutating tool fired → `CommitAsync(label:null, chatNodeId, ops, Auto)`.

**Manual commit:** UI button → `CommitAsync(label:"...", trigger:Manual)`.

**Where to wire (single place, both UIs benefit):** a small coordinator owned alongside `FlAgent`.

```csharp
public sealed class ProjectVersionCoordinator   // FruityLink.Agent
{
    private readonly IProjectVersionControl _vc;
    private readonly FlAgent _agent;
    private readonly IOperationAuditSink _audit;
    private readonly HashSet<string> _mutatingThisTurn = new(StringComparer.OrdinalIgnoreCase);

    public ProjectVersionCoordinator(IProjectVersionControl vc, FlAgent agent, IOperationAuditSink audit)
    { _vc = vc; _agent = agent; _audit = audit; _agent.ToolInvoked += OnTool; }

    private void OnTool(ToolCallInfo t) { if (IsMutating(t.Function)) _mutatingThisTurn.Add(t.Function); }

    // Called by the presenter right after the StreamAsync loop finishes for a turn.
    public async Task OnTurnCompletedAsync(string? chatNodeId, CancellationToken ct)
    {
        if (_mutatingThisTurn.Count == 0) return;              // nothing persistent changed
        var ops = _audit.Drain().Select(o => o.Description).ToList();
        var label = ops.Count > 0 ? string.Join("; ", ops.Take(3)) : $"AI edit ({_mutatingThisTurn.Count} ops)";
        _mutatingThisTurn.Clear();
        await _vc.CommitAsync(label, chatNodeId, ops, CommitTrigger.Auto, ct);
    }
    private static bool IsMutating(string fn) =>
        !(fn.StartsWith("get_") || fn.StartsWith("list_")
          || fn is "seek" or "transport_play" or "transport_stop" or "transport_record" or "get_status"
          || fn.StartsWith("select_"));
}
```

Presenter hook (`AvaloniaChatPresenter.RunTurnAsync`, in the `finally` after the `await foreach`):
```csharp
finally { ... await _coordinator.OnTurnCompletedAsync(chatNodeId, CancellationToken.None); }
```
(Fire-and-forget on a background task so the commit's native save never blocks the UI thread.)

---

## 5. Backup + restore mechanism

### Commit (backup)
```csharp
public async Task<ProjectCommit?> CommitAsync(string? label, string? chatNodeId,
    IReadOnlyList<string>? operations, CommitTrigger trigger, CancellationToken ct)
{
    if (!await _fl.IsAvailableAsync(ct)) return null;          // no bridge → skip silently
    string id = Guid.NewGuid().ToString("N");
    string flp = _paths.ProjectFlpBackup(_sessionId, id);
    Directory.CreateDirectory(_paths.ProjectVersionDir(_sessionId));

    await _fl.SaveCopyAsync(flp, ct);                          // ONE native call — authoritative backup
    string? statePath = await TryCaptureStateAsync(id, ct);   // best-effort, bounded (Phase 3); may be null

    string original = ExtractPath(await _fl.GetProjectInfoAsync(ct));
    var commit = new ProjectCommit(id, _head?.Id, label ?? "AI edit", DateTimeOffset.UtcNow,
        _sessionId, chatNodeId, flp, statePath, original, operations ?? [], trigger);

    AppendAndAdvanceHead(commit);                             // history.Add; _head = commit; _redo cleared
    await SaveIndexAsync(ct);                                  // atomic index.json rewrite
    Raise(ProjectVersionChangeKind.Committed, commit);
    return commit;
}
```
- `SaveCopyAsync` writes the `.flp` **without** touching FL's current project path/title → the user's own
  save state is undisturbed. Works on untitled projects too (explicit path passed to `SaveProjectToFlp`).

### Restore (undo/redo/restore)
```csharp
public async Task<ProjectCommit?> RestoreAsync(string commitId, CancellationToken ct)
{
    var target = History.FirstOrDefault(c => c.Id == commitId) ?? throw ...;
    // 1) safety net: capture the CURRENT live state before we overwrite it, so a mis-click is recoverable
    await SafetyBackupAsync(ct);                               // SaveCopyAsync → pre-restore-{ts}.flp (own slot)
    // 2) authoritative restore
    if (File.Exists(target.FlpBackupPath)) await _fl.OpenProjectAsync(target.FlpBackupPath, ct);
    else await ReplayStateAsync(target, ct);                   // fallback: partial JSON replay via setters
    _head = target; ClampRedoCursor(target);
    await SaveIndexAsync(ct);
    Raise(ProjectVersionChangeKind.Restored, target);
    return target;
}
```
- **Undo** = `RestoreAsync(Head.ParentId)`; **Redo** = `RestoreAsync(redoCursorChildId)`.
- Redo bookkeeping: on undo, remember the child we departed from; committing after an undo from a
  non-tip commit **branches** (parent = current HEAD), exactly mirroring the chat tree's branch-on-edit.
- **Restore caveat (verify):** `OpenProjectAsync` may raise FL's own "project modified — save?" modal if
  FL considers the live project dirty. The §5 safety backup means we can force-discard; if a modal
  appears, RE a "discard changes / suppress prompt" path or set the project's dirty flag to 0 before
  open (bridge already pokes project-manager flags in `NewProjectAsync`). **This is the only possible
  small RE item — for restore UX, not for the core backup/save capability.**

### Chat alignment on restore
Optionally re-seed the conversation so chat + project move together: look up `commit.ChatNodeId` via the
existing `IVersionTree.GetNodeAsync`, then `FlAgent.ResetConversation(node.Messages)`. Keep this behind a
UI toggle ("restore chat too") since users may want to keep talking.

---

## 6. Crash recovery

`.flp` backups are part of VC, so we can recover AI edits FL never saved.

**Metadata stored per session (in `index.json`):** `LatestCommitId`, `LatestCommitAt`,
`Head`, and per-commit `OriginalProjectPath` (FL's own path at commit time).

**Startup detector** (run in `FlAgentPlugin.EnableAsync`, after the bridge is up, non-blocking):
```
load index.json for the (most-recently-active) session
if no commits → nothing to do
latest = newest commit
info = GetProjectInfoAsync()   // FL's current path + saved state
recoverable =
    info.Untitled                                   // FL opened blank / never saved  → offer
    OR (info.Path == latest.OriginalProjectPath     // same project the AI edited …
        AND File.mtime(info.Path) < latest.CreatedAt)  // …but user's on-disk .flp is OLDER than our last backup
if recoverable → prompt in chat UI:
    "FL may have closed without saving the AI's changes. Restore backup from {latest.CreatedAt}?"
    [Restore] → RestoreAsync(latest.Id)   [Ignore] → leave as-is
```
Rationale: we can't get a reliable live "is-dirty" bit, so we compare **our last backup timestamp vs the
user's on-disk `.flp` mtime**. If our backup is newer, the AI's edits weren't persisted by the user → offer
recovery. Untitled/blank FL after a crash always offers (a matching session backup exists).

**Emergency safety backups** (`PreRestoreSafety` + optional periodic) mean even a bad restore is undoable.

---

## 7. Phased build plan

- **Phase 0 — types & paths.** Add `ProjectCommit`, `CommitTrigger`, `IProjectVersionControl`,
  `ProjectVersionChanged` to `FruityLink.Core`; extend `StoragePaths` (§2). No behavior yet. *(UI agent
  unblocked here.)*
- **Phase 1 — backup-only VC.** `ProjectVersionControl` in `FruityLink.Persistence` (or a new
  `FruityLink.VersionControl`) using `INativeFlControl.SaveCopyAsync`/`OpenProjectAsync`: `OpenSessionAsync`,
  `CommitAsync` (.flp only), `RestoreAsync`, `UndoAsync`, `RedoAsync`, `index.json` via `AtomicFile`.
  Unit-test with a fake `INativeFlControl` (see `tests/.../FakeNativeFlControl.cs`) — assert `.flp` written,
  HEAD/undo/redo math, DAG branch-on-commit-after-undo, atomic index. **Delivers the core promise.**
- **Phase 2 — commit trigger.** `ProjectVersionCoordinator` (§4) + presenter hook + mutation
  classifier; auto-label from `OperationAuditSink`/user message; manual commit path. Test the gate
  (read-only/transient turn → no commit; mutating turn → one commit).
- **Phase 3 — JSON state capture.** `FlProjectState` + `FlProjectStateReader` (bounded, best-effort,
  timeouts, size cap; skip note-detail for huge projects) → `{commitId}.state.json`; `GetStateAsync`;
  `ReplayStateAsync` fallback. Runs off the hot path.
- **Phase 4 — crash recovery + retention.** Startup detector (§6) + non-blocking UI prompt; safety
  backups; pruning (cap N commits or M MB per session; delete oldest `.flp`/`.state.json`, keep the
  metadata row flagged `Pruned`; never prune HEAD or the latest).
- **Phase 5 — UI (UI agent).** History/timeline panel bound to `History` + `Changed`; undo/redo
  buttons (`CanUndo`/`CanRedo`); per-commit restore/label/inspect; recovery prompt surface.

---

## 8. Integration points & gaps to close

- **DI/composition:** construct one `ProjectVersionControl` + `ProjectVersionCoordinator` where `FlAgent`
  is composed — `AgentComposition.ResolveOrBuild` (self-composed path) and the host's shared-agent path.
  Share the single `StoragePaths`/`OperationAuditSink` already threaded there.
- **Turn hook:** `AvaloniaChatPresenter.RunTurnAsync` `finally` (and WPF `FlAgentChatWindow` for parity).
- **Session id:** the VC keys on the chat `sessionId`; call `OpenSessionAsync` when a session opens/switches;
  pair each commit's `ChatNodeId` with a `IVersionTree.CreateCheckpointAsync` call so chat + project commits
  are 1:1 (this also finally *wires* the existing-but-dormant chat version tree).
- **Audit completeness (recommended):** today only `MusicTheoryPlugin` calls `IOperationAuditSink.Record`;
  `NativeControlPlugin` does not. Add `Record(...)` to the mutating `NativeControlPlugin` tools so commit
  labels/op-lists are accurate. The `ToolInvoked` gate does **not** depend on this, so it's non-blocking.
- **RE status:** **none required** for the core feature — headless save-copy (`SaveCopyAsync`/`0x10d6190`)
  and open-path (`OpenProjectAsync`/`0x10d50c0`) already exist and are verified. The **only** possible RE
  is a *nice-to-have*: suppress/auto-discard FL's "project modified — save?" prompt on `OpenProjectAsync`
  during a restore (poke the project dirty flag to 0 before open, à la `NewProjectAsync`).
- **Stability:** keep `CommitAsync` to the single `SaveCopyAsync` call on the hot path; run JSON capture and
  crash-detection off-thread; debounce/serialize with the bridge's single-pipe contract; enforce retention
  so `versions/project/` can't grow unbounded (`.flp`s are full project copies).
```
