using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.UI;

public sealed record LegacyFpsTargetView(
    string DisplayName,
    string? ExecutablePath,
    bool Enabled,
    bool Managed,
    bool CanEnable,
    bool CanDisable,
    RtssCompatibilityCode Code);

public enum GameOverlayPositionChange
{
    None,
    Set,
    Reset
}

public sealed record OverlaySettingsCoordinateContext(
    string StableMonitorId,
    string DisplayName,
    int Left,
    int Top,
    int Right,
    int Bottom,
    int CurrentX,
    int CurrentY,
    bool ExactEnabled,
    int? MinimumX = null,
    int? MaximumX = null,
    int? MinimumY = null,
    int? MaximumY = null);

/// <summary>
/// Immutable data sent to the host while the settings session is being edited.
/// The monitor context is the snapshot captured when the edit was made, so the
/// host can reject a stale preview without displaying a modal dialog.
/// </summary>
public sealed record GameOverlayPreviewRequest(
    string LayoutMode,
    OverlaySettingsCoordinateContext? Monitor,
    bool ExactEnabled,
    int X,
    int Y);

public sealed record GameOverlayConfigurationRequest(
    GameOverlayMetricVisibility Metrics,
    string Sampling,
    string? LegacyExecutablePath,
    bool LegacyEnabled,
    bool LegacyChanged,
    string LayoutMode = "vertical",
    string? PositionMonitorId = null,
    int PositionLeft = 0,
    int PositionTop = 0,
    int PositionRight = 0,
    int PositionBottom = 0,
    GameOverlayPositionChange PositionChange = GameOverlayPositionChange.None,
    int? PositionX = null,
    int? PositionY = null);

public partial class GameOverlaySettingsWindow : Window
{
    private readonly ObservableCollection<MetricItem> _items = [];
    private readonly ObservableCollection<LegacyFpsTargetView> _legacyTargets = [];
    private readonly HudPreviewScheduler _previewScheduler;
    private bool _allowClose;
    private bool _loadingLegacy;
    private bool _loadedLegacyEnabled;
    private bool _loadingCoordinates;
    private bool _coordinateDirty;
    private bool _suppressPreview;
    private bool _previewSessionActive;
    private bool _sessionFinalized = true;
    private OverlaySettingsCoordinateContext? _coordinateContext;
    private string _previewStatus = string.Empty;

    public GameOverlaySettingsWindow()
    {
        InitializeComponent();
        MetricList.ItemsSource = _items;
        LegacyTargetBox.ItemsSource = _legacyTargets;
        _previewScheduler = new HudPreviewScheduler(
            Dispatcher,
            RequestPreviewNow,
            DispatcherPriority.Render);
        Closing += (_, e) =>
        {
            if (_allowClose)
            {
                return;
            }

            e.Cancel = true;
            FinalizeAndHide(committed: false);
        };
        Closed += (_, _) =>
        {
            _previewScheduler.Dispose();
            LocalizationService.Current.CultureChanged -= OnCultureChanged;
        };
        LocalizationService.Current.CultureChanged += OnCultureChanged;
        RefreshLocalizedText();
        LoadLayoutItems("vertical");
        LoadCoordinateContext(null);
    }

    public Func<GameOverlayConfigurationRequest, bool>? ApplyRequested { get; set; }

    /// <summary>Raised when the user clicks to open HUD appearance and skin settings.</summary>
    public Action? AppearanceRequested { get; set; }

    /// <summary>Raised after a valid novice-setting edit should be shown immediately.</summary>
    public Action<GameOverlayPreviewRequest>? PreviewRequested { get; set; }

    /// <summary>Raised once when the current preview session ends (true means Apply succeeded).</summary>
    public Action<bool>? PreviewSessionFinished { get; set; }

    private void AppearanceButton_Click(object sender, RoutedEventArgs e) => AppearanceRequested?.Invoke();

    /// <summary>Inline status supplied by the host for stale monitor or unavailable preview.</summary>
    public string PreviewStatus => _previewStatus;

