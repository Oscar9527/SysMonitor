using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    bool ExactEnabled);

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
    private bool _allowClose;
    private bool _loadingLegacy;
    private bool _loadedLegacyEnabled;
    private bool _loadingCoordinates;
    private bool _coordinateDirty;
    private OverlaySettingsCoordinateContext? _coordinateContext;

    public GameOverlaySettingsWindow()
    {
        InitializeComponent();
        MetricList.ItemsSource = _items;
        LegacyTargetBox.ItemsSource = _legacyTargets;
        Closing += (_, e) =>
        {
            if (_allowClose)
            {
                return;
            }

            e.Cancel = true;
            Hide();
        };
        Closed += (_, _) => LocalizationService.Current.CultureChanged -= OnCultureChanged;
        LocalizationService.Current.CultureChanged += OnCultureChanged;
        RefreshLocalizedText();
        LoadLayoutItems("vertical");
        LoadCoordinateContext(null);
    }

    public Func<GameOverlayConfigurationRequest, bool>? ApplyRequested { get; set; }

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

    public void CloseForExit()
    {
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
            LayoutModeBox.SelectedValue as string ?? "vertical",
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
            Hide();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void ExactPositionBox_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingCoordinates || _coordinateContext is null)
        {
            return;
        }

        _coordinateDirty = true;
        RefreshCoordinateEnabled();
    }

    private void PositionCoordinateTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingCoordinates || _coordinateContext is null)
        {
            return;
        }

        _coordinateDirty = true;
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
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (MetricItem item in _items)
        {
            item.Name = NameFor(item.Id);
        }

        string sampling = SamplingBox.SelectedValue as string ?? "standard";
        string layoutMode = LayoutModeBox.SelectedValue as string ?? "vertical";
        LoadSamplingItems(sampling);
        LoadLayoutItems(layoutMode);
        RefreshLocalizedText();
        RefreshLegacySelection();
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
        LayoutModeBox.Items.Clear();
        LayoutModeBox.Items.Add(new ComboBoxItem { Content = L("HudLayoutVertical"), Tag = "vertical" });
        LayoutModeBox.Items.Add(new ComboBoxItem { Content = L("HudLayoutHorizontal"), Tag = "horizontal" });
        LayoutModeBox.SelectedValue = string.Equals(layoutMode?.Trim(), "horizontal", StringComparison.OrdinalIgnoreCase)
            ? "horizontal"
            : "vertical";
    }

    private void LoadCoordinateContext(OverlaySettingsCoordinateContext? coordinateContext)
    {
        _loadingCoordinates = true;
        try
        {
            _coordinateContext = coordinateContext;
            ExactPositionBox.IsChecked = coordinateContext?.ExactEnabled == true;
            PositionXBox.Text = coordinateContext is null
                ? string.Empty
                : coordinateContext.CurrentX.ToString(CultureInfo.InvariantCulture);
            PositionYBox.Text = coordinateContext is null
                ? string.Empty
                : coordinateContext.CurrentY.ToString(CultureInfo.InvariantCulture);
            _coordinateDirty = false;
        }
        finally
        {
            _loadingCoordinates = false;
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
            : string.Format(CultureInfo.InvariantCulture, "{0}, {1} – {2}, {3}",
                context.Left, context.Top, context.Right, context.Bottom);
        PositionCurrentText.Text = context is null
            ? L("HudPositionUnavailable")
            : string.Format(CultureInfo.InvariantCulture, "{0}, {1}", context.CurrentX, context.CurrentY);
    }

    private void RefreshCoordinateEnabled()
    {
        bool enabled = _coordinateContext is not null && ExactPositionBox.IsChecked == true;
        ExactPositionBox.IsEnabled = _coordinateContext is not null;
        PositionXBox.IsEnabled = enabled;
        PositionYBox.IsEnabled = enabled;
        ResetPositionButton.IsEnabled = _coordinateContext is not null;
    }

    private static bool TryReadCoordinate(string? text, out int value)
    {
        value = 0;
        return !string.IsNullOrEmpty(text) &&
            int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) &&
            value is >= -1_000_000 and <= 1_000_000;
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

    private void RefreshLocalizedText()
    {
        Title = L("HudSettingsTitle");
        MetricsTitleText.Text = L("HudMetricsTitle");
        LayoutTitleText.Text = L("HudLayoutTitle");
        LayoutLabelText.Text = L("HudLayoutLabel");
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
