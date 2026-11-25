using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Extraction;
using LightRGB.Hardware;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using Microsoft.ClearScript.V8;

namespace LightRGB.Runtime;

public sealed record SmbusDetectedDevice(PluginIndexEntry Plugin, ISmbusTransport Bus, byte Address);

public static class SmbusDeviceScanner
{
    private static readonly string[] RamPluginPriority =
    [
        "Asus/ENE_RAM.js",
        "Corsair/Ram/Corsair_Vengeance_Pro_Ram.js",
        "Corsair/Ram/Corsair_Dominator_Ram.js",
        "Corsair/Ram/Corsair_Dominator_Titanium_Ram.js",
        "Kingston/Kingston Hyper Fury Ram.js",
        "Crucial/Crucial Ballistix Ram.js",
        "Patriot/Patriot_Viper_RAM.js",
        "Patriot/Patriot_Viper_Steel_RGB_RAM.js"
    ];

    public static List<SmbusDetectedDevice> ScanRamPlugins(
        PluginIndexDocument index,
        IReadOnlyList<ISmbusTransport> buses,
        string pluginsDir,
        string userPluginsDir,
        SystemInfoProvider systemInfo,
        Action<string>? log = null,
        Action<int, int, string>? onProgress = null)
    {
        if (buses.Count == 0) return [];

        var candidates = new List<PluginIndexEntry>();
        foreach (var rel in RamPluginPriority)
        {
            var entry = index.Entries.FirstOrDefault(e =>
                e.Type.Equals("smbus", StringComparison.OrdinalIgnoreCase) &&
                e.RelativePath.Equals(rel, StringComparison.OrdinalIgnoreCase));
            if (entry is not null)
                candidates.Add(entry);
        }

        var sysBuses = buses.Where(b => b.IsSystemBus).ToList();
        return ScanPluginsCore(candidates, sysBuses, pluginsDir, userPluginsDir, systemInfo, "SMBus", log, onProgress);
    }

