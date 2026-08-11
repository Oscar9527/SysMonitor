using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using SysMonitor.Models;
using SysMonitor.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SysMonitor.UI;

public partial class DetailWindow : Window
{
    private static readonly Brush CpuBrush = CreateFrozenBrush("#007AFF");
    private static readonly Brush MemoryBrush = CreateFrozenBrush("#AF52DE");
    private static readonly Brush GpuBrush = CreateFrozenBrush("#34C759");
    private static readonly Brush WarningBrush = CreateFrozenBrush("#FF9500");
    private static readonly Brush CriticalBrush = CreateFrozenBrush("#FF3B30");
    private static readonly Brush PinnedBackgroundBrush = CreateFrozenBrush("#EAF3FF");
    private static readonly Brush PinnedForegroundBrush = CreateFrozenBrush("#007AFF");
    private static readonly Brush UnpinnedForegroundBrush = CreateFrozenBrush("#6E6E73");

    private MonitorSnapshot _latestSnapshot = MonitorSnapshot.Empty;
    private bool _allowClose;
    private bool _isPinned;

    public DetailWindow()
    {
        InitializeComponent();
        Closing += DetailWindow_Closing;
        Closed += DetailWindow_Closed;
        LocalizationService.Current.CultureChanged += OnCultureChanged;
        RefreshLocalizedText();
        UpdateSnapshot(MonitorSnapshot.Empty);
        SetPinned(false);
    }

    public bool IsPinned => _isPinned;

    public event EventHandler? PinChanged;
    public event EventHandler? HideRequested;

