using System.Diagnostics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

/// <summary>
/// Borderless Mica popup: host-network status card on top, then a control card per configured
/// VM (current switch/rule, state, CPU / memory / VHD meters, power buttons).  Appears
/// bottom-right above the taskbar and auto-dismisses when it loses focus.  VM metrics refresh
/// on a timer only while it is open, so a closed dashboard costs nothing.
/// </summary>
public sealed partial class DashboardWindow : Window
{
    private const double ContentWidth = 320;
    private const int    EdgeMargin   = 12;

    private static readonly TimeSpan PollInterval    = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan VhdPollInterval = TimeSpan.FromSeconds(15);

    private readonly ConfigManager  _config;
    private readonly NetworkMonitor _monitor;
    private readonly HyperVManager  _hyperV;

    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private readonly Dictionary<string, long> _vhd = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastVhdUtc = DateTime.MinValue;
    private bool     _loading;

    private DateTime _hiddenAtUtc = DateTime.MinValue;
    public TimeSpan SinceHidden => DateTime.UtcNow - _hiddenAtUtc;

    public DashboardWindow(ConfigManager config, NetworkMonitor monitor, HyperVManager hyperV)
    {
        _config  = config;
        _monitor = monitor;
        _hyperV  = hyperV;

        InitializeComponent();
        ConfigureWindowChrome();

        _timer.Tick += (_, _) => _ = LoadVmsAsync();
        Activated   += OnActivated;
        Closed      += (_, _) => _timer.Stop();
    }

    // ── Public surface ──────────────────────────────────────────────────────────

    public void ShowNearTray()
    {
        Refresh();
        ResizeAndPlace();
        AppWindow.Show();
        Activate();
    }

    public void HideWindow()
    {
        _timer.Stop();
        _hiddenAtUtc = DateTime.UtcNow;
        AppWindow.Hide();
    }

    /// <summary>Called by App (UI thread) when a switch change is applied.</summary>
    public void OnSwitchApplied(MatchResult result) => ApplyHostStatus(result);

    // ── Window chrome / placement ───────────────────────────────────────────────

    private void ConfigureWindowChrome()
    {
        AppWindow.IsShownInSwitchers = false;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
    }

    private void ResizeAndPlace()
    {
        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();

        Root.Width = ContentWidth;
        Root.Measure(new Size(ContentWidth, double.PositiveInfinity));
        double contentHeight = Root.DesiredSize.Height;

        int w      = (int)Math.Ceiling(ContentWidth * scale);
        int margin = (int)Math.Ceiling(EdgeMargin   * scale);
        int maxH   = work.Bottom - work.Top - margin * 2;
        int h      = Math.Min((int)Math.Ceiling(contentHeight * scale), maxH);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
        AppWindow.Move(new Windows.Graphics.PointInt32(work.Right - w - margin, work.Bottom - h - margin));
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
            HideWindow();
        else
        {
            _timer.Start();
            _ = LoadVmsAsync();
        }
    }

    // ── Host network card ───────────────────────────────────────────────────────

    private void Refresh()
    {
        var result = _monitor.LastApplied ?? AdapterMatcher.Evaluate(_config.Current);
        ApplyHostStatus(result);
    }

    private void ApplyHostStatus(MatchResult result)
    {
        AdapterText.Text = result.HostAdapterName;
        IpText.Text      = result.HostIp;
        GatewayText.Text = result.Gateway;
        DnsText.Text     = result.DnsServers.Count > 0
            ? string.Join("  ·  ", result.DnsServers.Take(2)) : "—";
    }

    // ── Per-VM cards ────────────────────────────────────────────────────────────

    private async Task LoadVmsAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var configVms = _config.Current.VirtualMachines;
            var names     = configVms.Select(v => v.Name).ToList();

            // Query statuses for config VMs (may be empty list — that's fine)
            var statuses = names.Count > 0
                ? await _hyperV.GetVmStatusesAsync(names)
                : Array.Empty<VmStatus>();

