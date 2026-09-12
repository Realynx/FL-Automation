using System.IO;
using System.Text.Json;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Ambient per-TURN structured transcript. One JSON object per agent turn is appended to
/// <c>%APPDATA%\FLAutomate\logs\turns-YYYYMMDD.jsonl</c> — the reviewable record of HOW the backend
/// LLM chose tools and whether it did so efficiently. Per turn it captures: the triggering prompt,
/// the model REQUESTED vs. the model USED (so backend swaps are attributable), the full ordered
/// tool-call sequence (fuller args/results than the plaintext <c>tools-*.log</c>), per-request token
/// usage + byte sizes, wire-repairs, round count, per-round reasoning, the final answer, status
/// (ok/capped/cancelled/error) and elapsed time.
///
/// <para>Correlation across the Agent→Llm layer boundary uses the SAME <see cref="AsyncLocal{T}"/>
/// scope pattern as <see cref="LlmTurnBudget"/>: <see cref="AgentTurnRunner"/> opens a scope that
/// flows down into SK's tool loop (the tool-call filter records each call) and the HTTP handlers
/// (which record per-request usage/model/repairs), and it nests cleanly for parallel sub-agent turns
/// — each sub-agent turn shadows the parent for its own async flow only, tagged with
/// <see cref="TurnScope.ParentTurnId"/>. Best-effort throughout: a transcript failure must never
/// fault a turn.</para>
/// </summary>
public static class SessionTranscript
{
    private static readonly AsyncLocal<TurnScope?> CurrentScope = new();

    /// <summary>The turn scope active on the current async flow, or null outside a turn.</summary>
    public static TurnScope? Current => CurrentScope.Value;

    /// <summary>One id per process run — a coarse "session" grouping for a run's turns. (A
    /// per-conversation id is a future refinement; a process id is enough to group turns today.)</summary>
    public static readonly string SessionId = Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Optional EXTRA sink for finished turns: receives the same serialized JSON record the local
    /// <c>turns-*.jsonl</c> line gets, AFTER the local write. Composition sets this once to the
    /// opt-in gateway debug uploader (which itself drops everything unless the user enabled
    /// "Share debug data") — so this class stays free of any HTTP/auth dependency. Both the invoke
    /// and the sink are best-effort: a sink failure must never fault a turn.
    /// </summary>
    public static Action<string>? UploadSink { get; set; }

    /// <summary>Opens a turn scope for the current async flow. Dispose writes the accumulated turn as
    /// one JSONL line and restores the previous scope (so nested sub-agent turns compose).</summary>
    public static IDisposable Begin(TurnScope scope)
    {
        TurnScope? prior = CurrentScope.Value;
        scope.ParentTurnId = prior?.TurnId;
        CurrentScope.Value = scope;
        return new Restorer(prior, scope);
    }

    private sealed class Restorer(TurnScope? prior, TurnScope scope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { SessionTranscriptWriter.Write(scope); } catch { /* logging must never fault a turn */ }
            try { UploadSink?.Invoke(SessionTranscriptWriter.Serialize(scope)); }
            catch { /* the opt-in upload is best-effort; it must never fault a turn either */ }
            CurrentScope.Value = prior;
        }
    }
}

/// <summary>
/// Accumulator for one turn's transcript. Written once when its <see cref="SessionTranscript"/> scope
/// disposes. Fed from three places on the turn's async flow: the runner (header + reasoning + footer
/// + model-used), the tool-call filter (each tool call), and the HTTP handlers (per-request usage +
/// wire-repairs). All mutation is gated because SK may fan work across pooled threads within a turn.
/// </summary>
public sealed class TurnScope
{
    private readonly object _gate = new();
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    private int _toolSeq;

    public string TurnId { get; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; }
    public string? ParentTurnId { get; set; }
    public DateTime StartedLocal { get; } = DateTime.Now;
    public string ModelRequested { get; }
    public string Prompt { get; }
    /// <summary>Tools advertised THIS turn: the per-turn subset size, or -1 for the full surface.</summary>
    public int AdvertisedTools { get; }
    public bool Streamed { get; }

