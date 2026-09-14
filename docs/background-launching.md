# Background FL Studio processes

`FruityLink.Core.Hosting` provides the low-level Windows launch primitive used by automated build tools. It starts a normal GUI process with its normal message pump while keeping its windows off the user's interactive desktop.

```csharp
using FruityLink.Core.Hosting;

await using var studio = FlStudioProcessLauncher.Start(new(@"C:\Program Files\Image-Line\FL Studio 2025\FL64.exe")
{
    Arguments = [@"C:\jobs\song.flp"],
    Environment = new Dictionary<string, string?>
    {
        ["FRUITYLINK_AUTOMATION"] = "1",
        ["FRUITYLINK_AUTOMATION_DISCOVERY"] = @"C:\jobs\session-1\scripting"
    },
    Mode = FlStudioLaunchMode.PrivateDesktop
});

await studio.WaitForExitAsync(cancellationToken);
```

Private launches serialize FL's cold-start phase for the same executable. FL Studio 26 can run concurrent jobs after initialization, but simultaneous cold starts can leave one instance without fully initialized native UI and song objects. The SDK therefore holds a cross-process mutex until FL signals the per-launch `FRUITYLINK_STARTUP_EVENT`, or until the consumer calls `CompleteStartup()`. The gate is released automatically if the process exits or the lease is disposed. `StartupTimeout` bounds mutex acquisition and `Start(options, cancellationToken)` supports cancellation. Set `SerializeStartup = false` only when the caller already provides equivalent coordination.

The startup event is a readiness heuristic calibrated to FruityLink's native `fl_ready` check, not a security boundary. Consumers should still authenticate scripting endpoints and validate process identity.

Private mode creates a unique Win32 desktop but never calls `SwitchDesktop`. Each lease also owns a Windows Job Object configured to terminate its assigned process tree when the lease closes. The process starts suspended, enters that job, and only then resumes, so child processes cannot escape during startup. Independent leases use independent desktops and jobs, which permits concurrent automation jobs.

Arguments are quoted with Windows `CommandLineToArgvW` rules and environment changes use a Unicode environment block. Set an environment value to `null` to remove an inherited variable. Interactive mode is available when the user needs to see and control FL Studio:

```csharp
var options = new FlStudioLaunchOptions(flPath)
{
    Mode = FlStudioLaunchMode.Interactive
};
```

`ReadWindows` enumerates only top-level windows belonging to the launched process on its assigned desktop. It returns handles, class names, titles, visibility, enabled state, and owner state without changing the foreground desktop. Consumers may use these snapshots to report blocked dialogs. Handle values are transient and must be revalidated before any response.

Dispose every lease. `TerminateAsync` provides explicit cancellation-aware shutdown; disposal is the final orphan-prevention boundary. The lease never attaches to or terminates a process it did not create.

## Standalone Python builds

Install the framework including its `tools/session-host` component and use the matching `fruitylink-python` package. No MCP client, server, or plugin is required. The installer includes a private Python runtime; an existing Python 3.11+ environment can also install the SDK wheel.

```python
from fruitylink import launch

with launch(
    r"C:\Program Files\Image-Line\FL Studio 2026\FL64.exe",
    r"C:\jobs\new-song.flp",  # Must not already exist.
    background=True,
) as session:
    fl = session.studio
    fl.transport.tempo = 140
    channel = fl.channels.add("3x Osc", name="Build synth")
    pattern = fl.patterns.create("Build notes")
    pattern.notes.add_beats(channel=channel.index, key=64, start=0, length=4)
    fl.playlist.add_pattern_ticks(pattern.index, track=1, start=0, length=4 * fl.timebase.ppq)
    fl.transport.song_mode = True
    session.save()
    session.render(r"C:\jobs\new-song.wav")
```

`session.render_range(path, start_tick=..., length_tick=...)` renders one section instead: it trims the live project with [`fruitylink.audition.isolate_range`](python/api.md#render-one-section) and then behaves like `render()`. The WAV ends exactly at the range unless `tail_beats` keeps an `End` marker past it for the tails.

`source=` optionally supplies an existing FLP to copy. The source is never opened by FL; the new working copy is. `save()` writes that working copy, while `save(new_path)` creates a new snapshot. `render()` snapshots the session, closes its editor, and starts a separate owned export process. It refuses existing output paths and validates the resulting WAV. Closing a session without saving discards unsaved changes. A failed render preserves its snapshot under the session's `job_directory`.

Each `launch()` owns a separate job, process, desktop, discovery directory and project. Use separate output paths and one session per worker to run concurrent builds. Calls into one session remain serialized. Discovery verifies the launched PID and instance identity before returning a `Studio`.

Standalone startup bypasses FruityLink plugin prewarming, UI, enable/disable preferences and menus. FL's instruments and effects still load normally. Unexpected modal dialogs fail with a readable message and a saved diagnostic; standalone sessions do not automatically answer them. MCP retains its separately documented exact-dialog recovery policy.

## Meaning of background mode

This is a private Windows desktop, **not a renderer-free FL engine**. Windows and FL's message pump still exist, and plugins may continue drawing internally. The SDK never switches your active desktop. This is also not a security sandbox or an isolated Windows user profile: FL preferences, plugin licenses, external services and audio devices can still be shared.

FL's documented command-line exporter provides audio export, not full headless authoring. [Image-Line export documentation](https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/fformats_save_export.htm). Windows supports assigning a process to a private desktop at creation. [Windows desktops](https://learn.microsoft.com/en-us/windows/win32/winstation/desktops), [STARTUPINFO desktop selection](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow).

Concurrent real-time playback needs a multi-client audio driver. Offline builds still consume memory, CPU, graphics resources and desktop heap; choose a bounded worker count. Third-party bridged plugins, external activation dialogs and service-launched helper processes need individual compatibility testing. No blanket promise of service/session-zero operation or every plugin's behavior is made.

## Live verification — September 13, 2026

On FL Studio **26.1.3.5570**, two standalone Python workers authored 24 notes each, saved and reopened them, and exported non-silent stereo WAVs containing 493,714 frames each. Both owned FL instances were alive together, on different private desktops, with neither process represented by a top-level window on the controller's active desktop. The standalone framework endpoints were used; no MCP plugin was required.

A separate Serum 2 test authored an E-minor chord, saved, and exported 96,000 frames at 48 kHz with nonzero audio. Two independent MCP clients also authored and rendered concurrently, and exact invalid-note recovery worked on a private desktop while preserving its source file. These are representative checks, not coverage of every plugin or audio driver. FL 2025 has not been tested live for this feature.

The first simultaneous cold-start test exposed a race in FL initialization. The shared startup gate resolved the subsequent two-worker tests. Another test identified FL's normal `TWAVRenderForm` progress window as modal; the monitor now permits that exact render-progress form only during export, while continuing to inspect other dialogs and enforce the timeout.

Local receipts and reproducible fixtures are under `artifacts/background-20260913/`, including `live-verification.json`, `standalone-progress-live.log`, `vst-live.log`, and the per-run project/WAV results. These generated fixtures are not distributed as user assets.
