using System.Diagnostics;
using SysMonitor.Services;
using Xunit.Abstractions;

namespace SysMonitor.Tests;

public sealed class LiveSharedMemoryProbeTests
{
    private readonly ITestOutputHelper _output;

    public LiveSharedMemoryProbeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ReadInstalledProducersWithoutChangingThem()
    {
        var rtss = new RtssSharedMemoryReader();
        (int pid, double fps, string name)? active = null;
        Process[] processes = Process.GetProcesses();
        foreach (Process process in processes
                     .OrderByDescending(candidate =>
                         SafeName(candidate).Contains("DeltaForce", StringComparison.OrdinalIgnoreCase)))
        {
            using (process)
            {
                SharedMemoryValue result = rtss.Read(process.Id);
                if (result.Value is double fps)
                {
                    active = (process.Id, fps, SafeName(process));
                    break;
                }
            }
        }

        _output.WriteLine(active is { } frame
            ? $"RTSS pid={frame.pid} process={frame.name} fps={frame.fps:F1}"
            : "RTSS active target not found");
    }

    private static string SafeName(Process process)
    {
        try { return process.ProcessName; }
        catch { return "unknown"; }
    }
}
