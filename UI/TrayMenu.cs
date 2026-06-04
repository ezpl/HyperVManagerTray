using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using HyperVNetworkSwitcher.Helpers;
using HyperVNetworkSwitcher.Models;
using HyperVNetworkSwitcher.Services;

namespace HyperVNetworkSwitcher.UI;

/// <summary>
/// The tray icon's right-click context menu.
///
/// H.NotifyIcon builds a native Win32 popup menu from <see cref="Flyout"/> on every right-click
/// and invokes each item's <c>Command</c> (the XAML <c>Click</c>/<c>Opening</c> events do NOT
/// fire for the native menu).  Items are created once with command bindings; <see cref="RefreshState"/>
/// resyncs the dynamic parts (override list, startup check) right before the menu opens.
/// </summary>
internal sealed class TrayMenu
{
    private readonly ConfigManager  _config;
    private readonly NetworkMonitor _monitor;
    private readonly HyperVManager  _hyperV;
    private readonly StartupManager _startup;

    private readonly MenuFlyoutSubItem  _overrideMenu = new() { Text = "Manual Override" };
    private readonly MenuFlyoutSubItem  _vmPowerMenu  = new() { Text = "VM Power" };
    private readonly ToggleMenuFlyoutItem _startupItem = new() { Text = "Run on startup" };

    public MenuFlyout Flyout { get; }

    public TrayMenu(ConfigManager config, NetworkMonitor monitor, HyperVManager hyperV,
                    StartupManager startup, Action onExit)
    {
        _config  = config;
        _monitor = monitor;
        _hyperV  = hyperV;
        _startup = startup;

        _startupItem.Command = new RelayCommand(ToggleStartup);

        Flyout = new MenuFlyout();
        Add("Force Re-evaluate", () => _ = _monitor.ForceEvaluateAsync());
        Flyout.Items.Add(_vmPowerMenu);
        Flyout.Items.Add(_overrideMenu);
        Add("Add current network as bridged", AddCurrentAsBridged);
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Add("Open config.json", () => OpenPath(ConfigManager.GetConfigPath()));
        Add("Open log file",    () => OpenPath(LogPath()));
        Add("Reload config",    () => _config.Load());
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Flyout.Items.Add(_startupItem);
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Add("Exit", onExit);

        RefreshState();
    }

    /// <summary>Re-reads live state into the dynamic menu parts. Call right before the menu opens.</summary>
    public void RefreshState()
    {
        RebuildOverrideMenu();
        RebuildVmPowerMenu();
        _startupItem.IsChecked = SafeStartupEnabled();
    }

    private void RebuildVmPowerMenu()
    {
        _vmPowerMenu.Items.Clear();
        foreach (var vm in _config.Current.VirtualMachines)
        {
            var name = vm.Name;
            var nic  = vm.NicName;
            var sub  = new MenuFlyoutSubItem { Text = vm.Name };
            sub.Items.Add(Item("Start",           () => _hyperV.StartOrResumeVmAsync(name)));
            sub.Items.Add(Item("Start && Connect", () => StartAndConnect(name, nic)));
            sub.Items.Add(Item("Shutdown",        () => _hyperV.ShutdownVmAsync(name)));
            sub.Items.Add(Item("Pause",           () => _hyperV.SuspendVmAsync(name)));
            sub.Items.Add(Item("Resume",          () => _hyperV.ResumeVmAsync(name)));
            sub.Items.Add(Item("Save",            () => _hyperV.SaveVmAsync(name)));
            _vmPowerMenu.Items.Add(sub);
        }
        if (_vmPowerMenu.Items.Count == 0)
            _vmPowerMenu.Items.Add(new MenuFlyoutItem { Text = "(no VMs in config)", IsEnabled = false });
    }

    private MenuFlyoutItem Item(string text, Func<Task> action)
        => new() { Text = text, Command = new RelayCommand(() => _ = action()) };