    public void UpdateSnapshot(MonitorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => UpdateSnapshot(snapshot));
            return;
        }

        _latestSnapshot = snapshot;
        UpdateMetric(snapshot.CpuUsagePercent, CpuBrush, CpuValueText, CpuProgress);
        CpuDetailsText.Text = BuildCpuDetails(
            snapshot.LogicalProcessorCount,
            snapshot.CpuTemperatureCelsius);

        UpdateMetric(snapshot.MemoryUsagePercent, MemoryBrush, MemoryValueText, MemoryProgress);
        MemoryDetailsText.Text = string.Format(
            LocalizationService.Current.ActiveCulture,
            "{0} / {1} GB",
            FormatGigabytes(snapshot.MemoryUsedBytes),
            FormatGigabytes(snapshot.MemoryTotalBytes));

        if (snapshot.Gpu is { } gpu)
        {
            GpuCard.Visibility = Visibility.Visible;
            UpdateOptionalMetric(gpu.UsagePercent, GpuBrush, GpuValueText, GpuProgress);
            GpuNameText.Text = string.IsNullOrWhiteSpace(gpu.Name)
                ? LocalizationService.Current.GetString("GpuFallbackName")
                : gpu.Name.Trim();
            GpuDetailsText.Text = BuildGpuDetails(gpu);
        }
        else
        {
            GpuCard.Visibility = Visibility.Collapsed;
        }

        DownloadValueText.Text = FormatRate(snapshot.DownloadBytesPerSecond);
        UploadValueText.Text = FormatRate(snapshot.UploadBytesPerSecond);

        double driveUsage = ClampPercent(snapshot.SystemDriveUsagePercent);
        DriveNameText.Text = string.IsNullOrWhiteSpace(snapshot.SystemDriveName)
            ? LocalizationService.Current.GetString("SystemDisk")
            : LocalizationService.Current.Format("SystemDiskNamed", snapshot.SystemDriveName.Trim());
        DriveValueText.Text = FormatPercent(driveUsage);
        DriveProgress.Value = driveUsage;

        Brush driveBrush = SelectBrush(driveUsage, CpuBrush);
        DriveValueText.Foreground = driveBrush;
        DriveProgress.Foreground = driveBrush;
    }

    public void SetPinned(bool isPinned)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetPinned(isPinned));
            return;
        }

        bool changed = _isPinned != isPinned;
        _isPinned = isPinned;
        Topmost = isPinned;
        PinButton.Background = isPinned ? PinnedBackgroundBrush : Brushes.Transparent;
        PinIcon.Fill = isPinned ? PinnedForegroundBrush : UnpinnedForegroundBrush;
        UpdatePinTooltip();

        if (changed)
        {
            PinChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ForceClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(ForceClose);
            return;
        }

        _allowClose = true;
        Close();
    }

    internal void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Current;
        ProcessorLabelText.Text = localization.GetString("DetailProcessor");
        MemoryLabelText.Text = localization.GetString("DetailMemory");
        GraphicsLabelText.Text = localization.GetString("DetailGraphics");
        DownloadLabelText.Text = localization.GetString("DetailDownload");
        UploadLabelText.Text = localization.GetString("DetailUpload");
        System.Windows.Automation.AutomationProperties.SetName(
            CpuProgress,
            localization.GetString("DetailProcessor"));
        System.Windows.Automation.AutomationProperties.SetName(
            MemoryProgress,
            localization.GetString("DetailMemory"));
        System.Windows.Automation.AutomationProperties.SetName(
            GpuProgress,
            localization.GetString("DetailGraphics"));
        System.Windows.Automation.AutomationProperties.SetName(
            DriveProgress,
            localization.GetString("SystemDisk"));
        MinimizeButton.ToolTip = localization.GetString("MinimizeTooltip");
        CloseButton.ToolTip = localization.GetString("CloseTooltip");
        System.Windows.Automation.AutomationProperties.SetName(
            MinimizeButton,
            localization.GetString("MinimizeTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(
            CloseButton,
            localization.GetString("CloseTooltip"));
        UpdatePinTooltip();
    }

    internal static string BuildCpuDetails(int logicalProcessorCount, double? temperature)
    {
        LocalizationService localization = LocalizationService.Current;
        string processorText = logicalProcessorCount > 0
            ? localization.Format("CpuLogicalProcessors", logicalProcessorCount)
            : localization.GetString("CpuLogicalProcessorsUnavailable");
        return IsFinite(temperature)
            ? $"{processorText} · {FormatTemperature(temperature!.Value)}"
            : processorText;
    }

    internal static string BuildGpuDetails(GpuSnapshot gpu)
    {
        LocalizationService localization = LocalizationService.Current;
        var details = new List<string>();
        if (IsFinite(gpu.TemperatureCelsius))
        {
            details.Add(FormatTemperature(gpu.TemperatureCelsius!.Value));
        }

        if (gpu.MemoryTotalBytes is { } total && total > 0)
        {
            string used = gpu.MemoryUsedBytes is { } memoryUsed && memoryUsed >= 0
                ? FormatGigabytes(memoryUsed)
                : "--";
            details.Add(localization.Format(
                "GpuVramUsage",
                used,
                FormatGigabytes(total)));
        }
        else if (gpu.MemoryUsedBytes is { } allocated && allocated >= 0)
        {
            details.Add(localization.Format(
                "GpuDedicatedAllocated",
                FormatGigabytes(allocated)));
        }

        return string.Join(" · ", details);
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => OnCultureChanged(sender, e));
            return;
        }

        RefreshLocalizedText();
        UpdateSnapshot(_latestSnapshot);
    }

    private void UpdatePinTooltip()
    {
        string tooltip = LocalizationService.Current.GetString(
            _isPinned ? "PinDisableTooltip" : "PinEnableTooltip");
        PinButton.ToolTip = tooltip;
        System.Windows.Automation.AutomationProperties.SetName(PinButton, tooltip);
    }

    private static void UpdateMetric(
        double rawValue,
        Brush normalBrush,
        System.Windows.Controls.TextBlock valueText,
        System.Windows.Controls.ProgressBar progress)
    {
        double value = ClampPercent(rawValue);
        Brush brush = SelectBrush(value, normalBrush);
        valueText.Text = FormatPercent(value);
        valueText.Foreground = brush;
        progress.Value = value;
        progress.Foreground = brush;
    }

    private static void UpdateOptionalMetric(
        double? rawValue,
        Brush normalBrush,
        System.Windows.Controls.TextBlock valueText,
        System.Windows.Controls.ProgressBar progress)
    {
        if (IsFinite(rawValue))
        {
            UpdateMetric(rawValue!.Value, normalBrush, valueText, progress);
            return;
        }

        valueText.Text = "--%";
        valueText.Foreground = normalBrush;
        progress.Value = 0d;
        progress.Foreground = normalBrush;
    }

    private static string FormatGigabytes(long bytes)
    {
        double gigabytes = Math.Max(0L, bytes) / (1024d * 1024d * 1024d);
        return gigabytes.ToString("0.0", LocalizationService.Current.ActiveCulture);
    }

    private static string FormatRate(double bytesPerSecond)
    {
        double value = IsFinite(bytesPerSecond) && bytesPerSecond > 0 ? bytesPerSecond : 0d;
        CultureInfo culture = LocalizationService.Current.ActiveCulture;
        if (value < 1024d)
        {
            return string.Format(culture, "{0:0} B/s", value);
        }

        if (value < 1024d * 1024d)
        {
            return string.Format(culture, "{0:0.0} KB/s", value / 1024d);
        }

        if (value < 1024d * 1024d * 1024d)
        {
            return string.Format(culture, "{0:0.0} MB/s", value / (1024d * 1024d));
        }

        return string.Format(culture, "{0:0.0} GB/s", value / (1024d * 1024d * 1024d));
    }

    private static string FormatTemperature(double temperature) =>
        string.Format(LocalizationService.Current.ActiveCulture, "{0:0}°C", temperature);

    private static string FormatPercent(double value) =>
        string.Format(LocalizationService.Current.ActiveCulture, "{0:0}%", value);

    private static double ClampPercent(double value) =>
        IsFinite(value) ? Math.Clamp(value, 0d, 100d) : 0d;

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsFinite(double? value) => value.HasValue && IsFinite(value.Value);

    private static Brush SelectBrush(double value, Brush normalBrush)
    {
        if (value >= 90d)
        {
            return CriticalBrush;
        }

        return value >= 75d ? WarningBrush : normalBrush;
    }

    private static Brush CreateFrozenBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e) => SetPinned(!_isPinned);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button can be released between the event and DragMove.
        }
    }

    private void DetailWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        HideRequested?.Invoke(this, EventArgs.Empty);
        Hide();
    }

    private void DetailWindow_Closed(object? sender, EventArgs e) =>
        LocalizationService.Current.CultureChanged -= OnCultureChanged;
}
