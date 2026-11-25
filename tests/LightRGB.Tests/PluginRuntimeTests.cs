using System.Collections.Concurrent;
using System.Text.Json;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Extraction;
using LightRGB.Hardware;
using LightRGB.Runtime;
using Xunit;

namespace LightRGB.Tests;

internal sealed class MockHidTransport : IHidTransport
{
    public ConcurrentQueue<byte[]> WrittenPackets { get; } = new();
    public ConcurrentQueue<byte[]> FeatureReports { get; } = new();
    public Func<byte[]?, int, byte[]>? OnRead { get; set; }
    public Action<byte[]>? OnWrite { get; set; }

    public MockHidTransport(ushort vid, ushort pid, int iface = 0, ushort usagePage = 0xFF00, ushort usage = 0x0001)
    {
        Endpoints =
        [
            new HidEndpointInfo("mock://hid0", vid, pid, iface, usagePage, usage, 0, 65, 65, 65, "Mock", "MockDevice", true)
        ];
        ActiveEndpoint = Endpoints[0];
    }

    public IReadOnlyList<HidEndpointInfo> Endpoints { get; }
    public HidEndpointInfo ActiveEndpoint { get; private set; }
    public int LastReadSize { get; private set; }

    public bool SetEndpoint(int? iface, int? usage, int? usagePage, int? collection)
    {
        var match = Endpoints.FirstOrDefault(ep =>
            (!iface.HasValue || iface.Value < 0 || ep.Interface == iface.Value) &&
            (!usage.HasValue || usage.Value < 0 || ep.Usage == usage.Value) &&
            (!usagePage.HasValue || usagePage.Value < 0 || ep.UsagePage == usagePage.Value) &&
            (!collection.HasValue || collection.Value < 0 || ep.Collection == collection.Value));
        if (match is null) return false;
        ActiveEndpoint = match;
        return true;
    }

    public int Write(byte[] data, int length)
    {
        var copy = new byte[Math.Max(length, data.Length)];
        Array.Copy(data, copy, data.Length);
        OnWrite?.Invoke(copy);
        WrittenPackets.Enqueue(copy);
        return copy.Length;
    }

    public byte[] Read(byte[]? cmd, int length, int timeoutMs = 60)
    {
        var res = OnRead?.Invoke(cmd, length) ?? new byte[Math.Max(1, length)];
        LastReadSize = res.Length;
        return res;
    }

    public int SendFeatureReport(byte[] data, int length)
    {
        var copy = new byte[Math.Max(length, data.Length)];
        Array.Copy(data, copy, data.Length);
        FeatureReports.Enqueue(copy);
        return copy.Length;
    }

    public byte[] GetFeatureReport(byte[] data, int length)
    {
        var res = new byte[Math.Max(1, length)];
        LastReadSize = res.Length;
        return res;
    }

    public void ClearReadBuffer() { }
    public void Dispose() { }
}

