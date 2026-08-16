using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SysMonitor.Services;

public static class GameOverlayTargetCatalog
{
    public static IReadOnlyList<GameOverlayTargetOption> Enumerate(int? currentProcessId = null)
    {
        int ownProcessId = currentProcessId ?? Environment.ProcessId;
        var targets = new Dictionary<int, GameOverlayTargetOption>();
        _ = EnumWindows((window, _) =>
        {
            GameOverlayTargetOption? target = TryCreate(window, ownProcessId);
            if (target is not null &&
                (!targets.TryGetValue(target.ProcessId, out GameOverlayTargetOption? existing) ||
                 target.WindowTitle.Length > existing.WindowTitle.Length))
            {
                targets[target.ProcessId] = target;
            }

            return true;
        }, nint.Zero);

        return targets.Values
            .OrderBy(target => target.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(target => target.WindowTitle, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static ForegroundTarget? ToForegroundTarget(GameOverlayTargetOption? target)
    {
        if (target is null || !IsCurrent(target))
        {
            return null;
        }

        return new ForegroundTarget(
            target.WindowHandle,
            target.ProcessId,
            target.ProcessStartedAt,
            DateTimeOffset.UtcNow);
    }

    internal static string BuildDisplayName(GameOverlayTargetOption target)
    {
        string process = string.IsNullOrWhiteSpace(target.ProcessName)
            ? $"PID {target.ProcessId}"
            : target.ProcessName;
        string title = target.WindowTitle.Trim();
        return string.IsNullOrWhiteSpace(title) ||
            string.Equals(title, process, StringComparison.OrdinalIgnoreCase)
                ? process
                : $"{process} — {title}";
    }

    private static GameOverlayTargetOption? TryCreate(nint window, int currentProcessId)
    {
        if (window == nint.Zero || !IsWindowVisible(window) || GetWindow(window, GwOwner) != nint.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out uint processIdValue);
        if (processIdValue == 0 || processIdValue > int.MaxValue || processIdValue == currentProcessId)
        {
            return null;
        }

        int titleLength = GetWindowTextLength(window);
        if (titleLength <= 0)
        {
            return null;
        }

        var title = new StringBuilder(titleLength + 1);
        _ = GetWindowText(window, title, title.Capacity);
        if (string.IsNullOrWhiteSpace(title.ToString()))
        {
            return null;
        }

        try
        {
            using Process process = Process.GetProcessById((int)processIdValue);
            if (process.HasExited)
            {
                return null;
            }

            return new GameOverlayTargetOption(
                window,
                process.Id,
                new DateTimeOffset(process.StartTime.ToUniversalTime()),
                process.ProcessName,
                title.ToString());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool IsCurrent(GameOverlayTargetOption target)
    {
        if (!IsWindow(target.WindowHandle) || !IsWindowVisible(target.WindowHandle))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(target.WindowHandle, out uint processId);
        if (processId != target.ProcessId)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(target.ProcessId);
            return !process.HasExited &&
                new DateTimeOffset(process.StartTime.ToUniversalTime()) == target.ProcessStartedAt;
        }
        catch
        {
            return false;
        }
    }

    private const uint GwOwner = 4;
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint window);
}
