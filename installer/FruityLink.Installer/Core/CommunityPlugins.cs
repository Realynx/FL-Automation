using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FruityLink.Installer.Core;

/// <summary>
/// One entry from the community plugin catalog: an open-source plugin the installer can
/// optionally download and drop into <c>FL\FruityLink\plugins\&lt;id&gt;\</c>.
/// </summary>
public sealed class CommunityPlugin
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Version { get; set; }

    /// <summary>HTTPS URL of a zip whose contents are the plugin folder (DLL + deps).</summary>
    public string DownloadUrl { get; set; } = string.Empty;
}

/// <summary>
/// The supported-community-plugins catalog, fetched at install time from the open-source GitHub
/// repo. Both installer editions (packaged and community) offer these as optional checkboxes;
/// a fetch failure or an empty list degrades gracefully — the base install never depends on it.
/// </summary>
public static class CommunityPluginCatalog
{
    /// <summary>
    /// Where the curated list lives (the open-source repo; HEAD = default branch, so a branch
    /// rename never breaks shipped installers). Env-overridable for dev.
    /// </summary>
    public const string DefaultManifestUrl =
        "https://raw.githubusercontent.com/Realynx/FL-Automation/HEAD/community-plugins.json";

    /// <summary>Ids must be safe to use as a folder name under FruityLink\plugins\.</summary>
    private static readonly Regex SafeId = new("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FLAutomate-Installer");
        return client;
    }

    public static string ManifestUrl =>
        Environment.GetEnvironmentVariable("FL_COMMUNITY_PLUGINS_URL") is { Length: > 0 } url
            ? url
            : DefaultManifestUrl;

    /// <summary>
    /// Downloads and parses the catalog. Returns an empty list when the manifest doesn't exist
    /// yet (404); throws on network errors so the caller can tell "no plugins" from "offline".
    /// </summary>
    public static async Task<IReadOnlyList<CommunityPlugin>> FetchAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(ManifestUrl, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return Array.Empty<CommunityPlugin>();
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>
    /// Parses catalog JSON, dropping entries that are malformed or unsafe (bad folder-name id,
    /// non-HTTPS download URL, duplicate id) rather than failing the whole list.
    /// </summary>
    public static IReadOnlyList<CommunityPlugin> Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<CatalogDoc>(json, CatalogJson);
        if (doc?.Plugins is null)
            return Array.Empty<CommunityPlugin>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return doc.Plugins
            .Where(p => p is not null
                        && SafeId.IsMatch(p.Id)
                        && !string.IsNullOrWhiteSpace(p.Name)
                        && Uri.TryCreate(p.DownloadUrl, UriKind.Absolute, out var uri)
                        && uri.Scheme == Uri.UriSchemeHttps
                        && seen.Add(p.Id))
            .ToList();
    }

    /// <summary>
    /// Downloads a plugin's zip and extracts it into <paramref name="stagingRoot"/>\&lt;id&gt;.
    /// If the zip wraps everything in a single top-level folder, that folder is unwrapped so the
    /// plugin DLL always lands directly in plugins\&lt;id&gt;\. Returns the staged directory,
    /// ready to be added to the install manifest as an absolute-source directory item.
    /// </summary>
    public static async Task<string> StageAsync(
        CommunityPlugin plugin, string stagingRoot, CancellationToken ct = default)
    {
        var pluginDir = Path.Combine(stagingRoot, plugin.Id);
        if (Directory.Exists(pluginDir))
            Directory.Delete(pluginDir, recursive: true);
        Directory.CreateDirectory(pluginDir);

        var zipPath = Path.Combine(stagingRoot, plugin.Id + ".zip");
        await using (var download = await Http.GetStreamAsync(plugin.DownloadUrl, ct).ConfigureAwait(false))
        await using (var file = File.Create(zipPath))
        {
            await download.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        // .NET's ExtractToDirectory rejects entries that escape the target (zip-slip safe).
        ZipFile.ExtractToDirectory(zipPath, pluginDir);
        File.Delete(zipPath);

        // Unwrap a zip-with-single-root-folder layout so the DLL sits at plugins\<id>\ directly.
        var rootFiles = Directory.GetFiles(pluginDir);
        var rootDirs = Directory.GetDirectories(pluginDir);
        if (rootFiles.Length == 0 && rootDirs.Length == 1)
        {
            var unwrapped = pluginDir + ".unwrap";
            Directory.Move(rootDirs[0], unwrapped);
            Directory.Delete(pluginDir, recursive: true);
            Directory.Move(unwrapped, pluginDir);
        }

        if (!Directory.EnumerateFiles(pluginDir, "*.dll", SearchOption.AllDirectories).Any())
            throw new InvalidDataException(
                $"The downloaded archive for '{plugin.Name}' contains no plugin DLL.");

        return pluginDir;
    }

    /// <summary>Builds the manifest item that installs a staged community plugin.</summary>
    public static PayloadItem ToPayloadItem(CommunityPlugin plugin, string stagedDir) => new()
    {
        Kind = PayloadKind.ManagedDir,
        Source = stagedDir, // absolute — ResolveSource passes rooted paths through as-is
        Destination = Path.Combine("FruityLink", "plugins", plugin.Id),
        IsDirectory = true,
        Required = true,
        Description = $"community plugin: {plugin.Name}" +
                      (string.IsNullOrWhiteSpace(plugin.Version) ? "" : $" v{plugin.Version}"),
    };

    private static readonly JsonSerializerOptions CatalogJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CatalogDoc
    {
        public int Version { get; set; }
        public List<CommunityPlugin>? Plugins { get; set; }
    }
}
