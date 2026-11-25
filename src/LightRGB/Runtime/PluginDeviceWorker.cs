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

public interface IDeviceWorker : IDisposable
{
    string DeviceId { get; }
    string DisplayName { get; }
    string PluginPath { get; }
    string DeviceType { get; }
    ushort VendorId { get; }
    ushort ProductId { get; }
    DeviceRunMode Mode { get; }
    bool IsIdle { get; }
    int FramesRendered { get; }
    string LastError { get; }

    void Start();
    void ApplyConfig(DeviceConfig config, int globalBrightness, int globalTargetFps, bool globalPaused, bool skipShutdownBlackout);
    void QueueParameterUpdate(string property, JsonElement value);
    void WakeForRender(int frames = 2);
    IReadOnlyList<ChannelState> GetChannels();
    IReadOnlyList<SubdeviceState> GetSubdevices();
    IReadOnlyList<ControllableParamInfo> GetParameters();
    IReadOnlyList<string> GetRecentLogs();
}

public sealed class PluginDeviceWorker : IDeviceWorker
{
    private readonly PluginIndexEntry _plugin;
    private readonly IHidTransport _transport;
    private readonly CanvasEngine _canvas;
    private readonly string _pluginsDir;
    private readonly string _userPluginsDir;
    private readonly SystemInfoProvider _systemInfo;

    private readonly DeviceHostBridge _bridge;
    private readonly ConcurrentQueue<(string Prop, JsonElement Value)> _paramUpdates = new();
    private readonly ConcurrentQueue<ControllableParamInfo> _dynamicParamAdds = new();
    private readonly List<ControllableParamInfo> _params = [];
    private readonly object _paramLock = new();
    private readonly ManualResetEventSlim _wakeEvent = new(true);
    private readonly CancellationTokenSource _cts = new();

    private Thread? _thread;
    private volatile DeviceRunMode _mode = DeviceRunMode.Animated;
    private volatile int _globalTargetFps = 20;
    private volatile bool _globalPaused;
    private volatile bool _skipShutdownBlackout;
    private int _applyFramesRemaining = 3;
    private int _framesRendered;
    private volatile bool _isIdle;
    private string _lastError = "";

    public PluginDeviceWorker(
        string deviceId,
        PluginIndexEntry plugin,
        IHidTransport transport,
        CanvasEngine canvas,
        string pluginsDir,
        string userPluginsDir,
        SystemInfoProvider systemInfo,
        DeviceConfig initialConfig,
        int globalBrightness,
        int globalTargetFps,
        bool globalPaused,
        bool skipShutdownBlackout)
    {
        DeviceId = deviceId;
        _plugin = plugin;
        _transport = transport;
        _canvas = canvas;
        _pluginsDir = pluginsDir;
        _userPluginsDir = userPluginsDir;
        _systemInfo = systemInfo;

        _bridge = new DeviceHostBridge(transport, canvas, plugin.Name);
        _bridge.OnPropertyAdded = OnDynamicPropertyAdded;
        ApplyConfig(initialConfig, globalBrightness, globalTargetFps, globalPaused, skipShutdownBlackout);
    }

