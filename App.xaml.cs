using System.Net.Http;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Services;
using HyperVManagerTray.UI;

namespace HyperVManagerTray;

/// <summary>
/// Application entry point.  Owns the long-lived services (config / Hyper-V / network monitor),
/// the tray icon, and the dashboard popup.  Replaces the old WinForms <c>Program</c> +
/// <c>TrayApplication</c>.  A hidden host window keeps the WinUI app alive while only the tray
/// icon is visible.
/// </summary>
public partial class App : Application
{
    private ILoggerFactory? _loggerFactory;
    private ConfigManager?  _config;
    private HyperVManager?  _hyperV;
    private NetworkMonitor? _monitor;
    private StartupManager  _startup = null!;
    private HttpClient?     _httpClient;
    private UpdateChecker?  _updateChecker;

    private DispatcherQueue  _ui = null!;
    private Window?          _hostWindow;
    private TaskbarIcon?     _trayIcon;
    private DashboardWindow? _dashboard;
    private TrayMenu?        _menu;

    private string _exeDir = AppContext.BaseDirectory;
    private bool   _bridged;
    private System.Drawing.Icon? _iconImage;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Opt native Win32 elements (tray context menu, etc.) into system dark mode.
        // Must run before any UI is created so the menu HWND inherits the setting.
        NativeMethods.EnableDarkModeForNativeUi();

        _ui         = DispatcherQueue.GetForCurrentThread();
        _hostWindow = new MainWindow();   // never shown; keeps the app alive

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HyperVManagerTray");
            Directory.CreateDirectory(logDir);
            _loggerFactory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Information);
                b.AddSimpleFileLogger(Path.Combine(logDir, "switcher.log"));
            });

            _startup       = new StartupManager(_loggerFactory.CreateLogger<StartupManager>());
            _httpClient    = new HttpClient();
            _updateChecker = new UpdateChecker(_httpClient, _loggerFactory.CreateLogger<UpdateChecker>());

            var configPath = ConfigManager.GetConfigPath();
            if (!File.Exists(configPath))
            {
                NativeMethods.Error(
                    $"config.json not found at:\n{configPath}\n\nPlace config.json next to the executable and restart.",
                    "Hyper-V Manager Tray");
                Exit();
                return;
            }

            _exeDir  = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            _config  = new ConfigManager(configPath, _loggerFactory.CreateLogger<ConfigManager>());
            _hyperV  = new HyperVManager(_loggerFactory.CreateLogger<HyperVManager>());
            _monitor = new NetworkMonitor(_config, _hyperV, _loggerFactory.CreateLogger<NetworkMonitor>());

            InitTrayIcon();

            _monitor.SwitchApplied += OnSwitchApplied;
            _monitor.Start();

            // Populate the tooltip immediately — before the first SwitchApplied fires.
            _ = UpdateTooltipAsync();
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"Failed to start Hyper-V Manager Tray:\n\n{ex}", "Hyper-V Manager Tray");
            Exit();
        }
    }

    // ── Tray icon ───────────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];
        SetTrayIcon(bridged: false);

        _menu = new TrayMenu(_config!, _monitor!, _hyperV!, _startup, _updateChecker!, OnExit);
        _trayIcon.ContextFlyout     = _menu.Flyout;
        _trayIcon.LeftClickCommand  = new RelayCommand(ToggleDashboard);
        _trayIcon.RightClickCommand = new RelayCommand(() => _menu!.RefreshState());

        _trayIcon.ForceCreate();
    }

    // ── Switch-applied → update icon + open dashboard ───────────────────────────

    private void OnSwitchApplied(object? sender, MatchResult result)
    {
        _ui.TryEnqueue(() =>
        {
            bool bridged = result.VirtualSwitch != _config!.Current.Fallback.VirtualSwitch;
            if (bridged != _bridged)
            {
                _bridged = bridged;
                SetTrayIcon(bridged);
            }
            _dashboard?.OnSwitchApplied(result);
        });

        // Update tooltip with the new switch + fresh VM IPs (runs on thread-pool; posts to UI when done).
        _ = UpdateTooltipAsync();
    }

    /// <summary>
    /// Builds a multi-line tooltip showing the active virtual switch and each VM's first IPv4
    /// address, then posts it to the UI thread.  Never throws.
    /// </summary>
    private async Task UpdateTooltipAsync()
    {
        if (_hyperV is null || _config is null || _trayIcon is null) return;

        try
        {
            var switchName = _monitor?.LastApplied?.VirtualSwitch ?? "No switch";
            var vmNames    = _config.Current.VirtualMachines.Select(v => v.Name).ToList();
            var ips        = await _hyperV.GetVmIpAddressesAsync(vmNames);

            var lines = new System.Collections.Generic.List<string>
            {
                "Hyper-V Manager Tray",
                TruncateLine($"Switch: {switchName}", 63),
            };

            foreach (var name in vmNames)
            {
                if (ips.TryGetValue(name, out var ip))
                    lines.Add(TruncateLine($"{name}: {ip}", 63));
            }

            var tooltip = string.Join("\n", lines);
            // Win32 balloon-tip tooltip hard limit is 127 chars total.
            if (tooltip.Length > 127)
                tooltip = tooltip[..126] + "…";

            _ui.TryEnqueue(() => _trayIcon.ToolTipText = tooltip);
        }
        catch (Exception ex)
        {
            // Best-effort — never let a tooltip failure surface to the user.
            try { _ui.TryEnqueue(() => _trayIcon.ToolTipText = "Hyper-V Manager Tray"); } catch { }
            _ = ex; // suppress unused-variable warning
        }
    }

    /// <summary>Truncates <paramref name="line"/> to <paramref name="maxLen"/> chars, appending "…" if trimmed.</summary>
    private static string TruncateLine(string line, int maxLen) =>
        line.Length <= maxLen ? line : line[..(maxLen - 1)] + "…";

    /// <summary>Swaps the tray icon (blue = bridged, grey = fallback), disposing the previous one.</summary>
    private void SetTrayIcon(bool bridged)
    {
        var previous = _iconImage;
        _iconImage = new System.Drawing.Icon(IconGenerator.GenerateAndSave(_exeDir, bridged));
        _trayIcon!.Icon = _iconImage;
        previous?.Dispose();
    }

    // ── Dashboard ───────────────────────────────────────────────────────────────

    // A tray click that lands while the popup is open first deactivates it (auto-hiding);
    // guard against immediately re-showing it from the same click.
    private const int ReopenGuardMs = 300;

    private void ToggleDashboard()
    {
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow(_config!, _monitor!, _hyperV!);
            _dashboard.Closed += (_, _) => _dashboard = null;
        }

        if (_dashboard.AppWindow.IsVisible)
            _dashboard.HideWindow();
        else if (_dashboard.SinceHidden.TotalMilliseconds > ReopenGuardMs)
            _dashboard.ShowNearTray();
    }

    // ── Lifecycle ───────────────────────────────────────────────────────────────

    private void OnExit()
    {
        _trayIcon?.Dispose();
        _iconImage?.Dispose();
        _monitor?.Dispose();
        _config?.Dispose();
        _hyperV?.Dispose();
        _httpClient?.Dispose();
        _loggerFactory?.Dispose();
        Exit();
    }
}
