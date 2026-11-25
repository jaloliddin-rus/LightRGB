using System.Diagnostics;
using System.Text.Json;
using LightRGB.Effects;
using LightRGB.Extraction;
using LightRGB.Hardware;
using LightRGB.Runtime;
using LightRGB.Tray;

namespace LightRGB.Core;

public sealed record UnmatchedHidInfo(ushort VendorId, ushort ProductId, string Manufacturer, string Product, int EndpointCount);

public sealed record ScanProgressSnapshot(bool IsScanning, int Percent, string Stage, int FoundCount);

public sealed class DeviceManager : IDisposable
{
    private readonly string _dataDir;
    private readonly string _pluginsDir;
    private readonly string _userPluginsDir;
    private readonly ConfigStore _configStore;
    private readonly CanvasEngine _canvas;
    private readonly SystemInfoProvider _systemInfo;
    private volatile PluginIndexDocument _pluginIndex;
    private readonly bool _forceKeepLightsOn;

    private readonly object _workersLock = new();
    private readonly object _scanStateLock = new();
    private readonly ReaderWriterLockSlim _smbusScanGate = new();
    private readonly Dictionary<string, IDeviceWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private List<UnmatchedHidInfo> _unmatchedHid = [];
    private List<ISmbusTransport> _smbusBuses = [];
    private List<IAmdI2cTransport> _adlBuses = [];
    private List<ISmbusTransport> _nvGpuBuses = [];
    private string _smbusStatus = "Not initialized";
    private string _adlGpuStatus = "Not initialized";
    private string _nvGpuStatus = "Not initialized";

    private MotherboardInfo _cachedMobo;
    private (bool Installed, string ImagePath) _cachedPawnIo;
    private IReadOnlyList<PciDeviceInfo> _cachedPciDevices = [];

    private int _rescanRunning;
    private bool _isScanning;
    private int _scanPercent;
    private string _scanStage = "Idle";
    private int _scanFoundCount;

    private readonly ManualResetEventSlim _canvasWake = new(true);
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _canvasClock = Stopwatch.StartNew();
    private Thread? _canvasThread;

    private DateTime _lastCpuSampleTime = DateTime.UtcNow;
    private TimeSpan _lastCpuTotalTime = Process.GetCurrentProcess().TotalProcessorTime;
    private double _lastCpuPercent;

    public DeviceManager(
        string dataDir,
        ConfigStore configStore,
        CanvasEngine canvas,
        SystemInfoProvider systemInfo,
        PluginIndexDocument pluginIndex,
        bool forceKeepLightsOn = false)
    {
        _dataDir = dataDir;
        _pluginsDir = Path.Combine(dataDir, "plugins");
        _userPluginsDir = Path.Combine(dataDir, "user_plugins");
        _configStore = configStore;
        _canvas = canvas;
        _systemInfo = systemInfo;
        _pluginIndex = pluginIndex;
        _forceKeepLightsOn = forceKeepLightsOn;

        _cachedMobo = _systemInfo.GetMotherboardInfo();
        _cachedPawnIo = _systemInfo.GetPawnIoStatus();
        _cachedPciDevices = _systemInfo.GetDisplayAndSmbusDevices();

        var cfg = _configStore.GetSnapshot();
        _canvas.UpdateSettings(cfg.Effect);
        _canvas.Tick(0);
    }

    public string DataDir => _dataDir;
    public SystemInfoProvider SystemInfo => _systemInfo;
    public CanvasEngine Canvas => _canvas;
    public ConfigStore Config => _configStore;
    public PluginIndexDocument PluginIndex => _pluginIndex;

    public void ReloadPluginIndex(PluginIndexDocument newIndex)
    {
        _pluginIndex = newIndex;
        RescanDevicesAsync(includeSmbus: true);
    }

    public void Start(bool asyncInitialScan = false)
    {
        if (asyncInitialScan)
            RescanDevicesAsync(includeSmbus: true);
        else
            RescanDevices(includeSmbus: true);

        _canvasThread = new Thread(CanvasLoop)
        {
            IsBackground = true,
            Name = "LightRGB-Canvas"
        };
        _canvasThread.Start();
    }

