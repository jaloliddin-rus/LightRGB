using System.Text.Json;
using System.Text.Json.Serialization;
using LightRGB.Effects;

namespace LightRGB.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceRunMode
{
    Animated,
    Static,
    Off
}

public sealed class DeviceConfig
{
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DeviceRunMode Mode { get; set; } = DeviceRunMode.Animated;
    public string StaticColor { get; set; } = "#009BDE";
    public int Brightness { get; set; } = 100;
    public bool SyncBrightness { get; set; } = true;
    public float CanvasX { get; set; } = 0.5f;
    public float CanvasY { get; set; } = 0.5f;
    public float Scale { get; set; } = 1.0f;
    public Dictionary<string, int> ChannelLedCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonElement> PluginParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppConfig
{
    public EffectSettings Effect { get; set; } = new();
    public int TargetFps { get; set; } = 20;
    public int WebPort { get; set; } = 4747;
    public bool SkipShutdownBlackout { get; set; } = false;
    public bool Paused { get; set; } = false;
    public bool StartupApplyAndExit { get; set; } = false;
    public bool PawnIoPromptDismissed { get; set; } = false;
    public Dictionary<string, DeviceConfig> Devices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configPath;
    private readonly object _lock = new();
    private readonly System.Threading.Timer _saveTimer;
    private AppConfig _config;
    private bool _dirty;
    private bool _disposed;

    public ConfigStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _configPath = Path.Combine(dataDirectory, "config.json");
        _config = LoadInternal();
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string ConfigPath => _configPath;

    public AppConfig GetSnapshot()
    {
        lock (_lock)
        {
            string json = JsonSerializer.Serialize(_config, JsonOpts);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
        }
    }

    public DeviceConfig GetOrCreateDevice(string deviceId, string defaultName, DeviceRunMode defaultMode = DeviceRunMode.Animated)
    {
        lock (_lock)
        {
            if (!_config.Devices.TryGetValue(deviceId, out var dev))
            {
                dev = new DeviceConfig
                {
                    DeviceId = deviceId,
                    DisplayName = defaultName,
                    Mode = defaultMode
                };
                _config.Devices[deviceId] = dev;
                ScheduleSave();
            }
            string json = JsonSerializer.Serialize(dev, JsonOpts);
            return JsonSerializer.Deserialize<DeviceConfig>(json, JsonOpts)!;
        }
    }

    public void UpdateEffect(EffectSettings effect)
    {
        lock (_lock)
        {
            _config.Effect = effect;
            ScheduleSave();
        }
    }

    public void UpdateGlobalSettings(int? targetFps = null, bool? skipShutdownBlackout = null, bool? paused = null, int? webPort = null, bool? startupApplyAndExit = null, bool? pawnIoPromptDismissed = null)
    {
        lock (_lock)
        {
            if (targetFps.HasValue) _config.TargetFps = Math.Clamp(targetFps.Value, 1, 60);
            if (skipShutdownBlackout.HasValue) _config.SkipShutdownBlackout = skipShutdownBlackout.Value;
            if (paused.HasValue) _config.Paused = paused.Value;
            if (webPort.HasValue) _config.WebPort = Math.Clamp(webPort.Value, 1024, 65535);
            if (startupApplyAndExit.HasValue) _config.StartupApplyAndExit = startupApplyAndExit.Value;
            if (pawnIoPromptDismissed.HasValue) _config.PawnIoPromptDismissed = pawnIoPromptDismissed.Value;
            ScheduleSave();
        }
    }

    public void UpdateDevice(string deviceId, Action<DeviceConfig> mutate)
    {
        lock (_lock)
        {
            if (!_config.Devices.TryGetValue(deviceId, out var dev))
            {
                dev = new DeviceConfig { DeviceId = deviceId };
                _config.Devices[deviceId] = dev;
            }
            mutate(dev);
            ScheduleSave();
        }
    }

    public void SetAllDevicesSyncBrightness(bool sync, int? seedBrightnessWhenUnsyncing = null)
    {
        lock (_lock)
        {
            foreach (var d in _config.Devices.Values)
            {
                if (!sync && d.SyncBrightness && seedBrightnessWhenUnsyncing.HasValue)
                    d.Brightness = Math.Clamp(seedBrightnessWhenUnsyncing.Value, 0, 100);
                d.SyncBrightness = sync;
            }
            ScheduleSave();
        }
    }

    public void Flush()
    {
        string? jsonToWrite = null;
        lock (_lock)
        {
            if (!_dirty) return;
            _dirty = false;
            jsonToWrite = JsonSerializer.Serialize(_config, JsonOpts);
        }

        if (jsonToWrite is not null)
            WriteToDisk(jsonToWrite);
    }

    private void ScheduleSave()
    {
        _dirty = true;
        if (!_disposed)
            _saveTimer.Change(400, Timeout.Infinite);
    }

    private AppConfig LoadInternal()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_configPath), JsonOpts);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Fallback to defaults if config corrupt
        }
        return new AppConfig();
    }

    private void WriteToDisk(string json)
    {
        try
        {
            string tmp = _configPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch
        {
            // Non-fatal
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _saveTimer.Dispose();
        Flush();
    }
}
