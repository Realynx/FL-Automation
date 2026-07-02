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
    private readonly ScrollViewer _scroller;
    private readonly TextBox _inputBox;
    private INotifyCollectionChanged? _observedMessages;

    public ChatWindow()
    {
        // Load XAML directly (rather than a generated InitializeComponent) and resolve named
        // controls by lookup — robust regardless of the XAML name-generator configuration.
        AvaloniaXamlLoader.Load(this);
        _scroller = this.FindControl<ScrollViewer>("TranscriptScroller")!;
        _inputBox = this.FindControl<TextBox>("InputBox")!;

        // Enter sends; Shift+Enter inserts a newline (matches the WPF chat window). Tunnel so we
        // intercept before the TextBox consumes the Enter as text.
        _inputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Follow the transcript to the bottom whenever its content GROWS — not just when a message is
        // added, but as the assistant reply STREAMS token-by-token into the current bubble (which grows
        // the extent without changing the message count). Honours the Auto-scroll preference.
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

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollToBottom();

    private void OnScrollerPropertyChanged(object? sender, global::Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        // Content grew (new message or streamed tokens) → re-pin to the bottom (if Auto-scroll is on).
        if (e.Property == ScrollViewer.ExtentProperty) ScrollToBottom();
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
