namespace LightRGB.Hardware;

public interface IAmdI2cTransport : IDisposable
{
    int AdapterIndex { get; }
    string AdapterName { get; }
    ushort VendorId { get; }
    ushort DeviceId { get; }
    ushort SubVendorId { get; }
    ushort SubDeviceId { get; }

    int ReadByte(byte address);
    int ReadByteData(byte address, byte register);
    bool WriteByteData(byte address, byte register, byte value);

    int WriteQuick(byte address) => ReadByte(address) >= 0 ? 0 : -1;

    bool WriteBlockData(byte address, byte register, byte[] data) => false;

    (int Status, byte[] Data) ReadBlockData(byte address, byte register, int length)
    {
        int len = Math.Clamp(length, 0, 256);
        var buf = new byte[len];
        for (int i = 0; i < len; i++)
        {
            int b = ReadByteData(address, (byte)((register + i) & 0xFF));
            if (b < 0) return (-1, []);
            buf[i] = (byte)b;
        }
        return (0, buf);
    }

    (int Status, byte[] Data) ReadBlockWithoutRegister(byte address, int length) => (-1, []);
}
