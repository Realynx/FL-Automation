using System.Text;

namespace FruityLink.Host;

/// <summary>
/// Shared marshaling + body helpers for the <c>[UnmanagedCallersOnly]</c> glue classes
/// (<see cref="PluginGlue"/> / <see cref="MenuGlue"/> / <see cref="ToolbarGlue"/>). The exported
/// entry points keep their exact names/signatures (the native side resolves them by name); only the
/// duplicated bodies live here. Return-code semantics are the callers' contract: -1 = no registry /
/// failure, 0 = unknown id / failure, 1 = ok — see each caller's doc comment.
/// </summary>
internal static class GlueMarshal
{
    /// <summary>
    /// Write <paramref name="s"/> as UTF-8 (not null-terminated) into <paramref name="buf"/>,
    /// truncated to <paramref name="len"/>. Returns the FULL byte length so the native caller can
    /// resize and retry when it exceeds <paramref name="len"/>.
    /// </summary>
    internal static unsafe int WriteUtf8(byte* buf, int len, string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        if (buf != null && len > 0)
        {
            int copy = Math.Min(bytes.Length, len);
            fixed (byte* src = bytes) Buffer.MemoryCopy(src, buf, len, copy);
        }
        return bytes.Length;
    }

    /// <summary>
    /// Read a UTF-8 id handed across the native boundary; null when the pointer is null or the
    /// length is not positive (the callers translate that to their 0 = "unknown/no id" code).
    /// </summary>
    internal static unsafe string? ReadUtf8(byte* ptr, int len)
        => ptr == null || len <= 0 ? null : Encoding.UTF8.GetString(ptr, len);

    /// <summary>
    /// Shared body of <see cref="MenuGlue.ContributionsJson"/> / <see cref="ToolbarGlue.ContributionsJson"/>:
    /// serialize the registry's contribution list (or <c>[]</c> when <paramref name="listJson"/> yields
    /// null — no registry set) into <paramref name="buf"/>. Returns the full byte length, -1 on failure.
    /// </summary>
    internal static unsafe int ContributionsJsonCore(Func<string?> listJson, string logContext, byte* buf, int len)
    {
        try
        {
            string json = listJson() ?? "[]";
            return WriteUtf8(buf, len, json);
        }
        catch (Exception ex)
        {
            HostEntry.Log(logContext + " failed: " + ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// Shared body of <see cref="MenuGlue.Invoke"/> / <see cref="ToolbarGlue.Invoke"/>: fire a
    /// contribution's handler by id. Returns 1 on success, 0 for an unknown/missing id or failure,
    /// -1 when <paramref name="invoke"/> is null (no registry set).
    /// </summary>
    internal static unsafe int InvokeCore(Func<string, bool>? invoke, string logContext, byte* idPtr, int idLen)
    {
        try
        {
            if (invoke is null) return -1;
            string? id = ReadUtf8(idPtr, idLen);
            if (id is null) return 0;
            return invoke(id) ? 1 : 0;
        }
        catch (Exception ex)
        {
            HostEntry.Log(logContext + " failed: " + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Shared body of <see cref="MenuGlue.Checked"/> / <see cref="ToolbarGlue.Active"/>: query a
    /// contribution's live state by id. Returns 1 when the query reports &gt;0, 0 otherwise
    /// (collapsing unknown(-1) to 0 so the native side just sees "not checked/active"), and -1 when
    /// <paramref name="query"/> is null (no registry set).
    /// </summary>
    internal static unsafe int StateCore(Func<string, int>? query, string logContext, byte* idPtr, int idLen)
    {
        try
        {
            if (query is null) return -1;
            string? id = ReadUtf8(idPtr, idLen);
            if (id is null) return 0;
            return query(id) > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            HostEntry.Log(logContext + " failed: " + ex.Message);
            return 0;
        }
    }
}
