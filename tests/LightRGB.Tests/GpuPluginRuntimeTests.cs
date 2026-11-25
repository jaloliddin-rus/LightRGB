using System.Collections.Concurrent;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Extraction;
using LightRGB.Hardware;
using LightRGB.Runtime;
using Xunit;

namespace LightRGB.Tests;

internal sealed class MockGpuI2cTransport : ISmbusTransport
{
    private readonly Func<byte, int>? _onWriteQuick;
    private readonly Func<byte, byte, int, (int Status, byte[] Data)>? _onReadBlockBytes;
    private readonly byte _ackAddress;

    public ConcurrentQueue<(byte Addr, byte Reg, byte Val)> ByteWrites { get; } = new();
    public ConcurrentQueue<(byte Addr, byte Reg, byte[] Data)> BlockWrites { get; } = new();

    public MockGpuI2cTransport(
        string name,
        ushort vendorId,
        ushort deviceId,
        ushort subVendorId,
        ushort subDeviceId,
        bool isNvidia,
        byte ackAddress,
        Func<byte, int>? onWriteQuick = null,
        Func<byte, byte, int, (int Status, byte[] Data)>? onReadBlockBytes = null)
    {
        Name = name;
        VendorId = vendorId;
        DeviceId = deviceId;
        SubVendorId = subVendorId;
        SubDeviceId = subDeviceId;
        IsNvidiaBus = isNvidia;
        IsAmdGpuBus = !isNvidia;
        _ackAddress = ackAddress;
        _onWriteQuick = onWriteQuick;
        _onReadBlockBytes = onReadBlockBytes;
    }

    public string Name { get; }
    public ushort VendorId { get; }
    public ushort DeviceId { get; }
    public ushort SubVendorId { get; }
    public ushort SubDeviceId { get; }
    public int Port => 0;
    public bool IsSystemBus => false;
    public bool IsAmdBus => false;
    public bool IsIntelBus => false;
    public bool IsNvidiaBus { get; }
    public bool IsAmdGpuBus { get; }

    public int WriteQuick(byte address)
    {
        if (!SmbusSafetyPolicy.IsWriteAllowedForBus(this, address)) return -1;
        if (_onWriteQuick is not null) return _onWriteQuick(address);
        return address == _ackAddress ? 0 : -1;
    }

    public int ReadByte(byte address, byte register) => 0x01;

    public int WriteByte(byte address, byte register, byte value)
    {
        if (!SmbusSafetyPolicy.IsWriteAllowedForBus(this, address)) return -1;
        ByteWrites.Enqueue((address, register, value));
        return 0;
    }

    public int ReadWord(byte address, byte register) => 0;

    public int WriteWord(byte address, byte register, ushort value)
    {
        if (!SmbusSafetyPolicy.IsWriteAllowedForBus(this, address)) return -1;
        return 0;
    }

    public int WriteBlock(byte address, byte register, byte[] data)
    {
        if (!SmbusSafetyPolicy.IsWriteAllowedForBus(this, address)) return -1;
        BlockWrites.Enqueue((address, register, data.ToArray()));
        return 0;
    }

    public int ReadByteWithoutRegister(byte address) => 0;

    public int WriteBlockWithoutRegister(byte address, byte[] data)
        => SmbusSafetyPolicy.IsWriteAllowedForBus(this, address) ? 0 : -1;

    public (int Status, byte[] Data) ReadBlockBytes(byte address, byte register, int length)
    {
        if (_onReadBlockBytes is not null)
            return _onReadBlockBytes(address, register, length);
        return (0, new byte[Math.Max(0, length)]);
    }

    public void Dispose() { }
}

