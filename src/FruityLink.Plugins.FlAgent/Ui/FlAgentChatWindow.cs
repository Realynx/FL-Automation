using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FruityLink.Agent;
using FruityLink.Core.Abstractions;

namespace FruityLink.Plugins.FlAgent.Ui;

/// <summary>
/// Small self-contained chat window for the FL Agent plugin. It is pure presentation glue: every
/// message is streamed straight from the reused <see cref="FruityLink.Agent.FlAgent"/>
/// (<see cref="FruityLink.Agent.FlAgent.StreamAsync"/>) and each tool the agent invokes is surfaced
/// via <see cref="FruityLink.Agent.FlAgent.ToolInvoked"/>. No LLM logic lives here.
///
/// Built in code (no XAML) so the plugin DLL has no markup-compile/theme-resource coupling to the
/// host app — it draws its own dark palette. The parked in-FL native chat tab is intentionally NOT
/// used (its input can't capture space); this real OS window has proper keyboard focus.
///
/// Three interactive affordances beyond plain chat:
///  • Whisper speech-to-text (the mic button) — live partial transcript streamed into the input box
///    while recording, with one authoritative final pass on stop / send. Mirrors the prior app.
///  • Cancel-in-progress — the Send button becomes Cancel while a turn streams; it cancels the
///    in-flight <see cref="FruityLink.Agent.FlAgent.StreamAsync"/> via a CancellationTokenSource.
///  • Thoughts/debug view — the model's streamed reasoning is shown in a distinct dim/italic block,
///    toggleable via the "Thoughts" checkbox, so the user can verify thoughts flow under the
///    low-context/caveman protocol. Tool-call display is kept alongside it.
/// </summary>
internal sealed partial class FlAgentChatWindow : Window
{
    private static readonly Brush WindowBg = Frozen(0x1E, 0x1E, 0x22);
    /// <summary>Opaque backdrop for the software HwndTarget (matches <see cref="WindowBg"/>) so empty child
    /// areas don't render transparent inside the foreign FL parent (which would let FL content bleed through).</summary>
    private static readonly Color OpaqueBackdrop = Color.FromRgb(0x1E, 0x1E, 0x22);
    private static readonly Brush ComposerBg = Frozen(0x26, 0x26, 0x2B);
    private static readonly Brush UserBubble = Frozen(0x2F, 0x5B, 0x8A);
    private static readonly Brush AsstBubble = Frozen(0x2C, 0x2C, 0x32);
    private static readonly Brush TextFg = Frozen(0xEC, 0xEC, 0xF1);
    private static readonly Brush MutedFg = Frozen(0x9A, 0x9A, 0xA6);
    private static readonly Brush AccentBg = Frozen(0x4C, 0x8B, 0xF5);
    private static readonly Brush CancelBg = Frozen(0xB5, 0x4A, 0x4A);
    private static readonly Brush MicBg = Frozen(0x3A, 0x3A, 0x42);
    private static readonly Brush MicActiveBg = Frozen(0xB5, 0x4A, 0x4A);
    private static readonly Brush ThoughtFg = Frozen(0x8F, 0xB8, 0xC9);
    private static readonly Brush BorderFg = Frozen(0x3A, 0x3A, 0x42);
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, monospace");

    private readonly FruityLink.Agent.FlAgent _agent;
    private readonly IDictationService? _dictation;
    private readonly bool _ownsDictation;
    private readonly Action<string> _log;

    private readonly StackPanel _transcript;
    private readonly ScrollViewer _scroller;
    private readonly TextBox _input;
    private readonly Button _send;
    private readonly Button _mic;
    private readonly CheckBox _thoughtsToggle;
    private readonly CheckBox _toolCallsToggle;
    private readonly TextBlock _status;
    private readonly TextBlock _dictationStatus;

    private TextBlock? _currentAnswer;
    private TextBlock? _currentThoughts;
    private FrameworkElement? _currentThoughtsBlock;
    private TextBlock? _currentTools;
    private readonly StringBuilder _toolsBuf = new();
    private readonly StringBuilder _thoughtsBuf = new();

    /// <summary>All thoughts blocks ever created, so the "Thoughts" toggle can show/hide the lot.</summary>
    private readonly List<FrameworkElement> _thoughtBlocks = new();

