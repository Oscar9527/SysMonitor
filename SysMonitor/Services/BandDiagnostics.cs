using System.Globalization;
using System.IO;
using System.Reflection;

namespace SysMonitor.Services;

public static class BandDiagnostics
{
    private const long MaximumFileBytes = 256 * 1024;
    private static readonly TimeSpan RateLimitedKeyTtl = TimeSpan.FromHours(24);
    private const int MaximumRateLimitedKeys = 512;
    private const int PruneIntervalUniqueKeys = 64;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTimeOffset> LastRateLimitedLog = new();
    private static readonly string SessionId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
    private static int _uniqueKeysSincePrune;

    // These hooks are intentionally internal so tests can inspect the bounded
    // bookkeeping without expanding the application's public API.
    internal static int RateLimitedKeyCount
    {
        get
        {
            lock (Gate)
            {
                return LastRateLimitedLog.Count;
            }
        }
    }

    internal static bool IsRateLimitedKeyTrackedForTests(string key)
    {
        lock (Gate)
        {
            return LastRateLimitedLog.ContainsKey(key);
        }
    }

    internal static bool IsRateLimitedKeyExpiredForTests(
        DateTimeOffset now,
        DateTimeOffset last) =>
        now - last >= RateLimitedKeyTtl;

    internal static string SelectOldestRateLimitedKeyForTests(
        IEnumerable<KeyValuePair<string, DateTimeOffset>> entries) =>
        SelectOldestRateLimitedKey(entries);

    internal static bool TrackRateLimitedKeyForTests(
        string key,
        TimeSpan minimumInterval,
        DateTimeOffset now)
    {
        lock (Gate)
        {
            return TrackRateLimitedKeyLocked(key, minimumInterval, now);
        }
    }

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

        bool shouldLog;
        lock (Gate)
        {
            shouldLog = TrackRateLimitedKeyLocked(key, minimumInterval, DateTimeOffset.Now);
        }

        if (shouldLog)
        {
            Log(message);
        }
    }

    private static bool TrackRateLimitedKeyLocked(
        string key,
        TimeSpan minimumInterval,
        DateTimeOffset now)
    {
        if (LastRateLimitedLog.TryGetValue(key, out DateTimeOffset last))
        {
            if (now - last < RateLimitedKeyTtl)
            {
                if (now - last < minimumInterval)
                {
                    return false;
                }

                LastRateLimitedLog[key] = now;
                return true;
            }

            // Expired keys are treated as new keys, so they cannot occupy a
            // slot indefinitely or suppress a log forever.
            LastRateLimitedLog.Remove(key);
        }

        InsertNewRateLimitedKey(key, now);
        return true;
    }

    private static void InsertNewRateLimitedKey(string key, DateTimeOffset now)
    {
        _uniqueKeysSincePrune++;
        if (_uniqueKeysSincePrune >= PruneIntervalUniqueKeys)
        {
            PruneExpired(now);
            _uniqueKeysSincePrune = 0;
        }

        if (LastRateLimitedLog.Count >= MaximumRateLimitedKeys)
        {
            // Prune before evicting a live key whenever the hard cap is hit.
            PruneExpired(now);
        }

        while (LastRateLimitedLog.Count >= MaximumRateLimitedKeys)
        {
            EvictOldest();
        }

        LastRateLimitedLog[key] = now;
    }

    private static void PruneExpired(DateTimeOffset now)
    {
        foreach (string key in LastRateLimitedLog
                     .Where(pair => now - pair.Value >= RateLimitedKeyTtl)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            LastRateLimitedLog.Remove(key);
        }
    }

    private static void EvictOldest()
    {
        if (LastRateLimitedLog.Count == 0)
        {
            return;
        }

        string oldestKey = SelectOldestRateLimitedKey(LastRateLimitedLog);
        LastRateLimitedLog.Remove(oldestKey);
    }

    private static string SelectOldestRateLimitedKey(
        IEnumerable<KeyValuePair<string, DateTimeOffset>> entries) =>
        entries
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .First()
            .Key;

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
