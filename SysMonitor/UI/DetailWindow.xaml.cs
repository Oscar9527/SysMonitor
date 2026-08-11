using System.ComponentModel;
using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SysMonitor.Models;
using SysMonitor.Services;
using Border = System.Windows.Controls.Border;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using Grid = System.Windows.Controls.Grid;
using ProgressBar = System.Windows.Controls.ProgressBar;
using RowDefinition = System.Windows.Controls.RowDefinition;
using TextBlock = System.Windows.Controls.TextBlock;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using TextTrimming = System.Windows.TextTrimming;
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
    private readonly Dictionary<string, DriveRowElements> _driveRows =
        new(StringComparer.OrdinalIgnoreCase);
    private ImmutableArray<string> _driveOrder = ImmutableArray<string>.Empty;
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

        UpdateDrives(snapshot.FixedDrives);
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
        StorageLabelText.Text = localization.GetString("DetailStorage");
        NoDrivesText.Text = localization.GetString("NoFixedDrives");
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
            DriveScrollViewer,
            localization.GetString("DetailStorage"));
        MinimizeButton.ToolTip = localization.GetString("MinimizeTooltip");
        CloseButton.ToolTip = localization.GetString("CloseTooltip");
        System.Windows.Automation.AutomationProperties.SetName(
            MinimizeButton,
            localization.GetString("MinimizeTooltip"));
        System.Windows.Automation.AutomationProperties.SetName(
            CloseButton,
            localization.GetString("CloseTooltip"));
        UpdatePinTooltip();
        UpdateDrives(_latestSnapshot.FixedDrives);
    }

    private void UpdateDrives(ImmutableArray<DriveSnapshot> drives)
    {
        if (drives.IsDefault)
        {
            drives = ImmutableArray<DriveSnapshot>.Empty;
        }

        ImmutableArray<string> nextOrder = drives.Select(drive => drive.Name).ToImmutableArray();
        bool collectionChanged = !_driveOrder.SequenceEqual(
            nextOrder,
            StringComparer.OrdinalIgnoreCase);
        if (collectionChanged)
        {
            var present = nextOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string removed in _driveRows.Keys.Where(name => !present.Contains(name)).ToArray())
            {
                _driveRows.Remove(removed);
            }

            DriveRowsPanel.Children.Clear();
            foreach (string name in nextOrder)
            {
                if (!_driveRows.TryGetValue(name, out DriveRowElements? row))
                {
                    row = CreateDriveRow();
                    _driveRows.Add(name, row);
                }

                DriveRowsPanel.Children.Add(row.Container);
            }

            _driveOrder = nextOrder;
        }

        NoDrivesText.Visibility = drives.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DriveScrollViewer.Visibility = drives.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (DriveSnapshot drive in drives)
        {
            if (!_driveRows.TryGetValue(drive.Name, out DriveRowElements? row))
            {
                continue;
            }

            string marker = drive.IsSystemDrive
                ? LocalizationService.Current.GetString("DriveSystemMarker")
                : string.Empty;
            string title = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                ? drive.Name + marker
                : LocalizationService.Current.Format(
                    "DriveNameWithLabel",
                    drive.Name,
                    drive.VolumeLabel.Trim(),
                    marker);
            double usage = ClampPercent(drive.UsagePercent);
            Brush driveBrush = SelectBrush(usage, CpuBrush);
            row.Name.Text = title;
            row.Name.ToolTip = title;
            row.Details.Text = LocalizationService.Current.Format(
                "DriveUsageDetails",
                FormatGigabytes(drive.UsedBytes),
                FormatGigabytes(drive.TotalBytes));
            row.Value.Text = FormatPercent(usage);
            row.Value.Foreground = driveBrush;
            row.Progress.Value = usage;
            row.Progress.Foreground = driveBrush;
            System.Windows.Automation.AutomationProperties.SetName(row.Progress, title);
        }
    }

    private DriveRowElements CreateDriveRow()
    {
        var container = new Border
        {
            Height = 58,
            Padding = new Thickness(0, 4, 0, 7),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });

        var name = new TextBlock
        {
            Margin = new Thickness(0, 0, 12, 0),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var value = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
        };
        Grid.SetColumn(value, 1);

        var details = new TextBlock
        {
            Margin = new Thickness(0, 2, 12, 4),
            FontSize = 10.5,
        };
        details.SetResourceReference(ForegroundProperty, "AppSecondaryTextBrush");
        Grid.SetRow(details, 1);
        Grid.SetColumnSpan(details, 2);

        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
        };
        progress.SetResourceReference(StyleProperty, "MetricProgressStyle");
        Grid.SetRow(progress, 2);
        Grid.SetColumnSpan(progress, 2);

        grid.Children.Add(name);
        grid.Children.Add(value);
        grid.Children.Add(details);
        grid.Children.Add(progress);
        container.Child = grid;
        return new DriveRowElements(container, name, details, value, progress);
    }

    private void DetailWindow_Loaded(object sender, RoutedEventArgs e) => ConstrainToWorkArea();

    private void DetailWindow_Activated(object? sender, EventArgs e) => ConstrainToWorkArea();

    private void DetailWindow_LocationChanged(object? sender, EventArgs e) =>
        ConstrainToWorkArea();

    private void DetailWindow_DpiChanged(object sender, System.Windows.DpiChangedEventArgs e) =>
        ConstrainToWorkArea();

    private void ConstrainToWorkArea()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        System.Drawing.Rectangle workingArea =
            System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        HwndSource? source = HwndSource.FromHwnd(handle);
        double deviceToDipY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1d;
        double availableHeight = Math.Max(1d, workingArea.Height * deviceToDipY - 16d);
        MaxHeight = availableHeight;
        Height = Math.Min(700d, availableHeight);
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

    private sealed record DriveRowElements(
        Border Container,
        TextBlock Name,
        TextBlock Details,
        TextBlock Value,
        ProgressBar Progress);
}
