using HyperVNetworkSwitcher.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace HyperVNetworkSwitcher;

/// <summary>
/// The system-tray UI and application context.  Owns the <see cref="NotifyIcon"/>, its
/// context menu, and the status popup; wires user actions (force re-evaluate, manual
/// override, add current network, open config/log, toggle startup, exit) to the underlying
/// services; and updates the icon colour and popup whenever a switch change is applied.
/// </summary>
public sealed class TrayApplication : ApplicationContext, IDisposable
{
    private const string AppName    = "HyperVNetworkSwitcher";
    private const string TaskName   = "HyperVNetworkSwitcher";
    // Legacy HKCU Run-key location used by older versions — cleaned up on first toggle.
    private const string RunRegKey  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private readonly ConfigManager  _config;
    private readonly NetworkMonitor _monitor;
    private readonly HyperVManager  _hyperV;
    private readonly ILogger<TrayApplication> _logger;
    private readonly SynchronizationContext   _uiContext;
    private readonly NotifyIcon        _trayIcon;
    private readonly StatusPopupForm   _popup;
    private readonly ToolStripMenuItem _overrideMenu;
    private readonly ToolStripMenuItem _startupItem;
    private Icon? _currentIcon;

    // ── Constructor ───────────────────────────────────────────────────────────

    public TrayApplication(
        ConfigManager  config,
        NetworkMonitor monitor,
        HyperVManager  hyperV,
        ILogger<TrayApplication> logger)
    {
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _config    = config;
        _monitor   = monitor;
        _hyperV    = hyperV;
        _logger    = logger;

        _popup        = new StatusPopupForm();
        _overrideMenu = new ToolStripMenuItem("Manual Override");
        _startupItem  = new ToolStripMenuItem("Run on startup", null, OnToggleStartup)
        {
            Checked = IsStartupEnabled()
        };

        RebuildOverrideMenu();

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Force Re-evaluate", null, async (_, _) => await _monitor.ForceEvaluateAsync());
        contextMenu.Items.Add(_overrideMenu);
        contextMenu.Items.Add("Add current network as bridged", null, OnAddCurrentAsBridged);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Open config.json", null, OnOpenConfig);
        contextMenu.Items.Add("Open log file",    null, OnOpenLogFile);
        contextMenu.Items.Add("Reload config",    null, OnReloadConfig);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(_startupItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, OnExit);

        _currentIcon = TrayIconBuilder.Build(bridged: false);
        _trayIcon = new NotifyIcon
        {
            Icon             = _currentIcon,
            Text             = AppName,
            ContextMenuStrip = contextMenu,
            Visible          = true
        };

        // Left-click toggles the status popup
        _trayIcon.MouseClick += OnTrayIconClick;

        _monitor.SwitchApplied += OnSwitchApplied;
        _config.ConfigReloaded += (_, _) => RebuildOverrideMenu();

        // Pre-populate the popup with the current network state so it has content on the
        // very first click, without waiting for PowerShell commands to complete.
        // This runs on the constructor thread (before Application.Run), which is correct —
        // the WinForms sync context is not set up yet, so posting via _uiContext is unreliable
        // at this stage.  The popup fields are plain strings; the first paint reads them when
        // ShowNearTray() is called after the message loop starts.
        try
        {
            var initial = AdapterMatcher.Evaluate(_config.Current);
            _popup.Update(initial);
        }
        catch { /* non-fatal; full evaluation follows once the monitor starts */ }
    }

    // ── Network switch events ─────────────────────────────────────────────────

    private void OnSwitchApplied(object? sender, MatchResult result)
    {
        _uiContext.Post(_ => ApplyStatusUpdate(result), null);

        // Fetch VM IPs in background — VM needs a moment after switch to get a DHCP lease
        _ = RefreshVmIpAsync(result.TargetVms, delayMs: 3000);
    }

    private void ApplyStatusUpdate(MatchResult result)
    {
        _popup.Update(result);

        _trayIcon.Text = $"{AppName}: {result.VirtualSwitch}";

        var bridged = result.VirtualSwitch != _config.Current.Fallback.VirtualSwitch;
        var oldIcon  = _currentIcon;
        _currentIcon     = TrayIconBuilder.Build(bridged);
        _trayIcon.Icon   = _currentIcon;
        oldIcon?.Dispose();

        var summary = $"{string.Join(", ", result.TargetVms)} → {result.VirtualSwitch}  ({result.RuleName})";
        _trayIcon.BalloonTipTitle = AppName;
        _trayIcon.BalloonTipText  = summary;
        _trayIcon.ShowBalloonTip(4000);
    }

