namespace SysMonitor.Models;

public enum FrameRateStatus
{
    Disabled,
    NoTarget,
    Starting,
    WaitingForFrames,
    Active,
    Stale,
    NoPresentEvents,
    PermissionDenied,
    SessionConflict,
    IncompatibleOutput,
    ProviderExited,
    Stopping,
}

public sealed record FrameRateSnapshot(
    double? PresentFps,
    FrameRateStatus Status,
    int? TargetProcessId,
    DateTimeOffset SampledAt,
    string? Detail = null)
{
    public static FrameRateSnapshot Disabled { get; } = new(
        null,
        FrameRateStatus.Disabled,
        null,
        DateTimeOffset.UtcNow);
}
