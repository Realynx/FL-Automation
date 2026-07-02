# Project-State Version Control — Avalonia UI design + build plan

Design for the AI edit-history / undo-redo / restore feature in FL Automate's Avalonia UI
(`src/FruityLink.Ui.Avalonia`). Written to match the existing chat shell exactly and to bind against the
shared `IProjectVersionControl` contract a sibling agent is building for the backend.

---

## 1. Findings — what exists, what to match

- **Framework**: Avalonia + hand-rolled MVVM. `ViewModelBase` (INotifyPropertyChanged + `SetProperty`);
  `RelayCommand` is a *sync* `ICommand` with `RaiseCanExecuteChanged()`. Async is done with the
  `new RelayCommand(() => _ = DoAsync(), () => !_isBusy)` + `IsBusy` gating pattern
  (see `BackendSettingsViewModel`). No MVVM toolkit, no DI container in the UI lib.
- **No conversation version-tree UI exists in the Avalonia project today.** Grep for
  `version|history|branch|Snapshot|VersionTree` across `src/FruityLink.Ui.Avalonia` returns nothing but
  the backend-settings seam. The nearest sibling — and the established pattern this panel MUST match — is
  the in-place **Settings panel**: `ChatWindow.axaml` is a `Grid` holding two overlaid full-window
  surfaces switched by `IsVisible`:
  - chat surface — `IsVisible="{Binding !IsSettingsOpen}"`
  - `<views:SettingsView IsVisible="{Binding IsSettingsOpen}" />`
  A **gear** button in the header runs `ToggleSettingsCommand`. **We replicate this exactly**: a third
  overlay surface (`VersionControlView`) toggled by a **history/clock** button beside the gear. If/when a
  conversation-version tree lands, it should share this same overlay (a segmented "History" surface with a
  Versions section + a Conversation section) — the design below is structured so that unification is a
  later addition, not a rewrite.
- **Design tokens** are fully wired in `Theme/Tokens.axaml` + `Theme/Controls.axaml`. Reusable classes:
  `card`, `glass`, `panel`, `pill`+`primary`/`secondary`/`danger`, `icon`, `badge`, `h`, `body`, `muted`,
  `meta`, `mono`, `settingLabel`, `settingDesc`, `toggle`, `statusDot`, `infoBubble`, `toolChip`.
  Brushes: `BrandGradientBrush`, `HeadlineGradientBrush`, `Accent300/400Brush`, `Magenta400/500Brush`,
  `Glass04/05/08Brush`, `BorderWhite10/20Brush`, `SurfaceBrush`, `SurfaceTranslucentBrush`, `Aurora*Brush`,
  `DangerBrush`, `MutedTextBrush`, `MetaTextBrush`. Radii: card 16, glass/panel 12, pill 9999.
- **Host-service seam** (how the panel reaches the backend): the UI lib is **deliberately
  backend-agnostic** — it never references `FruityLink.Core`/`Persistence`. The pattern (see
  `IBackendSettingsGateway` + `BackendSettings` DTO):
  1. Define a UI-local interface + a plain-primitives DTO in `FruityLink.Ui.Avalonia/Services`.
  2. Ship an in-memory stub so `dotnet run` (standalone dev head) stays interactive.
  3. The VM holds the stub by default; `ChatViewModel.AttachXxx(gateway)` swaps in the real one.
  4. The FL Agent plugin's **presenter** (`AvaloniaChatPresenter`) builds the real gateway via
     `AgentComposition` and calls `_host.Post(() => _vm.AttachXxx(gateway))` on the Avalonia UI thread.
- **Thread marshalling**: background→UI via `EmbeddedAvaloniaHost.Post` (presenter) or
  `Avalonia.Threading.Dispatcher.UIThread.Post` (usable directly from VMs — `ChatWindow.axaml.cs`
  already does). The `IProjectVersionControl.Changed` event will fire on a background thread and MUST be
  marshalled before touching the `ObservableCollection`.

**Consequence for the shared contract**: exactly like `BackendSettings` mirrors Core's `AppSettings`, we
mirror `IProjectVersionControl` + `VersionCommit` as **UI-local types** in `Services/`. The plugin writes
a thin adapter mapping the backend's real `IProjectVersionControl` → this UI-local one. Names are kept
identical to the shared contract so the mapping is 1:1.

---

## 2. Placement in the app

- **Primary surface**: a full-window in-place overlay `VersionControlView` (UserControl), mutually
  exclusive with the chat + settings surfaces — identical mechanism to `SettingsView`.
- **Entry point**: a **history icon button** in the chat header, immediately left of the gear, running
  `ToggleVersionsCommand`. (Same `Classes="icon"` chrome as the gear.)
- **Crash-recovery entry**: a dismissible **recovery banner** rendered at the top of the *chat* surface
  (so the user sees it the instant the window opens, without hunting for the panel). Clicking Restore/Open
  routes into the VC flow.
