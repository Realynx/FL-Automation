using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
internal sealed class FlAgentChatWindow : Window
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

    /// <summary>What was already typed when dictation began, so live partials append to it.</summary>
    private string _dictationPrefix = string.Empty;
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
            _dictation.StateChanged += OnDictationStateChanged;
            _dictation.PartialTranscribed += OnPartialTranscribed;
        }
        Closed += OnClosed;

        UpdateMicEnabled();
    }

    // ---- window-host embed support (task #22, Phase 1) ----

    /// <summary>Ensure the Win32 HWND exists (without requiring a prior Show) and return it — the handle
    /// we hand to the native bridge to reparent this window into an FL host form.</summary>
    public IntPtr EnsureNativeHandle() => new WindowInteropHelper(this).EnsureHandle();

    /// <summary>
    /// Make the window child-embed-friendly BEFORE it is shown: drop OS chrome + taskbar presence, don't
    /// steal activation, and park it off-screen so the brief pre-embed <see cref="Window.Show"/> (which
    /// forces a WPF layout/render pass) never flashes on the desktop. The native side also strips the
    /// top-level styles during the reparent; setting them here keeps WPF's own state consistent.
    /// </summary>
    public void PrepareForEmbedding()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;

        // Force SOFTWARE rendering BEFORE the first Show()/reparent. As a WS_CHILD of FL's non-WPF parent,
        // hardware/DWM composition hits the airspace bug (blank until an input event). Setting it in
        // PinToHostContent (AFTER the reparent) is too late for the first paint → blank on load.
        try
        {
            IntPtr h = new WindowInteropHelper(this).EnsureHandle();
            _pinSrc ??= HwndSource.FromHwnd(h);
            if (_pinSrc?.CompositionTarget is HwndTarget ht)
            {
                ht.RenderMode = RenderMode.SoftwareOnly;
                // Composite onto an OPAQUE backdrop. Without this, areas of the child not covered by a control
                // stay transparent in software mode inside the foreign FL parent — so FL content behind (e.g.
                // the animated mascot it shows in the MAXIMIZED script-dialog) bleeds through the empty chat
                // area. An opaque backdrop matching the window bg makes the whole child cover the FL form.
                ht.BackgroundColor = OpaqueBackdrop;
            }
        }
        catch { /* best-effort; PinToHostContent also sets it */ }
    }

    /// <summary>Undo <see cref="PrepareForEmbedding"/> for the external fallback: normal chrome, on-screen,
    /// roughly centred. Safe to call whether or not embedding was attempted.</summary>
    public void RestoreExternalChrome()
    {
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Left = Math.Max(0, (SystemParameters.PrimaryScreenWidth - Width) / 2);
        Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - Height) / 2);
    }

    // --- Embed positioning: pin our HWND to fill the FL host's content control ---
    // Once we're a WS_CHILD, WPF keeps re-applying Window.Left/Top and lands us OFF the parent (the classic
    // WPF-window-as-child coord bug). We intercept WM_WINDOWPOSCHANGING at the Win32 level (WPF can't override
    // it) and force x=0,y=0 filling the parent's client, so the chat always sits exactly over the FL content
    // control regardless of what WPF wants.
    private HwndSource? _pinSrc;
    private int _insetX, _insetY;   // FL content inset: border (left/right/bottom) + titlebar (top)
    public void PinToHostContent(int insetX, int insetY)
    {
        _insetX = insetX; _insetY = insetY;
        try
        {
            IntPtr h = new WindowInteropHelper(this).EnsureHandle();
            _pinSrc ??= HwndSource.FromHwnd(h);
            _pinSrc?.AddHook(PinHook);
            // As a WS_CHILD of a NON-WPF (FL) parent, WPF's default DWM/hardware composition hits the classic
            // "airspace" bug: the child's redirection surface isn't presented reliably — it goes blank on
            // re-show and only repaints the strip under a moving cursor. Forcing SOFTWARE rendering makes WPF
            // paint straight through WM_PAINT/GDI, which composites correctly inside a foreign parent. This is
            // the real fix for the blank/lazy-render bug (the earlier size-nudge only masked it). Perf is a
            // non-issue for a small text chat.
            if (_pinSrc?.CompositionTarget is HwndTarget ht) { ht.RenderMode = RenderMode.SoftwareOnly; ht.BackgroundColor = OpaqueBackdrop; }
            SnapToContent(h);
        }
        catch { /* best-effort */ }
    }
    private void SnapToContent(IntPtr h)
    {
        IntPtr parent = GetParent(h);
        if (parent == IntPtr.Zero || !GetClientRect(parent, out RECT rc)) return;
        int w = (rc.right - rc.left) - 2 * _insetX, hh = (rc.bottom - rc.top) - _insetY - _insetX;
        if (w < 1) w = 1; if (hh < 1) hh = 1;
        SetWindowPos(h, IntPtr.Zero, _insetX, _insetY, w, hh, 0x0014 /*NOZORDER|NOACTIVATE*/);
    }
    private IntPtr PinHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_WINDOWPOSCHANGING = 0x0046;
        if (msg == WM_WINDOWPOSCHANGING)
        {
            IntPtr parent = GetParent(hwnd);
            if (parent != IntPtr.Zero && GetClientRect(parent, out RECT rc))
            {
                int w = (rc.right - rc.left) - 2 * _insetX, hh = (rc.bottom - rc.top) - _insetY - _insetX;
                if (w < 1) w = 1; if (hh < 1) hh = 1;
                var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                wp.x = _insetX; wp.y = _insetY; wp.cx = w; wp.cy = hh;   // stay below the FL titlebar
                wp.flags &= ~0x0003u;   // clear SWP_NOSIZE(0x1)|SWP_NOMOVE(0x2) so our x/y/cx/cy apply
                Marshal.StructureToPtr(wp, lParam, false);
            }
        }
        const int WM_WINDOWPOSCHANGED = 0x0047;
        if (msg == WM_WINDOWPOSCHANGED)
        {
            // The FL host was minimized / maximized / docked, so the host subclass just resized+repositioned
            // this child. In SOFTWARE-render mode inside a foreign (FL) parent, WPF does NOT re-present on its
            // own after such a change — the airspace bug leaves the child transparent/blank (FL's form shows
            // through) until an input event.
            ForceRerender();
            const uint SWP_NOSIZE = 0x0001, WM_MOUSEMOVE = 0x0200;
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((wp.flags & SWP_NOSIZE) == 0)
                // A SIZE change (maximize / restore / dock). ForceRerender alone presents the OLD frame
                // (present-before-rerender), so the newly-exposed area stays blank until real input. A
                // synthetic mouse-move — POSTED, so it does NOT move the OS cursor and never clicks — kicks
                // WPF's full render+present cycle at the new size. Verified live: this is the reliable
                // re-present after a resize inside the FL parent. Only fires on actual resizes (not moves/
                // scroll/typing). Coords (10,10) sit over the transcript → harmless (no button state).
                PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((10 << 16) | 10));
        }
        return IntPtr.Zero;
    }
    /// <summary>
    /// Force the embedded WPF child to actually re-present after the FL host form was hidden→re-shown. With
    /// software rendering (see <see cref="PinToHostContent"/>) WPF paints via WM_PAINT, so we invalidate the
    /// visual tree AND immediately drive a synchronous native repaint of the whole client — otherwise the
    /// child can stay blank until the next input event. Self-dispatches to the UI thread.
    /// </summary>
    public void ForceRerender()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    InvalidateVisual();
                    UpdateLayout();
                    IntPtr h = new WindowInteropHelper(this).Handle;
                    if (h != IntPtr.Zero)
                        RedrawWindow(h, IntPtr.Zero, IntPtr.Zero,
                            RDW_INVALIDATE | RDW_ERASE | RDW_UPDATENOW | RDW_ALLCHILDREN);
                }
                catch { }
            }), DispatcherPriority.Render);
        }
        catch { }
    }

    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, IntPtr lprc, IntPtr hrgn, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);

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
            if (_dictation is not null && _dictation.State == DictationState.Recording)
                await FinalizeDictationAsync();

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
                        ? "Done."
                        : "(The model returned no text. If it never calls tools, try a tool-capable model.)";
            }
            catch (OperationCanceledException)
            {
                NoteCancelled();
                _log("[fl-agent] turn cancelled by user.");
            }
            catch (Exception ex)
            {
                string detail = ex.Message;
                if (ex.InnerException is not null && ex.InnerException.Message != ex.Message)
                    detail += " — " + ex.InnerException.Message;
                if (_currentAnswer is not null)
                {
                    _currentAnswer.Text = "⚠ " + detail;
                    _currentAnswer.Foreground = Frozen(0xF2, 0x8B, 0x82);
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
            _currentAnswer.Text = "(cancelled)";
            _currentAnswer.Foreground = MutedFg;
        }
        else
        {
            _currentAnswer.Text += "\n\n(cancelled)";
        }
    }

    // ---- speech-to-text (Whisper) — mirrors the prior app's flow ----

    /// <summary>Mic button: start recording, or stop + take the final transcription into the box.</summary>
    private async Task ToggleMicAsync()
    {
        if (_dictation is null || _busy) return;

        if (_dictation.State == DictationState.Recording)
        {
            await FinalizeDictationAsync();
            return;
        }

        if (_dictation.State != DictationState.Idle) return;

        if (!_dictation.IsModelReady)
        {
            SetDictationStatus("Downloading speech model (~150 MB, one time)…");
            var progress = new Progress<double>(p => SetDictationStatus($"Downloading speech model… {p * 100:0}%"));
            try { await _dictation.EnsureModelAsync(progress); }
            catch (Exception ex) { SetDictationStatus("Model download failed: " + ex.Message); return; }
            SetDictationStatus(null);
        }

        // Remember what's already typed so live partials append to (not clobber) it.
        _dictationPrefix = (_input.Text ?? string.Empty).TrimEnd();
        try { _dictation.StartRecording(); }
        catch (Exception ex) { SetDictationStatus("Microphone unavailable: " + ex.Message); }
    }

    /// <summary>Stops capture and writes the authoritative final transcript into the input box.</summary>
    private async Task FinalizeDictationAsync()
    {
        if (_dictation is null) return;
        string prefix = _dictationPrefix;
        string text;
        try { text = await _dictation.StopAndTranscribeAsync(); }
        catch (Exception ex) { SetDictationStatus("Transcription failed: " + ex.Message); return; }
        _input.Text = Combine(prefix, text);
    }

    private static string Combine(string prefix, string text)
    {
        if (string.IsNullOrEmpty(prefix)) return text;
        return string.IsNullOrEmpty(text) ? prefix : prefix + " " + text;
    }

    private void OnPartialTranscribed(string text)
    {
        void Apply()
        {
            // Ignore late partials that arrive after recording has stopped.
            if (_dictation is not null && _dictation.State == DictationState.Recording)
                _input.Text = Combine(_dictationPrefix, text);
        }
        MarshalToUi(Apply);
    }

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
