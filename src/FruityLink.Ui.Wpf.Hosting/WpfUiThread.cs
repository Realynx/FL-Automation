using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FruityLink.Ui.Wpf.Hosting;

/// <summary>
/// Provides a WPF <see cref="System.Windows.Threading.Dispatcher"/> to host plugin UI on.
/// When the process already runs a WPF <see cref="Application"/> (the FruityLink host is a WPF app,
/// so inside FL Studio this is the normal case), it reuses that UI thread. Otherwise it spins up a
/// private STA dispatcher thread so the same plugin also works in a non-WPF host (e.g. a test
/// harness). <see cref="Stop"/> only tears down a thread we own; it never touches the host's
/// dispatcher.
/// </summary>
public sealed class WpfUiThread
{
    private Dispatcher? _dispatcher;
    private Dispatcher? _ownedDispatcher;
    private Thread? _thread;
    private readonly string _threadName;

    /// <param name="threadName">Name given to a privately created dispatcher thread (diagnostics).</param>
    public WpfUiThread(string threadName = "FruityLink WPF UI") => _threadName = threadName;

    /// <summary>The dispatcher to marshal UI work onto. Valid after <see cref="Start"/>.</summary>
    public Dispatcher Dispatcher =>
        _dispatcher ?? throw new InvalidOperationException("WpfUiThread.Start() has not been called.");

    /// <summary>Acquire a UI thread: the host app's if present, else a private STA thread.</summary>
    public void Start()
    {
        if (_dispatcher is not null) return;

        Dispatcher? appDispatcher = Application.Current?.Dispatcher;
        if (appDispatcher is not null)
        {
            _dispatcher = appDispatcher;          // reuse the host's UI thread; not ours to stop
            return;
        }

        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _ownedDispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = _threadName,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _dispatcher = _ownedDispatcher;
    }

    /// <summary>Shut down only a dispatcher thread we created. No-op when reusing the host's.</summary>
    public void Stop()
    {
        Dispatcher? owned = _ownedDispatcher;
        if (owned is not null)
        {
            try { owned.InvokeShutdown(); } catch { /* already gone */ }
            _ownedDispatcher = null;
        }
        _dispatcher = null;
        _thread = null;
    }
}
