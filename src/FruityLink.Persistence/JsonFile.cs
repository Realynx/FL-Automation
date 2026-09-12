using System.Text.Json;

namespace FruityLink.Persistence;

/// <summary>
/// Shared JSON-file helpers for the file-backed stores: reads deserialize with
/// <see cref="JsonDefaults.Options"/> and treat a missing or unparseable file as absent
/// (<c>default</c>); writes serialize with the same options and go through
/// <see cref="AtomicFile"/> so a reader never observes a half-written file.
/// </summary>
internal static class JsonFile
{
    /// <summary>
    /// Reads and deserializes <paramref name="path"/>, returning <c>default</c> when the file is
    /// missing or contains invalid JSON. When <paramref name="swallowIoErrors"/> is true an
    /// <see cref="IOException"/> is also treated as "absent" instead of propagating (used by
    /// callers whose reads are best-effort, e.g. project version control).
    /// </summary>
    public static async Task<T?> TryReadAsync<T>(string path, CancellationToken ct, bool swallowIoErrors = false)
    {
        if (!File.Exists(path)) return default;
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<T>(stream, JsonDefaults.Options, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Skip corrupt/unparseable files rather than failing the caller.
            return default;
        }
        catch (IOException) when (swallowIoErrors)
        {
            return default;
        }
    }

    /// <summary>Serializes <paramref name="value"/> and writes it to <paramref name="path"/> atomically.</summary>
    public static Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(value, JsonDefaults.Options);
        return AtomicFile.WriteAllTextAsync(path, json, ct);
    }
}
