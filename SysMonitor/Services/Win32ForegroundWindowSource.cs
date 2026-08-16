using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SysMonitor.Services;

public sealed class Win32ForegroundWindowSource : IForegroundWindowSource
{
    public ForegroundWindowCandidate? Capture()
    {
        nint window = GetForegroundWindow();
        if (window == nint.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out uint processIdValue);
        if (processIdValue == 0 || processIdValue > int.MaxValue)
        {
            return null;
        }

        int processId = (int)processIdValue;
        string processName = string.Empty;
        DateTimeOffset processStartedAt = default;
        bool hasExited = true;
        try
        {
            using Process process = Process.GetProcessById(processId);
            processName = process.ProcessName;
            processStartedAt = process.StartTime.ToUniversalTime();
            hasExited = process.HasExited;
        }
        catch (ArgumentException)
        {
            // The process exited between foreground capture and identity lookup.
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        var className = new StringBuilder(256);
        _ = GetClassName(window, className, className.Capacity);
        var windowTitle = new StringBuilder(Math.Max(1, GetWindowTextLength(window) + 1));
        _ = GetWindowText(window, windowTitle, windowTitle.Capacity);
        return new ForegroundWindowCandidate(
            window,
            processId,
            processStartedAt,
            processName,
            className.ToString(),
            IsWindow(window),
            IsWindowVisible(window),
            hasExited,
            windowTitle.ToString(),
            ProcessExecutablePathResolver.TryResolve(processId));
    }

    public bool IsCurrentIdentity(ForegroundTarget target)
    {
        if (!IsWindow(target.WindowHandle))
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
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, StringBuilder className, int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int maximum);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);
}
