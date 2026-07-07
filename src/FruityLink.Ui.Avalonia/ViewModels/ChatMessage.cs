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
    /// <summary>Cadence at which the markdown renderer re-parses WHILE STREAMING (~8×/sec). Bounds the
    /// control-tree rebuilds so live markdown never turns into a per-token render storm in the
    /// embedded FL host; the final, complete markdown always renders when the turn settles.</summary>
    private const int RenderThrottleMs = 120;

    private string _text;
    private string _renderText;
    private DateTime _lastRenderAt = DateTime.MinValue;
    private string? _thoughts;
    private bool _isError;
    private bool _thoughtsVisible;
    private bool _toolCallsVisible;
    private bool _isStreaming;

    public ChatMessage(MessageRole role, string text = "")
    {
        Role = role;
        _text = text;
        _renderText = text;
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

    /// <summary>When this entry was added (local time). Shown as a quiet HH:mm on the bubble.</summary>
    public DateTime Timestamp { get; } = DateTime.Now;

    public string TimeLabel => Timestamp.ToString("HH:mm");

    /// <summary>The answer text. An agent can append tokens here for live streaming.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(HasText));
                OnPropertyChanged(nameof(PendingLabel));
                OnPropertyChanged(nameof(ShowPlainText));
                OnPropertyChanged(nameof(ShowRenderedText));
                // Live markdown, throttled: push into RenderText immediately when not streaming, else
                // at most every RenderThrottleMs. The final complete text always renders because the
                // stream-end (IsStreaming -> false) forces a flush.
                if (!_isStreaming || DateTime.UtcNow - _lastRenderAt >= TimeSpan.FromMilliseconds(RenderThrottleMs))
                    FlushRenderText();
            }
        }
    }

    public bool HasText => !string.IsNullOrEmpty(_text);

    /// <summary>
    /// The text the markdown renderer binds to — a throttled mirror of <see cref="Text"/> so the
    /// renderer rebuilds its control tree a few times a second while streaming, not per token.
    /// </summary>
    public string RenderText
    {
        get => _renderText;
        private set => SetProperty(ref _renderText, value);
    }

    /// <summary>Copy the current <see cref="Text"/> into <see cref="RenderText"/> (re-renders markdown).</summary>
    private void FlushRenderText()
    {
        _lastRenderAt = DateTime.UtcNow;
        RenderText = _text;
    }

    /// <summary>
    /// Which renderer shows the answer. The AI is never told to use markdown, but if it does we want
    /// it rendered rather than shown as raw syntax. Normal answers use the markdown renderer (live,
    /// throttled — see <see cref="RenderText"/>); error turns use the plain red <c>SelectableTextBlock</c>
    /// (their text isn't markdown and needs the danger colour).
    /// </summary>
    public bool ShowPlainText => HasText && _isError;

    /// <summary>The non-error answer — rendered as markdown, live while streaming (throttled).</summary>
    public bool ShowRenderedText => HasText && !_isError;

    /// <summary>True from <see cref="ChatViewModel.StartAssistantMessage"/> until the turn settles
    /// (cleared when the host drops <see cref="ChatViewModel.IsBusy"/>). Drives the animated
    /// working indicator for the WHOLE turn — a turn is often still running tool rounds after its
    /// first text has streamed out, and the indicator disappearing early read as "done" (user bug
    /// report). The label distinguishes the phases (see <see cref="PendingLabel"/>).</summary>
    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (SetProperty(ref _isStreaming, value))
            {
                OnPropertyChanged(nameof(PendingVisible));
                OnPropertyChanged(nameof(PendingLabel));
                // Stream ended → force the final, COMPLETE markdown render (throttling may have
                // skipped the last fragment while the tokens were arriving).
                if (!value) FlushRenderText();
            }
        }
    }

    /// <summary>Show the animated indicator for as long as the turn is running.</summary>
    public bool PendingVisible => _isStreaming;

    /// <summary>"Thinking…" before the first text lands; "Working…" while the turn keeps going
    /// (streaming more text, running tool calls) after partial text is already visible.</summary>
    public string PendingLabel => HasText ? "Working…" : "Thinking…";

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
        set
        {
            if (SetProperty(ref _isError, value))
            {
                OnPropertyChanged(nameof(ShowPlainText));
                OnPropertyChanged(nameof(ShowRenderedText));
            }
        }
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
