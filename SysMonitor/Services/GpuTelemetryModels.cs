using System.Diagnostics;

namespace SysMonitor.Services;

internal enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Unknown,
}

internal enum GpuTelemetrySource
{
    NvidiaSmi,
    LibreHardwareMonitor,
}

internal sealed record GpuProviderSample(
    string StableId,
    int Index,
    string Name,
    GpuVendor Vendor,
    GpuTelemetrySource Source,
    string? HardwareId,
    string? PnpDeviceId,
    string? PciBusId,
    string? Uuid,
    double? UsagePercent,
    double? TemperatureCelsius,
    long? DedicatedMemoryUsedBytes,
    long? DedicatedMemoryTotalBytes,
    DateTimeOffset SampledAt,
    long MonotonicTimestamp);

internal sealed class GpuProviderCycle
{
    internal GpuProviderCycle(
        GpuTelemetrySource source,
        DateTimeOffset sampledAt,
        long monotonicTimestamp,
        IEnumerable<GpuProviderSample> samples)
    {
        Source = source;
        SampledAt = sampledAt;
        MonotonicTimestamp = monotonicTimestamp;
        Samples = samples.ToArray();
    }

    internal GpuTelemetrySource Source { get; }
    internal DateTimeOffset SampledAt { get; }
    internal long MonotonicTimestamp { get; }
    internal IReadOnlyList<GpuProviderSample> Samples { get; }
}

internal interface IGpuTelemetryProvider : IAsyncDisposable
{
    GpuProviderCycle? LatestCycle { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

internal static class GpuMonotonicClock
{
    internal static long GetTimestamp() => Stopwatch.GetTimestamp();

    internal static TimeSpan Elapsed(long start, long end) =>
        TimeSpan.FromSeconds((end - start) / (double)Stopwatch.Frequency);
}
