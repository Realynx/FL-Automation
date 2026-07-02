using Microsoft.SemanticKernel;

namespace FruityLink.Agent;

/// <summary>
/// Caps how many auto-invoke tool-call rounds a single turn may run. Without this, Semantic Kernel's
/// auto-function-calling loop only stops at its internal default (128 rounds) or the 10-minute HTTP
/// timeout — so a flaky backend that keeps re-calling the same tool (or ping-pongs between two) can
/// drive dozens of real FL bridge mutations and freeze the UI for minutes. When the cap is hit we
/// set <see cref="AutoFunctionInvocationContext.Terminate"/>, which ends the loop cleanly (SK returns
/// the assistant message it has) rather than throwing.
///
/// <para>On the cap round itself, Terminate is only set on the LAST function of the current batch:
/// setting it mid-batch would stop SK from invoking the batch's remaining calls, leaving assistant
/// tool_call ids without tool results. But SK skips this filter entirely for calls that fail
/// validation ("wasn't defined" / "could not be found"), so a batch whose LAST entry is invalid
/// never presents FunctionSequenceIndex == FunctionCount - 1 — waiting for batch end again could
/// repeat forever. Any invocation observed on an OVERSHOOT round (past the cap) therefore
/// terminates immediately, even mid-batch: <see cref="AgentTurnRunner"/> unconditionally repairs
/// the turn's tool_call/tool pairing afterwards and appends synthetic results for any orphans.
/// Rounds where EVERY call fails validation never reach this filter at all — those are bounded by
/// the per-turn HTTP request budget (see <c>LlmTurnBudget</c>).</para>
///
/// <para>Per-turn lifecycle: the filter lives as long as its kernel (added once by
/// <see cref="AgentKernelBuilder"/>), so the runner calls <see cref="BeginTurn"/> before each send
/// and reads <see cref="WasCapped"/> after — the flag never leaks across turns. Turns on one kernel
/// are serialized by the owning agent's gate/loop; the flag is volatile because it is written on
/// SK's execution thread and read on the awaiting caller's continuation.</para>
/// </summary>
internal sealed class AutoInvokeIterationFilter : IAutoFunctionInvocationFilter
{
    /// <summary>Max model round-trips (each may batch several parallel tool calls) per turn.</summary>
    private readonly int _maxRounds;

    /// <summary>Set when the current turn hit the cap; reset by <see cref="BeginTurn"/>.</summary>
    private volatile bool _wasCapped;

    public AutoInvokeIterationFilter(int maxRounds = 12) => _maxRounds = maxRounds;

    /// <summary>The configured round cap — the turn runner sizes its per-turn HTTP request budget
    /// (the backstop for filter-blind all-invalid rounds) from this.</summary>
    public int MaxRounds => _maxRounds;

    /// <summary>True when the turn since the last <see cref="BeginTurn"/> hit the round cap and was
    /// terminated early — the runner uses this to repair pairing and tell the user how to resume.</summary>
    public bool WasCapped => _wasCapped;

    /// <summary>Resets the per-turn cap signal. Call before each send on this filter's kernel.</summary>
    public void BeginTurn() => _wasCapped = false;

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        await next(context).ConfigureAwait(false);

        // RequestSequenceIndex is the 0-based auto-invoke round; stop once we've done enough. On
        // the cap round itself, terminate only at the batch's last function (a mid-batch Terminate
        // orphans the remaining calls for no gain). If we are ever invoked on an OVERSHOOT round —
        // reachable when the cap round's batch ENDED on a validation-failed call, which SK never
        // presents to this filter — terminate immediately, even mid-batch: waiting for a clean
        // batch end again may never happen, and the runner's unconditional pairing repair answers
        // any orphaned sibling tool_call ids (see class doc).
        if (context.RequestSequenceIndex >= _maxRounds - 1)
        {
            _wasCapped = true;
            if (context.RequestSequenceIndex >= _maxRounds
                || context.FunctionSequenceIndex == context.FunctionCount - 1)
            {
                context.Terminate = true;
            }
        }
    }
}
