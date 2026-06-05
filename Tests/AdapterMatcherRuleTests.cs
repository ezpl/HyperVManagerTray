using System.Net;
using HyperVManagerTray.Services;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Tests for AdapterMatcher rule-evaluation helpers.
/// The high-level Evaluate() method reads live NetworkInterface objects and cannot be
/// exercised here without a real host adapter; these tests verify the pure building-block
/// helpers that underpin every rule condition check.
/// </summary>
public class AdapterMatcherRuleTests
{
    // ── MAC normalization used by every MAC-condition check ───────────────────

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "aabbccddeeff", true)]   // colon lower  == dash lower stripped
    [InlineData("AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF", true)]   // dash upper   == raw upper
    [InlineData("AA:BB:CC:DD:EE:FF", "aabbccddeeff", true)]   // colon upper  == lower raw
    [InlineData("AABBCCDDEEFF",      "001122334455", false)]   // different MACs
    public void NormalizeMac_MacConditionEquality(string a, string b, bool shouldEqual)
    {
        var na = AdapterMatcher.NormalizeMac(a);
        var nb = AdapterMatcher.NormalizeMac(b);
        Assert.Equal(shouldEqual, na == nb);
    }

    // ── CIDR matching: boundary conditions used by IP-condition checks ────────

    [Theory]
    [InlineData("192.168.1.0",   "192.168.1.0/24", true)]    // network address itself
    [InlineData("192.168.1.255", "192.168.1.0/24", true)]    // broadcast address
    [InlineData("192.168.2.0",   "192.168.1.0/24", false)]   // one network above
    [InlineData("172.16.0.1",    "172.16.0.0/12",  true)]    // /12 — RFC-1918 range
    [InlineData("172.31.255.254","172.16.0.0/12",  true)]    // last address in /12
    [InlineData("172.32.0.0",    "172.16.0.0/12",  false)]   // just outside /12
    [InlineData("10.255.255.255","10.0.0.0/8",     true)]    // /8 broadcast
    [InlineData("11.0.0.0",      "10.0.0.0/8",     false)]   // outside /8
    [InlineData("1.2.3.4",       "1.2.3.4/32",     true)]    // /32 exact host
    [InlineData("1.2.3.5",       "1.2.3.4/32",     false)]   // /32 miss
    public void IsInCidr_BoundaryConditions(string ip, string cidr, bool expected)
        => Assert.Equal(expected, AdapterMatcher.IsInCidr(IPAddress.Parse(ip), cidr));

    // ── FormatMac edge cases ──────────────────────────────────────────────────

    [Theory]
    [InlineData("aabbccddeeff", "AA:BB:CC:DD:EE:FF")]  // lower-case input
    [InlineData("000000000000", "00:00:00:00:00:00")]  // all-zero MAC
    [InlineData("ffffffffffff", "FF:FF:FF:FF:FF:FF")]  // broadcast MAC
    public void FormatMac_AdditionalCases(string raw, string expected)
        => Assert.Equal(expected, AdapterMatcher.FormatMac(raw));

    // ── NormalizeMac: idempotent ──────────────────────────────────────────────

    [Fact]
    public void NormalizeMac_IsIdempotent()
    {
        var once  = AdapterMatcher.NormalizeMac("aa:bb:cc:dd:ee:ff");
        var twice = AdapterMatcher.NormalizeMac(once);
        Assert.Equal(once, twice);
    }
}
