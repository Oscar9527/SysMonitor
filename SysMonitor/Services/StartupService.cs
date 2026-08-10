using System.IO;
using Microsoft.Win32;

namespace SysMonitor.Services;

public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SysMonitor";

    public bool RefreshExistingRegistration()
    {
        string? processPath = GetLaunchPath();
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null || !key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{processPath}\"", RegistryValueKind.String);
            BandDiagnostics.Log($"startup registration refreshed path=\"{processPath}\"");
            return true;
        }
        catch (Exception exception)
        {
            BandDiagnostics.Log($"startup registration refresh failed type={exception.GetType().Name}");
            return false;
        }
    }

    public bool IsEnabled()
    {
        string? processPath = GetLaunchPath();
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            string? configuredValue = key?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                as string;
            if (string.IsNullOrWhiteSpace(configuredValue))
            {
                return false;
            }

            string configuredPath = RemoveOnePairOfQuotes(configuredValue);
            return string.Equals(
                NormalizePath(configuredPath),
                NormalizePath(processPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            string? processPath = GetLaunchPath();
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return;
            }

            key.SetValue(ValueName, $"\"{processPath}\"", RegistryValueKind.String);
        }
        catch
        {
            // HKCU normally needs no elevation. Failure is non-fatal for the monitor.
        }
    }

    private static string RemoveOnePairOfQuotes(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1]
            : trimmed;
    }

    private static string? GetLaunchPath()
    {
        string? launcherPath = Environment.GetEnvironmentVariable("SYSMONITOR_LAUNCHER_PATH");
        if (!string.IsNullOrWhiteSpace(launcherPath) && File.Exists(launcherPath))
        {
            return launcherPath;
        }

        return Environment.ProcessPath;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return path.Trim();
        }
    }
}
