using System;
using System.Threading;
using Avalonia;
using Avalonia.Threading;

namespace FruityLink.Ui.Avalonia.Hosting;

/// <summary>
/// Runs the Avalonia application + its <see cref="Dispatcher"/> IN-PROCESS on a dedicated STA thread,
/// so a flagship Avalonia UI can be hosted inside a NON-Avalonia process (FL Studio) and reparented
/// into FL's native host form. This is the Avalonia analog of a plugin's WPF <c>UiHost</c>: Avalonia
/// and WPF coexist happily as long as each owns its own dispatcher on its own thread.
///
/// <para><b>Process-singleton by necessity.</b> Avalonia can be set up exactly ONCE per process
/// (<see cref="Application.Current"/> / the platform threading are global). It also cannot be torn down
/// and re-initialized cleanly, so this host owns the Avalonia thread for the WHOLE process lifetime:
/// <see cref="EnsureStarted(Func{AppBuilder})"/> spins the thread + <c>SetupWithoutStarting</c> + a
/// permanent <see cref="Dispatcher.MainLoop"/> once and is idempotent. Plugin enable/disable just
/// creates/closes its window(s) on this thread — the thread keeps running so a later re-enable can
/// reuse it.</para>
///
/// <para><b>The application is the caller's.</b> This SDK host doesn't know the product's
/// <c>Application</c> subclass: the FIRST <see cref="EnsureStarted(Func{AppBuilder})"/> call supplies an
/// <see cref="AppBuilder"/> factory (e.g. <c>() =&gt; AppBuilder.Configure&lt;App&gt;().UsePlatformDetect().WithInterFont()</c>);
/// the host then appends its embed-safe platform options and performs the setup on the Avalonia thread.
/// Later calls (any overload) just wait for that one startup.</para>
///
/// <para><b>Rendering mode.</b> As a reparented <c>WS_CHILD</c> of FL's non-Avalonia parent, GPU /
/// composition presenters hit the same "airspace" problem WPF did (blank until input). We therefore
/// pin the Win32 backend to SOFTWARE rendering onto the window's REDIRECTION SURFACE (a GDI blit),
/// which presents reliably inside a foreign parent — the Avalonia equivalent of WPF's
/// <c>RenderMode.SoftwareOnly</c>. Perf is a non-issue for a text chat.</para>
/// </summary>
public sealed class EmbeddedAvaloniaHost
{
    private static readonly EmbeddedAvaloniaHost _instance = new();

    /// <summary>The process-wide Avalonia host.</summary>
    public static EmbeddedAvaloniaHost Instance => _instance;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private Func<AppBuilder>? _appBuilderFactory;
    private volatile Exception? _startError;

    private EmbeddedAvaloniaHost() { }

    /// <summary>
    /// Ensure the Avalonia app + dispatcher thread is up (idempotent, once per process). Blocks the
    /// caller until Avalonia is initialized (or throws if init failed — e.g. a missing native such as
    /// libSkiaSharp, which the caller catches to fall back to another UI). Requires a previous call to
    /// have supplied the <see cref="AppBuilder"/> factory (<see cref="EnsureStarted(Func{AppBuilder})"/>);
    /// throws <see cref="InvalidOperationException"/> if none ever did.
    /// </summary>
    public void EnsureStarted() => EnsureStarted(null);

    /// <summary>
    /// Ensure the Avalonia app + dispatcher thread is up (idempotent, once per process), supplying the
    /// product's <see cref="AppBuilder"/> factory used on the very first start. The factory should
    /// configure the application type + platform (e.g.
    /// <c>AppBuilder.Configure&lt;App&gt;().UsePlatformDetect().WithInterFont()</c>); this host appends the
    /// embed-safe software-rendering options and calls <c>SetupWithoutStarting</c> on the Avalonia
    /// thread. Blocks the caller until Avalonia is initialized (or throws if init failed).
    /// </summary>
    public void EnsureStarted(Func<AppBuilder>? appBuilder)
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                _ready.Wait();
                if (_startError is not null)
                    throw new InvalidOperationException("Avalonia UI init failed: " + _startError.Message, _startError);
                return;
            }

            _appBuilderFactory = appBuilder ?? throw new InvalidOperationException(
                "The first EnsureStarted call must supply the product's AppBuilder factory " +
                "(e.g. () => AppBuilder.Configure<App>().UsePlatformDetect()).");

            _thread = new Thread(ThreadMain) { IsBackground = true, Name = "FL Agent Avalonia UI" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        _ready.Wait();
        if (_startError is not null)
            throw new InvalidOperationException("Avalonia UI init failed: " + _startError.Message, _startError);
    }

    private void ThreadMain()
    {
        try
        {
            _appBuilderFactory!()
                .With(new global::Avalonia.Win32PlatformOptions
                {
                    // Software rendering (Skia → bitmap) blitted to the window's redirection surface via
                    // GDI. This is the reliable presenter for a window that will be reparented as a
                    // WS_CHILD of FL's foreign HWND (GPU/composition presenters go blank in that case).
                    RenderingMode = new[] { global::Avalonia.Win32RenderingMode.Software },
                    CompositionMode = new[] { global::Avalonia.Win32CompositionMode.RedirectionSurface },
                })
                .SetupWithoutStarting();
        }
        catch (Exception ex)
        {
            _startError = ex;
            _ready.Set();
            return;
        }

        // Dispatcher is live now (SetupWithoutStarting bound Dispatcher.UIThread to this thread), so
        // Invoke/Post work from here on. Signal ready BEFORE entering the loop.
        _ready.Set();

        // Permanent message loop for the process lifetime. Never cancelled: closing the hosted window
        // must NOT stop the thread (no application lifetime is configured, so a last-window-closed does
        // not shut anything down), so a later plugin re-enable can create a fresh window on this thread.
        try { Dispatcher.UIThread.MainLoop(CancellationToken.None); }
        catch { /* thread ends with the process */ }
    }

    /// <summary>Run <paramref name="action"/> on the Avalonia UI thread and wait for it.</summary>
    public void Invoke(Action action) => Dispatcher.UIThread.Invoke(action);

    /// <summary>Run <paramref name="func"/> on the Avalonia UI thread and return its result.</summary>
    public T Invoke<T>(Func<T> func) => Dispatcher.UIThread.Invoke(func);

    /// <summary>Post <paramref name="action"/> to the Avalonia UI thread (fire-and-forget, non-blocking).</summary>
    public void Post(Action action)
    {
        try { Dispatcher.UIThread.Post(action); }
        catch { /* dispatcher gone / shutting down — best-effort */ }
    }
}
