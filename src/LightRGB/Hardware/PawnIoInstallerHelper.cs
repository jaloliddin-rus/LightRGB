using System.Diagnostics;

namespace LightRGB.Hardware;

public static class PawnIoInstallerHelper
{
    public const string DownloadUrl = "https://pawnio.eu";

    public static string? FindBundledInstaller(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return null;

        string candidate = Path.Combine(baseDirectory, "pawnio", "PawnIO_setup.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public static (bool Success, bool RebootRequired, string Message) TryRunSilentInstall(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
            return (false, false, "Bundled PawnIO_setup.exe not found.");

        try
        {
            using var proc = Process.Start(new ProcessStartInfo(installerPath, "-install -silent")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null)
                return (false, false, "Could not launch PawnIO_setup.exe.");

            if (!proc.WaitForExit(60000))
            {
                try { proc.Kill(); } catch { }
                return (false, false, "PawnIO installer timed out.");
            }

            return proc.ExitCode switch
            {
                0 => (true, false, "PawnIO installed successfully."),
                3010 => (true, true, "PawnIO installed. Please restart Windows to finish loading the driver."),
                _ => (false, false, $"PawnIO installer exited with code {proc.ExitCode}.")
            };
        }
        catch (Exception ex)
        {
            return (false, false, $"Failed to run PawnIO installer: {ex.Message}");
        }
    }
}
