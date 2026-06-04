using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using HyperVNetworkSwitcher.Helpers;
using HyperVNetworkSwitcher.Services;
using HyperVNetworkSwitcher.UI;

namespace HyperVNetworkSwitcher;

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
    private readonly StartupManager _startup = new();

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
        _ui         = DispatcherQueue.GetForCurrentThread();
        _hostWindow = new MainWindow();   // never shown; keeps the app alive

        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HyperVNetworkSwitcher");
            Directory.CreateDirectory(logDir);
            _loggerFactory = LoggerFactory.Create(b =>
            {
                b.SetMinimumLevel(LogLevel.Information);
                b.AddSimpleFileLogger(Path.Combine(logDir, "switcher.log"));
            });

            var configPath = ConfigManager.GetConfigPath();
            if (!File.Exists(configPath))
            {
                NativeMethods.Error(
                    $"config.json not found at:\n{configPath}\n\nPlace config.json next to the executable and restart.",
                    "HyperV Network Switcher");
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
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"Failed to start HyperV Network Switcher:\n\n{ex}", "HyperV Network Switcher");
            Exit();
        }
    }

    // ── Tray icon ───────────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];
        SetTrayIcon(bridged: false);

        _menu = new TrayMenu(_config!, _monitor!, _hyperV!, _startup, OnExit);
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
            _trayIcon!.ToolTipText = $"HyperV Network Switcher: {result.VirtualSwitch}";
            _dashboard?.OnSwitchApplied(result);
        });
    }

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
        _loggerFactory?.Dispose();
        Exit();
    }
}
