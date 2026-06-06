using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;

namespace HyperVManagerTray.UI;

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
    private readonly UpdateChecker  _updateChecker;

    private readonly MenuFlyoutSubItem  _overrideMenu = new() { Text = "VM Network Override" };
    private readonly MenuFlyoutSubItem  _vmPowerMenu  = new() { Text = "VM Power" };
    private readonly ToggleMenuFlyoutItem _startupItem = new() { Text = "Run on startup" };

    public MenuFlyout Flyout { get; }

    public TrayMenu(ConfigManager config, NetworkMonitor monitor, HyperVManager hyperV,
                    StartupManager startup, UpdateChecker updateChecker, Action onExit)
    {
        _config        = config;
        _monitor       = monitor;
        _hyperV        = hyperV;
        _startup       = startup;
        _updateChecker = updateChecker;

        _startupItem.Command = new RelayCommand(ToggleStartup);

        Flyout = new MenuFlyout();

        Flyout.Items.Add(_vmPowerMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        var vmNetworkMenu = new MenuFlyoutSubItem { Text = "VM Network" };
        vmNetworkMenu.Items.Add(new MenuFlyoutItem { Text = "Force Re-evaluate", Command = new RelayCommand(() => _ = _monitor.ForceEvaluateAsync()) });
        vmNetworkMenu.Items.Add(new MenuFlyoutSeparator());
        vmNetworkMenu.Items.Add(_overrideMenu);
        vmNetworkMenu.Items.Add(new MenuFlyoutSeparator());
        vmNetworkMenu.Items.Add(new MenuFlyoutItem { Text = "Add current network as bridged", Command = new RelayCommand(AddCurrentAsBridged) });
        Flyout.Items.Add(vmNetworkMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());

        var settingsMenu = new MenuFlyoutSubItem { Text = "Settings" };
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Open config.json", Command = new RelayCommand(() => OpenPath(ConfigManager.GetConfigPath())) });
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Open log file",    Command = new RelayCommand(() => OpenPath(LogPath())) });
        settingsMenu.Items.Add(new MenuFlyoutSeparator());
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Reload config",    Command = new RelayCommand(() => _config.Load()) });
        settingsMenu.Items.Add(new MenuFlyoutSeparator());
        settingsMenu.Items.Add(_startupItem);
        settingsMenu.Items.Add(new MenuFlyoutSeparator());
        settingsMenu.Items.Add(new MenuFlyoutItem { Text = "Check for updates", Command = new RelayCommand(() => _ = CheckForUpdatesAsync()) });
        Flyout.Items.Add(settingsMenu);
        Flyout.Items.Add(new MenuFlyoutSeparator());
        Add("About…", ShowAbout);
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

        // Managed VMs (in config) — full power submenu
        var configNames = new HashSet<string>(_config.Current.VirtualMachines
            .Select(v => v.Name), StringComparer.OrdinalIgnoreCase);

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
            sub.Items.Add(new MenuFlyoutSeparator());
            sub.Items.Add(Item("Remove from config…", () =>
            {
                if (NativeMethods.Confirm(
                        $"Remove {name} from config.json?\n\nThis only removes the app's management of this VM — it does not delete the VM.",
                        "Remove VM from Config"))
                {
                    _config.RemoveVmFromConfig(name);
                    NativeMethods.Info($"{name} removed from config.", AppName);
                }
                return Task.CompletedTask;
            }));
            _vmPowerMenu.Items.Add(sub);
        }

        // Unmanaged VMs (discovered but not in config) — limited submenu
        List<HyperVManager.DiscoveredVm> discovered;
        try
        {
            // Run on a thread-pool thread so the async continuations inside GetAllVmsAsync
            // don't try to resume on the (blocked) UI dispatcher — avoids deadlock.
            discovered = Task.Run(() => _hyperV.GetAllVmsAsync()).GetAwaiter().GetResult();
        }
        catch
        {
            discovered = [];
        }

        var unmanaged = discovered
            .Where(d => !configNames.Contains(d.Name))
            .OrderBy(d => d.Name)
            .ToList();

        if (unmanaged.Count > 0 && _vmPowerMenu.Items.Count > 0)
            _vmPowerMenu.Items.Add(new MenuFlyoutSeparator());

        foreach (var vm in unmanaged)
        {
            var name   = vm.Name;
            var nicName = vm.NicName;
            var sub    = new MenuFlyoutSubItem { Text = $"{name} (unmanaged)" };
            sub.Items.Add(Item("Start",    () => _hyperV.StartOrResumeVmAsync(name)));
            sub.Items.Add(Item("Shutdown", () => _hyperV.ShutdownVmAsync(name)));
            sub.Items.Add(Item("Connect",  () => { ConnectUnmanaged(name); return Task.CompletedTask; }));
            sub.Items.Add(new MenuFlyoutSeparator());
            sub.Items.Add(Item("Add to config…", () =>
            {
                try
                {
                    _config.AddVmToConfig(name, nicName);
                    NativeMethods.Info(
                        $"Added \"{name}\" to config.\nReload to manage it fully.",
                        AppName);
                }
                catch (Exception ex)
                {
                    NativeMethods.Error($"Failed to add VM to config:\n{ex.Message}", AppName);
                }
                return Task.CompletedTask;
            }));
            _vmPowerMenu.Items.Add(sub);
        }

        if (_vmPowerMenu.Items.Count == 0)
            _vmPowerMenu.Items.Add(new MenuFlyoutItem { Text = "(no VMs found)", IsEnabled = false });
    }

    private static void ConnectUnmanaged(string vmName)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("vmconnect.exe", $"localhost \"{vmName}\"")
                { UseShellExecute = true });
        }
        catch
        {
            NativeMethods.Warn(
                "Could not open VM Connection.\n\nEnsure Hyper-V Manager tools are installed.",
                AppName);
        }
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

        var switches = new HashSet<string> { _config.Current.Fallback.VirtualSwitch };
        foreach (var rule in _config.Current.Rules) switches.Add(rule.VirtualSwitch);
        var orderedSwitches = switches.Order().ToList();

        foreach (var vm in _config.Current.VirtualMachines)
        {
            foreach (var sw in orderedSwitches)
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

    private async Task CheckForUpdatesAsync()
    {
        var result  = await _updateChecker.CheckAsync().ConfigureAwait(false);
        var running = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        if (result.UpdateAvailable)
        {
            bool canDownload = !string.IsNullOrEmpty(result.InstallerUrl);

            // Win32 TaskDialog — 3 custom buttons (Update / Releases page / Cancel) +
            // expandable release notes section.  Blocks until the user responds; safe
            // to call from any thread without needing a WinUI XamlRoot.
            var action = NativeMethods.ShowUpdateDialog(
                result.LatestVersion, running,
                result.ReleaseNotes,  AppName,
                canDownload);

            switch (action)
            {
                case NativeMethods.UpdateAction.Update:
                    // Download in background; Inno Setup's CloseApplications=yes restarts us.
                    NativeMethods.Info(
                        $"Downloading v{result.LatestVersion}...\n\nThe installer will launch automatically when ready.",
                        AppName);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var path = await _updateChecker
                                .DownloadInstallerAsync(result.InstallerUrl)
                                .ConfigureAwait(false);
                            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                        }
                        catch (Exception ex)
                        {
                            NativeMethods.Warn(
                                $"Download failed:\n{ex.Message}\n\nTry updating from the releases page.",
                                AppName);
                            if (!string.IsNullOrEmpty(result.ReleasePageUrl))
                                Process.Start(new ProcessStartInfo(result.ReleasePageUrl) { UseShellExecute = true });
                        }
                    });
                    break;

                case NativeMethods.UpdateAction.ShowReleases:
                    if (!string.IsNullOrEmpty(result.ReleasePageUrl))
                        Process.Start(new ProcessStartInfo(result.ReleasePageUrl) { UseShellExecute = true });
                    break;

                // Cancel — do nothing
            }
        }
        else if (result.LatestVersion == "none")
        {
            NativeMethods.Info("No releases have been published yet.", AppName);
        }
        else if (!string.IsNullOrEmpty(result.LatestVersion))
        {
            NativeMethods.Info($"You're on the latest version ({running}).", AppName);
        }
        else
        {
            NativeMethods.Warn("Could not check for updates. Check your internet connection.", AppName);
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

    private const string AppName   = "Hyper-V Manager Tray";
    private const string Publisher  = "Zero Zero Software";
    private const string RepoUrl    = "https://github.com/ezpl/HyperVManagerTray";

    private static void ShowAbout()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
                          .GetName().Version?.ToString(3) ?? "—";
        bool openGitHub = NativeMethods.Confirm(
            $"{AppName}\nVersion {version}\n\n" +
            $"Publisher:  {Publisher}\n" +
            $"License:    MIT\n\n" +
            $"Open the GitHub page?",
            $"About {AppName}");
        if (openGitHub)
            Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
    }

    private void Add(string text, Action action)
        => Flyout.Items.Add(new MenuFlyoutItem { Text = text, Command = new RelayCommand(action) });

    private bool SafeStartupEnabled()
    {
        try { return _startup.IsEnabled; } catch { return false; }
    }

    private static string LogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HyperVManagerTray", "switcher.log");

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { /* ignore */ } }
    }
}
