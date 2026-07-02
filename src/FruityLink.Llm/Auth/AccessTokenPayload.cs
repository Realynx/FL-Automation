using System.Text;
using System.Text.Json;

namespace FruityLink.Llm.Auth;

/// <summary>
/// Client-side decode of the access token's JWT payload (the middle base64url segment). The token
/// is RS256-signed and VERIFIED BY THE SERVERS — the client only reads claims for display and for
/// proactive refresh timing, so no signature check happens (or could: the client has no need for
/// the public key).
/// </summary>
public static class AccessTokenPayload
{
    /// <summary>
    /// Decodes <paramref name="jwt"/>'s payload into an <see cref="AccountSession"/>, or null when
    /// the token is malformed. Missing claims fall back to safe defaults ('free', inactive, expired).
    /// </summary>
    public static AccountSession? Decode(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt)) return null;
        string[] parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            JsonElement root = doc.RootElement;

            long exp = root.TryGetProperty("exp", out JsonElement e) && e.ValueKind == JsonValueKind.Number
                ? e.GetInt64() : 0;

            return new AccountSession(
                Email: GetString(root, "email") ?? string.Empty,
                Tier: GetString(root, "tier") ?? "free",
                Plan: GetString(root, "plan") ?? "free",
                SubActive: root.TryGetProperty("subActive", out JsonElement s) && s.ValueKind == JsonValueKind.True,
                ExpiresAt: DateTimeOffset.FromUnixTimeSeconds(exp));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string Base64UrlDecode(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s,
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(s));
    }
}
