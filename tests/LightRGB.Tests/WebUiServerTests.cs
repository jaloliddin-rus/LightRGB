using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using LightRGB.Core;
using LightRGB.Effects;
using LightRGB.Extraction;
using LightRGB.Hardware;
using LightRGB.Tray;
using LightRGB.Web;
using Xunit;

namespace LightRGB.Tests;

public sealed class WebUiServerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lightrgb_web_{Guid.NewGuid():N}");

    [Fact]
    public async Task WebUiServer_ServesHtmlApiScanAndFastResponses()
    {
        Directory.CreateDirectory(_tempDir);
        using var config = new ConfigStore(_tempDir);
        var canvas = new CanvasEngine();
        var sysInfo = new SystemInfoProvider();
        var index = new PluginIndexDocument();

        using var manager = new DeviceManager(_tempDir, config, canvas, sysInfo, index);
        string wwwroot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "wwwroot"));
        using var server = new WebUiServer(manager, 0, wwwroot);
        server.Start();

        using var http = new HttpClient { BaseAddress = new Uri(server.BaseUrl) };

        var htmlResp = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, htmlResp.StatusCode);
        string html = await htmlResp.Content.ReadAsStringAsync();
        Assert.Contains("LightRGB", html);
        Assert.Contains("scanBanner", html);
        Assert.Contains("syncAllBright", html);
        Assert.Contains("startupApplyExit", html);

        var statusJson = await http.GetStringAsync("/api/status");
        using (var doc = JsonDocument.Parse(statusJson))
        {
            Assert.True(doc.RootElement.TryGetProperty("ramMb", out _));
            Assert.True(doc.RootElement.TryGetProperty("availableEffects", out _));
            Assert.True(doc.RootElement.TryGetProperty("allBrightnessSynced", out var sabEl));
            Assert.True(sabEl.GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("startupMode", out _));
            Assert.True(doc.RootElement.TryGetProperty("startupApplyAndExit", out var saeEl));
            Assert.False(saeEl.GetBoolean());
            Assert.True(doc.RootElement.TryGetProperty("scan", out var scanEl));
            Assert.True(scanEl.TryGetProperty("percent", out _));
            Assert.True(scanEl.TryGetProperty("stage", out _));
        }

        // Verify 10 consecutive /api/status calls finish quickly (no Nagle / PCI polling stall)
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
            await http.GetStringAsync("/api/status");
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"10 /api/status calls took {sw.ElapsedMilliseconds}ms");

        var canvasJson = await http.GetStringAsync("/api/canvas");
        using (var doc = JsonDocument.Parse(canvasJson))
        {
            Assert.Equal(16, doc.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(10, doc.RootElement.GetProperty("height").GetInt32());
        }

        var postContent = new StringContent(
            JsonSerializer.Serialize(new EffectSettings { EffectName = "Neon Shift", Brightness = 75, Speed = 2.0f }),
            Encoding.UTF8,
            "application/json");
        var postResp = await http.PostAsync("/api/effect", postContent);
        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);
        Assert.Equal("Neon Shift", manager.Canvas.GetSettingsSnapshot().EffectName);
        Assert.Equal(75, manager.Canvas.GetSettingsSnapshot().Brightness);

        config.Flush();
        Assert.True(File.Exists(config.ConfigPath));
        Assert.Contains("Neon Shift", File.ReadAllText(config.ConfigPath));

        // Verify per-device brightness + syncAllBrightness API persistence
        var devPost = new StringContent("{\"brightness\":65,\"syncBrightness\":false}", Encoding.UTF8, "application/json");
        var devResp = await http.PostAsync("/api/devices/TEST:0001", devPost);
        Assert.Equal(HttpStatusCode.OK, devResp.StatusCode);

        var devSnap = config.GetSnapshot().Devices["TEST:0001"];
        Assert.Equal(65, devSnap.Brightness);
        Assert.False(devSnap.SyncBrightness);

        // syncAllBrightness = true flips device back to synced
        var syncTrueResp = await http.PostAsync("/api/settings", new StringContent("{\"syncAllBrightness\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, syncTrueResp.StatusCode);
        Assert.True(config.GetSnapshot().Devices["TEST:0001"].SyncBrightness);

        // syncAllBrightness = false seeds device brightness from current global Brightness (75)
        var syncFalseResp = await http.PostAsync("/api/settings", new StringContent("{\"syncAllBrightness\":false}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, syncFalseResp.StatusCode);
        devSnap = config.GetSnapshot().Devices["TEST:0001"];
        Assert.False(devSnap.SyncBrightness);
        Assert.Equal(75, devSnap.Brightness);

        // Verify startupApplyAndExit persistence via /api/settings
        var saeResp = await http.PostAsync("/api/settings", new StringContent("{\"startupApplyAndExit\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, saeResp.StatusCode);
        Assert.True(config.GetSnapshot().StartupApplyAndExit);
    }

    [Fact]
    public void StartupManager_BuildsArgsAndElevatedTaskXml()
    {
        string trayArgs = StartupManager.BuildStartupArguments(@"C:\Tools\LightRGB\data", applyAndExit: false);
        Assert.Equal("--minimized --data-dir \"C:\\Tools\\LightRGB\\data\"", trayArgs);
        Assert.DoesNotContain("--apply-and-exit", trayArgs);

        string exitArgs = StartupManager.BuildStartupArguments(@"C:\Tools\LightRGB\data", applyAndExit: true);
        Assert.Equal("--minimized --apply-and-exit 3500 --data-dir \"C:\\Tools\\LightRGB\\data\"", exitArgs);

        string xml = StartupManager.BuildScheduledTaskXml(
            @"C:\Tools\LightRGB\LightRGB.exe",
            exitArgs,
            @"PC\User&Test");

        var doc = XDocument.Parse(xml);
        Assert.Contains("HighestAvailable", doc.ToString());
        Assert.Contains("PT0S", doc.ToString());
        Assert.Contains("PT5S", doc.ToString());
        Assert.Equal(
            @"C:\Tools\LightRGB\LightRGB.exe",
            doc.Descendants().First(e => e.Name.LocalName == "Command").Value);
        Assert.Equal(
            exitArgs,
            doc.Descendants().First(e => e.Name.LocalName == "Arguments").Value);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