- Opening Versions closes Settings and vice-versa (single active overlay). `ToggleVersionsCommand` sets
  `IsVersionsOpen = !IsVersionsOpen` and forces `IsSettingsOpen = false`.
- Opening the panel calls `Versions.Refresh()` so it always reflects current backend state (mirrors how
  opening Settings calls `_backend.Reload()`).

```
Header:  [◆ logo] FL Automate            (•)READY   [🕑 history]  [⚙ gear]
```

---

## 3. Panel layout + ASCII mock

Three bounded rows, mirroring the chat/settings surfaces: header (Auto) · timeline (*) · footer (Auto).
Newest commit first (top). Each commit is a `glass` row; the **Current** commit gets a brand-lit left rule
+ a "NOW" badge; commits with a `.flp` backup show a small cyan "backup" chip. A commit *ahead* of Current
(redoable future) renders dimmed to read as "undone".

```
┌──────────────────────────────────────────────────────────────┐
│ ← Version history                                             │  header (back arrow → chat)
│   The AI's edits, grouped by work-unit. Jump to any state.    │
├──────────────────────────────────────────────────────────────┤
│  [ ↶ Undo ]  [ ↷ Redo ]              12 states · backups: 5   │  toolbar (Undo/Redo + summary)
├──────────────────────────────────────────────────────────────┤
│  ⚠ Recovered work available — FL closed with unsaved edits.   │  recovery banner (only if HasRecovery;
│    "Sidechain the bass to the kick"   [ Restore ] [ Dismiss ] │   can also live on the chat surface)
├──────────────────────────────────────────────────────────────┤
│  ┃ ● Chopped the vocal into 8 slices          2 min ago  ⬤bk │  ← Current (brand rule + NOW badge)
│  ┃   NOW                                                       │
│                                                               │
│    ○ Routed drums to Bus 1, added glue comp    9 min ago  ⬤bk │  older commit (glass row)
│         [ Jump here ]  [ Restore .flp ]                        │   actions reveal on hover / selection
│                                                               │
│    ○ Set project tempo to 140 & key to F min   14 min ago     │  (no backup → no chip / restore btn)
│         [ Jump here ]                                          │
│                                                               │
│    ○ Created 4 mixer tracks, named them        20 min ago ⬤bk │
│         [ Jump here ]  [ Restore .flp ]                        │
│  ─────────────────────────────────────────────────────────── │
│    (dimmed) Added reverb send                  redoable ↷      │  commit AHEAD of Current (undone)
├──────────────────────────────────────────────────────────────┤
│  Jumping to an older state discards newer edits.              │  footer hint (only shown near a jump)
└──────────────────────────────────────────────────────────────┘

Inline confirm (replaces the row's action buttons; NO modal dialog — app has none):
    ○ Routed drums to Bus 1...                              9 min ago
      Discard 3 newer edits and jump here?   [ Jump ]  [ Cancel ]
```

