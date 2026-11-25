using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace LightRGB.Tray;

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LightRGB";
    private const string TaskName = "LightRGB";

    private static readonly object CacheLock = new();
    private static bool _cacheReady;
    private static bool _cachedEnabled;
    private static string _cachedMode = "Off";

    public static string BuildStartupArguments(string? dataDir, bool applyAndExit)
    {
        string baseArgs = applyAndExit ? "--minimized --apply-and-exit 3500" : "--minimized";
        if (!string.IsNullOrWhiteSpace(dataDir))
            return $"{baseArgs} --data-dir \"{dataDir}\"";
        return baseArgs;
    }

    public static string BuildScheduledTaskXml(string exePath, string arguments, string userId)
    {
        string escapedUser = SecurityElement.Escape(userId) ?? "";
        string escapedExe = SecurityElement.Escape(exePath) ?? "";
        string escapedArgs = SecurityElement.Escape(arguments) ?? "";
        string escapedDir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "") ?? "";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{escapedUser}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{escapedUser}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{escapedExe}</Command>
                  <Arguments>{escapedArgs}</Arguments>
                  <WorkingDirectory>{escapedDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsEnabled()
    {
        EnsureCache();
        lock (CacheLock) { return _cachedEnabled; }
    }

    public static string GetStatusMode()
    {
        EnsureCache();
        lock (CacheLock) { return _cachedMode; }
    }

    public static bool SetEnabled(bool enabled, string? dataDir = null, bool applyAndExit = false)
    {
        try
        {
            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;

                string args = BuildStartupArguments(dataDir, applyAndExit);

                if (IsAdministrator())
                {
                    string userId = WindowsIdentity.GetCurrent().Name;
                    string xml = BuildScheduledTaskXml(exe, args, userId);
                    string tmpXml = Path.Combine(Path.GetTempPath(), $"lightrgb_task_{Guid.NewGuid():N}.xml");
                    try
                    {
                        File.WriteAllText(tmpXml, xml, Encoding.Unicode);
                        int code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmpXml}\" /F");
                        if (code == 0)
                        {
                            DeleteRunKey();
                            SetCache(true, "Admin Task");
                            return true;
                        }
                    }
                    finally
                    {
                        try { if (File.Exists(tmpXml)) File.Delete(tmpXml); } catch { }
                    }
                }

                WriteRunKey($"\"{exe}\" {args}");
                SetCache(true, "User Registry");
                return true;
            }
            else
            {
                DeleteRunKey();
                RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
                SetCache(false, "Off");
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureCache()
    {
        lock (CacheLock)
        {
            if (_cacheReady) return;
        }

        bool hasTask = QueryScheduledTaskExists();
        bool hasRun = HasRunKey();
        lock (CacheLock)
        {
            if (!_cacheReady)
            {
                if (hasTask)
                {
                    _cachedEnabled = true;
                    _cachedMode = "Admin Task";
                }
                else if (hasRun)
                {
                    _cachedEnabled = true;
                    _cachedMode = "User Registry";
                }
                else
                {
                    _cachedEnabled = false;
                    _cachedMode = "Off";
                }
                _cacheReady = true;
            }
        }
    }

    private static void SetCache(bool enabled, string mode)
    {
        lock (CacheLock)
        {
            _cachedEnabled = enabled;
            _cachedMode = mode;
            _cacheReady = true;
        }
    }

    private static bool QueryScheduledTaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    private static bool HasRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteRunKey(string commandLine)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key?.SetValue(ValueName, commandLine);
    }

    private static void DeleteRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }

    private static int RunSchtasks(string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc is null) return -1;
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { }
                return -1;
            }
            return proc.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
