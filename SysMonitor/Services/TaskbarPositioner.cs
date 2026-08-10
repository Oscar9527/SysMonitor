using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SysMonitor.Services;

public static class TaskbarPositioner
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoSendChanging = 0x0400;
    private static readonly nint HwndTop = nint.Zero;

    public const double BandHeightDip = 34;
    public const double MinimumBandWidthDip = 320;

    public static void Invalidate()
    {
        // Region discovery is owned by TaskbarRegionMonitor. Kept for callers that
        // also request a fresh asynchronous snapshot after invalidation.
    }

    public static bool IsTaskbarAvailable() => FindTaskbarWindow() != nint.Zero;

    public static nint FindTaskbarWindow() => FindWindow("Shell_TrayWnd", null);

    public static bool IsWindowHandleAlive(nint handle) =>
        handle != nint.Zero && IsWindow(handle);

    public static bool IsNativeWindowAlive(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return IsWindowHandleAlive(new WindowInteropHelper(window).Handle);
    }

    public static TaskbarPositionResult Position(
        Window window,
        TaskbarRegionSnapshot? snapshot,
        double? horizontalPositionPercent,
        double legacyHorizontalOffsetDip,
        double itemSpacingDip)
    {
        ArgumentNullException.ThrowIfNull(window);
        nint bandHandle = new WindowInteropHelper(window).Handle;
        if (bandHandle == nint.Zero)
        {
            return TaskbarPositionResult.Hide(window.Width);
        }

        if (snapshot is not { IsValid: true } ||
            snapshot.TaskbarHandle == nint.Zero ||
            !IsWindow(snapshot.TaskbarHandle) ||
            GetParent(bandHandle) != snapshot.TaskbarHandle)
        {
            return TaskbarPositionResult.Hide(
                window.Width,
                nativeParentValid: snapshot is null ||
                    snapshot.TaskbarHandle == nint.Zero ||
                    GetParent(bandHandle) == snapshot.TaskbarHandle);
        }

        int taskbarWidth = snapshot.TaskbarRight - snapshot.TaskbarLeft;
        int taskbarHeight = snapshot.TaskbarBottom - snapshot.TaskbarTop;
        int availableWidth = snapshot.SafeRight - snapshot.SafeLeft;
        if (taskbarWidth <= 0 || taskbarHeight <= 4 || availableWidth <= 0)
        {
            return TaskbarPositionResult.Hide(window.Width);
        }

        uint bandDpi = GetDpiForWindow(bandHandle);
        if (bandDpi == 0)
        {
            bandDpi = 96;
        }

        uint taskbarDpi = snapshot.TaskbarDpi == 0 ? 96 : snapshot.TaskbarDpi;
        double taskbarScale = taskbarDpi / 96d;
        double bandScale = bandDpi / 96d;
        double taskbarThicknessDip = taskbarHeight / taskbarScale;
        bool compactLayout = taskbarThicknessDip <= 30;
        bool wideLayout = taskbarThicknessDip > 40;
        double desiredWidthDip = SelectWidth(taskbarThicknessDip, itemSpacingDip);
        double availableWidthDip = availableWidth / bandScale;
        double widthDip = Math.Min(desiredWidthDip, availableWidthDip);
        if (widthDip < MinimumBandWidthDip)
        {
            return TaskbarPositionResult.Hide(window.Width);
        }

        int widthPixels = Math.Max(1, (int)Math.Floor(widthDip * bandScale));
        int heightPixels = Math.Max(1, (int)Math.Round(BandHeightDip * bandScale));
        int minimumX = snapshot.SafeLeft;
        int maximumX = snapshot.SafeRight - widthPixels;
        if (minimumX > maximumX)
        {
            return TaskbarPositionResult.Hide(window.Width);
        }

        double travelDip = (maximumX - minimumX) / bandScale;
        double resolvedPercent = ResolvePositionPercent(
            horizontalPositionPercent,
            legacyHorizontalOffsetDip,
            travelDip);
        int screenX = minimumX + (int)Math.Round(
            (maximumX - minimumX) * resolvedPercent / 100d,
            MidpointRounding.AwayFromZero);
        screenX = Math.Clamp(screenX, minimumX, maximumX);
        int screenY = taskbarHeight >= heightPixels
            ? snapshot.TaskbarTop + (taskbarHeight - heightPixels) / 2
            : snapshot.TaskbarTop;

        var clientPoint = new NativePoint(screenX, screenY);
        if (!ScreenToClient(snapshot.TaskbarHandle, ref clientPoint))
        {
            return TaskbarPositionResult.Hide(window.Width);
        }

        if (Math.Abs(window.Width - widthDip) > 0.1)
        {
            window.Width = widthDip;
        }

        if (Math.Abs(window.Height - BandHeightDip) > 0.1)
        {
            window.Height = BandHeightDip;
        }

        bool matches = GetWindowRect(bandHandle, out NativeRect current) &&
            current.Left == screenX && current.Top == screenY &&
            current.Width == widthPixels && current.Height == heightPixels;
        if (!matches)
        {
            bool positioned = SetWindowPos(
                bandHandle,
                HwndTop,
                clientPoint.X,
                clientPoint.Y,
                widthPixels,
                heightPixels,
                SwpNoActivate | SwpNoZOrder | SwpNoSendChanging);
            if (positioned)
            {
                BandDiagnostics.Log(
                    $"band positioned x={screenX} y={screenY} widthPx={widthPixels} " +
                    $"widthDip={widthDip:0.##} safeLeft={snapshot.SafeLeft} " +
                    $"safeRight={snapshot.SafeRight} travelPx={maximumX - minimumX} " +
                    $"position={resolvedPercent:0.##}% spacingDip={itemSpacingDip:0.##}");
            }
        }

        return new TaskbarPositionResult(
            widthDip,
            false,
            true,
            true,
            false,
            compactLayout,
            wideLayout,
            horizontalPositionPercent is null ? resolvedPercent : null);
    }

    public static double ResolvePositionPercent(
        double? configuredPercent,
        double legacyOffsetDip,
        double travelDip)
    {
        if (configuredPercent is double percent && double.IsFinite(percent))
        {
            return Math.Clamp(percent, 0, 100);
        }

        if (!double.IsFinite(legacyOffsetDip) || legacyOffsetDip >= 0 || travelDip <= 0)
        {
            return 100;
        }

        double leftwardFraction = Math.Clamp(-legacyOffsetDip / travelDip, 0, 1);
        return 100 * (1 - leftwardFraction);
    }

    private static double SelectWidth(double taskbarThicknessDip, double itemSpacingDip)
    {
        double spacing = double.IsFinite(itemSpacingDip)
            ? Math.Clamp(itemSpacingDip, 0, 18)
            : 10;
        if (taskbarThicknessDip <= 30)
        {
            // Five visible groups: 294 DIP of metric slots + four separators.
            return Math.Max(MinimumBandWidthDip, 298 + 5 * spacing);
        }

        // Six visible groups. The width follows the requested spacing instead
        // of reserving a fixed 450/520 DIP rectangle, so lower spacing also
        // creates materially more safe horizontal travel.
        return taskbarThicknessDip <= 40
            ? Math.Max(MinimumBandWidthDip, 361 + 6 * spacing)
            : Math.Max(MinimumBandWidthDip, 413 + 6 * spacing);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativePoint(int X, int Y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}

public readonly record struct TaskbarPositionResult(
    double WidthDip,
    bool RetrySuggested,
    bool LayoutValid,
    bool NativeParentValid = true,
    bool HideRequested = false,
    bool CompactLayout = false,
    bool WideLayout = false,
    double? ResolvedMigratedPositionPercent = null)
{
    public static TaskbarPositionResult Hide(
        double currentWidthDip,
        bool nativeParentValid = true) =>
        new(
            double.IsFinite(currentWidthDip) ? Math.Max(0, currentWidthDip) : 0,
            true,
            false,
            nativeParentValid,
            true,
            false,
            false,
            null);
}
