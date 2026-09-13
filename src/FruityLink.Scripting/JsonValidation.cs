using System.Text.Json;

namespace FruityLink.Scripting;

internal static class JsonValidation
{
    internal static void Object(JsonElement value, string label, IEnumerable<string> allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ScriptingException("invalid_arguments", $"{label} must be a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var permitted = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new ScriptingException("invalid_arguments", $"Duplicate property '{property.Name}' in {label}.");
            if (!permitted.Contains(property.Name))
                throw new ScriptingException("invalid_arguments", $"Unknown property '{property.Name}' in {label}.");
        }
    }

    internal static void Values(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new ScriptingException("invalid_arguments", $"Duplicate property '{property.Name}'.");
                    Values(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) Values(item);
                break;
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out double number) || !double.IsFinite(number))
                    throw new ScriptingException("invalid_arguments", "Numeric values must be finite.");
                break;
        }
    }

    internal static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new ScriptingException("invalid_arguments", $"'{name}' must be a nonempty string.");
        return property.GetString()!;
    }

    internal static JsonElement Arguments(JsonElement value) => value.TryGetProperty("arguments", out var arguments)
        ? arguments : ScriptingJson.EmptyObject;
}
