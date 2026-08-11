using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.UI;

public partial class GameOverlayWindow : Window, IGameOverlayView
{
    private const int GwlExStyle = -20;
    private const int WmMouseActivate = 0x0021;
    private const int WmNcHitTest = 0x0084;
    private const int WmDpiChanged = 0x02E0;
    private const int MaNoActivate = 3;
    private const int HtTransparent = -1;
    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);

    private readonly DispatcherTimer _positionTimer;
    private HwndSource? _source;
    private nint _targetWindow;

    public GameOverlayWindow()
    {
        InitializeComponent();
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _positionTimer.Tick += (_, _) => PositionWithoutActivation();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        LocalizationService.Current.CultureChanged += OnCultureChanged;
        ApplyLocalizedText();
    }

    public event EventHandler? TargetInvalidated;

    public bool OverlayVisible => IsVisible;

    public void SetTarget(ForegroundTarget? target)
    {
        _targetWindow = target?.WindowHandle ?? nint.Zero;
        PositionWithoutActivation();
    }

    public void UpdateMetrics(
        MonitorSnapshot monitor,
        GameOverlayFrameSnapshot frame,
        double? currentFrequencyMegahertz = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        PresentFpsValue.Text = frame.Status == GameOverlayFrameStatus.Active &&
            frame.FramesPerSecond is double fps && double.IsFinite(fps)
            ? fps.ToString("0", LocalizationService.Current.ActiveCulture)
            : "--";
        FrameStateText.Text = frame.Status switch
        {
            GameOverlayFrameStatus.WaitingForTarget =>
                LocalizationService.Current.GetString("OverlayStateWaitingForTarget"),
            GameOverlayFrameStatus.Starting =>
                LocalizationService.Current.GetString("OverlayStateStarting"),
            GameOverlayFrameStatus.Faulted =>
                LocalizationService.Current.GetString("OverlayStateUnavailable"),
            _ => string.Empty,
        };
        CpuValue.Text = FormatPercent(monitor.CpuUsagePercent);
        MemoryValue.Text = FormatPercent(monitor.MemoryUsagePercent);
        GpuValue.Text = FormatPercent(monitor.Gpu?.UsagePercent);
        CpuTemperatureValue.Text = FormatTemperature(monitor.CpuTemperatureCelsius);
        GpuTemperatureValue.Text = FormatTemperature(monitor.Gpu?.TemperatureCelsius);
        double? cpuClock = currentFrequencyMegahertz ?? monitor.CpuFrequencyMhz;
        FrequencyValue.Text = FormatClocks(cpuClock, monitor.Gpu?.CoreClockMhz);
    }

    public void ShowWithoutActivation()
    {
        if (!IsVisible)
        {
            Show();
        }

        PositionWithoutActivation();
        _positionTimer.Start();
    }

    public void HideOverlay()
    {
        _positionTimer.Stop();
        Hide();
    }

    internal static long ApplyNoActivateStyles(long existingStyle) =>
        GameOverlayNativeStyles.Apply(existingStyle);

    internal static OverlayPixelRect CalculatePlacement(
        OverlayPixelRect workingArea,
        double widthDip,
        double heightDip,
        uint dpi,
        double marginDip = 14)
    {
        double scale = Math.Max(1, dpi) / 96d;
        int width = Math.Min(workingArea.Width, Math.Max(1, (int)Math.Ceiling(widthDip * scale)));
        int height = Math.Min(workingArea.Height, Math.Max(1, (int)Math.Ceiling(heightDip * scale)));
        int margin = Math.Max(0, (int)Math.Round(marginDip * scale));
        int x = Math.Clamp(
            workingArea.Right - width - margin,
            workingArea.Left,
            workingArea.Right - width);
        int y = Math.Clamp(
            workingArea.Top + margin,
            workingArea.Top,
            workingArea.Bottom - height);
        return new OverlayPixelRect(x, y, x + width, y + height);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this);
        _source.AddHook(WindowProc);
        nint existing = GetWindowLongPtr(_source.Handle, GwlExStyle);
        _ = SetWindowLongPtr(
            _source.Handle,
            GwlExStyle,
            new nint(ApplyNoActivateStyles(existing.ToInt64())));
        PositionWithoutActivation();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        LocalizationService.Current.CultureChanged -= OnCultureChanged;
        _positionTimer.Stop();
        if (_source is not null)
        {
            _source.RemoveHook(WindowProc);
            _source = null;
        }
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new nint(MaNoActivate);
        }

        if (message == WmNcHitTest)
        {
            handled = true;
            return new nint(HtTransparent);
        }

        if (message == WmDpiChanged)
        {
            Dispatcher.BeginInvoke(PositionWithoutActivation, DispatcherPriority.Background);
        }

        return nint.Zero;
    }

    private void PositionWithoutActivation()
    {
        if (_source is null)
        {
            return;
        }

        nint target = _targetWindow;
        if (target != nint.Zero && !IsWindow(target))
        {
            _targetWindow = nint.Zero;
            TargetInvalidated?.Invoke(this, EventArgs.Empty);
            target = nint.Zero;
        }

        nint monitor = MonitorFromWindow(
            target,
            target == nint.Zero ? MonitorDefaultToPrimary : MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        uint dpi = target != nint.Zero ? GetDpiForWindow(target) : GetDpiForWindow(_source.Handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        OverlayPixelRect placement = CalculatePlacement(
            new OverlayPixelRect(
                info.WorkArea.Left,
                info.WorkArea.Top,
                info.WorkArea.Right,
                info.WorkArea.Bottom),
            Width,
            Height,
            dpi);
        _ = SetWindowPos(
            _source.Handle,
            HwndTopmost,
            placement.Left,
            placement.Top,
            placement.Width,
            placement.Height,
            SwpNoActivate | SwpShowWindow);
    }

    private void OnCultureChanged(object? sender, EventArgs e) => ApplyLocalizedText();

    private void ApplyLocalizedText()
    {
        LocalizationService localization = LocalizationService.Current;
        PresentFpsLabel.Text = localization.GetString("OverlayPresentFps");
        CpuLabel.Text = localization.GetString("OverlaySystemCpu");
        MemoryLabel.Text = localization.GetString("OverlaySystemMemory");
        GpuLabel.Text = localization.GetString("OverlaySystemGpu");
        CpuTemperatureLabel.Text = localization.GetString("OverlayCpuTemperature");
        GpuTemperatureLabel.Text = localization.GetString("OverlayGpuTemperature");
        FrequencyLabel.Text = localization.GetString("OverlayCurrentFrequency");
    }

    private static string FormatPercent(double? value) =>
        value is double percent && double.IsFinite(percent)
            ? $"{Math.Clamp(percent, 0, 100):0}%"
            : "--";

    private static string FormatTemperature(double? value) =>
        value is double celsius && double.IsFinite(celsius)
            ? $"{celsius:0} °C"
            : "--";

    private static string FormatClocks(double? cpuMhz, double? gpuMhz)
    {
        string cpu = cpuMhz is double cpuValue && double.IsFinite(cpuValue) && cpuValue > 0
            ? $"{cpuValue / 1000d:0.0}"
            : "--";
        string gpu = gpuMhz is double gpuValue && double.IsFinite(gpuValue) && gpuValue > 0
            ? $"{gpuValue / 1000d:0.0}"
            : "--";
        return cpu == "--" && gpu == "--" ? "--" : $"{cpu} / {gpu} GHz";
    }

    private static nint GetWindowLongPtr(nint windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new nint(GetWindowLong32(windowHandle, index));

    private static nint SetWindowLongPtr(nint windowHandle, int index, nint value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new nint(SetWindowLong32(windowHandle, index, value.ToInt32()));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(nint windowHandle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint value);

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

internal static class GameOverlayNativeStyles
{
    internal const long AppWindow = 0x00040000L;
    internal const long Transparent = 0x00000020L;
    internal const long ToolWindow = 0x00000080L;
    internal const long NoActivate = 0x08000000L;

    internal static long Apply(long existing) =>
        (existing | ToolWindow | NoActivate | Transparent) & ~AppWindow;
}

internal readonly record struct OverlayPixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
