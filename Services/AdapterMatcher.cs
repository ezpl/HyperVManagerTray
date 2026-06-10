using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using HyperVManagerTray.Models;

namespace HyperVManagerTray.Services;

/// <summary>Result of evaluating config rules against the current host network state.</summary>
public sealed record MatchResult(string RuleName, string VirtualSwitch, IReadOnlyList<string> TargetVms)
{
    public string HostAdapterName          { get; init; } = "—";  // human-readable description
    public string HostAdapterInterfaceName { get; init; } = "—";  // OS interface alias used by Set-VMSwitch
    public string HostIp                   { get; init; } = "—";
    public string Gateway                  { get; init; } = "—";
    public IReadOnlyList<string> DnsServers { get; init; } = [];
}

/// <summary>Network details of the current primary host adapter, used by "Add current network" feature.</summary>
public sealed record CurrentNetworkInfo(
    string AdapterDescription,
    string Mac,          // colon-separated, e.g. "AA:BB:CC:DD:EE:FF"
    string Ip,           // e.g. "10.0.0.45"
    string IpCidr);      // e.g. "10.0.0.0/23"

/// <summary>
/// Core rule-evaluation logic: inspects the host's live network adapters and decides which
/// virtual switch the VMs should use.  Handles the Hyper-V bridging quirk where an external
/// switch with AllowManagementOS=true moves the physical NIC's IP onto a virtual NIC, and
/// filters out WFP/NDIS filter-layer adapters that share a MAC/IP with the real NIC.
/// All members are pure/stateless.
/// </summary>
public static class AdapterMatcher
{
    /// <summary>Returns details for the current primary active adapter, or null if none found.</summary>
    public static CurrentNetworkInfo? GetCurrentNetworkInfo()
    {
        var (physical, virtual_) = SplitAdapters();

        var nic = PrimaryAdapter(physical, virtual_);
        if (nic is null) return null;

        // When the physical NIC is bridged its IP moves to the Hyper-V virtual NIC.
        // Try the physical NIC first, then fall back to any virtual NIC with an IPv4.
        var unicast = nic.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            ?? virtual_
                .SelectMany(v => v.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

        if (unicast is null) return null;

        return new CurrentNetworkInfo(
            AdapterDescription: FriendlyAdapterName(nic),
            Mac:    FormatMac(nic.GetPhysicalAddress().ToString()),
            Ip:     unicast.Address.ToString(),
            IpCidr: CalculateCidr(unicast));
    }

    /// <summary>
    /// Evaluates the config rules (in priority order) against the current host network and
    /// returns the matching rule's switch/VMs, or the fallback if nothing matched.
    /// </summary>
    public static MatchResult Evaluate(AppConfig config)
    {
        var (physical, virtual_) = SplitAdapters();

        foreach (var rule in config.Rules)
        {
            var matched = MatchingNic(rule, physical, virtual_);
            if (matched is not null)
                return BuildResult(rule.Name, rule.VirtualSwitch, rule.TargetVms, matched, virtual_);
        }

        return BuildResult("Fallback", config.Fallback.VirtualSwitch, config.Fallback.TargetVms,
                           PrimaryAdapter(physical, virtual_), virtual_);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Splits all Up, non-loopback adapters into physical (non-Hyper-V-virtual) and
    /// virtual (Hyper-V management NICs created by AllowManagementOS=true).
    ///
    /// WFP/NDIS filter-layer adapters are excluded from both lists: Windows creates them
    /// on top of physical NICs, they share the same MAC and IP as the underlying adapter,
    /// but they are NOT valid targets for Set-VMSwitch -NetAdapterName and should not
    /// participate in rule matching or primary-adapter detection.
    /// </summary>
    private static (List<NetworkInterface> Physical, List<NetworkInterface> Virtual) SplitAdapters()
    {
        var all = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && !IsFilterLayerAdapter(n))
            .ToList();

        return (all.Where(n => !IsHyperVVirtual(n)).ToList(),
                all.Where(n =>  IsHyperVVirtual(n)).ToList());
    }

    private static MatchResult BuildResult(
        string ruleName, string virtualSwitch, List<string> targetVms,
        NetworkInterface? nic, List<NetworkInterface> virtualAdapters)
    {
        var props = nic?.GetIPProperties();

        // When a physical NIC is bridged (AllowManagementOS=true), Windows moves the
        // IP/gateway/DNS to a Hyper-V virtual NIC.  If the physical NIC has no IPv4
        // address, source IP/gateway/DNS from the best available virtual NIC instead.
        bool hasIpv4 = props?.UnicastAddresses
            .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork) == true;

        if (!hasIpv4 && virtualAdapters.Count > 0)
        {
            // Prefer the virtual NIC that also has a default gateway (bridges always do).
            var vNic = virtualAdapters
                .Where(n => n.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork) ? 1 : 0)
                .FirstOrDefault();

            if (vNic is not null)
                props = vNic.GetIPProperties();
        }

