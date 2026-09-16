using System;

namespace FruityLink.Ui.Avalonia.Hosting;

/// <summary>Coalesces native layout notifications into one later UI repaint.</summary>
internal sealed class DeferredRepaint(Action<Action> post, Action repaint) : IDisposable
{
    private bool _pending;
    private bool _disposed;

    internal void Request()
    {
        if (_disposed || _pending) return;
        _pending = true;
        try { post(Apply); }
        catch { _pending = false; throw; }
    }

    private void Apply()
    {
        _pending = false;
        if (!_disposed) repaint();
    }

    public void Dispose() => _disposed = true;
}