Design choices tied to the codebase:
- **No `MessageBox`/dialog anywhere** in the app, so destructive confirms are an **inline two-step**
  (the row's buttons swap to a "Discard N newer edits and jump here? [Jump][Cancel]" strip). This matches
  the in-place, chrome-light philosophy of the whole UI.
- Relative timestamps ("2 min ago") computed in the `CommitVm`; absolute time in a `ToolTip.Tip`.
- Backup chip uses the accent-cyan chip style already defined for tool calls (`toolChip`).

---

## 4. Shared contract, mirrored UI-local (`Services/IProjectVersionControl.cs`)

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FruityLink.Ui.Avalonia.Services;

/// <summary>One AI work-unit commit. UI-local mirror of the backend record (plain primitives only, so the
/// UI lib never references FruityLink.Core) — exactly how BackendSettings mirrors AppSettings.</summary>
public sealed record VersionCommit(
    string Id,
    DateTimeOffset Timestamp,
    string Label,          // the AI work-unit summary
    bool HasBackup,        // a .flp backup exists for this state
    string? ParentId);     // parent commit id (null = root)

/// <summary>Optional crash-recovery info surfaced at startup (newer-than-saved backup detected).</summary>
public sealed record RecoveryInfo(string CommitId, string Label, DateTimeOffset Timestamp);

/// <summary>The seam the VC panel binds to. Mirrors the sibling backend's IProjectVersionControl; the FL
/// Agent plugin adapts the real Core interface onto this. Standalone dev head uses the in-memory stub.</summary>
public interface IProjectVersionControl
{
    IReadOnlyList<VersionCommit> History { get; }   // chronological (oldest → newest)
    VersionCommit? Current { get; }                  // the state FL currently reflects

    Task CommitAsync(string label, CancellationToken ct = default);
    Task UndoAsync(CancellationToken ct = default);
    Task RedoAsync(CancellationToken ct = default);
    Task RestoreAsync(string commitId, CancellationToken ct = default);      // jump-to-this-state
    Task RestoreBackupAsync(string commitId, CancellationToken ct = default);// re-open the .flp backup

    /// <summary>Pending crash-recovery, or null. UI shows a banner when non-null.</summary>
    RecoveryInfo? PendingRecovery { get; }
    Task AcceptRecoveryAsync(CancellationToken ct = default);
    void DismissRecovery();

    /// <summary>Raised (possibly off-thread) whenever History/Current/PendingRecovery change.</summary>
    event Action Changed;
}
```

> The shared contract lists only `RestoreAsync`; we add `RestoreBackupAsync` + the recovery members
> because the UI requires them (task points 1, 5). These are additive and named to match the backend's
> likely surface — the sibling agent can fold `RestoreBackupAsync` into `RestoreAsync(commitId, fromBackup)`
> and we adjust the one adapter call. `CommitAsync` is unused by the UI (the agent commits) but kept for
> parity with the contract.

Ship an **in-memory stub** `InMemoryProjectVersionControl` (same file, à la
`InMemoryBackendSettingsGateway`) that seeds ~5 sample commits, implements undo/redo by moving a cursor,
and raises `Changed` — so the standalone dev head renders a populated, interactive panel.

---

## 5. ViewModels

### `ViewModels/CommitVm.cs` — one row

Per-item commands live on the row (the shared `RelayCommand` is parameterless — `Execute` ignores its
parameter — so per-commit Jump/Restore are cleanest as row-owned commands calling back into the parent,
rather than adding a `RelayCommand<T>`).

```csharp
using System;
using System.Windows.Input;

namespace FruityLink.Ui.Avalonia.ViewModels;

public sealed class CommitVm : ViewModelBase
{
    private readonly VersionControlViewModel _owner;
    private bool _isCurrent;
    private bool _isAhead;       // ahead of Current ⇒ redoable/"undone" (dimmed)
    private bool _confirming;    // inline confirm strip is showing for THIS row

    public CommitVm(VersionControlViewModel owner, Services.VersionCommit model)
    {
        _owner = owner;
        Model = model;
        JumpCommand          = new RelayCommand(() => _owner.BeginJump(this), () => !_isCurrent && !_owner.IsBusy);
        ConfirmJumpCommand   = new RelayCommand(() => _ = _owner.ConfirmJumpAsync(this), () => !_owner.IsBusy);
        CancelJumpCommand    = new RelayCommand(() => _owner.CancelJump(this));
        RestoreBackupCommand = new RelayCommand(() => _ = _owner.RestoreBackupAsync(this),
                                                () => Model.HasBackup && !_owner.IsBusy);
    }

    public Services.VersionCommit Model { get; }
    public string Id => Model.Id;
    public string Label => Model.Label;
    public bool HasBackup => Model.HasBackup;

    /// <summary>"2 min ago" etc. Recomputed on each Rebuild; absolute time shown in a tooltip.</summary>
    public string RelativeTime => Format(DateTimeOffset.Now - Model.Timestamp);
    public string AbsoluteTime => Model.Timestamp.LocalDateTime.ToString("f");

    public bool IsCurrent { get => _isCurrent; set { if (SetProperty(ref _isCurrent, value)) Requery(); } }
    public bool IsAhead   { get => _isAhead;   set => SetProperty(ref _isAhead, value); }
    public bool Confirming{ get => _confirming;set => SetProperty(ref _confirming, value); }

    public ICommand JumpCommand { get; }
    public ICommand ConfirmJumpCommand { get; }
    public ICommand CancelJumpCommand { get; }
    public ICommand RestoreBackupCommand { get; }

    public void Requery()
    {
        (JumpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ConfirmJumpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestoreBackupCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string Format(TimeSpan d) => d.TotalSeconds < 60 ? "just now"
        : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago"
        : d.TotalHours   < 24 ? $"{(int)d.TotalHours} hr ago"
        : $"{(int)d.TotalDays} d ago";
}
```

### `ViewModels/VersionControlViewModel.cs` — the panel

```csharp
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using FruityLink.Ui.Avalonia.Services;

namespace FruityLink.Ui.Avalonia.ViewModels;

public sealed class VersionControlViewModel : ViewModelBase
{
    private IProjectVersionControl _vc;
    private readonly RelayCommand _undo, _redo, _restoreRecovery, _dismissRecovery, _refresh;
    private bool _isBusy;
    private string? _status;
    private CommitVm? _current;
    private CommitVm? _pendingJump;   // row whose inline confirm is open

    public VersionControlViewModel(IProjectVersionControl vc)
    {
        _vc = vc ?? throw new ArgumentNullException(nameof(vc));
        _undo            = new RelayCommand(() => _ = UndoAsync(),  () => CanUndo && !_isBusy);
        _redo            = new RelayCommand(() => _ = RedoAsync(),  () => CanRedo && !_isBusy);
        _restoreRecovery = new RelayCommand(() => _ = AcceptRecoveryAsync(), () => HasRecovery && !_isBusy);
        _dismissRecovery = new RelayCommand(DismissRecovery, () => HasRecovery);
        _refresh         = new RelayCommand(Rebuild);
        Subscribe();
        Rebuild();
    }

    public ObservableCollection<CommitVm> Commits { get; } = new();  // newest first (display order)

    public ICommand UndoCommand => _undo;
    public ICommand RedoCommand => _redo;
    public ICommand RestoreRecoveryCommand => _restoreRecovery;
    public ICommand DismissRecoveryCommand => _dismissRecovery;
    public ICommand RefreshCommand => _refresh;

    public bool HasCommits => Commits.Count > 0;
    public CommitVm? Current { get => _current; private set => SetProperty(ref _current, value); }

    public bool CanUndo => Current is not null && _vc.History.Any(c => c.Id == Current.Id)
                           && _vc.History.TakeWhile(c => c.Id != Current.Id).Any() // has an older parent chain
                           || (Current is not null && _vc.Current?.ParentId is not null);
    public bool CanRedo => Commits.Any(c => c.IsAhead);

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RequeryAll(); } }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }

    public string Summary => $"{Commits.Count} states · backups: {Commits.Count(c => c.HasBackup)}";

    // ---- crash recovery ----
    public bool HasRecovery => _vc.PendingRecovery is not null;
    public string? RecoveryLabel => _vc.PendingRecovery?.Label;

    // ---- attach the real gateway (plugin, UI thread) ----
    public void AttachGateway(IProjectVersionControl vc)
    {
        Unsubscribe();
        _vc = vc ?? throw new ArgumentNullException(nameof(vc));
        Subscribe();
        Rebuild();
    }

    private void Subscribe()   => _vc.Changed += OnBackendChanged;
    private void Unsubscribe() => _vc.Changed -= OnBackendChanged;

    // Fires off-thread → marshal onto the UI thread before touching the ObservableCollection.
    private void OnBackendChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Rebuild();
        else Dispatcher.UIThread.Post(Rebuild);
    }

    /// <summary>Rebuild the row list from backend History (newest first) + recompute Current/ahead flags.</summary>
    public void Rebuild()
    {
        var history = _vc.History;                 // oldest → newest
        var curId = _vc.Current?.Id;
        int curIdx = curId is null ? -1 : IndexOf(history, curId);

        Commits.Clear();
        for (int i = history.Count - 1; i >= 0; i--)   // newest first
        {
            var row = new CommitVm(this, history[i])
            {
                IsCurrent = history[i].Id == curId,
                IsAhead   = curIdx >= 0 && i > curIdx,  // later than Current ⇒ redoable
            };
            Commits.Add(row);
        }
        Current = Commits.FirstOrDefault(c => c.IsCurrent);
        _pendingJump = null;

        OnPropertyChanged(nameof(HasCommits));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(HasRecovery));
        OnPropertyChanged(nameof(RecoveryLabel));
        RequeryAll();
    }

    // ---- toolbar undo / redo ----
    private async Task UndoAsync() => await Run(() => _vc.UndoAsync());
    private async Task RedoAsync() => await Run(() => _vc.RedoAsync());

    // ---- per-row jump (2-step inline confirm when it discards newer edits) ----
    public void BeginJump(CommitVm row)
    {
        CancelJump(_pendingJump);
        // Only older-than-Current jumps are destructive; jumping forward (redo) needs no confirm.
        if (row.IsAhead) { _ = ConfirmJumpAsync(row); return; }
        _pendingJump = row;
        row.Confirming = true;
    }

    public void CancelJump(CommitVm? row) { if (row is not null) row.Confirming = false; if (ReferenceEquals(row, _pendingJump)) _pendingJump = null; }

    public async Task ConfirmJumpAsync(CommitVm row)
    {
        row.Confirming = false; _pendingJump = null;
        await Run(() => _vc.RestoreAsync(row.Id));
    }

    public async Task RestoreBackupAsync(CommitVm row) => await Run(() => _vc.RestoreBackupAsync(row.Id));

    // ---- recovery ----
    private async Task AcceptRecoveryAsync() => await Run(() => _vc.AcceptRecoveryAsync());
    public void DismissRecovery() { _vc.DismissRecovery(); Rebuild(); }

    private async Task Run(Func<Task> op)
    {
        if (_isBusy) return;
        IsBusy = true; Status = "Working…";
        try { await op().ConfigureAwait(true); Status = null; }
        catch (Exception ex) { Status = "Failed: " + ex.Message; }
        finally { IsBusy = false; }   // Changed → Rebuild refreshes the list
    }

    private void RequeryAll()
    {
        _undo.RaiseCanExecuteChanged(); _redo.RaiseCanExecuteChanged();
        _restoreRecovery.RaiseCanExecuteChanged(); _dismissRecovery.RaiseCanExecuteChanged();
        foreach (var c in Commits) c.Requery();
    }

    private static int IndexOf(IReadOnlyList<VersionCommit> h, string id)
    { for (int i = 0; i < h.Count; i++) if (h[i].Id == id) return i; return -1; }
}
```

> `CanUndo` is shown simplified above; the backend is the source of truth — the cleanest final form is to
> let the backend expose `CanUndo`/`CanRedo` (or infer: undo enabled when `Current?.ParentId != null`,
> redo enabled when any history entry is `IsAhead`). Keep whichever the sibling backend exposes.

### Additions to `ChatViewModel`

```csharp
private bool _isVersionsOpen;
public VersionControlViewModel Versions { get; }        // ctor: new(new InMemoryProjectVersionControl())
public ICommand ToggleVersionsCommand { get; }          // ctor: new RelayCommand(() => { IsVersionsOpen = !IsVersionsOpen; })

