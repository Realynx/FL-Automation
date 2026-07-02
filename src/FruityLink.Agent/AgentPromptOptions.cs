namespace FruityLink.Agent;

/// <summary>
/// Prompt-shaping options for the agents. Replaces the old mutable
/// <c>SystemPrompts.CavemanModeEnabled</c> static so the setting is injected per agent instance
/// (main agent, in-FL chat tab, sub-agents) instead of being process-global mutable state that
/// tests and hosts could race on. Composition roots that live outside this assembly can omit it —
/// every consumer defaults to <c>new AgentPromptOptions()</c>.
/// </summary>
/// <param name="CavemanModeEnabled">
/// A/B toggle for "caveman speak" output compression. Default ON. When true,
/// <see cref="SystemPrompts.BuildDefault"/> and <see cref="SystemPrompts.BuildSubAgent"/> append
/// <see cref="SystemPrompts.CavemanPrompt"/>, instructing the model to write its REASONING + PROSE
/// replies in terse caveman style to cut output tokens (SaaS margin). Tool calls are unaffected.
/// Flip to false to restore normal prose for token A/B comparison.
/// </param>
public sealed record AgentPromptOptions(bool CavemanModeEnabled = true);
