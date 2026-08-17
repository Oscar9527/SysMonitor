using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
    private readonly OverlayMonitorIdentityResolver _monitorIdentityResolver = new();
    private HwndSource? _source;
    private nint _targetWindow;
    private double _horizontalPositionPercent = 50d;
    private string _preset = "rivatuner";
    private string _layoutMode = "vertical";
    private IReadOnlyList<GameOverlayMonitorPositionSettings> _monitorPositions = [];
    private OverlayPixelRect? _lastPlacement;
    private string? _lastPlacementMonitorId;
    private OverlayMonitorIdentity _cachedMonitorIdentity;
    private nint _cachedIdentityWindow;
    private bool _hasCachedMonitorIdentity;
    private OverlayPixelRect _lastAppliedPlacement;
    private nint _lastAppliedInsertAfter;
    private uint _lastAppliedFlags;
    private bool _placementApplied;
    private bool? _lastFrameRateVisible;
    private GameOverlayMetricVisibility _metrics = new();
    private GameOverlayAppearance _appearance = new();

    public GameOverlayWindow()
    {
        InitializeComponent();
        // The legacy XAML margin was intended as a visual inset, but it also
        // leaked into the native window bounds.  Exact coordinates are
        // physical pixels, so keep the root content flush with the HWND.
        if (Content is FrameworkElement root)
        {
            root.Margin = new Thickness(0);
        }
        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(750)
        };
        _positionTimer.Tick += (_, _) => PositionWithoutActivation(refreshMonitor: true);
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    public event EventHandler? TargetInvalidated;
    public bool OverlayVisible => IsVisible;

    public void SetTarget(ForegroundTarget? target)
    {
        _targetWindow = target?.WindowHandle ?? nint.Zero;
        InvalidateMonitorCache();
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
        ConfigureGridLayout();
        SchedulePosition();
    }

    public void SetLayoutMode(string? layoutMode)
    {
        string normalized = string.Equals(layoutMode, "horizontal", StringComparison.OrdinalIgnoreCase)
            ? "horizontal"
            : "vertical";
        if (string.Equals(_layoutMode, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _layoutMode = normalized;
        ConfigureGridLayout();
        SchedulePosition();
    }

    public void SetMonitorPositions(IEnumerable<GameOverlayMonitorPositionSettings>? positions)
    {
        _monitorPositions = SettingsService.NormalizeOverlayMonitorPositions(positions);
        PositionWithoutActivation();
    }

    internal GameOverlayPreviewState CapturePreviewState() =>
        new(_layoutMode, CloneMonitorPositions(_monitorPositions));

    internal void RestorePreviewState(GameOverlayPreviewState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        SetLayoutMode(state.LayoutMode);
        SetMonitorPositions(state.MonitorPositions);
    }

    internal static IReadOnlyList<GameOverlayMonitorPositionSettings> BuildPreviewMonitorPositions(
        IEnumerable<GameOverlayMonitorPositionSettings>? baseline,
        OverlayMonitorIdentity identity,
        bool exactEnabled,
        int requestedX,
        int requestedY)
    {
        List<GameOverlayMonitorPositionSettings> preview = CloneMonitorPositions(baseline);
        preview.RemoveAll(position => MatchesMonitorPosition(position, identity));
        if (exactEnabled)
        {
            preview.Add(new GameOverlayMonitorPositionSettings
            {
                StableMonitorId = identity.StableMonitorId,
                GdiDeviceName = identity.GdiDeviceName,
                IsFallbackIdentity = identity.IsFallback,
                Left = identity.Bounds.Left,
                Top = identity.Bounds.Top,
                Right = identity.Bounds.Right,
                Bottom = identity.Bounds.Bottom,
                X = requestedX,
                Y = requestedY
            });
        }

        return preview;
    }

    private static List<GameOverlayMonitorPositionSettings> CloneMonitorPositions(
        IEnumerable<GameOverlayMonitorPositionSettings>? positions) =>
        SettingsService.NormalizeOverlayMonitorPositions(positions);

    private static bool MatchesMonitorPosition(
        GameOverlayMonitorPositionSettings position,
        OverlayMonitorIdentity identity) =>
        identity.IsFallback
            ? position.IsFallbackIdentity &&
              string.Equals(position.GdiDeviceName, identity.GdiDeviceName, StringComparison.OrdinalIgnoreCase) &&
              position.Left == identity.Bounds.Left && position.Top == identity.Bounds.Top &&
              position.Right == identity.Bounds.Right && position.Bottom == identity.Bounds.Bottom
            : !position.IsFallbackIdentity &&
              string.Equals(position.StableMonitorId, identity.StableMonitorId, StringComparison.OrdinalIgnoreCase);

    internal bool TryGetCurrentMonitorIdentity(out OverlayMonitorIdentity identity) =>
        TryResolveMonitorIdentity(forceRefresh: false, out identity);

    public bool TryGetCurrentCoordinateContext(out OverlaySettingsCoordinateContext context)
    {
        context = default!;
        if (!TryGetCurrentMonitorIdentity(out OverlayMonitorIdentity identity))
        {
            return false;
        }

        bool exact = TryFindExactPosition(identity, _monitorPositions, out GameOverlayMonitorPositionSettings? position);
        OverlayPixelRect monitorBounds = ToOverlayRect(identity.Bounds);
        uint dpi = GetCurrentDpi();
        OverlayPixelRect placement = exact
            ? CalculateExactPlacement(
                monitorBounds,
                GetWidthDip(),
                GetHeightDip(),
                dpi,
                position!.X,
                position.Y)
            : CalculateLegacyPlacementForContext(identity);
        OverlayPixelRect minimumPlacement = CalculateExactPlacement(
            monitorBounds, GetWidthDip(), GetHeightDip(), dpi, int.MinValue, int.MinValue);
        OverlayPixelRect maximumPlacement = CalculateExactPlacement(
            monitorBounds, GetWidthDip(), GetHeightDip(), dpi, int.MaxValue, int.MaxValue);
        if (string.Equals(_lastPlacementMonitorId, identity.StableMonitorId, StringComparison.Ordinal) &&
            _lastPlacement is OverlayPixelRect current)
        {
            placement = current;
        }

        context = new OverlaySettingsCoordinateContext(
            identity.StableMonitorId,
            $"{identity.DisplayName} ({identity.GdiDeviceName})",
            identity.Bounds.Left,
            identity.Bounds.Top,
            identity.Bounds.Right,
            identity.Bounds.Bottom,
            placement.Left,
            placement.Top,
            exact,
            minimumPlacement.Left,
            maximumPlacement.Left,
            minimumPlacement.Top,
            maximumPlacement.Top);
        return true;
    }

    internal static bool CoordinateContextMatches(
        OverlaySettingsCoordinateContext requested,
        OverlaySettingsCoordinateContext current) =>
        string.Equals(requested.StableMonitorId, current.StableMonitorId, StringComparison.Ordinal) &&
        requested.Left == current.Left &&
        requested.Top == current.Top &&
        requested.Right == current.Right &&
        requested.Bottom == current.Bottom;

    internal bool TryClampCurrentExactPosition(int requestedX, int requestedY, out int x, out int y)
    {
        x = requestedX;
        y = requestedY;
        if (!TryGetCurrentMonitorIdentity(out OverlayMonitorIdentity identity))
        {
            return false;
        }

        OverlayPixelRect placement = CalculateExactPlacement(
            ToOverlayRect(identity.Bounds),
            GetWidthDip(),
            GetHeightDip(),
            GetCurrentDpi(),
            requestedX,
            requestedY);
        x = placement.Left;
        y = placement.Top;
        return true;
    }

    internal static bool TryFindExactPosition(
        OverlayMonitorIdentity identity,
        IEnumerable<GameOverlayMonitorPositionSettings>? positions,
        out GameOverlayMonitorPositionSettings? match)
    {
        match = null;
        List<GameOverlayMonitorPositionSettings> all = (positions ?? []).ToList();
        var stableIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (all.Any(candidate => !candidate.IsFallbackIdentity &&
            !stableIds.Add(candidate.StableMonitorId)))
        {
            return false;
        }

        List<GameOverlayMonitorPositionSettings> candidates = all
            .Where(candidate => identity.IsFallback
                ? candidate.IsFallbackIdentity &&
                  string.Equals(candidate.GdiDeviceName, identity.GdiDeviceName, StringComparison.OrdinalIgnoreCase) &&
                  candidate.Left == identity.Bounds.Left && candidate.Top == identity.Bounds.Top &&
                  candidate.Right == identity.Bounds.Right && candidate.Bottom == identity.Bounds.Bottom
                : !candidate.IsFallbackIdentity &&
                  string.Equals(candidate.StableMonitorId, identity.StableMonitorId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count != 1)
        {
            return false;
        }

        match = candidates[0];
        return true;
    }

    private void ConfigureGridLayout()
    {
        if (_layoutMode == "horizontal")
        {
            ConfigureHorizontalGrid();
        }
        else
        {
            ConfigureVerticalGrid();
        }

        bool horizontal = _layoutMode == "horizontal";
        CpuLabel.Visibility = CpuValue.Visibility = horizontal || _metrics.Cpu
            ? Visibility.Visible
            : Visibility.Collapsed;
        GpuLabel.Visibility = GpuValue.Visibility = horizontal || _metrics.Gpu
            ? Visibility.Visible
            : Visibility.Collapsed;
        FpsLabel.Visibility = FpsValue.Visibility = _lastFrameRateVisible == true &&
            (horizontal || _metrics.FrameRate)
                ? Visibility.Visible
                : Visibility.Collapsed;
        MemoryLabel.Visibility = MemoryValue.Visibility = !horizontal && _metrics.Memory
            ? Visibility.Visible
            : Visibility.Collapsed;
        NetworkLabel.Visibility = NetworkValue.Visibility = !horizontal && _metrics.Network
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ConfigureVerticalGrid()
    {
        OverlayGrid.MinWidth = 250;
        OverlayGrid.RowDefinitions.Clear();
        OverlayGrid.ColumnDefinitions.Clear();
        for (int index = 0; index < 5; index++) OverlayGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        OverlayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        OverlayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
            Grid.SetColumn(label, 0);
            Grid.SetColumn(value, 1);
            label.Margin = value.Margin = new Thickness(0, 0, 8, 1);
            label.HorizontalAlignment = value.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        }
    }

    private void ConfigureHorizontalGrid()
    {
        OverlayGrid.MinWidth = 0;
        OverlayGrid.RowDefinitions.Clear();
        OverlayGrid.ColumnDefinitions.Clear();
        OverlayGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int index = 0; index < 6; index++) OverlayGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        (TextBlock label, TextBlock value)[] items =
        [
            (CpuLabel, CpuValue),
            (GpuLabel, GpuValue),
            (FpsLabel, FpsValue)
        ];
        for (int index = 0; index < items.Length; index++)
        {
            (TextBlock label, TextBlock value) = items[index];
            Grid.SetRow(label, 0);
            Grid.SetRow(value, 0);
            Grid.SetColumn(label, index * 2);
            Grid.SetColumn(value, (index * 2) + 1);
            label.Margin = new Thickness(index == 0 ? 0 : 18, 0, 5, 0);
            value.Margin = new Thickness(0);
            label.HorizontalAlignment = value.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        }

        Grid.SetRow(MemoryLabel, 0);
        Grid.SetRow(MemoryValue, 0);
        Grid.SetRow(NetworkLabel, 0);
        Grid.SetRow(NetworkValue, 0);
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
        foreach (TextBlock text in new[] { GpuLabel, GpuValue, CpuLabel, CpuValue, FpsLabel, FpsValue, MemoryLabel, MemoryValue, NetworkLabel, NetworkValue })
        {
            text.FontFamily = family;
            text.FontSize = _appearance.FontSize;
            // Text effects allocate a render target/blur shader on every
            // appearance refresh and are particularly expensive while a HUD
            // is being previewed.  Solid text remains readable over games and
            // avoids a per-refresh GPU effect allocation.
            text.Effect = null;
        }
        GpuLabel.Foreground = GpuValue.Foreground = gpuBrush;
        CpuLabel.Foreground = CpuValue.Foreground = cpuBrush;
        FpsLabel.Foreground = FpsValue.Foreground = fpsBrush;
        MemoryLabel.Foreground = MemoryValue.Foreground = memoryBrush;
        NetworkLabel.Foreground = NetworkValue.Foreground = networkBrush;
        SchedulePosition();
    }

    public void UpdateMetrics(MonitorSnapshot monitor, GameOverlayFrameSnapshot frame, double? currentFrequencyMegahertz = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        bool horizontal = _layoutMode == "horizontal";
        bool showFrameRate = ShouldShowFrameRate(frame, horizontal || _metrics.FrameRate);
        string fps = showFrameRate && frame.FramesPerSecond is double fpsValue
            ? fpsValue.ToString("0", LocalizationService.Current.ActiveCulture)
            : "--";
        string cpu = FormatPercent(monitor.CpuUsagePercent);
        string cpuTemperature = FormatTemperature(monitor.CpuTemperatureCelsius);
        string gpu = FormatPercent(monitor.Gpu?.UsagePercent);
        string gpuTemperature = FormatTemperature(monitor.Gpu?.TemperatureCelsius);

        SetRow(GpuLabel, GpuValue, horizontal || _metrics.Gpu,
            horizontal ? BuildHorizontalMetricValue(gpu, gpuTemperature) : BuildGpuValue(monitor, gpu, gpuTemperature));
        SetRow(CpuLabel, CpuValue, horizontal || _metrics.Cpu,
            horizontal ? BuildHorizontalMetricValue(cpu, cpuTemperature) : BuildCpuValue(monitor, cpu, cpuTemperature));
        SetRow(FpsLabel, FpsValue, showFrameRate, fps);
        SetRow(MemoryLabel, MemoryValue, !horizontal && _metrics.Memory, BuildMemoryValue(monitor, FormatPercent(monitor.MemoryUsagePercent)));
        SetRow(NetworkLabel, NetworkValue, !horizontal && _metrics.Network,
            $"\u2193 {FormatRate(monitor.DownloadBytesPerSecond)}  \u2191 {FormatRate(monitor.UploadBytesPerSecond)}");
        if (_lastFrameRateVisible != showFrameRate)
        {
            _lastFrameRateVisible = showFrameRate;
            SchedulePosition();
        }
    }

    public void ShowWithoutActivation()
    {
        if (!IsVisible) Show();
        _placementApplied = false;
        PositionWithoutActivation();
        _positionTimer.Start();
    }

    public void HideOverlay()
    {
        _positionTimer.Stop();
        _placementApplied = false;
        Hide();
    }

    internal static long ApplyNoActivateStyles(long existingStyle) => GameOverlayNativeStyles.Apply(existingStyle);

    internal static bool ShouldShowFrameRate(GameOverlayFrameSnapshot frame, bool configured) =>
        configured &&
        frame.Status == GameOverlayFrameStatus.Active &&
        frame.FramesPerSecond is double framesPerSecond &&
        double.IsFinite(framesPerSecond) &&
        framesPerSecond >= 0;

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

    internal static OverlayPixelRect CalculateExactPlacement(
        OverlayPixelRect screen,
        double widthDip,
        double heightDip,
        uint dpi,
        int requestedX,
        int requestedY)
    {
        double scale = Math.Max(1, dpi) / 96d;
        double safeWidth = double.IsFinite(widthDip) && widthDip > 0 ? widthDip : 1;
        double safeHeight = double.IsFinite(heightDip) && heightDip > 0 ? heightDip : 1;
        int width = Math.Min(screen.Width, Math.Max(1, (int)Math.Ceiling(safeWidth * scale)));
        int height = Math.Min(screen.Height, Math.Max(1, (int)Math.Ceiling(safeHeight * scale)));
        int x = Math.Clamp(requestedX, screen.Left, screen.Right - width);
        int y = Math.Clamp(requestedY, screen.Top, screen.Bottom - height);
        return new OverlayPixelRect(x, y, x + width, y + height);
    }

    internal static string BuildHorizontalMetricValue(string usage, string temperature) =>
        $"{(string.IsNullOrWhiteSpace(usage) ? "--" : usage)}  {(string.IsNullOrWhiteSpace(temperature) ? "--" : temperature)}";

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
            GameOverlayFrameStatus.NoFrames => string.Empty,
            GameOverlayFrameStatus.Faulted when frame.Detail?.Contains("ACCESS DENIED", StringComparison.OrdinalIgnoreCase) == true => "权限不足",
            GameOverlayFrameStatus.Faulted when frame.Detail?.Contains("permission", StringComparison.OrdinalIgnoreCase) == true => "权限不足",
            GameOverlayFrameStatus.Faulted => "采集失败",
            _ => string.Empty
        };
    }

    private static void SetRow(TextBlock label, TextBlock value, bool visible, string content)
    {
        Visibility nextVisibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (label.Visibility != nextVisibility) label.Visibility = nextVisibility;
        if (value.Visibility != nextVisibility) value.Visibility = nextVisibility;
        if (!string.Equals(value.Text, content, StringComparison.Ordinal))
        {
            value.Text = content;
        }
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
        _placementApplied = false;
        if (_source is not null) { _source.RemoveHook(WindowProc); _source = null; }
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmMouseActivate) { handled = true; return new nint(MaNoActivate); }
        if (message == WmNcHitTest) { handled = true; return new nint(HtTransparent); }
        if (message == WmDpiChanged) SchedulePosition();
        return nint.Zero;
    }

    private void SchedulePosition()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.InvokeAsync(
            () => PositionWithoutActivation(),
            DispatcherPriority.Render);
    }

    private void PositionWithoutActivation(bool refreshMonitor = false)
    {
        if (_source is null) return;
        nint target = _targetWindow;
        if (target != nint.Zero && !IsWindow(target))
        {
            _targetWindow = nint.Zero;
            TargetInvalidated?.Invoke(this, EventArgs.Empty);
            target = nint.Zero;
        }

        OverlayMonitorIdentity? monitorIdentity =
            TryResolveMonitorIdentity(refreshMonitor, out OverlayMonitorIdentity resolvedIdentity)
                ? resolvedIdentity
                : null;
        GameOverlayMonitorPositionSettings? exactPosition = null;
        bool hasExactPosition = monitorIdentity is OverlayMonitorIdentity identity &&
            TryFindExactPosition(identity, _monitorPositions, out exactPosition);

        OverlayPixelRect placementArea;
        if (!hasExactPosition && target != nint.Zero && TryGetClientAreaOnScreen(target, out OverlayPixelRect clientArea))
        {
            // A manually selected, windowed game must be anchored to its drawing
            // surface, not to the top-left corner of the physical display.
            placementArea = clientArea;
        }
        else if (hasExactPosition && monitorIdentity is OverlayMonitorIdentity exactIdentity)
        {
            placementArea = ToOverlayRect(exactIdentity.Bounds);
        }
        else if (!TryGetWorkArea(target, out placementArea))
        {
            return;
        }

        uint dpi = GetCurrentDpi();
        OverlayPixelRect placement = hasExactPosition
            ? CalculateExactPlacement(
                ToOverlayRect(monitorIdentity!.Value.Bounds),
                GetWidthDip(),
                GetHeightDip(),
                dpi,
                exactPosition!.X,
                exactPosition.Y)
            : CalculatePlacement(
                placementArea,
                GetWidthDip(),
                GetHeightDip(),
                dpi,
                marginDip: GetPresetMarginDip(dpi),
                horizontalPositionPercent: _horizontalPositionPercent);
        _lastPlacement = placement;
        _lastPlacementMonitorId = monitorIdentity?.StableMonitorId;
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

        if (_placementApplied &&
            _lastAppliedPlacement == placement &&
            _lastAppliedInsertAfter == zOrder.InsertAfter &&
            _lastAppliedFlags == flags)
        {
            return;
        }

        _ = SetWindowPos(
            overlay,
            zOrder.InsertAfter,
            placement.Left,
            placement.Top,
            placement.Width,
            placement.Height,
            flags);
        _lastAppliedPlacement = placement;
        _lastAppliedInsertAfter = zOrder.InsertAfter;
        _lastAppliedFlags = flags;
        _placementApplied = true;
    }

    private OverlayPixelRect CalculateLegacyPlacementForContext(OverlayMonitorIdentity identity)
    {
        OverlayPixelRect area = _targetWindow != nint.Zero &&
            TryGetClientAreaOnScreen(_targetWindow, out OverlayPixelRect clientArea)
                ? clientArea
                : TryGetWorkArea(_targetWindow, out OverlayPixelRect workArea)
                    ? workArea
                    : ToOverlayRect(identity.Bounds);
        return CalculatePlacement(
            area,
            GetWidthDip(),
            GetHeightDip(),
            GetCurrentDpi(),
            marginDip: GetPresetMarginDip(GetCurrentDpi()),
            horizontalPositionPercent: _horizontalPositionPercent);
    }

    private static double GetPresetMarginDip(uint dpi)
    {
        double scale = Math.Max(1, dpi) / 96d;
        return 2d / scale;
    }

    private bool TryResolveMonitorIdentity(
        bool forceRefresh,
        out OverlayMonitorIdentity identity)
    {
        identity = default;
        nint handle = _targetWindow != nint.Zero ? _targetWindow : _source?.Handle ?? nint.Zero;
        if (!forceRefresh && _hasCachedMonitorIdentity && _cachedIdentityWindow == handle)
        {
            identity = _cachedMonitorIdentity;
            return true;
        }

        if (!_monitorIdentityResolver.TryResolveForWindow(handle, out identity))
        {
            _hasCachedMonitorIdentity = false;
            return false;
        }

        _cachedIdentityWindow = handle;
        _cachedMonitorIdentity = identity;
        _hasCachedMonitorIdentity = true;
        return true;
    }

    private void InvalidateMonitorCache() => _hasCachedMonitorIdentity = false;

    private uint GetCurrentDpi()
    {
        // The target game may be DPI-unaware and would report 96 even on a
        // high-DPI monitor. The overlay is PerMonitorV2, so its own HWND is the
        // authoritative rendered DPI. A first cross-monitor move is corrected
        // by the ensuing WM_DPICHANGED render pass.
        nint handle = _source?.Handle ?? nint.Zero;
        uint dpi = handle != nint.Zero ? GetDpiForWindow(handle) : 96;
        return dpi == 0 ? 96 : dpi;
    }

    private double GetWidthDip() =>
        double.IsFinite(ActualWidth) && ActualWidth > 0
            ? ActualWidth
            : double.IsFinite(Width) && Width > 0
                ? Width
                : Math.Max(1, OverlayGrid.DesiredSize.Width);

    private double GetHeightDip() =>
        double.IsFinite(ActualHeight) && ActualHeight > 0
            ? ActualHeight
            : double.IsFinite(Height) && Height > 0
                ? Height
                : Math.Max(1, OverlayGrid.DesiredSize.Height);

    private static OverlayPixelRect ToOverlayRect(ScreenPixelBounds bounds) =>
        new(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);

    private static bool TryGetWorkArea(nint windowHandle, out OverlayPixelRect area)
    {
        area = default;
        nint monitor = MonitorFromWindow(
            windowHandle,
            windowHandle == nint.Zero ? MonitorDefaultToPrimary : MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        area = new OverlayPixelRect(
            info.WorkArea.Left,
            info.WorkArea.Top,
            info.WorkArea.Right,
            info.WorkArea.Bottom);
        return area.Width > 0 && area.Height > 0;
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