    private async Task RefreshVmIpAsync(IReadOnlyList<string> targetVms, int delayMs = 0)
    {
        try
        {
            if (delayMs > 0) await Task.Delay(delayMs);

            var parts = new List<string>();
            foreach (var vm in targetVms)
            {
                var ips = await _hyperV.GetVmIpAddressesAsync(vm);
                parts.Add(ips.Length > 0
                    ? string.Join(", ", ips)
                    : "no IP");
            }
            _uiContext.Post(_ => _popup.SetVmIp(string.Join(" | ", parts)), null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VM IP refresh failed");
            _uiContext.Post(_ => _popup.SetVmIp("—"), null);
        }
    }

    // ── Menu helpers ──────────────────────────────────────────────────────────

    private void RebuildOverrideMenu()
    {
        _overrideMenu.DropDownItems.Clear();
        foreach (var vm in _config.Current.VirtualMachines)
        {
            var switches = new HashSet<string> { _config.Current.Fallback.VirtualSwitch };
            foreach (var rule in _config.Current.Rules) switches.Add(rule.VirtualSwitch);

            foreach (var sw in switches.Order())
            {
                var vmCapture = vm.Name;
                var swCapture = sw;
                _overrideMenu.DropDownItems.Add(new ToolStripMenuItem(
                    $"{vm.Name} → {sw}", null,
                    async (_, _) => await _monitor.ManualOverrideAsync(vmCapture, swCapture)));
            }
        }
    }

    // ── Menu item handlers ────────────────────────────────────────────────────

    private async void OnAddCurrentAsBridged(object? sender, EventArgs e)
    {
        var info = AdapterMatcher.GetCurrentNetworkInfo();
        if (info is null)
        {
            MessageBox.Show(
                "No active network adapter with an IPv4 address was found.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var normNew   = AdapterMatcher.NormalizeMac(info.Mac);
        var duplicate = _config.Current.Rules.FirstOrDefault(r =>
            r.Conditions.AdapterMac is not null &&
            AdapterMatcher.NormalizeMac(r.Conditions.AdapterMac) == normNew);

        if (duplicate is not null)
        {
            MessageBox.Show(
                $"This adapter is already covered by rule \"{duplicate.Name}\".\n\nEdit config.json to update it.",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var fallbackSwitch = _config.Current.Fallback.VirtualSwitch;
        var bridgedSwitch  = _config.Current.Rules
            .Select(r => r.VirtualSwitch)
            .Where(s => s != fallbackSwitch)
            .OrderBy(s => s.Contains("bridge", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault() ?? "Bridged";

        var confirm = MessageBox.Show(
            $"Add the following rule to config.json?\n\n" +
            $"  Adapter :  {info.AdapterDescription}\n" +
            $"  MAC     :  {info.Mac}\n" +
            $"  Network :  {info.IpCidr}\n" +
            $"  Switch  :  {bridgedSwitch}",
            "Add Current Network as Bridged",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        var rule = new NetworkRule
        {
            Name          = info.AdapterDescription.Length > 40
                                ? info.AdapterDescription[..40].TrimEnd()
                                : info.AdapterDescription,
            Priority      = _config.Current.Rules.Count > 0
                                ? _config.Current.Rules.Max(r => r.Priority) + 10
                                : 10,
            Conditions    = new RuleConditions { AdapterMac = info.Mac, IpCidr = info.IpCidr },
            VirtualSwitch = bridgedSwitch,
            TargetVms     = _config.Current.Fallback.TargetVms.ToList()
        };

        try
        {
            _config.AddBridgedRule(rule);
            await _monitor.ForceEvaluateAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save rule:\n{ex.Message}",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnTrayIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_popup.Visible) _popup.Hide();
        else                _popup.ShowNearTray();
    }

    private void OnOpenLogFile(object? sender, EventArgs e)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HyperVNetworkSwitcher", "switcher.log");

        if (!File.Exists(logPath))
        {
            MessageBox.Show($"No log file found yet.\n\n{logPath}",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(logPath) { UseShellExecute = true });
        }
        catch
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{logPath}\"");
        }
    }

    private void OnOpenConfig(object? sender, EventArgs e)
    {
        var path = ConfigManager.GetConfigPath();
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
    }

    private void OnReloadConfig(object? sender, EventArgs e)
    {
        _config.Load();
        _trayIcon.BalloonTipTitle = AppName;
        _trayIcon.BalloonTipText  = "Config reloaded.";
        _trayIcon.ShowBalloonTip(2000);
    }

    // Auto-start is implemented as a Scheduled Task with "Run with highest privileges"
    // (/RL HIGHEST) and a logon trigger.  A plain HKCU\Run entry cannot launch this app at
    // logon because it requires elevation, and Windows starts Run-key items with a standard
    // token — silently skipping apps that demand administrator rights.  The task runs in the
    // user's interactive session, so the tray icon still appears, with no UAC prompt.
    private void OnToggleStartup(object? sender, EventArgs e)
    {
        try
        {
            if (IsStartupEnabled())
            {
                var (ok, output) = RunSchtasks("/Delete", "/TN", TaskName, "/F");
                if (!ok) throw new InvalidOperationException(output);
                _startupItem.Checked = false;
            }
            else
            {
                var exe = Environment.ProcessPath
                          ?? throw new InvalidOperationException("Cannot determine executable path.");
                var (ok, output) = RunSchtasks(
                    "/Create", "/TN", TaskName,
                    "/TR", $"\"{exe}\"",
                    "/SC", "ONLOGON",
                    "/RL", "HIGHEST",
                    "/F");
                if (!ok) throw new InvalidOperationException(output);
                _startupItem.Checked = true;
            }

            RemoveLegacyRunKey();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to toggle startup scheduled task");
            MessageBox.Show($"Could not change the startup setting:\n\n{ex.Message}",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>True if the auto-start scheduled task exists (schtasks /Query exits 0).</summary>
    private static bool IsStartupEnabled()
    {
        var (ok, _) = RunSchtasks("/Query", "/TN", TaskName);
        return ok;
    }

    /// <summary>Runs schtasks.exe with the given arguments and returns (success, trimmed output).</summary>
    private static (bool ok, string output) RunSchtasks(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "schtasks.exe",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);  // ArgumentList handles quoting

        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return (false, "Failed to start schtasks.exe");

        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(10_000);

        bool ok = p.HasExited && p.ExitCode == 0;
        return (ok, ok ? stdout.Trim()
                       : (stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim()));
    }

    /// <summary>Removes the obsolete HKCU\Run value written by older versions, if present.</summary>
    private static void RemoveLegacyRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegKey, writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _trayIcon.Visible = false;
        Application.Exit();
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _popup.Dispose();
            _trayIcon.Dispose();
            _currentIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

}
