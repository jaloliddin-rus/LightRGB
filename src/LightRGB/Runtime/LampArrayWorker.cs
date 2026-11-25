using System.Diagnostics;
using System.Text.Json;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Hardware;

namespace LightRGB.Runtime;

public sealed class LampArrayWorker : IDeviceWorker
{
    private readonly HidDeviceGroup _group;
    private readonly LampArrayController _controller;
    private readonly CanvasEngine _canvas;
    private readonly ManualResetEventSlim _wakeEvent = new(true);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _logs = [];

    private Thread? _thread;
    private volatile DeviceRunMode _mode = DeviceRunMode.Animated;
    private string _staticColor = "#009BDE";
    private float _canvasX = 0.5f;
    private float _canvasY = 0.5f;
    private volatile int _effectiveBrightness = 100;
    private volatile int _globalTargetFps = 20;
    private volatile bool _globalPaused;
    private volatile bool _skipShutdownBlackout;
    private int _applyFramesRemaining = 2;
    private int _framesRendered;
    private volatile bool _isIdle;
    private string _lastError = "";

    public LampArrayWorker(
        string deviceId,
        HidDeviceGroup group,
        CanvasEngine canvas,
        DeviceConfig initialConfig,
        int globalBrightness,
        int globalTargetFps,
        bool globalPaused,
        bool skipShutdownBlackout)
    {
        DeviceId = deviceId;
        _group = group;
        _controller = new LampArrayController(group);
        _canvas = canvas;
        ApplyConfig(initialConfig, globalBrightness, globalTargetFps, globalPaused, skipShutdownBlackout);
    }

    public string DeviceId { get; }
    public string DisplayName => _controller.DeviceName;
    public string PluginPath => "Built-in HID LampArray (0x59)";
    public string DeviceType => "lamparray";
    public ushort VendorId => _group.VendorId;
    public ushort ProductId => _group.ProductId;
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
            Name = $"LightRGB-LampArray-{DeviceId}"
        };
        _thread.Start();
    }

    public void ApplyConfig(DeviceConfig config, int globalBrightness, int globalTargetFps, bool globalPaused, bool skipShutdownBlackout)
    {
        _mode = config.Mode;
        _staticColor = config.StaticColor;
        _canvasX = config.CanvasX;
        _canvasY = config.CanvasY;
        _effectiveBrightness = config.SyncBrightness
            ? Math.Clamp(globalBrightness, 0, 100)
            : Math.Clamp(config.Brightness, 0, 100);
        _globalTargetFps = Math.Clamp(globalTargetFps, 1, 60);
        _globalPaused = globalPaused;
        _skipShutdownBlackout = skipShutdownBlackout;
        WakeForRender(2);
    }

    public void QueueParameterUpdate(string property, JsonElement value) => WakeForRender(2);

    public void WakeForRender(int frames = 2)
    {
        Interlocked.Exchange(ref _applyFramesRemaining, Math.Max(1, frames));
        _wakeEvent.Set();
    }

    public IReadOnlyList<ChannelState> GetChannels() => [];
    public IReadOnlyList<SubdeviceState> GetSubdevices() => [];
    public IReadOnlyList<ControllableParamInfo> GetParameters() => [];
    public IReadOnlyList<string> GetRecentLogs()
    {
        lock (_logs) { return _logs.ToList(); }
    }

    private void AddLog(string msg)
    {
        lock (_logs)
        {
            _logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (_logs.Count > 30) _logs.RemoveAt(0);
        }
    }

    private void WorkerLoop()
    {
        try
        {
            bool ok = _controller.Initialize();
            AddLog(_controller.LastStatus);
            if (!ok)
            {
                _lastError = _controller.LastStatus;
                _isIdle = true;
                return;
            }

            var sw = Stopwatch.StartNew();
            while (!_cts.IsCancellationRequested)
            {
                bool animated = _mode == DeviceRunMode.Animated && !_globalPaused;
                int remaining = Interlocked.CompareExchange(ref _applyFramesRemaining, 0, 0);

                if (animated || remaining > 0)
                {
                    _isIdle = false;
                    sw.Restart();

                    if (_mode == DeviceRunMode.Off)
                    {
                        _controller.SetAllColor(0, 0, 0);
                    }
                    else if (_mode == DeviceRunMode.Static)
                    {
                        var rgb = CanvasEngine.ApplyBrightness(CanvasEngine.ParseHexColor(_staticColor), _effectiveBrightness);
                        _controller.SetAllColor(rgb.R, rgb.G, rgb.B);
                    }
                    else
                    {
                        int bright = _effectiveBrightness;
                        int count = _controller.LampCount;
                        if (count <= 1)
                        {
                            var rgb = CanvasEngine.ApplyBrightness(_canvas.SampleUnscaled(_canvasX, _canvasY), bright);
                            _controller.SetAllColor(rgb.R, rgb.G, rgb.B);
                        }
                        else
                        {
                            var cols = new (byte R, byte G, byte B)[count];
                            for (int i = 0; i < count; i++)
                            {
                                float nx = Math.Clamp(_canvasX + ((i / (float)(count - 1)) - 0.5f) * 0.35f, 0f, 1f);
                                cols[i] = CanvasEngine.ApplyBrightness(_canvas.SampleUnscaled(nx, _canvasY), bright);
                            }
                            _controller.SetPerLampColors(cols);
                        }
                    }

                    Interlocked.Increment(ref _framesRendered);
                    if (!animated && remaining > 0)
                        Interlocked.Decrement(ref _applyFramesRemaining);

                    int sleepMs = (1000 / Math.Clamp(_globalTargetFps, 1, 60)) - (int)sw.ElapsedMilliseconds;
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
            AddLog($"Error: {ex.Message}");
        }
        finally
        {
            _isIdle = true;
            if (!_skipShutdownBlackout && !_controller.IsLockedByOs)
            {
                try { _controller.SetAllColor(0, 0, 0); } catch { }
            }
            _controller.Dispose();
        }
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
