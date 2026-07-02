using System.Runtime.CompilerServices;
using FruityLink.Core.Abstractions;
using FruityLink.Core.Domain;
using FruityLink.Llm;
using Microsoft.SemanticKernel.ChatCompletion;

namespace FruityLink.Agent;

/// <summary>
/// The conversational agent. A thin adapter over <see cref="AgentKernelBuilder"/> (build the kernel
/// against the FL Automate gateway + register the FL tool plugins) and <see cref="AgentTurnRunner"/>
/// (run one tool-using turn), mapping each turn's result onto streamed <see cref="AgentDelta"/>s.
/// Maintains conversation history so it can be snapshotted/restored by the version tree.
/// </summary>
public sealed class FlAgent(
    IChatKernelFactory kernelFactory,
    ISettingsStore settingsStore,
    FlPluginSet plugins,
    ToolCallFilter toolFilter,
    AgentPromptOptions? promptOptions = null)
{
    private readonly ChatHistory _history = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AgentKernelBuilder _builder = new(kernelFactory, settingsStore);
    private readonly AgentTurnRunner _runner = new();
    // Optional with a default so composition roots outside this assembly (which construct FlAgent
    // positionally) keep compiling; hosts that want to A/B caveman mode inject their own.
    private readonly AgentPromptOptions _promptOptions = promptOptions ?? new();
    private AgentKernel? _agentKernel;

    /// <summary>True once a backend has been configured.</summary>
    public bool IsConfigured => _agentKernel is not null;

    /// <summary>Late-binds the project version store into the Versioning tools (list_versions /
    /// get_version_changes) so the model can review its own past changes. Late-bound because the
    /// store is composed after the agent (it needs the live bridge); safe no-op when the plugin set
    /// carries no Versioning plugin. Works on hosted and self-composed agents alike.</summary>
    public void AttachVersionControl(IProjectVersionControl versionControl) =>
        plugins.AttachVersionControl(versionControl);

    /// <summary>Raised after each tool the agent invokes, for live UI display.</summary>
    public event Action<ToolCallInfo>? ToolInvoked
    {
        add => toolFilter.Invoked += value;
        remove => toolFilter.Invoked -= value;
    }

    /// <summary>
    /// Builds (or rebuilds) the kernel from current settings and registers all tool plugins.
    /// Call again after the user changes the backend.
    /// </summary>
    public async Task ConfigureAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await ConfigureCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task ConfigureCoreAsync(CancellationToken ct)
    {
        _agentKernel = await _builder.BuildAsync(plugins.All, toolFilter, ct: ct).ConfigureAwait(false);

        if (_history.Count == 0)
            _history.AddSystemMessage(SystemPrompts.BuildDefault(_promptOptions));
    }

    /// <summary>
    /// Sends the user's message and streams the assistant's reply as <see cref="AgentDelta"/>s
    /// (visible text and, where the model exposes it, reasoning "thoughts"). Tools are invoked
    /// automatically and surfaced via <see cref="ToolInvoked"/>.
    /// </summary>
    public async IAsyncEnumerable<AgentDelta> StreamAsync(
        string userInput, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_agentKernel is null)
                await ConfigureCoreAsync(ct).ConfigureAwait(false);

            AgentKernel agentKernel = _agentKernel!;
            TurnResult result = await _runner
                .RunTurnAsync(agentKernel.Kernel, agentKernel.Chat, _history, userInput, agentKernel.Settings, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.Thought))
                yield return new AgentDelta(AgentDeltaKind.Thought, result.Thought);

            if (!string.IsNullOrEmpty(result.Text))
                yield return new AgentDelta(AgentDeltaKind.Text, result.Text);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Resets the conversation, optionally seeding it from a restored session (used when
    /// switching to a version-tree branch). A seed that carries its own System message REPLACES the
    /// built-in prompt — stacking both would double the prompt tokens and let two personas fight —
    /// otherwise the default system prompt is re-added first.
    /// </summary>
    public void ResetConversation(IEnumerable<ChatMessage>? seed = null)
    {
        _history.Clear();

        List<ChatMessage>? messages = seed?.ToList();
        bool seedHasSystem = messages?.Any(m => m.Role == ChatRole.System) == true;
        if (!seedHasSystem)
            _history.AddSystemMessage(SystemPrompts.BuildDefault(_promptOptions));
        if (messages is null) return;

        foreach (ChatMessage message in messages)
        {
            switch (message.Role)
            {
                case ChatRole.User:
                    _history.AddUserMessage(message.Content);
                    break;
                case ChatRole.Assistant:
                    _history.AddAssistantMessage(message.Content);
                    break;
                case ChatRole.System:
                    _history.AddSystemMessage(message.Content);
                    break;
                // Tool messages are replayed implicitly via assistant content; skip.
            }
        }
    }
}
