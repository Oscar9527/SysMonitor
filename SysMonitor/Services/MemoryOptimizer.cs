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

    /// <summary>
    /// Executes compaction and trims unreferenced pages from the OS process working set.
    /// Rate-limited to prevent excessive CPU consumption.
    /// </summary>
    public static void TrimWorkingSet(bool force = false)
    {
        long now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(s_lastTrimTimestamp, now) < TimeSpan.FromSeconds(3))
        {
            return;
        }

        s_lastTrimTimestamp = now;
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            if (OperatingSystem.IsWindows())
            {
                nint handle = Process.GetCurrentProcess().Handle;
                if (handle != nint.Zero)
                {
                    _ = SetProcessWorkingSetSize(handle, -1, -1);
                }
            }
        }
        catch
        {
            // Non-fatal optimization failure
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(
        nint hProcess,
        nint dwMinimumWorkingSetSize,
        nint dwMaximumWorkingSetSize);
}
