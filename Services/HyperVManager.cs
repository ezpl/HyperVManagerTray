using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>
/// Runs Hyper-V PowerShell cmdlets by spawning powershell.exe as a child process.
/// This avoids the Microsoft.PowerShell.SDK in-process runspace, which fails to
/// initialise in self-contained single-file builds due to a registry lookup
/// (PSSnapInReader) that returns null when the Windows PowerShell engine key is absent.
///
/// powershell.exe (Windows PowerShell 5.1) is always present on Windows 10/11 and
/// supports all required Hyper-V cmdlets.  Commands are passed as a Base64-encoded
/// Unicode string (-EncodedCommand) to sidestep all quoting/escaping concerns.
/// </summary>
public sealed class HyperVManager : IDisposable
{
    private readonly ILogger<HyperVManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);  // serialise concurrent calls

    // ── GetAllVmsAsync cache ──────────────────────────────────────────────────
    private List<DiscoveredVm>? _allVmsCache;
    private DateTime _allVmsCacheUtc = DateTime.MinValue;
    private static readonly TimeSpan AllVmsCacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns the cached VM list without any I/O or lock acquisition — safe to call on the
    /// UI thread.  Returns <c>null</c> when the cache has never been populated (i.e. before
    /// the first <see cref="GetAllVmsAsync"/> call completes).  Returns the list even when
    /// stale; callers should trigger a background <see cref="GetAllVmsAsync"/> for the next use.
    /// </summary>
    public List<DiscoveredVm>? GetCachedVmsSync() => _allVmsCache;

    /// <summary>Represents a VM discovered on the local Hyper-V host (may or may not be in config).</summary>
    public sealed record DiscoveredVm(string Name, string NicName);

    public HyperVManager(ILogger<HyperVManager> logger) => _logger = logger;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects a VM's NIC to the given virtual switch, but only if it isn't already there.
    /// <c>Connect-VMNetworkAdapter</c> re-applies the binding and briefly bounces the VM's
    /// network even when the switch is unchanged, so we check first to avoid a needless blip
    /// (e.g. on every app launch, where the in-session guards start empty).
    /// </summary>
    public async Task ApplySwitchAsync(string vmName, string nicName, string switchName)
    {
        var vm  = Esc(vmName);
        var nic = Esc(nicName);
        var sw  = Esc(switchName);
        var (ok, output) = await RunAsync(
            $"if ((Get-VMNetworkAdapter -VMName '{vm}' -Name '{nic}').SwitchName -eq '{sw}') {{ 'SKIP' }} " +
            $"else {{ Connect-VMNetworkAdapter -VMName '{vm}' -Name '{nic}' -SwitchName '{sw}'; 'CONNECTED' }}");

        if (!ok)                          _logger.LogError("ApplySwitchAsync error: {Error}", output);
        else if (output.Contains("SKIP")) _logger.LogInformation("VM {Vm} already on '{Switch}' — no reconnect", vmName, switchName);
        else                              _logger.LogInformation("Switch applied: {Vm} → {Switch}", vmName, switchName);
    }

    // The bind sequence toggles AllowManagementOS and re-homes the external adapter, which is
    // slow (~25 s observed).  It gets a longer timeout than the default so it is not killed
    // mid-sequence: a kill after '-AllowManagementOS $false' but before '$true' would leave the
    // host adapter with no management vNIC (and therefore no IP) until the next successful bind.
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(120);

    // Save-VM writes all assigned RAM to disk — can take several minutes for large VMs.
    // Stop-VM (graceful) sends a shutdown signal and waits for the guest OS to finish.
    private static readonly TimeSpan SlowVmTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Binds a Hyper-V virtual switch to a physical NIC (makes it External, with the host
    /// sharing the adapter) — but only when it isn't already in that exact state.
    ///
    /// Host sharing is detached (<c>-AllowManagementOS $false</c>) <em>before</em> the external
    /// adapter is changed, then re-enabled.  This forces Windows to remove the previous host
    /// management vNIC instead of leaving it behind: rebinding a shared switch straight to a new
    /// adapter orphans the old <c>vEthernet (&lt;switch&gt;)</c> NIC, and those accumulate across
    /// rebinds — which locks the switch's settings and pollutes host routing with stale default
    /// routes.  The two-step keeps exactly one management vNIC alive.
    ///
    /// That toggle briefly drops the host's vNIC, so it is guarded by a check: if the switch is
    /// already External, sharing with the management OS, and bound to the target adapter, nothing
    /// is done.  This stops the host network flickering on every launch / redundant evaluation.
    /// </summary>
    public async Task UpdateSwitchBindingAsync(string switchName, string adapterName)
    {
        var sw  = Esc(switchName);
        var nic = Esc(adapterName);
        var script =
            $"$want = (Get-NetAdapter -Name '{nic}' -ErrorAction SilentlyContinue).InterfaceDescription; " +
            $"$s = Get-VMSwitch -Name '{sw}' -ErrorAction SilentlyContinue; " +
             "if ($s -and $s.SwitchType -eq 'External' -and $s.AllowManagementOS -and $want -and " +
             "$s.NetAdapterInterfaceDescription -eq $want) { 'SKIP' } else { " +
            $"Set-VMSwitch -Name '{sw}' -AllowManagementOS $false; " +
            $"Set-VMSwitch -Name '{sw}' -NetAdapterName '{nic}' -AllowManagementOS $true; 'BOUND' }}";

        var (ok, output) = await RunAsync(script, BindTimeout);

        if (!ok)                          _logger.LogError("UpdateSwitchBindingAsync error: {Error}", output);
        else if (output.Contains("SKIP")) _logger.LogInformation("Switch '{Switch}' already bound to '{Adapter}' — no rebind", switchName, adapterName);
        else                              _logger.LogInformation("Switch '{Switch}' bound to '{Adapter}'", switchName, adapterName);
    }

    /// <summary>
    /// Queries the first IPv4 address for each of the named VMs in a single PowerShell call.
    /// Returns a dictionary of VM name → first IPv4 address; VMs with no address are omitted.
    /// Never throws — returns an empty dictionary on any error.
    /// </summary>
    public async Task<Dictionary<string, string>> GetVmIpAddressesAsync(IEnumerable<string> vmNames)
    {
        var names = vmNames.ToList();
        if (names.Count == 0) return [];

        _logger.LogDebug("Querying IP addresses for {Count} VM(s)...", names.Count);

        // Build a PowerShell array literal: @('vm1','vm2')
        var quoted = string.Join(",", names.Select(n => $"'{Esc(n)}'"));
        var script =
            $"Get-VM -Name @({quoted}) -ErrorAction SilentlyContinue | " +
             "Get-VMNetworkAdapter | " +
             "Where-Object { $_.IPAddresses.Count -gt 0 } | " +
             "Select-Object @{N='Name';E={$_.VMName}}, " +
             "@{N='IP';E={($_.IPAddresses | Where-Object { $_ -notmatch ':' } | Select-Object -First 1)}} | " +
             "ConvertTo-Json -Compress";

        var (ok, output) = await RunAsync(script);
        if (!ok || string.IsNullOrWhiteSpace(output))
        {
            if (!ok) _logger.LogWarning("GetVmIpAddressesAsync failed: {Error}", output);
            return [];
        }

        try
        {
            // ConvertTo-Json emits a bare object for a single VM — normalise to array.
            var json = output.TrimStart();
            if (!json.StartsWith('[')) json = $"[{json}]";

            var entries = JsonSerializer.Deserialize<List<VmIpEntry>>(json, JsonOpts) ?? [];
            return entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.IP))
                .ToDictionary(e => e.Name, e => e.IP);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetVmIpAddressesAsync: failed to parse JSON output");
            return [];
        }
    }

    private sealed class VmIpEntry { public string Name { get; set; } = ""; public string IP { get; set; } = ""; }

    /// <summary>Returns the IPv4 addresses reported by the VM's network adapter (may be empty).</summary>
    public async Task<string[]> GetVmIpAddressesAsync(string vmName)
    {
        _logger.LogDebug("Querying IP addresses for VM '{VmName}'...", vmName);
        var (ok, output) = await RunAsync(
            $"(Get-VMNetworkAdapter -VMName '{Esc(vmName)}').IPAddresses | " +
            $"Where-Object {{ $_ -match '\\.' }}");

        if (!ok)
        {
            _logger.LogWarning("Failed to query IP addresses for VM '{VmName}': {Error}", vmName, output);
            return [];
        }
        if (string.IsNullOrWhiteSpace(output)) return [];

        return output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.Trim())
                     .Where(l => l.Length > 0)
                     .ToArray();
    }

    // ── All-VM discovery ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all VMs on the local Hyper-V host, regardless of whether they are in config.
    /// Result is cached for 30 seconds to avoid repeated PowerShell startups.
    /// Never throws — returns an empty list on any error.
    /// </summary>
    public async Task<List<DiscoveredVm>> GetAllVmsAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && _allVmsCache is not null
            && DateTime.UtcNow - _allVmsCacheUtc < AllVmsCacheTtl)
        {
            return _allVmsCache;
        }

        _logger.LogDebug("Discovering all Hyper-V VMs on localhost...");

        const string script =
            "$ProgressPreference='SilentlyContinue'; " +
            "Get-VM -ErrorAction SilentlyContinue | ForEach-Object { " +
            "$adapters = $_ | Get-VMNetworkAdapter -ErrorAction SilentlyContinue; " +
            "[PSCustomObject]@{ " +
            "Name = $_.Name; " +
            "NicName = if ($adapters) { $adapters[0].Name } else { '' } " +
            "} } | ConvertTo-Json -Compress";

        var (ok, output) = await RunAsync(script);

        if (!ok || string.IsNullOrWhiteSpace(output))
        {
            if (!ok) _logger.LogWarning("GetAllVmsAsync failed: {Error}", output);
            _allVmsCache   = [];
            _allVmsCacheUtc = DateTime.UtcNow;
            return _allVmsCache;
        }

        try
        {
            var list = DeserializeArrayOrObject<DiscoveredVmJson>(output);
            _allVmsCache = list
                .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                .Select(v => new DiscoveredVm(v.Name, v.NicName ?? ""))
                .ToList();
            _allVmsCacheUtc = DateTime.UtcNow;
            _logger.LogInformation("Discovered {Count} VM(s) on localhost.", _allVmsCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetAllVmsAsync: failed to parse JSON output");
            _allVmsCache   = [];
            _allVmsCacheUtc = DateTime.UtcNow;
        }

        return _allVmsCache;
    }

    private sealed class DiscoveredVmJson { public string Name { get; set; } = ""; public string? NicName { get; set; } }

    // ── VM power control ────────────────────────────────────────────────────────

    /// <summary>Graceful shutdown via guest OS integration services (no -Force/-TurnOff).</summary>
    public Task ShutdownVmAsync(string vm) => VmActionAsync(vm, $"Stop-VM -Name '{Esc(vm)}'",   "shut down", SlowVmTimeout);
    public Task SuspendVmAsync(string vm)  => VmActionAsync(vm, $"Suspend-VM -Name '{Esc(vm)}'","paused");
    /// <summary>Saves VM state (RAM) to disk — can take several minutes; uses extended timeout.</summary>
    public Task SaveVmAsync(string vm)     => VmActionAsync(vm, $"Save-VM -Name '{Esc(vm)}'",   "saved",     SlowVmTimeout);
    public Task ResumeVmAsync(string vm)   => VmActionAsync(vm, $"Resume-VM -Name '{Esc(vm)}'", "resumed");

    /// <summary>Starts the VM, or resumes it if currently Paused (covers Off/Saved via Start-VM).</summary>
    public Task StartOrResumeVmAsync(string vm) => VmActionAsync(vm,
        $"$v = Get-VM -Name '{Esc(vm)}' -ErrorAction Stop; " +
        $"if ($v.State -eq 'Paused') {{ Resume-VM -Name '{Esc(vm)}' }} else {{ Start-VM -Name '{Esc(vm)}' }}",
        "started/resumed");

    private async Task VmActionAsync(string vmName, string script, string verb, TimeSpan? timeout = null)
    {
        _logger.LogInformation("VM '{VmName}': {Verb}...", vmName, verb);
        var (ok, output) = await RunAsync(script, timeout);
        if (ok) _logger.LogInformation("VM '{VmName}' {Verb} successfully.", vmName, verb);
        else    _logger.LogWarning("VM '{VmName}' {Verb} failed: {Error}", vmName, verb, output);
    }

    // ── VM status / metrics ─────────────────────────────────────────────────────

    /// <summary>Queries live state + metrics for the named VMs (one PowerShell call).</summary>
    public async Task<IReadOnlyList<VmStatus>> GetVmStatusesAsync(IEnumerable<string> names)
    {
        var quoted = names.Select(n => $"'{Esc(n)}'").ToList();
        if (quoted.Count == 0) return [];

        _logger.LogDebug("Querying VM statuses for {VmCount} VM(s)...", quoted.Count);

        var script =
            "$ProgressPreference='SilentlyContinue'; " +
            $"Get-VM -Name {string.Join(",", quoted)} -ErrorAction SilentlyContinue | ForEach-Object {{ " +
            "[PSCustomObject]@{ Name = $_.Name; State = $_.State.ToString(); Cpu = [int]$_.CPUUsage; " +
            "MemAssigned = [int64]$_.MemoryAssigned; MemMax = [int64]$_.MemoryMaximum; " +
            "Uptime = $_.Uptime.ToString(); " +
            "Switch = ($_.NetworkAdapters | Select-Object -First 1).SwitchName; " +
            "StatusDesc = ($_.StatusDescriptions -join ' ') } } | ConvertTo-Json -Depth 3";

        var (ok, output) = await RunAsync(script);
        if (!ok) _logger.LogWarning("GetVmStatusesAsync: PowerShell query failed: {Error}", output);
        else     _logger.LogDebug("GetVmStatusesAsync: PS query completed.");
        return DeserializeArrayOrObject<VmStatus>(ok ? output : "");
    }

    /// <summary>Sums each VM's attached VHD file sizes on the host (slower — call less often).</summary>
    public async Task<IReadOnlyDictionary<string, long>> GetVmVhdSizesAsync(IEnumerable<string> names)
    {
        var quoted = names.Select(n => $"'{Esc(n)}'").ToList();
        if (quoted.Count == 0) return new Dictionary<string, long>();

        _logger.LogDebug("Querying VHD sizes for {VmCount} VM(s)...", quoted.Count);

        var script =
            "$ProgressPreference='SilentlyContinue'; " +
            $"Get-VM -Name {string.Join(",", quoted)} -ErrorAction SilentlyContinue | ForEach-Object {{ " +
            "[PSCustomObject]@{ Name = $_.Name; Vhd = [int64](($_ | Get-VMHardDiskDrive | " +
            "Get-VHD -ErrorAction SilentlyContinue | Measure-Object -Property FileSize -Sum).Sum) } } | ConvertTo-Json -Depth 3";

        var (ok, output) = await RunAsync(script);
        if (!ok) _logger.LogWarning("GetVmVhdSizesAsync: PowerShell query failed: {Error}", output);
        else     _logger.LogDebug("GetVmVhdSizesAsync: PS query completed.");
        var list = DeserializeArrayOrObject<VhdEntry>(ok ? output : "");
        return list.ToDictionary(e => e.Name, e => e.Vhd);
    }

    private sealed class VhdEntry { public string Name { get; set; } = ""; public long Vhd { get; set; } }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Deserialises PS <c>ConvertTo-Json</c> output, which emits a bare object for a single item.</summary>
    private static IReadOnlyList<T> DeserializeArrayOrObject<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<T>>(json, JsonOpts) ?? [];
            var one = JsonSerializer.Deserialize<T>(json, JsonOpts);
            return one is null ? [] : [one];
        }
        catch { return []; }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<(bool ok, string output)> RunAsync(string psScript, TimeSpan? timeout = null)
    {
        await _lock.WaitAsync();
        try
        {
            // Base64-encode the script (UTF-16 LE) for -EncodedCommand.
            // This avoids every quoting/escaping issue on the process command line.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
            _logger.LogDebug("PS> {Script}", psScript);

            // Default 30 s timeout guards against hanging Hyper-V cmdlets (e.g. invalid adapter
            // names causing WMI/DCOM lookups that never time out on their own); slow operations
            // such as the switch rebind pass a longer one.
            var result = await ProcessRunner.RunAsync(
                "powershell.exe",
                $"-NonInteractive -NoProfile -WindowStyle Hidden -EncodedCommand {encoded}",
                timeout ?? TimeSpan.FromSeconds(30));

            if (!result.Success && result.ExitCode == -1)
                _logger.LogWarning("PowerShell command failed/timed out: {Script} ({Error})", psScript, result.Output);

            return (result.Success, result.Output);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Escapes a value for use inside a PowerShell single-quoted string.</summary>
    private static string Esc(string s) => s.Replace("'", "''");

    public void Dispose() => _lock.Dispose();
}
