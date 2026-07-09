using System.Runtime.InteropServices;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Host;

/// <summary>
/// Native ↔ managed bridge for plugin menu contributions. Exposes three
/// <see cref="UnmanagedCallersOnlyAttribute"/> functions the native bridge (FlBridge.dll) calls to
/// materialize plugin entries into FL's native top-level dropdowns — resolved by the CoreCLR host
/// (FlClrHost.dll) the same way <see cref="PluginGlue"/> is, then handed to C++ via the host's
/// <c>FlClr_GetMenuFns</c> export.
///
/// <para>All three read the live <see cref="FlMenuRegistryLocator.Current"/>. When it is null (the
/// plugin host has not initialized yet) the menu degrades gracefully:
/// <see cref="ContributionsJson"/> writes <c>[]</c> (no entries), and
/// <see cref="Invoke"/>/<see cref="Checked"/> return -1 (no-op). None ever throws across the native
/// boundary.</para>
///
/// <para>These are called on FL's UI thread. Callbacks may need a different UI thread (e.g. a WPF
/// window on its own dispatcher); per the SDK contract a plugin marshals inside its own
/// handler/isChecked, so the registry invokes them directly (and exception-guarded) here.</para>
/// </summary>
public static class MenuGlue
{
    /// <summary>
    /// Serialize the current menu contributions as compact JSON into <paramref name="buf"/> (UTF-8,
    /// not null-terminated). Returns the FULL byte length (may exceed <paramref name="len"/> → the
    /// caller resizes and retries). Returns -1 on failure. Emits <c>[]</c> when no registry is set.
    /// Shape: see <see cref="IFlMenuContributions.ListJson"/>.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int ContributionsJson(byte* buf, int len)
        => GlueMarshal.ContributionsJsonCore(
            static () => FlMenuRegistryLocator.Current?.ListJson(), "MenuGlue.ContributionsJson", buf, len);

    /// <summary>
    /// Invoke a contribution by id (fire its toggle/command handler). Returns 1 on success, 0 for an
    /// unknown id, -1 when no registry is set. The handler self-marshals to its UI thread, so this
    /// returns promptly and never deadlocks FL's UI thread.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int Invoke(byte* idPtr, int idLen)
        => GlueMarshal.InvokeCore(
            FlMenuRegistryLocator.Current is { } reg ? reg.Invoke : null, "MenuGlue.Invoke", idPtr, idLen);

    /// <summary>
    /// Live checked-state of a contribution: 1 = checked, 0 = unchecked/command/unknown-when-no-id,
    /// -1 = no registry. Backs the ✓ glyph on toggle entries when the menu is (re)built.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int Checked(byte* idPtr, int idLen)
        => GlueMarshal.StateCore(
            FlMenuRegistryLocator.Current is { } reg ? reg.Checked : null, "MenuGlue.Checked", idPtr, idLen);
}
