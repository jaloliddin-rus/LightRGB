using System.Collections.Concurrent;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Extraction;
using LightRGB.Hardware;
using LightRGB.Runtime;
using Xunit;

namespace LightRGB.Tests;

internal sealed class MockSmbusTransport : ISmbusTransport
{
    private readonly Dictionary<(byte Addr, byte Reg), byte> _registers = new();
    private ushort _activeReg;

    public ConcurrentQueue<(byte Addr, byte Reg, byte[] Data)> BlockWrites { get; } = new();
    public ConcurrentQueue<(byte Addr, byte Reg, byte Val)> ByteWrites { get; } = new();

    public string Name => "Mock PIIX4 SMBus";
    public ushort VendorId => 0x1022;
    public ushort DeviceId => 0x790B;
    public ushort SubVendorId => 0x1849;
    public ushort SubDeviceId => 0xFFFF;
    public int Port => 0;
    public bool IsSystemBus => true;
    public bool IsAmdBus => true;
    public bool IsIntelBus => false;
    public bool IsNvidiaBus => false;
    public bool IsAmdGpuBus => false;

    public int WriteQuick(byte address)
    {
        if (!SmbusSafetyPolicy.IsWriteAddressAllowed(address)) return -1;
        return address == 0x70 ? 0 : -1;
    }

    public int ReadByte(byte address, byte register)
    {
        if (address != 0x70) return -1;
        if (register >= 0xA0 && register <= 0xAF)
            return register - 0xA0;

        if (register == 0x81)
        {
            ushort reg = _activeReg;
            if (reg >= 0x1000 && reg < 0x1010)
            {
                byte[] name = System.Text.Encoding.ASCII.GetBytes("AUMA0-E6K5-0106\0");
                return name[reg - 0x1000];
            }
            if (reg >= 0x1030 && reg < 0x1045)
            {
                byte[] mfg = System.Text.Encoding.ASCII.GetBytes("Thermaltake\0\0\0\0\0\0\0\0\0\0");
                return mfg[reg - 0x1030];
            }
            if (reg == 0x1C00) return 0x00;
            if (reg == 0x1C02) return 0x0A;
            if (reg >= 0x1C00 && reg < 0x1C40) return 0x01;
            if (reg == 0x8020 || reg == 0xE020) return 0x01;
            return 0x00;
        }
        return _registers.TryGetValue((address, register), out byte v) ? v : 0;
    }

    public int WriteByte(byte address, byte register, byte value)
    {
        if (!SmbusSafetyPolicy.IsWriteAddressAllowed(address)) return -1;
        ByteWrites.Enqueue((address, register, value));
        _registers[(address, register)] = value;
        return 0;
    }

    public int ReadWord(byte address, byte register) => 0;

    public int WriteWord(byte address, byte register, ushort value)
    {
        if (!SmbusSafetyPolicy.IsWriteAddressAllowed(address)) return -1;
        if (register == 0x00)
        {
            _activeReg = (ushort)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));
        }
        return 0;
    }

    public int WriteBlock(byte address, byte register, byte[] data)
    {
        if (!SmbusSafetyPolicy.IsWriteAddressAllowed(address)) return -1;
        BlockWrites.Enqueue((address, register, data.ToArray()));
        return 0;
    }

    public int ReadByteWithoutRegister(byte address) => 0;
    public int WriteBlockWithoutRegister(byte address, byte[] data) => SmbusSafetyPolicy.IsWriteAddressAllowed(address) ? 0 : -1;
    public void Dispose() { }
}

public sealed class SmbusRuntimeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lightrgb_smbus_{Guid.NewGuid():N}");

    [Fact]
    public void SafetyPolicy_BlocksSpdEepromAndSystemAddresses()
    {
        for (byte addr = 0x50; addr <= 0x57; addr++)
            Assert.False(SmbusSafetyPolicy.IsWriteAddressAllowed(addr));

        Assert.False(SmbusSafetyPolicy.IsWriteAddressAllowed(0x18));
        Assert.False(SmbusSafetyPolicy.IsWriteAddressAllowed(0x30));
        Assert.True(SmbusSafetyPolicy.IsWriteAddressAllowed(0x70));
        Assert.True(SmbusSafetyPolicy.IsWriteAddressAllowed(0x39));
    }

    [Fact]
    public void EneRamPlugin_ScansWithScanGateDuringActiveRenderAndIdlesInStatic()
    {
        string? installer = TestHelpers.TryFindSignalRgbInstaller();
        if (installer is null) return;
        new SignalRgbExtractor().Extract(installer, _tempDir);
        var sysInfo = new SystemInfoProvider();
        var index = new PluginIndexer().LoadOrBuild(_tempDir, sysInfo, forceRebuild: true);

        var mockBus = new MockSmbusTransport();
        using var scanGate = new ReaderWriterLockSlim();

        var detected = SmbusDeviceScanner.ScanRamPlugins(
            index,
            [mockBus],
            Path.Combine(_tempDir, "plugins"),
            Path.Combine(_tempDir, "user_plugins"),
            sysInfo);

        Assert.Single(detected);
        Assert.Equal((byte)0x70, detected[0].Address);
        Assert.Equal("Asus/ENE_RAM.js", detected[0].Plugin.RelativePath);

        var canvas = new CanvasEngine();
        canvas.Tick(0.1);
        var cfg = new DeviceConfig
        {
            DeviceId = "SMBUS:0:70",
            Mode = DeviceRunMode.Animated,
            StaticColor = "#AA22CC"
        };

        using var worker = new SmbusDeviceWorker(
            "SMBUS:0:70",
            detected[0].Plugin,
            mockBus,
            0x70,
            canvas,
            Path.Combine(_tempDir, "plugins"),
            Path.Combine(_tempDir, "user_plugins"),
            sysInfo,
            cfg,
            globalBrightness: 100,
            globalTargetFps: 15,
            globalPaused: false,
            skipShutdownBlackout: true,
            busScanGate: scanGate);

        worker.Start();
        Assert.True(SpinWait.SpinUntil(() => worker.FramesRendered >= 2, 4000),
            $"SMBus worker error={worker.LastError}, frames={worker.FramesRendered}");

        // Simulate a Rescan while worker is actively streaming Animated frames
        List<SmbusDetectedDevice> rescanResult;
        scanGate.EnterWriteLock();
        try
        {
            rescanResult = SmbusDeviceScanner.ScanRamPlugins(
                index,
                [mockBus],
                Path.Combine(_tempDir, "plugins"),
                Path.Combine(_tempDir, "user_plugins"),
                sysInfo);
        }
        finally
        {
            scanGate.ExitWriteLock();
        }
        Assert.Single(rescanResult);
        Assert.Equal((byte)0x70, rescanResult[0].Address);

        // Switch to Static and verify idle
        cfg.Mode = DeviceRunMode.Static;
        worker.ApplyConfig(cfg, 100, 15, false, true);
        Assert.True(SpinWait.SpinUntil(() => worker.IsIdle, 4000));
        Assert.Equal("", worker.LastError);
        Assert.NotEmpty(mockBus.BlockWrites);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