    private async Task StartAndConnect(string vmName, string nicName)
    {
        await _hyperV.StartOrResumeVmAsync(vmName);
        await Task.Delay(2500);
        var sw = _monitor.LastApplied?.VirtualSwitch;
        if (!string.IsNullOrEmpty(sw)) await _hyperV.ApplySwitchAsync(vmName, nicName, sw);
    }

    // ── Dynamic submenus ────────────────────────────────────────────────────────

    private void RebuildOverrideMenu()
    {
        _overrideMenu.Items.Clear();
        foreach (var vm in _config.Current.VirtualMachines)
        {
            var switches = new HashSet<string> { _config.Current.Fallback.VirtualSwitch };
            foreach (var rule in _config.Current.Rules) switches.Add(rule.VirtualSwitch);

            foreach (var sw in switches.Order())
            {
                var vmName = vm.Name;
                var swName = sw;
                _overrideMenu.Items.Add(new MenuFlyoutItem
                {
                    Text    = $"{vm.Name} → {sw}",
                    Command = new RelayCommand(() => _ = _monitor.ManualOverrideAsync(vmName, swName)),
                });
            }
        }
    }

    // ── Actions ─────────────────────────────────────────────────────────────────

    private void AddCurrentAsBridged()
    {
        var info = AdapterMatcher.GetCurrentNetworkInfo();
        if (info is null)
        {
            NativeMethods.Warn("No active network adapter with an IPv4 address was found.", AppName);
            return;
        }

        var normNew   = AdapterMatcher.NormalizeMac(info.Mac);
        var duplicate = _config.Current.Rules.FirstOrDefault(r =>
            r.Conditions.AdapterMac is not null &&
            AdapterMatcher.NormalizeMac(r.Conditions.AdapterMac) == normNew);
        if (duplicate is not null)
        {
            NativeMethods.Info($"This adapter is already covered by rule \"{duplicate.Name}\".\n\nEdit config.json to update it.", AppName);
            return;
        }

        var fallbackSwitch = _config.Current.Fallback.VirtualSwitch;
        var bridgedSwitch  = _config.Current.Rules
            .Select(r => r.VirtualSwitch)
            .Where(s => s != fallbackSwitch)
            .OrderBy(s => s.Contains("bridge", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault() ?? "Bridged";

        if (!NativeMethods.Confirm(
                $"Add the following rule to config.json?\n\n" +
                $"  Adapter :  {info.AdapterDescription}\n" +
                $"  MAC     :  {info.Mac}\n" +
                $"  Network :  {info.IpCidr}\n" +
                $"  Switch  :  {bridgedSwitch}",
                "Add Current Network as Bridged"))
            return;

        var rule = new NetworkRule
        {
            Name          = info.AdapterDescription.Length > 40 ? info.AdapterDescription[..40].TrimEnd() : info.AdapterDescription,
            Priority      = _config.Current.Rules.Count > 0 ? _config.Current.Rules.Max(r => r.Priority) + 10 : 10,
            Conditions    = new RuleConditions { AdapterMac = info.Mac, IpCidr = info.IpCidr },
            VirtualSwitch = bridgedSwitch,
            TargetVms     = _config.Current.Fallback.TargetVms.ToList(),
        };

        try
        {
            _config.AddBridgedRule(rule);
            _ = _monitor.ForceEvaluateAsync();
        }
        catch (Exception ex)
        {
            NativeMethods.Error($"Failed to save rule:\n{ex.Message}", AppName);
        }
    }

    private void ToggleStartup()
    {
        try
        {
            if (_startup.IsEnabled) _startup.Disable();
            else _startup.Enable(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path."));
        }
        catch (Exception ex)
        {
            NativeMethods.Warn($"Could not change the startup setting:\n\n{ex.Message}", AppName);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private const string AppName = "HyperV Network Switcher";

    private void Add(string text, Action action)
        => Flyout.Items.Add(new MenuFlyoutItem { Text = text, Command = new RelayCommand(action) });

    private bool SafeStartupEnabled()
    {
        try { return _startup.IsEnabled; } catch { return false; }
    }

    private static string LogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HyperVNetworkSwitcher", "switcher.log");

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { /* ignore */ } }
    }
}
