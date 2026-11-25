using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Hardware;

namespace LightRGB.Runtime;

public static class SapphireNitroGlowSafety
{
    public const byte DeviceAddress = 0x28;
    public const byte RegExternalControl = 0x0F;
    public const byte RegMode = 0x10;
    public const byte RegRed = 0x1A;
    public const byte RegGreen = 0x1B;
    public const byte RegBlue = 0x1C;

    public static bool IsSafeWrite(byte address, byte register)
        => address == DeviceAddress &&
           (register == RegExternalControl ||
            register == RegMode ||
            register == RegRed ||
            register == RegGreen ||
            register == RegBlue);
}

public sealed class SapphireGpuWorker : IDeviceWorker
{
    private const byte ModeRainbow = 0x00;
    private const byte ModeRunway = 0x01;
    private const byte ModeColorCycle = 0x02;
    private const byte ModeSerial = 0x03;
    private const byte ModeSapphireBlue = 0x04;
    private const byte ModeCustom = 0x06;
    private const byte ModeOff = 0x07;

    private static readonly string[] GpuModeOptions =
    [
        "Custom (Software)",
        "External ARGB Header",
        "Rainbow",
        "Runway",
        "Color Cycle",
        "Serial",
        "Sapphire Blue",
        "Off"
    ];

    private static readonly string[] ColorOrderOptions =
    [
        "RGB",
        "RBG",
        "GRB",
        "GBR",
        "BRG",
        "BGR"
    ];

    private readonly IAmdI2cTransport _i2c;
    private readonly CanvasEngine _canvas;
    private readonly ManualResetEventSlim _wakeEvent = new(true);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<string> _logs = new();
    private readonly object _cfgLock = new();

    private Thread? _thread;
    private volatile DeviceRunMode _mode = DeviceRunMode.Animated;
    private string _staticColor = "#009BDE";
    private int _effectiveBrightness = 100;
    private float _canvasX = 0.5f;
    private float _canvasY = 0.5f;
    private string _gpuMode = "Custom (Software)";
    private string _colorOrder = "RGB";
    private double _gamma = 2.2;
    private int _redBalance = 100;
    private int _greenBalance = 100;
    private int _blueBalance = 100;
    private volatile int _globalTargetFps = 12;
    private volatile bool _globalPaused;
    private volatile bool _skipShutdownBlackout;
    private int _applyFramesRemaining = 2;
    private int _framesRendered;
    private volatile bool _isIdle;
    private string _lastError = "";

    private byte? _lastExt;
    private byte? _lastModeByte;
    private (byte C0, byte C1, byte C2)? _lastHwColors;

