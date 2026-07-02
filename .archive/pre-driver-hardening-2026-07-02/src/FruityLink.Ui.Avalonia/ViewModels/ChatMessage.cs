using System.Collections.ObjectModel;

namespace FruityLink.Ui.Avalonia.ViewModels;

public enum MessageRole
{
    /// <summary>Something the producer typed.</summary>
    User,

    /// <summary>The agent's reply (may carry streamed thoughts + tool calls).</summary>
    Assistant,

    /// <summary>A quiet system / onboarding line (e.g. the ready banner).</summary>
    Info,
}

/// <summary>
/// One transcript entry. Mirrors the affordances of the WPF chat window
/// (<c>FlAgentChatWindow</c>): an assistant turn can surface a distinct "thoughts" block and a
/// list of tool calls alongside its answer text. All the mutable bits raise change notifications
/// so a future agent can stream tokens straight into <see cref="Text"/> / <see cref="Thoughts"/>.
/// </summary>
public sealed class ChatMessage : ViewModelBase
{
    private string _text;
    private string? _thoughts;
    private bool _isError;
    private bool _thoughtsVisible;
    private bool _toolCallsVisible;

    public ChatMessage(MessageRole role, string text = "")
    {
        Role = role;
        _text = text;
        ToolCalls.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasToolCalls));
            OnPropertyChanged(nameof(ToolCallsBlockVisible));
        };
    }

    public MessageRole Role { get; }

    public bool IsUser => Role == MessageRole.User;
    public bool IsAssistant => Role == MessageRole.Assistant;
    public bool IsInfo => Role == MessageRole.Info;

    /// <summary>The answer text. An agent can append tokens here for live streaming.</summary>
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value);
    }

    /// <summary>Streamed reasoning. Null/empty ⇒ no thoughts block.</summary>
    public string? Thoughts
    {
        get => _thoughts;
        set
        {
            if (SetProperty(ref _thoughts, value))
            {
                OnPropertyChanged(nameof(HasThoughts));
                OnPropertyChanged(nameof(ThoughtsBlockVisible));
            }
        }
    }

    /// <summary>Each entry is a single "→ tool(args)" line, shown as an accent chip.</summary>
    public ObservableCollection<string> ToolCalls { get; } = new();

    public bool IsError
    {
        get => _isError;
        set => SetProperty(ref _isError, value);
    }

    public bool HasThoughts => !string.IsNullOrWhiteSpace(_thoughts);
    public bool HasToolCalls => ToolCalls.Count > 0;

    /// <summary>Debug-toggle state, pushed down from the ChatViewModel. Combined with content
    /// presence so an empty block never shows a bare header.</summary>
    public bool ThoughtsVisible
    {
        get => _thoughtsVisible;
        set
        {
            if (SetProperty(ref _thoughtsVisible, value))
                OnPropertyChanged(nameof(ThoughtsBlockVisible));
        }
    }

    public bool ToolCallsVisible
    {
        get => _toolCallsVisible;
        set
        {
            if (SetProperty(ref _toolCallsVisible, value))
                OnPropertyChanged(nameof(ToolCallsBlockVisible));
        }
    }

    public bool ThoughtsBlockVisible => HasThoughts && _thoughtsVisible;
    public bool ToolCallsBlockVisible => HasToolCalls && _toolCallsVisible;
}
