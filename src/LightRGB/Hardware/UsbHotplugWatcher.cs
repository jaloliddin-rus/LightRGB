using System.Windows.Forms;

namespace LightRGB.Hardware;

public sealed class UsbHotplugWatcher : NativeWindow, IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    private readonly Action _onDevicesChanged;
    private readonly System.Threading.Timer _debounceTimer;

    public UsbHotplugWatcher(Action onDevicesChanged)
    {
        _onDevicesChanged = onDevicesChanged;
        _debounceTimer = new System.Threading.Timer(_ =>
        {
            try { _onDevicesChanged(); } catch { }
        }, null, Timeout.Infinite, Timeout.Infinite);

        CreateHandle(new CreateParams
        {
            Caption = "LightRGB_HotplugWatcher"
        });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DEVICECHANGE && (int)m.WParam == DBT_DEVNODES_CHANGED)
        {
            _debounceTimer.Change(1000, Timeout.Infinite);
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        _debounceTimer.Dispose();
        DestroyHandle();
    }
}
