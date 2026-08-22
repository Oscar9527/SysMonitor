using System.IO;

namespace SysMonitor.Services;

internal static class EnvironmentBootstrap
{
    internal static void EnsureWindowsDirectoryEnvironment()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINDIR")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        string windowsDirectory = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\Windows\\";
        windowsDirectory = windowsDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        Environment.SetEnvironmentVariable("WINDIR", windowsDirectory);
        Environment.SetEnvironmentVariable("windir", windowsDirectory);
    }
}
