using System.Collections.Concurrent;
using System.Text.Json;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Hardware;

namespace LightRGB.Runtime;

public sealed class ChannelState
{
    public string Name { get; set; } = "";
    public int MaxLeds { get; set; }
    public int LedLimit { get; set; }
    public int LedCount { get; set; }
}

public sealed class SubdeviceState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;
    public string ImageUrl { get; set; } = "";
    public int LedCount { get; set; }
}

public sealed class ControllableParamInfo
{
    public string Property { get; set; } = "";
    public string Group { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "text";
    public JsonElement? Default { get; set; }
    public List<string> Values { get; set; } = [];
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public object? CurrentValue { get; set; }
}

public sealed class DeviceHostBridge
{
    private readonly IHidTransport? _transport;
    private readonly CanvasEngine _canvas;
    private readonly object _stateLock = new();
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly Dictionary<string, ChannelState> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SubdeviceState> _subdevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _configuredChannelCounts = new(StringComparer.OrdinalIgnoreCase);

    private DeviceRunMode _mode = DeviceRunMode.Animated;
    private string _staticColor = "#009BDE";
    private int _effectiveBrightness = 100;
    private float _canvasX = 0.5f;
    private float _canvasY = 0.5f;
    private float _scale = 1.0f;
    private int _sizeW = 1;
    private int _sizeH = 1;

    public DeviceHostBridge(IHidTransport? transport, CanvasEngine canvas, string initialName)
    {
        _transport = transport;
        _canvas = canvas;
        DeviceName = initialName;
    }

    public string DeviceName { get; private set; }
    public string ImageUrl { get; private set; } = "";
    public int? RequestedFrameRateTarget { get; private set; }
    public int GlobalLedLimit { get; private set; }
    public int ControllableLedCount { get; private set; }
    public bool SupportsFanControl { get; set; }
    public Action<string>? OnPropertyAdded { get; set; }

    public void ApplyDeviceConfig(DeviceConfig cfg, int globalBrightness)
    {
        lock (_stateLock)
        {
            _mode = cfg.Mode;
            _staticColor = cfg.StaticColor;
            _effectiveBrightness = cfg.SyncBrightness
                ? Math.Clamp(globalBrightness, 0, 100)
                : Math.Clamp(cfg.Brightness, 0, 100);
            _canvasX = cfg.CanvasX;
            _canvasY = cfg.CanvasY;
            _scale = cfg.Scale <= 0 ? 1.0f : cfg.Scale;
            _configuredChannelCounts.Clear();
            foreach (var kv in cfg.ChannelLedCounts)
            {
                _configuredChannelCounts[kv.Key] = Math.Max(0, kv.Value);
                if (_channels.TryGetValue(kv.Key, out var ch))
                    ch.LedCount = Math.Clamp(kv.Value, 0, ch.MaxLeds > 0 ? ch.MaxLeds : 512);
            }
        }
    }

    public IReadOnlyList<ChannelState> GetChannelsSnapshot()
    {
        lock (_stateLock)
        {
            return _channels.Values.Select(c => new ChannelState
            {
                Name = c.Name,
                MaxLeds = c.MaxLeds,
                LedLimit = c.LedLimit,
                LedCount = c.LedCount
            }).ToList();
        }
    }

    public IReadOnlyList<SubdeviceState> GetSubdevicesSnapshot()
    {
        lock (_stateLock)
        {
            return _subdevices.Values.Select(s => new SubdeviceState
            {
                Id = s.Id,
                Name = s.Name,
                Width = s.Width,
                Height = s.Height,
                ImageUrl = s.ImageUrl,
                LedCount = s.LedCount
            }).ToList();
        }
    }

    public IReadOnlyList<string> GetRecentLogs() => _logs.ToArray();

    // --- Methods called by JS shim ---

    public int WriteBytes(string base64OrHex) => 0;

