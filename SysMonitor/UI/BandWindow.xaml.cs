using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using Microsoft.Win32;
using SysMonitor.Models;
using SysMonitor.Services;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;

namespace SysMonitor.UI;

public partial class BandWindow : Window
{
    private const int GwlExStyle = -20;
    private const int GwlStyle = -16;
    private const uint GwOwner = 4;
    private const long WsChild = 0x40000000L;
    private const long WsClipSiblings = 0x04000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsSysMenu = 0x00080000L;
    private const long WsMinimizeBox = 0x00020000L;
    private const long WsMaximizeBox = 0x00010000L;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExWindowEdge = 0x00000100L;
    private const long WsExClientEdge = 0x00000200L;
    private const long WsExDlgModalFrame = 0x00000001L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoSendChanging = 0x0400;
    private static readonly nint HwndTop = nint.Zero;
    private static readonly nint HwndNotTopmost = new(-2);
    private const int WmMouseActivate = 0x0021;
    private const int WmShowWindow = 0x0018;
    private const int WmDestroy = 0x0002;
    private const int WmLeftButtonDown = 0x0201;
    private const int WmDpiChanged = 0x02E0;
    private const int WmNcDestroy = 0x0082;
    private const int MaNoActivate = 3;
    private readonly TaskbarMotionTracker _motionTracker;
    private readonly TaskbarRegionMonitor _regionMonitor;
    private readonly DispatcherTimer _layoutRetryTimer;
    private SolidColorBrush _mainTextBrush = CreateBrush(Colors.White);
    private SolidColorBrush _separatorBrush = CreateBrush(ColorFrom("#66FFFFFF"));
    private SolidColorBrush _warningBrush = CreateBrush(ColorFrom("#FFB340"));
    private SolidColorBrush _criticalBrush = CreateBrush(ColorFrom("#FF6961"));
    private ResolvedTheme? _activeTheme;
    private bool _positionTracking;
    private bool _highContrast;
    private bool _systemUsesLightTheme;
    private double? _horizontalPositionPercent;
    private double _itemSpacingDip = 10;
    private double _legacyHorizontalOffsetDip;
    private BandMetricVisibility _metricVisibility = BandMetricVisibility.All;
    private readonly GpuCapabilityStabilizer _gpuCapability = new();
    private EffectiveBandLayout? _effectiveLayout;
    private TaskbarRegionSnapshot? _regionSnapshot;
    private readonly BandClickDebouncer _clickDebouncer = new(
        TimeSpan.FromMilliseconds(350),
        Stopwatch.Frequency);
    private int _toggleGeneration;
    private HwndSource? _source;
    private nint _attachedTaskbar;
    private nint _attachedBandDpiContext;
    private nint _attachedTaskbarDpiContext;
    private nint _attachedThreadDpiContext;
    private NativeIntegritySnapshot? _lastIntegritySnapshot;
    private bool _explicitClose;
    private bool _nativeDestroyedNotified;
    private bool _dpiRepositionPending;
    private bool _degraded;
    private bool _safetyParked;
    private bool _placementInvalidated = true;
    private bool _constraintExpansionPending;
    private bool _constraintConfirmationInFlight;
    private long _constraintConfirmationBlockedThroughGeneration = long.MinValue;
    private int _retryDelayMilliseconds = 150;
    private nint _nativeHandle;

    public BandWindow(long generation)
    {
        Generation = generation;
        InitializeComponent();
        _regionMonitor = new TaskbarRegionMonitor(Dispatcher, OnRegionSnapshotAvailable);
        _motionTracker = new TaskbarMotionTracker(
            Dispatcher,
            Reposition,
            _regionMonitor.RequestProbe,
            RequestRecoveryRegionProbe,
            CheckAttachedWindowHealth);
        _layoutRetryTimer = new DispatcherTimer(
            DispatcherPriority.Render,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _layoutRetryTimer.Tick += OnLayoutRetryTimerTick;
        SourceInitialized += OnSourceInitialized;
        ApplySystemTheme();
        UpdateSnapshot(MonitorSnapshot.Empty);
    }

    public event EventHandler? ToggleDetailsRequested;
    public event EventHandler<BandNativeDestroyedEventArgs>? NativeDestroyed;
    public event EventHandler<double>? HorizontalPositionResolved;

    internal static bool IsToggleMessage(int message) => message == WmLeftButtonDown;

    public void ApplyTheme(ResolvedTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => ApplyTheme(theme));
            return;
        }

