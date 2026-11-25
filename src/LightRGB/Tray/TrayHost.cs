using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Extraction;
using LightRGB.Hardware;

namespace LightRGB.Tray;

public sealed class TrayHost : ApplicationContext
{
    private readonly DeviceManager _manager;
    private readonly string _webUrl;
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _appIcon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly UsbHotplugWatcher _hotplug;

    public TrayHost(DeviceManager manager, string webUrl)
    {
        _manager = manager;
        _webUrl = webUrl;
        _appIcon = CreateRgbIcon();

        var effectsMenu = new ToolStripMenuItem("Effects");
        foreach (var eff in CanvasEngine.AvailableEffects)
        {
            string name = eff;
            effectsMenu.DropDownItems.Add(name, null, (_, _) =>
            {
                var cur = _manager.Canvas.GetSettingsSnapshot();
                cur.EffectName = name;
                _manager.UpdateEffect(cur);
            });
        }

        _pauseItem = new ToolStripMenuItem("Pause Effects", null, (_, _) => TogglePause());
        _startupItem = new ToolStripMenuItem("Run at Windows Startup", null, (_, _) => ToggleStartup())
        {
            Checked = StartupManager.IsEnabled()
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Control Panel", null, (_, _) => OpenWebUi());
        menu.Items.Add(effectsMenu);
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Rescan Devices", null, (_, _) => _manager.RescanDevicesAsync(includeSmbus: true));
        menu.Items.Add("Import SignalRGB Plugins...", null, (_, _) => ImportPluginsFromInstaller());
        menu.Items.Add("Install PawnIO Driver (RAM)...", null, (_, _) => InstallPawnIoDriver());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _trayIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = "LightRGB",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => OpenWebUi();

        _hotplug = new UsbHotplugWatcher(() => _manager.RescanDevicesAsync(includeSmbus: false));
    }

    private void TogglePause()
    {
        bool paused = !_manager.Config.GetSnapshot().Paused;
        _manager.UpdateGlobalSettings(paused: paused);
        _pauseItem.Checked = paused;
    }

    private void ToggleStartup()
    {
        bool next = !StartupManager.IsEnabled();
        bool applyAndExit = _manager.Config.GetSnapshot().StartupApplyAndExit;
        if (StartupManager.SetEnabled(next, _manager.DataDir, applyAndExit))
            _startupItem.Checked = next;
    }

    private void ImportPluginsFromInstaller()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select SignalRGB Installer (Install_SignalRgb.exe)",
            Filter = "SignalRGB Installer (*.exe)|*.exe|All Files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            FileName = "Install_SignalRgb.exe"
        };

        if (dlg.ShowDialog() != DialogResult.OK)
            return;

        try
        {
            var res = new SignalRgbExtractor().Extract(dlg.FileName, _manager.DataDir);
            Program.SyncDefaultUserPlugins(_manager.DataDir);
            var newIndex = new PluginIndexer().LoadOrBuild(_manager.DataDir, _manager.SystemInfo, forceRebuild: true);
            _manager.ReloadPluginIndex(newIndex);
            MessageBox.Show(
                $"Imported {res.PluginsExtracted} plugins ({res.ComponentsExtracted} components) and indexed {newIndex.Entries.Count} runnable plugins.\n\nLightRGB is rescanning your devices now.",
                "SignalRGB Plugins Imported",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to import plugins from installer:\n{ex.Message}",
                "LightRGB Import Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void InstallPawnIoDriver()
    {
        string? installer = PawnIoInstallerHelper.FindBundledInstaller(AppContext.BaseDirectory);
        if (installer is null)
        {
            MessageBox.Show(
                $"Bundled PawnIO_setup.exe was not found next to LightRGB. Opening {PawnIoInstallerHelper.DownloadUrl}.",
                "PawnIO Installer Missing",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            try { Process.Start(new ProcessStartInfo(PawnIoInstallerHelper.DownloadUrl) { UseShellExecute = true }); } catch { }
            return;
        }

        var (ok, rebootRequired, msg) = PawnIoInstallerHelper.TryRunSilentInstall(installer);
        MessageBox.Show(
            msg,
            ok ? "PawnIO Setup" : "PawnIO Setup Error",
            MessageBoxButtons.OK,
            ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

        if (ok && !rebootRequired)
            _manager.RescanDevicesAsync(includeSmbus: true);
    }

    private void OpenWebUi()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_webUrl) { UseShellExecute = true });
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        _hotplug.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static Icon CreateRgbIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bgBrush = new SolidBrush(Color.FromArgb(24, 24, 34));
            g.FillEllipse(bgBrush, 1, 1, 30, 30);

            using var path = new GraphicsPath();
            path.AddEllipse(3, 3, 26, 26);
            using var pgb = new PathGradientBrush(path)
            {
                CenterColor = Color.White,
                SurroundColors = [Color.FromArgb(0, 180, 255)]
            };
            using var pen1 = new Pen(Color.FromArgb(255, 60, 140), 4.5f);
            using var pen2 = new Pen(Color.FromArgb(0, 210, 255), 4.5f);
            using var pen3 = new Pen(Color.FromArgb(90, 255, 130), 4.5f);
            g.DrawArc(pen1, 5, 5, 22, 22, 210, 120);
            g.DrawArc(pen2, 5, 5, 22, 22, 330, 120);
            g.DrawArc(pen3, 5, 5, 22, 22, 90, 120);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
