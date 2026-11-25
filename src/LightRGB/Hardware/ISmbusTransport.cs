namespace LightRGB.Hardware;

public static class SmbusSafetyPolicy
{
    public static bool IsWriteAddressAllowed(byte address)
    {
        // Strictly block SPD EEPROMs (0x50..0x57), thermal sensors (0x18..0x1F),
        // and low control / VRM / clock generator addresses (0x00..0x38, 0x78..0x7F).
        return (address >= 0x39 && address <= 0x40) ||
               address == 0x4F ||
               (address >= 0x58 && address <= 0x67) ||
               (address >= 0x70 && address <= 0x77);
    }

    public static bool IsGpuI2cAddressAllowed(byte address)
    {
        // Dedicated GPU I2C buses do not host system SPD EEPROMs; block reserved 7-bit I2C ranges.
        return address >= 0x10 && address <= 0x77;
    }

    public static bool IsWriteAllowedForBus(ISmbusTransport bus, byte address)
    {
        if (bus.IsSystemBus)
            return IsWriteAddressAllowed(address);
        if (bus.IsNvidiaBus || bus.IsAmdGpuBus)
            return IsGpuI2cAddressAllowed(address);
        return false;
    }
}

public interface ISmbusTransport : IDisposable
{
    string Name { get; }
    ushort VendorId { get; }
    ushort DeviceId { get; }
    ushort SubVendorId { get; }
    ushort SubDeviceId { get; }
    int Port { get; }
    bool IsSystemBus { get; }
    bool IsAmdBus { get; }
    bool IsIntelBus { get; }
    bool IsNvidiaBus { get; }
    bool IsAmdGpuBus { get; }

    int WriteQuick(byte address);
    int ReadByte(byte address, byte register);
    int WriteByte(byte address, byte register, byte value);
    int ReadWord(byte address, byte register);
    int WriteWord(byte address, byte register, ushort value);
    int WriteBlock(byte address, byte register, byte[] data);
    int ReadByteWithoutRegister(byte address);
    int WriteBlockWithoutRegister(byte address, byte[] data);

    (int Status, byte[] Data) ReadBlockBytes(byte address, byte register, int length)
    {
        int len = Math.Clamp(length, 0, 256);
        var data = new byte[len];
        for (int i = 0; i < len; i++)
        {
            int b = ReadByte(address, (byte)((register + i) & 0xFF));
            if (b < 0) return (-1, []);
            data[i] = (byte)b;
        }
        return (0, data);
    }

    (int Status, byte[] Data) ReadBlockWithoutRegister(byte address, int length) => (-1, []);
}