        var ip = props?.UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            ?.Address.ToString() ?? "—";

        var gw = props?.GatewayAddresses
            .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(g => g.Address.ToString())
            .FirstOrDefault() ?? "—";

        var dns = props?.DnsAddresses
            .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
            .Select(d => d.ToString())
            .ToList() ?? [];

        return new MatchResult(ruleName, virtualSwitch, targetVms)
        {
            HostAdapterName          = nic is not null ? FriendlyAdapterName(nic) : "—",
            HostAdapterInterfaceName = nic?.Name ?? "—",
            HostIp                   = ip,
            Gateway                  = gw,
            DnsServers               = dns
        };
    }

    /// <summary>
    /// Returns the first physical NIC that satisfies all conditions of the rule, or null.
    ///
    /// When AllowManagementOS=true the physical NIC may lose its IPv4 address to a
    /// Hyper-V virtual NIC.  If the MAC matches but no IP is found on the physical NIC,
    /// we also check virtual NICs for the CIDR condition so the rule still fires.
    /// </summary>
    private static NetworkInterface? MatchingNic(
        NetworkRule rule,
        List<NetworkInterface> physicalAdapters,
        List<NetworkInterface> virtualAdapters)
    {
        foreach (var nic in physicalAdapters)
        {
            bool macOk = rule.Conditions.AdapterMac is null
                         || NormalizeMac(nic.GetPhysicalAddress().ToString()) ==
                            NormalizeMac(rule.Conditions.AdapterMac);

            if (!macOk) continue;
            if (rule.Conditions.IpCidr is null) return nic;

            // Check the physical NIC's own addresses.
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IsInCidr(addr.Address, rule.Conditions.IpCidr)) return nic;
            }

            // When bridged, the physical NIC has no IP — check virtual NICs for the CIDR.
            // If any virtual NIC carries an IP inside the rule's subnet, the rule is matched
            // and we return the physical NIC (its Name alias is what Set-VMSwitch needs).
            foreach (var vNic in virtualAdapters)
            {
                foreach (var addr in vNic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IsInCidr(addr.Address, rule.Conditions.IpCidr)) return nic;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the physical NIC that Windows currently routes traffic through.
    ///
    /// Uses GetBestInterface (Win32) for accuracy.  When the result is a Hyper-V virtual
    /// NIC (bridge active), we look for the physical NIC that has been stripped of its IP
    /// (it's the one driving the bridge) and return that instead, preferring wired over
    /// wireless when ambiguous.  Falls back to a gateway/speed heuristic if needed.
    /// </summary>
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    private static NetworkInterface? PrimaryAdapter(
        List<NetworkInterface> physicalAdapters,
        List<NetworkInterface> virtualAdapters)
    {
        try
        {
            var bytes = IPAddress.Parse("8.8.8.8").GetAddressBytes();
            uint dest = BitConverter.ToUInt32(bytes, 0);

            if (GetBestInterface(dest, out uint bestIndex) == 0)
            {
                // Happy path: best interface is a physical adapter.
                var best = physicalAdapters.FirstOrDefault(n =>
                {
                    try   { return n.GetIPProperties().GetIPv4Properties().Index == (int)bestIndex; }
                    catch { return false; }
                });
                if (best is not null) return best;

                // GetBestInterface returned a Hyper-V virtual adapter (bridge is active).
                // The physical NIC driving the bridge has had its IP moved to that virtual
                // NIC; it is now Up but carries no IPv4 address.  Find it, preferring
                // wired over wireless so we don't accidentally pick an unrelated NIC.
                // Require a valid 6-byte MAC to exclude tunnel/WAN-miniport adapters that
                // also lack an IPv4 but are not real NICs.
                var bridged = physicalAdapters
                    .Where(n => HasValidMac(n)
                                && !n.GetIPProperties().UnicastAddresses
                                    .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
                    .FirstOrDefault();

                if (bridged is not null) return bridged;
            }
        }
        catch { /* fall through to heuristic */ }

        // Heuristic fallback: prefer adapters with a default gateway, then wired over wireless,
        // then pick the fastest.  Wi-Fi 6E can report a higher Speed than Gigabit Ethernet, so
        // Speed alone would incorrectly prefer wireless when both share the same subnet.
        return physicalAdapters
            .Where(n => n.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0)
            .ThenByDescending(n => n.Speed)
            .FirstOrDefault()
            ?? physicalAdapters.FirstOrDefault();
    }

    // ── Small utilities ───────────────────────────────────────────────────────

    /// <summary>True only for adapters that carry a standard 48-bit (6-byte) MAC address.</summary>
    private static bool HasValidMac(NetworkInterface nic) =>
        nic.GetPhysicalAddress().GetAddressBytes().Length == 6;

    /// <summary>
    /// Returns a display-friendly adapter name by stripping the Windows filter-driver
    /// suffix that appears in <see cref="NetworkInterface.Description"/> for some adapters,
    /// e.g. "Lenovo USB Ethernet-WFP Native MAC Layer LightWeight Filter-0000" → "Lenovo USB Ethernet".
    /// </summary>
    private static string FriendlyAdapterName(NetworkInterface nic)
    {
        var desc = nic.Description;
        // Windows appends filter-driver names separated by a dash.  Strip from the first
        // occurrence of any known filter-chain marker so only the base device name remains.
        foreach (var marker in new[] { "-WFP ", " - WFP ", "-NDIS ", " - NDIS " })
        {
            var idx = desc.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return desc[..idx].Trim();
        }
        return desc;
    }

    // Formats "AABBCCDDEEFF" → "AA:BB:CC:DD:EE:FF"
    internal static string FormatMac(string raw)
    {
        var clean = raw.Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (clean.Length != 12) return raw; // not a standard 48-bit MAC — return as-is
        return string.Join(":", Enumerable.Range(0, 6).Select(i => clean.Substring(i * 2, 2)));
    }

    private static string CalculateCidr(UnicastIPAddressInformation unicast)
    {
        try
        {
            var maskBytes = unicast.IPv4Mask.GetAddressBytes();
            var prefixLen = 0;
            foreach (var b in maskBytes) { var v = (int)b; while (v != 0) { prefixLen += v & 1; v >>= 1; } }

            var ipBytes  = unicast.Address.GetAddressBytes();
            var netBytes = ipBytes.Zip(maskBytes, (a, b) => (byte)(a & b)).ToArray();
            return $"{new IPAddress(netBytes)}/{prefixLen}";
        }
        catch
        {
            var parts = unicast.Address.ToString().Split('.');
            return $"{parts[0]}.{parts[1]}.{parts[2]}.0/24";
        }
    }

    /// <summary>
    /// Returns true for the Hyper-V management NICs that Windows creates on the host
    /// when AllowManagementOS=true is set on a virtual switch.  These adapters share the
    /// IP with the bridged physical NIC (which loses its own IP) but have a Microsoft-
    /// assigned MAC (00:15:5D prefix).  They are excluded from rule MAC matching and
    /// primary-adapter detection, but their IP/gateway/DNS is used when the paired
    /// physical NIC has no IPv4 address.
    /// </summary>
    private static bool IsHyperVVirtual(NetworkInterface nic) =>
        nic.Description.StartsWith("Hyper-V Virtual", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true for software/virtual adapters that should never be used as a
    /// <c>Set-VMSwitch -NetAdapterName</c> target or participate in rule matching.
    ///
    /// <list type="bullet">
    ///   <item><b>WFP / NDIS filter-driver adapters</b> — Windows creates these alongside the
    ///   real physical NIC (e.g. "Ethernet-WFP Native MAC Layer LightWeight Filter-0000").
    ///   They share the same MAC/IP but are not valid switch targets and cause WMI hangs.</item>
    ///   <item><b>Microsoft Network Adapter Multiplexor Driver</b> — the adapter created by
    ///   the Windows "Bridge Connections" (Network Bridge / ms_bridge) feature.  If selected
    ///   as the external NIC, <c>Set-VMSwitch</c> binds the Hyper-V switch to the Windows
    ///   bridge instead of the underlying physical NIC, which activates the MAC Bridge service
    ///   and causes the host to route through the wrong adapter.</item>
    /// </list>
    /// </summary>
    private static bool IsFilterLayerAdapter(NetworkInterface nic)
    {
        static bool HasMarker(string s) =>
            s.IndexOf("-WFP ",             StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("LightWeight Filter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("-NDIS ",            StringComparison.OrdinalIgnoreCase) >= 0 ||
            s.IndexOf("Multiplexor",       StringComparison.OrdinalIgnoreCase) >= 0;   // Windows Network Bridge
        return HasMarker(nic.Name) || HasMarker(nic.Description);
    }

    internal static bool IsInCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[1], out int prefixLen)) return false;

        var network = IPAddress.Parse(parts[0]);
        uint mask = prefixLen == 0 ? 0u : ~((1u << (32 - prefixLen)) - 1u);
        return (ToUInt32(network) & mask) == (ToUInt32(address) & mask);
    }

    private static uint ToUInt32(IPAddress addr)
    {
        var bytes = addr.GetAddressBytes();
        if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
        return BitConverter.ToUInt32(bytes, 0);
    }

    /// <summary>Strips separators and upper-cases a MAC so two forms compare equal (e.g. "aa:bb…" == "AA-BB…").</summary>
    internal static string NormalizeMac(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToUpperInvariant();
}