            if (names.Count > 0 && DateTime.UtcNow - _lastVhdUtc > VhdPollInterval)
            {
                foreach (var kv in await _hyperV.GetVmVhdSizesAsync(names)) _vhd[kv.Key] = kv.Value;
                _lastVhdUtc = DateTime.UtcNow;
            }
            foreach (var s in statuses) if (_vhd.TryGetValue(s.Name, out var b)) s.VhdBytes = b;

            DispatcherQueue.TryEnqueue(() =>
            {
                BuildCards(statuses);
                // Defer resize by one extra frame so WinUI's layout system processes
                // the newly added card children before Measure() is called — otherwise
                // DesiredSize still reflects the pre-cards (empty) layout.
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (AppWindow.IsVisible) ResizeAndPlace();
                });
            });
        }
        finally { _loading = false; }
    }

    private void BuildCards(IReadOnlyList<VmStatus> statuses)
    {
        VmPanel.Children.Clear();

        foreach (var vm in _config.Current.VirtualMachines)
        {
            var s = statuses.FirstOrDefault(x => x.Name.Equals(vm.Name, StringComparison.OrdinalIgnoreCase));
            VmPanel.Children.Add(BuildCard(vm, s));
        }
    }

    private Border BuildCard(VmTarget vm, VmStatus? s)
    {
        bool running = s?.IsRunning == true;
        var rows = new StackPanel { Spacing = 6 };

        // ── Header: VM name + state (+ uptime when running) ─────────────────
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text              = vm.Name,
            FontSize          = 12,
            FontWeight        = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRowSpan(title, 2);
        var stateLabel = new TextBlock
        {
            Text              = FormatState(s),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground        = StateBrush(s),
        };
        Grid.SetColumn(stateLabel, 1);
        Grid.SetRow(stateLabel, 0);
        header.Children.Add(title);
        header.Children.Add(stateLabel);

        var uptimeText = FormatUptime(s);
        if (!string.IsNullOrEmpty(uptimeText))
        {
            var uptimeLbl = new TextBlock
            {
                Text                = uptimeText,
                FontSize            = 10,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground          = AppColors.IndicatorGreyBrush,
            };
            Grid.SetColumn(uptimeLbl, 1);
            Grid.SetRow(uptimeLbl, 1);
            header.Children.Add(uptimeLbl);
        }

        rows.Children.Add(header);

        // ── Switch / rule subtitle ───────────────────────────────────────────
        var switchText = !string.IsNullOrWhiteSpace(s?.Switch) ? s.Switch : "—";
        var ruleText   = _monitor.LastApplied?.RuleName ?? "—";
        rows.Children.Add(new TextBlock
        {
            Text       = $"{switchText}  ·  {ruleText}",
            FontSize   = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });

        // ── Metrics (running VMs only) ───────────────────────────────────────
        if (running && s is not null)
        {
            rows.Children.Add(Meter("CPU", $"{s.Cpu}%",           s.Cpu / 100.0));
            rows.Children.Add(Meter("Mem", $"{s.MemAssignedMb:N0} MB", s.MemoryFraction));
        }
        if (s is not null && s.VhdBytes > 0)
            rows.Children.Add(Meter("Disk", $"{s.VhdGb:N1} GB", -1));

        // ── Power buttons ────────────────────────────────────────────────────
        rows.Children.Add(BuildButtons(vm, s));

        return new Border
        {
            CornerRadius  = new CornerRadius(6),
            Padding       = new Thickness(10, 8, 10, 8),
            Background    = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush   = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child         = rows,
        };
    }

    // label + optional progress bar + value
    private static Grid Meter(string label, string value, double fraction)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text              = label,
            FontSize          = 10,
            FontWeight        = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground        = AppColors.IndicatorGreyBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        g.Children.Add(lbl);

        if (fraction >= 0)
        {
            var bar = new ProgressBar
            {
                Minimum           = 0,
                Maximum           = 100,
                Value             = Math.Clamp(fraction * 100, 0, 100),
                Height            = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground        = fraction <= 0.5 ? AppColors.GaugeLowBrush
                                  : fraction <= 0.85 ? AppColors.GaugeMedBrush
                                  : AppColors.GaugeHighBrush,
            };
            Grid.SetColumn(bar, 1);
            g.Children.Add(bar);
        }

        var val = new TextBlock { Text = value, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(val, 2);
        g.Children.Add(val);
        return g;
    }

    private StackPanel BuildButtons(VmTarget vm, VmStatus? s)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };

        void Btn(string text, Func<Task> action) => panel.Children.Add(new Button
        {
            Content  = text,
            FontSize = 11,
            Padding  = new Thickness(8, 3, 8, 3),
            Command  = new RelayCommand(() => _ = RunThenReload(action)),
        });

        bool running = s?.IsRunning == true;
        bool paused  = s?.IsPaused  == true;

        if (running)
        {
            Btn("Shutdown",  () => _hyperV.ShutdownVmAsync(vm.Name));
            Btn("Pause",     () => _hyperV.SuspendVmAsync(vm.Name));
            Btn("Save",      () => _hyperV.SaveVmAsync(vm.Name));
            Btn("Connect",   () => ConnectAsync(vm));
        }
        else if (paused)
        {
            Btn("Resume", () => _hyperV.ResumeVmAsync(vm.Name));
            Btn("Save",   () => _hyperV.SaveVmAsync(vm.Name));
        }
        else // Off / Saved / unknown
        {
            Btn("Start",           () => _hyperV.StartOrResumeVmAsync(vm.Name));
            Btn("Start & Connect", () => StartAndConnectAsync(vm));
        }
        return panel;
    }

    private async Task ConnectAsync(VmTarget vm)
    {
        var sw = _monitor.LastApplied?.VirtualSwitch;
        if (!string.IsNullOrEmpty(sw)) await _hyperV.ApplySwitchAsync(vm.Name, vm.NicName, sw);

        try
        {
            Process.Start(new ProcessStartInfo("vmconnect.exe", $"localhost \"{vm.Name}\"")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            NativeMethods.Warn(
                "Could not open VM Connection.\n\nEnsure Hyper-V Manager tools are installed.",
                "Hyper-V Manager Tray");
        }
    }

    private async Task StartAndConnectAsync(VmTarget vm)
    {
        await _hyperV.StartOrResumeVmAsync(vm.Name);
        await Task.Delay(2500);
        await ConnectAsync(vm);
    }

    private async Task RunThenReload(Func<Task> action)
    {
        try { await action(); } catch { /* logged in HyperVManager */ }
        await Task.Delay(1200);
        await LoadVmsAsync();
    }

    private static Brush StateBrush(VmStatus? s) => s switch
    {
        { IsRunning: true } => AppColors.IndicatorGreenBrush,
        { IsPaused:  true } => AppColors.IndicatorOrangeBrush,
        { IsSaved:   true } => AppColors.IndicatorOrangeBrush,
        _                   => AppColors.IndicatorGreyBrush,
    };

    /// <summary>
    /// Formats the VM uptime for display on the card header.
    /// Returns empty string when the VM is not running or the uptime string is unavailable.
    /// Examples: "47m", "3h 14m", "1d 3h".
    /// </summary>
    private static string FormatUptime(VmStatus? s) => UptimeFormatter.Format(s);

    /// <summary>
    /// Formats the VM state string, appending a save/resume percentage when available.
    /// Hyper-V StatusDescriptions during transient states contains strings like "Saving, 47 %"
    /// or "Restoring, 12 %".  We extract the number and produce "Saving (47%)".
    /// </summary>
    private static string FormatState(VmStatus? s)
    {
        var state = s?.State ?? "Unknown";
        if (s is null) return state;

        var desc = s.StatusDesc ?? "";
        var pIdx = desc.IndexOf('%');
        if (pIdx > 0)
        {
            // Walk back past digits and spaces to find the start of the number
            var i = pIdx - 1;
            while (i > 0 && (char.IsDigit(desc[i - 1]) || desc[i - 1] == ' '))
                i--;
            var num = desc[i..pIdx].Trim();
            if (!string.IsNullOrEmpty(num))
                return $"{state} ({num}%)";
        }
        return state;
    }
}