    // Model actually used, per the response (differs from requested when "default"/plan-routing or a
    // stale-model fallback rewrote it). Captured from the reply/chunk ModelId + the HTTP response body.
    public string? ModelUsed { get; private set; }

    internal readonly List<RequestRec> Requests = new();
    internal readonly List<ToolRec> Tools = new();
    internal readonly List<string> Reasonings = new();
    internal readonly Dictionary<string, int> Repairs = new(StringComparer.Ordinal);

    // Footer (set by the runner at turn end).
    public string Status { get; private set; } = "ok";
    public bool WasCapped { get; private set; }
    public string FinalThought { get; private set; } = string.Empty;
    public string FinalText { get; private set; } = string.Empty;
    public string? Error { get; private set; }
    public long ElapsedMs { get; private set; }

    public TurnScope(string sessionId, string modelRequested, string prompt, int advertisedTools, bool streamed)
    {
        SessionId = sessionId;
        ModelRequested = modelRequested;
        Prompt = prompt;
        AdvertisedTools = advertisedTools;
        Streamed = streamed;
    }

    /// <summary>Records the model the backend reported (first non-empty wins — it's stable within a turn).</summary>
    public void SetModelUsed(string? modelUsed)
    {
        if (string.IsNullOrEmpty(modelUsed)) return;
        lock (_gate) ModelUsed ??= modelUsed;
    }

    /// <summary>One successful (buffered) chat-completion request's token/byte telemetry. The HTTP
    /// layer only sees usage on non-streamed 2xx bodies, so streamed turns may record none — the
    /// round COUNT still comes from <see cref="Reasonings"/>/assistant messages instead.</summary>
    public void RecordRequest(string? modelUsed, long promptTokens, long completionTokens, long requestBytes, long toolsBytes)
    {
        SetModelUsed(modelUsed);
        lock (_gate) Requests.Add(new RequestRec(modelUsed, promptTokens, completionTokens, requestBytes, toolsBytes));
    }

    /// <summary>Adds wire-repair counts (by kind) the repair handler applied to a response.</summary>
    public void AddRepairs(IEnumerable<KeyValuePair<string, int>> kinds)
    {
        lock (_gate)
            foreach (KeyValuePair<string, int> k in kinds)
                Repairs[k.Key] = Repairs.GetValueOrDefault(k.Key) + k.Value;
    }

    /// <summary>Records one tool call the model invoked, in order, tagged with the round it belongs to
    /// (the request count seen so far — best-effort on the streamed path where requests aren't recorded).</summary>
    public void RecordTool(string name, string args, string? result, long ms)
    {
        lock (_gate)
            Tools.Add(new ToolRec(Interlocked.Increment(ref _toolSeq), Requests.Count, name, args, result ?? string.Empty, ms));
    }

    /// <summary>Sets the per-round reasoning (the intermediate rounds' <c>&lt;think&gt;</c> blocks,
    /// captured before the runner strips them from history). The final round's reasoning is the
    /// footer's <see cref="FinalThought"/>.</summary>
    public void SetReasonings(IEnumerable<string> reasonings)
    {
        lock (_gate)
        {
            Reasonings.Clear();
            foreach (string r in reasonings)
                if (!string.IsNullOrWhiteSpace(r)) Reasonings.Add(r);
        }
    }

    /// <summary>Discards telemetry an ABORTED attempt accumulated when the turn retries (the
    /// streamed→buffered fallback), so the written transcript reflects only the attempt that actually
    /// completed — mirroring the history rollback (<c>TruncateHistoryTo</c>) on that same path.
    /// (Reasonings are also re-filled by FinishTurn; ModelUsed is kept — the retry hits the same model.)</summary>
    public void ResetForRetry()
    {
        lock (_gate)
        {
            Tools.Clear();
            Requests.Clear();
            Repairs.Clear();
            Reasonings.Clear();
            _toolSeq = 0;
        }
    }

