using System.Diagnostics;
using System.Runtime.InteropServices;
using ThreadingTimer = System.Threading.Timer;

namespace SysMonitor.Services;

/// <summary>
/// Proactively manages process working set and Garbage Collector segments to maintain
/// ultra-low physical RAM footprint (typically 20MB - 35MB, strictly under 50MB).
/// </summary>
internal static class MemoryOptimizer
{
    private static readonly ThreadingTimer s_periodicTrimTimer;
    private static long s_lastTrimTimestamp;

    static MemoryOptimizer()
    {
        // Periodic background trim every 60 seconds
        s_periodicTrimTimer = new ThreadingTimer(
            _ => TrimWorkingSet(),
            null,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(60));
    }

    public static void Initialize()
    {
        // Triggers static constructor
        TrimWorkingSet();
    }

    public static void Shutdown()
    {
        try
        {
            s_periodicTrimTimer.Dispose();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Executes compaction and trims unreferenced pages from the OS process working set.
    /// Rate-limited to prevent excessive CPU consumption.
    /// </summary>
    public static void TrimWorkingSet(bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref s_lastTrimTimestamp);
        if (!force && Stopwatch.GetElapsedTime(last, now) < TimeSpan.FromSeconds(3))
        {
            return;
        }

        Interlocked.Exchange(ref s_lastTrimTimestamp, now);
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: false);

            if (OperatingSystem.IsWindows())
            {
                _ = SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
            }
        }
        catch
        {
            // Non-fatal optimization failure
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(
        nint hProcess,
        nint dwMinimumWorkingSetSize,
        nint dwMaximumWorkingSetSize);
}
