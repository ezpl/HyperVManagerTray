using System.Text;
using Microsoft.Extensions.Logging;

namespace HyperVNetworkSwitcher;

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

    /// <summary>Connects a VM's NIC to the given virtual switch (Connect-VMNetworkAdapter).</summary>
    public async Task ApplySwitchAsync(string vmName, string nicName, string switchName)
    {
        _logger.LogInformation("Connecting {Vm}/{Nic} → {Switch}", vmName, nicName, switchName);
        var (ok, output) = await RunAsync(
            $"Connect-VMNetworkAdapter -VMName '{Esc(vmName)}' -Name '{Esc(nicName)}' -SwitchName '{Esc(switchName)}'");

        if (ok) _logger.LogInformation("Switch applied: {Vm} → {Switch}", vmName, switchName);
        else    _logger.LogError("ApplySwitchAsync error: {Error}", output);
    }

    // The bind sequence toggles AllowManagementOS and re-homes the external adapter, which is
    // slow (~25 s observed).  It gets a longer timeout than the default so it is not killed
    // mid-sequence: a kill after '-AllowManagementOS $false' but before '$true' would leave the
    // host adapter with no management vNIC (and therefore no IP) until the next successful bind.
    private static readonly TimeSpan BindTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Binds a Hyper-V virtual switch to a physical NIC (makes it External, with the host
    /// sharing the adapter).
    ///
    /// Host sharing is detached (<c>-AllowManagementOS $false</c>) <em>before</em> the external
    /// adapter is changed, then re-enabled.  This forces Windows to remove the previous host
    /// management vNIC instead of leaving it behind: rebinding a shared switch straight to a new
    /// adapter orphans the old <c>vEthernet (&lt;switch&gt;)</c> NIC, and those accumulate across
    /// rebinds — which locks the switch's settings and pollutes host routing with stale default
    /// routes.  The two-step keeps exactly one management vNIC alive.
    /// </summary>
    public async Task UpdateSwitchBindingAsync(string switchName, string adapterName)
    {
        _logger.LogInformation("Binding switch '{Switch}' → adapter '{Adapter}'", switchName, adapterName);
        var (ok, output) = await RunAsync(
            $"Set-VMSwitch -Name '{Esc(switchName)}' -AllowManagementOS $false; " +
            $"Set-VMSwitch -Name '{Esc(switchName)}' -NetAdapterName '{Esc(adapterName)}' -AllowManagementOS $true",
            BindTimeout);

        if (ok) _logger.LogInformation("Switch '{Switch}' bound to '{Adapter}'", switchName, adapterName);
        else    _logger.LogError("UpdateSwitchBindingAsync error: {Error}", output);
    }

    /// <summary>Returns the IPv4 addresses reported by the VM's network adapter (may be empty).</summary>
    public async Task<string[]> GetVmIpAddressesAsync(string vmName)
    {
        var (ok, output) = await RunAsync(
            $"(Get-VMNetworkAdapter -VMName '{Esc(vmName)}').IPAddresses | " +
            $"Where-Object {{ $_ -match '\\.' }}");

        if (!ok || string.IsNullOrWhiteSpace(output)) return [];

        return output.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.Trim())
                     .Where(l => l.Length > 0)
                     .ToArray();
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
