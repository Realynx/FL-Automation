using FruityLink.Core.Abstractions;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace FruityLink.Scripting;

internal sealed class OperationRegistry
{
    private readonly Dictionary<string, BoundOperation> _operations = new(StringComparer.Ordinal);
    internal ScriptingCatalog Catalog { get; }

    internal OperationRegistry(INativeFlControl control)
    {
        AddInterface(typeof(INativeFlControl));
        if (control is IFlStructuredQuery) AddInterface(typeof(IFlStructuredQuery));
        Catalog = new(1, _operations.Values.Select(value => value.Metadata).OrderBy(value => value.Name, StringComparer.Ordinal).ToArray());
    }

    internal BoundOperation Find(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation) || !_operations.TryGetValue(operation, out var bound))
            throw new ScriptingException("operation_not_found", $"Unknown scripting operation '{operation}'.");
        return bound;
    }

    private void AddInterface(Type contract)
    {
        foreach (var method in contract.GetMethods())
        {
            if (!typeof(Task).IsAssignableFrom(method.ReturnType))
                throw new InvalidOperationException($"Scripting contract method {method.Name} is not asynchronous.");
            var bound = new BoundOperation(method);
            if (!_operations.TryAdd(bound.Metadata.Name, bound))
                throw new InvalidOperationException($"Duplicate scripting operation {bound.Metadata.Name}.");
        }
    }
}

internal sealed class BoundOperation
{
    private readonly MethodInfo _method;
    private readonly ParameterInfo[] _parameters;
    private readonly PropertyInfo? _result;
    private readonly IReadOnlyDictionary<string, JsonElement> _argumentSchemas;
    internal ScriptingOperation Metadata { get; }

    internal BoundOperation(MethodInfo method)
    {
        _method = method;
        _parameters = method.GetParameters();
        _result = method.ReturnType.GetProperty("Result");
        string name = JsonNamingPolicy.SnakeCaseLower.ConvertName(method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name[..^5] : method.Name);
        var parameters = _parameters.Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .Select(ParameterMetadata).ToArray();
        _argumentSchemas = parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Schema, StringComparer.Ordinal);
        Metadata = new(name, OperationDocumentation.Describe(method.Name), parameters,
            _result is null ? null : SchemaBuilder.TypeName(_result.PropertyType), IsMutation(method.Name),
            OperationAvailability.Requirements(name), _result is null ? null : SchemaBuilder.ForType(_result.PropertyType));
    }

    /// <summary>Wire names accepted as aliases of a canonical parameter (operation -> alias, canonical): renamed
    /// parameters keep working from an older wheel, and the volume/pan setters accept the property name callers reach
    /// for ("volume"/"pan") next to the canonical "value". An alias is only honoured when the canonical name is absent.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<(string Alias, string Canonical)>> ArgumentAliases =
        new Dictionary<string, IReadOnlyList<(string, string)>>(StringComparer.Ordinal)
        {
            ["select_channel"] = new[] { ("index", "channel") },
            ["set_channel_solo"] = new[] { ("index", "channel") },
            ["set_channel_volume"] = new[] { ("volume", "value") },
            ["set_mixer_volume"] = new[] { ("volume", "value") },
            ["set_master_volume"] = new[] { ("volume", "value") },
            ["set_channel_pan"] = new[] { ("pan", "value") },
            ["set_mixer_pan"] = new[] { ("pan", "value") },
        };

    internal object?[] Bind(JsonElement arguments)
    {
        arguments = ApplyLegacyAliases(arguments);
        JsonValidation.Object(arguments, "arguments", Metadata.Parameters.Select(parameter => parameter.Name));
        JsonValidation.Values(arguments);
        return _parameters.Select(parameter => BindParameter(parameter, arguments)).ToArray();
    }

    internal async Task<object?> InvokeAsync(INativeFlControl control, object?[] values)
    {
        Task task;
        try { task = (Task)(_method.Invoke(control, values) ?? throw new InvalidOperationException("SDK operation returned no task.")); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        await task.ConfigureAwait(false);
        return _result?.GetValue(task);
    }

    private JsonElement ApplyLegacyAliases(JsonElement arguments)
    {
        if (!ArgumentAliases.TryGetValue(Metadata.Name, out var aliases) || arguments.ValueKind != JsonValueKind.Object)
            return arguments;
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (alias, canonical) in aliases)
            if (arguments.TryGetProperty(alias, out _) && !arguments.TryGetProperty(canonical, out _)) rename[alias] = canonical;
        if (rename.Count == 0) return arguments;
        var renamed = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            string name = rename.TryGetValue(property.Name, out var canonical) ? canonical : property.Name;
            if (!renamed.TryAdd(name, property.Value)) return arguments;   // duplicate keys: let the strict validation report them
        }
        return JsonSerializer.SerializeToElement(renamed, ScriptingJson.Options);
    }

    private object? BindParameter(ParameterInfo parameter, JsonElement arguments)
    {
        if (parameter.ParameterType == typeof(CancellationToken)) return CancellationToken.None;
        string name = JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!);
        if (!arguments.TryGetProperty(name, out var value))
        {
            if (parameter.HasDefaultValue) return parameter.DefaultValue;
            throw new ScriptingException("invalid_arguments", $"Missing required argument '{name}'.");
        }
        ArgumentSchemaValidation.Validate(value, _argumentSchemas[name], name);
        try { return value.Deserialize(parameter.ParameterType, ScriptingJson.Options); }
        catch (JsonException error) { throw new ScriptingException("invalid_arguments", $"Invalid argument '{name}': {error.Message}"); }
    }

    private static ScriptingParameter ParameterMetadata(ParameterInfo parameter) => new(
        JsonNamingPolicy.CamelCase.ConvertName(parameter.Name!), SchemaBuilder.TypeName(parameter.ParameterType),
        !parameter.HasDefaultValue, parameter.HasDefaultValue ? parameter.DefaultValue : null,
        SchemaBuilder.ForParameter(parameter));

    private static bool IsMutation(string name) => !new[] { "Get", "List", "Query", "Is" }
        .Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
}
