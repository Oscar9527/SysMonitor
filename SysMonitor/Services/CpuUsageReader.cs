using System.Runtime.InteropServices;

namespace SysMonitor.Services;

internal sealed class CpuUsageReader : IDisposable
{
    private const uint ErrorSuccess = 0;
    private const uint PdhCstatusValidData = 0;
    private const uint PdhCstatusNewData = 1;
    private const uint PdhFormatDouble = 0x00000200;
    private const string UtilityCounter =
        @"\Processor Information(_Total)\% Processor Utility";
    private const string ProcessorTimeCounter =
        @"\Processor Information(_Total)\% Processor Time";

    private nint _query;
    private nint _counter;

    public void Start()
    {
        Stop();
        if (TryStart(UtilityCounter))
        {
            BandDiagnostics.Log("CPU source=PDH Processor Utility (Task Manager semantics)");
            return;
        }

        if (TryStart(ProcessorTimeCounter))
        {
            BandDiagnostics.Log("CPU source=PDH Processor Time fallback");
            return;
        }

        BandDiagnostics.Log("CPU source=GetSystemTimes fallback");
    }

    public double? Read()
    {
        if (_query == nint.Zero || _counter == nint.Zero)
        {
            return null;
        }

        try
        {
            if (PdhCollectQueryData(_query) != ErrorSuccess ||
                PdhGetFormattedCounterValue(
                    _counter,
                    PdhFormatDouble,
                    out _,
                    out PdhFormattedCounterValue value) != ErrorSuccess ||
                value.Status is not (PdhCstatusValidData or PdhCstatusNewData) ||
                !double.IsFinite(value.DoubleValue))
            {
                return null;
            }

            return Math.Clamp(value.DoubleValue, 0d, 100d);
        }
        catch
        {
            return null;
        }
    }

    public void Stop()
    {
        nint query = _query;
        _query = nint.Zero;
        _counter = nint.Zero;
        if (query != nint.Zero)
        {
            _ = PdhCloseQuery(query);
        }
    }

    public void Dispose() => Stop();

    private bool TryStart(string counterPath)
    {
        nint query = nint.Zero;
        try
        {
            if (PdhOpenQueryW(null, nint.Zero, out query) != ErrorSuccess ||
                query == nint.Zero ||
                PdhAddEnglishCounterW(
                    query,
                    counterPath,
                    nint.Zero,
                    out nint counter) != ErrorSuccess ||
                counter == nint.Zero ||
                PdhCollectQueryData(query) != ErrorSuccess)
            {
                if (query != nint.Zero)
                {
                    _ = PdhCloseQuery(query);
                }

                return false;
            }

            _query = query;
            _counter = counter;
            return true;
        }
        catch
        {
            if (query != nint.Zero)
            {
                _ = PdhCloseQuery(query);
            }

            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        internal uint Status;
        internal double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(
        string? dataSource,
        nint userData,
        out nint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(
        nint query,
        string fullCounterPath,
        nint userData,
        out nint counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(nint query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(
        nint counter,
        uint format,
        out uint counterType,
        out PdhFormattedCounterValue value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(nint query);
}
