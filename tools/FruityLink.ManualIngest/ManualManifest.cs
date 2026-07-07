using System.Text.Json;
using System.Text.Json.Serialization;

namespace FruityLink.ManualIngest;

/// <summary>One crawled manual page: source URL, on-disk path, title, and change-detection state.</summary>
/// <param name="Url">Absolute source URL the page was fetched from (citation URI at runtime).</param>
/// <param name="RelPath">Path relative to the corpus root, forward-slashed (e.g. <c>html/playlist.htm</c>).</param>
/// <param name="Title">Page title (from <c>&lt;title&gt;</c>), or the URL when none was found.</param>
/// <param name="ContentHash">SHA-256 of the fetched HTML, for incremental change detection.</param>
/// <param name="ETag">Server ETag, replayed as <c>If-None-Match</c> on re-crawl (null if none).</param>
/// <param name="LastModified">Server Last-Modified (HTTP-date), replayed as <c>If-Modified-Since</c>.</param>
public sealed record PageEntry(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("relPath")] string RelPath,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("contentHash")] string ContentHash = "",
    [property: JsonPropertyName("etag")] string? ETag = null,
    [property: JsonPropertyName("lastModified")] string? LastModified = null);

/// <summary>One converted markdown page: its markdown path plus the source URL/title for citation.</summary>
public sealed record MarkdownEntry(
    [property: JsonPropertyName("mdPath")] string MdPath,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("title")] string Title);

/// <summary>JSON (de)serialization for the small crawl/convert manifests.</summary>
public static class ManualManifest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static void Write<T>(string path, IReadOnlyList<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string json = JsonSerializer.Serialize(items, Options);
        // Atomic write: a crash/power-loss mid-write can never leave a truncated manifest —
        // the old file stays intact until the fully-written temp file replaces it.
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }

    public static IReadOnlyList<T> Read<T>(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Manifest not found: {path}. Run the previous stage first.", path);
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Options) ?? new List<T>();
    }
}
