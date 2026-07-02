using FruityLink.Agent.Plugins;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Configuration;
using FruityLink.Llm;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace FruityLink.Agent;

/// <summary>
/// Runs a single, self-contained task on a fresh sub-agent: its own kernel + conversation, the FL
/// control / music-theory / knowledge tools (but NOT the orchestration tool — sub-agents never spawn
/// further sub-agents), and a task-focused prompt. The orchestrator fans many of these out in
/// parallel so multi-part jobs (e.g. one pattern per agent) complete faster.
/// </summary>
public sealed class SubAgentService(
    IChatKernelFactory kernelFactory,
    ISettingsStore settingsStore,
    ISecretStore secretStore,
    MusicTheoryPlugin musicTheory,
    NativeControlPlugin nativeControl,
    KnowledgePlugin knowledge,
    ToolCallFilter toolFilter)
{
    /// <summary>Runs one task to completion (auto-invoking tools) and returns the sub-agent's summary.</summary>
    public async Task<string> RunTaskAsync(string task, CancellationToken ct = default)
    {
        AppSettings app = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        LlmSettings llm = app.LlmOrDefault;
        string? apiKey = llm.ApiKeyRef is null ? null : await secretStore.GetAsync(llm.ApiKeyRef, ct).ConfigureAwait(false);

        Kernel kernel = kernelFactory.CreateKernel(llm, apiKey);
        kernel.FunctionInvocationFilters.Add(toolFilter);   // surface sub-agent tool calls in the UI too
        kernel.AutoFunctionInvocationFilters.Add(new AutoInvokeIterationFilter(maxRounds: 12));
        kernel.Plugins.AddFromObject(musicTheory, "MusicTheory");
        kernel.Plugins.AddFromObject(nativeControl, "NativeControl");
        kernel.Plugins.AddFromObject(knowledge, "Knowledge");

        IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(SystemPrompts.SubAgent);
        history.AddUserMessage(task);

        // Allow several tool calls per turn (fewer LLM round-trips); SK still auto-invokes them
        // sequentially, so the single-client FL bridge is never hit concurrently.
        var settings = new OpenAIPromptExecutionSettings
        {
            // Safety ceiling, not a tuning knob: bounds a runaway turn. Generous so it never
            // truncates a normal task turn (incl. large tool-calls or model reasoning).
            MaxTokens = 8192,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                options: new FunctionChoiceBehaviorOptions { AllowParallelCalls = true }),
            // Match the main agent: brief plan-before-tools reasoning over the OpenAI-compatible endpoint.
            ExtensionData = new Dictionary<string, object> { ["reasoning_effort"] = "medium" },
        };
        ChatMessageContent reply = await chat
            .GetChatMessageContentAsync(history, settings, kernel, ct)
            .ConfigureAwait(false);

        // A sub-agent that ends on a tool call with empty content (common with weak models) would
        // otherwise return "" — making the orchestrator's report an unreadable blank. Give a clear
        // fallback, and strip any folded <think> reasoning so it never leaks into the report.
        string summary = FlAgent.SplitThink(reply.Content ?? string.Empty).Text;
        return string.IsNullOrWhiteSpace(summary) ? "(task completed; no summary produced)" : summary;
    }
}
