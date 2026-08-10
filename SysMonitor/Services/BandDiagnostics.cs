using System.Globalization;
using System.IO;
using System.Reflection;

namespace SysMonitor.Services;

public static class BandDiagnostics
{
    private const long MaximumFileBytes = 256 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTimeOffset> LastRateLimitedLog = new();
    private static readonly string SessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];

    public static void LogProcessSession()
    {
        string path = Environment.ProcessPath ?? "<unknown>";
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "<unknown>";
        Log($"session-start id={SessionId} pid={Environment.ProcessId} version={version} path=\"{Path.GetFullPath(path)}\"");
    }

    public static void LogRateLimited(string key, string message, TimeSpan minimumInterval)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        lock (Gate)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            if (LastRateLimitedLog.TryGetValue(key, out DateTimeOffset last) &&
                now - last < minimumInterval)
            {
                return;
            }

            LastRateLimitedLog[key] = now;
        }

        Log(message);
    }

    public static void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SysMonitor");
                Directory.CreateDirectory(directory);

                string path = Path.Combine(directory, "band-debug.log");
                string backupPath = path + ".1";
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumFileBytes)
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }

                    File.Move(path, backupPath);
                }

                string timestamp = DateTimeOffset.Now.ToString(
                    "O",
                    CultureInfo.InvariantCulture);
                string line = $"{timestamp} session={SessionId} {message}{Environment.NewLine}";
                File.AppendAllText(path, line);
            }
        }
        catch
        {
        }
    }
}
