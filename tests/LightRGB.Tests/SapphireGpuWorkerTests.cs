using System.Collections.Concurrent;
using System.Text.Json;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Hardware;
using LightRGB.Runtime;
using Xunit;

namespace LightRGB.Tests;

internal sealed class MockAmdI2cTransport : IAmdI2cTransport
{
    public ConcurrentQueue<(byte Addr, byte Reg, byte Val)> Writes { get; } = new();
    public bool ProbeSuccess { get; set; } = true;

    public int AdapterIndex => 0;
    public string AdapterName => "AMD Radeon RX 9070 XT";
    public ushort VendorId => 0x1002;
    public ushort DeviceId => 0x7550;
    public ushort SubVendorId => 0x1DA2;
    public ushort SubDeviceId => 0x3490;

    public int ReadByte(byte address) => ProbeSuccess && address == 0x28 ? 0x06 : -1;
    public int ReadByteData(byte address, byte register) => ProbeSuccess && address == 0x28 ? 0x00 : -1;

    public bool WriteByteData(byte address, byte register, byte value)
    {
        Writes.Enqueue((address, register, value));
        return true;
    }

    public void Dispose() { }
}

public sealed class SapphireGpuWorkerTests
{
    [Fact]
    public void SapphireSafety_RejectsUnsafeAddressOrRegisterIncluding0x3E()
    {
        Assert.True(SapphireNitroGlowSafety.IsSafeWrite(0x28, 0x1A));
        Assert.True(SapphireNitroGlowSafety.IsSafeWrite(0x28, 0x0F));
        Assert.False(SapphireNitroGlowSafety.IsSafeWrite(0x28, 0x3E));
        Assert.False(SapphireNitroGlowSafety.IsSafeWrite(0x50, 0x1A));
        Assert.False(SapphireNitroGlowSafety.IsSafeWrite(0x28, 0x99));
    }

