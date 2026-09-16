using FruityLink.Core.Abstractions;
using System.Text.Json;

namespace FruityLink.Scripting;

/// <summary>A public scripting operation's argument contract.</summary>
/// <param name="Name">Case-sensitive camelCase argument name.</param>
/// <param name="Type">Human-readable CLR-independent type name.</param>
/// <param name="Required">Whether the argument must be present, independently of nullability.</param>
/// <param name="DefaultValue">Value used when an optional argument is omitted.</param>
/// <param name="Schema">JSON Schema describing accepted values.</param>
public sealed record ScriptingParameter(string Name, string Type, bool Required, object? DefaultValue, JsonElement Schema);

/// <summary>An allowlisted SDK operation and its input/output description.</summary>
/// <param name="Name">Stable snake_case operation name.</param>
/// <param name="Description">SDK documentation for the operation.</param>
/// <param name="Parameters">Named parameters, excluding the internal cancellation token.</param>
/// <param name="ReturnType">Human-readable result type, or null for a command.</param>
/// <param name="Mutates">Whether this operation may change project or application state.</param>
/// <param name="Requires">Runtime capabilities required before dispatch.</param>
/// <param name="ReturnSchema">JSON Schema describing a successful result, or null for a command.</param>
public sealed record ScriptingOperation(string Name, string Description, IReadOnlyList<ScriptingParameter> Parameters,
    string? ReturnType, bool Mutates, IReadOnlyList<string> Requires, JsonElement? ReturnSchema = null);

/// <summary>The versioned, machine-readable SDK operation catalogue.</summary>
/// <param name="ApiVersion">Scripting protocol version.</param>
/// <param name="Operations">Operations supported by the supplied typed control interfaces.</param>
public sealed record ScriptingCatalog(int ApiVersion, IReadOnlyList<ScriptingOperation> Operations);

/// <summary>Identity and availability of the current scripting backend.</summary>
/// <param name="ApiVersion">Scripting protocol version.</param>
/// <param name="Pid">Process hosting the control surface.</param>
/// <param name="InstanceId">Unique dispatcher lifetime identifier.</param>
/// <param name="OperationCount">Number of catalogued operations.</param>
/// <param name="Available">Whether the control surface currently responds.</param>
/// <param name="Symbols">Optional native version/scanner diagnostics.</param>
/// <param name="MixerLayoutAvailable">Whether complete mixer offsets are verified.</param>
/// <param name="UnavailableOperations">Operation names with an explanation when a known capability is missing.</param>
public sealed record ScriptingCapabilities(int ApiVersion, int Pid, string InstanceId, int OperationCount,
    bool Available, FlSymbolStatus? Symbols, bool MixerLayoutAvailable,
    IReadOnlyDictionary<string, string> UnavailableOperations);

/// <summary>A normalized error suitable for local scripting clients.</summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Message">Human-readable error detail, without a stack trace.</param>
/// <param name="Data">Optional structured detail.</param>
public sealed record ScriptingError(string Code, string Message, object? Data = null)
{
    /// <summary>Maps an exception without exposing internal stack traces.</summary>
    public static ScriptingError FromException(Exception error) => error switch
    {
        ScriptingException scripting => new(scripting.Code, scripting.Message),
        OperationCanceledException => new("cancelled", "The operation was cancelled; earlier changes may have completed."),
        ArgumentException => new("invalid_arguments", error.Message),
        JsonException => new("invalid_arguments", error.Message),
        _ => new("operation_failed", error.Message)
    };
}

/// <summary>An expected scripting failure with a stable protocol code.</summary>
public sealed class ScriptingException : Exception
{
    /// <summary>Creates a scripting failure.</summary>
    public ScriptingException(string code, string message) : base(message) => Code = code;
    /// <summary>Machine-readable error code.</summary>
    public string Code { get; }
}

/// <summary>One operation in a serialized, non-transactional batch.</summary>
/// <param name="Operation">Catalogue operation name.</param>
/// <param name="Arguments">Named operation arguments.</param>
public sealed record ScriptingCall(string Operation, JsonElement Arguments);

/// <summary>The outcome of a single attempted batch operation.</summary>
/// <param name="Operation">Operation name.</param>
/// <param name="Result">Successful return value, including null for commands.</param>
/// <param name="Error">Failure detail, or null on success.</param>
public sealed record ScriptingBatchItem(string Operation, object? Result, ScriptingError? Error);

/// <summary>A batch's ordered outcomes; completed changes are never rolled back.</summary>
/// <param name="Results">Outcomes of attempted operations.</param>
/// <param name="StoppedOnError">Whether the remaining operations were skipped after a failure.</param>
public sealed record ScriptingBatchResult(IReadOnlyList<ScriptingBatchItem> Results, bool StoppedOnError);

/// <summary>Per-user discovery metadata for one authenticated local endpoint.</summary>
/// <param name="ApiVersion">Scripting protocol version.</param>
/// <param name="Pid">Process hosting the endpoint.</param>
/// <param name="InstanceId">Unique endpoint lifetime identifier.</param>
/// <param name="PipeName">Windows named-pipe name without a path prefix.</param>
/// <param name="Token">Secret authenticating this endpoint's local requests.</param>
/// <param name="CreatedAt">UTC creation timestamp.</param>
public sealed record ScriptingEndpoint(int ApiVersion, int Pid, string InstanceId, string PipeName,
    string Token, DateTimeOffset CreatedAt);
