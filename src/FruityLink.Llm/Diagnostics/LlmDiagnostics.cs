using System.Globalization;
using System.IO;
using System.Text;
using FruityLink.Core.Abstractions;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Kinds of wire-layer repair <see cref="LlmToolCallRepairHandler"/> performs on a response, used
/// to key the per-session counters so the UI/logs can show which malformations a backend actually
/// produces (and how often).
/// </summary>
public enum LlmRepairKind
{
    /// <summary>A missing, empty, or duplicate <c>tool_call.id</c> was replaced with a synthesized one.</summary>
    IdSynth,

    /// <summary>A tool-call function name was rewritten to the advertised spelling.</summary>
    NameResolve,

    /// <summary>Tool-call arguments were coerced/repaired into a strict JSON object string.</summary>
    ArgsCoerce,

    /// <summary>A non-standard <c>reasoning_content</c>/<c>reasoning</c> field was folded into <c>content</c>.</summary>
    ReasoningFold,

    /// <summary>The repair pass could not parse the response body and passed it through unrepaired.</summary>
    ParseFailure,

    /// <summary>Arguments were unrecoverable and collapsed to <c>{}</c> — potential argument loss.</summary>
    EmptyArgsFallback,
}

/// <summary>
/// Optional capability of an <see cref="ILlmDiagnostics"/> sink: per-kind session counters for
/// wire-layer repairs. Defined in this layer rather than on Core's <see cref="ILlmDiagnostics"/>
/// because repair kinds are an HTTP-pipeline concept this assembly owns — Core stays
/// transport-agnostic. Handlers discover the capability via a soft cast and degrade gracefully
/// when a custom sink doesn't implement it.
/// </summary>
public interface ILlmRepairTelemetry
{
    /// <summary>Adds <paramref name="count"/> occurrences of <paramref name="kind"/> to the session counters.</summary>
    void CountRepair(LlmRepairKind kind, int count = 1);

    /// <summary>Snapshot of the per-kind repair counts accumulated this session.</summary>
    IReadOnlyDictionary<LlmRepairKind, long> RepairCounters { get; }
}

/// <summary>
/// One chat-completion request's token/byte usage, parsed from the response's <c>usage</c> block
/// plus a cheap measurement of the request body.
/// </summary>
/// <param name="PromptTokens">The response's <c>usage.prompt_tokens</c> (0 when absent).</param>
/// <param name="CompletionTokens">The response's <c>usage.completion_tokens</c> (0 when absent).</param>
/// <param name="RequestBytes">UTF-8 byte length of the whole request body.</param>
/// <param name="ToolsBytes">UTF-8 byte length of the request's <c>"tools": […]</c> property span.</param>
public readonly record struct LlmUsageSample(
    long PromptTokens, long CompletionTokens, long RequestBytes, long ToolsBytes);

/// <summary>
/// Session-cumulative usage totals. The tools-byte figures exist so the token savings of the
/// tool-surface diet work can be measured against real traffic, not estimated.
/// </summary>
public sealed record LlmUsageTotals
{
    /// <summary>Number of chat-completion responses that carried a <c>usage</c> block.</summary>
    public long Requests { get; init; }

    /// <summary>Cumulative prompt (input) tokens.</summary>
    public long PromptTokens { get; init; }

    /// <summary>Cumulative completion (output) tokens.</summary>
    public long CompletionTokens { get; init; }

    /// <summary>Cumulative UTF-8 bytes of request bodies sent.</summary>
    public long RequestBytes { get; init; }

    /// <summary>Cumulative UTF-8 bytes of the requests' <c>"tools"</c> property spans.</summary>
    public long ToolsBytes { get; init; }

    /// <summary>Fraction (0..1) of request bytes spent advertising tools.</summary>
    public double ToolsByteShare => RequestBytes > 0 ? (double)ToolsBytes / RequestBytes : 0;

    /// <summary>Compact session summary for the UI, e.g. <c>"41.2k in / 1.1k out"</c>.</summary>
    public string Summary => $"{FormatCount(PromptTokens)} in / {FormatCount(CompletionTokens)} out";

