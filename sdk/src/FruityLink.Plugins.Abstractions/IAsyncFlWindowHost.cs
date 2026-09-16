namespace FruityLink.Plugins.Abstractions;

/// <summary>Nonblocking native hosting for windows that share a UI thread with another embedded plugin.</summary>
public interface IAsyncFlWindowHost : IFlWindowHost
{
    /// <summary>Probe the bridge without blocking the caller's UI dispatcher.</summary>
    Task<bool> IsBridgeAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Create an independent FL form, then attach on the child's owning UI context.
    /// Call on the child's UI thread with a synchronization context. Cancellation waits for any
    /// started native operation to finish and cleans up before completing.</summary>
    Task<bool> TryEmbedAsync(IntPtr childHwnd, FlWindowOptions options, bool show = true,
        CancellationToken cancellationToken = default);

    /// <summary>Change native visibility without blocking either UI thread. Activation is explicit.</summary>
    Task<bool> SetVisibleAsync(bool visible, bool activate = false, CancellationToken cancellationToken = default);

    /// <summary>Detach on the child's UI thread, then release its native host. False preserves ownership
    /// when cleanup cannot be confirmed, so callers must keep the window alive and retry.</summary>
    Task<bool> CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Requested native window caption and content dimensions, all sizes in physical pixels.</summary>
/// <param name="Caption">Text shown by FL's native window chrome.</param>
/// <param name="Width">Preferred content width.</param>
/// <param name="Height">Preferred content height.</param>
/// <param name="MinimumWidth">Minimum content width while resizing.</param>
/// <param name="MinimumHeight">Minimum content height while resizing.</param>
public sealed record FlWindowOptions(string Caption, int Width, int Height, int MinimumWidth = 1, int MinimumHeight = 1);

/// <summary>Creates independent native window sessions. A plugin context owns and closes all sessions
/// created through its factory when the plugin is disabled.</summary>
public interface IFlWindowHostFactory
{
    /// <summary>Create or retrieve a window scope identified within this plugin. Creating a scope
    /// does not create/show a native window; call its async embed capability on the UI thread.</summary>
    IFlWindowHost CreateWindowHost(string windowId, string caption);
}
