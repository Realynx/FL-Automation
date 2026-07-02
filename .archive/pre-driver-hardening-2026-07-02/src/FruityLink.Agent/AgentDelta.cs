namespace FruityLink.Agent;

/// <summary>The kind of streamed piece coming back from the agent.</summary>
public enum AgentDeltaKind
{
    /// <summary>Visible answer text.</summary>
    Text,

    /// <summary>The model's reasoning / "thinking" (shown collapsed in the UI).</summary>
    Thought,
}

/// <summary>One streamed chunk from <see cref="FlAgent.StreamAsync"/>.</summary>
public readonly record struct AgentDelta(AgentDeltaKind Kind, string Text);

/// <summary>A tool (plugin function) the agent invoked during a turn, for display.</summary>
/// <param name="Function">The function name, e.g. <c>get_channels</c>.</param>
/// <param name="Arguments">A short, human-readable argument summary.</param>
public sealed record ToolCallInfo(string Function, string Arguments);
