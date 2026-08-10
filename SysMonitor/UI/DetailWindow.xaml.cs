using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using SysMonitor.Models;
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

    private bool _allowClose;
    private bool _isPinned;

    public DetailWindow()
    {
        InitializeComponent();
        Closing += DetailWindow_Closing;
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

        UpdateMetric(
            snapshot.CpuUsagePercent,
            CpuBrush,
            CpuValueText,
            CpuProgress);

        CpuDetailsText.Text = BuildCpuDetails(
            snapshot.LogicalProcessorCount,
            snapshot.CpuTemperatureCelsius);

        UpdateMetric(
            snapshot.MemoryUsagePercent,
            MemoryBrush,
            MemoryValueText,
            MemoryProgress);

        MemoryDetailsText.Text = string.Format(
            CultureInfo.CurrentCulture,
            "{0} / {1} GB",
            FormatGigabytes(snapshot.MemoryUsedBytes),
            FormatGigabytes(snapshot.MemoryTotalBytes));

        if (snapshot.Gpu is { } gpu)
        {
            GpuCard.Visibility = Visibility.Visible;
            UpdateOptionalMetric(gpu.UsagePercent, GpuBrush, GpuValueText, GpuProgress);
            GpuNameText.Text = string.IsNullOrWhiteSpace(gpu.Name)
                ? "Graphics adapter"
                : gpu.Name.Trim();
            GpuDetailsText.Text = BuildGpuDetails(gpu);
        }
        else
        {
            GpuCard.Visibility = Visibility.Collapsed;
        }

        DownloadValueText.Text = FormatRate(snapshot.DownloadBytesPerSecond);
        UploadValueText.Text = FormatRate(snapshot.UploadBytesPerSecond);

        var driveUsage = ClampPercent(snapshot.SystemDriveUsagePercent);
        DriveNameText.Text = string.IsNullOrWhiteSpace(snapshot.SystemDriveName)
            ? "System disk"
            : $"{snapshot.SystemDriveName.Trim()}  System disk";
        DriveValueText.Text = FormatPercent(driveUsage);
        DriveProgress.Value = driveUsage;

        var driveBrush = SelectBrush(driveUsage, CpuBrush);
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

        var changed = _isPinned != isPinned;
        _isPinned = isPinned;
        Topmost = isPinned;

        PinButton.Background = isPinned ? PinnedBackgroundBrush : Brushes.Transparent;
        PinIcon.Fill = isPinned ? PinnedForegroundBrush : UnpinnedForegroundBrush;
        PinButton.ToolTip = isPinned ? "Stop keeping on top" : "Keep on top";

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

    private static void UpdateMetric(
        double rawValue,
        Brush normalBrush,
        System.Windows.Controls.TextBlock valueText,
        System.Windows.Controls.ProgressBar progress)
    {
        var value = ClampPercent(rawValue);
        var brush = SelectBrush(value, normalBrush);

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

    private static string BuildCpuDetails(int logicalProcessorCount, double? temperature)
    {
        var processorText = logicalProcessorCount > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                "{0:N0} logical processors",
                logicalProcessorCount)
            : "Logical processors unavailable";

        return IsFinite(temperature)
            ? $"{processorText} · {FormatTemperature(temperature!.Value)}"
            : processorText;
    }

    private static string BuildGpuDetails(GpuSnapshot gpu)
    {
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
            details.Add(string.Format(
                CultureInfo.CurrentCulture,
                "{0} / {1} GB VRAM",
                used,
                FormatGigabytes(total)));
        }
        else if (gpu.MemoryUsedBytes is { } allocated && allocated >= 0)
        {
            details.Add(string.Format(
                CultureInfo.CurrentCulture,
                "{0} GB dedicated allocated",
                FormatGigabytes(allocated)));
        }

        return string.Join(" · ", details);
    }

    private static string FormatGigabytes(long bytes)
    {
        var safeBytes = Math.Max(0L, bytes);
        var gigabytes = safeBytes / (1024d * 1024d * 1024d);
        return gigabytes.ToString("0.0", CultureInfo.CurrentCulture);
    }

    private static string FormatRate(double bytesPerSecond)
    {
        var value = IsFinite(bytesPerSecond) && bytesPerSecond > 0
            ? bytesPerSecond
            : 0d;

        if (value < 1024d)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0} B/s", value);
        }

        if (value < 1024d * 1024d)
        {
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0} KB/s", value / 1024d);
        }

        return string.Format(
            CultureInfo.CurrentCulture,
            "{0:0.0} MB/s",
            value / (1024d * 1024d));
    }

    private static string FormatTemperature(double temperature)
    {
        return string.Format(CultureInfo.CurrentCulture, "{0:0}℃", temperature);
    }

    private static string FormatPercent(double value)
    {
        return string.Format(CultureInfo.CurrentCulture, "{0:0}%", value);
    }

    private static double ClampPercent(double value)
    {
        return IsFinite(value) ? Math.Clamp(value, 0d, 100d) : 0d;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool IsFinite(double? value)
    {
        return value.HasValue && IsFinite(value.Value);
    }

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

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        SetPinned(!_isPinned);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

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
}
