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

    // ── Same-click detection ────────────────────────────────────────────────────
    // Clicking the tray icon while the popup is open first deactivates it (auto-hide),
    // then delivers the click command — without a guard that click would instantly
    // re-show the window it just toggled closed.  Time alone can't tell that apart from
    // a genuine quick re-open (dismiss by clicking the desktop, then click the tray), so
    // we also require the cursor to still be where it was when the hide happened: for
    // the click-through case both events come from the same physical click (distance≈0);
    // for a real re-open the cursor has travelled from the dismiss point to the tray.
    private const int SameClickMs       = 400;
    private const int SameClickRadiusPx = 24;

    private DateTime  _hiddenAtUtc = DateTime.MinValue;
    private (int X, int Y) _hiddenCursor = (int.MinValue, int.MinValue);

    /// <summary>True when the tray click being handled is the same physical click that just auto-hid this window.</summary>
    public bool HiddenByThisClick
    {
        get
        {
            if ((DateTime.UtcNow - _hiddenAtUtc).TotalMilliseconds > SameClickMs) return false;
            var (x, y) = NativeMethods.GetCursorPosition();
            return Math.Abs(x - _hiddenCursor.X) <= SameClickRadiusPx
                && Math.Abs(y - _hiddenCursor.Y) <= SameClickRadiusPx;
        }
    }

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
        _hiddenAtUtc  = DateTime.UtcNow;
        _hiddenCursor = NativeMethods.GetCursorPosition();
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
        // Never evaluate the network synchronously here — GetAllNetworkInterfaces() +
        // GetIPProperties() can block the UI thread for hundreds of ms, delaying the
        // window's appearance.  LastApplied is populated by NetworkMonitor's immediate
        // startup evaluation; until it lands, the host card keeps its placeholder text
        // and OnSwitchApplied fills it in moments later.
        if (_monitor.LastApplied is { } result) ApplyHostStatus(result);

        // First open only: build placeholder "Updating" cards so the popup opens at full
        // size instead of growing when data arrives.  On later opens the cards from the
        // previous session are still present (the window is hidden, never closed) and
        // LoadVmsAsync — triggered by OnActivated — refreshes them in place.
        if (VmPanel.Children.Count == 0) BuildCards([]);
    }

    private void ApplyHostStatus(MatchResult result)
    {
        RuleText.Text    = result.RuleName;
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

            IReadOnlyList<VmStatus> statuses = [];
            if (names.Count > 0)
            {
                // Status + (periodically) VHD sizes in ONE PowerShell round-trip — the
                // worker serialises commands anyway, so two separate queries would just
                // double the per-cycle cost.
                bool wantVhd = DateTime.UtcNow - _lastVhdUtc > VhdPollInterval;
                var (st, vhd) = await _hyperV.GetVmDashboardAsync(names, wantVhd);
                statuses = st;
                if (wantVhd)
                {
                    foreach (var kv in vhd) _vhd[kv.Key] = kv.Value;
                    _lastVhdUtc = DateTime.UtcNow;
                }
            }
            foreach (var s in statuses) if (_vhd.TryGetValue(s.Name, out var b)) s.VhdBytes = b;

            DispatcherQueue.TryEnqueue(() =>
            {
                bool layoutChanged = BuildCards(statuses);
                // Only re-measure the window when a card's layout actually changed (rows
                // appeared/disappeared); pure value updates never affect the size.  Defer
                // by one frame so WinUI's layout pass has processed the new children —
                // otherwise DesiredSize still reflects the previous layout.
                if (layoutChanged)
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (AppWindow.IsVisible) ResizeAndPlace();
                    });
            });
        }
        finally { _loading = false; }
    }

    // ── Card cache ──────────────────────────────────────────────────────────────
    // Cards are rebuilt only when their layout shape changes (state category, meter rows,
    // button set); per-tick value changes (CPU %, memory, uptime) are written into the
    // existing TextBlocks/ProgressBars.  This avoids reconstructing ~40 UI objects per VM
    // every second while the dashboard is open.

    private sealed class VmCard
    {
        public required Border    Root;
        public required string    Shape;     // layout signature — rebuild when it changes
        public required TextBlock State;
        public required TextBlock Subtitle;
        public TextBlock?   Uptime;
        public TextBlock?   CpuValue;
        public ProgressBar? CpuBar;
        public TextBlock?   MemValue;
        public ProgressBar? MemBar;
        public TextBlock?   DiskValue;
    }

    private readonly Dictionary<string, VmCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _cardOrder = [];

    /// <summary>Categorises everything that affects a card's row/button layout.</summary>
    private static string ShapeOf(VmStatus? s) =>
        (s switch { null => "none", { IsRunning: true } => "run", { IsPaused: true } => "pause", _ => "off" })
        + "|" + (FormatUptime(s).Length > 0)
        + "|" + (s is { VhdBytes: > 0 });

    /// <summary>
    /// Creates/updates the VM cards; returns true when any card's layout changed
    /// (so the window needs re-measuring).
    /// </summary>
    private bool BuildCards(IReadOnlyList<VmStatus> statuses)
    {
        var vms = _config.Current.VirtualMachines;

        // VM set/order changed (config edit, first open) → rebuild the panel wholesale.
        if (!vms.Select(v => v.Name).SequenceEqual(_cardOrder, StringComparer.OrdinalIgnoreCase))
        {
            VmPanel.Children.Clear();
            _cards.Clear();
            _cardOrder = vms.Select(v => v.Name).ToList();
            foreach (var vm in vms)
            {
                var card = BuildCard(vm, FindStatus(statuses, vm.Name));
                _cards[vm.Name] = card;
                VmPanel.Children.Add(card.Root);
            }
            return true;
        }

        bool layoutChanged = false;
        for (int i = 0; i < vms.Count; i++)
        {
            var vm = vms[i];
            var s  = FindStatus(statuses, vm.Name);
            if (!_cards.TryGetValue(vm.Name, out var card) || card.Shape != ShapeOf(s))
            {
                card = BuildCard(vm, s);          // layout shape changed → rebuild this card
                _cards[vm.Name]      = card;
                VmPanel.Children[i]  = card.Root;
                layoutChanged        = true;
            }
            else
            {
                UpdateCard(card, s);              // same shape → update values in place
            }
        }
        return layoutChanged;
    }

    private static VmStatus? FindStatus(IReadOnlyList<VmStatus> statuses, string name) =>
        statuses.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void UpdateCard(VmCard card, VmStatus? s)
    {
        card.State.Text       = FormatState(s);
        card.State.Foreground = StateBrush(s);
        card.Subtitle.Text    = Subtitle(s);
        if (card.Uptime is not null) card.Uptime.Text = FormatUptime(s);
        if (s is null) return;
        if (card.CpuValue is not null) { card.CpuValue.Text = $"{s.Cpu}%"; SetBar(card.CpuBar, s.Cpu / 100.0); }
        if (card.MemValue is not null) { card.MemValue.Text = $"{s.MemAssignedMb:N0} MB"; SetBar(card.MemBar, s.MemoryFraction); }
        if (card.DiskValue is not null) card.DiskValue.Text = $"{s.VhdGb:N1} GB";
    }

    private static void SetBar(ProgressBar? bar, double fraction)
    {
        if (bar is null) return;
        bar.Value      = Math.Clamp(fraction * 100, 0, 100);
        bar.Foreground = BarBrush(fraction);
    }

    private static Brush BarBrush(double fraction) =>
        fraction <= 0.5  ? AppColors.GaugeLowBrush
      : fraction <= 0.85 ? AppColors.GaugeMedBrush
      :                    AppColors.GaugeHighBrush;

    private string Subtitle(VmStatus? s)
    {
        var switchText = !string.IsNullOrWhiteSpace(s?.Switch) ? s!.Switch : "—";
        return $"{switchText}  ·  {_monitor.LastApplied?.RuleName ?? "—"}";
    }

    private VmCard BuildCard(VmTarget vm, VmStatus? s)
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

        TextBlock? uptimeLbl = null;
        var uptimeText = FormatUptime(s);
        if (!string.IsNullOrEmpty(uptimeText))
        {
            uptimeLbl = new TextBlock
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
        var subtitle = new TextBlock
        {
            Text       = Subtitle(s),
            FontSize   = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };
        rows.Children.Add(subtitle);

        // ── Metrics (running VMs only) ───────────────────────────────────────
        (TextBlock Value, ProgressBar? Bar)? cpu = null, mem = null, disk = null;
        if (running && s is not null)
        {
            cpu = AddMeter(rows, "CPU", $"{s.Cpu}%",                s.Cpu / 100.0);
            mem = AddMeter(rows, "Mem", $"{s.MemAssignedMb:N0} MB", s.MemoryFraction);
        }
        if (s is not null && s.VhdBytes > 0)
            disk = AddMeter(rows, "Disk", $"{s.VhdGb:N1} GB", -1);

        // ── Power buttons ────────────────────────────────────────────────────
        rows.Children.Add(BuildButtons(vm, s));

        var root = new Border
        {
            CornerRadius  = new CornerRadius(6),
            Padding       = new Thickness(10, 8, 10, 8),
            Background    = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush   = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child         = rows,
        };

        return new VmCard
        {
            Root      = root,
            Shape     = ShapeOf(s),
            State     = stateLabel,
            Subtitle  = subtitle,
            Uptime    = uptimeLbl,
            CpuValue  = cpu?.Value,  CpuBar = cpu?.Bar,
            MemValue  = mem?.Value,  MemBar = mem?.Bar,
            DiskValue = disk?.Value,
        };
    }

    // Adds a "label + optional progress bar + value" row and returns the mutable parts
    // so UpdateCard can refresh them in place on later ticks.
    private static (TextBlock Value, ProgressBar? Bar) AddMeter(
        StackPanel rows, string label, string value, double fraction)
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

        ProgressBar? bar = null;
        if (fraction >= 0)
        {
            bar = new ProgressBar
            {
                Minimum           = 0,
                Maximum           = 100,
                Value             = Math.Clamp(fraction * 100, 0, 100),
                Height            = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground        = BarBrush(fraction),
            };
            Grid.SetColumn(bar, 1);
            g.Children.Add(bar);
        }

        var val = new TextBlock { Text = value, FontSize = 11, Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"], VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(val, 2);
        g.Children.Add(val);

        rows.Children.Add(g);
        return (val, bar);
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
        else if (s is not null) // Off / Saved — hide Start buttons while status is still loading
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
        var state = s?.State ?? "Updating";
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