    public string DeviceId { get; }
    public string DisplayName => _bridge.DeviceName;
    public string PluginPath => _plugin.RelativePath;
    public string DeviceType => _plugin.DeviceType;
    public ushort VendorId => _transport.ActiveEndpoint.VendorId;
    public ushort ProductId => _transport.ActiveEndpoint.ProductId;
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
            Name = $"LightRGB-{DeviceId}"
        };
        _thread.Start();
    }

    public void ApplyConfig(DeviceConfig config, int globalBrightness, int globalTargetFps, bool globalPaused, bool skipShutdownBlackout)
    {
        _mode = config.Mode;
        _globalTargetFps = Math.Clamp(globalTargetFps, 1, 60);
        _globalPaused = globalPaused;
        _skipShutdownBlackout = skipShutdownBlackout;
        _bridge.ApplyDeviceConfig(config, globalBrightness);

        foreach (var kv in config.PluginParams)
            _paramUpdates.Enqueue((kv.Key, kv.Value));

        WakeForRender(3);
    }

    public void QueueParameterUpdate(string property, JsonElement value)
    {
        _paramUpdates.Enqueue((property, value));
        WakeForRender(3);
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
        DrainDynamicParamAdditions();
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

    private void OnDynamicPropertyAdded(string propJson)
    {
        try
        {
            var info = ParseParamElement(JsonDocument.Parse(propJson).RootElement);
            if (info is not null)
                _dynamicParamAdds.Enqueue(info);
        }
        catch { }
    }

    private void DrainDynamicParamAdditions()
    {
        while (_dynamicParamAdds.TryDequeue(out var info))
        {
            lock (_paramLock)
            {
                if (_params.All(p => !string.Equals(p.Property, info.Property, StringComparison.OrdinalIgnoreCase)))
                    _params.Add(info);
            }
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
            engine.Execute(DeviceHostBridge.JsDeviceShim);

            string escapedRel = _plugin.RelativePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string wrapper = $$"""
                import * as __mod from "plugin://{{escapedRel}}";
                globalThis.__plugin = __mod;
                """;
            engine.Execute(new DocumentInfo($"worker://{_plugin.RelativePath}") { Category = ModuleCategory.Standard }, wrapper);

            SelectValidatedEndpoint(engine);
            InitializeParameters(engine);
            ApplyQueuedParameterUpdates(engine, invokeChangedCallbacks: false);

            engine.Execute("""
                if (typeof globalThis.__plugin.Initialize === "function") {
                    globalThis.__plugin.Initialize();
                }
                """);

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
                    engine.Execute("""
                        if (typeof globalThis.__plugin.Render === "function") {
                            globalThis.__plugin.Render();
                        }
                        """);
                    Interlocked.Increment(ref _framesRendered);

                    if (!animated && remaining > 0)
                        Interlocked.Decrement(ref _applyFramesRemaining);

                    int fps = _bridge.RequestedFrameRateTarget.HasValue
                        ? Math.Min(_globalTargetFps, _bridge.RequestedFrameRateTarget.Value)
                        : _globalTargetFps;
                    fps = Math.Clamp(fps, 1, 60);
                    int targetMs = 1000 / fps;
                    int elapsed = (int)sw.ElapsedMilliseconds;
                    int sleepMs = targetMs - elapsed;
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
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _bridge.Log($"Plugin error: {ex.Message}");
        }
        finally
        {
            _isIdle = true;
            if (engine is not null && !_skipShutdownBlackout)
            {
                try
                {
                    engine.Execute("""
                        if (typeof globalThis.__plugin?.Shutdown === "function") {
                            globalThis.__plugin.Shutdown(false);
                        }
                        """);
                }
                catch { }
            }
            engine?.Dispose();
            _transport.Dispose();
        }
    }

    private void SelectValidatedEndpoint(V8ScriptEngine engine)
    {
        var endpoints = _transport.Endpoints;
        if (endpoints.Count == 0) return;

        string epJson = JsonSerializer.Serialize(endpoints.Select(e => new
        {
            @interface = e.Interface,
            usage = e.Usage,
            usage_page = e.UsagePage,
            collection = e.Collection,
            length = e.OutputReportLength
        }));

        engine.Script.__endpointsJson = epJson;
        var idxObj = engine.Evaluate("""
            (() => {
                if (typeof globalThis.__plugin.Validate !== "function") return 0;
                const eps = JSON.parse(globalThis.__endpointsJson);
                for (let i = 0; i < eps.length; i++) {
                    try {
                        if (globalThis.__plugin.Validate(eps[i])) return i;
                    } catch {}
                }
                return -1;
            })()
            """);

        int idx = Convert.ToInt32(idxObj);
        if (idx >= 0 && idx < endpoints.Count)
        {
            var ep = endpoints[idx];
            _transport.SetEndpoint(ep.Interface, ep.Usage, ep.UsagePage, ep.Collection);
        }
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
                        var p = ParseParamElement(el);
                        if (p is not null)
                            _params.Add(p);
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
            string prop = update.Prop;
            string rawJson = update.Value.GetRawText();

            engine.Script.__uProp = prop;
            engine.Script.__uValJson = rawJson;
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

            DrainDynamicParamAdditions();
            lock (_paramLock)
            {
                var p = _params.FirstOrDefault(x => string.Equals(x.Property, prop, StringComparison.OrdinalIgnoreCase));
                if (p is not null && applied is string aj)
                {
                    try { p.CurrentValue = JsonDocument.Parse(aj).RootElement.Clone(); } catch { }
                }
            }
        }
    }

    private static ControllableParamInfo? ParseParamElement(JsonElement el)
    {
        if (!el.TryGetProperty("property", out var propEl)) return null;
        string prop = propEl.GetString() ?? "";
        if (string.IsNullOrEmpty(prop)) return null;

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
        if (el.TryGetProperty("min", out var minEl) && TryGetDouble(minEl, out double minV)) info.Min = minV;
        if (el.TryGetProperty("max", out var maxEl) && TryGetDouble(maxEl, out double maxV)) info.Max = maxV;
        if (el.TryGetProperty("step", out var stepEl) && TryGetDouble(stepEl, out double stepV)) info.Step = stepV;
        return info;
    }

    private static bool TryGetDouble(JsonElement el, out double val)
    {
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetDouble(out val);
        if (el.ValueKind == JsonValueKind.String) return double.TryParse(el.GetString(), out val);
        val = 0;
        return false;
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
