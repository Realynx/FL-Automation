namespace FruityLink.Core.Abstractions;

/// <summary>One LLM HTTP round-trip (one attempt), captured for diagnostics.</summary>
public sealed record LlmCallRecord
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Method { get; init; }
    public required string Uri { get; init; }

    /// <summary>HTTP status code, or null if the request threw before a response.</summary>
    public int? StatusCode { get; init; }

    public long ElapsedMs { get; init; }

    /// <summary>True for a 2xx response.</summary>
    public bool Ok { get; init; }

    /// <summary>Convenience inverse of <see cref="Ok"/> for UI binding.</summary>
    public bool IsError => !Ok;

    /// <summary>Attempt number (1-based); &gt;1 means this was a retry.</summary>
    public int Attempt { get; init; }

    /// <summary>Status reason or exception summary, when not OK.</summary>
    public string? Error { get; init; }

    /// <summary>Request body snippet — captured only on failure.</summary>
    public string? RequestPreview { get; init; }

    /// <summary>Response body snippet — captured only on failure.</summary>
    public string? ResponsePreview { get; init; }

    /// <summary>A compact one-line summary for lists/logs.</summary>
    public string Summary =>
        $"{Timestamp:HH:mm:ss}  {Method} {Trim(Uri)}  →  {(StatusCode?.ToString() ?? "—")}" +
        $"  {ElapsedMs} ms{(Attempt > 1 ? $"  (attempt {Attempt})" : string.Empty)}" +
        $"{(Ok ? string.Empty : $"  ✕ {Error}")}";

    private static string Trim(string uri)
    {
        int q = uri.IndexOf('?');
        return q > 0 ? uri[..q] : uri;
    }
}

/// <summary>
/// Captures LLM HTTP traffic (every request/response attempt, with bodies on failure) to an
/// in-memory ring buffer and a rolling log file, so intermittent backend errors during
/// tool-calling turns can be diagnosed.
/// </summary>
public interface ILlmDiagnostics
{
    /// <summary>Folder where rolling <c>llm-*.log</c> files are written.</summary>
    string LogDirectory { get; }

    /// <summary>Most-recent-first snapshot of recent calls (bounded ring buffer).</summary>
    IReadOnlyList<LlmCallRecord> Recent { get; }

    /// <summary>Raised (possibly off the UI thread) whenever a new call is recorded or cleared.</summary>
    event EventHandler? Changed;

    /// <summary>Records one call attempt (invoked by the HTTP handlers).</summary>
    void Record(LlmCallRecord record);

    /// <summary>Clears the in-memory buffer (log files are kept).</summary>
    void Clear();
}