    /// <summary>
    /// Starts a fresh preview lifecycle. Call this before showing the window for a new open.
    /// It is also called automatically by LoadConfiguration for compatibility with older hosts.
    /// </summary>
    public void BeginPreviewSession()
    {
        _previewScheduler.Cancel();
        _sessionFinalized = false;
        _previewSessionActive = true;
        SetPreviewStatus(_coordinateContext is null ? L("HudPreviewNoMonitor") : string.Empty);
    }

    /// <summary>
    /// Re-arms preview callbacks after a failed Apply was restored or reloaded by the host.
    /// This does not hide the window or mutate the saved configuration.
    /// </summary>
    public void RebasePreviewSession()
    {
        _previewScheduler.Cancel();
        _sessionFinalized = false;
        _previewSessionActive = true;
    }

    public void ResetAfterFailedApply() => RebasePreviewSession();

    public void SetPreviewStatus(string? message)
    {
        _previewStatus = message ?? string.Empty;
        if (PreviewStatusText is not null)
        {
            PreviewStatusText.Text = _previewStatus;
        }
    }

    public void ReportPreviewStatus(string? message) => SetPreviewStatus(message);

    /// <summary>
    /// Reloads the monitor snapshot and current exact-position values after a stale-target rejection.
    /// Reloading clears the pending coordinate mutation; it does not apply anything by itself.
    /// </summary>
    public void ReloadCoordinateContext(OverlaySettingsCoordinateContext? coordinateContext) =>
        LoadCoordinateContext(coordinateContext);

