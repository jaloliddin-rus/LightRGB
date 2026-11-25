namespace LightRGB.Hardware;

public interface IHidTransport : IDisposable
{
    IReadOnlyList<HidEndpointInfo> Endpoints { get; }
    HidEndpointInfo ActiveEndpoint { get; }
    int LastReadSize { get; }

    bool SetEndpoint(int? iface, int? usage, int? usagePage, int? collection);
    int Write(byte[] data, int length);
    byte[] Read(byte[]? cmd, int length, int timeoutMs = 60);
    int SendFeatureReport(byte[] data, int length);
    byte[] GetFeatureReport(byte[] data, int length);
    void ClearReadBuffer();
}