    /// <summary>Formats a count compactly ("987", "41.2k", "1.3M") — invariant culture so log
    /// lines don't grow locale-dependent decimal separators.</summary>
    public static string FormatCount(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Optional capability of an <see cref="ILlmDiagnostics"/> sink: token/byte usage telemetry.
/// Same rationale as <see cref="ILlmRepairTelemetry"/> — lives here, not in Core.
/// </summary>
public interface ILlmUsageTelemetry
{
    /// <summary>Records one request's usage sample and folds it into the session totals.</summary>
    void RecordUsage(LlmUsageSample sample);

    /// <summary>Snapshot of the session-cumulative usage totals.</summary>
    LlmUsageTotals UsageTotals { get; }
}

/// <summary>
/// Default <see cref="ILlmDiagnostics"/>: keeps the last N calls in memory and appends a rolling
/// per-day log file. Every call gets a one-line summary; failures also log the request/response
/// bodies. Also the default <see cref="ILlmRepairTelemetry"/>/<see cref="ILlmUsageTelemetry"/>
/// sink: per-kind wire-repair counters and per-session token/byte totals for the UI and logs.
/// </summary>
public sealed class LlmDiagnostics : ILlmDiagnostics, ILlmRepairTelemetry, ILlmUsageTelemetry
{
    private const int Capacity = 200;

    // Two locks on purpose: _gate guards the in-memory state the UI reads (ring buffer, counters,
    // usage totals); _fileGate serializes disk appends. Splitting them means a UI read of
    // Recent/RepairCounters/UsageTotals never waits behind a slow File.AppendAllText.
    private readonly object _gate = new();
    private readonly object _fileGate = new();

    private readonly LinkedList<LlmCallRecord> _recent = new();
    private readonly Dictionary<LlmRepairKind, long> _repairCounters = new();
    private LlmUsageTotals _usageTotals = new();
    private readonly string _logDirectory;

    public LlmDiagnostics(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public string LogDirectory => _logDirectory;

    public IReadOnlyList<LlmCallRecord> Recent
    {
        get { lock (_gate) return _recent.ToArray(); }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<LlmRepairKind, long> RepairCounters
    {
        get { lock (_gate) return new Dictionary<LlmRepairKind, long>(_repairCounters); }
    }

    /// <inheritdoc />
    public LlmUsageTotals UsageTotals
    {
        get { lock (_gate) return _usageTotals; }
    }

    public event EventHandler? Changed;

    public void Record(LlmCallRecord record)
    {
        lock (_gate)
        {
            _recent.AddFirst(record);
            while (_recent.Count > Capacity) _recent.RemoveLast();
        }

        AppendToFile(record);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void CountRepair(LlmRepairKind kind, int count = 1)
    {
        if (count <= 0) return;
        lock (_gate)
            _repairCounters[kind] = _repairCounters.TryGetValue(kind, out long current) ? current + count : count;
        // No Changed here: the repair handler pairs every counted repair with a Record() call,
        // which already notifies listeners once per repaired response.
    }

    /// <inheritdoc />
    public void RecordUsage(LlmUsageSample sample)
    {
        LlmUsageTotals totals;
        lock (_gate)
        {
            totals = _usageTotals with
            {
                Requests = _usageTotals.Requests + 1,
                PromptTokens = _usageTotals.PromptTokens + sample.PromptTokens,
                CompletionTokens = _usageTotals.CompletionTokens + sample.CompletionTokens,
                RequestBytes = _usageTotals.RequestBytes + sample.RequestBytes,
                ToolsBytes = _usageTotals.ToolsBytes + sample.ToolsBytes,
            };
            _usageTotals = totals;
        }

        AppendUsageToFile(sample, totals);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        // "Session" state resets together: a user clearing the call list expects the counters and
        // totals shown next to it to restart too (log files are kept, as documented on Core).
        lock (_gate)
        {
            _recent.Clear();
            _repairCounters.Clear();
            _usageTotals = new LlmUsageTotals();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AppendToFile(LlmCallRecord r)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("]  ")
          .Append(r.Method).Append(' ').Append(r.Uri)
          .Append("  -> ").Append(r.StatusCode?.ToString() ?? "(no response)")
          .Append("  ").Append(r.ElapsedMs).Append("ms");
        if (r.Attempt > 1) sb.Append("  attempt ").Append(r.Attempt);
        if (!r.Ok) sb.Append("  ERROR: ").Append(r.Error);
        sb.AppendLine();

        // Previews are logged whenever present (not just on failure): successful REPAIR entries
        // carry their kind/snippet detail in ResponsePreview, and that detail is the whole point.
        if (!string.IsNullOrEmpty(r.RequestPreview))
            sb.Append("  REQUEST: ").AppendLine(r.RequestPreview);
        if (!string.IsNullOrEmpty(r.ResponsePreview))
            sb.Append("  RESPONSE: ").AppendLine(r.ResponsePreview);

        AppendText(r.Timestamp, sb.ToString());
    }

    private void AppendUsageToFile(LlmUsageSample sample, LlmUsageTotals totals)
    {
        DateTimeOffset timestamp = DateTimeOffset.Now;
        var sb = new StringBuilder();
        sb.Append('[').Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("]  USAGE  ")
          .Append(sample.PromptTokens).Append(" in / ").Append(sample.CompletionTokens).Append(" out");
        if (sample.RequestBytes > 0)
            sb.Append("  tools ").Append(sample.ToolsBytes).Append(" B of ")
              .Append(sample.RequestBytes).Append(" B request");
        sb.Append("  |  session: ").Append(totals.Summary).AppendLine();

        AppendText(timestamp, sb.ToString());
    }

    private void AppendText(DateTimeOffset timestamp, string text)
    {
        try
        {
            string path = Path.Combine(_logDirectory, $"llm-{timestamp:yyyyMMdd}.log");
            lock (_fileGate)
                File.AppendAllText(path, text);
        }
        catch
        {
            // Diagnostics must never break the call path.
        }
    }
}
