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

    // ── VM power control ────────────────────────────────────────────────────────

    public Task StartVmAsync(string vm)    => VmActionAsync(vm, $"Start-VM -Name '{Esc(vm)}'",   "started");
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
