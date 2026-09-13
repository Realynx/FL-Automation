# Native plugin window consumers

FL Python IDE and FL Automate use the same SDK window host. Each plugin's `context.Windows` owns an independent native form; additional windows can use `IFlWindowHostFactory`. Native FL chrome owns frame dragging, resizing and close/hide. Avalonia owns the content and its normal controls. The product header keeps its settings/history buttons, and the IDE keeps its editor toolbar.

| Consumer | Initial content size | Minimum content size | Native caption |
| --- | --- | --- | --- |
| Python IDE | 1120 × 780 DIPs | 820 × 560 DIPs | FL Python IDE |
| FL Automate Avalonia | 540 × 800 DIPs | 430 × 580 DIPs | FL Automate |

`EmbeddedAvaloniaView.GetWindowOptions` converts the window's desired dimensions to physical pixels using its current render scale. The native frame receives **content** dimensions, excluding its own chrome. Do not subtract guessed titlebar heights or apply FL-version-specific offsets in a consumer. `PinToHostContent` keeps the child aligned during host resize and DPI changes.

## Integration order

Create the toolkit window on its UI thread. Resolve its handle without putting a floating window on screen, then await `IAsyncFlWindowHost.TryEmbedAsync` on that same UI context. A shared Avalonia dispatcher must remain free while FL creates another plugin's native form: never use `.Wait()`, `.Result`, or a synchronous bridge probe before the async call.

```csharp
view.PrepareForEmbedding();
bool embedded = await native.TryEmbedAsync(
    view.Handle, view.GetWindowOptions("My plugin"), cancellationToken: ct);
if (embedded)
    view.PinToHostContent(native.LastInsetX, native.LastInsetY);
else
    view.ShowExternal();
```

The SDK's `false` result permits an external fallback after rollback. An exception may indicate that detachment could not be confirmed; propagate it into lifecycle cleanup instead of showing another window over a still-parented child. The default plugin window session now exposes `IAsyncFlWindowHost`. Existing third-party plugins still load, but an unchanged synchronous `TryEmbed` call can return an async-required diagnostic and use their external fallback. They must adopt the async API to obtain independent native frames. First-party consumers are migrated; this is not an automatic native-window upgrade for every existing binary.

Initial native display does not request activation. An explicit menu/toolbar reopen awaits `SetVisibleAsync(true, activate: true)` and then focuses the IDE editor or chat composer. Hide does not activate anything. Visibility queries reflect the native frame, so closing its X and reopening from the plugin toggle takes one click. External fallback reopens preserve user placement.

On disable, drain active plugin work, call `CloseAsync` on the child's UI context, and destroy the toolkit window only after the SDK confirms detachment. A `false` result retains the existing window and plugin ownership for retry. FL Automate marshals both Avalonia and WPF detachment to their respective UI threads. The shared toolkit dispatcher and Python interpreter remain available to other plugins.

## Verification boundaries

Installer 0.1.14 introduced the integration, but its live FL Studio 2026 attempt fell back to an external window: the native factory incorrectly read the `Touch` settings object as content geometry. The correction prepared for 0.1.15 queries FL's actual inner client rectangle and caption geometry. A fixture exercises the real factory with an unreadable `Touch` pointer to prevent that assumption from returning, and the IDE now records the bridge's refusal reason when using an external window.

Signature/layout evidence for a particular FL build is tracked by the native scanner documentation. That evidence does not prove mouse, keyboard, focus, DPI, resize, or simultaneous-window behavior in the live application. The earlier installer 0.1.13 IDE checks covered FL Studio 2026 under the previous host; live checks of the corrected independent-frame behavior in both 2025 and 2026 remain pending.

Headless tests cover requested IDE dimensions and focus, exact selection execution, cancellation/drain, and retaining a window after a failed detach. Shared-host tests cover native ownership and rollback independently. No UI-only test submits an AI prompt or requires an account. Enabling the actual FL Automate plugin still performs its existing account restore, model discovery and update checks; this hosting work does not change that behavior.

Windows does not change `WS_CHILD`/`WS_POPUP` automatically during `SetParent`, and incompatible DPI-awareness contexts can make reparenting fail. Those concerns belong in the SDK, which restores the original window state on rollback. [Microsoft: SetParent](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent).

Posted show operations avoid blocking one UI thread on another; a successfully posted operation is not proof that a window is already visible. Consumers await the SDK's result and query its native visibility state. [Microsoft: ShowWindowAsync](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-showwindowasync).
