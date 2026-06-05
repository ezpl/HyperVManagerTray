using HyperVManagerTray.Helpers;
using HyperVManagerTray.Models;
using Xunit;

namespace HyperVManagerTray.Tests;

public class UptimeFormatterTests
{
    // ── Normal running cases ──────────────────────────────────────────────────

    [Theory]
    [InlineData("00:47:00", "47m")]        // minutes only
    [InlineData("00:01:00", "1m")]         // 1 minute
    [InlineData("00:59:59", "59m")]        // just under 1 hour
    [InlineData("03:14:00", "3h 14m")]     // hours + minutes
    [InlineData("01:00:00", "1h 0m")]      // exactly 1 hour
    [InlineData("23:59:00", "23h 59m")]    // just under 1 day
    [InlineData("1.03:14:00", "1d 3h")]    // 1 day 3 hours (TimeSpan "d.hh:mm:ss")
    [InlineData("2.00:00:00", "2d 0h")]    // exactly 2 days
    [InlineData("10.12:30:00", "10d 12h")] // 10 days
    public void Format_RunningVm_ReturnsExpectedString(string uptime, string expected)
    {
        var s = new VmStatus { State = "Running", Uptime = uptime };
        Assert.Equal(expected, UptimeFormatter.Format(s));
    }

    // ── Empty / null / not-running cases ─────────────────────────────────────

    [Fact]
    public void Format_NullStatus_ReturnsEmpty()
        => Assert.Equal(string.Empty, UptimeFormatter.Format(null));

    [Fact]
    public void Format_VmNotRunning_ReturnsEmpty()
    {
        var s = new VmStatus { State = "Off", Uptime = "01:00:00" };
        Assert.Equal(string.Empty, UptimeFormatter.Format(s));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Format_NullOrWhitespaceUptime_ReturnsEmpty(string? uptime)
    {
        var s = new VmStatus { State = "Running", Uptime = uptime };
        Assert.Equal(string.Empty, UptimeFormatter.Format(s));
    }

    [Fact]
    public void Format_InvalidUptimeString_ReturnsEmpty()
    {
        var s = new VmStatus { State = "Running", Uptime = "not-a-timespan" };
        Assert.Equal(string.Empty, UptimeFormatter.Format(s));
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Format_ZeroUptime_ReturnsZeroMinutes()
    {
        // A VM that just started: 00:00:00 → 0 total minutes → "0m"
        var s = new VmStatus { State = "Running", Uptime = "00:00:00" };
        Assert.Equal("0m", UptimeFormatter.Format(s));
    }

    [Fact]
    public void Format_PausedVm_ReturnsEmpty()
    {
        // Paused VMs are not "Running" — uptime should be suppressed.
        var s = new VmStatus { State = "Paused", Uptime = "02:00:00" };
        Assert.Equal(string.Empty, UptimeFormatter.Format(s));
    }

    [Fact]
    public void Format_SavedVm_ReturnsEmpty()
    {
        var s = new VmStatus { State = "Saved", Uptime = "02:00:00" };
        Assert.Equal(string.Empty, UptimeFormatter.Format(s));
    }
}
