using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SysMonitor.Models;
using SysMonitor.Services;
using MediaFontFamily = System.Windows.Media.FontFamily;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using ThemeOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace SysMonitor.UI;

public partial class AppearanceSettingsWindow : Window
{
    private const string DefaultFontFamily = "Microsoft YaHei UI";
    private const string BlankFontFallback = "Microsoft YaHei UI";
    private const double DefaultFontSize = 13d;
    private const double DefaultItemSpacingDip = 10d;
    private const double DefaultPositionPercent = 100d;
    private readonly DispatcherTimer _previewTimer;
    private readonly string _displayVersion;
    private bool _allowClose;
    private bool _controlsReady;
    private bool _loading;
    private bool _loadingLanguage;
    private bool _loadingTheme;
    private bool _showingAppliedStatus;
    private int _themeImportGeneration;
    private CancellationTokenSource? _themeImportCancellation;
    private IReadOnlyList<ThemeCatalogItem> _themeItems = Array.Empty<ThemeCatalogItem>();
    private string _lastAppliedThemeId = AppSettings.DefaultThemeId;
    private BandAppearanceSettings _lastApplied =
        new(DefaultFontFamily, DefaultFontSize, DefaultPositionPercent, DefaultItemSpacingDip);

    public AppearanceSettingsWindow()
    {
        InitializeComponent();
        Version? assemblyVersion = typeof(AppearanceSettingsWindow).Assembly.GetName().Version;
        _displayVersion = assemblyVersion is null
            ? string.Empty
            : $" · v{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(125)
        };
        _previewTimer.Tick += OnPreviewTimerTick;

        FontFamilyComboBox.ItemsSource = Fonts.SystemFontFamilies
            .Select(font => font.Source)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        FontFamilyComboBox.AddHandler(
            WpfTextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(FontFamilyComboBox_TextChanged));
        Closing += AppearanceSettingsWindow_Closing;
        Closed += AppearanceSettingsWindow_Closed;
        LocalizationService.Current.CultureChanged += OnCultureChanged;

