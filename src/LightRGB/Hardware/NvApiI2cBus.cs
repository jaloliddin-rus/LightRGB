using System.Runtime.InteropServices;
using System.Text;

namespace LightRGB.Hardware;

public sealed class NvApiI2cBus : ISmbusTransport
{
    private const int NVAPI_OK = 0;
    private const uint ID_Initialize = 0x0150E828;
    private const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
    private const uint ID_GetPCIIdentifiers = 0x2DDFB66E;
    private const uint ID_GetFullName = 0xCEEE8E9F;
    private const uint ID_I2CReadEx = 0x4D7B0709;
    private const uint ID_I2CWriteEx = 0x283AC65A;
    private const uint NVAPI_I2C_SPEED_DEPRECATED = 0xFFFF;
    private const uint NVAPI_I2C_SPEED_100KHZ = 4;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NvQueryInterfaceDelegate(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvInitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvEnumPhysicalGpusDelegate([Out] IntPtr[] handles, out int gpuCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvGetPciIdentifiersDelegate(IntPtr gpu, out uint deviceId, out uint subSystemId, out uint revisionId, out uint extDeviceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvGetFullNameDelegate(IntPtr gpu, byte[] nameBuf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvI2cXferExDelegate(IntPtr gpu, ref NvI2cInfoV3 i2cInfo, ref uint unknown);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NvI2cInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte I2cDevAddress;
        public IntPtr PbI2cRegAddress;
        public uint RegAddrSize;
        public IntPtr PbData;
        public uint CbSize;
        public uint I2cSpeed;
        public uint I2cSpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;
    }

    public static int StructSize => Marshal.SizeOf<NvI2cInfoV3>();
    public static uint StructVersion => (uint)(StructSize | (3 << 16));

    private readonly IntPtr _gpuHandle;
    private readonly NvI2cXferExDelegate _readEx;
    private readonly NvI2cXferExDelegate _writeEx;
    private readonly object _lock = new();
    private bool _disposed;

    private NvApiI2cBus(
        IntPtr gpuHandle,
        NvI2cXferExDelegate readEx,
        NvI2cXferExDelegate writeEx,
        int port,
        string name,
        ushort vendorId,
        ushort deviceId,
        ushort subVendorId,
        ushort subDeviceId)
    {
        _gpuHandle = gpuHandle;
        _readEx = readEx;
        _writeEx = writeEx;
        Port = port;
        Name = name;
        VendorId = vendorId;
        DeviceId = deviceId;
        SubVendorId = subVendorId;
        SubDeviceId = subDeviceId;
    }

    public string Name { get; }
    public ushort VendorId { get; }
    public ushort DeviceId { get; }
    public ushort SubVendorId { get; }
    public ushort SubDeviceId { get; }
    public int Port { get; }
    public bool IsSystemBus => false;
    public bool IsAmdBus => false;
    public bool IsIntelBus => false;
    public bool IsNvidiaBus => true;
    public bool IsAmdGpuBus => false;

    public static IReadOnlyList<ISmbusTransport> OpenAllAdapters(out string diagnosticMessage)
    {
        var list = new List<ISmbusTransport>();
        if (!NativeLibrary.TryLoad("nvapi64.dll", out IntPtr lib))
        {
            diagnosticMessage = "NVIDIA NVAPI (nvapi64.dll) not found.";
            return list;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out IntPtr qiPtr))
            {
                diagnosticMessage = "nvapi_QueryInterface export not found.";
                return list;
            }

            var qi = Marshal.GetDelegateForFunctionPointer<NvQueryInterfaceDelegate>(qiPtr);
            IntPtr initPtr = qi(ID_Initialize);
            IntPtr enumPtr = qi(ID_EnumPhysicalGPUs);
            IntPtr pciPtr = qi(ID_GetPCIIdentifiers);
            IntPtr namePtr = qi(ID_GetFullName);
            IntPtr readPtr = qi(ID_I2CReadEx);
            IntPtr writePtr = qi(ID_I2CWriteEx);

            if (initPtr == IntPtr.Zero || enumPtr == IntPtr.Zero || pciPtr == IntPtr.Zero || readPtr == IntPtr.Zero || writePtr == IntPtr.Zero)
            {
                diagnosticMessage = "Failed to resolve required NVAPI I2C endpoints.";
                return list;
            }

            var initFn = Marshal.GetDelegateForFunctionPointer<NvInitializeDelegate>(initPtr);
            if (initFn() != NVAPI_OK)
            {
                diagnosticMessage = "NvAPI_Initialize failed.";
                return list;
            }

            var enumFn = Marshal.GetDelegateForFunctionPointer<NvEnumPhysicalGpusDelegate>(enumPtr);
            var pciFn = Marshal.GetDelegateForFunctionPointer<NvGetPciIdentifiersDelegate>(pciPtr);
            NvGetFullNameDelegate? nameFn = namePtr != IntPtr.Zero
                ? Marshal.GetDelegateForFunctionPointer<NvGetFullNameDelegate>(namePtr)
                : null;
            var readEx = Marshal.GetDelegateForFunctionPointer<NvI2cXferExDelegate>(readPtr);
            var writeEx = Marshal.GetDelegateForFunctionPointer<NvI2cXferExDelegate>(writePtr);

            var gpuHandles = new IntPtr[64];
            if (enumFn(gpuHandles, out int gpuCount) != NVAPI_OK || gpuCount <= 0)
            {
                diagnosticMessage = "No NVIDIA physical GPUs detected.";
                return list;
            }

            for (int i = 0; i < Math.Min(gpuCount, gpuHandles.Length); i++)
            {
                IntPtr hGpu = gpuHandles[i];
                if (hGpu == IntPtr.Zero) continue;

                if (pciFn(hGpu, out uint devId, out uint subSysId, out _, out _) != NVAPI_OK)
                    continue;

                ushort ven = (ushort)(devId & 0xFFFF);
                ushort dev = (ushort)((devId >> 16) & 0xFFFF);
                ushort sven = (ushort)(subSysId & 0xFFFF);
                ushort sdev = (ushort)((subSysId >> 16) & 0xFFFF);

                string gpuName = $"NVIDIA GPU #{i}";
                if (nameFn is not null)
                {
                    var nameBuf = new byte[64];
                    if (nameFn(hGpu, nameBuf) == NVAPI_OK)
                    {
                        int zero = Array.IndexOf(nameBuf, (byte)0);
                        string parsed = Encoding.ASCII.GetString(nameBuf, 0, zero >= 0 ? zero : nameBuf.Length).Trim();
                        if (!string.IsNullOrWhiteSpace(parsed))
                            gpuName = parsed;
                    }
                }

                list.Add(new NvApiI2cBus(hGpu, readEx, writeEx, i, gpuName, ven, dev, sven, sdev));
            }

            diagnosticMessage = list.Count > 0
                ? $"NVIDIA NVAPI initialized ({list.Count} GPU(s))"
                : "No NVIDIA GPUs returned from NVAPI.";
            return list;
        }
        catch (Exception ex)
        {
            diagnosticMessage = $"NVIDIA NVAPI error: {ex.Message}";
            return list;
        }
    }

    public int WriteQuick(byte address)
    {
        if (!SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address)) return -1;
        return ExecuteRead(address, null, 1, out _);
    }