    /// <summary>All tool-call blocks ever created, so the "Tool calls" toggle can show/hide the lot.</summary>
    private readonly List<TextBlock> _toolBlocks = new();

    private bool _busy;
    private bool _sending;
    private CancellationTokenSource? _turnCts;

    private readonly DictationController? _dictationCtl;
    private string? _lastDictationStatus;

    public FlAgentChatWindow(
        FruityLink.Agent.FlAgent agent,
        Action<string> log,
        IDictationService? dictation = null,
        bool ownsDictation = false)
    {
        _agent = agent;
        _log = log;
        _dictation = dictation;
        _ownsDictation = ownsDictation;

        Title = "FL Automate";
        Width = 470;
        Height = 660;
        Background = WindowBg;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _transcript = new StackPanel { Margin = new Thickness(12, 12, 12, 12) };
        _scroller = new ScrollViewer
        {
            Content = _transcript,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(_scroller, 0);
        root.Children.Add(_scroller);

        Border statusBar = BuildStatusBar(out _thoughtsToggle, out _toolCallsToggle, out _status, out _dictationStatus);
        Grid.SetRow(statusBar, 1);
        root.Children.Add(statusBar);

        Border composer = BuildComposer(out _input, out _send, out _mic);
        Grid.SetRow(composer, 2);
        root.Children.Add(composer);

        Content = root;

        AddInfoBubble("FL Automate ready. Ask me to compose, edit, or control your FL Studio project — "
                    + "I drive FL natively. Enter sends; Shift+Enter for a new line."
                    + (_dictation is not null ? " Tap the mic to dictate." : string.Empty));

        _agent.ToolInvoked += OnToolInvoked;
        if (_dictation is not null)
        {
            _dictationCtl = new DictationController(
                _dictation,
                isBusy: () => _busy,
                setStatus: s => MarshalToUi(() => SetDictationStatus(s)),
                // Applied synchronously on the UI thread (the finalize continuation resumes there), so
                // SendAsync sees the full utterance in the box as soon as its awaited finalize returns.
                applyFinalText: t => { _input.Text = t; return Task.CompletedTask; },
                applyPartialText: t => _input.Text = t,
                readComposer: () => _input.Text,
                marshalToUi: MarshalToUi);
            _dictation.StateChanged += OnDictationStateChanged;
            _dictation.PartialTranscribed += OnPartialTranscribed;
        }
        Closed += OnClosed;

        UpdateMicEnabled();
    }

    // The window-host embed / pinning machinery (PrepareForEmbedding, PinToHostContent, ForceRerender,
    // the airspace-bug workarounds and their P/Invokes) lives in FlAgentChatWindow.Embed.cs.

    /// <summary>The thin row above the composer: the debug toggles plus transient status text.</summary>
    private Border BuildStatusBar(out CheckBox thoughtsToggle, out CheckBox toolCallsToggle, out TextBlock status, out TextBlock dictationStatus)
    {
        var bar = new Grid { Margin = new Thickness(14, 4, 14, 4) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Both debug toggles sit together on the left.
        var togglePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        thoughtsToggle = new CheckBox
        {
            Content = "Thoughts",
            IsChecked = true,
            Foreground = MutedFg,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Show the model's streamed reasoning (debug view).",
        };
        thoughtsToggle.Checked += (_, _) => ApplyThoughtsVisibility();
        thoughtsToggle.Unchecked += (_, _) => ApplyThoughtsVisibility();
        togglePanel.Children.Add(thoughtsToggle);

        // Tool calls are shown by default (user preference); untick this toggle to hide them when noisy.
        toolCallsToggle = new CheckBox
        {
            Content = "Tool calls",
            IsChecked = true,
            Foreground = MutedFg,
            FontSize = 12,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Show each tool the agent invokes (debug view).",
        };
        toolCallsToggle.Checked += (_, _) => ApplyToolCallsVisibility();
        toolCallsToggle.Unchecked += (_, _) => ApplyToolCallsVisibility();
        togglePanel.Children.Add(toolCallsToggle);

        Grid.SetColumn(togglePanel, 0);
        bar.Children.Add(togglePanel);

        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dictationStatus = new TextBlock
        {
            Foreground = ThoughtFg,
            FontSize = 12,
            Margin = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed,
        };
        status = new TextBlock
        {
            Foreground = MutedFg,
            FontSize = 12,
            Visibility = Visibility.Collapsed,
        };
        statusPanel.Children.Add(dictationStatus);
        statusPanel.Children.Add(status);
        Grid.SetColumn(statusPanel, 1);
        bar.Children.Add(statusPanel);

        return new Border { Child = bar };
    }

    /// <summary>Builds the composer (input box + mic + Send) and returns its container row.</summary>
    private Border BuildComposer(out TextBox input, out Button send, out Button mic)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        input = new TextBox
        {
            MinHeight = 40,
            MaxHeight = 160,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = WindowBg,
            Foreground = TextFg,
            CaretBrush = TextFg,
            BorderBrush = BorderFg,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
        };
        input.PreviewKeyDown += OnInputKeyDown;
        Grid.SetColumn(input, 0);
        grid.Children.Add(input);

        mic = new Button
        {
            Content = "Mic",
            MinWidth = 54,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            Background = MicBg,
            Foreground = TextFg,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            ToolTip = "Dictate (speech-to-text).",
            Visibility = _dictation is null ? Visibility.Collapsed : Visibility.Visible,
        };
        mic.Click += async (_, _) => await ToggleMicAsync();
        Grid.SetColumn(mic, 1);
        grid.Children.Add(mic);

        send = new Button
        {
            Content = "Send",
            MinWidth = 78,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(10, 6, 10, 6),
            Background = AccentBg,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
        send.Click += async (_, _) => await OnSendOrCancelAsync();
        Grid.SetColumn(send, 2);
        grid.Children.Add(send);

        return new Border
        {
            Background = ComposerBg,
            BorderBrush = BorderFg,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 10, 12, 12),
            Child = grid,
        };
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = OnSendOrCancelAsync();
        }
    }

