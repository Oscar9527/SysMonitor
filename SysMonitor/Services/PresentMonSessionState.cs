using System.Globalization;
using System.IO;

namespace SysMonitor.Services;

/// <summary>
/// Retains only the product-owned ETW session name so a later process can
/// clean up after an abnormal termination without touching another tool's
/// PresentMon session.
/// </summary>
internal static class PresentMonSessionState
{
    private const int MaximumNameLength = 96;

    internal static string? ReadOwnedSession()
    {
        try
        {
            string path = GetPath();
            if (!File.Exists(path))
            {
                return null;
            }

            string value = File.ReadAllText(path).Trim();
            return IsOwnedName(value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    internal static void Register(string sessionName)
    {
        if (!IsOwnedName(sessionName))
        {
            throw new ArgumentException("Invalid SysMonitor PresentMon session name.", nameof(sessionName));
        }

        string path = GetPath();
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, sessionName);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
            }
        }
    }

    internal static void Clear(string sessionName)
    {
        try
        {
            string path = GetPath();
            if (File.Exists(path) &&
                string.Equals(File.ReadAllText(path).Trim(), sessionName, StringComparison.Ordinal))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    internal static bool IsOwnedName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumNameLength)
        {
            return false;
        }

        string[] parts = value.Split('-', StringSplitOptions.None);
        return parts.Length == 3 &&
            string.Equals(parts[0], "SysMonitor", StringComparison.Ordinal) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) &&
            pid > 0 &&
            Guid.TryParseExact(parts[2], "N", out _);
    }

    private static string GetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysMonitor",
        "runtime",
        "presentmon-session.txt");
}
