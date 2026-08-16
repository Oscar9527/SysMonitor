using System.Management;

namespace SysMonitor.Services;

/// <summary>
/// Reads the DIMM configured clock speed from Windows CIM. The result is cached:
/// physical-memory configuration cannot change during ordinary use, and querying WMI
/// on every HUD update would add needless overhead.
/// </summary>
internal sealed class MemoryFrequencyReader
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private DateTimeOffset _nextRefresh;
    private double? _cachedMhz;
    private bool _hasCachedResult;

    internal double? ReadConfiguredMhz()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_hasCachedResult && now < _nextRefresh)
        {
            return _cachedMhz;
        }

        _cachedMhz = ReadConfiguredMhzFromCim();
        _hasCachedResult = true;
        _nextRefresh = now + RefreshInterval;
        return _cachedMhz;
    }

    internal static double? SelectConfiguredMhz(IEnumerable<uint?> configuredClockSpeeds, IEnumerable<uint?> fallbackSpeeds)
    {
        double? configured = AverageValidMhz(configuredClockSpeeds);
        return configured ?? AverageValidMhz(fallbackSpeeds);
    }

    private static double? ReadConfiguredMhzFromCim()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var configured = new List<uint?>();
            var fallback = new List<uint?>();
            using var searcher = new ManagementObjectSearcher(
                "root\\CIMV2",
                "SELECT ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory");
            using ManagementObjectCollection modules = searcher.Get();
            foreach (ManagementObject module in modules)
            {
                configured.Add(ToMhz(module["ConfiguredClockSpeed"]));
                fallback.Add(ToMhz(module["Speed"]));
            }

            return SelectConfiguredMhz(configured, fallback);
        }
        catch
        {
            return null;
        }
    }

    private static double? AverageValidMhz(IEnumerable<uint?> values)
    {
        ulong total = 0;
        int count = 0;
        foreach (uint? value in values)
        {
            if (value is not uint mhz || mhz is 0 or > 10_000_000)
            {
                continue;
            }

            total += mhz;
            count++;
        }

        return count == 0 ? null : total / (double)count;
    }

    private static uint? ToMhz(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }
}