    public int ReadByte(byte address, byte register)
    {
        int rc = ExecuteRead(address, [register], 1, out byte[] data);
        return rc == 0 && data.Length >= 1 ? data[0] : -1;
    }

    public int WriteByte(byte address, byte register, byte value)
    {
        if (!SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address)) return -1;
        return ExecuteWrite(address, [register], [value]);
    }

    public int ReadWord(byte address, byte register)
    {
        int rc = ExecuteRead(address, [register], 2, out byte[] data);
        return rc == 0 && data.Length >= 2 ? (data[0] | (data[1] << 8)) : -1;
    }

    public int WriteWord(byte address, byte register, ushort value)
    {
        if (!SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address)) return -1;
        return ExecuteWrite(address, [register], [(byte)(value & 0xFF), (byte)((value >> 8) & 0xFF)]);
    }

    public int WriteBlock(byte address, byte register, byte[] data)
    {
        if (!SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address)) return -1;
        if (data is null || data.Length == 0)
            return ExecuteWrite(address, null, [register]);

        if (data.Length <= 16 && ExecuteWrite(address, [register], data) == 0)
            return 0;

        byte[] combined = new byte[1 + Math.Min(data.Length, 255)];
        combined[0] = register;
        Buffer.BlockCopy(data, 0, combined, 1, combined.Length - 1);
        return ExecuteWrite(address, null, combined);
    }

    public int ReadByteWithoutRegister(byte address)
    {
        int rc = ExecuteRead(address, null, 1, out byte[] data);
        return rc == 0 && data.Length >= 1 ? data[0] : -1;
    }

    public int WriteBlockWithoutRegister(byte address, byte[] data)
    {
        if (!SmbusSafetyPolicy.IsGpuI2cAddressAllowed(address) || data is null || data.Length == 0)
            return -1;
        return ExecuteWrite(address, null, data);
    }

    public (int Status, byte[] Data) ReadBlockBytes(byte address, byte register, int length)
    {
        int rc = ExecuteRead(address, [register], Math.Clamp(length, 1, 256), out byte[] data);
        return (rc, data);
    }

    public (int Status, byte[] Data) ReadBlockWithoutRegister(byte address, int length)
    {
        int rc = ExecuteRead(address, null, Math.Clamp(length, 1, 256), out byte[] data);
        return (rc, data);
    }

    private int ExecuteRead(byte address, byte[]? regBytes, int readLen, out byte[] result)
    {
        result = [];
        lock (_lock)
        {
            if (_disposed) return -1;
            int regLen = regBytes?.Length ?? 0;
            int dataLen = Math.Max(1, readLen);

            IntPtr regPtr = regLen > 0 ? Marshal.AllocHGlobal(regLen) : IntPtr.Zero;
            IntPtr dataPtr = Marshal.AllocHGlobal(dataLen);
            try
            {
                if (regLen > 0 && regBytes is not null)
                    Marshal.Copy(regBytes, 0, regPtr, regLen);

                var info = new NvI2cInfoV3
                {
                    Version = StructVersion,
                    DisplayMask = 0,
                    IsDdcPort = 0,
                    I2cDevAddress = (byte)(address << 1),
                    PbI2cRegAddress = regPtr,
                    RegAddrSize = (uint)regLen,
                    PbData = dataPtr,
                    CbSize = (uint)dataLen,
                    I2cSpeed = NVAPI_I2C_SPEED_DEPRECATED,
                    I2cSpeedKhz = NVAPI_I2C_SPEED_100KHZ,
                    PortId = 1,
                    IsPortIdSet = 1
                };
                uint unknown = 0;
                int rc = _readEx(_gpuHandle, ref info, ref unknown);
                if (rc != NVAPI_OK) return -1;

                result = new byte[readLen];
                Marshal.Copy(dataPtr, result, 0, readLen);
                return 0;
            }
            catch
            {
                return -1;
            }
            finally
            {
                if (regPtr != IntPtr.Zero) Marshal.FreeHGlobal(regPtr);
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }

    private int ExecuteWrite(byte address, byte[]? regBytes, byte[] payload)
    {
        lock (_lock)
        {
            if (_disposed || payload.Length == 0) return -1;
            int regLen = regBytes?.Length ?? 0;

            IntPtr regPtr = regLen > 0 ? Marshal.AllocHGlobal(regLen) : IntPtr.Zero;
            IntPtr dataPtr = Marshal.AllocHGlobal(payload.Length);
            try
            {
                if (regLen > 0 && regBytes is not null)
                    Marshal.Copy(regBytes, 0, regPtr, regLen);
                Marshal.Copy(payload, 0, dataPtr, payload.Length);

                var info = new NvI2cInfoV3
                {
                    Version = StructVersion,
                    DisplayMask = 0,
                    IsDdcPort = 0,
                    I2cDevAddress = (byte)(address << 1),
                    PbI2cRegAddress = regPtr,
                    RegAddrSize = (uint)regLen,
                    PbData = dataPtr,
                    CbSize = (uint)payload.Length,
                    I2cSpeed = NVAPI_I2C_SPEED_DEPRECATED,
                    I2cSpeedKhz = NVAPI_I2C_SPEED_100KHZ,
                    PortId = 1,
                    IsPortIdSet = 1
                };
                uint unknown = 0;
                return _writeEx(_gpuHandle, ref info, ref unknown) == NVAPI_OK ? 0 : -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                if (regPtr != IntPtr.Zero) Marshal.FreeHGlobal(regPtr);
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock) { _disposed = true; }
    }
}
