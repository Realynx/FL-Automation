using Microsoft.SemanticKernel;

namespace FruityLink.Agent;

/// <summary>
/// Caps how many auto-invoke tool-call rounds a single turn may run. Without this, Semantic Kernel's
/// auto-function-calling loop only stops at its internal default (128 rounds) or the 10-minute HTTP
/// timeout — so a flaky backend that keeps re-calling the same tool (or ping-pongs between two) can
/// drive dozens of real FL bridge mutations and freeze the UI for minutes. When the cap is hit we
/// set <see cref="AutoFunctionInvocationContext.Terminate"/>, which ends the loop cleanly (SK returns
/// the assistant message it has, with clean tool_call/tool pairing) rather than throwing.
/// </summary>
public sealed class AutoInvokeIterationFilter : IAutoFunctionInvocationFilter
{
    /// <summary>Max model round-trips (each may batch several parallel tool calls) per turn.</summary>
    private readonly int _maxRounds;

    public AutoInvokeIterationFilter(int maxRounds = 12) => _maxRounds = maxRounds;

    public async Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        await next(context).ConfigureAwait(false);

        // RequestSequenceIndex is the 0-based auto-invoke round; stop once we've done enough. Terminate
        // takes effect after the current function completes, so the pairing stays valid.
        if (context.RequestSequenceIndex >= _maxRounds - 1)
            context.Terminate = true;
    }
}
