using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace FruityLink.Ui.Avalonia.Controls;

/// <summary>
/// The chat's "thinking" indicator: three brand-gradient dots pulsing in a left-to-right wave.
/// Animated WITHOUT Avalonia's animation system (which crashes FL's embedded software-rendered
/// host): a <see cref="DispatcherTimer"/> steps the dots' opacity a few times per second — the
/// same class of invalidation as streamed-text updates, which the embedded host handles fine.
/// The timer runs only while attached to the visual tree, and each tick no-ops when the control
/// isn't effectively visible (e.g. the reply arrived and PendingVisible collapsed the row).
/// </summary>
public partial class ThinkingDots : UserControl
{
    private const double LitOpacity = 1.0;
    private const double DimOpacity = 0.3;

    private readonly DispatcherTimer _timer;
    private Border? _dot1, _dot2, _dot3;
    private int _phase;

    public ThinkingDots()
    {
        // Same direct-load pattern as the Views/LogoMark.
        AvaloniaXamlLoader.Load(this);
        _dot1 = this.FindControl<Border>("Dot1");
        _dot2 = this.FindControl<Border>("Dot2");
        _dot3 = this.FindControl<Border>("Dot3");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _timer.Tick += OnTick;
        ApplyPhase();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Hidden (reply text arrived) → skip the repaint entirely; the wave resumes
        // from wherever it was if the row becomes visible again.
        if (!IsEffectivelyVisible) return;

        _phase = (_phase + 1) % 3;
        ApplyPhase();
    }

    private void ApplyPhase()
    {
        if (_dot1 is null || _dot2 is null || _dot3 is null) return;
        _dot1.Opacity = _phase == 0 ? LitOpacity : DimOpacity;
        _dot2.Opacity = _phase == 1 ? LitOpacity : DimOpacity;
        _dot3.Opacity = _phase == 2 ? LitOpacity : DimOpacity;
    }
}