    public void LoadConfiguration(
        GameOverlayMetricVisibility visibility,
        string? sampling,
        IEnumerable<LegacyFpsTargetView> legacyTargets,
        string? preferredLegacyPath,
        string layoutMode = "vertical",
        OverlaySettingsCoordinateContext? coordinateContext = null)
    {
        if (!_previewSessionActive || _sessionFinalized)
        {
            BeginPreviewSession();
        }

        _suppressPreview = true;
        try
        {
            var enabled = new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["fps"] = visibility.FrameRate, ["cpu"] = visibility.Cpu, ["gpu"] = visibility.Gpu,
                ["memory"] = visibility.Memory, ["network"] = visibility.Network
            };
            _items.Clear();
            foreach (string id in GameOverlayMetricOrder.Normalize(visibility.Order))
            {
                _items.Add(new MetricItem(id, NameFor(id), enabled[id]));
            }

            LoadSamplingItems(sampling);
            LoadLayoutItems(layoutMode);
            MetricList.SelectedIndex = _items.Count == 0 ? -1 : 0;

            LoadCoordinateContext(coordinateContext);

            _loadingLegacy = true;
            try
            {
                _legacyTargets.Clear();
                foreach (LegacyFpsTargetView target in legacyTargets)
                {
                    _legacyTargets.Add(target);
                }

                LegacyTargetBox.SelectedItem = _legacyTargets.FirstOrDefault(target =>
                        string.Equals(target.ExecutablePath, preferredLegacyPath, StringComparison.OrdinalIgnoreCase)) ??
                    _legacyTargets.FirstOrDefault();
            }
            finally
            {
                _loadingLegacy = false;
            }

            RefreshLegacySelection();
            RefreshLocalizedText();
        }
        finally
        {
            _suppressPreview = false;
        }
    }

    public void CloseForExit()
    {
        FinalizePreviewSession(committed: false);
        _allowClose = true;
        Close();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        int current = MetricList.SelectedIndex;
        int target = current + delta;
        if (current < 0 || target < 0 || target >= _items.Count) return;
        _items.Move(current, target);
        MetricList.SelectedIndex = target;
    }

    private void LayoutMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressPreview || sender is not System.Windows.Controls.RadioButton { IsChecked: true })
        {
            return;
        }

        RequestPreviewNow();
    }

    private void LegacyTargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingLegacy)
        {
            RefreshLegacySelection();
        }
    }

    private void RefreshLegacySelection()
    {
        LegacyFpsTargetView? target = LegacyTargetBox.SelectedItem as LegacyFpsTargetView;
        _loadingLegacy = true;
        try
        {
            _loadedLegacyEnabled = target?.Enabled == true;
            LegacyCompatibilityBox.IsChecked = _loadedLegacyEnabled;
            LegacyCompatibilityBox.IsEnabled = target is not null &&
                (_loadedLegacyEnabled ? target.CanDisable : target.CanEnable);
        }
        finally
        {
            _loadingLegacy = false;
        }

        LegacyStatusText.Text = target is null
            ? L("HudLegacyNoTarget")
            : target.Code switch
            {
                RtssCompatibilityCode.SameBasenameConflict => L("HudLegacySameNameConflict"),
                RtssCompatibilityCode.Conflict or RtssCompatibilityCode.CorruptManifest or RtssCompatibilityCode.InvalidManifest => L("HudLegacyConflict"),
                RtssCompatibilityCode.ProfilesUnavailable => L("HudLegacyRtssUnavailable"),
                _ when target.Enabled && target.Managed => L("HudLegacyEnabledManaged"),
                _ when target.Enabled => L("HudLegacyEnabledExternal"),
                _ => L("HudLegacyDisabled")
            };
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        bool Enabled(string id) => _items.First(item => item.Id == id).Enabled;
        var metrics = new GameOverlayMetricVisibility(
            Enabled("fps"), Enabled("cpu"), Enabled("gpu"), Enabled("memory"), Enabled("network"))
        {
            Order = _items.Select(item => item.Id).ToArray()
        };
        LegacyFpsTargetView? target = LegacyTargetBox.SelectedItem as LegacyFpsTargetView;
        bool legacyEnabled = LegacyCompatibilityBox.IsChecked == true;
        bool legacyChanged = target is not null && legacyEnabled != _loadedLegacyEnabled;
        if (legacyChanged && legacyEnabled)
        {
            MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                this,
                LocalizationService.Current.Format("HudLegacyConfirmMessage", target!.DisplayName),
                L("HudLegacyConfirmTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }
        }

        GameOverlayPositionChange positionChange = GameOverlayPositionChange.None;
        int? positionX = null;
        int? positionY = null;
        if (!TryResolvePositionRequest(
                _coordinateContext is not null,
                _coordinateDirty,
                ExactPositionBox.IsChecked == true,
                PositionXBox.Text,
                PositionYBox.Text,
                out positionChange,
                out positionX,
                out positionY))
        {
            System.Windows.MessageBox.Show(
                this,
                L("HudPositionInvalidMessage"),
                L("HudPositionInvalidTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        OverlaySettingsCoordinateContext? context = _coordinateContext;
        var request = new GameOverlayConfigurationRequest(
            metrics,
            SamplingBox.SelectedValue as string ?? "standard",
            target?.ExecutablePath,
            legacyEnabled,
            legacyChanged,
            GetLayoutMode(),
            context?.StableMonitorId,
            context?.Left ?? 0,
            context?.Top ?? 0,
            context?.Right ?? 0,
            context?.Bottom ?? 0,
            positionChange,
            positionX,
            positionY);
        if (ApplyRequested?.Invoke(request) != false)
        {
            _coordinateDirty = false;
            FinalizeAndHide(committed: true);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => FinalizeAndHide(committed: false);

    private void ExactPositionBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingCoordinates || _coordinateContext is null)
        {
            return;
        }

        _coordinateDirty = true;
        RefreshCoordinateEnabled();
        RequestPreviewNow();
    }

    private void PositionCoordinateTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingCoordinates || _coordinateContext is null)
        {
            return;
        }

        _coordinateDirty = true;
        SyncSliderFromText(sender as System.Windows.Controls.TextBox);
        RefreshSelectedCoordinateText();
        QueuePreviewRequest();
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingCoordinates || _coordinateContext is null)
        {
            return;
        }

        _loadingCoordinates = true;
        try
        {
            if (ReferenceEquals(sender, PositionXSlider))
            {
                PositionXBox.Text = Math.Round(PositionXSlider.Value).ToString(CultureInfo.InvariantCulture);
            }
            else if (ReferenceEquals(sender, PositionYSlider))
            {
                PositionYBox.Text = Math.Round(PositionYSlider.Value).ToString(CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _loadingCoordinates = false;
        }

        _coordinateDirty = true;
        RefreshSelectedCoordinateText();
        QueuePreviewRequest();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinateContext is null)
        {
            return;
        }

        _coordinateDirty = true;
        ExactPositionBox.IsChecked = false;
        RefreshCoordinateEnabled();
        RequestPreviewNow();
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        bool previous = _suppressPreview;
        _suppressPreview = true;
        try
        {
            foreach (MetricItem item in _items)
            {
                item.Name = NameFor(item.Id);
            }

            string sampling = SamplingBox.SelectedValue as string ?? "standard";
            string layoutMode = GetLayoutMode();
            LoadSamplingItems(sampling);
            LoadLayoutItems(layoutMode);
            RefreshLocalizedText();
            RefreshLegacySelection();
        }
        finally
        {
            _suppressPreview = previous;
        }
    }

    private void LoadSamplingItems(string? sampling)
    {
        SamplingBox.Items.Clear();
        SamplingBox.Items.Add(new ComboBoxItem { Content = L("HudSamplingLow"), Tag = "low" });
        SamplingBox.Items.Add(new ComboBoxItem { Content = L("HudSamplingStandard"), Tag = "standard" });
        SamplingBox.Items.Add(new ComboBoxItem { Content = L("HudSamplingHigh"), Tag = "high" });
        SamplingBox.SelectedValue = sampling?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "high" => "high",
            _ => "standard"
        };
    }

    private void LoadLayoutItems(string? layoutMode)
    {
        bool horizontal = string.Equals(layoutMode?.Trim(), "horizontal", StringComparison.OrdinalIgnoreCase);
        VerticalLayoutRadio.IsChecked = !horizontal;
        HorizontalLayoutRadio.IsChecked = horizontal;
    }

    private string GetLayoutMode() => HorizontalLayoutRadio.IsChecked == true ? "horizontal" : "vertical";

    private void LoadCoordinateContext(OverlaySettingsCoordinateContext? coordinateContext)
    {
        bool previous = _suppressPreview;
        _suppressPreview = true;
        _loadingCoordinates = true;
        try
        {
            _coordinateContext = coordinateContext;
            ExactPositionBox.IsChecked = coordinateContext?.ExactEnabled == true;
            ConfigureCoordinateSlider(PositionXSlider,
                coordinateContext?.MinimumX ?? coordinateContext?.Left ?? 0,
                coordinateContext?.MaximumX ?? coordinateContext?.Right - 1 ?? 1,
                coordinateContext?.CurrentX ?? 0);
            ConfigureCoordinateSlider(PositionYSlider,
                coordinateContext?.MinimumY ?? coordinateContext?.Top ?? 0,
                coordinateContext?.MaximumY ?? coordinateContext?.Bottom - 1 ?? 1,
                coordinateContext?.CurrentY ?? 0);
            PositionXBox.Text = coordinateContext is null
                ? string.Empty
                : Math.Round(PositionXSlider.Value).ToString(CultureInfo.InvariantCulture);
            PositionYBox.Text = coordinateContext is null
                ? string.Empty
                : Math.Round(PositionYSlider.Value).ToString(CultureInfo.InvariantCulture);
            _coordinateDirty = false;
        }
        finally
        {
            _loadingCoordinates = false;
            _suppressPreview = previous;
        }

        RefreshCoordinateDisplay();
        RefreshCoordinateEnabled();
    }

    private void RefreshCoordinateDisplay()
    {
        OverlaySettingsCoordinateContext? context = _coordinateContext;
        PositionMonitorText.Text = context is null
            ? L("HudPositionNoMonitor")
            : context.DisplayName;
        PositionBoundsText.Text = context is null
            ? L("HudPositionUnavailable")
            : string.Format(CultureInfo.InvariantCulture, "{0}, {1} - {2}, {3}",
                context.Left, context.Top, context.Right, context.Bottom);
        PositionCurrentText.Text = context is null
            ? L("HudPositionUnavailable")
            : string.Format(CultureInfo.InvariantCulture, "{0}, {1}",
                Math.Round(PositionXSlider.Value), Math.Round(PositionYSlider.Value));
        if (context is null)
        {
            SetPreviewStatus(L("HudPreviewNoMonitor"));
        }
        else if (string.Equals(_previewStatus, L("HudPreviewNoMonitor"), StringComparison.Ordinal))
        {
            SetPreviewStatus(string.Empty);
        }
    }

    private void RefreshCoordinateEnabled()
    {
        bool enabled = _coordinateContext is not null && ExactPositionBox.IsChecked == true;
        ExactPositionBox.IsEnabled = _coordinateContext is not null;
        PositionXSlider.IsEnabled = enabled;
        PositionYSlider.IsEnabled = enabled;
        PositionXBox.IsEnabled = enabled;
        PositionYBox.IsEnabled = enabled;
        ResetPositionButton.IsEnabled = _coordinateContext is not null;
    }

    private static void ConfigureCoordinateSlider(Slider slider, int minimum, int maximum, int value)
    {
        if (maximum < minimum)
        {
            maximum = minimum;
        }

        slider.Minimum = minimum;
        slider.Maximum = maximum;
        slider.Value = Math.Clamp(value, minimum, maximum);
    }

    private void SyncSliderFromText(System.Windows.Controls.TextBox? source)
    {
        if (source is null || !TryReadCoordinate(source.Text, out int value))
        {
            return;
        }

        Slider slider = ReferenceEquals(source, PositionXBox) ? PositionXSlider : PositionYSlider;
        if (value < slider.Minimum || value > slider.Maximum)
        {
            return;
        }

        _loadingCoordinates = true;
        try
        {
            slider.Value = value;
        }
        finally
        {
            _loadingCoordinates = false;
        }
    }

    private void RefreshSelectedCoordinateText()
    {
        PositionCurrentText.Text = _coordinateContext is not null &&
            TryReadCoordinate(PositionXBox.Text, out int x) &&
            TryReadCoordinate(PositionYBox.Text, out int y)
                ? string.Format(CultureInfo.InvariantCulture, "{0}, {1}", x, y)
                : L("HudPositionUnavailable");
    }

    private void QueuePreviewRequest()
    {
        if (_suppressPreview || !_previewSessionActive || _coordinateContext is null)
        {
            return;
        }

        _previewScheduler.Request();
    }

    private void RequestPreviewNow()
    {
        if (_suppressPreview || !_previewSessionActive)
        {
            return;
        }

        if (!TryBuildPreviewRequest(out GameOverlayPreviewRequest request))
        {
            if (_coordinateContext is null)
            {
                SetPreviewStatus(L("HudPreviewNoMonitor"));
            }
            else if (ExactPositionBox.IsChecked == true)
            {
                SetPreviewStatus(L("HudPreviewInvalid"));
            }

            return;
        }

        SetPreviewStatus(string.Empty);
        try
        {
            PreviewRequested?.Invoke(request);
        }
        catch (Exception ex)
        {
            SetPreviewStatus(ex.Message);
        }
    }

    private bool TryBuildPreviewRequest(out GameOverlayPreviewRequest request)
    {
        request = null!;
        OverlaySettingsCoordinateContext? context = _coordinateContext;
        if (context is null)
        {
            request = new GameOverlayPreviewRequest(
                GetLayoutMode(), Monitor: null, ExactEnabled: false, X: 0, Y: 0);
            return true;
        }

        int x;
        int y;
        if (ExactPositionBox.IsChecked == true)
        {
            if (!TryReadCoordinate(PositionXBox.Text, out x) ||
                !TryReadCoordinate(PositionYBox.Text, out y) ||
                x < PositionXSlider.Minimum || x > PositionXSlider.Maximum ||
                y < PositionYSlider.Minimum || y > PositionYSlider.Maximum)
            {
                return false;
            }
        }
        else
        {
            x = context.CurrentX;
            y = context.CurrentY;
        }

        request = new GameOverlayPreviewRequest(GetLayoutMode(), context, ExactPositionBox.IsChecked == true, x, y);
        return true;
    }

    internal static bool TryResolvePositionRequest(
        bool hasContext,
        bool dirty,
        bool exactChecked,
        string? xText,
        string? yText,
        out GameOverlayPositionChange change,
        out int? x,
        out int? y)
    {
        change = GameOverlayPositionChange.None;
        x = null;
        y = null;
        if (!hasContext || !dirty)
        {
            return true;
        }

        if (!exactChecked)
        {
            change = GameOverlayPositionChange.Reset;
            return true;
        }

        if (!TryReadCoordinate(xText, out int parsedX) ||
            !TryReadCoordinate(yText, out int parsedY))
        {
            return false;
        }

        change = GameOverlayPositionChange.Set;
        x = parsedX;
        y = parsedY;
        return true;
    }

    private void FinalizeAndHide(bool committed)
    {
        FinalizePreviewSession(committed);
        Hide();
    }

    private void FinalizePreviewSession(bool committed)
    {
        if (!TryFinalizePreviewSessionState(ref _sessionFinalized, ref _previewSessionActive))
        {
            return;
        }

        _previewScheduler.Cancel();
        try
        {
            PreviewSessionFinished?.Invoke(committed);
        }
        catch (Exception ex)
        {
            SetPreviewStatus(ex.Message);
        }
    }

    internal static bool TryFinalizePreviewSessionState(
        ref bool sessionFinalized,
        ref bool sessionActive)
    {
        if (sessionFinalized)
        {
            return false;
        }

        sessionFinalized = true;
        sessionActive = false;
        return true;
    }

    private void RefreshLocalizedText()
    {
        Title = L("HudSettingsTitle");
        MetricsTitleText.Text = L("HudSettingsTitle");
        LayoutTitleText.Text = L("HudLayoutTitle");
        LayoutLabelText.Text = L("HudLayoutLabel");
        VerticalLayoutTitleText.Text = L("HudLayoutVertical");
        VerticalLayoutDescriptionText.Text = L("HudLayoutVerticalDescription");
        HorizontalLayoutTitleText.Text = L("HudLayoutHorizontal");
        HorizontalLayoutDescriptionText.Text = L("HudLayoutHorizontalDescription");
        AdvancedTitleText.Text = L("HudAdvancedTitle");
        AdvancedMetricsTitleText.Text = L("HudMetricsTitle");
        PositionTitleText.Text = L("HudPositionTitle");
        PositionHelpText.Text = L("HudPositionHelp");
        PositionMonitorLabelText.Text = L("HudPositionCurrentMonitor");
        PositionBoundsLabelText.Text = L("HudPositionBounds");
        PositionCurrentLabelText.Text = L("HudPositionCurrent");
        ExactPositionBox.Content = L("HudPositionUseExact");
        PositionXLabelText.Text = L("HudPositionX");
        PositionYLabelText.Text = L("HudPositionY");
        ResetPositionButton.Content = L("HudPositionReset");
        MoveUpButton.Content = L("HudMoveUp");
        MoveDownButton.Content = L("HudMoveDown");
        OrderHelpText.Text = L("HudOrderHelp");
        SamplingLabelText.Text = L("HudSamplingLabel");
        LegacyTitleText.Text = L("HudLegacyTitle");
        LegacyTargetLabelText.Text = L("HudLegacyTargetLabel");
        LegacyCompatibilityBox.Content = L("HudLegacyCheckbox");
        LegacyWarningText.Text = L("HudLegacyWarning");
        ApplyButton.Content = L("HudApply");
        CancelButton.Content = L("HudCancel");
        RefreshCoordinateDisplay();
    }

    private static string NameFor(string id) => id switch
    {
        "gpu" => L("HudMetricGpu"),
        "cpu" => L("HudMetricCpu"),
        "fps" => L("HudMetricFps"),
        "memory" => L("HudMetricMemory"),
        "network" => L("HudMetricNetwork"),
        _ => id
    };

    private static string L(string key) => LocalizationService.Current.GetString(key);

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private static bool TryReadCoordinate(string? text, out int value)
    {
        value = 0;
        return !string.IsNullOrEmpty(text) &&
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) &&
            value is >= -1_000_000 and <= 1_000_000;
    }

    private sealed class MetricItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name;

        internal MetricItem(string id, string name, bool enabled)
        {
            Id = id;
            _name = name;
            Enabled = enabled;
        }

        public string Id { get; }
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
            }
        }
        public bool Enabled { get; set; }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
