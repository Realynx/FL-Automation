using System.Globalization;
using System.Text.Json;

namespace FruityLink.FlStudio.Windowing;

/// <summary>The bridge's window-host response, independent of JSON whitespace and field order.</summary>
internal readonly record struct WindowEmbedReply(IntPtr Content, IntPtr Host, int X, int Y, int Width, int Height)
{
    internal static bool TryParse(string json, out WindowEmbedReply reply)
    {
        reply = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadInt(root, "ok") != 1) return false;
            reply = new WindowEmbedReply(ReadHandle(root, "content"), ReadHandle(root, "host"),
                ReadInt(root, "cx"), ReadInt(root, "cy"), ReadInt(root, "cw"), ReadInt(root, "ch"));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static int ReadInt(JsonElement root, string field)
        => root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int number) ? number : 0;

    private static IntPtr ReadHandle(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String)
            return IntPtr.Zero;
        string? text = value.GetString();
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        return long.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long handle)
            ? new IntPtr(handle) : IntPtr.Zero;
    }
}
