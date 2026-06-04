using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVNetworkSwitcher.Models;
using Microsoft.Extensions.Logging;

namespace HyperVNetworkSwitcher.Services;

/// <summary>
/// Loads <c>config.json</c>, exposes the current <see cref="AppConfig"/>, and watches the file
/// for changes (debounced) so edits take effect without a restart.  Rules are kept sorted by
/// ascending priority after every load.
/// </summary>
public sealed class ConfigManager : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _configPath;
    private readonly ILogger<ConfigManager> _logger;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Threading.Timer _debounceTimer;
    private AppConfig _config = new();

    /// <summary>Raised after the config is reloaded (file change or rule addition).</summary>
    public event EventHandler<AppConfig>? ConfigReloaded;

    /// <summary>The most recently loaded configuration.</summary>
    public AppConfig Current => _config;

    public ConfigManager(string configPath, ILogger<ConfigManager> logger)
    {
        _configPath = configPath;
        _logger = logger;
        _debounceTimer = new System.Threading.Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(configPath)!, Path.GetFileName(configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => _debounceTimer.Change(500, Timeout.Infinite);

        Load();
    }

    /// <summary>Reads and deserialises config.json, ordering rules by priority. Errors are logged, not thrown.</summary>
    public void Load()
    {
        try
        {
            var json = File.ReadAllText(_configPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            loaded.Rules = [.. loaded.Rules.OrderBy(r => r.Priority)];
            _config = loaded;
            _logger.LogInformation("Config loaded from {Path} ({RuleCount} rules)", _configPath, _config.Rules.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load config from {Path}", _configPath);
        }
    }

    private void OnDebounceElapsed(object? _)
    {
        _logger.LogInformation("Config file changed — reloading");
        Load();
        ConfigReloaded?.Invoke(this, _config);
    }

    /// <summary>
    /// Appends a new rule to config.json, saves it, and triggers an immediate reload.
    /// The FileSystemWatcher is paused during the write to avoid a redundant debounced reload.
    /// </summary>
    public void AddBridgedRule(NetworkRule rule)
    {
        _watcher.EnableRaisingEvents = false;
        try
        {
            var updated = new AppConfig
            {
                VirtualMachines = _config.VirtualMachines,
                Rules           = [.. _config.Rules, rule],
                Fallback        = _config.Fallback
            };

            var writeOptions = new JsonSerializerOptions
            {
                WriteIndented            = true,
                PropertyNamingPolicy     = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
                Converters               = { new JsonStringEnumConverter() }
            };

            File.WriteAllText(_configPath, JsonSerializer.Serialize(updated, writeOptions));
            _logger.LogInformation("Rule '{Name}' added and saved to {Path}", rule.Name, _configPath);

            Load();
            ConfigReloaded?.Invoke(this, _config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save new rule '{Name}'", rule.Name);
            throw;
        }
        finally
        {
            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>Returns the expected config.json path: next to the executable.</summary>
    public static string GetConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public void Dispose()
    {
        _debounceTimer.Dispose();
        _watcher.Dispose();
    }
}
