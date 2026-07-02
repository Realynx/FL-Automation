using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// Builds synthetic <see cref="AutoFunctionInvocationContext"/>s carrying the round/batch indices
/// SK's auto-invoke loop would supply, so <see cref="AutoInvokeIterationFilter"/> can be driven
/// without a real backend. Shared with <c>AgentTurnRunnerTests</c>, whose scripted chat service
/// uses it to flip the kernel's cap filter mid-send exactly like a real capped turn.
/// </summary>
internal static class SyntheticAutoInvoke
{
    /// <summary>A context for round <paramref name="requestSequenceIndex"/>, positioned at function
    /// <paramref name="functionSequenceIndex"/> of a batch of <paramref name="functionCount"/>.</summary>
    internal static AutoFunctionInvocationContext Context(
        Kernel kernel, int requestSequenceIndex, int functionSequenceIndex = 0, int functionCount = 1)
    {
        KernelFunction function = KernelFunctionFactory.CreateFromMethod(() => "noop", "noop");
        var history = new ChatHistory();
        history.AddUserMessage("test");
        return new AutoFunctionInvocationContext(
            kernel, function, new FunctionResult(function), history,
            new ChatMessageContent(AuthorRole.Assistant, string.Empty))
        {
            RequestSequenceIndex = requestSequenceIndex,
            FunctionSequenceIndex = functionSequenceIndex,
            FunctionCount = functionCount,
        };
    }

    /// <summary>Runs the filter over the context with a no-op inner pipeline.</summary>
    internal static Task RunAsync(AutoInvokeIterationFilter filter, AutoFunctionInvocationContext context) =>
        filter.OnAutoFunctionInvocationAsync(context, _ => Task.CompletedTask);
}

/// <summary>
/// <see cref="AutoInvokeIterationFilter"/> must cap a turn at EXACTLY the configured round (an
/// off-by-one either wastes a legitimate round or lets a runaway loop run one longer than
/// promised). On the cap round it must not terminate mid-batch (that would orphan the batch's
/// remaining tool_call ids for no gain) — but an OVERSHOOT round (only reachable when the cap
/// round's batch ended on a validation-failed call the filter never saw) must terminate
/// IMMEDIATELY, even mid-batch: waiting for a clean batch end again could repeat until SK's
/// internal 128-attempt limit, with real FL mutations continuing every round. The runner's
/// unconditional pairing repair answers any tool_call ids a mid-batch Terminate orphans.
/// </summary>
public sealed class AutoInvokeIterationFilterTests
{
    [Fact]
    public async Task RoundBeforeTheCap_DoesNotTerminate()
    {
        var filter = new AutoInvokeIterationFilter(maxRounds: 3);
        var kernel = new Kernel();
        filter.BeginTurn();

        // Rounds are 0-based: with a cap of 3, rounds 0 and 1 must run to completion untouched.
        AutoFunctionInvocationContext context = SyntheticAutoInvoke.Context(kernel, requestSequenceIndex: 1);
        await SyntheticAutoInvoke.RunAsync(filter, context);

        context.Terminate.ShouldBeFalse();
        filter.WasCapped.ShouldBeFalse();
    }

    [Fact]
    public async Task ConfiguredRound_TerminatesAndFlagsCap()
    {
        var filter = new AutoInvokeIterationFilter(maxRounds: 3);
        var kernel = new Kernel();
        filter.BeginTurn();

        // Round index 2 is the 3rd round — the cap fires here, not one round early.
        AutoFunctionInvocationContext context = SyntheticAutoInvoke.Context(kernel, requestSequenceIndex: 2);
        await SyntheticAutoInvoke.RunAsync(filter, context);

        context.Terminate.ShouldBeTrue();
        filter.WasCapped.ShouldBeTrue();
    }

    [Fact]
    public async Task CapRound_ParallelBatch_DoesNotTerminateMidBatch()
    {
        // UPDATED for the round-cap bypass fix: mid-batch termination is now allowed on OVERSHOOT
        // rounds (see the next test) but still not on the cap round itself, where letting the
        // batch finish cleanly costs nothing and keeps every tool_call id paired by SK.
        var filter = new AutoInvokeIterationFilter(maxRounds: 3);
        var kernel = new Kernel();
        filter.BeginTurn();

        // Round index 2 IS the cap round: functions 0 and 1 of a 3-call batch must still complete.
        AutoFunctionInvocationContext first = SyntheticAutoInvoke.Context(kernel, requestSequenceIndex: 2, functionSequenceIndex: 0, functionCount: 3);
        AutoFunctionInvocationContext middle = SyntheticAutoInvoke.Context(kernel, requestSequenceIndex: 2, functionSequenceIndex: 1, functionCount: 3);
        AutoFunctionInvocationContext last = SyntheticAutoInvoke.Context(kernel, requestSequenceIndex: 2, functionSequenceIndex: 2, functionCount: 3);

        await SyntheticAutoInvoke.RunAsync(filter, first);
        first.Terminate.ShouldBeFalse();

        await SyntheticAutoInvoke.RunAsync(filter, middle);
        middle.Terminate.ShouldBeFalse();

        await SyntheticAutoInvoke.RunAsync(filter, last);
        last.Terminate.ShouldBeTrue();
        filter.WasCapped.ShouldBeTrue();
    }

    [Fact]
    public async Task OvershootRound_TerminatesImmediately_EvenMidBatch()
    {
        // An overshoot round is only reachable when the cap round's batch ENDED on a call that
        // failed validation — SK skips this filter for such calls, so the batch-end terminate
        // condition never fired. Waiting for a clean batch end again could repeat to SK's
        // 128-attempt limit while every VALID call keeps mutating FL; terminate on the first
        // invocation instead (the runner's unconditional pairing repair answers orphaned ids).
        var filter = new AutoInvokeIterationFilter(maxRounds: 3);
        var kernel = new Kernel();
        filter.BeginTurn();

        AutoFunctionInvocationContext first = SyntheticAutoInvoke.Context(kernel, requestSequenceIndex: 3, functionSequenceIndex: 0, functionCount: 3);
        await SyntheticAutoInvoke.RunAsync(filter, first);

        first.Terminate.ShouldBeTrue();
        filter.WasCapped.ShouldBeTrue();
    }

    [Fact]
    public async Task CapRound_MidBatch_StillFlagsWasCapped()
    {
        // Even when the cap round's LAST batch entry is invalid (so no Terminate fires that round),
        // the turn must still be reported as capped — the runner uses the flag to repair pairing
        // and surface the resume notice.
        var filter = new AutoInvokeIterationFilter(maxRounds: 3);
        var kernel = new Kernel();
        filter.BeginTurn();

        AutoFunctionInvocationContext midBatch = SyntheticAutoInvoke.Context(kernel, requestSequenceIndex: 2, functionSequenceIndex: 0, functionCount: 3);
        await SyntheticAutoInvoke.RunAsync(filter, midBatch);

        midBatch.Terminate.ShouldBeFalse();
        filter.WasCapped.ShouldBeTrue();
    }

    [Fact]
    public async Task BeginTurn_ResetsTheCapSignal()
    {
        var filter = new AutoInvokeIterationFilter(maxRounds: 1);
        var kernel = new Kernel();
        filter.BeginTurn();
        await SyntheticAutoInvoke.RunAsync(filter, SyntheticAutoInvoke.Context(kernel, requestSequenceIndex: 0));
        filter.WasCapped.ShouldBeTrue();

        // The filter is kernel-lifetime; a previous turn's cap must not leak into the next turn.
        filter.BeginTurn();

        filter.WasCapped.ShouldBeFalse();
    }
}
