using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SysMonitor.Models;
using SysMonitor.Services;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;

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
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const long WsExTopmost = 0x00000008L;
    private const uint GwHwndPrev = 3;
    private static readonly nint HwndTop = nint.Zero;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNoTopmost = new(-2);

    private readonly DispatcherTimer _positionTimer;
    private HwndSource? _source;
    private nint _targetWindow;
    private double _horizontalPositionPercent = 50d;
    private string _preset = "rivatuner";
    private GameOverlayMetricVisibility _metrics = new();
    private GameOverlayAppearance _appearance = new();

    public GameOverlayWindow()
    {
        InitializeComponent();
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _positionTimer.Tick += (_, _) => PositionWithoutActivation();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    public event EventHandler? TargetInvalidated;
    public bool OverlayVisible => IsVisible;

    public void SetTarget(ForegroundTarget? target)
    {
        _targetWindow = target?.WindowHandle ?? nint.Zero;
        PositionWithoutActivation();
    }

    public void SetHorizontalPositionPercent(double positionPercent)
    {
        _horizontalPositionPercent = double.IsFinite(positionPercent)
            ? Math.Clamp(positionPercent, 0, 100)
            : 50d;
        PositionWithoutActivation();
    }

    public void SetLayout(string preset, GameOverlayMetricVisibility metrics)
    {
        _preset = string.Equals(preset, "compact", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(preset, "detailed", StringComparison.OrdinalIgnoreCase)
            ? preset.ToLowerInvariant()
            : "rivatuner";
        _metrics = metrics ?? new GameOverlayMetricVisibility();
        ApplyMetricOrder();
    }

    private void ApplyMetricOrder()
    {
        string[] order = GameOverlayMetricOrder.Normalize(_metrics.Order).ToArray();
        for (int index = 0; index < order.Length; index++)
        {
            int row = index;
            TextBlock label = order[index] switch
            {
                "cpu" => CpuLabel, "gpu" => GpuLabel, "fps" => FpsLabel,
                "memory" => MemoryLabel, "network" => NetworkLabel, _ => GpuLabel
            };
            TextBlock value = order[index] switch
            {
                "cpu" => CpuValue, "gpu" => GpuValue, "fps" => FpsValue,
                "memory" => MemoryValue, "network" => NetworkValue, _ => GpuValue
            };
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
        }
    }

    public void SetAppearance(GameOverlayAppearance appearance)
    {
        _appearance = SettingsService.NormalizeOverlayAppearance(appearance ?? new GameOverlayAppearance());
        MediaFontFamily family;
        try { family = new MediaFontFamily(_appearance.FontFamily); }
        catch (ArgumentException) { family = new MediaFontFamily("Consolas"); }

        SolidColorBrush gpuBrush = CreateBrush(_appearance.GpuColor, Colors.DeepSkyBlue);
        SolidColorBrush cpuBrush = CreateBrush(_appearance.CpuColor, Colors.LightCyan);
        SolidColorBrush fpsBrush = CreateBrush(_appearance.FpsColor, Colors.LightGreen);
        SolidColorBrush memoryBrush = CreateBrush(_appearance.MemoryColor, Colors.Khaki);
        SolidColorBrush networkBrush = CreateBrush(_appearance.NetworkColor, Colors.Orange);
        MediaColor outline = ParseColor(_appearance.OutlineColor, Colors.Black);
        MediaColor shadow = ParseColor(_appearance.ShadowColor, Colors.Black);
        foreach (TextBlock text in new[] { GpuLabel, GpuValue, CpuLabel, CpuValue, FpsLabel, FpsValue, MemoryLabel, MemoryValue, NetworkLabel, NetworkValue })
        {
            text.FontFamily = family;
            text.FontSize = _appearance.FontSize;
            text.Effect = CreateTextEffect(outline, shadow);
        }
        GpuLabel.Foreground = GpuValue.Foreground = gpuBrush;
        CpuLabel.Foreground = CpuValue.Foreground = cpuBrush;
        FpsLabel.Foreground = FpsValue.Foreground = fpsBrush;
        MemoryLabel.Foreground = MemoryValue.Foreground = memoryBrush;
        NetworkLabel.Foreground = NetworkValue.Foreground = networkBrush;
        PositionWithoutActivation();
    }

    public void UpdateMetrics(MonitorSnapshot monitor, GameOverlayFrameSnapshot frame, double? currentFrequencyMegahertz = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        string fps = frame.Status == GameOverlayFrameStatus.Active &&
            frame.FramesPerSecond is double fpsValue && double.IsFinite(fpsValue)
            ? fpsValue.ToString("0", LocalizationService.Current.ActiveCulture)
            : "--";
        string state = GetCompactFrameState(frame);
        string cpu = FormatPercent(monitor.CpuUsagePercent);
        string cpuTemperature = FormatTemperature(monitor.CpuTemperatureCelsius);
        string gpu = FormatPercent(monitor.Gpu?.UsagePercent);
        string gpuTemperature = FormatTemperature(monitor.Gpu?.TemperatureCelsius);

        SetRow(GpuLabel, GpuValue, _metrics.Gpu, BuildGpuValue(monitor, gpu, gpuTemperature));
        SetRow(CpuLabel, CpuValue, _metrics.Cpu, BuildCpuValue(monitor, cpu, cpuTemperature));
        SetRow(FpsLabel, FpsValue, _metrics.FrameRate, string.IsNullOrWhiteSpace(state) ? fps : $"{fps}  {state}");
        SetRow(MemoryLabel, MemoryValue, _metrics.Memory, BuildMemoryValue(monitor, FormatPercent(monitor.MemoryUsagePercent)));
        SetRow(NetworkLabel, NetworkValue, _metrics.Network,
            $"\u2193 {FormatRate(monitor.DownloadBytesPerSecond)}  \u2191 {FormatRate(monitor.UploadBytesPerSecond)}");
    }

    public void ShowWithoutActivation()
    {
        if (!IsVisible) Show();
        PositionWithoutActivation();
        _positionTimer.Start();
    }

    public void HideOverlay()
    {
        _positionTimer.Stop();
        Hide();
    }

    internal static long ApplyNoActivateStyles(long existingStyle) => GameOverlayNativeStyles.Apply(existingStyle);

    internal static OverlayPixelRect CalculatePlacement(OverlayPixelRect workingArea, double widthDip, double heightDip, uint dpi, double marginDip = 14, double horizontalPositionPercent = 100)
    {
        double scale = Math.Max(1, dpi) / 96d;
        int width = Math.Min(workingArea.Width, Math.Max(1, (int)Math.Ceiling(widthDip * scale)));
        int height = Math.Min(workingArea.Height, Math.Max(1, (int)Math.Ceiling(heightDip * scale)));
        int margin = Math.Max(0, (int)Math.Round(marginDip * scale));
        double travel = Math.Max(0, workingArea.Width - width - (margin * 2));
        int x = Math.Clamp(workingArea.Left + margin + (int)Math.Round(travel * Math.Clamp(horizontalPositionPercent, 0, 100) / 100d), workingArea.Left, workingArea.Right - width);
        int y = Math.Clamp(workingArea.Top + margin, workingArea.Top, workingArea.Bottom - height);
        return new OverlayPixelRect(x, y, x + width, y + height);
    }

    internal static string BuildOverlayText(string fps, string frameState, string cpu, string cpuTemperature, string gpu, string gpuTemperature, string memory, string download = "--", string upload = "--", string preset = "rivatuner", GameOverlayMetricVisibility? metrics = null, string? memoryFrequency = null)
    {
        metrics ??= new GameOverlayMetricVisibility();
        var rows = new List<string>();
        if (metrics.Gpu) rows.Add(preset == "compact" ? $"GPU {gpu}" : $"GPU {gpu}  {gpuTemperature}");
        if (metrics.Cpu) rows.Add(preset == "compact" ? $"CPU {cpu}" : $"CPU {cpu}  {cpuTemperature}");
        if (metrics.FrameRate) rows.Add(string.IsNullOrWhiteSpace(frameState) ? $"FPS {fps}" : $"FPS {fps}  {frameState}");
        if (metrics.Memory) rows.Add(preset == "detailed" && !string.IsNullOrWhiteSpace(memoryFrequency)
            ? $"RAM {memory}  {memoryFrequency}"
            : $"RAM {memory}");
        if (metrics.Network) rows.Add($"NET \u2193 {download}  \u2191 {upload}");
        return string.Join(Environment.NewLine, rows);
    }

    internal static string GetCompactFrameState(GameOverlayFrameSnapshot frame)
    {
        if (frame.Status == GameOverlayFrameStatus.Faulted && frame.Detail?.Contains("session name", StringComparison.OrdinalIgnoreCase) == true)
            return "\u5E27\u7387\u91C7\u96C6\u88AB\u5360\u7528";
        if (frame.Status == GameOverlayFrameStatus.Faulted && frame.Detail?.Contains("ETW resources", StringComparison.OrdinalIgnoreCase) == true)
            return "ETW \u8D44\u6E90\u4E0D\u8DB3";
        return frame.Status switch
        {
            GameOverlayFrameStatus.Unavailable => "未启用",
            GameOverlayFrameStatus.WaitingForTarget => "未选择目标",
            GameOverlayFrameStatus.Starting => "正在采集",
            GameOverlayFrameStatus.NoFrames => "未捕获到帧",
            GameOverlayFrameStatus.Faulted when frame.Detail?.Contains("ACCESS DENIED", StringComparison.OrdinalIgnoreCase) == true => "权限不足",
            GameOverlayFrameStatus.Faulted when frame.Detail?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true => "权限不足",
            GameOverlayFrameStatus.Faulted => "采集失败",
            _ => string.Empty
        };
    }

    private static void SetRow(TextBlock label, TextBlock value, bool visible, string content)
    {
        label.Visibility = value.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        value.Text = content;
    }

    private string BuildCpuValue(MonitorSnapshot monitor, string usage, string temperature)
    {
        if (_preset == "compact") return usage;
        string frequency = FormatFrequency(monitor.CpuFrequencyMhz);
        return _preset == "detailed" ? $"{usage}  {temperature}  {frequency}" : $"{usage}  {temperature}";
    }

    private string BuildGpuValue(MonitorSnapshot monitor, string usage, string temperature)
    {
        if (_preset == "compact") return usage;
        string frequency = FormatFrequency(monitor.Gpu?.CoreClockMhz);
        return _preset == "detailed" ? $"{usage}  {temperature}  {frequency}" : $"{usage}  {temperature}";
    }

    private string BuildMemoryValue(MonitorSnapshot monitor, string usage)
    {
        if (_preset != "detailed") return usage;
        return $"{usage}  {FormatFrequency(monitor.MemoryFrequencyMhz)}";
    }

    private static string FormatPercent(double? value) => value is double percent && double.IsFinite(percent) ? $"{Math.Clamp(percent, 0, 100):0}%" : "--";
    private static string FormatTemperature(double? value) => value is double celsius && double.IsFinite(celsius) ? $"{celsius:0}\u00B0C" : "--";
    private static string FormatFrequency(double? value) => value is double mhz && double.IsFinite(mhz) && mhz > 0 ? $"{mhz:0} MHz" : "--";
    private static string FormatRate(double value) => value switch { <= 0 => "0", < 1024 * 1024 => $"{value / 1024d:0.#}K", _ => $"{value / 1024d / 1024d:0.#}M" };

    private Effect? CreateTextEffect(MediaColor outline, MediaColor shadow)
    {
        if (_appearance.OutlineThickness <= 0 && _appearance.ShadowOpacity <= 0)
            return null;
        // A zero-depth shadow creates a tight outline; the blur/depth setting retains
        // contrast on bright game scenes without adding a solid HUD background.
        MediaColor effectColor = _appearance.OutlineThickness > 0 ? outline : shadow;
        return new DropShadowEffect
        {
            Color = effectColor,
            BlurRadius = Math.Max(0, _appearance.OutlineThickness * 2),
            ShadowDepth = _appearance.ShadowDepth,
            Direction = 315,
            Opacity = _appearance.OutlineThickness > 0 ? 1d : _appearance.ShadowOpacity
        };
    }

    private static SolidColorBrush CreateBrush(string value, MediaColor fallback) => new(ParseColor(value, fallback));
    private static MediaColor ParseColor(string value, MediaColor fallback)
    {
        try { return (MediaColor)MediaColorConverter.ConvertFromString(value)!; }
        catch { return fallback; }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this);
        _source.AddHook(WindowProc);
        nint existing = GetWindowLongPtr(_source.Handle, GwlExStyle);
        _ = SetWindowLongPtr(_source.Handle, GwlExStyle, new nint(ApplyNoActivateStyles(existing.ToInt64())));
        PositionWithoutActivation();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _positionTimer.Stop();
        if (_source is not null) { _source.RemoveHook(WindowProc); _source = null; }
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate) { handled = true; return new nint(MaNoActivate); }
        if (message == WmNcHitTest) { handled = true; return new nint(HtTransparent); }
        if (message == WmDpiChanged) Dispatcher.BeginInvoke(PositionWithoutActivation, DispatcherPriority.Background);
        return nint.Zero;
    }

    private void PositionWithoutActivation()
    {
        if (_source is null) return;
        nint target = _targetWindow;
        if (target != nint.Zero && !IsWindow(target))
        {
            _targetWindow = nint.Zero;
            TargetInvalidated?.Invoke(this, EventArgs.Empty);
            target = nint.Zero;
        }

        OverlayPixelRect placementArea;
        if (target != nint.Zero && TryGetClientAreaOnScreen(target, out OverlayPixelRect clientArea))
        {
            // A manually selected, windowed game must be anchored to its drawing
            // surface, not to the top-left corner of the physical display.
            placementArea = clientArea;
        }
        else
        {
            nint monitor = MonitorFromWindow(target, target == nint.Zero ? MonitorDefaultToPrimary : MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info)) return;
            placementArea = new OverlayPixelRect(info.WorkArea.Left, info.WorkArea.Top, info.WorkArea.Right, info.WorkArea.Bottom);
        }

        uint dpi = target != nint.Zero ? GetDpiForWindow(target) : GetDpiForWindow(_source.Handle);
        if (dpi == 0) dpi = 96;
        OverlayPixelRect placement = CalculatePlacement(placementArea, Width, Height, dpi, marginDip: 4, horizontalPositionPercent: _horizontalPositionPercent);
        nint overlay = _source.Handle;
        bool targetTopmost = target != nint.Zero && IsTopmost(target);
        SynchronizeTopmostTier(overlay, targetTopmost);
        nint predecessor = target != nint.Zero ? GetWindow(target, GwHwndPrev) : nint.Zero;
        OverlayZOrderDecision zOrder = ResolveZOrder(
            overlay,
            target,
            predecessor,
            targetTopmost);
        uint flags = SwpNoActivate | SwpShowWindow;
        if (zOrder.PreserveZOrder)
        {
            flags |= SwpNoZOrder;
        }

        _ = SetWindowPos(
            overlay,
            zOrder.InsertAfter,
            placement.Left,
            placement.Top,
            placement.Width,
            placement.Height,
            flags);
    }

    internal static OverlayZOrderDecision ResolveZOrder(
        nint overlay,
        nint target,
        nint targetPredecessor,
        bool targetTopmost)
    {
        if (target == nint.Zero)
        {
            return new OverlayZOrderDecision(false, HwndNoTopmost, false);
        }

        if (targetPredecessor == overlay)
        {
            return new OverlayZOrderDecision(targetTopmost, nint.Zero, true);
        }

        nint insertAfter = targetPredecessor != nint.Zero
            ? targetPredecessor
            : targetTopmost
                ? HwndTopmost
                : HwndTop;
        return new OverlayZOrderDecision(targetTopmost, insertAfter, false);
    }

    private static bool IsTopmost(nint windowHandle) =>
        (GetWindowLongPtr(windowHandle, GwlExStyle).ToInt64() & WsExTopmost) != 0;

    private static void SynchronizeTopmostTier(nint overlay, bool topmost)
    {
        bool currentlyTopmost = IsTopmost(overlay);
        if (currentlyTopmost == topmost)
        {
            return;
        }

        _ = SetWindowPos(
            overlay,
            topmost ? HwndTopmost : HwndNoTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private static bool TryGetClientAreaOnScreen(nint windowHandle, out OverlayPixelRect area)
    {
        area = default;
        if (!GetClientRect(windowHandle, out NativeRect client) || client.Right <= client.Left || client.Bottom <= client.Top)
        {
            return false;
        }

        var origin = new NativePoint { X = client.Left, Y = client.Top };
        if (!ClientToScreen(windowHandle, ref origin))
        {
            return false;
        }

        area = new OverlayPixelRect(
            origin.X,
            origin.Y,
            origin.X + (client.Right - client.Left),
            origin.Y + (client.Bottom - client.Top));
        return area.Width > 0 && area.Height > 0;
    }

    private static nint GetWindowLongPtr(nint windowHandle, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(windowHandle, index) : new nint(GetWindowLong32(windowHandle, index));
    private static nint SetWindowLongPtr(nint windowHandle, int index, nint value) => IntPtr.Size == 8 ? SetWindowLongPtr64(windowHandle, index, value) : new nint(SetWindowLong32(windowHandle, index, value.ToInt32()));

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect MonitorArea; public NativeRect WorkArea; public uint Flags; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint windowHandle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint windowHandle, out NativeRect rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint windowHandle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint windowHandle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint windowHandle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLong")] private static extern int GetWindowLong32(nint windowHandle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] private static extern nint GetWindowLongPtr64(nint windowHandle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)] private static extern int SetWindowLong32(nint windowHandle, int index, int value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)] private static extern nint SetWindowLongPtr64(nint windowHandle, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint windowHandle, uint command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
}

internal static class GameOverlayNativeStyles
{
    internal const long AppWindow = 0x00040000L;
    internal const long Transparent = 0x00000020L;
    internal const long ToolWindow = 0x00000080L;
    internal const long NoActivate = 0x08000000L;
    internal static long Apply(long existing) => (existing | ToolWindow | NoActivate | Transparent) & ~AppWindow;
}

internal readonly record struct OverlayZOrderDecision(
    bool Topmost,
    nint InsertAfter,
    bool PreserveZOrder);

internal readonly record struct OverlayPixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
