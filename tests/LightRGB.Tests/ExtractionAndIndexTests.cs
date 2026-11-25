using LightRGB.Extraction;
using LightRGB.Hardware;
using Xunit;

namespace LightRGB.Tests;

public sealed class ExtractionAndIndexTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lightrgb_test_{Guid.NewGuid():N}");

    [Fact]
    public void ExtractorAndIndexer_ExtractAllPluginsAndIndexKnownDevices()
    {
        string? installer = TestHelpers.TryFindSignalRgbInstaller();
        if (installer is null) return;

        var extractor = new SignalRgbExtractor();
        var res = extractor.Extract(installer!, _tempDir);

        Assert.True(res.PluginsExtracted >= 470, $"Expected >= 470 plugins, got {res.PluginsExtracted}");
        Assert.True(res.ComponentsExtracted >= 200, $"Expected >= 200 components, got {res.ComponentsExtracted}");
        Assert.True(Directory.Exists(res.UserPluginsDirectory));

        var indexer = new PluginIndexer();
        var index = indexer.LoadOrBuild(_tempDir, new SystemInfoProvider(), forceRebuild: true);

        Assert.True(index.Entries.Count >= 400, $"Expected >= 400 runnable plugins, got {index.Entries.Count}");
        Assert.True(index.V8IndexedCount >= 380, $"Expected V8 indexing for most plugins, got {index.V8IndexedCount} (fallback={index.RegexFallbackCount})");
        Assert.True(File.Exists(Path.Combine(_tempDir, "plugin-index.json")));

        // Verify ASRock Motherboard Controller (0x26CE:0x01A2)
        var asrock = PluginIndexer.FindMatches(index, 0x26CE, 0x01A2);
        Assert.Contains(asrock, p => p.RelativePath.Equals("AsRock/Asrock_Motherboard_Controller.js", StringComparison.OrdinalIgnoreCase));

        // Verify Logitech Modern Device (0x046D:0xC539) - ProductId() returns an imported class array!
        var logi = PluginIndexer.FindMatches(index, 0x046D, 0xC539);
        Assert.Contains(logi, p => p.RelativePath.Equals("Logitech/Logitech_Modern_Device.js", StringComparison.OrdinalIgnoreCase));

        // Verify cached index loads quickly without rebuilding
        var cached = indexer.LoadOrBuild(_tempDir, new SystemInfoProvider(), forceRebuild: false);
        Assert.Equal(index.Entries.Count, cached.Entries.Count);
    }

    [Fact]
    public void Program_ResolveDefaultDataDir_RespectsOverrideAndPortableMarker()
    {
        string explicitDir = Path.Combine(_tempDir, "custom-data");
        Assert.Equal(explicitDir, Program.ResolveDefaultDataDir(explicitDir, _tempDir));

        string portableAppDir = Path.Combine(_tempDir, "portable-app");
        Directory.CreateDirectory(portableAppDir);
        File.WriteAllText(Path.Combine(portableAppDir, "portable.txt"), "");
        Assert.Equal(Path.Combine(portableAppDir, "data"), Program.ResolveDefaultDataDir(null, portableAppDir));

        string nonPortableDir = Path.Combine(_tempDir, "standard-app");
        Directory.CreateDirectory(nonPortableDir);
        string expectedLocalApp = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LightRGB");
        Assert.Equal(expectedLocalApp, Program.ResolveDefaultDataDir(null, nonPortableDir));

        // Verify missing installer path resolves cleanly to null without hardcoded fallback
        string emptyDir = Path.Combine(_tempDir, "empty-dirs", "sub");
        Directory.CreateDirectory(emptyDir);
        Assert.Null(SignalRgbExtractor.FindInstallerPath(
            @"Z:\definitely_missing_installer.exe",
            emptyDir,
            emptyDir,
            emptyDir));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
