using System.Runtime.InteropServices;
using System.Text;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Host;

/// <summary>
/// Native ↔ managed bridge for plugin toolbar buttons. Exposes three
/// <see cref="UnmanagedCallersOnlyAttribute"/> functions the native bridge (FlBridge.dll) calls to
/// materialize plugin buttons onto FL's main toolbar — resolved by the CoreCLR host (FlClrHost.dll) the
/// same way <see cref="MenuGlue"/> is, then handed to C++ via the host's <c>FlClr_GetToolbarFns</c>
/// export.
///
/// <para>All three read the live <see cref="FlToolbarRegistryLocator.Current"/>. When it is null (the
/// plugin host has not initialized yet) the toolbar degrades gracefully:
/// <see cref="ContributionsJson"/> writes <c>[]</c> (no buttons), and
/// <see cref="Invoke"/>/<see cref="Active"/> return -1 (no-op). None ever throws across the native
/// boundary.</para>
///
/// <para>These are called on FL's UI thread. Callbacks may need a different UI thread (e.g. a WPF/Avalonia
/// window on its own dispatcher); per the SDK contract a plugin marshals inside its own
/// handler/isActive, so the registry invokes them directly (and exception-guarded) here.</para>
/// </summary>
public static class ToolbarGlue
{
    /// <summary>
    /// Serialize the current toolbar buttons as compact JSON into <paramref name="buf"/> (UTF-8, not
    /// null-terminated). Returns the FULL byte length (may exceed <paramref name="len"/> → the caller
    /// resizes and retries). Returns -1 on failure. Emits <c>[]</c> when no registry is set.
    /// Shape: see <see cref="IFlToolbarContributions.ListJson"/>.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int ContributionsJson(byte* buf, int len)
    {
        try
        {
            string json = FlToolbarRegistryLocator.Current?.ListJson() ?? "[]";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            if (buf != null && len > 0)
            {
                int copy = Math.Min(bytes.Length, len);
                fixed (byte* src = bytes) Buffer.MemoryCopy(src, buf, len, copy);
            }
            return bytes.Length;
        }
        catch (Exception ex)
        {
            HostEntry.Log("ToolbarGlue.ContributionsJson failed: " + ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// Invoke a button by id (fire its toggle/click handler). Returns 1 on success, 0 for an unknown id,
    /// -1 when no registry is set. The handler self-marshals to its UI thread, so this returns promptly
    /// and never deadlocks FL's UI thread.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int Invoke(byte* idPtr, int idLen)
    {
        try
        {
            IFlToolbarContributions? reg = FlToolbarRegistryLocator.Current;
            if (reg is null) return -1;
            if (idPtr == null || idLen <= 0) return 0;
            string id = Encoding.UTF8.GetString(idPtr, idLen);
            return reg.Invoke(id) ? 1 : 0;
        }
        catch (Exception ex)
        {
            HostEntry.Log("ToolbarGlue.Invoke failed: " + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Live lit-state of a button: 1 = active, 0 = inactive/momentary/unknown-when-no-id, -1 = no
    /// registry. Backs the lit face on toggle buttons when the toolbar is (re)built.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int Active(byte* idPtr, int idLen)
    {
        try
        {
            IFlToolbarContributions? reg = FlToolbarRegistryLocator.Current;
            if (reg is null) return -1;
            if (idPtr == null || idLen <= 0) return 0;
            string id = Encoding.UTF8.GetString(idPtr, idLen);
            return reg.Active(id) > 0 ? 1 : 0;   // collapse unknown(-1) to 0 so the native side just sees "not active"
        }
        catch (Exception ex)
        {
            HostEntry.Log("ToolbarGlue.Active failed: " + ex.Message);
            return 0;
        }
    }
}
