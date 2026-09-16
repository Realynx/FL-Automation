using System.Text.Json;

namespace FruityLink.Scripting;

internal static class ArgumentSchemaValidation
{
    internal static void Validate(JsonElement value, JsonElement schema, string path)
    {
        if (schema.TryGetProperty("anyOf", out var alternatives))
        {
            if (value.ValueKind == JsonValueKind.Null) return;
            schema = alternatives[0];
        }
        if (value.ValueKind == JsonValueKind.Null) Fail(path, "cannot be null");
        string? type = schema.GetProperty("type").GetString();
        if (type == "object") ValidateObject(value, schema, path);
        if (type == "array") ValidateArray(value, schema.GetProperty("items"), path);
    }

    private static void ValidateObject(JsonElement value, JsonElement schema, string path)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail(path, "must be an object");
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (!value.TryGetProperty(required.GetString()!, out _))
                Fail($"{path}.{required.GetString()}", "is required");
        var properties = schema.GetProperty("properties");
        foreach (var property in value.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out var propertySchema))
                Fail($"{path}.{property.Name}", "is not a supported field");
            Validate(property.Value, propertySchema, $"{path}.{property.Name}");
        }
    }

    private static void ValidateArray(JsonElement value, JsonElement itemSchema, string path)
    {
        if (value.ValueKind != JsonValueKind.Array) Fail(path, "must be an array");
        int index = 0;
        foreach (var item in value.EnumerateArray()) Validate(item, itemSchema, $"{path}[{index++}]");
    }

    private static void Fail(string path, string reason) =>
        throw new ScriptingException("invalid_arguments", $"Argument '{path}' {reason}.");
}
