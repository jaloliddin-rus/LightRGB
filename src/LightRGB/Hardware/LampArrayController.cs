namespace LightRGB.Hardware;

public sealed class LampArrayController : IDisposable
{
    private readonly HidDeviceSession _session;
    private int _lampCount;
    private bool _autonomousDisabled;

    public LampArrayController(HidDeviceGroup group)
    {
        var ep = group.Endpoints.First(e => e.UsagePage == 0x0059);
        _session = new HidDeviceSession(group.Endpoints, ep);
        DeviceName = string.IsNullOrWhiteSpace(group.Product)
            ? $"HID LampArray (0x{group.VendorId:X4}:0x{group.ProductId:X4})"
            : $"{group.Product} (LampArray)";
    }

    public string DeviceName { get; }
    public int LampCount => _lampCount;
    public bool IsLockedByOs { get; private set; }
    public string LastStatus { get; private set; } = "Initialized";

    public bool Initialize()
    {
        int featLen = Math.Max(22, (int)_session.ActiveEndpoint.FeatureReportLength);
        var attrReport = _session.GetFeatureReport([0x01], featLen);
        if (attrReport.Length >= 3)
        {
            _lampCount = BitConverter.ToUInt16(attrReport, 1);
            if (_lampCount <= 0) _lampCount = 1;
            IsLockedByOs = false;
            LastStatus = $"Ready ({_lampCount} lamps)";
            SetAutonomousMode(false);
            return true;
        }

        IsLockedByOs = true;
        LastStatus = "Controlled exclusively by Windows Dynamic Lighting or unavailable";
        return false;
    }

    public bool SetAutonomousMode(bool autonomous)
    {
        byte[] report = [0x06, (byte)(autonomous ? 1 : 0)];
        int res = _session.SendFeatureReport(report, report.Length);
        _autonomousDisabled = !autonomous && res > 0;
        return res > 0;
    }

    public bool SetAllColor(byte r, byte g, byte b)
    {
        if (IsLockedByOs || _lampCount <= 0)
            return false;

        // LampRangeUpdateReport (Report ID 0x05)
        ushort endLamp = (ushort)Math.Max(0, _lampCount - 1);
        byte[] report =
        [
            0x05,
            0x01, // UpdateComplete flag
            0x00, 0x00, // LampIdStart
            (byte)(endLamp & 0xFF), (byte)((endLamp >> 8) & 0xFF), // LampIdEnd
            r, g, b, 255
        ];

        int res = _session.SendFeatureReport(report, report.Length);
        if (res < 0)
            res = _session.Write(report, report.Length);

        return res > 0;
    }

    public bool SetPerLampColors(IReadOnlyList<(byte R, byte G, byte B)> colors)
    {
        if (IsLockedByOs || _lampCount <= 0)
            return false;

        int total = Math.Min(_lampCount, colors.Count);
        const int batchMax = 8;
        bool anyOk = false;

        for (int start = 0; start < total; start += batchMax)
        {
            int count = Math.Min(batchMax, total - start);
            bool isLast = start + count >= total;
            var report = new byte[51];
            report[0] = 0x04; // LampMultiUpdateReport
            report[1] = (byte)count;
            report[2] = (byte)(isLast ? 0x01 : 0x00);

            for (int i = 0; i < count; i++)
            {
                ushort id = (ushort)(start + i);
                int idOff = 3 + i * 2;
                report[idOff] = (byte)(id & 0xFF);
                report[idOff + 1] = (byte)((id >> 8) & 0xFF);

                int colOff = 19 + i * 4;
                var c = colors[id];
                report[colOff] = c.R;
                report[colOff + 1] = c.G;
                report[colOff + 2] = c.B;
                report[colOff + 3] = 255;
            }

            int res = _session.SendFeatureReport(report, report.Length);
            if (res < 0)
                res = _session.Write(report, report.Length);
            if (res > 0) anyOk = true;
        }

        return anyOk;
    }

    public void Dispose()
    {
        if (_autonomousDisabled)
        {
            try { SetAutonomousMode(true); } catch { }
        }
        _session.Dispose();
    }
}
