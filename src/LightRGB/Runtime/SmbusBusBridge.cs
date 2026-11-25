using System.Text.Json;
using LightRGB.Hardware;

namespace LightRGB.Runtime;

public sealed class SmbusBusBridge
{
    private readonly ISmbusTransport _bus;
    private readonly byte? _fixedAddress;
    private readonly Action<string> _log;

    public SmbusBusBridge(ISmbusTransport bus, byte? fixedAddress, Action<string> log)
    {
        _bus = bus;
        _fixedAddress = fixedAddress;
        _log = log;
    }

    public string Name() => _bus.Name;
    public int Vendor() => _bus.VendorId;
    public int Product() => _bus.DeviceId;
    public int SubVendor() => _bus.SubVendorId;
    public int SubDevice() => _bus.SubDeviceId;
    public int Port() => _bus.Port;
    public bool IsSystemBus() => _bus.IsSystemBus;
    public bool IsAMDBus() => _bus.IsAmdBus;
    public bool IsNuvotonBus() => false;
    public bool IsNvidiaBus() => _bus.IsNvidiaBus;
    public bool IsAMDGpuBus() => _bus.IsAmdGpuBus;
    public int GetAddress() => _fixedAddress ?? 0;

    public int WriteQuick(int addr)
    {
        byte target = ResolveAddr(addr);
        if (!SmbusSafetyPolicy.IsWriteAllowedForBus(_bus, target)) return -1;
        return _bus.WriteQuick(target);
    }

    public int ReadByte(int addr, int reg)
    {
        byte target = ResolveAddr(addr);
        return _bus.ReadByte(target, (byte)(reg & 0xFF));
    }

    public int WriteByte(int addr, int reg, int val)
    {
        byte target = ResolveAddr(addr);
        if (!SmbusSafetyPolicy.IsWriteAllowedForBus(_bus, target)) return -1;
        return _bus.WriteByte(target, (byte)(reg & 0xFF), (byte)(val & 0xFF));
    }

    public int ReadWord(int addr, int reg)
    {
        byte target = ResolveAddr(addr);
        return _bus.ReadWord(target, (byte)(reg & 0xFF));
    }

    public int WriteWord(int addr, int reg, int val)
    {
        byte target = ResolveAddr(addr);
        if (!SmbusSafetyPolicy.IsWriteAllowedForBus(_bus, target)) return -1;
        return _bus.WriteWord(target, (byte)(reg & 0xFF), (ushort)(val & 0xFFFF));
    }

    public int WriteBlock(int addr, int reg, int length, string jsonArray)
    {
        byte target = ResolveAddr(addr);
        if (!SmbusSafetyPolicy.IsWriteAllowedForBus(_bus, target)) return -1;
        byte[] data = ParseBytes(jsonArray, length);
        return _bus.WriteBlock(target, (byte)(reg & 0xFF), data);
    }

    public string ReadBlockBytes(int addr, int reg, int length)
    {
        byte target = ResolveAddr(addr);
        var (status, bytes) = _bus.ReadBlockBytes(target, (byte)(reg & 0xFF), length);
        int[] dataInts = Array.ConvertAll(bytes, static b => (int)b);
        return JsonSerializer.Serialize(new object[] { status, dataInts });
    }

    public int ReadByteWithoutRegister(int addr)
    {
        byte target = ResolveAddr(addr);
        return _bus.ReadByteWithoutRegister(target);
    }

    public string ReadBlockWithoutRegister(int addr, int length)
    {
        byte target = ResolveAddr(addr);
        var (status, bytes) = _bus.ReadBlockWithoutRegister(target, length);
        int[] dataInts = Array.ConvertAll(bytes, static b => (int)b);
        return JsonSerializer.Serialize(new object[] { status, dataInts });
    }

    public int WriteBlockWithoutRegister(int addr, int length, string jsonArray)
    {
        byte target = ResolveAddr(addr);
        if (!SmbusSafetyPolicy.IsWriteAllowedForBus(_bus, target)) return -1;
        byte[] data = ParseBytes(jsonArray, length);
        return _bus.WriteBlockWithoutRegister(target, data);
    }

    public void Pause(int ms)
    {
        if (ms > 0) Thread.Sleep(Math.Min(ms, 2000));
    }

    public void Log(string msg) => _log(msg);

    private byte ResolveAddr(int addr)
        => _fixedAddress ?? (byte)(addr & 0x7F);

