using System.Text.Json;
using System.Text.Json.Serialization;

namespace FruityLink.Scripting;

/// <summary>Strict, shared JSON settings for the scripting protocol.</summary>
public static class ScriptingJson
{
    /// <summary>Immutable camelCase settings: case-sensitive arguments, named enums, and required constructor arguments.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>An empty JSON object for operations without arguments.</summary>
    public static JsonElement EmptyObject { get; } = JsonSerializer.SerializeToElement(new Dictionary<string, object?>());

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 64
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
