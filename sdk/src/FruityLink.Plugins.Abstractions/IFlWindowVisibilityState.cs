namespace FruityLink.Plugins.Abstractions;

/// <summary>Optional per-plugin/window presentation preference, independent of plugin enablement.</summary>
public interface IFlWindowVisibilityState
{
    /// <summary>Last user-selected visibility, or true for a window without a saved preference.
    /// Use this for initial external presentation; the framework applies it to initial embedding.</summary>
    bool StartupVisible { get; }

    /// <summary>Remember an explicit user show/hide action in an external window. Native menu
    /// show/hide and native close are remembered by the framework. Never call for teardown.</summary>
    void RememberVisibility(bool visible);
}

/// <summary>Optional native-host notification of a user action, excluding lifecycle teardown.</summary>
public interface IFlWindowVisibilityNotifications
{
    /// <summary>Raised after a user closes native window chrome. Handlers must not block FL's UI.</summary>
    event Action<bool>? UserVisibilityChanged;
}