    private static byte[] ParseBytes(string jsonArray, int length)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonArray);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            int arrLen = doc.RootElement.GetArrayLength();
            int len = length > 0 ? Math.Min(length, arrLen) : arrLen;
            var res = new byte[len];
            int idx = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (idx >= len) break;
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int v))
                    res[idx] = unchecked((byte)(v & 0xFF));
                idx++;
            }
            return res;
        }
        catch
        {
            return [];
        }
    }

    public const string JsFreeAddressBusShim = """
        globalThis.bus = {
            toString() { return "I2CBusWrapperFreeAddress"; },
            Name() { return _busHost.Name(); },
            Type() { return "smbus"; },
            Vendor() { return _busHost.Vendor(); },
            Product() { return _busHost.Product(); },
            SubVendor() { return _busHost.SubVendor(); },
            SubDevice() { return _busHost.SubDevice(); },
            Port() { return _busHost.Port(); },
            IsSystemBus() { return _busHost.IsSystemBus(); },
            IsAMDBus() { return _busHost.IsAMDBus(); },
            IsNuvotonBus() { return _busHost.IsNuvotonBus(); },
            IsNvidiaBus() { return _busHost.IsNvidiaBus(); },
            IsAMDGpuBus() { return _busHost.IsAMDGpuBus(); },
            GetAddress() { return _busHost.GetAddress(); },
            TransactionLock() {},
            TransactionUnlock() {},
            WriteQuick(addr) { return _busHost.WriteQuick(addr | 0); },
            ReadByte(addr, reg) { return _busHost.ReadByte(addr | 0, reg | 0); },
            WriteByte(addr, reg, val) { return _busHost.WriteByte(addr | 0, reg | 0, val | 0); },
            ReadWord(addr, reg) { return _busHost.ReadWord(addr | 0, reg | 0); },
            WriteWord(addr, reg, val) { return _busHost.WriteWord(addr | 0, reg | 0, val | 0); },
            WriteBlock(addr, reg, len, data) {
                const arr = Array.isArray(data) ? data.map(x => (x | 0) & 0xff) : [];
                return _busHost.WriteBlock(addr | 0, reg | 0, len | 0, JSON.stringify(arr));
            },
            WriteBlockBytes(addr, reg, len, data) { return this.WriteBlock(addr, reg, len, data); },
            ReadBlockBytes(addr, reg, len) { return JSON.parse(_busHost.ReadBlockBytes(addr | 0, reg | 0, len | 0)); },
            ReadByteWithoutRegister(addr) { return _busHost.ReadByteWithoutRegister(addr | 0); },
            ReadBlockWithoutRegister(addr, len) { return JSON.parse(_busHost.ReadBlockWithoutRegister(addr | 0, len | 0)); },
            WriteBlockWithoutRegister(addr, len, data) {
                const arr = Array.isArray(data) ? data.map(x => (x | 0) & 0xff) : [];
                return _busHost.WriteBlockWithoutRegister(addr | 0, len | 0, JSON.stringify(arr));
            },
            pause(ms) { _busHost.Pause(ms | 0); },
            log(msg) { _busHost.Log(String(msg)); }
        };
        """;

    public const string JsFixedAddressBusShim = """
        globalThis.bus = {
            toString() { return "I2CBusWrapperFixedAddress"; },
            Name() { return _busHost.Name(); },
            Type() { return "smbus"; },
            Vendor() { return _busHost.Vendor(); },
            Product() { return _busHost.Product(); },
            SubVendor() { return _busHost.SubVendor(); },
            SubDevice() { return _busHost.SubDevice(); },
            Port() { return _busHost.Port(); },
            IsSystemBus() { return _busHost.IsSystemBus(); },
            IsAMDBus() { return _busHost.IsAMDBus(); },
            IsNuvotonBus() { return _busHost.IsNuvotonBus(); },
            IsNvidiaBus() { return _busHost.IsNvidiaBus(); },
            IsAMDGpuBus() { return _busHost.IsAMDGpuBus(); },
            GetAddress() { return _busHost.GetAddress(); },
            TransactionLock() {},
            TransactionUnlock() {},
            WriteQuick() { return _busHost.WriteQuick(0); },
            ReadByte(reg) { return _busHost.ReadByte(0, reg | 0); },
            WriteByte(reg, val) { return _busHost.WriteByte(0, reg | 0, val | 0); },
            ReadWord(reg) { return _busHost.ReadWord(0, reg | 0); },
            WriteWord(reg, val) { return _busHost.WriteWord(0, reg | 0, val | 0); },
            WriteBlock(reg, len, data) {
                const arr = Array.isArray(data) ? data.map(x => (x | 0) & 0xff) : [];
                return _busHost.WriteBlock(0, reg | 0, len | 0, JSON.stringify(arr));
            },
            WriteBlockBytes(reg, len, data) { return this.WriteBlock(reg, len, data); },
            ReadBlockBytes(reg, len) { return JSON.parse(_busHost.ReadBlockBytes(0, reg | 0, len | 0)); },
            ReadByteWithoutRegister() { return _busHost.ReadByteWithoutRegister(0); },
            ReadBlockWithoutRegister(len) { return JSON.parse(_busHost.ReadBlockWithoutRegister(0, len | 0)); },
            WriteBlockWithoutRegister(len, data) {
                const arr = Array.isArray(data) ? data.map(x => (x | 0) & 0xff) : [];
                return _busHost.WriteBlockWithoutRegister(0, len | 0, JSON.stringify(arr));
            },
            pause(ms) { _busHost.Pause(ms | 0); },
            log(msg) { _busHost.Log(String(msg)); }
        };
        """;
}
