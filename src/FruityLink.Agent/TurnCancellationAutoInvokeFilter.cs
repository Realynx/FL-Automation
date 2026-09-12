using Microsoft.SemanticKernel;

namespace FruityLink.Agent;

/// <summary>
/// Stops the auto-invoke tool loop the instant the user hits Stop, so NO new tool call is dispatched
/// after cancellation — including partway through a long chain of tool calls.
///
/// <para>WHY A FILTER (and not just a token check in the turn runner): Semantic Kernel owns the
/// tool-call loop, and its function-calls processor wraps every invocation in a broad
/// <c>catch (Exception)</c> that turns the exception into an "Error: …" tool result and CONTINUES the
/// loop. That means throwing <see cref="OperationCanceledException"/> from a tool or an
/// <see cref="IFunctionInvocationFilter"/> is SWALLOWED — the loop keeps calling the round's remaining
/// tools (SK only re-checks the token at the HTTP/round boundary, not between the sequential tool
/// calls of a single round). The one reliable way to end the loop from inside it is
/// <see cref="AutoFunctionInvocationContext.Terminate"/>, which SK checks after each call and, when
/// set, returns immediately. This filter sets it.</para>
///
/// <para>The token here is the SAME per-turn <see cref="System.Threading.CancellationToken"/> the UI's
/// Stop button cancels: SK threads the chat-completion token into every
/// <see cref="AutoFunctionInvocationContext.CancellationToken"/>. The check runs BEFORE dispatching
/// the pending tool (we do not call <paramref name="next"/> when cancelled), so that tool never runs;
/// a tool ALREADY executing is already inside its own invocation and finishes its single native op —
/// we never hard-kill a mid-flight bridge call, which could corrupt FL.</para>
///
/// <para>Stateless and kernel-lifetime (added once by <see cref="AgentKernelBuilder"/>); it reads only
/// the ambient per-invocation token, so nothing leaks across turns. Deliberately SEPARATE from
/// <see cref="AutoInvokeIterationFilter"/> (the tool-round cap) so cancellation and round-limit
/// concerns evolve — and merge — independently.</para>
/// </summary>
internal sealed class TurnCancellationAutoInvokeFilter : IAutoFunctionInvocationFilter
{
    public Task OnAutoFunctionInvocationAsync(
        AutoFunctionInvocationContext context, Func<AutoFunctionInvocationContext, Task> next)
    {
        if (context.CancellationToken.IsCancellationRequested)
        {
            // End the loop cleanly WITHOUT dispatching this (or any further) tool call. SK checks
            // Terminate after the invocation returns and stops the loop; the runner's unconditional
            // tool-pairing repair answers any tool_call id this leaves unpaired. Not calling next()
            // is what prevents the pending tool from running.
            context.Terminate = true;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
