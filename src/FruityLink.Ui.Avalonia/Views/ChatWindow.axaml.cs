using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using FruityLink.Ui.Avalonia.ViewModels;

namespace FruityLink.Ui.Avalonia.Views;

public partial class ChatWindow : Window
{
    /// <summary>How close (px) to the bottom still counts as "at the bottom" for auto-scroll pinning.</summary>
    private const double BottomSlackPx = 24;

    private readonly ScrollViewer _scroller;
    private readonly TextBox _inputBox;
    private readonly Button _jumpToLatest;
    private INotifyCollectionChanged? _observedMessages;

    /// <summary>True while the view should follow the newest content. Cleared when the user scrolls
    /// up (so streaming never yanks their reading position); restored when they return to the bottom,
    /// click "jump to latest", or send a message.</summary>
    private bool _pinnedToBottom = true;

    public ChatWindow()
    {
        App.ApplyEmbeddedWindowTheme(this);
        // Load XAML directly (rather than a generated InitializeComponent) and resolve named
        // controls by lookup — robust regardless of the XAML name-generator configuration.
        AvaloniaXamlLoader.Load(this);
        _scroller = this.FindControl<ScrollViewer>("TranscriptScroller")!;
        _inputBox = this.FindControl<TextBox>("InputBox")!;
        _jumpToLatest = this.FindControl<Button>("JumpToLatest")!;

        // Enter sends; Shift+Enter inserts a newline (matches the WPF chat window). Tunnel so we
        // intercept before the TextBox consumes the Enter as text.
        _inputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Follow the transcript to the bottom whenever its content GROWS — not just when a message is
        // added, but as the assistant reply STREAMS token-by-token into the current bubble (which grows
        // the extent without changing the message count). Honours the Auto-scroll preference AND the
        // user's reading position (scrolling up unpins; see OnScrollerPropertyChanged).
        _scroller.PropertyChanged += OnScrollerPropertyChanged;

        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => ScrollToBottom();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Keep the transcript pinned to the newest message as the conversation grows.
        if (_observedMessages is not null)
            _observedMessages.CollectionChanged -= OnMessagesChanged;

        _observedMessages = (DataContext as ChatViewModel)?.Messages;
        if (_observedMessages is not null)
            _observedMessages.CollectionChanged += OnMessagesChanged;

        ScrollToBottom();
    }

    /// <summary>Focuses the existing composer without replacing its text or caret.</summary>
    public void FocusComposer() => _inputBox.Focus();

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A new message (the user just sent, or a reply bubble opened) re-pins even if the user had
        // scrolled up — a fresh turn is an explicit "show me the newest" signal.
        _pinnedToBottom = true;
        ScrollToBottom();
    }

    private void OnScrollerPropertyChanged(object? sender, global::Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ExtentProperty)
        {
            // Content grew (new message or streamed tokens) → re-pin to the bottom, but only if the
            // user hasn't scrolled up to read something.
            if (_pinnedToBottom) ScrollToBottom();
            UpdateJumpToLatest();
        }
        else if (e.Property == ScrollViewer.OffsetProperty || e.Property == ScrollViewer.ViewportProperty)
        {
            // Any offset move (user wheel/drag or our own pinning) re-derives the pinned state from
            // where the viewport actually is: at the bottom = pinned, away from it = unpinned.
            _pinnedToBottom = IsAtBottom();
            UpdateJumpToLatest();
        }
    }

    private bool IsAtBottom()
    {
        if (_scroller.Extent.Height <= _scroller.Viewport.Height + 1) return true;   // nothing to scroll
        return _scroller.Offset.Y + _scroller.Viewport.Height >= _scroller.Extent.Height - BottomSlackPx;
    }

    private void UpdateJumpToLatest()
    {
        bool scrollable = _scroller.Extent.Height > _scroller.Viewport.Height + 1;
        _jumpToLatest.IsVisible = scrollable && !_pinnedToBottom;
    }

    private void OnJumpToLatestClick(object? sender, RoutedEventArgs e)
    {
        _pinnedToBottom = true;
        _scroller.Offset = _scroller.Offset.WithY(_scroller.Extent.Height);
        UpdateJumpToLatest();
    }

    private void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        // Empty-state quick-start chip → drop the prompt into the composer (editable, not auto-sent)
        // and focus it so Enter fires immediately.
        if (DataContext is not ChatViewModel vm) return;
        if ((sender as Button)?.Content is not string text) return;

        vm.InputText = text;
        _inputBox.Focus();
        _inputBox.CaretIndex = text.Length;
    }

    private async void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChatMessage msg || !msg.HasText) return;

        var clipboard = Clipboard;
        if (clipboard is null) return;

        await clipboard.SetTextAsync(msg.Text);
        (DataContext as ChatViewModel)?.FlashStatus("Copied to clipboard");
    }

    private void OnReportBugClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ChatMessage msg) return;
        (DataContext as ChatViewModel)?.RequestBugReport(msg);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return; // newline

        e.Handled = true;
        if (DataContext is ChatViewModel vm && vm.SendCommand.CanExecute(null))
            vm.SendCommand.Execute(null);
    }

    private void ScrollToBottom()
    {
        // Honour the persisted Auto-scroll preference (Settings panel). When off, leave the user's
        // scroll position alone as new messages arrive.
        if (DataContext is ChatViewModel { AutoScroll: false }) return;

        // Defer to after layout so Extent reflects the just-added item; Offset is coerced to max.
        Dispatcher.UIThread.Post(
            () => _scroller.Offset = _scroller.Offset.WithY(_scroller.Extent.Height),
            DispatcherPriority.Background);
    }
}
