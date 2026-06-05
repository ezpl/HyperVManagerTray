namespace HyperVManagerTray.Models;

/// <summary>A Hyper-V virtual machine this app manages, plus the NIC and fallback switch to use.</summary>
public sealed class VmTarget
{
    /// <summary>Exact Hyper-V VM name (as shown in Hyper-V Manager).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Name of the VM's network adapter to reconnect (default "Network Adapter").</summary>
    public string NicName { get; set; } = "Network Adapter";
}