    // ---- send / cancel ----

    /// <summary>The Send button doubles as Cancel while a turn streams.</summary>
    private async Task OnSendOrCancelAsync()
    {
        if (_busy) { CancelTurn(); return; }
        await SendAsync();
    }

    /// <summary>Idempotent + race-safe: cancels the in-flight turn; late deltas are ignored.</summary>
    private void CancelTurn()
    {
        try { _turnCts?.Cancel(); }
        catch { /* already disposed / cancelled */ }
    }

    private async Task SendAsync()
    {
        // _sending guards re-entrancy across the (awaited) final-transcription step, so a double
        // click or Enter-spam can't kick off two turns. _busy guards once a turn is actually running.
        if (_busy || _sending) return;
        _sending = true;
        try
        {
            // If the mic is live, take one authoritative final pass so we send the full utterance
            // (not just the last live partial that happens to be in the box).
            if (_dictationCtl is not null && _dictation is not null && _dictation.State == DictationState.Recording)
                await _dictationCtl.FinalizeAsync();

            string text = _input.Text.Trim();
            if (text.Length == 0) return;

            _input.Text = string.Empty;
            SetBusy(true);
            AddUserBubble(text);
            StartAssistantBubble();

            _turnCts = new CancellationTokenSource();
            CancellationToken ct = _turnCts.Token;
            try
            {
                await foreach (AgentDelta delta in _agent.StreamAsync(text, ct))
                {
                    if (delta.Kind == AgentDeltaKind.Thought)
                        AppendThought(delta.Text);
                    else
                        AppendAnswer(delta.Text);
                }

                if (_currentAnswer is not null && _currentAnswer.Text.Length == 0)
                    _currentAnswer.Text = _toolsBuf.Length > 0 || _thoughtsBuf.Length > 0
                        ? ChatTurnText.DoneFallback
                        : ChatTurnText.NoTextFallback;
            }
            catch (OperationCanceledException)
            {
                NoteCancelled();
                _log("[fl-agent] turn cancelled by user.");
            }
            catch (Exception ex)
            {
                string detail = ChatTurnText.BuildDetail(ex);
                if (_currentAnswer is not null)
                {
                    // Mirror AvaloniaChatPresenter: when the stream died AFTER partial answer text
                    // arrived, keep it and append the marker instead of clobbering it with the error.
                    if (_currentAnswer.Text.Length > 0)
                    {
                        _currentAnswer.Text += ChatTurnText.PartialKeptNotice;
                    }
                    else
                    {
                        _currentAnswer.Text = "⚠ " + detail;
                        _currentAnswer.Foreground = Frozen(0xF2, 0x8B, 0x82);
                    }
                }
                _log("[fl-agent] turn error: " + ex.Message);
            }
            finally
            {
                CancellationTokenSource? cts = _turnCts;
                _turnCts = null;
                cts?.Dispose();
                SetBusy(false);
                ScrollToBottom();
            }
        }
        finally
        {
            _sending = false;
        }
    }