public bool IsVersionsOpen
{
    get => _isVersionsOpen;
    set { if (SetProperty(ref _isVersionsOpen, value) && value) { IsSettingsOpen = false; Versions.Refresh(); } }
}

// mutual exclusion: also force IsVersionsOpen=false inside the IsSettingsOpen setter.

/// <summary>Attach the real VC gateway (plugin presenter, UI thread), mirroring AttachBackendGateway.</summary>
public void AttachVersionControl(IProjectVersionControl vc) => Versions.AttachGateway(vc);

// Recovery banner shown on the CHAT surface reads through to the panel VM:
public bool HasRecovery => Versions.HasRecovery;
public string? RecoveryLabel => Versions.RecoveryLabel;
```
(`Refresh()` = alias for `RefreshCommand`/`Rebuild`; expose a public `void Refresh() => Rebuild();`.)

---

## 6. AXAML

### `Views/VersionControlView.axaml` (new UserControl, `x:DataType="vm:ChatViewModel"`)

Binds through `Versions.*` (the view inherits the window's `ChatViewModel`, like `SettingsView`).

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:FruityLink.Ui.Avalonia.ViewModels"
             x:Class="FruityLink.Ui.Avalonia.Views.VersionControlView"
             x:DataType="vm:ChatViewModel">
  <Grid RowDefinitions="Auto,Auto,*,Auto" ClipToBounds="True">

    <!-- Header (matches SettingsView header) -->
    <Border Grid.Row="0" Background="{StaticResource SurfaceTranslucentBrush}"
            BorderBrush="{StaticResource BorderWhite10Brush}" BorderThickness="0,0,0,1" Padding="14,12">
      <StackPanel Orientation="Horizontal" Spacing="12" VerticalAlignment="Center">
        <Button Classes="icon" Width="34" Height="34" Command="{Binding ToggleVersionsCommand}"
                ToolTip.Tip="Back to chat">
          <Viewbox Width="18" Height="18">
            <Path Fill="White" Data="M20 11H7.83l5.59-5.59L12 4l-8 8 8 8 1.41-1.41L7.83 13H20v-2z" />
          </Viewbox>
        </Button>
        <StackPanel Spacing="1" VerticalAlignment="Center">
          <TextBlock Classes="h" Text="Version history" />
          <TextBlock Classes="meta" Text="The AI's edits, grouped by work-unit. Jump to any state." />
        </StackPanel>
      </StackPanel>
    </Border>

    <!-- Toolbar: prominent Undo / Redo + summary -->
    <Grid Grid.Row="1" ColumnDefinitions="Auto,Auto,*,Auto" Margin="16,12,16,4">
      <Button Grid.Column="0" Classes="pill secondary" MinHeight="38" Margin="0,0,8,0"
              Command="{Binding Versions.UndoCommand}" Content="↶  Undo" />
      <Button Grid.Column="1" Classes="pill secondary" MinHeight="38"
              Command="{Binding Versions.RedoCommand}" Content="↷  Redo" />
      <TextBlock Grid.Column="3" Classes="meta" VerticalAlignment="Center" Text="{Binding Versions.Summary}" />
    </Grid>

    <!-- Recovery banner (only if HasRecovery) -->
    <Border Grid.Row="1" Margin="16,56,16,0" Classes="glass" Padding="12,10"
            IsVisible="{Binding Versions.HasRecovery}"
            BorderBrush="{StaticResource Magenta400Brush}">
      <!-- ...icon + RecoveryLabel + [Restore][Dismiss] bound to RestoreRecoveryCommand/DismissRecoveryCommand -->
    </Border>

    <!-- Timeline -->
    <ScrollViewer Grid.Row="2" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
      <ItemsControl ItemsSource="{Binding Versions.Commits}" Margin="16,8,16,24">
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="vm:CommitVm">
            <Border Classes="glass" Padding="12,10" Margin="0,4"
                    Opacity="{Binding IsAhead, Converter={x:Static ...BoolToDimOpacity}}"
                    BorderBrush="{Binding IsCurrent, Converter=...CurrentBorderBrush}">
              <StackPanel Spacing="6">
                <Grid ColumnDefinitions="Auto,*,Auto">
                  <!-- current dot: brand gradient when IsCurrent, else hollow -->
                  <Border Grid.Column="0" Width="10" Height="10" CornerRadius="9999" VerticalAlignment="Center"
                          Background="{StaticResource BrandGradientBrush}" IsVisible="{Binding IsCurrent}" />
                  <TextBlock Grid.Column="1" Classes="settingLabel" Margin="10,0,0,0" Text="{Binding Label}"
                             ToolTip.Tip="{Binding AbsoluteTime}" />
                  <StackPanel Grid.Column="2" Orientation="Horizontal" Spacing="8">
                    <Border Classes="toolChip" IsVisible="{Binding HasBackup}">
                      <TextBlock Classes="mono" Text="backup" />
                    </Border>
                    <TextBlock Classes="meta" VerticalAlignment="Center" Text="{Binding RelativeTime}" />
                  </StackPanel>
                </Grid>

                <!-- NOW badge -->
                <TextBlock Classes="badge" Text="NOW" IsVisible="{Binding IsCurrent}" />

                <!-- Row actions (hidden on Current; hidden while confirming) -->
                <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding !IsCurrent}">
                  <StackPanel.IsVisible><!-- !IsCurrent && !Confirming (MultiBinding/converter) --></StackPanel.IsVisible>
                  <Button Classes="pill secondary" MinHeight="32" Content="Jump here"
                          Command="{Binding JumpCommand}" />
                  <Button Classes="pill secondary" MinHeight="32" Content="Restore .flp"
                          IsVisible="{Binding HasBackup}" Command="{Binding RestoreBackupCommand}" />
                </StackPanel>

                <!-- Inline destructive confirm (replaces actions) -->
                <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding Confirming}">
                  <TextBlock Classes="settingDesc" VerticalAlignment="Center"
                             Text="Discard newer edits and jump here?" />
                  <Button Classes="pill danger" MinHeight="32" Content="Jump" Command="{Binding ConfirmJumpCommand}" />
                  <Button Classes="pill secondary" MinHeight="32" Content="Cancel" Command="{Binding CancelJumpCommand}" />
                </StackPanel>
              </StackPanel>
            </Border>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </ScrollViewer>

    <!-- Empty state -->
    <StackPanel Grid.Row="2" IsVisible="{Binding !Versions.HasCommits}" VerticalAlignment="Center"
                HorizontalAlignment="Center" MaxWidth="320" Spacing="8">
      <TextBlock Classes="muted" TextAlignment="Center"
                 Text="No AI edits yet. As FL Automate works, each change appears here so you can undo or jump back." />
    </StackPanel>

    <!-- Footer status -->
    <TextBlock Grid.Row="3" Classes="meta" Margin="16,0,16,12"
               IsVisible="{Binding Versions.Status, Converter={x:Static ObjectConverters.IsNotNull}}"
               Text="{Binding Versions.Status}" />
  </Grid>
</UserControl>
```

