using System.Text.Json;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;

namespace FruityLink.Agent.Versioning;

/// <summary>
/// Concrete <see cref="IInverseOpRegistry"/>: a case-sensitive op-id → <see cref="IInverseOp"/> map,
/// populated once (see <see cref="InverseOps.CreateRegistry"/>). An unregistered op reports
/// <see cref="CanInvert"/> = false, which taints its commit to the <c>.flp</c> fallback.
/// </summary>
public sealed class InverseOpRegistry : IInverseOpRegistry
{
    private readonly Dictionary<string, IInverseOp> _ops = new(StringComparer.Ordinal);

    /// <summary>Register (or replace) the inverse op for <paramref name="op"/>. Returns this for chaining.</summary>
    public InverseOpRegistry Register(string op, IInverseOp inverseOp)
    {
        ArgumentException.ThrowIfNullOrEmpty(op);
        ArgumentNullException.ThrowIfNull(inverseOp);
        _ops[op] = inverseOp;
        return this;
    }

    /// <inheritdoc />
    public bool TryGet(string op, out IInverseOp inverseOp)
    {
        if (op is not null && _ops.TryGetValue(op, out var found))
        {
            inverseOp = found;
            return true;
        }
        inverseOp = null!;
        return false;
    }

    /// <inheritdoc />
    public bool CanInvert(ChangeRecord record) =>
        record is not null && _ops.ContainsKey(record.Op);
}

/// <summary>
/// An <see cref="IInverseOp"/> built from two delegates — the read-before-write and the apply — so each
/// op is defined in one place as a pair of lambdas instead of a bespoke class.
/// </summary>
public sealed class DelegateInverseOp(
    Func<INativeFlControl, IReadOnlyDictionary<string, object?>, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> read,
    Func<INativeFlControl, IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>?, CancellationToken, Task> apply)
    : IInverseOp
{
    public Task<IReadOnlyDictionary<string, object?>> ReadAsync(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target, CancellationToken ct = default)
        => read(fl, target, ct);

    public Task ApplyAsync(
        INativeFlControl fl, IReadOnlyDictionary<string, object?> target,
        IReadOnlyDictionary<string, object?>? value, CancellationToken ct = default)
        => apply(fl, target, value, ct);
}

/// <summary>
/// Tolerant readers for journal value tokens. Values captured in-process are raw CLR scalars, but after a
/// disk round-trip they arrive as <see cref="JsonElement"/> — these accessors accept both so the same
/// inverse-op code works whether the record is fresh or reloaded.
/// </summary>
public static class JournalValue
{
    public static long AsLong(object? v) => v switch
    {
        null => 0,
        long l => l,
        int i => i,
        short s => s,
        double d => (long)d,
        float f => (long)f,
        bool b => b ? 1 : 0,
        string str => long.TryParse(str, out var r) ? r : 0,
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : (long)e.GetDouble(),
            JsonValueKind.String => long.TryParse(e.GetString(), out var r) ? r : 0,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => 0,
        },
        _ => 0,
    };

    public static int AsInt(object? v) => (int)AsLong(v);

    public static double AsDouble(object? v) => v switch
    {
        null => 0,
        double d => d,
        float f => f,
        long l => l,
        int i => i,
        bool b => b ? 1 : 0,
        string s => double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0,
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDouble(),
            JsonValueKind.String => double.TryParse(e.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0,
            _ => 0,
        },
        _ => 0,
    };

    public static bool AsBool(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => bool.TryParse(s, out var r) ? r : s is "1",
        JsonElement e => e.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => e.GetDouble() != 0,
            JsonValueKind.String => bool.TryParse(e.GetString(), out var r) && r,
            _ => false,
        },
        _ => false,
    };

    public static string AsString(object? v) => v switch
    {
        null => string.Empty,
        string s => s,
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.ToString(),
        _ => v.ToString() ?? string.Empty,
    };

    /// <summary>Read a named field from a token dictionary (null-safe), then coerce with the As* helpers.</summary>
    public static object? Field(IReadOnlyDictionary<string, object?>? token, string name)
        => token is not null && token.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Builds the small immutable identity/value dictionaries carried by <see cref="ChangeRecord"/>.</summary>
public static class JournalDict
{
    /// <summary>An empty target (for global scalars like tempo).</summary>
    public static IReadOnlyDictionary<string, object?> Empty { get; } =
        new Dictionary<string, object?>(0);

    public static IReadOnlyDictionary<string, object?> Of(string k1, object? v1)
        => new Dictionary<string, object?>(1) { [k1] = v1 };

    public static IReadOnlyDictionary<string, object?> Of(string k1, object? v1, string k2, object? v2)
        => new Dictionary<string, object?>(2) { [k1] = v1, [k2] = v2 };

    public static IReadOnlyDictionary<string, object?> Of(string k1, object? v1, string k2, object? v2, string k3, object? v3)
        => new Dictionary<string, object?>(3) { [k1] = v1, [k2] = v2, [k3] = v3 };

    public static IReadOnlyDictionary<string, object?> Of(
        string k1, object? v1, string k2, object? v2, string k3, object? v3, string k4, object? v4)
        => new Dictionary<string, object?>(4) { [k1] = v1, [k2] = v2, [k3] = v3, [k4] = v4 };

    public static IReadOnlyDictionary<string, object?> Of(
        string k1, object? v1, string k2, object? v2, string k3, object? v3, string k4, object? v4, string k5, object? v5)
        => new Dictionary<string, object?>(5) { [k1] = v1, [k2] = v2, [k3] = v3, [k4] = v4, [k5] = v5 };
}
