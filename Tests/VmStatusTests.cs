using HyperVNetworkSwitcher.Models;
using Xunit;

namespace HyperVNetworkSwitcher.Tests;

public class VmStatusTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void MemoryFraction_ComputesRatio()
        => Assert.Equal(0.5, new VmStatus { MemAssigned = 2 * Gb, MemMax = 4 * Gb }.MemoryFraction, 3);

    [Fact]
    public void MemoryFraction_ZeroMax_ReturnsZero()
        => Assert.Equal(0, new VmStatus { MemAssigned = 100, MemMax = 0 }.MemoryFraction);

    [Fact]
    public void MemoryFraction_ClampedToOne()
        => Assert.Equal(1.0, new VmStatus { MemAssigned = 8 * Gb, MemMax = 4 * Gb }.MemoryFraction);

    [Fact]
    public void MemAssignedMb_Converts()
        => Assert.Equal(2048, new VmStatus { MemAssigned = 2 * Gb }.MemAssignedMb, 0);

    [Theory]
    [InlineData("Running", true,  false, false, false)]
    [InlineData("Off",     false, false, false, true)]
    [InlineData("Paused",  false, true,  false, false)]
    [InlineData("Saved",   false, false, true,  false)]
    [InlineData("running", true,  false, false, false)]  // case-insensitive
    public void StateFlags(string state, bool running, bool paused, bool saved, bool off)
    {
        var s = new VmStatus { State = state };
        Assert.Equal(running, s.IsRunning);
        Assert.Equal(paused,  s.IsPaused);
        Assert.Equal(saved,   s.IsSaved);
        Assert.Equal(off,     s.IsOff);
    }
}
