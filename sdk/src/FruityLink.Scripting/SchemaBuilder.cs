using System.Reflection;
using System.Text.Json;

namespace FruityLink.Scripting;

internal static class SchemaBuilder
{
    internal static bool IsNullable(ParameterInfo parameter) => Nullable.GetUnderlyingType(parameter.ParameterType) is not null ||
        (!parameter.ParameterType.IsValueType && new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable);

    internal static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return TypeName(inner) + "?";
        if (CollectionElement(type) is { } element) return $"array<{TypeName(element)}>";
        if (type.IsGenericType) return $"{type.Name.Split('`')[0]}<{string.Join(", ", type.GenericTypeArguments.Select(TypeName))}>";
        return ScalarName(type) ?? type.Name;
    }

    internal static JsonElement ForParameter(ParameterInfo parameter)
    {
        object schema = Build(parameter.ParameterType, 0);
        if (IsNullable(parameter) && Nullable.GetUnderlyingType(parameter.ParameterType) is null)
            schema = new { anyOf = new[] { schema, new { type = "null" } } };
        return JsonSerializer.SerializeToElement(schema, ScriptingJson.Options);
    }

    internal static JsonElement ForType(Type type) => JsonSerializer.SerializeToElement(Build(type, 0), ScriptingJson.Options);

    private static object Build(Type type, int depth)
    {
        if (depth > 12) throw new InvalidOperationException("Recursive scripting argument type is not supported.");
        if (Nullable.GetUnderlyingType(type) is { } inner) return new { anyOf = new[] { Build(inner, depth + 1), new { type = "null" } } };
        if (type.IsEnum) return new Dictionary<string, object> { ["type"] = "string", ["enum"] = Enum.GetNames(type).Select(JsonNamingPolicy.CamelCase.ConvertName).ToArray() };
        if (ScalarName(type) is { } scalar) return new { type = scalar };
        if (CollectionElement(type) is { } element) return new { type = "array", items = Build(element, depth + 1) };
        return ObjectSchema(type, depth);
    }

    private static object ObjectSchema(Type type, int depth)
    {
        var constructor = type.GetConstructors().OrderByDescending(value => value.GetParameters().Length).FirstOrDefault();
        var parameters = constructor?.GetParameters().ToDictionary(parameter => parameter.Name!, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, ParameterInfo>(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            object schema = Build(property.PropertyType, depth + 1);
            if (IsNullableProperty(property) && Nullable.GetUnderlyingType(property.PropertyType) is null)
                schema = new { anyOf = new[] { schema, new { type = "null" } } };
            properties.Add(name, schema);
            if (parameters.TryGetValue(property.Name, out var parameter) && !parameter.HasDefaultValue) required.Add(name);
        }
        return new { type = "object", properties, required, additionalProperties = false };
    }

    private static bool IsNullableProperty(PropertyInfo property) => Nullable.GetUnderlyingType(property.PropertyType) is not null ||
        (!property.PropertyType.IsValueType && new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable);

    private static Type? CollectionElement(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) return type.GenericTypeArguments[0];
        return null;
    }

    private static string? ScalarName(Type type)
    {
        if (type == typeof(string)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "number";
        if (type.IsPrimitive && type != typeof(char)) return "integer";
        return null;
    }
}
