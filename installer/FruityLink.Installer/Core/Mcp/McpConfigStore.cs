using System.Text;

namespace FruityLink.Installer.Core.Mcp;

internal sealed record McpConfigChange(string Path, byte[]? Original, string Content, string Description);

internal static class McpConfigStore
{
    public static byte[]? Read(string path)
    {
        CheckPath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("Client configuration exceeds the 16 MiB setup limit.");
        return File.ReadAllBytes(path);
    }

    public static string Decode(byte[]? bytes) => bytes is null ? "" : new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');

    public static string Write(McpConfigChange change)
    {
        CheckPath(change.Path);
        var current = Read(change.Path);
        if (!Equal(current, change.Original)) throw new IOException("Client configuration changed during setup; retry after closing the client.");
        var parent = Path.GetDirectoryName(change.Path)!;
        Directory.CreateDirectory(parent);
        var temporary = change.Path + ".flmcp-" + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = change.Path + ".flmcp-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N") + ".bak";
        try
        {
            File.WriteAllText(temporary, change.Content, new UTF8Encoding(false));
            if (!Equal(Read(change.Path), change.Original)) throw new IOException("Client configuration changed during setup; retry after closing the client.");
            if (change.Original is null) File.Move(temporary, change.Path, overwrite: false);
            else File.Replace(temporary, change.Path, backup);
            return change.Original is null ? "Created " + change.Path : "Updated " + change.Path + "; backup: " + backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool Equal(byte[]? first, byte[]? second) =>
        first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);

    private static void CheckPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Client config path must be absolute.");
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("MCP setup refuses configuration paths through a junction or symbolic link.");
    }
}
