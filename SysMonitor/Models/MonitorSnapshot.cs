namespace SysMonitor.Models;

public sealed record GpuSnapshot(
    int Index,
    string Name,
    double UsagePercent,
    double TemperatureCelsius,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    DateTimeOffset SampledAt);

public sealed record MonitorSnapshot(
    long Sequence,
    DateTimeOffset SampledAt,
    double CpuUsagePercent,
    double? CpuTemperatureCelsius,
    int LogicalProcessorCount,
    double MemoryUsagePercent,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    GpuSnapshot? Gpu,
    double DownloadBytesPerSecond,
    double UploadBytesPerSecond,
    string SystemDriveName,
    double SystemDriveUsagePercent)
{
    public static MonitorSnapshot Empty { get; } = new(
        0,
        DateTimeOffset.Now,
        0,
        null,
        Environment.ProcessorCount,
        0,
        0,
        0,
        null,
        0,
        0,
        "C:",
        0);
}