    public int WriteJson(string jsonArray, int length)
    {
        if (_transport is null) return 0;
        byte[] bytes = ParseByteArray(jsonArray, length);
        return _transport.Write(bytes, Math.Max(length, bytes.Length));
    }

    public int SendReportJson(string jsonArray, int length)
    {
        if (_transport is null) return 0;
        byte[] bytes = ParseByteArray(jsonArray, length);
        return _transport.SendFeatureReport(bytes, Math.Max(length, bytes.Length));
    }

    public string ReadJson(string jsonArray, int length)
    {
        if (_transport is null) return "[]";
        byte[]? cmd = string.IsNullOrEmpty(jsonArray) ? null : ParseByteArray(jsonArray, 0);
        byte[] res = _transport.Read(cmd, length);
        return JsonSerializer.Serialize(Array.ConvertAll(res, static b => (int)b));
    }

    public string GetReportJson(string jsonArray, int length)
    {
        if (_transport is null) return "[]";
        byte[] cmd = ParseByteArray(jsonArray, 0);
        byte[] res = _transport.GetFeatureReport(cmd, length);
        return JsonSerializer.Serialize(Array.ConvertAll(res, static b => (int)b));
    }

    public void ClearReadBuffer() => _transport?.ClearReadBuffer();
    public int GetLastReadSize() => _transport?.LastReadSize ?? 0;

    public bool SetEndpoint(int iface, int usage, int usagePage, int collection)
        => _transport?.SetEndpoint(
            iface < 0 ? null : iface,
            usage < 0 ? null : usage,
            usagePage < 0 ? null : usagePage,
            collection < 0 ? null : collection) ?? false;

    public string GetHidEndpointsJson()
    {
        if (_transport is null) return "[]";
        var list = _transport.Endpoints.Select(e => new
        {
            @interface = e.Interface,
            usage = e.Usage,
            usage_page = e.UsagePage,
            collection = e.Collection,
            length = e.OutputReportLength
        });
        return JsonSerializer.Serialize(list);
    }

    public int ProductId() => _transport?.ActiveEndpoint.ProductId ?? 0;
    public int VendorId() => _transport?.ActiveEndpoint.VendorId ?? 0;

    public void Pause(int ms)
    {
        if (ms > 0)
            Thread.Sleep(Math.Min(ms, 2000));
    }

    public void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _logs.Enqueue(line);
        while (_logs.Count > 50)
            _logs.TryDequeue(out _);
    }

    public void SetName(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            DeviceName = name.Trim();
    }

    public void SetSize(int w, int h)
    {
        lock (_stateLock)
        {
            _sizeW = Math.Max(1, w);
            _sizeH = Math.Max(1, h);
        }
    }

    public void SetImageFromUrl(string url) => ImageUrl = url ?? "";
    public void SetFrameRateTarget(int fps) => RequestedFrameRateTarget = Math.Clamp(fps, 1, 120);
    public void SetLedLimit(int limit) => GlobalLedLimit = Math.Max(0, limit);
    public void SetControllableLeds(int count) => ControllableLedCount = Math.Max(0, count);
    public int GetLedCount() => ControllableLedCount;

    public int SampleColorPacked(double x, double y)
    {
        int w, h;
        lock (_stateLock) { w = _sizeW; h = _sizeH; }
        var (r, g, b) = SampleNormalized(x, y, w, h);
        return (r << 16) | (g << 8) | b;
    }

    public int SampleSubdeviceColorPacked(string id, double x, double y)
    {
        int w = 1, h = 1;
        lock (_stateLock)
        {
            if (_subdevices.TryGetValue(id ?? "", out var sub))
            {
                w = sub.Width;
                h = sub.Height;
            }
        }
        var (r, g, b) = SampleNormalized(x, y, w, h);
        return (r << 16) | (g << 8) | b;
    }

