using System.Threading;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// <see cref="TurnCancellationAutoInvokeFilter"/> is the ONE reliable way to stop SK's auto-invoke
/// tool loop when the user hits Stop: SK's function-calls processor swallows any exception a tool or
/// filter throws into an "Error: …" result and keeps looping, so the only thing that actually ends
/// the loop from inside it is <see cref="AutoFunctionInvocationContext.Terminate"/>. When the turn's
/// token is cancelled the filter must set Terminate AND NOT dispatch the pending tool (never call
/// <c>next</c>); when it is not cancelled it must be a transparent pass-through.
/// </summary>
public sealed class TurnCancellationAutoInvokeFilterTests
{
    private static AutoFunctionInvocationContext Context(CancellationToken ct)
    {
        var kernel = new Kernel();
        KernelFunction function = KernelFunctionFactory.CreateFromMethod(() => "noop", "noop");
        var history = new ChatHistory();
        history.AddUserMessage("test");
        return new AutoFunctionInvocationContext(
            kernel, function, new FunctionResult(function), history,
            new ChatMessageContent(AuthorRole.Assistant, string.Empty))
        {
            CancellationToken = ct,
        };
    }

    [Fact]
    public async Task Cancelled_TerminatesTheLoopWithoutDispatchingTheTool()
    {
        var filter = new TurnCancellationAutoInvokeFilter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        AutoFunctionInvocationContext context = Context(cts.Token);

        var dispatched = false;
        await filter.OnAutoFunctionInvocationAsync(context, _ => { dispatched = true; return Task.CompletedTask; });

        // The pending tool must NOT run (no new native FL op after Stop) and the loop must end.
        dispatched.ShouldBeFalse();
        context.Terminate.ShouldBeTrue();
    }

    [Fact]
    public async Task NotCancelled_DispatchesTheToolAndDoesNotTerminate()
    {
        var filter = new TurnCancellationAutoInvokeFilter();
        AutoFunctionInvocationContext context = Context(CancellationToken.None);

        var dispatched = false;
        await filter.OnAutoFunctionInvocationAsync(context, _ => { dispatched = true; return Task.CompletedTask; });

        // A normal turn is untouched: the tool runs and the loop keeps going.
        dispatched.ShouldBeTrue();
        context.Terminate.ShouldBeFalse();
    }
}
