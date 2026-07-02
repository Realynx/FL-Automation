using System.Net;
using System.Net.Http;
using FruityLink.Llm.Diagnostics;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

/// <summary>
/// The per-turn request budget is the backstop for the ONE runaway shape the round-cap filter can
/// never see: SK skips auto-function-invocation filters for tool calls that fail validation
/// ("wasn't defined"), so a model that repeats an unresolvable tool name every round performs a
/// full model round-trip per round with the cap filter never firing — unbounded, the loop only
/// stops at SK's internal 128-attempt limit. These tests pin the budget mechanics (scoping,
/// nesting, no-scope passthrough) and drive SK's REAL auto-invoke loop end-to-end to prove the
/// budget — not the 128-attempt limit — is what ends an all-invalid-rounds turn.
/// </summary>
public sealed class LlmTurnBudgetTests
{
    private const string RequestUri = "http://localhost:11434/v1/chat/completions";

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client) =>
        await client.PostAsync(RequestUri, new StringContent("{}"));

    // ---- scope mechanics --------------------------------------------------------------------

    [Fact]
    public async Task NoActiveScope_NeverLimits()
    {
        // Non-turn traffic (connectivity probes, direct chat calls) must be unaffected.
        var fake = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(new LlmTurnBudgetHandler(fake));

        for (int i = 0; i < 20; i++)
            (await SendAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);

        fake.RequestCount.ShouldBe(20);
    }

    [Fact]
    public async Task ActiveScope_AllowsExactlyTheBudget_ThenThrowsLoudly()
    {
        var fake = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(new LlmTurnBudgetHandler(fake));

        using (LlmTurnBudget.Begin(maxRequests: 3))
        {
            for (int i = 0; i < 3; i++)
                (await SendAsync(client)).StatusCode.ShouldBe(HttpStatusCode.OK);

            await Should.ThrowAsync<LlmTurnBudgetExceededException>(() => SendAsync(client));
        }

        // The blocked request never reached the network.
        fake.RequestCount.ShouldBe(3);
    }

    [Fact]
    public async Task DisposingTheScope_RestoresTheOuterScope()
    {
        // A sub-agent turn opens its own scope INSIDE the parent turn's tool invocation; the
        // inner scope must shadow (not consume) the outer budget and hand it back on dispose.
        var fake = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(new LlmTurnBudgetHandler(fake));

        using (LlmTurnBudget.Begin(maxRequests: 2))
        {
            await SendAsync(client); // outer: 1 of 2

            using (LlmTurnBudget.Begin(maxRequests: 3))
            {
                for (int i = 0; i < 3; i++) await SendAsync(client); // inner: 3 of 3
                await Should.ThrowAsync<LlmTurnBudgetExceededException>(() => SendAsync(client));
            }

            await SendAsync(client); // outer again: 2 of 2 — the inner turn did not consume it
            await Should.ThrowAsync<LlmTurnBudgetExceededException>(() => SendAsync(client));
        }

        // Scope fully closed — unlimited again.
        await SendAsync(client);
        fake.RequestCount.ShouldBe(6);
    }

    // ---- end-to-end: SK's real auto-invoke loop, every round failing validation --------------

    /// <summary>A complete chat response whose only tool call names a function that is NOT
    /// advertised — SK's FunctionCallsProcessor fails validation, appends an error tool result,
    /// and loops WITHOUT ever creating an AutoFunctionInvocationContext (the cap filter is blind
    /// to the whole round). Served for every request = the runaway shape.</summary>
    private const string ToolCallToUnregisteredFunction = """
        {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"m",
         "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
           "tool_calls":[{"id":"call_1","type":"function","function":{"name":"nope_tool","arguments":"{}"}}]}}],
         "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
        """;

    [Fact]
    public async Task SkAutoInvokeLoop_AllRoundsFailValidation_EndsAtTheBudget_Not128Attempts()
    {
        var fake = FakeHttpMessageHandler.Json(HttpStatusCode.OK, ToolCallToUnregisteredFunction);
        using var client = new HttpClient(new LlmTurnBudgetHandler(fake));

        Kernel kernel = Kernel.CreateBuilder()
            .AddOpenAIChatCompletion("m", new Uri("http://localhost:11434/v1"), "key", httpClient: client)
            .Build();
        kernel.Plugins.AddFromFunctions("Tools",
            new[] { KernelFunctionFactory.CreateFromMethod(() => "ok", "real_tool") });
        IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
        };
        var history = new ChatHistory();
        history.AddUserMessage("go");

        const int budget = 5;
        Exception? thrown;
        using (LlmTurnBudget.Begin(budget))
        {
            thrown = await Record.ExceptionAsync(
                () => chat.GetChatMessageContentAsync(history, settings, kernel));
        }

        // The turn must fail (not silently return) and the ROOT cause must be the budget — the
        // OpenAI SDK / SK may wrap the handler's exception on the way out.
        thrown.ShouldNotBeNull();
        ContainsBudgetException(thrown).ShouldBeTrue(
            $"expected LlmTurnBudgetExceededException in the chain, got: {thrown}");

        // The wire saw EXACTLY the budget — near the round cap, nowhere near SK's 128 attempts.
        fake.RequestCount.ShouldBe(budget);
    }

    /// <summary>Walks the exception chain (including AggregateException branches) looking for the
    /// budget exception.</summary>
    private static bool ContainsBudgetException(Exception? ex) => ex switch
    {
        null => false,
        LlmTurnBudgetExceededException => true,
        AggregateException agg => agg.InnerExceptions.Any(ContainsBudgetException),
        _ => ContainsBudgetException(ex.InnerException),
    };
}