    public int GetStaticOrCanvasPacked(double normX, double normY)
    {
        var (r, g, b) = SampleNormalized(normX, normY, 1, 1);
        return (r << 16) | (g << 8) | b;
    }

    public void AddChannel(string name, int maxLeds, int ledLimit)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_stateLock)
        {
            int defaultCount = maxLeds > 0 ? Math.Min(maxLeds, 120) : 80;
            if (_configuredChannelCounts.TryGetValue(name, out int saved))
                defaultCount = Math.Clamp(saved, 0, maxLeds > 0 ? maxLeds : 512);

            _channels[name] = new ChannelState
            {
                Name = name,
                MaxLeds = maxLeds,
                LedLimit = ledLimit > 0 ? ledLimit : maxLeds,
                LedCount = defaultCount
            };
        }
    }

    public void RemoveChannel(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        lock (_stateLock) { _channels.Remove(name); }
    }

    public string GetChannelNamesJson()
    {
        lock (_stateLock)
        {
            return JsonSerializer.Serialize(_channels.Keys.ToArray());
        }
    }

    public int GetChannelLedCount(string name)
    {
        lock (_stateLock)
        {
            return _channels.TryGetValue(name ?? "", out var ch) ? ch.LedCount : 0;
        }
    }

    public int GetChannelLedLimit(string name)
    {
        lock (_stateLock)
        {
            return _channels.TryGetValue(name ?? "", out var ch) ? ch.LedLimit : 0;
        }
    }

    public void SetChannelLedLimit(string name, int limit)
    {
        lock (_stateLock)
        {
            if (_channels.TryGetValue(name ?? "", out var ch))
                ch.LedLimit = Math.Max(0, limit);
        }
    }

    public string GetChannelColorsJson(string name, string mode, string order)
    {
        int count;
        lock (_stateLock)
        {
            count = _channels.TryGetValue(name ?? "", out var ch) ? ch.LedCount : 0;
        }
        return BuildColorArrayJson(null, count, mode, order, useCanvasStrip: true);
    }

    public string CreateColorArrayJson(string hexColor, int count, string mode, string order)
    {
        return BuildColorArrayJson(hexColor, count, mode, order, useCanvasStrip: false);
    }

    public string GetChannelPulseColor(string name)
    {
        var (r, g, b) = SampleNormalized(0.5, 0.5, 1, 1);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public void CreateSubdevice(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_stateLock)
        {
            if (!_subdevices.ContainsKey(id))
                _subdevices[id] = new SubdeviceState { Id = id, Name = id };
        }
    }

    public void RemoveSubdevice(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_stateLock) { _subdevices.Remove(id); }
    }

    public string GetCurrentSubdevicesJson()
    {
        lock (_stateLock) { return JsonSerializer.Serialize(_subdevices.Keys.ToArray()); }
    }

    public void SetSubdeviceName(string id, string name)
    {
        lock (_stateLock)
        {
            if (_subdevices.TryGetValue(id ?? "", out var s)) s.Name = name ?? id ?? "";
        }
    }

    public void SetSubdeviceSize(string id, int w, int h)
    {
        lock (_stateLock)
        {
            if (_subdevices.TryGetValue(id ?? "", out var s))
            {
                s.Width = Math.Max(1, w);
                s.Height = Math.Max(1, h);
            }
        }
    }

    public void SetSubdeviceLeds(string id, int ledCount)
    {
        lock (_stateLock)
        {
            if (_subdevices.TryGetValue(id ?? "", out var s)) s.LedCount = Math.Max(0, ledCount);
        }
    }

    public void SetSubdeviceImageUrl(string id, string url)
    {
        lock (_stateLock)
        {
            if (_subdevices.TryGetValue(id ?? "", out var s)) s.ImageUrl = url ?? "";
        }
    }

    public void NotifyPropertyAdded(string propJson) => OnPropertyAdded?.Invoke(propJson);

    private (byte R, byte G, byte B) SampleNormalized(double x, double y, int w, int h)
    {
        DeviceRunMode mode;
        string staticHex;
        int bright;
        float cx, cy, scale;

        lock (_stateLock)
        {
            mode = _mode;
            staticHex = _staticColor;
            bright = _effectiveBrightness;
            cx = _canvasX;
            cy = _canvasY;
            scale = _scale;
        }

        if (mode == DeviceRunMode.Off)
            return (0, 0, 0);

        if (mode == DeviceRunMode.Static)
            return CanvasEngine.ApplyBrightness(CanvasEngine.ParseHexColor(staticHex), bright);

        float localX = w > 1 ? (float)(x / (w - 1)) : 0.5f;
        float localY = h > 1 ? (float)(y / (h - 1)) : 0.5f;
        float span = 0.35f * scale;
        float nx = Math.Clamp(cx + (localX - 0.5f) * span, 0f, 1f);
        float ny = Math.Clamp(cy + (localY - 0.5f) * span, 0f, 1f);
        return CanvasEngine.ApplyBrightness(_canvas.SampleUnscaled(nx, ny), bright);
    }

    private string BuildColorArrayJson(string? hexColor, int count, string? mode, string? order, bool useCanvasStrip)
    {
        count = Math.Clamp(count, 0, 2048);
        bool separate = string.Equals(mode, "Separate", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(mode, "Seperate", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(mode, "Planar", StringComparison.OrdinalIgnoreCase);

        (int i0, int i1, int i2) = ParseOrder(order);

        if (separate)
        {
            var c0 = new int[count];
            var c1 = new int[count];
            var c2 = new int[count];
            for (int i = 0; i < count; i++)
            {
                var rgb = GetLedRgb(hexColor, i, count, useCanvasStrip);
                int[] arr = [rgb.R, rgb.G, rgb.B];
                c0[i] = arr[i0];
                c1[i] = arr[i1];
                c2[i] = arr[i2];
            }
            return JsonSerializer.Serialize(new[] { c0, c1, c2 });
        }
        else
        {
            var flat = new int[count * 3];
            for (int i = 0; i < count; i++)
            {
                var rgb = GetLedRgb(hexColor, i, count, useCanvasStrip);
                int[] arr = [rgb.R, rgb.G, rgb.B];
                int baseIdx = i * 3;
                flat[baseIdx] = arr[i0];
                flat[baseIdx + 1] = arr[i1];
                flat[baseIdx + 2] = arr[i2];
            }
            return JsonSerializer.Serialize(flat);
        }
    }

    private (byte R, byte G, byte B) GetLedRgb(string? hexColor, int index, int count, bool useCanvasStrip)
    {
        if (!useCanvasStrip && !string.IsNullOrWhiteSpace(hexColor))
        {
            return CanvasEngine.ParseHexColor(hexColor);
        }

        double normX = count > 1 ? index / (double)(count - 1) : 0.5;
        return SampleNormalized(normX, 0.5, 2, 2);
    }

    private static (int, int, int) ParseOrder(string? order) => (order?.ToUpperInvariant()) switch
    {
        "GRB" => (1, 0, 2),
        "BRG" => (2, 0, 1),
        "RBG" => (0, 2, 1),
        "BGR" => (2, 1, 0),
        "GBR" => (1, 2, 0),
        _ => (0, 1, 2)
    };

    private static byte[] ParseByteArray(string json, int minLength)
    {
        if (string.IsNullOrWhiteSpace(json))
            return minLength > 0 ? new byte[minLength] : [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return minLength > 0 ? new byte[minLength] : [];

            int arrLen = doc.RootElement.GetArrayLength();
            int len = Math.Max(arrLen, minLength);
            var bytes = new byte[len];
            int idx = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (idx >= len) break;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v))
                    bytes[idx] = unchecked((byte)(v & 0xFF));
                idx++;
            }
            return bytes;
        }
        catch
        {
            return minLength > 0 ? new byte[minLength] : [];
        }
    }

    public const string JsDeviceShim = """
        globalThis.console = {
            log(...args) { _host.Log(args.map(a => typeof a === "object" ? JSON.stringify(a) : String(a)).join(" ")); },
            warn(...args) { _host.Log("[warn] " + args.map(a => String(a)).join(" ")); },
            error(...args) { _host.Log("[error] " + args.map(a => String(a)).join(" ")); },
            info(...args) { _host.Log(args.map(a => String(a)).join(" ")); }
        };

        const _dynamicProps = {};

        globalThis.device = {
            write(data, length) {
                const arr = Array.isArray(data) ? data : [];
                const len = (length === undefined || length === null) ? arr.length : (length | 0);
                const clean = new Array(arr.length);
                for (let i = 0; i < arr.length; i++) clean[i] = (arr[i] | 0) & 0xff;
                return _host.WriteJson(JSON.stringify(clean), len);
            },
            send_report(data, length) {
                const arr = Array.isArray(data) ? data : [];
                const len = (length === undefined || length === null) ? arr.length : (length | 0);
                const clean = new Array(arr.length);
                for (let i = 0; i < arr.length; i++) clean[i] = (arr[i] | 0) & 0xff;
                return _host.SendReportJson(JSON.stringify(clean), len);
            },
            read(data, length) {
                const arr = Array.isArray(data) ? data : [];
                const len = (length === undefined || length === null) ? 65 : (length | 0);
                const clean = new Array(arr.length);
                for (let i = 0; i < arr.length; i++) clean[i] = (arr[i] | 0) & 0xff;
                return JSON.parse(_host.ReadJson(JSON.stringify(clean), len));
            },
            get_report(data, length) {
                const arr = Array.isArray(data) ? data : [];
                const len = (length === undefined || length === null) ? 65 : (length | 0);
                const clean = new Array(arr.length);
                for (let i = 0; i < arr.length; i++) clean[i] = (arr[i] | 0) & 0xff;
                return JSON.parse(_host.GetReportJson(JSON.stringify(clean), len));
            },
            clearReadBuffer() { _host.ClearReadBuffer(); },
            getLastReadSize() { return _host.GetLastReadSize(); },
            set_endpoint(iface, usage, usagePage, collection) {
                return _host.SetEndpoint(
                    iface === undefined || iface === null ? -1 : (iface | 0),
                    usage === undefined || usage === null ? -1 : (usage | 0),
                    usagePage === undefined || usagePage === null ? -1 : (usagePage | 0),
                    collection === undefined || collection === null ? -1 : (collection | 0)
                );
            },
            getHidEndpoints() { return JSON.parse(_host.GetHidEndpointsJson()); },
            productId() { return _host.ProductId(); },
            vendorId() { return _host.VendorId(); },
            pause(ms) { _host.Pause(ms | 0); },
            log(msg) { _host.Log(String(msg)); },
            notify(title, msg) { _host.Log(`[notify] ${title}: ${msg}`); },
            setName(name) { _host.SetName(String(name)); },
            setSize(size) {
                if (Array.isArray(size) && size.length >= 2) _host.SetSize(size[0] | 0, size[1] | 0);
            },
            setControllableLeds(names, positions) {
                const count = Array.isArray(names) ? names.length : (names | 0);
                _host.SetControllableLeds(count);
            },
            setImageFromUrl(url) { _host.SetImageFromUrl(String(url || "")); },
            setFrameRateTarget(fps) { _host.SetFrameRateTarget(fps | 0); },
            SetLedLimit(limit) { _host.SetLedLimit(limit | 0); },
            getLedCount() { return _host.GetLedCount(); },
            color(x, y) {
                const c = _host.SampleColorPacked(Number(x) || 0, Number(y) || 0);
                return [(c >> 16) & 0xff, (c >> 8) & 0xff, c & 0xff];
            },
            subdeviceColor(id, x, y) {
                const c = _host.SampleSubdeviceColorPacked(String(id || ""), Number(x) || 0, Number(y) || 0);
                return [(c >> 16) & 0xff, (c >> 8) & 0xff, c & 0xff];
            },
            createColorArray(hex, count, mode, order) {
                return JSON.parse(_host.CreateColorArrayJson(String(hex || "#000000"), count | 0, String(mode || "Inline"), String(order || "RGB")));
            },
            getChannelPulseColor(name) { return _host.GetChannelPulseColor(String(name || "")); },
            addChannel(name, maxLeds, ledLimit) {
                _host.AddChannel(String(name || ""), maxLeds | 0, (ledLimit === undefined ? maxLeds : ledLimit) | 0);
            },
            removeChannel(name) { _host.RemoveChannel(String(name || "")); },
            getChannelNames() { return JSON.parse(_host.GetChannelNamesJson()); },
            channel(name) {
                const n = String(name || "");
                return {
                    get ledCount() { return _host.GetChannelLedCount(n); },
                    LedCount() { return _host.GetChannelLedCount(n); },
                    get LedLimit() { return _host.GetChannelLedLimit(n); },
                    SetLedLimit(lim) { _host.SetChannelLedLimit(n, lim | 0); },
                    shouldPulseColors() { return false; },
                    getColors(mode, order) {
                        return JSON.parse(_host.GetChannelColorsJson(n, String(mode || "Inline"), String(order || "RGB")));
                    },
                    getPulse(count, mode, order) {
                        return JSON.parse(_host.GetChannelColorsJson(n, String(mode || "Inline"), String(order || "RGB")));
                    },
                    getComponentNames() { return []; },
                    getComponentData() { return {}; }
                };
            },
            createSubdevice(id) { _host.CreateSubdevice(String(id || "")); },
            removeSubdevice(id) { _host.RemoveSubdevice(String(id || "")); },
            getCurrentSubdevices() { return JSON.parse(_host.GetCurrentSubdevicesJson()); },
            setSubdeviceName(id, name) { _host.SetSubdeviceName(String(id || ""), String(name || "")); },
            setSubdeviceSize(id, w, h) { _host.SetSubdeviceSize(String(id || ""), w | 0, h | 0); },
            setSubdeviceLeds(id, names, positions) {
                const cnt = Array.isArray(names) ? names.length : (names | 0);
                _host.SetSubdeviceLeds(String(id || ""), cnt);
            },
            setSubdeviceImageUrl(id, url) { _host.SetSubdeviceImageUrl(String(id || ""), String(url || "")); },
            addProperty(prop) {
                if (prop && prop.property) {
                    _dynamicProps[prop.property] = prop;
                    if (globalThis[prop.property] === undefined && prop.default !== undefined) {
                        globalThis[prop.property] = _coerceParamValue(prop, prop.default);
                    }
                    _host.NotifyPropertyAdded(JSON.stringify(prop));
                }
            },
            removeProperty(name) { delete _dynamicProps[name]; },
            getProperty(name) { return globalThis[name]; },
            addFeature() {},
            fanControlDisabled() { return true; },
            lcdControlAllowed() { return false; },
            createFanControl() {},
            removeFanControl() {},
            getNormalizedFanlevel() { return 0; },
            setRPM() {},
            createTemperatureSensor() {},
            SetTemperature() {},
            bulk_transfer() { return []; },
            control_transfer() { return []; }
        };

        globalThis._coerceParamValue = function(def, val) {
            if (!def) return val;
            if (def.type === "boolean") {
                if (typeof val === "boolean") return val;
                return String(val).toLowerCase() === "true";
            }
            if (def.type === "number") {
                const n = Number(val);
                return Number.isFinite(n) ? n : Number(def.default || 0);
            }
            return val;
        };
        """;
}
