using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using HyperVNetworkSwitcher.Helpers;
using HyperVNetworkSwitcher.Models;
using HyperVNetworkSwitcher.Services;

namespace HyperVNetworkSwitcher.UI;

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

    private static readonly TimeSpan PollInterval    = TimeSpan.FromSeconds(2);
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
            var names = _config.Current.VirtualMachines.Select(v => v.Name).ToList();
            if (names.Count == 0) return;

            var statuses = await _hyperV.GetVmStatusesAsync(names);

            if (DateTime.UtcNow - _lastVhdUtc > VhdPollInterval)
            {
                foreach (var kv in await _hyperV.GetVmVhdSizesAsync(names)) _vhd[kv.Key] = kv.Value;
                _lastVhdUtc = DateTime.UtcNow;
            }
            foreach (var s in statuses) if (_vhd.TryGetValue(s.Name, out var b)) s.VhdBytes = b;

            DispatcherQueue.TryEnqueue(() =>
            {
                BuildCards(statuses);
                if (AppWindow.IsVisible) ResizeAndPlace();
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

        // ── Header: VM name + state ──────────────────────────────────────────
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text       = vm.Name,
            FontSize   = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var stateLabel = new TextBlock
        {
            Text              = s?.State ?? "Unknown",
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = StateBrush(s),
        };
        Grid.SetColumn(stateLabel, 1);
        header.Children.Add(title);
        header.Children.Add(stateLabel);
        rows.Children.Add(header);

        // ── Switch / rule subtitle ───────────────────────────────────────────
        var switchText = !string.IsNullOrWhiteSpace(s?.Switch) ? s.Switch : "—";
        var ruleText   = _monitor.LastApplied?.RuleName ?? "—";
        rows.Children.Add(new TextBlock
        {
            Text       = $"{switchText}  ·  {ruleText}",
            FontSize   = 10,
            Foreground = (SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"],
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
            FontSize          = 11,
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

        var val = new TextBlock { Text = value, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
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
}
