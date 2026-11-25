using System.Collections;

namespace LightRGB.Tests;

internal static class TestHelpers
{
    public static string? TryFindSignalRgbInstaller()
    {
        string? envPath = Environment.GetEnvironmentVariable("SIGNALRGB_INSTALLER");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
            return Path.GetFullPath(envPath);

        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            var dir = new DirectoryInfo(start);
            for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Install_SignalRgb.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
