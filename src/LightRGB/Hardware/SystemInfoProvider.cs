using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace LightRGB.Hardware;

public sealed record MotherboardInfo(string Manufacturer, string Model);
public sealed record BiosInfo(string Vendor, string Version);
public sealed record PciDeviceInfo(string Class, string Name, string InstanceId);

public sealed class SystemInfoProvider
{
    public MotherboardInfo GetMotherboardInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is not null)
            {
                var mfg = key.GetValue("BaseBoardManufacturer")?.ToString()?.Trim() ?? "";
                var model = key.GetValue("BaseBoardProduct")?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(mfg) || !string.IsNullOrEmpty(model))
                    return new MotherboardInfo(mfg, model);
            }
        }
        catch
        {
            // Ignore registry read issues in restricted environments
        }

        return new MotherboardInfo("Generic", "Unknown");
    }

    public BiosInfo GetBiosInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is not null)
            {
                var vendor = key.GetValue("BIOSVendor")?.ToString()?.Trim() ?? "";
                var version = key.GetValue("BIOSVersion")?.ToString()?.Trim() ?? "";
                return new BiosInfo(vendor, version);
            }
        }
        catch
        {
            // Ignore registry read issues
        }

        return new BiosInfo("", "");
    }

    public (bool Installed, string ImagePath) GetPawnIoStatus()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            if (key is not null)
            {
                var path = key.GetValue("ImagePath")?.ToString() ?? "";
                return (true, path);
            }
        }
        catch
        {
            // Ignore
        }

        return (false, "");
    }

    public IReadOnlyList<PciDeviceInfo> GetDisplayAndSmbusDevices()
    {
        var results = new List<PciDeviceInfo>();
        const uint DIGCF_PRESENT = 0x00000002;
        const uint DIGCF_ALLCLASSES = 0x00000004;
        const uint SPDRP_DEVICEDESC = 0x00000000;
        const uint SPDRP_CLASS = 0x00000007;
        const uint SPDRP_FRIENDLYNAME = 0x0000000C;

        IntPtr hDevInfo = SetupDiGetClassDevsW(IntPtr.Zero, "PCI", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1))
            return results;

        try
        {
            var devInfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            uint index = 0;
            var idBuf = new StringBuilder(512);
            var propBuf = new byte[1024];

            while (SetupDiEnumDeviceInfo(hDevInfo, index++, ref devInfo))
            {
                idBuf.Clear();
                if (!SetupDiGetDeviceInstanceIdW(hDevInfo, ref devInfo, idBuf, idBuf.Capacity, out _))
                    continue;

                string cls = ReadStringProperty(hDevInfo, ref devInfo, SPDRP_CLASS, propBuf);
                if (!string.Equals(cls, "Display", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(cls, "System", StringComparison.OrdinalIgnoreCase))
                    continue;

                string name = ReadStringProperty(hDevInfo, ref devInfo, SPDRP_FRIENDLYNAME, propBuf);
                if (string.IsNullOrEmpty(name))
                    name = ReadStringProperty(hDevInfo, ref devInfo, SPDRP_DEVICEDESC, propBuf);

                string lower = name.ToLowerInvariant();
                if (lower.Contains("smbus") || lower.Contains("i2c") ||
                    cls.Equals("Display", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new PciDeviceInfo(cls, name, idBuf.ToString()));
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(hDevInfo);
        }

        return results;
    }

    private static string ReadStringProperty(IntPtr hDevInfo, ref SP_DEVINFO_DATA devInfo, uint property, byte[] buffer)
    {
        if (SetupDiGetDeviceRegistryPropertyW(hDevInfo, ref devInfo, property, out _, buffer, (uint)buffer.Length, out uint reqSize) && reqSize >= 2)
        {
            int len = (int)Math.Min(reqSize, (uint)buffer.Length);
            return Encoding.Unicode.GetString(buffer, 0, len).TrimEnd('\0');
        }
        return "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, uint property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
}
