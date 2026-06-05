namespace HyperVManagerTray.Models;

/// <summary>
/// Match conditions for a <see cref="NetworkRule"/>.  Both are optional; when both are set
/// the host adapter must satisfy both (logical AND).  A rule with no conditions matches the
/// current primary adapter unconditionally.
/// </summary>
public sealed class RuleConditions
{
    /// <summary>Physical host adapter MAC, e.g. "AA:BB:CC:DD:EE:FF". Null = don't match on MAC.</summary>
    public string? AdapterMac { get; set; }

    /// <summary>CIDR the adapter's IP must fall within, e.g. "10.0.0.0/23". Null = don't match on IP.</summary>
    public string? IpCidr { get; set; }
}

/// <summary>
/// Maps a recognised host network (by adapter MAC and/or IP subnet) to the Hyper-V virtual
/// switch the listed VMs should be connected to.
/// </summary>
public sealed class NetworkRule
{
    /// <summary>Human-readable rule name, shown in the tray status popup.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Evaluation order — lower numbers are checked first.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Conditions the active host adapter must satisfy for this rule to match.</summary>
    public RuleConditions Conditions { get; set; } = new();

    /// <summary>Hyper-V virtual switch to connect to when this rule matches.</summary>
    public string VirtualSwitch { get; set; } = string.Empty;

    /// <summary>Names of the VMs to reconnect to <see cref="VirtualSwitch"/>.</summary>
    public List<string> TargetVms { get; set; } = [];

    /// <summary>
    /// When true, the <see cref="TargetVms"/> are started (or resumed if paused) the moment this
    /// rule becomes active.  They are never auto-stopped when the rule deactivates.
    /// </summary>
    public bool AutoStart { get; set; } = false;
}