    /// <summary>Seals the turn: status, cap flag, final reasoning/answer, error, and elapsed time.</summary>
    public void Finish(string status, bool wasCapped, string finalThought, string finalText, string? error)
    {
        lock (_gate)
        {
            Status = status;
            WasCapped = wasCapped;
            FinalThought = finalThought ?? string.Empty;
            FinalText = finalText ?? string.Empty;
            Error = error;
            ElapsedMs = _sw.ElapsedMilliseconds;
        }
    }

    internal readonly record struct RequestRec(
        string? ModelUsed, long PromptTokens, long CompletionTokens, long RequestBytes, long ToolsBytes);

    internal readonly record struct ToolRec(int Seq, int Round, string Name, string Args, string Result, long Ms);
}

/// <summary>Serializes a finished <see cref="TurnScope"/> to one JSONL line in the shared logs dir.
/// Best-effort + test-host-suppressed, exactly like the plaintext tool transcript.</summary>
internal static class SessionTranscriptWriter
{
    private static readonly object FileGate = new();

    // Don't pollute the user's real transcript when running under a test host (mirrors ToolCallFilter).
    private static readonly bool Suppress =
        (AppContext.GetData("FLAUTOMATE_TEST") as bool? ?? false)
        || (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty)
            .Contains("testhost", StringComparison.OrdinalIgnoreCase)
        || (AppDomain.CurrentDomain.FriendlyName ?? string.Empty)
            .Contains("testhost", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static void Write(TurnScope s)
    {
        if (Suppress) return;

        string line = Serialize(s);
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FLAutomate", "logs");
            Directory.CreateDirectory(dir);
            lock (FileGate)
                File.AppendAllText(Path.Combine(dir, $"turns-{DateTime.Now:yyyyMMdd}.jsonl"), line + Environment.NewLine);
        }
        catch { /* transcript logging must never break tool execution */ }
    }

    /// <summary>Builds the one-line JSON record for a turn (no file I/O, no suppression) — the
    /// testable seam so the transcript shape can be asserted without a live backend.</summary>
    internal static string Serialize(TurnScope s)
    {
        long? promptTotal = null, completionTotal = null;
        if (s.Requests.Count > 0)
        {
            promptTotal = s.Requests.Sum(r => r.PromptTokens);
            completionTotal = s.Requests.Sum(r => r.CompletionTokens);
        }

        // Round count: buffered turns count HTTP requests; streamed turns (no usage recorded) fall
        // back to the number of assistant reasoning blocks + 1 for the final answer round.
        int rounds = Math.Max(s.Requests.Count, s.Reasonings.Count + 1);

        var record = new
        {
            ts = s.StartedLocal.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
            session = s.SessionId,
            turn = s.TurnId,
            parent = s.ParentTurnId,
            streamed = s.Streamed,
            model_requested = s.ModelRequested,
            model_used = s.ModelUsed,
            prompt = Clip(s.Prompt, 2000),
            advertised_tools = s.AdvertisedTools,
            status = s.Status,
            capped = s.WasCapped,
            error = s.Error is null ? null : Clip(s.Error, 500),
            rounds,
            tool_calls = s.Tools.Count,
            elapsed_ms = s.ElapsedMs,
            tokens = promptTotal is null
                ? null
                : new { prompt = promptTotal, completion = completionTotal, total = promptTotal + completionTotal },
            repairs = s.Repairs.Count == 0 ? null : s.Repairs,
            requests = s.Requests.Select(r => new
            {
                model = r.ModelUsed,
                prompt_tokens = r.PromptTokens,
                completion_tokens = r.CompletionTokens,
                req_bytes = r.RequestBytes,
                tools_bytes = r.ToolsBytes,
            }),
            reasoning = s.Reasonings.Select(r => Clip(r, 4000)),
            final_thought = Clip(s.FinalThought, 4000),
            final_text = Clip(s.FinalText, 4000),
            calls = s.Tools.Select(t => new
            {
                seq = t.Seq,
                round = t.Round,
                name = t.Name,
                args = Clip(t.Args, 2000),
                result = Clip(t.Result, 2000),
                ms = t.Ms,
            }),
        };

        return JsonSerializer.Serialize(record, Options);
    }

    private static string Clip(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? string.Empty : s[..max] + $"…(+{s.Length - max})";
}
