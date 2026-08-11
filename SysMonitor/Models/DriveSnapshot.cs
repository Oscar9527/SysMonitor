namespace SysMonitor.Models;

public sealed record DriveSnapshot(
    string Name,
    string VolumeLabel,
    long UsedBytes,
    long TotalBytes,
    double UsagePercent,
    bool IsSystemDrive);
