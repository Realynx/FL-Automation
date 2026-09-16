using System.Buffers.Binary;
using System.Text.Json;

namespace FruityLink.Scripting;

/// <summary>Length-prefixed UTF-8 JSON framing shared by local scripting transports.</summary>
public static class ScriptingPipeFraming
{
    /// <summary>Maximum JSON frame payload, excluding the four-byte little-endian length.</summary>
    public const int MaximumFrameBytes = 4 * 1024 * 1024;

    /// <summary>Reads one complete JSON frame, refusing truncated or oversized input.</summary>
    public static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaximumFrameBytes)
            throw new ScriptingException("invalid_frame", $"Frame length must be between 1 and {MaximumFrameBytes} bytes.");
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64 });
    }

    /// <summary>Writes a single bounded JSON frame.</summary>
    public static async Task WriteAsync(Stream stream, object value, CancellationToken ct = default)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, ScriptingJson.Options);
        if (payload.Length > MaximumFrameBytes)
            throw new ScriptingException("response_too_large", "The operation result exceeds the 4 MiB frame limit; use a smaller page or batch.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