        _activeTheme = theme;
        BandRoot.SetResourceReference(Border.BackgroundProperty, "BandBackgroundBrush");
        BandRoot.CornerRadius = new CornerRadius(theme.Definition.Band.CornerRadius);
        ApplySystemTheme();
    }

    public long Generation { get; }

    public nint NativeHandle => _nativeHandle;

    public bool IsNativeWindowAlive =>
        Dispatcher.CheckAccess() && TaskbarPositioner.IsNativeWindowAlive(this);

    public void ApplyAppearance(BandAppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => ApplyAppearance(appearance));
            return;
        }

        string familyName = string.IsNullOrWhiteSpace(appearance.FontFamily)
            ? "Segoe UI"
            : appearance.FontFamily.Trim();
        double valueSize = double.IsFinite(appearance.FontSize)
            ? Math.Clamp(Math.Round(appearance.FontSize), 9, 20)
            : 13;
        double labelSize = Math.Max(8, valueSize - 3);

        MediaFontFamily fontFamily;
        try
        {
            fontFamily = new MediaFontFamily(familyName);
        }
        catch (ArgumentException)
        {
            fontFamily = new MediaFontFamily("Segoe UI");
        }

        var valueBlocks = new HashSet<TextBlock>
        {
            CpuValueText,
            MemoryValueText,
            GpuValueText,
            DownloadValueText,
            UploadValueText,
            DiskValueText
        };

        foreach (TextBlock textBlock in EnumerateTextBlocks(BandRoot))
        {
            textBlock.FontFamily = fontFamily;
            textBlock.FontSize = valueBlocks.Contains(textBlock) ? valueSize : labelSize;
        }

        double? priorPosition = _horizontalPositionPercent;
        double priorLegacyOffset = _legacyHorizontalOffsetDip;
        _horizontalPositionPercent =
            appearance.HorizontalPositionPercent is double position &&
            double.IsFinite(position)
                ? Math.Clamp(position, 0, 100)
                : null;
        _itemSpacingDip = double.IsFinite(appearance.ItemSpacingDip)
            ? Math.Clamp(
                Math.Round(appearance.ItemSpacingDip, MidpointRounding.AwayFromZero),
                0,
                18)
            : 10;
        _legacyHorizontalOffsetDip = double.IsFinite(appearance.LegacyHorizontalOffsetDip)
            ? appearance.LegacyHorizontalOffsetDip
            : 0;
        _metricVisibility = appearance.EffectiveMetricVisibility;
        bool layoutChanged = ApplyEffectiveLayout(CreateEffectiveLayout());
        bool placementChanged = priorPosition != _horizontalPositionPercent ||
            priorLegacyOffset != _legacyHorizontalOffsetDip;
        if (layoutChanged || placementChanged)
        {
            _placementInvalidated = true;
            TaskbarPositioner.Invalidate();
            if (_positionTracking)
            {
                Reposition();
            }
        }
    }

    public void UpdateSnapshot(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => UpdateSnapshot(snapshot));
            return;
        }

        // CPU
        if (_metricVisibility.CpuUsage)
        {
            CpuValueText.Text = FormatPercent(snapshot.CpuUsagePercent);
            CpuValueText.Foreground = GetUsageBrush(snapshot.CpuUsagePercent);
        }
        else if (_metricVisibility.CpuPower && snapshot.CpuPowerWatts is double cpuPower)
        {
            CpuValueText.Text = $"{cpuPower:0}W";
            CpuValueText.Foreground = _mainTextBrush;
        }
        else
        {
            CpuValueText.Text = FormatPercent(snapshot.CpuUsagePercent);
            CpuValueText.Foreground = GetUsageBrush(snapshot.CpuUsagePercent);
        }

        if (_metricVisibility.CpuTemperature && _metricVisibility.CpuPower)
        {
            string t = snapshot.CpuTemperatureCelsius is double temp ? $"{temp:0}°" : "--°";
            string p = snapshot.CpuPowerWatts is double pow && pow > 0.5 ? $"{pow:0}W" : "--W";
            CpuTemperatureText.Text = $"{t} {p}";
            CpuTemperatureText.Visibility = Visibility.Visible;
        }
        else if (_metricVisibility.CpuTemperature)
        {
            CpuTemperatureText.Text = snapshot.CpuTemperatureCelsius is double cpuTemperature
                ? $"{cpuTemperature:0}°"
                : "--°";
            CpuTemperatureText.Visibility = Visibility.Visible;
        }
        else if (_metricVisibility.CpuPower)
        {
            CpuTemperatureText.Text = snapshot.CpuPowerWatts is double cpuPower && cpuPower > 0.5
                ? $"{cpuPower:0}W"
                : "--W";
            CpuTemperatureText.Visibility = Visibility.Visible;
        }
        else
        {
            CpuTemperatureText.Text = string.Empty;
            CpuTemperatureText.Visibility = Visibility.Collapsed;
        }
        CpuTemperatureText.Foreground = _mainTextBrush;

        // Memory
        double usedGb = snapshot.MemoryUsedBytes / (1024d * 1024d * 1024d);
        if (_metricVisibility.MemoryUsage && _metricVisibility.MemoryUsedCapacity)
        {
            MemoryCapacityText.Text = $"{usedGb:0.0}G";
            MemoryCapacityText.Visibility = Visibility.Visible;
            MemoryValueText.Text = FormatPercent(snapshot.MemoryUsagePercent);
            MemoryValueText.Foreground = GetUsageBrush(snapshot.MemoryUsagePercent);
        }
        else if (_metricVisibility.MemoryUsedCapacity)
        {
            MemoryCapacityText.Text = string.Empty;
            MemoryCapacityText.Visibility = Visibility.Collapsed;
            MemoryValueText.Text = $"{usedGb:0.0}G";
            MemoryValueText.Foreground = GetUsageBrush(snapshot.MemoryUsagePercent);
        }
        else
        {
            MemoryCapacityText.Text = string.Empty;
            MemoryCapacityText.Visibility = Visibility.Collapsed;
            MemoryValueText.Text = FormatPercent(snapshot.MemoryUsagePercent);
            MemoryValueText.Foreground = GetUsageBrush(snapshot.MemoryUsagePercent);
        }
        MemoryCapacityText.Foreground = _mainTextBrush;

        // GPU
        bool gpuCapabilityChanged = _gpuCapability.Observe(snapshot.Gpu is not null);
        if (snapshot.Gpu is { } gpu)
        {
            if (_metricVisibility.GpuUsage)
            {
                if (gpu.UsagePercent is { } gpuUsage && double.IsFinite(gpuUsage))
                {
                    GpuValueText.Text = FormatPercent(gpuUsage);
                    GpuValueText.Foreground = GetUsageBrush(gpuUsage);
                }
                else
                {
                    GpuValueText.Text = "--%";
                    GpuValueText.Foreground = _mainTextBrush;
                }
            }
            else if (_metricVisibility.GpuPower && gpu.PowerWatts is double gpuPower && gpuPower > 0.5)
            {
                GpuValueText.Text = $"{gpuPower:0}W";
                GpuValueText.Foreground = _mainTextBrush;
            }
            else
            {
                GpuValueText.Text = gpu.UsagePercent is { } gpuUsage && double.IsFinite(gpuUsage) ? FormatPercent(gpuUsage) : "--%";
                GpuValueText.Foreground = gpu.UsagePercent is { } gUsage && double.IsFinite(gUsage) ? GetUsageBrush(gUsage) : _mainTextBrush;
            }

            if (_metricVisibility.GpuTemperature && _metricVisibility.GpuPower)
            {
                string t = gpu.TemperatureCelsius is { } gTemp && double.IsFinite(gTemp) ? $"{gTemp:0}°" : "--°";
                string p = gpu.PowerWatts is { } gPow && double.IsFinite(gPow) && gPow > 0.5 ? $"{gPow:0}W" : "--W";
                GpuTemperatureText.Text = $"{t} {p}";
                GpuTemperatureText.Visibility = Visibility.Visible;
            }
            else if (_metricVisibility.GpuTemperature)
            {
                GpuTemperatureText.Text = gpu.TemperatureCelsius is { } gpuTemperature &&
                                          double.IsFinite(gpuTemperature)
                    ? $"{gpuTemperature:0}°"
                    : "--°";
                GpuTemperatureText.Visibility = Visibility.Visible;
            }
            else if (_metricVisibility.GpuPower)
            {
                GpuTemperatureText.Text = gpu.PowerWatts is { } gpuPower && double.IsFinite(gpuPower) && gpuPower > 0.5
                    ? $"{gpuPower:0}W"
                    : "--W";
                GpuTemperatureText.Visibility = Visibility.Visible;
            }
            else
            {
                GpuTemperatureText.Text = string.Empty;
                GpuTemperatureText.Visibility = Visibility.Collapsed;
            }

            string tooltip = gpu.Name;
            if (gpu.TemperatureCelsius is { } tooltipTemperature && double.IsFinite(tooltipTemperature))
            {
                tooltip += $"  {tooltipTemperature:0}°C";
            }
            if (gpu.PowerWatts is { } tooltipPower && double.IsFinite(tooltipPower))
            {
                tooltip += $"  {tooltipPower:0}W";
            }
            GpuValueText.ToolTip = tooltip;
        }
        else
        {
            // Keep the slot stable during the five-sample disappearance grace
            // period, but never leave stale telemetry on screen.
            GpuValueText.Text = "--%";
            GpuValueText.Foreground = _mainTextBrush;
            GpuTemperatureText.Text = "--℃";
            GpuValueText.ToolTip = null;
        }

        DownloadValueText.Text = FormatRate(snapshot.DownloadBytesPerSecond);
        UploadValueText.Text = FormatRate(snapshot.UploadBytesPerSecond);
        DownloadValueText.Foreground = _mainTextBrush;
        UploadValueText.Foreground = _mainTextBrush;

        DiskLabelText.Text = string.IsNullOrWhiteSpace(snapshot.SystemDriveName)
            ? "DISK"
            : snapshot.SystemDriveName.TrimEnd('\\').ToUpperInvariant();
        bool hasSystemDriveTelemetry = snapshot.FixedDrives.Any(drive => drive.IsSystemDrive);
        DiskValueText.Text = hasSystemDriveTelemetry
            ? FormatPercent(snapshot.SystemDriveUsagePercent)
            : "--%";
        DiskValueText.Foreground = hasSystemDriveTelemetry
            ? GetUsageBrush(snapshot.SystemDriveUsagePercent)
            : _mainTextBrush;

        if (gpuCapabilityChanged && _metricVisibility.Gpu &&
            ApplyEffectiveLayout(CreateEffectiveLayout()))
        {
            _placementInvalidated = true;
            if (_positionTracking)
            {
                Reposition();
            }
        }
    }

    public void StartPositionTracking()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(StartPositionTracking);
            return;
        }

        if (_positionTracking)
        {
            Reposition();
            return;
        }

        _ = new WindowInteropHelper(this).EnsureHandle();
        _positionTracking = true;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        ApplySystemTheme();
        _regionMonitor.Start();
        _motionTracker.Start();
    }

    public void StopPositionTracking()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(StopPositionTracking);
            return;
        }

        if (!_positionTracking)
        {
            return;
        }

        _positionTracking = false;
        _motionTracker.Stop();
        _regionMonitor.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private EffectiveBandLayout CreateEffectiveLayout()
    {
        bool compact = false;
        bool wide = false;
        if (_regionSnapshot is { } snapshot)
        {
            double scale = (snapshot.TaskbarDpi == 0 ? 96 : snapshot.TaskbarDpi) / 96d;
            double thicknessDip = (snapshot.TaskbarBottom - snapshot.TaskbarTop) / scale;
            compact = thicknessDip <= 30;
            wide = thicknessDip > 40;
        }

        return EffectiveBandLayout.Create(
            _metricVisibility,
            compact,
            wide,
            _gpuCapability.IsCapable,
            _itemSpacingDip);
    }

    private bool ApplyEffectiveLayout(EffectiveBandLayout layout)
    {
        if (_effectiveLayout == layout)
        {
            return false;
        }

        _effectiveLayout = layout;
        var groups = new Dictionary<BandMetric, FrameworkElement>
        {
            [BandMetric.Cpu] = CpuGroup,
            [BandMetric.Memory] = MemoryGroup,
            [BandMetric.Gpu] = GpuGroup,
            [BandMetric.Download] = DownloadGroup,
            [BandMetric.Upload] = UploadGroup,
            [BandMetric.SystemDisk] = DiskGroup
        };
        var precedingSeparators = new Dictionary<BandMetric, FrameworkElement>
        {
            [BandMetric.Memory] = CpuMemorySeparator,
            [BandMetric.Gpu] = MemoryGpuSeparator,
            [BandMetric.Download] = GpuDownloadSeparator,
            [BandMetric.Upload] = DownloadUploadSeparator,
            [BandMetric.SystemDisk] = UploadDiskSeparator
        };
        Thickness margin = new(
            layout.ItemSpacingDip / 2,
            0,
            layout.ItemSpacingDip / 2,
            0);
        foreach ((BandMetric metric, FrameworkElement group) in groups)
        {
            group.Width = layout.SlotWidth(metric);
            group.Margin = margin;
            group.Visibility = layout.IsVisible(metric)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        foreach (FrameworkElement separator in precedingSeparators.Values)
        {
            separator.Visibility = Visibility.Collapsed;
        }

        foreach (BandMetric metric in layout.ActiveGroups.Skip(1))
        {
            precedingSeparators[metric].Visibility = Visibility.Visible;
        }

        CpuTemperatureText.Visibility = layout.Compact
            ? Visibility.Collapsed
            : Visibility.Visible;
        return true;
    }

    public void RequestClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RequestClose);
            return;
        }

        _explicitClose = true;
        Interlocked.Increment(ref _toggleGeneration);
        _layoutRetryTimer.Stop();
        Close();
    }

    public void RequestHealthCheck()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RequestHealthCheck);
            return;
        }

        if (_explicitClose || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_positionTracking)
        {
            CheckAttachedWindowHealth();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        BandDiagnostics.Log(
            $"band WPF closed generation={Generation} hwnd=0x{_nativeHandle.ToInt64():X} " +
            $"explicit={_explicitClose}");
        StopPositionTracking();
        _motionTracker.Dispose();
        _regionMonitor.Dispose();
        _layoutRetryTimer.Stop();
        _layoutRetryTimer.Tick -= OnLayoutRetryTimerTick;
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }

        Interlocked.Increment(ref _toggleGeneration);
        SourceInitialized -= OnSourceInitialized;
        base.OnClosed(e);
        NotifyNativeDestroyed(_nativeHandle, "WPF closed");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        _nativeHandle = handle;
        BandDiagnostics.Log($"band created generation={Generation} hwnd=0x{handle.ToInt64():X}");
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        if (_positionTracking)
        {
            Reposition();
        }
    }

    private nint WndProc(
        nint windowHandle,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmShowWindow)
        {
            BandDiagnostics.Log(
                $"band WM_SHOWWINDOW generation={Generation} hwnd=0x{windowHandle.ToInt64():X} " +
                $"show={wParam != nint.Zero}");
            return nint.Zero;
        }

        if (message == WmDestroy)
        {
            BandDiagnostics.Log(
                $"band WM_DESTROY generation={Generation} hwnd=0x{windowHandle.ToInt64():X}");
            return nint.Zero;
        }

        if (message == WmMouseActivate)
        {
            handled = true;
            return new nint(MaNoActivate);
        }

        if (message == WmDpiChanged)
        {
            ScheduleDpiReposition();
            return nint.Zero;
        }

        if (message == WmNcDestroy)
        {
            BandDiagnostics.Log(
                $"band WM_NCDESTROY generation={Generation} hwnd=0x{windowHandle.ToInt64():X}");
            NotifyNativeDestroyed(windowHandle, "WM_NCDESTROY");
            return nint.Zero;
        }

        if (!IsToggleMessage(message))
        {
            return nint.Zero;
        }

        handled = true;
        long now = Stopwatch.GetTimestamp();
        if (!_clickDebouncer.TryAccept(now))
        {
            BandDiagnostics.Log("band click suppressed by 350ms debounce");
            return nint.Zero;
        }

        int generation = Interlocked.Increment(ref _toggleGeneration);
        BandDiagnostics.Log(
            $"band click accepted generation={Generation} hwnd=0x{windowHandle.ToInt64():X} " +
            $"clickSequence={generation}");
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try
            {
                Dispatcher.InvokeAsync(
                    () =>
                    {
                        if (!_explicitClose &&
                            generation == Volatile.Read(ref _toggleGeneration) &&
                            !Dispatcher.HasShutdownStarted)
                        {
                            BandDiagnostics.Log(
                                $"band toggle dispatched generation={Generation} " +
                                $"hwnd=0x{windowHandle.ToInt64():X} clickSequence={generation}");
                            ToggleDetailsRequested?.Invoke(this, EventArgs.Empty);
                        }
                    },
                    DispatcherPriority.Input);
            }
            catch (InvalidOperationException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }
        return nint.Zero;
    }

    private void ScheduleDpiReposition()
    {
        if (_dpiRepositionPending ||
            _explicitClose ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _dpiRepositionPending = true;
        try
        {
            Dispatcher.InvokeAsync(
                () =>
                {
                    _dpiRepositionPending = false;
                    if (!_explicitClose && !Dispatcher.HasShutdownStarted)
                    {
                        _placementInvalidated = true;
                        Reposition();
                    }
                },
                DispatcherPriority.Render);
        }
        catch (InvalidOperationException)
        {
            _dpiRepositionPending = false;
        }
        catch (TaskCanceledException)
        {
            _dpiRepositionPending = false;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TaskbarPositioner.Invalidate();
                _placementInvalidated = true;
                InvalidateRegionSnapshotAndRequestProbe();
                Reposition();
                _motionTracker.NotifyTaskbarStateChanged();
            });
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.InvokeAsync(() =>
            {
                ApplySystemTheme();
                TaskbarPositioner.Invalidate();
                _placementInvalidated = true;
                InvalidateRegionSnapshotAndRequestProbe();
                Reposition();
                _motionTracker.NotifyTaskbarStateChanged();
            });
        }
    }

    private void Reposition()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero ||
            !TaskbarPositioner.IsWindowHandleAlive(handle) ||
            Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!EnsureTaskbarChild(handle))
        {
            SafetyPark("taskbar attachment unavailable");
            InvalidateRegionSnapshotAndRequestProbe();
            ScheduleRetry("taskbar attachment unavailable");
            return;
        }

        EffectiveBandLayout layout = CreateEffectiveLayout();
        bool layoutChanged = ApplyEffectiveLayout(layout);
        if (!layout.HasVisibleGroups)
        {
            _placementInvalidated = false;
            _layoutRetryTimer.Stop();
            SafetyPark("all band metrics disabled");
            return;
        }

        TaskbarPositionResult result = TaskbarPositioner.Position(
            this,
            _regionSnapshot,
            _horizontalPositionPercent,
            _legacyHorizontalOffsetDip,
            layout.TargetWidthDip,
            _placementInvalidated || layoutChanged);
        _constraintExpansionPending = result.ConstraintConfirmationSuggested;
        if (result.ConstraintConfirmationSuggested &&
            !_constraintConfirmationInFlight &&
            _regionSnapshot is { } confirmationSource &&
            confirmationSource.Generation > _constraintConfirmationBlockedThroughGeneration)
        {
            // One real asynchronous probe may confirm this outward candidate.
            // Its callback blocks this generation from requesting again, even
            // when the second observation changes or fails.
            _constraintConfirmationInFlight = true;
            _regionMonitor.RequestProbe();
        }
        if (!result.NativeParentValid)
        {
            SafetyPark("taskbar snapshot no longer matches native parent");
            InvalidateRegionSnapshotAndRequestProbe();
            EnterDegraded("positioning detected a lost taskbar parent");
            return;
        }

        if (result.ResolvedMigratedPositionPercent is double resolvedPercent)
        {
            _horizontalPositionPercent = resolvedPercent;
            HorizontalPositionResolved?.Invoke(this, resolvedPercent);
        }

        if (result.HideRequested)
        {
            SafetyPark("taskbar safe region unavailable");
        }

        if (result.LayoutValid)
        {
            _placementInvalidated = false;
            if (_safetyParked)
            {
                _safetyParked = false;
                BandDiagnostics.Log($"band recovered from safety parking hwnd=0x{handle.ToInt64():X}");
            }

            if (!IsVisible && !_explicitClose)
            {
                Show();
                BandDiagnostics.Log($"band shown hwnd=0x{handle.ToInt64():X}");
                Dispatcher.InvokeAsync(
                    VerifyFirstShowContract,
                    DispatcherPriority.ApplicationIdle);
            }
        }

        bool confirmationSettledOnCurrentSnapshot =
            _regionSnapshot is { } currentSnapshot &&
            currentSnapshot.Generation <= _constraintConfirmationBlockedThroughGeneration;
        if (result.RetrySuggested &&
            !_constraintExpansionPending &&
            !_constraintConfirmationInFlight &&
            !confirmationSettledOnCurrentSnapshot)
        {
            ScheduleRetry("taskbar layout unavailable");
        }
        else
        {
            _layoutRetryTimer.Stop();
            if (!result.RetrySuggested)
            {
                MarkHealthy();
            }
        }
    }

    private void OnRegionSnapshotAvailable(TaskbarRegionSnapshot snapshot)
    {
        if (_explicitClose || !_positionTracking || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (_regionSnapshot is not null && snapshot.Generation <= _regionSnapshot.Generation)
        {
            return;
        }

        bool completesConstraintConfirmation = _constraintConfirmationInFlight;
        if (completesConstraintConfirmation)
        {
            _constraintConfirmationInFlight = false;
            _constraintConfirmationBlockedThroughGeneration = snapshot.Generation;
        }

        if (!snapshot.IsValid &&
            !snapshot.HasTrustedBounds &&
            _regionSnapshot is { IsValid: true } trustedPrior &&
            snapshot.TaskbarHandle == trustedPrior.TaskbarHandle &&
            (completesConstraintConfirmation || _constraintExpansionPending))
        {
            // A failed real observation terminates confirmation without
            // replacing the last trusted geometry or expanding a boundary.
            TaskbarPositioner.RejectConstraintExpansion(snapshot.Generation);
            _constraintExpansionPending = false;
            _layoutRetryTimer.Stop();
            return;
        }

        // Explorer reduces an auto-hidden taskbar to only a few pixels. That is
        // not a loss of the last trusted icon boundaries: keeping the existing
        // child rectangle lets it move smoothly with its taskbar parent.
        if (!snapshot.IsValid &&
            !snapshot.HasTrustedBounds &&
            _regionSnapshot is { IsValid: true } prior &&
            snapshot.TaskbarHandle == prior.TaskbarHandle)
        {
            return;
        }

        TaskbarRegionSnapshot? previous = _regionSnapshot;
        _regionSnapshot = snapshot;

        // A healthy periodic probe can return the exact same taskbar geometry.
        // Do not run another WPF/native layout pass when nothing positional
        // changed; metric text updates must never trigger window movement.
        if (previous is { IsValid: true } priorValid &&
            snapshot.IsValid &&
            snapshot.TaskbarHandle == priorValid.TaskbarHandle &&
            snapshot.TaskbarLeft == priorValid.TaskbarLeft &&
            snapshot.TaskbarTop == priorValid.TaskbarTop &&
            snapshot.TaskbarRight == priorValid.TaskbarRight &&
            snapshot.TaskbarBottom == priorValid.TaskbarBottom &&
            snapshot.SafeLeft == priorValid.SafeLeft &&
            snapshot.SafeRight == priorValid.SafeRight &&
            snapshot.TaskbarDpi == priorValid.TaskbarDpi &&
            !completesConstraintConfirmation &&
            !_constraintExpansionPending)
        {
            return;
        }

        Reposition();
    }

    private void RequestRecoveryRegionProbe()
    {
        // Layout-change WinEvents request an immediate probe through the normal
        // callback. The periodic recovery probe is only needed while there is
        // no trustworthy layout; repeatedly probing a healthy taskbar can make
        // unstable UIA providers report slightly different icon rectangles.
        if (_regionSnapshot is not { IsValid: true } || _safetyParked || _degraded)
        {
            _regionMonitor.RequestProbe();
        }
    }

    private void InvalidateRegionSnapshotAndRequestProbe()
    {
        _regionSnapshot = null;
        _regionMonitor.RequestProbe();
    }

    private void SafetyPark(string reason)
    {
        if (!IsVisible || _explicitClose || _safetyParked)
        {
            return;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !TaskbarPositioner.IsWindowHandleAlive(handle))
        {
            return;
        }

        // Keep the persistent child HWND visible but fully clipped by its
        // taskbar parent. Recovery is a single SetWindowPos back into the safe
        // region, so no WM_SHOWWINDOW/Hide/Show cycle can flash the Band.
        _ = SetWindowPos(
            handle,
            HwndTop,
            -32768,
            0,
            1,
            1,
            SwpNoActivate | SwpNoZOrder | SwpNoSendChanging);
        _safetyParked = true;
        BandDiagnostics.LogRateLimited(
            "band-safety-parked",
            $"band safety-parked with HWND retained: {reason}",
            TimeSpan.FromSeconds(10));
    }

    private bool EnsureTaskbarChild(nint handle)
    {
        nint taskbarHandle = TaskbarPositioner.FindTaskbarWindow();
        if (taskbarHandle == nint.Zero ||
            !TaskbarPositioner.IsWindowHandleAlive(taskbarHandle))
        {
            EnterDegraded("taskbar unavailable; live hwnd retained");
            return false;
        }

        NativeIntegritySnapshot preAttach = CaptureIntegritySnapshot(handle, taskbarHandle);
        bool initialOwnerless = _attachedTaskbar == nint.Zero &&
            preAttach.Parent == nint.Zero &&
            preAttach.Owner == nint.Zero;
        if (_attachedTaskbar == nint.Zero)
        {
            LogIntegritySnapshot("preattach", preAttach);
        }

        if (!AllDpiContextsEqual(preAttach) ||
            preAttach.BandDpiContext == nint.Zero ||
            preAttach.TaskbarDpiContext == nint.Zero ||
            preAttach.ThreadDpiContext == nint.Zero)
        {
            EnterDegraded("DPI awareness contexts unequal; live hwnd retained");
            return false;
        }

        if (_attachedTaskbar == nint.Zero &&
            !initialOwnerless &&
            preAttach.Parent != taskbarHandle)
        {
            EnterDegraded("initial band HWND is not ownerless; live hwnd retained");
            return false;
        }

        if (_attachedTaskbar == taskbarHandle &&
            IsAttachedContractValid(preAttach, taskbarHandle))
        {
            MarkHealthy();
            return true;
        }

        string operation = _attachedTaskbar == nint.Zero ? "attach" : "repair";
        nint priorTaskbar = _attachedTaskbar;
        if (!TryAttachOrRepairInPlace(handle, taskbarHandle, preAttach, operation))
        {
            return false;
        }

        BandDiagnostics.Log(
            $"band {operation} succeeded generation={Generation} hwnd=0x{handle.ToInt64():X} " +
            $"taskbar=0x{taskbarHandle.ToInt64():X} priorTaskbar=0x{priorTaskbar.ToInt64():X}");
        MarkHealthy();
        return true;
    }

    private bool TryAttachOrRepairInPlace(
        nint handle,
        nint taskbarHandle,
        NativeIntegritySnapshot before,
        string operation)
    {
        _ = SetWindowPos(
            handle,
            HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);

        long topLevelBits =
            WsPopup |
            WsCaption |
            WsThickFrame |
            WsSysMenu |
            WsMinimizeBox |
            WsMaximizeBox;
        long childStyle = (before.Style | WsChild | WsClipSiblings) & ~topLevelBits;
        long conflictingExtendedBits =
            WsExTopmost |
            WsExAppWindow |
            WsExWindowEdge |
            WsExClientEdge |
            WsExDlgModalFrame;
        long childExtendedStyle =
            (before.ExtendedStyle | WsExToolWindow | WsExNoActivate) &
            ~conflictingExtendedBits;
        _ = SetWindowLongPtr(handle, GwlStyle, new nint(childStyle));
        _ = SetWindowLongPtr(handle, GwlExStyle, new nint(childExtendedStyle));

        Marshal.SetLastPInvokeError(0);
        nint previousParent = SetParent(handle, taskbarHandle);
        int setParentError = Marshal.GetLastPInvokeError();
        NativeIntegritySnapshot postParent = CaptureIntegritySnapshot(handle, taskbarHandle);
        LogIntegritySnapshot($"post-SetParent-{operation}", postParent);
        if (postParent.Parent != taskbarHandle ||
            postParent.Owner != nint.Zero ||
            !HasRequiredChildContract(postParent) ||
            !AllDpiContextsEqual(postParent))
        {
            EnterDegraded(
                $"band {operation} SetParent verification failed " +
                $"previous=0x{previousParent.ToInt64():X} error={setParentError}",
                postParent);
            return false;
        }

        _ = SetWindowPos(
            handle,
            HwndTop,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
        NativeIntegritySnapshot postFrame = CaptureIntegritySnapshot(handle, taskbarHandle);
        LogIntegritySnapshot($"post-frame-{operation}", postFrame);
        if (postFrame.Parent != taskbarHandle ||
            postFrame.Owner != nint.Zero ||
            !HasRequiredChildContract(postFrame) ||
            !AllDpiContextsEqual(postFrame) ||
            !HaveSameDpiContexts(postParent, postFrame))
        {
            EnterDegraded($"band {operation} contract failed after frame update", postFrame);
            return false;
        }

        _attachedTaskbar = taskbarHandle;
        _attachedBandDpiContext = postFrame.BandDpiContext;
        _attachedTaskbarDpiContext = postFrame.TaskbarDpiContext;
        _attachedThreadDpiContext = postFrame.ThreadDpiContext;
        return true;
    }

    private void OnLayoutRetryTimerTick(object? sender, EventArgs e)
    {
        if (!_explicitClose && _positionTracking && !Dispatcher.HasShutdownStarted)
        {
            _regionMonitor.RequestProbe();
            Reposition();
        }
        else
        {
            _layoutRetryTimer.Stop();
        }
    }

    private void NotifyNativeDestroyed(nint handle, string source)
    {
        if (_nativeDestroyedNotified)
        {
            return;
        }

        _nativeDestroyedNotified = true;
        StopPositionTracking();
        BandDiagnostics.Log(
            $"band native destruction notified generation={Generation} " +
            $"hwnd=0x{handle.ToInt64():X} source={source} explicit={_explicitClose}");
        NativeDestroyed?.Invoke(this, new BandNativeDestroyedEventArgs(Generation, handle, source));
    }

    private void VerifyFirstShowContract()
    {
        if (_explicitClose ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        nint taskbar = TaskbarPositioner.FindTaskbarWindow();
        NativeIntegritySnapshot snapshot = CaptureIntegritySnapshot(handle, taskbar);
        LogIntegritySnapshot("first-show-idle", snapshot);
        if (!ValidateAttachedContract(handle, taskbar, "first-show-idle", snapshot))
        {
            return;
        }
    }

    private void CheckAttachedWindowHealth()
    {
        if (_explicitClose ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished)
        {
            return;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        nint taskbar = TaskbarPositioner.FindTaskbarWindow();
        NativeIntegritySnapshot snapshot = CaptureIntegritySnapshot(handle, taskbar);
        if (IsAttachedContractValid(snapshot, taskbar))
        {
            MarkHealthy();
            return;
        }

        LogIntegritySnapshot("health", snapshot);
        BandDiagnostics.LogRateLimited(
            $"band-health-invalid-{Generation}",
            $"band integrity invalid at health; attempting in-place repair " +
            $"generation={Generation} hwnd=0x{handle.ToInt64():X}",
            TimeSpan.FromSeconds(2));
        if (EnsureTaskbarChild(handle))
        {
            // A repaired parent/style contract is the exceptional health path
            // that needs placement restored from the latest safe snapshot.
            Reposition();
        }
    }

    private bool ValidateAttachedContract(
        nint handle,
        nint taskbar,
        string checkpoint,
        NativeIntegritySnapshot? captured = null)
    {
        NativeIntegritySnapshot snapshot =
            captured ?? CaptureIntegritySnapshot(handle, taskbar);
        if (checkpoint == "health" &&
            _lastIntegritySnapshot is { } prior &&
            prior != snapshot)
        {
            LogIntegritySnapshot(checkpoint, snapshot);
        }

        bool valid = IsAttachedContractValid(snapshot, taskbar);
        if (!valid)
        {
            BandDiagnostics.Log(
                $"band integrity invalid at {checkpoint}; attempting in-place repair " +
                $"generation={Generation} hwnd=0x{handle.ToInt64():X}");
            return EnsureTaskbarChild(handle);
        }

        MarkHealthy();
        return valid;
    }

    private bool IsAttachedContractValid(NativeIntegritySnapshot snapshot, nint taskbar) =>
        taskbar != nint.Zero &&
        taskbar == _attachedTaskbar &&
        snapshot.Parent == taskbar &&
        snapshot.Owner == nint.Zero &&
        AllDpiContextsEqual(snapshot) &&
        HasRequiredChildContract(snapshot);

    private static bool AllDpiContextsEqual(NativeIntegritySnapshot snapshot) =>
        snapshot.ContextsEqual &&
        snapshot.ThreadDpiContext != nint.Zero &&
        AreDpiAwarenessContextsEqual(snapshot.BandDpiContext, snapshot.ThreadDpiContext);

    private static bool HasRequiredChildContract(NativeIntegritySnapshot snapshot)
    {
        long forbiddenStyle =
            WsPopup |
            WsCaption |
            WsThickFrame |
            WsSysMenu |
            WsMinimizeBox |
            WsMaximizeBox;
        long requiredExtendedStyle = WsExToolWindow | WsExNoActivate;
        return (snapshot.Style & WsChild) != 0 &&
               (snapshot.Style & WsClipSiblings) != 0 &&
               (snapshot.Style & forbiddenStyle) == 0 &&
               (snapshot.ExtendedStyle & requiredExtendedStyle) == requiredExtendedStyle &&
               (snapshot.ExtendedStyle & WsExTopmost) == 0;
    }

    private static bool HaveSameDpiContexts(
        NativeIntegritySnapshot first,
        NativeIntegritySnapshot second) =>
        AreDpiAwarenessContextsEqual(
            first.BandDpiContext,
            second.BandDpiContext) &&
        AreDpiAwarenessContextsEqual(
            first.TaskbarDpiContext,
            second.TaskbarDpiContext) &&
        AreDpiAwarenessContextsEqual(
            first.ThreadDpiContext,
            second.ThreadDpiContext);

    private void EnterDegraded(string reason, NativeIntegritySnapshot? captured = null)
    {
        if (captured is { } snapshot)
        {
            LogIntegritySnapshot("degraded", snapshot);
        }

        _degraded = true;
        BandDiagnostics.LogRateLimited(
            $"band-retry-{Generation}",
            $"band degraded generation={Generation} hwnd=0x{_nativeHandle.ToInt64():X} " +
            $"reason={reason} retryMs={_retryDelayMilliseconds}",
            TimeSpan.FromSeconds(2));
        ScheduleRetry(reason);
    }

    private void ScheduleRetry(string reason)
    {
        _layoutRetryTimer.Interval = TimeSpan.FromMilliseconds(_retryDelayMilliseconds);
        _retryDelayMilliseconds = Math.Min(1000, _retryDelayMilliseconds * 2);
        if (!_layoutRetryTimer.IsEnabled)
        {
            _layoutRetryTimer.Start();
        }

        BandDiagnostics.LogRateLimited(
            $"band-retry-detail-{Generation}",
            $"band retry scheduled generation={Generation} reason={reason} " +
            $"intervalMs={_layoutRetryTimer.Interval.TotalMilliseconds:0}",
            TimeSpan.FromSeconds(2));
    }

    private void MarkHealthy()
    {
        if (_degraded)
        {
            BandDiagnostics.Log(
                $"band recovered generation={Generation} hwnd=0x{_nativeHandle.ToInt64():X} " +
                $"taskbar=0x{_attachedTaskbar.ToInt64():X}");
        }

        _degraded = false;
        _retryDelayMilliseconds = 150;
    }

    private void LogIntegritySnapshot(
        string checkpoint,
        NativeIntegritySnapshot snapshot)
    {
        if (checkpoint == "health" &&
            _lastIntegritySnapshot is { } prior &&
            prior == snapshot)
        {
            return;
        }

        _lastIntegritySnapshot = snapshot;
        BandDiagnostics.Log(
            $"band integrity checkpoint={checkpoint} " +
            $"taskbar=0x{snapshot.Taskbar.ToInt64():X} " +
            $"bandContext=0x{snapshot.BandDpiContext.ToInt64():X} " +
            $"bandAwareness={snapshot.BandAwareness} " +
            $"taskbarContext=0x{snapshot.TaskbarDpiContext.ToInt64():X} " +
            $"taskbarAwareness={snapshot.TaskbarAwareness} " +
            $"contextsEqual={snapshot.ContextsEqual} " +
            $"threadContext=0x{snapshot.ThreadDpiContext.ToInt64():X} " +
            $"threadAwareness={snapshot.ThreadAwareness} " +
            $"parent=0x{snapshot.Parent.ToInt64():X} " +
            $"owner=0x{snapshot.Owner.ToInt64():X} " +
            $"style=0x{snapshot.Style:X} exstyle=0x{snapshot.ExtendedStyle:X} " +
            $"taskbarVisible={snapshot.TaskbarVisible} " +
            $"bandVisible={snapshot.BandVisible}");
    }

    private static NativeIntegritySnapshot CaptureIntegritySnapshot(
        nint handle,
        nint taskbar)
    {
        nint bandContext = handle == nint.Zero
            ? nint.Zero
            : GetWindowDpiAwarenessContext(handle);
        nint taskbarContext = taskbar == nint.Zero
            ? nint.Zero
            : GetWindowDpiAwarenessContext(taskbar);
        nint threadContext = GetThreadDpiAwarenessContext();
        bool contextsEqual =
            bandContext != nint.Zero &&
            taskbarContext != nint.Zero &&
            AreDpiAwarenessContextsEqual(bandContext, taskbarContext);
        return new NativeIntegritySnapshot(
            taskbar,
            bandContext,
            GetAwarenessFromDpiAwarenessContext(bandContext),
            taskbarContext,
            GetAwarenessFromDpiAwarenessContext(taskbarContext),
            contextsEqual,
            threadContext,
            GetAwarenessFromDpiAwarenessContext(threadContext),
            handle == nint.Zero ? nint.Zero : GetParent(handle),
            handle == nint.Zero ? nint.Zero : GetWindow(handle, GwOwner),
            handle == nint.Zero ? 0 : GetWindowLongPtr(handle, GwlStyle).ToInt64(),
            handle == nint.Zero ? 0 : GetWindowLongPtr(handle, GwlExStyle).ToInt64(),
            taskbar != nint.Zero && IsWindowVisible(taskbar),
            handle != nint.Zero && IsWindowVisible(handle));
    }

    private static string FormatPercent(double value) =>
        $"{ClampMetric(value):0}%";

    private static string FormatRate(double bytesPerSecond)
    {
        double value = double.IsFinite(bytesPerSecond) ? Math.Max(0, bytesPerSecond) : 0;
        string[] units = { "B/s", "K/s", "M/s", "G/s" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private MediaBrush GetUsageBrush(double value)
    {
        if (_highContrast)
        {
            return _mainTextBrush;
        }

        value = ClampMetric(value);
        if (value >= 90)
        {
            return _criticalBrush;
        }

        return value >= 75 ? _warningBrush : _mainTextBrush;
    }

    private static double ClampMetric(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    private void ApplySystemTheme()
    {
        _highContrast = SystemParameters.HighContrast;
        _systemUsesLightTheme = ReadSystemUsesLightTheme();

        MediaColor main;
        MediaColor separator;
        MediaColor warning;
        MediaColor critical;
        if (_highContrast)
        {
            MediaColor systemColor = System.Windows.SystemColors.WindowTextColor;
            main = systemColor;
            separator = MediaColor.FromArgb(160, systemColor.R, systemColor.G, systemColor.B);
            warning = systemColor;
            critical = systemColor;
        }
        else if (_systemUsesLightTheme)
        {
            main = ColorFrom("#111111");
            separator = ColorFrom("#44111111");
            warning = ColorFrom("#9A4D00");
            critical = ColorFrom("#B42318");
        }
        else
        {
            main = Colors.White;
            separator = ColorFrom("#66FFFFFF");
            warning = ColorFrom("#FFB340");
            critical = ColorFrom("#FF6961");
        }

        if (_activeTheme is { } theme)
        {
            ThemeBandStyle band = theme.Definition.Band;
            main = string.IsNullOrWhiteSpace(band.TextColor) ? main : ColorFrom(band.TextColor);
            separator = string.IsNullOrWhiteSpace(band.SeparatorColor)
                ? separator
                : ColorFrom(band.SeparatorColor);
            if (!string.Equals(
                    theme.Identity.Id,
                    ThemeCatalogService.DefaultThemeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                warning = ColorFrom(theme.Definition.Metrics.Warning);
                critical = ColorFrom(theme.Definition.Metrics.Critical);
            }
        }

        SetThemeBrushes(main, separator, warning, critical);
    }

    private void SetThemeBrushes(
        MediaColor main,
        MediaColor separator,
        MediaColor warning,
        MediaColor critical)
    {
        _mainTextBrush = CreateBrush(main);
        _separatorBrush = CreateBrush(separator);
        _warningBrush = CreateBrush(warning);
        _criticalBrush = CreateBrush(critical);
        Resources["BandTextBrush"] = _mainTextBrush;
        Resources["BandSeparatorBrush"] = _separatorBrush;
    }

    private static bool ReadSystemUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            return key?.GetValue("SystemUsesLightTheme") is not int value || value != 0;
        }
        catch
        {
            return true;
        }
    }

    private static MediaColor ColorFrom(string value) =>
        (MediaColor)MediaColorConverter.ConvertFromString(value);

    private static IEnumerable<TextBlock> EnumerateTextBlocks(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is TextBlock textBlock)
            {
                yield return textBlock;
            }

            foreach (TextBlock descendant in EnumerateTextBlocks(child))
            {
                yield return descendant;
            }
        }
    }

    private static SolidColorBrush CreateBrush(MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private readonly record struct NativeIntegritySnapshot(
        nint Taskbar,
        nint BandDpiContext,
        int BandAwareness,
        nint TaskbarDpiContext,
        int TaskbarAwareness,
        bool ContextsEqual,
        nint ThreadDpiContext,
        int ThreadAwareness,
        nint Parent,
        nint Owner,
        long Style,
        long ExtendedStyle,
        bool TaskbarVisible,
        bool BandVisible);

    private static nint GetWindowLongPtr(nint windowHandle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(windowHandle, index)
            : new nint(GetWindowLong32(windowHandle, index));

    private static nint SetWindowLongPtr(nint windowHandle, int index, nint value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(windowHandle, index, value)
            : new nint(SetWindowLong32(windowHandle, index, value.ToInt32()));

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, uint command);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint childWindow, nint newParentWindow);

    [DllImport("user32.dll")]
    private static extern nint GetWindowDpiAwarenessContext(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(
        nint firstDpiContext,
        nint secondDpiContext);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

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

public sealed class BandNativeDestroyedEventArgs : EventArgs
{
    public BandNativeDestroyedEventArgs(long generation, nint handle, string source)
    {
        Generation = generation;
        Handle = handle;
        Source = source;
    }

    public long Generation { get; }
    public nint Handle { get; }
    public string Source { get; }
}
