using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Extraction;
using LightRGB.Hardware;
using LightRGB.Tray;
using LightRGB.Web;

namespace LightRGB;

public static class Program
{
    private static bool _winFormsInitialized;
    private static bool _consoleDetached;

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    public static string ResolveDefaultDataDir(string? dataDirOverride, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(dataDirOverride))
            return dataDirOverride;

        if (!string.IsNullOrWhiteSpace(baseDirectory) &&
            File.Exists(Path.Combine(baseDirectory, "portable.txt")))
        {
            return Path.Combine(baseDirectory, "data");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LightRGB");
    }

    private static void EnsureWinFormsInitialized()
    {
        if (_winFormsInitialized) return;
        ApplicationConfiguration.Initialize();
        _winFormsInitialized = true;
    }

    private static void DetachConsoleOnce()
    {
        if (_consoleDetached) return;
        _consoleDetached = true;
        try { FreeConsole(); } catch { }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private static int GetConfiguredPort(string dataDir)
    {
        try
        {
            using var tmp = new ConfigStore(dataDir);
            return tmp.GetSnapshot().WebPort;
        }
        catch
        {
            return 4747;
        }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        bool doExtract = false;
        string? installerArg = null;
        bool doScan = false;
        bool minimized = false;
        bool noTray = false;
        int? applyAndExitMs = null;
        int? portOverride = null;
        string? dataDirOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a.ToLowerInvariant())
            {
                case "--extract":
                    doExtract = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        installerArg = args[++i];
                    break;
                case "--scan":
                    doScan = true;
                    break;
                case "--minimized":
                case "--silent":
                    minimized = true;
                    break;
                case "--no-tray":
                    noTray = true;
                    break;
                case "--apply-and-exit":
                    applyAndExitMs = 2500;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int ms))
                    {
                        applyAndExitMs = Math.Clamp(ms, 200, 30000);
                        i++;
                    }
                    break;
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                    {
                        portOverride = Math.Clamp(p, 1024, 65535);
                        i++;
                    }
                    break;
                case "--data-dir":
                    if (i + 1 < args.Length)
                        dataDirOverride = args[++i];
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
            }
        }

        string dataDir = ResolveDefaultDataDir(dataDirOverride, AppContext.BaseDirectory);
        Directory.CreateDirectory(dataDir);

        Mutex? instanceMutex = null;
        if (!doExtract && !doScan && !applyAndExitMs.HasValue)
        {
            instanceMutex = new Mutex(true, @"Local\LightRGB_SingleInstance_Mutex", out bool createdNew);
            if (!createdNew)
            {
                instanceMutex.Dispose();
                int existingPort = portOverride ?? GetConfiguredPort(dataDir);
                OpenBrowser($"http://127.0.0.1:{existingPort}/");
                return 0;
            }
        }

        try
        {
            var sysInfo = new SystemInfoProvider();
            var extractor = new SignalRgbExtractor();
            var indexer = new PluginIndexer();

            if (doExtract)
            {
                string? instPath = SignalRgbExtractor.FindInstallerPath(installerArg);
                if (instPath is null)
                {
                    Console.Error.WriteLine("Error: Install_SignalRgb.exe not found. Pass its path: --extract <path>");
                    return 1;
                }

                Console.WriteLine($"Extracting SignalRGB plugins from: {instPath}");
                var res = extractor.Extract(instPath, dataDir, Console.WriteLine);
                SyncDefaultUserPlugins(dataDir);
                var index = indexer.LoadOrBuild(dataDir, sysInfo, forceRebuild: true, Console.WriteLine);
                Console.WriteLine($"Done. Plugins={res.PluginsExtracted}, Components={res.ComponentsExtracted}, Indexed={index.Entries.Count}");
                return 0;
            }

            if (!noTray && !doScan && !applyAndExitMs.HasValue)
                DetachConsoleOnce();

            // Auto-extract on first run if plugins folder is empty and installer is nearby (or prompt GUI user)
            string pluginsDir = Path.Combine(dataDir, "plugins");
            if (!Directory.Exists(pluginsDir) || !Directory.EnumerateFiles(pluginsDir, "*.js", SearchOption.AllDirectories).Any())
            {
                string? instPath = SignalRgbExtractor.FindInstallerPath(installerArg);
                if (instPath is null && !minimized && !noTray && !applyAndExitMs.HasValue && !doScan)
                {
                    EnsureWinFormsInitialized();
                    var choice = MessageBox.Show(
                        "No SignalRGB device plugins have been imported yet.\n\nWould you like to select Install_SignalRgb.exe now to import device plugins? (Built-in RAM, GPU, LampArray, and MonsGeek support works even without it.)",
                        "LightRGB First-Time Setup",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (choice == DialogResult.Yes)
                    {
                        using var dlg = new OpenFileDialog
                        {
                            Title = "Select SignalRGB Installer (Install_SignalRgb.exe)",
                            Filter = "SignalRGB Installer (*.exe)|*.exe|All Files (*.*)|*.*",
                            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                            FileName = "Install_SignalRgb.exe"
                        };
                        if (dlg.ShowDialog() == DialogResult.OK)
                            instPath = dlg.FileName;
                    }
                }

                if (instPath is not null)
                {
                    try
                    {
                        if (!minimized && (noTray || doScan))
                            Console.WriteLine($"First run: extracting SignalRGB plugins from {instPath}...");
                        extractor.Extract(instPath, dataDir, msg =>
                        {
                            if (!minimized && (noTray || doScan)) Console.WriteLine(msg);
                        });
                    }
                    catch (Exception ex)
                    {
                        if (!minimized && !noTray && !applyAndExitMs.HasValue && !doScan)
                        {
                            EnsureWinFormsInitialized();
                            MessageBox.Show($"Could not extract plugins:\n{ex.Message}", "LightRGB Extraction Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            Console.Error.WriteLine($"Plugin extraction error: {ex.Message}");
                        }
                    }
                }
            }

            SyncDefaultUserPlugins(dataDir);

            var pluginIndex = indexer.LoadOrBuild(dataDir, sysInfo, forceRebuild: false, msg =>
            {
                if (!minimized && (noTray || doScan)) Console.WriteLine(msg);
            });

            if (doScan)
            {
                RunScan(dataDir, sysInfo, pluginIndex);
                return 0;
            }

            using var configStore = new ConfigStore(dataDir);
            if (portOverride.HasValue)
                configStore.UpdateGlobalSettings(webPort: portOverride.Value);

            if (!minimized && !noTray && !applyAndExitMs.HasValue)
            {
                var pawnStatus = sysInfo.GetPawnIoStatus();
                if (!pawnStatus.Installed && !configStore.GetSnapshot().PawnIoPromptDismissed)
                {
                    EnsureWinFormsInitialized();
                    var installChoice = MessageBox.Show(
                        "PawnIO driver is not installed.\n\nLightRGB uses PawnIO (by namazso) to control RGB RAM sticks over SMBus. Without it, USB devices and GPUs still work, but RGB RAM won't be detected.\n\nWould you like to install PawnIO now?",
                        "PawnIO Driver Setup (RGB RAM)",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (installChoice == DialogResult.Yes)
                    {
                        string? bundled = PawnIoInstallerHelper.FindBundledInstaller(AppContext.BaseDirectory);
                        if (bundled is not null)
                        {
                            var (ok, _, msg) = PawnIoInstallerHelper.TryRunSilentInstall(bundled);
                            MessageBox.Show(msg, ok ? "PawnIO Setup" : "PawnIO Setup Warning", MessageBoxButtons.OK,
                                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                        }
                        else
                        {
                            OpenBrowser(PawnIoInstallerHelper.DownloadUrl);
                        }
                    }
                    else
                    {
                        configStore.UpdateGlobalSettings(pawnIoPromptDismissed: true);
                    }
                }
            }

            var canvas = new CanvasEngine();
            using var manager = new DeviceManager(dataDir, configStore, canvas, sysInfo, pluginIndex, forceKeepLightsOn: applyAndExitMs.HasValue);
            manager.Start(asyncInitialScan: !applyAndExitMs.HasValue);

            if (minimized && !noTray)
                DetachConsoleOnce();

            if (applyAndExitMs.HasValue)
            {
                if (!minimized)
                    Console.WriteLine($"LightRGB applying configured lighting for {applyAndExitMs.Value} ms...");
                Thread.Sleep(applyAndExitMs.Value);
                return 0;
            }

            int port = portOverride ?? configStore.GetSnapshot().WebPort;
            using var webServer = new WebUiServer(manager, port);
            webServer.Start();

            if (noTray)
            {
                Console.WriteLine($"LightRGB running headless. Web UI: {webServer.BaseUrl}");
                Console.WriteLine($"Indexed SignalRGB plugins: {pluginIndex.Entries.Count}. Press Ctrl+C to exit.");
                using var exitEvt = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    exitEvt.Set();
                };
                exitEvt.Wait();
                return 0;
            }

            EnsureWinFormsInitialized();
            using var tray = new TrayHost(manager, webServer.BaseUrl);
            if (!minimized)
                OpenBrowser(webServer.BaseUrl);
            Application.Run(tray);
            return 0;
        }
        finally
        {
            if (instanceMutex is not null)
            {
                try { instanceMutex.ReleaseMutex(); } catch { }
                instanceMutex.Dispose();
            }
        }
    }

    internal static void SyncDefaultUserPlugins(string dataDir)
    {
        try
        {
            string srcDir = Path.Combine(AppContext.BaseDirectory, "default_user_plugins");
            string dstDir = Path.Combine(dataDir, "user_plugins");
            Directory.CreateDirectory(dstDir);
            if (!Directory.Exists(srcDir)) return;

            foreach (var file in Directory.EnumerateFiles(srcDir, "*.js", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcDir, file);
                string dst = Path.Combine(dstDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst))
                {
                    File.Copy(file, dst, overwrite: false);
                }
                else
                {
                    string existing = File.ReadAllText(dst);
                    bool isManaged = existing.StartsWith("// @lightrgb-managed-plugin", StringComparison.Ordinal) ||
                                     existing.Contains("Publisher() { return \"LightRGB Community\"; }", StringComparison.Ordinal);
                    if (isManaged)
                    {
                        string incoming = File.ReadAllText(file);
                        if (!string.Equals(existing, incoming, StringComparison.Ordinal))
                            File.WriteAllText(dst, incoming);
                    }
                }
            }
        }
        catch
        {
            // Non-fatal
        }
    }

    private static void RunScan(string dataDir, SystemInfoProvider sysInfo, PluginIndexDocument index)
    {
        var mobo = sysInfo.GetMotherboardInfo();
        var bios = sysInfo.GetBiosInfo();
        var pawn = sysInfo.GetPawnIoStatus();
        var pci = sysInfo.GetDisplayAndSmbusDevices();
        var hidGroups = HidNative.EnumerateDeviceGroups();

        Console.WriteLine("=== LightRGB Hardware & Plugin Scan ===");
        Console.WriteLine($"Motherboard : {mobo.Manufacturer} {mobo.Model} (BIOS: {bios.Vendor} {bios.Version})");
        Console.WriteLine($"PawnIO Driver: {(pawn.Installed ? $"INSTALLED ({pawn.ImagePath})" : "Not installed")}");
        Console.WriteLine($"Indexed Plugins: {index.Entries.Count} (V8={index.V8IndexedCount}, fallback={index.RegexFallbackCount})");
        Console.WriteLine();
        Console.WriteLine("--- PCI Display / SMBus Devices ---");
        foreach (var d in pci)
            Console.WriteLine($"  [{d.Class}] {d.Name} | {d.InstanceId}");

        Console.WriteLine();
        Console.WriteLine("--- AMD ADL GPU I2C Probe (Read-Only) ---");
        var adlBuses = LightRGB.Hardware.AmdAdlI2cBus.OpenAllAdapters(out string adlStatus);
        Console.WriteLine($"  {adlStatus}");
        foreach (var gb in adlBuses)
        {
            if (LightRGB.Runtime.SapphireGpuWorker.TryDetect(gb, out string gpuName))
                Console.WriteLine($"  MATCH -> {gpuName} (VEN=0x{gb.VendorId:X4} DEV=0x{gb.DeviceId:X4} SUB=0x{gb.SubVendorId:X4}:0x{gb.SubDeviceId:X4})");
            gb.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine("--- NVIDIA NVAPI GPU I2C Probe ---");
        var nvBuses = LightRGB.Hardware.NvApiI2cBus.OpenAllAdapters(out string nvStatus);
        Console.WriteLine($"  {nvStatus}");
        if (nvBuses.Count > 0)
        {
            var nvDevs = LightRGB.Runtime.SmbusDeviceScanner.ScanGpuPlugins(
                index, nvBuses, Path.Combine(dataDir, "plugins"), Path.Combine(dataDir, "user_plugins"), sysInfo);
            foreach (var gd in nvDevs)
                Console.WriteLine($"  MATCH -> {gd.Plugin.Name} at I2C 0x{gd.Address:X2} ({gd.Bus.Name})");
            foreach (var nb in nvBuses) nb.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine("--- PawnIO SMBus Probe (RAM) ---");
        var smbusBuses = LightRGB.Hardware.PawnIoSmbusTransport.TryOpenSystemBuses(Path.Combine(AppContext.BaseDirectory, "pawnio"), out string smbusMsg);
        Console.WriteLine($"  {smbusMsg}");
        if (smbusBuses.Count > 0)
        {
            var ramDevs = LightRGB.Runtime.SmbusDeviceScanner.ScanRamPlugins(
                index, smbusBuses, Path.Combine(dataDir, "plugins"), Path.Combine(dataDir, "user_plugins"), sysInfo);
            foreach (var r in ramDevs)
                Console.WriteLine($"  MATCH -> {r.Plugin.Name} at SMBus 0x{r.Address:X2} ({r.Bus.Name})");
            foreach (var b in smbusBuses) b.Dispose();
        }

        Console.WriteLine();
        Console.WriteLine($"--- Connected USB HID Groups ({hidGroups.Count}) ---");
        foreach (var g in hidGroups)
        {
            var matches = PluginIndexer.FindMatches(index, g.VendorId, g.ProductId);
            string matchStr = matches.Count > 0
                ? $"MATCH -> {string.Join(", ", matches.Select(m => $"{m.Name} ({m.RelativePath})"))}"
                : (g.IsLampArray ? "MATCH -> Built-in HID LampArray (0x59)" : "No SignalRGB plugin match");

            Console.WriteLine($"  VID=0x{g.VendorId:X4} PID=0x{g.ProductId:X4} | {g.Product} ({g.Endpoints.Count} endpoints, accessible={g.AnyAccessible})");
            Console.WriteLine($"    {matchStr}");
            foreach (var ep in g.Endpoints.Take(4))
            {
                Console.WriteLine($"      ep: if={ep.Interface} usagePage=0x{ep.UsagePage:X4} usage=0x{ep.Usage:X4} col={ep.Collection} out={ep.OutputReportLength}");
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            LightRGB - Lightweight SignalRGB Plugin Runner

            Usage:
              LightRGB.exe [options]

            Options:
              --extract [installer]   Extract SignalRGB JS plugins & components and rebuild index
              --scan                  Scan connected HID, LampArray, and SMBus/PawnIO hardware
              --minimized | --silent  Run minimized in Windows System Tray (hide console)
              --no-tray               Run headless in console (Ctrl+C to exit)
              --apply-and-exit [ms]   Apply configured colors/effects for ms (default 2500) and exit
              --port <port>           Web UI localhost port (default 4747)
              --data-dir <path>       Custom data/config/plugins directory
            """);
    }
}
