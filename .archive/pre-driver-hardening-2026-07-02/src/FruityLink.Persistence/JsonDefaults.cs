using System.Text.Json;
using System.Text.Json.Serialization;

namespace FruityLink.Persistence;

/// <summary>
/// Centralizes the <see cref="JsonSerializerOptions"/> shared by every JSON-backed store:
/// camelCase property names, string enum values, indented output and null-property omission.
/// Kept internal so the on-disk format stays an implementation detail of this assembly.
/// </summary>
internal static class JsonDefaults
{
    /// <summary>Shared, immutable serializer options used for all persisted JSON.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
