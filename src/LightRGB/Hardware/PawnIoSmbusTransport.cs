using System.Runtime.InteropServices;

namespace LightRGB.Hardware;

public sealed class PawnIoSmbusTransport : ISmbusTransport
{
    private const int I2C_SMBUS_WRITE = 0;
    private const int I2C_SMBUS_READ = 1;

    private const int I2C_SMBUS_QUICK = 0;
    private const int I2C_SMBUS_BYTE = 1;
    private const int I2C_SMBUS_BYTE_DATA = 2;
    private const int I2C_SMBUS_WORD_DATA = 3;
    private const int I2C_SMBUS_BLOCK_DATA = 5;

    private const string GlobalSmbusMutexName = @"Global\Access_SMBUS.HTP.Method";
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoVersionDelegate(out uint version);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoOpenDelegate(out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoLoadDelegate(IntPtr handle, byte[] blob, nuint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoExecuteDelegate(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input,
        nuint inSize,
        ulong[]? output,
        nuint outSize,
        out nuint returnSize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnIoCloseDelegate(IntPtr handle);

    private sealed class PawnIoApi
    {
        public PawnIoVersionDelegate Version { get; }
        public PawnIoOpenDelegate Open { get; }
        public PawnIoLoadDelegate Load { get; }
        public PawnIoExecuteDelegate Execute { get; }
        public PawnIoCloseDelegate Close { get; }

        private PawnIoApi(IntPtr lib)
        {
            Version = Marshal.GetDelegateForFunctionPointer<PawnIoVersionDelegate>(NativeLibrary.GetExport(lib, "pawnio_version"));
            Open = Marshal.GetDelegateForFunctionPointer<PawnIoOpenDelegate>(NativeLibrary.GetExport(lib, "pawnio_open"));
            Load = Marshal.GetDelegateForFunctionPointer<PawnIoLoadDelegate>(NativeLibrary.GetExport(lib, "pawnio_load"));
            Execute = Marshal.GetDelegateForFunctionPointer<PawnIoExecuteDelegate>(NativeLibrary.GetExport(lib, "pawnio_execute"));
            Close = Marshal.GetDelegateForFunctionPointer<PawnIoCloseDelegate>(NativeLibrary.GetExport(lib, "pawnio_close"));
        }

        public static bool TryLoad(out PawnIoApi? api, out string error)
        {
            api = null;
            string[] candidates =
            [
                @"C:\Program Files\PawnIO\PawnIOLib.dll",
                Path.Combine(AppContext.BaseDirectory, "PawnIOLib.dll"),
                "PawnIOLib.dll"
            ];

            foreach (var path in candidates)
            {
                if (NativeLibrary.TryLoad(path, out IntPtr lib))
                {
                    try
                    {
                        api = new PawnIoApi(lib);
                        error = "";
                        return true;
                    }
                    catch (Exception ex)
                    {
                        error = $"Failed to resolve PawnIOLib exports: {ex.Message}";
                        return false;
                    }
                }
            }

            error = "PawnIOLib.dll not found (install PawnIO from pawnio.eu).";
            return false;
        }
    }

    private readonly PawnIoApi _api;
    private readonly IntPtr _handle;
    private readonly object _busLock = new();
    private readonly Mutex? _globalMutex;
    private bool _disposed;

    private PawnIoSmbusTransport(
        PawnIoApi api,
        IntPtr handle,
        string name,
        int port,
        ushort vendorId,
        ushort deviceId,
        ushort subVendorId,
        ushort subDeviceId,
        bool isAmd,
        bool isIntel)
    {
        _api = api;
        _handle = handle;
        Name = name;
        Port = port;
        VendorId = vendorId;
        DeviceId = deviceId;
        SubVendorId = subVendorId;
        SubDeviceId = subDeviceId;
        IsSystemBus = isAmd || isIntel;
        IsAmdBus = isAmd;
        IsIntelBus = isIntel;

        try { _globalMutex = new Mutex(false, GlobalSmbusMutexName); } catch { }
    }

    public string Name { get; }
    public ushort VendorId { get; }
    public ushort DeviceId { get; }
    public ushort SubVendorId { get; }
    public ushort SubDeviceId { get; }
    public int Port { get; }
    public bool IsSystemBus { get; }
    public bool IsAmdBus { get; }
    public bool IsIntelBus { get; }
    public bool IsNvidiaBus => false;
    public bool IsAmdGpuBus => false;

    public static IReadOnlyList<ISmbusTransport> TryOpenSystemBuses(string pawnioDir, out string diagnosticMessage)
    {
        var buses = new List<ISmbusTransport>();
        if (!PawnIoApi.TryLoad(out var api, out string loadErr) || api is null)
        {
            diagnosticMessage = loadErr;
            return buses;
        }

        int verHr = api.Version(out uint ver);
        if (verHr != 0)
        {
            diagnosticMessage = $"PawnIO driver unavailable (0x{verHr:X8}).";
            return buses;
        }

        // Probe AMD PIIX4 (port 0 and optional port 1)
        string piix4Path = Path.Combine(pawnioDir, "SmbusPIIX4.bin");
        if (File.Exists(piix4Path))
        {
            byte[] blob = File.ReadAllBytes(piix4Path);
            var h0 = TryOpenModule(api, blob, out int openHr);
            if (h0 == IntPtr.Zero && openHr == E_ACCESSDENIED)
            {
                diagnosticMessage = "PawnIO requires Administrator privileges for SMBus RAM control. Right-click LightRGB -> Run as Administrator.";
                return buses;
            }

            if (h0 != IntPtr.Zero)
            {
                if (SelectPiix4Port(api, h0, 0) && TryReadIdentity(api, h0, out ushort vid, out ushort did, out ushort svid, out ushort sdid))
                {
                    TrySetSleepMode(api, h0, 0);
                    buses.Add(new PawnIoSmbusTransport(api, h0, "PawnIO PIIX4 Port 0", 0, vid, did, svid, sdid, isAmd: true, isIntel: false));
                }
                else
                {
                    api.Close(h0);
                }

                // Try auxiliary port 1 (0x0B20) if hardware exposes it
                var h1 = TryOpenModule(api, blob, out _);
                if (h1 != IntPtr.Zero)
                {
                    if (SelectPiix4Port(api, h1, 1) && TryReadIdentity(api, h1, out ushort vid1, out ushort did1, out ushort svid1, out ushort sdid1))
                    {
                        TrySetSleepMode(api, h1, 0);
                        buses.Add(new PawnIoSmbusTransport(api, h1, "PawnIO PIIX4 Port 1", 1, vid1, did1, svid1, sdid1, isAmd: true, isIntel: false));
                    }
                    else
                    {
                        api.Close(h1);
                    }
                }
            }
        }

        // Fallback probe for Intel i801 if no AMD bus opened
        if (buses.Count == 0)
        {
            string i801Path = Path.Combine(pawnioDir, "SmbusI801.bin");
            if (File.Exists(i801Path))
            {
                byte[] blob = File.ReadAllBytes(i801Path);
                var hi = TryOpenModule(api, blob, out int openHr);
                if (hi == IntPtr.Zero && openHr == E_ACCESSDENIED)
                {
                    diagnosticMessage = "PawnIO requires Administrator privileges for SMBus RAM control. Right-click LightRGB -> Run as Administrator.";
                    return buses;
                }
                if (hi != IntPtr.Zero)
                {
                    if (TryReadIdentity(api, hi, out ushort vid, out ushort did, out ushort svid, out ushort sdid))
                    {
                        TrySetSleepMode(api, hi, 0);
                        buses.Add(new PawnIoSmbusTransport(api, hi, "PawnIO Intel i801", 0, vid, did, svid, sdid, isAmd: false, isIntel: true));
                    }
                    else
                    {
                        api.Close(hi);
                    }
                }
            }
        }

        diagnosticMessage = buses.Count > 0
            ? $"PawnIO v{ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF} active ({string.Join(", ", buses.Select(b => b.Name))})"
            : $"PawnIO v{ver >> 16}.{(ver >> 8) & 0xFF}.{ver & 0xFF} loaded, no compatible system SMBus opened.";
        return buses;
    }

    private static IntPtr TryOpenModule(PawnIoApi api, byte[] blob, out int lastHr)
    {
        lastHr = api.Open(out IntPtr handle);
        if (lastHr != 0 || handle == IntPtr.Zero)
            return IntPtr.Zero;

        lastHr = api.Load(handle, blob, (nuint)blob.Length);
        if (lastHr != 0)
        {
            api.Close(handle);
            return IntPtr.Zero;
        }
        return handle;
    }

    private static bool SelectPiix4Port(PawnIoApi api, IntPtr handle, int port)
    {
        Mutex? mtx = null;
        bool acquired = false;
        try
        {
            mtx = new Mutex(false, GlobalSmbusMutexName);
            try { acquired = mtx.WaitOne(2000); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return false;

            ulong[] inp = [unchecked((ulong)port)];
            ulong[] outp = new ulong[1];
            int hr = api.Execute(handle, "ioctl_piix4_port_sel", inp, 1, outp, 1, out _);
            return hr == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (acquired) { try { mtx?.ReleaseMutex(); } catch { } }
            mtx?.Dispose();
        }
    }

    private static void TrySetSleepMode(PawnIoApi api, IntPtr handle, int mode)
    {
        try
        {
            ulong[] inp = [unchecked((ulong)mode)];
            api.Execute(handle, "ioctl_set_sleep_mode", inp, 1, null, 0, out _);
        }
        catch { }
    }

    private static bool TryReadIdentity(PawnIoApi api, IntPtr handle, out ushort vid, out ushort did, out ushort svid, out ushort sdid)
    {
        vid = did = svid = sdid = 0;
        ulong[] inp = [0];
        ulong[] outp = new ulong[3];
        int hr = api.Execute(handle, "ioctl_identity", inp, 1, outp, 3, out _);
        if (hr != 0) return false;

        ulong ids = outp[2];
        vid = (ushort)(ids & 0xFFFF);
        did = (ushort)((ids >> 16) & 0xFFFF);
        svid = (ushort)((ids >> 32) & 0xFFFF);
        sdid = (ushort)((ids >> 48) & 0xFFFF);
        return true;
    }

    public int WriteQuick(byte address)
    {
        if (!SmbusSafetyPolicy.IsWriteAddressAllowed(address)) return -1;
        return ExecuteXfer(address, I2C_SMBUS_WRITE, 0, I2C_SMBUS_QUICK, null, out _);
    }

    public int ReadByte(byte address, byte register)
    {
        int rc = ExecuteXfer(address, I2C_SMBUS_READ, register, I2C_SMBUS_BYTE_DATA, null, out ulong[] outp);
        return rc == 0 ? (int)(outp[0] & 0xFF) : -1;
    }

    public int WriteByte(byte address, byte register, byte value)
    {
        if (!SmbusSafetyPolicy.IsWriteAddressAllowed(address)) return -1;
        return ExecuteXfer(address, I2C_SMBUS_WRITE, register, I2C_SMBUS_BYTE_DATA, [value], out _);
    }

    public int ReadWord(byte address, byte register)
    {
        int rc = ExecuteXfer(address, I2C_SMBUS_READ, register, I2C_SMBUS_WORD_DATA, null, out ulong[] outp);
        return rc == 0 ? (int)(outp[0] & 0xFFFF) : -1;
    }

    public int WriteWord(byte address, byte register, ushort value)
    {
        if (!SmbusSafetyPolicy.IsWriteAddressAllowed(address)) return -1;
        return ExecuteXfer(address, I2C_SMBUS_WRITE, register, I2C_SMBUS_WORD_DATA, [value], out _);
    }

    public int WriteBlock(byte address, byte register, byte[] data)
    {
        if (!SmbusSafetyPolicy.IsWriteAddressAllowed(address)) return -1;
        int len = Math.Clamp(data.Length, 1, 32);
        byte[] blockBuf = new byte[40];
        blockBuf[0] = (byte)len;
        Array.Copy(data, 0, blockBuf, 1, len);

        ulong[] packed = new ulong[5];
        Buffer.BlockCopy(blockBuf, 0, packed, 0, 40);
        return ExecuteXfer(address, I2C_SMBUS_WRITE, register, I2C_SMBUS_BLOCK_DATA, packed, out _);
    }

    public int ReadByteWithoutRegister(byte address)
    {
        int rc = ExecuteXfer(address, I2C_SMBUS_READ, 0, I2C_SMBUS_BYTE, null, out ulong[] outp);
        return rc == 0 ? (int)(outp[0] & 0xFF) : -1;
    }

    public int WriteBlockWithoutRegister(byte address, byte[] data)
    {
        if (!SmbusSafetyPolicy.IsWriteAddressAllowed(address) || data.Length == 0) return -1;
        byte cmd = data[0];
        byte[] rest = data.Length > 1 ? data[1..] : [];
        if (rest.Length == 0)
            return ExecuteXfer(address, I2C_SMBUS_WRITE, cmd, I2C_SMBUS_BYTE, null, out _);
        return WriteBlock(address, cmd, rest);
    }

    private int ExecuteXfer(byte address, int readWrite, byte command, int protocol, ulong[]? dataWords, out ulong[] outBuf)
    {
        outBuf = new ulong[5];
        if (_disposed) return -1;

        ulong[] inp = new ulong[9];
        inp[0] = address;
        inp[1] = (ulong)readWrite;
        inp[2] = command;
        inp[3] = (ulong)protocol;
        if (dataWords is not null)
        {
            int copy = Math.Min(dataWords.Length, 5);
            Array.Copy(dataWords, 0, inp, 4, copy);
        }

        lock (_busLock)
        {
            if (_disposed) return -1;
            bool acquired = false;
            try
            {
                if (_globalMutex is not null)
                {
                    try { acquired = _globalMutex.WaitOne(2000); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) return -1;
                }

                int hr = _api.Execute(_handle, "ioctl_smbus_xfer", inp, 9, outBuf, 5, out _);
                return hr == 0 ? 0 : -1;
            }
            catch
            {
                return -1;
            }
            finally
            {
                if (acquired) { try { _globalMutex?.ReleaseMutex(); } catch { } }
            }
        }
    }

    public void Dispose()
    {
        lock (_busLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _api.Close(_handle); } catch { }
            _globalMutex?.Dispose();
        }
    }
}
