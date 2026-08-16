using System.Collections.ObjectModel;
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

public sealed record GameOverlayConfigurationRequest(
    GameOverlayMetricVisibility Metrics,
    string Sampling,
    string? LegacyExecutablePath,
    bool LegacyEnabled,
    bool LegacyChanged);

public partial class GameOverlaySettingsWindow : Window
{
    private readonly ObservableCollection<MetricItem> _items = [];
    private readonly ObservableCollection<LegacyFpsTargetView> _legacyTargets = [];
    private bool _allowClose;
    private bool _loadingLegacy;
    private bool _loadedLegacyEnabled;

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
    }

    public Func<GameOverlayConfigurationRequest, bool>? ApplyRequested { get; set; }

    public void LoadConfiguration(
        GameOverlayMetricVisibility visibility,
        string? sampling,
        IEnumerable<LegacyFpsTargetView> legacyTargets,
        string? preferredLegacyPath)
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
        MetricList.SelectedIndex = _items.Count == 0 ? -1 : 0;

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

        var request = new GameOverlayConfigurationRequest(
            metrics,
            SamplingBox.SelectedValue as string ?? "standard",
            target?.ExecutablePath,
            legacyEnabled,
            legacyChanged);
        if (ApplyRequested?.Invoke(request) != false)
        {
            Hide();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        foreach (MetricItem item in _items)
        {
            item.Name = NameFor(item.Id);
        }

        string sampling = SamplingBox.SelectedValue as string ?? "standard";
        LoadSamplingItems(sampling);
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

    private void RefreshLocalizedText()
    {
        Title = L("HudSettingsTitle");
        MetricsTitleText.Text = L("HudMetricsTitle");
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
