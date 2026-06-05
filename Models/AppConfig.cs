namespace HyperVManagerTray.Models;

/// <summary>
/// The switch and VMs to use when no <see cref="NetworkRule"/> matches the current network
/// (typically a NAT switch such as the Hyper-V "Default Switch").
/// </summary>
public sealed class FallbackAction
{
    /// <summary>Hyper-V virtual switch to connect the target VMs to when no rule matches.</summary>
    public string VirtualSwitch { get; set; } = "Default Switch";

    /// <summary>Names of the VMs to reconnect to <see cref="VirtualSwitch"/>.</summary>
    public List<string> TargetVms { get; set; } = [];
}

/// <summary>
/// Root configuration object deserialised from <c>config.json</c>: the managed VMs,
/// the priority-ordered match rules, and the fallback action.
/// </summary>
public sealed class AppConfig
{
    /// <summary>VMs this app manages, keyed by Hyper-V VM name.</summary>
    public List<VmTarget> VirtualMachines { get; set; } = [];

    /// <summary>Network-to-switch rules, evaluated in ascending <see cref="NetworkRule.Priority"/> order.</summary>
    public List<NetworkRule> Rules { get; set; } = [];

    /// <summary>Action applied when no rule matches the current host network.</summary>
    public FallbackAction Fallback { get; set; } = new();
}