    public SapphireGpuWorker(
        string deviceId,
        string displayName,
        IAmdI2cTransport i2c,
        CanvasEngine canvas,
        DeviceConfig initialConfig,
        int globalBrightness,
        int globalTargetFps,
        bool globalPaused,
        bool skipShutdownBlackout)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        _i2c = i2c;
        _canvas = canvas;
        ApplyConfig(initialConfig, globalBrightness, globalTargetFps, globalPaused, skipShutdownBlackout);
    }

    public string DeviceId { get; }
    public string DisplayName { get; }
    public string PluginPath => "AMD ADL I2C -> Sapphire Nitro Glow V3 (0x28)";
    public string DeviceType => "gpu";
    public ushort VendorId => _i2c.SubVendorId != 0 ? _i2c.SubVendorId : _i2c.VendorId;
    public ushort ProductId => _i2c.SubDeviceId != 0 ? _i2c.SubDeviceId : _i2c.DeviceId;
    public DeviceRunMode Mode => _mode;
    public bool IsIdle => _isIdle;
    public int FramesRendered => Volatile.Read(ref _framesRendered);
    public string LastError => _lastError;

    public static bool TryDetect(IAmdI2cTransport bus, out string detectedName)
    {
        detectedName = "";
        if (bus.SubVendorId != 0x1DA2)
            return false;

        int probe = bus.ReadByte(SapphireNitroGlowSafety.DeviceAddress);
        if (probe < 0)
            return false;

        detectedName = (bus.DeviceId, bus.SubDeviceId) switch
        {
            (0x7550, 0x3490) => "Sapphire Radeon RX 9070 XT Pure",
            (0x7550, 0xE489) or (0x7550, 0x4892) => "Sapphire Radeon RX 9070 XT Nitro+",
            (0x7550, 0x4499) => "Sapphire Radeon RX 9070 Pure",
            _ => string.IsNullOrWhiteSpace(bus.AdapterName)
                ? $"Sapphire Radeon GPU (0x{bus.DeviceId:X4}:0x{bus.SubDeviceId:X4})"
                : $"{bus.AdapterName} (Sapphire Nitro Glow)"
        };
        return true;
    }

    public void Start()
    {
        if (_thread is not null) return;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = $"LightRGB-GPU-{DeviceId}"
        };
        _thread.Start();
    }

    public void ApplyConfig(DeviceConfig config, int globalBrightness, int globalTargetFps, bool globalPaused, bool skipShutdownBlackout)
    {
        lock (_cfgLock)
        {
            _mode = config.Mode;
            _staticColor = config.StaticColor;
            _effectiveBrightness = config.SyncBrightness
                ? Math.Clamp(globalBrightness, 0, 100)
                : Math.Clamp(config.Brightness, 0, 100);
            _canvasX = config.CanvasX;
            _canvasY = config.CanvasY;
            _globalTargetFps = Math.Clamp(globalTargetFps, 1, 12);
            _globalPaused = globalPaused;
            _skipShutdownBlackout = skipShutdownBlackout;

            if (config.PluginParams.TryGetValue("gpuMode", out var gm) && gm.ValueKind == JsonValueKind.String)
            {
                string? val = gm.GetString();
                if (!string.IsNullOrWhiteSpace(val)) _gpuMode = val;
            }
            if (config.PluginParams.TryGetValue("colorOrder", out var co) && co.ValueKind == JsonValueKind.String)
            {
                string? val = co.GetString();
                if (!string.IsNullOrWhiteSpace(val)) _colorOrder = val.ToUpperInvariant();
            }
            if (config.PluginParams.TryGetValue("gamma", out var gmVal) && TryReadDouble(gmVal, out double gammaVal))
                _gamma = Math.Clamp(gammaVal, 1.0, 3.0);
            if (config.PluginParams.TryGetValue("redBalance", out var rb) && TryReadInt(rb, out int rVal))
                _redBalance = Math.Clamp(rVal, 0, 100);
            if (config.PluginParams.TryGetValue("greenBalance", out var gb) && TryReadInt(gb, out int gVal))
                _greenBalance = Math.Clamp(gVal, 0, 100);
            if (config.PluginParams.TryGetValue("blueBalance", out var bb) && TryReadInt(bb, out int bVal))
                _blueBalance = Math.Clamp(bVal, 0, 100);
        }
        WakeForRender(2);
    }

    public void QueueParameterUpdate(string property, JsonElement value)
    {
        lock (_cfgLock)
        {
            if (string.Equals(property, "gpuMode", StringComparison.OrdinalIgnoreCase) && value.ValueKind == JsonValueKind.String)
                _gpuMode = value.GetString() ?? "Custom (Software)";
            else if (string.Equals(property, "colorOrder", StringComparison.OrdinalIgnoreCase) && value.ValueKind == JsonValueKind.String)
                _colorOrder = (value.GetString() ?? "RGB").ToUpperInvariant();
            else if (string.Equals(property, "gamma", StringComparison.OrdinalIgnoreCase) && TryReadDouble(value, out double gmf))
                _gamma = Math.Clamp(gmf, 1.0, 3.0);
            else if (string.Equals(property, "redBalance", StringComparison.OrdinalIgnoreCase) && TryReadInt(value, out int rv))
                _redBalance = Math.Clamp(rv, 0, 100);
            else if (string.Equals(property, "greenBalance", StringComparison.OrdinalIgnoreCase) && TryReadInt(value, out int gv))
                _greenBalance = Math.Clamp(gv, 0, 100);
            else if (string.Equals(property, "blueBalance", StringComparison.OrdinalIgnoreCase) && TryReadInt(value, out int bv))
                _blueBalance = Math.Clamp(bv, 0, 100);
        }
        WakeForRender(2);
    }

    public void WakeForRender(int frames = 2)
    {
        Interlocked.Exchange(ref _applyFramesRemaining, Math.Max(1, frames));
        _wakeEvent.Set();
    }

    public IReadOnlyList<ChannelState> GetChannels() => [];
    public IReadOnlyList<SubdeviceState> GetSubdevices() => [];

    public IReadOnlyList<ControllableParamInfo> GetParameters()
    {
        string gm, co;
        double gamma;
        int rb, gb, bb;
        lock (_cfgLock)
        {
            gm = _gpuMode;
            co = _colorOrder;
            gamma = _gamma;
            rb = _redBalance;
            gb = _greenBalance;
            bb = _blueBalance;
        }

        return
        [
            new ControllableParamInfo
            {
                Property = "gpuMode",
                Group = "lighting",
                Label = "GPU Lighting Mode",
                Description = "Software color sync, External ARGB Header passthrough, or Sapphire built-in effect",
                Type = "combobox",
                Values = [.. GpuModeOptions],
                Default = JsonDocument.Parse("\"Custom (Software)\"").RootElement.Clone(),
                CurrentValue = gm
            },
            new ControllableParamInfo
            {
                Property = "colorOrder",
                Group = "lighting",
                Label = "Channel Order (RGB default)",
                Description = "Hardware RGB register order (RGB = 0x1A Red, 0x1B Green, 0x1C Blue)",
                Type = "combobox",
                Values = [.. ColorOrderOptions],
                Default = JsonDocument.Parse("\"RGB\"").RootElement.Clone(),
                CurrentValue = co
            },
            new ControllableParamInfo
            {
                Property = "gamma",
                Group = "lighting",
                Label = "LED Gamma (2.2 = True Hue)",
                Description = "Perceptual sRGB-to-LED gamma correction so hue stays consistent across brightness levels (1.0 = raw linear, 2.2 = standard)",
                Type = "number",
                Min = 1.0,
                Max = 3.0,
                Step = 0.1,
                Default = JsonDocument.Parse("2.2").RootElement.Clone(),
                CurrentValue = Math.Round(gamma, 2)
            },
            new ControllableParamInfo
            {
                Property = "redBalance",
                Group = "lighting",
                Label = "Red Balance (%)",
                Description = "Fine-tune Red channel intensity (0-100%)",
                Type = "number",
                Min = 0,
                Max = 100,
                Step = 1,
                Default = JsonDocument.Parse("100").RootElement.Clone(),
                CurrentValue = rb
            },
            new ControllableParamInfo
            {
                Property = "greenBalance",
                Group = "lighting",
                Label = "Green Balance (%)",
                Description = "Fine-tune Green channel intensity (0-100%)",
                Type = "number",
                Min = 0,
                Max = 100,
                Step = 1,
                Default = JsonDocument.Parse("100").RootElement.Clone(),
                CurrentValue = gb
            },
            new ControllableParamInfo
            {
                Property = "blueBalance",
                Group = "lighting",
                Label = "Blue Balance (%)",
                Description = "Fine-tune Blue channel intensity (0-100%)",
                Type = "number",
                Min = 0,
                Max = 100,
                Step = 1,
                Default = JsonDocument.Parse("100").RootElement.Clone(),
                CurrentValue = bb
            }
        ];
    }

    public IReadOnlyList<string> GetRecentLogs() => _logs.ToArray();

    private void Log(string msg)
    {
        _logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] {msg}");
        while (_logs.Count > 30) _logs.TryDequeue(out _);
    }

    private void WorkerLoop()
    {
        try
        {
            Log($"Initialized {DisplayName} via AMD ADL I2C 0x28 (order={_colorOrder}, gamma={_gamma.ToString("0.0", CultureInfo.InvariantCulture)})");
            var sw = Stopwatch.StartNew();

            while (!_cts.IsCancellationRequested)
            {
                bool animated;
                string gpuMode;
                lock (_cfgLock)
                {
                    gpuMode = _gpuMode;
                    animated = _mode == DeviceRunMode.Animated && !_globalPaused && gpuMode == "Custom (Software)";
                }

                int remaining = Interlocked.CompareExchange(ref _applyFramesRemaining, 0, 0);
                if (animated || remaining > 0)
                {
                    _isIdle = false;
                    sw.Restart();
                    ApplyFrame();
                    Interlocked.Increment(ref _framesRendered);

                    if (!animated && remaining > 0)
                        Interlocked.Decrement(ref _applyFramesRemaining);

                    int fps = Math.Clamp(_globalTargetFps, 1, 12);
                    int sleepMs = (1000 / fps) - (int)sw.ElapsedMilliseconds;
                    if (sleepMs > 1)
                        _cts.Token.WaitHandle.WaitOne(sleepMs);
                }
                else
                {
                    _isIdle = true;
                    _wakeEvent.Reset();
                    bool shouldRunNow;
                    lock (_cfgLock)
                    {
                        shouldRunNow = (_mode == DeviceRunMode.Animated && !_globalPaused && _gpuMode == "Custom (Software)") ||
                                       Interlocked.CompareExchange(ref _applyFramesRemaining, 0, 0) > 0;
                    }
                    if (shouldRunNow) continue;
                    WaitHandle.WaitAny([_wakeEvent.WaitHandle, _cts.Token.WaitHandle]);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log($"GPU I2C error: {ex.Message}");
        }
        finally
        {
            _isIdle = true;
            if (!_skipShutdownBlackout)
            {
                try { SafeWrite(SapphireNitroGlowSafety.RegMode, ModeOff); } catch { }
            }
            _i2c.Dispose();
        }
    }

    private void ApplyFrame()
    {
        DeviceRunMode mode;
        string staticHex, gpuMode, colorOrder;
        double gamma;
        int brightPct, rb, gb, bb;
        float cx, cy;

        lock (_cfgLock)
        {
            mode = _mode;
            staticHex = _staticColor;
            brightPct = _effectiveBrightness;
            cx = _canvasX;
            cy = _canvasY;
            gpuMode = _gpuMode;
            colorOrder = _colorOrder;
            gamma = _gamma;
            rb = _redBalance;
            gb = _greenBalance;
            bb = _blueBalance;
        }

        if (mode == DeviceRunMode.Off || gpuMode == "Off")
        {
            WriteExternalControl(0);
            WriteMode(ModeOff);
            return;
        }

        if (gpuMode == "External ARGB Header")
        {
            WriteExternalControl(1);
            return;
        }

        WriteExternalControl(0);

        byte targetModeByte = gpuMode switch
        {
            "Rainbow" => ModeRainbow,
            "Runway" => ModeRunway,
            "Color Cycle" => ModeColorCycle,
            "Serial" => ModeSerial,
            "Sapphire Blue" => ModeSapphireBlue,
            _ => ModeCustom
        };

        WriteMode(targetModeByte);

        if (targetModeByte == ModeCustom)
        {
            var raw = mode == DeviceRunMode.Static
                ? CanvasEngine.ParseHexColor(staticHex)
                : _canvas.SampleUnscaled(cx, cy);

            double brightScale = Math.Clamp(brightPct / 100.0, 0.0, 1.0);
            byte r = CalibrateChannel(raw.R, gamma, rb, brightScale);
            byte g = CalibrateChannel(raw.G, gamma, gb, brightScale);
            byte b = CalibrateChannel(raw.B, gamma, bb, brightScale);

            var (c0, c1, c2) = MapColorOrder(r, g, b, colorOrder);
            WriteHwColors(c0, c1, c2);
        }
    }

    private static byte CalibrateChannel(byte srgb, double gamma, int balancePct, double brightScale)
    {
        if (srgb == 0 || balancePct <= 0 || brightScale <= 0.0)
            return 0;

        double linear = Math.Pow(srgb / 255.0, gamma);
        double scaled = linear * (balancePct / 100.0) * brightScale * 255.0;
        return (byte)Math.Clamp((int)Math.Round(scaled, MidpointRounding.AwayFromZero), 0, 255);
    }

    private static (byte C0, byte C1, byte C2) MapColorOrder(byte r, byte g, byte b, string order) => order switch
    {
        "RBG" => (r, b, g),
        "GRB" => (g, r, b),
        "GBR" => (g, b, r),
        "BRG" => (b, r, g),
        "BGR" => (b, g, r),
        _ => (r, g, b) // RGB (0x1A=Red, 0x1B=Green, 0x1C=Blue)
    };

    private void WriteExternalControl(byte ext)
    {
        if (_lastExt == ext) return;
        if (SafeWrite(SapphireNitroGlowSafety.RegExternalControl, ext))
            _lastExt = ext;
    }

    private void WriteMode(byte modeByte)
    {
        if (_lastModeByte == modeByte) return;
        if (SafeWrite(SapphireNitroGlowSafety.RegMode, modeByte))
            _lastModeByte = modeByte;
    }

    private void WriteHwColors(byte c0, byte c1, byte c2)
    {
        var next = (c0, c1, c2);
        if (_lastHwColors == next) return;
        bool okR = SafeWrite(SapphireNitroGlowSafety.RegRed, c0);
        bool okG = SafeWrite(SapphireNitroGlowSafety.RegGreen, c1);
        bool okB = SafeWrite(SapphireNitroGlowSafety.RegBlue, c2);
        if (okR && okG && okB)
            _lastHwColors = next;
    }

    private bool SafeWrite(byte register, byte value)
    {
        if (!SapphireNitroGlowSafety.IsSafeWrite(SapphireNitroGlowSafety.DeviceAddress, register))
            throw new InvalidOperationException($"Blocked unsafe GPU I2C write to reg 0x{register:X2}");

        return _i2c.WriteByteData(SapphireNitroGlowSafety.DeviceAddress, register, value);
    }

    private static bool TryReadInt(JsonElement el, out int val)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out val)) return true;
        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out val)) return true;
        val = 0;
        return false;
    }

    private static bool TryReadDouble(JsonElement el, out double val)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out val)) return true;
        if (el.ValueKind == JsonValueKind.String && double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out val)) return true;
        val = 0;
        return false;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wakeEvent.Set();
        try { _thread?.Join(1500); } catch { }
        _wakeEvent.Dispose();
        _cts.Dispose();
    }
}
