using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>
/// Runs Hyper-V PowerShell cmdlets through a single persistent powershell.exe worker
/// process (read-eval loop over stdin/stdout), avoiding the 1-2 s process+module startup
/// that a per-command spawn would cost on every dashboard poll.  The worker is reaped
/// after a couple of minutes of inactivity so an idle tray app holds no extra process.
///
/// An out-of-process worker (rather than the Microsoft.PowerShell.SDK in-process runspace)
/// is used because the SDK fails to initialise in self-contained builds due to a registry
/// lookup (PSSnapInReader) that returns null when the Windows PowerShell engine key is
/// absent.  powershell.exe (Windows PowerShell 5.1) is always present on Windows 10/11 and
/// supports all required Hyper-V cmdlets.  Commands are passed as Base64-encoded Unicode
/// lines to sidestep all quoting/escaping concerns.
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

    public HyperVManager(ILogger<HyperVManager> logger)
    {
        _logger = logger;
        // Periodically stop the worker process when it has been idle long enough that the
        // next caller is better served by a fresh spawn than by ~80 MB of warm-but-unused PS.
        _workerReaper = new System.Threading.Timer(
            _ => ReapIdleWorker(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

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

    // Re-homing an external switch's adapter is slow (~25 s observed), so this gets a longer
    // timeout than the default. The bind is now done as a single atomic Set-VMSwitch call that
    // never disables host sharing, so even a hard kill mid-sequence cannot leave the host
    // without a management vNIC (the failure mode that previously disconnected the host).
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(120);

    // Save-VM writes all assigned RAM to disk — can take several minutes for large VMs.
    // Stop-VM (graceful) sends a shutdown signal and waits for the guest OS to finish.
    private static readonly TimeSpan SlowVmTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Binds a Hyper-V virtual switch to a physical NIC (makes it External, with the host
    /// sharing the adapter) — but only when it isn't already in that exact state.
    ///
    /// <para><b>Crash/kill safety.</b> The rebind is a single atomic <c>Set-VMSwitch
    /// -NetAdapterName … -AllowManagementOS $true</c>. Host sharing is <em>never</em> disabled,
    /// so there is no window in which a hard kill (process crash, timeout kill) could leave the
    /// host adapter with no management vNIC and therefore no IP — the failure that previously
    /// disconnected the host when the app crashed between a <c>$false</c> and <c>$true</c> toggle.</para>
    ///
    /// <para><b>No-op fast path.</b> If the switch is already External, sharing with the management
    /// OS, and bound to the target adapter, nothing is changed — this stops host-network flicker
    /// on every launch / redundant evaluation.</para>
    ///
    /// <para><b>Self-heal.</b> Any duplicate/orphaned <c>vEthernet (&lt;switch&gt;)</c> management
    /// vNICs left behind by older builds are collapsed back to a single one (without toggling
    /// sharing, so no connectivity blip).</para>
    ///
    /// <para>If the target adapter isn't present (e.g. the USB NIC is unplugged / on a different
    /// network), the switch is left untouched.</para>
    /// </summary>
    public async Task UpdateSwitchBindingAsync(string switchName, string adapterName)
    {
        var sw  = Esc(switchName);
        var nic = Esc(adapterName);
        var script =
            $"$want = (Get-NetAdapter -Name '{nic}' -ErrorAction SilentlyContinue).InterfaceDescription; " +
            $"$s = Get-VMSwitch -Name '{sw}' -ErrorAction SilentlyContinue; " +
             "if (-not $want) { 'NOADAPTER' } elseif (-not $s) { 'NOSWITCH' } " +
             "elseif ($s.SwitchType -eq 'External' -and $s.AllowManagementOS -and " +
             "$s.NetAdapterInterfaceDescription -eq $want) { 'SKIP' } else { " +
            $"Set-VMSwitch -Name '{sw}' -NetAdapterName '{nic}' -AllowManagementOS $true; 'BOUND' }}";

        var (ok, output) = await RunAsync(script, BindTimeout);

        if (!ok)                                _logger.LogError("UpdateSwitchBindingAsync error: {Error}", output);
        else if (output.Contains("NOADAPTER"))  _logger.LogInformation("Adapter '{Adapter}' not present — switch '{Switch}' left unchanged", adapterName, switchName);
        else if (output.Contains("NOSWITCH"))   _logger.LogWarning("Virtual switch '{Switch}' not found — cannot bind", switchName);
        else if (output.Contains("SKIP"))       _logger.LogInformation("Switch '{Switch}' already bound to '{Adapter}' — no rebind", switchName, adapterName);
        else                                    _logger.LogInformation("Switch '{Switch}' bound to '{Adapter}'", switchName, adapterName);
    }

    /// <summary>
    /// Removes orphaned/duplicate <c>vEthernet (&lt;switch&gt;)</c> management vNICs that older
    /// builds could leave behind — but <b>only when it is provably safe</b>: it acts solely when
    /// none of the switch's host vNICs is currently <c>Up</c> (i.e. the switch is not carrying a
    /// live host connection), so the cleanup can never interrupt networking. Always keeps one
    /// management vNIC. No-op when there's nothing to clean or the switch is in use. Safe to call
    /// on a timer or after startup.
    /// </summary>
    public async Task HealSwitchOrphansAsync(string switchName)
    {
        var sw = Esc(switchName);
        var script =
            $"$h = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like 'vEthernet ({sw})*' }}); " +
            $"$m = @(Get-VMNetworkAdapter -ManagementOS -SwitchName '{sw}' -ErrorAction SilentlyContinue); " +
             "if ($m.Count -le 1) { 'NONE' } " +
             "elseif (@($h | Where-Object { $_.Status -eq 'Up' }).Count -gt 0) { 'BUSY' } " +
             "else { $m | Select-Object -Skip 1 | Remove-VMNetworkAdapter -ErrorAction SilentlyContinue; 'HEALED ' + ($m.Count - 1) }";

        var (ok, output) = await RunAsync(script, BindTimeout);

        if (!ok)                            _logger.LogWarning("HealSwitchOrphansAsync('{Switch}') error: {Error}", switchName, output);
        else if (output.Contains("HEALED")) _logger.LogInformation("Removed orphaned management vNIC(s) on switch '{Switch}' ({Detail})", switchName, output.Trim());
        else if (output.Contains("BUSY"))   _logger.LogInformation("Switch '{Switch}' has duplicate vNICs but is in use — deferring cleanup", switchName);
        // NONE → nothing to clean; no log.
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

    /// <summary>
    /// Queries live state + metrics — and optionally VHD sizes — for the named VMs in a
    /// SINGLE PowerShell round-trip.  The dashboard polls this every second while open, so
    /// batching the (slow) VHD enumeration into the same call halves the per-cycle cost
    /// versus issuing two separate queries that the worker would serialise anyway.
    /// Never throws — returns empty collections on any error.
    /// </summary>
    public async Task<(IReadOnlyList<VmStatus> Statuses, IReadOnlyDictionary<string, long> VhdBytes)>
        GetVmDashboardAsync(IEnumerable<string> names, bool includeVhd)
    {
        var quoted = names.Select(n => $"'{Esc(n)}'").ToList();
        if (quoted.Count == 0) return ([], new Dictionary<string, long>());

        _logger.LogDebug("Querying dashboard data for {VmCount} VM(s) (vhd={Vhd})...", quoted.Count, includeVhd);

        var vhdPart = includeVhd
            ? "@($vms | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Vhd = [int64](($_ | Get-VMHardDiskDrive | " +
              "Get-VHD -ErrorAction SilentlyContinue | Measure-Object -Property FileSize -Sum).Sum) } })"
            : "@()";

        var script =
            "$ProgressPreference='SilentlyContinue'; " +
            $"$vms = @(Get-VM -Name {string.Join(",", quoted)} -ErrorAction SilentlyContinue); " +
            "$st = @($vms | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; State = $_.State.ToString(); Cpu = [int]$_.CPUUsage; " +
            "MemAssigned = [int64]$_.MemoryAssigned; MemMax = [int64]$_.MemoryMaximum; " +
            "Uptime = $_.Uptime.ToString(); " +
            "Switch = ($_.NetworkAdapters | Select-Object -First 1).SwitchName; " +
            "StatusDesc = ($_.StatusDescriptions -join ' ') } }); " +
            $"$vhd = {vhdPart}; " +
            "[PSCustomObject]@{ Status = $st; Vhd = $vhd } | ConvertTo-Json -Depth 4";

        var (ok, output) = await RunAsync(script);
        if (!ok || string.IsNullOrWhiteSpace(output))
        {
            if (!ok) _logger.LogWarning("GetVmDashboardAsync: PowerShell query failed: {Error}", output);
            return ([], new Dictionary<string, long>());
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            var statuses = doc.RootElement.TryGetProperty("Status", out var st)
                ? ParseListElement<VmStatus>(st) : [];
            var vhdList  = doc.RootElement.TryGetProperty("Vhd", out var vh)
                ? ParseListElement<VhdEntry>(vh) : [];
            return (statuses, vhdList.ToDictionary(e => e.Name, e => e.Vhd));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetVmDashboardAsync: failed to parse JSON output");
            return ([], new Dictionary<string, long>());
        }
    }

    private sealed class VhdEntry { public string Name { get; set; } = ""; public long Vhd { get; set; } }

    /// <summary>
    /// Deserialises a nested element that PS 5.1's <c>ConvertTo-Json</c> may emit as an array,
    /// a bare object (single item), or null/absent (empty).
    /// </summary>
    private static IReadOnlyList<T> ParseListElement<T>(JsonElement el)
    {
        try
        {
            return el.ValueKind switch
            {
                JsonValueKind.Array  => JsonSerializer.Deserialize<List<T>>(el.GetRawText(), JsonOpts) ?? [],
                JsonValueKind.Object => [JsonSerializer.Deserialize<T>(el.GetRawText(), JsonOpts)!],
                _                    => [],
            };
        }
        catch { return []; }
    }

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

    // ── Persistent PowerShell worker ────────────────────────────────────────────
    //
    // Spawning a fresh powershell.exe per query costs 1-2 s of startup + Hyper-V module
    // load each time; with the dashboard polling every second that meant dozens of process
    // launches per minute.  Instead, one hidden worker process runs a read-eval loop:
    // each command is sent as a Base64 line on stdin, executed in the warm session, and
    // the output is terminated by an OK/ERR sentinel line.  Commands keep the same
    // semantics as before (non-terminating errors are merged into output; terminating
    // errors yield ok=false).
    //
    // Lifecycle: spawned lazily on first use, killed+respawned on timeout or crash, and
    // reaped after WorkerIdleTimeout of inactivity so an idle tray app holds no extra
    // process (~80 MB) — process startup is only paid again on the next burst of activity.

    private const string SentinelOk  = "<<HVMT:OK>>";
    private const string SentinelErr = "<<HVMT:ERR>>";
    private static readonly TimeSpan WorkerIdleTimeout = TimeSpan.FromMinutes(2);

    private System.Diagnostics.Process? _worker;
    private StreamWriter? _workerIn;
    private StreamReader? _workerOut;
    private DateTime _workerLastUseUtc;
    private readonly System.Threading.Timer _workerReaper;

    // The worker's read-eval loop. $ProgressPreference is session-wide; each command runs
    // in a fresh scriptblock with default (Continue) error semantics, mirroring how the
    // scripts behaved as standalone -EncodedCommand invocations.
    private const string WorkerBootstrap =
        "$ProgressPreference='SilentlyContinue'; " +
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " +
        "while ($true) { " +
        "$l = [Console]::In.ReadLine(); " +
        "if ($null -eq $l) { break }; " +
        "if ($l.Length -eq 0) { continue }; " +
        "$e = $false; " +
        "try { " +
        "$s = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($l)); " +
        "$o = & ([ScriptBlock]::Create($s)) 2>&1 | Out-String; " +
        "if ($o.Length -gt 0) { [Console]::Out.Write($o); if (-not $o.EndsWith(\"`n\")) { [Console]::Out.WriteLine() } } " +
        "} catch { [Console]::Out.WriteLine($_.Exception.Message); $e = $true }; " +
        "if ($e) { [Console]::Out.WriteLine('" + SentinelErr + "') } else { [Console]::Out.WriteLine('" + SentinelOk + "') }; " +
        "[Console]::Out.Flush() }";

    private void EnsureWorker()
    {
        if (_worker is { HasExited: false }) return;

        KillWorker(); // clean up any dead remnants

        var bootstrap = Convert.ToBase64String(Encoding.Unicode.GetBytes(WorkerBootstrap));
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "powershell.exe",
            Arguments              = $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -EncodedCommand {bootstrap}",
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        _worker = System.Diagnostics.Process.Start(psi)
                  ?? throw new InvalidOperationException("Failed to start PowerShell worker");
        _workerIn  = _worker.StandardInput;
        _workerIn.AutoFlush = true;
        _workerOut = _worker.StandardOutput;

        // Drain stderr in the background so a full pipe can never block the worker.
        var stderr = _worker.StandardError;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await stderr.ReadLineAsync().ConfigureAwait(false)) is not null)
                    _logger.LogDebug("PS worker stderr: {Line}", line);
            }
            catch { /* worker exited — expected */ }
        });

        _logger.LogInformation("PowerShell worker started (pid {Pid}).", _worker.Id);
    }

    private void KillWorker()
    {
        var w = _worker;
        _worker = null;
        if (w is null) return;
        try { if (!w.HasExited) w.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { w.Dispose(); } catch { }
        _workerIn  = null;
        _workerOut = null;
    }

    /// <summary>Reaper callback: kills the worker after a period of inactivity (never blocks).</summary>
    private void ReapIdleWorker()
    {
        try
        {
            if (_worker is null || DateTime.UtcNow - _workerLastUseUtc < WorkerIdleTimeout) return;
            if (!_lock.Wait(0)) return;   // a command is running — check again next tick
            try
            {
                if (_worker is not null && DateTime.UtcNow - _workerLastUseUtc >= WorkerIdleTimeout)
                {
                    _logger.LogDebug("PowerShell worker idle for {Idle} — stopping.", WorkerIdleTimeout);
                    KillWorker();
                }
            }
            finally { _lock.Release(); }
        }
        catch (ObjectDisposedException) { /* raced with Dispose — nothing to do */ }
    }

    private async Task<(bool ok, string output)> RunAsync(string psScript, TimeSpan? timeout = null)
    {
        await _lock.WaitAsync();
        try
        {
            var to = timeout ?? TimeSpan.FromSeconds(30);
            _logger.LogDebug("PS> {Script}", psScript);
            _workerLastUseUtc = DateTime.UtcNow;

            // One retry: if the worker died between commands (or was idle-reaped mid-write),
            // respawn and resend. A timeout does NOT retry — the command may be genuinely hung.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    EnsureWorker();
                    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(psScript));
                    await _workerIn!.WriteLineAsync(encoded);

                    var sb = new StringBuilder();
                    using var cts = new CancellationTokenSource(to);
                    while (true)
                    {
                        var line = await _workerOut!.ReadLineAsync(cts.Token);
                        if (line is null) throw new EndOfStreamException("PowerShell worker exited unexpectedly");
                        if (line == SentinelOk)  { _workerLastUseUtc = DateTime.UtcNow; return (true,  sb.ToString().Trim()); }
                        if (line == SentinelErr) { _workerLastUseUtc = DateTime.UtcNow; return (false, sb.ToString().Trim()); }
                        sb.AppendLine(line);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Hung cmdlet (e.g. WMI/DCOM lookup that never returns) — kill the whole
                    // worker; the next command starts a fresh one.
                    _logger.LogWarning("PowerShell command timed out after {Timeout}s: {Script}", to.TotalSeconds, psScript);
                    KillWorker();
                    return (false, $"Timed out after {to.TotalSeconds:0} s");
                }
                catch (Exception ex) when (attempt == 0)
                {
                    _logger.LogDebug(ex, "PowerShell worker unavailable — restarting once.");
                    KillWorker();
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Escapes a value for use inside a PowerShell single-quoted string.</summary>
    private static string Esc(string s) => s.Replace("'", "''");

    public void Dispose()
    {
        _workerReaper.Dispose();
        KillWorker();
        _lock.Dispose();
    }
}