public sealed class PluginRuntimeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lightrgb_rt_{Guid.NewGuid():N}");
    private PluginIndexDocument? _index;
    private readonly SystemInfoProvider _sysInfo = new();

    private PluginIndexDocument? EnsureExtractedIndex()
    {
        if (_index is not null) return _index;
        string? installer = TestHelpers.TryFindSignalRgbInstaller();
        if (installer is null) return null;
        new SignalRgbExtractor().Extract(installer, _tempDir);
        _index = new PluginIndexer().LoadOrBuild(_tempDir, _sysInfo, forceRebuild: true);
        return _index;
    }

    [Fact]
    public void AsrockMotherboardPlugin_RendersAnimatedAndStaticAndGoesIdle()
    {
        var index = EnsureExtractedIndex();
        if (index is null) return;
        var plugin = index.Entries.First(e => e.RelativePath.Equals("AsRock/Asrock_Motherboard_Controller.js", StringComparison.OrdinalIgnoreCase));
        var mock = new MockHidTransport(0x26CE, 0x01A2, iface: 0);
        byte lastConfigId = 0;
        mock.OnWrite = pkt =>
        {
            if (pkt.Length >= 4 && pkt[1] == 0x14 && pkt[2] == 0x00)
                lastConfigId = pkt[3];
        };
        mock.OnRead = (_, len) =>
        {
            var buf = new byte[len];
            if (lastConfigId == 1)
            {
                buf[5] = 0x7F; // enable zones 0..6
            }
            else if (lastConfigId == 2)
            {
                buf[5] = 1;  // RGBHeader1
                buf[6] = 1;  // RGBHeader2
                buf[7] = 80; // ARGBHeader1
                buf[8] = 80; // ARGBHeader2
                buf[9] = 4;  // PCH
                buf[10] = 4; // IOShield
                buf[11] = 4; // PCB
            }
            return buf;
        };
        // Track ReadConfig(1) vs ReadConfig(2) from preceding write([0x00, 0x14, 0x00, ConfigId])
        var origCanvas = new CanvasEngine();
        origCanvas.Tick(0.5);
        var canvas = origCanvas;

        var cfg = new DeviceConfig
        {
            DeviceId = "26CE:01A2",
            Mode = DeviceRunMode.Static,
            StaticColor = "#FF1122"
        };

        using var worker = new PluginDeviceWorker(
            "26CE:01A2",
            plugin,
            mock,
            canvas,
            Path.Combine(_tempDir, "plugins"),
            Path.Combine(_tempDir, "user_plugins"),
            _sysInfo,
            cfg,
            globalBrightness: 100,
            globalTargetFps: 30,
            globalPaused: false,
            skipShutdownBlackout: false);

        worker.Start();

        // Wait up to 4s for initial static frames to render and settle to idle
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered >= 3 && worker.IsIdle, 4000),
            $"Worker failed to complete static render + idle. Error={worker.LastError}, Frames={worker.FramesRendered}");

        Assert.Equal("", worker.LastError);
        Assert.True(mock.WrittenPackets.Count >= 16, $"Expected >= 16 HID packets from ASRock plugin, got {mock.WrittenPackets.Count}");

        // Verify all emitted packets are 65 bytes (including sparse Array(240) zero padding)
        Assert.All(mock.WrittenPackets, p => Assert.Equal(65, p.Length));

        // Verify static color bytes (FF, 11, 22) appear in emitted payload
        bool hasColor = mock.WrittenPackets.Any(p =>
        {
            for (int i = 0; i <= p.Length - 3; i++)
            {
                if (p[i] == 0xFF && p[i + 1] == 0x11 && p[i + 2] == 0x22)
                    return true;
            }
            return false;
        });
        Assert.True(hasColor,
            "Static color (FF 11 22) not found. Logs=" + string.Join(" | ", worker.GetRecentLogs()) +
            " SamplePkts=" + string.Join(" ; ", mock.WrittenPackets.Skip(2).Take(4).Select(p => BitConverter.ToString(p.Take(24).ToArray()))));

        // Verify ASRock ARGB channels default to 80 LEDs (covering AIO + daisy-chained fans)
        var ch1 = worker.GetChannels().FirstOrDefault(c => c.Name == "Channel 1");
        Assert.NotNull(ch1);
        Assert.Equal(80, ch1!.LedCount);

        // Verify zero renders occur while idle in Static mode
        int framesAtIdle = worker.FramesRendered;
        Thread.Sleep(250);
        Assert.Equal(framesAtIdle, worker.FramesRendered);

        // Verify parameter callback onARGBModeChanged works cleanly
        worker.QueueParameterUpdate("ARGBMode", JsonDocument.Parse("false").RootElement);
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered > framesAtIdle && worker.IsIdle, 3000));
        Assert.Equal("", worker.LastError);
        Assert.NotEmpty(worker.GetSubdevices());
    }

    [Fact]
    public void AmdWraithPrismPlugin_SwitchesBetweenAnimatedAndStatic()
    {
        var index = EnsureExtractedIndex();
        if (index is null) return;
        var plugin = index.Entries.First(e => e.RelativePath.Equals("AMD/AMD_Wraith_Prism.js", StringComparison.OrdinalIgnoreCase));
        var mock = new MockHidTransport(0x2516, 0x0051, iface: 0);
        var canvas = new CanvasEngine();
        canvas.Tick(0.2);

        var cfg = new DeviceConfig
        {
            DeviceId = "2516:0051",
            Mode = DeviceRunMode.Animated,
            StaticColor = "#00FF44"
        };

        using var worker = new PluginDeviceWorker(
            "2516:0051",
            plugin,
            mock,
            canvas,
            Path.Combine(_tempDir, "plugins"),
            Path.Combine(_tempDir, "user_plugins"),
            _sysInfo,
            cfg,
            globalBrightness: 100,
            globalTargetFps: 30,
            globalPaused: false,
            skipShutdownBlackout: true);

        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered >= 3, 3000), $"Error: {worker.LastError}");
        Assert.Equal("", worker.LastError);
        Assert.False(worker.IsIdle);

        // Switch to Static
        cfg.Mode = DeviceRunMode.Static;
        worker.ApplyConfig(cfg, 100, 30, false, true);
        Assert.True(SpinWait.SpinUntil(() => worker.IsIdle, 3000));
        Assert.Contains(mock.WrittenPackets, p => p.Length == 65 && p[1] == 0xC0);
    }

    [Fact]
    public void DeviceHostBridge_CreateColorArray_HandlesOrdersAndSeparateMode()
    {
        var mock = new MockHidTransport(0x1234, 0x5678);
        var bridge = new DeviceHostBridge(mock, new CanvasEngine(), "Test");

        // RGB #112233
        Assert.Equal("[17,34,51,17,34,51]", bridge.CreateColorArrayJson("#112233", 2, "Inline", "RGB"));
        // GRB -> [34,17,51]
        Assert.Equal("[34,17,51]", bridge.CreateColorArrayJson("#112233", 1, "Inline", "GRB"));
        // BGR -> [51,34,17]
        Assert.Equal("[51,34,17]", bridge.CreateColorArrayJson("#112233", 1, "Inline", "BGR"));
        // Separate (and SignalRGB typo "Seperate") -> [[17,17],[34,34],[51,51]]
        Assert.Equal("[[17,17],[34,34],[51,51]]", bridge.CreateColorArrayJson("#112233", 2, "Separate", "RGB"));
        Assert.Equal("[[17,17],[34,34],[51,51]]", bridge.CreateColorArrayJson("#112233", 2, "Seperate", "RGB"));
    }

    [Fact]
    public void DeviceHostBridge_PerDeviceBrightnessAndSync_OverridesGlobalBrightness()
    {
        var canvas = new CanvasEngine();
        canvas.UpdateSettings(new EffectSettings
        {
            EffectName = "Solid Color",
            PrimaryColor = "#009BDE",
            Brightness = 10
        });
        canvas.Tick(0.1);

        var mock = new MockHidTransport(0x1234, 0x5678);
        var bridge = new DeviceHostBridge(mock, canvas, "Test");

        var cfg = new DeviceConfig
        {
            DeviceId = "1234:5678",
            Mode = DeviceRunMode.Animated,
            StaticColor = "#009BDE",
            Brightness = 80,
            SyncBrightness = false
        };
        bridge.ApplyDeviceConfig(cfg, globalBrightness: 10);

        // Animated unsynced 80%: R=0, G=124, B=177
        int packedAnim = bridge.SampleColorPacked(0, 0);
        Assert.Equal((0, 124, 177), ((packedAnim >> 16) & 0xFF, (packedAnim >> 8) & 0xFF, packedAnim & 0xFF));

        // Static unsynced 80%: R=0, G=124, B=177
        cfg.Mode = DeviceRunMode.Static;
        bridge.ApplyDeviceConfig(cfg, globalBrightness: 10);
        int packedStatic = bridge.SampleColorPacked(0, 0);
        Assert.Equal((0, 124, 177), ((packedStatic >> 16) & 0xFF, (packedStatic >> 8) & 0xFF, packedStatic & 0xFF));

        // Re-synced to global 10%: R=0, G=15, B=22
        cfg.SyncBrightness = true;
        bridge.ApplyDeviceConfig(cfg, globalBrightness: 10);
        int packedSynced = bridge.SampleColorPacked(0, 0);
        Assert.Equal((0, 15, 22), ((packedSynced >> 16) & 0xFF, (packedSynced >> 8) & 0xFF, packedSynced & 0xFF));
    }

    [Fact]
    public void MonsGeekKeyboardPlugin_EmitsValidChecksumAndDeduplicatesWrites()
    {
        string srcPlugin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "default_user_plugins", "MonsGeek_Keyboard.js"));
        Assert.True(File.Exists(srcPlugin), $"Missing {srcPlugin}");
        string userPluginsDir = Path.Combine(_tempDir, "user_plugins");
        Directory.CreateDirectory(userPluginsDir);
        File.Copy(srcPlugin, Path.Combine(userPluginsDir, "MonsGeek_Keyboard.js"), overwrite: true);

        var localIndex = new PluginIndexer().LoadOrBuild(_tempDir, _sysInfo, forceRebuild: true);
        var plugin = localIndex.Entries.First(e => e.RelativePath.Equals("MonsGeek_Keyboard.js", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(0x3151, plugin.VendorIds);
        Assert.Contains(0x5029, plugin.ProductIds);

        var mock = new MockHidTransport(0x3151, 0x5029, iface: 2, usagePage: 0xFFFF, usage: 0x0002);
        var canvas = new CanvasEngine();
        canvas.Tick(0.1);
        var cfg = new DeviceConfig
        {
            DeviceId = "3151:5029",
            Mode = DeviceRunMode.Static,
            StaticColor = "#102030"
        };

        using var worker = new PluginDeviceWorker(
            "3151:5029",
            plugin,
            mock,
            canvas,
            Path.Combine(_tempDir, "plugins"),
            userPluginsDir,
            _sysInfo,
            cfg,
            globalBrightness: 100,
            globalTargetFps: 20,
            globalPaused: false,
            skipShutdownBlackout: true);

        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered >= 2 && worker.IsIdle, 4000), $"Error: {worker.LastError}");
        Assert.Equal("", worker.LastError);

        // Default Direct Sync (Smooth) emits 1x 0x07 Screen Sync entry report (0x15) + 1x volatile 0x0E frame
        Assert.Equal(2, mock.FeatureReports.Count);
        Assert.True(mock.FeatureReports.TryDequeue(out var enterPkt));
        Assert.Equal(65, enterPkt.Length);
        Assert.Equal(0x07, enterPkt[1]);
        Assert.Equal(0x15, enterPkt[2]); // Screen Sync mode
        Assert.Equal(0x04, enterPkt[4]); // Brightness 4
        Assert.Equal(0x07, enterPkt[5]); // Normal flag
        Assert.Equal(0xD4, enterPkt[9]); // Exact Bit8 checksum for default Direct Mode entry

        Assert.True(mock.FeatureReports.TryDequeue(out var directPkt));
        Assert.Equal(65, directPkt.Length);
        Assert.Equal(0x0E, directPkt[1]); // SET_SCREEN_COLOR
        Assert.Equal(0x10, directPkt[2]);
        Assert.Equal(0x20, directPkt[3]);
        Assert.Equal(0x30, directPkt[4]);
        byte expectedDirectCrc = unchecked((byte)(0xFF - ((directPkt[1] + directPkt[2] + directPkt[3] + directPkt[4]) & 0xFF)));
        Assert.Equal(expectedDirectCrc, directPkt[8]);

        // Changing color streams a new 0x0E frame immediately without a 1000ms stutter delay or another 0x07 write
        cfg.StaticColor = "#405060";
        int prevFrames = worker.FramesRendered;
        worker.ApplyConfig(cfg, 100, 30, false, true);
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered > prevFrames && worker.IsIdle && mock.FeatureReports.Count >= 1, 500),
            "Expected immediate 0x0E frame update without 1000ms throttle");
        Assert.Single(mock.FeatureReports);
        Assert.True(mock.FeatureReports.TryDequeue(out var directPkt2));
        Assert.Equal(0x0E, directPkt2[1]);
        Assert.Equal(0x40, directPkt2[2]);
        Assert.Equal(0x50, directPkt2[3]);
        Assert.Equal(0x60, directPkt2[4]);
        byte expectedDirectCrc2 = unchecked((byte)(0xFF - ((directPkt2[1] + directPkt2[2] + directPkt2[3] + directPkt2[4]) & 0xFF)));
        Assert.Equal(expectedDirectCrc2, directPkt2[8]);

        // Switching to Static (0x07) emits a valid 0x07 0x01 packet with 0x07 single-color flag and Bit8 checksum
        cfg.StaticColor = "#102030";
        worker.ApplyConfig(cfg, 100, 30, false, true);
        worker.QueueParameterUpdate("keyboardMode", JsonDocument.Parse("\"Static (0x07)\"").RootElement);
        Assert.True(SpinWait.SpinUntil(() => worker.IsIdle && mock.FeatureReports.Any(p => p[1] == 0x07 && p[2] == 0x01), 3000));
        var staticPkt = mock.FeatureReports.Last(p => p[1] == 0x07 && p[2] == 0x01);
        Assert.Equal(65, staticPkt.Length);
        Assert.Equal(0x04, staticPkt[4]); // Brightness 4
        Assert.Equal(0x07, staticPkt[5]); // Single Color flag
        Assert.Equal(0x10, staticPkt[6]);
        Assert.Equal(0x20, staticPkt[7]);
        Assert.Equal(0x30, staticPkt[8]);
        byte expectedChecksum = unchecked((byte)(0xFF - ((staticPkt[1] + staticPkt[2] + staticPkt[3] + staticPkt[4] + staticPkt[5] + staticPkt[6] + staticPkt[7] + staticPkt[8]) & 0xFF)));
        Assert.Equal(expectedChecksum, staticPkt[9]);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