Small helper converters (put in a `Theme`/`Converters` static class, or reuse Avalonia's built-ins):
`IsAhead → Opacity 0.5/1.0`, `IsCurrent → Brand500Brush/BorderWhite10Brush`, and a
`!IsCurrent && !Confirming` visibility. Minimal `IValueConverter`s, consistent with the lib's no-toolkit
stance. (Avalonia's `!` binding + `ObjectConverters.IsNotNull` are already used in the codebase.)

### Header button in `Views/ChatWindow.axaml`

Add a history button before the gear (put both in a column, or add a column). Sketch:

```xml
<!-- history/versions → opens the VC overlay (left of the gear) -->
<Button Grid.Column="4" Classes="icon" Width="34" Height="34" Margin="10,0,0,0"
        VerticalAlignment="Center" Command="{Binding ToggleVersionsCommand}"
        ToolTip.Tip="Version history">
  <Viewbox Width="17" Height="17">
    <Path Fill="{StaticResource BodyTextBrush}"
          Data="M13 3a9 9 0 0 0-9 9H1l3.9 3.9.1.1L9 12H6a7 7 0 1 1 7 7 6.9 6.9 0 0 1-4.9-2L6.7 18.4A9 9 0 1 0 13 3zm-1 5v5l4.3 2.5.7-1.2-3.5-2.1V8z" />
  </Viewbox>
</Button>
<!-- move the existing gear to Grid.Column="5" and add a 6th ColumnDefinition="Auto" -->
```

### Version-control overlay in `ChatWindow.axaml`

Beside the existing `<views:SettingsView .../>`:
```xml
<views:VersionControlView IsVisible="{Binding IsVersionsOpen}" />
```
and gate the chat surface on both: `IsVisible="{Binding !IsSettingsOpen}"` → add an `AnyOverlayOpen`
inverse (or a small converter) so the chat hides when *either* overlay is open. Simplest: expose
`bool IsChatVisible => !IsSettingsOpen && !IsVersionsOpen;` on `ChatViewModel` and bind the chat grid's
`IsVisible` to it (raise `OnPropertyChanged(nameof(IsChatVisible))` from both setters).

### Recovery banner on the chat surface (optional but recommended)

At the top of the chat transcript grid, a dismissible banner bound to `HasRecovery`/`RecoveryLabel` with
Restore → `Versions.RestoreRecoveryCommand` and "Review" → `ToggleVersionsCommand`, styled `infoBubble`
with a magenta rule (reuses the thought-block accent-rule pattern). Ensures the crash-recovery prompt is
seen on open without navigating to the panel.

---

## 7. Wiring to `IProjectVersionControl`

Exactly parallels the backend-settings seam:

1. **UI lib** — `Services/IProjectVersionControl.cs` (interface + `VersionCommit` + `RecoveryInfo` +
   `InMemoryProjectVersionControl` stub). `ChatViewModel` constructs `Versions` with the stub;
   `AttachVersionControl(vc)` swaps in the real one.

2. **Plugin adapter** — `FruityLink.Plugins.FlAgent/Composition/AgentVersionControlGateway.cs`:
   ```csharp
   internal sealed class AgentVersionControlGateway : IProjectVersionControl   // UI-local iface
   {
       private readonly FruityLink.Core.Abstractions.IProjectVersionControl _backend; // real one
       public AgentVersionControlGateway(Core...IProjectVersionControl backend) { _backend = backend;
           _backend.Changed += () => Changed?.Invoke(); }
       public IReadOnlyList<VersionCommit> History => _backend.History.Select(Map).ToList();
       public VersionCommit? Current => _backend.Current is { } c ? Map(c) : null;
       public Task UndoAsync(CancellationToken ct = default) => _backend.UndoAsync(ct);
       public Task RestoreAsync(string id, CancellationToken ct = default) => _backend.RestoreAsync(id, ct);
       // ...RedoAsync/RestoreBackupAsync/recovery/CommitAsync map through; Map converts the records.
       public event Action? Changed;
       private static VersionCommit Map(Core...VersionCommit m) =>
           new(m.Id, m.Timestamp, m.Label, m.HasBackup, m.ParentId);
   }
   ```

3. **Composition** — add to `AgentComposition`:
   ```csharp
   public static IProjectVersionControl? BuildVersionControlGateway(IPluginContext ctx)
       => ctx.Services?.GetService(typeof(Core...IProjectVersionControl)) is Core...IProjectVersionControl vc
          ? new AgentVersionControlGateway(vc) : null;
   ```
   (Resolve the host's real VC service; return null if the host doesn't provide one — the UI then keeps
   the in-memory stub, or we hide the history button. Prefer: keep the stub so the surface stays alive.)

4. **Presenter** — in `AvaloniaChatPresenter` ctor, accept an optional `IProjectVersionControl? versions`
   and, like the backend gateway:
   ```csharp
   if (versions is not null) _host.Post(() => _vm.AttachVersionControl(versions));
   ```

5. **Plugin** — in `FlAgentPlugin.EnableAvalonia`, build and pass it:
   ```csharp
   var versions = AgentComposition.BuildVersionControlGateway(context);
   var presenter = new AvaloniaChatPresenter(host, chat.ViewModel, agent, context.Log,
                                             dictation, ownsDictation, backendSettings, versions);
   ```

No new project references leak into the UI lib; the plugin already references both Core and the UI lib.

---

## 8. Live updates

- The VM subscribes to `IProjectVersionControl.Changed` in `Subscribe()` and marshals to the UI thread
  (`Dispatcher.UIThread.CheckAccess()/Post`) before calling `Rebuild()`. This mirrors how the presenter
  marshals agent deltas via `_host.Post`.
- **Trigger point**: after each AI work-unit the backend appends a commit and raises `Changed` → a new row
  animates in at the top of the timeline, and `Current`/`Summary`/`CanUndo` update — even while the panel
  is closed (so re-opening is instant and correct). If the panel is a heavy list later, gate `Rebuild()`
  on "panel open OR small history", but for a chat-scale history a full rebuild per change is fine.
- `AttachGateway` re-subscribes (unsub old, sub new) so swapping stub→real is clean.
- Dispose: the VM should `Unsubscribe()` if the window is torn down (add to `AvaloniaChatPresenter.Dispose`
  or a `ChatViewModel` cleanup) to avoid a dangling handler on the long-lived backend.

## 9. Crash-recovery UX

- Backend exposes `PendingRecovery` (a newer-than-saved `.flp` backup detected at startup) + raises
  `Changed`. On attach/rebuild, `HasRecovery` becomes true.
- **On window open**: the **recovery banner** shows on the chat surface (magenta-ruled `infoBubble`):
  "Recovered work available — FL closed with unsaved edits: *<label>*  [Restore] [Review] [Dismiss]".
  - **Restore** → `Versions.RestoreRecoveryCommand` → `AcceptRecoveryAsync()` (opens the newer backup /
    fast-forwards to it), banner clears.
  - **Review** → `ToggleVersionsCommand` opens the panel with the same banner atop the timeline.
  - **Dismiss** → `DismissRecovery()` (backend drops the pending flag), banner clears.
- No modal — consistent with the app's dialog-free design. The banner is the only startup interruption.

## 10. Undo / Redo affordances + destructive confirm + keyboard

- **Prominent Undo/Redo** as pill buttons in the panel toolbar, `CanExecute`-gated by `CanUndo`/`CanRedo`
  so they disable at the ends of history and reflect `Current` position.
- **Per-commit jump** = `RestoreAsync`. Jumping to an **older** state is destructive (discards newer /
  branches per backend model) → **inline two-step confirm** (row buttons swap to
  "Discard newer edits and jump here? [Jump][Cancel]"). Jumping **forward** (a redoable `IsAhead` commit)
  is non-destructive → executes immediately.
- **Restore .flp backup** = `RestoreBackupAsync`, shown only when `HasBackup`. (Treat as destructive too
  if it replaces the live project — reuse the same inline confirm if the backend says so.)
- **Keyboard (optional)**: the app currently intercepts Enter/Shift+Enter via a *tunneled* `KeyDown` on
  the composer. Ctrl+Z inside the composer is text-undo, and FL owns a global Ctrl+Z, so **do not** bind a
  bare Ctrl+Z to project-undo. If shortcuts are wanted, add window-level `KeyBindings` with
  **Ctrl+Alt+Z / Ctrl+Alt+Y** routed to `Versions.UndoCommand`/`RedoCommand`, active regardless of the
  open surface. Document that the primary affordance is the buttons; shortcuts are additive.

---

## 11. Build plan (ordered)

New files (UI lib):
1. `src/FruityLink.Ui.Avalonia/Services/IProjectVersionControl.cs` — iface + `VersionCommit` +
   `RecoveryInfo` + `InMemoryProjectVersionControl` stub (seed sample commits).
2. `src/FruityLink.Ui.Avalonia/ViewModels/CommitVm.cs`.
3. `src/FruityLink.Ui.Avalonia/ViewModels/VersionControlViewModel.cs`.
4. `src/FruityLink.Ui.Avalonia/Views/VersionControlView.axaml` (+ `.axaml.cs` mirroring `SettingsView`;
   `AvaloniaXamlLoader.Load(this)`).
5. `src/FruityLink.Ui.Avalonia/Theme/Converters.cs` (tiny `IValueConverter`s: dim-opacity,
   current-border, current+confirm visibility) — or fold into existing binding tricks.

Edits (UI lib):
6. `ViewModels/ChatViewModel.cs` — `Versions` property (stub), `IsVersionsOpen` (mutual-exclusion +
   `Refresh`), `ToggleVersionsCommand`, `IsChatVisible`, `AttachVersionControl`, `HasRecovery`/
   `RecoveryLabel` pass-throughs.
7. `Views/ChatWindow.axaml` — history header button; bind chat grid `IsVisible` to `IsChatVisible`; add
   `<views:VersionControlView IsVisible="{Binding IsVersionsOpen}" />`; optional recovery banner on the
   chat surface.
8. (optional) `Views/ChatWindow.axaml.cs` — window `KeyBindings` for Ctrl+Alt+Z/Y.

Edits (plugin — bind to real backend):
9. `Composition/AgentVersionControlGateway.cs` (new) — adapter Core `IProjectVersionControl` → UI-local.
10. `Composition/AgentComposition.cs` — `BuildVersionControlGateway(context)`.
11. `Ui/AvaloniaChatPresenter.cs` — accept `IProjectVersionControl? versions`, `_host.Post(AttachVersionControl)`;
    unsubscribe on `Dispose`.
12. `FlAgentPlugin.cs` (`EnableAvalonia`) — build + pass the gateway.

Verify:
13. Standalone `dotnet run` on the UI lib head → history button opens the panel populated by the stub;
    undo/redo/jump/inline-confirm all interactive with no backend.
14. In-FL: real gateway drives it; new AI work-units appear live; crash-recovery banner on next launch.

Test surface (mirror `BackendSettingsViewModel` unit tests if present): `VersionControlViewModel` against a
fake `IProjectVersionControl` — Rebuild ordering (newest first), Current/IsAhead flags, CanUndo/CanRedo,
inline-confirm state machine, `Changed`→Rebuild marshalling, recovery flags.

---

### Notes for the backend sibling
- UI needs `RestoreBackupAsync(commitId)` + recovery members (`PendingRecovery`/`AcceptRecoveryAsync`/
  `DismissRecovery`) in addition to the shared contract; fold `RestoreBackupAsync` into
  `RestoreAsync(id, fromBackup)` if preferred — one adapter line changes.
- Expose `CanUndo`/`CanRedo` (or guarantee `Current.ParentId`/`IsAhead` semantics) so the UI doesn't
  re-derive reachability.
- Decide the jump model (discard-newer vs branch) and expose enough for the confirm copy
  ("discard N newer edits"): a `CountNewerThan(commitId)` or the branch semantics.
```
