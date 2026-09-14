using System;
using FruityLink.Plugins.Abstractions;
using FruityLink.Ui.Avalonia.ViewModels;
using FruityLink.Ui.Avalonia.Views;

namespace FruityLink.Ui.Avalonia.Hosting;

/// <summary>
/// A single chat window instance living on the <see cref="EmbeddedAvaloniaHost"/> UI thread. The
/// PRODUCT part is just the window + view-model pairing; all the embed mechanics (HWND, embed-friendly /
/// external presentation, the forced re-present airspace fix, show/hide/close, hide-on-external-close)
/// live in the SDK's generic <see cref="EmbeddedAvaloniaView"/>, which this class wraps 1:1. The plugin
/// owns the reparent itself (the SDK's <c>IFlWindowHost.TryEmbed</c> takes this view's
/// <see cref="Handle"/>).
///
/// <para><b>Threading.</b> Construct + <see cref="PrepareForEmbedding"/> + read <see cref="Handle"/> on
/// the Avalonia UI thread (the plugin wraps them in <see cref="EmbeddedAvaloniaHost.Invoke"/>).
/// <see cref="ForceRender"/> / <see cref="SetVisible"/> / <see cref="Close"/> self-marshal, so they are
/// safe to call from any thread.</para>
/// </summary>
public sealed class AvaloniaChatView
{
    private readonly EmbeddedAvaloniaView _view;

    /// <summary>The chat view-model — the plugin's presenter binds this to the real agent + dictation.</summary>
    public ChatViewModel ViewModel { get; }

    /// <summary>Raised (on the UI thread) when the user closes the EXTERNAL window via its OS close (X):
    /// we hide it instead of destroying it (mirroring FL's View-menu windows) so the toggle can re-show it.
    /// Not raised in the embedded case (there FL's native close is handled by the bridge).</summary>
    public event Action? HiddenByUser;

    /// <summary>
    /// Must be constructed on the Avalonia UI thread (via <see cref="EmbeddedAvaloniaHost.Invoke"/>).
    /// Creates the window + view-model but does NOT show it — call <see cref="PrepareForEmbedding"/> or
    /// <see cref="ShowExternal"/>.
    /// </summary>
    public AvaloniaChatView()
    {
        ViewModel = new ChatViewModel();
        _view = new EmbeddedAvaloniaView(new ChatWindow { DataContext = ViewModel });
        _view.HiddenByUser += () => HiddenByUser?.Invoke();
    }

    /// <summary>
    /// The native window handle (HWND) to hand to the bridge for reparenting. Valid only after the
    /// window has been shown (<see cref="PrepareForEmbedding"/> / <see cref="ShowExternal"/>). Call on
    /// the UI thread.
    /// </summary>
    public IntPtr Handle => _view.Handle;

    /// <summary>Preferred content dimensions in physical pixels for the native FL frame.</summary>
    public FlWindowOptions GetWindowOptions(string caption) => _view.GetWindowOptions(caption);

    /// <summary>Keep the embedded child aligned with the FL frame's client area as it resizes.</summary>
    public void PinToHostContent(int insetX, int insetY) => _view.PinToHostContent(insetX, insetY);

    /// <summary>Restore typing focus after an explicit user request to show the chat.</summary>
    public void FocusComposer() => ((ChatWindow)_view.Window).FocusComposer();

    /// <summary>
    /// Make the window child-embed-friendly BEFORE the reparent: no OS chrome (FL draws the chrome), no
    /// taskbar button, no activation steal, parked off-screen so the pre-embed show (which realizes the
    /// HWND + forces the first Skia paint) never flashes on the desktop. Call on the UI thread; returns
    /// once the HWND exists.
    /// </summary>
    public void PrepareForEmbedding() => _view.PrepareForEmbedding();

    /// <summary>External top-level fallback (no bridge / reparent failed): normal chrome, on-screen.</summary>
    public void ShowExternal() => _view.ShowExternal();

    /// <summary>
    /// Force the embedded child to actually re-present. In software/redirection mode Avalonia paints via
    /// the redirection surface, but after a host hide→show (or an initial reparent) it can stay blank
    /// until an input event — so we invalidate the visual tree AND drive a synchronous native repaint.
    /// Safe from any thread.
    /// </summary>
    public void ForceRender() => _view.ForceRender();

    /// <summary>Show/hide the window (used by the external, non-embedded fallback path). Any thread.</summary>
    public void SetVisible(bool visible) => _view.SetVisible(visible);

    /// <summary>Close (destroy) the window. The Avalonia THREAD keeps running for a later re-enable.</summary>
    public void Close() => _view.Close();
}
