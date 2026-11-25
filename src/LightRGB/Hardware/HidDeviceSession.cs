using Microsoft.Win32.SafeHandles;

namespace LightRGB.Hardware;

public sealed class HidDeviceSession : IHidTransport
{
    private readonly List<HidEndpointInfo> _endpoints;
    private readonly object _ioLock = new();
    private HidEndpointInfo _activeEndpoint;
    private SafeFileHandle? _handle;
    private bool _canRead;

    public HidDeviceSession(IReadOnlyList<HidEndpointInfo> endpoints, HidEndpointInfo? initialEndpoint = null)
    {
        if (endpoints.Count == 0)
            throw new ArgumentException("At least one HID endpoint is required.", nameof(endpoints));

        _endpoints = endpoints.ToList();
        _activeEndpoint = initialEndpoint ?? _endpoints[0];
    }

    public IReadOnlyList<HidEndpointInfo> Endpoints => _endpoints;
    public HidEndpointInfo ActiveEndpoint => _activeEndpoint;
    public int LastReadSize { get; private set; }

    public bool SetEndpoint(int? iface, int? usage, int? usagePage, int? collection)
    {
        var match = _endpoints.FirstOrDefault(ep =>
            (!iface.HasValue || iface.Value < 0 || ep.Interface == iface.Value) &&
            (!usage.HasValue || usage.Value < 0 || ep.Usage == usage.Value) &&
            (!usagePage.HasValue || usagePage.Value < 0 || ep.UsagePage == usagePage.Value) &&
            (!collection.HasValue || collection.Value < 0 || ep.Collection == collection.Value));

        if (match is null)
            return false;

        lock (_ioLock)
        {
            if (match.DevicePath != _activeEndpoint.DevicePath)
            {
                _handle?.Dispose();
                _handle = null;
                _activeEndpoint = match;
            }
        }
        return true;
    }

    public int Write(byte[] data, int length)
    {
        if (data is null || length <= 0) return 0;

        lock (_ioLock)
        {
            var handle = EnsureHandle();
            if (handle is null || handle.IsInvalid)
                return -1;

            int targetLen = length;
            if (_activeEndpoint.OutputReportLength > 0)
                targetLen = Math.Max(targetLen, _activeEndpoint.OutputReportLength);

            var buf = new byte[targetLen];
            Array.Copy(data, 0, buf, 0, Math.Min(data.Length, targetLen));
            return HidNative.WriteOverlapped(handle, buf);
        }
    }

    public byte[] Read(byte[]? cmd, int length, int timeoutMs = 60)
    {
        lock (_ioLock)
        {
            var handle = EnsureHandle();
            if (handle is null || handle.IsInvalid || !_canRead)
            {
                LastReadSize = 0;
                return [];
            }

            int targetLen = Math.Max(length, _activeEndpoint.InputReportLength);
            if (targetLen <= 0) targetLen = 65;

            var buf = new byte[targetLen];
            if (cmd is { Length: > 0 })
                buf[0] = cmd[0];

            int read = HidNative.ReadOverlapped(handle, buf, timeoutMs);
            LastReadSize = read;
            if (read <= 0) return [];
            if (read == buf.Length) return buf;
            return buf[..read];
        }
    }

    public int SendFeatureReport(byte[] data, int length)
    {
        if (data is null || length <= 0) return 0;

        lock (_ioLock)
        {
            var handle = EnsureHandle();
            if (handle is null || handle.IsInvalid)
                return -1;

            int targetLen = length;
            if (_activeEndpoint.FeatureReportLength > 0)
                targetLen = Math.Max(targetLen, _activeEndpoint.FeatureReportLength);

            var buf = new byte[targetLen];
            Array.Copy(data, 0, buf, 0, Math.Min(data.Length, targetLen));
            return HidNative.SetFeature(handle, buf) ? targetLen : -1;
        }
    }

    public byte[] GetFeatureReport(byte[] data, int length)
    {
        lock (_ioLock)
        {
            var handle = EnsureHandle();
            if (handle is null || handle.IsInvalid)
            {
                LastReadSize = 0;
                return [];
            }

            int targetLen = Math.Max(length, _activeEndpoint.FeatureReportLength);
            if (targetLen <= 0) targetLen = 65;

            var buf = new byte[targetLen];
            if (data is { Length: > 0 })
                buf[0] = data[0];

            if (!HidNative.GetFeature(handle, buf))
            {
                LastReadSize = 0;
                return [];
            }

            LastReadSize = buf.Length;
            return buf;
        }
    }

    public void ClearReadBuffer()
    {
        lock (_ioLock)
        {
            var handle = EnsureHandle();
            if (handle is not null && !handle.IsInvalid)
                HidNative.FlushQueue(handle);
        }
    }

    private SafeFileHandle? EnsureHandle()
    {
        if (_handle is not null && !_handle.IsInvalid)
            return _handle;

        _handle?.Dispose();
        _handle = HidNative.OpenIoHandle(_activeEndpoint.DevicePath, out _canRead);
        return _handle;
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            _handle?.Dispose();
            _handle = null;
        }
    }
}
