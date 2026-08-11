namespace SysMonitor.Services;

internal enum GpuSensorKind
{
    Load,
    Temperature,
    SmallData,
    Clock,
}

internal readonly record struct GpuSensorReading(
    string Name,
    GpuSensorKind Kind,
    double? Value);

internal readonly record struct GpuSensorSelection(
    double? UsagePercent,
    double? TemperatureCelsius,
    long? DedicatedMemoryUsedBytes,
    long? DedicatedMemoryTotalBytes)
{
    internal double? CoreClockMhz { get; init; }
    internal double? MemoryClockMhz { get; init; }
}

internal static class GpuSensorSelector
{
    private const double BytesPerMiB = 1024d * 1024d;

    internal static GpuSensorSelection Select(
        GpuVendor vendor,
        IEnumerable<GpuSensorReading> readings)
    {
        double? usage = null;
        double? temperature = null;
        double? memoryUsedMiB = null;
        double? memoryTotalMiB = null;
        double? memoryFreeMiB = null;
        double? d3dDedicatedUsedMiB = null;
        double? coreClockMhz = null;
        double? memoryClockMhz = null;

        foreach (GpuSensorReading reading in readings)
        {
            string name = Normalize(reading.Name);
            double? value = Finite(reading.Value);
            if (value is null)
            {
                continue;
            }

            switch (reading.Kind)
            {
                case GpuSensorKind.Load when vendor is GpuVendor.Nvidia or GpuVendor.Amd:
                    if (name == "GPU CORE")
                    {
                        usage = Math.Clamp(value.Value, 0d, 100d);
                    }

                    break;

                case GpuSensorKind.Load when vendor == GpuVendor.Intel:
                    if (name.Contains("D3D", StringComparison.Ordinal))
                    {
                        double candidate = Math.Clamp(value.Value, 0d, 100d);
                        usage = usage is null ? candidate : Math.Max(usage.Value, candidate);
                    }

                    break;

                case GpuSensorKind.Temperature:
                    if (name == "GPU CORE" && value is >= 1d and <= 150d)
                    {
                        temperature = value;
                    }

                    break;

                case GpuSensorKind.SmallData:
                    if (value < 0d)
                    {
                        break;
                    }

                    if (name == "GPU MEMORY USED")
                    {
                        memoryUsedMiB = value;
                    }
                    else if (name == "GPU MEMORY TOTAL")
                    {
                        memoryTotalMiB = value;
                    }
                    else if (name == "GPU MEMORY FREE")
                    {
                        memoryFreeMiB = value;
                    }
                    else if (name == "D3D DEDICATED MEMORY USED")
                    {
                        d3dDedicatedUsedMiB = value;
                    }

                    break;

                case GpuSensorKind.Clock:
                    if (value <= 0d)
                    {
                        break;
                    }

                    if (name == "GPU CORE")
                    {
                        coreClockMhz = value;
                    }
                    else if (name == "GPU MEMORY")
                    {
                        memoryClockMhz = value;
                    }

                    break;
            }
        }

        if (memoryUsedMiB is null &&
            memoryTotalMiB is { } total &&
            memoryFreeMiB is { } free &&
            total >= free)
        {
            memoryUsedMiB = total - free;
        }

        memoryUsedMiB ??= d3dDedicatedUsedMiB;
        long? totalBytes = memoryTotalMiB is > 0d
            ? MiBToBytes(memoryTotalMiB)
            : null;
        return new GpuSensorSelection(
            usage,
            temperature,
            MiBToBytes(memoryUsedMiB),
            totalBytes)
        {
            CoreClockMhz = coreClockMhz,
            MemoryClockMhz = memoryClockMhz,
        };
    }

    internal static long? MiBToBytes(double? value)
    {
        if (value is null || !double.IsFinite(value.Value) || value.Value < 0d)
        {
            return null;
        }

        double bytes = value.Value * BytesPerMiB;
        if (!double.IsFinite(bytes))
        {
            return null;
        }

        return bytes >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Round(bytes, MidpointRounding.AwayFromZero);
    }

    private static double? Finite(double? value) =>
        value is { } actual && double.IsFinite(actual) ? actual : null;

    private static string Normalize(string value) =>
        string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
}
