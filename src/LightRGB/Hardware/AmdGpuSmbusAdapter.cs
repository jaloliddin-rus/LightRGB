namespace LightRGB.Hardware;

public sealed class AmdGpuSmbusAdapter : ISmbusTransport
{
    private readonly IAmdI2cTransport _inner;

    public AmdGpuSmbusAdapter(IAmdI2cTransport inner)
    {
        _inner = inner;
    }

    public string Name => string.IsNullOrWhiteSpace(_inner.AdapterName)
        ? $"AMD GPU I2C ({_inner.AdapterIndex})"
        : _inner.AdapterName;
    public ushort VendorId => _inner.VendorId;
    public ushort DeviceId => _inner.DeviceId;
    public ushort SubVendorId => _inner.SubVendorId;
    public ushort SubDeviceId => _inner.SubDeviceId;
    public int Port => _inner.AdapterIndex;
    public bool IsSystemBus => false;
    public bool IsAmdBus => false;
    public bool IsIntelBus => false;
    public bool IsNvidiaBus => false;
    public bool IsAmdGpuBus => true;

    public int WriteQuick(byte address)
        => SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address) ? _inner.WriteQuick(address) : -1;

    public int ReadByte(byte address, byte register)
        => _inner.ReadByteData(address, register);

    public int WriteByte(byte address, byte register, byte value)
        => SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address) && _inner.WriteByteData(address, register, value) ? 0 : -1;

    public int ReadWord(byte address, byte register)
    {
        var (status, data) = _inner.ReadBlockData(address, register, 2);
        return status == 0 && data.Length >= 2 ? (data[0] | (data[1] << 8)) : -1;
    }

    public int WriteWord(byte address, byte register, ushort value)
    {
        if (!SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address)) return -1;
        return _inner.WriteBlockData(address, register, [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF)]) ? 0 : -1;
    }

    public int WriteBlock(byte address, byte register, byte[] data)
    {
        if (!SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address)) return -1;
        return _inner.WriteBlockData(address, register, data) ? 0 : -1;
    }

    public int ReadByteWithoutRegister(byte address) => _inner.ReadByte(address);

    public int WriteBlockWithoutRegister(byte address, byte[] data)
    {
        if (!SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address) || data.Length == 0) return -1;
        byte cmd = data[0];
        byte[] rest = data.Length > 1 ? data[1..] : [];
        return rest.Length == 0
            ? (_inner.WriteByteData(address, cmd, 0) ? 0 : -1)
            : (_inner.WriteBlockData(address, cmd, rest) ? 0 : -1);
    }

    public (int Status, byte[] Data) ReadBlockBytes(byte address, byte register, int length)
        => _inner.ReadBlockData(address, register, length);

    public (int Status, byte[] Data) ReadBlockWithoutRegister(byte address, int length)
        => _inner.ReadBlockWithoutRegister(address, length);

    // Underlying IAmdI2cTransport is owned by DeviceManager.
    public void Dispose() { }
}
