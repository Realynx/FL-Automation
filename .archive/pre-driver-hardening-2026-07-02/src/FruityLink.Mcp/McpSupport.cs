using System.IO;
using FruityLink.Core.Abstractions;

namespace FruityLink.Mcp;

/// <summary>
/// Shared helpers for the FruityLink MCP tools. Mirrors
/// <c>FruityLink.Agent.Plugins.PluginSupport</c> (error classification) and
/// <c>NativeControlPlugin.ParseNotes</c> (note-list parsing) so the MCP surface behaves
/// identically to the in-app Semantic-Kernel tool surface.
/// </summary>
internal static class McpSupport
{
    /// <summary>
    /// Friendly message returned to the MCP client when a tool fails, so the call returns a clear
    /// text result instead of throwing. Distinguishes a real bridge outage (pipe unreachable — FL not
    /// running / bridge not injected) from a logic/validation error (bad arguments, no project open, a
    /// native fault): the latter is surfaced verbatim so the client can self-correct rather than
    /// wrongly concluding "the bridge is down".
    /// </summary>
    public static string BridgeError(Exception ex) =>
        IsConnectivity(ex)
            ? $"FL Studio's native bridge isn't reachable ({ex.Message}). " +
              "Make sure FL Studio (FL64.exe) is running AND the FruityLink bridge DLL is injected into it, then retry."
            : $"That FL Studio operation failed: {ex.Message}";

    /// <summary>True if the exception (or an inner one) indicates the named-pipe bridge couldn't be reached.</summary>
    private static bool IsConnectivity(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is TimeoutException or IOException or System.ComponentModel.Win32Exception)
                return true;
        return false;
    }

    /// <summary>
    /// Runs a tool operation and returns its success text, converting ANY exception into a friendly
    /// text result via <see cref="BridgeError"/>. This is the shared try/catch that every native_* tool
    /// would otherwise repeat verbatim, so each tool body collapses to a single delegating call.
    /// Behaviour is identical to the inline <c>try { … } catch (Exception ex) { return BridgeError(ex); }</c>.
    /// </summary>
    public static async Task<string> SafeAsync(Func<Task<string>> op)
    {
        try { return await op(); }
        catch (Exception ex) { return BridgeError(ex); }
    }

    /// <summary>
    /// Parse the <c>native_add_notes</c> string ('key,start,length,velocity[,channel]' per note,
    /// separated by ';' or newlines) into <see cref="NoteSpec"/>s; notes without a 5th field use
    /// <paramref name="defaultChannel"/>. Ported verbatim from <c>NativeControlPlugin.ParseNotes</c>.
    /// </summary>
    public static List<NoteSpec> ParseNotes(string notes, int defaultChannel)
    {
        var list = new List<NoteSpec>();
        if (string.IsNullOrWhiteSpace(notes)) return list;
        foreach (var item in notes.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var f = item.Split(',', StringSplitOptions.TrimEntries);
            if (f.Length < 4)
                throw new FormatException($"Bad note '{item}': expected 'key,start,length,velocity[,channel]'.");
            if (!int.TryParse(f[0], out int key) || !int.TryParse(f[1], out int start) ||
                !int.TryParse(f[2], out int len) || !int.TryParse(f[3], out int vel))
                throw new FormatException($"Bad note '{item}': key,start,length,velocity must be whole numbers.");
            int chan = defaultChannel;
            if (f.Length >= 5 && f[4].Length > 0 && !int.TryParse(f[4], out chan))
                throw new FormatException($"Bad channel in note '{item}'.");
            list.Add(new NoteSpec(chan, key, start, len, vel));
        }
        return list;
    }
}
