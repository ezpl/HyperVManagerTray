using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVManagerTray.Models;
using Microsoft.Extensions.Logging;

namespace HyperVManagerTray.Services;

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
        try { ConfigReloaded?.Invoke(this, _config); }
        catch (Exception ex) { _logger.LogError(ex, "A ConfigReloaded subscriber threw an exception"); }
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

    /// <summary>
    /// Appends a new <see cref="VmTarget"/> to config.json and reloads.
    /// Does nothing if a VM with the same name is already present.
    /// </summary>
    public void AddVmToConfig(string name, string nicName)
    {
        if (_config.VirtualMachines.Any(v =>
                v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("AddVmToConfig: '{Name}' is already in config — skipping.", name);
            return;
        }

        _watcher.EnableRaisingEvents = false;
        try
        {
            var newVm = new VmTarget
            {
                Name    = name,
                NicName = string.IsNullOrWhiteSpace(nicName) ? "Network Adapter" : nicName,
            };

            var updated = new AppConfig
            {
                VirtualMachines = [.. _config.VirtualMachines, newVm],
                Rules           = _config.Rules,
                Fallback        = _config.Fallback,
            };

            var writeOptions = new JsonSerializerOptions
            {
                WriteIndented            = true,
                PropertyNamingPolicy     = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
                Converters               = { new JsonStringEnumConverter() }
            };

            File.WriteAllText(_configPath, JsonSerializer.Serialize(updated, writeOptions));
            _logger.LogInformation("VM '{Name}' added and saved to {Path}", name, _configPath);

            Load();
            ConfigReloaded?.Invoke(this, _config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save new VM '{Name}'", name);
            throw;
        }
        finally
        {
            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>
    /// Removes the named VM from config.json and reloads.
    /// Does nothing if no VM with that name exists.
    /// </summary>
    public void RemoveVmFromConfig(string name)
    {
        if (!_config.VirtualMachines.Any(v =>
                v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("RemoveVmFromConfig: '{Name}' not found in config — skipping.", name);
            return;
        }

        _watcher.EnableRaisingEvents = false;
        try
        {
            var updated = new AppConfig
            {
                VirtualMachines = [.. _config.VirtualMachines.Where(v =>
                    !v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))],
                Rules           = _config.Rules,
                Fallback        = _config.Fallback,
            };

            var writeOptions = new JsonSerializerOptions
            {
                WriteIndented            = true,
                PropertyNamingPolicy     = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition   = JsonIgnoreCondition.WhenWritingNull,
                Converters               = { new JsonStringEnumConverter() }
            };

            File.WriteAllText(_configPath, JsonSerializer.Serialize(updated, writeOptions));
            _logger.LogInformation("VM '{Name}' removed and saved to {Path}", name, _configPath);

            Load();
            ConfigReloaded?.Invoke(this, _config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove VM '{Name}'", name);
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
