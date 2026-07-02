using Microsoft.SemanticKernel;
using Shouldly;
using Xunit;

namespace FruityLink.Agent.Tests;

/// <summary>
/// <see cref="ToolCallFilter"/> is the safety net between tools and the auto-invoke loop: a
/// throwing tool must become an "ERR: …" string the model can react to (not a turn-aborting
/// exception), oversized results must be truncated before they flood a small model's context, and
/// UI observation must never affect tool execution. Exercised through a real kernel invocation so
/// the filter runs on SK's actual pipeline rather than a hand-built context.
/// </summary>
public sealed class ToolCallFilterTests
{
    private readonly ToolCallFilter _filter = new();
    private readonly Kernel _kernel;

    public ToolCallFilterTests()
    {
        _kernel = new Kernel();
        _kernel.FunctionInvocationFilters.Add(_filter);
    }

    [Fact]
    public async Task ThrowingTool_YieldsErrResultInsteadOfPropagating()
    {
        KernelFunction boom = KernelFunctionFactory.CreateFromMethod(
            (Func<string>)(() => throw new InvalidOperationException("kaboom happened\nnative stack detail")),
            "boom");

        FunctionResult result = await _kernel.InvokeAsync(boom);

        string value = result.GetValue<string>()!;
        // First line only — multi-line native/stack detail is token noise — plus the anti-retry nudge.
        value.ShouldStartWith("ERR: boom: kaboom happened.");
        value.ShouldContain("DIFFERENT call");
        value.ShouldNotContain("native stack detail");
    }

    [Fact]
    public async Task ThrowingTool_StillRaisesInvokedForTheUi()
    {
        var observed = new List<ToolCallInfo>();
        _filter.Invoked += observed.Add;
        KernelFunction boom = KernelFunctionFactory.CreateFromMethod(
            (Func<string>)(() => throw new InvalidOperationException("kaboom")), "boom");

        await _kernel.InvokeAsync(boom);

        // A failing tool call that never shows in the UI is undiagnosable flakiness.
        observed.ShouldHaveSingleItem().Function.ShouldBe("boom");
    }

    [Fact]
    public async Task SubscriberException_IsSwallowedAndResultSurvives()
    {
        _filter.Invoked += _ => throw new InvalidOperationException("UI handler crashed");
        KernelFunction fn = KernelFunctionFactory.CreateFromMethod(() => "fine", "fine_tool");

        FunctionResult result = await _kernel.InvokeAsync(fn);

        result.GetValue<string>().ShouldBe("fine");
    }

    [Fact]
    public async Task ArgumentSummary_TruncatesLongValues()
    {
        ToolCallInfo? observed = null;
        _filter.Invoked += info => observed = info;
        KernelFunction fn = KernelFunctionFactory.CreateFromMethod((string text) => "ok", "echo");
        string longValue = new('a', 100);

        await _kernel.InvokeAsync(fn, new KernelArguments { ["text"] = longValue });

        observed.ShouldNotBeNull();
        observed!.Arguments.ShouldBe($"text={new string('a', 64)}…");
    }

    [Fact]
    public async Task OversizedStringResult_IsTruncatedWithGuidanceMarker()
    {
        const int maxResultChars = 6 * 1024;
        KernelFunction fn = KernelFunctionFactory.CreateFromMethod(
            () => new string('x', maxResultChars + 1000), "big_list");

        FunctionResult result = await _kernel.InvokeAsync(fn);

        string value = result.GetValue<string>()!;
        value.ShouldEndWith("… [truncated — use a filter or narrower query]");
        // Exactly the cap survives; the marker tells the model HOW to get the rest.
        value[..maxResultChars].ShouldBe(new string('x', maxResultChars));
        value.Length.ShouldBe(maxResultChars + "… [truncated — use a filter or narrower query]".Length);
    }

    [Fact]
    public async Task ResultAtTheCap_IsNotTruncated()
    {
        const int maxResultChars = 6 * 1024;
        KernelFunction fn = KernelFunctionFactory.CreateFromMethod(
            () => new string('x', maxResultChars), "exact_fit");

        FunctionResult result = await _kernel.InvokeAsync(fn);

        result.GetValue<string>()!.Length.ShouldBe(maxResultChars);
    }
}
