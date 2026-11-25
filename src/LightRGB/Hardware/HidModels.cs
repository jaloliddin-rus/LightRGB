namespace LightRGB.Hardware;

public sealed record HidEndpointInfo(
    string DevicePath,
    ushort VendorId,
    ushort ProductId,
    int Interface,
    ushort UsagePage,
    ushort Usage,
    int Collection,
    ushort InputReportLength,
    ushort OutputReportLength,
    ushort FeatureReportLength,
    string Manufacturer,
    string Product,
    bool IsAccessible);

public sealed record HidDeviceGroup(
    ushort VendorId,
    ushort ProductId,
    string Manufacturer,
    string Product,
    IReadOnlyList<HidEndpointInfo> Endpoints)
{
    public bool IsLampArray => Endpoints.Any(e => e.UsagePage == 0x0059);
    public bool AnyAccessible => Endpoints.Any(e => e.IsAccessible);
}
