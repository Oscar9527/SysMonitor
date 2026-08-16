using System.IO;

namespace SysMonitor.Services;

public enum ForegroundTargetState
{
    Idle,
    WaitingForTarget,
    Ready
}

public sealed record ForegroundTarget(
    nint WindowHandle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    DateTimeOffset QualifiedAt)
{
    public bool SameIdentity(ForegroundTarget? other) =>
        other is not null &&
        WindowHandle == other.WindowHandle &&
        ProcessId == other.ProcessId &&
        ProcessStartedAt == other.ProcessStartedAt;
}

public sealed record GameOverlayTargetOption(
    nint WindowHandle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string ProcessName,
    string WindowTitle);

public sealed record ForegroundWindowCandidate(
    nint WindowHandle,
    int ProcessId,
    DateTimeOffset ProcessStartedAt,
    string ProcessName,
    string WindowClass,
    bool IsWindow,
    bool IsVisible,
    bool HasExited,
    string WindowTitle = "");

public interface IForegroundWindowSource
{
    ForegroundWindowCandidate? Capture();
    bool IsCurrentIdentity(ForegroundTarget target);
}

public static class ForegroundTargetPolicy
{
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "dwm",
        "ShellExperienceHost",
        "StartMenuExperienceHost"
    };

    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
        "DV2ControlHost",
        "Windows.UI.Core.CoreWindow"
    };

    public static bool IsQualified(ForegroundWindowCandidate? candidate, int currentProcessId)
    {
        if (candidate is null ||
            candidate.WindowHandle == nint.Zero ||
            candidate.ProcessId <= 0 ||
            candidate.ProcessId == currentProcessId ||
            !candidate.IsWindow ||
            !candidate.IsVisible ||
            candidate.HasExited ||
            candidate.ProcessStartedAt == default)
        {
            return false;
        }

        string processName = Path.GetFileNameWithoutExtension(candidate.ProcessName ?? string.Empty);
        return !ExcludedProcesses.Contains(processName) &&
            !ExcludedClasses.Contains(candidate.WindowClass ?? string.Empty);
    }
}
