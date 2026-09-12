using System.Net.Http;
using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;

namespace FruityLink.Llm.Auth;

/// <summary>A newer client release the gateway advertises.</summary>
public readonly record struct UpdateInfo(string Version, string DownloadUrl);

/// <summary>
/// Asks the AI gateway whether a newer plugin release exists (<c>GET {gateway}/v1/client-version</c>,
/// a public endpoint — no account session required). Fire-and-forget friendly:
/// <see cref="CheckAsync"/> returns null instead of throwing for every expected failure
/// (offline, non-2xx, nothing published, unparseable versions) — an update check must never
/// surface an error to the user.
/// <para>JSON is parsed by hand with System.Text.Json — NOT System.Net.Http.Json, which breaks
/// under the plugin's AssemblyLoadContext (STJ type-identity split; see the same note in
/// <see cref="AccountAuthService"/> / <see cref="BugReportClient"/>).</para>
/// </summary>
public sealed class UpdateCheckClient
{
    /// <summary>Where to send the user when the gateway advertises no explicit download URL.</summary>
    private const string FallbackDownloadUrl = "https://fl-automate.com/account";

    private readonly HttpClient _http;
    private readonly ISettingsStore _settings;

    public UpdateCheckClient(HttpClient http, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(settings);
        _http = http;
        _settings = settings;
    }

    /// <summary>Null = up to date, no version published, or any failure (quiet by contract).</summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        try
        {
            AppSettings app = await _settings.LoadAsync(ct).ConfigureAwait(false);
            string url = app.AccountOrDefault.GatewayBaseUrl.TrimEnd('/') + "/v1/client-version";

            using HttpResponseMessage response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);

            string? remoteVersion = doc.RootElement.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(remoteVersion)) return null;   // nothing published yet

            Version? remote = TryParseVersion(remoteVersion);
            Version? current = TryParseVersion(currentVersion);
            if (remote is null || current is null) return null;
            if (remote.CompareTo(current) <= 0) return null;             // up to date (or ahead)

            string? downloadUrl = doc.RootElement.TryGetProperty("downloadUrl", out JsonElement d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;

            // The advertised version string is returned VERBATIM — it's for display, not comparison.
            return new UpdateInfo(
                remoteVersion,
                string.IsNullOrWhiteSpace(downloadUrl) ? FallbackDownloadUrl : downloadUrl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // an explicit caller cancel is not a check failure
        }
        catch (Exception)
        {
            return null;   // offline / DNS / timeout / malformed JSON — quiet failure by contract
        }
    }

    /// <summary>
    /// Parses "1.4", "v1.4.2", "1.4.2.7" into a comparable <see cref="Version"/>. Missing components
    /// are padded to 0 (raw <see cref="Version"/> treats them as -1, so "1.4" would otherwise sort
    /// BELOW "1.4.0" — same release, different spelling).
    /// </summary>
    private static Version? TryParseVersion(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V')) span = span[1..];
        if (!Version.TryParse(span, out Version? parsed)) return null;
        return new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
    }
}
