using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>Mirrors the JSON options ConfigManager uses, to lock the config.json contract.</summary>
public class ConfigDeserializeTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void Deserializes_FullConfig()
    {
        const string json = """
        {
          "virtualMachines": [ { "name": "MyVM", "nicName": "Network Adapter" } ],
          "rules": [ {
            "name": "Office LAN", "priority": 1,
            "conditions": { "adapterMac": "AA:BB:CC:DD:EE:FF", "ipCidr": "10.0.0.0/23" },
            "virtualSwitch": "Bridged", "targetVms": [ "MyVM" ], "autoStart": true
          } ],
          "fallback": { "virtualSwitch": "Default Switch", "targetVms": [ "MyVM" ] }
        }
        """;

        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Opts)!;

        var vm = Assert.Single(cfg.VirtualMachines);
        Assert.Equal("MyVM", vm.Name);
        Assert.Equal("Network Adapter", vm.NicName);

        var rule = Assert.Single(cfg.Rules);
        Assert.Equal("Office LAN", rule.Name);
        Assert.Equal(1, rule.Priority);
        Assert.True(rule.AutoStart);
        Assert.Equal("AA:BB:CC:DD:EE:FF", rule.Conditions.AdapterMac);
        Assert.Equal("10.0.0.0/23", rule.Conditions.IpCidr);
        Assert.Equal("Bridged", rule.VirtualSwitch);
        Assert.Equal(["MyVM"], rule.TargetVms);

        Assert.Equal("Default Switch", cfg.Fallback.VirtualSwitch);
    }

    [Fact]
    public void AutoStart_DefaultsFalse_WhenOmitted()
    {
        const string json = """{ "rules": [ { "name": "R", "virtualSwitch": "Bridged" } ] }""";
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Opts)!;
        Assert.False(cfg.Rules[0].AutoStart);
    }

    [Fact]
    public void Conditions_OptionalFields_DefaultNull()
    {
        const string json = """{ "rules": [ { "name": "R", "virtualSwitch": "Bridged", "conditions": {} } ] }""";
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Opts)!;
        Assert.Null(cfg.Rules[0].Conditions.AdapterMac);
        Assert.Null(cfg.Rules[0].Conditions.IpCidr);
    }

    [Fact]
    public void PropertyNames_AreCaseInsensitive()
    {
        const string json = """{ "RULES": [ { "NAME": "R", "VirtualSwitch": "Bridged" } ] }""";
        var cfg = JsonSerializer.Deserialize<AppConfig>(json, Opts)!;
        Assert.Equal("R", cfg.Rules[0].Name);
    }
}
