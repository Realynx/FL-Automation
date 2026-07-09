using System;
using Avalonia.Threading;

namespace FruityLink.Ui.Avalonia.Hosting;

/// <summary>
/// Tiny UI-thread marshaling helper for the "safe from any thread" members: run the action inline
/// when already on the Avalonia UI thread, otherwise post it (fire-and-forget) to the dispatcher.
/// </summary>
public static class UiThread
{
    /// <summary>Run <paramref name="action"/> now if on the Avalonia UI thread, else post it.</summary>
    public static void RunOrPost(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
