using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Models;
using HyperVManagerTray.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HyperVManagerTray.Tests;

/// <summary>
/// Tests for ConfigManager file-write operations.
/// Each test writes a real temp file so we exercise the actual serialisation path.
/// </summary>
public class ConfigManagerTests : IDisposable
{
    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string WriteTempConfig(AppConfig cfg)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hvmt_test_{Guid.NewGuid():N}.json");
        var writeOpts = new JsonSerializerOptions
        {
            WriteIndented          = true,
            PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters             = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, writeOpts));
        return path;
    }

    private static AppConfig ReadConfig(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, ReadOpts) ?? new AppConfig();
    }

    private readonly List<string> _tempFiles = [];

    private ConfigManager MakeManager(string path)
    {
        _tempFiles.Add(path);
        return new ConfigManager(path, NullLogger<ConfigManager>.Instance);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best-effort */ }
    }

    // ── AddBridgedRule ────────────────────────────────────────────────────────

    [Fact]
    public void AddBridgedRule_AppendsRuleToFile()
    {
        var initial = new AppConfig
        {
            Fallback = new FallbackAction { VirtualSwitch = "Default Switch" }
        };
        var path = WriteTempConfig(initial);
        using var mgr = MakeManager(path);

        var rule = new NetworkRule
        {
            Name         = "Office LAN",
            Priority     = 1,
            VirtualSwitch = "Bridged",
            TargetVms    = ["TestVM"],
            Conditions   = new RuleConditions { AdapterMac = "AA:BB:CC:DD:EE:FF" }
        };

        mgr.AddBridgedRule(rule);

        var saved = ReadConfig(path);
        var added = Assert.Single(saved.Rules);
        Assert.Equal("Office LAN", added.Name);
        Assert.Equal("Bridged",    added.VirtualSwitch);
        Assert.Equal(["TestVM"],   added.TargetVms);
    }

    [Fact]
    public void AddBridgedRule_PreservesExistingRulesAndFallback()
    {
        var initial = new AppConfig
        {
            Rules    = [ new NetworkRule { Name = "Existing", Priority = 10, VirtualSwitch = "OldSwitch" } ],
            Fallback = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["VM1"] }
        };
        var path = WriteTempConfig(initial);
        using var mgr = MakeManager(path);

        mgr.AddBridgedRule(new NetworkRule { Name = "New", Priority = 5, VirtualSwitch = "Bridged" });

        var saved = ReadConfig(path);
        Assert.Equal(2, saved.Rules.Count);
        Assert.Contains(saved.Rules, r => r.Name == "Existing");
        Assert.Contains(saved.Rules, r => r.Name == "New");
        Assert.Equal("Default Switch", saved.Fallback.VirtualSwitch);
        Assert.Equal(["VM1"],          saved.Fallback.TargetVms);
    }

    [Fact]
    public void AddBridgedRule_UpdatesInMemoryConfig()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddBridgedRule(new NetworkRule { Name = "Test", VirtualSwitch = "Bridged" });

        Assert.Single(mgr.Current.Rules);
        Assert.Equal("Test", mgr.Current.Rules[0].Name);
    }

    // ── AddVmToConfig ─────────────────────────────────────────────────────────

    [Fact]
    public void AddVmToConfig_AddsVmToFile()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("TestVM", "Network Adapter");

        var saved = ReadConfig(path);
        var vm = Assert.Single(saved.VirtualMachines);
        Assert.Equal("TestVM",          vm.Name);
        Assert.Equal("Network Adapter", vm.NicName);
    }

    [Fact]
    public void AddVmToConfig_NoDuplicate_WhenCalledTwiceWithSameName()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("TestVM", "Network Adapter");
        mgr.AddVmToConfig("TestVM", "Network Adapter"); // second call — must be idempotent

        var saved = ReadConfig(path);
        Assert.Single(saved.VirtualMachines);
    }

    [Fact]
    public void AddVmToConfig_CaseInsensitiveDuplicateCheck()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("testvm", "Network Adapter");
        mgr.AddVmToConfig("TESTVM", "Network Adapter"); // same name, different case

        var saved = ReadConfig(path);
        Assert.Single(saved.VirtualMachines);
    }

    [Fact]
    public void AddVmToConfig_BlankNicName_DefaultsToNetworkAdapter()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("MyVM", "   "); // whitespace-only nicName

        var saved = ReadConfig(path);
        Assert.Equal("Network Adapter", saved.VirtualMachines[0].NicName);
    }

    [Fact]
    public void AddVmToConfig_UpdatesInMemoryConfig()
    {
        var path = WriteTempConfig(new AppConfig());
        using var mgr = MakeManager(path);

        mgr.AddVmToConfig("TestVM", "Network Adapter");

        Assert.Single(mgr.Current.VirtualMachines);
        Assert.Equal("TestVM", mgr.Current.VirtualMachines[0].Name);
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_AllFieldsPreserved()
    {
        var original = new AppConfig
        {
            VirtualMachines = [ new VmTarget { Name = "Alpha", NicName = "NIC 1" } ],
            Rules =
            [
                new NetworkRule
                {
                    Name          = "Home",
                    Priority      = 5,
                    VirtualSwitch = "ExternalSwitch",
                    TargetVms     = ["Alpha"],
                    AutoStart     = true,
                    Conditions    = new RuleConditions { AdapterMac = "AA:BB:CC:DD:EE:FF", IpCidr = "192.168.1.0/24" }
                }
            ],
            Fallback = new FallbackAction { VirtualSwitch = "Default Switch", TargetVms = ["Alpha"] }
        };

        var path = WriteTempConfig(original);
        _tempFiles.Add(path);

        var roundTripped = ReadConfig(path);

        Assert.Single(roundTripped.VirtualMachines);
        Assert.Equal("Alpha", roundTripped.VirtualMachines[0].Name);
        Assert.Equal("NIC 1", roundTripped.VirtualMachines[0].NicName);

        var rule = Assert.Single(roundTripped.Rules);
        Assert.Equal("Home",            rule.Name);
        Assert.Equal(5,                 rule.Priority);
        Assert.Equal("ExternalSwitch",  rule.VirtualSwitch);
        Assert.Equal(["Alpha"],         rule.TargetVms);
        Assert.True(rule.AutoStart);
        Assert.Equal("AA:BB:CC:DD:EE:FF", rule.Conditions.AdapterMac);
        Assert.Equal("192.168.1.0/24",    rule.Conditions.IpCidr);

        Assert.Equal("Default Switch", roundTripped.Fallback.VirtualSwitch);
        Assert.Equal(["Alpha"],        roundTripped.Fallback.TargetVms);
    }
}
