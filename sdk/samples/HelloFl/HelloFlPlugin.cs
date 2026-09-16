using FruityLink.Plugins.Abstractions;

namespace HelloFl;

/// <summary>
/// Teaching sample: the smallest useful FruityLink plugin.
///
/// It shows the three things almost every plugin does:
/// <list type="number">
///   <item>Contribute a command to one of FL Studio's native menus (here: Tools ▸ "Hello FL: Log tempo"),</item>
///   <item>Contribute a toggle button to FL's main toolbar,</item>
///   <item>Talk to FL Studio through the safe, typed control surface (<c>context.Fl</c>).</item>
/// </list>
///
/// Rules this sample demonstrates:
/// <list type="bullet">
///   <item>The constructor stays empty — all work happens in <see cref="EnableAsync"/> and is fully
///   undone in <see cref="DisableAsync"/> (both must be idempotent).</item>
///   <item>Menu/toolbar callbacks fire on FL's UI thread, so anything that awaits the bridge is pushed
///   to the thread pool instead of blocking that thread.</item>
///   <item>Registration handles are kept and disposed on disable. (The host also sweeps a plugin's
///   contributions automatically when it is disabled — disposing yourself is still good hygiene and
///   lets you remove a single entry early.)</item>
/// </list>
/// </summary>
public sealed class HelloFlPlugin : IFlPlugin
{
    // Handles returned by the menu/toolbar registrars. Disposing one removes that contribution.
    private readonly List<IDisposable> _registrations = new();

    // Backing state for the toolbar toggle. The registrar POLLS this via the isActive callback while
    // FL rebuilds its toolbar, so it must be a cheap, non-blocking read (a plain field is perfect).
    private bool _toggleOn;

    public string Id => "hello-fl";
    public string Name => "Hello FL";
    public string Description => "Sample plugin: Tools-menu tempo logger + a toolbar toggle.";
    public string Version => "0.1.0";

    public Task EnableAsync(IPluginContext context, CancellationToken ct = default)
    {
        // 1) A plain command in FL's native Tools dropdown. The callback runs on FL's UI thread;
        //    reading the tempo goes through the bridge, so do it on the thread pool and log the result.
        _registrations.Add(context.Menu.AddCommand(
            FlNativeMenu.Tools,
            "Hello FL: Log tempo",
            onInvoke: () => _ = Task.Run(async () =>
            {
                try
                {
                    double bpm = await context.Fl.GetTempoAsync();
                    context.Log($"[hello-fl] current tempo: {bpm:0.###} BPM");
                }
                catch (Exception ex)
                {
                    // A plugin must never let a failure escape into the host — report and carry on.
                    context.Log($"[hello-fl] reading the tempo failed: {ex.Message}");
                }
            })));

        // 2) A toggle button on FL's main toolbar (metronome-style). The caption is a short glyph
        //    drawn on the square button face; isActive is polled for the lit state.
        _registrations.Add(context.Toolbar.AddToggle(
            caption: "HF",
            tooltip: "Hello FL sample toggle",
            isActive: () => _toggleOn,
            onToggled: () =>
            {
                _toggleOn = !_toggleOn;
                context.Log($"[hello-fl] toolbar toggle is now {(_toggleOn ? "ON" : "OFF")}");
            }));

        context.Log("[hello-fl] enabled — see Tools ▸ \"Hello FL: Log tempo\" and the HF toolbar button");
        return Task.CompletedTask;
    }

    public Task DisableAsync(CancellationToken ct = default)
    {
        // Undo everything EnableAsync did. Idempotent: a second call finds an empty list.
        foreach (IDisposable registration in _registrations)
        {
            try { registration.Dispose(); }
            catch { /* disposal must never throw into the host */ }
        }
        _registrations.Clear();
        _toggleOn = false;
        return Task.CompletedTask;
    }
}
