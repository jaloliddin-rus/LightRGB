using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace LightRGB.Hardware;

public static class HidNative
{
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const uint WAIT_OBJECT_0 = 0x00000000;

    private static readonly Regex VidRegex = new(@"vid_([0-9a-fA-F]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PidRegex = new(@"pid_([0-9a-fA-F]{4})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MiRegex = new(@"&mi_([0-9a-fA-F]{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ColRegex = new(@"&col([0-9a-fA-F]{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static IReadOnlyList<HidEndpointInfo> EnumerateEndpoints()
    {
        var results = new List<HidEndpointInfo>();
        HidD_GetHidGuid(out Guid hidGuid);

        IntPtr hDevInfo = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1))
            return results;

        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };

            uint index = 0;
            while (SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref hidGuid, index++, ref did))
            {
                uint requiredSize = 0;
                SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref did, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
                if (requiredSize < 8)
                    continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    int cbSize = IntPtr.Size == 8 ? 8 : 6;
                    Marshal.WriteInt32(detailBuf, cbSize);

                    if (!SetupDiGetDeviceInterfaceDetailW(hDevInfo, ref did, detailBuf, requiredSize, out _, IntPtr.Zero))
                        continue;

                    string? path = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                    if (string.IsNullOrEmpty(path))
                        continue;

                    results.Add(InspectEndpoint(path));
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }

        return results;
    }

    public static IReadOnlyList<HidDeviceGroup> EnumerateDeviceGroups()
    {
        var endpoints = EnumerateEndpoints();
        var groups = new List<HidDeviceGroup>();

        foreach (var g in endpoints
            .Where(e => e.VendorId != 0 && e.ProductId != 0)
            .GroupBy(e => (e.VendorId, e.ProductId))
            .OrderBy(g => g.Key.VendorId).ThenBy(g => g.Key.ProductId))
        {
            var list = g.ToList();
            string mfg = list.Select(e => e.Manufacturer).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            string prod = list.Select(e => e.Product).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
            groups.Add(new HidDeviceGroup(g.Key.VendorId, g.Key.ProductId, mfg, prod, list));
        }

        return groups;
    }

    private static HidEndpointInfo InspectEndpoint(string path)
    {
        ushort vid = ParseHexUshort(VidRegex, path);
        ushort pid = ParseHexUshort(PidRegex, path);
        int iface = ParseHexInt(MiRegex, path, defaultValue: 0);
        int col = ParseHexInt(ColRegex, path, defaultValue: 0);

        ushort usagePage = 0;
        ushort usage = 0;
        ushort inLen = 0;
        ushort outLen = 0;
        ushort featLen = 0;
        string mfg = "";
        string prod = "";
        bool accessible = false;

        using var handle = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            accessible = true;
            var attrs = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (HidD_GetAttributes(handle, ref attrs))
            {
                if (attrs.VendorID != 0) vid = attrs.VendorID;
                if (attrs.ProductID != 0) pid = attrs.ProductID;
            }

            var strBuf = new byte[512];
            if (HidD_GetManufacturerString(handle, strBuf, (uint)strBuf.Length))
                mfg = DecodeUtf16Z(strBuf);
            Array.Clear(strBuf);
            if (HidD_GetProductString(handle, strBuf, (uint)strBuf.Length))
                prod = DecodeUtf16Z(strBuf);

            if (HidD_GetPreparsedData(handle, out IntPtr preparsed) && preparsed != IntPtr.Zero)
            {
                try
                {
                    if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) == HIDP_STATUS_SUCCESS)
                    {
                        usagePage = caps.UsagePage;
                        usage = caps.Usage;
                        inLen = caps.InputReportByteLength;
                        outLen = caps.OutputReportByteLength;
                        featLen = caps.FeatureReportByteLength;
                    }
                }
                finally
                {
                    HidD_FreePreparsedData(preparsed);
                }
            }
        }

        return new HidEndpointInfo(
            path, vid, pid, iface, usagePage, usage, col,
            inLen, outLen, featLen, mfg, prod, accessible);
    }

    internal static SafeFileHandle OpenIoHandle(string path, out bool canRead)
    {
        var handle = CreateFileW(
            path,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);

        if (!handle.IsInvalid)
        {
            canRead = true;
            return handle;
        }

        handle.Dispose();
        canRead = false;
        return CreateFileW(
            path,
            GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_OVERLAPPED,
            IntPtr.Zero);
    }

    internal static int WriteOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs = 1000)
    {
        if (handle.IsInvalid) return -1;

        using var evt = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped
        {
            EventHandle = evt.SafeWaitHandle.DangerousGetHandle()
        };

        bool ok = WriteFile(handle, buffer, (uint)buffer.Length, out uint written, ref overlapped);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            const int ERROR_IO_PENDING = 997;
            if (err != ERROR_IO_PENDING)
                return -1;

            if (WaitForSingleObject(overlapped.EventHandle, (uint)timeoutMs) != WAIT_OBJECT_0)
            {
                CancelIoEx(handle, ref overlapped);
                return -1;
            }

            if (!GetOverlappedResult(handle, ref overlapped, out written, false))
                return -1;
        }

        return (int)written;
    }

    internal static int ReadOverlapped(SafeFileHandle handle, byte[] buffer, int timeoutMs = 60)
    {
        if (handle.IsInvalid) return 0;

        using var evt = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped
        {
            EventHandle = evt.SafeWaitHandle.DangerousGetHandle()
        };

        bool ok = ReadFile(handle, buffer, (uint)buffer.Length, out uint bytesRead, ref overlapped);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            const int ERROR_IO_PENDING = 997;
            if (err != ERROR_IO_PENDING)
                return 0;

            if (WaitForSingleObject(overlapped.EventHandle, (uint)timeoutMs) != WAIT_OBJECT_0)
            {
                CancelIoEx(handle, ref overlapped);
                return 0;
            }

            if (!GetOverlappedResult(handle, ref overlapped, out bytesRead, false))
                return 0;
        }

        return (int)bytesRead;
    }

    internal static bool SetFeature(SafeFileHandle handle, byte[] buffer)
        => !handle.IsInvalid && HidD_SetFeature(handle, buffer, (uint)buffer.Length);

    internal static bool GetFeature(SafeFileHandle handle, byte[] buffer)
        => !handle.IsInvalid && HidD_GetFeature(handle, buffer, (uint)buffer.Length);

    internal static void FlushQueue(SafeFileHandle handle)
    {
        if (!handle.IsInvalid)
            HidD_FlushQueue(handle);
    }

    private static ushort ParseHexUshort(Regex regex, string input)
    {
        var m = regex.Match(input);
        return m.Success && ushort.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort val)
            ? val
            : (ushort)0;
    }

    private static int ParseHexInt(Regex regex, string input, int defaultValue)
    {
        var m = regex.Match(input);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int val)
            ? val
            : defaultValue;
    }

    private static string DecodeUtf16Z(byte[] bytes)
    {
        string raw = Encoding.Unicode.GetString(bytes);
        int nul = raw.IndexOf('\0');
        return (nul >= 0 ? raw[..nul] : raw).Trim();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle hidDeviceObject, byte[] buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, byte[] buffer, uint bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, uint reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FlushQueue(SafeFileHandle hidDeviceObject);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}
