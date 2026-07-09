using FruityLink.Plugins.Abstractions;

namespace FruityLink.Plugins.Host;

/// <summary>
/// Fail-soft <see cref="IFlWindowHost"/> used when the host process supplies no window host (tests,
/// non-FL hosts). Mirrors the real implementation's no-bridge behavior exactly: embedding is reported
/// unavailable and every operation is a silent no-op, so a plugin written against
/// <see cref="IPluginContext.Windows"/> simply falls back to its external top-level window.
/// </summary>
internal sealed class NullFlWindowHost : IFlWindowHost
{
    public static readonly NullFlWindowHost Instance = new();

    private NullFlWindowHost() { }

    /// <inheritdoc/>
    public bool IsBridgeAvailable() => false;

    /// <inheritdoc/>
    public string LastEmbedReply => "";

    /// <inheritdoc/>
    public int LastInsetX => 0;

    /// <inheritdoc/>
    public int LastInsetY => 0;

    /// <inheritdoc/>
    public bool TryEmbed(IntPtr childHwnd, bool show) => false;

    /// <inheritdoc/>
    public bool IsHostVisible() => false;

    /// <inheritdoc/>
    public void SetVisible(bool visible) { /* no host form — best-effort no-op */ }

    /// <inheritdoc/>
    public void Close() { /* nothing embedded — best-effort no-op */ }

    /// <inheritdoc/>
    public void SetStatusHint(string text) { /* no FL hint bar — best-effort no-op */ }
}