        _controlsReady = true;
        LoadUiCulture(LocalizationService.Current.CulturePreference);
        RefreshLocalizedText();
        LoadAppearanceCore(_lastApplied, true);
    }

    public event EventHandler<BandAppearanceSettings>? AppearanceApplied;
    public event EventHandler<BandAppearanceSettings>? AppearancePreviewChanged;
    public event EventHandler<AppearanceThemeApplyEventArgs>? AppearanceThemeApplied;
    public event Action<string>? ThemePreviewRequested;
    public event Action<ThemeImportResult>? ThemeImported;
    public event Action<string>? UiCultureChanged;
    public Func<string, CancellationToken, Task<ThemeImportResult>>? ThemeImportRequested { get; set; }

    public string SelectedThemeId =>
        (ThemeComboBox.SelectedItem as ThemeCatalogItem)?.Id ?? _lastAppliedThemeId;

    public void LoadAppearance(BandAppearanceSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => LoadAppearance(value));
            return;
        }

        LoadAppearanceCore(value, true);
    }

    public void LoadUiCulture(string? culturePreference)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => LoadUiCulture(culturePreference));
            return;
        }

        string normalized = LocalizationService.NormalizeCulturePreference(culturePreference);
        _loadingLanguage = true;
        try
        {
            LanguageComboBox.SelectedItem = LanguageComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag as string, normalized, StringComparison.Ordinal));
        }
        finally
        {
            _loadingLanguage = false;
        }
    }

    public void LoadThemes(
        IEnumerable<ThemeCatalogItem> themes,
        string? selectedThemeId,
        bool markApplied = true)
    {
        ArgumentNullException.ThrowIfNull(themes);
        if (!Dispatcher.CheckAccess())
        {
            ThemeCatalogItem[] copy = themes.ToArray();
            _ = Dispatcher.InvokeAsync(() => LoadThemes(copy, selectedThemeId, markApplied));
            return;
        }

        _themeItems = themes.Select(item =>
        {
            if (string.Equals(item.Id, ThemeCatalogService.SystemThemeId, StringComparison.OrdinalIgnoreCase))
            {
                return item with { Name = LocalizationService.Current.GetString("ThemeSystem") };
            }
            if (string.Equals(item.Id, ThemeCatalogService.DefaultThemeId, StringComparison.OrdinalIgnoreCase))
            {
                return item with { Name = LocalizationService.Current.GetString("ThemeDefault") };
            }
            if (string.Equals(item.Id, ThemeCatalogService.MidnightThemeId, StringComparison.OrdinalIgnoreCase))
            {
                return item with { Name = LocalizationService.Current.GetString("ThemeMidnight") };
            }
            return item;
        }).ToArray();

        string requested = string.IsNullOrWhiteSpace(selectedThemeId)
            ? ThemeCatalogService.SystemThemeId
            : selectedThemeId;
        ThemeCatalogItem? selection = _themeItems.FirstOrDefault(item =>
                string.Equals(item.Id, requested, StringComparison.OrdinalIgnoreCase)) ??
            _themeItems.FirstOrDefault(item =>
                string.Equals(item.Id, ThemeCatalogService.SystemThemeId, StringComparison.OrdinalIgnoreCase)) ??
            _themeItems.FirstOrDefault(item =>
                string.Equals(item.Id, ThemeCatalogService.DefaultThemeId, StringComparison.OrdinalIgnoreCase)) ??
            _themeItems.FirstOrDefault();
        _loadingTheme = true;
        try
        {
            ThemeComboBox.ItemsSource = _themeItems;
            ThemeComboBox.SelectedItem = selection;
        }
        finally
        {
            _loadingTheme = false;
        }

        if (markApplied && selection is not null)
        {
            _lastAppliedThemeId = selection.Id;
        }

        UpdateThemeDetails(selection);
    }

    public void ForceClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(ForceClose);
            return;
        }

        _allowClose = true;
        _previewTimer.Stop();
        CancelThemeImport();
        Close();
    }

    internal void RefreshLocalizedText()
    {
        LocalizationService localization = LocalizationService.Current;
        string title = localization.GetString("AppearanceTitle");
        WindowTitleText.Text = title + _displayVersion;
        Title = WindowTitleText.Text;
        FontLabelText.Text = localization.GetString("AppearanceFont");
        FontSizeLabelText.Text = localization.GetString("AppearanceFontSize");
        ItemSpacingLabelText.Text = localization.GetString("AppearanceItemSpacing");
        PositionLabelText.Text = localization.GetString("AppearancePosition");
        PositionHelpText.Text = localization.GetString("AppearancePositionHelp");
        VisibleItemsLabelText.Text = localization.GetString("AppearanceVisibleItems");
        VisibleItemsHelpText.Text = localization.GetString("AppearanceVisibleItemsHelp");
        ThemeLabelText.Text = localization.GetString("AppearanceTheme");
        ImportThemeButton.Content = localization.GetString("AppearanceThemeImport");
        DefaultThemeButton.Content = localization.GetString("AppearanceThemeDefault");
        CpuUsageCheckBox.Content = localization.GetString("MetricCpuUsage") ?? "使用率";
        CpuTemperatureCheckBox.Content = localization.GetString("MetricCpuTemperature") ?? "温度";
        CpuPowerCheckBox.Content = localization.GetString("MetricCpuPower") ?? "功耗";
        GpuUsageCheckBox.Content = localization.GetString("MetricGpuUsage") ?? "使用率";
        GpuTemperatureCheckBox.Content = localization.GetString("MetricGpuTemperature") ?? "温度";
        GpuPowerCheckBox.Content = localization.GetString("MetricGpuPower") ?? "功耗";
        MemoryUsageCheckBox.Content = localization.GetString("MetricMemoryUsage") ?? "使用率";
        MemoryCapacityCheckBox.Content = localization.GetString("MetricMemoryCapacity") ?? "已用容量";
        DownloadVisibilityCheckBox.Content = localization.GetString("AppearanceVisibleDownload") ?? "下载";
        UploadVisibilityCheckBox.Content = localization.GetString("AppearanceVisibleUpload") ?? "上传";
        DiskVisibilityCheckBox.Content = localization.GetString("AppearanceVisibleSystemDisk") ?? "系统盘";
        LanguageLabelText.Text = localization.GetString("AppearanceLanguage");
        PreviewLabelText.Text = localization.GetString("AppearancePreview");
        ApplyButton.Content = localization.GetString("AppearanceApply");
        RestoreDefaultsButton.Content = localization.GetString("AppearanceRestoreDefaults");
        BottomCloseButton.Content = localization.GetString("AppearanceClose");
        string close = localization.GetString("CloseTooltip");
        TitleCloseButton.ToolTip = close;
        System.Windows.Automation.AutomationProperties.SetName(TitleCloseButton, close);
        SystemLanguageItem.Content = localization.GetString("LanguageSystem");
        ChineseLanguageItem.Content = localization.GetString("LanguageSimplifiedChinese");
        EnglishLanguageItem.Content = localization.GetString("LanguageEnglish");
        SetAutomationName(FontFamilyComboBox, localization.GetString("AppearanceFont"));
        SetAutomationName(FontSizeSlider, localization.GetString("AppearanceFontSize"));
        SetAutomationName(ItemSpacingSlider, localization.GetString("AppearanceItemSpacing"));
        SetAutomationName(HorizontalOffsetSlider, localization.GetString("AppearancePosition"));
        SetAutomationName(CpuUsageCheckBox, "CPU Usage");
        SetAutomationName(CpuTemperatureCheckBox, "CPU Temperature");
        SetAutomationName(CpuPowerCheckBox, "CPU Power");
        SetAutomationName(GpuUsageCheckBox, "GPU Usage");
        SetAutomationName(GpuTemperatureCheckBox, "GPU Temperature");
        SetAutomationName(GpuPowerCheckBox, "GPU Power");
        SetAutomationName(MemoryUsageCheckBox, "Memory Usage");
        SetAutomationName(MemoryCapacityCheckBox, "Memory Capacity");
        SetAutomationName(DownloadVisibilityCheckBox, localization.GetString("AppearanceVisibleDownload"));
        SetAutomationName(UploadVisibilityCheckBox, localization.GetString("AppearanceVisibleUpload"));
        SetAutomationName(DiskVisibilityCheckBox, localization.GetString("AppearanceVisibleSystemDisk"));
        SetAutomationName(ThemeComboBox, localization.GetString("AppearanceTheme"));
        SetAutomationName(ImportThemeButton, localization.GetString("AppearanceThemeImport"));
        SetAutomationName(DefaultThemeButton, localization.GetString("AppearanceThemeDefault"));
        SetAutomationName(LanguageComboBox, localization.GetString("AppearanceLanguage"));
        SetAutomationName(ApplyButton, localization.GetString("AppearanceApply"));
        SetAutomationName(RestoreDefaultsButton, localization.GetString("AppearanceRestoreDefaults"));
        SetAutomationName(BottomCloseButton, localization.GetString("AppearanceClose"));
        if (_showingAppliedStatus)
        {
            StatusText.Text = localization.GetString("AppearanceApplied");
        }

        if (_themeItems.Count > 0)
        {
            string? currentSelectedId = SelectedThemeId;
            LoadThemes(_themeItems, currentSelectedId, markApplied: false);
        }

        if (_controlsReady)
        {
            UpdatePreview();
        }
    }

    private void LoadAppearanceCore(BandAppearanceSettings value, bool markApplied)
    {
        _loading = true;
        try
        {
            FontFamilyComboBox.Text = NormalizeFontFamily(value.FontFamily);
            FontSizeSlider.Value = NormalizeFontSize(value.FontSize);
            ItemSpacingSlider.Value = NormalizeItemSpacing(value.ItemSpacingDip);
            HorizontalOffsetSlider.Value = NormalizePosition(value.HorizontalPositionPercent);
            BandMetricVisibility visibility = value.EffectiveMetricVisibility;
            CpuUsageCheckBox.IsChecked = visibility.CpuUsage;
            CpuTemperatureCheckBox.IsChecked = visibility.CpuTemperature;
            CpuPowerCheckBox.IsChecked = visibility.CpuPower;
            GpuUsageCheckBox.IsChecked = visibility.GpuUsage;
            GpuTemperatureCheckBox.IsChecked = visibility.GpuTemperature;
            GpuPowerCheckBox.IsChecked = visibility.GpuPower;
            MemoryUsageCheckBox.IsChecked = visibility.MemoryUsage;
            MemoryCapacityCheckBox.IsChecked = visibility.MemoryUsedCapacity;
            DownloadVisibilityCheckBox.IsChecked = visibility.Download;
            UploadVisibilityCheckBox.IsChecked = visibility.Upload;
            DiskVisibilityCheckBox.IsChecked = visibility.SystemDisk;
            if (markApplied)
            {
                _lastApplied = new BandAppearanceSettings(
                    NormalizeFontFamily(value.FontFamily),
                    NormalizeFontSize(value.FontSize),
                    value.HorizontalPositionPercent is double position && double.IsFinite(position)
                        ? Math.Clamp(position, 0, 100)
                        : null,
                    NormalizeItemSpacing(value.ItemSpacingDip),
                    value.LegacyHorizontalOffsetDip,
                    visibility);
            }

            _showingAppliedStatus = false;
            StatusText.Text = string.Empty;
            UpdatePreview();
        }
        finally
        {
            _loading = false;
        }
    }

    private BandAppearanceSettings ReadControls()
    {
        bool cpuUsage = CpuUsageCheckBox.IsChecked == true;
        bool cpuTemp = CpuTemperatureCheckBox.IsChecked == true;
        bool cpuPower = CpuPowerCheckBox.IsChecked == true;
        bool cpuAny = cpuUsage || cpuTemp || cpuPower;

        bool memUsage = MemoryUsageCheckBox.IsChecked == true;
        bool memCap = MemoryCapacityCheckBox.IsChecked == true;
        bool memAny = memUsage || memCap;

        bool gpuUsage = GpuUsageCheckBox.IsChecked == true;
        bool gpuTemp = GpuTemperatureCheckBox.IsChecked == true;
        bool gpuPower = GpuPowerCheckBox.IsChecked == true;
        bool gpuAny = gpuUsage || gpuTemp || gpuPower;

        bool download = DownloadVisibilityCheckBox.IsChecked == true;
        bool upload = UploadVisibilityCheckBox.IsChecked == true;
        bool disk = DiskVisibilityCheckBox.IsChecked == true;

        return new(
            NormalizeFontFamily(FontFamilyComboBox.Text),
            NormalizeFontSize(FontSizeSlider.Value),
            NormalizePosition(HorizontalOffsetSlider.Value),
            NormalizeItemSpacing(ItemSpacingSlider.Value),
            0,
            new BandMetricVisibility(
                cpuAny,
                memAny,
                gpuAny,
                download,
                upload,
                disk,
                cpuUsage,
                cpuTemp,
                cpuPower,
                memUsage,
                memCap,
                gpuUsage,
                gpuTemp,
                gpuPower));
    }

    private void UpdatePreview()
    {
        BandAppearanceSettings value = ReadControls();
        CultureInfo culture = LocalizationService.Current.ActiveCulture;
        FontSizeValueText.Text = value.FontSize.ToString("0", culture);
        ItemSpacingValueText.Text = $"{value.ItemSpacingDip.ToString("0", culture)} px";
        HorizontalOffsetValueText.Text = FormatPosition(value.HorizontalPositionPercent ?? 100);
        PreviewText.FontSize = value.FontSize;
        int previewSpaces = 1 + (int)Math.Round(value.ItemSpacingDip / 3);
        string gap = new(' ', previewSpaces);
        BandMetricVisibility visibility = value.EffectiveMetricVisibility;
        var previewItems = new List<string>(6);
        if (visibility.Cpu)
        {
            string cpuStr = "CPU";
            if (visibility.CpuTemperature) cpuStr += " 65°";
            if (visibility.CpuPower) cpuStr += " 45W";
            if (visibility.CpuUsage) cpuStr += " 37%";
            previewItems.Add(cpuStr);
        }
        if (visibility.Memory)
        {
            string memStr = "MEM";
            if (visibility.MemoryUsedCapacity) memStr += " 8.2G";
            if (visibility.MemoryUsage) memStr += " 63%";
            previewItems.Add(memStr);
        }
        if (visibility.Gpu)
        {
            string gpuStr = "GPU";
            if (visibility.GpuTemperature) gpuStr += " 58℃";
            if (visibility.GpuPower) gpuStr += " 115W";
            if (visibility.GpuUsage) gpuStr += " 12%";
            previewItems.Add(gpuStr);
        }
        if (visibility.Download) previewItems.Add("↓ 1.2 MB/s");
        if (visibility.Upload) previewItems.Add("↑ 256 KB/s");
        if (visibility.SystemDisk) previewItems.Add("C: 45%");
        PreviewText.Text = previewItems.Count == 0
            ? LocalizationService.Current.GetString("AppearanceVisibleItemsHelp")
            : string.Join(gap, previewItems);
        try
        {
            PreviewText.FontFamily = new MediaFontFamily(value.FontFamily);
        }
        catch (ArgumentException)
        {
            PreviewText.FontFamily = new MediaFontFamily(BlankFontFallback);
        }
    }

    private void ScheduleLivePreview()
    {
        if (!_controlsReady || _loading)
        {
            return;
        }

        _showingAppliedStatus = false;
        StatusText.Text = string.Empty;
        UpdatePreview();
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private void OnPreviewTimerTick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        AppearancePreviewChanged?.Invoke(this, ReadControls());
    }

    private static string NormalizeFontFamily(string? value) =>
        string.IsNullOrWhiteSpace(value) ? BlankFontFallback : value.Trim();

    private static double NormalizeFontSize(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 9, 20)
            : DefaultFontSize;

    private static double NormalizeItemSpacing(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 18)
            : DefaultItemSpacingDip;

    private static double NormalizePosition(double? value) =>
        value is double position && double.IsFinite(position)
            ? Math.Clamp(Math.Round(position, MidpointRounding.AwayFromZero), 0, 100)
            : DefaultPositionPercent;

    private static string FormatPosition(double value)
    {
        LocalizationService localization = LocalizationService.Current;
        string key = value <= 20 ? "PositionLeft" : value >= 80 ? "PositionRight" : "PositionCenter";
        return localization.Format(key, value);
    }

    private static void SetAutomationName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        BandAppearanceSettings value = ReadControls();
        _previewTimer.Stop();
        var request = new AppearanceThemeApplyEventArgs(value, SelectedThemeId);
        AppearanceThemeApplied?.Invoke(this, request);
        if (AppearanceThemeApplied is null)
        {
            AppearanceApplied?.Invoke(this, value);
            request.Accepted = true;
        }

        if (request.Accepted)
        {
            LoadAppearanceCore(value, true);
            _lastAppliedThemeId = SelectedThemeId;
            _showingAppliedStatus = true;
            StatusText.Foreground = FindResource("GpuMetricBrush") as MediaBrush ?? MediaBrushes.Green;
            StatusText.Text = LocalizationService.Current.GetString("AppearanceApplied");
        }
        else
        {
            RestoreLastAppliedPreview();
            StatusText.Foreground = FindResource("CriticalMetricBrush") as MediaBrush ?? MediaBrushes.Red;
            StatusText.Text = request.ErrorMessage ??
                LocalizationService.Current.GetString("AppearanceSaveFailed");
        }
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        LoadAppearanceCore(
            new BandAppearanceSettings(
                DefaultFontFamily,
                DefaultFontSize,
                DefaultPositionPercent,
                DefaultItemSpacingDip),
            false);
        ScheduleLivePreview();
    }

    private void RestoreLastAppliedPreview()
    {
        _previewTimer.Stop();
        LoadAppearanceCore(_lastApplied, false);
        AppearancePreviewChanged?.Invoke(this, _lastApplied);
        SelectTheme(_lastAppliedThemeId, requestPreview: true);
    }

    private void ThemeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ThemeCatalogItem? selected = ThemeComboBox.SelectedItem as ThemeCatalogItem;
        UpdateThemeDetails(selected);
        if (!_loadingTheme && selected is not null)
        {
            _showingAppliedStatus = false;
            StatusText.Text = string.Empty;
            ThemePreviewRequested?.Invoke(selected.Id);
        }
    }

    private async void ImportThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ThemeOpenFileDialog
        {
            Filter = LocalizationService.Current.GetString("ThemePackageFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true || ThemeImportRequested is null)
        {
            return;
        }

        ImportThemeButton.IsEnabled = false;
        StatusText.Foreground = FindResource("AppSecondaryTextBrush") as MediaBrush ?? MediaBrushes.Gray;
        StatusText.Text = LocalizationService.Current.GetString("ThemeImporting");
        var cancellation = new CancellationTokenSource();
        int generation = checked(++_themeImportGeneration);
        _themeImportCancellation = cancellation;
        try
        {
            ThemeImportResult result = await ThemeImportRequested(dialog.FileName, cancellation.Token);
            if (cancellation.IsCancellationRequested || generation != _themeImportGeneration)
            {
                return;
            }

            if (result.Success && result.Theme is not null)
            {
                ThemeImported?.Invoke(result);
                StatusText.Foreground = FindResource("GpuMetricBrush") as MediaBrush ?? MediaBrushes.Green;
                StatusText.Text = LocalizationService.Current.GetString("ThemeImportInstalledNotApplied");
            }
            else
            {
                StatusText.Foreground = FindResource("CriticalMetricBrush") as MediaBrush ?? MediaBrushes.Red;
                StatusText.Text = LocalizationService.Current.Format(
                    "ThemeImportFailed",
                    GetThemeImportErrorText(result.ErrorCode));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            if (cancellation.IsCancellationRequested || generation != _themeImportGeneration)
            {
                return;
            }

            StatusText.Foreground = FindResource("CriticalMetricBrush") as MediaBrush ?? MediaBrushes.Red;
            StatusText.Text = LocalizationService.Current.Format(
                "ThemeImportFailed",
                GetThemeImportErrorText(ThemeImportErrorCode.IoFailure));
        }
        finally
        {
            if (ReferenceEquals(_themeImportCancellation, cancellation))
            {
                _themeImportCancellation = null;
                ImportThemeButton.IsEnabled = true;
            }

            cancellation.Dispose();
        }
    }

    private void DefaultThemeButton_Click(object sender, RoutedEventArgs e) =>
        SelectTheme(ThemeCatalogService.SystemThemeId, requestPreview: true);

    private static string GetThemeImportErrorText(ThemeImportErrorCode errorCode)
    {
        string key = errorCode switch
        {
            ThemeImportErrorCode.Cancelled => "ThemeImportReasonCancelled",
            ThemeImportErrorCode.PackageNotFound => "ThemeImportReasonPackageNotFound",
            ThemeImportErrorCode.InvalidPath => "ThemeImportReasonInvalidPath",
            ThemeImportErrorCode.LimitExceeded => "ThemeImportReasonLimitExceeded",
            ThemeImportErrorCode.InvalidManifest => "ThemeImportReasonInvalidManifest",
            ThemeImportErrorCode.InvalidTheme => "ThemeImportReasonInvalidTheme",
            ThemeImportErrorCode.InvalidAsset => "ThemeImportReasonInvalidAsset",
            ThemeImportErrorCode.IncompatibleVersion => "ThemeImportReasonIncompatibleVersion",
            ThemeImportErrorCode.DuplicateId => "ThemeImportReasonDuplicateId",
            ThemeImportErrorCode.IoFailure => "ThemeImportReasonIoFailure",
            _ => "ThemeImportReasonInvalidPackage"
        };
        return LocalizationService.Current.GetString(key);
    }

    private void SelectTheme(string id, bool requestPreview)
    {
        ThemeCatalogItem? item = _themeItems.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        _loadingTheme = true;
        try
        {
            ThemeComboBox.SelectedItem = item;
        }
        finally
        {
            _loadingTheme = false;
        }

        UpdateThemeDetails(item);
        if (requestPreview)
        {
            ThemePreviewRequested?.Invoke(item.Id);
        }
    }

    private void UpdateThemeDetails(ThemeCatalogItem? item)
    {
        if (item is null)
        {
            ThemeNameText.Text = string.Empty;
            ThemeMetadataText.Text = string.Empty;
            ThemePreviewImage.Source = null;
            return;
        }

        if (string.Equals(item.Id, ThemeCatalogService.SystemThemeId, StringComparison.OrdinalIgnoreCase))
        {
            ThemeNameText.Text = LocalizationService.Current.GetString("ThemeSystem");
            ThemeMetadataText.Text = LocalizationService.Current.GetString("ThemeSystemDescription");
            ThemePreviewImage.Source = null;
            return;
        }

        if (string.Equals(item.Id, ThemeCatalogService.DefaultThemeId, StringComparison.OrdinalIgnoreCase))
        {
            ThemeNameText.Text = LocalizationService.Current.GetString("ThemeDefault");
            ThemeMetadataText.Text = LocalizationService.Current.Format(
                "ThemeMetadata",
                item.Author,
                item.Version);
            ThemePreviewImage.Source = LoadPreview(item.PreviewPath);
            return;
        }

        if (string.Equals(item.Id, ThemeCatalogService.MidnightThemeId, StringComparison.OrdinalIgnoreCase))
        {
            ThemeNameText.Text = LocalizationService.Current.GetString("ThemeMidnight");
            ThemeMetadataText.Text = LocalizationService.Current.Format(
                "ThemeMetadata",
                item.Author,
                item.Version);
            ThemePreviewImage.Source = LoadPreview(item.PreviewPath);
            return;
        }

        ThemeNameText.Text = item.Name;
        ThemeMetadataText.Text = LocalizationService.Current.Format(
            "ThemeMetadata",
            item.Author,
            item.Version);
        ThemePreviewImage.Source = LoadPreview(item.PreviewPath);
    }

    private static BitmapSource? LoadPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 176;
            image.UriSource = new Uri(path!, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CancelThemeImport();
        RestoreLastAppliedPreview();
        Hide();
    }

    private void CancelThemeImport()
    {
        checked { _themeImportGeneration++; }
        _themeImportCancellation?.Cancel();
        _themeImportCancellation = null;
        ImportThemeButton.IsEnabled = true;
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
        }
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLanguage || LanguageComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string culturePreference)
        {
            return;
        }

        UiCultureChanged?.Invoke(LocalizationService.NormalizeCulturePreference(culturePreference));
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(() => OnCultureChanged(sender, e));
            return;
        }

        RefreshLocalizedText();
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controlsReady)
        {
            _ = Dispatcher.InvokeAsync(ScheduleLivePreview);
        }
    }

    private void FontFamilyComboBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ScheduleLivePreview();

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ScheduleLivePreview();

    private void ItemSpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ScheduleLivePreview();

    private void HorizontalOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        ScheduleLivePreview();

    private void VisibilityCheckBox_Changed(object sender, RoutedEventArgs e) =>
        ScheduleLivePreview();

    private void AppearanceSettingsWindow_Loaded(object sender, RoutedEventArgs e) =>
        ConstrainToWorkArea();

    private void AppearanceSettingsWindow_Activated(object? sender, EventArgs e) =>
        ConstrainToWorkArea();

    private void AppearanceSettingsWindow_LocationChanged(object? sender, EventArgs e) =>
        ConstrainToWorkArea();

    private void AppearanceSettingsWindow_DpiChanged(
        object sender,
        System.Windows.DpiChangedEventArgs e) =>
        ConstrainToWorkArea();

    private void ConstrainToWorkArea()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        System.Drawing.Rectangle workingArea =
            System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        HwndSource? source = HwndSource.FromHwnd(handle);
        double deviceToDipX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1d;
        double deviceToDipY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1d;
        double availableHeight = Math.Max(360d, workingArea.Height * deviceToDipY - 16d);
        double availableWidth = Math.Max(400d, workingArea.Width * deviceToDipX - 16d);
        MaxHeight = availableHeight;
        MaxWidth = availableWidth;
        Height = Math.Min(700d, availableHeight);
        Width = Math.Min(580d, availableWidth);
    }

    private void AppearanceSettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        CancelThemeImport();
        RestoreLastAppliedPreview();
        Hide();
    }

    private void AppearanceSettingsWindow_Closed(object? sender, EventArgs e)
    {
        CancelThemeImport();
        LocalizationService.Current.CultureChanged -= OnCultureChanged;
    }
}

public sealed class AppearanceThemeApplyEventArgs : EventArgs
{
    public AppearanceThemeApplyEventArgs(BandAppearanceSettings appearance, string themeId)
    {
        Appearance = appearance;
        ThemeId = themeId;
    }

    public BandAppearanceSettings Appearance { get; }
    public string ThemeId { get; }
    public bool Accepted { get; set; }
    public string? ErrorMessage { get; set; }
}