    public bool RescanDevicesAsync(bool includeSmbus = true)
    {
        if (Interlocked.CompareExchange(ref _rescanRunning, 1, 0) != 0)
            return false;

        SetScanProgress(true, 5, "Starting hardware scan...", CountWorkersSafe());
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                RescanDevicesCore(includeSmbus);
            }
            finally
            {
                Interlocked.Exchange(ref _rescanRunning, 0);
            }
        });
        return true;
    }

    public void RescanDevices(bool includeSmbus = true)
    {
        if (Interlocked.CompareExchange(ref _rescanRunning, 1, 0) != 0)
            return;

        try
        {
            RescanDevicesCore(includeSmbus);
        }
        finally
        {
            Interlocked.Exchange(ref _rescanRunning, 0);
        }
    }

    private void RescanDevicesCore(bool includeSmbus)
    {
        try
        {
            int foundCount = 0;
            SetScanProgress(true, 10, "Refreshing system & PCI metadata...", foundCount);
            _cachedMobo = _systemInfo.GetMotherboardInfo();
            _cachedPawnIo = _systemInfo.GetPawnIoStatus();
            _cachedPciDevices = _systemInfo.GetDisplayAndSmbusDevices();

            SetScanProgress(true, 25, "Scanning USB HID & LampArray devices...", foundCount);
            var groups = HidNative.EnumerateDeviceGroups();
            var appCfg = _configStore.GetSnapshot();
            var pluginIndex = _pluginIndex;

            var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unmatched = new List<UnmatchedHidInfo>();
            var pendingActions = new List<(string Id, Func<IDeviceWorker> Factory)>();

            foreach (var g in groups)
            {
                string devId = $"{g.VendorId:X4}:{g.ProductId:X4}";
                var matches = PluginIndexer.FindMatches(pluginIndex, g.VendorId, g.ProductId);

                if (matches.Count > 0)
                {
                    var chosen = matches.FirstOrDefault(m => m.IsUserPlugin) ?? matches[0];
                    activeIds.Add(devId);
                    foundCount++;
                    var groupCopy = g;
                    pendingActions.Add((devId, () =>
                    {
                        var devCfg = _configStore.GetOrCreateDevice(devId, chosen.Name, DeviceRunMode.Animated);
                        var transport = new HidDeviceSession(groupCopy.Endpoints);
                        return new PluginDeviceWorker(
                            devId,
                            chosen,
                            transport,
                            _canvas,
                            _pluginsDir,
                            _userPluginsDir,
                            _systemInfo,
                            devCfg,
                            appCfg.Effect.Brightness,
                            appCfg.TargetFps,
                            appCfg.Paused,
                            appCfg.SkipShutdownBlackout || _forceKeepLightsOn);
                    }));
                }
                else if (g.IsLampArray)
                {
                    activeIds.Add(devId);
                    foundCount++;
                    var groupCopy = g;
                    pendingActions.Add((devId, () =>
                    {
                        string name = string.IsNullOrWhiteSpace(groupCopy.Product) ? $"LampArray {devId}" : groupCopy.Product;
                        var devCfg = _configStore.GetOrCreateDevice(devId, name);
                        return new LampArrayWorker(
                            devId,
                            groupCopy,
                            _canvas,
                            devCfg,
                            appCfg.Effect.Brightness,
                            appCfg.TargetFps,
                            appCfg.Paused,
                            appCfg.SkipShutdownBlackout || _forceKeepLightsOn);
                    }));
                }
                else
                {
                    unmatched.Add(new UnmatchedHidInfo(g.VendorId, g.ProductId, g.Manufacturer, g.Product, g.Endpoints.Count));
                }
            }

            SetScanProgress(true, 45, "Probing GPUs (AMD ADL & NVIDIA NVAPI)...", foundCount);
            if (_adlBuses.Count == 0)
            {
                _adlBuses = AmdAdlI2cBus.OpenAllAdapters(out _adlGpuStatus).ToList();
            }
            if (_nvGpuBuses.Count == 0)
            {
                _nvGpuBuses = NvApiI2cBus.OpenAllAdapters(out _nvGpuStatus).ToList();
            }

            var gpuSmbusBuses = new List<ISmbusTransport>();
            gpuSmbusBuses.AddRange(_nvGpuBuses);

            foreach (var gpuBus in _adlBuses)
            {
                if (SapphireGpuWorker.TryDetect(gpuBus, out string gpuName))
                {
                    string devId = $"GPU:{gpuBus.AdapterIndex}:{gpuBus.SubVendorId:X4}:{gpuBus.SubDeviceId:X4}";
                    activeIds.Add(devId);
                    foundCount++;
                    var busCopy = gpuBus;
                    string nameCopy = gpuName;
                    pendingActions.Add((devId, () =>
                    {
                        var devCfg = _configStore.GetOrCreateDevice(devId, nameCopy);
                        return new SapphireGpuWorker(
                            devId,
                            nameCopy,
                            busCopy,
                            _canvas,
                            devCfg,
                            appCfg.Effect.Brightness,
                            appCfg.TargetFps,
                            appCfg.Paused,
                            appCfg.SkipShutdownBlackout || _forceKeepLightsOn);
                    }));
                }
                else
                {
                    gpuSmbusBuses.Add(new AmdGpuSmbusAdapter(gpuBus));
                }
            }

            if (gpuSmbusBuses.Count > 0)
            {
                var gpuDevices = SmbusDeviceScanner.ScanGpuPlugins(
                    pluginIndex,
                    gpuSmbusBuses,
                    _pluginsDir,
                    _userPluginsDir,
                    _systemInfo);

                foreach (var gd in gpuDevices)
                {
                    string devId = $"GPU-I2C:{gd.Bus.Port}:{gd.Bus.SubVendorId:X4}:{gd.Bus.SubDeviceId:X4}:{gd.Address:X2}";
                    activeIds.Add(devId);
                    foundCount++;
                    var gdCopy = gd;
                    pendingActions.Add((devId, () =>
                    {
                        var devCfg = _configStore.GetOrCreateDevice(devId, $"{gdCopy.Plugin.Name} (0x{gdCopy.Address:X2})");
                        return new SmbusDeviceWorker(
                            devId,
                            gdCopy.Plugin,
                            gdCopy.Bus,
                            gdCopy.Address,
                            _canvas,
                            _pluginsDir,
                            _userPluginsDir,
                            _systemInfo,
                            devCfg,
                            appCfg.Effect.Brightness,
                            appCfg.TargetFps,
                            appCfg.Paused,
                            appCfg.SkipShutdownBlackout || _forceKeepLightsOn);
                    }));
                }
            }

            if (includeSmbus)
            {
                SetScanProgress(true, 55, "Pausing RAM workers & scanning SMBus...", foundCount);
                if (_smbusBuses.Count == 0)
                {
                    string pawnioDir = Path.Combine(AppContext.BaseDirectory, "pawnio");
                    _smbusBuses = PawnIoSmbusTransport.TryOpenSystemBuses(pawnioDir, out _smbusStatus).ToList();
                }
                if (_smbusBuses.Count > 0)
                {
                    List<SmbusDetectedDevice> ramDevices;
                    _smbusScanGate.EnterWriteLock();
                    try
                    {
                        ramDevices = SmbusDeviceScanner.ScanRamPlugins(
                            pluginIndex,
                            _smbusBuses,
                            _pluginsDir,
                            _userPluginsDir,
                            _systemInfo,
                            onProgress: (done, total, pluginName) =>
                            {
                                int pct = total > 0 ? 55 + (int)Math.Round(35.0 * done / total) : 75;
                                SetScanProgress(true, pct, $"Scanning SMBus RAM ({pluginName})...", foundCount);
                            });
                    }
                    finally
                    {
                        _smbusScanGate.ExitWriteLock();
                    }

                    foreach (var rd in ramDevices)
                    {
                        string devId = $"SMBUS:{rd.Bus.Port}:{rd.Address:X2}";
                        activeIds.Add(devId);
                        foundCount++;
                        var rdCopy = rd;
                        pendingActions.Add((devId, () =>
                        {
                            var devCfg = _configStore.GetOrCreateDevice(devId, $"{rdCopy.Plugin.Name} (0x{rdCopy.Address:X2})");
                            return new SmbusDeviceWorker(
                                devId,
                                rdCopy.Plugin,
                                rdCopy.Bus,
                                rdCopy.Address,
                                _canvas,
                                _pluginsDir,
                                _userPluginsDir,
                                _systemInfo,
                                devCfg,
                                appCfg.Effect.Brightness,
                                appCfg.TargetFps,
                                appCfg.Paused,
                                appCfg.SkipShutdownBlackout || _forceKeepLightsOn,
                                _smbusScanGate);
                        }));
                    }
                }
            }
            else
            {
                lock (_workersLock)
                {
                    foreach (var id in _workers.Keys.Where(k => k.StartsWith("SMBUS:", StringComparison.OrdinalIgnoreCase)))
                    {
                        activeIds.Add(id);
                        foundCount++;
                    }
                }
            }

            SetScanProgress(true, 95, "Updating active device workers...", foundCount);
            lock (_workersLock)
            {
                foreach (var (id, factory) in pendingActions)
                {
                    if (!_workers.ContainsKey(id))
                    {
                        var worker = factory();
                        _workers[id] = worker;
                        worker.Start();
                    }
                }

                var removed = _workers.Keys.Where(id => !activeIds.Contains(id)).ToList();
                foreach (var id in removed)
                {
                    _workers[id].Dispose();
                    _workers.Remove(id);
                }

                _unmatchedHid = unmatched;
            }

            _canvasWake.Set();
            SetScanProgress(false, 100, $"Scan complete ({foundCount} device(s))", foundCount);
        }
        catch (Exception ex)
        {
            SetScanProgress(false, 100, $"Scan error: {ex.Message}", CountWorkersSafe());
        }
    }

    private void SetScanProgress(bool isScanning, int percent, string stage, int foundCount)
    {
        lock (_scanStateLock)
        {
            _isScanning = isScanning;
            _scanPercent = Math.Clamp(percent, 0, 100);
            _scanStage = stage;
            _scanFoundCount = foundCount;
        }
    }

    private int CountWorkersSafe()
    {
        lock (_workersLock) { return _workers.Count; }
    }

    public void UpdateEffect(EffectSettings settings)
    {
        _canvas.UpdateSettings(settings);
        _canvas.Tick(_canvasClock.Elapsed.TotalSeconds);
        _configStore.UpdateEffect(_canvas.GetSettingsSnapshot());
        PushConfigToWorkers();
    }

    public void UpdateGlobalSettings(int? targetFps = null, bool? skipShutdownBlackout = null, bool? paused = null, bool? startupApplyAndExit = null)
    {
        _configStore.UpdateGlobalSettings(targetFps, skipShutdownBlackout, paused, startupApplyAndExit: startupApplyAndExit);
        if (startupApplyAndExit.HasValue && StartupManager.IsEnabled())
            StartupManager.SetEnabled(true, _dataDir, startupApplyAndExit.Value);
        PushConfigToWorkers();
    }

    public void SetAllDevicesSyncBrightness(bool sync)
    {
        int globalBright = _configStore.GetSnapshot().Effect.Brightness;
        _configStore.SetAllDevicesSyncBrightness(sync, sync ? null : globalBright);
        PushConfigToWorkers();
    }

    public void UpdateDevice(
        string deviceId,
        DeviceRunMode? mode = null,
        string? staticColor = null,
        float? canvasX = null,
        float? canvasY = null,
        Dictionary<string, int>? channelLedCounts = null,
        Dictionary<string, JsonElement>? pluginParams = null,
        int? brightness = null,
        bool? syncBrightness = null)
    {
        _configStore.UpdateDevice(deviceId, d =>
        {
            if (mode.HasValue) d.Mode = mode.Value;
            if (!string.IsNullOrWhiteSpace(staticColor)) d.StaticColor = staticColor;
            if (brightness.HasValue) d.Brightness = Math.Clamp(brightness.Value, 0, 100);
            if (syncBrightness.HasValue) d.SyncBrightness = syncBrightness.Value;
            if (canvasX.HasValue) d.CanvasX = Math.Clamp(canvasX.Value, 0f, 1f);
            if (canvasY.HasValue) d.CanvasY = Math.Clamp(canvasY.Value, 0f, 1f);
            if (channelLedCounts is not null)
            {
                foreach (var kv in channelLedCounts)
                    d.ChannelLedCounts[kv.Key] = Math.Max(0, kv.Value);
            }
            if (pluginParams is not null)
            {
                foreach (var kv in pluginParams)
                    d.PluginParams[kv.Key] = kv.Value.Clone();
            }
        });

        var appCfg = _configStore.GetSnapshot();
        lock (_workersLock)
        {
            if (_workers.TryGetValue(deviceId, out var w) && appCfg.Devices.TryGetValue(deviceId, out var devCfg))
            {
                w.ApplyConfig(devCfg, appCfg.Effect.Brightness, appCfg.TargetFps, appCfg.Paused, appCfg.SkipShutdownBlackout || _forceKeepLightsOn);
            }
        }
        _canvasWake.Set();
    }

    private void PushConfigToWorkers()
    {
        var appCfg = _configStore.GetSnapshot();
        lock (_workersLock)
        {
            foreach (var (id, w) in _workers)
            {
                var devCfg = appCfg.Devices.TryGetValue(id, out var dc) ? dc : new DeviceConfig { DeviceId = id };
                w.ApplyConfig(devCfg, appCfg.Effect.Brightness, appCfg.TargetFps, appCfg.Paused, appCfg.SkipShutdownBlackout || _forceKeepLightsOn);
            }
        }
        _canvasWake.Set();
    }

    private void CanvasLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var cfg = _configStore.GetSnapshot();
            bool anyAnimated;
            lock (_workersLock)
            {
                anyAnimated = !cfg.Paused && _workers.Values.Any(w => w.Mode == DeviceRunMode.Animated);
            }

            if (!anyAnimated)
            {
                _canvasWake.Reset();
                lock (_workersLock)
                {
                    cfg = _configStore.GetSnapshot();
                    anyAnimated = !cfg.Paused && _workers.Values.Any(w => w.Mode == DeviceRunMode.Animated);
                }
                if (!anyAnimated)
                {
                    WaitHandle.WaitAny([_canvasWake.WaitHandle, _cts.Token.WaitHandle]);
                    continue;
                }
            }

            _canvas.Tick(_canvasClock.Elapsed.TotalSeconds);
            int delayMs = 1000 / Math.Clamp(cfg.TargetFps, 1, 60);
            _cts.Token.WaitHandle.WaitOne(delayMs);
        }
    }

    public object GetStatusSnapshot()
    {
        var proc = Process.GetCurrentProcess();
        double ramMb = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1);

        var now = DateTime.UtcNow;
        var cpuTime = proc.TotalProcessorTime;
        double wallMs = (now - _lastCpuSampleTime).TotalMilliseconds;
        if (wallMs >= 250)
        {
            double cpuMs = (cpuTime - _lastCpuTotalTime).TotalMilliseconds;
            _lastCpuPercent = Math.Round(Math.Clamp(cpuMs / (wallMs * Environment.ProcessorCount) * 100.0, 0, 100), 1);
            _lastCpuSampleTime = now;
            _lastCpuTotalTime = cpuTime;
        }

        var cfg = _configStore.GetSnapshot();
        ScanProgressSnapshot scanSnap;
        lock (_scanStateLock)
        {
            scanSnap = new ScanProgressSnapshot(_isScanning, _scanPercent, _scanStage, _scanFoundCount);
        }

        List<object> devices;
        List<UnmatchedHidInfo> unmatched;
        bool allBrightnessSynced;
        lock (_workersLock)
        {
            allBrightnessSynced = _workers.Count > 0
                ? _workers.Keys.All(id => !cfg.Devices.TryGetValue(id, out var d) || d.SyncBrightness)
                : cfg.Devices.Values.All(d => d.SyncBrightness);

            devices = _workers.Values.Select(w =>
            {
                cfg.Devices.TryGetValue(w.DeviceId, out var dc);
                bool syncBright = dc?.SyncBrightness ?? true;
                int devBright = dc?.Brightness ?? 100;
                int effBright = syncBright ? cfg.Effect.Brightness : devBright;
                return (object)new
                {
                    deviceId = w.DeviceId,
                    name = w.DisplayName,
                    pluginPath = w.PluginPath,
                    deviceType = w.DeviceType,
                    vendorId = $"0x{w.VendorId:X4}",
                    productId = $"0x{w.ProductId:X4}",
                    mode = w.Mode.ToString(),
                    staticColor = dc?.StaticColor ?? "#009BDE",
                    brightness = devBright,
                    syncBrightness = syncBright,
                    effectiveBrightness = effBright,
                    canvasX = dc?.CanvasX ?? 0.5f,
                    canvasY = dc?.CanvasY ?? 0.5f,
                    isIdle = w.IsIdle,
                    framesRendered = w.FramesRendered,
                    lastError = w.LastError,
                    channels = w.GetChannels(),
                    subdevices = w.GetSubdevices(),
                    parameters = w.GetParameters(),
                    logs = w.GetRecentLogs()
                };
            }).ToList();

            unmatched = _unmatchedHid.ToList();
        }

        return new
        {
            ramMb,
            cpuPercent = _lastCpuPercent,
            pluginCount = _pluginIndex.Entries.Count,
            availableEffects = CanvasEngine.AvailableEffects,
            startupEnabled = StartupManager.IsEnabled(),
            startupMode = StartupManager.GetStatusMode(),
            startupApplyAndExit = cfg.StartupApplyAndExit,
            targetFps = cfg.TargetFps,
            paused = cfg.Paused,
            skipShutdownBlackout = cfg.SkipShutdownBlackout,
            allBrightnessSynced,
            effect = cfg.Effect,
            scan = new
            {
                isScanning = scanSnap.IsScanning,
                percent = scanSnap.Percent,
                stage = scanSnap.Stage,
                foundCount = scanSnap.FoundCount
            },
            motherboard = new { _cachedMobo.Manufacturer, _cachedMobo.Model },
            pawnIo = new { _cachedPawnIo.Installed, _cachedPawnIo.ImagePath },
            smbusStatus = _smbusStatus,
            adlGpuStatus = _adlGpuStatus,
            nvGpuStatus = _nvGpuStatus,
            pciDevices = _cachedPciDevices,
            devices,
            unmatchedHid = unmatched.Select(u => new
            {
                vendorId = $"0x{u.VendorId:X4}",
                productId = $"0x{u.ProductId:X4}",
                u.Manufacturer,
                u.Product,
                u.EndpointCount
            })
        };
    }

    public void Dispose()
    {
        _cts.Cancel();
        _canvasWake.Set();
        try { _canvasThread?.Join(1500); } catch { }
        lock (_workersLock)
        {
            foreach (var w in _workers.Values)
            {
                try { w.Dispose(); } catch { }
            }
            _workers.Clear();
            foreach (var b in _smbusBuses)
            {
                try { b.Dispose(); } catch { }
            }
            _smbusBuses.Clear();
            foreach (var g in _adlBuses)
            {
                try { g.Dispose(); } catch { }
            }
            _adlBuses.Clear();
            foreach (var nv in _nvGpuBuses)
            {
                try { nv.Dispose(); } catch { }
            }
            _nvGpuBuses.Clear();
        }
        _smbusScanGate.Dispose();
        _canvasWake.Dispose();
        _cts.Dispose();
    }
}
