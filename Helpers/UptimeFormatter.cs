using HyperVManagerTray.Models;

namespace HyperVManagerTray.Helpers;

/// <summary>
/// Pure formatting helpers for VM uptime display.  Extracted from DashboardWindow so
/// this logic can be exercised by unit tests without a WinUI runtime dependency.
/// </summary>
public static class UptimeFormatter
{
    /// <summary>
    /// Formats the VM uptime for display on the dashboard card header.
    /// Returns an empty string when the VM is not running or the uptime string is unavailable.
    /// Examples: "47m", "3h 14m", "1d 3h".
    /// </summary>
    public static string Format(VmStatus? s)
    {
        if (s is null || !s.IsRunning || string.IsNullOrWhiteSpace(s.Uptime))
            return string.Empty;

        if (!TimeSpan.TryParse(s.Uptime, out var ts) || ts < TimeSpan.Zero)
            return string.Empty;

        if (ts.TotalDays >= 1)
            return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{(int)ts.TotalMinutes}m";
    }
}
