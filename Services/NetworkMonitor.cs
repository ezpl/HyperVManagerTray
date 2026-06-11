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

    // Per-VM cancellation tokens for the bridge-lost delay timers.
    // Protected by _disconnectLock (not _evalLock) so Dispose() can safely cancel pending
    // actions while an evaluation is still in flight without deadlocking on the semaphore.
    private readonly object _disconnectLock = new();
    private readonly Dictionary<string, CancellationTokenSource> _pendingDisconnect = new();

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
        // Guard against ObjectDisposedException when the timer fires after Dispose() is called
        // (e.g. rapid dock disconnect followed by app exit).  In async void this would become
        // an unhandled exception and crash the process via AppDomain.UnhandledException.
        bool acquired;
        try { acquired = await _evalLock.WaitAsync(0); }
        catch (ObjectDisposedException) { return; }

        // If an evaluation is already running, just flag that another is needed and bail —
        // the in-flight pass will pick it up.  This stops overlapping timer callbacks from
        // applying switch changes concurrently.  Crucially, a rebind briefly drops the host's
        // bridged vNIC and fires its own NetworkChange events; coalescing them into one
        // follow-up pass (run after the rebind settles) prevents the VM flip-flopping
        // Bridged → Fallback → Bridged mid-operation.
        if (!acquired)
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

                bool switchUnchanged = _lastApplied?.VirtualSwitch == result.VirtualSwitch &&
                                       _lastApplied?.TargetVms.SequenceEqual(result.TargetVms) == true;
                if (switchUnchanged)
                {
                    // Same Hyper-V switch — skip rebind to avoid a VM network drop. But the
                    // host adapter/IP/gateway may have changed (e.g. two different mobile
                    // networks both resolving to Fallback), so still update _lastApplied and
                    // fire SwitchApplied so the dashboard reflects the current network.
                    // Also run bridge-transition detection: an adapter change that keeps the
                    // same switch might cross a bridge-lost or bridge-restored boundary and
                    // needs to schedule or cancel the per-VM disconnect actions.
                    _logger.LogDebug("No switch change needed");
                    HandleBridgeTransition(_lastApplied?.RuleName, result);
                    _lastApplied = result;
                    SwitchApplied?.Invoke(this, result);
                }
                else
                {
                    await ApplyAsync(result);
                }
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
        bool acquired;
        try { acquired = await _evalLock.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { return; }
        if (!acquired) return;

        try
        {
            var result = AdapterMatcher.Evaluate(_config.Current);
            await ApplyAsync(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during forced evaluation");
        }
        finally
        {
            _evalLock.Release();
        }
    }

    /// <summary>Forces a specific VM onto a specific switch, ignoring rules (used by the tray override menu).</summary>
    public async Task ManualOverrideAsync(string vmName, string switchName)
    {
        _logger.LogInformation("Manual override: {Vm} → {Switch}", vmName, switchName);
        if (_config.Current.VirtualMachines.FirstOrDefault(v => v.Name == vmName) is not { } vm) return;

        await _hyperV.ApplySwitchAsync(vmName, vm.NicName, switchName);

        // Manual override bypasses the binding logic; force a re-bind next time a rule fires.
        _lastBoundAdapterInterface = null;

        var result = new MatchResult($"Manual ({switchName})", switchName, [vmName]);
        _lastApplied = result;
        SwitchApplied?.Invoke(this, result);
    }

    private async Task ApplyAsync(MatchResult result)
    {
        // Capture the previously-active rule before any state changes so autostart and
        // bridge-transition detection both see a consistent before/after snapshot.
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

        HandleBridgeTransition(previousRule, result);

        _lastApplied = result;
        SwitchApplied?.Invoke(this, result);
    }

    // ── Bridge-lost / bridge-restored transition ────────────────────────────────

    // Called from both the switchUnchanged fast path and ApplyAsync (full path) so that
    // disconnect actions are scheduled or cancelled regardless of whether the Hyper-V
    // switch binding itself needed to change.
    private void HandleBridgeTransition(string? previousRule, MatchResult result)
    {
        // previousRule == null means first evaluation at startup — never trigger on startup
        // even if the initial result is Fallback.
        bool bridgeJustLost     = previousRule != null
                               && previousRule != "Fallback"
                               && result.RuleName == "Fallback";
        bool bridgeJustRestored = previousRule == "Fallback"
                               && result.RuleName != "Fallback";

        if (bridgeJustLost)
            ScheduleDisconnectActions();
        else if (bridgeJustRestored)
            CancelDisconnectActions();
    }

    // ── Bridge-lost delayed actions ─────────────────────────────────────────────

    private void ScheduleDisconnectActions()
    {
        lock (_disconnectLock)
        {
            foreach (var vm in _config.Current.VirtualMachines)
            {
                var action = vm.OnBridgeLostAction;
                if (string.IsNullOrEmpty(action) || action == "none") continue;

                // Cancel any existing timer for this VM (bridge may have flapped).
                if (_pendingDisconnect.TryGetValue(vm.Name, out var existing))
                {
                    existing.Cancel();
                    existing.Dispose();
                }

                var cts      = new CancellationTokenSource();
                var vmName   = vm.Name;
                var delaySec = vm.OnBridgeLostDelaySeconds > 0 ? vm.OnBridgeLostDelaySeconds : 30;
                _pendingDisconnect[vmName] = cts;

                _logger.LogInformation(
                    "Bridge lost — scheduling '{Action}' for {Vm} in {Delay}s",
                    action, vmName, delaySec);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(delaySec), cts.Token);

                        _logger.LogInformation(
                            "Bridge-lost action: {Action} {Vm} (bridge absent for {Delay}s)",
                            action, vmName, delaySec);

                        await (action switch
                        {
                            "pause"    => _hyperV.SuspendVmAsync(vmName),
                            "save"     => _hyperV.SaveVmAsync(vmName),
                            "shutdown" => _hyperV.ShutdownVmAsync(vmName),
                            _          => Task.CompletedTask,
                        });
                    }
                    catch (OperationCanceledException) { /* bridge restored — expected */ }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Bridge-lost action failed for {Vm}", vmName);
                    }
                    finally
                    {
                        // Remove the completed entry so the dictionary doesn't accumulate
                        // stale CTS objects after actions have already fired.
                        lock (_disconnectLock)
                        {
                            if (_pendingDisconnect.TryGetValue(vmName, out var current) &&
                                ReferenceEquals(current, cts))
                                _pendingDisconnect.Remove(vmName);
                        }
                    }
                }, CancellationToken.None);
            }
        }
    }

    private void CancelDisconnectActions()
    {
        lock (_disconnectLock)
        {
            foreach (var kv in _pendingDisconnect)
            {
                _logger.LogInformation("Bridge restored — cancelling pending action for {Vm}", kv.Key);
                kv.Value.Cancel();
                kv.Value.Dispose();
            }
            _pendingDisconnect.Clear();
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _debounceTimer.Dispose();
        CancelDisconnectActions();
        _evalLock.Dispose();
    }
}