    public static List<SmbusDetectedDevice> ScanGpuPlugins(
        PluginIndexDocument index,
        IReadOnlyList<ISmbusTransport> gpuBuses,
        string pluginsDir,
        string userPluginsDir,
        SystemInfoProvider systemInfo,
        Action<string>? log = null,
        Action<int, int, string>? onProgress = null)
    {
        if (gpuBuses.Count == 0) return [];

        var candidates = index.Entries
            .Where(e => e.Type.Equals("smbus", StringComparison.OrdinalIgnoreCase) &&
                        e.DeviceType.Equals("gpu", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var validBuses = gpuBuses.Where(b => b.IsNvidiaBus || b.IsAmdGpuBus).ToList();
        return ScanPluginsCore(candidates, validBuses, pluginsDir, userPluginsDir, systemInfo, "GPU-I2C", log, onProgress);
    }

    private static List<SmbusDetectedDevice> ScanPluginsCore(
        IReadOnlyList<PluginIndexEntry> candidates,
        IReadOnlyList<ISmbusTransport> targetBuses,
        string pluginsDir,
        string userPluginsDir,
        SystemInfoProvider systemInfo,
        string tag,
        Action<string>? log,
        Action<int, int, string>? onProgress)
    {
        var found = new List<SmbusDetectedDevice>();
        if (candidates.Count == 0 || targetBuses.Count == 0) return found;

        int totalSteps = targetBuses.Count * candidates.Count;
        int doneSteps = 0;

        foreach (var bus in targetBuses)
        {
            var claimedAddresses = new HashSet<byte>();

            using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableDynamicModuleImports);
            engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
            engine.DocumentSettings.Loader = new SignalRgbModuleLoader(pluginsDir, userPluginsDir, systemInfo, log);

            var bridge = new DeviceHostBridge(null, new CanvasEngine(), $"{tag} Scanner");
            var busBridge = new SmbusBusBridge(bus, null, msg => log?.Invoke($"[{tag} Scan] {msg}"));
            engine.AddHostObject("_host", bridge);
            engine.AddHostObject("_busHost", busBridge);
            engine.Execute(DeviceHostBridge.JsDeviceShim);
            engine.Execute(SmbusBusBridge.JsFreeAddressBusShim);

            foreach (var plugin in candidates)
            {
                onProgress?.Invoke(doneSteps, totalSteps, plugin.Name);
                try
                {
                    string escaped = plugin.RelativePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    engine.Script.__scanResultJson = "[]";
                    string script = $$"""
                        import * as __mod from "plugin://{{escaped}}";
                        if (typeof __mod.Scan === "function") {
                            const addrs = __mod.Scan(globalThis.bus) || [];
                            globalThis.__scanResultJson = JSON.stringify(Array.isArray(addrs) ? addrs : []);
                        }
                        """;
                    engine.Execute(new DocumentInfo($"smbus-scan://{plugin.RelativePath}") { Category = ModuleCategory.Standard }, script);

                    if (engine.Script.__scanResultJson is string json)
                    {
                        using var jdoc = JsonDocument.Parse(json);
                        foreach (var el in jdoc.RootElement.EnumerateArray())
                        {
                            if (el.TryGetInt32(out int rawAddr) && rawAddr > 0 && rawAddr <= 0x7F)
                            {
                                byte addr = (byte)rawAddr;
                                if (SmbusSafetyPolicy.IsWriteAllowedForBus(bus, addr) && claimedAddresses.Add(addr))
                                {
                                    found.Add(new SmbusDetectedDevice(plugin, bus, addr));
                                    log?.Invoke($"[{tag}] Found {plugin.Name} at 0x{addr:X2} on {bus.Name}");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[{tag} Scan] {plugin.RelativePath} error: {ex.Message}");
                }
                finally
                {
                    doneSteps++;
                    onProgress?.Invoke(doneSteps, totalSteps, plugin.Name);
                }
            }
        }

        return found;
    }
}

public sealed class SmbusDeviceWorker : IDeviceWorker
{
    private readonly PluginIndexEntry _plugin;
    private readonly ISmbusTransport _bus;
    private readonly byte _address;
    private readonly CanvasEngine _canvas;
    private readonly string _pluginsDir;
    private readonly string _userPluginsDir;
    private readonly SystemInfoProvider _systemInfo;
    private readonly ReaderWriterLockSlim? _busScanGate;

    private readonly DeviceHostBridge _bridge;
    private readonly SmbusBusBridge _busBridge;
    private readonly ConcurrentQueue<(string Prop, JsonElement Value)> _paramUpdates = new();
    private readonly List<ControllableParamInfo> _params = [];
    private readonly object _paramLock = new();
    private readonly ManualResetEventSlim _wakeEvent = new(true);
    private readonly CancellationTokenSource _cts = new();

    private Thread? _thread;
    private volatile DeviceRunMode _mode = DeviceRunMode.Animated;
    private volatile int _globalTargetFps = 15;
    private volatile bool _globalPaused;
    private volatile bool _skipShutdownBlackout;
    private int _applyFramesRemaining = 2;
    private int _framesRendered;
    private volatile bool _isIdle;
    private string _lastError = "";

    public SmbusDeviceWorker(
        string deviceId,
        PluginIndexEntry plugin,
        ISmbusTransport bus,
        byte address,
        CanvasEngine canvas,
        string pluginsDir,
        string userPluginsDir,
        SystemInfoProvider systemInfo,
        DeviceConfig initialConfig,
        int globalBrightness,
        int globalTargetFps,
        bool globalPaused,
        bool skipShutdownBlackout,
        ReaderWriterLockSlim? busScanGate = null)
    {
        DeviceId = deviceId;
        _plugin = plugin;
        _bus = bus;
        _address = address;
        _canvas = canvas;
        _pluginsDir = pluginsDir;
        _userPluginsDir = userPluginsDir;
        _systemInfo = systemInfo;
        _busScanGate = busScanGate;

        _bridge = new DeviceHostBridge(null, canvas, $"{plugin.Name} (0x{address:X2})");
        _busBridge = new SmbusBusBridge(bus, address, msg => _bridge.Log(msg));
        ApplyConfig(initialConfig, globalBrightness, globalTargetFps, globalPaused, skipShutdownBlackout);
    }

    public string DeviceId { get; }
    public string DisplayName => _bridge.DeviceName;
    public string PluginPath => $"{_plugin.RelativePath} @ 0x{_address:X2}";
    public string DeviceType => string.IsNullOrWhiteSpace(_plugin.DeviceType) ? "ram" : _plugin.DeviceType;
    public ushort VendorId => _bus.VendorId;
    public ushort ProductId => _address;
    public DeviceRunMode Mode => _mode;
    public bool IsIdle => _isIdle;
    public int FramesRendered => Volatile.Read(ref _framesRendered);
    public string LastError => _lastError;

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = $"LightRGB-SMBus-{DeviceId}"
        };
        _thread.Start();
    }

    public void ApplyConfig(DeviceConfig config, int globalBrightness, int globalTargetFps, bool globalPaused, bool skipShutdownBlackout)
    {
        _mode = config.Mode;
        _globalTargetFps = Math.Clamp(globalTargetFps, 1, 15);
        _globalPaused = globalPaused;
        _skipShutdownBlackout = skipShutdownBlackout;
        _bridge.ApplyDeviceConfig(config, globalBrightness);

        foreach (var kv in config.PluginParams)
            _paramUpdates.Enqueue((kv.Key, kv.Value));

        WakeForRender(2);
    }

    public void QueueParameterUpdate(string property, JsonElement value)
    {
        _paramUpdates.Enqueue((property, value));
        WakeForRender(2);
    }

    public void WakeForRender(int frames = 2)
    {
        Interlocked.Exchange(ref _applyFramesRemaining, Math.Max(1, frames));
        _wakeEvent.Set();
    }

    public IReadOnlyList<ChannelState> GetChannels() => _bridge.GetChannelsSnapshot();
    public IReadOnlyList<SubdeviceState> GetSubdevices() => _bridge.GetSubdevicesSnapshot();
    public IReadOnlyList<string> GetRecentLogs() => _bridge.GetRecentLogs();

    public IReadOnlyList<ControllableParamInfo> GetParameters()
    {
        lock (_paramLock)
        {
            return _params.Select(p => new ControllableParamInfo
            {
                Property = p.Property,
                Group = p.Group,
                Label = p.Label,
                Description = p.Description,
                Type = p.Type,
                Default = p.Default,
                Values = p.Values.ToList(),
                Min = p.Min,
                Max = p.Max,
                Step = p.Step,
                CurrentValue = p.CurrentValue
            }).ToList();
        }
    }

    private void WorkerLoop()
    {
        V8ScriptEngine? engine = null;
        try
        {
            engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableDynamicModuleImports);
            engine.DocumentSettings.AccessFlags = DocumentAccessFlags.EnableFileLoading;
            engine.DocumentSettings.Loader = new SignalRgbModuleLoader(_pluginsDir, _userPluginsDir, _systemInfo, msg => _bridge.Log(msg));

            engine.AddHostObject("_host", _bridge);
            engine.AddHostObject("_busHost", _busBridge);
            engine.Execute(DeviceHostBridge.JsDeviceShim);
            engine.Execute(SmbusBusBridge.JsFixedAddressBusShim);

            string escapedRel = _plugin.RelativePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string wrapper = $$"""
                import * as __mod from "plugin://{{escapedRel}}";
                globalThis.__plugin = __mod;
                """;
            engine.Execute(new DocumentInfo($"smbus-worker://{_plugin.RelativePath}@{_address:X2}") { Category = ModuleCategory.Standard }, wrapper);

            InitializeParameters(engine);
            ApplyQueuedParameterUpdates(engine, invokeChangedCallbacks: false);

            WithScanReadLock(() =>
            {
                engine.Execute("""
                    if (typeof globalThis.__plugin.Initialize === "function") {
                        globalThis.__plugin.Initialize();
                    }
                    """);
            });

            var sw = Stopwatch.StartNew();
            while (!_cts.IsCancellationRequested)
            {
                ApplyQueuedParameterUpdates(engine, invokeChangedCallbacks: true);

                bool animated = _mode == DeviceRunMode.Animated && !_globalPaused;
                int remaining = Interlocked.CompareExchange(ref _applyFramesRemaining, 0, 0);

                if (animated || remaining > 0)
                {
                    _isIdle = false;
                    sw.Restart();
                    WithScanReadLock(() =>
                    {
                        engine.Execute("""
                            if (typeof globalThis.__plugin.Render === "function") {
                                globalThis.__plugin.Render();
                            }
                            """);
                    });
                    Interlocked.Increment(ref _framesRendered);

                    if (!animated && remaining > 0)
                        Interlocked.Decrement(ref _applyFramesRemaining);

                    int fps = Math.Clamp(_globalTargetFps, 1, 15);
                    int sleepMs = (1000 / fps) - (int)sw.ElapsedMilliseconds;
                    if (sleepMs > 1)
                        _cts.Token.WaitHandle.WaitOne(sleepMs);
                }
                else
                {
                    _isIdle = true;
                    _wakeEvent.Reset();
                    if (Interlocked.CompareExchange(ref _applyFramesRemaining, 0, 0) > 0 ||
                        (_mode == DeviceRunMode.Animated && !_globalPaused))
                    {
                        continue;
                    }
                    WaitHandle.WaitAny([_wakeEvent.WaitHandle, _cts.Token.WaitHandle]);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _bridge.Log($"SMBus plugin error: {ex.Message}");
        }
        finally
        {
            _isIdle = true;
            if (engine is not null && !_skipShutdownBlackout)
            {
                try
                {
                    WithScanReadLock(() =>
                    {
                        engine.Execute("""
                            if (typeof globalThis.__plugin?.Shutdown === "function") {
                                globalThis.__plugin.Shutdown(false);
                            }
                            """);
                    });
                }
                catch { }
            }
            engine?.Dispose();
        }
    }

    private void WithScanReadLock(Action action)
    {
        if (_busScanGate is null)
        {
            action();
            return;
        }
        _busScanGate.EnterReadLock();
        try { action(); }
        finally { _busScanGate.ExitReadLock(); }
    }

    private void InitializeParameters(V8ScriptEngine engine)
    {
        var jsonObj = engine.Evaluate("""
            (() => {
                if (typeof globalThis.__plugin.ControllableParameters !== "function") return "[]";
                const list = globalThis.__plugin.ControllableParameters() || [];
                globalThis.__paramDefs = {};
                for (const p of list) {
                    if (p && p.property) {
                        globalThis.__paramDefs[p.property] = p;
                        if (p.default !== undefined) {
                            globalThis[p.property] = globalThis._coerceParamValue(p, p.default);
                        }
                    }
                }
                return JSON.stringify(list);
            })()
            """);

        if (jsonObj is string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                lock (_paramLock)
                {
                    _params.Clear();
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (!el.TryGetProperty("property", out var propEl)) continue;
                        string prop = propEl.GetString() ?? "";
                        if (string.IsNullOrEmpty(prop)) continue;
                        var info = new ControllableParamInfo
                        {
                            Property = prop,
                            Group = el.TryGetProperty("group", out var g) ? g.GetString() ?? "" : "",
                            Label = el.TryGetProperty("label", out var l) ? l.GetString() ?? prop : prop,
                            Description = el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                            Type = el.TryGetProperty("type", out var t) ? t.GetString() ?? "text" : "text",
                        };
                        if (el.TryGetProperty("default", out var defEl))
                        {
                            info.Default = defEl.Clone();
                            info.CurrentValue = defEl.Clone();
                        }
                        if (el.TryGetProperty("values", out var valsEl) && valsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var v in valsEl.EnumerateArray())
                                info.Values.Add(v.ToString());
                        }
                        _params.Add(info);
                    }
                }
            }
            catch { }
        }
    }

    private void ApplyQueuedParameterUpdates(V8ScriptEngine engine, bool invokeChangedCallbacks)
    {
        while (_paramUpdates.TryDequeue(out var update))
        {
            engine.Script.__uProp = update.Prop;
            engine.Script.__uValJson = update.Value.GetRawText();
            engine.Script.__uInvoke = invokeChangedCallbacks;

            var applied = engine.Evaluate("""
                (() => {
                    const prop = globalThis.__uProp;
                    const raw = JSON.parse(globalThis.__uValJson);
                    const def = (globalThis.__paramDefs || {})[prop];
                    const coerced = globalThis._coerceParamValue(def, raw);
                    globalThis[prop] = coerced;
                    if (globalThis.__uInvoke) {
                        const cbName = "on" + prop + "Changed";
                        if (typeof globalThis.__plugin[cbName] === "function") {
                            globalThis.__plugin[cbName]();
                        }
                    }
                    return JSON.stringify(coerced);
                })()
                """);

            lock (_paramLock)
            {
                var p = _params.FirstOrDefault(x => string.Equals(x.Property, update.Prop, StringComparison.OrdinalIgnoreCase));
                if (p is not null && applied is string aj)
                {
                    try { p.CurrentValue = JsonDocument.Parse(aj).RootElement.Clone(); } catch { }
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wakeEvent.Set();
        try { _thread?.Join(2000); } catch { }
        _wakeEvent.Dispose();
        _cts.Dispose();
    }
}
