using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace HyperVManagerTray.Services;

/// <summary>
/// Manages "run at Windows logon" for this elevated app.
///
/// A plain <c>HKCU\…\Run</c> entry cannot launch a <c>requireAdministrator</c> app at logon
/// (Windows starts Run-key items with a standard token and silently skips them), so auto-start
/// is implemented as a Scheduled Task with "Run with highest privileges" (<c>/RL HIGHEST</c>)
/// and a logon trigger.  The task runs in the user's interactive session, so the tray icon
/// still appears, with no UAC prompt.  Any obsolete Run-key value from older versions is
/// removed whenever the setting is toggled.
/// </summary>
internal sealed class StartupManager
{
    private const string TaskName       = "HyperVManagerTray";
    private const string LegacyRunKey   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValue = "HyperVManagerTray";

    private readonly ILogger<StartupManager> _logger;

    public StartupManager(ILogger<StartupManager> logger) => _logger = logger;

    /// <summary>True if the auto-start scheduled task exists (<c>schtasks /Query</c> exits 0).</summary>
    public bool IsEnabled => ProcessRunner.Run("schtasks.exe", "/Query", "/TN", TaskName).Success;

    /// <summary>Creates the logon task pointing at <paramref name="exePath"/>. Throws on failure.</summary>
    public void Enable(string exePath)
    {
        _logger.LogInformation("Enabling startup task '{TaskName}' for '{ExePath}'...", TaskName, exePath);
        var result = ProcessRunner.Run("schtasks.exe",
            "/Create", "/TN", TaskName,
            "/TR", $"\"{exePath}\"",
            "/SC", "ONLOGON",
            "/RL", "HIGHEST",
            "/F");

        if (!result.Success)
        {
            _logger.LogWarning("Failed to create startup task '{TaskName}': {Error}", TaskName, result.Output);
            throw new InvalidOperationException(result.Output);
        }

        _logger.LogInformation("Startup task '{TaskName}' enabled successfully.", TaskName);
        RemoveLegacyRunKey();
    }

    /// <summary>Deletes the logon task. Throws on failure.</summary>
    public void Disable()
    {
        _logger.LogInformation("Disabling startup task '{TaskName}'...", TaskName);
        var result = ProcessRunner.Run("schtasks.exe", "/Delete", "/TN", TaskName, "/F");
        if (!result.Success)
        {
            _logger.LogWarning("Failed to delete startup task '{TaskName}': {Error}", TaskName, result.Output);
            throw new InvalidOperationException(result.Output);
        }

        _logger.LogInformation("Startup task '{TaskName}' disabled successfully.", TaskName);
        RemoveLegacyRunKey();
    }

    /// <summary>Removes the obsolete HKCU\Run value written by older versions, if present.</summary>
    private static void RemoveLegacyRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
        key?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
    }
}