    [Fact]
    public void SapphireGpuWorker_UsesRgbGammaDefaultAndPreservesHueAcrossBrightness()
    {
        var mock = new MockAmdI2cTransport();
        Assert.True(SapphireGpuWorker.TryDetect(mock, out string name));
        Assert.Contains("9070 XT", name);

        var canvas = new CanvasEngine();
        canvas.Tick(0.1);
        var cfg = new DeviceConfig
        {
            DeviceId = "GPU:0:1DA2:3490",
            Mode = DeviceRunMode.Static,
            StaticColor = "#009BDE" // R=0x00, G=0x9B (155), B=0xDE (222)
        };

        using var worker = new SapphireGpuWorker(
            cfg.DeviceId,
            name,
            mock,
            canvas,
            cfg,
            globalBrightness: 100,
            globalTargetFps: 12,
            globalPaused: false,
            skipShutdownBlackout: true);

        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered >= 2 && worker.IsIdle, 3000));

        // Must never touch 0x3E
        Assert.DoesNotContain(mock.Writes, w => w.Reg == 0x3E);
        Assert.Contains(mock.Writes, w => w == (0x28, 0x0F, 0x00));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x10, 0x06));

        // Default gamma=2.2 in RGB order at 100%: 0x1A=R(0), 0x1B=G(85), 0x1C=B(188)
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1A, 0));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1B, 85));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1C, 188));

        // At 80% brightness, hue ratio G:B (~0.45) stays constant: G=68, B=150
        mock.Writes.Clear();
        worker.ApplyConfig(cfg, 80, 12, false, true);
        Assert.True(SpinWait.SpinUntil(() => worker.IsIdle && mock.Writes.Any(w => w == (0x28, 0x1B, 68)), 3000));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1B, 68));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1C, 150));

        // At 10% brightness, hue ratio stays proportional: G=9, B=19
        mock.Writes.Clear();
        worker.ApplyConfig(cfg, 10, 12, false, true);
        Assert.True(SpinWait.SpinUntil(() => worker.IsIdle && mock.Writes.Any(w => w == (0x28, 0x1B, 9)), 3000));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1B, 9));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1C, 19));

        // Setting gamma=1.0 gives exact linear values (0x1A=0x00, 0x1B=0x9B, 0x1C=0xDE)
        mock.Writes.Clear();
        worker.ApplyConfig(cfg, 100, 12, false, true);
        worker.QueueParameterUpdate("gamma", JsonDocument.Parse("1.0").RootElement);
        Assert.True(SpinWait.SpinUntil(() => worker.IsIdle && mock.Writes.Any(w => w == (0x28, 0x1C, 0xDE)), 3000));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1A, 0x00));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1B, 0x9B));

        // Switch to RBG and Red=50% on #FF0080 (with gamma=1.0) -> 0x1A=127..128, 0x1B=0x80(B), 0x1C=0x00(G)
        mock.Writes.Clear();
        cfg.StaticColor = "#FF0080";
        worker.ApplyConfig(cfg, 100, 12, false, true);
        worker.QueueParameterUpdate("colorOrder", JsonDocument.Parse("\"RBG\"").RootElement);
        worker.QueueParameterUpdate("redBalance", JsonDocument.Parse("50").RootElement);
        Assert.True(SpinWait.SpinUntil(() => worker.IsIdle && mock.Writes.Any(w => w == (0x28, 0x1B, 0x80)), 3000));
        Assert.Contains(mock.Writes, w => w.Addr == 0x28 && w.Reg == 0x1A && w.Val is >= 127 and <= 128);
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1B, 0x80));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1C, 0x00));

        int prevFrames = worker.FramesRendered;
        worker.QueueParameterUpdate("gpuMode", JsonDocument.Parse("\"External ARGB Header\"").RootElement);
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered > prevFrames && worker.IsIdle, 3000));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x0F, 0x01));

        prevFrames = worker.FramesRendered;
        cfg.Mode = DeviceRunMode.Off;
        worker.ApplyConfig(cfg, 100, 12, false, true);
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered > prevFrames && worker.IsIdle, 3000));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x10, 0x07));
    }

    [Fact]
    public void CanvasEngine_SampleUnscaledAndSapphireAnimated_PreservesHueAtLowBrightness()
    {
        var canvas = new CanvasEngine();
        canvas.UpdateSettings(new EffectSettings
        {
            EffectName = "Solid Color",
            PrimaryColor = "#009BDE",
            Brightness = 10
        });
        canvas.Tick(0.1);

        // Scaled sample reflects 10% brightness
        var scaled = canvas.Sample(0.5f, 0.5f);
        Assert.Equal(0, scaled.R);
        Assert.Equal(15, scaled.G);
        Assert.Equal(22, scaled.B);

        // Unscaled sample keeps full hue fidelity for gamma-corrected GPU output
        var unscaled = canvas.SampleUnscaled(0.5f, 0.5f);
        Assert.Equal((0, 155, 222), unscaled);

        var mock = new MockAmdI2cTransport();
        var cfg = new DeviceConfig
        {
            DeviceId = "GPU:0:1DA2:3490",
            Mode = DeviceRunMode.Animated,
            StaticColor = "#009BDE"
        };

        using var worker = new SapphireGpuWorker(
            cfg.DeviceId,
            "Sapphire Radeon RX 9070 XT Pure",
            mock,
            canvas,
            cfg,
            globalBrightness: 10,
            globalTargetFps: 12,
            globalPaused: false,
            skipShutdownBlackout: true);

        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered >= 2 && mock.Writes.Any(w => w == (0x28, 0x1B, 9)), 3000));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1A, 0));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1B, 9));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1C, 19));
    }

    [Fact]
    public void SapphireGpuWorker_PerDeviceBrightnessAndSync_OverridesGlobalWhenUnsynced()
    {
        var canvas = new CanvasEngine();
        canvas.UpdateSettings(new EffectSettings
        {
            EffectName = "Solid Color",
            PrimaryColor = "#009BDE",
            Brightness = 10
        });
        canvas.Tick(0.1);

        var mock = new MockAmdI2cTransport();
        var cfg = new DeviceConfig
        {
            DeviceId = "GPU:0:1DA2:3490",
            Mode = DeviceRunMode.Animated,
            StaticColor = "#009BDE",
            Brightness = 80,
            SyncBrightness = false
        };

        using var worker = new SapphireGpuWorker(
            cfg.DeviceId,
            "Sapphire Radeon RX 9070 XT Pure",
            mock,
            canvas,
            cfg,
            globalBrightness: 10,
            globalTargetFps: 12,
            globalPaused: false,
            skipShutdownBlackout: true);

        worker.Start();
        // Unsynced Animated device uses its own 80% brightness despite global 10%: G=68, B=150
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered >= 2 && mock.Writes.Any(w => w == (0x28, 0x1B, 68)), 3000));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1C, 150));

        // Switch to Static while still unsynced at 80% -> keeps G=68, B=150
        mock.Writes.Clear();
        cfg.Mode = DeviceRunMode.Static;
        worker.ApplyConfig(cfg, globalBrightness: 10, 12, false, true);
        Assert.True(SpinWait.SpinUntil(() => worker.IsIdle, 3000));

        // Re-sync -> drops immediately to global 10% brightness: G=9, B=19
        mock.Writes.Clear();
        cfg.SyncBrightness = true;
        worker.ApplyConfig(cfg, globalBrightness: 10, 12, false, true);
        Assert.True(SpinWait.SpinUntil(() => worker.IsIdle && mock.Writes.Any(w => w == (0x28, 0x1B, 9)), 3000));
        Assert.Contains(mock.Writes, w => w == (0x28, 0x1C, 19));
    }
}
