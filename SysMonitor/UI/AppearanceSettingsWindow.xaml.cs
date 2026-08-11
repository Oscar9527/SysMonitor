using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SysMonitor.Models;
using SysMonitor.Services;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfTextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace SysMonitor.UI;

public partial class AppearanceSettingsWindow : Window
{
    private const string DefaultFontFamily = "Segoe UI Variable Text";
    private const string BlankFontFallback = "Segoe UI";
    private const double DefaultFontSize = 13d;
    private const double DefaultItemSpacingDip = 10d;
    private const double DefaultPositionPercent = 100d;
    private readonly DispatcherTimer _previewTimer;
    private readonly string _displayVersion;
    private bool _allowClose;
    private bool _controlsReady;
    private bool _loading;
    private bool _loadingLanguage;
    private bool _showingAppliedStatus;
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
    public event Action<string>? UiCultureChanged;

    public void LoadAppearance(BandAppearanceSettings value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => LoadAppearance(value));
            return;
        }

        LoadAppearanceCore(value, true);
    }

    public void LoadUiCulture(string? culturePreference)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => LoadUiCulture(culturePreference));
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

    public void ForceClose()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(ForceClose);
            return;
        }

        _allowClose = true;
        _previewTimer.Stop();
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
        SetAutomationName(LanguageComboBox, localization.GetString("AppearanceLanguage"));
        SetAutomationName(ApplyButton, localization.GetString("AppearanceApply"));
        SetAutomationName(RestoreDefaultsButton, localization.GetString("AppearanceRestoreDefaults"));
        SetAutomationName(BottomCloseButton, localization.GetString("AppearanceClose"));
        if (_showingAppliedStatus)
        {
            StatusText.Text = localization.GetString("AppearanceApplied");
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
            if (markApplied)
            {
                _lastApplied = new BandAppearanceSettings(
                    NormalizeFontFamily(value.FontFamily),
                    NormalizeFontSize(value.FontSize),
                    value.HorizontalPositionPercent is double position && double.IsFinite(position)
                        ? Math.Clamp(position, 0, 100)
                        : null,
                    NormalizeItemSpacing(value.ItemSpacingDip),
                    value.LegacyHorizontalOffsetDip);
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

    private BandAppearanceSettings ReadControls() =>
        new(
            NormalizeFontFamily(FontFamilyComboBox.Text),
            NormalizeFontSize(FontSizeSlider.Value),
            NormalizePosition(HorizontalOffsetSlider.Value),
            NormalizeItemSpacing(ItemSpacingSlider.Value));

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
        PreviewText.Text = $"CPU 37%{gap}MEM 63%{gap}↓ 1.2 MB/s";
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
        LoadAppearanceCore(value, true);
        _previewTimer.Stop();
        AppearanceApplied?.Invoke(this, value);
        _showingAppliedStatus = true;
        StatusText.Text = LocalizationService.Current.GetString("AppearanceApplied");
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
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        RestoreLastAppliedPreview();
        Hide();
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
            _ = Dispatcher.BeginInvoke(() => OnCultureChanged(sender, e));
            return;
        }

        RefreshLocalizedText();
    }

    private void FontFamilyComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_controlsReady)
        {
            _ = Dispatcher.BeginInvoke(ScheduleLivePreview);
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

    private void AppearanceSettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        RestoreLastAppliedPreview();
        Hide();
    }

    private void AppearanceSettingsWindow_Closed(object? sender, EventArgs e) =>
        LocalizationService.Current.CultureChanged -= OnCultureChanged;
}
