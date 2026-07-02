using System.Net.Http;
using FruityLink.Core.Abstractions;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Thrown when a turn exceeds its per-turn LLM request budget (see <see cref="LlmTurnBudget"/>) —
/// the auto-invoke loop kept round-tripping without converging.
/// </summary>
public sealed class LlmTurnBudgetExceededException : InvalidOperationException
{
    /// <summary>The budget that was exhausted (requests allowed for the turn).</summary>
    public int Limit { get; }

    public LlmTurnBudgetExceededException(int limit) : base(
        $"LLM turn aborted after {limit} requests: the model kept requesting tool calls without " +
        "converging (for example, repeatedly calling a tool name that fails validation — those " +
        "rounds bypass the round-cap filter entirely). The turn was rolled back.")
        => Limit = limit;
}

/// <summary>
/// Ambient per-turn LLM HTTP request budget — the backstop for auto-invoke rounds the round-cap
/// filter can never see. Semantic Kernel skips auto-function-invocation filters for tool calls
/// that fail validation ("wasn't defined"), so a round whose calls ALL fail validation performs a
/// full model round-trip without the cap filter ever running; left unbounded, SK grinds through
/// its internal 128-attempt limit, one growing-history request per round. The turn runner opens a
/// scope (<see cref="Begin"/>) sized to its round cap plus slack, and
/// <see cref="LlmTurnBudgetHandler"/> — the OUTERMOST HTTP handler, so retries of one logical
/// request count once — throws <see cref="LlmTurnBudgetExceededException"/> past the budget.
///
/// <para>The scope flows via <see cref="AsyncLocal{T}"/>: parallel turns (sub-agents run their own
/// kernels concurrently) each see only their own budget, and a sub-agent turn started INSIDE a
/// parent turn's tool invocation shadows the parent's scope for its own flow only. Requests sent
/// with no active scope (connectivity probes, non-turn calls) are never limited.</para>
/// </summary>
public static class LlmTurnBudget
{
    private sealed class Scope
    {
        public int Limit;
        public int Sent; // via Interlocked — SK may issue requests from pooled threads
    }

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Opens a budget scope for the current async flow. Dispose restores the previous
    /// scope (or none), so nested turns compose.</summary>
    public static IDisposable Begin(int maxRequests)
    {
        Scope? prior = Current.Value;
        Current.Value = new Scope { Limit = maxRequests };
        return new Restorer(prior);
    }

    /// <summary>Counts one outgoing request against the active scope, if any; throws once the
    /// budget is exhausted. No-op when no scope is active.</summary>
    internal static void CountRequest()
    {
        Scope? scope = Current.Value;
        if (scope is null) return;
        if (Interlocked.Increment(ref scope.Sent) > scope.Limit)
            throw new LlmTurnBudgetExceededException(scope.Limit);
    }

    private sealed class Restorer(Scope? prior) : IDisposable
    {
        public void Dispose() => Current.Value = prior;
    }
}

/// <summary>
/// Enforces the ambient <see cref="LlmTurnBudget"/>. Must sit OUTERMOST in the handler chain —
/// above <see cref="LlmRetryHandler"/> — so the retries of one logical request consume one budget
/// unit, not one per attempt. A tripped budget is recorded to diagnostics (never silent) and then
/// thrown, which aborts the send; the turn runner rolls the turn's half-applied assistant/tool edits
/// back (keeping the user's message + the prior conversation), so no orphaned tool_call ids survive.
/// </summary>
public sealed class LlmTurnBudgetHandler : DelegatingHandler
{
    private readonly ILlmDiagnostics? _diagnostics;

    public LlmTurnBudgetHandler(HttpMessageHandler innerHandler, ILlmDiagnostics? diagnostics = null)
        : base(innerHandler)
        => _diagnostics = diagnostics;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            LlmTurnBudget.CountRequest();
        }
        catch (LlmTurnBudgetExceededException ex)
        {
            _diagnostics?.Record(new LlmCallRecord
            {
                Timestamp = DateTimeOffset.UtcNow,
                Method = request.Method.Method,
                Uri = request.RequestUri?.ToString() ?? string.Empty,
                StatusCode = null,
                Ok = false,
                Attempt = 1,
                Error = ex.Message,
            });
            throw;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
