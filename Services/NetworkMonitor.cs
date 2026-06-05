using System.Net.NetworkInformation;
using HyperVManagerTray.Models;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

/// <summary>
/// Watches the host's network state (via <see cref="NetworkChange"/>), re-evaluates the
/// config rules on each change (debounced), and drives <see cref="HyperVManager"/> to bind
/// the virtual switch and reconnect the VMs.  Redundant switch changes are skipped.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private readonly ConfigManager _config;
    private readonly HyperVManager _hyperV;
    private readonly ILogger<NetworkMonitor> _logger;
    private readonly System.Threading.Timer _debounceTimer;
    // Single-flight guard: only one evaluate/apply runs at a time.  '_evaluatePending'
    // coalesces changes that arrive while one is running into exactly one follow-up pass.
    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private volatile bool _evaluatePending;
    private MatchResult? _lastApplied;
    // Tracks which physical adapter name was last passed to Set-VMSwitch so we can skip
    // redundant re-binds (which cause a brief VM network drop) when nothing has changed.
    private string? _lastBoundAdapterInterface;

    /// <summary>Raised after a switch change has been applied (used to update the tray UI).</summary>
    public event EventHandler<MatchResult>? SwitchApplied;

    /// <summary>The most recently applied match result, or null if nothing applied yet.</summary>
    public MatchResult? LastApplied => _lastApplied;

    public NetworkMonitor(ConfigManager config, HyperVManager hyperV, ILogger<NetworkMonitor> logger)
    {
        _config = config;
        _hyperV = hyperV;
        _logger = logger;
        _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _config.ConfigReloaded += (_, _) => Schedule();
    }

    /// <summary>Triggers an immediate first evaluation (called once at startup).</summary>
    public void Start() => Schedule();

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        _debounceTimer.Change(1500, Timeout.Infinite);

    private void Schedule() =>
        _debounceTimer.Change(0, Timeout.Infinite);

    private async void OnDebounceElapsed(object? _)
    {
        // If an evaluation is already running, just flag that another is needed and bail —
        // the in-flight pass will pick it up.  This stops overlapping timer callbacks from
        // applying switch changes concurrently.  Crucially, a rebind briefly drops the host's
        // bridged vNIC and fires its own NetworkChange events; coalescing them into one
        // follow-up pass (run after the rebind settles) prevents the VM flip-flopping
        // Bridged → Fallback → Bridged mid-operation.
        if (!await _evalLock.WaitAsync(0))
        {
            _evaluatePending = true;
            return;
        }

        try
        {
            do
            {
                _evaluatePending = false;

                var result = AdapterMatcher.Evaluate(_config.Current);
                _logger.LogInformation("Evaluated: rule='{Rule}' switch='{Switch}'", result.RuleName, result.VirtualSwitch);

                bool unchanged = _lastApplied?.VirtualSwitch == result.VirtualSwitch &&
                                 _lastApplied?.TargetVms.SequenceEqual(result.TargetVms) == true;
                if (unchanged)
                    _logger.LogDebug("No switch change needed");
                else
                    await ApplyAsync(result);
            }
            while (_evaluatePending);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during network evaluation");
        }
        finally
        {
            _evalLock.Release();
        }
    }

    /// <summary>Re-evaluates rules and applies the result immediately, bypassing the "no change" check.</summary>
    public async Task ForceEvaluateAsync()
    {
        var result = AdapterMatcher.Evaluate(_config.Current);
        await ApplyAsync(result);
    }

    /// <summary>Forces a specific VM onto a specific switch, ignoring rules (used by the tray override menu).</summary>
    public async Task ManualOverrideAsync(string vmName, string switchName)
    {
        _logger.LogInformation("Manual override: {Vm} → {Switch}", vmName, switchName);
        var vm = _config.Current.VirtualMachines.FirstOrDefault(v => v.Name == vmName);
        if (vm is null) return;

        await _hyperV.ApplySwitchAsync(vmName, vm.NicName, switchName);

        // Manual override bypasses the binding logic; force a re-bind next time a rule fires.
        _lastBoundAdapterInterface = null;

        var result = new MatchResult($"Manual ({switchName})", switchName, [vmName]);
        _lastApplied = result;
        SwitchApplied?.Invoke(this, result);
    }

    private async Task ApplyAsync(MatchResult result)
    {
        // Capture the previously-active rule so autostart fires only on a rule *transition*,
        // not on every (debounced) re-evaluation of the same network.
        var previousRule = _lastApplied?.RuleName;

        // When a specific rule matched, re-bind the Hyper-V virtual switch to the detected
        // physical adapter before connecting any VMs.  This is what makes an "Internal"
        // switch become an "External" (bridged) switch pointing at the right LAN NIC.
        // Skip for fallback — the fallback switch (Default Switch / NAT) needs no binding.
        //
        // Only call Set-VMSwitch when the physical adapter actually changed: repeated calls
        // with the same adapter cause a brief VM network drop even if nothing changed.
        // Reset _lastBoundAdapterInterface when falling back so the next rule-match
        // always re-binds (the switch may have been left on a different adapter).
        if (result.RuleName == "Fallback")
        {
            _lastBoundAdapterInterface = null;
        }
        else if (result.HostAdapterInterfaceName != "—" &&
                 result.HostAdapterInterfaceName != _lastBoundAdapterInterface)
        {
            await _hyperV.UpdateSwitchBindingAsync(result.VirtualSwitch, result.HostAdapterInterfaceName);
            _lastBoundAdapterInterface = result.HostAdapterInterfaceName;
        }

        foreach (var vmName in result.TargetVms)
        {
            var vm = _config.Current.VirtualMachines.FirstOrDefault(v => v.Name == vmName);
            if (vm is null)
            {
                _logger.LogWarning("VM '{Vm}' not found in config", vmName);
                continue;
            }
            await _hyperV.ApplySwitchAsync(vmName, vm.NicName, result.VirtualSwitch);
        }

        // Per-network autostart: when this rule has just become active and opts in, start (or
        // resume) its target VMs.  Never auto-stop on leaving — by design.
        if (result.RuleName != previousRule && result.RuleName != "Fallback")
        {
            var rule = _config.Current.Rules.FirstOrDefault(r => r.Name == result.RuleName);
            if (rule?.AutoStart == true)
            {
                foreach (var vmName in rule.TargetVms)
                {
                    _logger.LogInformation("Autostart: starting/resuming {Vm} for rule '{Rule}'", vmName, rule.Name);
                    _ = _hyperV.StartOrResumeVmAsync(vmName);
                }
            }
        }

        _lastApplied = result;
        SwitchApplied?.Invoke(this, result);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _debounceTimer.Dispose();
        _evalLock.Dispose();
    }
}
