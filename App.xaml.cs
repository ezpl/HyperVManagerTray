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
    private bool?  _bridged;  // null = icon not yet initialized; ensures first switch always updates
    private System.Drawing.Icon? _iconImage;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Capture *every* unhandled exception to disk first thing — a WinUI tray app that
        // throws on the dispatcher thread otherwise dies silently (stowed exception in
        // CoreMessagingXP.dll) and the tray icon just vanishes with nothing in the log.
        RegisterGlobalExceptionHandlers();

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

            // Pre-warm the VM discovery cache so the right-click menu never blocks the UI
            // thread on first open.  Runs on the thread-pool; rebuilds the menu on the UI
            // thread once data arrives.  Failures are silently swallowed.
            _ = PreWarmVmCacheAsync();

            // Background startup update check — inserts a badge at the top of the tray menu
            // if a newer GitHub release exists.  Never blocks startup; failures are silent.
            _ = CheckForUpdatesOnStartupAsync();

            // Once initial binding has settled, clean up any orphaned management vNICs left on
            // the rule switches by older builds.  Idle-guarded, so it never disturbs a live link.
            _ = HealSwitchOrphansOnStartupAsync();
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"Failed to start Hyper-V Manager Tray:\n\n{ex}", "Hyper-V Manager Tray");
            Exit();
        }
    }

    // ── Global crash logging ────────────────────────────────────────────────────

    /// <summary>
    /// Wires the three process-wide exception sinks so a crash is never silent.
    /// UI/XAML-thread exceptions are logged and marked handled (the tray survives);
    /// background and unobserved-task exceptions are logged before the runtime acts.
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        // UI/XAML dispatcher thread — keep the tray alive instead of a silent stowed-exception crash.
        UnhandledException += (_, e) =>
        {
            LogCrash("UI/XAML UnhandledException", e.Exception);
            e.Handled = true;
        };

        // Background / finalizer threads — can't stop termination, but log and notify the user.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            LogCrash("AppDomain UnhandledException (fatal)", ex);
            NativeMethods.Error(
                $"Hyper-V Manager Tray crashed and needs to close.\n\n" +
                $"{ex?.Message ?? "Unknown error"}\n\n" +
                $"Details written to crash.log in %AppData%\\HyperVManagerTray.",
                "Hyper-V Manager Tray");
        };

        // Faulted Tasks whose exception was never awaited/observed.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Appends a full exception dump to crash.log (and the normal log if available). Never throws.</summary>
    private void LogCrash(string source, Exception? ex)
    {
        try { _loggerFactory?.CreateLogger("Crash").LogError(ex, "Unhandled exception ({Source})", source); }
        catch { /* logging must never throw */ }
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HyperVManagerTray");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [CRASH] {source}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* never throw from the crash logger */ }
    }

    // ── Tray icon ───────────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];
        SetTrayIcon(null);  // grey = unknown until first SwitchApplied fires

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
            try
            {
                bool bridged = result.VirtualSwitch != _config!.Current.Fallback.VirtualSwitch;
                if (bridged != _bridged)
                {
                    _bridged = bridged;
                    SetTrayIcon(bridged);
                }
                _dashboard?.OnSwitchApplied(result);
            }
            catch (Exception ex) { LogCrash("OnSwitchApplied UI update", ex); }
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

    /// <summary>
    /// Pre-warms the VM discovery cache on startup so the first right-click menu open is
    /// instant.  Calls <see cref="TrayMenu.RefreshState"/> once the cache is populated.
    /// </summary>
    private async Task PreWarmVmCacheAsync()
    {
        if (_hyperV is null || _menu is null) return;
        try
        {
            await _hyperV.GetAllVmsAsync().ConfigureAwait(false);
            // Cache is now populated — rebuild the menu on the UI thread so any previously
            // shown "Loading VMs…" placeholder is replaced with the real VM list.
            _ui.TryEnqueue(() => _menu.RefreshState());
        }
        catch { /* non-fatal — menu will retry on next right-click */ }
    }

    /// <summary>
    /// Runs a silent update check in the background.  If a newer release exists on GitHub the
    /// tray menu badge is set on the UI thread.  Network / parse failures are swallowed.
    /// </summary>
    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (_updateChecker is null || _menu is null) return;
        try
        {
            var result = await _updateChecker.CheckAsync().ConfigureAwait(false);
            if (result.UpdateAvailable)
                _ui.TryEnqueue(() => _menu.SetUpdateBadge(result));
        }
        catch { /* never surface a background check failure */ }
    }

    /// <summary>
    /// A short while after startup (once initial binding has settled), removes any orphaned
    /// duplicate management vNICs left on the rule switches by older builds.  The cleanup is
    /// idle-guarded inside <see cref="HyperVManager.HealSwitchOrphansAsync"/> so it never
    /// disturbs a live connection.  Best-effort; all failures are swallowed.
    /// </summary>
    private async Task HealSwitchOrphansOnStartupAsync()
    {
        if (_hyperV is null || _config is null) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);

            var switches = _config.Current.Rules
                .Select(r => r.VirtualSwitch)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var sw in switches)
                await _hyperV.HealSwitchOrphansAsync(sw).ConfigureAwait(false);
        }
        catch { /* best-effort cleanup; never surface */ }
    }

    /// <summary>Truncates <paramref name="line"/> to <paramref name="maxLen"/> chars, appending "…" if trimmed.</summary>
    private static string TruncateLine(string line, int maxLen) =>
        line.Length <= maxLen ? line : line[..(maxLen - 1)] + "…";

    /// <summary>Swaps the tray icon based on network state, disposing the previous one.</summary>
    private void SetTrayIcon(bool? bridged)
    {
        var state = bridged switch
        {
            true  => TrayIconState.Bridged,
            false => TrayIconState.Fallback,
            null  => TrayIconState.Unknown,
        };
        var previous = _iconImage;
        _iconImage = new System.Drawing.Icon(IconGenerator.GenerateAndSave(_exeDir, state));
        _trayIcon!.Icon = _iconImage;
        previous?.Dispose();
    }

    // ── Dashboard ───────────────────────────────────────────────────────────────

    private void ToggleDashboard()
    {
        if (_dashboard is null)
        {
            _dashboard = new DashboardWindow(_config!, _monitor!, _hyperV!);
            _dashboard.Closed += (_, _) => _dashboard = null;
        }

        if (_dashboard.AppWindow.IsVisible)
            _dashboard.HideWindow();
        else if (!_dashboard.HiddenByThisClick)
            // HiddenByThisClick filters out exactly one case: the tray click that is itself
            // the deactivation that just auto-hid the popup (that click means "close", not
            // "reopen").  Any other click — including a quick dismiss-elsewhere-then-tray
            // sequence — shows the window.
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
