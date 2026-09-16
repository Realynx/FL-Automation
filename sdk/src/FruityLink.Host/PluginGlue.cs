using System.Runtime.InteropServices;
using System.Text;
using FruityLink.Plugins.Abstractions;

namespace FruityLink.Host;

/// <summary>
/// Native ↔ managed bridge for the "Plugins" toolbar dropdown. Exposes two
/// <see cref="UnmanagedCallersOnlyAttribute"/> functions that the native bridge (FlBridge.dll) calls
/// — resolved by the CoreCLR host (FlClrHost.dll) the same way <see cref="HostEntry.Bootstrap"/> is,
/// then handed to C++ via the host's <c>FlClr_GetPluginFns</c> export.
///
/// Both functions read the live <see cref="PluginManagerLocator.Current"/>. When it is null (the
/// plugin host has not initialized yet) the dropdown degrades gracefully:
///   • <see cref="ListJson"/> writes the literal "null" so the native side shows a single disabled
///     "Plugin host not ready" item, and
///   • <see cref="Toggle"/> returns -1 (no-op).
/// Neither ever throws across the native boundary.
/// </summary>
public static class PluginGlue
{
    /// <summary>
    /// Serialize the installed-plugin list as compact JSON into <paramref name="buf"/> (UTF-8, not
    /// null-terminated). Returns the FULL byte length (may exceed <paramref name="len"/> → the caller
    /// resizes and retries). Returns -1 on failure. Emits "null" when no manager is registered.
    ///
    /// JSON shape: <c>[{"id":"..","name":"..","version":"..","enabled":true|false,"loaded":true|false},
    /// ...]</c> (the <c>loaded</c> flag = the assembly is live in-process right now). Only <c>\</c> and
    /// <c>"</c> are escaped (the native parser unescapes just those), so plugin names may contain any
    /// other UTF-8 character.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int ListJson(byte* buf, int len)
    {
        try
        {
            string json;
            IPluginManager? mgr = PluginManagerLocator.Current;
            if (mgr is null)
            {
                json = "null";
            }
            else
            {
                IReadOnlyList<PluginInfo> list = mgr.List();
                var sb = new StringBuilder(256);
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    PluginInfo p = list[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"id\":\"").Append(Esc(p.Id))
                      .Append("\",\"name\":\"").Append(Esc(p.Name))
                      .Append("\",\"version\":\"").Append(Esc(p.Version))
                      .Append("\",\"enabled\":").Append(p.Enabled ? "true" : "false")
                      .Append(",\"loaded\":").Append(p.Loaded ? "true" : "false")
                      .Append('}');
                }
                sb.Append(']');
                json = sb.ToString();
            }

            return GlueMarshal.WriteUtf8(buf, len, json);
        }
        catch (Exception ex)
        {
            HostEntry.Log("PluginGlue.ListJson failed: " + ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// Enable (<paramref name="enable"/>!=0) or disable a plugin by id. Runs the async manager call on
    /// the thread pool and blocks briefly (so it never deadlocks FL's UI thread, which is where the
    /// dropdown click invokes this). Returns 1 on confirmed success, 0 on failure/timeout, -1 when no
    /// manager is registered.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int Toggle(byte* idPtr, int idLen, int enable)
    {
        try
        {
            IPluginManager? mgr = PluginManagerLocator.Current;
            if (mgr is null) return -1;
            string? id = GlueMarshal.ReadUtf8(idPtr, idLen);
            if (id is null) return 0;
            bool want = enable != 0;

            // Task.Run pushes the await continuations onto the thread pool, so blocking the caller
            // (FL's main thread) here cannot deadlock on a UI-thread continuation.
            Task<bool> t = Task.Run(() => want ? mgr.EnableAsync(id) : mgr.DisableAsync(id));
            if (!t.Wait(TimeSpan.FromSeconds(4))) return 0;
            return t.Result ? 1 : 0;
        }
        catch (Exception ex)
        {
            HostEntry.Log("PluginGlue.Toggle failed: " + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Reload a plugin by id (stop → unload → load the current bytes → re-enable if it was enabled).
    /// Backs the debug-pipe <c>plugin_reload &lt;id&gt;</c> command. Returns 1 on success, 0 on
    /// failure/timeout, -1 when no manager is registered, -2 when the live manager is not the concrete
    /// <see cref="FruityLink.Plugins.Host.PluginManager"/> (reload unsupported — e.g. the test stub).
    /// Runs the async reload on the thread pool and blocks briefly so it cannot deadlock the caller.
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int Reload(byte* idPtr, int idLen)
    {
        try
        {
            IPluginManager? mgr = PluginManagerLocator.Current;
            if (mgr is null) return -1;
            string? id = GlueMarshal.ReadUtf8(idPtr, idLen);
            if (id is null) return 0;
            if (mgr is not FruityLink.Plugins.Host.PluginManager pm) return -2;
            // Reload does disable → ALC unload → re-load → re-enable, which can take a few seconds.
            Task<bool> t = Task.Run(() => pm.ReloadAsync(id));
            if (!t.Wait(TimeSpan.FromSeconds(20))) return 0;
            return t.Result ? 1 : 0;
        }
        catch (Exception ex)
        {
            HostEntry.Log("PluginGlue.Reload failed: " + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Write the absolute path of the directory the host watches for plugins (UTF-8, not
    /// null-terminated) into <paramref name="buf"/>. Backs the debug-pipe <c>plugins_dir</c> command.
    /// Returns the FULL byte length (caller resizes + retries if it exceeds <paramref name="len"/>), or
    /// -1 when no concrete host manager is registered (so the native side can answer <c>err:no-host</c>).
    /// </summary>
    [UnmanagedCallersOnly]
    public static unsafe int PluginsDir(byte* buf, int len)
    {
        try
        {
            string? dir = (PluginManagerLocator.Current as FruityLink.Plugins.Host.PluginManager)?.PluginsDirectory;
            if (string.IsNullOrEmpty(dir)) return -1;

            return GlueMarshal.WriteUtf8(buf, len, dir);
        }
        catch (Exception ex)
        {
            HostEntry.Log("PluginGlue.PluginsDir failed: " + ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// Settings glue (task #61): show/hide the diagnostic proof window. The window is HIDDEN by
    /// default; the native "FL Plugins ▸ Settings ▸ Show Debug Output" item (and the
    /// <c>debug_show</c> bridge command) call this. Marshals to the WPF UI thread internally and is
    /// safe before the window has ever been created. Returns 0 ok / -1 on failure.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int SetDebugVisible(int visible)
    {
        try { HostEntry.SetDebugVisible(visible != 0); return 0; }
        catch (Exception ex) { HostEntry.Log("PluginGlue.SetDebugVisible failed: " + ex.Message); return -1; }
    }

    /// <summary>
    /// Current visibility of the diagnostic window: 1 = shown, 0 = hidden / not created. Backs the
    /// ✓ glyph on the "Show Debug Output" menu item and the <c>settings_get</c> bridge command.
    /// </summary>
    [UnmanagedCallersOnly]
    public static int GetDebugVisible()
    {
        try { return HostEntry.GetDebugVisible() ? 1 : 0; }
        catch (Exception ex) { HostEntry.Log("PluginGlue.GetDebugVisible failed: " + ex.Message); return 0; }
    }

    // Minimal JSON string escaping: only the two characters the native unescaper handles, plus
    // newlines flattened to spaces (so a description never breaks the single-line menu caption).
    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r':
                case '\n':
                case '\t': sb.Append(' '); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