public sealed class GpuPluginRuntimeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lightrgb_gpu_{Guid.NewGuid():N}");

    [Fact]
    public void NvApiStructLayout_MatchesNativeWindowsX64Abi()
    {
        Assert.Equal(64, NvApiI2cBus.StructSize);
        Assert.Equal(0x00030040u, NvApiI2cBus.StructVersion);
    }

    [Fact]
    public void SmbusSafetyPolicy_AllowsGpuI2cWhileProtectingSystemSpdEeproms()
    {
        var sysBus = new MockSmbusTransport();
        var nvBus = new MockGpuI2cTransport("Mock NVIDIA", 0x10DE, 0x2684, 0x1462, 0x5102, isNvidia: true, ackAddress: 0x68);
        var amdBus = new MockGpuI2cTransport("Mock AMD", 0x1002, 0x7550, 0x1849, 0x5403, isNvidia: false, ackAddress: 0x36);

        // SPD EEPROMs (0x50..0x57) and low addresses (0x36) stay strictly blocked on System RAM SMBus
        for (byte a = 0x50; a <= 0x57; a++)
            Assert.False(SmbusSafetyPolicy.IsWriteAllowedForBus(sysBus, a));
        Assert.False(SmbusSafetyPolicy.IsWriteAllowedForBus(sysBus, 0x36));
        Assert.True(SmbusSafetyPolicy.IsWriteAllowedForBus(sysBus, 0x70));

        // Dedicated GPU I2C buses allow vendor RGB controller addresses (0x36, 0x49, 0x62, 0x68)
        Assert.True(SmbusSafetyPolicy.IsWriteAllowedForBus(nvBus, 0x49));
        Assert.True(SmbusSafetyPolicy.IsWriteAllowedForBus(nvBus, 0x62));
        Assert.True(SmbusSafetyPolicy.IsWriteAllowedForBus(nvBus, 0x68));
        Assert.True(SmbusSafetyPolicy.IsWriteAllowedForBus(amdBus, 0x36));

        // Reserved 7-bit I2C addresses remain blocked everywhere
        Assert.False(SmbusSafetyPolicy.IsWriteAllowedForBus(nvBus, 0x05));
        Assert.False(SmbusSafetyPolicy.IsWriteAllowedForBus(amdBus, 0x78));
    }

    [Fact]
    public void PawnIoInstallerHelper_FindsBundledSignedInstaller()
    {
        string tempApp = Path.Combine(_tempDir, "app-pawnio");
        string pawnDir = Path.Combine(tempApp, "pawnio");
        Directory.CreateDirectory(pawnDir);
        string dummyExe = Path.Combine(pawnDir, "PawnIO_setup.exe");
        File.WriteAllBytes(dummyExe, [0x4D, 0x5A, 0x90, 0x00]);

        string? installer = PawnIoInstallerHelper.FindBundledInstaller(tempApp);
        Assert.Equal(dummyExe, installer);
        Assert.Null(PawnIoInstallerHelper.FindBundledInstaller(Path.Combine(_tempDir, "missing-app")));
    }

    [Fact]
    public void NvidiaAndAmdGpuPlugins_ScanInitializeAndRenderThroughMockI2cBuses()
    {
        string? installer = TestHelpers.TryFindSignalRgbInstaller();
        if (installer is null) return;
        new SignalRgbExtractor().Extract(installer, _tempDir);
        var sysInfo = new SystemInfoProvider();
        var index = new PluginIndexer().LoadOrBuild(_tempDir, sysInfo, forceRebuild: true);
        string pluginsDir = Path.Combine(_tempDir, "plugins");
        string userPluginsDir = Path.Combine(_tempDir, "user_plugins");

        // 1. ASUS RTX 3070 Strix (NVIDIA)
        var asusBus = new MockGpuI2cTransport("Mock ASUS 3070", 0x10DE, 0x2484, 0x1043, 0x87BE, isNvidia: true, ackAddress: 0x67);
        var asusDetected = SmbusDeviceScanner.ScanGpuPlugins(index, [asusBus], pluginsDir, userPluginsDir, sysInfo);
        Assert.Contains(asusDetected, d => d.Plugin.RelativePath.Equals("Asus/GPUs/Asus_Ampere_Lovelace_GPU.js", StringComparison.OrdinalIgnoreCase));
        var asusMatch = asusDetected.First(d => d.Plugin.RelativePath.Equals("Asus/GPUs/Asus_Ampere_Lovelace_GPU.js", StringComparison.OrdinalIgnoreCase));

        var canvas = new CanvasEngine();
        canvas.Tick(0.1);
        var cfg = new DeviceConfig { DeviceId = "GPU-ASUS", Mode = DeviceRunMode.Static, StaticColor = "#11AAFF" };
        using (var w = new SmbusDeviceWorker("GPU-ASUS", asusMatch.Plugin, asusBus, asusMatch.Address, canvas, pluginsDir, userPluginsDir, sysInfo, cfg, 100, 15, false, true))
        {
            w.Start();
            Assert.True(SpinWait.SpinUntil(() => w.FramesRendered >= 2 && w.IsIdle, 4000), $"ASUS GPU error: {w.LastError}");
            Assert.Equal("", w.LastError);
            Assert.False(asusBus.ByteWrites.IsEmpty && asusBus.BlockWrites.IsEmpty);
        }

        // 2. MSI RTX 4090 Suprim X (NVIDIA, uses @SignalRGB/permissions())
        var msiBus = new MockGpuI2cTransport("Mock MSI 4090", 0x10DE, 0x2684, 0x1462, 0x5102, isNvidia: true, ackAddress: 0x68);
        var msiDetected = SmbusDeviceScanner.ScanGpuPlugins(index, [msiBus], pluginsDir, userPluginsDir, sysInfo);
        Assert.Contains(msiDetected, d => d.Plugin.RelativePath.Equals("Msi/GPU's/MSI_GPU_Lovelace.js", StringComparison.OrdinalIgnoreCase));
        var msiMatch = msiDetected.First(d => d.Plugin.RelativePath.Equals("Msi/GPU's/MSI_GPU_Lovelace.js", StringComparison.OrdinalIgnoreCase));

        using (var w = new SmbusDeviceWorker("GPU-MSI", msiMatch.Plugin, msiBus, msiMatch.Address, canvas, pluginsDir, userPluginsDir, sysInfo, cfg, 100, 15, false, true))
        {
            w.Start();
            Assert.True(SpinWait.SpinUntil(() => w.FramesRendered >= 2 && w.IsIdle, 4000), $"MSI GPU error: {w.LastError}");
            Assert.Equal("", w.LastError);
            Assert.NotEmpty(msiBus.BlockWrites);
        }

        // 3. ASRock RX 9070 XT Steel Legend (AMD ADL I2C via ReadBlockBytes([code, bytes]))
        byte lastSubcmd = 0;
        var asrockBus = new MockGpuI2cTransport(
            "Mock ASRock AMD",
            0x1002,
            0x7550,
            0x1849,
            0x5403,
            isNvidia: false,
            ackAddress: 0x36,
            onReadBlockBytes: (_, _, len) =>
            {
                var buf = new byte[Math.Max(12, len)];
                if (lastSubcmd == 0x01)
                    buf[4] = 0x01; // Backplate channel enabled
                else if (lastSubcmd == 0x02)
                    buf[4] = 8; // 8 LEDs
                return (0, buf);
            });
        // Capture last WriteBlock subcommand implicitly when BlockWrites records packets
        var wrappedAsrock = new AsrockProbeBus(asrockBus, sub => lastSubcmd = sub);
        var asrockDetected = SmbusDeviceScanner.ScanGpuPlugins(index, [wrappedAsrock], pluginsDir, userPluginsDir, sysInfo);
        Assert.Contains(asrockDetected, d => d.Plugin.RelativePath.Equals("AsRock/ASRock GPU.js", StringComparison.OrdinalIgnoreCase));
        var asrockMatch = asrockDetected.First(d => d.Plugin.RelativePath.Equals("AsRock/ASRock GPU.js", StringComparison.OrdinalIgnoreCase));

        using (var w = new SmbusDeviceWorker("GPU-ASROCK", asrockMatch.Plugin, wrappedAsrock, asrockMatch.Address, canvas, pluginsDir, userPluginsDir, sysInfo, cfg, 100, 15, false, true))
        {
            w.Start();
            Assert.True(SpinWait.SpinUntil(() => w.FramesRendered >= 2 && w.IsIdle, 4000), $"ASRock GPU error: {w.LastError}");
            Assert.Equal("", w.LastError);
            Assert.NotEmpty(asrockBus.BlockWrites);
        }
    }

    private sealed class AsrockProbeBus(MockGpuI2cTransport inner, Action<byte> onSubcmd) : ISmbusTransport
    {
        public string Name => inner.Name;
        public ushort VendorId => inner.VendorId;
        public ushort DeviceId => inner.DeviceId;
        public ushort SubVendorId => inner.SubVendorId;
        public ushort SubDeviceId => inner.SubDeviceId;
        public int Port => inner.Port;
        public bool IsSystemBus => inner.IsSystemBus;
        public bool IsAmdBus => inner.IsAmdBus;
        public bool IsIntelBus => inner.IsIntelBus;
        public bool IsNvidiaBus => inner.IsNvidiaBus;
        public bool IsAmdGpuBus => inner.IsAmdGpuBus;

        public int WriteQuick(byte address) => inner.WriteQuick(address);
        public int ReadByte(byte address, byte register) => inner.ReadByte(address, register);
        public int WriteByte(byte address, byte register, byte value) => inner.WriteByte(address, register, value);
        public int ReadWord(byte address, byte register) => inner.ReadWord(address, register);
        public int WriteWord(byte address, byte register, ushort value) => inner.WriteWord(address, register, value);
        public int WriteBlock(byte address, byte register, byte[] data)
        {
            if (data.Length >= 3) onSubcmd(data[2]);
            return inner.WriteBlock(address, register, data);
        }
        public int ReadByteWithoutRegister(byte address) => inner.ReadByteWithoutRegister(address);
        public int WriteBlockWithoutRegister(byte address, byte[] data) => inner.WriteBlockWithoutRegister(address, data);
        public (int Status, byte[] Data) ReadBlockBytes(byte address, byte register, int length) => inner.ReadBlockBytes(address, register, length);
        public void Dispose() { }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