    private void NoteCancelled()
    {
        if (_currentAnswer is null) return;
        if (_currentAnswer.Text.Length == 0)
        {
            _currentAnswer.Text = ChatTurnText.Cancelled;
            _currentAnswer.Foreground = MutedFg;
        }
        else
        {
            _currentAnswer.Text += "\n\n" + ChatTurnText.Cancelled;
        }
    }

    // ---- speech-to-text (Whisper) — the flow itself lives in the shared DictationController ----

    /// <summary>Mic button: start recording, or stop + take the final transcription into the box.</summary>
    private async Task ToggleMicAsync()
    {
        if (_dictationCtl is null) return;
        await _dictationCtl.ToggleAsync();
    }

    private void OnPartialTranscribed(string text) => _dictationCtl?.HandlePartialTranscribed(text);

    private void OnDictationStateChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            DictationState state = _dictation?.State ?? DictationState.Idle;
            bool recording = state == DictationState.Recording;
            _mic.Content = recording ? "Stop" : "Mic";
            _mic.Background = recording ? MicActiveBg : MicBg;
            _mic.Foreground = recording ? Brushes.White : TextFg;
            UpdateMicEnabled();
            SetDictationStatus(state switch
            {
                DictationState.Recording => "Listening… tap Stop to finish",
                DictationState.Transcribing => "Transcribing…",
                DictationState.Downloading => _lastDictationStatus, // keep the % set by progress
                _ => null,
            });
        }
        MarshalToUi(Apply);
    }

    // ---- transcript helpers (all run on the UI thread; deltas resume here) ----

    private void AddUserBubble(string text)
    {
        var tb = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = TextFg };
        _transcript.Children.Add(Bubble(tb, user: true));
        ScrollToBottom();
    }

    private void AddInfoBubble(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedFg,
            FontStyle = FontStyles.Italic,
        };
        _transcript.Children.Add(Bubble(tb, user: false));
    }

    private void StartAssistantBubble()
    {
        var content = new StackPanel();

        _currentTools = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = MutedFg,
            FontFamily = Mono,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            Visibility = Visibility.Collapsed,
        };
        _toolBlocks.Add(_currentTools);

        // Thoughts get their own dim/italic block with a header, visually distinct from the answer
        // and the tool calls — a debug surface to confirm reasoning is flowing.
        var thoughtsPanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        var thoughtsHeader = new TextBlock
        {
            Text = "thinking…",
            Foreground = ThoughtFg,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        };
        _currentThoughts = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThoughtFg,
            FontStyle = FontStyles.Italic,
            FontSize = 12,
        };
        thoughtsPanel.Children.Add(thoughtsHeader);
        thoughtsPanel.Children.Add(_currentThoughts);
        _currentThoughtsBlock = thoughtsPanel;
        _thoughtBlocks.Add(thoughtsPanel);

        _currentAnswer = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = TextFg };

        content.Children.Add(_currentTools);
        content.Children.Add(thoughtsPanel);
        content.Children.Add(_currentAnswer);

        _toolsBuf.Clear();
        _thoughtsBuf.Clear();
        _transcript.Children.Add(BubbleContent(content, user: false));
        ScrollToBottom();
    }

    private void AppendAnswer(string text)
    {
        if (_currentAnswer is null) return;
        _currentAnswer.Text += text;
        ScrollToBottom();
    }

    private void AppendThought(string text)
    {
        if (_currentThoughts is null) return;
        _thoughtsBuf.Append(text);
        _currentThoughts.Text = _thoughtsBuf.ToString();
        if (_currentThoughtsBlock is not null)
            _currentThoughtsBlock.Visibility = ThoughtsVisible ? Visibility.Visible : Visibility.Collapsed;
        ScrollToBottom();
    }

    private bool ThoughtsVisible => _thoughtsToggle.IsChecked == true;

    /// <summary>Show/hide every thoughts block (those that have content) when the toggle flips.</summary>
    private void ApplyThoughtsVisibility()
    {
        bool show = ThoughtsVisible;
        foreach (FrameworkElement block in _thoughtBlocks)
        {
            bool hasContent = block is StackPanel sp
                && sp.Children.Count > 1
                && sp.Children[1] is TextBlock body
                && body.Text.Length > 0;
            block.Visibility = show && hasContent ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool ToolCallsVisible => _toolCallsToggle.IsChecked == true;

    /// <summary>Show/hide every tool-call block (those that have content) when the toggle flips.</summary>
    private void ApplyToolCallsVisibility()
    {
        bool show = ToolCallsVisible;
        foreach (TextBlock block in _toolBlocks)
            block.Visibility = show && block.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToolInvoked(ToolCallInfo info)
    {
        // Raised on the SK execution thread — marshal to the UI thread.
        void Apply()
        {
            if (_currentTools is null) return;
            if (_toolsBuf.Length > 0) _toolsBuf.Append('\n');
            string args = string.IsNullOrEmpty(info.Arguments) ? string.Empty : "(" + info.Arguments + ")";
            _toolsBuf.Append("→ ").Append(info.Function).Append(args);
            _currentTools.Text = _toolsBuf.ToString();
            // Data is always captured; visibility follows the (default-off) "Tool calls" debug toggle.
            _currentTools.Visibility = ToolCallsVisible ? Visibility.Visible : Visibility.Collapsed;
            ScrollToBottom();
        }
        MarshalToUi(Apply);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _send.Content = busy ? "Cancel" : "Send";
        _send.Background = busy ? CancelBg : AccentBg;
        _send.IsEnabled = true;             // always live: acts as Cancel while a turn streams
        _input.IsEnabled = !busy;
        UpdateMicEnabled();
        _status.Text = busy ? "Working…" : string.Empty;
        _status.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Mic is usable only when dictation is wired, no turn is running, and STT is idle/recording.</summary>
    private void UpdateMicEnabled()
    {
        if (_dictation is null) { _mic.IsEnabled = false; return; }
        DictationState state = _dictation.State;
        bool free = state is DictationState.Idle or DictationState.Recording;
        _mic.IsEnabled = !_busy && free;
    }

    private void SetDictationStatus(string? text)
    {
        _lastDictationStatus = text;
        _dictationStatus.Text = text ?? string.Empty;
        _dictationStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void MarshalToUi(Action action)
    {
        try
        {
            if (Dispatcher.CheckAccess()) action();
            else Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
        catch { /* window may be closing; UI updates are best-effort */ }
    }

    private void ScrollToBottom() => _scroller.ScrollToBottom();

    private Border Bubble(UIElement child, bool user)
    {
        var panel = new StackPanel();
        panel.Children.Add(child);
        return BubbleContent(panel, user);
    }

    private static Border BubbleContent(UIElement content, bool user) => new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(11, 8, 11, 8),
        Margin = new Thickness(0, 4, 0, 4),
        MaxWidth = 380,
        Background = user ? UserBubble : AsstBubble,
        BorderBrush = BorderFg,
        BorderThickness = new Thickness(user ? 0 : 1),
        HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        Child = content,
    };

    private void OnClosed(object? sender, EventArgs e)
    {
        // Stop any in-flight turn so a cancelled stream doesn't outlive the window.
        CancelTurn();

        // Detach from the (possibly shared/singleton) agent so a closed window never leaks a handler.
        _agent.ToolInvoked -= OnToolInvoked;

        if (_dictation is not null)
        {
            _dictation.StateChanged -= OnDictationStateChanged;
            _dictation.PartialTranscribed -= OnPartialTranscribed;
            try { _dictation.Cancel(); } catch { /* best-effort */ }
            // Only dispose the dictation service if we built it; a host-provided one is shared.
            if (_ownsDictation && _dictation is IDisposable disposable)
                try { disposable.Dispose(); } catch { /* best-effort */ }
        }

        Closed -= OnClosed;
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
