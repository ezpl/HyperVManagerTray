using System.Net;
using HyperVNetworkSwitcher.Services;
using Xunit;

namespace HyperVNetworkSwitcher.Tests;

public class AdapterMatcherTests
{
    [Theory]
    [InlineData("10.0.0.45",   "10.0.0.0/23", true)]
    [InlineData("10.0.1.200",  "10.0.0.0/23", true)]    // /23 spans .0 and .1
    [InlineData("10.0.2.1",    "10.0.0.0/23", false)]   // just outside the /23
    [InlineData("192.168.1.5", "192.168.1.0/24", true)]
    [InlineData("192.168.2.5", "192.168.1.0/24", false)]
    [InlineData("10.0.0.1",    "10.0.0.1/32", true)]
    [InlineData("10.0.0.2",    "10.0.0.1/32", false)]
    [InlineData("8.8.8.8",     "0.0.0.0/0", true)]      // /0 matches everything
    public void IsInCidr_Works(string ip, string cidr, bool expected)
        => Assert.Equal(expected, AdapterMatcher.IsInCidr(IPAddress.Parse(ip), cidr));

    [Theory]
    [InlineData("not-a-cidr")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/abc")]
    public void IsInCidr_InvalidCidr_ReturnsFalse(string cidr)
        => Assert.False(AdapterMatcher.IsInCidr(IPAddress.Parse("10.0.0.1"), cidr));

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AABBCCDDEEFF")]
    [InlineData("AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF")]
    [InlineData("AABBCCDDEEFF",      "AABBCCDDEEFF")]
    public void NormalizeMac_StripsSeparatorsAndUppercases(string input, string expected)
        => Assert.Equal(expected, AdapterMatcher.NormalizeMac(input));

    [Fact]
    public void NormalizeMac_EqualAcrossFormats()
        => Assert.Equal(AdapterMatcher.NormalizeMac("aa:bb:cc:dd:ee:ff"),
                        AdapterMatcher.NormalizeMac("AA-BB-CC-DD-EE-FF"));

    [Theory]
    [InlineData("AABBCCDDEEFF", "AA:BB:CC:DD:EE:FF")]
    [InlineData("001122334455", "00:11:22:33:44:55")]
    public void FormatMac_FormatsValidMac(string raw, string expected)
        => Assert.Equal(expected, AdapterMatcher.FormatMac(raw));

    [Theory]
    [InlineData("")]        // empty MAC (e.g. tunnel / WAN miniport)
    [InlineData("ABCDEF")]  // wrong length
    public void FormatMac_InvalidLength_ReturnsRawUnchanged(string raw)
        => Assert.Equal(raw, AdapterMatcher.FormatMac(raw));
}
