using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FruityLink.FlStudio.Inject;

namespace FruityLink.Host;

/// <summary>
/// Minimal, self-contained proof window shown INSIDE FL Studio's process. It exercises the
/// in-process bridge (ping/info/tempo/channels) so milestones 2 (managed code runs in FL),
/// 3 (a WPF window inside FL) and 4 (a real FL action via the in-proc C++ bridge, no pipe) are
/// all visibly demonstrated. Code-only (no XAML/pack-URIs) to keep the host dependency-free.
/// </summary>
internal sealed class HostWindow : Window
{
    private readonly TextBox _out;

    public HostWindow()
    {
        Title = "FruityLink — running INSIDE FL Studio (in-process, no pipe)";
        Width = 620; Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x22));

        var root = new DockPanel { Margin = new Thickness(10) };

        var header = new TextBlock
        {
            Text = "FruityLink in-process host\nversion.dll proxy → CoreCLR → managed WPF → in-proc C++ bridge",
            Foreground = Brushes.White,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        DockPanel.SetDock(header, Dock.Top);

        var rerun = new Button { Content = "Re-run probes", Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        rerun.Click += (_, _) => RunProbes();
        DockPanel.SetDock(rerun, Dock.Bottom);

        _out = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)),
            Foreground = Brushes.LightGreen,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            BorderThickness = new Thickness(0)
        };

        root.Children.Add(header);
        root.Children.Add(rerun);
        root.Children.Add(_out);
        Content = root;

        Loaded += (_, _) => RunProbes();
    }

    private async void RunProbes() => await RunProbeSequence(Append);

    /// <summary>
    /// The in-process bridge probe sequence (ping/info/tempo read+reversible-write/channels). Each
    /// line is handed to <paramref name="emit"/>: the proof window passes its textbox+file sink, while
    /// <see cref="HostEntry"/> passes the file logger so the probes still run — after FL is ready — even
    /// when the window is hidden by default. Never throws (each step is guarded).
    /// </summary>
    internal static async Task RunProbeSequence(Action<string> emit)
    {
        emit("──────── in-process bridge probes ────────");
        var bridge = new FlInjectBridge(); // RawAsync now routes through the in-proc transport
        try
        {
            emit("ping             -> " + InProcBridge.Raw("ping"));
            emit("info             -> " + InProcBridge.Raw("info"));
            emit("IsAvailableAsync -> " + await bridge.IsAvailableAsync());
            try
            {
                double t0 = await bridge.GetTempoAsync();
                emit("GetTempoAsync    -> " + t0 + " BPM");
                // Reversible WRITE through the same in-proc transport (proves a real mutation, no pipe).
                double probe = t0 >= 521 ? t0 - 1 : t0 + 1;
                await bridge.SetTempoAsync(probe);
                double t1 = await bridge.GetTempoAsync();
                await bridge.SetTempoAsync(t0); // restore
                emit($"SetTempo write   -> set {probe}, read back {t1}, restored {t0} (in-proc write OK)");
            }
            catch (Exception ex) { emit("Tempo r/w        -> (no project?) " + ex.Message); }
            try { emit("ListChannels     ->\n" + await bridge.ListChannelsAsync()); }
            catch (Exception ex) { emit("ListChannels     -> " + ex.Message); }
        }
        catch (Exception ex)
        {
            emit("ERROR: " + ex);
        }
    }

    private void Append(string s)
    {
        HostEntry.Log("WINDOW: " + s.Replace("\r", "").Replace("\n", " | "));
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Append(s)); return; }
        _out.AppendText(s + "\r\n");
        _out.ScrollToEnd();
    }
}
