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
    private static readonly TaskbarSafeConstraintTracker ConstraintTracker = new();

    public const double BandHeightDip = 34;
    public const double MinimumBandWidthDip = 320;

    public static void Invalidate()
    {
        // Region discovery is owned by TaskbarRegionMonitor. Kept for callers that
        // also request a fresh asynchronous snapshot after invalidation.
    }

    public static bool IsTaskbarAvailable() => FindTaskbarWindow() != nint.Zero;

    public static void RejectConstraintExpansion(long observedGeneration)
    {
        ConstraintTracker.RejectPendingExpansion(observedGeneration);
    }

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
        double itemSpacingDip,
        bool explicitLayoutChange = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        nint bandHandle = new WindowInteropHelper(window).Handle;
        if (bandHandle == nint.Zero)
        {
            return TaskbarPositionResult.Hide(window.Width);
        }

        if (snapshot is null ||
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
        if (!TaskbarPlacementStabilizer.IsHorizontal(taskbarWidth, taskbarHeight))
        {
            return TaskbarPositionResult.Hide(
                window.Width,
                retrySuggested: false);
        }

        TaskbarSafeConstraint? observedConstraint = ConstraintTracker.Observe(snapshot);
        bool constraintConfirmationSuggested = ConstraintTracker.HasPendingExpansion;
        if (observedConstraint is not { IsValid: true } constraint)
        {
            return TaskbarPositionResult.Hide(
                window.Width,
                retrySuggested: !snapshot.HasTrustedBounds,
                constraintConfirmationSuggested: constraintConfirmationSuggested);
        }

        int availableWidth = constraint.Right - constraint.Left;

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
            return TaskbarPositionResult.Hide(
                window.Width,
                constraintConfirmationSuggested: constraintConfirmationSuggested);
        }

        int widthPixels = Math.Max(1, (int)Math.Floor(widthDip * bandScale));
        int heightPixels = Math.Max(1, (int)Math.Round(BandHeightDip * bandScale));
        int maximumX = constraint.Right - widthPixels;
        double travelDip = Math.Max(0, maximumX - constraint.Left) / bandScale;
        double resolvedPercent = ResolvePositionPercent(
            horizontalPositionPercent,
            legacyHorizontalOffsetDip,
            travelDip);

        TaskbarBandRect? currentBand = null;
        var clientOrigin = new NativePoint(0, 0);
        if (GetWindowRect(bandHandle, out NativeRect current) &&
            ClientToScreen(snapshot.TaskbarHandle, ref clientOrigin))
        {
            currentBand = new TaskbarBandRect(
                current.Left - clientOrigin.X,
                current.Top - clientOrigin.Y,
                current.Width,
                current.Height);
        }

        TaskbarPlacementDecision placement = TaskbarPlacementStabilizer.Decide(
            constraint,
            taskbarHeight,
            widthPixels,
            heightPixels,
            resolvedPercent,
            currentBand,
            explicitLayoutChange);
        if (placement.HideRequested)
        {
            return TaskbarPositionResult.Hide(
                window.Width,
                constraintConfirmationSuggested: constraintConfirmationSuggested);
        }

        if (Math.Abs(window.Width - widthDip) > 0.1)
        {
            window.Width = widthDip;
        }

        if (Math.Abs(window.Height - BandHeightDip) > 0.1)
        {
            window.Height = BandHeightDip;
        }

        if (placement.SetWindowPosition)
        {
            bool positioned = SetWindowPos(
                bandHandle,
                HwndTop,
                placement.Rect.X,
                placement.Rect.Y,
                placement.Rect.Width,
                placement.Rect.Height,
                SwpNoActivate | SwpNoZOrder | SwpNoSendChanging);
            if (positioned)
            {
                BandDiagnostics.Log(
                    $"band positioned localX={placement.Rect.X} localY={placement.Rect.Y} " +
                    $"widthPx={widthPixels} widthDip={widthDip:0.##} " +
                    $"safeLeftLocal={constraint.Left} safeRightLocal={constraint.Right} " +
                    $"travelPx={Math.Max(0, maximumX - constraint.Left)} " +
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
            horizontalPositionPercent is null ? resolvedPercent : null,
            constraintConfirmationSuggested);
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
    private static extern bool ClientToScreen(nint windowHandle, ref NativePoint point);

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
    double? ResolvedMigratedPositionPercent = null,
    bool ConstraintConfirmationSuggested = false)
{
    public static TaskbarPositionResult Hide(
        double currentWidthDip,
        bool nativeParentValid = true,
        bool retrySuggested = true,
        bool constraintConfirmationSuggested = false) =>
        new(
            double.IsFinite(currentWidthDip) ? Math.Max(0, currentWidthDip) : 0,
            retrySuggested,
            false,
            nativeParentValid,
            true,
            false,
            false,
            null,
            constraintConfirmationSuggested);
}
