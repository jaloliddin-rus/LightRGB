using System.Runtime.InteropServices;

namespace LightRGB.Hardware;

public sealed class AmdAdlI2cBus : IAmdI2cTransport
{
    private const int ADL_OK = 0;
    private const int ADL_MAX_PATH = 256;
    private const int ADL_DL_I2C_ACTIONREAD = 1;
    private const int ADL_DL_I2C_ACTIONWRITE = 2;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AdlMainMallocDelegate(int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2MainControlCreateDelegate(AdlMainMallocDelegate callback, int enumConnectedAdapters, out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2MainControlDestroyDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2AdapterNumberOfAdaptersGetDelegate(IntPtr context, out int numAdapters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2AdapterAdapterInfoGetDelegate(IntPtr context, IntPtr lpInfo, int iInputSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2DisplayWriteAndReadI2CDelegate(IntPtr context, int iAdapterIndex, ref ADLI2C plI2C);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdapterInfo
    {
        public int iSize;
        public int iAdapterIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string strUDID;
        public int iBusNumber;
        public int iDeviceNumber;
        public int iFunctionNumber;
        public int iVendorID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string strAdapterName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string strDisplayName;
        public int iPresent;
        public int iExist;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string strDriverPath;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string strDriverPathExt;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string strPNPString;
        public int iOSDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ADLI2C
    {
        public int iSize;
        public int iLine;
        public int iAddress;
        public int iOffset;
        public int iAction;
        public int iSpeed;
        public int iDataSize;
        public IntPtr pcData;
    }

    private sealed class AdlContextHolder : IDisposable
    {
        public IntPtr Context;
        public AdlMainMallocDelegate MallocDelegate;
        public Adl2MainControlDestroyDelegate DestroyFn;
        public Adl2DisplayWriteAndReadI2CDelegate I2CFn;
        private int _refCount;

        public AdlContextHolder(
            IntPtr context,
            AdlMainMallocDelegate mallocDelegate,
            Adl2MainControlDestroyDelegate destroyFn,
            Adl2DisplayWriteAndReadI2CDelegate i2cFn)
        {
            Context = context;
            MallocDelegate = mallocDelegate;
            DestroyFn = destroyFn;
            I2CFn = i2cFn;
        }

        public void AddRef() => Interlocked.Increment(ref _refCount);

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0 && Context != IntPtr.Zero)
            {
                try { DestroyFn(Context); } catch { }
                Context = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            if (_refCount == 0 && Context != IntPtr.Zero)
            {
                try { DestroyFn(Context); } catch { }
                Context = IntPtr.Zero;
            }
        }
    }

    private readonly AdlContextHolder _holder;
    private readonly object _lock = new();
    private bool _disposed;

    private AmdAdlI2cBus(
        AdlContextHolder holder,
        int adapterIndex,
        string adapterName,
        ushort vendorId,
        ushort deviceId,
        ushort subVendorId,
        ushort subDeviceId)
    {
        _holder = holder;
        _holder.AddRef();
        AdapterIndex = adapterIndex;
        AdapterName = adapterName;
        VendorId = vendorId;
        DeviceId = deviceId;
        SubVendorId = subVendorId;
        SubDeviceId = subDeviceId;
    }

    public int AdapterIndex { get; }
    public string AdapterName { get; }
    public ushort VendorId { get; }
    public ushort DeviceId { get; }
    public ushort SubVendorId { get; }
    public ushort SubDeviceId { get; }

    public static IReadOnlyList<IAmdI2cTransport> OpenAllAdapters(out string diagnosticMessage)
    {
        var list = new List<IAmdI2cTransport>();
        if (!NativeLibrary.TryLoad("atiadlxx.dll", out IntPtr lib))
        {
            diagnosticMessage = "AMD ADL (atiadlxx.dll) not found.";
            return list;
        }

        try
        {
            var createFn = Marshal.GetDelegateForFunctionPointer<Adl2MainControlCreateDelegate>(NativeLibrary.GetExport(lib, "ADL2_Main_Control_Create"));
            var destroyFn = Marshal.GetDelegateForFunctionPointer<Adl2MainControlDestroyDelegate>(NativeLibrary.GetExport(lib, "ADL2_Main_Control_Destroy"));
            var countFn = Marshal.GetDelegateForFunctionPointer<Adl2AdapterNumberOfAdaptersGetDelegate>(NativeLibrary.GetExport(lib, "ADL2_Adapter_NumberOfAdapters_Get"));
            var infoFn = Marshal.GetDelegateForFunctionPointer<Adl2AdapterAdapterInfoGetDelegate>(NativeLibrary.GetExport(lib, "ADL2_Adapter_AdapterInfo_Get"));
            var i2cFn = Marshal.GetDelegateForFunctionPointer<Adl2DisplayWriteAndReadI2CDelegate>(NativeLibrary.GetExport(lib, "ADL2_Display_WriteAndReadI2C"));

            AdlMainMallocDelegate mallocDel = static size => Marshal.AllocHGlobal(size);
            if (createFn(mallocDel, 1, out IntPtr ctx) != ADL_OK || ctx == IntPtr.Zero)
            {
                diagnosticMessage = "ADL2_Main_Control_Create failed.";
                return list;
            }

            var holder = new AdlContextHolder(ctx, mallocDel, destroyFn, i2cFn);
            if (countFn(ctx, out int numAdapters) != ADL_OK || numAdapters <= 0)
            {
                holder.Dispose();
                diagnosticMessage = "No AMD ADL display adapters reported.";
                return list;
            }

            int structSize = Marshal.SizeOf<AdapterInfo>();
            int totalBytes = numAdapters * structSize;
            IntPtr buf = Marshal.AllocHGlobal(totalBytes);
            try
            {
                // Zero-init buffer
                for (int b = 0; b < totalBytes; b += 8)
                    Marshal.WriteInt64(buf, b, 0);

                if (infoFn(ctx, buf, totalBytes) != ADL_OK)
                {
                    holder.Dispose();
                    diagnosticMessage = "ADL2_Adapter_AdapterInfo_Get failed.";
                    return list;
                }

                var seenBuses = new HashSet<int>();
                for (int i = 0; i < numAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<AdapterInfo>(IntPtr.Add(buf, i * structSize));
                    if (!seenBuses.Add(info.iBusNumber))
                        continue;

                    if (TryParsePciIds(info.strPNPString, out ushort ven, out ushort dev, out ushort sven, out ushort sdev) &&
                        ven == 0x1002)
                    {
                        list.Add(new AmdAdlI2cBus(holder, info.iAdapterIndex, info.strAdapterName?.Trim() ?? "AMD Radeon GPU", ven, dev, sven, sdev));
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }

            if (list.Count == 0)
                holder.Dispose();

            diagnosticMessage = list.Count > 0
                ? $"AMD ADL initialized ({list.Count} Radeon adapter(s))"
                : "No AMD GPU (0x1002) adapters found via ADL.";
            return list;
        }
        catch (Exception ex)
        {
            diagnosticMessage = $"AMD ADL error: {ex.Message}";
            return list;
        }
    }

    public int ReadByte(byte address)
    {
        return ReadByteData(address, 0x00);
    }

    public int ReadByteData(byte address, byte register)
    {
        lock (_lock)
        {
            if (_disposed || _holder.Context == IntPtr.Zero) return -1;
            IntPtr dataPtr = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteByte(dataPtr, 0, 0);
                var i2c = new ADLI2C
                {
                    iSize = Marshal.SizeOf<ADLI2C>(),
                    iLine = 1,
                    iAddress = address << 1,
                    iOffset = register,
                    iAction = ADL_DL_I2C_ACTIONREAD,
                    iSpeed = 100,
                    iDataSize = 1,
                    pcData = dataPtr
                };
                int rc = _holder.I2CFn(_holder.Context, AdapterIndex, ref i2c);
                return rc == ADL_OK ? Marshal.ReadByte(dataPtr, 0) : -1;
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }

    public bool WriteByteData(byte address, byte register, byte value)
    {
        lock (_lock)
        {
            if (_disposed || _holder.Context == IntPtr.Zero) return false;
            IntPtr dataPtr = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteByte(dataPtr, 0, register);
                Marshal.WriteByte(dataPtr, 1, value);
                var i2c = new ADLI2C
                {
                    iSize = Marshal.SizeOf<ADLI2C>(),
                    iLine = 1,
                    iAddress = address << 1,
                    iOffset = 0,
                    iAction = ADL_DL_I2C_ACTIONWRITE,
                    iSpeed = 100,
                    iDataSize = 2,
                    pcData = dataPtr
                };
                int rc = _holder.I2CFn(_holder.Context, AdapterIndex, ref i2c);
                return rc == ADL_OK;
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }

    public int WriteQuick(byte address) => ReadByte(address) >= 0 ? 0 : -1;

    public bool WriteBlockData(byte address, byte register, byte[] data)
    {
        lock (_lock)
        {
            if (_disposed || _holder.Context == IntPtr.Zero || data is null) return false;
            int total = 1 + Math.Min(data.Length, 255);
            IntPtr dataPtr = Marshal.AllocHGlobal(Math.Max(8, total));
            try
            {
                Marshal.WriteByte(dataPtr, 0, register);
                for (int i = 0; i < total - 1; i++)
                    Marshal.WriteByte(dataPtr, 1 + i, data[i]);

                var i2c = new ADLI2C
                {
                    iSize = Marshal.SizeOf<ADLI2C>(),
                    iLine = 1,
                    iAddress = address << 1,
                    iOffset = 0,
                    iAction = ADL_DL_I2C_ACTIONWRITE,
                    iSpeed = 100,
                    iDataSize = total,
                    pcData = dataPtr
                };
                return _holder.I2CFn(_holder.Context, AdapterIndex, ref i2c) == ADL_OK;
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }

    public (int Status, byte[] Data) ReadBlockData(byte address, byte register, int length)
    {
        lock (_lock)
        {
            int len = Math.Clamp(length, 1, 256);
            if (_disposed || _holder.Context == IntPtr.Zero) return (-1, []);
            IntPtr dataPtr = Marshal.AllocHGlobal(Math.Max(8, len));
            try
            {
                for (int i = 0; i < len; i++)
                    Marshal.WriteByte(dataPtr, i, 0);

                var i2c = new ADLI2C
                {
                    iSize = Marshal.SizeOf<ADLI2C>(),
                    iLine = 1,
                    iAddress = address << 1,
                    iOffset = register,
                    iAction = ADL_DL_I2C_ACTIONREAD,
                    iSpeed = 100,
                    iDataSize = len,
                    pcData = dataPtr
                };
                int rc = _holder.I2CFn(_holder.Context, AdapterIndex, ref i2c);
                if (rc != ADL_OK) return (-1, []);

                var result = new byte[len];
                Marshal.Copy(dataPtr, result, 0, len);
                return (0, result);
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }

    public (int Status, byte[] Data) ReadBlockWithoutRegister(byte address, int length)
    {
        lock (_lock)
        {
            int len = Math.Clamp(length, 1, 256);
            if (_disposed || _holder.Context == IntPtr.Zero) return (-1, []);
            IntPtr dataPtr = Marshal.AllocHGlobal(Math.Max(8, len));
            try
            {
                for (int i = 0; i < len; i++)
                    Marshal.WriteByte(dataPtr, i, 0);

                var i2c = new ADLI2C
                {
                    iSize = Marshal.SizeOf<ADLI2C>(),
                    iLine = 1,
                    iAddress = address << 1,
                    iOffset = 0,
                    iAction = ADL_DL_I2C_ACTIONREAD,
                    iSpeed = 100,
                    iDataSize = len,
                    pcData = dataPtr
                };
                int rc = _holder.I2CFn(_holder.Context, AdapterIndex, ref i2c);
                if (rc != ADL_OK) return (-1, []);

                var result = new byte[len];
                Marshal.Copy(dataPtr, result, 0, len);
                return (0, result);
            }
            finally
            {
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }

    private static bool TryParsePciIds(string? pnp, out ushort ven, out ushort dev, out ushort sven, out ushort sdev)
    {
        ven = dev = sven = sdev = 0;
        if (string.IsNullOrEmpty(pnp)) return false;

        int vIdx = pnp.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        int dIdx = pnp.IndexOf("DEV_", StringComparison.OrdinalIgnoreCase);
        int sIdx = pnp.IndexOf("SUBSYS_", StringComparison.OrdinalIgnoreCase);
        if (vIdx < 0 || dIdx < 0 || sIdx < 0) return false;
        if (vIdx + 8 > pnp.Length || dIdx + 8 > pnp.Length || sIdx + 15 > pnp.Length) return false;

        return ushort.TryParse(pnp.AsSpan(vIdx + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out ven) &&
               ushort.TryParse(pnp.AsSpan(dIdx + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out dev) &&
               ushort.TryParse(pnp.AsSpan(sIdx + 7, 4), System.Globalization.NumberStyles.HexNumber, null, out sdev) &&
               ushort.TryParse(pnp.AsSpan(sIdx + 11, 4), System.Globalization.NumberStyles.HexNumber, null, out sven);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _holder.Release();
        }
    }
}
